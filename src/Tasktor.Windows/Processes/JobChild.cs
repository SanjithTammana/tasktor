using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Tasktor.Contracts;
using Tasktor.Core.Plugins;

namespace Tasktor.Windows.Processes;

/// <summary>
/// D-H9 worker start on Win32 (F-PC9): the child is created <b>inside</b> its kill-on-close, memory-capped Job Object
/// (<c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c>), so it never runs outside it and everything it starts is in the job too. It
/// inherits exactly its three pipe ends (<c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c>). Ending it terminates the job: the child
/// and every descendant. Disposing it closes the job, which kills whatever is still running in it.
/// </summary>
public sealed partial class JobChild : IChild
{
    private const uint ExtendedStartupInfoPresent = 0x80000;
    private const uint CreateUnicodeEnvironment = 0x400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint BelowNormalPriorityClass = 0x4000;
    private const int StartfUseStdHandles = 0x100;
    private const uint HandleFlagInherit = 0x1;
    private const nint ProcThreadAttributeHandleList = 0x20002;
    private const nint ProcThreadAttributeJobList = 0x2000D;

    private readonly JobObject _job;
    private readonly SafeProcessHandle _process;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly RegisteredWaitHandle _wait;
    private readonly ProcessWaitHandle _waitHandle;

    private JobChild(JobObject job, SafeProcessHandle process, int id, StreamWriter stdin, StreamReader stdout, StreamReader stderr)
    {
        _job = job;
        _process = process;
        Id = id;
        Stdin = stdin;
        Stdout = stdout;
        Stderr = stderr;
        StartTicks = GetProcessTimes(process, out var created, out _, out _, out _) ? created : 0;
        _waitHandle = new ProcessWaitHandle(process);
        _wait = ThreadPool.RegisterWaitForSingleObject(_waitHandle, (_, _) => _exited.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public int Id { get; }

    public long StartTicks { get; }

    public StreamWriter Stdin { get; }

    public StreamReader Stdout { get; }

    public StreamReader Stderr { get; }

    public int ExitCode => GetExitCodeProcess(_process, out var code) ? (int)code : -1;

    /// <summary>The <see cref="ChildProcessRunner.Launch"/> hook: job + BelowNormal (+ EcoQoS for learning / content).</summary>
    public static IChild Start(ProcessStartInfo psi, WorkerSpec spec)
    {
        ArgumentNullException.ThrowIfNull(psi);
        ArgumentNullException.ThrowIfNull(spec);
        var job = new JobObject((long)spec.MemoryMb * 1024 * 1024);
        try
        {
            var child = Create(psi, job);
            if (spec.Kind is not (WorkerKind.Capture or WorkerKind.Shell)) // hosts the user waits on keep normal priority
            {
                SetPriorityClass(child._process, BelowNormalPriorityClass);
                if (spec.Eco || spec.Kind is WorkerKind.Learning or WorkerKind.Content)
                {
                    Native.SetEcoQoS(child._process.DangerousGetHandle(), on: true);
                }
            }

            return child;
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    public Task WaitForExitAsync(CancellationToken cancel) => _exited.Task.WaitAsync(cancel);

    public void KillTree() => _job.Terminate(1);

    public void Dispose()
    {
        _wait.Unregister(null);
        _waitHandle.Dispose();
        Stdin.Dispose();
        Stdout.Dispose();
        Stderr.Dispose();
        _job.Dispose(); // kill-on-close: anything still in the job ends here
        _process.Dispose();
    }

    private static unsafe JobChild Create(ProcessStartInfo psi, JobObject job)
    {
        var exe = Resolve(psi.FileName, psi.WorkingDirectory);
        var commandLine = CommandLine(exe, psi.ArgumentList);
        var environment = EnvironmentBlock(psi.Environment);

        var sa = new SecurityAttributes { Length = sizeof(SecurityAttributes), InheritHandle = 1 };
        nint inRead = 0, inWrite = 0, outRead = 0, outWrite = 0, errRead = 0, errWrite = 0;
        nint attributes = 0;
        var attributesReady = false;
        try
        {
            if (!CreatePipe(out inRead, out inWrite, ref sa, 0) || !CreatePipe(out outRead, out outWrite, ref sa, 0) || !CreatePipe(out errRead, out errWrite, ref sa, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            // Tasktor's ends stay here; only the child's ends may be inherited, and only by this child (handle list).
            SetHandleInformation(inWrite, HandleFlagInherit, 0);
            SetHandleInformation(outRead, HandleFlagInherit, 0);
            SetHandleInformation(errRead, HandleFlagInherit, 0);

            nint size = 0;
            InitializeProcThreadAttributeList(0, 2, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 2, 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            attributesReady = true;
            var inherit = stackalloc nint[3] { inRead, outWrite, errWrite };
            var jobs = stackalloc nint[1] { job.Handle };
            if (!UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributeHandleList, (nint)inherit, 3 * sizeof(nint), 0, 0)
                || !UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributeJobList, (nint)jobs, sizeof(nint), 0, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var si = new StartupInfoEx
            {
                StartupInfo = new StartupInfo { Size = sizeof(StartupInfoEx), Flags = StartfUseStdHandles, StdInput = inRead, StdOutput = outWrite, StdError = errWrite },
                AttributeList = attributes,
            };
            ProcessInformation pi;
            fixed (char* cmd = commandLine)
            fixed (char* env = environment)
            {
                if (!CreateProcessW(exe, cmd, 0, 0, true, ExtendedStartupInfoPresent | CreateUnicodeEnvironment | CreateNoWindow, (nint)env, string.IsNullOrEmpty(psi.WorkingDirectory) ? null : psi.WorkingDirectory, ref si, out pi))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "could not start " + Path.GetFileName(exe));
                }
            }

            CloseHandle(pi.Thread);
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var stdin = new StreamWriter(new FileStream(new SafeFileHandle(inWrite, ownsHandle: true), FileAccess.Write, 4096, isAsync: false), utf8) { AutoFlush = true };
            inWrite = 0;
            var stdout = new StreamReader(new FileStream(new SafeFileHandle(outRead, ownsHandle: true), FileAccess.Read, 4096, isAsync: false), utf8);
            outRead = 0;
            var stderr = new StreamReader(new FileStream(new SafeFileHandle(errRead, ownsHandle: true), FileAccess.Read, 4096, isAsync: false), utf8);
            errRead = 0;
            return new JobChild(job, new SafeProcessHandle(pi.Process, ownsHandle: true), pi.ProcessId, stdin, stdout, stderr);
        }
        finally
        {
            // The child's ends: closed here so the pipes report end-of-file when the child (and its descendants) exit.
            foreach (var h in new[] { inRead, outWrite, errWrite, inWrite, outRead, errRead })
            {
                if (h != 0)
                {
                    CloseHandle(h);
                }
            }

            if (attributesReady)
            {
                DeleteProcThreadAttributeList(attributes);
            }

            if (attributes != 0)
            {
                Marshal.FreeHGlobal(attributes);
            }
        }
    }

    /// <summary>The full path of the exe: as given when rooted, else under the working directory when it names a folder,
    /// else the first match on PATH (".exe" added when it has no extension). The current directory is never searched.</summary>
    internal static string Resolve(string file, string? workingDir)
    {
        if (Path.IsPathRooted(file))
        {
            return file;
        }

        if (file.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) || file.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            return Path.GetFullPath(Path.Combine(string.IsNullOrEmpty(workingDir) ? Environment.CurrentDirectory : workingDir, file));
        }

        var name = Path.HasExtension(file) ? file : file + ".exe";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim('"'), name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new Win32Exception(2, "could not find " + file);
    }

    /// <summary>The command line the way the C runtime parses it back into argv (the rules .NET's own Process uses).</summary>
    internal static string CommandLine(string exe, IEnumerable<string> args)
    {
        var sb = new StringBuilder().Append('"').Append(exe).Append('"');
        foreach (var arg in args)
        {
            sb.Append(' ');
            if (arg.Length > 0 && !arg.Any(c => char.IsWhiteSpace(c) || c == '"'))
            {
                sb.Append(arg);
                continue;
            }

            sb.Append('"');
            for (var i = 0; i < arg.Length;)
            {
                var c = arg[i++];
                if (c == '\\')
                {
                    var n = 1;
                    while (i < arg.Length && arg[i] == '\\')
                    {
                        i++;
                        n++;
                    }

                    if (i == arg.Length)
                    {
                        sb.Append('\\', n * 2);
                    }
                    else if (arg[i] == '"')
                    {
                        sb.Append('\\', (n * 2) + 1).Append('"');
                        i++;
                    }
                    else
                    {
                        sb.Append('\\', n);
                    }

                    continue;
                }

                if (c == '"')
                {
                    sb.Append('\\');
                }

                sb.Append(c);
            }

            sb.Append('"');
        }

        return sb.Append('\0').ToString();
    }

    private static string EnvironmentBlock(IDictionary<string, string?> env)
    {
        var sb = new StringBuilder();
        foreach (var kv in env.Where(kv => kv.Value is not null).OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        }

        return sb.Append('\0').ToString();
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle process) => SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public nint Reserved2;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreatePipe(out nint read, out nint write, ref SecurityAttributes attributes, int size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(nint handle, uint mask, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nint size, nint previous, nint returnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(nint list);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcessW(string? application, char* commandLine, nint processAttributes, nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, string? currentDirectory, ref StartupInfoEx startup, out ProcessInformation info);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint code);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetPriorityClass(SafeProcessHandle process, uint priority);
}
