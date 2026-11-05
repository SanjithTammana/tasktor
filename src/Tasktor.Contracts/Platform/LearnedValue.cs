using Tasktor.Contracts.Snapshot;

namespace Tasktor.Contracts.Platform;

/// <summary>
/// An adaptive decision's value and its explanation (B D-B11): the value, where it came from, how many runs back it,
/// and a basis key + number so a toast or the Learned view can say why without re-deriving it.
/// </summary>
public sealed record LearnedValue(double Value, ValueSource Source, int Runs, string BasisKey, double BasisValue);
