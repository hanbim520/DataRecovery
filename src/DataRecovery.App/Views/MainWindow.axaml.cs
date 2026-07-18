using Avalonia.Controls;
using Avalonia.Platform.Storage;
using DataRecovery.App.ViewModels;

namespace DataRecovery.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WireViewModel();
    }

    private void WireViewModel()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.SelectImageRequested = async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择磁盘镜像",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("磁盘镜像") { Patterns = ["*.img", "*.dd", "*.raw", "*.bin", "*.iso"] },
                    FilePickerFileTypes.All
                ]
            });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        };
        vm.SelectFolderRequested = async () =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择恢复文件保存位置",
                AllowMultiple = false
            });
            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        };
    }
}
