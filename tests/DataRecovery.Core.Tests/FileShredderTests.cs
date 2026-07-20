using System.ComponentModel;
using System.Runtime.InteropServices;
using DataRecovery.Core.Models;
using DataRecovery.Core.Services;

namespace DataRecovery.Core.Tests;

public sealed class FileShredderTests
{
    [Fact]
    public async Task ShredsFileWithThreePassesAndReportsVerifiedDeletion()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.CreateFile("secret.bin", 2 * 1024 * 1024 + 137);
        var updates = new List<FileShredProgress>();
        var progress = new InlineProgress<FileShredProgress>(updates.Add);
        var shredder = new FileShredder();

        var result = await shredder.ShredAsync(path, FileShredMode.ThreePass, progress);

        Assert.Equal(Path.GetFullPath(path), result.OriginalPath);
        Assert.Equal(3, result.PassesCompleted);
        Assert.Equal((2L * 1024 * 1024 + 137) * 3, result.BytesOverwritten);
        Assert.True(result.Verified);
        Assert.True(result.Deleted);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporary.Path));
        Assert.Contains(updates, update =>
            update.Stage == FileShredStage.Overwriting && update.PassNumber == 3);
        Assert.Contains(updates, update => update.Stage == FileShredStage.Verifying);
        Assert.Equal(FileShredStage.Completed, updates[^1].Stage);
        Assert.Equal(100d, updates[^1].Percentage);
        Assert.Equal(
            updates.Select(update => update.Percentage).OrderBy(value => value),
            updates.Select(update => update.Percentage));
    }

    [Fact]
    public async Task ShredsZeroLengthFile()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.CreateFile("empty.txt", 0);
        var shredder = RecoveryServiceFactory.CreateFileShredder();

        var result = await shredder.ShredAsync(path, FileShredMode.OnePass);

        Assert.Equal(0, result.BytesOverwritten);
        Assert.True(result.Verified);
        Assert.True(result.Deleted);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task FailsWithoutDeletingWhenFileIsExclusivelyOccupied()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.CreateFile("occupied.dat", 4096);
        var original = await File.ReadAllBytesAsync(path);
        var shredder = new FileShredder();

        await using (var heldOpen = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(
                () => shredder.ShredAsync(path, FileShredMode.OnePass));
        }

        Assert.True(File.Exists(path));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task CancellationDuringOverwriteKeepsOriginalPathAndDoesNotReportSuccess()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.CreateFile("cancel.bin", 4 * 1024 * 1024);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<FileShredProgress>(update =>
        {
            if (update.Stage == FileShredStage.Overwriting && update.BytesProcessed > 0)
                cancellation.Cancel();
        });
        var shredder = new FileShredder();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            shredder.ShredAsync(
                path,
                FileShredMode.SevenPass,
                progress,
                cancellation.Token));

        Assert.True(File.Exists(path));
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(temporary.Path),
            entry => System.IO.Path.GetFileName(entry).StartsWith(".datarecovery-shred-"));
    }

    [Fact]
    public async Task RejectsDirectoryWithoutChangingIt()
    {
        using var temporary = new TemporaryDirectory();
        var shredder = new FileShredder();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            shredder.ShredAsync(temporary.Path));

        Assert.True(Directory.Exists(temporary.Path));
    }

    [Fact]
    public void PreflightDoesNotModifySelectedFileOrLeaveProbeFiles()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.CreateFile("preflight.bin", 32_777);
        var original = File.ReadAllBytes(path);
        var shredder = new FileShredder();

        var result = shredder.Preflight(path);

        Assert.Equal(Path.GetFullPath(path), result.FullPath);
        Assert.Equal(original.Length, result.Length);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal([path], Directory.EnumerateFiles(temporary.Path));
    }

    [Fact]
    public void RejectsWindowsHardLinksWithoutChangingSharedContent()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temporary = new TemporaryDirectory();
        var path = temporary.CreateFile("linked.bin", 8192);
        var link = System.IO.Path.Combine(temporary.Path, "other-link.bin");
        if (!CreateHardLink(link, path, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var original = File.ReadAllBytes(path);
        var shredder = new FileShredder();

        Assert.Throws<NotSupportedException>(() => shredder.Preflight(path));

        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(original, File.ReadAllBytes(link));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"datarecovery-shred-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateFile(string name, int length)
        {
            var path = System.IO.Path.Combine(Path, name);
            var bytes = new byte[length];
            Random.Shared.NextBytes(bytes);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);
}
