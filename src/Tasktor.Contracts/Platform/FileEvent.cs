namespace Tasktor.Contracts.Platform;

/// <summary>
/// U2 <c>FolderWatchSource</c> (I12): a new file that has settled (size stable, not locked, not a partial download) in a
/// folder the user chose. Names only, never contents (D-S5); the path is personal data.
/// </summary>
public sealed record FileEvent(string Folder, Sensitive<string> Path, DateTimeOffset At);
