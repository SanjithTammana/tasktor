using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tasktor.Contracts.Platform;

namespace Tasktor.Windows.Sources;

/// <summary>A8: <c>GetSystemPowerStatus</c> into battery metrics (flag 128 = no system battery; 255 % / −1 s = unknown).</summary>
public sealed partial class PowerSource : IMetricSource
{
    public string Name => "power";

    public void Read(MetricFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!OperatingSystem.IsWindows() || !GetSystemPowerStatus(out var s))
        {
            frame.Set("battery.flag", 128);
            return;
        }

        frame.Set("battery.flag", s.BatteryFlag);
        frame.Set("battery.ac", s.ACLineStatus);
        frame.Set("battery.pct", s.BatteryLifePercent);
        frame.Set("battery.time_left_s", s.BatteryLifeTime == uint.MaxValue ? -1 : s.BatteryLifeTime);
        frame.Set("battery.saver", s.SystemStatusFlag);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);
}

/// <summary>
/// A8 push events: AC/DC source, battery percent and battery saver changes through <c>PowerSettingRegisterNotification</c>
/// with a callback (no window, no polling). Each change posts a tick to the loop, which re-reads <see cref="PowerSource"/>.
/// </summary>
public sealed unsafe partial class PowerEvents : IEventSource<int>
{
    private static readonly Guid AcDcSource = new("5d3e9a59-e9D5-4b00-a6bd-ff34ff516548");
    private static readonly Guid BatteryPercent = new("a7ad8041-b45a-4cae-87a3-eecbb468a9e1");
    private static readonly Guid PowerSaving = new("e00958c0-c213-4ace-ac77-fecced2eeea5");
    private static Action<int>? s_post;
    private readonly List<nint> _registrations = [];

    public string Name => "power-events";

    public void Start(Action<int> post)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        s_post = post;
        foreach (var g in new[] { AcDcSource, BatteryPercent, PowerSaving })
        {
            var guid = g;
            var p = new SubscribeParameters { Callback = &Callback, Context = 0 };
            if (PowerSettingRegisterNotification(&guid, 2 /* DEVICE_NOTIFY_CALLBACK */, &p, out var handle) == 0)
            {
                _registrations.Add(handle);
            }
        }
    }

    public void Stop()
    {
        foreach (var h in _registrations)
        {
            PowerSettingUnregisterNotification(h);
        }

        _registrations.Clear();
        s_post = null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Callback(nint context, uint type, nint setting)
    {
        s_post?.Invoke((int)type);
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SubscribeParameters
    {
        public delegate* unmanaged[Stdcall]<nint, uint, nint, uint> Callback;
        public nint Context;
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerSettingRegisterNotification(Guid* setting, uint flags, SubscribeParameters* recipient, out nint handle);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerSettingUnregisterNotification(nint handle);
}

/// <summary>
/// A6's cheap watch: a thread blocked on Windows' <c>LowMemoryResourceNotification</c>, then on the high-memory one
/// (no polling, zero wake-ups between changes). Posts true when memory goes low, false when it recovers.
/// </summary>
public sealed partial class LowMemoryEvents : IEventSource<bool>
{
    private nint _low;
    private nint _high;
    private Thread? _thread;
    private volatile bool _stop;

    public string Name => "low-memory";

    public void Start(Action<bool> post)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _low = CreateMemoryResourceNotification(0);
        _high = CreateMemoryResourceNotification(1);
        _thread = new Thread(() =>
        {
            while (!_stop)
            {
                if (WaitForSingleObject(_low, 60_000) == 0 && !_stop)
                {
                    post(true);
                    while (!_stop && WaitForSingleObject(_high, 60_000) != 0)
                    {
                    }

                    if (!_stop)
                    {
                        post(false);
                    }
                }
            }
        })
        { IsBackground = true, Name = "tasktor-low-memory" };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join(TimeSpan.FromSeconds(1));
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateMemoryResourceNotification(int type);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint ms);
}
