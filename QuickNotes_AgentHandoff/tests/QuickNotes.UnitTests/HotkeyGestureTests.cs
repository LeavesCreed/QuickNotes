using QuickNotes.Desktop.Hotkeys;

namespace QuickNotes.UnitTests;

public sealed class HotkeyGestureTests
{
    [Fact]
    public void Parse_DefaultGestures_NormalizesDisplay()
    {
        var gesture = HotkeyGesture.Parse("ctrl + alt + n");

        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt, gesture.Modifiers);
        Assert.Equal((uint)'N', gesture.VirtualKey);
        Assert.Equal("Ctrl+Alt+N", gesture.Display);
    }

    [Fact]
    public void Parse_SupportsFunctionKeysAndMultipleModifiers()
    {
        var gesture = HotkeyGesture.Parse("Ctrl+Shift+F12");

        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Shift, gesture.Modifiers);
        Assert.Equal(0x7Bu, gesture.VirtualKey);
        Assert.Equal("Ctrl+Shift+F12", gesture.Display);
    }

    [Theory]
    [InlineData("N")]
    [InlineData("Ctrl+N+M")]
    [InlineData("Ctrl+Unknown")]
    [InlineData("")]
    public void TryParse_InvalidGestures_ReturnFalse(string text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out _, out var error));
        Assert.NotNull(error);
    }
}
