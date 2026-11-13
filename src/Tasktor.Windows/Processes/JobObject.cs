using System.Diagnostics;
using System.Runtime.InteropServices;
using Tasktor.Contracts;

namespace Tasktor.Windows.Processes;

/// <summary>
/// A kill-on-close Job Object with a memory cap and no breakaway (U1 D-H7, D-H9 condition 2). Closing the handle, or the
/// agent dying, kills every process in the job.
/// </summary>
public sealed partial class JobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const int JobObjectCpuRateControlInformation = 15;
    private const uint LimitProcessMemory = 0x100;
    private const uint LimitKillOnJobClose = 0x2000;
    private const uint CpuRateControlEnable = 0x1;
    private const uint CpuRateControlHardCap = 0x4;
    private const uint JobObjectSetAttributes = 0x2;
    private const uint JobObjectQuery = 0x4;

    private nint _handle;

    public JobObject(long memoryLimitBytes, bool killOnClose = true, string? name = null)
    {
        _handle = CreateJobObjectW(0, name);
        if (_handle == 0)
        {
            throw new InvalidOperationException("CreateJobObject failed: " + Marshal.GetLastPInvokeError());
        }

        var info = new ExtendedLimitInformation
        {
            Basic = new BasicLimitInformation { LimitFlags = (killOnClose ? LimitKillOnJobClose : 0) | (memoryLimitBytes > 0 ? LimitProcessMemory : 0) },
            ProcessMemoryLimit = (nuint)Math.Max(0, memoryLimitBytes),
        };
        if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            var err = Marshal.GetLastPInvokeError();
            Dispose();
            throw new InvalidOperationException("SetInformationJobObject failed: " + err);
        }
    }

    /// <summary>Give <paramref name="process"/> a query-only handle to this job. A job's name lasts only while some handle to
    /// it is open; with one in each capped process the name lives exactly as long as the cap does, so an agent restarted
    /// after a crash can still open the job by name and lift the cap (F-PC5).</summary>
    public bool ShareWith(nint process) =>
        DuplicateHandle(GetCurrentProcess(), _handle, process, out _, JobObjectQuery, false, 0);

    /// <summary>Close this agent's handle without lifting the cap, as a crash would.</summary>
    internal void Abandon() => Dispose();

    /// <summary>The job handle, for <c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c> (<see cref="JobChild"/>).</summary>
    internal nint Handle => _handle;

    /// <summary>End every process in the job.</summary>
    public bool Terminate(uint exitCode) => _handle != 0 && TerminateJobObject(_handle, exitCode);

    public bool Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return AssignProcessToJobObject(_handle, process.Handle);
    }

    /// <summary>Turn the CPU cap off (caps' jobs are not kill-on-close, so closing them leaves the processes running).</summary>
    public bool ReleaseCap()
    {
        var info = new CpuRateControl { ControlFlags = 0, CpuRate = 0 };
        return SetCpuRate(_handle, JobObjectCpuRateControlInformation, ref info, Marshal.SizeOf<CpuRateControl>());
    }

    /// <summary>Turn off the CPU cap of a named job another run of the agent created. False when no such job exists (its
    /// processes have all exited) or it cannot be opened.</summary>
    public static bool ReleaseNamed(string name)
    {
        var h = OpenJobObjectW(JobObjectSetAttributes, false, name);
        if (h == 0)
        {
            return false;
        }

        try
        {
            var info = new CpuRateControl { ControlFlags = 0, CpuRate = 0 };
            return SetCpuRate(h, JobObjectCpuRateControlInformation, ref info, Marshal.SizeOf<CpuRateControl>());
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>The CPU rate a named job enforces (0 = none); null when no such job exists.</summary>
    public static uint? CpuRate(string name)
    {
        var h = OpenJobObjectW(JobObjectQuery, false, name);
        if (h == 0)
        {
            return null;
        }

        try
        {
            var info = default(CpuRateControl);
            return QueryCpuRate(h, JobObjectCpuRateControlInformation, ref info, Marshal.SizeOf<CpuRateControl>(), 0) && (info.ControlFlags & CpuRateControlEnable) != 0 ? info.CpuRate : 0;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>B3.5 / I4 core cap: a hard CPU rate = cores / logical CPUs of the whole machine.</summary>
    public bool CapCores(int maxCores)
    {
        var rate = (uint)Math.Clamp(maxCores * 10000 / Math.Max(1, Environment.ProcessorCount), 1, 10000);
        var info = new CpuRateControl { ControlFlags = CpuRateControlEnable | CpuRateControlHardCap, CpuRate = rate };
        return SetCpuRate(_handle, JobObjectCpuRateControlInformation, ref info, Marshal.SizeOf<CpuRateControl>());
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            CloseHandle(_handle);
            _handle = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CpuRateControl
    {
        public uint ControlFlags;
        public uint CpuRate;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenJobObjectW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimitInformation info, int length);

    [LibraryImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCpuRate(nint job, int infoClass, ref CpuRateControl info, int length);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryCpuRate(nint job, int infoClass, ref CpuRateControl info, int length, nint returned);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(nint job, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateHandle(nint sourceProcess, nint source, nint targetProcess, out nint target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
}
