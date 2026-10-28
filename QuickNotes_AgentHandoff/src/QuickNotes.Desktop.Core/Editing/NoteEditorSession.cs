using QuickNotes.Application;
using QuickNotes.Domain;

namespace QuickNotes.Desktop.Editing;

public sealed class NoteEditorSession : IAsyncDisposable
{
    private readonly INoteRepository _notes;
    private readonly INoteSaveCoordinator _saves;
    private readonly INoteDraftStore _drafts;
    private readonly EditorAutosaveOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly SemaphoreSlim _draftGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    private EditorSnapshot _snapshot;
    private EditorSaveState _saveState;
    private DraftProtectionState _draftState;
    private EditorConflict? _conflict;
    private SaveResult? _lastSaveResult;
    private string? _errorCode;
    private string? _message;
    private bool _hasPendingChanges;
    private bool _disposed;
    private CancellationTokenSource? _draftDebounce;
    private CancellationTokenSource? _saveDebounce;
    private DateTimeOffset? _draftWindowStartedAt;
    private DateTimeOffset? _saveWindowStartedAt;

    public NoteEditorSession(
        INoteRepository notes,
        INoteSaveCoordinator saves,
        INoteDraftStore drafts,
        EditorSnapshot initial,
        EditorAutosaveOptions? options = null,
        TimeProvider? timeProvider = null,
        bool startDirty = false,
        DraftProtectionState initialDraftState = DraftProtectionState.NotNeeded)
    {
        _notes = notes ?? throw new ArgumentNullException(nameof(notes));
        _saves = saves ?? throw new ArgumentNullException(nameof(saves));
        _drafts = drafts ?? throw new ArgumentNullException(nameof(drafts));
        _snapshot = initial ?? throw new ArgumentNullException(nameof(initial));
        _options = options ?? new EditorAutosaveOptions();
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _saveState = startDirty ? EditorSaveState.Modified : EditorSaveState.Clean;
        _draftState = initialDraftState;
        _hasPendingChanges = startDirty;
    }

    public event EventHandler? StatusChanged;

    public EditorSessionStatus Status
    {
        get
        {
            lock (_sync)
            {
                return BuildStatus();
            }
        }
    }

    public EditorConflict? Conflict
    {
        get
        {
            lock (_sync)
            {
                return _conflict;
            }
        }
    }

    public Guid NoteId => _snapshot.NoteId;
    public Guid DraftId => _snapshot.DraftId;
    public bool IsNew => _snapshot.IsNew;

    public string? Title
    {
        get
        {
            lock (_sync)
            {
                return _snapshot.Title;
            }
        }
    }

    public string Body
    {
        get
        {
            lock (_sync)
            {
                return _snapshot.Body;
            }
        }
    }

    public bool HasPendingChanges
    {
        get
        {
            lock (_sync)
            {
                return _hasPendingChanges;
            }
        }
    }

    public void ChangeText(string? title, string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (string.Equals(_snapshot.Title, title, StringComparison.Ordinal)
                && string.Equals(_snapshot.Body, body, StringComparison.Ordinal))
            {
                return;
            }

            var now = _timeProvider.GetUtcNow().ToUniversalTime();
            _snapshot = _snapshot with
            {
                Sequence = _snapshot.Sequence + 1,
                Title = title,
                Body = body,
                ModifiedAtUtc = now
            };
            if (_saveState is not (EditorSaveState.Conflict or EditorSaveState.OutcomeUnknown))
            {
                _saveState = EditorSaveState.Modified;
            }

            _draftState = DraftProtectionState.Pending;
            _hasPendingChanges = true;
            _errorCode = null;
            _message = null;
            _draftWindowStartedAt ??= now;
            _saveWindowStartedAt ??= now;
            ScheduleDraftLocked();
            if (_saveState is not (EditorSaveState.Conflict or EditorSaveState.OutcomeUnknown))
            {
                ScheduleSaveLocked();
            }
        }

