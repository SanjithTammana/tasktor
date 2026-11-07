using System.Runtime.InteropServices;

namespace Tasktor.Windows.Processes;

/// <summary>Small Win32 helpers shared by the process adapters.</summary>
internal static partial class Native
{
    private const int ProcessPowerThrottling = 4;
    private const uint ThrottleExecutionSpeed = 0x1;

    /// <summary>EcoQoS on / off: <c>SetProcessInformation(ProcessPowerThrottling, EXECUTION_SPEED)</c> (A3.4).</summary>
    public static bool SetEcoQoS(nint process, bool on)
    {
        var state = new PowerThrottlingState { Version = 1, ControlMask = ThrottleExecutionSpeed, StateMask = on ? ThrottleExecutionSpeed : 0 };
        return SetProcessInformation(process, ProcessPowerThrottling, ref state, Marshal.SizeOf<PowerThrottlingState>());
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessInformation(nint process, int infoClass, ref PowerThrottlingState info, int size);
}
