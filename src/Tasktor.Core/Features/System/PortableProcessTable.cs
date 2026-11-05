using System.Diagnostics;
using Tasktor.Contracts.Platform;

namespace Tasktor.Core.Features.System;

/// <summary>
/// A process table through <see cref="Process"/> for non-Windows development runs (no parent PIDs). Windows uses
/// <c>Win32ProcessTable</c>.
/// </summary>
public sealed class PortableProcessTable : ITableSource<ProcKey, ProcessRow>
{
    public string Name => "portable-processes";

    public void Read(ICollection<KeyValuePair<ProcKey, ProcessRow>> into, ColumnSet columns)
    {
        ArgumentNullException.ThrowIfNull(into);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    var created = (columns & ColumnSet.Usage) != 0 ? p.StartTime.ToUniversalTime().Ticks : 0;
                    var row = new ProcessRow
                    {
                        Key = new ProcKey((uint)p.Id, created),
                        Exe = p.ProcessName,
                        CpuTimeS = (columns & ColumnSet.Usage) != 0 ? p.TotalProcessorTime.TotalSeconds : 0,
                        PrivateBytes = (columns & ColumnSet.Usage) != 0 ? p.PrivateMemorySize64 : 0,
                        Threads = p.Threads.Count,
                    };
                    into.Add(new KeyValuePair<ProcKey, ProcessRow>(row.Key, row));
                }
                catch (Exception ex) when (ex is InvalidOperationException or global::System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // exited mid-read, or not ours: skip silently (A2 edge case)
                }
            }
        }
    }

    public static ProcessRow? ReadOne(ProcKey key)
    {
        try
        {
            using var p = Process.GetProcessById((int)key.Pid);
            return new ProcessRow { Key = new ProcKey(key.Pid, key.CreatedTicks == 0 ? 0 : p.StartTime.ToUniversalTime().Ticks), Exe = p.ProcessName };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or global::System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
