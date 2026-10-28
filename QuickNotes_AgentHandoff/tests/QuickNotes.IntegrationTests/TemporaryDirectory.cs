namespace QuickNotes.IntegrationTests;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        RootPath = Path.Combine(Path.GetTempPath(), "QuickNotes-Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public string DraftDirectory => Path.Combine(RootPath, "drafts");

    public void Dispose()
    {
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }
}
