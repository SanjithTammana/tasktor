namespace Tasktor.Contracts.Platform;

/// <summary>
/// U2 <c>CallStateSource</c> (I3): which apps have the microphone in use right now, by exe name or package family only
/// (never audio, video or who is in the call, D-S5). <see cref="Available"/> is false when the OS route failed its probe,
/// so I3 falls back to the calendar.
/// </summary>
public sealed record CallState(IReadOnlyList<string> MicInUse, bool Available);
