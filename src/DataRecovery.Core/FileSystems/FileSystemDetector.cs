using System.Buffers.Binary;
using System.Text;
using DataRecovery.Core.Models;

namespace DataRecovery.Core.FileSystems;

public interface IFileSystemDetector
{
    Task<DetectedFileSystem> DetectAsync(Stream stream, CancellationToken cancellationToken = default);
    DetectedFileSystem Detect(ReadOnlySpan<byte> header);
}

public sealed class FileSystemDetector : IFileSystemDetector
{
    public async Task<DetectedFileSystem> DetectAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var original = stream.CanSeek ? stream.Position : 0;
        var header = new byte[4096];
        var read = 0;
        while (read < header.Length)
        {
            var count = await stream.ReadAsync(header.AsMemory(read), cancellationToken);
            if (count == 0) break;
            read += count;
        }
        if (stream.CanSeek) stream.Position = original;
        return Detect(header.AsSpan(0, read));
    }

    public DetectedFileSystem Detect(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 512 && Encoding.ASCII.GetString(data.Slice(3, 8)) == "EXFAT   ")
        {
            var bytesPerSector = 1 << data[108];
            var sectors = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(72, 8));
            var totalBytes = sectors <= long.MaxValue
                ? SafeMultiply((long)sectors, bytesPerSector)
                : 0;
            return new(FileSystemKind.ExFat, bytesPerSector, totalBytes, "exFAT",
                "Microsoft exFAT 文件系统（支持删除目录项恢复）");
        }

        if (data.Length >= 512 && Encoding.ASCII.GetString(data.Slice(3, 8)) == "NTFS    ")
        {
            var bps = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(11, 2));
            var sectors = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(40, 8));
            return new(FileSystemKind.Ntfs, bps, SafeMultiply(sectors, bps), "NTFS", "Windows NT/2000/XP 及后续 NTFS");
        }

        if (data.Length >= 512 && data[510] == 0x55 && data[511] == 0xAA)
        {
            var bps = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(11, 2));
            var spc = data[13];
            var reserved = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(14, 2));
            var fats = data[16];
            var rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(17, 2));
            var total16 = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(19, 2));
            var total32 = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(32, 4));
            var fat16 = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(22, 2));
            var fat32 = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(36, 4));
            if (bps is >= 128 and <= 4096 && spc > 0 && (spc & (spc - 1)) == 0)
            {
                var total = total16 != 0 ? total16 : total32;
                var fatSize = fat16 != 0 ? fat16 : fat32;
                var rootSectors = ((uint)rootEntries * 32 + (uint)bps - 1) / (uint)bps;
                var dataSectors = total - (uint)reserved - (uint)fats * fatSize - rootSectors;
                var clusters = dataSectors / spc;
                var kind = clusters < 4085 ? FileSystemKind.Fat12 : clusters < 65525 ? FileSystemKind.Fat16 : FileSystemKind.Fat32;
                return new(kind, bps, SafeMultiply(total, bps), kind.ToString().ToUpperInvariant(), "DOS/Windows FAT 文件系统");
            }
        }

        if (data.Length >= 2048)
        {
            var sb = data.Slice(1024);
            if (BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(56, 2)) == 0xEF53)
            {
                var blocks = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(4, 4));
                var blockSize = 1024L << (int)BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(24, 4));
                var compat = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(92, 4));
                var hasJournal = (compat & 0x4) != 0;
                var kind = hasJournal ? FileSystemKind.Ext3 : FileSystemKind.Ext2;
                return new(kind, 512, SafeMultiply(blocks, blockSize), kind.ToString(),
                    hasJournal ? "Linux Ext3（含日志）" : "Linux Ext2");
            }
        }
        return DetectedFileSystem.Unknown;
    }

    private static long SafeMultiply(long a, long b)
    {
        try { return checked(a * b); }
        catch (OverflowException) { return 0; }
    }
}
