using System.Runtime.InteropServices;
using Tasktor.Contracts.Platform;

namespace Tasktor.Windows.Sources;

/// <summary>
/// U2 <c>CallStateSource</c> (I3): which apps use the microphone now, from the capability consent store
/// (<c>HKCU\…\CapabilityAccessManager\ConsentStore\microphone</c>: per-package keys and <c>NonPackaged\C:#…#app.exe</c>; an
/// entry with <c>LastUsedTimeStart</c> set and <c>LastUsedTimeStop = 0</c> is in use now). Event-driven: one thread waits
/// on <c>RegNotifyChangeKeyValue</c> for the subtree, no polling. Reads names only, never audio or who is in the call. If
/// the key cannot be opened, posts <c>Available = false</c> once so I3 falls back to the calendar (OQ-S1).
/// </summary>
public sealed partial class CallStateSource : IEventSource<CallState>
{
    public const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    private static readonly nint HkeyCurrentUser = unchecked((nint)0x80000001);
    private const int KeyRead = 0x20019;
    private const int RegNotifyChangeName = 0x1;
    private const int RegNotifyChangeLastSet = 0x4;

    private Thread? _thread;
    private nint _stopEvent;
    private volatile bool _stop;

    public string Name => "call-state";

    public void Start(Action<CallState> post)
    {
        ArgumentNullException.ThrowIfNull(post);
        if (!OperatingSystem.IsWindows())
        {
            post(new CallState([], false));
            return;
        }

        _stop = false;
        _stopEvent = CreateEventW(0, 1, 0, 0);
        _thread = new Thread(() => Run(post)) { IsBackground = true, Name = "tasktor-call-state" };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        if (_stopEvent != 0)
        {
            SetEvent(_stopEvent);
        }

        _thread?.Join(TimeSpan.FromSeconds(1));
        if (_stopEvent != 0)
        {
            CloseHandle(_stopEvent);
            _stopEvent = 0;
        }
    }

    private void Run(Action<CallState> post)
    {
        if (RegOpenKeyExW(HkeyCurrentUser, KeyPath, 0, KeyRead, out var key) != 0)
        {
            post(new CallState([], false));
            return;
        }

        var changed = CreateEventW(0, 0, 0, 0);
        try
        {
            string[]? last = null;
            while (!_stop)
            {
                if (RegNotifyChangeKeyValue(key, 1, RegNotifyChangeName | RegNotifyChangeLastSet, changed, 1) != 0)
                {
                    post(new CallState([], false));
                    return;
                }

                var now = InUse(key);
                if (last is null || !now.SequenceEqual(last, StringComparer.OrdinalIgnoreCase))
                {
                    last = now;
                    post(new CallState(now, true));
                }

                WaitForMultipleObjects(2, [changed, _stopEvent], 0, 0xFFFFFFFF);
            }
        }
        finally
        {
            CloseHandle(changed);
            RegCloseKey(key);
        }
    }

    /// <summary>Entries in use now, sorted: package families and NonPackaged keys (<c>C:#…#app.exe</c>).</summary>
    private static string[] InUse(nint root)
    {
        var list = new List<string>();
        foreach (var name in SubKeys(root))
        {
            if (name.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
            {
                if (RegOpenKeyExW(root, name, 0, KeyRead, out var np) == 0)
                {
                    try
                    {
                        list.AddRange(SubKeys(np).Where(n => Active(np, n)));
                    }
                    finally
                    {
                        RegCloseKey(np);
                    }
                }
            }
            else if (Active(root, name))
            {
                list.Add(name);
            }
        }

        list.Sort(StringComparer.OrdinalIgnoreCase);
        return [.. list];
    }

    private static bool Active(nint parent, string sub)
    {
        if (RegOpenKeyExW(parent, sub, 0, KeyRead, out var k) != 0)
        {
            return false;
        }

        try
        {
            return QueryQword(k, "LastUsedTimeStart") is > 0 && QueryQword(k, "LastUsedTimeStop") == 0;
        }
        finally
        {
            RegCloseKey(k);
        }
    }

    private static List<string> SubKeys(nint key)
    {
        var names = new List<string>();
        var buf = new ushort[512];
        for (uint i = 0; ; i++)
        {
            var len = (uint)buf.Length;
            var rc = RegEnumKeyExW(key, i, ref buf[0], ref len, 0, 0, 0, 0);
            if (rc != 0)
            {
                break; // ERROR_NO_MORE_ITEMS or a key too long to be one of ours
            }

            names.Add(new string(MemoryMarshal.Cast<ushort, char>(buf.AsSpan(0, (int)len))));
        }

        return names;
    }

    private static long? QueryQword(nint key, string value)
    {
        long data = 0;
        var size = (uint)sizeof(long);
        return RegQueryValueExW(key, value, 0, out var type, ref data, ref size) == 0 && type == 11 ? data : null;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegOpenKeyExW(nint key, string subKey, int options, int access, out nint result);

    [LibraryImport("advapi32.dll", EntryPoint = "RegEnumKeyExW")]
    private static partial int RegEnumKeyExW(nint key, uint index, ref ushort name, ref uint nameLen, nint reserved, nint cls, nint clsLen, nint lastWrite);

    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryValueExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegQueryValueExW(nint key, string value, nint reserved, out uint type, ref long data, ref uint size);

    [LibraryImport("advapi32.dll")]
    private static partial int RegNotifyChangeKeyValue(nint key, int watchSubtree, int filter, nint evt, int asynchronous);

    [LibraryImport("advapi32.dll")]
    private static partial int RegCloseKey(nint key);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW")]
    private static partial nint CreateEventW(nint attrs, int manualReset, int initialState, nint name);

    [LibraryImport("kernel32.dll")]
    private static partial int SetEvent(nint evt);

    [LibraryImport("kernel32.dll")]
    private static partial int CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForMultipleObjects(uint count, nint[] handles, int waitAll, uint ms);
}
