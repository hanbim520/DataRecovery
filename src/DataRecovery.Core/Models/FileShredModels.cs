namespace DataRecovery.Core.Models;

/// <summary>
/// Selects the number of overwrite passes performed before a file is deleted.
/// </summary>
public enum FileShredMode
{
    OnePass = 1,
    ThreePass = 3,
    SevenPass = 7
}

/// <summary>
/// Describes the current stage of a file-shredding operation.
/// </summary>
public enum FileShredStage
{
    Validating,
    Overwriting,
    Verifying,
    Renaming,
    Truncating,
    Deleting,
    Completed
}

/// <summary>
/// Progress reported while shredding a file. During overwriting,
/// <see cref="BytesProcessed"/> and <see cref="TotalBytes"/> cover all overwrite
/// passes plus the final verification read, keeping percentage monotonic.
/// </summary>
public sealed record FileShredProgress(
    FileShredStage Stage,
    int PassNumber,
    int TotalPasses,
    long BytesProcessed,
    long TotalBytes)
{
    public double Percentage => TotalBytes <= 0
        ? Stage == FileShredStage.Completed ? 100d : 0d
        : Math.Clamp(BytesProcessed * 100d / TotalBytes, 0d, 100d);
}

/// <summary>
/// Returned only after overwrite verification and deletion have both succeeded.
/// Failures and cancellation are reported as exceptions instead.
/// </summary>
public sealed record FileShredResult(
    string OriginalPath,
    int PassesCompleted,
    long BytesOverwritten,
    bool Verified,
    bool Deleted);

/// <summary>
/// A successful, non-destructive preflight result. It does not guarantee that
/// the path cannot change afterwards, so the shred operation repeats validation.
/// </summary>
public sealed record FileShredPreflight(
    string FullPath,
    long Length,
    DriveType DriveType);
