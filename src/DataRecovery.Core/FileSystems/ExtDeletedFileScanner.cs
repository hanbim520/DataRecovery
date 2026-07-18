using System.Buffers.Binary;
using DataRecovery.Core.Models;

namespace DataRecovery.Core.FileSystems;

/// <summary>
/// Scans Ext2/Ext3 inode tables for unallocated regular-file inodes whose block
/// pointers have not yet been erased. Ext3 journal records are deliberately not
/// replayed; only the on-disk superblock, group descriptors, bitmaps and inode
/// tables are used.
/// </summary>
public sealed class ExtDeletedFileScanner
{
    public const string Ext3JournalLimitation =
        "Ext3 日志不会被重放；文件系统未正常卸载时，候选文件可能只恢复部分内容。";

    private const ushort ExtMagic = 0xEF53;
    private const ushort RegularFileMode = 0x8000;
    private const ushort FileTypeMask = 0xF000;
    private const uint CompatHasJournal = 0x0004;
    private const uint IncompatFileType = 0x0002;
    private const uint IncompatNeedsRecovery = 0x0004;
    private const uint IncompatExtents = 0x0040;
    private const uint InodeUsesExtents = 0x0008_0000;
    private const int LegacyInodeSize = 128;
    private const int GroupDescriptorSize = 32;
    private const int DirectBlockCount = 12;
    private const int MaximumGroups = 1_000_000;
    private const int MaximumCandidates = 100_000;
    private const long MaximumMappedBlocksPerFile = 131_072;

    public async Task<IReadOnlyList<RecoveredFile>> ScanAsync(
        FileStream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
            return Array.Empty<RecoveredFile>();

        var streamLength = TryGetLength(stream);
        var superblock = new byte[1024];
        if (!await TryReadExactlyAtAsync(stream, 1024, superblock, streamLength, cancellationToken) ||
            !TryReadLayout(superblock, out var layout))
        {
            return Array.Empty<RecoveredFile>();
        }

        var descriptors = await ReadGroupDescriptorsAsync(
            stream, layout, streamLength, cancellationToken);
        var context = new ScanContext(stream, streamLength, layout, descriptors);
        var recovered = new List<RecoveredFile>();

        for (var groupIndex = 0;
             groupIndex < descriptors.Length && recovered.Count < MaximumCandidates;
             groupIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var descriptor = descriptors[groupIndex];
            if (!descriptor.IsValid || descriptor.InodesInGroup == 0)
                continue;

            var inodeBitmap = new byte[layout.BlockSize];
            if (!await context.TryReadBlockAsync(
                    descriptor.InodeBitmap, inodeBitmap, cancellationToken))
            {
                continue;
            }

            var inodesPerTableBlock = layout.BlockSize / layout.InodeSize;
            var tableBlock = new byte[layout.BlockSize];
            var tableBlockCount = DivideRoundUp(descriptor.InodesInGroup, (uint)inodesPerTableBlock);

            for (uint tableBlockIndex = 0;
                 tableBlockIndex < tableBlockCount && recovered.Count < MaximumCandidates;
                 tableBlockIndex++)
            {
                var physicalTableBlock = (ulong)descriptor.InodeTable + tableBlockIndex;
                if (physicalTableBlock >= layout.BlocksCount ||
                    !await context.TryReadBlockAsync(
                        (uint)physicalTableBlock, tableBlock, cancellationToken))
                {
                    break;
                }

                var firstInodeInBlock = (ulong)tableBlockIndex * (uint)inodesPerTableBlock;
                var remainingInodes = (ulong)descriptor.InodesInGroup - firstInodeInBlock;
                var inodeCount = (int)Math.Min((ulong)inodesPerTableBlock, remainingInodes);
                for (var slot = 0; slot < inodeCount; slot++)
                {
                    var inodeIndexInGroup = firstInodeInBlock + (uint)slot;
                    if (IsBitmapBitSet(inodeBitmap, inodeIndexInGroup))
                        continue;

                    var inodeNumber = (ulong)groupIndex * layout.InodesPerGroup +
                                      inodeIndexInGroup + 1;
                    if (inodeNumber < layout.FirstNonReservedInode || inodeNumber > uint.MaxValue)
                        continue;

                    var inodeBytes = tableBlock.AsSpan(
                        slot * layout.InodeSize, layout.InodeSize);
                    if (!TryParseDeletedInode(inodeBytes, layout, out var inode))
                        continue;

                    var mapping = await BuildRecoveryExtentsAsync(
                        context, inode, cancellationToken);
                    if (mapping.PhysicalDataBlockCount == 0 || mapping.Extents.Count == 0)
                        continue;

                    var fileSystemName = layout.HasJournal ? "Ext3" : "Ext2";
                    var signature = layout.HasJournal
                        ? "Ext3 删除 inode（未重放 journal）"
                        : "Ext2 删除 inode";
                    var isPartial = mapping.IsPartial || inode.HasWeakDeletionEvidence ||
                                    layout.NeedsJournalRecovery;
                    recovered.Add(new RecoveredFile(
                        $"恢复候选_inode_{inodeNumber:D8}.bin",
                        $"已删除文件/{fileSystemName} inode {inodeNumber}",
                        inode.FileSize,
                        "其他",
                        isPartial ? RecoveryState.Partial : RecoveryState.Good,
                        mapping.FirstDataOffset,
                        signature)
                    {
                        RecoveryExtents = mapping.Extents
                    });
                }
            }
        }

        return recovered;
    }

