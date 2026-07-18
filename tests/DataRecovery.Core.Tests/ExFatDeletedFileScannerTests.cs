using System.Buffers.Binary;
using System.Text;
using DataRecovery.Core.FileSystems;
using DataRecovery.Core.Models;
using DataRecovery.Core.Services;

namespace DataRecovery.Core.Tests;

public sealed class ExFatDeletedFileScannerTests
{
    [Fact]
    public async Task RestoresDeletedContiguousEntryAndSuppressesEmbeddedSignatures()
    {
        var path = Path.Combine(Path.GetTempPath(), $"exfat-deleted-{Guid.NewGuid():N}.img");
        try
        {
            var image = CreateImage("deleted.exe", 256);
            await File.WriteAllBytesAsync(path, image);
            var scanner = new RecoveryScanner(new FileSystemDetector());

            var result = await scanner.ScanAsync(path, ScanMode.AllFiles);
            var deletedOnly = await scanner.ScanAsync(path, ScanMode.DeletedFiles);
            var lostOnly = await scanner.ScanAsync(path, ScanMode.LostFiles);

            Assert.Equal(FileSystemKind.ExFat, result.FileSystem.Kind);
            var file = Assert.Single(result.Files);
            Assert.Equal("deleted.exe", file.Name);
            Assert.Equal("程序", file.Category);
            Assert.Equal(256, file.Size);
            Assert.Equal(12_800, file.Offset);
            Assert.Equal(RecoveryState.Excellent, file.State);
            Assert.Equal("exFAT 删除目录项", file.Signature);
            Assert.Single(deletedOnly.Files);
            Assert.Empty(lostOnly.Files);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] CreateImage(string fileName, int fileSize)
    {
        const int bytesPerSector = 512;
        const int clusterHeapSector = 24;
        const int rootCluster = 2;
        const int fileCluster = 3;
        var image = new byte[64 * bytesPerSector];

        Encoding.ASCII.GetBytes("EXFAT   ").CopyTo(image, 3);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(72, 8), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(80, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(84, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(88, 4), clusterHeapSector);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(92, 4), 40);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(96, 4), rootCluster);
        image[108] = 9;
        image[109] = 0;
        image[110] = 1;
        image[510] = 0x55;
        image[511] = 0xAA;

        var directoryOffset = clusterHeapSector * bytesPerSector;
        image[directoryOffset] = 0x05;
        image[directoryOffset + 1] = 2;

        var streamOffset = directoryOffset + 32;
        image[streamOffset] = 0x40;
        image[streamOffset + 1] = 0x03;
        image[streamOffset + 3] = (byte)fileName.Length;
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(streamOffset + 8, 8), (ulong)fileSize);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(streamOffset + 20, 4), fileCluster);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(streamOffset + 24, 8), (ulong)fileSize);

        var nameOffset = directoryOffset + 64;
        image[nameOffset] = 0x41;
        Encoding.Unicode.GetBytes(fileName).CopyTo(image, nameOffset + 2);

        var dataOffset = (clusterHeapSector + fileCluster - 2) * bytesPerSector;
        Encoding.ASCII.GetBytes("MZ").CopyTo(image, dataOffset);
        byte[] embeddedJpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x11, 0x22, 0xFF, 0xD9];
        embeddedJpeg.CopyTo(image, dataOffset + 64);
        return image;
    }
}
