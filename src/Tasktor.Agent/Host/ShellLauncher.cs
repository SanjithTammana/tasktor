using System.Diagnostics;

namespace Tasktor.Agent.Host;

/// <summary>
/// Opens URIs, files and folders with the shell (A3.5 open file location, A5.4 Explorer / Storage settings, E2 Join links).
/// <c>select:&lt;path&gt;</c> opens Explorer with the file selected. A click from the user is the only trigger.
/// </summary>
internal static class ShellLauncher
{
    public static bool Launch(string target)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        try
        {
            if (target.StartsWith("edit:", StringComparison.Ordinal))
            {
                // The file's "edit" verb; a .json without one opens in Notepad (an editor, never a browser).
                var file = target[5..];
                try
                {
                    using var edit = Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, Verb = "edit" });
                    return true;
                }
                catch (System.ComponentModel.Win32Exception) when (OperatingSystem.IsWindows())
                {
                    using var notepad = Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { file }, UseShellExecute = false });
                    return notepad is not null;
                }
            }

            if (target.StartsWith("select:", StringComparison.Ordinal))
            {
                var path = target[7..];
                if (OperatingSystem.IsWindows())
                {
                    using var p = Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select," + path }, UseShellExecute = false });
                    return p is not null;
                }

                target = Path.GetDirectoryName(path) ?? path;
            }

            using var proc = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
