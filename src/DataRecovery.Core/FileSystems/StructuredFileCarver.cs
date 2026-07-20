using System.Buffers.Binary;
using System.Text;
using DataRecovery.Core.Models;

namespace DataRecovery.Core.FileSystems;

/// <summary>
/// 识别可从文件头内部字段计算长度的常见格式。扫描结果不依赖旧文件系统，
/// 但只能安全恢复物理上连续且尚未被覆盖的数据。
/// </summary>
public static class StructuredFileCarver
{
    private const long MaximumCandidateSize = 32L * 1024 * 1024 * 1024;

    public static IReadOnlyList<RecoveredFile> FindCandidates(
        ReadOnlySpan<byte> data,
        long absoluteOffset,
        long sourceLength = 0)
    {
        var files = new List<RecoveredFile>();
        FindBmp(data, absoluteOffset, sourceLength, files);
        FindRiff(data, absoluteOffset, sourceLength, files);
        FindSevenZip(data, absoluteOffset, sourceLength, files);
        FindSqlite(data, absoluteOffset, sourceLength, files);
        FindElf(data, absoluteOffset, sourceLength, files);
        return files;
    }

    private static void FindBmp(
        ReadOnlySpan<byte> data, long absoluteOffset, long sourceLength,
        ICollection<RecoveredFile> files)
    {
        foreach (var index in FindMagicOffsets(data, "BM"u8))
        {
            var value = data[index..];
            if (value.Length < 34 || value[6] != 0 || value[7] != 0 ||
                value[8] != 0 || value[9] != 0)
                continue;
            var size = BinaryPrimitives.ReadUInt32LittleEndian(value.Slice(2, 4));
            var pixelOffset = BinaryPrimitives.ReadUInt32LittleEndian(value.Slice(10, 4));
            var dibSize = BinaryPrimitives.ReadUInt32LittleEndian(value.Slice(14, 4));
            if (size < 30 || pixelOffset < 26 || pixelOffset >= size ||
                dibSize is not (12 or 40 or 52 or 56 or 108 or 124))
                continue;

            int width;
            int height;
            ushort planes;
            ushort bitsPerPixel;
            uint compression = 0;
            if (dibSize == 12)
            {
                width = BinaryPrimitives.ReadUInt16LittleEndian(value.Slice(18, 2));
                height = BinaryPrimitives.ReadUInt16LittleEndian(value.Slice(20, 2));
                planes = BinaryPrimitives.ReadUInt16LittleEndian(value.Slice(22, 2));
                bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(value.Slice(24, 2));
            }
            else
            {
                width = BinaryPrimitives.ReadInt32LittleEndian(value.Slice(18, 4));
                height = BinaryPrimitives.ReadInt32LittleEndian(value.Slice(22, 4));
                planes = BinaryPrimitives.ReadUInt16LittleEndian(value.Slice(26, 2));
                bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(value.Slice(28, 2));
                compression = BinaryPrimitives.ReadUInt32LittleEndian(value.Slice(30, 4));
            }

            if (width is <= 0 or > 1_000_000 || height == 0 || height == int.MinValue ||
                Math.Abs(height) > 1_000_000 || planes != 1 ||
                bitsPerPixel is not (1 or 4 or 8 or 16 or 24 or 32) ||
                compression > 6 || pixelOffset < 14 + dibSize)
                continue;
            AddCandidate(files, absoluteOffset + index, size, ".bmp", "照片", "BMP 结构", sourceLength);
        }
    }

    private static void FindRiff(
        ReadOnlySpan<byte> data, long absoluteOffset, long sourceLength,
        ICollection<RecoveredFile> files)
    {
        foreach (var index in FindMagicOffsets(data, "RIFF"u8))
        {
            var value = data[index..];
            if (value.Length < 12) continue;
            var payloadSize = BinaryPrimitives.ReadUInt32LittleEndian(value.Slice(4, 4));
            var totalSize = (long)payloadSize + 8;
            var type = Encoding.ASCII.GetString(value.Slice(8, 4));
            var format = type switch
            {
                "WEBP" => (Extension: ".webp", Category: "照片", Signature: "WebP/RIFF 结构"),
                "WAVE" => (Extension: ".wav", Category: "其他", Signature: "WAV/RIFF 结构"),
                "AVI " => (Extension: ".avi", Category: "其他", Signature: "AVI/RIFF 结构"),
                _ => default
            };
            if (format.Extension is null || totalSize < 12) continue;
            AddCandidate(
                files, absoluteOffset + index, totalSize,
                format.Extension, format.Category, format.Signature, sourceLength);
        }
    }

