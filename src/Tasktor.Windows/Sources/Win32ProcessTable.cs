using System.Runtime.InteropServices;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Snapshot;

namespace Tasktor.Windows.Sources;

/// <summary>
/// The process table (A2, B D-B2): one Toolhelp snapshot gives exe, PID, parent and thread count with no handle
/// (names-only, the 60 s scan); per-process handles (<c>PROCESS_QUERY_LIMITED_INFORMATION</c>, not on the protected
/// deny list) are opened only when the lease asks for usage, identity or I/O columns. A process that exits mid-read is
/// dropped silently. <see cref="ReadOne"/> is the fresh re-read the A3 guards use at action time.
/// </summary>
public sealed partial class Win32ProcessTable : ITableSource<ProcKey, ProcessRow>
{
    private const uint Th32csSnapProcess = 0x2;
    private const uint ProcessQueryLimited = 0x1000;
    private const uint TokenQuery = 0x8;
    private static readonly nint InvalidHandle = -1;

    private readonly byte[]? _ownSid = OperatingSystem.IsWindows() ? CurrentUserSid() : null;

    public string Name => "toolhelp";

    /// <summary>GPU % per PID (A2.3), from the PDH source's last read.</summary>
    public Func<uint, double?>? GpuByPid { get; set; }

    public void Read(ICollection<KeyValuePair<ProcKey, ProcessRow>> into, ColumnSet columns)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var snap = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snap == InvalidHandle)
        {
            return;
        }

        try
        {
            var entry = new ProcessEntry32W { Size = (uint)Marshal.SizeOf<ProcessEntry32W>() };
            if (!Process32FirstW(snap, ref entry))
            {
                return;
            }

            do
            {
                var exe = entry.GetExeName();
                var pid = entry.ProcessId;
                ProcessRow row;
                if ((columns & (ColumnSet.Usage | ColumnSet.Identity | ColumnSet.Io)) == 0 || pid is 0 or 4)
                {
                    row = new ProcessRow { Key = new ProcKey(pid, 0), ParentPid = entry.ParentProcessId, Exe = exe, Threads = (int)entry.Threads, OwnUser = pid is not (0 or 4) };
                }
                else if (Query(pid, exe, entry.ParentProcessId, (int)entry.Threads, columns) is { } r)
                {
                    row = r;
                }
                else
                {
                    // No handle (elevated / another user / protected): listed by name only.
                    row = new ProcessRow { Key = new ProcKey(pid, 0), ParentPid = entry.ParentProcessId, Exe = exe, Threads = (int)entry.Threads, OwnUser = false, Elevated = true };
                }

                into.Add(new KeyValuePair<ProcKey, ProcessRow>(row.Key, row));
            }
            while (Process32NextW(snap, ref entry));
        }
        finally
        {
            CloseHandle(snap);
        }
    }

    /// <summary>A fresh read of one process (guards' identity re-check). Null when it is gone.</summary>
    public ProcessRow? ReadOne(ProcKey key)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var h = OpenProcess(ProcessQueryLimited, false, key.Pid);
        if (h == 0)
        {
            return null;
        }

        try
        {
            if (!GetProcessTimes(h, out var created, out var exit, out _, out _) || (exit != 0 && GetExitCodeProcess(h, out var code) && code != 259))
            {
                return null;
            }

            var path = ImagePath(h);
            return Fill(h, new ProcessRow { Key = new ProcKey(key.Pid, created), Exe = Path.GetFileName(path ?? "?"), Path = path }, ColumnSet.All);
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private ProcessRow? Query(uint pid, string exe, uint parent, int threads, ColumnSet columns)
    {
        var h = OpenProcess(ProcessQueryLimited, false, pid);
        if (h == 0)
        {
            return null;
        }

        try
        {
            if (!GetProcessTimes(h, out var created, out _, out _, out _))
            {
                return null;
            }

            var row = new ProcessRow { Key = new ProcKey(pid, created), ParentPid = parent, Exe = exe, Threads = threads };
            return Fill(h, row, columns);
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private ProcessRow Fill(nint h, ProcessRow row, ColumnSet columns)
    {
        if ((columns & ColumnSet.Usage) != 0)
        {
            GetProcessTimes(h, out _, out _, out var kernel, out var user);
            var mem = new ProcessMemoryCountersEx { Size = (uint)Marshal.SizeOf<ProcessMemoryCountersEx>() };
            var priv = GetProcessMemoryInfo(h, ref mem, mem.Size) ? (double)mem.PrivateUsage : 0;
            row = row with
            {
                CpuTimeS = (kernel + user) / 1e7,
                PrivateBytes = priv,
                Priority = MapPriority(GetPriorityClass(h)),
                GpuPct = GpuByPid?.Invoke(row.Key.Pid),
            };
        }

        if ((columns & ColumnSet.Io) != 0 && GetProcessIoCounters(h, out var io))
        {
            row = row with { IoBytes = io.ReadTransferCount + io.WriteTransferCount };
        }

        if ((columns & ColumnSet.Identity) != 0)
        {
            var (elevated, own) = TokenInfo(h);
            row = row with { Path = row.Path ?? ImagePath(h), Elevated = elevated, OwnUser = own, Protected = IsProtected(h) };
        }

        return row;
    }

    private (bool Elevated, bool Own) TokenInfo(nint process)
    {
        if (!OpenProcessToken(process, TokenQuery, out var token))
        {
            return (true, false);
        }

        try
        {
            var elevated = GetTokenInformation(token, 20 /* TokenElevation */, out int elev, 4, out _) && elev != 0;
            var own = _ownSid is not null && TokenSid(token) is { } sid && sid.AsSpan().SequenceEqual(_ownSid);
            return (elevated, own);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static unsafe byte[]? TokenSid(nint token)
    {
        GetTokenInformationRaw(token, 1 /* TokenUser */, null, 0, out var needed);
        if (needed == 0)
        {
            return null;
        }

        var buf = new byte[needed];
        fixed (byte* p = buf)
        {
            if (!GetTokenInformationRaw(token, 1, p, needed, out _))
            {
                return null;
            }

            var sid = *(nint*)p; // TOKEN_USER.User.Sid
            var len = GetLengthSid(sid);
            var copy = new byte[len];
            Marshal.Copy(sid, copy, 0, (int)len);
            return copy;
        }
    }

    private static byte[]? CurrentUserSid()
    {
        var self = GetCurrentProcess();
        if (!OpenProcessToken(self, TokenQuery, out var token))
        {
            return null;
        }

        try
        {
            return TokenSid(token);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static unsafe bool IsProtected(nint h)
    {
        // ProcessProtectionLevelInfo (7): PROTECTION_LEVEL_NONE = 0xFFFFFFFE.
        uint level = 0xFFFFFFFE;
        return GetProcessInformation(h, 7, &level, 4) && level != 0xFFFFFFFE;
    }

    private static unsafe string? ImagePath(nint h)
    {
        var buf = stackalloc char[1024];
        var size = 1024u;
        return QueryFullProcessImageNameW(h, 0, buf, ref size) ? new string(buf, 0, (int)size) : null;
    }

    private static ProcessPriority MapPriority(uint c) => c switch
    {
        0x40 => ProcessPriority.Idle,
        0x4000 => ProcessPriority.BelowNormal,
        0x20 => ProcessPriority.Normal,
        0x8000 => ProcessPriority.AboveNormal,
        0x80 => ProcessPriority.High,
        0x100 => ProcessPriority.Realtime,
        _ => ProcessPriority.Unknown,
    };

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ProcessEntry32W
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriClassBase;
        public uint Flags;
        public fixed char ExeFile[260];

        public string GetExeName()
        {
            fixed (char* p = ExeFile)
            {
                return new string(p);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateToolhelp32Snapshot(uint flags, uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32FirstW(nint snapshot, ref ProcessEntry32W entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32NextW(nint snapshot, ref ProcessEntry32W entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint h);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint h, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(nint h, out uint code);

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMemoryInfo(nint h, ref ProcessMemoryCountersEx counters, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessIoCounters(nint h, out IoCounters counters);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetPriorityClass(nint h);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetProcessInformation(nint h, int infoClass, void* info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageNameW(nint h, uint flags, char* name, ref uint size);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(nint token, int infoClass, out int info, int length, out int returned);

    [LibraryImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetTokenInformationRaw(nint token, int infoClass, byte* info, int length, out int returned);

    [LibraryImport("advapi32.dll")]
    private static partial uint GetLengthSid(nint sid);
}
