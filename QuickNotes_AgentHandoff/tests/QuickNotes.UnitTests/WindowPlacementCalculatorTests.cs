using QuickNotes.Desktop.Windows;
using QuickNotes.Domain;

namespace QuickNotes.UnitTests;

public sealed class WindowPlacementCalculatorTests
{
    private static readonly WindowWorkArea Primary =
        new(0, 0, 1920, 1080, "DISPLAY1", true);
    private static readonly WindowWorkArea Secondary =
        new(-1280, 0, 1280, 1024, "DISPLAY2", false);

    [Fact]
    public void NoLayout_CreatesDefaultInsidePrimary()
    {
        var placement = WindowPlacementCalculator.EnsureVisible(
            null,
            new[] { Primary });

        Assert.Equal(WindowPlacementCalculator.DefaultWidth, placement.Width);
        Assert.Equal(WindowPlacementCalculator.DefaultHeight, placement.Height);
        Assert.InRange(placement.Left, Primary.Left, Primary.Right - placement.Width);
        Assert.InRange(placement.Top, Primary.Top, Primary.Bottom - placement.Height);
    }

    [Fact]
    public void VisibleLayout_IsPreserved()
    {
        var layout = new NoteWindowLayout { Left = 100, Top = 120, Width = 360, Height = 420 };

        var placement = WindowPlacementCalculator.EnsureVisible(layout, new[] { Primary });

        Assert.Equal(layout.Left, placement.Left);
        Assert.Equal(layout.Top, placement.Top);
        Assert.Equal(layout.Width, placement.Width);
        Assert.Equal(layout.Height, placement.Height);
    }

    [Fact]
    public void LayoutOnRemovedDisplay_IsMovedToNearestAvailableArea()
    {
        var layout = new NoteWindowLayout { Left = 5000, Top = 5000, Width = 360, Height = 420 };

        var placement = WindowPlacementCalculator.EnsureVisible(layout, new[] { Primary, Secondary });

        Assert.True(placement.Left + placement.Width <= Primary.Right);
        Assert.True(placement.Top + placement.Height <= Primary.Bottom);
        Assert.True(placement.Left >= Primary.Left);
        Assert.True(placement.Top >= Primary.Top);
    }

    [Fact]
    public void NegativeCoordinatesOnLeftMonitor_AreAllowedAndClamped()
    {
        var layout = new NoteWindowLayout { Left = -1200, Top = 100, Width = 360, Height = 420 };

        var placement = WindowPlacementCalculator.EnsureVisible(
            layout,
            new[] { Primary, Secondary },
            preferredDisplayId: "DISPLAY2");

        Assert.Equal(-1200, placement.Left);
        Assert.Equal(100, placement.Top);
    }

    [Fact]
    public void OversizedLayout_IsReducedToWorkArea()
    {
        var layout = new NoteWindowLayout { Left = 10, Top = 10, Width = 5000, Height = 4000 };

        var placement = WindowPlacementCalculator.EnsureVisible(layout, new[] { Primary });

        Assert.True(placement.Width <= Primary.Width - 2 * WindowPlacementCalculator.EdgeMargin);
        Assert.True(placement.Height <= Primary.Height - 2 * WindowPlacementCalculator.EdgeMargin);
    }

    [Fact]
    public void FindDisplayId_UsesWindowCenter()
    {
        var onSecondary = new NoteWindowLayout { Left = -1200, Top = 100, Width = 360, Height = 420 };
        var onPrimary = new NoteWindowLayout { Left = 100, Top = 100, Width = 360, Height = 420 };

        Assert.Equal(
            "DISPLAY2",
            WindowPlacementCalculator.FindDisplayId(onSecondary, new[] { Primary, Secondary }));
        Assert.Equal(
            "DISPLAY1",
            WindowPlacementCalculator.FindDisplayId(onPrimary, new[] { Primary, Secondary }));
    }
}
