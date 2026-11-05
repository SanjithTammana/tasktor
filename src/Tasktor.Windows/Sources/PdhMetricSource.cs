using System.Runtime.InteropServices;
using Tasktor.Contracts.Platform;
using Tasktor.Core.Features.System;

namespace Tasktor.Windows.Sources;

/// <summary>
/// One PDH query for CPU, RAM, commit, GPU per adapter, physical disks and network (A1 data sources, D-A5: one API family =
/// one class). Rate counters need two collects, so the first read of a fresh query has no rates (A1 sampling note). The GPU
/// adapter value is the busiest engine type per adapter LUID; adapters and disks come from wildcard instances.
/// </summary>
public sealed partial class PdhMetricSource : IMetricSource, IDisposable
{
    private nint _query;
    private nint _cpu;
    private nint _cpuCores;
    private nint _avail;
    private nint _committed;
    private nint _limit;
    private nint _gpuEngine;
    private nint _gpuDedicated;
    private nint _gpuShared;
    private nint _diskIdle;
    private nint _diskRead;
    private nint _diskWrite;
    private nint _netRx;
    private nint _netTx;

    public string Name => "pdh";

    /// <summary>Per-process GPU % from the last read, keyed by PID (A2.3 GPU column).</summary>
    public IReadOnlyDictionary<uint, double> GpuByPid { get; private set; } = new Dictionary<uint, double>();

    public void Read(MetricFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        EnsureQuery();
        if (PdhCollectQueryData(_query) != 0)
        {
            return;
        }

        if (Single(_cpu) is { } cpu)
        {
            frame.Set(MetricIds.CpuPct, cpu);
        }

        foreach (var (inst, v) in Array(_cpuCores))
        {
            if (!inst.Contains("_Total", StringComparison.Ordinal))
            {
                frame.Set(new MetricKey(MetricIds.CpuCorePct, inst), v);
            }
        }

        if (Single(_avail) is { } avail)
        {
            frame.Set(MetricIds.RamAvailableMb, avail);
        }

        var mem = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            frame.Set(MetricIds.RamTotalMb, mem.TotalPhys / (1024.0 * 1024));
        }

        if (Single(_committed) is { } c && Single(_limit) is { } l)
        {
            frame.Set(MetricIds.CommitBytes, c);
            frame.Set(MetricIds.CommitLimitBytes, l);
        }

        // GPU Engine instances: pid_<pid>_luid_<hi>_<lo>_phys_<n>_eng_<n>_engtype_<type>
        var perEngineType = new Dictionary<(string Luid, string Type), double>();
        var perPid = new Dictionary<uint, double>();
        foreach (var (inst, v) in Array(_gpuEngine))
        {
            var luid = Between(inst, "luid_", "_phys");
            var type = After(inst, "engtype_");
            if (luid is null || type is null)
            {
                continue;
            }

            perEngineType[(luid, type)] = perEngineType.GetValueOrDefault((luid, type)) + v;
            if (Between(inst, "pid_", "_luid") is { } pidText && uint.TryParse(pidText, out var pid))
            {
                perPid[pid] = Math.Max(perPid.GetValueOrDefault(pid), v);
            }
        }

        GpuByPid = perPid;
        foreach (var g in perEngineType.GroupBy(kv => kv.Key.Luid))
        {
            frame.Set(new MetricKey(MetricIds.GpuPct, "luid_" + g.Key), Math.Min(100, g.Max(kv => kv.Value)));
        }

        foreach (var (inst, v) in Array(_gpuDedicated))
        {
            if (AdapterLuid(inst) is { } luid)
            {
                frame.Set(new MetricKey(MetricIds.GpuDedicatedMb, "luid_" + luid), v / (1024 * 1024));
            }
        }

        foreach (var (inst, v) in Array(_gpuShared))
        {
            if (AdapterLuid(inst) is { } luid)
            {
                frame.Set(new MetricKey(MetricIds.GpuSharedMb, "luid_" + luid), v / (1024 * 1024));
            }
        }

        foreach (var (inst, v) in Array(_diskIdle))
        {
            if (inst != "_Total")
            {
                frame.Set(new MetricKey(MetricIds.DiskIdlePct, inst), Math.Clamp(v, 0, 100));
            }
        }

        foreach (var (inst, v) in Array(_diskRead))
        {
            if (inst != "_Total")
            {
                frame.Set(new MetricKey(MetricIds.DiskReadBps, inst), v);
            }
        }

        foreach (var (inst, v) in Array(_diskWrite))
        {
            if (inst != "_Total")
            {
                frame.Set(new MetricKey(MetricIds.DiskWriteBps, inst), v);
            }
        }