    private static void FindSevenZip(
        ReadOnlySpan<byte> data, long absoluteOffset, long sourceLength,
        ICollection<RecoveredFile> files)
    {
        ReadOnlySpan<byte> signature = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
        foreach (var index in FindMagicOffsets(data, signature))
        {
            var value = data[index..];
            if (value.Length < 32 || value[6] != 0) continue;
            var nextHeaderOffset = BinaryPrimitives.ReadUInt64LittleEndian(value.Slice(12, 8));
            var nextHeaderSize = BinaryPrimitives.ReadUInt64LittleEndian(value.Slice(20, 8));
            ulong total;
            try { total = checked(32ul + nextHeaderOffset + nextHeaderSize); }
            catch (OverflowException) { continue; }
            if (total is < 32 or > (ulong)MaximumCandidateSize) continue;
            AddCandidate(
                files, absoluteOffset + index, (long)total,
                ".7z", "压缩包", "7-Zip 结构", sourceLength);
        }
    }

    private static void FindSqlite(
        ReadOnlySpan<byte> data, long absoluteOffset, long sourceLength,
        ICollection<RecoveredFile> files)
    {
        ReadOnlySpan<byte> signature = "SQLite format 3\0"u8;
        foreach (var index in FindMagicOffsets(data, signature))
        {
            var value = data[index..];
            if (value.Length < 100) continue;
            var encodedPageSize = BinaryPrimitives.ReadUInt16BigEndian(value.Slice(16, 2));
            var pageSize = encodedPageSize == 1 ? 65_536 : encodedPageSize;
            var pageCount = BinaryPrimitives.ReadUInt32BigEndian(value.Slice(28, 4));
            if (!IsPowerOfTwo((uint)pageSize) || pageSize is < 512 or > 65_536 || pageCount == 0)
                continue;
            long size;
            try { size = checked((long)pageSize * pageCount); }
            catch (OverflowException) { continue; }
            AddCandidate(
                files, absoluteOffset + index, size,
                ".sqlite", "其他", "SQLite 结构", sourceLength);
        }
    }

    private static void FindElf(
        ReadOnlySpan<byte> data, long absoluteOffset, long sourceLength,
        ICollection<RecoveredFile> files)
    {
        ReadOnlySpan<byte> signature = [0x7F, (byte)'E', (byte)'L', (byte)'F'];
        foreach (var index in FindMagicOffsets(data, signature))
        {
            var value = data[index..];
            if (!TryGetElfSize(value, out var size, out var extension)) continue;
            AddCandidate(
                files, absoluteOffset + index, size,
                extension, "程序", "ELF 结构", sourceLength);
        }
    }

