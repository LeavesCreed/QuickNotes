namespace QuickNotes.Application;

public interface IRestoreCoordinator
{
    Task<RestorePreflightResult> PreflightAsync(
        RestoreRequest request,
        CancellationToken cancellationToken = default);

    Task<RestoreResult> RestoreAsync(
        RestoreRequest request,
        CancellationToken cancellationToken = default);
}
