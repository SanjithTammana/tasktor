using System.Text.Json;

namespace Tasktor.Core.Features.Windows;

/// <summary>Reads the live frozen-job intent file; an unreadable file blocks moves rather than assuming it is empty.</summary>
public static class FrozenWindowGuard
{
    public static bool IsFrozen(string path, uint pid)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (!document.RootElement.TryGetProperty("jobs", out var jobs) || jobs.ValueKind != JsonValueKind.Array) return true;
            foreach (var job in jobs.EnumerateArray())
            {
                if (!job.TryGetProperty("processes", out var processes) || processes.ValueKind != JsonValueKind.Array) return true;
                foreach (var process in processes.EnumerateArray())
                {
                    if (!process.TryGetProperty("pid", out var value) || !value.TryGetUInt32(out var candidate)) return true;
                    if (candidate == pid) return true;
                }
            }
            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return true;
        }
    }
}
