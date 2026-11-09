using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

#pragma warning disable SA1201, SA1600
namespace Tasktor.Capture.Media;

/// <summary>
/// C20.1 / §5.6 audible source: once a second, on its own MTA thread, the default render endpoint's audio sessions
/// (pid, state, peak) through source-generated COM, folded by <see cref="AudioMeterLogic"/>. Reads levels only, never
/// audio. The session list is re-read each second (a snapshot is cheap and needs no notification callback).
/// </summary>
public sealed unsafe partial class WasapiMeters : IDisposable
{
    private static readonly StrategyBasedComWrappers Wrappers = new();
    private static readonly Guid ClsidMmDeviceEnumerator = new("bcde0395-e52f-467c-8e3d-c4579291692e");
    private readonly AudioMeterLogic _logic;
    private readonly Func<DateTimeOffset> _now;
    private readonly ManualResetEventSlim _stop = new();
    private Thread? _thread;

    public WasapiMeters(AudioMeterLogic logic, Func<DateTimeOffset> now)
    {
        _logic = logic ?? throw new ArgumentNullException(nameof(logic));
        _now = now ?? throw new ArgumentNullException(nameof(now));
    }

    public bool Start()
    {
        if (!OperatingSystem.IsWindows() || _thread is not null) return false;
        _thread = new Thread(Run) { IsBackground = true, Name = "tasktor-audio-meters" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        return true;
    }

    public void Dispose()
    {
        _stop.Set();
        _thread?.Join(TimeSpan.FromSeconds(5));
        _thread = null;
    }

    /// <summary>One snapshot of the default render endpoint's sessions (empty when there is no device).</summary>
    public static List<AudioSessionSample> Read()
    {
        var list = new List<AudioSessionSample>();
        if (!OperatingSystem.IsWindows()) return list;
        var clsid = ClsidMmDeviceEnumerator;
        var iid = typeof(IMMDeviceEnumerator).GUID;
        if (CoCreateInstance(&clsid, 0, 1, &iid, out var ptr) < 0 || ptr == 0) return list;
        IMMDeviceEnumerator devices;
        try
        {
            devices = (IMMDeviceEnumerator)Wrappers.GetOrCreateObjectForComInstance(ptr, CreateObjectFlags.None);
        }
        finally
        {
            Marshal.Release(ptr);
        }

        IMMDevice device;
        try
        {
            device = devices.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */);
        }
        catch (COMException)
        {
            return list; // no output device
        }

        var managerIid = typeof(IAudioSessionManager2).GUID;
        var managerPtr = device.Activate(in managerIid, 1, 0);
        IAudioSessionManager2 manager;
        try
        {
            manager = (IAudioSessionManager2)Wrappers.GetOrCreateObjectForComInstance(managerPtr, CreateObjectFlags.None);
        }
        finally
        {
            Marshal.Release(managerPtr);
        }

        var sessions = manager.GetSessionEnumerator();
        var n = sessions.GetCount();
        for (var i = 0; i < n; i++)
        {
            try
            {
                var control = sessions.GetSession(i);
                var state = control.GetState(); // 0 inactive, 1 active, 2 expired
                var pid = control.GetProcessId();
                var peak = state == 1 ? ((IAudioMeterInformation)control).GetPeakValue() : 0f;
                list.Add(new AudioSessionSample(pid, peak, state == 1));
            }
            catch (Exception)
            {
                // a session that ended while being read
            }
        }

        return list;
    }

    private void Run()
    {
        while (!_stop.Wait(TimeSpan.FromSeconds(1)))
        {
            try
            {
                _logic.Sample(_now(), Read());
            }
            catch (Exception)
            {
                // the audio service restarting: try again next second; never logged
            }
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, out nint instance);
}

[GeneratedComInterface]
[Guid("a95664d2-9614-4f35-a746-de8db63617e6")]
internal partial interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints();
    IMMDevice GetDefaultAudioEndpoint(int dataFlow, int role);
    void GetDevice();
    void RegisterEndpointNotificationCallback();
    void UnregisterEndpointNotificationCallback();
}

[GeneratedComInterface]
[Guid("d666063f-1587-4e43-81f1-b948e807363f")]
internal partial interface IMMDevice
{
    nint Activate(in Guid iid, uint clsCtx, nint activationParams);
    void OpenPropertyStore();
    void GetId();
    void GetState();
}

[GeneratedComInterface]
[Guid("bfa971f1-4d5e-40bb-935e-967039bfbee4")]
internal partial interface IAudioSessionManager
{
    void GetAudioSessionControl();
    void GetSimpleAudioVolume();
}

[GeneratedComInterface]
[Guid("77aa99a0-1bd6-484f-8bc7-2c654c9a9b6f")]
internal partial interface IAudioSessionManager2 : IAudioSessionManager
{
    IAudioSessionEnumerator GetSessionEnumerator();
    void RegisterSessionNotification();
    void UnregisterSessionNotification();
    void RegisterDuckNotification();
    void UnregisterDuckNotification();
}

[GeneratedComInterface]
[Guid("e2f5bb11-0570-40ca-acdd-3aa01277dee8")]
internal partial interface IAudioSessionEnumerator
{
    int GetCount();

    // The header says IAudioSessionControl**, but on Windows 11 (26200) the session object answers QueryInterface for
    // IAudioSessionControl2 and IAudioMeterInformation only, never IAudioSessionControl itself (E_NOINTERFACE), so the
    // marshaller must ask for the derived interface. Found by --check-media (#9).
    IAudioSessionControl2 GetSession(int index);
}

[GeneratedComInterface]
[Guid("f4b1a599-7266-4319-a8c1-e70acb11e8cd")]
internal partial interface IAudioSessionControl
{
    int GetState();
    void GetDisplayName();
    void SetDisplayName();
    void GetIconPath();
    void SetIconPath();
    void GetGroupingParam();
    void SetGroupingParam();
    void RegisterAudioSessionNotification();
    void UnregisterAudioSessionNotification();
}

[GeneratedComInterface]
[Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d")]
internal partial interface IAudioSessionControl2 : IAudioSessionControl
{
    void GetSessionIdentifier();
    void GetSessionInstanceIdentifier();
    uint GetProcessId();
    void IsSystemSoundsSession();
    void SetDuckingPreference();
}

[GeneratedComInterface]
[Guid("c02216f6-8c67-4b5b-9d00-d008e73e0064")]
internal partial interface IAudioMeterInformation
{
    float GetPeakValue();
}
