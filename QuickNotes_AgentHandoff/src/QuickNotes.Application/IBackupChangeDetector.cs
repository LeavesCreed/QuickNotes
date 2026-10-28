namespace QuickNotes.Application;

public interface IBackupChangeDetector
{
    Task<string> GetFingerprintAsync(CancellationToken cancellationToken = default);
}
