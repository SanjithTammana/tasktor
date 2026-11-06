namespace Tasktor.Tests.Shared;

/// <summary>Directory links for tests that check the scanners never follow one.</summary>
internal static class Links
{
    /// <summary>
    /// A directory link back to <paramref name="target"/>: a symbolic link where the account may create one (Linux, or
    /// Windows with Developer Mode or admin), else an NTFS junction, which any Windows account can make and which is
    /// the loop users actually meet. Either way the scanner must not follow it.
    /// </summary>
    public static void Directory(string link, string target)
    {
        try
        {
            System.IO.Directory.CreateSymbolicLink(link, target);
            return;
        }
        catch (Exception e) when (OperatingSystem.IsWindows() && e is IOException or UnauthorizedAccessException)
        {
            // ERROR_PRIVILEGE_NOT_HELD (1314): fall through to a junction.
        }

        using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", link, target },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;
        mklink.WaitForExit();
        Assert.True(mklink.ExitCode == 0 && System.IO.Directory.Exists(link), "could not create a junction for the loop");
        Assert.True((new DirectoryInfo(link).Attributes & FileAttributes.ReparsePoint) != 0);
    }
}
