using QuickNotes.Domain;

namespace QuickNotes.UnitTests;

public sealed class NoteRulesTests
{
    [Theory]
    [InlineData("#E6E6FA", true)]
    [InlineData("#000000", true)]
    [InlineData("E6E6FA", false)]
    [InlineData("#GGGGGG", false)]
    public void ManualColor_RequiresHex(string value, bool valid)
    {
        if (valid)
        {
            NoteRules.ValidateColor(NoteColorMode.Manual, value);
        }
        else
        {
            Assert.Throws<ArgumentException>(() =>
                NoteRules.ValidateColor(NoteColorMode.Manual, value));
        }
    }

    [Fact]
    public void FollowCategory_RequiresNullColor()
    {
        NoteRules.ValidateColor(NoteColorMode.FollowCategory, null);
        Assert.Throws<ArgumentException>(() =>
            NoteRules.ValidateColor(NoteColorMode.FollowCategory, "#E6E6FA"));
    }

    [Theory]
    [InlineData(8, true)]
    [InlineData(72, true)]
    [InlineData(7, false)]
    [InlineData(73, false)]
    public void FontSize_UsesCandidateRange(int size, bool valid)
    {
        if (valid)
        {
            NoteRules.ValidateFontSize(size);
        }
        else
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                NoteRules.ValidateFontSize(size));
        }

        NoteRules.ValidateFontSize(null);
    }

    [Fact]
    public void WindowLayout_RequiresPositiveSizeAndFiniteValues()
    {
        NoteRules.ValidateWindowLayout(new NoteWindowLayout
        {
            Left = -100,
            Top = 50,
            Width = 320,
            Height = 240
        });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NoteRules.ValidateWindowLayout(new NoteWindowLayout
            {
                Left = 0,
                Top = 0,
                Width = 0,
                Height = 240
            }));
        NoteRules.ValidateWindowLayout(null);
    }

    [Fact]
    public void Defaults_AreFollowCategoryAndGlobalFont()
    {
        var note = new Note
        {
            Id = Guid.NewGuid(),
            Body = "body",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ModifiedAtUtc = DateTimeOffset.UtcNow,
            CategoryId = Category.UncategorizedId
        };

        Assert.Equal(NoteColorMode.FollowCategory, note.ColorMode);
        Assert.Null(note.ColorHex);
        Assert.Null(note.FontSize);
        Assert.Null(note.WindowLayout);
    }
}
