using System.Buffers.Binary;
using System.Text;
using DataRecovery.Core.FileSystems;
using DataRecovery.Core.Models;
using DataRecovery.Core.Services;

namespace DataRecovery.Core.Tests;

public sealed class ExFatPreviousVolumeScannerTests
{
    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(4096)]
    public async Task FindsActiveAndDeletedFilesBehindNtfsBootSector(int bytesPerSector)
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"previous-exfat-{bytesPerSector}-{Guid.NewGuid():N}.img");
        try
        {
            await File.WriteAllBytesAsync(path, CreateReformattedImage(bytesPerSector));
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);

            var currentFileSystem = await new FileSystemDetector().DetectAsync(stream);
            Assert.Equal(FileSystemKind.Ntfs, currentFileSystem.Kind);

            var scanner = new ExFatDeletedFileScanner();
            var currentExFatFiles = await scanner.ScanAsync(stream);
            stream.Position = 37;
            var files = await scanner.ScanPreviousExFatVolumeAsync(stream);

            Assert.Empty(currentExFatFiles);
            Assert.Equal(37, stream.Position);
            Assert.Equal(2, files.Count);

            var active = Assert.Single(files, file => file.Name == "before.png");
            Assert.Equal("格式化前 exFAT/原有文件", active.OriginalPath);
            Assert.Equal("格式化前 exFAT 活动目录项", active.Signature);
            Assert.Equal(RecoveryState.Good, active.State);
            Assert.Equal(48, active.Size);
            Assert.Equal(33L * bytesPerSector, active.Offset);
            Assert.Equal(active.Offset, Assert.Single(active.RecoveryExtents).SourceOffset);

            var deleted = Assert.Single(files, file => file.Name == "deleted.exe");
            Assert.Equal("格式化前 exFAT/已删除文件", deleted.OriginalPath);
            Assert.Equal("格式化前 exFAT 删除目录项", deleted.Signature);
            Assert.Equal(RecoveryState.Partial, deleted.State);
            Assert.Equal(64, deleted.Size);
            Assert.Equal(34L * bytesPerSector, deleted.Offset);

            Assert.DoesNotContain(files, file => file.Name == "fragmented.bin");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RejectsBackupBootGeometryOutsideSourceBoundary()
    {
        const int bytesPerSector = 512;
        var image = CreateReformattedImage(bytesPerSector);
        Array.Resize(ref image, image.Length - bytesPerSector);
        var path = Path.Combine(Path.GetTempPath(), $"invalid-previous-exfat-{Guid.NewGuid():N}.img");
        try
        {
            await File.WriteAllBytesAsync(path, image);
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);

            var files = await new ExFatDeletedFileScanner()
                .ScanPreviousExFatVolumeAsync(stream);

            Assert.Empty(files);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RecoveryScannerIncludesPreviousMetadataInFormattedScan()
    {
        var path = Path.Combine(Path.GetTempPath(), $"previous-exfat-route-{Guid.NewGuid():N}.img");
        try
        {
            await File.WriteAllBytesAsync(path, CreateReformattedImage(512));

            var result = await new RecoveryScanner(new FileSystemDetector())
                .ScanAsync(path, ScanMode.LostFiles);

            Assert.Equal(FileSystemKind.Ntfs, result.FileSystem.Kind);
            Assert.Contains(result.Files, file => file.Name == "before.png");
            Assert.Contains(result.Files, file => file.Name == "deleted.exe");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] CreateReformattedImage(int bytesPerSector)
    {
        const int volumeSectors = 160;
        const int fatOffset = 24;
        const int fatLength = 1;
        const int clusterHeapOffset = 32;
        const int clusterCount = 120;
        const int rootCluster = 2;
        var image = new byte[volumeSectors * bytesPerSector];

        // 当前卷已经被快速格式化为 NTFS。
        Encoding.ASCII.GetBytes("NTFS    ").CopyTo(image, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(11, 2), (ushort)bytesPerSector);
        BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(40, 8), volumeSectors);
        image[510] = 0x55;
        image[511] = 0xAA;

        // exFAT 主引导区已被覆盖，但第 12 扇区的备份引导扇区仍存在。
        var backupOffset = 12 * bytesPerSector;
        var boot = image.AsSpan(backupOffset, 512);
        boot[0] = 0xEB;
        boot[1] = 0x76;
        boot[2] = 0x90;
        Encoding.ASCII.GetBytes("EXFAT   ").CopyTo(boot[3..]);
        BinaryPrimitives.WriteUInt64LittleEndian(boot.Slice(72, 8), volumeSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(boot.Slice(80, 4), fatOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(boot.Slice(84, 4), fatLength);
        BinaryPrimitives.WriteUInt32LittleEndian(boot.Slice(88, 4), clusterHeapOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(boot.Slice(92, 4), clusterCount);
        BinaryPrimitives.WriteUInt32LittleEndian(boot.Slice(96, 4), rootCluster);
        BinaryPrimitives.WriteUInt16LittleEndian(boot.Slice(104, 2), 0x0100);
        boot[108] = (byte)(bytesPerSector switch
        {
            512 => 9,
            1024 => 10,
            2048 => 11,
            4096 => 12,
            _ => throw new ArgumentOutOfRangeException(nameof(bytesPerSector))
        });
        boot[109] = 0;
        boot[110] = 1;
        boot[111] = 0x80;
        boot[112] = 0xFF;
        boot[510] = 0x55;
        boot[511] = 0xAA;

        var directoryOffset = clusterHeapOffset * bytesPerSector;
        WriteEntrySet(image.AsSpan(directoryOffset, 96),
            "before.png", 3, 48, isActive: true, noFatChain: true);
        WriteEntrySet(image.AsSpan(directoryOffset + 96, 96),
            "deleted.exe", 4, 64, isActive: false, noFatChain: true);
        WriteEntrySet(image.AsSpan(directoryOffset + 192, 96),
            "fragmented.bin", 5, 80, isActive: true, noFatChain: false);
        image[directoryOffset + 288] = 0;

        Encoding.ASCII.GetBytes("ACTIVE EXFAT DATA")
            .CopyTo(image, (clusterHeapOffset + 1) * bytesPerSector);
        Encoding.ASCII.GetBytes("DELETED EXFAT DATA")
            .CopyTo(image, (clusterHeapOffset + 2) * bytesPerSector);
        return image;
    }

    private static void WriteEntrySet(
        Span<byte> entrySet,
        string name,
        uint firstCluster,
        ulong size,
        bool isActive,
        bool noFatChain)
    {
        entrySet.Clear();
        entrySet[0] = isActive ? (byte)0x85 : (byte)0x05;
        entrySet[1] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(entrySet.Slice(4, 2), 0x20);

        var stream = entrySet.Slice(32, 32);
        stream[0] = isActive ? (byte)0xC0 : (byte)0x40;
        stream[1] = noFatChain ? (byte)0x03 : (byte)0x01;
        stream[3] = (byte)name.Length;
        BinaryPrimitives.WriteUInt64LittleEndian(stream.Slice(8, 8), size);
        BinaryPrimitives.WriteUInt32LittleEndian(stream.Slice(20, 4), firstCluster);
        BinaryPrimitives.WriteUInt64LittleEndian(stream.Slice(24, 8), size);

        var fileName = entrySet.Slice(64, 32);
        fileName[0] = isActive ? (byte)0xC1 : (byte)0x41;
        Encoding.Unicode.GetBytes(name).CopyTo(fileName.Slice(2));

        BinaryPrimitives.WriteUInt16LittleEndian(
            entrySet.Slice(2, 2), CalculateEntrySetChecksum(entrySet, isActive));
    }

    private static ushort CalculateEntrySetChecksum(ReadOnlySpan<byte> entrySet, bool isActive)
    {
        ushort checksum = 0;
        for (var index = 0; index < entrySet.Length; index++)
        {
            if (index is 2 or 3)
                continue;
            var value = entrySet[index];
            if (!isActive && index % 32 == 0)
                value |= 0x80;
            checksum = (ushort)(((checksum & 1) != 0 ? 0x8000 : 0) +
                                (checksum >> 1) + value);
        }
        return checksum;
    }
}