    private static bool TryGetElfSize(
        ReadOnlySpan<byte> data,
        out long size,
        out string extension)
    {
        size = 0;
        extension = ".elf";
        if (data.Length < 52 || data[4] is not (1 or 2) || data[5] is not (1 or 2) || data[6] != 1)
            return false;

        var is64 = data[4] == 2;
        var littleEndian = data[5] == 1;
        var headerSize = is64 ? 64 : 52;
        if (data.Length < headerSize) return false;
        var type = ReadUInt16(data.Slice(16, 2), littleEndian);
        var machine = ReadUInt16(data.Slice(18, 2), littleEndian);
        if (type is not (2 or 3) || machine == 0) return false;
        extension = type == 3 ? ".so" : ".elf";

        var programOffset = is64
            ? ReadUInt64(data.Slice(32, 8), littleEndian)
            : ReadUInt32(data.Slice(28, 4), littleEndian);
        var sectionOffset = is64
            ? ReadUInt64(data.Slice(40, 8), littleEndian)
            : ReadUInt32(data.Slice(32, 4), littleEndian);
        var programEntrySize = ReadUInt16(data.Slice(is64 ? 54 : 42, 2), littleEndian);
        var programCount = ReadUInt16(data.Slice(is64 ? 56 : 44, 2), littleEndian);
        var sectionEntrySize = ReadUInt16(data.Slice(is64 ? 58 : 46, 2), littleEndian);
        var sectionCount = ReadUInt16(data.Slice(is64 ? 60 : 48, 2), littleEndian);
        if (programCount is 0 or > 4096 ||
            programEntrySize < (is64 ? 56 : 32) || programEntrySize > 4096)
            return false;

        ulong programTableEnd;
        try { programTableEnd = checked(programOffset + (ulong)programEntrySize * programCount); }
        catch (OverflowException) { return false; }
        if (programOffset < (ulong)headerSize || programTableEnd > (ulong)data.Length)
            return false;

        ulong maximumEnd = Math.Max((ulong)headerSize, programTableEnd);
        for (var index = 0; index < programCount; index++)
        {
            var entryOffset = checked((int)programOffset + index * programEntrySize);
            var entry = data.Slice(entryOffset, programEntrySize);
            var fileOffset = is64
                ? ReadUInt64(entry.Slice(8, 8), littleEndian)
                : ReadUInt32(entry.Slice(4, 4), littleEndian);
            var fileSize = is64
                ? ReadUInt64(entry.Slice(32, 8), littleEndian)
                : ReadUInt32(entry.Slice(16, 4), littleEndian);
            ulong end;
            try { end = checked(fileOffset + fileSize); }
            catch (OverflowException) { return false; }
            maximumEnd = Math.Max(maximumEnd, end);
        }

        if (sectionCount > 0)
        {
            if (sectionCount > 8192 || sectionEntrySize < (is64 ? 64 : 40) || sectionEntrySize > 4096)
                return false;
            ulong sectionTableEnd;
            try { sectionTableEnd = checked(sectionOffset + (ulong)sectionEntrySize * sectionCount); }
            catch (OverflowException) { return false; }
            maximumEnd = Math.Max(maximumEnd, sectionTableEnd);
        }

        if (maximumEnd is 0 or > (ulong)MaximumCandidateSize)
            return false;
        size = (long)maximumEnd;
        return true;
    }

    private static IReadOnlyList<int> FindMagicOffsets(
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> magic)
    {
        var matches = new List<int>();
        var searchOffset = 0;
        while (searchOffset <= data.Length - magic.Length)
        {
            var relative = data[searchOffset..].IndexOf(magic);
            if (relative < 0) break;
            var index = searchOffset + relative;
            matches.Add(index);
            searchOffset = index + Math.Max(1, magic.Length);
        }
        return matches;
    }

    private static void AddCandidate(
        ICollection<RecoveredFile> files,
        long sourceOffset,
        long size,
        string extension,
        string category,
        string signature,
        long sourceLength)
    {
        if (sourceOffset < 0 || size <= 0 || size > MaximumCandidateSize ||
            (sourceLength > 0 &&
             (sourceOffset > sourceLength || size > sourceLength - sourceOffset)))
            return;
        files.Add(new RecoveredFile(
            $"恢复候选_{sourceOffset:X}{extension}",
            $"格式化或丢失文件/{category}",
            size,
            category,
            RecoveryState.Good,
            sourceOffset,
            signature)
        {
            RecoveryExtents = [new RecoveryExtent(sourceOffset, size)]
        });
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> value, bool littleEndian) =>
        littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(value)
            : BinaryPrimitives.ReadUInt16BigEndian(value);

    private static uint ReadUInt32(ReadOnlySpan<byte> value, bool littleEndian) =>
        littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(value)
            : BinaryPrimitives.ReadUInt32BigEndian(value);

    private static ulong ReadUInt64(ReadOnlySpan<byte> value, bool littleEndian) =>
        littleEndian
            ? BinaryPrimitives.ReadUInt64LittleEndian(value)
            : BinaryPrimitives.ReadUInt64BigEndian(value);

    private static bool IsPowerOfTwo(uint value) => value != 0 && (value & (value - 1)) == 0;
}
