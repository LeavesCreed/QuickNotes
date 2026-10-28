using QuickNotes.Domain;

namespace QuickNotes.Application;

public interface IApplicationSettingsRepository
{
    Task<ApplicationSettings> GetAsync(CancellationToken cancellationToken = default);

    Task<ApplicationSettings> UpdateAsync(
        ApplicationSettings settings,
        long expectedVersion,
        CancellationToken cancellationToken = default);
}
