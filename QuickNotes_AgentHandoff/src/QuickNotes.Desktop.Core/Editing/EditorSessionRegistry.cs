using QuickNotes.Application;

namespace QuickNotes.Desktop.Editing;

public sealed class EditorSessionRegistry : IAsyncDisposable
{
    private readonly EditorWorkspaceService _workspace;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, Entry> _entries = new();
    private bool _disposed;

    public EditorSessionRegistry(EditorWorkspaceService workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    public int Count => _entries.Count;

    public async Task<NoteEditorSession> GetOrOpenAsync(
        Guid noteId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_entries.TryGetValue(noteId, out var existing))
            {
                existing.RefCount++;
                return existing.Session;
            }

            var session = await _workspace.OpenAsync(noteId, cancellationToken).ConfigureAwait(false)
                ?? throw new NoteNotFoundException(noteId, "open_session");
            _entries[noteId] = new Entry(session);
            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NoteEditorSession> CreateNewAsync(
        CancellationToken cancellationToken = default)
    {
        var session = await _workspace.CreateNewAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _entries[session.NoteId] = new Entry(session);
            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NoteEditorSession> AdoptAsync(
        NoteEditorSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        NoteEditorSession? toDispose = null;
        NoteEditorSession result;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_entries.TryGetValue(session.NoteId, out var existing))
            {
                existing.RefCount++;
                result = existing.Session;
                if (!ReferenceEquals(existing.Session, session))
                {
                    toDispose = session;
                }
            }
            else
            {
                _entries[session.NoteId] = new Entry(session);
                result = session;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (toDispose is not null)
        {
            await toDispose.DisposeAsync().ConfigureAwait(false);
        }

        return result;
    }

    public async Task ReleaseAsync(Guid noteId)
    {
        NoteEditorSession? toDispose = null;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_entries.TryGetValue(noteId, out var entry))
            {
                entry.RefCount--;
                if (entry.RefCount <= 0)
                {
                    _entries.Remove(noteId);
                    toDispose = entry.Session;
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        if (toDispose is not null)
        {
            await toDispose.DisposeAsync().ConfigureAwait(false);
        }
    }

    public bool Contains(Guid noteId) => _entries.ContainsKey(noteId);

    public async ValueTask DisposeAsync()
    {
        NoteEditorSession[] sessions;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            sessions = _entries.Values.Select(entry => entry.Session).ToArray();
            _entries.Clear();
        }
        finally
        {
            _gate.Release();
        }

        foreach (var session in sessions)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class Entry
    {
        public Entry(NoteEditorSession session)
        {
            Session = session;
            RefCount = 1;
        }

        public NoteEditorSession Session { get; }
        public int RefCount { get; set; }
    }
}
