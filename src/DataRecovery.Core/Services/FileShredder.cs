using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using DataRecovery.Core.Models;

namespace DataRecovery.Core.Services;

public interface IFileShredder
{
    FileShredPreflight Preflight(string filePath);

    Task<FileShredResult> ShredAsync(
        string filePath,
        FileShredMode mode = FileShredMode.OnePass,
        IProgress<FileShredProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Overwrites and deletes one ordinary file. This makes recovery from magnetic
/// media substantially harder, but cannot guarantee erasure on SSDs, flash media,
/// copy-on-write file systems, snapshots, backups, or remapped storage sectors.
/// </summary>
public sealed class FileShredder : IFileShredder
{
    private const int BufferSize = 1024 * 1024;
    private const int DeletionVerificationAttempts = 4;
    private const int DeletionVerificationDelayMilliseconds = 100;
    private const uint ShcneDelete = 0x00000004;
    private const uint ShcneUpdateDirectory = 0x00001000;
    private const uint ShcnfPathW = 0x0005;

    public FileShredPreflight Preflight(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        ValidateOrdinaryFile(fullPath);
        ValidateParentChain(fullPath);
        var driveType = ValidateStorageLocation(fullPath);
        ValidateParentDirectoryMutation(fullPath);

        using var stream = OpenExclusive(fullPath);
        ValidateOpenFile(stream.SafeFileHandle);
        return new FileShredPreflight(fullPath, stream.Length, driveType);
    }

    public async Task<FileShredResult> ShredAsync(
        string filePath,
        FileShredMode mode = FileShredMode.OnePass,
        IProgress<FileShredProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ValidateMode(mode);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(filePath);
        progress?.Report(new FileShredProgress(
            FileShredStage.Validating, 0, (int)mode, 0, 0));
        _ = Preflight(fullPath);

        var patterns = GetPatterns(mode);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long fileLength;
        byte[] expectedHash;
        FileIdentity originalIdentity;

        try
        {
            await using (var stream = OpenExclusive(fullPath))
            {
                if (!stream.CanSeek)
                    throw new IOException("Only seekable ordinary files can be shredded.");

                originalIdentity = ValidateOpenFile(stream.SafeFileHandle);

                fileLength = stream.Length;
                var totalOverwriteBytes = CheckedMultiply(fileLength, patterns.Length);
                var totalProgressBytes = CheckedMultiply(fileLength, patterns.Length + 1);
                expectedHash = Array.Empty<byte>();

                for (var passIndex = 0; passIndex < patterns.Length; passIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    stream.Position = 0;
                    using var passHash = passIndex == patterns.Length - 1
                        ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
                        : null;

                    long writtenThisPass = 0;
                    progress?.Report(new FileShredProgress(
                        FileShredStage.Overwriting,
                        passIndex + 1,
                        patterns.Length,
                        CheckedMultiply(fileLength, passIndex),
                        totalProgressBytes));
                    while (writtenThisPass < fileLength)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var count = (int)Math.Min(buffer.Length, fileLength - writtenThisPass);
                        Fill(buffer.AsSpan(0, count), patterns[passIndex]);
                        await stream.WriteAsync(buffer.AsMemory(0, count), cancellationToken)
                            .ConfigureAwait(false);
                        passHash?.AppendData(buffer, 0, count);
                        writtenThisPass += count;

                        progress?.Report(new FileShredProgress(
                            FileShredStage.Overwriting,
                            passIndex + 1,
                            patterns.Length,
                            CheckedAdd(CheckedMultiply(fileLength, passIndex), writtenThisPass),
                            totalProgressBytes));
                    }

                    // Flush every completed pass so the next pass is not merely replacing
                    // dirty pages in the operating-system cache.
                    stream.Flush(flushToDisk: true);

                    if (passHash is not null)
                        expectedHash = passHash.GetHashAndReset();
                }

                cancellationToken.ThrowIfCancellationRequested();
                stream.Position = 0;
                var actualHash = await ComputeHashAsync(
                    stream,
                    buffer,
                    fileLength,
                    patterns.Length,
                    totalOverwriteBytes,
                    totalProgressBytes,
                    progress,
                    cancellationToken)
                    .ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
                    throw new IOException("The final overwrite pass could not be verified.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }

        cancellationToken.ThrowIfCancellationRequested();
        ValidateSameFileBeforeCleanup(fullPath, originalIdentity);

        // Once cleanup starts, finish it without observing cancellation. Stopping between
        // rename and delete would leave a difficult-to-find randomly named file behind.
        progress?.Report(new FileShredProgress(
            FileShredStage.Renaming, patterns.Length, patterns.Length, fileLength, fileLength));
        var renamedPath = CreateRandomSiblingPath(fullPath);
        File.Move(fullPath, renamedPath);

        try
        {
            progress?.Report(new FileShredProgress(
                FileShredStage.Truncating, patterns.Length, patterns.Length, fileLength, fileLength));
            await using (var truncateStream = new FileStream(renamedPath, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 1,
                Options = FileOptions.Asynchronous
            }))
            {
                truncateStream.SetLength(0);
                truncateStream.Flush(flushToDisk: true);
            }

            progress?.Report(new FileShredProgress(
                FileShredStage.Deleting, patterns.Length, patterns.Length, fileLength, fileLength));
            File.Delete(renamedPath);
        }
        catch
        {
            TryRestoreOriginalName(renamedPath, fullPath);
            throw;
        }

        await VerifyDeletionRemainsStableAsync(fullPath, renamedPath).ConfigureAwait(false);
        NotifyWindowsShellDeletion(fullPath);

        var bytesOverwritten = CheckedMultiply(fileLength, patterns.Length);
        progress?.Report(new FileShredProgress(
            FileShredStage.Completed,
            patterns.Length,
            patterns.Length,
            bytesOverwritten,
            bytesOverwritten));

        return new FileShredResult(fullPath, patterns.Length, bytesOverwritten, true, true);
    }

    private static async Task<byte[]> ComputeHashAsync(
        FileStream stream,
        byte[] buffer,
        long fileLength,
        int totalPasses,
        long verificationOffset,
        long totalProgressBytes,
        IProgress<FileShredProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long readTotal = 0;
        progress?.Report(new FileShredProgress(
            FileShredStage.Verifying,
            totalPasses,
            totalPasses,
            verificationOffset,
            totalProgressBytes));
        while (readTotal < fileLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = (int)Math.Min(buffer.Length, fileLength - readTotal);
            var read = await stream.ReadAsync(buffer.AsMemory(0, requested), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("The file ended during overwrite verification.");

            hash.AppendData(buffer, 0, read);
            readTotal += read;
            progress?.Report(new FileShredProgress(
                FileShredStage.Verifying,
                totalPasses,
                totalPasses,
                CheckedAdd(verificationOffset, readTotal),
                totalProgressBytes));
        }

        return hash.GetHashAndReset();
    }

    private static FileStream OpenExclusive(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Open,
        Access = FileAccess.ReadWrite,
        Share = FileShare.None,
        BufferSize = BufferSize,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough
    });

    private static DriveType ValidateStorageLocation(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root))
            throw new IOException("The file is not located on a recognized local volume.");

        DriveType driveType;
        try { driveType = new DriveInfo(root).DriveType; }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            throw new IOException("The storage type could not be determined safely.", exception);
        }

        if (driveType is DriveType.Network or DriveType.CDRom or DriveType.Ram)
            throw new NotSupportedException(
                $"Files on {driveType} storage are not supported for file-level shredding.");
        return driveType;
    }

