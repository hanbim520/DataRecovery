using System.Buffers.Binary;
using System.Text;
using DataRecovery.Core.Models;

namespace DataRecovery.Core.FileSystems;

/// <summary>
/// 从 NTFS 的 $MFT 中读取仍保留有效属性的已删除 FILE 记录。
/// 支持驻留数据，以及未压缩、未加密的非驻留 runlist（包括碎片和稀疏区段）。
/// </summary>
public sealed class NtfsDeletedFileScanner
{
    private const uint FileNameAttribute = 0x30;
    private const uint DataAttribute = 0x80;
    private const uint AttributeListAttribute = 0x20;
    private const uint EndAttribute = 0xFFFFFFFF;
    private const ushort InUseRecordFlag = 0x0001;
    private const ushort DirectoryRecordFlag = 0x0002;
    private const ushort CompressedAttributeFlag = 0x0001;
    private const ushort EncryptedAttributeFlag = 0x4000;
    private const int MaximumRecordSize = 1024 * 1024;
    private const int ScanBatchSize = 4 * 1024 * 1024;
    private const long MaximumRecordCount = 16 * 1024 * 1024;

    public Task<IReadOnlyList<RecoveredFile>> ScanAsync(
        FileStream stream,
        CancellationToken cancellationToken = default) =>
        ScanAsync((Stream)stream, cancellationToken);

    public async Task<IReadOnlyList<RecoveredFile>> ScanAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
            return Array.Empty<RecoveredFile>();

        var boot = new byte[512];
        if (!await TryReadExactlyAtAsync(stream, 0, boot, cancellationToken) ||
            Encoding.ASCII.GetString(boot, 3, 8) != "NTFS    ")
            return Array.Empty<RecoveredFile>();

        if (!TryReadBootGeometry(boot, out var geometry))
            return Array.Empty<RecoveredFile>();

        var streamLength = TryGetLength(stream);
        var readableLength = streamLength > 0
            ? Math.Min(streamLength, geometry.VolumeSize)
            : geometry.VolumeSize;
        if (readableLength < 512 ||
            geometry.MftOffset > readableLength - geometry.RecordSize)
            return Array.Empty<RecoveredFile>();

        var firstRecord = new byte[geometry.RecordSize];
        if (!await TryReadExactlyAtAsync(
                stream, geometry.MftOffset, firstRecord, cancellationToken) ||
            !TryApplyUsaFixup(firstRecord, geometry.BytesPerSector, out _))
            return Array.Empty<RecoveredFile>();

        var mftData = TryReadUnnamedNonResidentData(
            firstRecord, geometry.ClusterSize, readableLength);
        if (mftData is null || mftData.Runs.Count == 0 ||
            mftData.Runs[0].LogicalOffset != 0 || mftData.Runs.Any(run => run.IsSparse))
            return Array.Empty<RecoveredFile>();

        var initializedSize = mftData.InitializedSize > 0
            ? Math.Min(mftData.Size, mftData.InitializedSize)
            : mftData.Size;
        var recordCount = Math.Min(
            initializedSize / geometry.RecordSize,
            MaximumRecordCount);
        if (recordCount <= 0)
            return Array.Empty<RecoveredFile>();

        var files = new List<RecoveredFile>();
        var recordsPerBatch = Math.Max(1, ScanBatchSize / geometry.RecordSize);
        var batch = new byte[checked(recordsPerBatch * geometry.RecordSize)];

        for (long firstRecordNumber = 0;
             firstRecordNumber < recordCount;
             firstRecordNumber += recordsPerBatch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchRecordCount = (int)Math.Min(recordsPerBatch, recordCount - firstRecordNumber);
            var byteCount = checked(batchRecordCount * geometry.RecordSize);
            var logicalOffset = checked(firstRecordNumber * geometry.RecordSize);
            if (!await TryReadVirtualAsync(
                    stream,
                    mftData.Runs,
                    logicalOffset,
                    batch.AsMemory(0, byteCount),
                    readableLength,
                    cancellationToken))
                break;

            ParseRecordBatch(
                batch,
                batchRecordCount,
                firstRecordNumber,
                geometry,
                mftData.Runs,
                readableLength,
                files);
        }

