using DataRecovery.Core.Models;

namespace DataRecovery.Core.Services;

public interface IStorageSourceService
{
    IReadOnlyList<StorageSource> GetSources();
    StorageSource FromImage(string path);
}

public sealed class StorageSourceService : IStorageSourceService
{
    public IReadOnlyList<StorageSource> GetSources()
    {
        var sources = new List<StorageSource>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                var ready = drive.IsReady;
                var name = ready && !string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? $"{drive.VolumeLabel} ({drive.Name.TrimEnd(Path.DirectorySeparatorChar)})"
                    : drive.Name;
                var kind = drive.DriveType == DriveType.Removable
                    ? DeviceKind.RemovableDisk
                    : DeviceKind.LocalDisk;
                sources.Add(new StorageSource(
                    drive.Name, name, ToRawPath(drive), kind,
                    ready ? drive.TotalSize : 0,
                    ready ? drive.AvailableFreeSpace : 0,
                    ready ? drive.DriveFormat : "不可用", ready));
            }
            catch
            {
                sources.Add(new StorageSource(drive.Name, drive.Name, drive.Name,
                    DeviceKind.LocalDisk, 0, 0, "需要权限", false));
            }
        }
        return sources;
    }

    public StorageSource FromImage(string path)
    {
        var file = new FileInfo(path);
        return new StorageSource(path, file.Name, file.FullName, DeviceKind.DiskImage,
            file.Length, 0, "自动检测");
    }

    private static string ToRawPath(DriveInfo drive)
    {
        if (OperatingSystem.IsWindows())
            return $@"\\.\{drive.Name.TrimEnd('\\')}";
        if (OperatingSystem.IsLinux())
            return ResolveLinuxDevice(drive.Name) ?? drive.Name;
        if (OperatingSystem.IsMacOS())
            return ResolveMacDevice(drive.Name) ?? drive.Name;
        return drive.Name;
    }

    private static string? ResolveLinuxDevice(string mountPoint)
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/self/mountinfo"))
            {
                var fields = line.Split(' ');
                var separator = Array.IndexOf(fields, "-");
                if (separator < 0 || fields.Length <= separator + 2) continue;
                var mountedAt = UnescapeMountField(fields[4]);
                var source = UnescapeMountField(fields[separator + 2]);
                if (Path.GetFullPath(mountedAt) == Path.GetFullPath(mountPoint) && source.StartsWith("/dev/"))
                    return source;
            }
        }
        catch { }
        return null;
    }

    private static string? ResolveMacDevice(string mountPoint)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/df",
                ArgumentList = { "-P", mountPoint },
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            var output = process?.StandardOutput.ReadToEnd();
            process?.WaitForExit(2000);
            var last = output?.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            var source = last?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return source?.StartsWith("/dev/") == true ? source : null;
        }
        catch { return null; }
    }

    private static string UnescapeMountField(string value) => value
        .Replace("\\040", " ")
        .Replace("\\011", "\t")
        .Replace("\\012", "\n")
        .Replace("\\134", "\\");
}
