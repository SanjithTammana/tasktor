namespace Tasktor.Hosts.Common.Shelf;

/// <summary>A rectangle in physical pixels, as Windows reports monitors and work areas.</summary>
public readonly record struct PxRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;
}

/// <summary>Where the taskbar sits on a monitor; <see cref="Hidden"/> covers auto-hide (the work area is the monitor).</summary>
public enum TaskbarEdge
{
    Bottom,
    Top,
    Left,
    Right,
    Hidden,
}

/// <summary>The edge strip at rest and the shelf when open, both in physical pixels.</summary>
public sealed record ShelfPlacement(TaskbarEdge Taskbar, PxRect Edge, PxRect Shelf);

/// <summary>
/// U4 amended D-G10/D-G11 geometry. The edge is a strip <see cref="EdgeDip"/> high on the taskbar's inner edge, across
/// the monitor; the open shelf spans the monitor and grows from the taskbar into the work area, as tall as its content
/// but never more than half the work area. With a vertical taskbar both sit on the bottom of the work area; with
/// auto-hide, on the bottom of the monitor.
/// </summary>
public static class ShelfLayout
{
    public const int EdgeDip = 6;

    public static TaskbarEdge TaskbarOf(PxRect monitor, PxRect work)
    {
        if (work.Bottom < monitor.Bottom)
        {
            return TaskbarEdge.Bottom;
        }

        if (work.Y > monitor.Y)
        {
            return TaskbarEdge.Top;
        }

        if (work.X > monitor.X)
        {
            return TaskbarEdge.Left;
        }

        return work.Right < monitor.Right ? TaskbarEdge.Right : TaskbarEdge.Hidden;
    }

    public static ShelfPlacement Place(PxRect monitor, PxRect work, double scale, double contentDip)
    {
        if (scale <= 0 || double.IsNaN(scale))
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }

        var taskbar = TaskbarOf(monitor, work);
        var edge = Math.Max(1, (int)Math.Round(EdgeDip * scale));
        var height = Math.Clamp((int)Math.Ceiling(Math.Max(0, contentDip) * scale), edge, Math.Max(edge, work.Height / 2));
        return taskbar switch
        {
            TaskbarEdge.Bottom => new(taskbar, new PxRect(monitor.X, work.Bottom, monitor.Width, edge), new PxRect(monitor.X, work.Bottom - height, monitor.Width, height)),
            TaskbarEdge.Top => new(taskbar, new PxRect(monitor.X, work.Y - edge, monitor.Width, edge), new PxRect(monitor.X, work.Y, monitor.Width, height)),
            TaskbarEdge.Hidden => new(taskbar, new PxRect(monitor.X, monitor.Bottom - edge, monitor.Width, edge), new PxRect(monitor.X, monitor.Bottom - height, monitor.Width, height)),
            _ => new(taskbar, new PxRect(work.X, work.Bottom - edge, work.Width, edge), new PxRect(work.X, work.Bottom - height, work.Width, height)),
        };
    }
}
