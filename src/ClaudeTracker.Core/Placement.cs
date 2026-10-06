namespace ClaudeTracker.Core;

/// <summary>An edge of a screen: the one a taskbar stands on.</summary>
public enum ScreenEdge
{
    Left,
    Top,
    Right,
    Bottom,
}

/// <summary>
/// A rectangle on a screen, in pixels. Its right and bottom are the first pixels outside it,
/// as Windows gives them.
/// </summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;
}

/// <summary>
/// Where a window is held on the screen: the pixel one of its corners stays on while its size
/// changes. A window grows from its top-left corner; one that stands on a taskbar at the
/// bottom has to grow from its bottom instead, or it grows off the screen.
/// </summary>
/// <param name="AtRight">The held corner is on the window's right side; else on its left.</param>
/// <param name="AtBottom">The held corner is at the window's bottom; else at its top.</param>
public readonly record struct Pin(int X, int Y, bool AtRight, bool AtBottom)
{
    /// <summary>The window's top-left pixel when it is this wide and this tall.</summary>
    public (int X, int Y) TopLeft(int width, int height) => (AtRight ? X - width : X, AtBottom ? Y - height : Y);
}

/// <summary>
/// Where the popover and the toasts go: beside the taskbar, on whichever edge of the screen
/// it is, and clear of it when it hides itself. The Mac app has none of this — the system
/// places its popover under the menu bar, which is always at the top. Asking Windows where
/// the taskbar is, and moving the windows, is the app project's; the arithmetic is here, so
/// it is tested.
/// </summary>
public static class PopoverPlacement
{
    /// <summary>
    /// The edge on which a screen's work area stops short of the screen: where a bar that is
    /// always showing stands. Null when it stops short nowhere — a screen without a taskbar,
    /// or one whose taskbar hides itself.
    /// </summary>
    public static ScreenEdge? EdgeOf(ScreenRect screen, ScreenRect workArea)
    {
        var sides = new (ScreenEdge Edge, int Short)[]
        {
            (ScreenEdge.Bottom, screen.Bottom - workArea.Bottom),
            (ScreenEdge.Top, workArea.Top - screen.Top),
            (ScreenEdge.Left, workArea.Left - screen.Left),
            (ScreenEdge.Right, screen.Right - workArea.Right),
        };
        var most = sides.MaxBy(side => side.Short);
        return most.Short > 0 ? most.Edge : null;
    }

    /// <summary>
    /// The room there is beside the taskbar: the work area, less the taskbar's strip where the
    /// work area still holds it. Windows counts the strip of a taskbar that hides itself as
    /// room for windows, and the taskbar comes back over whatever stands there.
    /// </summary>
    /// <param name="thickness">How thick the taskbar is, across its edge; 0 when Windows would not say.</param>
    public static ScreenRect Room(ScreenRect screen, ScreenRect workArea, ScreenEdge edge, int thickness)
    {
        thickness = Math.Max(thickness, 0);
        return edge switch
        {
            ScreenEdge.Left => workArea with { Left = Math.Max(workArea.Left, screen.Left + thickness) },
            ScreenEdge.Top => workArea with { Top = Math.Max(workArea.Top, screen.Top + thickness) },
            ScreenEdge.Right => workArea with { Right = Math.Min(workArea.Right, screen.Right - thickness) },
            _ => workArea with { Bottom = Math.Min(workArea.Bottom, screen.Bottom - thickness) },
        };
    }

    /// <summary>
    /// The point to open at when no press says where the tray icon is — a launch, the
    /// keyboard: the end of the screen the tray is at, counted along the taskbar, so that the
    /// window stands flush in the tray's corner as Windows' own do. The tray is at the right
    /// end of a taskbar at the bottom, and at the left end where Windows is laid out from
    /// right to left; at one end or the other of a taskbar on a side.
    /// </summary>
    /// <param name="tray">The notification area's rectangle; null when Windows would not say, and the far end is taken.</param>
    public static int TrayEnd(ScreenEdge edge, ScreenRect screen, ScreenRect? tray)
    {
        if (edge is ScreenEdge.Top or ScreenEdge.Bottom)
        {
            return tray is { } across && across.Left + across.Right < screen.Left + screen.Right ? screen.Left : screen.Right;
        }
        return tray is { } down && down.Top + down.Bottom < screen.Top + screen.Bottom ? screen.Top : screen.Bottom;
    }

    /// <summary>
    /// Where a window this wide is held beside the taskbar: by the corner nearest the tray,
    /// so that it grows away from the bar.
    /// </summary>
    /// <param name="room">What <see cref="Room"/> gave.</param>
    /// <param name="along">
    /// The point to open at, counted along the taskbar: across the screen for one at the top
    /// or the bottom, down it for one on a side. The press on the tray icon, or <see cref="TrayEnd"/>.
    /// </param>
    /// <param name="margin">The gap left between the window and the taskbar, and the screen's edges.</param>
    public static Pin Place(ScreenEdge edge, ScreenRect room, int width, int along, int margin)
    {
        if (edge is ScreenEdge.Top or ScreenEdge.Bottom)
        {
            // Centred on the point and kept inside the room. The right end wins on a screen
            // narrower than the window, which is where the tray is.
            var right = Math.Min(Math.Max(along + width / 2, room.Left + margin + width), room.Right - margin);
            return edge == ScreenEdge.Top
                ? new Pin(right, room.Top + margin, AtRight: true, AtBottom: false)
                : new Pin(right, room.Bottom - margin, AtRight: true, AtBottom: true);
        }

        // A window as tall as its content has no middle to put on the point. It stands in the
        // corner at the point's end of the bar: the tray is at one end, and nothing says which.
        var atBottom = along >= room.Top + room.Height / 2;
        var y = atBottom ? room.Bottom - margin : room.Top + margin;
        return edge == ScreenEdge.Left
            ? new Pin(room.Left + margin, y, AtRight: false, atBottom)
            : new Pin(room.Right - margin, y, AtRight: true, atBottom);
    }

    /// <summary>
    /// Where each toast goes, as its top-left pixel: the first where a popover would be held,
    /// each later one past the one before, away from the taskbar. While the popover shows,
    /// they start past it — both stand in the tray's corner.
    /// </summary>
    /// <param name="from">Where a popover opened at the tray would be held.</param>
    /// <param name="sizes">Each toast's width and height, in the order they were shown.</param>
    /// <param name="taken">The popover's rectangle while it shows.</param>
    public static List<(int X, int Y)> Stack(Pin from, IReadOnlyList<(int Width, int Height)> sizes, int gap, ScreenRect? taken = null)
    {
        var next = from.Y;
        if (taken is { } popover) next = from.AtBottom ? Math.Min(next, popover.Top - gap) : Math.Max(next, popover.Bottom + gap);
        var places = new List<(int X, int Y)>();
        foreach (var (width, height) in sizes)
        {
            places.Add((from.AtRight ? from.X - width : from.X, from.AtBottom ? next - height : next));
            next += from.AtBottom ? -(height + gap) : height + gap;
        }
        return places;
    }
}
