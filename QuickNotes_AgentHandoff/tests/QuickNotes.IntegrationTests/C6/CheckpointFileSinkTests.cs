using System.Globalization;
using QuickNotes.C6.Harness;

namespace QuickNotes.IntegrationTests.C6;

public sealed class CheckpointFileSinkTests
{
    [Fact]
    public void CheckpointFileSink_WritesMarkerAtomically()
    {
        using var temp = new TemporaryDirectory();
        var sink = new CheckpointFileSink(temp.RootPath);

        sink.Reached("restore.before_switch");

        var path = Path.Combine(temp.RootPath, "restore.before_switch.reached");
        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(temp.RootPath, "*.tmp"));
    }

    [Fact]
    public void CheckpointFileSink_MarkerContainsOnlyATimestamp()
    {
        using var temp = new TemporaryDirectory();
        var sink = new CheckpointFileSink(temp.RootPath);

        sink.Reached("save.before_commit");

        var path = Path.Combine(temp.RootPath, "save.before_commit.reached");
        var content = File.ReadAllText(path);
        Assert.True(
            DateTimeOffset.TryParse(
                content,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _));
        Assert.DoesNotContain("body", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(temp.RootPath, content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData(@"sub\escape")]
    public void CheckpointFileSink_RejectsUnsafeNames(string name)
    {
        using var temp = new TemporaryDirectory();
        var sink = new CheckpointFileSink(temp.RootPath);

        Assert.Throws<ArgumentException>(() => sink.Reached(name));
        Assert.Empty(Directory.GetFiles(temp.RootPath, "*.reached"));
    }

    [Fact]
    public void CheckpointFileSink_OverwritesMarkerWithoutTempResidue()
    {
        using var temp = new TemporaryDirectory();
        var sink = new CheckpointFileSink(temp.RootPath);

        sink.Reached("backup.before_publish");
        sink.Reached("backup.before_publish");

        Assert.Single(Directory.GetFiles(temp.RootPath, "*.reached"));
        Assert.Empty(Directory.GetFiles(temp.RootPath, "*.tmp"));
    }
}
