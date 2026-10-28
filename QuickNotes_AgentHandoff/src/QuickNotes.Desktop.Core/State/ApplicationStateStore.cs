using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuickNotes.Desktop.State;

public sealed class ApplicationStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private readonly string _statePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ApplicationStateStore(string statePath)
    {
        if (string.IsNullOrWhiteSpace(statePath))
        {
            throw new ArgumentException("State path must not be empty.", nameof(statePath));
        }

        _statePath = Path.GetFullPath(statePath);
    }

    public string StatePath => _statePath;

    public async Task<ApplicationStateReadResult> ReadAsync(
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
        ApplicationState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        Validate(state);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WriteCore(state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool TryMarkWritesBlocked(
        string reason,
        DateTimeOffset now,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            error = "write_block_reason_empty";
            return false;
        }

        if (!_gate.Wait(TimeSpan.FromSeconds(2)))
        {
            error = "state_lock_timeout";
            return false;
        }

        try
        {
            var read = ReadCore();
            if (read.Status != ApplicationStateReadStatus.Found || read.State is null)
            {
                error = $"state_unavailable:{read.Status}";
                return false;
            }

            try
            {
                var blocked = read.State.MarkWritesBlocked(reason, now);
                Validate(blocked);
                WriteCore(blocked);
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException or ArgumentException)
            {
                error = exception.GetType().Name;
                return false;
            }

            error = null;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private ApplicationStateReadResult ReadCore()
    {
        if (!File.Exists(_statePath))
        {
            return ApplicationStateReadResult.NotFound();
        }

        try
        {
            var bytes = File.ReadAllBytes(_statePath);
            var state = JsonSerializer.Deserialize<ApplicationState>(bytes, SerializerOptions);
            if (state is null)
            {
                return ApplicationStateReadResult.Invalid("state_empty");
            }

            Validate(state);
            return ApplicationStateReadResult.Found(state);
        }
        catch (JsonException)
        {
            return ApplicationStateReadResult.Invalid("state_json_invalid");
        }
        catch (ArgumentException)
        {
            return ApplicationStateReadResult.Invalid("state_invalid");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ApplicationStateReadResult.Invalid("state_io");
        }
    }

    private void WriteCore(ApplicationState state)
    {
        var directory = Path.GetDirectoryName(_statePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_statePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state, SerializerOptions);
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

            File.Move(temporaryPath, _statePath, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static void Validate(ApplicationState state)
    {
        if (state.FormatVersion != ApplicationState.CurrentFormatVersion)
        {
            throw new ArgumentException("Application state format version is not supported.");
        }

        if (state.InstallationId == Guid.Empty)
        {
            throw new ArgumentException("InstallationId must not be empty.");
        }

        if (!Enum.IsDefined(state.InitializationStatus))
        {
            throw new ArgumentException("InitializationStatus must be defined.");
        }

        if (state.InitializationStatus == ApplicationInitializationStatus.Ready
            && state.SchemaVersion < 1)
        {
            throw new ArgumentException("Ready state requires a positive schema version.");
        }

        if (state.LastStartupUtc == default)
        {
            throw new ArgumentException("LastStartupUtc must be set.");
        }

        if (state.WritesBlocked && string.IsNullOrWhiteSpace(state.WriteBlockReason))
        {
            throw new ArgumentException("A blocked state requires a reason.");
        }

        if (state.RecoverySourceSha256 is { } hash
            && (hash.Length != 64 || hash.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))))
        {
            throw new ArgumentException("RecoverySourceSha256 must be 64 lowercase hexadecimal characters.");
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