        return files;
    }

    private static void ParseRecordBatch(
        byte[] batch,
        int recordCount,
        long firstRecordNumber,
        NtfsGeometry geometry,
        IReadOnlyList<NtfsRun> mftRuns,
        long readableLength,
        ICollection<RecoveredFile> files)
    {
        for (var index = 0; index < recordCount; index++)
        {
            var record = batch.AsSpan(index * geometry.RecordSize, geometry.RecordSize);
            if (!TryApplyUsaFixup(record, geometry.BytesPerSector, out var usa))
                continue;

            var flags = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(22, 2));
            if ((flags & InUseRecordFlag) != 0 || (flags & DirectoryRecordFlag) != 0)
                continue;

            // 扩展记录中的属性需要 ATTRIBUTE_LIST 才能完整拼接，不能当作独立文件恢复。
            if (BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(32, 8)) != 0)
                continue;

            var recordLogicalOffset = checked(
                (firstRecordNumber + index) * geometry.RecordSize);
            var recovered = TryParseDeletedRecord(
                record,
                recordLogicalOffset,
                geometry,
                usa,
                mftRuns,
                readableLength);
            if (recovered is not null)
                files.Add(recovered);
        }
    }

    private static RecoveredFile? TryParseDeletedRecord(
        Span<byte> record,
        long recordLogicalOffset,
        NtfsGeometry geometry,
        UsaFixup usa,
        IReadOnlyList<NtfsRun> mftRuns,
        long readableLength)
    {
        var firstAttribute = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(20, 2));
        var bytesInUse = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(24, 4));
        if (firstAttribute < 48 || firstAttribute > record.Length - 4 ||
            bytesInUse < firstAttribute + 4 || bytesInUse > record.Length)
            return null;

        FileNameValue? bestName = null;
        DataValue? data = null;
        var hasAttributeList = false;
        var attributeOffset = (int)firstAttribute;
        var attributeLimit = (int)bytesInUse;
        while (attributeOffset <= attributeLimit - 4)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(attributeOffset, 4));
            if (type == EndAttribute)
                break;
            if (attributeOffset > attributeLimit - 16)
                return null;

            var attributeLengthValue = BinaryPrimitives.ReadUInt32LittleEndian(
                record.Slice(attributeOffset + 4, 4));
            if (attributeLengthValue < 16 || attributeLengthValue > int.MaxValue)
                return null;
            var attributeLength = (int)attributeLengthValue;
            if ((attributeLength & 7) != 0 || attributeLength > attributeLimit - attributeOffset)
                return null;

            var nonResident = record[attributeOffset + 8] != 0;
            var nameLength = record[attributeOffset + 9];
            if (type == AttributeListAttribute)
            {
                hasAttributeList = true;
            }
            else if (type == FileNameAttribute && !nonResident)
            {
                var candidate = TryReadFileName(record, attributeOffset, attributeLength);
                if (candidate is not null &&
                    (bestName is null || candidate.Rank > bestName.Rank))
                    bestName = candidate;
            }
            else if (type == DataAttribute && nameLength == 0 && data is null)
            {
                data = nonResident
                    ? TryReadNonResidentData(
                        record, attributeOffset, attributeLength,
                        geometry.ClusterSize, readableLength)
                    : TryReadResidentData(record, attributeOffset, attributeLength);
            }

            attributeOffset += attributeLength;
        }

        if (hasAttributeList || bestName is null || data is null || data.Size <= 0)
            return null;

        List<RecoveryExtent>? recoveryExtents;
        if (data is ResidentData resident)
        {
            recoveryExtents = BuildResidentRecoveryExtents(
                mftRuns,
                recordLogicalOffset,
                resident.ValueOffset,
                resident.Size,
                geometry.RecordSize,
                geometry.BytesPerSector,
                usa,
                readableLength);
        }
        else
        {
            recoveryExtents = BuildNonResidentRecoveryExtents(
                ((NonResidentData)data).Runs,
                data.Size,
                readableLength);
        }

        if (recoveryExtents is null || SumExtentLengths(recoveryExtents) != data.Size)
            return null;

        var offset = recoveryExtents.FirstOrDefault(extent => !extent.IsSparse)?.SourceOffset ?? 0;
        var name = SanitizeFileName(bestName.Name);
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return new RecoveredFile(
            name,
            "已删除文件/NTFS MFT",
            data.Size,
            GetCategory(name),
            data is ResidentData ? RecoveryState.Excellent : RecoveryState.Good,
            offset,
            "NTFS 删除 MFT 记录")
        {
            RecoveryExtents = recoveryExtents
        };
    }

    private static FileNameValue? TryReadFileName(
        ReadOnlySpan<byte> record,
        int attributeOffset,
        int attributeLength)
    {
        if (attributeLength < 24)
            return null;
        var valueLengthValue = BinaryPrimitives.ReadUInt32LittleEndian(
            record.Slice(attributeOffset + 16, 4));
        var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(
            record.Slice(attributeOffset + 20, 2));
        if (valueLengthValue > int.MaxValue)
            return null;
        var valueLength = (int)valueLengthValue;
        if (valueOffset < 24 || valueLength < 66 ||
            valueOffset > attributeLength || valueLength > attributeLength - valueOffset)
            return null;

        var value = record.Slice(attributeOffset + valueOffset, valueLength);
        var characterCount = value[64];
        var namespaceValue = value[65];
        var nameByteCount = checked(characterCount * 2);
        if (characterCount == 0 || nameByteCount > value.Length - 66)
            return null;

        var name = Encoding.Unicode.GetString(value.Slice(66, nameByteCount));
        var rank = namespaceValue switch
        {
            3 => 4, // Win32 + DOS
            1 => 3, // Win32
            0 => 2, // POSIX
            2 => 1, // DOS 8.3
            _ => 0
        };
        return rank == 0 ? null : new FileNameValue(name, rank);
    }

    private static ResidentData? TryReadResidentData(
        ReadOnlySpan<byte> record,
        int attributeOffset,
        int attributeLength)
    {
        if (attributeLength < 24)
            return null;
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(attributeOffset + 12, 2));
        if ((flags & (CompressedAttributeFlag | EncryptedAttributeFlag)) != 0)
            return null;

        var valueLengthValue = BinaryPrimitives.ReadUInt32LittleEndian(
            record.Slice(attributeOffset + 16, 4));
        var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(
            record.Slice(attributeOffset + 20, 2));
        if (valueLengthValue == 0 || valueLengthValue > int.MaxValue)
            return null;
        var valueLength = (int)valueLengthValue;
        if (valueOffset < 24 || valueOffset > attributeLength ||
            valueLength > attributeLength - valueOffset)
            return null;

        return new ResidentData(attributeOffset + valueOffset, valueLength);
    }

    private static NonResidentData? TryReadUnnamedNonResidentData(
        ReadOnlySpan<byte> record,
        int clusterSize,
        long readableLength)
    {
        var firstAttribute = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(20, 2));
        var bytesInUse = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(24, 4));
        if (firstAttribute < 48 || firstAttribute > record.Length - 4 ||
            bytesInUse < firstAttribute + 4 || bytesInUse > record.Length)
            return null;

        var offset = (int)firstAttribute;
        var limit = (int)bytesInUse;
        while (offset <= limit - 4)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(offset, 4));
            if (type == EndAttribute)
                break;
            if (offset > limit - 16)
                return null;
            var lengthValue = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(offset + 4, 4));
            if (lengthValue < 16 || lengthValue > int.MaxValue)
                return null;
            var length = (int)lengthValue;
            if ((length & 7) != 0 || length > limit - offset)
                return null;

            if (type == DataAttribute && record[offset + 8] != 0 && record[offset + 9] == 0)
                return TryReadNonResidentData(record, offset, length, clusterSize, readableLength);
            offset += length;
        }
        return null;
    }

    private static NonResidentData? TryReadNonResidentData(
        ReadOnlySpan<byte> record,
        int attributeOffset,
        int attributeLength,
        int clusterSize,
        long readableLength)
    {
        if (attributeLength < 64)
            return null;
        var attributeFlags = BinaryPrimitives.ReadUInt16LittleEndian(
            record.Slice(attributeOffset + 12, 2));
        var compressionUnit = BinaryPrimitives.ReadUInt16LittleEndian(
            record.Slice(attributeOffset + 34, 2));
        if ((attributeFlags & (CompressedAttributeFlag | EncryptedAttributeFlag)) != 0 ||
            compressionUnit != 0)
            return null;

        var startingVcnValue = BinaryPrimitives.ReadUInt64LittleEndian(
            record.Slice(attributeOffset + 16, 8));
        var lastVcnValue = BinaryPrimitives.ReadUInt64LittleEndian(
            record.Slice(attributeOffset + 24, 8));
        var runListOffset = BinaryPrimitives.ReadUInt16LittleEndian(
            record.Slice(attributeOffset + 32, 2));
        var realSizeValue = BinaryPrimitives.ReadUInt64LittleEndian(
            record.Slice(attributeOffset + 48, 8));
        var initializedSizeValue = BinaryPrimitives.ReadUInt64LittleEndian(
            record.Slice(attributeOffset + 56, 8));
        if (startingVcnValue != 0 || lastVcnValue < startingVcnValue ||
            lastVcnValue > long.MaxValue || realSizeValue > long.MaxValue ||
            initializedSizeValue > long.MaxValue || runListOffset < 64 ||
            runListOffset >= attributeLength)
            return null;

        var runList = record.Slice(
            attributeOffset + runListOffset,
            attributeLength - runListOffset);
        if (!TryParseRunList(
                runList,
                (long)startingVcnValue,
                (long)lastVcnValue,
                clusterSize,
                readableLength,
                out var runs))
            return null;

        return new NonResidentData(
            (long)realSizeValue,
            (long)initializedSizeValue,
            runs);
    }

    private static bool TryParseRunList(
        ReadOnlySpan<byte> runList,
        long startingVcn,
        long lastVcn,
        int clusterSize,
        long readableLength,
        out IReadOnlyList<NtfsRun> runs)
    {
        var result = new List<NtfsRun>();
        var position = 0;
        var currentVcn = startingVcn;
        long currentLcn = 0;
        var terminated = false;
        try
        {
            while (position < runList.Length)
            {
                var header = runList[position++];
                if (header == 0)
                {
                    terminated = true;
                    break;
                }

                var lengthByteCount = header & 0x0F;
                var offsetByteCount = header >> 4;
                if (lengthByteCount is < 1 or > 8 || offsetByteCount > 8 ||
                    position > runList.Length - lengthByteCount - offsetByteCount)
                    break;

                var clusterCountValue = ReadUnsignedLittleEndian(
                    runList.Slice(position, lengthByteCount));
                position += lengthByteCount;
                if (clusterCountValue == 0 || clusterCountValue > long.MaxValue)
                    break;
                var clusterCount = (long)clusterCountValue;

                var sparse = offsetByteCount == 0;
                if (!sparse)
                {
                    var delta = ReadSignedLittleEndian(runList.Slice(position, offsetByteCount));
                    currentLcn = checked(currentLcn + delta);
                    if (currentLcn < 0)
                        break;
                }
                position += offsetByteCount;

                var nextVcn = checked(currentVcn + clusterCount);
                var logicalOffset = checked(currentVcn * clusterSize);
                var length = checked(clusterCount * clusterSize);
                _ = checked(nextVcn * clusterSize);
                var sourceOffset = sparse ? 0 : checked(currentLcn * clusterSize);
                if (!sparse && (sourceOffset > readableLength || length > readableLength - sourceOffset))
                    break;

                result.Add(new NtfsRun(logicalOffset, sourceOffset, length, sparse));
                currentVcn = nextVcn;
                if (currentVcn - 1 > lastVcn)
                    break;
            }
        }
        catch (OverflowException)
        {
            runs = Array.Empty<NtfsRun>();
            return false;
        }

        if (!terminated || result.Count == 0 || currentVcn - 1 != lastVcn)
        {
            runs = Array.Empty<NtfsRun>();
            return false;
        }

        runs = result;
        return true;
    }

    private static List<RecoveryExtent>? BuildNonResidentRecoveryExtents(
        IReadOnlyList<NtfsRun> runs,
        long size,
        long readableLength)
    {
        var extents = new List<RecoveryExtent>();
        long remaining = size;
        long expectedLogicalOffset = 0;
        foreach (var run in runs)
        {
            if (remaining == 0)
                break;
            if (run.LogicalOffset != expectedLogicalOffset)
                return null;
            var length = Math.Min(run.Length, remaining);
            if (!run.IsSparse &&
                (run.SourceOffset > readableLength || length > readableLength - run.SourceOffset))
                return null;
            AppendExtent(extents, new RecoveryExtent(run.SourceOffset, length, run.IsSparse));
            remaining -= length;
            expectedLogicalOffset = checked(expectedLogicalOffset + run.Length);
        }
        return remaining == 0 ? extents : null;
    }

    private static List<RecoveryExtent>? BuildResidentRecoveryExtents(
        IReadOnlyList<NtfsRun> mftRuns,
        long recordLogicalOffset,
        int valueOffset,
        long size,
        int recordSize,
        int bytesPerSector,
        UsaFixup usa,
        long readableLength)
    {
        if (valueOffset < 0 || size <= 0 || valueOffset > recordSize - size)
            return null;

        var extents = new List<RecoveryExtent>();
        long cursor = valueOffset;
        var end = checked(valueOffset + size);
        var sectorCount = recordSize / bytesPerSector;
        for (var sector = 0; sector < sectorCount && cursor < end; sector++)
        {
            var trailer = (sector + 1L) * bytesPerSector - 2;
            if (cursor < trailer)
            {
                var normalEnd = Math.Min(end, trailer);
                if (!TryAppendMappedMftRange(
                        extents, mftRuns,
                        checked(recordLogicalOffset + cursor),
                        normalEnd - cursor,
                        readableLength))
                    return null;
                cursor = normalEnd;
            }

            if (cursor < end && cursor < trailer + 2 && end > trailer)
            {
                var overlapStart = Math.Max(cursor, trailer);
                var overlapEnd = Math.Min(end, trailer + 2);
                var replacementOffset = usa.ArrayOffset + 2L + sector * 2L +
                                        (overlapStart - trailer);
                if (!TryAppendMappedMftRange(
                        extents, mftRuns,
                        checked(recordLogicalOffset + replacementOffset),
                        overlapEnd - overlapStart,
                        readableLength))
                    return null;
                cursor = overlapEnd;
            }
        }

        if (cursor < end && !TryAppendMappedMftRange(
                extents, mftRuns,
                checked(recordLogicalOffset + cursor),
                end - cursor,
                readableLength))
            return null;
        return extents;
    }

    private static bool TryAppendMappedMftRange(
        List<RecoveryExtent> extents,
        IReadOnlyList<NtfsRun> runs,
        long logicalOffset,
        long length,
        long readableLength)
    {
        var remaining = length;
        var current = logicalOffset;
        foreach (var run in runs)
        {
            if (remaining == 0)
                return true;
            if (current < run.LogicalOffset)
                return false;
            if (current >= run.LogicalOffset + run.Length)
                continue;
            if (run.IsSparse)
                return false;

            var withinRun = current - run.LogicalOffset;
            var partLength = Math.Min(remaining, run.Length - withinRun);
            var sourceOffset = checked(run.SourceOffset + withinRun);
            if (sourceOffset > readableLength || partLength > readableLength - sourceOffset)
                return false;
            AppendExtent(extents, new RecoveryExtent(sourceOffset, partLength));
            current += partLength;
            remaining -= partLength;
        }
        return remaining == 0;
    }

    private static async Task<bool> TryReadVirtualAsync(
        Stream stream,
        IReadOnlyList<NtfsRun> runs,
        long logicalOffset,
        Memory<byte> destination,
        long readableLength,
        CancellationToken cancellationToken)
    {
        var remaining = destination.Length;
        var destinationOffset = 0;
        var current = logicalOffset;
        foreach (var run in runs)
        {
            if (remaining == 0)
                return true;
            if (current < run.LogicalOffset)
                return false;
            if (current >= run.LogicalOffset + run.Length)
                continue;
            if (run.IsSparse)
                return false;

            var withinRun = current - run.LogicalOffset;
            var count = (int)Math.Min(remaining, run.Length - withinRun);
            var sourceOffset = checked(run.SourceOffset + withinRun);
            if (sourceOffset > readableLength || count > readableLength - sourceOffset ||
                !await TryReadExactlyAtAsync(
                    stream,
                    sourceOffset,
                    destination.Slice(destinationOffset, count),
                    cancellationToken))
                return false;
            current += count;
            destinationOffset += count;
            remaining -= count;
        }
        return remaining == 0;
    }

    private static bool TryApplyUsaFixup(
        Span<byte> record,
        int bytesPerSector,
        out UsaFixup fixup)
    {
        fixup = default;
        if (record.Length < 48 || record.Length % bytesPerSector != 0 ||
            !record[..4].SequenceEqual("FILE"u8))
            return false;

        var arrayOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(4, 2));
        var arrayCount = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(6, 2));
        var sectorCount = record.Length / bytesPerSector;
        if (arrayCount != sectorCount + 1 || arrayOffset < 8 ||
            arrayOffset > record.Length - arrayCount * 2)
            return false;

        var sequence = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(arrayOffset, 2));
        for (var sector = 0; sector < sectorCount; sector++)
        {
            var trailerOffset = (sector + 1) * bytesPerSector - 2;
            if (BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(trailerOffset, 2)) != sequence)
                return false;
        }

        for (var sector = 0; sector < sectorCount; sector++)
        {
            var trailerOffset = (sector + 1) * bytesPerSector - 2;
            record.Slice(arrayOffset + 2 + sector * 2, 2).CopyTo(
                record.Slice(trailerOffset, 2));
        }
        fixup = new UsaFixup(arrayOffset);
        return true;
    }

    private static bool TryReadBootGeometry(
        ReadOnlySpan<byte> boot,
        out NtfsGeometry geometry)
    {
        geometry = default;
        var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(11, 2));
        var sectorsPerCluster = boot[13];
        if (!IsPowerOfTwo(bytesPerSector) || bytesPerSector is < 256 or > 32768 ||
            !IsPowerOfTwo(sectorsPerCluster))
            return false;

        long clusterSize;
        try
        {
            clusterSize = checked((long)bytesPerSector * sectorsPerCluster);
        }
        catch (OverflowException)
        {
            return false;
        }
        if (clusterSize <= 0 || clusterSize > 2 * 1024 * 1024)
            return false;

        var recordSizeCode = unchecked((sbyte)boot[64]);
        long recordSize;
        try
        {
            if (recordSizeCode > 0)
                recordSize = checked(recordSizeCode * clusterSize);
            else if (recordSizeCode is >= -30 and < 0)
                recordSize = 1L << -recordSizeCode;
            else
                return false;
        }
        catch (OverflowException)
        {
            return false;
        }
        if (recordSize < bytesPerSector || recordSize > MaximumRecordSize ||
            recordSize % bytesPerSector != 0 || recordSize > int.MaxValue)
            return false;

        var totalSectors = BinaryPrimitives.ReadUInt64LittleEndian(boot.Slice(40, 8));
        var mftCluster = BinaryPrimitives.ReadUInt64LittleEndian(boot.Slice(48, 8));
        if (totalSectors == 0 || totalSectors > (ulong)(long.MaxValue / bytesPerSector) ||
            mftCluster > (ulong)(long.MaxValue / clusterSize))
            return false;

        var volumeSize = checked((long)totalSectors * bytesPerSector);
        var mftOffset = checked((long)mftCluster * clusterSize);
        geometry = new NtfsGeometry(
            bytesPerSector,
            (int)clusterSize,
            (int)recordSize,
            volumeSize,
            mftOffset);
        return true;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var character in name.Trim())
            builder.Append(character is '/' or '\\' || invalid.Contains(character) ? '_' : character);
        var result = builder.ToString().TrimEnd(' ', '.');
        return result is "." or ".." ? "" : result;
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

    private static ulong ReadUnsignedLittleEndian(ReadOnlySpan<byte> value)
    {
        ulong result = 0;
        for (var index = 0; index < value.Length; index++)
            result |= (ulong)value[index] << (index * 8);
        return result;
    }

    private static long ReadSignedLittleEndian(ReadOnlySpan<byte> value)
    {
        var unsigned = ReadUnsignedLittleEndian(value);
        if (value.Length < 8 && (value[^1] & 0x80) != 0)
            unsigned |= ulong.MaxValue << (value.Length * 8);
        return unchecked((long)unsigned);
    }

    private static void AppendExtent(List<RecoveryExtent> extents, RecoveryExtent extent)
    {
        if (extent.Length <= 0)
            return;
        if (extents.Count > 0)
        {
            var previous = extents[^1];
            var contiguous = previous.IsSparse && extent.IsSparse ||
                             !previous.IsSparse && !extent.IsSparse &&
                             previous.SourceOffset <= long.MaxValue - previous.Length &&
                             previous.SourceOffset + previous.Length == extent.SourceOffset;
            if (contiguous)
            {
                extents[^1] = previous with { Length = checked(previous.Length + extent.Length) };
                return;
            }
        }
        extents.Add(extent);
    }

    private static long SumExtentLengths(IEnumerable<RecoveryExtent> extents)
    {
        long total = 0;
        foreach (var extent in extents)
            total = checked(total + extent.Length);
        return total;
    }

    private static async Task<bool> TryReadExactlyAtAsync(
        Stream stream,
        long offset,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        if (offset < 0)
            return false;
        try
        {
            stream.Position = offset;
            var read = 0;
            while (read < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer[read..], cancellationToken);
                if (count == 0)
                    return false;
                read += count;
            }
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static long TryGetLength(Stream stream)
    {
        try { return stream.Length; }
        catch (IOException) { return 0; }
        catch (NotSupportedException) { return 0; }
    }

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    private readonly record struct NtfsGeometry(
        int BytesPerSector,
        int ClusterSize,
        int RecordSize,
        long VolumeSize,
        long MftOffset);

    private readonly record struct UsaFixup(int ArrayOffset);
    private sealed record FileNameValue(string Name, int Rank);
    private abstract record DataValue(long Size);
    private sealed record ResidentData(int ValueOffset, long Length) : DataValue(Length);
    private sealed record NonResidentData(
        long Length,
        long InitializedSize,
        IReadOnlyList<NtfsRun> Runs) : DataValue(Length);
    private sealed record NtfsRun(
        long LogicalOffset,
        long SourceOffset,
        long Length,
        bool IsSparse);
}
