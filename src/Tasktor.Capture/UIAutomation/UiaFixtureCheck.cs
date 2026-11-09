using System.Diagnostics;
using System.Runtime.InteropServices;
using Tasktor.Capture.Deep;

namespace Tasktor.Capture.UIAutomation;

/// <summary>
/// The #8 real-PC check (<c>tasktor-capture --check-uia</c>), driving only its own windows: a fixture with known text
/// and a password box, and a window whose thread hangs. It checks that the fixture text is walked within 3 s, that the
/// password box's text never appears, and that a walk of the hung window is given up near the 1 s timeout; it prints
/// pass / fail and timings only. Both windows live in child processes (<c>--uia-fixture-child</c>): UIA's connection and
/// transaction timeouts govern only cross-process calls, so an in-process hung window would never exercise them.
/// </summary>
public static unsafe partial class UiaFixtureCheck
{
    public const string FixtureText = "Tasktor fixture text 7f3a";
    public const string FixtureSecret = "fixture-secret-91c2";
    public const string ChildFlag = "--uia-fixture-child", HangFlag = "--hang";
    private const uint WsOverlappedWindow = 0x00CF0000, WsVisible = 0x10000000, WsChild = 0x40000000, EsPassword = 0x0020;

    public static int Run(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!OperatingSystem.IsWindows())
        {
            output.WriteLine("check-uia: Windows only");
            return 2;
        }

        using var fixture = FixtureProcess.Start(hang: false);
        if (fixture is null)
        {
            output.WriteLine("check-uia: FAIL (fixture process did not start)");
            return 1;
        }

        int code = 1;
        var thread = new Thread(() => code = Check(output, fixture.Hwnd)) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        return code;
    }

    /// <summary>The child side: shows one fixture window (hung for its first 6 s with <see cref="HangFlag"/>), prints its handle, lives ≤ 30 s.</summary>
    public static int RunChild(bool hang, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var window = new FixtureWindow(hang, output);
        window.Join(TimeSpan.FromSeconds(30));
        return 0;
    }

    private static int Check(TextWriter output, long fixture)
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

        // Started only now, so its 6 s of silence cover the walk: an unanswered walk ends near the 1 s timeout, not at 6 s.
        using var hungProcess = FixtureProcess.Start(hang: true);
        if (hungProcess is null)
        {
            output.WriteLine("check-uia: FAIL (hung fixture process did not start)");
            return 1;
        }

        // The worker's path: ask the window first, walk only if it answers.
        var hung = hungProcess.Hwnd;
        var h = Stopwatch.StartNew();
        var hungWalk = client.Responds(hung) && client.Root(hung, textBodies: true) is { } r ? UiaWalkLogic.TryWalk(new ViewKey(hung, 0), r) : null;
        var hungMs = h.Elapsed.TotalMilliseconds;
        var abandoned = hungWalk is null && hungMs < UiaWorker.TimeoutMs * 2.5;
        var (connection, transaction) = client.Timeouts;
        var timeoutsSet = connection == Win32UiaClient.TimeoutMs && transaction == Win32UiaClient.TimeoutMs;
        output.WriteLine(FormattableString.Invariant($"fixture text within 3 s: {(found ? "pass" : "FAIL")}"));
        output.WriteLine(FormattableString.Invariant($"password box never read: {(secretSeen ? "FAIL" : "pass")}"));
        output.WriteLine(FormattableString.Invariant($"hung window given up after {hungMs:0} ms: {(abandoned ? "pass" : "FAIL")}{(hungWalk is null ? string.Empty : " (walk returned)")}"));
        output.WriteLine(FormattableString.Invariant($"UIA timeouts read back {connection} / {transaction} ms: {(timeoutsSet ? "pass" : "FAIL")}"));
        output.WriteLine(FormattableString.Invariant($"walk p50 {times[times.Count / 2]:0.0} ms, p95 {p95:0.0} ms, max {times[^1]:0.0} ms (target p95 ≤ 150 ms)"));
        return found && !secretSeen && abandoned && timeoutsSet && p95 <= 150 ? 0 : 1;
    }

    /// <summary>A child <c>tasktor-capture</c> showing one fixture window; disposing it ends the child.</summary>
    private sealed class FixtureProcess : IDisposable
    {
        private readonly Process _process;

        private FixtureProcess(Process process, long hwnd)
        {
            _process = process;
            Hwnd = hwnd;
        }

        public long Hwnd { get; }

        public static FixtureProcess? Start(bool hang)
        {
            if (Environment.ProcessPath is not { } exe) return null;
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            info.ArgumentList.Add(ChildFlag);
            if (hang) info.ArgumentList.Add(HangFlag);
            var process = Process.Start(info);
            if (process is null) return null;
            var line = process.StandardOutput.ReadLineAsync();
            if (line.Wait(TimeSpan.FromSeconds(10))
                && long.TryParse(line.Result, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var hwnd)
                && hwnd != 0)
            {
                return new FixtureProcess(process, hwnd);
            }

            Kill(process);
            return null;
        }

        public void Dispose() => Kill(_process);

        private static void Kill(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill();
                process.WaitForExit(5000);
            }
            catch (InvalidOperationException)
            {
                // already gone
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // exiting as we asked
            }

            process.Dispose();
        }
    }

    /// <summary>A top-level window with an edit holding <see cref="FixtureText"/> and a password edit, on its own thread.</summary>
    private sealed class FixtureWindow
    {
        private readonly Thread _thread;

        public FixtureWindow(bool hang, TextWriter output)
        {
            _thread = new Thread(() => Run(hang, output)) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public void Join(TimeSpan limit) => _thread.Join(limit);

        private static void Run(bool hang, TextWriter output)
        {
            fixed (char* cls = "Static")
            fixed (char* title = hang ? "Tasktor UIA check (hung)" : "Tasktor UIA check")
            fixed (char* edit = "Edit")
            fixed (char* text = FixtureText)
            fixed (char* secret = FixtureSecret)
            {
                var h = CreateWindowExW(0, cls, title, WsOverlappedWindow | WsVisible, 100, 100, 480, 200, 0, 0, 0, 0);
                CreateWindowExW(0, edit, text, WsChild | WsVisible, 10, 10, 440, 30, h, 0, 0, 0);
                CreateWindowExW(0, edit, secret, WsChild | WsVisible | EsPassword, 10, 60, 440, 30, h, 0, 0, 0);
                output.WriteLine(((long)h).ToString(System.Globalization.CultureInfo.InvariantCulture));
                output.Flush();
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
}
