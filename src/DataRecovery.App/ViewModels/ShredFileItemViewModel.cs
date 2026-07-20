using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using DataRecovery.Core.Models;

namespace DataRecovery.App.ViewModels;

public partial class ShredFileItemViewModel : ObservableObject
{
    public string FullPath { get; }
    public string Name { get; }
    public string DirectoryName { get; }
    public long Size { get; }
    public string SizeText => SizeFormatter.Format(Size);

    [ObservableProperty] private string status = "等待粉碎";
    [ObservableProperty] private bool completed;

    public ShredFileItemViewModel(string path)
    {
        var file = new FileInfo(path);
        FullPath = file.FullName;
        Name = file.Name;
        DirectoryName = file.DirectoryName ?? "";
        Size = file.Exists ? file.Length : 0;
    }
}
