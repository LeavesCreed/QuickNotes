using System.Text.Json;
using System.Text.Json.Serialization;
using QuickNotes.Application;

namespace QuickNotes.Desktop.Recovery;

public sealed class FileRestoreJournalStore : IRestoreJournalStore
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private readonly string _journalPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileRestoreJournalStore(string journalPath)
    {
        if (string.IsNullOrWhiteSpace(journalPath))
        {
            throw new ArgumentException("Restore journal path must not be empty.", nameof(journalPath));
        }

        _journalPath = Path.GetFullPath(journalPath);
    }

    public string JournalPath => _journalPath;

    public async Task<RestoreJournalReadResult> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return ReadCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteAsync(
        RestoreJournal journal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        Validate(journal);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WriteCore(journal);
        }
        finally
        {
            _gate.Release();
        }
    }

    private RestoreJournalReadResult ReadCore()
    {
        if (!File.Exists(_journalPath))
        {
            return RestoreJournalReadResult.NotFound();
        }

        try
        {
            var bytes = File.ReadAllBytes(_journalPath);
            var journal = JsonSerializer.Deserialize<RestoreJournal>(bytes, SerializerOptions);
            if (journal is null)
            {
                return RestoreJournalReadResult.Invalid("journal_empty");
            }

            Validate(journal);
            return RestoreJournalReadResult.Found(journal);
        }
        catch (JsonException)
        {
            return RestoreJournalReadResult.Invalid("journal_json_invalid");
        }
        catch (ArgumentException)
        {
            return RestoreJournalReadResult.Invalid("journal_invalid");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return RestoreJournalReadResult.Invalid("journal_io");
        }
    }

    private void WriteCore(RestoreJournal journal)
    {
        var directory = Path.GetDirectoryName(_journalPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_journalPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, SerializerOptions);
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _journalPath, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static void Validate(RestoreJournal journal)
    {
        if (journal.FormatVersion != RestoreJournal.CurrentFormatVersion)
        {
            throw new ArgumentException("Restore journal format version is not supported.");
        }

        if (journal.RestoreId == Guid.Empty)
        {
            throw new ArgumentException("RestoreId must not be empty.");
        }

        if (!Enum.IsDefined(journal.Stage))
        {
            throw new ArgumentException("Restore stage must be defined.");
        }

        if (string.IsNullOrWhiteSpace(journal.CurrentDatabasePath)
            || string.IsNullOrWhiteSpace(journal.StagingDirectory))
        {
            throw new ArgumentException("Restore journal paths must not be empty.");
        }

        if (journal.StartedAtUtc == default || journal.UpdatedAtUtc == default)
        {
            throw new ArgumentException("Restore journal timestamps must be set.");
        }

        if (journal.UpdatedAtUtc < journal.StartedAtUtc)
        {
            throw new ArgumentException("Restore journal update time must not precede start time.");
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

    private static void TryDelete(string path)
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
}
