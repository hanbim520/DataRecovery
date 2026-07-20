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
        new("JPEG", "照片", [0xFF, 0xD8, 0xFF], [0xFF, 0xD9], ".jpg", 48),
        new("PNG", "照片", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], [0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82], ".png", 67),
        new("PDF", "文档", Encoding.ASCII.GetBytes("%PDF-"), Encoding.ASCII.GetBytes("%%EOF"), ".pdf", 14),
        new("ZIP/Office", "压缩包", [0x50, 0x4B, 0x03, 0x04], [0x50, 0x4B, 0x05, 0x06], ".zip", 22),
        new("GIF", "照片", Encoding.ASCII.GetBytes("GIF8"), [0x3B], ".gif", 13)
    ];

    public async Task<(DetectedFileSystem FileSystem, IReadOnlyList<RecoveredFile> Files)> ScanAsync(
        string path, ScanMode scanMode, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using var stream = OpenReadOnly(path);
        var fileSystem = await detector.DetectAsync(stream, cancellationToken);
        var includeDeletedFiles = scanMode is ScanMode.DeletedFiles or ScanMode.AllFiles;
        var includeLostFiles = scanMode is ScanMode.LostFiles or ScanMode.AllFiles;
        IReadOnlyList<RecoveredFile> currentMetadataFiles = fileSystem.Kind switch
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
        IReadOnlyList<RecoveredFile> previousMetadataFiles =
            includeLostFiles && fileSystem.Kind != FileSystemKind.ExFat
                ? await new ExFatDeletedFileScanner()
                    .ScanPreviousExFatVolumeAsync(stream, cancellationToken)
                : Array.Empty<RecoveredFile>();
        var metadataFiles = (includeDeletedFiles
                ? currentMetadataFiles
                : Array.Empty<RecoveredFile>())
            .Concat(previousMetadataFiles)
            .GroupBy(file => (file.Offset, file.Name, file.Signature))
            .Select(group => group.First())
            .ToArray();
        if (stream.CanSeek) stream.Position = 0;

        if (!includeLostFiles)
        {
            progress?.Report(new ScanProgress(
                100,
                $"{fileSystem.Label} 删除元数据扫描完成",
                4096,
                metadataFiles.Length,
                metadataFiles));
            return (fileSystem, metadataFiles);
        }

        var workerCount = RecommendedWorkerCount;
        var length = TryGetLength(stream);
        var results = new ConcurrentDictionary<long, RecoveredFile>();
        var pendingDiscoveries = progress is null ? null : new ConcurrentQueue<RecoveredFile>();
        foreach (var file in metadataFiles)
        {
            results.TryAdd(file.Offset, file);
            pendingDiscoveries?.Enqueue(file);
        }
        var knownFileRanges = currentMetadataFiles
            .Concat(previousMetadataFiles)
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
                            chunk, results, pendingDiscoveries, knownFileRanges, length);
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
        try
        {
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, RecoveryBufferSize, true);
            var remaining = file.Size;
            var buffer = new byte[RecoveryBufferSize];
            var requiresAlignedReads = IsWindowsRawDevicePath(sourcePath);
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

                var copied = await CopyExtentAsync(
                    source, output, buffer, extent.SourceOffset, extentRemaining,
                    requiresAlignedReads, cancellationToken);
                remaining -= copied;
                if (copied != extentRemaining) break;
            }

            if (remaining != 0)
                throw new EndOfStreamException(
                    $"源设备数据不足，恢复文件还缺少 {remaining:N0} 字节。");

            // 当最后一个区段为稀疏区段时，移动文件位置本身不会扩展文件。
            if (output.Length < file.Size)
                output.SetLength(file.Size);
            await output.FlushAsync(cancellationToken);
        }
        catch
        {
            // 失败结果不能伪装成完整文件；关闭输出句柄后删除残缺目标。
            try { File.Delete(target); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static async Task<long> CopyExtentAsync(
        FileStream source,
        FileStream output,
        byte[] buffer,
        long sourceOffset,
        long length,
        bool requiresAlignedReads,
        CancellationToken cancellationToken)
    {
        if (!requiresAlignedReads)
        {
            source.Position = sourceOffset;
            long directCopied = 0;
            while (directCopied < length)
            {
                var requested = (int)Math.Min(buffer.Length, length - directCopied);
                var read = await source.ReadAsync(
                    buffer.AsMemory(0, requested), cancellationToken);
                if (read == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                directCopied += read;
            }
            return directCopied;
        }

        // Windows 原始卷要求 Seek/Read 使用扇区对齐的偏移和长度。统一按 4 KB
        // 对齐读取，再从内存块中跳过文件真实起点前的字节。
        const int alignment = 4096;
        var alignedOffset = sourceOffset - sourceOffset % alignment;
        var prefix = (int)(sourceOffset - alignedOffset);
        source.Position = alignedOffset;
        long copied = 0;
        while (copied < length)
        {
            var needed = Math.Min((long)buffer.Length - prefix, length - copied);
            var requested = checked(prefix + (int)needed);
            requested = Math.Min(buffer.Length, RoundUp(requested, alignment));
            var read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
            if (read <= prefix) break;
            var available = Math.Min(read - prefix, (int)Math.Min(int.MaxValue, length - copied));
            await output.WriteAsync(buffer.AsMemory(prefix, available), cancellationToken);
            copied += available;
            prefix = 0;
        }
        return copied;
    }

    private static int RoundUp(int value, int alignment)
    {
        var remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    private static bool IsWindowsRawDevicePath(string path) =>
        OperatingSystem.IsWindows() &&
        (path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase) ||
         path.StartsWith(@"\\?\GLOBALROOT\Device\", StringComparison.OrdinalIgnoreCase));

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
        IReadOnlyList<KnownFileRange> knownFileRanges,
        long sourceLength)
    {
        var span = chunk.Buffer.AsSpan(0, chunk.Count);
        foreach (var signature in Signatures)
        {
            var start = 0;
            long lastAcceptedEnd = -1;
            while (start <= span.Length - signature.Header.Length)
            {
                var relative = span[start..].IndexOf(signature.Header);
                if (relative < 0) break;

                var index = start + relative;
                var offset = chunk.AbsoluteOffset + index;
                if (offset < lastAcceptedEnd ||
                    !IsPlausibleSignatureHeader(span, index, signature) ||
                    knownFileRanges.Any(range => range.Contains(offset)))
                {
                    start = index + signature.Header.Length;
                    continue;
                }

                int size;
                if (signature.Name == "JPEG")
                {
                    if (!TryGetJpegSize(span[index..], out size))
                    {
                        start = index + signature.Header.Length;
                        continue;
                    }
                }
                else
                {
                    var payloadStart = index + signature.Header.Length;
                    var relativeTail = span[payloadStart..].IndexOf(signature.Footer);
                    if (relativeTail < 0)
                    {
                        start = index + signature.Header.Length;
                        continue;
                    }

                    var tail = payloadStart + relativeTail;
                    size = GetCompleteSignatureSize(span, index, tail, signature);
                }
                if (size < signature.MinimumSize ||
                    (sourceLength > 0 &&
                     (offset > sourceLength || size > sourceLength - offset)))
                {
                    start = index + signature.Header.Length;
                    continue;
                }

                var candidate = new RecoveredFile(
                    $"恢复候选_{offset:X}{signature.Extension}",
                    $"格式化或丢失文件/{signature.Category}",
                    size,
                    signature.Category,
                    RecoveryState.Good,
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

                lastAcceptedEnd = offset + size;
                start = index + signature.Header.Length;
            }
        }

        foreach (var candidate in PeFileCarver.FindCandidates(
                     span, chunk.AbsoluteOffset, sourceLength))
            AddStructuredCandidate(candidate, results, pendingDiscoveries, knownFileRanges);
        foreach (var candidate in StructuredFileCarver.FindCandidates(
                     span, chunk.AbsoluteOffset, sourceLength))
            AddStructuredCandidate(candidate, results, pendingDiscoveries, knownFileRanges);
    }

    private static bool IsPlausibleSignatureHeader(
        ReadOnlySpan<byte> data,
        int index,
        Signature signature)
    {
        var value = data[index..];
        switch (signature.Name)
        {
            case "JPEG":
                if (value.Length < 6) return false;
                var marker = value[3];
                if (marker is not (>= 0xC0 and <= 0xCF) and
                    not (>= 0xDB and <= 0xDF) and
                    not (>= 0xE0 and <= 0xEF) and not 0xFE)
                    return false;
                if (marker is 0xD8 or 0xD9) return false;
                var segmentLength = (value[4] << 8) | value[5];
                return segmentLength >= 2;

            case "PNG":
                return value.Length >= 24 &&
                       value[8] == 0 && value[9] == 0 && value[10] == 0 && value[11] == 13 &&
                       value.Slice(12, 4).SequenceEqual("IHDR"u8) &&
                       (value[16] != 0 || value[17] != 0 || value[18] != 0 || value[19] != 0) &&
                       (value[20] != 0 || value[21] != 0 || value[22] != 0 || value[23] != 0);

            case "PDF":
                return value.Length >= 8 &&
                       value[5] is >= (byte)'1' and <= (byte)'9' &&
                       value[6] == (byte)'.' &&
                       value[7] is >= (byte)'0' and <= (byte)'9';

            case "GIF":
                return value.Length >= 10 &&
                       (value.Slice(4, 2).SequenceEqual("7a"u8) ||
                        value.Slice(4, 2).SequenceEqual("9a"u8)) &&
                       (value[6] != 0 || value[7] != 0) &&
                       (value[8] != 0 || value[9] != 0);

            case "ZIP/Office":
                if (value.Length < 30) return false;
                var versionNeeded = value[4] | value[5] << 8;
                var compression = value[8] | value[9] << 8;
                return versionNeeded is >= 10 and <= 100 &&
                       compression is 0 or 8 or 9 or 12 or 14 or 93 or 95 or 98;

            default:
                return true;
        }
    }

    private static bool TryGetJpegSize(ReadOnlySpan<byte> data, out int size)
    {
        size = 0;
        if (data.Length < 12 || data[0] != 0xFF || data[1] != 0xD8)
            return false;

        var position = 2;
        var sawFrame = false;
        var sawScan = false;
        while (position < data.Length - 1)
        {
            if (data[position] != 0xFF) return false;
            while (position < data.Length && data[position] == 0xFF) position++;
            if (position >= data.Length) return false;
            var marker = data[position++];
            if (marker is 0x00 or 0xD8 or >= 0xD0 and <= 0xD7)
                return false;
            if (marker == 0xD9)
            {
                if (!sawFrame || !sawScan) return false;
                size = position;
                return true;
            }

            if (position > data.Length - 2) return false;
            var segmentLength = (data[position] << 8) | data[position + 1];
            if (segmentLength < 2 || segmentLength > data.Length - position)
                return false;

            if (IsJpegFrameMarker(marker))
            {
                if (segmentLength < 8) return false;
                var height = (data[position + 3] << 8) | data[position + 4];
                var width = (data[position + 5] << 8) | data[position + 6];
                var componentCount = data[position + 7];
                if (width == 0 || height == 0 || componentCount is 0 or > 4 ||
                    segmentLength < 8 + componentCount * 3)
                    return false;
                sawFrame = true;
            }

            position += segmentLength;
            if (marker != 0xDA) continue;

            sawScan = true;
            // 熵编码区内 FF 00 是转义数据，FF D0-D7 是重启标记；其余标记
            // 交回外层解析，以支持渐进式 JPEG 的多次扫描。
            while (position < data.Length - 1)
            {
                if (data[position] != 0xFF)
                {
                    position++;
                    continue;
                }

                var markerStart = position;
                while (position < data.Length && data[position] == 0xFF) position++;
                if (position >= data.Length) return false;
                var entropyMarker = data[position];
                if (entropyMarker == 0x00 || entropyMarker is >= 0xD0 and <= 0xD7)
                {
                    position++;
                    continue;
                }

                position = markerStart;
                break;
            }
        }

        return false;
    }

    private static bool IsJpegFrameMarker(byte marker) =>
        marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC);

    private static int GetCompleteSignatureSize(
        ReadOnlySpan<byte> data,
        int headerIndex,
        int footerIndex,
        Signature signature)
    {
        if (signature.Name != "ZIP/Office")
            return footerIndex - headerIndex + signature.Footer.Length;

        // ZIP 的 EOCD 固定部分是 22 字节，最后两个字节还给出可选注释长度。
        if (footerIndex > data.Length - 22) return 0;
        var commentLength = data[footerIndex + 20] | data[footerIndex + 21] << 8;
        var end = (long)footerIndex + 22 + commentLength;
        return end <= data.Length ? checked((int)(end - headerIndex)) : 0;
    }

    private static void AddStructuredCandidate(
        RecoveredFile candidate,
        ConcurrentDictionary<long, RecoveredFile> results,
        ConcurrentQueue<RecoveredFile>? pendingDiscoveries,
        IReadOnlyList<KnownFileRange> knownFileRanges)
    {
        if (knownFileRanges.Any(range => range.Contains(candidate.Offset)))
            return;
        if (results.TryAdd(candidate.Offset, candidate))
        {
            pendingDiscoveries?.Enqueue(candidate);
        }
        else if (results.TryGetValue(candidate.Offset, out var existing) &&
                 candidate.Size > existing.Size &&
                 results.TryUpdate(candidate.Offset, candidate, existing))
        {
            pendingDiscoveries?.Enqueue(candidate);
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
        string Name, string Category, byte[] Header, byte[] Footer, string Extension,
        int MinimumSize);
}