    private static bool TryReadLayout(byte[] superblock, out ExtLayout layout)
    {
        layout = default!;
        var data = superblock.AsSpan();
        if (BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(56, 2)) != ExtMagic)
            return false;

        var inodesCount = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0, 4));
        var blocksCount = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4));
        var firstDataBlock = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(20, 4));
        var logBlockSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(24, 4));
        var blocksPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(32, 4));
        var inodesPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(40, 4));
        var revision = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(76, 4));
        var featureCompat = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(92, 4));
        var featureIncompat = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(96, 4));
        var featureReadOnlyCompat = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(100, 4));

        // Extents, 64-bit block numbers, meta block groups and other Ext4-only
        // layouts require different metadata structures and must not be guessed.
        const uint supportedIncompat = IncompatFileType | IncompatNeedsRecovery;
        const uint supportedReadOnlyCompat = 0x0001 | 0x0002 | 0x0004;
        if ((featureIncompat & IncompatExtents) != 0 ||
            (featureIncompat & ~supportedIncompat) != 0 ||
            (featureReadOnlyCompat & ~supportedReadOnlyCompat) != 0 ||
            logBlockSize > 6 || inodesCount == 0 || blocksCount == 0 ||
            blocksPerGroup == 0 || inodesPerGroup == 0 || blocksCount <= firstDataBlock)
        {
            return false;
        }

        var blockSizeLong = 1024L << (int)logBlockSize;
        if (blockSizeLong is < 1024 or > 65_536)
            return false;
        var blockSize = (int)blockSizeLong;
        if (blocksPerGroup > (uint)blockSize * 8 ||
            inodesPerGroup > (uint)blockSize * 8)
        {
            return false;
        }

        var inodeSize = revision == 0
            ? LegacyInodeSize
            : BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(88, 2));
        if (inodeSize == 0) inodeSize = LegacyInodeSize;
        if (inodeSize < LegacyInodeSize || inodeSize > blockSize ||
            inodeSize % 4 != 0 || blockSize % inodeSize != 0)
        {
            return false;
        }

        var blockGroupCount = DivideRoundUp(
            (ulong)blocksCount - firstDataBlock, blocksPerGroup);
        var inodeGroupCount = DivideRoundUp(inodesCount, inodesPerGroup);
        var groupCount64 = Math.Max(blockGroupCount, inodeGroupCount);
        if (groupCount64 is 0 or > MaximumGroups)
            return false;
        var groupCount = (int)groupCount64;

        var descriptorStartBlock = blockSize == 1024 ? 2u : 1u;
        var descriptorBlocks = DivideRoundUp(
            checked((ulong)groupCount * GroupDescriptorSize), (uint)blockSize);
        var descriptorTableOffset = checked((long)descriptorStartBlock * blockSize);
        var volumeBytes = checked((long)blocksCount * blockSize);
        var descriptorEnd = checked(
            descriptorTableOffset + (long)groupCount * GroupDescriptorSize);
        if (descriptorEnd > volumeBytes)
            return false;

        var firstNonReservedInode = revision == 0
            ? 11u
            : BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(84, 4));
        if (firstNonReservedInode < 11)
            firstNonReservedInode = 11;

        layout = new ExtLayout(
            inodesCount,
            blocksCount,
            firstDataBlock,
            blocksPerGroup,
            inodesPerGroup,
            blockSize,
            inodeSize,
            groupCount,
            descriptorStartBlock,
            (uint)descriptorBlocks,
            descriptorTableOffset,
            volumeBytes,
            firstNonReservedInode,
            (featureCompat & CompatHasJournal) != 0,
            (featureIncompat & IncompatNeedsRecovery) != 0,
            revision);
        return true;
    }

    private static async Task<GroupDescriptor[]> ReadGroupDescriptorsAsync(
        FileStream stream,
        ExtLayout layout,
        long streamLength,
        CancellationToken cancellationToken)
    {
        var descriptors = new GroupDescriptor[layout.GroupCount];
        var buffer = new byte[GroupDescriptorSize];
        for (var groupIndex = 0; groupIndex < descriptors.Length; groupIndex++)
        {
            var offset = checked(
                layout.DescriptorTableOffset + (long)groupIndex * GroupDescriptorSize);
            if (!await TryReadExactlyAtAsync(
                    stream, offset, buffer, streamLength, cancellationToken))
            {
                break;
            }

            var blockBitmap = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0, 4));
            var inodeBitmap = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4, 4));
            var inodeTable = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(8, 4));
            var firstInode = (ulong)groupIndex * layout.InodesPerGroup;
            var remainingInodes = firstInode >= layout.InodesCount
                ? 0ul
                : (ulong)layout.InodesCount - firstInode;
            var inodesInGroup = (uint)Math.Min((ulong)layout.InodesPerGroup, remainingInodes);
            var inodeTableBlocks = DivideRoundUp(
                checked((ulong)inodesInGroup * (uint)layout.InodeSize),
                (uint)layout.BlockSize);

            var valid = blockBitmap < layout.BlocksCount &&
                        inodeBitmap < layout.BlocksCount &&
                        inodeTable < layout.BlocksCount &&
                        blockBitmap >= layout.FirstDataBlock &&
                        inodeBitmap >= layout.FirstDataBlock &&
                        inodeTable >= layout.FirstDataBlock &&
                        (ulong)inodeTable + inodeTableBlocks <= layout.BlocksCount;
            descriptors[groupIndex] = new GroupDescriptor(
                blockBitmap, inodeBitmap, inodeTable, inodesInGroup,
                (uint)inodeTableBlocks, valid);
        }

        return descriptors;
    }

    private static bool TryParseDeletedInode(
        ReadOnlySpan<byte> inodeBytes,
        ExtLayout layout,
        out DeletedInode inode)
    {
        inode = default!;
        var mode = BinaryPrimitives.ReadUInt16LittleEndian(inodeBytes.Slice(0, 2));
        if ((mode & FileTypeMask) != RegularFileMode)
            return false;

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(inodeBytes.Slice(32, 4));
        if ((flags & InodeUsesExtents) != 0)
            return false;

        var sizeLow = BinaryPrimitives.ReadUInt32LittleEndian(inodeBytes.Slice(4, 4));
        var sizeHigh = layout.Revision == 0
            ? 0u
            : BinaryPrimitives.ReadUInt32LittleEndian(inodeBytes.Slice(108, 4));
        var fileSize64 = ((ulong)sizeHigh << 32) | sizeLow;
        if (fileSize64 == 0 || fileSize64 > long.MaxValue ||
            fileSize64 > (ulong)layout.VolumeBytes)
        {
            return false;
        }

        var pointers = new uint[15];
        var hasPointer = false;
        for (var index = 0; index < pointers.Length; index++)
        {
            pointers[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                inodeBytes.Slice(40 + index * 4, 4));
            hasPointer |= pointers[index] != 0;
        }
        if (!hasPointer)
            return false;

        var deletionTime = BinaryPrimitives.ReadUInt32LittleEndian(inodeBytes.Slice(20, 4));
        var linkCount = BinaryPrimitives.ReadUInt16LittleEndian(inodeBytes.Slice(26, 2));
        inode = new DeletedInode(
            (long)fileSize64,
            pointers,
            deletionTime == 0 && linkCount != 0);
        return true;
    }

    private static async Task<ExtentMapping> BuildRecoveryExtentsAsync(
        ScanContext context,
        DeletedInode inode,
        CancellationToken cancellationToken)
    {
        var blockSize = context.Layout.BlockSize;
        var totalLogicalBlocks = DivideRoundUp((ulong)inode.FileSize, (uint)blockSize);
        var supportedByAddressing = (ulong)DirectBlockCount +
                                    (uint)(blockSize / sizeof(uint)) +
                                    (ulong)(blockSize / sizeof(uint)) *
                                    (uint)(blockSize / sizeof(uint));
        var blocksToMap = (long)Math.Min(
            totalLogicalBlocks,
            Math.Min((ulong)MaximumMappedBlocksPerFile, supportedByAddressing));
        var bytesToMap = Math.Min(inode.FileSize, checked(blocksToMap * (long)blockSize));
        var accumulator = new ExtentAccumulator(inode.FileSize, blockSize);

        var directBlocks = (int)Math.Min(blocksToMap, DirectBlockCount);
        for (var index = 0; index < directBlocks; index++)
        {
            await AppendDataBlockAsync(
                context, accumulator, inode.BlockPointers[index], cancellationToken);
        }

        var remainingBlocks = blocksToMap - directBlocks;
        var pointersPerBlock = blockSize / sizeof(uint);
        var singleBlocks = (int)Math.Min(remainingBlocks, pointersPerBlock);
        if (singleBlocks > 0)
        {
            await AppendSingleIndirectAsync(
                context, accumulator, inode.BlockPointers[12], singleBlocks,
                cancellationToken);
            remainingBlocks -= singleBlocks;
        }

        if (remainingBlocks > 0)
        {
            await AppendDoubleIndirectAsync(
                context, accumulator, inode.BlockPointers[13], remainingBlocks,
                pointersPerBlock, cancellationToken);
        }

        var unmappedBytes = inode.FileSize - bytesToMap;
        var missingMappedBytes = accumulator.RemainingBytes - unmappedBytes;
        if (missingMappedBytes > 0)
        {
            accumulator.MarkPartial();
            accumulator.AppendSparseBytes(missingMappedBytes);
        }

        if (unmappedBytes > 0)
        {
            // Triple-indirect data and the safety cap are represented as holes.
            // This preserves the logical output size without reading guessed blocks.
            accumulator.MarkPartial();
            accumulator.AppendSparseBytes(unmappedBytes);
        }

        return accumulator.ToMapping();
    }

    private static async Task AppendSingleIndirectAsync(
        ScanContext context,
        ExtentAccumulator accumulator,
        uint indirectBlock,
        int logicalBlockCount,
        CancellationToken cancellationToken)
    {
        if (logicalBlockCount <= 0) return;
        if (indirectBlock == 0)
        {
            accumulator.AppendSparseBlocks(logicalBlockCount);
            return;
        }

        var pointerBytes = new byte[context.Layout.BlockSize];
        if (!context.IsUsableDataBlock(indirectBlock) ||
            !await context.TryReadBlockAsync(indirectBlock, pointerBytes, cancellationToken))
        {
            accumulator.MarkPartial();
            accumulator.AppendSparseBlocks(logicalBlockCount);
            return;
        }

        if (await context.IsBlockAllocatedAsync(indirectBlock, cancellationToken) is not false)
            accumulator.MarkPartial();

        for (var index = 0; index < logicalBlockCount; index++)
        {
            var pointer = BinaryPrimitives.ReadUInt32LittleEndian(
                pointerBytes.AsSpan(index * sizeof(uint), sizeof(uint)));
            await AppendDataBlockAsync(context, accumulator, pointer, cancellationToken);
        }
    }

    private static async Task AppendDoubleIndirectAsync(
        ScanContext context,
        ExtentAccumulator accumulator,
        uint doubleIndirectBlock,
        long logicalBlockCount,
        int pointersPerBlock,
        CancellationToken cancellationToken)
    {
        if (logicalBlockCount <= 0) return;
        if (doubleIndirectBlock == 0)
        {
            accumulator.AppendSparseBlocks(logicalBlockCount);
            return;
        }

        var rootPointers = new byte[context.Layout.BlockSize];
        if (!context.IsUsableDataBlock(doubleIndirectBlock) ||
            !await context.TryReadBlockAsync(doubleIndirectBlock, rootPointers, cancellationToken))
        {
            accumulator.MarkPartial();
            accumulator.AppendSparseBlocks(logicalBlockCount);
            return;
        }

        if (await context.IsBlockAllocatedAsync(doubleIndirectBlock, cancellationToken) is not false)
            accumulator.MarkPartial();

        var remaining = logicalBlockCount;
        for (var index = 0; index < pointersPerBlock && remaining > 0; index++)
        {
            var childCount = (int)Math.Min(remaining, pointersPerBlock);
            var childBlock = BinaryPrimitives.ReadUInt32LittleEndian(
                rootPointers.AsSpan(index * sizeof(uint), sizeof(uint)));
            await AppendSingleIndirectAsync(
                context, accumulator, childBlock, childCount, cancellationToken);
            remaining -= childCount;
        }

        if (remaining > 0)
        {
            accumulator.MarkPartial();
            accumulator.AppendSparseBlocks(remaining);
        }
    }

    private static async Task AppendDataBlockAsync(
        ScanContext context,
        ExtentAccumulator accumulator,
        uint dataBlock,
        CancellationToken cancellationToken)
    {
        if (accumulator.RemainingBytes <= 0) return;
        if (dataBlock == 0)
        {
            accumulator.AppendSparseBlocks(1);
            return;
        }

        if (!context.IsUsableDataBlock(dataBlock))
        {
            accumulator.MarkPartial();
            accumulator.AppendSparseBlocks(1);
            return;
        }

        var allocated = await context.IsBlockAllocatedAsync(dataBlock, cancellationToken);
        if (allocated is not false)
            accumulator.MarkPartial();
        accumulator.AppendDataBlock(checked((long)dataBlock * context.Layout.BlockSize));
    }

    private static bool IsBitmapBitSet(byte[] bitmap, ulong bitIndex)
    {
        var byteIndex = bitIndex >> 3;
        return byteIndex < (ulong)bitmap.Length &&
               (bitmap[(int)byteIndex] & (1 << (int)(bitIndex & 7))) != 0;
    }

    private static ulong DivideRoundUp(ulong value, uint divisor) =>
        value == 0 ? 0 : (value - 1) / divisor + 1;

    private static long TryGetLength(FileStream stream)
    {
        try { return stream.Length; }
        catch (IOException) { return -1; }
        catch (NotSupportedException) { return -1; }
    }

    private static async Task<bool> TryReadExactlyAtAsync(
        FileStream stream,
        long offset,
        Memory<byte> buffer,
        long streamLength,
        CancellationToken cancellationToken)
    {
        if (offset < 0 ||
            (streamLength >= 0 && (offset > streamLength || buffer.Length > streamLength - offset)))
        {
            return false;
        }

        try
        {
            stream.Position = offset;
            var read = 0;
            while (read < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer[read..], cancellationToken);
                if (count == 0) return false;
                read += count;
            }
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private sealed class ScanContext(
        FileStream stream,
        long streamLength,
        ExtLayout layout,
        GroupDescriptor[] descriptors)
    {
        private readonly Dictionary<int, byte[]?> _blockBitmaps = new();

        public ExtLayout Layout { get; } = layout;

        public bool IsUsableDataBlock(uint block)
        {
            if (block == 0 || block >= Layout.BlocksCount || block < Layout.FirstDataBlock)
                return false;
            if (block == (Layout.BlockSize == 1024 ? 1u : 0u))
                return false;
            if (block >= Layout.DescriptorStartBlock &&
                block - Layout.DescriptorStartBlock < Layout.DescriptorBlockCount)
            {
                return false;
            }

            var groupIndex64 = ((ulong)block - Layout.FirstDataBlock) / Layout.BlocksPerGroup;
            if (groupIndex64 >= (ulong)descriptors.Length)
                return false;
            var descriptor = descriptors[(int)groupIndex64];
            if (!descriptor.IsValid || block == descriptor.BlockBitmap ||
                block == descriptor.InodeBitmap)
            {
                return false;
            }
            return block < descriptor.InodeTable ||
                   block - descriptor.InodeTable >= descriptor.InodeTableBlocks;
        }

        public Task<bool> TryReadBlockAsync(
            uint block,
            Memory<byte> destination,
            CancellationToken cancellationToken)
        {
            if (block >= Layout.BlocksCount || destination.Length != Layout.BlockSize)
                return Task.FromResult(false);
            var offset = checked((long)block * Layout.BlockSize);
            return TryReadExactlyAtAsync(
                stream, offset, destination, streamLength, cancellationToken);
        }

        public async Task<bool?> IsBlockAllocatedAsync(
            uint block,
            CancellationToken cancellationToken)
        {
            if (block < Layout.FirstDataBlock || block >= Layout.BlocksCount)
                return null;
            var groupIndex64 = ((ulong)block - Layout.FirstDataBlock) / Layout.BlocksPerGroup;
            if (groupIndex64 >= (ulong)descriptors.Length)
                return null;
            var groupIndex = (int)groupIndex64;
            if (!_blockBitmaps.TryGetValue(groupIndex, out var bitmap))
            {
                var descriptor = descriptors[groupIndex];
                if (!descriptor.IsValid)
                {
                    _blockBitmaps[groupIndex] = null;
                    return null;
                }
                bitmap = new byte[Layout.BlockSize];
                if (!await TryReadBlockAsync(
                        descriptor.BlockBitmap, bitmap, cancellationToken))
                {
                    bitmap = null;
                }
                _blockBitmaps[groupIndex] = bitmap;
            }
            if (bitmap is null) return null;

            var groupFirstBlock = (ulong)Layout.FirstDataBlock +
                                  (ulong)groupIndex * Layout.BlocksPerGroup;
            var bitIndex = (ulong)block - groupFirstBlock;
            if (bitIndex >= Layout.BlocksPerGroup || bitIndex >= (ulong)bitmap.Length * 8)
                return null;
            return IsBitmapBitSet(bitmap, bitIndex);
        }
    }

    private sealed class ExtentAccumulator(long remainingBytes, int blockSize)
    {
        private readonly List<RecoveryExtent> _extents = new();

        public long RemainingBytes { get; private set; } = remainingBytes;
        public int PhysicalDataBlockCount { get; private set; }
        public bool IsPartial { get; private set; }
        public long FirstDataOffset { get; private set; } = -1;

        public void MarkPartial() => IsPartial = true;

        public void AppendDataBlock(long sourceOffset)
        {
            var length = Math.Min(RemainingBytes, blockSize);
            if (length <= 0) return;
            AppendExtent(new RecoveryExtent(sourceOffset, length));
            FirstDataOffset = FirstDataOffset < 0 ? sourceOffset : FirstDataOffset;
            PhysicalDataBlockCount++;
            RemainingBytes -= length;
        }

        public void AppendSparseBlocks(long blockCount)
        {
            if (blockCount <= 0 || RemainingBytes <= 0) return;
            long maximumLength;
            try { maximumLength = checked(blockCount * (long)blockSize); }
            catch (OverflowException) { maximumLength = long.MaxValue; }
            AppendSparseBytes(Math.Min(RemainingBytes, maximumLength));
        }

        public void AppendSparseBytes(long length)
        {
            length = Math.Min(Math.Max(0, length), RemainingBytes);
            if (length <= 0) return;
            AppendExtent(new RecoveryExtent(0, length, true));
            RemainingBytes -= length;
        }

        public ExtentMapping ToMapping() => new(
            _extents.ToArray(),
            PhysicalDataBlockCount,
            IsPartial,
            FirstDataOffset);

        private void AppendExtent(RecoveryExtent extent)
        {
            if (_extents.Count > 0)
            {
                var previous = _extents[^1];
                var canMergeSparse = previous.IsSparse && extent.IsSparse;
                var canMergePhysical = !previous.IsSparse && !extent.IsSparse &&
                                       previous.SourceOffset <= long.MaxValue - previous.Length &&
                                       previous.SourceOffset + previous.Length == extent.SourceOffset;
                if (canMergeSparse || canMergePhysical)
                {
                    _extents[^1] = previous with
                    {
                        Length = checked(previous.Length + extent.Length)
                    };
                    return;
                }
            }
            _extents.Add(extent);
        }
    }

    private sealed record ExtLayout(
        uint InodesCount,
        uint BlocksCount,
        uint FirstDataBlock,
        uint BlocksPerGroup,
        uint InodesPerGroup,
        int BlockSize,
        int InodeSize,
        int GroupCount,
        uint DescriptorStartBlock,
        uint DescriptorBlockCount,
        long DescriptorTableOffset,
        long VolumeBytes,
        uint FirstNonReservedInode,
        bool HasJournal,
        bool NeedsJournalRecovery,
        uint Revision);

    private sealed record GroupDescriptor(
        uint BlockBitmap,
        uint InodeBitmap,
        uint InodeTable,
        uint InodesInGroup,
        uint InodeTableBlocks,
        bool IsValid);

    private sealed record DeletedInode(
        long FileSize,
        uint[] BlockPointers,
        bool HasWeakDeletionEvidence);

    private sealed record ExtentMapping(
        IReadOnlyList<RecoveryExtent> Extents,
        int PhysicalDataBlockCount,
        bool IsPartial,
        long FirstDataOffset);
}
