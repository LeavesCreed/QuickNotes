using QuickNotes.Desktop.Diagnostics;

namespace QuickNotes.Desktop.Tests;

public sealed class DiagnosticLogTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void TryWrite_AppendsLinesToDailyFile()
    {
        using var temporary = new TestTemporaryDirectory();
        var log = new DiagnosticLog(
            Path.Combine(temporary.RootPath, "logs"),
            new TestTimeProvider(Now));

        Assert.True(log.TryWrite("INFO", "startup", "ready"));
        Assert.True(log.TryWrite("WARN", "startup", "recovery needed", "state_invalid"));

        var path = Path.Combine(temporary.RootPath, "logs", "quicknotes-20261028.log");
        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Contains("startup", lines[0]);
        Assert.Contains("state_invalid", lines[1]);
    }

    [Fact]
    public void TryWrite_SanitizesLineBreaks()
    {
        using var temporary = new TestTemporaryDirectory();
        var log = new DiagnosticLog(temporary.RootPath, new TestTimeProvider(Now));

        Assert.True(log.TryWrite("INFO", "startup", "line1\r\nline2"));

        var path = Path.Combine(temporary.RootPath, "quicknotes-20261028.log");
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("line1\r\nline2", text);
        Assert.Contains("line1  line2", text);
    }

    [Fact]
    public void TryWrite_WhenLogPathIsFile_ReturnsFalse()
    {
        using var temporary = new TestTemporaryDirectory();
        var logPath = Path.Combine(temporary.RootPath, "logs");
        File.WriteAllText(logPath, "occupied");
        var log = new DiagnosticLog(logPath, new TestTimeProvider(Now));

        Assert.False(log.TryWrite("ERROR", "startup", "cannot write"));
    }
}
