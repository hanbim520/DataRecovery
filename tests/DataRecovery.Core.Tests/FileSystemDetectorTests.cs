using System.Buffers.Binary;
using System.Text;
using DataRecovery.Core.FileSystems;
using DataRecovery.Core.Models;

namespace DataRecovery.Core.Tests;

public sealed class FileSystemDetectorTests
{
    private readonly FileSystemDetector _detector = new();

    [Fact]
    public void DetectsNtfsBootSector()
    {
        var sector = new byte[4096];
        Encoding.ASCII.GetBytes("NTFS    ").CopyTo(sector, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(11, 2), 512);
        BinaryPrimitives.WriteInt64LittleEndian(sector.AsSpan(40, 8), 2_000_000);

        var result = _detector.Detect(sector);

        Assert.Equal(FileSystemKind.Ntfs, result.Kind);
        Assert.Equal(1_024_000_000, result.TotalBytes);
    }

    [Fact]
    public void DetectsExFatBootSector()
    {
        var sector = new byte[4096];
        Encoding.ASCII.GetBytes("EXFAT   ").CopyTo(sector, 3);
        BinaryPrimitives.WriteUInt64LittleEndian(sector.AsSpan(72, 8), 122_879_744);
        sector[108] = 9;

        var result = _detector.Detect(sector);

        Assert.Equal(FileSystemKind.ExFat, result.Kind);
        Assert.Equal(62_914_428_928, result.TotalBytes);
    }

    [Theory]
    [InlineData(2_880, FileSystemKind.Fat12)]
    [InlineData(64_000, FileSystemKind.Fat16)]
    [InlineData(1_000_000, FileSystemKind.Fat32)]
    public void DetectsFatVariantFromClusterCount(uint totalSectors, FileSystemKind expected)
    {
        var sector = CreateFatBootSector(totalSectors, expected == FileSystemKind.Fat32);

        var result = _detector.Detect(sector);

        Assert.Equal(expected, result.Kind);
    }

    [Theory]
    [InlineData(false, FileSystemKind.Ext2)]
    [InlineData(true, FileSystemKind.Ext3)]
    public void DetectsExtSuperblockAndJournal(bool journal, FileSystemKind expected)
    {
        var image = new byte[4096];
        var sb = image.AsSpan(1024);
        BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(4, 4), 10_000);
        BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(24, 4), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(56, 2), 0xEF53);
        BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(92, 4), journal ? 0x4u : 0u);

        var result = _detector.Detect(image);

        Assert.Equal(expected, result.Kind);
        Assert.Equal(40_960_000, result.TotalBytes);
    }

    [Fact]
    public void UnknownDataIsNotMisidentified()
    {
        Assert.Equal(FileSystemKind.Unknown, _detector.Detect(new byte[4096]).Kind);
    }

    private static byte[] CreateFatBootSector(uint totalSectors, bool fat32)
    {
        var sector = new byte[4096];
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(11, 2), 512);
        sector[13] = fat32 ? (byte)1 : (byte)1;
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(14, 2), fat32 ? (ushort)32 : (ushort)1);
        sector[16] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(17, 2), fat32 ? (ushort)0 : (ushort)224);
        if (totalSectors <= ushort.MaxValue)
            BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(19, 2), (ushort)totalSectors);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(32, 4), totalSectors);
        if (fat32)
            BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(36, 4), 1_000);
        else
            BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(22, 2), totalSectors < 5_000 ? (ushort)9 : (ushort)250);
        sector[510] = 0x55;
        sector[511] = 0xAA;
        return sector;
    }
}
