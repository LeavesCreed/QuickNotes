namespace QuickNotes.Desktop.Tests;

internal sealed class TestTemporaryDirectory : IDisposable
{
    public TestTemporaryDirectory()
    {
        RootPath = Path.Combine(
            Path.GetTempPath(),
            "QuickNotes-C1-Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class TestTimeProvider : TimeProvider
{
    public TestTimeProvider(DateTimeOffset utcNow, TimeZoneInfo? localTimeZone = null)
    {
        UtcNow = utcNow;
        LocalTimeZone = localTimeZone ?? TimeZoneInfo.Utc;
    }

    public DateTimeOffset UtcNow { get; set; }
    public override TimeZoneInfo LocalTimeZone { get; }

    public override DateTimeOffset GetUtcNow() => UtcNow;
}
