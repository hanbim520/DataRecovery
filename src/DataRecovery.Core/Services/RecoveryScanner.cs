using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using DataRecovery.Core.FileSystems;
using DataRecovery.Core.Models;

namespace DataRecovery.Core.Services;

public interface IRecoveryScanner
{
    Task<(DetectedFileSystem FileSystem, IReadOnlyList<RecoveredFile> Files)> ScanAsync(
        string path, ScanMode scanMode, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class RecoveryScanner(IFileSystemDetector detector) : IRecoveryScanner
{
    // 顺序读盘、并行分析。大块读取可以降低机械硬盘寻道和系统调用开销。
    private const int ChunkSize = 8 * 1024 * 1024;
    private const int BoundaryOverlap = 64 * 1024;
    private const int RecoveryBufferSize = 1024 * 1024;

    // 使用运行环境可用的全部逻辑处理器；至少保留一个工作线程。
    public static int RecommendedWorkerCount => Math.Max(1, Environment.ProcessorCount);

    private static readonly Signature[] Signatures =
    [
        new("JPEG", "照片", [0xFF, 0xD8, 0xFF], [0xFF, 0xD9], ".jpg"),
        new("PNG", "照片", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], [0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82], ".png"),
        new("PDF", "文档", Encoding.ASCII.GetBytes("%PDF-"), Encoding.ASCII.GetBytes("%%EOF"), ".pdf"),
        new("ZIP/Office", "压缩包", [0x50, 0x4B, 0x03, 0x04], [0x50, 0x4B, 0x05, 0x06], ".zip"),
        new("GIF", "照片", Encoding.ASCII.GetBytes("GIF8"), [0x3B], ".gif")
    ];

    public async Task<(DetectedFileSystem FileSystem, IReadOnlyList<RecoveredFile> Files)> ScanAsync(
        string path, ScanMode scanMode, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using var stream = OpenReadOnly(path);
        var fileSystem = await detector.DetectAsync(stream, cancellationToken);
        var metadataFiles = fileSystem.Kind switch
        {
            FileSystemKind.Fat12 or FileSystemKind.Fat16 or FileSystemKind.Fat32 =>
                await new FatDeletedFileScanner().ScanAsync(stream, cancellationToken),
            FileSystemKind.ExFat =>
                await new ExFatDeletedFileScanner().ScanAsync(stream, cancellationToken),
            FileSystemKind.Ntfs =>
                await new NtfsDeletedFileScanner().ScanAsync(stream, cancellationToken),
            FileSystemKind.Ext2 or FileSystemKind.Ext3 =>
                await new ExtDeletedFileScanner().ScanAsync(stream, cancellationToken),
            _ => Array.Empty<RecoveredFile>()
        };
        if (stream.CanSeek) stream.Position = 0;

        var includeDeletedFiles = scanMode is ScanMode.DeletedFiles or ScanMode.AllFiles;
        var includeLostFiles = scanMode is ScanMode.LostFiles or ScanMode.AllFiles;
        var selectedMetadataFiles = includeDeletedFiles
            ? metadataFiles
            : Array.Empty<RecoveredFile>();

        if (!includeLostFiles)
        {
            progress?.Report(new ScanProgress(
                100,
                $"{fileSystem.Label} 删除元数据扫描完成",
                4096,
                selectedMetadataFiles.Count,
                selectedMetadataFiles));
            return (fileSystem, selectedMetadataFiles);
        }

        var workerCount = RecommendedWorkerCount;
        var length = TryGetLength(stream);
        var results = new ConcurrentDictionary<long, RecoveredFile>();
        var pendingDiscoveries = progress is null ? null : new ConcurrentQueue<RecoveredFile>();
        foreach (var file in selectedMetadataFiles)
        {
            results.TryAdd(file.Offset, file);
            pendingDiscoveries?.Enqueue(file);
        }
        var knownFileRanges = metadataFiles
            .SelectMany(GetKnownFileRanges)
            .ToArray();
        // 工作线程可以等于全部 CPU 数量，但队列无需同比扩张，避免大盘扫描占用过多内存。
        var queueCapacity = Math.Clamp(workerCount, 2, 16);
        var channel = Channel.CreateBounded<ScanChunk>(new BoundedChannelOptions(queueCapacity)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        long processedBytes = 0;
        long lastProgressTick = 0;

        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(async () =>
            {
                await foreach (var chunk in channel.Reader.ReadAllAsync(cancellationToken))
                {
                    try
                    {
                        ScanChunkForSignatures(
                            chunk, results, pendingDiscoveries, knownFileRanges);
                        var processed = Interlocked.Add(ref processedBytes, chunk.SourceBytes);
                        ReportProgress(progress, pendingDiscoveries, length, processed,
                            results.Count, workerCount, ref lastProgressTick);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(chunk.Buffer);
                    }
                }
            }, cancellationToken))
            .ToArray();

        progress?.Report(new ScanProgress(0, $"正在启动 {workerCount} 个并行扫描线程…", 0, 0));
        Exception? producerError = null;
        try
        {
            await ProduceChunksAsync(stream, channel.Writer, cancellationToken);
        }
        catch (Exception ex)
        {
            producerError = ex;
        }
        finally
        {
            channel.Writer.TryComplete(producerError);
        }

        try
        {
            await Task.WhenAll(workers);
        }
        finally
        {
            // 取消或读取失败时，归还尚未被工作线程领取的池化缓冲区。
            while (channel.Reader.TryRead(out var pending))
                ArrayPool<byte>.Shared.Return(pending.Buffer);
        }

        if (producerError is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(producerError).Throw();

        var ordered = results.Values
            .OrderBy(file => file.Offset)
            .Select((file, index) => file with
            {
                Name = IsMetadataResult(file)
                    ? file.Name
                    : $"恢复文件_{index + 1:0000}{Path.GetExtension(file.Name)}"
            })
            .ToList();

        PublishRemainingDiscoveries(
            progress, pendingDiscoveries, processedBytes, ordered.Count, workerCount);
        progress?.Report(new ScanProgress(100, $"{workerCount} 线程扫描完成", processedBytes, ordered.Count));
        return (fileSystem, ordered);
    }

    public static async Task RecoverAsync(string sourcePath, RecoveredFile file, string destination,
        CancellationToken cancellationToken = default)
    {
        if (file.Size <= 0)
            throw new InvalidOperationException("文件尾未找到，当前结果无法自动恢复。请使用完整镜像进行进一步分析。");

        Directory.CreateDirectory(destination);
        var target = GetUniquePath(Path.Combine(destination, file.Name));
        await using var source = OpenReadOnly(sourcePath);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, RecoveryBufferSize, true);
        var remaining = file.Size;
        var buffer = new byte[RecoveryBufferSize];
        var extents = file.RecoveryExtents.Count > 0
            ? file.RecoveryExtents
            : [new RecoveryExtent(file.Offset, file.Size)];
        foreach (var extent in extents)
        {
            if (remaining <= 0) break;
            var extentRemaining = Math.Min(remaining, Math.Max(0, extent.Length));
            if (extentRemaining == 0) continue;
            if (extent.IsSparse)
            {
                output.Position += extentRemaining;
                remaining -= extentRemaining;
                continue;
            }
            if (extent.SourceOffset < 0)
                throw new InvalidDataException("恢复区段包含无效的源偏移。");

            source.Position = extent.SourceOffset;
            while (extentRemaining > 0)
            {
                var read = await source.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, extentRemaining)), cancellationToken);
                if (read == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                extentRemaining -= read;
                remaining -= read;
            }
            if (extentRemaining > 0) break;
        }
        // 当最后一个区段为稀疏区段时，移动文件位置本身不会扩展文件。
        if (remaining == 0 && output.Length < file.Size)
            output.SetLength(file.Size);
    }

    private static async Task ProduceChunksAsync(
        FileStream stream, ChannelWriter<ScanChunk> writer, CancellationToken cancellationToken)
    {
        var previousTail = new byte[BoundaryOverlap];
        var carry = 0;
        long absoluteOffset = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize + BoundaryOverlap);
            var handedToConsumer = false;
            try
            {
                if (carry > 0)
                    previousTail.AsSpan(0, carry).CopyTo(buffer);

                var bytesRead = 0;
                while (bytesRead < ChunkSize)
                {
                    var read = await stream.ReadAsync(
                        buffer.AsMemory(carry + bytesRead, ChunkSize - bytesRead), cancellationToken);
                    if (read == 0) break;
                    bytesRead += read;
                }

                if (bytesRead == 0) break;

                var count = carry + bytesRead;
                var chunkOffset = absoluteOffset - carry;
                var nextCarry = Math.Min(BoundaryOverlap, count);
                buffer.AsSpan(count - nextCarry, nextCarry).CopyTo(previousTail);

                await writer.WriteAsync(
                    new ScanChunk(buffer, count, chunkOffset, bytesRead), cancellationToken);
                handedToConsumer = true;
                absoluteOffset += bytesRead;
                carry = nextCarry;
            }
            finally
            {
                if (!handedToConsumer)
                    ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private static void ScanChunkForSignatures(
        ScanChunk chunk,
        ConcurrentDictionary<long, RecoveredFile> results,
        ConcurrentQueue<RecoveredFile>? pendingDiscoveries,
        IReadOnlyList<KnownFileRange> knownFileRanges)
    {
        var span = chunk.Buffer.AsSpan(0, chunk.Count);
        foreach (var signature in Signatures)
        {
            var start = 0;
            while (start <= span.Length - signature.Header.Length)
            {
                var relative = span[start..].IndexOf(signature.Header);
                if (relative < 0) break;

                var index = start + relative;
                var offset = chunk.AbsoluteOffset + index;
                if (knownFileRanges.Any(range => range.Contains(offset)))
                {
                    start = index + signature.Header.Length;
                    continue;
                }
                var tail = span[index..].IndexOf(signature.Footer);
                var size = tail >= 0 ? tail + signature.Footer.Length : 0;
                var candidate = new RecoveredFile(
                    $"恢复候选_{offset:X}{signature.Extension}",
                    $"丢失文件/{signature.Category}",
                    size,
                    signature.Category,
                    size > 0 ? RecoveryState.Good : RecoveryState.Partial,
                    offset,
                    signature.Name);
                if (results.TryAdd(offset, candidate))
                {
                    pendingDiscoveries?.Enqueue(candidate);
                }
                else if (results.TryGetValue(offset, out var existing) && candidate.Size > existing.Size)
                {
                    results.TryUpdate(offset, candidate, existing);
                }

                start = index + signature.Header.Length;
            }
        }
    }

    private static void ReportProgress(
        IProgress<ScanProgress>? progress,
        ConcurrentQueue<RecoveredFile>? pendingDiscoveries,
        long length,
        long processed,
        int filesFound,
        int workerCount,
        ref long lastProgressTick)
    {
        if (progress is null) return;
        var now = Environment.TickCount64;
        var previous = Volatile.Read(ref lastProgressTick);
        if (now - previous < 100 || Interlocked.CompareExchange(ref lastProgressTick, now, previous) != previous)
            return;

        var percent = length > 0 ? Math.Min(100, processed * 100d / length) : 0;
        progress.Report(new ScanProgress(
            percent,
            $"正在使用 {workerCount} 个线程深度扫描…",
            processed,
            filesFound,
            DrainDiscoveries(pendingDiscoveries, 512)));
    }

    private static void PublishRemainingDiscoveries(
        IProgress<ScanProgress>? progress,
        ConcurrentQueue<RecoveredFile>? pendingDiscoveries,
        long processedBytes,
        int filesFound,
        int workerCount)
    {
        if (progress is null || pendingDiscoveries is null) return;
        while (!pendingDiscoveries.IsEmpty)
        {
            progress.Report(new ScanProgress(
                100,
                $"正在整理 {filesFound} 个扫描结果…",
                processedBytes,
                filesFound,
                DrainDiscoveries(pendingDiscoveries, 1024)));
        }
    }

    private static IReadOnlyList<RecoveredFile>? DrainDiscoveries(
        ConcurrentQueue<RecoveredFile>? pendingDiscoveries,
        int maximumCount)
    {
        if (pendingDiscoveries is null || pendingDiscoveries.IsEmpty) return null;
        var batch = new List<RecoveredFile>(maximumCount);
        while (batch.Count < maximumCount && pendingDiscoveries.TryDequeue(out var file))
            batch.Add(file);
        return batch.Count == 0 ? null : batch;
    }

    private static long TryGetLength(FileStream stream)
    {
        try { return stream.CanSeek ? stream.Length : 0; }
        catch (IOException) { return 0; }
        catch (NotSupportedException) { return 0; }
    }

    private static FileStream OpenReadOnly(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        RecoveryBufferSize,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static string GetUniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static IEnumerable<KnownFileRange> GetKnownFileRanges(RecoveredFile file)
    {
        if (file.RecoveryExtents.Count == 0)
        {
            if (file.Size > 0) yield return new KnownFileRange(file.Offset, file.Size);
            yield break;
        }

        foreach (var extent in file.RecoveryExtents)
        {
            if (!extent.IsSparse && extent.SourceOffset >= 0 && extent.Length > 0)
                yield return new KnownFileRange(extent.SourceOffset, extent.Length);
        }
    }

    private static bool IsMetadataResult(RecoveredFile file) =>
        file.RecoveryExtents.Count > 0 ||
        file.Signature.Contains("删除", StringComparison.OrdinalIgnoreCase) ||
        file.Signature.Contains("$MFT", StringComparison.OrdinalIgnoreCase) ||
        file.Signature.Contains("inode", StringComparison.OrdinalIgnoreCase);

    private sealed record ScanChunk(byte[] Buffer, int Count, long AbsoluteOffset, int SourceBytes);
    private sealed record KnownFileRange(long Offset, long Length)
    {
        public bool Contains(long value) => value >= Offset && value - Offset < Length;
    }
    private sealed record Signature(
        string Name, string Category, byte[] Header, byte[] Footer, string Extension);
}
