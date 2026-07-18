using System.Buffers.Binary;
using System.Text;
using DataRecovery.Core.Models;

namespace DataRecovery.Core.FileSystems;

/// <summary>
/// 读取 exFAT 根目录中仍保留的删除目录项。只发布 NoFatChain 连续文件，
/// 从而保证现有按偏移复制的恢复方式不会错误拼接碎片文件。
/// </summary>
public sealed class ExFatDeletedFileScanner
{
    private const byte InUseFlag = 0x80;
    private const byte FileEntryType = 0x05;
    private const byte StreamEntryType = 0x40;
    private const byte FileNameEntryType = 0x41;
    private const ushort DirectoryAttribute = 0x10;
    private const byte NoFatChainFlag = 0x02;

    public async Task<IReadOnlyList<RecoveredFile>> ScanAsync(
        FileStream stream,
        CancellationToken cancellationToken = default)
    {
        var boot = new byte[512];
        await ReadExactlyAtAsync(stream, 0, boot, cancellationToken);
        if (Encoding.ASCII.GetString(boot, 3, 8) != "EXFAT   ")
            return Array.Empty<RecoveredFile>();

        var bytesPerSector = 1 << boot[108];
        var sectorsPerCluster = 1 << boot[109];
        var clusterSize64 = (long)bytesPerSector * sectorsPerCluster;
        if (clusterSize64 is <= 0 or > 32 * 1024 * 1024)
            return Array.Empty<RecoveredFile>();

        var clusterSize = (int)clusterSize64;
        var fatOffset = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(80, 4));
        var clusterHeapOffset = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(88, 4));
        var clusterCount = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(92, 4));
        var rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(96, 4));
        if (rootCluster < 2 || rootCluster >= clusterCount + 2)
            return Array.Empty<RecoveredFile>();

        var directory = await ReadDirectoryChainAsync(
            stream, rootCluster, fatOffset, clusterHeapOffset, clusterCount,
            bytesPerSector, sectorsPerCluster, clusterSize, cancellationToken);

        return ParseDeletedFiles(
            directory, clusterHeapOffset, clusterCount, bytesPerSector, sectorsPerCluster);
    }

    private static IReadOnlyList<RecoveredFile> ParseDeletedFiles(
        byte[] directory,
        uint clusterHeapOffset,
        uint clusterCount,
        int bytesPerSector,
        int sectorsPerCluster)
    {
        var files = new List<RecoveredFile>();
        for (var offset = 0; offset + 32 <= directory.Length; offset += 32)
        {
            var entryType = directory[offset];
            if (entryType == 0) break;
            if ((entryType & 0x7F) != FileEntryType || (entryType & InUseFlag) != 0)
                continue;

            var secondaryCount = directory[offset + 1];
            var entrySetEnd = offset + (secondaryCount + 1) * 32;
            if (secondaryCount < 2 || entrySetEnd > directory.Length)
                continue;

            var attributes = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(offset + 4, 2));
            if ((attributes & DirectoryAttribute) != 0)
                continue;

            var streamOffset = offset + 32;
            if ((directory[streamOffset] & 0x7F) != StreamEntryType)
                continue;

            var flags = directory[streamOffset + 1];
            if ((flags & NoFatChainFlag) == 0)
                continue;

            var nameLength = directory[streamOffset + 3];
            var firstCluster = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(streamOffset + 20, 4));
            var dataLength = BinaryPrimitives.ReadUInt64LittleEndian(directory.AsSpan(streamOffset + 24, 8));
            if (nameLength == 0 || firstCluster < 2 || firstCluster >= clusterCount + 2 ||
                dataLength == 0 || dataLength > long.MaxValue)
                continue;

            var name = ReadFileName(directory, streamOffset + 32, secondaryCount - 1, nameLength);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var dataOffset = checked(
                ((long)clusterHeapOffset + ((long)firstCluster - 2) * sectorsPerCluster) * bytesPerSector);
            var size = (long)dataLength;
            files.Add(new RecoveredFile(
                name,
                "已删除文件/exFAT 根目录",
                size,
                GetCategory(name),
                RecoveryState.Excellent,
                dataOffset,
                "exFAT 删除目录项"));

            offset = entrySetEnd - 32;
        }

        return files;
    }

    private static string ReadFileName(byte[] directory, int offset, int entryCount, int nameLength)
    {
        var builder = new StringBuilder(Math.Min(nameLength, entryCount * 15));
        for (var entry = 0; entry < entryCount && builder.Length < nameLength; entry++, offset += 32)
        {
            if (offset + 32 > directory.Length ||
                (directory[offset] & 0x7F) != FileNameEntryType)
                break;

            for (var character = 0; character < 15 && builder.Length < nameLength; character++)
            {
                var value = BinaryPrimitives.ReadUInt16LittleEndian(
                    directory.AsSpan(offset + 2 + character * 2, 2));
                if (value == 0) break;
                builder.Append((char)value);
            }
        }
        return builder.ToString();
    }

    private static string GetCategory(string name)
    {
        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".tif" or ".tiff" => "照片",
            ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".pdf" or ".txt" => "文档",
            ".zip" or ".rar" or ".7z" or ".gz" or ".tar" => "压缩包",
            ".exe" or ".dll" or ".msi" => "程序",
            _ => "其他"
        };
    }

    private static async Task<byte[]> ReadDirectoryChainAsync(
        FileStream stream,
        uint firstCluster,
        uint fatOffset,
        uint clusterHeapOffset,
        uint clusterCount,
        int bytesPerSector,
        int sectorsPerCluster,
        int clusterSize,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(clusterSize);
        var cluster = firstCluster;
        var visited = new HashSet<uint>();
        for (var count = 0; count < 4096 && cluster >= 2 && cluster < clusterCount + 2; count++)
        {
            if (!visited.Add(cluster)) break;
            var buffer = new byte[clusterSize];
            var clusterOffset = checked(
                ((long)clusterHeapOffset + ((long)cluster - 2) * sectorsPerCluster) * bytesPerSector);
            await ReadExactlyAtAsync(stream, clusterOffset, buffer, cancellationToken);
            await output.WriteAsync(buffer, cancellationToken);
            if (ContainsEndMarker(buffer)) break;

            var fatEntry = new byte[4];
            var fatEntryOffset = checked((long)fatOffset * bytesPerSector + (long)cluster * 4);
            await ReadExactlyAtAsync(stream, fatEntryOffset, fatEntry, cancellationToken);
            var next = BinaryPrimitives.ReadUInt32LittleEndian(fatEntry) & 0x0FFFFFFF;
            if (next == 0 || next >= 0x0FFFFFF8) break;
            cluster = next;
        }
        return output.ToArray();
    }

    private static bool ContainsEndMarker(byte[] cluster)
    {
        for (var offset = 0; offset + 32 <= cluster.Length; offset += 32)
            if (cluster[offset] == 0) return true;
        return false;
    }

    private static async Task ReadExactlyAtAsync(
        FileStream stream, long offset, byte[] buffer, CancellationToken cancellationToken)
    {
        stream.Position = offset;
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (count == 0) throw new EndOfStreamException("exFAT 元数据读取超出数据源范围。");
            read += count;
        }
    }
}
