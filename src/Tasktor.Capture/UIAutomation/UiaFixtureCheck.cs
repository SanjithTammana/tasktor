using System.Diagnostics;
using System.Runtime.InteropServices;
using Tasktor.Capture.Deep;

namespace Tasktor.Capture.UIAutomation;

/// <summary>
/// The #8 real-PC check (<c>tasktor-capture --check-uia</c>), driving only its own windows: a fixture with known text
/// and a password box, and a window whose thread hangs. It checks that the fixture text is walked within 3 s, that the
/// password box's text never appears, and that a walk of the hung window is given up near the 1 s timeout; it prints
/// pass / fail and timings only.
/// </summary>
public static unsafe partial class UiaFixtureCheck
{
    public const string FixtureText = "Tasktor fixture text 7f3a";
    public const string FixtureSecret = "fixture-secret-91c2";
    private const uint WsOverlappedWindow = 0x00CF0000, WsVisible = 0x10000000, WsChild = 0x40000000, EsPassword = 0x0020;

    public static int Run(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!OperatingSystem.IsWindows())
        {
            output.WriteLine("check-uia: Windows only");
            return 2;
        }

        using var fixture = new FixtureWindow(hang: false);
        using var hung = new FixtureWindow(hang: true);
        int code = 1;
        var thread = new Thread(() => code = Check(output, fixture.Hwnd, hung.Hwnd)) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        return code;
    }

    private static int Check(TextWriter output, long fixture, long hung)
    {
        using var client = Win32UiaClient.Create();
        if (client is null)
        {
            output.WriteLine("check-uia: FAIL (UIA client not created)");
            return 1;
        }

        var found = false;
        var secretSeen = false;
        var times = new List<double>();
        var deadline = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            var t = Stopwatch.StartNew();
            var walk = client.Root(fixture, textBodies: true) is { } root ? UiaWalkLogic.TryWalk(new ViewKey(fixture, 0), root)?.Reveal() : null;
            times.Add(t.Elapsed.TotalMilliseconds);
            if (walk is null) continue;
            found |= walk.Strings.Any(s => s.Contains(FixtureText, StringComparison.Ordinal)) && deadline.ElapsedMilliseconds <= 3000;
            secretSeen |= walk.Strings.Any(s => s.Contains(FixtureSecret, StringComparison.Ordinal));
        }

        times.Sort();
        var p95 = times[(int)Math.Ceiling(0.95 * times.Count) - 1];
        var h = Stopwatch.StartNew();
        var hungWalk = client.Root(hung, textBodies: true) is { } r ? UiaWalkLogic.TryWalk(new ViewKey(hung, 0), r) : null;
        var hungMs = h.Elapsed.TotalMilliseconds;
        var abandoned = hungMs < UiaWorker.TimeoutMs * 2.5;
        output.WriteLine(FormattableString.Invariant($"fixture text within 3 s: {(found ? "pass" : "FAIL")}"));
        output.WriteLine(FormattableString.Invariant($"password box never read: {(secretSeen ? "FAIL" : "pass")}"));
        output.WriteLine(FormattableString.Invariant($"hung window given up after {hungMs:0} ms: {(abandoned ? "pass" : "FAIL")}{(hungWalk is null ? string.Empty : " (walk returned)")}"));
        output.WriteLine(FormattableString.Invariant($"walk p50 {times[times.Count / 2]:0.0} ms, p95 {p95:0.0} ms, max {times[^1]:0.0} ms (target p95 ≤ 150 ms)"));
        return found && !secretSeen && abandoned && p95 <= 150 ? 0 : 1;
    }

    /// <summary>A top-level window with an edit holding <see cref="FixtureText"/> and a password edit, on its own thread.</summary>
    private sealed class FixtureWindow : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        private uint _threadId;

        public FixtureWindow(bool hang)
        {
            _thread = new Thread(() => Run(hang)) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.Wait(TimeSpan.FromSeconds(5));
        }

        public long Hwnd { get; private set; }

        public void Dispose()
        {
            if (_threadId != 0) PostThreadMessageW(_threadId, 0x0012, 0, 0);
            _thread.Join(TimeSpan.FromSeconds(10));
        }

        private void Run(bool hang)
        {
            _threadId = GetCurrentThreadId();
            fixed (char* cls = "Static")
            fixed (char* title = hang ? "Tasktor UIA check (hung)" : "Tasktor UIA check")
            fixed (char* edit = "Edit")
            fixed (char* text = FixtureText)
            fixed (char* secret = FixtureSecret)
            {
                var h = CreateWindowExW(0, cls, title, WsOverlappedWindow | WsVisible, 100, 100, 480, 200, 0, 0, 0, 0);
                CreateWindowExW(0, edit, text, WsChild | WsVisible, 10, 10, 440, 30, h, 0, 0, 0);
                CreateWindowExW(0, edit, secret, WsChild | WsVisible | EsPassword, 10, 60, 440, 30, h, 0, 0, 0);
                Hwnd = h;
                _ready.Set();
                if (hang) Thread.Sleep(6000); // a window that answers nothing for 6 s
                Msg msg;
                while (GetMessageW(&msg, 0, 0, 0) > 0)
                {
                    TranslateMessage(&msg);
                    DispatchMessageW(&msg);
                }

                DestroyWindow(h);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Msg { public nint Hwnd; public uint Message; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [LibraryImport("user32.dll")] private static partial nint CreateWindowExW(uint exStyle, char* className, char* name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [LibraryImport("user32.dll")] private static partial int DestroyWindow(nint hwnd);
    [LibraryImport("user32.dll")] private static partial int GetMessageW(Msg* msg, nint hwnd, uint min, uint max);
    [LibraryImport("user32.dll")] private static partial int TranslateMessage(Msg* msg);
    [LibraryImport("user32.dll")] private static partial nint DispatchMessageW(Msg* msg);
    [LibraryImport("user32.dll")] private static partial int PostThreadMessageW(uint thread, uint message, nuint wp, nint lp);
    [LibraryImport("kernel32.dll")] private static partial uint GetCurrentThreadId();
}
