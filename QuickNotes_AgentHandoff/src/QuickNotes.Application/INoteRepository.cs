using QuickNotes.Domain;

namespace QuickNotes.Application;

public interface INoteRepository
{
    Task<Note> CreateAsync(Note note, IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken = default);
    Task<Note?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Note>> ListAsync(NoteLifecycle lifecycle, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Note>> QueryAsync(NoteQuery query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>> GetTagIdsAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Note> UpdateAsync(Note note, long expectedVersion, IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken = default);
    Task<Note> SetLifecycleAsync(Guid id, NoteLifecycle lifecycle, long expectedVersion, DateTimeOffset modifiedAtUtc, CancellationToken cancellationToken = default);
    Task<Note> SetLifecycleAndDisplayAsync(Guid id, NoteLifecycle lifecycle, NoteDisplayIntent displayIntent, long expectedVersion, DateTimeOffset modifiedAtUtc, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, long expectedVersion, CancellationToken cancellationToken = default);
}
