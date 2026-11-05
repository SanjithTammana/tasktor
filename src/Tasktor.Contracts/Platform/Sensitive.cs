namespace Tasktor.Contracts.Platform;

/// <summary>
/// Personal data: window titles, command lines, paths under the profile, URLs, plugin output, typed text (U1 H6).
/// <see cref="ToString"/> prints <c>&lt;redacted&gt;</c>; the value is reachable only through <see cref="Reveal"/>,
/// so it cannot reach a log line or a string concatenation by accident.
/// </summary>
public readonly struct Sensitive<T> : IEquatable<Sensitive<T>>
{
    public const string Redacted = "<redacted>";

    private readonly T _value;

    public Sensitive(T value) => _value = value;

    /// <summary>The value. Call sites that use it are the audit points for G-redact.</summary>
    public T Reveal() => _value;

    public override string ToString() => Redacted;

    public bool Equals(Sensitive<T> other) => EqualityComparer<T>.Default.Equals(_value, other._value);

    public override bool Equals(object? obj) => obj is Sensitive<T> s && Equals(s);

    public override int GetHashCode() => _value is null ? 0 : EqualityComparer<T>.Default.GetHashCode(_value);

    public static bool operator ==(Sensitive<T> left, Sensitive<T> right) => left.Equals(right);

    public static bool operator !=(Sensitive<T> left, Sensitive<T> right) => !left.Equals(right);
}

public static class Sensitive
{
    public static Sensitive<T> Of<T>(T value) => new(value);
}
