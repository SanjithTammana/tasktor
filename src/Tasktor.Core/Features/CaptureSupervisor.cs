using Tasktor.Contracts;
using Tasktor.Contracts.Platform;

namespace Tasktor.Core.Features;

/// <summary>
/// U1 D-H9 supervision of <c>tasktor-capture</c>: the agent starts it with <see cref="IProcessControl.StartWorker"/> (kind
/// capture: a kill-on-close Job Object, so capture never outlives the agent) only while the Capture Module is on and its
/// consent exists, restarts a crashed host at most <see cref="MaxRestartsPerHour"/> times an hour with backoff, then stops
/// and reports <see cref="Failure"/> for the error card. A deliberate exit (code 0: another instance or a clean stop;
/// <see cref="ExitNoConsent"/>) is never restarted. Loop-only; the exit arrives as a task continuation posted to the loop.
/// </summary>
public sealed class CaptureSupervisor : IDisposable
{
    public const int MaxRestartsPerHour = 3;

    /// <summary>The capture host's exit code when it finds no capture consent on disk (it refuses to start).</summary>
    public const int ExitNoConsent = 3;

    public static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];

    private readonly FeatureContext _context;
    private readonly WorkerSpec _spec;
    private readonly Func<bool> _consented;
    private readonly List<DateTimeOffset> _restarts = [];
    private WorkerHandle? _handle;
    private IDisposable? _pending;
    private bool _wanted;

    public CaptureSupervisor(FeatureContext context, WorkerSpec spec, Func<bool> consented)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Kind != WorkerKind.Capture)
        {
            throw new ArgumentException("the capture supervisor starts capture workers only", nameof(spec));
        }

        _context = context;
        _spec = spec;
        _consented = consented;
    }

    /// <summary>The default spec: <c>tasktor-capture(.exe)</c> next to the agent, its store root passed through.</summary>
    public static WorkerSpec DefaultSpec(string installDir, string? dataDir) => new()
    {
        Kind = WorkerKind.Capture,
        Exe = Path.Combine(installDir, OperatingSystem.IsWindows() ? "tasktor-capture.exe" : "tasktor-capture"),
        Args = dataDir is null ? null : ["--data-dir", dataDir],
        WorkingDir = installDir,
        MemoryMb = 64, // D-C21: ≤ 40 MB private; the Job Object cap leaves headroom and stops a runaway
    };

    public bool Running => _handle is not null;

    /// <summary>Why capture is not running although it is wanted: <c>no_consent</c>, <c>no_process_control</c>,
    /// <c>start_failed</c>, <c>crashed</c> (restart budget spent) or <c>refused</c> (the host found no consent). Null while fine.</summary>
    public string? Failure { get; private set; }

    public int Starts { get; private set; }

    /// <summary>Running or <see cref="Failure"/> changed (on the loop).</summary>
    public event Action? Changed;

    public void Start()
    {
        _wanted = true;
        _restarts.Clear();
        Failure = null;
        Launch();
    }

    public void Stop()
    {
        _wanted = false;
        _pending?.Dispose();
        _pending = null;
        if (_handle is { } h)
        {
            _handle = null;
            _context.Processes?.CancelWorker(h);
        }

        Failure = null;
        Changed?.Invoke();
    }

    public void Dispose() => Stop();

    private void Launch()
    {
        _pending = null;
        if (!_wanted || _handle is not null)
        {
            return;
        }

        if (!_consented())
        {
            Fail("no_consent");
            return;
        }

        if (_context.Processes is not { } pc)
        {
            Fail("no_process_control");
            return;
        }

        WorkerHandle handle;
        try
        {
            handle = pc.StartWorker(_spec);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _context.Log.Warn("capture.start_failed", Logging.LogField.Of("error", e.GetType().Name));
            Fail("start_failed");
            return;
        }

        _handle = handle;
        Starts++;
        Failure = null;
        _context.Log.Info("capture.started", Logging.LogField.Of("starts", Starts));
        Changed?.Invoke();
        // No thread waits for the host's lifetime: the exit completes a task and the continuation posts it to the loop.
        Task<WorkerExit> wait;
        try
        {
            wait = pc.WaitWorker(handle, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            wait = Task.FromException<WorkerExit>(new InvalidOperationException("unknown worker"));
        }

        wait.ContinueWith(
            t => _context.Loop.Post(() => Exited(handle, t.IsCompletedSuccessfully ? t.Result.ExitCode : null)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void Exited(WorkerHandle handle, int? code)
    {
        if (!ReferenceEquals(handle, _handle))
        {
            return; // a stopped (cancelled) host, or an older one
        }

        _handle = null;
        if (!_wanted)
        {
            return;
        }

        _context.Log.Warn("capture.exited", Logging.LogField.Of("code", code ?? -1));
        if (code == 0)
        {
            Changed?.Invoke(); // deliberate: another instance is running, or it stopped cleanly
            return;
        }

        if (code == ExitNoConsent)
        {
            Fail("refused");
            return;
        }

        var now = _context.Clock.UtcNow;
        _restarts.RemoveAll(t => now - t >= TimeSpan.FromHours(1));
        if (_restarts.Count >= MaxRestartsPerHour)
        {
            Fail("crashed");
            return;
        }

        var delay = Backoff[Math.Min(_restarts.Count, Backoff.Length - 1)];
        _restarts.Add(now);
        Changed?.Invoke();
        _pending = _context.Loop.At(now + delay, Launch);
    }

    private void Fail(string why)
    {
        Failure = why;
        _context.Log.Warn("capture.not_running", Logging.LogField.Of("reason", why));
        Changed?.Invoke();
    }
}
