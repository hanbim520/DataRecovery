using System.Buffers.Binary;
using System.Text;
using DataRecovery.Core.FileSystems;
using DataRecovery.Core.Models;
using DataRecovery.Core.Services;

namespace DataRecovery.Core.Tests;

public sealed class NtfsDeletedFileScannerTests
{
    private const int BytesPerSector = 512;
    private const int ClusterSize = 512;
    private const int RecordSize = 1024;
    private const int MftCluster = 4;

    [Fact]
    public async Task RecoversResidentDataIncludingBytesProtectedByUsaFixup()
    {
        var residentContent = Encoding.ASCII.GetBytes("resident-data-123456");
        var image = CreateImage(residentContent, CreateNonResidentContent(), secondRunCluster: 40);
        await using var stream = new MemoryStream(image, writable: false);

        var files = await new NtfsDeletedFileScanner().ScanAsync(stream);

        var file = Assert.Single(files, item => item.Name == "resident.txt");
        Assert.Equal("文档", file.Category);
        Assert.Equal(RecoveryState.Excellent, file.State);
        Assert.Equal(residentContent.Length, file.Size);
        Assert.Equal("NTFS 删除 MFT 记录", file.Signature);
        // DATA 值跨过第一个扇区末尾；中间区段应从 USA 替换数组恢复原始两个字节。
        Assert.Equal(3, file.RecoveryExtents.Count);
        Assert.Equal(residentContent, Recover(image, file));
    }

    [Fact]
    public async Task RecoversFragmentedNonResidentRunListInLogicalOrder()
    {
        var content = CreateNonResidentContent();
        var image = CreateImage(
            Encoding.ASCII.GetBytes("resident-data-123456"),
            content,
            secondRunCluster: 40);
        await using var stream = new MemoryStream(image, writable: false);

        var files = await new NtfsDeletedFileScanner().ScanAsync(stream);

        var file = Assert.Single(files, item => item.Name == "fragmented.bin");
        Assert.Equal(RecoveryState.Good, file.State);
        Assert.Equal(content.Length, file.Size);
        Assert.Collection(
            file.RecoveryExtents,
            first =>
            {
                Assert.Equal(30L * ClusterSize, first.SourceOffset);
                Assert.Equal(ClusterSize, first.Length);
                Assert.False(first.IsSparse);
            },
            second =>
            {
                Assert.Equal(40L * ClusterSize, second.SourceOffset);
                Assert.Equal(content.Length - ClusterSize, second.Length);
                Assert.False(second.IsSparse);
            });
        Assert.Equal(content, Recover(image, file));
    }

    [Fact]
    public async Task MergesPhysicallyAdjacentRunListSegments()
    {
        var content = CreateNonResidentContent();
        var image = CreateImage(
            Encoding.ASCII.GetBytes("resident-data-123456"),
            content,
            secondRunCluster: 31);
        await using var stream = new MemoryStream(image, writable: false);

        var files = await new NtfsDeletedFileScanner().ScanAsync(stream);

        var file = Assert.Single(files, item => item.Name == "fragmented.bin");
        var extent = Assert.Single(file.RecoveryExtents);
        Assert.Equal(30L * ClusterSize, extent.SourceOffset);
        Assert.Equal(content.Length, extent.Length);
        Assert.Equal(content, Recover(image, file));
    }

    [Fact]
    public async Task SkipsRecordWhoseUsaSequenceDoesNotMatchSectorTrailer()
    {
        var image = CreateImage(
            Encoding.ASCII.GetBytes("resident-data-123456"),
            CreateNonResidentContent(),
            secondRunCluster: 40);
        var residentRecordOffset = (MftCluster * ClusterSize) + RecordSize;
        image[residentRecordOffset + BytesPerSector - 2] ^= 0x5A;
        await using var stream = new MemoryStream(image, writable: false);

        var files = await new NtfsDeletedFileScanner().ScanAsync(stream);

        Assert.DoesNotContain(files, item => item.Name == "resident.txt");
        Assert.Contains(files, item => item.Name == "fragmented.bin");
    }

