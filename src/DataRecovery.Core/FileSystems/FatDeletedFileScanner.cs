using System.Buffers.Binary;
using System.Text;
using DataRecovery.Core.Models;

namespace DataRecovery.Core.FileSystems;

/// <summary>
/// 从 FAT12/FAT16/FAT32 目录项中查找已删除文件，并把 FAT 簇链转换为可恢复区段。
/// 对已被清零的 FAT 链仅在后续簇仍为空闲且位于卷边界内时采用连续簇推断。
/// </summary>
public sealed class FatDeletedFileScanner
{
    private const byte DeletedMarker = 0xE5;
    private const byte LongNameAttribute = 0x0F;
    private const byte VolumeLabelAttribute = 0x08;
    private const byte DirectoryAttribute = 0x10;
    private const int DirectoryEntrySize = 32;
    private const int MaximumLongNameEntries = 20;
    private const int MaximumDirectoryDepth = 64;
    private const long MaximumDirectoryBytes = 64L * 1024 * 1024;
    private const int MaximumClustersPerFile = 1_048_576;

    private static readonly int[] LongNameCharacterOffsets =
        [1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30];

    public async Task<IReadOnlyList<RecoveredFile>> ScanAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        if (!stream.CanRead || !stream.CanSeek)
            return Array.Empty<RecoveredFile>();

