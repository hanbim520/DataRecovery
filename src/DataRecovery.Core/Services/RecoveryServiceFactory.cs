using DataRecovery.Core.FileSystems;

namespace DataRecovery.Core.Services;

public static class RecoveryServiceFactory
{
    public static IStorageSourceService CreateStorageSourceService() => new StorageSourceService();
    public static IRecoveryScanner CreateScanner() => new RecoveryScanner(new FileSystemDetector());
    public static IFileShredder CreateFileShredder() => new FileShredder();
}
