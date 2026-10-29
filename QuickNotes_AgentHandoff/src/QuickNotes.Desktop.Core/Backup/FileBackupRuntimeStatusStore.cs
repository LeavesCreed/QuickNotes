using System.Text.Json;
using QuickNotes.Application;

namespace QuickNotes.Desktop.Backup;

public sealed class FileBackupRuntimeStatusStore : IBackupRuntimeStatusStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileBackupRuntimeStatusStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Backup status path must not be empty.", nameof(path));
        }

        _path = Path.GetFullPath(path);
    }

    public async Task<BackupRuntimeStatus?> ReadAsync(
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

            BackupRuntimeStatus? status;
            try
            {
                status = JsonSerializer.Deserialize<BackupRuntimeStatus>(bytes, SerializerOptions);
            }
            catch (JsonException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }

            return status?.FormatVersion == BackupRuntimeStatus.CurrentFormatVersion
                ? status
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteAsync(
        BackupRuntimeStatus status,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.FormatVersion != BackupRuntimeStatus.CurrentFormatVersion)
        {
            throw new ArgumentException("Backup status format version is not supported.", nameof(status));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(status, SerializerOptions);
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