    [Fact]
    public async Task RecoveryScannerRoutesNtfsDeletedMetadataScan()
    {
        var image = CreateImage(
            Encoding.ASCII.GetBytes("resident-data-123456"),
            CreateNonResidentContent(),
            secondRunCluster: 40);
        var path = Path.Combine(Path.GetTempPath(), $"ntfs-route-{Guid.NewGuid():N}.img");
        try
        {
            await File.WriteAllBytesAsync(path, image);
            var result = await new RecoveryScanner(new FileSystemDetector())
                .ScanAsync(path, ScanMode.DeletedFiles);

            Assert.Equal(FileSystemKind.Ntfs, result.FileSystem.Kind);
            Assert.Contains(result.Files, file => file.Name == "resident.txt");
            Assert.Contains(result.Files, file => file.Name == "fragmented.bin");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] CreateImage(
        byte[] residentContent,
        byte[] nonResidentContent,
        int secondRunCluster)
    {
        var image = new byte[128 * BytesPerSector];
        WriteBootSector(image);

        var mftOffset = MftCluster * ClusterSize;
        WriteMftRecord(image.AsSpan(mftOffset, RecordSize));
        WriteDeletedResidentRecord(
            image.AsSpan(mftOffset + RecordSize, RecordSize),
            "resident.txt",
            residentContent);
        WriteDeletedNonResidentRecord(
            image.AsSpan(mftOffset + RecordSize * 2, RecordSize),
            "fragmented.bin",
            nonResidentContent.Length,
            secondRunCluster);

        nonResidentContent.AsSpan(0, ClusterSize).CopyTo(
            image.AsSpan(30 * ClusterSize, ClusterSize));
        nonResidentContent.AsSpan(ClusterSize).CopyTo(
            image.AsSpan(secondRunCluster * ClusterSize));
        return image;
    }

    private static void WriteBootSector(Span<byte> image)
    {
        Encoding.ASCII.GetBytes("NTFS    ").CopyTo(image.Slice(3, 8));
        BinaryPrimitives.WriteUInt16LittleEndian(image.Slice(11, 2), BytesPerSector);
        image[13] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(image.Slice(40, 8), 128);
        BinaryPrimitives.WriteUInt64LittleEndian(image.Slice(48, 8), MftCluster);
        image[64] = unchecked((byte)-10); // 2^10 = 1024-byte FILE record
        image[510] = 0x55;
        image[511] = 0xAA;
    }

    private static void WriteMftRecord(Span<byte> record)
    {
        InitializeRecord(record, inUse: true);
        const int dataAttributeOffset = 56;
        byte[] runList = [0x11, 0x06, MftCluster, 0x00];
        var end = WriteNonResidentDataAttribute(
            record,
            dataAttributeOffset,
            runList,
            clusterCount: 6,
            realSize: 3 * RecordSize);
        FinishRecord(record, end);
    }

    private static void WriteDeletedResidentRecord(
        Span<byte> record,
        string name,
        byte[] content)
    {
        InitializeRecord(record, inUse: false);
        var offset = WriteFileNameAttribute(record, 56, name);

        // 将下一个属性推到 480，使 DATA 值从 504 开始并跨越 510/511 的 USA 尾标记。
        const int dataAttributeOffset = 480;
        WritePaddingAttribute(record, offset, dataAttributeOffset - offset);
        var end = WriteResidentAttribute(record, dataAttributeOffset, 0x80, content);
        FinishRecord(record, end);
    }

    private static void WriteDeletedNonResidentRecord(
        Span<byte> record,
        string name,
        int realSize,
        int secondRunCluster)
    {
        InitializeRecord(record, inUse: false);
        var offset = WriteFileNameAttribute(record, 56, name);
        var delta = secondRunCluster - 30;
        byte[] runList =
        [
            0x11, 0x01, 30,
            0x11, 0x01, checked((byte)delta),
            0x00
        ];
        var end = WriteNonResidentDataAttribute(
            record,
            offset,
            runList,
            clusterCount: 2,
            realSize);
        FinishRecord(record, end);
    }

    private static void InitializeRecord(Span<byte> record, bool inUse)
    {
        record.Clear();
        "FILE"u8.CopyTo(record);
        BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(4, 2), 48);
        BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(6, 2), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(20, 2), 56);
        BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(22, 2), inUse ? (ushort)1 : (ushort)0);
        BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(28, 4), RecordSize);
    }

    private static int WriteFileNameAttribute(Span<byte> record, int offset, string name)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var value = new byte[66 + nameBytes.Length];
        value[64] = checked((byte)name.Length);
        value[65] = 1; // Win32 namespace
        nameBytes.CopyTo(value, 66);
        return WriteResidentAttribute(record, offset, 0x30, value);
    }

    private static int WriteResidentAttribute(
        Span<byte> record,
        int offset,
        uint type,
        ReadOnlySpan<byte> value)
    {
        var length = Align8(24 + value.Length);
        var attribute = record.Slice(offset, length);
        attribute.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(attribute, type);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute.Slice(4, 4), (uint)length);
        attribute[8] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(attribute.Slice(16, 4), (uint)value.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.Slice(20, 2), 24);
        value.CopyTo(attribute.Slice(24));
        return offset + length;
    }

    private static void WritePaddingAttribute(Span<byte> record, int offset, int length)
    {
        Assert.True(length >= 24 && (length & 7) == 0);
        var attribute = record.Slice(offset, length);
        attribute.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(attribute, 0x90);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute.Slice(4, 4), (uint)length);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.Slice(20, 2), 24);
    }

    private static int WriteNonResidentDataAttribute(
        Span<byte> record,
        int offset,
        byte[] runList,
        int clusterCount,
        int realSize)
    {
        var length = Align8(64 + runList.Length);
        var attribute = record.Slice(offset, length);
        attribute.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(attribute, 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute.Slice(4, 4), (uint)length);
        attribute[8] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(attribute.Slice(16, 8), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(
            attribute.Slice(24, 8), checked((ulong)(clusterCount - 1)));
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.Slice(32, 2), 64);
        BinaryPrimitives.WriteUInt64LittleEndian(
            attribute.Slice(40, 8), checked((ulong)(clusterCount * ClusterSize)));
        BinaryPrimitives.WriteUInt64LittleEndian(attribute.Slice(48, 8), checked((ulong)realSize));
        BinaryPrimitives.WriteUInt64LittleEndian(attribute.Slice(56, 8), checked((ulong)realSize));
        runList.AsSpan().CopyTo(attribute.Slice(64));
        return offset + length;
    }

    private static void FinishRecord(Span<byte> record, int endAttributeOffset)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(
            record.Slice(endAttributeOffset, 4), 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(
            record.Slice(24, 4), checked((uint)(endAttributeOffset + 8)));

        const ushort sequence = 0xA55A;
        BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(48, 2), sequence);
        for (var sector = 0; sector < 2; sector++)
        {
            var trailer = (sector + 1) * BytesPerSector - 2;
            record.Slice(trailer, 2).CopyTo(record.Slice(50 + sector * 2, 2));
            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(trailer, 2), sequence);
        }
    }

    private static byte[] CreateNonResidentContent()
    {
        var content = new byte[700];
        for (var index = 0; index < content.Length; index++)
            content[index] = (byte)((index * 17 + 3) & 0xFF);
        return content;
    }

    private static byte[] Recover(byte[] image, RecoveredFile file)
    {
        using var output = new MemoryStream();
        foreach (var extent in file.RecoveryExtents)
        {
            if (extent.IsSparse)
            {
                output.Write(new byte[checked((int)extent.Length)]);
                continue;
            }
            output.Write(image, checked((int)extent.SourceOffset), checked((int)extent.Length));
        }
        return output.ToArray();
    }

    private static int Align8(int value) => (value + 7) & ~7;
}