        RaiseStatusChanged();
    }

    public async Task FlushDraftAsync(CancellationToken cancellationToken = default)
    {
        EditorSnapshot snapshot;
        lock (_sync)
        {
            ThrowIfDisposed();
            snapshot = _snapshot;
        }

        var draft = ToDraft(snapshot);
        try
        {
            await _draftGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _drafts.SaveAsync(draft, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _draftGate.Release();
            }

            lock (_sync)
            {
                if (_snapshot.Sequence == snapshot.Sequence)
                {
                    _draftState = DraftProtectionState.Protected;
                    _draftWindowStartedAt = null;
                }
                else
                {
                    _draftState = DraftProtectionState.Pending;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is DraftStoreException or IOException or UnauthorizedAccessException)
        {
            var code = exception is DraftStoreException store
                ? store.ErrorCode
                : exception.GetType().Name;
            lock (_sync)
            {
                _draftState = DraftProtectionState.Failed;
                _errorCode = code;
                _message = "Draft protection failed.";
            }
        }

        RaiseStatusChanged();
    }

    public async Task<SaveResult> SaveNowAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            _saveDebounce?.Cancel();
            _saveDebounce = null;
        }

        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EditorSnapshot sent;
            lock (_sync)
            {
                if (_saveState == EditorSaveState.Conflict)
                {
                    return BlockedByConflict();
                }

                if (_saveState == EditorSaveState.OutcomeUnknown)
                {
                    return BlockedByOutcomeUnknown();
                }

                if (!_hasPendingChanges
                    && _saveState is EditorSaveState.Clean or EditorSaveState.Saved)
                {
                    return _lastSaveResult ?? NoOpSavedResult();
                }

                sent = _snapshot;
                _saveState = EditorSaveState.Saving;
                _errorCode = null;
                _message = null;
            }

            RaiseStatusChanged();
            await FlushDraftAsync(cancellationToken).ConfigureAwait(false);

            var note = ToNote(sent);
            SaveResult result;
            try
            {
                result = sent.IsNew
                    ? await _saves.CreateAsync(note, sent.TagIds, sent.DraftId, cancellationToken)
                        .ConfigureAwait(false)
                    : await _saves.UpdateAsync(
                            note,
                            sent.BaseVersion,
                            sent.TagIds,
                            sent.DraftId,
                            cancellationToken)
                        .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lock (_sync)
                {
                    if (_snapshot.Sequence == sent.Sequence)
                    {
                        _saveState = EditorSaveState.Modified;
                    }
                }

                RaiseStatusChanged();
                throw;
            }
            catch (Exception exception)
            {
                lock (_sync)
                {
                    _saveState = EditorSaveState.SaveFailed;
                    _hasPendingChanges = true;
                    _errorCode = exception.GetType().Name;
                    _message = "The save operation failed unexpectedly.";
                }

                RaiseStatusChanged();
                throw;
            }

            bool scheduleFollowUp;
            lock (_sync)
            {
                _lastSaveResult = result;
                ApplySaveResultLocked(result, sent);
                scheduleFollowUp = result.Status == SaveStatus.Saved && _hasPendingChanges;
            }

            RaiseStatusChanged();
            if (scheduleFollowUp)
            {
                lock (_sync)
                {
                    if (!_disposed)
                    {
                        _saveWindowStartedAt = _timeProvider.GetUtcNow().ToUniversalTime();
                        ScheduleSaveLocked();
                    }
                }
            }

            return result;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public async Task<EditorSaveResolution> RetryAsync(CancellationToken cancellationToken = default)
    {
        EditorSnapshot snapshot;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_saveState != EditorSaveState.OutcomeUnknown)
            {
                return _saveState == EditorSaveState.Conflict
                    ? new EditorSaveResolution(
                        EditorSaveResolutionStatus.Conflict,
                        _errorCode,
                        _message)
                    : new EditorSaveResolution(
                        EditorSaveResolutionStatus.SaveFailed,
                        _errorCode,
                        "There is no unknown outcome to retry.");
            }

            snapshot = _snapshot;
        }

        Note? current;
        IReadOnlyList<Guid> currentTags;
        try
        {
            current = await _notes.GetAsync(snapshot.NoteId, cancellationToken).ConfigureAwait(false);
            currentTags = current is null
                ? Array.Empty<Guid>()
                : await _notes.GetTagIdsAsync(snapshot.NoteId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                _saveState = EditorSaveState.SaveFailed;
                _errorCode = exception.GetType().Name;
                _message = "The stored state could not be read for retry.";
            }

            RaiseStatusChanged();
            return new EditorSaveResolution(
                EditorSaveResolutionStatus.SaveFailed,
                _errorCode,
                _message);
        }

        if (current is null)
        {
            if (snapshot.IsNew)
            {
                lock (_sync)
                {
                    _saveState = EditorSaveState.Modified;
                    _errorCode = null;
                    _message = null;
                }

                RaiseStatusChanged();
                var result = await SaveNowAsync(cancellationToken).ConfigureAwait(false);
                return MapResult(result);
            }

            lock (_sync)
            {
                _saveState = EditorSaveState.SaveFailed;
                _errorCode = SaveErrorCode.NoteNotFound.ToString();
                _message = "The note no longer exists.";
            }

            RaiseStatusChanged();
            return new EditorSaveResolution(
                EditorSaveResolutionStatus.SaveFailed,
                _errorCode,
                _message);
        }

        var matches = ContentMatches(current, snapshot)
            && TagSetsEqual(currentTags, snapshot.TagIds)
            && ((snapshot.IsNew && current.Version == 1)
                || (!snapshot.IsNew && current.Version == snapshot.BaseVersion + 1));
        if (matches)
        {
            lock (_sync)
            {
                _snapshot = _snapshot with
                {
                    IsNew = false,
                    BaseVersion = current.Version,
                    CreatedAtUtc = current.CreatedAtUtc
                };
                if (_snapshot.Sequence == snapshot.Sequence)
                {
                    _saveState = EditorSaveState.Saved;
                    _hasPendingChanges = false;
                    _draftState = DraftProtectionState.NotNeeded;
                }
                else
                {
                    _saveState = EditorSaveState.Modified;
                    _hasPendingChanges = true;
                }

                _conflict = null;
                _errorCode = null;
                _message = null;
            }

            RaiseStatusChanged();
            return new EditorSaveResolution(EditorSaveResolutionStatus.Saved, null, null);
        }

        lock (_sync)
        {
            _saveState = EditorSaveState.Conflict;
            _conflict = new EditorConflict(snapshot, current, current.Version);
            _hasPendingChanges = true;
            _errorCode = SaveErrorCode.ConcurrencyConflict.ToString();
            _message = "The stored note differs from the local content.";
        }

        RaiseStatusChanged();
        return new EditorSaveResolution(
            EditorSaveResolutionStatus.Conflict,
            _errorCode,
            _message);
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        Guid noteId;
        Guid draftId;
        lock (_sync)
        {
            ThrowIfDisposed();
            noteId = _snapshot.NoteId;
            draftId = _snapshot.DraftId;
        }

        var note = await _notes.GetAsync(noteId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The stored note no longer exists.");
        var tagIds = await _notes.GetTagIdsAsync(noteId, cancellationToken).ConfigureAwait(false);
        try
        {
            await _drafts.DeleteAsync(draftId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (DraftStoreException)
        {
        }

        lock (_sync)
        {
            _snapshot = new EditorSnapshot
            {
                Sequence = _snapshot.Sequence + 1,
                NoteId = note.Id,
                DraftId = draftId,
                IsNew = false,
                Title = note.Title,
                Body = note.Body,
                CategoryId = note.CategoryId,
                TagIds = tagIds.ToArray(),
                IsPinned = note.IsPinned,
                DisplayIntent = note.DisplayIntent,
                CreatedAtUtc = note.CreatedAtUtc,
                ModifiedAtUtc = note.ModifiedAtUtc,
                BaseVersion = note.Version
            };
            _saveState = EditorSaveState.Clean;
            _draftState = DraftProtectionState.NotNeeded;
            _hasPendingChanges = false;
            _conflict = null;
            _errorCode = null;
            _message = null;
            _draftWindowStartedAt = null;
            _saveWindowStartedAt = null;
        }

        RaiseStatusChanged();
    }

    public async Task<NoteEditorSession> SaveAsNewAsync(CancellationToken cancellationToken = default)
    {
        EditorSnapshot local;
        lock (_sync)
        {
            ThrowIfDisposed();
            local = _snapshot;
        }

        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var next = local with
        {
            Sequence = 1,
            NoteId = Guid.NewGuid(),
            DraftId = Guid.NewGuid(),
            IsNew = true,
            BaseVersion = 0,
            CreatedAtUtc = now,
            ModifiedAtUtc = now
        };
        var session = new NoteEditorSession(
            _notes,
            _saves,
            _drafts,
            next,
            _options,
            _timeProvider,
            startDirty: true,
            initialDraftState: DraftProtectionState.Pending);
        await session.SaveNowAsync(cancellationToken).ConfigureAwait(false);
        return session;
    }

    public async Task DiscardDraftAsync(CancellationToken cancellationToken = default)
    {
        Guid draftId;
        lock (_sync)
        {
            ThrowIfDisposed();
            draftId = _snapshot.DraftId;
        }

        try
        {
            await _drafts.DeleteAsync(draftId, cancellationToken).ConfigureAwait(false);
        }
        catch (DraftStoreException exception)
        {
            lock (_sync)
            {
                _draftState = DraftProtectionState.Failed;
                _errorCode = exception.ErrorCode;
                _message = "The draft could not be discarded.";
            }

            RaiseStatusChanged();
            return;
        }

        lock (_sync)
        {
            _draftState = DraftProtectionState.NotNeeded;
        }

        RaiseStatusChanged();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _draftDebounce?.Cancel();
        _draftDebounce = null;
        _saveDebounce?.Cancel();
        _saveDebounce = null;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private void ScheduleDraftLocked()
    {
        if (_disposed)
        {
            return;
        }

        _draftDebounce?.Cancel();
        var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _draftDebounce = source;
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var delay = CalculateDelay(
            now,
            _draftWindowStartedAt,
            _options.DraftDebounce,
            _options.DraftMaxDelay);
        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, source.Token).ConfigureAwait(false);
                }

                await FlushDraftAsync(_lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                lock (_sync)
                {
                    _draftState = DraftProtectionState.Failed;
                    _errorCode = exception.GetType().Name;
                    _message = "Draft protection failed unexpectedly.";
                }

                RaiseStatusChanged();
            }
        });
    }

    private void ScheduleSaveLocked()
    {
        if (_disposed)
        {
            return;
        }

        _saveDebounce?.Cancel();
        var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _saveDebounce = source;
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var delay = CalculateDelay(
            now,
            _saveWindowStartedAt,
            _options.SaveDebounce,
            _options.SaveMaxDelay);
        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, source.Token).ConfigureAwait(false);
                }

                await SaveNowAsync(_lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                lock (_sync)
                {
                    _saveState = EditorSaveState.SaveFailed;
                    _hasPendingChanges = true;
                    _errorCode = exception.GetType().Name;
                    _message = "Automatic save failed unexpectedly.";
                }

                RaiseStatusChanged();
            }
        });
    }

    private void ApplySaveResultLocked(SaveResult result, EditorSnapshot sent)
    {
        switch (result.Status)
        {
            case SaveStatus.Saved:
                if (result.SavedNote is not { } saved)
                {
                    _saveState = EditorSaveState.SaveFailed;
                    _hasPendingChanges = true;
                    _errorCode = SaveErrorCode.CommitOutcomeUnknown.ToString();
                    _message = "The save returned no stored note.";
                    return;
                }

                _snapshot = _snapshot with
                {
                    IsNew = false,
                    BaseVersion = saved.Version,
                    CreatedAtUtc = saved.CreatedAtUtc
                };
                _conflict = null;
                _errorCode = null;
                _message = null;
                if (_snapshot.Sequence == sent.Sequence)
                {
                    _saveState = EditorSaveState.Saved;
                    _hasPendingChanges = false;
                    _draftState = result.DraftDisposition is
                        DraftDisposition.DeletedAfterSave or DraftDisposition.AlreadyAbsent
                        ? DraftProtectionState.NotNeeded
                        : DraftProtectionState.Protected;
                }
                else
                {
                    _saveState = EditorSaveState.Modified;
                    _hasPendingChanges = true;
                    _draftState = DraftProtectionState.Pending;
                    _draftWindowStartedAt = _timeProvider.GetUtcNow().ToUniversalTime();
                    ScheduleDraftLocked();
                }

                _saveWindowStartedAt = null;
                break;

            case SaveStatus.Conflict:
                _saveState = EditorSaveState.Conflict;
                _conflict = new EditorConflict(
                    sent,
                    result.CurrentNote,
                    result.CurrentVersion);
                _hasPendingChanges = true;
                _errorCode = result.Error?.Code.ToString() ?? SaveErrorCode.ConcurrencyConflict.ToString();
                _message = result.Error?.Message ?? "The note was changed elsewhere.";
                break;

            case SaveStatus.OutcomeUnknown:
                _saveState = EditorSaveState.OutcomeUnknown;
                _hasPendingChanges = true;
                _errorCode = result.Error?.Code.ToString() ?? SaveErrorCode.CommitOutcomeUnknown.ToString();
                _message = result.Error?.Message ?? "The save outcome is unknown.";
                break;

            default:
                _saveState = EditorSaveState.SaveFailed;
                _hasPendingChanges = true;
                _errorCode = result.Error?.Code.ToString() ?? SaveErrorCode.StorageUnavailable.ToString();
                _message = result.Error?.Message ?? "The note could not be saved.";
                break;
        }
    }

    private SaveResult BlockedByConflict() => new()
    {
        Status = SaveStatus.Conflict,
        NoteId = _snapshot.NoteId,
        DraftId = _snapshot.DraftId,
        CurrentNote = _conflict?.RemoteNote,
        CurrentVersion = _conflict?.RemoteVersion,
        Error = new SaveError(
            SaveErrorCode.ConcurrencyConflict,
            "The conflict must be resolved before saving.",
            false,
            "editor_conflict")
    };

    private SaveResult BlockedByOutcomeUnknown() => new()
    {
        Status = SaveStatus.OutcomeUnknown,
        NoteId = _snapshot.NoteId,
        DraftId = _snapshot.DraftId,
        Error = new SaveError(
            SaveErrorCode.CommitOutcomeUnknown,
            "The save outcome must be resolved before saving.",
            false,
            "editor_outcome_unknown")
    };

    private SaveResult NoOpSavedResult() => new()
    {
        Status = SaveStatus.Saved,
        NoteId = _snapshot.NoteId,
        DraftId = _snapshot.DraftId,
        DraftDisposition = DraftDisposition.NotProvided
    };

    private static EditorSaveResolution MapResult(SaveResult result) => result.Status switch
    {
        SaveStatus.Saved => new EditorSaveResolution(EditorSaveResolutionStatus.Saved, null, null),
        SaveStatus.Conflict => new EditorSaveResolution(
            EditorSaveResolutionStatus.Conflict,
            result.Error?.Code.ToString(),
            result.Error?.Message),
        SaveStatus.OutcomeUnknown => new EditorSaveResolution(
            EditorSaveResolutionStatus.OutcomeUnknown,
            result.Error?.Code.ToString(),
            result.Error?.Message),
        _ => new EditorSaveResolution(
            EditorSaveResolutionStatus.SaveFailed,
            result.Error?.Code.ToString(),
            result.Error?.Message)
    };

    private EditorSessionStatus BuildStatus() => new()
    {
        NoteId = _snapshot.NoteId,
        DraftId = _snapshot.DraftId,
        IsNew = _snapshot.IsNew,
        SaveState = _saveState,
        DraftState = _draftState,
        Sequence = _snapshot.Sequence,
        BaseVersion = _snapshot.BaseVersion,
        ErrorCode = _errorCode,
        Message = _message,
        HasPendingChanges = _hasPendingChanges
    };

    private Note ToNote(EditorSnapshot snapshot) => new()
    {
        Id = snapshot.NoteId,
        Title = snapshot.Title,
        Body = snapshot.Body,
        CreatedAtUtc = snapshot.CreatedAtUtc,
        ModifiedAtUtc = snapshot.ModifiedAtUtc,
        Version = snapshot.IsNew ? 1 : snapshot.BaseVersion,
        Lifecycle = NoteLifecycle.Active,
        CategoryId = snapshot.CategoryId,
        IsPinned = snapshot.IsPinned,
        DisplayIntent = snapshot.DisplayIntent
    };

    private NoteDraft ToDraft(EditorSnapshot snapshot) => new()
    {
        DraftId = snapshot.DraftId,
        NoteId = snapshot.NoteId,
        BaseVersion = snapshot.BaseVersion,
        Title = snapshot.Title,
        Body = snapshot.Body,
        CategoryId = snapshot.CategoryId,
        TagIds = snapshot.TagIds,
        IsPinned = snapshot.IsPinned,
        DisplayIntent = snapshot.DisplayIntent,
        CapturedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime()
    };

    private static bool ContentMatches(Note stored, EditorSnapshot snapshot) =>
        string.Equals(stored.Title, snapshot.Title, StringComparison.Ordinal)
        && string.Equals(stored.Body, snapshot.Body, StringComparison.Ordinal)
        && stored.CategoryId == snapshot.CategoryId
        && stored.IsPinned == snapshot.IsPinned
        && stored.DisplayIntent == snapshot.DisplayIntent;

    private static bool TagSetsEqual(
        IReadOnlyCollection<Guid> left,
        IReadOnlyCollection<Guid> right) =>
        left.Count == right.Count
        && left.ToHashSet().SetEquals(right);

    private static TimeSpan CalculateDelay(
        DateTimeOffset now,
        DateTimeOffset? windowStartedAt,
        TimeSpan debounce,
        TimeSpan maximum)
    {
        if (windowStartedAt is null)
        {
            return debounce;
        }

        var remaining = maximum - (now - windowStartedAt.Value);
        if (remaining <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return remaining < debounce ? remaining : debounce;
    }

    private void RaiseStatusChanged() => StatusChanged?.Invoke(this, EventArgs.Empty);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
