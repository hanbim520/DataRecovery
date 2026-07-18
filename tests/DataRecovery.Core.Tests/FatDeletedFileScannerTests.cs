using System.Buffers.Binary;
using System.Text;
using DataRecovery.Core.FileSystems;
using DataRecovery.Core.Models;
using DataRecovery.Core.Services;

namespace DataRecovery.Core.Tests;

public sealed class FatDeletedFileScannerTests
{
    [Theory]
    [InlineData(FileSystemKind.Fat12)]
    [InlineData(FileSystemKind.Fat16)]
    [InlineData(FileSystemKind.Fat32)]
    public async Task RecoversDeletedLongNameAcrossFragmentedFatChain(FileSystemKind kind)
    {
        var layout = TestFatLayout.Create(kind);
        var image = CreateImage(layout);
        const string longName = "deleted photo.jpg";
        var shortName = Encoding.ASCII.GetBytes("DELETE~1JPG");

        var directoryOffset = layout.GetRootDirectoryOffset();
        var nextEntryOffset = WriteDeletedLongNameEntries(
            image, directoryOffset, longName, shortName);
        WriteDirectoryEntry(
            image, nextEntryOffset, shortName, deleted: true,
            attributes: 0x20, firstCluster: 3, size: 700);

        SetFatEntry(image, layout, 3, 5);
        SetFatEntry(image, layout, 5, EndOfChain(kind));
        image.AsSpan(layout.GetClusterOffset(3), layout.ClusterSize).Fill((byte)'A');
        image.AsSpan(layout.GetClusterOffset(5), layout.ClusterSize).Fill((byte)'B');

        await using var stream = new MemoryStream(image, writable: false);
        var results = await new FatDeletedFileScanner().ScanAsync(stream);

        var file = Assert.Single(results);
        Assert.Equal(longName, file.Name);
        Assert.Equal("照片", file.Category);
        Assert.Equal(700, file.Size);
        Assert.Equal(RecoveryState.Excellent, file.State);
        Assert.Equal($"{kind.ToString().ToUpperInvariant()} 删除目录项", file.Signature);
        Assert.Equal(layout.GetClusterOffset(3), file.Offset);
        Assert.Collection(
            file.RecoveryExtents,
            first =>
            {
                Assert.Equal(layout.GetClusterOffset(3), first.SourceOffset);
                Assert.Equal(layout.ClusterSize, first.Length);
                Assert.False(first.IsSparse);
            },
            second =>
            {
                Assert.Equal(layout.GetClusterOffset(5), second.SourceOffset);
                Assert.Equal(700 - layout.ClusterSize, second.Length);
                Assert.False(second.IsSparse);
            });

        var recovered = RecoverFromExtents(image, file);
        Assert.All(recovered[..layout.ClusterSize], value => Assert.Equal((byte)'A', value));
        Assert.All(recovered[layout.ClusterSize..], value => Assert.Equal((byte)'B', value));
    }

    [Fact]
    public async Task UsesMergedContiguousExtentsWhenDeletedFatChainWasCleared()
    {
        var layout = TestFatLayout.Create(FileSystemKind.Fat12);
        var image = CreateImage(layout);
        var shortName = Encoding.ASCII.GetBytes("FILE    TXT");
        WriteDirectoryEntry(
            image, layout.GetRootDirectoryOffset(), shortName, deleted: true,
            attributes: 0x20, firstCluster: 3, size: 700);

        // 删除后的簇 3、4 均为空闲，允许采用连续簇推断，但不能标记为“极佳”。
        image.AsSpan(layout.GetClusterOffset(3), layout.ClusterSize).Fill(0x31);
        image.AsSpan(layout.GetClusterOffset(4), layout.ClusterSize).Fill(0x32);

        await using var stream = new MemoryStream(image, writable: false);
        var file = Assert.Single(await new FatDeletedFileScanner().ScanAsync(stream));

        Assert.Equal("_ILE.TXT", file.Name);
        Assert.Equal(RecoveryState.Good, file.State);
        var extent = Assert.Single(file.RecoveryExtents);
        Assert.Equal(layout.GetClusterOffset(3), extent.SourceOffset);
        Assert.Equal(700, extent.Length);
    }

