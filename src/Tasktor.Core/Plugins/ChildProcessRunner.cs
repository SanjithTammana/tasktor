using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Tasktor.Contracts;
using Tasktor.Contracts.Platform;

namespace Tasktor.Core.Plugins;

/// <summary>A started child, however it was launched: its streams, its exit, and a way to end it and everything it
/// started.</summary>
public interface IChild : IDisposable
{
    int Id { get; }

    /// <summary>Creation time as a FILETIME (0 when unknown): matches the process table's <see cref="ProcKey"/>.</summary>
    long StartTicks { get; }

    StreamWriter Stdin { get; }

    StreamReader Stdout { get; }

    StreamReader Stderr { get; }

    int ExitCode { get; }

    Task WaitForExitAsync(CancellationToken cancel);

    /// <summary>End the child and everything it started.</summary>
    void KillTree();
}

/// <summary>
/// Starts Tasktor's children (D-H9): a closed <see cref="WorkerKind"/>, an exe that must resolve inside the install dir
/// for every kind but <c>plugin</c>, stdin written then closed, stdout captured up to a cap, the last 4 KB of stderr kept,
/// killed (whole tree) at the timeout. <see cref="Launch"/> lets the Windows adapter create the child inside a kill-on-close
/// Job Object with a memory cap, so nothing it starts can escape the job (F-PC9). Portable: the Linux tests run real child
/// processes through it.
/// </summary>
public sealed class ChildProcessRunner
{
    public const int StdoutCapBytes = 64 * 1024;
    public const int StderrTailBytes = 4 * 1024;

    private readonly ConcurrentDictionary<string, Running> _running = new(StringComparer.Ordinal);
    private long _next;

    public ChildProcessRunner(string? installDir = null) => InstallDir = installDir;

    /// <summary>The install dir (non-plugin workers must live inside it). Null disables the check (development).</summary>
    public string? InstallDir { get; }

    /// <summary>
    /// How a child is started. Default: <see cref="Process.Start(ProcessStartInfo)"/>. Windows: created inside a kill-on-close
    /// Job Object (memory cap), BelowNormal / EcoQoS; the job is closed when the child is disposed, and if the agent dies
    /// Windows closes it and kills the child and everything it started.
    /// </summary>
    public Func<ProcessStartInfo, WorkerSpec, IChild>? Launch { get; set; }

    public int RunningCount => _running.Count;

    public WorkerHandle Start(WorkerSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Kind != WorkerKind.Plugin && InstallDir is not null)
        {
            var exe = Path.GetFullPath(spec.Exe);
            var root = Path.GetFullPath(InstallDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!exe.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("A " + spec.Kind.ToString().ToLowerInvariant() + " worker must live inside the install dir");
            }
        }

        var psi = new ProcessStartInfo(spec.Exe)
        {
            WorkingDirectory = spec.WorkingDir,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in spec.Args ?? [])
        {
            psi.ArgumentList.Add(a);
        }

        foreach (var name in spec.ClearEnv ?? [])
        {
            psi.Environment.Remove(name);
        }

