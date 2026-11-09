using Microsoft.Win32;
using Tasktor.Core.Platform;

namespace Tasktor.Windows.Platform;

/// <summary>The <see cref="LogonStart"/> value in <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> (per user, no admin).</summary>
public static class RunKeyLogonStart
{
    public static LogonStart Create() => new(Read, Write, Delete);

    private static string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(LogonStart.RunKey, writable: false);
        return key?.GetValue(LogonStart.ValueName) as string;
    }

    private static void Write(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(LogonStart.RunKey, writable: true);
        key.SetValue(LogonStart.ValueName, command, RegistryValueKind.String);
    }

    private static void Delete()
    {
        using var key = Registry.CurrentUser.OpenSubKey(LogonStart.RunKey, writable: true);
        key?.DeleteValue(LogonStart.ValueName, throwOnMissingValue: false);
    }
}
