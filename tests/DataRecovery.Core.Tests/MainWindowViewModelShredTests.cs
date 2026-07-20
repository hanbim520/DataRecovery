using DataRecovery.App.ViewModels;
using DataRecovery.Core.Models;
using DataRecovery.Core.Services;

namespace DataRecovery.Core.Tests;

public sealed class MainWindowViewModelShredTests
{
    [Fact]
    public async Task ConfirmedUiWorkflowShredsOnlyTheSelectedTemporaryFile()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.CreateFile("ui-secret.txt", "temporary test content");
        var viewModel = CreateViewModel(new FileShredder());
        viewModel.SelectShredFilesRequested = () =>
            Task.FromResult<IReadOnlyList<string>>([path]);

        await viewModel.ChooseShredFilesCommand.ExecuteAsync(null);
        viewModel.ShredConfirmation = true;

        Assert.True(viewModel.CanStartShred);
        await viewModel.StartShredCommand.ExecuteAsync(null);

        Assert.False(File.Exists(path));
        var item = Assert.Single(viewModel.ShredFiles);
        Assert.True(item.Completed);
        Assert.Equal("逻辑覆盖已校验，文件已删除（此处仅保留操作记录）", item.Status);
        Assert.Contains("成功 1 个", viewModel.ShredStatusText);
    }

    [Fact]
    public async Task BatchPreflightFailurePreventsEveryOverwrite()
    {
        using var temporary = new TemporaryDirectory();
        var first = temporary.CreateFile("first.txt", "first remains unchanged");
        var second = temporary.CreateFile("second.txt", "second remains unchanged");
        var firstBytes = File.ReadAllBytes(first);
        var secondBytes = File.ReadAllBytes(second);
        var shredder = new RejectingShredder(second);
        var viewModel = CreateViewModel(shredder);
        viewModel.SelectShredFilesRequested = () =>
            Task.FromResult<IReadOnlyList<string>>([first, second]);
        await viewModel.ChooseShredFilesCommand.ExecuteAsync(null);
        viewModel.ShredConfirmation = true;

        await viewModel.StartShredCommand.ExecuteAsync(null);

        Assert.Equal(0, shredder.ShredCalls);
        Assert.Equal(firstBytes, File.ReadAllBytes(first));
        Assert.Equal(secondBytes, File.ReadAllBytes(second));
        Assert.Contains("未修改任何所选文件", viewModel.ShredStatusText);
    }

    [Fact]
    public void ChangingOverwriteModeInvalidatesConfirmation()
    {
        var viewModel = CreateViewModel(new FileShredder());
        viewModel.ShredConfirmation = true;

        viewModel.SelectedShredMode = FileShredMode.SevenPass;

        Assert.False(viewModel.ShredConfirmation);
        Assert.False(viewModel.CanStartShred);
    }

    private static MainWindowViewModel CreateViewModel(IFileShredder shredder) => new(
        new EmptyStorageSourceService(),
        new EmptyRecoveryScanner(),
        shredder);

    private sealed class EmptyStorageSourceService : IStorageSourceService
    {
        public IReadOnlyList<StorageSource> GetSources() => [];

        public StorageSource FromImage(string path) =>
            new(path, Path.GetFileName(path), path, DeviceKind.DiskImage, 0, 0, "未知");
    }

    private sealed class EmptyRecoveryScanner : IRecoveryScanner
    {
        public Task<(DetectedFileSystem FileSystem, IReadOnlyList<RecoveredFile> Files)> ScanAsync(
            string path,
            ScanMode scanMode,
            IProgress<ScanProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<(DetectedFileSystem, IReadOnlyList<RecoveredFile>)>(
                (DetectedFileSystem.Unknown, []));
    }

    private sealed class RejectingShredder(string rejectedPath) : IFileShredder
    {
        public int ShredCalls { get; private set; }

        public FileShredPreflight Preflight(string filePath)
        {
            if (Path.GetFullPath(filePath) == Path.GetFullPath(rejectedPath))
                throw new IOException("synthetic preflight failure");
            return new FileShredPreflight(Path.GetFullPath(filePath), new FileInfo(filePath).Length,
                DriveType.Fixed);
        }

        public Task<FileShredResult> ShredAsync(
            string filePath,
            FileShredMode mode = FileShredMode.OnePass,
            IProgress<FileShredProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ShredCalls++;
            throw new InvalidOperationException("ShredAsync must not be reached after preflight failure.");
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"datarecovery-ui-shred-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateFile(string name, string content)
        {
            var path = System.IO.Path.Combine(Path, name);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
