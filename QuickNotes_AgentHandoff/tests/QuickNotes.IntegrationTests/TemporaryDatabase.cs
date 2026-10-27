namespace QuickNotes.IntegrationTests;

internal sealed class TemporaryDatabase : IDisposable
{
    public TemporaryDatabase()
    {
        DatabasePath = Path.Combine(Path.GetTempPath(), "QuickNotes-B1", Guid.NewGuid().ToString("N"), "notes.db");
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
    }

    public string DatabasePath { get; }

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(DatabasePath)!;
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
