using System;
using CommunityToolkit.Mvvm.ComponentModel;
using DataRecovery.Core.Models;

namespace DataRecovery.App.ViewModels;

public partial class StorageSourceItemViewModel(StorageSource source) : ObservableObject
{
    public StorageSource Source { get; } = source;

    [ObservableProperty] private bool isSelected;

    public event EventHandler? SelectionChanged;

    public string Id => Source.Id;
    public string DisplayName => Source.DisplayName;
    public DeviceKind Kind => Source.Kind;
    public string KindText => Source.KindText;
    public string FileSystem => Source.FileSystem;
    public string CapacityText => Source.CapacityText;
    public bool IsReady => Source.IsReady;

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke(this, EventArgs.Empty);
}
