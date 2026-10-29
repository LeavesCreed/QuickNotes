using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuickNotes.Desktop.Editing;

public sealed class DraftRestoreBoundaryStore
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DraftRestoreBoundaryStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Draft boundary path must not be empty.", nameof(path));
        }

        _path = Path.GetFullPath(path);
    }

    public async Task<DraftRestoreBoundary?> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            DraftRestoreBoundary? boundary;
            try
            {
                boundary = JsonSerializer.Deserialize<DraftRestoreBoundary>(bytes, SerializerOptions);
            }
            catch (JsonException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }

            return IsValid(boundary) ? boundary : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteAsync(
        DraftRestoreBoundary boundary,
        CancellationToken cancellationToken = default)
    {
        if (!IsValid(boundary))
        {
            throw new ArgumentException("Draft restore boundary is invalid.", nameof(boundary));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(boundary, SerializerOptions);
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsValid(DraftRestoreBoundary? boundary) =>
        boundary is not null
        && boundary.FormatVersion == DraftRestoreBoundary.CurrentFormatVersion
        && boundary.RestoreId != Guid.Empty
        && boundary.RestoredAtUtc != default
        && boundary.DraftIds is not null
        && boundary.DraftIds.All(id => id != Guid.Empty);

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
}
