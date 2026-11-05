using Tasktor.Contracts.Platform;

namespace Tasktor.Core.Features.System;

/// <summary>
/// A5.1 free space per fixed volume through <see cref="DriveInfo"/> (on Windows: <c>GetDiskFreeSpaceEx</c> on fixed drives with
/// a letter). Removable and network drives are left out; a drive that disappears or is locked is skipped without error.
/// </summary>
public sealed class DriveVolumeSource : IMetricSource
{
    public string Name => "volumes";

    public void Read(MetricFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady || d.TotalSize <= 0)
                {
                    continue;
                }

                var id = OperatingSystem.IsWindows() ? d.Name.TrimEnd('\\') : d.Name;
                if (!OperatingSystem.IsWindows() && id != "/")
                {
                    continue; // Linux: the root file system only (development builds)
                }

                frame.Set(new MetricKey(MetricIds.VolumeFreeBytes, id), d.TotalFreeSpace);
                frame.Set(new MetricKey(MetricIds.VolumeTotalBytes, id), d.TotalSize);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>Several metric sources read as one (the tick reads PDH + volumes in one go).</summary>
public sealed class CompositeMetricSource(params IMetricSource[] sources) : IMetricSource
{
    public string Name => "composite";

    public void Read(MetricFrame frame)
    {
        foreach (var s in sources)
        {
            s.Read(frame);
        }
    }
}
