using Microsoft.Extensions.Logging;
using QuickNotes.Application;
using QuickNotes.Domain;

namespace QuickNotes.UnitTests;

internal sealed class FakeNoteRepository : INoteRepository
{
    public Func<Note, IReadOnlyCollection<Guid>, CancellationToken, Task<Note>> CreateHandler { get; set; } =
        static (note, _, _) => Task.FromResult(note with { Version = 1 });

    public Func<Note, long, IReadOnlyCollection<Guid>, CancellationToken, Task<Note>> UpdateHandler { get; set; } =
        static (note, expectedVersion, _, _) => Task.FromResult(note with { Version = expectedVersion + 1 });

    public Func<Guid, NoteLifecycle, long, DateTimeOffset, CancellationToken, Task<Note>> SetLifecycleHandler { get; set; } =
        static (id, lifecycle, expectedVersion, modifiedAtUtc, _) => Task.FromResult(new Note
        {
            Id = id,
            Body = "stored",
            CreatedAtUtc = TestData.Timestamp,
            ModifiedAtUtc = modifiedAtUtc,
            Version = expectedVersion + 1,
            Lifecycle = lifecycle,
            CategoryId = Category.UncategorizedId,
            DisplayIntent = NoteDisplayIntent.Hidden
        });

    public Func<Guid, CancellationToken, Task<Note?>> GetHandler { get; set; } =
        static (_, _) => Task.FromResult<Note?>(null);

    public Func<Guid, CancellationToken, Task<IReadOnlyList<Guid>>> GetTagIdsHandler { get; set; } =
        static (_, _) => Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());

    public int CreateCalls { get; private set; }
    public int UpdateCalls { get; private set; }
    public int SetLifecycleCalls { get; private set; }
    public int GetCalls { get; private set; }
    public int GetTagIdsCalls { get; private set; }
    public Note? LastCreatedNote { get; private set; }
    public IReadOnlyCollection<Guid>? LastCreateTagIds { get; private set; }

    public Task<Note> CreateAsync(
        Note note,
        IReadOnlyCollection<Guid> tagIds,
        CancellationToken cancellationToken = default)
    {
        CreateCalls++;
        LastCreatedNote = note;
        LastCreateTagIds = tagIds;
        return CreateHandler(note, tagIds, cancellationToken);
    }

    public Task<Note?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        GetCalls++;
        return GetHandler(id, cancellationToken);
    }

    public Task<IReadOnlyList<Guid>> GetTagIdsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        GetTagIdsCalls++;
        return GetTagIdsHandler(id, cancellationToken);
    }

    public Task<Note> UpdateAsync(
        Note note,
        long expectedVersion,
        IReadOnlyCollection<Guid> tagIds,
        CancellationToken cancellationToken = default)
    {
        UpdateCalls++;
        return UpdateHandler(note, expectedVersion, tagIds, cancellationToken);
    }

    public Task<Note> SetLifecycleAsync(
        Guid id,
        NoteLifecycle lifecycle,
        long expectedVersion,
        DateTimeOffset modifiedAtUtc,
        CancellationToken cancellationToken = default)
    {
        SetLifecycleCalls++;
        return SetLifecycleHandler(id, lifecycle, expectedVersion, modifiedAtUtc, cancellationToken);
    }
}

internal sealed class FakeDraftStore : INoteDraftStore
{
    public Dictionary<Guid, NoteDraft> Drafts { get; } = new();
    public bool FailOnGet { get; set; }
    public bool FailOnDelete { get; set; }
    public int SaveCalls { get; private set; }
    public int GetCalls { get; private set; }
    public int ListCalls { get; private set; }
    public int DeleteCalls { get; private set; }

    public Task SaveAsync(NoteDraft draft, CancellationToken cancellationToken = default)
    {
        SaveCalls++;
        Drafts[draft.DraftId] = draft;
        return Task.CompletedTask;
    }

    public Task<NoteDraft?> GetAsync(Guid draftId, CancellationToken cancellationToken = default)
    {
        GetCalls++;
        if (FailOnGet)
        {
            throw new DraftStoreException("get", draftId, "draft_io", "simulated draft read failure");
        }

        return Task.FromResult(Drafts.TryGetValue(draftId, out var draft) ? draft : null);
    }

    public Task<IReadOnlyList<NoteDraft>> ListAsync(
        Guid? noteId = null,
        CancellationToken cancellationToken = default)
    {
        ListCalls++;
        IReadOnlyList<NoteDraft> drafts = Drafts.Values
            .Where(draft => noteId is null || draft.NoteId == noteId)
            .ToArray();
        return Task.FromResult(drafts);
    }

    public Task DeleteAsync(Guid draftId, CancellationToken cancellationToken = default)
    {
        DeleteCalls++;
        if (FailOnDelete)
        {
            throw new DraftStoreException("delete", draftId, "draft_io", "simulated draft delete failure");
        }

        Drafts.Remove(draftId);
        return Task.CompletedTask;
    }
}

internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<LogEntry> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
    }
}

internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

internal static class TestData
{
    public static readonly DateTimeOffset Timestamp = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    public static Note NewNote() => new()
    {
        Id = Guid.NewGuid(),
        Title = "title",
        Body = "body",
        CreatedAtUtc = Timestamp,
        ModifiedAtUtc = Timestamp,
        CategoryId = Category.UncategorizedId
    };

    public static NoteDraft NewDraft(
        Guid draftId,
        Note note,
        IReadOnlyCollection<Guid>? tagIds = null) => new()
    {
        DraftId = draftId,
        NoteId = note.Id,
        BaseVersion = note.Version,
        Title = note.Title,
        Body = note.Body,
        CategoryId = note.CategoryId,
        TagIds = tagIds ?? Array.Empty<Guid>(),
        IsPinned = note.IsPinned,
        DisplayIntent = note.DisplayIntent,
        CapturedAtUtc = Timestamp
    };
}