    private static void ValidateParentChain(string path)
    {
        var directory = Directory.GetParent(path)
            ?? throw new IOException("The file has no parent directory.");
        for (var current = directory; current is not null; current = current.Parent)
        {
            var attributes = File.GetAttributes(current.FullName);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new NotSupportedException(
                    "Files below symbolic-link, junction, or reparse-point directories cannot be shredded safely.");
        }
    }

    private static void ValidateParentDirectoryMutation(string path)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new IOException("The file has no parent directory.");
        var first = Path.Combine(
            directory, $".datarecovery-preflight-{RandomNumberGenerator.GetHexString(20)}.tmp");
        var second = Path.Combine(
            directory, $".datarecovery-preflight-{RandomNumberGenerator.GetHexString(20)}.tmp");
        try
        {
            using (var probe = new FileStream(first, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 1,
                Options = FileOptions.WriteThrough
            }))
            {
                probe.Flush(flushToDisk: true);
            }
            File.Move(first, second);
            File.Delete(second);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDeleteProbe(first);
            TryDeleteProbe(second);
            throw new IOException(
                "The parent directory does not permit the rename and delete steps required for shredding.",
                exception);
        }
    }

    private static void TryDeleteProbe(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static FileIdentity ValidateOpenFile(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
            return FileIdentity.Unsupported;
        if (!GetFileInformationByHandle(handle, out var information))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "The file identity could not be verified.");
        if (information.NumberOfLinks != 1)
            throw new NotSupportedException(
                "Files with multiple hard links cannot be shredded safely. Remove extra links first.");
        return new FileIdentity(
            true,
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
    }

    private static void ValidateSameFileBeforeCleanup(string path, FileIdentity expected)
    {
        ValidateOrdinaryFile(path);
        ValidateParentChain(path);
        if (!expected.Supported) return;
        using var stream = OpenExclusive(path);
        var actual = ValidateOpenFile(stream.SafeFileHandle);
        if (actual != expected)
            throw new IOException(
                "The selected path changed while it was being shredded; cleanup was stopped.");
    }

    private static void ValidateOrdinaryFile(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            throw new FileNotFoundException("The file to shred does not exist.", path);
        }
        catch (DirectoryNotFoundException)
        {
            throw new FileNotFoundException("The file to shred does not exist.", path);
        }

        if ((attributes & FileAttributes.Directory) != 0)
            throw new ArgumentException("Directories cannot be shredded by this operation.", nameof(path));
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Symbolic links and reparse points cannot be shredded.", nameof(path));
        if ((attributes & FileAttributes.Device) != 0)
            throw new ArgumentException("Device files cannot be shredded.", nameof(path));
        if ((attributes & FileAttributes.SparseFile) != 0)
            throw new NotSupportedException(
                "Sparse files are not supported because expanding holes could exhaust the volume.");
        if ((attributes & FileAttributes.Compressed) != 0)
            throw new NotSupportedException(
                "Compressed files cannot be overwritten in place reliably.");
        if ((attributes & FileAttributes.Encrypted) != 0)
            throw new NotSupportedException(
                "File-system encrypted files require cryptographic or volume-level erasure.");
    }

    private static void ValidateMode(FileShredMode mode)
    {
        if (mode is not FileShredMode.OnePass
            and not FileShredMode.ThreePass
            and not FileShredMode.SevenPass)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Supported pass counts are 1, 3, and 7.");
        }
    }

    private static OverwritePattern[] GetPatterns(FileShredMode mode) => mode switch
    {
        FileShredMode.OnePass => [OverwritePattern.Random],
        FileShredMode.ThreePass =>
            [OverwritePattern.Random, OverwritePattern.Zero, OverwritePattern.One],
        FileShredMode.SevenPass =>
            [
                OverwritePattern.Random,
                OverwritePattern.Zero,
                OverwritePattern.One,
                OverwritePattern.Random,
                OverwritePattern.Zero,
                OverwritePattern.One,
                OverwritePattern.Random
            ],
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static void Fill(Span<byte> destination, OverwritePattern pattern)
    {
        switch (pattern)
        {
            case OverwritePattern.Random:
                RandomNumberGenerator.Fill(destination);
                break;
            case OverwritePattern.Zero:
                destination.Clear();
                break;
            case OverwritePattern.One:
                destination.Fill(0xFF);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(pattern));
        }
    }

    private static string CreateRandomSiblingPath(string originalPath)
    {
        var directory = Path.GetDirectoryName(originalPath)
            ?? throw new IOException("The file has no parent directory.");

        for (var attempt = 0; attempt < 100; attempt++)
        {
            var candidate = Path.Combine(
                directory,
                $".datarecovery-shred-{RandomNumberGenerator.GetHexString(24).ToLowerInvariant()}.tmp");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }

        throw new IOException("A unique temporary name could not be created for secure deletion.");
    }

    private static void TryRestoreOriginalName(string renamedPath, string originalPath)
    {
        try
        {
            if (File.Exists(renamedPath) && !File.Exists(originalPath))
                File.Move(renamedPath, originalPath);
        }
        catch
        {
            // Preserve the cleanup exception. If restoration also fails, the random path
            // remains in the same directory rather than being reported as deleted.
        }
    }

    private static bool PathExistsStrict(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static async Task VerifyDeletionRemainsStableAsync(
        string originalPath,
        string renamedPath)
    {
        // Some removable-media drivers acknowledge a namespace update before their
        // directory metadata has fully settled. Require several consecutive misses so
        // the operation cannot report success from one transient lookup.
        for (var attempt = 0; attempt < DeletionVerificationAttempts; attempt++)
        {
            if (PathExistsStrict(renamedPath) || PathExistsStrict(originalPath))
                throw new IOException("The file still exists after the delete operation.");

            if (attempt + 1 < DeletionVerificationAttempts)
            {
                await Task.Delay(DeletionVerificationDelayMilliseconds)
                    .ConfigureAwait(false);
            }
        }
    }

    private static void NotifyWindowsShellDeletion(string originalPath)
    {
        if (!OperatingSystem.IsWindows()) return;

        // File.Delete updates the file system, but Explorer can retain a stale row for
        // removable volumes. Notify both the deleted item and its containing directory.
        SHChangeNotify(ShcneDelete, ShcnfPathW, originalPath, IntPtr.Zero);
        var directory = Path.GetDirectoryName(originalPath);
        if (!string.IsNullOrWhiteSpace(directory))
            SHChangeNotify(ShcneUpdateDirectory, ShcnfPathW, directory, IntPtr.Zero);
    }

    private static long CheckedMultiply(long value, int multiplier)
    {
        try
        {
            return checked(value * multiplier);
        }
        catch (OverflowException exception)
        {
            throw new IOException("The selected pass count exceeds the supported progress range.", exception);
        }
    }

    private static long CheckedAdd(long left, long right)
    {
        try
        {
            return checked(left + right);
        }
        catch (OverflowException exception)
        {
            throw new IOException("The overwrite progress exceeds the supported range.", exception);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(
        uint eventId,
        uint flags,
        string item1,
        IntPtr item2);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private readonly record struct FileIdentity(
        bool Supported,
        uint VolumeSerialNumber,
        ulong FileIndex)
    {
        public static FileIdentity Unsupported => new(false, 0, 0);
    }

    private enum OverwritePattern
    {
        Random,
        Zero,
        One
    }
}
