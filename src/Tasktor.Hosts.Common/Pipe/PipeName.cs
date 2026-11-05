using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Tasktor.Hosts.Common.Pipe;

/// <summary>
/// <c>tasktor-&lt;hash of the user's SID&gt;</c> (U1 D-H3). On other OSes (tests, CI) the user name stands in for the SID.
/// <c>TASKTOR_PIPE</c> overrides it (tests run several agents side by side).
/// </summary>
public static class PipeName
{
    public static string ForCurrentUser()
    {
        if (Environment.GetEnvironmentVariable("TASKTOR_PIPE") is { Length: > 0 } custom)
        {
            return custom;
        }

        var who = OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName : Environment.UserName;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(who));
        return "tasktor-" + Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }
}
