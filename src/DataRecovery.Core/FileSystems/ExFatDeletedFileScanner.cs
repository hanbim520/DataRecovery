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
    private static readonly int[] SupportedSectorSizes = [512, 1024, 2048, 4096];
    private const int BackupBootSectorIndex = 12;
    private const int MaximumDirectoryBytes = 64 * 1024 * 1024;
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

    /// <summary>
    /// 在当前文件系统已不是 exFAT 时，探测格式化前遗留在第 12 扇区的 exFAT
    /// 备份引导扇区，并从旧根目录恢复仍可安全表达为连续区段的文件。
    /// 找不到可信的旧卷，或旧卷几何已超出当前数据源时返回空集合。
    /// </summary>
    public async Task<IReadOnlyList<RecoveredFile>> ScanPreviousExFatVolumeAsync(
        FileStream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
            return Array.Empty<RecoveredFile>();

        long sourceLength;
        try
        {
            sourceLength = stream.Length;
        }
        catch (IOException)
        {
            return Array.Empty<RecoveredFile>();
        }
        catch (NotSupportedException)
        {
            return Array.Empty<RecoveredFile>();
        }

        var originalPosition = stream.Position;
        try
        {
            foreach (var sectorSize in SupportedSectorSizes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var backupOffset = (long)BackupBootSectorIndex * sectorSize;
                var boot = new byte[512];
                if (!await TryReadExactlyAtAsync(
                        stream, backupOffset, boot, sourceLength, cancellationToken))
                    continue;

                if (!TryReadPreviousVolumeGeometry(
                        boot, sectorSize, sourceLength, out var geometry))
                    continue;

                var directory = await TryReadPreviousDirectoryChainAsync(
                    stream, geometry, sourceLength, cancellationToken);
                if (directory is null)
                    return Array.Empty<RecoveredFile>();

                return ParsePreviousVolumeFiles(directory, geometry, sourceLength);
            }

            return Array.Empty<RecoveredFile>();
        }
        finally
        {
            stream.Position = originalPosition;
        }
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
                "exFAT 删除目录项")
            {
                RecoveryExtents = [new RecoveryExtent(dataOffset, size)]
            });

            offset = entrySetEnd - 32;
        }

        return files;
    }

    private static bool TryReadPreviousVolumeGeometry(
        ReadOnlySpan<byte> boot,
        int probedSectorSize,
        long sourceLength,
        out PreviousVolumeGeometry geometry)
    {
        geometry = default!;
        if (boot.Length < 512 ||
            boot[0] != 0xEB || boot[1] != 0x76 || boot[2] != 0x90 ||
            !boot.Slice(3, 8).SequenceEqual("EXFAT   "u8) ||
            boot.Slice(11, 53).IndexOfAnyExcept((byte)0) >= 0 ||
            boot[510] != 0x55 || boot[511] != 0xAA)
            return false;

        var bytesPerSectorShift = boot[108];
        if (bytesPerSectorShift is < 9 or > 12 ||
            (1 << bytesPerSectorShift) != probedSectorSize)
            return false;

        var sectorsPerClusterShift = boot[109];
        if (sectorsPerClusterShift > 25 - bytesPerSectorShift)
            return false;

        var sectorsPerCluster = 1 << sectorsPerClusterShift;
        var clusterSize = (long)probedSectorSize * sectorsPerCluster;
        if (clusterSize is <= 0 or > 32 * 1024 * 1024)
            return false;

        var volumeLength = BinaryPrimitives.ReadUInt64LittleEndian(boot.Slice(72, 8));
        var fatOffset = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(80, 4));
        var fatLength = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(84, 4));
        var clusterHeapOffset = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(88, 4));
        var clusterCount = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(92, 4));
        var rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(96, 4));
        var revision = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(104, 2));
        var volumeFlags = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(106, 2));
        var numberOfFats = boot[110];
        var percentInUse = boot[112];

        if ((revision >> 8) != 1 ||
            numberOfFats is < 1 or > 2 ||
            percentInUse > 100 && percentInUse != 0xFF ||
            volumeLength < 24 || fatOffset < 24 || fatLength == 0 ||
            clusterCount == 0 || clusterCount > 0xFFFFFFF5 ||
            rootCluster < 2 || (ulong)rootCluster >= (ulong)clusterCount + 2)
            return false;

        var allFatsEnd = (ulong)fatOffset + (ulong)fatLength * numberOfFats;
        var heapEnd = (ulong)clusterHeapOffset + (ulong)clusterCount * (uint)sectorsPerCluster;
        var fatCapacity = (ulong)fatLength * (uint)probedSectorSize / 4;
        if (allFatsEnd > clusterHeapOffset ||
            clusterHeapOffset >= volumeLength || heapEnd > volumeLength ||
            fatCapacity < (ulong)clusterCount + 2)
            return false;

        if (!TryMultiply(volumeLength, (ulong)probedSectorSize, out var volumeBytes) ||
            volumeBytes > (ulong)sourceLength)
            return false;

        var activeFatIndex = numberOfFats == 2 ? volumeFlags & 1 : 0;
        var activeFatOffset = (ulong)fatOffset + (ulong)activeFatIndex * fatLength;
        if (!TryMultiply(activeFatOffset, (ulong)probedSectorSize, out var fatByteOffset) ||
            !TryMultiply((ulong)fatLength, (ulong)probedSectorSize, out var fatByteLength) ||
            fatByteOffset > volumeBytes || fatByteLength > volumeBytes - fatByteOffset)
            return false;

        geometry = new PreviousVolumeGeometry(
            probedSectorSize,
            sectorsPerCluster,
            (int)clusterSize,
            clusterHeapOffset,
            clusterCount,
            rootCluster,
            fatByteOffset,
            fatByteLength,
            volumeBytes);

        return TryGetClusterOffset(geometry, rootCluster, out var rootOffset) &&
               (ulong)geometry.ClusterSize <= geometry.VolumeBytes - rootOffset;
    }

    private static async Task<byte[]?> TryReadPreviousDirectoryChainAsync(
        FileStream stream,
        PreviousVolumeGeometry geometry,
        long sourceLength,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(geometry.ClusterSize, MaximumDirectoryBytes));
        var cluster = geometry.RootCluster;
        var visited = new HashSet<uint>();
        var maximumClusters = Math.Max(1, MaximumDirectoryBytes / geometry.ClusterSize);

        for (var count = 0; count < maximumClusters; count++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(cluster) ||
                !TryGetClusterOffset(geometry, cluster, out var clusterOffset))
                return null;

            var buffer = new byte[geometry.ClusterSize];
            if (!await TryReadExactlyAtAsync(
                    stream, (long)clusterOffset, buffer, sourceLength, cancellationToken))
                return null;

            await output.WriteAsync(buffer, cancellationToken);
            if (ContainsEndMarker(buffer))
                return output.ToArray();

            var relativeFatOffset = (ulong)cluster * 4;
            if (relativeFatOffset > geometry.FatByteLength ||
                4 > geometry.FatByteLength - relativeFatOffset)
                return null;

            var fatEntryOffset = geometry.FatByteOffset + relativeFatOffset;
            var fatEntry = new byte[4];
            if (!await TryReadExactlyAtAsync(
                    stream, (long)fatEntryOffset, fatEntry, sourceLength, cancellationToken))
                return null;

            var next = BinaryPrimitives.ReadUInt32LittleEndian(fatEntry) & 0x0FFFFFFF;
            if (next >= 0x0FFFFFF8)
                return output.ToArray();
            if (next < 2 || (ulong)next >= (ulong)geometry.ClusterCount + 2)
                return null;
            cluster = next;
        }

        // 没有遇到目录终止项或 FAT 链尾时，目录并不完整，不发布可能误判的记录。
        return null;
    }

    private static IReadOnlyList<RecoveredFile> ParsePreviousVolumeFiles(
        byte[] directory,
        PreviousVolumeGeometry geometry,
        long sourceLength)
    {
        var files = new List<RecoveredFile>();
        for (var offset = 0; offset + 32 <= directory.Length; offset += 32)
        {
            var entryType = directory[offset];
            if (entryType == 0)
                break;
            if ((entryType & 0x7F) != FileEntryType)
                continue;

            var isActive = (entryType & InUseFlag) != 0;
            var secondaryCount = directory[offset + 1];
            var entrySetLength = (secondaryCount + 1) * 32;
            if (secondaryCount < 2 || entrySetLength > directory.Length - offset)
                continue;

            var entrySet = directory.AsSpan(offset, entrySetLength);
            if (!HasValidEntrySetChecksum(entrySet, isActive))
                continue;

            var attributes = BinaryPrimitives.ReadUInt16LittleEndian(entrySet.Slice(4, 2));
            if ((attributes & DirectoryAttribute) != 0)
                continue;

            var streamOffset = offset + 32;
            var streamType = directory[streamOffset];
            if ((streamType & 0x7F) != StreamEntryType ||
                ((streamType & InUseFlag) != 0) != isActive)
                continue;

            var flags = directory[streamOffset + 1];
            if ((flags & NoFatChainFlag) == 0)
                continue;

            var nameLength = directory[streamOffset + 3];
            var requiredNameEntries = (nameLength + 14) / 15;
            if (nameLength == 0 || requiredNameEntries == 0 ||
                requiredNameEntries > secondaryCount - 1 ||
                !HasExpectedNameEntries(
                    directory, streamOffset + 32, requiredNameEntries, isActive))
                continue;

            var validDataLength = BinaryPrimitives.ReadUInt64LittleEndian(
                directory.AsSpan(streamOffset + 8, 8));
            var firstCluster = BinaryPrimitives.ReadUInt32LittleEndian(
                directory.AsSpan(streamOffset + 20, 4));
            var dataLength = BinaryPrimitives.ReadUInt64LittleEndian(
                directory.AsSpan(streamOffset + 24, 8));
            if (dataLength == 0 || dataLength > long.MaxValue ||
                validDataLength > dataLength ||
                firstCluster < 2 || (ulong)firstCluster >= (ulong)geometry.ClusterCount + 2)
                continue;

            var clustersNeeded = (dataLength + (ulong)geometry.ClusterSize - 1) /
                                 (ulong)geometry.ClusterSize;
            var firstClusterIndex = (ulong)firstCluster - 2;
            if (clustersNeeded == 0 || firstClusterIndex >= geometry.ClusterCount ||
                clustersNeeded > (ulong)geometry.ClusterCount - firstClusterIndex ||
                !TryGetClusterOffset(geometry, firstCluster, out var dataOffset) ||
                dataOffset > geometry.VolumeBytes || dataLength > geometry.VolumeBytes - dataOffset ||
                dataOffset > (ulong)sourceLength || dataLength > (ulong)sourceLength - dataOffset)
                continue;

            var name = SanitizeFileName(ReadFileName(
                directory, streamOffset + 32, requiredNameEntries, nameLength));
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var state = isActive && validDataLength == dataLength
                ? RecoveryState.Good
                : RecoveryState.Partial;
            var origin = isActive ? "原有文件" : "已删除文件";
            files.Add(new RecoveredFile(
                name,
                $"格式化前 exFAT/{origin}",
                (long)dataLength,
                GetCategory(name),
                state,
                (long)dataOffset,
                $"格式化前 exFAT {(isActive ? "活动" : "删除")}目录项")
            {
                RecoveryExtents = [new RecoveryExtent((long)dataOffset, (long)dataLength)]
            });

            offset += entrySetLength - 32;
        }

        return files;
    }

    private static bool HasExpectedNameEntries(
        byte[] directory,
        int offset,
        int entryCount,
        bool isActive)
    {
        for (var index = 0; index < entryCount; index++, offset += 32)
        {
            if (offset + 32 > directory.Length ||
                (directory[offset] & 0x7F) != FileNameEntryType ||
                (((directory[offset] & InUseFlag) != 0) != isActive))
                return false;
        }
        return true;
    }

    private static bool HasValidEntrySetChecksum(ReadOnlySpan<byte> entrySet, bool isActive)
    {
        var expected = BinaryPrimitives.ReadUInt16LittleEndian(entrySet.Slice(2, 2));
        ushort checksum = 0;
        for (var index = 0; index < entrySet.Length; index++)
        {
            if (index is 2 or 3)
                continue;

            var value = entrySet[index];
            // 删除文件时 exFAT 只清除各目录项的 InUse 位，原校验和仍基于活动类型。
            if (!isActive && index % 32 == 0)
                value |= InUseFlag;
            checksum = (ushort)(((checksum & 1) != 0 ? 0x8000 : 0) +
                                (checksum >> 1) + value);
        }
        return checksum == expected;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = new HashSet<char>("<>:\"/\\|?*".Concat(
            Enumerable.Range(0, 32).Select(value => (char)value)));
        var sanitized = new string(name.Select(character =>
            invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return sanitized is "." or ".." ? $"恢复文件_{sanitized.Length}" : sanitized;
    }

    private static bool TryGetClusterOffset(
        PreviousVolumeGeometry geometry,
        uint cluster,
        out ulong offset)
    {
        offset = 0;
        if (cluster < 2 || (ulong)cluster >= (ulong)geometry.ClusterCount + 2)
            return false;

        var sector = (ulong)geometry.ClusterHeapOffset +
                     ((ulong)cluster - 2) * (uint)geometry.SectorsPerCluster;
        return TryMultiply(sector, (ulong)geometry.BytesPerSector, out offset) &&
               offset <= geometry.VolumeBytes;
    }

    private static bool TryMultiply(ulong left, ulong right, out ulong value)
    {
        if (right != 0 && left > ulong.MaxValue / right)
        {
            value = 0;
            return false;
        }
        value = left * right;
        return true;
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

    private static async Task<bool> TryReadExactlyAtAsync(
        FileStream stream,
        long offset,
        byte[] buffer,
        long sourceLength,
        CancellationToken cancellationToken)
    {
        if (offset < 0 || offset > sourceLength || buffer.Length > sourceLength - offset)
            return false;

        stream.Position = offset;
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (count == 0)
                return false;
            read += count;
        }
        return true;
    }

    private sealed record PreviousVolumeGeometry(
        int BytesPerSector,
        int SectorsPerCluster,
        int ClusterSize,
        uint ClusterHeapOffset,
        uint ClusterCount,
        uint RootCluster,
        ulong FatByteOffset,
        ulong FatByteLength,
        ulong VolumeBytes);
}
