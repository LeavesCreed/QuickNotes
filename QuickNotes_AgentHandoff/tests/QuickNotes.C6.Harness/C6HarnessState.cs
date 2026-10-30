using System.Security.Cryptography;
using QuickNotes.Desktop.State;

namespace QuickNotes.C6.Harness;

/// <summary>
/// Writes the production <c>application-state.json</c> format into a
/// disposable C6 data root so the restart probe can exercise the real
/// <see cref="QuickNotes.Desktop.Startup.StartupCoordinator"/>. This is test
/// data only; no production composition reads anything from this helper.
/// </summary>
public static class C6HarnessState
{
    public static async Task WriteReadyAsync(
        string dataRoot,
        int schemaVersion,
        string runId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Data root must not be empty.", nameof(dataRoot));
        }

        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("C6 run ID must not be empty.", nameof(runId));
        }

        Directory.CreateDirectory(dataRoot);
        var store = new ApplicationStateStore(Path.Combine(dataRoot, "application-state.json"));
        var now = DateTimeOffset.UtcNow;
        var state = ApplicationState
            .CreateInitializing(DeterministicInstallationId(runId), now)
            .MarkReady(schemaVersion, now);
        await store.WriteAsync(state, cancellationToken).ConfigureAwait(false);
    }

    public static async Task MarkWritesBlockedAsync(
        string dataRoot,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var store = new ApplicationStateStore(Path.Combine(dataRoot, "application-state.json"));
        var read = await store.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (read.Status != ApplicationStateReadStatus.Found || read.State is null)
        {
            return;
        }

        var blocked = read.State.MarkWritesBlocked(reason, DateTimeOffset.UtcNow);
        await store.WriteAsync(blocked, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ApplicationState?> ReadAsync(
        string dataRoot,
        CancellationToken cancellationToken = default)
    {
        var store = new ApplicationStateStore(Path.Combine(dataRoot, "application-state.json"));
        var read = await store.ReadAsync(cancellationToken).ConfigureAwait(false);
        return read.Status == ApplicationStateReadStatus.Found ? read.State : null;
    }

    private static Guid DeterministicInstallationId(string runId)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(runId));
        return new Guid(hash.AsSpan(0, 16));
    }
}
