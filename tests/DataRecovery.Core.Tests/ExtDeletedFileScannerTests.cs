using System.Buffers.Binary;
using DataRecovery.Core.FileSystems;
using DataRecovery.Core.Models;
using DataRecovery.Core.Services;

namespace DataRecovery.Core.Tests;

public sealed class ExtDeletedFileScannerTests
{
    private const int BlockSize = 1024;
    private const int InodeSize = 128;
    private const int TargetInode = 12;

    [Fact]
    public async Task Ext2RecoversDeletedDirectBlocksAndCoalescesPhysicalExtents()
    {
        var image = CreateImage(hasJournal: false, totalBlocks: 128);
        WriteDeletedInode(image, fileSize: 1500, pointers: [20, 21]);
        FillBlock(image, 20, 0x41);
        FillBlock(image, 21, 0x42);

        var files = await ScanImageAsync(image);

        var file = Assert.Single(files);
        Assert.Equal("恢复候选_inode_00000012.bin", file.Name);
        Assert.Equal("已删除文件/Ext2 inode 12", file.OriginalPath);
        Assert.Equal("Ext2 删除 inode", file.Signature);
        Assert.Equal(1500, file.Size);
        Assert.Equal(20 * BlockSize, file.Offset);
        Assert.Equal(RecoveryState.Good, file.State);
        var extent = Assert.Single(file.RecoveryExtents);
        Assert.False(extent.IsSparse);
        Assert.Equal(20 * BlockSize, extent.SourceOffset);
        Assert.Equal(1500, extent.Length);

        var restored = Materialize(image, file);
        Assert.All(restored[..BlockSize], value => Assert.Equal(0x41, value));
        Assert.All(restored[BlockSize..], value => Assert.Equal(0x42, value));
    }

    [Fact]
    public async Task Ext3RecoversSingleAndDoubleIndirectBlocksWithoutJournalReplay()
    {
        var image = CreateImage(hasJournal: true, totalBlocks: 512);
        var pointers = new uint[14];
        pointers[0] = 40;
        pointers[12] = 20;
        pointers[13] = 21;
        WriteDeletedInode(image, fileSize: 269L * BlockSize, pointers);

        WritePointer(image, pointerBlock: 20, index: 0, dataBlock: 41);
        WritePointer(image, pointerBlock: 21, index: 0, dataBlock: 22);
        WritePointer(image, pointerBlock: 22, index: 0, dataBlock: 42);
        FillBlock(image, 40, 0x11);
        FillBlock(image, 41, 0x22);
        FillBlock(image, 42, 0x33);

        var files = await ScanImageAsync(image);

        var file = Assert.Single(files);
        Assert.Equal("Ext3 删除 inode（未重放 journal）", file.Signature);
        Assert.Contains("不", ExtDeletedFileScanner.Ext3JournalLimitation);
        Assert.Equal(RecoveryState.Good, file.State);
        Assert.Equal(269L * BlockSize, file.Size);
        Assert.Collection(
            file.RecoveryExtents,
            extent => AssertExtent(extent, 40L * BlockSize, BlockSize, false),
            extent => AssertExtent(extent, 0, 11L * BlockSize, true),
            extent => AssertExtent(extent, 41L * BlockSize, BlockSize, false),
            extent => AssertExtent(extent, 0, 255L * BlockSize, true),
            extent => AssertExtent(extent, 42L * BlockSize, BlockSize, false));

        var restored = Materialize(image, file);
        Assert.Equal(0x11, restored[0]);
        Assert.Equal(0, restored[BlockSize]);
        Assert.Equal(0x22, restored[12 * BlockSize]);
        Assert.Equal(0, restored[13 * BlockSize]);
        Assert.Equal(0x33, restored[268 * BlockSize]);
    }

    [Fact]
    public async Task AllocatedInodeIsNotReportedAsDeleted()
    {
        var image = CreateImage(hasJournal: false, totalBlocks: 128);
        WriteDeletedInode(image, fileSize: BlockSize, pointers: [20]);
        SetBitmapBit(image, bitmapBlock: 4, TargetInode - 1);

        var files = await ScanImageAsync(image);

        Assert.Empty(files);
    }

    [Fact]
    public async Task OutOfRangeBlockBecomesSparseAndMarksCandidatePartial()
    {
        var image = CreateImage(hasJournal: false, totalBlocks: 128);
        WriteDeletedInode(image, fileSize: 2L * BlockSize, pointers: [999, 20]);
        FillBlock(image, 20, 0x5A);

        var files = await ScanImageAsync(image);

        var file = Assert.Single(files);
        Assert.Equal(RecoveryState.Partial, file.State);
        Assert.Equal(20L * BlockSize, file.Offset);
        Assert.Collection(
            file.RecoveryExtents,
            extent => AssertExtent(extent, 0, BlockSize, true),
            extent => AssertExtent(extent, 20L * BlockSize, BlockSize, false));
        var restored = Materialize(image, file);
        Assert.All(restored[..BlockSize], value => Assert.Equal(0, value));
        Assert.All(restored[BlockSize..], value => Assert.Equal(0x5A, value));
    }

