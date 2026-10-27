using QuickNotes.Domain;

namespace QuickNotes.Application;

public interface INoteDraftStore
{
    Task SaveAsync(NoteDraft draft, CancellationToken cancellationToken = default);
    Task<NoteDraft?> GetAsync(Guid draftId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NoteDraft>> ListAsync(Guid? noteId = null, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid draftId, CancellationToken cancellationToken = default);
}