    [Fact]
    public async Task FindsDeletedEntryInsideActiveSubdirectory()
    {
        var layout = TestFatLayout.Create(FileSystemKind.Fat16);
        var image = CreateImage(layout);

        WriteDirectoryEntry(
            image, layout.GetRootDirectoryOffset(), Encoding.ASCII.GetBytes("SUBDIR     "),
            deleted: false, attributes: 0x10, firstCluster: 4, size: 0);
        SetFatEntry(image, layout, 4, EndOfChain(layout.Kind));

        WriteDirectoryEntry(
            image, layout.GetClusterOffset(4), Encoding.ASCII.GetBytes("FILE    EXE"),
            deleted: true, attributes: 0x20, firstCluster: 6, size: 96);
        SetFatEntry(image, layout, 6, EndOfChain(layout.Kind));

        await using var stream = new MemoryStream(image, writable: false);
        var file = Assert.Single(await new FatDeletedFileScanner().ScanAsync(stream));

        Assert.Equal("_ILE.EXE", file.Name);
        Assert.Equal("程序", file.Category);
        Assert.Equal("已删除文件/FAT16 根目录/SUBDIR", file.OriginalPath);
        Assert.Equal(RecoveryState.Excellent, file.State);
    }

    [Fact]
    public async Task RejectsTruncatedOrOutOfBoundsVolumeWithoutReadingPastSource()
    {
        var layout = TestFatLayout.Create(FileSystemKind.Fat12);
        var fullImage = CreateImage(layout);
        var truncated = fullImage[..512];

        await using var stream = new MemoryStream(truncated, writable: false);
        var results = await new FatDeletedFileScanner().ScanAsync(stream);

        Assert.Empty(results);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task RecoveryScannerRoutesFatDeletedMetadataScan()
    {
        var layout = TestFatLayout.Create(FileSystemKind.Fat16);
        var image = CreateImage(layout);
        WriteDirectoryEntry(
            image, layout.GetRootDirectoryOffset(), Encoding.ASCII.GetBytes("FILE    TXT"),
            deleted: true, attributes: 0x20, firstCluster: 3, size: 64);
        SetFatEntry(image, layout, 3, EndOfChain(layout.Kind));

        var path = Path.Combine(Path.GetTempPath(), $"fat-route-{Guid.NewGuid():N}.img");
        try
        {
            await File.WriteAllBytesAsync(path, image);
            var result = await new RecoveryScanner(new FileSystemDetector())
                .ScanAsync(path, ScanMode.DeletedFiles);

            Assert.Equal(FileSystemKind.Fat16, result.FileSystem.Kind);
            Assert.Equal("_ILE.TXT", Assert.Single(result.Files).Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] CreateImage(TestFatLayout layout)
    {
        var image = new byte[checked(layout.TotalSectors * layout.BytesPerSector)];
        image[0] = 0xEB;
        image[1] = 0x3C;
        image[2] = 0x90;
        Encoding.ASCII.GetBytes("MSDOS5.0").CopyTo(image, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(11, 2), (ushort)layout.BytesPerSector);
        image[13] = (byte)layout.SectorsPerCluster;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(14, 2), (ushort)layout.ReservedSectors);
        image[16] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(17, 2), (ushort)layout.RootEntryCount);
        if (layout.TotalSectors <= ushort.MaxValue)
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(19, 2), (ushort)layout.TotalSectors);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(32, 4), (uint)layout.TotalSectors);
        image[21] = 0xF8;