        double rx = 0, tx = 0;
        foreach (var (_, v) in Array(_netRx))
        {
            rx += v;
        }

        foreach (var (_, v) in Array(_netTx))
        {
            tx += v;
        }

        frame.Set(MetricIds.NetRxBps, rx);
        frame.Set(MetricIds.NetTxBps, tx);
    }

    public void Dispose()
    {
        if (_query != 0)
        {
            PdhCloseQuery(_query);
            _query = 0;
        }
    }

    private void EnsureQuery()
    {
        if (_query != 0)
        {
            return;
        }

        if (PdhOpenQueryW(null, 0, out _query) != 0)
        {
            throw new InvalidOperationException("PdhOpenQuery failed");
        }

        _cpu = Add(@"\Processor Information(_Total)\% Processor Time");
        _cpuCores = Add(@"\Processor Information(*)\% Processor Time");
        _avail = Add(@"\Memory\Available MBytes");
        _committed = Add(@"\Memory\Committed Bytes");
        _limit = Add(@"\Memory\Commit Limit");
        _gpuEngine = Add(@"\GPU Engine(*)\Utilization Percentage");
        _gpuDedicated = Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
        _gpuShared = Add(@"\GPU Adapter Memory(*)\Shared Usage");
        _diskIdle = Add(@"\PhysicalDisk(*)\% Idle Time");
        _diskRead = Add(@"\PhysicalDisk(*)\Disk Read Bytes/sec");
        _diskWrite = Add(@"\PhysicalDisk(*)\Disk Write Bytes/sec");
        _netRx = Add(@"\Network Interface(*)\Bytes Received/sec");
        _netTx = Add(@"\Network Interface(*)\Bytes Sent/sec");
        PdhCollectQueryData(_query); // prime the rate counters
    }

    private nint Add(string path) => PdhAddEnglishCounterW(_query, path, 0, out var h) == 0 ? h : 0; // a missing counter (no GPU, VM) is simply absent

    private static double? Single(nint counter)
    {
        if (counter == 0)
        {
            return null;
        }

        return PdhGetFormattedCounterValue(counter, PdhFmtDouble | PdhFmtNoCap100, out _, out var value) == 0 && value.Status == 0 ? value.DoubleValue : null;
    }

    private static unsafe List<(string Instance, double Value)> Array(nint counter)
    {
        var list = new List<(string, double)>();
        if (counter == 0)
        {
            return list;
        }

        uint size = 0;
        uint count = 0;
        var rc = PdhGetFormattedCounterArrayW(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, ref count, null);
        if (rc != PdhMoreData || size == 0)
        {
            return list;
        }

        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            if (PdhGetFormattedCounterArrayW(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, ref count, p) != 0)
            {
                return list;
            }

            var items = (FmtCounterValueItem*)p;
            for (var i = 0; i < count; i++)
            {
                if (items[i].Value.Status == 0)
                {
                    list.Add((Marshal.PtrToStringUni(items[i].Name) ?? string.Empty, items[i].Value.DoubleValue));
                }
            }
        }

        return list;
    }

    private static string? Between(string s, string start, string end)
    {
        var a = s.IndexOf(start, StringComparison.Ordinal);
        if (a < 0)
        {
            return null;
        }

        a += start.Length;
        var b = s.IndexOf(end, a, StringComparison.Ordinal);
        return b < 0 ? null : s[a..b];
    }

    /// <summary>GPU Adapter Memory instances: <c>luid_&lt;hi&gt;_&lt;lo&gt;_phys_&lt;n&gt;</c> (or without the phys part).</summary>
    private static string? AdapterLuid(string inst) => Between(inst + "_end", "luid_", "_phys") ?? Between(inst + "_end", "luid_", "_end");

    private static string? After(string s, string start)
    {
        var a = s.IndexOf(start, StringComparison.Ordinal);
        return a < 0 ? null : s[(a + start.Length)..];
    }

    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhFmtNoCap100 = 0x00008000;
    private const uint PdhMoreData = 0x800007D2;

    [StructLayout(LayoutKind.Sequential)]
    private struct FmtCounterValue
    {
        public uint Status;
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FmtCounterValueItem
    {
        public nint Name;
        public FmtCounterValue Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQueryW(string? dataSource, nint userData, out nint query);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounterW(nint query, string path, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out FmtCounterValue value);

    [LibraryImport("pdh.dll")]
    private static unsafe partial uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bufferSize, ref uint itemCount, byte* items);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(nint query);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
