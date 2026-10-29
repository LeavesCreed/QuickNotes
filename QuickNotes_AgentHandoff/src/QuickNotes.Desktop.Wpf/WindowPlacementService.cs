using System.Runtime.InteropServices;
using System.Windows;
using QuickNotes.Desktop.Windows;
using QuickNotes.Domain;
using Forms = System.Windows.Forms;

namespace QuickNotes.Desktop.Wpf;

public sealed class WindowPlacementService
{
    private const int MonitorDefaultToNearest = 2;

    public IReadOnlyList<WindowWorkArea> GetWorkAreas()
    {
        var areas = new List<WindowWorkArea>();
        foreach (var screen in Forms.Screen.AllScreens)
        {
            var bounds = screen.Bounds;
            var center = new NativePoint
            {
                X = bounds.Left + bounds.Width / 2,
                Y = bounds.Top + bounds.Height / 2
            };
            var monitor = MonitorFromPoint(center, MonitorDefaultToNearest);
            var scale = GetDpiScale(monitor);
            areas.Add(new WindowWorkArea(
                bounds.Left / scale,
                bounds.Top / scale,
                bounds.Width / scale,
                bounds.Height / scale,
                screen.DeviceName,
                screen.Primary));
        }

        return areas;
    }

    public WindowPlacement ApplyToWindow(
        Window window,
        NoteWindowLayout? layout,
        string? preferredDisplayId = null,
        int cascadeIndex = 0)
    {
        var placement = WindowPlacementCalculator.EnsureVisible(
            layout,
            GetWorkAreas(),
            preferredDisplayId,
            cascadeIndex);
        window.Left = placement.Left;
        window.Top = placement.Top;
        window.Width = placement.Width;
        window.Height = placement.Height;
        return placement;
    }

    public string? GetDisplayId(Window window)
    {
        if (double.IsNaN(window.Left)
            || double.IsNaN(window.Top)
            || double.IsNaN(window.Width)
            || double.IsNaN(window.Height))
        {
            return null;
        }

        var center = new NativePoint
        {
            X = (int)(window.Left + window.Width / 2),
            Y = (int)(window.Top + window.Height / 2)
        };
        var monitor = MonitorFromPoint(center, MonitorDefaultToNearest);
        foreach (var screen in Forms.Screen.AllScreens)
        {
            var bounds = screen.Bounds;
            var scale = GetDpiScale(monitor);
            var left = bounds.Left / scale;
            var top = bounds.Top / scale;
            if (center.X >= left
                && center.X <= left + bounds.Width / scale
                && center.Y >= top
                && center.Y <= top + bounds.Height / scale)
            {
                return screen.DeviceName;
            }
        }

        return null;
    }

    private static double GetDpiScale(nint monitor)
    {
        if (monitor == nint.Zero)
        {
            return 1.0;
        }

        try
        {
            var result = GetDpiForMonitor(monitor, 0, out var dpiX, out _);
            return result == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;
        }
        catch (DllNotFoundException)
        {
            return 1.0;
        }
        catch (EntryPointNotFoundException)
        {
            return 1.0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, int flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        nint hmonitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);
}
