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

    private nint _handle;

    public JobObject(long memoryLimitBytes, bool killOnClose = true)
    {
        _handle = CreateJobObjectW(0, null);
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

    public bool Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return AssignProcessToJobObject(_handle, process.Handle);
    }

    /// <summary>B3.5 / I4 core cap: a hard CPU rate = cores / logical CPUs of the whole machine.</summary>
    /// <summary>Turn the CPU cap off (caps' jobs are not kill-on-close, so closing them leaves the processes running).</summary>
    public bool ReleaseCap()
    {
        var info = new CpuRateControl { ControlFlags = 0, CpuRate = 0 };
        return SetCpuRate(_handle, JobObjectCpuRateControlInformation, ref info, Marshal.SizeOf<CpuRateControl>());
    }

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

    /// <summary>The <see cref="Core.Plugins.ChildProcessRunner.AfterStart"/> hook: job + BelowNormal (+ EcoQoS for learning / content).</summary>
    public static IDisposable? ForWorker(Process process, WorkerSpec spec)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(spec);
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var job = new JobObject((long)spec.MemoryMb * 1024 * 1024);
        if (!job.Assign(process))
        {
            job.Dispose();
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Could not put the worker in a Job Object");
        }

        if (spec.Kind != WorkerKind.Capture)
        {
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (InvalidOperationException)
            {
            }

            if (spec.Eco || spec.Kind is WorkerKind.Learning or WorkerKind.Content)
            {
                Native.SetEcoQoS(process.Handle, on: true);
            }
        }

        return job;
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimitInformation info, int length);

    [LibraryImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCpuRate(nint job, int infoClass, ref CpuRateControl info, int length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
