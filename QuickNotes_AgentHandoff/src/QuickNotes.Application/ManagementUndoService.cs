using QuickNotes.Domain;

namespace QuickNotes.Application;

public sealed class ManagementUndoService
{
    private readonly INoteRepository _notes;
    private readonly IDatabaseWriteGate _writeGate;
    private readonly TimeProvider _timeProvider;

    public ManagementUndoService(
        INoteRepository notes,
        IDatabaseWriteGate writeGate,
        TimeProvider? timeProvider = null)
    {
        _notes = notes ?? throw new ArgumentNullException(nameof(notes));
        _writeGate = writeGate ?? throw new ArgumentNullException(nameof(writeGate));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ManagementUndoToken> CaptureAsync(
        ManagementUndoKind kind,
        IReadOnlyCollection<Guid> noteIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(noteIds);
        var snapshots = new List<ManagementUndoSnapshot>();
        foreach (var noteId in noteIds.Distinct())
        {
            var note = await _notes.GetAsync(noteId, cancellationToken).ConfigureAwait(false)
                ?? throw new NoteNotFoundException(noteId, "capture_undo");
            var tags = await _notes.GetTagIdsAsync(noteId, cancellationToken).ConfigureAwait(false);
            snapshots.Add(new ManagementUndoSnapshot(
                note.Id,
                note.Version,
                note.Lifecycle,
                note.DisplayIntent,
                note.CategoryId,
                tags.ToArray(),
                note.IsPinned,
                note.ColorMode,
                note.ColorHex,
                note.FontSize,
                note.WindowLayout,
                note.Title,
                note.Body));
        }

        return new ManagementUndoToken(
            Guid.NewGuid(),
            kind,
            snapshots,
            $"{kind} ({snapshots.Count} note(s))");
    }

    public async Task<ManagementUndoResult> TryUndoAsync(
        ManagementUndoToken token,
        bool allowModifiedCopy = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        var results = new List<ManagementUndoItemResult>();
        await using var lease = await _writeGate.EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        foreach (var snapshot in token.Snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var current = await _notes.GetAsync(snapshot.NoteId, cancellationToken).ConfigureAwait(false);
                if (current is null)
                {
                    results.Add(new ManagementUndoItemResult(
                        snapshot.NoteId,
                        ManagementUndoItemStatus.NotFound,
                        "The note no longer exists."));
                    continue;
                }

                if (token.Kind == ManagementUndoKind.Copy)
                {
                    var changed = current.Version != snapshot.PriorVersion
                        || !string.Equals(current.Title, snapshot.PriorTitle, StringComparison.Ordinal)
                        || !string.Equals(current.Body, snapshot.PriorBody, StringComparison.Ordinal);
                    if (changed && !allowModifiedCopy)
                    {
                        results.Add(new ManagementUndoItemResult(
                            snapshot.NoteId,
                            ManagementUndoItemStatus.NeedsConfirmation,
                            "The copied note was modified; confirmation is required."));
                        continue;
                    }

                    await _notes.SetLifecycleAndDisplayAsync(
                            snapshot.NoteId,
                            NoteLifecycle.Trash,
                            NoteDisplayIntent.Hidden,
                            current.Version,
                            Now(),
                            cancellationToken)
                        .ConfigureAwait(false);
                    results.Add(new ManagementUndoItemResult(
                        snapshot.NoteId,
                        ManagementUndoItemStatus.Succeeded,
                        null));
                    continue;
                }

                if (current.Version != snapshot.PriorVersion + 1)
                {
                    results.Add(new ManagementUndoItemResult(
                        snapshot.NoteId,
                        ManagementUndoItemStatus.Stale,
                        "The note changed after the operation and cannot be undone safely."));
                    continue;
                }

                if (token.Kind == ManagementUndoKind.Lifecycle)
                {
                    await _notes.SetLifecycleAndDisplayAsync(
                            snapshot.NoteId,
                            snapshot.PriorLifecycle,
                            snapshot.PriorDisplayIntent,
                            current.Version,
                            Now(),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    var restored = token.Kind == ManagementUndoKind.Category
                        ? current with { CategoryId = snapshot.PriorCategoryId, ModifiedAtUtc = Now() }
                        : current with { ModifiedAtUtc = Now() };
                    await _notes.UpdateAsync(
                            restored,
                            current.Version,
                            snapshot.PriorTagIds,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                results.Add(new ManagementUndoItemResult(
                    snapshot.NoteId,
                    ManagementUndoItemStatus.Succeeded,
                    null));
            }
            catch (NoteConcurrencyException exception)
            {
                results.Add(new ManagementUndoItemResult(
                    snapshot.NoteId,
                    ManagementUndoItemStatus.Stale,
                    exception.ErrorCode));
            }
            catch (NoteNotFoundException exception)
            {
                results.Add(new ManagementUndoItemResult(
                    snapshot.NoteId,
                    ManagementUndoItemStatus.NotFound,
                    exception.ErrorCode));
            }
            catch (Exception exception)
            {
                results.Add(new ManagementUndoItemResult(
                    snapshot.NoteId,
                    ManagementUndoItemStatus.Failed,
                    exception.GetType().Name));
            }
        }

        return new ManagementUndoResult(results);
    }

    private DateTimeOffset Now() => _timeProvider.GetUtcNow().ToUniversalTime();
}