        var originalPosition = stream.Position;
        try
        {
            var bootSector = new byte[512];
            await ReadExactlyAtAsync(stream, 0, bootSector, null, cancellationToken);
            if (!FatGeometry.TryCreate(bootSector, TryGetLength(stream), out var geometry))
                return Array.Empty<RecoveredFile>();

            var reader = new FatVolumeReader(stream, geometry!);
            var files = new List<RecoveredFile>();
            var visitedDirectories = new HashSet<uint>();

            if (geometry!.Kind is FileSystemKind.Fat12 or FileSystemKind.Fat16)
            {
                var rootDirectory = await reader.ReadFixedRootDirectoryAsync(cancellationToken);
                await ScanDirectoryAsync(
                    reader, rootDirectory, $"{geometry.Label} 根目录", false, 0,
                    files, visitedDirectories, cancellationToken);
            }
            else
            {
                visitedDirectories.Add(geometry.RootCluster);
                var rootDirectory = await reader.ReadDirectoryChainAsync(
                    geometry.RootCluster, cancellationToken);
                await ScanDirectoryAsync(
                    reader, rootDirectory, $"{geometry.Label} 根目录", false, 0,
                    files, visitedDirectories, cancellationToken);
            }

            return files;
        }
        catch (Exception exception) when (exception is
            EndOfStreamException or InvalidDataException or OverflowException or ArgumentOutOfRangeException)
        {
            // 损坏或截断的 BPB/FAT/目录不能突破卷边界，也不应使整个扫描任务失败。
            return Array.Empty<RecoveredFile>();
        }
        finally
        {
            if (stream.CanSeek)
                stream.Position = originalPosition;
        }
    }

    private static async Task ScanDirectoryAsync(
        FatVolumeReader reader,
        byte[] directory,
        string directoryPath,
        bool ancestorDeleted,
        int depth,
        List<RecoveredFile> files,
        HashSet<uint> visitedDirectories,
        CancellationToken cancellationToken)
    {
        if (depth > MaximumDirectoryDepth)
            return;

        var pendingLongNameEntries = new List<byte[]>(MaximumLongNameEntries);
        for (var offset = 0; offset + DirectoryEntrySize <= directory.Length; offset += DirectoryEntrySize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = directory.AsSpan(offset, DirectoryEntrySize);
            var firstByte = entry[0];
            if (firstByte == 0x00)
                break;

            var attributes = entry[11];
            if (attributes == LongNameAttribute)
            {
                if (pendingLongNameEntries.Count < MaximumLongNameEntries)
                    pendingLongNameEntries.Add(entry.ToArray());
                else
                    pendingLongNameEntries.Clear();
                continue;
            }

            var deleted = firstByte == DeletedMarker;
            if ((attributes & VolumeLabelAttribute) != 0 || (attributes & 0xC0) != 0)
            {
                pendingLongNameEntries.Clear();
                continue;
            }

            var shortNameBytes = entry[..11];
            var shortName = ReadShortName(shortNameBytes, deleted);
            var longName = ReadLongName(pendingLongNameEntries, shortNameBytes, deleted);
            pendingLongNameEntries.Clear();

            var name = SanitizeName(longName ?? shortName);
            if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
                continue;

            var firstCluster = ReadFirstCluster(entry, reader.Geometry.Kind);
            var isDirectory = (attributes & DirectoryAttribute) != 0;
            if (isDirectory)
            {
                if (!reader.Geometry.IsDataCluster(firstCluster) ||
                    !visitedDirectories.Add(firstCluster))
                    continue;

                var childDirectory = await reader.ReadDirectoryChainAsync(
                    firstCluster, cancellationToken);
                await ScanDirectoryAsync(
                    reader,
                    childDirectory,
                    $"{directoryPath}/{name}",
                    ancestorDeleted || deleted,
                    depth + 1,
                    files,
                    visitedDirectories,
                    cancellationToken);
                continue;
            }

            if (!deleted && !ancestorDeleted)
                continue;

            var size = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(28, 4));
            if (size == 0 || !reader.Geometry.IsDataCluster(firstCluster))
                continue;

            var chain = await reader.ResolveFileChainAsync(firstCluster, size, cancellationToken);
            if (chain.Clusters.Count == 0)
                continue;

            var extents = reader.BuildRecoveryExtents(chain.Clusters, size);
            if (extents.Count == 0)
                continue;

            var state = chain.IsComplete
                ? chain.UsedContiguousInference || !chain.HasExpectedTerminator
                    ? RecoveryState.Good
                    : RecoveryState.Excellent
                : RecoveryState.Partial;
            var signature = ancestorDeleted && !deleted
                ? $"{reader.Geometry.Label} 已删除目录内文件"
                : $"{reader.Geometry.Label} 删除目录项";

            files.Add(new RecoveredFile(
                name,
                $"已删除文件/{directoryPath}",
                size,
                GetCategory(name),
                state,
                extents[0].SourceOffset,
                signature)
            {
                RecoveryExtents = extents
            });
        }
    }

    private static uint ReadFirstCluster(ReadOnlySpan<byte> entry, FileSystemKind kind)
    {
        var low = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(26, 2));
        if (kind is not FileSystemKind.Fat32)
            return low;

        var high = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(20, 2));
        return ((uint)high << 16) | low;
    }

    private static string ReadShortName(ReadOnlySpan<byte> shortName, bool deleted)
    {
        Span<byte> baseNameBytes = stackalloc byte[8];
        shortName[..8].CopyTo(baseNameBytes);
        if (deleted)
            baseNameBytes[0] = (byte)'_';
        else if (baseNameBytes[0] == 0x05)
            baseNameBytes[0] = 0xE5;

        var baseName = Encoding.Latin1.GetString(baseNameBytes).TrimEnd(' ');
        var extension = Encoding.Latin1.GetString(shortName.Slice(8, 3)).TrimEnd(' ');
        return extension.Length == 0 ? baseName : $"{baseName}.{extension}";
    }

    private static string? ReadLongName(
        IReadOnlyList<byte[]> entries,
        ReadOnlySpan<byte> shortName,
        bool deleted)
    {
        if (entries.Count == 0 || entries.Count > MaximumLongNameEntries)
            return null;

        var checksum = entries[0][13];
        if (entries.Any(entry =>
                entry.Length != DirectoryEntrySize ||
                entry[11] != LongNameAttribute ||
                entry[12] != 0 ||
                entry[13] != checksum ||
                BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(26, 2)) != 0))
            return null;

        IEnumerable<byte[]> orderedEntries;
        if (deleted)
        {
            if (entries.Any(entry => entry[0] != DeletedMarker) ||
                !DeletedShortNameCouldMatchChecksum(shortName, checksum))
                return null;

            // 删除时 LFN 的序号字节也变为 E5；磁盘中的物理顺序仍是末段到首段。
            orderedEntries = entries.Reverse();
        }
        else
        {
            if (!TryOrderActiveLongNameEntries(entries, checksum, shortName, out var ordered))
                return null;
            orderedEntries = ordered;
        }

        var builder = new StringBuilder(entries.Count * 13);
        foreach (var entry in orderedEntries)
        {
            foreach (var characterOffset in LongNameCharacterOffsets)
            {
                var value = BinaryPrimitives.ReadUInt16LittleEndian(
                    entry.AsSpan(characterOffset, 2));
                if (value == 0x0000)
                    return builder.Length == 0 ? null : builder.ToString();
                if (value == 0xFFFF)
                    continue;
                if (char.IsSurrogate((char)value) || char.IsControl((char)value))
                    return null;
                builder.Append((char)value);
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static bool TryOrderActiveLongNameEntries(
        IReadOnlyList<byte[]> entries,
        byte checksum,
        ReadOnlySpan<byte> shortName,
        out IReadOnlyList<byte[]> ordered)
    {
        ordered = Array.Empty<byte[]>();
        if (ComputeShortNameChecksum(shortName) != checksum)
            return false;

        var firstOrdinal = entries[0][0];
        var expectedCount = firstOrdinal & 0x1F;
        if ((firstOrdinal & 0x40) == 0 || expectedCount != entries.Count)
            return false;

        for (var index = 0; index < entries.Count; index++)
        {
            var expectedOrdinal = entries.Count - index;
            var actualOrdinal = entries[index][0] & 0x1F;
            if (actualOrdinal != expectedOrdinal ||
                (index > 0 && (entries[index][0] & 0x40) != 0))
                return false;
        }

        ordered = entries.Reverse().ToArray();
        return true;
    }

    private static bool DeletedShortNameCouldMatchChecksum(
        ReadOnlySpan<byte> deletedShortName,
        byte expectedChecksum)
    {
        Span<byte> candidate = stackalloc byte[11];
        deletedShortName.CopyTo(candidate);
        for (var firstByte = 1; firstByte <= byte.MaxValue; firstByte++)
        {
            candidate[0] = (byte)firstByte;
            if (ComputeShortNameChecksum(candidate) == expectedChecksum)
                return true;
        }
        return false;
    }

    private static byte ComputeShortNameChecksum(ReadOnlySpan<byte> shortName)
    {
        byte checksum = 0;
        for (var index = 0; index < 11; index++)
            checksum = (byte)(((checksum & 1) << 7) + (checksum >> 1) + shortName[index]);
        return checksum;
    }

    private static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var invalidCharacters = "<>:\"/\\|?*";
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
            builder.Append(char.IsControl(character) || invalidCharacters.Contains(character)
                ? '_'
                : character);
        return builder.ToString().Trim().TrimEnd('.');
    }

    private static string GetCategory(string name) =>
        Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".tif" or ".tiff" => "照片",
            ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".pdf" or ".txt" => "文档",
            ".zip" or ".rar" or ".7z" or ".gz" or ".tar" => "压缩包",
            ".exe" or ".dll" or ".msi" => "程序",
            _ => "其他"
        };

    private static long? TryGetLength(Stream stream)
    {
        try { return stream.Length; }
        catch (NotSupportedException) { return null; }
        catch (IOException) { return null; }
    }

    private static async Task ReadExactlyAtAsync(
        Stream stream,
        long offset,
        Memory<byte> destination,
        long? maximumExclusive,
        CancellationToken cancellationToken)
    {
        if (offset < 0 || destination.Length < 0 ||
            (maximumExclusive.HasValue &&
             (offset > maximumExclusive.Value || destination.Length > maximumExclusive.Value - offset)))
            throw new InvalidDataException("FAT 元数据读取超出卷边界。");

        stream.Position = offset;
        var totalRead = 0;
        while (totalRead < destination.Length)
        {
            var read = await stream.ReadAsync(destination[totalRead..], cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("FAT 元数据读取超出数据源范围。");
            totalRead += read;
        }
    }

    private sealed class FatVolumeReader(Stream stream, FatGeometry geometry)
    {
        private readonly Dictionary<uint, uint> _fatEntryCache = [];

        public FatGeometry Geometry { get; } = geometry;

        public async Task<byte[]> ReadFixedRootDirectoryAsync(CancellationToken cancellationToken)
        {
            var length = checked(Geometry.RootEntryCount * DirectoryEntrySize);
            var directory = new byte[length];
            await ReadExactlyAtAsync(
                stream, Geometry.RootDirectoryOffset, directory, Geometry.VolumeBytes,
                cancellationToken);
            return directory;
        }

        public async Task<byte[]> ReadDirectoryChainAsync(
            uint firstCluster,
            CancellationToken cancellationToken)
        {
            if (!Geometry.IsDataCluster(firstCluster))
                return Array.Empty<byte>();

            using var output = new MemoryStream();
            var visited = new HashSet<uint>();
            var cluster = firstCluster;
            while (Geometry.IsDataCluster(cluster) &&
                   visited.Add(cluster) &&
                   output.Length + Geometry.ClusterSize <= MaximumDirectoryBytes)
            {
                var buffer = new byte[Geometry.ClusterSize];
                await ReadExactlyAtAsync(
                    stream, Geometry.GetClusterOffset(cluster), buffer, Geometry.VolumeBytes,
                    cancellationToken);
                await output.WriteAsync(buffer, cancellationToken);
                if (ContainsDirectoryEndMarker(buffer))
                    break;

                var next = await ReadFatEntryAsync(cluster, cancellationToken);
                if (!Geometry.IsDataCluster(next))
                    break;
                cluster = next;
            }
            return output.ToArray();
        }

        public async Task<ClusterChain> ResolveFileChainAsync(
            uint firstCluster,
            uint fileSize,
            CancellationToken cancellationToken)
        {
            var requiredClusterCount64 =
                ((long)fileSize + Geometry.ClusterSize - 1) / Geometry.ClusterSize;
            if (requiredClusterCount64 <= 0 ||
                requiredClusterCount64 > MaximumClustersPerFile ||
                requiredClusterCount64 > Geometry.ClusterCount)
                return ClusterChain.Empty;

            var requiredClusterCount = (int)requiredClusterCount64;
            var clusters = new List<uint>(Math.Min(requiredClusterCount, 4096));
            var visited = new HashSet<uint>();
            var cluster = firstCluster;
            var usedContiguousInference = false;
            var hasExpectedTerminator = false;

            while (clusters.Count < requiredClusterCount &&
                   Geometry.IsDataCluster(cluster) &&
                   visited.Add(cluster))
            {
                clusters.Add(cluster);
                var next = await ReadFatEntryAsync(cluster, cancellationToken);
                if (clusters.Count == requiredClusterCount)
                {
                    hasExpectedTerminator = Geometry.IsEndOfChain(next);
                    if (next == 0)
                        usedContiguousInference = true;
                    break;
                }

                if (Geometry.IsDataCluster(next))
                {
                    cluster = next;
                    continue;
                }

                if (next != 0 && !Geometry.IsEndOfChain(next))
                    break;

                // 标准删除通常会清空 FAT。只推断仍标记为空闲的紧邻簇，
                // 遇到已重新分配、坏簇、保留簇或卷尾即停止并发布“部分”结果。
                usedContiguousInference = true;
                while (clusters.Count < requiredClusterCount)
                {
                    var candidate64 = (ulong)clusters[^1] + 1;
                    if (candidate64 > uint.MaxValue)
                        break;
                    var candidate = (uint)candidate64;
                    if (!Geometry.IsDataCluster(candidate) || !visited.Add(candidate))
                        break;
                    var candidateFatValue = await ReadFatEntryAsync(candidate, cancellationToken);
                    if (candidateFatValue != 0)
                        break;
                    clusters.Add(candidate);
                }
                break;
            }

            return new ClusterChain(
                clusters,
                clusters.Count == requiredClusterCount,
                usedContiguousInference,
                hasExpectedTerminator);
        }

        public IReadOnlyList<RecoveryExtent> BuildRecoveryExtents(
            IReadOnlyList<uint> clusters,
            uint fileSize)
        {
            var extents = new List<RecoveryExtent>();
            long remaining = fileSize;
            foreach (var cluster in clusters)
            {
                if (remaining <= 0)
                    break;
                var offset = Geometry.GetClusterOffset(cluster);
                var length = Math.Min(remaining, Geometry.ClusterSize);
                if (extents.Count > 0)
                {
                    var previous = extents[^1];
                    if (previous.SourceOffset + previous.Length == offset)
                    {
                        extents[^1] = previous with { Length = previous.Length + length };
                        remaining -= length;
                        continue;
                    }
                }
                extents.Add(new RecoveryExtent(offset, length));
                remaining -= length;
            }
            return extents;
        }

        private async ValueTask<uint> ReadFatEntryAsync(
            uint cluster,
            CancellationToken cancellationToken)
        {
            if (_fatEntryCache.TryGetValue(cluster, out var cached))
                return cached;

            long entryOffset;
            var entryLength = Geometry.Kind == FileSystemKind.Fat32 ? 4 : 2;
            switch (Geometry.Kind)
            {
                case FileSystemKind.Fat12:
                    entryOffset = checked(Geometry.FatOffset + cluster + cluster / 2L);
                    break;
                case FileSystemKind.Fat16:
                    entryOffset = checked(Geometry.FatOffset + cluster * 2L);
                    break;
                default:
                    entryOffset = checked(Geometry.FatOffset + cluster * 4L);
                    break;
            }

            if (entryOffset < Geometry.FatOffset ||
                entryOffset + entryLength > Geometry.FatOffset + Geometry.FatSizeBytes)
                throw new InvalidDataException("FAT 簇号超出 FAT 表边界。");

            var bytes = new byte[entryLength];
            await ReadExactlyAtAsync(
                stream, entryOffset, bytes, Geometry.VolumeBytes, cancellationToken);
            uint value = Geometry.Kind switch
            {
                FileSystemKind.Fat12 when (cluster & 1) == 0 =>
                    (uint)(BinaryPrimitives.ReadUInt16LittleEndian(bytes) & 0x0FFF),
                FileSystemKind.Fat12 =>
                    (uint)(BinaryPrimitives.ReadUInt16LittleEndian(bytes) >> 4),
                FileSystemKind.Fat16 => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
                _ => BinaryPrimitives.ReadUInt32LittleEndian(bytes) & 0x0FFFFFFF
            };
            _fatEntryCache[cluster] = value;
            return value;
        }

        private static bool ContainsDirectoryEndMarker(ReadOnlySpan<byte> cluster)
        {
            for (var offset = 0; offset + DirectoryEntrySize <= cluster.Length; offset += DirectoryEntrySize)
                if (cluster[offset] == 0x00)
                    return true;
            return false;
        }
    }

    private sealed record ClusterChain(
        IReadOnlyList<uint> Clusters,
        bool IsComplete,
        bool UsedContiguousInference,
        bool HasExpectedTerminator)
    {
        public static ClusterChain Empty { get; } =
            new(Array.Empty<uint>(), false, false, false);
    }

    private sealed record FatGeometry(
        FileSystemKind Kind,
        int BytesPerSector,
        int SectorsPerCluster,
        ushort ReservedSectorCount,
        byte FatCount,
        ushort RootEntryCount,
        uint TotalSectors,
        uint FatSizeSectors,
        uint RootDirectorySectors,
        uint FirstDataSector,
        uint ClusterCount,
        uint RootCluster,
        long VolumeBytes,
        long FatOffset,
        long FatSizeBytes,
        long RootDirectoryOffset,
        int ClusterSize)
    {
        public string Label => Kind.ToString().ToUpperInvariant();

        public bool IsDataCluster(uint cluster) =>
            cluster >= 2 && (ulong)cluster < (ulong)ClusterCount + 2;

        public bool IsEndOfChain(uint value) => Kind switch
        {
            FileSystemKind.Fat12 => value >= 0x0FF8,
            FileSystemKind.Fat16 => value >= 0xFFF8,
            _ => value >= 0x0FFFFFF8
        };

        public long GetClusterOffset(uint cluster)
        {
            if (!IsDataCluster(cluster))
                throw new InvalidDataException("无效的 FAT 数据簇号。");
            var sector = checked((long)FirstDataSector +
                                 ((long)cluster - 2) * SectorsPerCluster);
            var offset = checked(sector * BytesPerSector);
            if (offset < 0 || offset + ClusterSize > VolumeBytes)
                throw new InvalidDataException("FAT 数据簇超出卷边界。");
            return offset;
        }

        public static bool TryCreate(
            ReadOnlySpan<byte> boot,
            long? sourceLength,
            out FatGeometry? geometry)
        {
            geometry = null;
            if (boot.Length < 512 || boot[510] != 0x55 || boot[511] != 0xAA ||
                Encoding.ASCII.GetString(boot.Slice(3, 8)) == "EXFAT   ")
                return false;

            var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(11, 2));
            var sectorsPerCluster = boot[13];
            var reservedSectorCount = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(14, 2));
            var fatCount = boot[16];
            var rootEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(17, 2));
            var total16 = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(19, 2));
            var total32 = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(32, 4));
            var fat16 = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(22, 2));
            var fat32 = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(36, 4));
            var totalSectors = total16 != 0 ? total16 : total32;
            var fatSizeSectors = fat16 != 0 ? fat16 : fat32;

            if (bytesPerSector is < 128 or > 4096 ||
                (bytesPerSector & (bytesPerSector - 1)) != 0 ||
                sectorsPerCluster == 0 ||
                (sectorsPerCluster & (sectorsPerCluster - 1)) != 0 ||
                reservedSectorCount == 0 ||
                fatCount is 0 or > 4 ||
                totalSectors == 0 ||
                fatSizeSectors == 0)
                return false;

            try
            {
                var clusterSize64 = checked((long)bytesPerSector * sectorsPerCluster);
                if (clusterSize64 is <= 0 or > 32 * 1024 * 1024)
                    return false;

                var rootDirectorySectors = checked(
                    ((uint)rootEntryCount * DirectoryEntrySize + (uint)bytesPerSector - 1) /
                    (uint)bytesPerSector);
                var firstDataSector64 = checked(
                    (ulong)reservedSectorCount + (ulong)fatCount * fatSizeSectors +
                    rootDirectorySectors);
                if (firstDataSector64 >= totalSectors)
                    return false;

                var dataSectors = (ulong)totalSectors - firstDataSector64;
                var clusterCount64 = dataSectors / sectorsPerCluster;
                if (clusterCount64 == 0 || clusterCount64 > 0x0FFFFFF5)
                    return false;

                var kind = clusterCount64 < 4085
                    ? FileSystemKind.Fat12
                    : clusterCount64 < 65525
                        ? FileSystemKind.Fat16
                        : FileSystemKind.Fat32;
                if (kind == FileSystemKind.Fat32 && (rootEntryCount != 0 || fat16 != 0))
                    return false;
                if (kind is FileSystemKind.Fat12 or FileSystemKind.Fat16 &&
                    (rootEntryCount == 0 || fat16 == 0))
                    return false;

                var fatSizeBytes = checked((long)fatSizeSectors * bytesPerSector);
                var requiredFatBytes = kind switch
                {
                    FileSystemKind.Fat12 => checked((clusterCount64 + 2) * 3 / 2 + 1),
                    FileSystemKind.Fat16 => checked((clusterCount64 + 2) * 2),
                    _ => checked((clusterCount64 + 2) * 4)
                };
                if ((ulong)fatSizeBytes < requiredFatBytes)
                    return false;

                var volumeBytes = checked((long)totalSectors * bytesPerSector);
                if (sourceLength.HasValue && sourceLength.Value < volumeBytes)
                    return false;

                var fatOffset = checked((long)reservedSectorCount * bytesPerSector);
                var rootDirectoryOffset = checked(
                    ((long)reservedSectorCount + (long)fatCount * fatSizeSectors) *
                    bytesPerSector);
                var rootCluster = kind == FileSystemKind.Fat32
                    ? BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(44, 4)) & 0x0FFFFFFF
                    : 0;
                var clusterCount = (uint)clusterCount64;
                if (kind == FileSystemKind.Fat32 &&
                    (rootCluster < 2 || (ulong)rootCluster >= clusterCount64 + 2))
                    return false;

                geometry = new FatGeometry(
                    kind,
                    bytesPerSector,
                    sectorsPerCluster,
                    reservedSectorCount,
                    fatCount,
                    rootEntryCount,
                    totalSectors,
                    fatSizeSectors,
                    rootDirectorySectors,
                    (uint)firstDataSector64,
                    clusterCount,
                    rootCluster,
                    volumeBytes,
                    fatOffset,
                    fatSizeBytes,
                    rootDirectoryOffset,
                    (int)clusterSize64);
                return true;
            }
            catch (OverflowException)
            {
                return false;
            }
        }
    }
}
