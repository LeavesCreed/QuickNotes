using QuickNotes.Domain;

namespace QuickNotes.Desktop.Windows;

public sealed record WindowWorkArea(
    double Left,
    double Top,
    double Width,
    double Height,
    string DisplayId,
    bool IsPrimary)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

public sealed record WindowPlacement(double Left, double Top, double Width, double Height);

public static class WindowPlacementCalculator
{
    public const double DefaultWidth = 360;
    public const double DefaultHeight = 420;
    public const double MinimumWidth = 220;
    public const double MinimumHeight = 180;
    public const double EdgeMargin = 24;
    public const double VisibleMargin = 48;

    public static WindowPlacement EnsureVisible(
        NoteWindowLayout? layout,
        IReadOnlyList<WindowWorkArea> workAreas,
        string? preferredDisplayId = null,
        int cascadeIndex = 0)
    {
        if (workAreas.Count == 0)
        {
            return new WindowPlacement(
                layout?.Left ?? EdgeMargin,
                layout?.Top ?? EdgeMargin,
                Math.Max(MinimumWidth, layout?.Width ?? DefaultWidth),
                Math.Max(MinimumHeight, layout?.Height ?? DefaultHeight));
        }

        var area = SelectArea(layout, workAreas, preferredDisplayId);
        var maxWidth = Math.Max(MinimumWidth, area.Width - 2 * EdgeMargin);
        var maxHeight = Math.Max(MinimumHeight, area.Height - 2 * EdgeMargin);
        var width = Math.Clamp(layout?.Width ?? DefaultWidth, MinimumWidth, maxWidth);
        var height = Math.Clamp(layout?.Height ?? DefaultHeight, MinimumHeight, maxHeight);
        var cascade = Math.Max(0, cascadeIndex) % 10 * 24;
        var desiredLeft = layout?.Left ?? area.Left + EdgeMargin + cascade;
        var desiredTop = layout?.Top ?? area.Top + EdgeMargin + cascade;
        var minLeft = area.Left + EdgeMargin;
        var maxLeft = Math.Max(minLeft, area.Right - width - EdgeMargin);
        var minTop = area.Top + EdgeMargin;
        var maxTop = Math.Max(minTop, area.Bottom - height - EdgeMargin);
        return new WindowPlacement(
            Math.Clamp(desiredLeft, minLeft, maxLeft),
            Math.Clamp(desiredTop, minTop, maxTop),
            width,
            height);
    }

    public static string? FindDisplayId(
        NoteWindowLayout layout,
        IReadOnlyList<WindowWorkArea> workAreas)
    {
        if (workAreas.Count == 0)
        {
            return null;
        }

        var centerX = layout.Left + layout.Width / 2;
        var centerY = layout.Top + layout.Height / 2;
        return workAreas
            .FirstOrDefault(area =>
                centerX >= area.Left
                && centerX < area.Right
                && centerY >= area.Top
                && centerY < area.Bottom)?.DisplayId;
    }

    private static WindowWorkArea SelectArea(
        NoteWindowLayout? layout,
        IReadOnlyList<WindowWorkArea> workAreas,
        string? preferredDisplayId)
    {
        if (preferredDisplayId is not null)
        {
            var preferred = workAreas.FirstOrDefault(area => area.DisplayId == preferredDisplayId);
            if (preferred is not null)
            {
                return preferred;
            }
        }

        if (layout is not null)
        {
            var intersecting = workAreas
                .Select(area => (Area: area, Intersection: Intersection(layout, area)))
                .Where(pair => pair.Intersection >= VisibleMargin)
                .OrderByDescending(pair => pair.Intersection)
                .FirstOrDefault();
            if (intersecting.Area is not null)
            {
                return intersecting.Area;
            }

            var centerX = layout.Left + layout.Width / 2;
            var centerY = layout.Top + layout.Height / 2;
            return workAreas
                .OrderBy(area => DistanceSquared(centerX, centerY, area.Left + area.Width / 2, area.Top + area.Height / 2))
                .First();
        }

        return workAreas.FirstOrDefault(area => area.IsPrimary) ?? workAreas[0];
    }

    private static double Intersection(NoteWindowLayout layout, WindowWorkArea area)
    {
        var width = Math.Min(layout.Left + layout.Width, area.Right) - Math.Max(layout.Left, area.Left);
        var height = Math.Min(layout.Top + layout.Height, area.Bottom) - Math.Max(layout.Top, area.Top);
        return width <= 0 || height <= 0 ? 0 : width * height;
    }

    private static double DistanceSquared(double x1, double y1, double x2, double y2)
    {
        var dx = x1 - x2;
        var dy = y1 - y2;
        return dx * dx + dy * dy;
    }
}
