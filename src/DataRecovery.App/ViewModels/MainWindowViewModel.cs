using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataRecovery.App.Collections;
using DataRecovery.Core.Models;
using DataRecovery.Core.Services;

namespace DataRecovery.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IStorageSourceService _sourceService;
    private readonly IRecoveryScanner _scanner;
    private readonly IFileShredder _fileShredder;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _shredCancellation;
    private readonly HashSet<string> _displayedFileKeys = [];

    public ObservableCollection<StorageSourceItemViewModel> Sources { get; } = [];
    public RangeObservableCollection<RecoveredFile> Files { get; } = [];
    public RangeObservableCollection<RecoveredFile> FilteredFiles { get; } = [];
    public ObservableCollection<ShredFileItemViewModel> ShredFiles { get; } = [];

    public Func<Task<string?>>? SelectImageRequested { get; set; }
    public Func<Task<string?>>? SelectFolderRequested { get; set; }
    public Func<Task<IReadOnlyList<string>>>? SelectShredFilesRequested { get; set; }

    [ObservableProperty] private RecoveredFile? selectedFile;
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string selectedCategory = "全部文件";
    [ObservableProperty] private string statusText = "请选择需要恢复数据的磁盘";
    [ObservableProperty] private string detectedFileSystem = "等待检测";
    [ObservableProperty] private string scanDetails = "支持 FAT12/16/32、NTFS、Ext2、Ext3";
    [ObservableProperty] private double scanProgress;
    [ObservableProperty] private ScanMode selectedScanMode = ScanMode.DeletedFiles;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool showSourcePage = true;
    [ObservableProperty] private bool showResultsPage;
    [ObservableProperty] private bool showRecoveryPage;
    [ObservableProperty] private bool showShredPage;
    [ObservableProperty] private string recoverySummary = "";
    [ObservableProperty] private int totalFoundCount;
    [ObservableProperty] private ShredFileItemViewModel? selectedShredFile;
    [ObservableProperty] private FileShredMode selectedShredMode = FileShredMode.OnePass;
    [ObservableProperty] private bool shredConfirmation;
    [ObservableProperty] private double shredProgress;
    [ObservableProperty] private string shredStatusText = "请选择需要永久销毁的文件";

    public string[] Categories { get; } = ["全部文件", "照片", "文档", "压缩包", "程序", "其他"];
    public bool CanStartScan => SelectedSourceCount > 0 && !IsBusy;
    public bool CanConfigureScan => !IsBusy;
    public int SelectedSourceCount => Sources.Count(source => source.IsSelected);
    public string SelectionSummary => $"已选择 {SelectedSourceCount} 个设备";
    public bool? AreAllSourcesSelected
    {
        get
        {
            var selectable = Sources.Where(source => source.IsReady).ToList();
            if (selectable.Count == 0 || selectable.All(source => !source.IsSelected)) return false;
            return selectable.All(source => source.IsSelected) ? true : null;
        }
        set
        {
            if (!value.HasValue) return;
            foreach (var source in Sources.Where(source => source.IsReady))
                source.IsSelected = value.Value;
        }
    }
    public bool HasFiles => FilteredFiles.Count > 0;
    public bool CanRecover => HasFiles && !IsBusy;
    public bool CanEditShredFiles => !IsBusy;
    public bool CanRemoveShredFile => SelectedShredFile is not null && !IsBusy;
    public bool CanStartShred =>
        ShredFiles.Any(file => !file.Completed) && ShredConfirmation && !IsBusy;
    public string ShredFileCountText =>
        $"已选择 {ShredFiles.Count(file => !file.Completed)} 个待处理文件";
    public string ShredProgressText => $"{ShredProgress:0}%";
    public string ShredModeTitle => SelectedShredMode switch
    {
        FileShredMode.OnePass => "快速粉碎 · 1 次覆盖",
        FileShredMode.ThreePass => "标准粉碎 · 3 次覆盖",
        _ => "增强粉碎 · 7 次覆盖"
    };
    public string ShredModeDescription => SelectedShredMode switch
    {
        FileShredMode.OnePass => "适合普通机械硬盘上的日常敏感文件，速度最快",
        FileShredMode.ThreePass => "随机、固定模式组合覆盖，并校验最后一遍",
        _ => "执行 7 次覆盖和最终校验，耗时及写入量显著增加"
    };
    public bool IsOnePassShredMode
    {
        get => SelectedShredMode == FileShredMode.OnePass;
        set { if (value) SelectedShredMode = FileShredMode.OnePass; }
    }
    public bool IsThreePassShredMode
    {
        get => SelectedShredMode == FileShredMode.ThreePass;
        set { if (value) SelectedShredMode = FileShredMode.ThreePass; }
    }
    public bool IsSevenPassShredMode
    {
        get => SelectedShredMode == FileShredMode.SevenPass;
        set { if (value) SelectedShredMode = FileShredMode.SevenPass; }
    }
    public int ResultCount => FilteredFiles.Count;
    public string ResultCountText => IsBusy
        ? $"已显示 {ResultCount} / 已发现 {TotalFoundCount}"
        : $"找到 {ResultCount} 个候选文件";
    public string ProgressText => $"{ScanProgress:0}%";
    public string ParallelismText => $"并行扫描：{RecoveryScanner.RecommendedWorkerCount} 个工作线程";
    public string ScanModeTitle => SelectedScanMode switch
    {
        ScanMode.DeletedFiles => "已删除文件（默认）",
        ScanMode.LostFiles => "格式化/丢失文件",
        _ => "全部类型"
    };
    public string ScanModeDescription => SelectedScanMode switch
    {
        ScanMode.DeletedFiles => "读取文件系统删除元数据，优先保留可用的原文件名、大小和路径信息",
        ScanMode.LostFiles => "不依赖格式化前的文件系统，按文件结构扫描仍残留的原始数据",
        _ => "先读取删除元数据，再执行原始扇区文件特征扫描"
    };
    public bool IsDeletedScanMode
    {
        get => SelectedScanMode == ScanMode.DeletedFiles;
        set { if (value) SelectedScanMode = ScanMode.DeletedFiles; }
    }
    public bool IsLostScanMode
    {
        get => SelectedScanMode == ScanMode.LostFiles;
        set { if (value) SelectedScanMode = ScanMode.LostFiles; }
    }
    public bool IsAllScanMode
    {
        get => SelectedScanMode == ScanMode.AllFiles;
        set { if (value) SelectedScanMode = ScanMode.AllFiles; }
    }

    public MainWindowViewModel()
        : this(
            RecoveryServiceFactory.CreateStorageSourceService(),
            RecoveryServiceFactory.CreateScanner(),
            RecoveryServiceFactory.CreateFileShredder()) { }

    public MainWindowViewModel(IStorageSourceService sourceService, IRecoveryScanner scanner)
        : this(sourceService, scanner, RecoveryServiceFactory.CreateFileShredder()) { }

    public MainWindowViewModel(
        IStorageSourceService sourceService,
        IRecoveryScanner scanner,
        IFileShredder fileShredder)
    {
        _sourceService = sourceService;
        _scanner = scanner;
        _fileShredder = fileShredder;
        ShredFiles.CollectionChanged += (_, _) => NotifyShredStateChanged();
        RefreshSources();
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartScan));
        StartScanCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanConfigureScan));
        OnPropertyChanged(nameof(CanRecover));
        OnPropertyChanged(nameof(ResultCountText));
        NotifyShredStateChanged();
    }
    partial void OnTotalFoundCountChanged(int value) => OnPropertyChanged(nameof(ResultCountText));
    partial void OnScanProgressChanged(double value) => OnPropertyChanged(nameof(ProgressText));
    partial void OnShredProgressChanged(double value) => OnPropertyChanged(nameof(ShredProgressText));
    partial void OnShredConfirmationChanged(bool value) =>
        OnPropertyChanged(nameof(CanStartShred));
    partial void OnSelectedShredFileChanged(ShredFileItemViewModel? value) =>
        OnPropertyChanged(nameof(CanRemoveShredFile));
    partial void OnSelectedShredModeChanged(FileShredMode value)
    {
        ShredConfirmation = false;
        OnPropertyChanged(nameof(ShredModeTitle));
        OnPropertyChanged(nameof(ShredModeDescription));
        OnPropertyChanged(nameof(IsOnePassShredMode));
        OnPropertyChanged(nameof(IsThreePassShredMode));
        OnPropertyChanged(nameof(IsSevenPassShredMode));
    }
    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedCategoryChanged(string value) => ApplyFilter();
    partial void OnSelectedScanModeChanged(ScanMode value)
    {
        OnPropertyChanged(nameof(ScanModeTitle));
        OnPropertyChanged(nameof(ScanModeDescription));
        OnPropertyChanged(nameof(IsDeletedScanMode));
        OnPropertyChanged(nameof(IsLostScanMode));
        OnPropertyChanged(nameof(IsAllScanMode));
        if (!IsBusy) StatusText = $"扫描类型已设置为：{ScanModeTitle}";
    }

    [RelayCommand]
    private void OpenShredPage()
    {
        if (IsBusy) return;
        ShowSourcePage = false;
        ShowResultsPage = false;
        ShowRecoveryPage = false;
        ShowShredPage = true;
        ShredStatusText = ShredFiles.Count == 0
            ? "请选择需要永久销毁的文件"
            : ShredFileCountText;
        StatusText = "文件粉碎不会扫描磁盘，只处理你明确选择的普通文件";
    }

    [RelayCommand]
    private void CloseShredPage()
    {
        if (IsBusy) return;
        ShowShredPage = false;
        ShowSourcePage = true;
        StatusText = "请选择需要恢复数据的磁盘";
    }

    [RelayCommand]
    private async Task ChooseShredFilesAsync()
    {
        if (IsBusy || SelectShredFilesRequested is null) return;
        var paths = await SelectShredFilesRequested();
        var existing = ShredFiles
            .Select(file => file.FullPath)
            .ToHashSet(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        var skipped = 0;
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (!existing.Add(fullPath) || !File.Exists(fullPath))
                {
                    skipped++;
                    continue;
                }
                ShredFiles.Add(new ShredFileItemViewModel(fullPath));
            }
            catch
            {
                skipped++;
            }
        }

        ShredConfirmation = false;
        ShredStatusText = ShredFileCountText +
                          (skipped > 0 ? $"，已跳过 {skipped} 个重复或无效路径" : "");
    }

    [RelayCommand]
    private void RemoveSelectedShredFile()
    {
        if (IsBusy || SelectedShredFile is null) return;
        ShredFiles.Remove(SelectedShredFile);
        SelectedShredFile = null;
        ShredConfirmation = false;
        ShredStatusText = ShredFiles.Count == 0
            ? "请选择需要永久销毁的文件"
            : ShredFileCountText;
    }

    [RelayCommand]
    private void ClearShredFiles()
    {
        if (IsBusy) return;
        ShredFiles.Clear();
        SelectedShredFile = null;
        ShredConfirmation = false;
        ShredProgress = 0;
        ShredStatusText = "请选择需要永久销毁的文件";
    }

    [RelayCommand]
    private async Task StartShredAsync()
    {
        var pending = ShredFiles.Where(file => !file.Completed).ToList();
        if (!CanStartShred || pending.Count == 0) return;
        var shredMode = SelectedShredMode;

        _shredCancellation = new CancellationTokenSource();
        IsBusy = true;
        ShredProgress = 0;
        var succeeded = 0;
        var failed = 0;
        var failures = new List<string>();
        var cancelled = false;
        foreach (var item in pending)
        {
            try
            {
                item.Status = "正在执行安全预检…";
                if (ConflictsWithRecoverySource(item.FullPath))
                    throw new InvalidOperationException(
                        "文件位于当前选中的恢复源上；请先取消选择该源，避免覆盖待恢复数据。");
                _ = _fileShredder.Preflight(item.FullPath);
            }
            catch (Exception ex)
            {
                item.Status = $"预检失败：{ex.Message}";
                ShredStatusText = $"未修改任何所选文件。{item.Name}：{ex.Message}";
                StatusText = ShredStatusText;
                IsBusy = false;
                _shredCancellation.Dispose();
                _shredCancellation = null;
                ShredConfirmation = false;
                return;
            }
        }

        try
        {
            for (var fileIndex = 0; fileIndex < pending.Count; fileIndex++)
            {
                var currentIndex = fileIndex;
                var item = pending[fileIndex];
                item.Status = "正在校验文件…";
                var acceptProgress = 1;
                var progress = new Progress<FileShredProgress>(update =>
                {
                    // Progress<T> posts through the UI synchronization context. A queued
                    // terminal callback can otherwise arrive after ShredAsync has returned
                    // and replace the authoritative deletion result with "粉碎完成".
                    if (Volatile.Read(ref acceptProgress) == 0 ||
                        update.Stage == FileShredStage.Completed)
                        return;

                    ShredProgress = (currentIndex + update.Percentage / 100d) /
                                    pending.Count * 100d;
                    item.Status = GetShredStageText(update);
                    ShredStatusText =
                        $"[{currentIndex + 1}/{pending.Count}] {item.Name} · {item.Status}";
                    StatusText = ShredStatusText;
                });

                try
                {
                    var result = await _fileShredder.ShredAsync(
                        item.FullPath, shredMode, progress,
                        _shredCancellation.Token);
                    Volatile.Write(ref acceptProgress, 0);
                    if (!result.Verified || !result.Deleted)
                        throw new IOException("覆盖校验或删除步骤未完成。");
                    item.Completed = true;
                    item.Status = "逻辑覆盖已校验，文件已删除（此处仅保留操作记录）";
                    succeeded++;
                }
                catch (OperationCanceledException)
                {
                    item.Status = "已取消，文件可能已被部分覆盖";
                    cancelled = true;
                    break;
                }
                catch (Exception ex)
                {
                    item.Status = $"失败：{ex.Message}";
                    failures.Add($"{item.Name}：{ex.Message}");
                    failed++;
                }
                finally
                {
                    Volatile.Write(ref acceptProgress, 0);
                }
            }
        }
        finally
        {
            IsBusy = false;
            _shredCancellation?.Dispose();
            _shredCancellation = null;
            ShredConfirmation = false;
        }

        ShredProgress = cancelled ? ShredProgress : 100;
        ShredStatusText = cancelled
            ? $"粉碎已取消；已完成 {succeeded} 个，正在处理的文件可能已被部分覆盖"
            : $"粉碎完成：成功 {succeeded} 个，失败 {failed} 个；不代表介质上的所有物理副本均已擦除";
        if (failures.Count > 0)
            ShredStatusText += $"。首个错误：{failures[0]}";
        StatusText = ShredStatusText;
        NotifyShredStateChanged();
    }

    [RelayCommand]
    private void CancelShred() => _shredCancellation?.Cancel();

    [RelayCommand]
    private void RefreshSources()
    {
        var previous = Sources
            .Where(source => source.IsSelected)
            .Select(source => source.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Sources.Clear();
        foreach (var source in _sourceService.GetSources())
        {
            var item = new StorageSourceItemViewModel(source)
            {
                IsSelected = previous.Contains(source.Id)
            };
            item.SelectionChanged += OnSourceSelectionChanged;
            Sources.Add(item);
        }
        NotifySourceSelectionChanged();
        StatusText = Sources.Count == 0 ? "未发现已挂载的存储设备，可选择磁盘镜像" : "已刷新存储设备";
    }

    [RelayCommand]
    private async Task ChooseImageAsync()
    {
        if (SelectImageRequested is null) return;
        var path = await SelectImageRequested();
        if (string.IsNullOrWhiteSpace(path)) return;
        var source = _sourceService.FromImage(path);
        var item = new StorageSourceItemViewModel(source) { IsSelected = true };
        item.SelectionChanged += OnSourceSelectionChanged;
        Sources.Insert(0, item);
        NotifySourceSelectionChanged();
    }

    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private async Task StartScanAsync()
    {
        var selectedSources = Sources
            .Where(source => source.IsSelected && source.IsReady)
            .Select(source => source.Source)
            .ToList();
        if (selectedSources.Count == 0) return;

        var scanMode = SelectedScanMode;
        _scanCancellation = new CancellationTokenSource();
        IsBusy = true;
        ShowSourcePage = false;
        ShowResultsPage = true;
        ShowRecoveryPage = false;
        ShowShredPage = false;
        Files.Clear();
        FilteredFiles.Clear();
        _displayedFileKeys.Clear();
        TotalFoundCount = 0;
        ScanProgress = 0;
        DetectedFileSystem = "正在识别…";
        StatusText = "正在以只读方式打开数据源…";
        try
        {
            var allResults = new List<RecoveredFile>();
            var detectedFileSystems = new List<string>();
            long completedBytes = 0;

            for (var sourceIndex = 0; sourceIndex < selectedSources.Count; sourceIndex++)
            {
                var currentSourceIndex = sourceIndex;
                var source = selectedSources[sourceIndex];
                var filesBefore = allResults.Count;
                var bytesBefore = completedBytes;
                long sourceBytesScanned = 0;
                var progress = new Progress<ScanProgress>(p =>
                {
                    sourceBytesScanned = Math.Max(sourceBytesScanned, p.BytesScanned);
                    ScanProgress = ((currentSourceIndex + p.Percent / 100d) / selectedSources.Count) * 100d;
                    StatusText = $"[{currentSourceIndex + 1}/{selectedSources.Count}] {source.DisplayName} · {p.Stage}";
                    TotalFoundCount = filesBefore + p.FilesFound;
                    ScanDetails = $"已扫描 {SizeFormatter.Format(bytesBefore + p.BytesScanned)} · " +
                                  $"找到 {TotalFoundCount} 个候选文件";
                    if (p.NewFiles is { Count: > 0 })
                    {
                        var files = p.NewFiles.Select(file => AttachSource(file, source)).ToArray();
                        AppendLiveFiles(files);
                    }
                });

                var result = await _scanner.ScanAsync(
                    source.Path, scanMode, progress, _scanCancellation.Token);
                detectedFileSystems.Add(result.FileSystem.Label);
                DetectedFileSystem = selectedSources.Count == 1
                    ? result.FileSystem.Label
                    : $"已检测 {sourceIndex + 1}/{selectedSources.Count}";
                allResults.AddRange(result.Files.Select(file => AttachSource(file, source)));
                completedBytes = bytesBefore + Math.Max(sourceBytesScanned, 4096);
            }

            Files.ReplaceRange(allResults);
            _displayedFileKeys.Clear();
            foreach (var file in allResults) _displayedFileKeys.Add(GetDisplayedFileKey(file));
            ApplyFilter();
            TotalFoundCount = allResults.Count;
            ScanProgress = 100;
            DetectedFileSystem = selectedSources.Count == 1
                ? detectedFileSystems.FirstOrDefault() ?? "未知"
                : string.Join(" / ", detectedFileSystems.Distinct());
            ScanDetails = $"已扫描 {selectedSources.Count} 个设备 · {ScanModeTitle}";
            StatusText = $"扫描完成，共找到 {allResults.Count} 个候选文件";
        }
        catch (OperationCanceledException) { StatusText = "扫描已取消"; }
        catch (UnauthorizedAccessException)
        {
            StatusText = OperatingSystem.IsWindows()
                ? "无法读取原始磁盘，请以管理员身份运行或先制作磁盘镜像"
                : "无法读取设备，请使用 sudo 运行或授予磁盘读取权限";
        }
        catch (Exception ex) { StatusText = $"扫描失败：{ex.Message}"; }
        finally
        {
            IsBusy = false;
            _scanCancellation?.Dispose();
            _scanCancellation = null;
        }
    }

    [RelayCommand] private void CancelScan() => _scanCancellation?.Cancel();

    [RelayCommand]
    private void BackToSources()
    {
        if (IsBusy) _scanCancellation?.Cancel();
        ShowSourcePage = true;
        ShowResultsPage = false;
        ShowRecoveryPage = false;
        StatusText = "请选择需要恢复数据的磁盘";
    }

    [RelayCommand]
    private async Task RecoverSelectedAsync()
    {
        if (IsBusy) return;
        if (SelectFolderRequested is null) return;
        var selected = Files.Where(x => x.IsSelected).ToList();
        if (selected.Count == 0 && SelectedFile is not null) selected.Add(SelectedFile);
        if (selected.Count == 0) { StatusText = "请先勾选要恢复的文件"; return; }
        var folder = await SelectFolderRequested();
        if (string.IsNullOrWhiteSpace(folder)) return;
        IsBusy = true;
        var recovered = 0;
        var failed = 0;
        foreach (var file in selected)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(file.SourcePath)) throw new InvalidOperationException("结果缺少源设备信息。");
                await RecoveryScanner.RecoverAsync(file.SourcePath, file, folder);
                recovered++;
            }
            catch { failed++; }
        }
        IsBusy = false;
        RecoverySummary = $"已恢复 {recovered} 个文件到 {folder}" + (failed > 0 ? $"，{failed} 个文件因不完整而跳过" : "");
        StatusText = RecoverySummary;
        ShowResultsPage = false;
        ShowRecoveryPage = true;
    }

    [RelayCommand]
    private void ReturnToResults()
    {
        ShowResultsPage = true;
        ShowRecoveryPage = false;
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        var filtered = Files.Where(x =>
            (SelectedCategory == "全部文件" || x.Category == SelectedCategory) &&
            (query.Length == 0 || x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             x.LocationText.Contains(query, StringComparison.OrdinalIgnoreCase)));
        FilteredFiles.ReplaceRange(filtered);
        OnPropertyChanged(nameof(ResultCount));
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(CanRecover));
        OnPropertyChanged(nameof(ResultCountText));
    }

    private void AppendLiveFiles(IReadOnlyList<RecoveredFile> files)
    {
        var unique = files.Where(file => _displayedFileKeys.Add(GetDisplayedFileKey(file))).ToArray();
        if (unique.Length == 0) return;
        Files.AddRange(unique);
        var matching = unique.Where(MatchesCurrentFilter).ToArray();
        FilteredFiles.AddRange(matching);
        OnPropertyChanged(nameof(ResultCount));
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(CanRecover));
        OnPropertyChanged(nameof(ResultCountText));
    }

    private bool MatchesCurrentFilter(RecoveredFile file)
    {
        var query = SearchText.Trim();
        return (SelectedCategory == "全部文件" || file.Category == SelectedCategory) &&
               (query.Length == 0 || file.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                file.LocationText.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private void NotifyShredStateChanged()
    {
        OnPropertyChanged(nameof(CanEditShredFiles));
        OnPropertyChanged(nameof(CanRemoveShredFile));
        OnPropertyChanged(nameof(CanStartShred));
        OnPropertyChanged(nameof(ShredFileCountText));
    }

    private static string GetShredStageText(FileShredProgress progress) => progress.Stage switch
    {
        FileShredStage.Validating => "正在校验文件",
        FileShredStage.Overwriting =>
            $"正在执行第 {progress.PassNumber}/{progress.TotalPasses} 次覆盖",
        FileShredStage.Verifying => "正在读取并校验最终覆盖",
        FileShredStage.Renaming => "正在清除原文件名",
        FileShredStage.Truncating => "正在截断文件",
        FileShredStage.Deleting => "正在删除目录项",
        FileShredStage.Completed => "粉碎完成",
        _ => "正在处理"
    };

    private bool ConflictsWithRecoverySource(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        foreach (var item in Sources.Where(source => source.IsSelected))
        {
            var source = item.Source;
            if (source.Kind == DeviceKind.DiskImage)
            {
                if (Path.GetFullPath(source.Path).Equals(fullPath,
                        OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal))
                    return true;
                continue;
            }

            if (!OperatingSystem.IsWindows() ||
                !source.Path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
                continue;
            var drive = source.Path[4..].TrimEnd(':', '\\') + @":\";
            var root = Path.GetPathRoot(fullPath);
            if (drive.Equals(root, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void OnSourceSelectionChanged(object? sender, EventArgs e) => NotifySourceSelectionChanged();

    private void NotifySourceSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedSourceCount));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(AreAllSourcesSelected));
        OnPropertyChanged(nameof(CanStartScan));
        StartScanCommand.NotifyCanExecuteChanged();
        if (!IsBusy)
        {
            StatusText = SelectedSourceCount == 0
                ? "请至少勾选一个需要恢复数据的设备"
                : SelectionSummary;
        }
    }

    private static RecoveredFile AttachSource(RecoveredFile file, StorageSource source) => file with
    {
        SourcePath = source.Path,
        SourceDisplayName = source.DisplayName
    };

    private static string GetDisplayedFileKey(RecoveredFile file) =>
        $"{file.SourcePath}\u001F{file.Offset}";
}
