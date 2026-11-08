using System.Globalization;

namespace Tasktor.Contracts.Platform;

/// <summary>Process identity: PID + creation time (B D-B11). Every lookup and action re-checks it (PID reuse).</summary>
public readonly record struct ProcKey(uint Pid, long CreatedTicks)
{
    public override string ToString() => Pid.ToString(CultureInfo.InvariantCulture) + "-" + CreatedTicks.ToString(CultureInfo.InvariantCulture);

    public static bool TryParse(string? text, out ProcKey key)
    {
        key = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var dash = text.IndexOf('-', StringComparison.Ordinal);
        if (dash <= 0)
        {
            return false;
        }

        if (uint.TryParse(text.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            && long.TryParse(text.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var created))
        {
            key = new ProcKey(pid, created);
            return true;
        }

        return false;
    }
}

/// <summary>A series key: metric id + instance (A D-A5: <c>disk.active</c>, <c>C:</c>).</summary>
public readonly record struct MetricKey(string Id, string Instance = "")
{
    public override string ToString() => Instance.Length == 0 ? Id : Id + "[" + Instance + "]";
}

/// <summary>
/// Every metric value at one timestamp (A D-A5 Series). Sources fill it off-loop; the loop reads it.
/// Reused between samples: <see cref="Clear"/> keeps capacity, so steady-state sampling does not allocate.
/// </summary>
public sealed class MetricFrame
{
    private readonly Dictionary<MetricKey, double> _values = [];

    public DateTimeOffset At { get; set; }

    public int Count => _values.Count;

    public IEnumerable<KeyValuePair<MetricKey, double>> Values => _values;

    public void Set(MetricKey key, double value) => _values[key] = value;

    public void Set(string id, double value) => _values[new MetricKey(id)] = value;

    public bool TryGet(MetricKey key, out double value) => _values.TryGetValue(key, out value);

    public bool TryGet(string id, out double value) => _values.TryGetValue(new MetricKey(id), out value);

    public void Clear() => _values.Clear();

    public void CopyTo(MetricFrame other)
    {
        ArgumentNullException.ThrowIfNull(other);
        other.Clear();
        other.At = At;
        foreach (var kv in _values)
        {
            other._values[kv.Key] = kv.Value;
        }
    }
}

/// <summary>Which process columns a table read needs (B A-1: names-only scans open no handles).</summary>
[Flags]
public enum ColumnSet
{
    None = 0,

    /// <summary>Exe name, PID, parent PID (Toolhelp; no handle).</summary>
    Names = 1,

    /// <summary>Creation time, CPU time, memory (needs a handle).</summary>
    Usage = 2,

    /// <summary>Exe path, user, integrity.</summary>
    Identity = 4,

    /// <summary>I/O counters.</summary>
    Io = 8,

    /// <summary>GPU per process.</summary>
    Gpu = 16,

    All = Names | Usage | Identity | Io | Gpu,
}
