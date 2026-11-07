using Tasktor.Contracts.Platform;

namespace Tasktor.Core.Features;

/// <summary>
/// Tasktor's own long tasks (B5: refits, the A5 folder scan) as progress rows the job card shows with a Cancel button.
/// Loop-only.
/// </summary>
public sealed class InternalTasks
{
    private readonly Dictionary<string, (ProgressRow Row, Action? Cancel)> _rows = new(StringComparer.Ordinal);

    public IEnumerable<ProgressRow> Rows => _rows.Values.Select(v => v.Row);

    public event Action? Changed;

    public void Set(ProgressRow row, Action? cancel = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        _rows[row.Id] = (row, cancel ?? (_rows.TryGetValue(row.Id, out var old) ? old.Cancel : null));
        Changed?.Invoke();
    }

    public void Remove(string id)
    {
        if (_rows.Remove(id))
        {
            Changed?.Invoke();
        }
    }

    public bool Cancel(string id)
    {
        if (!_rows.TryGetValue(id, out var v) || v.Cancel is null)
        {
            return false;
        }

        v.Cancel();
        return true;
    }
}
