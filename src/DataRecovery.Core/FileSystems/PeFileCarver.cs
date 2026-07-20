using System.Buffers.Binary;
using DataRecovery.Core.Models;

namespace DataRecovery.Core.FileSystems;

/// <summary>
/// 从原始数据块中识别结构完整的 Windows PE 文件头，并根据节表计算可恢复长度。
/// 该扫描不依赖文件系统元数据，适用于快速格式化后仍连续保留的数据。
/// </summary>
public static class PeFileCarver
{
    private const ushort ExecutableImageFlag = 0x0002;
    private const ushort DllFlag = 0x2000;
    private const int MinimumDosHeaderSize = 64;
    private const int CoffHeaderSize = 20;
    private const int SectionHeaderSize = 40;
    private const int MaximumSectionCount = 96;
    private const int MaximumPeHeaderOffset = 1024 * 1024;
    private const long MaximumCandidateSize = 32L * 1024 * 1024 * 1024;

    public static IReadOnlyList<RecoveredFile> FindCandidates(
        ReadOnlySpan<byte> data,
        long absoluteOffset,
        long sourceLength = 0)
    {
        var files = new List<RecoveredFile>();
        var searchOffset = 0;
        while (searchOffset <= data.Length - 2)
        {
            var relative = data[searchOffset..].IndexOf("MZ"u8);
            if (relative < 0) break;
            var candidateOffset = searchOffset + relative;
            if (TryParseCandidate(data[candidateOffset..], out var size, out var isDll))
            {
                var sourceOffset = absoluteOffset + candidateOffset;
                if (sourceOffset >= 0 &&
                    (sourceLength <= 0 ||
                     sourceOffset <= sourceLength && size <= sourceLength - sourceOffset))
                {
                    var extension = isDll ? ".dll" : ".exe";
                    files.Add(new RecoveredFile(
                        $"恢复候选_{sourceOffset:X}{extension}",
                        "格式化或丢失文件/程序",
                        size,
                        "程序",
                        RecoveryState.Good,
                        sourceOffset,
                        "PE/EXE 结构")
                    {
                        RecoveryExtents = [new RecoveryExtent(sourceOffset, size)]
                    });
                }
            }
            searchOffset = candidateOffset + 2;
        }
        return files;
    }

    private static bool TryParseCandidate(
        ReadOnlySpan<byte> data,
        out long candidateSize,
        out bool isDll)
    {
        candidateSize = 0;
        isDll = false;
        if (data.Length < MinimumDosHeaderSize || !data[..2].SequenceEqual("MZ"u8))
            return false;

        var peOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0x3C, 4));
        if (peOffset < MinimumDosHeaderSize || peOffset > MaximumPeHeaderOffset ||
            peOffset > (uint)(data.Length - 4 - CoffHeaderSize))
            return false;

        var pe = (int)peOffset;
        if (!data.Slice(pe, 4).SequenceEqual("PE\0\0"u8))
            return false;

        var coff = pe + 4;
        var machine = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(coff, 2));
        var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(coff + 2, 2));
        var optionalHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(coff + 16, 2));
        var characteristics = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(coff + 18, 2));
        if (!IsKnownMachine(machine) || sectionCount is 0 or > MaximumSectionCount ||
            optionalHeaderSize < 96 || optionalHeaderSize > 4096 ||
            (characteristics & ExecutableImageFlag) == 0)
            return false;

        var optional = coff + CoffHeaderSize;
        if (optional > data.Length - optionalHeaderSize)
            return false;
        var magic = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(optional, 2));
        var dataDirectoryOffset = magic switch
        {
            0x10B => 96,
            0x20B => 112,
            _ => -1
        };
        if (dataDirectoryOffset < 0 || optionalHeaderSize < dataDirectoryOffset)
            return false;

        var sectionAlignment = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(optional + 32, 4));
        var fileAlignment = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(optional + 36, 4));
        var sizeOfImage = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(optional + 56, 4));
        var sizeOfHeaders = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(optional + 60, 4));
        if (!IsPowerOfTwo(fileAlignment) || fileAlignment is < 512 or > 65_536 ||
            sectionAlignment < fileAlignment || sizeOfImage == 0 ||
            sizeOfHeaders < MinimumDosHeaderSize)
            return false;

        var sectionTable = optional + optionalHeaderSize;
        var sectionTableLength = checked((int)sectionCount * SectionHeaderSize);
        if (sectionTable > data.Length - sectionTableLength)
            return false;

        ulong maximumEnd = sizeOfHeaders;
        var hasRawSection = false;
        for (var index = 0; index < sectionCount; index++)
        {
            var section = data.Slice(sectionTable + index * SectionHeaderSize, SectionHeaderSize);
            var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(section.Slice(16, 4));
            var rawOffset = BinaryPrimitives.ReadUInt32LittleEndian(section.Slice(20, 4));
            if (rawSize == 0) continue;
            if (rawOffset < sizeOfHeaders)
                return false;
            var end = (ulong)rawOffset + rawSize;
            if (end > (ulong)MaximumCandidateSize)
                return false;
            maximumEnd = Math.Max(maximumEnd, end);
            hasRawSection = true;
        }
        if (!hasRawSection)
            return false;

        // 安全目录使用文件偏移而不是 RVA，应计入 Authenticode 证书尾部。
        var numberOfDirectoriesOffset = magic == 0x10B ? 92 : 108;
        if (optionalHeaderSize >= numberOfDirectoriesOffset + 4)
        {
            var directoryCount = BinaryPrimitives.ReadUInt32LittleEndian(
                data.Slice(optional + numberOfDirectoriesOffset, 4));
            const int securityDirectoryIndex = 4;
            var securityEntry = dataDirectoryOffset + securityDirectoryIndex * 8;
            if (directoryCount > securityDirectoryIndex &&
                optionalHeaderSize >= securityEntry + 8)
            {
                var certificateOffset = BinaryPrimitives.ReadUInt32LittleEndian(
                    data.Slice(optional + securityEntry, 4));
                var certificateSize = BinaryPrimitives.ReadUInt32LittleEndian(
                    data.Slice(optional + securityEntry + 4, 4));
                if (certificateSize > 0)
                {
                    var certificateEnd = (ulong)certificateOffset + certificateSize;
                    if (certificateOffset < sizeOfHeaders ||
                        certificateEnd > (ulong)MaximumCandidateSize)
                        return false;
                    maximumEnd = Math.Max(maximumEnd, certificateEnd);
                }
            }
        }

        candidateSize = (long)maximumEnd;
        isDll = (characteristics & DllFlag) != 0;
        return candidateSize > 0;
    }

    private static bool IsKnownMachine(ushort machine) => machine is
        0x014C or // x86
        0x01C0 or 0x01C2 or 0x01C4 or // ARM/Thumb
        0x8664 or // x64
        0xAA64;   // ARM64

    private static bool IsPowerOfTwo(uint value) => value != 0 && (value & (value - 1)) == 0;
}
