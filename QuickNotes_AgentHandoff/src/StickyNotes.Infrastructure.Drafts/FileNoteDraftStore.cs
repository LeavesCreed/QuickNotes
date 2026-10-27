using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using StickyNotes.Application;
using StickyNotes.Domain;

namespace StickyNotes.Infrastructure.Drafts;

public sealed class FileNoteDraftStore : INoteDraftStore
{
    private const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private readonly string _directoryPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileNoteDraftStore(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new ArgumentException("Draft directory path must not be empty.", nameof(directoryPath));
        }

        _directoryPath = directoryPath;
    }

    public async Task SaveAsync(NoteDraft draft, CancellationToken cancellationToken = default)
    {
        ValidateDraft(draft);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        var targetPath = GetDraftPath(draft.DraftId);
        var temporaryPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(_directoryPath);
            var document = DraftDocument.FromDraft(draft);
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, document, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(temporaryPath);
            throw;
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            TryDeleteFile(temporaryPath);
            throw new DraftStoreException(
                "save",
                draft.DraftId,
                "draft_io",
                $"Draft {draft.DraftId} could not be saved due to an I/O error.",
                exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NoteDraft?> GetAsync(Guid draftId, CancellationToken cancellationToken = default)
    {
        ValidateDraftId(draftId, nameof(draftId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadAsync(GetDraftPath(draftId), draftId, "get", cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<NoteDraft>> ListAsync(
        Guid? noteId = null,
        CancellationToken cancellationToken = default)
    {
        if (noteId == Guid.Empty)
        {
            throw new ArgumentException("NoteId filter must not be empty.", nameof(noteId));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_directoryPath))
            {
                return Array.Empty<NoteDraft>();
            }

            List<string> paths;
            try
            {
                paths = Directory.EnumerateFiles(_directoryPath, "*.json").ToList();
            }
            catch (DirectoryNotFoundException)
            {
                return Array.Empty<NoteDraft>();
            }
            catch (Exception exception) when (IsIoFailure(exception))
            {
                throw new DraftStoreException(
                    "list",
                    null,
                    "draft_io",
                    "Drafts could not be listed due to an I/O error.",
                    exception);
            }

            var drafts = new List<NoteDraft>();
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Guid.TryParse(Path.GetFileNameWithoutExtension(path), out var draftId))
                {
                    continue;
                }

                var draft = await ReadAsync(path, draftId, "list", cancellationToken)
                    .ConfigureAwait(false);
                if (draft is null || (noteId is not null && draft.NoteId != noteId))
                {
                    continue;
                }

                drafts.Add(draft);
            }

            return drafts
                .OrderByDescending(draft => draft.CapturedAtUtc)
                .ThenBy(draft => draft.DraftId)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(Guid draftId, CancellationToken cancellationToken = default)
    {
        ValidateDraftId(draftId, nameof(draftId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Delete(GetDraftPath(draftId));
            }
            catch (DirectoryNotFoundException)
            {
                // Missing directory means the draft is already absent.
            }
            catch (Exception exception) when (IsIoFailure(exception))
            {
                throw new DraftStoreException(
                    "delete",
                    draftId,
                    "draft_io",
                    $"Draft {draftId} could not be deleted due to an I/O error.",
                    exception);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<NoteDraft?> ReadAsync(
        string path,
        Guid expectedDraftId,
        string operation,
        CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            throw new DraftStoreException(
                operation,
                expectedDraftId,
                "draft_io",
                $"Draft {expectedDraftId} could not be read due to an I/O error.",
                exception);
        }

        DraftDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<DraftDocument>(bytes, SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw CreateCorruptException(operation, expectedDraftId, exception);
        }

        if (document is null)
        {
            throw CreateCorruptException(operation, expectedDraftId, inner: null);
        }

        if (document.FormatVersion != CurrentFormatVersion)
        {
            throw new DraftStoreException(
                operation,
                expectedDraftId,
                "draft_unsupported_version",
                $"Draft {expectedDraftId} uses unsupported format version {document.FormatVersion}.",
                inner: null);
        }

        if (document.DraftId != expectedDraftId || !IsValid(document))
        {
            throw CreateCorruptException(operation, expectedDraftId, inner: null);
        }

        return document.ToDraft();
    }

    private static DraftStoreException CreateCorruptException(
        string operation,
        Guid draftId,
        Exception? inner) =>
        new(
            operation,
            draftId,
            "draft_corrupt",
            $"Draft {draftId} is corrupt.",
            inner);

    private static bool IsValid(DraftDocument document) =>
        document.DraftId != Guid.Empty
        && document.NoteId != Guid.Empty
        && document.CategoryId != Guid.Empty
        && document.Body is not null
        && document.BaseVersion >= 0
        && document.TagIds is not null
        && document.TagIds.All(id => id != Guid.Empty)
        && Enum.IsDefined(document.DisplayIntent);

    private static void ValidateDraft(NoteDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateDraftId(draft.DraftId, nameof(draft));
        if (draft.NoteId == Guid.Empty)
        {
            throw new ArgumentException("NoteId must not be empty.", nameof(draft));
        }

        if (draft.CategoryId == Guid.Empty)
        {
            throw new ArgumentException("CategoryId must not be empty.", nameof(draft));
        }

        if (draft.Body is null)
        {
            throw new ArgumentException("Body must not be null.", nameof(draft));
        }

        if (draft.BaseVersion < 0)
        {
            throw new ArgumentException("BaseVersion must be non-negative.", nameof(draft));
        }

        if (draft.TagIds is null)
        {
            throw new ArgumentException("TagIds must not be null.", nameof(draft));
        }

        if (draft.TagIds.Any(id => id == Guid.Empty))
        {
            throw new ArgumentException("TagIds must not contain an empty ID.", nameof(draft));
        }

        if (!Enum.IsDefined(draft.DisplayIntent))
        {
            throw new ArgumentException("DisplayIntent must be a defined value.", nameof(draft));
        }
    }

    private static void ValidateDraftId(Guid draftId, string parameterName)
    {
        if (draftId == Guid.Empty)
        {
            throw new ArgumentException("DraftId must not be empty.", parameterName);
        }
    }

    private static bool IsIoFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException
            or SecurityException;

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    private string GetDraftPath(Guid draftId) =>
        Path.Combine(_directoryPath, $"{draftId:D}.json");

    private sealed class DraftDocument
    {
        public int FormatVersion { get; init; }
        public Guid DraftId { get; init; }
        public Guid NoteId { get; init; }
        public long BaseVersion { get; init; }
        public string? Title { get; init; }
        public string? Body { get; init; }
        public Guid CategoryId { get; init; }
        public List<Guid>? TagIds { get; init; }
        public bool IsPinned { get; init; }
        public NoteDisplayIntent DisplayIntent { get; init; }
        public DateTimeOffset CapturedAtUtc { get; init; }

        public static DraftDocument FromDraft(NoteDraft draft) => new()
        {
            FormatVersion = CurrentFormatVersion,
            DraftId = draft.DraftId,
            NoteId = draft.NoteId,
            BaseVersion = draft.BaseVersion,
            Title = draft.Title,
            Body = draft.Body,
            CategoryId = draft.CategoryId,
            TagIds = draft.TagIds.ToList(),
            IsPinned = draft.IsPinned,
            DisplayIntent = draft.DisplayIntent,
            CapturedAtUtc = draft.CapturedAtUtc
        };

        public NoteDraft ToDraft() => new()
        {
            DraftId = DraftId,
            NoteId = NoteId,
            BaseVersion = BaseVersion,
            Title = Title,
            Body = Body!,
            CategoryId = CategoryId,
            TagIds = (TagIds ?? new List<Guid>()).AsReadOnly(),
            IsPinned = IsPinned,
            DisplayIntent = DisplayIntent,
            CapturedAtUtc = CapturedAtUtc
        };
    }
}