    [Fact]
    public async Task RecoveryScannerRoutesExtDeletedMetadataScan()
    {
        var image = CreateImage(hasJournal: false, totalBlocks: 128);
        WriteDeletedInode(image, fileSize: BlockSize, pointers: [20]);
        FillBlock(image, 20, 0x4D);
        var path = Path.Combine(Path.GetTempPath(), $"ext-route-{Guid.NewGuid():N}.img");
        try
        {
            await File.WriteAllBytesAsync(path, image);
            var result = await new RecoveryScanner(new FileSystemDetector())
                .ScanAsync(path, ScanMode.DeletedFiles);

            Assert.Equal(FileSystemKind.Ext2, result.FileSystem.Kind);
            Assert.Equal("恢复候选_inode_00000012.bin", Assert.Single(result.Files).Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] CreateImage(bool hasJournal, int totalBlocks)
    {
        var image = new byte[totalBlocks * BlockSize];
        var superblock = image.AsSpan(BlockSize, BlockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(superblock.Slice(0, 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(superblock.Slice(4, 4), (uint)totalBlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(superblock.Slice(20, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(superblock.Slice(24, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(superblock.Slice(32, 4), (uint)totalBlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(superblock.Slice(40, 4), 32);
        BinaryPrimitives.WriteUInt16LittleEndian(superblock.Slice(56, 2), 0xEF53);
        BinaryPrimitives.WriteUInt32LittleEndian(superblock.Slice(76, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(superblock.Slice(84, 4), 11);
        BinaryPrimitives.WriteUInt16LittleEndian(superblock.Slice(88, 2), InodeSize);
        BinaryPrimitives.WriteUInt32LittleEndian(
            superblock.Slice(92, 4), hasJournal ? 0x0004u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(superblock.Slice(96, 4), 0x0002);
        BinaryPrimitives.WriteUInt32LittleEndian(superblock.Slice(100, 4), 0x0002);

        var descriptor = image.AsSpan(2 * BlockSize, 32);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.Slice(0, 4), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.Slice(4, 4), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.Slice(8, 4), 5);

        // Superblock, descriptor table, bitmaps and inode table are allocated.
        for (var block = 1; block <= 8; block++)
            SetBitmapBit(image, bitmapBlock: 3, block - 1);
        // Reserve the standard Ext inodes; inode 12 deliberately remains free.
        for (var inodeIndex = 0; inodeIndex < TargetInode - 1; inodeIndex++)
            SetBitmapBit(image, bitmapBlock: 4, inodeIndex);
        return image;
    }

    private static void WriteDeletedInode(byte[] image, long fileSize, IReadOnlyList<uint> pointers)
    {
        var inodeOffset = 5 * BlockSize + (TargetInode - 1) * InodeSize;
        var inode = image.AsSpan(inodeOffset, InodeSize);
        BinaryPrimitives.WriteUInt16LittleEndian(inode.Slice(0, 2), 0x81A4);
        BinaryPrimitives.WriteUInt32LittleEndian(inode.Slice(4, 4), (uint)fileSize);
        BinaryPrimitives.WriteUInt32LittleEndian(inode.Slice(20, 4), 1_700_000_000);
        BinaryPrimitives.WriteUInt16LittleEndian(inode.Slice(26, 2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(inode.Slice(108, 4), (uint)((ulong)fileSize >> 32));
        for (var index = 0; index < Math.Min(15, pointers.Count); index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                inode.Slice(40 + index * sizeof(uint), sizeof(uint)), pointers[index]);
        }
    }

    private static async Task<IReadOnlyList<RecoveredFile>> ScanImageAsync(byte[] image)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ext-deleted-{Guid.NewGuid():N}.img");
        try
        {
            await File.WriteAllBytesAsync(path, image);
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await new ExtDeletedFileScanner().ScanAsync(stream);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] Materialize(byte[] image, RecoveredFile file)
    {
        var restored = new byte[checked((int)file.Size)];
        var destinationOffset = 0;
        foreach (var extent in file.RecoveryExtents)
        {
            var length = checked((int)extent.Length);
            if (!extent.IsSparse)
            {
                Buffer.BlockCopy(
                    image, checked((int)extent.SourceOffset),
                    restored, destinationOffset, length);
            }
            destinationOffset += length;
        }
        Assert.Equal(restored.Length, destinationOffset);
        return restored;
    }

    private static void FillBlock(byte[] image, int block, byte value) =>
        image.AsSpan(block * BlockSize, BlockSize).Fill(value);

    private static void WritePointer(byte[] image, int pointerBlock, int index, uint dataBlock) =>
        BinaryPrimitives.WriteUInt32LittleEndian(
            image.AsSpan(pointerBlock * BlockSize + index * sizeof(uint), sizeof(uint)),
            dataBlock);

    private static void SetBitmapBit(byte[] image, int bitmapBlock, int bitIndex) =>
        image[bitmapBlock * BlockSize + bitIndex / 8] |= (byte)(1 << (bitIndex & 7));

    private static void AssertExtent(
        RecoveryExtent extent, long sourceOffset, long length, bool isSparse)
    {
        Assert.Equal(sourceOffset, extent.SourceOffset);
        Assert.Equal(length, extent.Length);
        Assert.Equal(isSparse, extent.IsSparse);
    }
}
