using StickyNotes.Domain;

namespace StickyNotes.Application;

public interface INoteRepository
{
    Task<Note> CreateAsync(Note note, IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken = default);
    Task<Note?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>> GetTagIdsAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Note> UpdateAsync(Note note, long expectedVersion, IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken = default);
    Task<Note> SetLifecycleAsync(Guid id, NoteLifecycle lifecycle, long expectedVersion, DateTimeOffset modifiedAtUtc, CancellationToken cancellationToken = default);
}