        if (layout.Kind == FileSystemKind.Fat32)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(36, 4), (uint)layout.FatSectors);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(44, 4), 2);
            Encoding.ASCII.GetBytes("FAT32   ").CopyTo(image, 82);
            SetFatEntry(image, layout, 2, EndOfChain(layout.Kind));
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(22, 2), (ushort)layout.FatSectors);
            Encoding.ASCII.GetBytes(layout.Kind == FileSystemKind.Fat12 ? "FAT12   " : "FAT16   ")
                .CopyTo(image, 54);
        }

        image[510] = 0x55;
        image[511] = 0xAA;
        return image;
    }

    private static int WriteDeletedLongNameEntries(
        byte[] image,
        int offset,
        string longName,
        byte[] originalShortName)
    {
        var checksum = ComputeShortNameChecksum(originalShortName);
        var entryCount = (longName.Length + 12) / 13;
        for (var ordinal = entryCount; ordinal >= 1; ordinal--)
        {
            var entry = image.AsSpan(offset, 32);
            entry.Fill(0xFF);
            entry[0] = (byte)(ordinal | (ordinal == entryCount ? 0x40 : 0));
            entry[11] = 0x0F;
            entry[12] = 0;
            entry[13] = checksum;
            BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(26, 2), 0);

            var start = (ordinal - 1) * 13;
            for (var characterIndex = 0; characterIndex < 13; characterIndex++)
            {
                var nameIndex = start + characterIndex;
                var value = nameIndex < longName.Length
                    ? longName[nameIndex]
                    : nameIndex == longName.Length
                        ? '\0'
                        : '\uFFFF';
                BinaryPrimitives.WriteUInt16LittleEndian(
                    entry.Slice(LongNameCharacterOffsets[characterIndex], 2), value);
            }

            entry[0] = 0xE5;
            offset += 32;
        }
        return offset;
    }

    private static void WriteDirectoryEntry(
        byte[] image,
        int offset,
        byte[] shortName,
        bool deleted,
        byte attributes,
        uint firstCluster,
        uint size)
    {
        Assert.Equal(11, shortName.Length);
        shortName.CopyTo(image, offset);
        if (deleted)
            image[offset] = 0xE5;
        image[offset + 11] = attributes;
        BinaryPrimitives.WriteUInt16LittleEndian(
            image.AsSpan(offset + 20, 2), (ushort)(firstCluster >> 16));
        BinaryPrimitives.WriteUInt16LittleEndian(
            image.AsSpan(offset + 26, 2), (ushort)firstCluster);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 28, 4), size);
    }

    private static void SetFatEntry(
        byte[] image,
        TestFatLayout layout,
        uint cluster,
        uint value)
    {
        var fatOffset = layout.ReservedSectors * layout.BytesPerSector;
        switch (layout.Kind)
        {
            case FileSystemKind.Fat12:
            {
                var offset = fatOffset + (int)(cluster + cluster / 2);
                var pair = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(offset, 2));
                pair = (cluster & 1) == 0
                    ? (ushort)(((uint)pair & 0xF000u) | (value & 0x0FFFu))
                    : (ushort)(((uint)pair & 0x000Fu) | ((value & 0x0FFFu) << 4));
                BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset, 2), pair);
                break;
            }
            case FileSystemKind.Fat16:
                BinaryPrimitives.WriteUInt16LittleEndian(
                    image.AsSpan(fatOffset + (int)cluster * 2, 2), (ushort)value);
                break;
            case FileSystemKind.Fat32:
                BinaryPrimitives.WriteUInt32LittleEndian(
                    image.AsSpan(fatOffset + (int)cluster * 4, 4), value & 0x0FFFFFFF);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(layout));
        }
    }

    private static uint EndOfChain(FileSystemKind kind) => kind switch
    {
        FileSystemKind.Fat12 => 0x0FFF,
        FileSystemKind.Fat16 => 0xFFFF,
        FileSystemKind.Fat32 => 0x0FFFFFFF,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static byte ComputeShortNameChecksum(ReadOnlySpan<byte> shortName)
    {
        byte checksum = 0;
        foreach (var value in shortName)
            checksum = (byte)(((checksum & 1) << 7) + (checksum >> 1) + value);
        return checksum;
    }

    private static byte[] RecoverFromExtents(byte[] image, RecoveredFile file)
    {
        var output = new byte[file.Size];
        var destinationOffset = 0;
        foreach (var extent in file.RecoveryExtents)
        {
            var length = (int)Math.Min(extent.Length, output.Length - destinationOffset);
            image.AsSpan((int)extent.SourceOffset, length)
                .CopyTo(output.AsSpan(destinationOffset, length));
            destinationOffset += length;
        }
        return output;
    }

    private static readonly int[] LongNameCharacterOffsets =
        [1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30];

    private sealed record TestFatLayout(
        FileSystemKind Kind,
        int TotalSectors,
        int ReservedSectors,
        int FatSectors,
        int RootEntryCount)
    {
        public int BytesPerSector => 512;
        public int SectorsPerCluster => 1;
        public int ClusterSize => BytesPerSector * SectorsPerCluster;
        public int RootDirectorySectors =>
            (RootEntryCount * 32 + BytesPerSector - 1) / BytesPerSector;
        public int FirstDataSector =>
            ReservedSectors + FatSectors + RootDirectorySectors;

        public int GetRootDirectoryOffset() => Kind == FileSystemKind.Fat32
            ? GetClusterOffset(2)
            : (ReservedSectors + FatSectors) * BytesPerSector;

        public int GetClusterOffset(uint cluster) => checked(
            (FirstDataSector + ((int)cluster - 2) * SectorsPerCluster) * BytesPerSector);

        public static TestFatLayout Create(FileSystemKind kind) => kind switch
        {
            FileSystemKind.Fat12 => new(kind, 100, 1, 1, 16),
            FileSystemKind.Fat16 => new(kind, 5_000, 1, 20, 16),
            FileSystemKind.Fat32 => new(kind, 66_050, 1, 520, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }
}
