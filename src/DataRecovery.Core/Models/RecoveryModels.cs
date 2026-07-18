namespace DataRecovery.Core.Models;

public enum FileSystemKind { Unknown, Fat12, Fat16, Fat32, ExFat, Ntfs, Ext2, Ext3 }
public enum DeviceKind { LocalDisk, RemovableDisk, DiskImage }
public enum RecoveryState { Excellent, Good, Partial, Unknown }
public enum ScanMode { DeletedFiles, LostFiles, AllFiles }

public sealed record StorageSource(
    string Id,
    string DisplayName,
    string Path,
    DeviceKind Kind,
    long TotalBytes,
    long FreeBytes,
    string FileSystem,
    bool IsReady = true)
{
    public string CapacityText => SizeFormatter.Format(TotalBytes);
    public string FreeText => SizeFormatter.Format(FreeBytes);
    public string KindText => Kind switch
    {
        DeviceKind.RemovableDisk => "可移动设备",
        DeviceKind.DiskImage => "磁盘镜像",
        _ => "本地磁盘"
    };
}

public sealed record DetectedFileSystem(
    FileSystemKind Kind,
    int BytesPerSector,
    long TotalBytes,
    string Label,
    string Details)
{
    public static DetectedFileSystem Unknown => new(FileSystemKind.Unknown, 512, 0, "未知", "未检测到受支持的文件系统");
}

public sealed record RecoveredFile(
    string Name,
    string OriginalPath,
    long Size,
    string Category,
    RecoveryState State,
    long Offset,
    string Signature)
{
    public bool IsSelected { get; set; }
    public string SourcePath { get; init; } = "";
    public string SourceDisplayName { get; init; } = "";
    public IReadOnlyList<RecoveryExtent> RecoveryExtents { get; init; } = Array.Empty<RecoveryExtent>();
    public string SizeText => SizeFormatter.Format(Size);
    public string LocationText => string.IsNullOrWhiteSpace(SourceDisplayName)
        ? OriginalPath
        : $"{SourceDisplayName} · {OriginalPath}";
    public string StateText => State switch
    {
        RecoveryState.Excellent => "极佳",
        RecoveryState.Good => "良好",
        RecoveryState.Partial => "部分",
        _ => "未知"
    };
}

/// <summary>
/// 描述恢复文件在源设备上的一个逻辑数据区段。区段按集合顺序拼接；
/// 稀疏区段不读取源设备，而是在目标文件中生成相同长度的零数据。
/// </summary>
public sealed record RecoveryExtent(long SourceOffset, long Length, bool IsSparse = false);

public sealed record ScanProgress(
    double Percent,
    string Stage,
    long BytesScanned,
    int FilesFound,
    IReadOnlyList<RecoveredFile>? NewFiles = null);

public static class SizeFormatter
{
    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }
}
