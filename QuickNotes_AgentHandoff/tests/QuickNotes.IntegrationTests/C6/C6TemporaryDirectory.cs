namespace QuickNotes.IntegrationTests.C6;

/// <summary>
/// Disposable temp root for C6 data-factory tests. It lives under
/// <c>%TEMP%\QuickNotes-C6</c> so it satisfies the factory's run-root guard
/// and never points at real QuickNotes data.
/// </summary>
internal sealed class C6TemporaryDirectory : IDisposable
{
    public C6TemporaryDirectory()
    {
        RootPath = Path.Combine(
            Path.GetTempPath(),
            QuickNotes.C6.Harness.C6DataFactory.RunRootSegment,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public void Dispose()
    {
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }
}