        var child = Launch is { } launch ? launch(psi, spec) : ProcessChild.Start(psi, spec);
        var id = "w" + Interlocked.Increment(ref _next).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var key = new ProcKey((uint)child.Id, child.StartTicks);
        var handle = new WorkerHandle(id, key, spec.Kind);
        var run = new Running(child, spec);
        _running[id] = run;
        run.Pump(spec.Stdin);
        return handle;
    }

    public async Task<WorkerExit> WaitAsync(WorkerHandle handle, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!_running.TryGetValue(handle.Id, out var run))
        {
            throw new InvalidOperationException("unknown worker " + handle.Id);
        }

        var timedOut = false;
        using var timeout = run.Spec.TimeoutMs > 0 ? new CancellationTokenSource(run.Spec.TimeoutMs) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancel, run.Cancel.Token);
        try
        {
            await run.Child.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeout.IsCancellationRequested;
            run.Child.KillTree();
            await run.Child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        // Whatever the worker left running ends with it; until then a leftover that inherited its pipes would hold the
        // output open and keep this wait from returning, past any timeout.
        run.Child.KillTree();
        await run.Drained.ConfigureAwait(false);
        _running.TryRemove(handle.Id, out _);
        int? code = timedOut || run.Cancel.IsCancellationRequested ? null : run.Child.ExitCode;
        run.Child.Dispose();
        return new WorkerExit(handle, code, run.Stdout, run.StderrTail, timedOut, run.TooLarge);
    }

    public void Cancel(WorkerHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (_running.TryGetValue(handle.Id, out var run))
        {
            run.Cancel.Cancel();
        }
    }

    /// <summary>Writes one control line to the worker's stdin (D-H9 condition 3: a closed message set). False when gone.</summary>
    public bool Send(WorkerHandle handle, ControlMessage message)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!_running.TryGetValue(handle.Id, out var run) || run.StdinClosed)
        {
            return false;
        }

        try
        {
            run.Child.Stdin.WriteLine("{\"control\":\"" + message.ToString().ToLowerInvariant() + "\"}");
            run.Child.Stdin.Flush();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>The portable child: <see cref="Process"/>, ended with <c>Kill(entireProcessTree)</c>.</summary>
    private sealed class ProcessChild(Process process) : IChild
    {
        public static ProcessChild Start(ProcessStartInfo psi, WorkerSpec spec) =>
            new(Process.Start(psi) ?? throw new InvalidOperationException("could not start " + Path.GetFileName(spec.Exe)));

        public int Id => process.Id;

        public long StartTicks
        {
            get
            {
                try
                {
                    return process.StartTime.ToFileTimeUtc(); // FILETIME: matches the process table's ProcKey
                }
                catch (InvalidOperationException)
                {
                    return 0;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    return 0;
                }
            }
        }

        public StreamWriter Stdin => process.StandardInput;

        public StreamReader Stdout => process.StandardOutput;

        public StreamReader Stderr => process.StandardError;

        public int ExitCode => process.ExitCode;

        public Task WaitForExitAsync(CancellationToken cancel) => process.WaitForExitAsync(cancel);

        public void KillTree()
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }

        public void Dispose() => process.Dispose();
    }

    private sealed class Running(IChild child, WorkerSpec spec)
    {
        private readonly StringBuilder _out = new();
        private readonly StringBuilder _err = new();

        public IChild Child { get; } = child;

        public WorkerSpec Spec { get; } = spec;

        public CancellationTokenSource Cancel { get; } = new();

        public Task Drained { get; private set; } = Task.CompletedTask;

        public bool TooLarge { get; private set; }

        public bool StdinClosed { get; private set; }

        public string Stdout => _out.ToString();

        public string StderrTail => _err.ToString();

        public void Pump(string? stdin)
        {
            var outTask = Task.Run(async () =>
            {
                var buf = new char[8192];
                int n;
                var total = 0;
                while ((n = await Child.Stdout.ReadAsync(buf).ConfigureAwait(false)) > 0)
                {
                    total += Encoding.UTF8.GetByteCount(buf, 0, n);
                    if (total > StdoutCapBytes)
                    {
                        TooLarge = true;
                        Child.KillTree();
                        break;
                    }

                    _out.Append(buf, 0, n);
                }
            });
            var errTask = Task.Run(async () =>
            {
                var buf = new char[4096];
                int n;
                while ((n = await Child.Stderr.ReadAsync(buf).ConfigureAwait(false)) > 0)
                {
                    _err.Append(buf, 0, n);
                    if (_err.Length > StderrTailBytes)
                    {
                        _err.Remove(0, _err.Length - StderrTailBytes);
                    }
                }
            });
            Drained = Task.WhenAll(outTask, errTask);
            try
            {
                if (stdin is not null)
                {
                    Child.Stdin.Write(stdin);
                }

                if (Spec.Kind == WorkerKind.Plugin || stdin is not null)
                {
                    Child.Stdin.Close();
                    StdinClosed = true;
                }
            }
            catch (IOException)
            {
                StdinClosed = true;
            }
        }
    }
}
