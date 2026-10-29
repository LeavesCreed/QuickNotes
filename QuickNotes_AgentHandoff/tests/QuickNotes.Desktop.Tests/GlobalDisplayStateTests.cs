using QuickNotes.Desktop.Windows;
using QuickNotes.Domain;

namespace QuickNotes.Desktop.Tests;

public sealed class GlobalDisplayStateTests
{
    [Fact]
    public void ShouldShow_OnlyActiveVisible_WhenNotGloballyHidden()
    {
        var visible = EditorTestData.NewNote();
        var hidden = visible with { DisplayIntent = NoteDisplayIntent.Hidden };
        var archived = visible with { Lifecycle = NoteLifecycle.Archived };
        var trash = visible with { Lifecycle = NoteLifecycle.Trash };

        Assert.True(GlobalDisplayState.ShouldShowOnDesktop(visible, globalHidden: false));
        Assert.False(GlobalDisplayState.ShouldShowOnDesktop(hidden, globalHidden: false));
        Assert.False(GlobalDisplayState.ShouldShowOnDesktop(archived, globalHidden: false));
        Assert.False(GlobalDisplayState.ShouldShowOnDesktop(trash, globalHidden: false));
    }

    [Fact]
    public void ShouldShow_NeverWhenGloballyHidden()
    {
        var visible = EditorTestData.NewNote();
        Assert.False(GlobalDisplayState.ShouldShowOnDesktop(visible, globalHidden: true));
        Assert.Empty(GlobalDisplayState.VisibleNotes(new[] { visible }, globalHidden: true));
        Assert.Single(GlobalDisplayState.VisibleNotes(new[] { visible }, globalHidden: false));
    }
}
