using System.Collections.Concurrent;
using System.Text;
using DataRecovery.Core.FileSystems;
using DataRecovery.Core.Models;
using DataRecovery.Core.Services;

namespace DataRecovery.Core.Tests;

public sealed class RecoveryScannerTests
{
    [Fact]
    public async Task FindsAndRecoversCompletePdfWithoutOverwriting()
    {
        var root = Path.Combine(Path.GetTempPath(), $"datarecovery-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var image = Path.Combine(root, "sample.img");
            var bytes = new byte[8192];
            var pdf = Encoding.ASCII.GetBytes("%PDF-1.4\nrecovery-test\n%%EOF");
            pdf.CopyTo(bytes, 2048);
            await File.WriteAllBytesAsync(image, bytes);
            var scanner = new RecoveryScanner(new FileSystemDetector());
            var discoveries = new ConcurrentBag<RecoveredFile>();
            var progress = new InlineProgress<ScanProgress>(update =>
            {
                if (update.NewFiles is null) return;
                foreach (var file in update.NewFiles) discoveries.Add(file);
            });

            var result = await scanner.ScanAsync(image, ScanMode.LostFiles, progress);

            var file = Assert.Single(result.Files);
            Assert.Single(discoveries.Select(item => item.Offset).Distinct());
            Assert.Equal(2048, file.Offset);
            Assert.True(file.Size > 0);
            var output = Path.Combine(root, "output");
            await RecoveryScanner.RecoverAsync(image, file, output);
            await RecoveryScanner.RecoverAsync(image, file, output);
            Assert.Equal(2, Directory.GetFiles(output).Length);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task FindsBoundarySpanningFilesOnceAndKeepsOffsetOrder()
    {
        const int chunkSize = 8 * 1024 * 1024;
        var root = Path.Combine(Path.GetTempPath(), $"datarecovery-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var image = Path.Combine(root, "parallel-boundary.img");
            var bytes = new byte[chunkSize * 3];
            var boundaryPdf = Encoding.ASCII.GetBytes("%PDF-1.7\nboundary\n%%EOF");
            var laterPdf = Encoding.ASCII.GetBytes("%PDF-1.7\nlater\n%%EOF");
            boundaryPdf.CopyTo(bytes, chunkSize - 3);
            laterPdf.CopyTo(bytes, chunkSize * 2 + 1024);
            await File.WriteAllBytesAsync(image, bytes);
            var scanner = new RecoveryScanner(new FileSystemDetector());

            var result = await scanner.ScanAsync(image, ScanMode.LostFiles);

            Assert.Equal(2, result.Files.Count);
            Assert.Equal(chunkSize - 3, result.Files[0].Offset);
            Assert.Equal(chunkSize * 2 + 1024, result.Files[1].Offset);
            Assert.All(result.Files, file => Assert.True(file.Size > 0));
            Assert.Equal("恢复文件_0001.pdf", result.Files[0].Name);
            Assert.Equal("恢复文件_0002.pdf", result.Files[1].Name);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RecoversFragmentedAndSparseExtentsInLogicalOrder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"datarecovery-extents-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var image = Path.Combine(root, "fragmented.img");
            var bytes = new byte[4096];
            Encoding.ASCII.GetBytes("ABCD").CopyTo(bytes, 128);
            Encoding.ASCII.GetBytes("WXYZ").CopyTo(bytes, 2048);
            await File.WriteAllBytesAsync(image, bytes);
            var file = new RecoveredFile(
                "fragmented.bin", "已删除文件/测试", 12, "其他",
                RecoveryState.Good, 128, "测试区段")
            {
                RecoveryExtents =
                [
                    new RecoveryExtent(128, 4),
                    new RecoveryExtent(0, 4, IsSparse: true),
                    new RecoveryExtent(2048, 4)
                ]
            };

            var output = Path.Combine(root, "output");
            await RecoveryScanner.RecoverAsync(image, file, output);

            var recovered = await File.ReadAllBytesAsync(Path.Combine(output, "fragmented.bin"));
            byte[] expected =
                [.. Encoding.ASCII.GetBytes("ABCD"), 0, 0, 0, 0, .. Encoding.ASCII.GetBytes("WXYZ")];
            Assert.Equal(expected, recovered);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
