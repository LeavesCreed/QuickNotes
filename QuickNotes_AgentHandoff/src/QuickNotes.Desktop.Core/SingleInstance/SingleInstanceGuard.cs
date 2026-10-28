using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using QuickNotes.Desktop.Diagnostics;

namespace QuickNotes.Desktop.SingleInstance;

public sealed class SingleInstanceGuard : IAsyncDisposable, IDisposable
{
    private const string LockFileName = ".quicknotes-instance.lock";

    private readonly string _lockFilePath;
    private readonly string _pipeName;
    private readonly DiagnosticLog _log;
    private FileStream? _lockStream;
    private CancellationTokenSource? _serverCancellation;
    private Task? _serverTask;
    private bool _started;
    private bool _disposed;

    public SingleInstanceGuard(string dataRoot, DiagnosticLog log)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Data root must not be empty.", nameof(dataRoot));
        }

        _log = log ?? throw new ArgumentNullException(nameof(log));
        var names = CreateNames(dataRoot);
        _lockFilePath = names.LockFilePath;
        _pipeName = names.PipeName;
    }

    public event Action? ActivationRequested;

    public async Task<SingleInstanceStartResult> StartAsync(
        TimeSpan activationTimeout,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            throw new InvalidOperationException("Single instance guard has already been started.");
        }

        _started = true;
        if (activationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(activationTimeout),
                activationTimeout,
                "Activation timeout must be positive.");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_lockFilePath)!);
            _lockStream = new FileStream(
                _lockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough);
            TryWriteOwnerInfo(_lockStream);
            _serverCancellation = new CancellationTokenSource();
            _serverTask = RunPipeServerAsync(_serverCancellation.Token);
            _log.TryWrite("INFO", "single_instance", "instance ownership acquired");
            return SingleInstanceStartResult.Owner;
        }
        catch (IOException)
        {
            DisposeLock();
            var activated = await TryActivateExistingAsync(activationTimeout, cancellationToken)
                .ConfigureAwait(false);
            return activated
                ? SingleInstanceStartResult.ActivatedExisting
                : SingleInstanceStartResult.Unresponsive;
        }
        catch (OperationCanceledException)
        {
            DisposeLock();
            throw;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or NotSupportedException)
        {
            _log.TryWrite("ERROR", "single_instance", "instance startup failed", exception.GetType().Name);
            DisposeLock();
            return SingleInstanceStartResult.Error;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeLock();
        if (_serverCancellation is not null)
        {
            await _serverCancellation.CancelAsync().ConfigureAwait(false);
        }

        if (_serverTask is not null)
        {
            try
            {
                await _serverTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
            }
        }

        _serverCancellation?.Dispose();
        _serverCancellation = null;
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    internal static (string LockFilePath, string PipeName) CreateNames(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Data root must not be empty.", nameof(dataRoot));
        }

        var fullRoot = Path.GetFullPath(dataRoot);
        var identity = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes($"{identity}|{fullRoot}")))
            .ToLowerInvariant()[..16];
        return (
            Path.Combine(fullRoot, LockFileName),
            $"QuickNotes.Activate.{hash}");
    }

    private static void TryWriteOwnerInfo(FileStream stream)
    {
        try
        {
            stream.SetLength(0);
            using var writer = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true);
            writer.WriteLine($"{Environment.ProcessId}|{DateTimeOffset.UtcNow:O}");
            writer.Flush();
        }
        catch (IOException)
        {
        }
    }

    private async Task<bool> TryActivateExistingAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            await client.ConnectAsync(timeoutSource.Token).ConfigureAwait(false);

            using var writer = new StreamWriter(
                client,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true)
            {
                AutoFlush = true
            };
            await writer.WriteLineAsync("ACTIVATE").ConfigureAwait(false);

            using var reader = new StreamReader(
                client,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 1024,
                leaveOpen: true);
            var response = await reader.ReadLineAsync(timeoutSource.Token).ConfigureAwait(false);
            return string.Equals(response, "OK", StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or TimeoutException)
        {
            return false;
        }
    }

    private async Task RunPipeServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(
                    server,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 1024,
                    leaveOpen: true);
                var request = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (string.Equals(request, "ACTIVATE", StringComparison.Ordinal))
                {
                    try
                    {
                        ActivationRequested?.Invoke();
                    }
                    catch (Exception exception)
                    {
                        _log.TryWrite(
                            "ERROR",
                            "single_instance",
                            "activation handler failed",
                            exception.GetType().Name);
                    }

                    using var writer = new StreamWriter(
                        server,
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                        bufferSize: 1024,
                        leaveOpen: true)
                    {
                        AutoFlush = true
                    };
                    await writer.WriteLineAsync("OK").ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                _log.TryWrite(
                    "ERROR",
                    "single_instance",
                    "activation server error",
                    exception.GetType().Name);
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private void DisposeLock()
    {
        if (_lockStream is null)
        {
            return;
        }

        _lockStream.Dispose();
        _lockStream = null;
    }
}
