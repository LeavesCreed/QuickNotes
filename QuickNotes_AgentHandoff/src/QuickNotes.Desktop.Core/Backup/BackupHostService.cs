using QuickNotes.Application;
using QuickNotes.Desktop.Diagnostics;
using QuickNotes.Desktop.Paths;
using QuickNotes.Domain;
using QuickNotes.Infrastructure.Backup;

namespace QuickNotes.Desktop.Backup;

public sealed class BackupHostService : IAsyncDisposable
{
    private readonly DesktopPaths _paths;
    private readonly IDeviceSettingsStore _deviceSettings;
    private readonly IBackupRuntimeStatusStore _statusStore;
    private readonly IDailyBackupService _dailyBackup;
    private readonly IBackupCoordinator _backups;
    private readonly IBackupCatalog _catalog;
    private readonly BackupPackageVerifier _verifier;
    private readonly BackupHostOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly DiagnosticLog _log;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private CancellationTokenSource? _loopCancellation;
    private Task? _loopTask;
    private Task? _immediateTask;
    private bool _started;
    private bool _suspended;

    public BackupHostService(
        DesktopPaths paths,
        IDeviceSettingsStore deviceSettings,
        IBackupRuntimeStatusStore statusStore,
        IDailyBackupService dailyBackup,
        IBackupCoordinator backups,
        IBackupCatalog catalog,
        BackupPackageVerifier verifier,
        BackupHostOptions? options = null,
        TimeProvider? timeProvider = null,
        DiagnosticLog? log = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _deviceSettings = deviceSettings ?? throw new ArgumentNullException(nameof(deviceSettings));
        _statusStore = statusStore ?? throw new ArgumentNullException(nameof(statusStore));
        _dailyBackup = dailyBackup ?? throw new ArgumentNullException(nameof(dailyBackup));
        _backups = backups ?? throw new ArgumentNullException(nameof(backups));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _options = options ?? new BackupHostOptions();
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<BackupRuntimeStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var stored = await _statusStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        var directory = (await ReadDeviceSettingsAsync(cancellationToken).ConfigureAwait(false))
            .BackupDirectory;
        if (stored is null)
        {
            return new BackupRuntimeStatus
            {
                FormatVersion = BackupRuntimeStatus.CurrentFormatVersion,
                BackupDirectory = directory,
                AutoBackupConfigured = directory is not null
            };
        }

        return stored with
        {
            BackupDirectory = directory,
            AutoBackupConfigured = directory is not null
        };
    }

    public async Task<BackupOperationResult> SetBackupDirectoryAsync(
        string? directory,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_suspended)
            {
                return Busy();
            }

            var settings = await ReadDeviceSettingsAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(directory))
            {
                await _deviceSettings.WriteAsync(
                        settings with { BackupDirectory = null },
                        cancellationToken)
                    .ConfigureAwait(false);
                await TryWriteStatusAsync(
                        BackupRuntimeStatus.NotConfigured(Now()),
                        cancellationToken)
                    .ConfigureAwait(false);
                return new BackupOperationResult
                {
                    Status = BackupOperationStatus.Succeeded,
                    Message = "Automatic backup is not configured."
                };
            }

            var validation = await BackupDirectoryValidator
                .ValidateAsync(directory, _paths.DataRoot, cancellationToken)
                .ConfigureAwait(false);
            if (validation.Status != BackupDirectoryValidationStatus.Valid)
            {
                return new BackupOperationResult
                {
                    Status = BackupOperationStatus.InvalidDirectory,
                    ErrorCode = validation.ErrorCode,
                    Message = validation.Message
                };
            }

            var fullDirectory = Path.GetFullPath(directory);
            await _deviceSettings.WriteAsync(
                    settings with { BackupDirectory = fullDirectory },
                    cancellationToken)
                .ConfigureAwait(false);
            await TryWriteStatusAsync(
                    new BackupRuntimeStatus
                    {
                        FormatVersion = BackupRuntimeStatus.CurrentFormatVersion,
                        BackupDirectory = fullDirectory,
                        LastCheckAtUtc = Now(),
                        AutoBackupConfigured = true
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return new BackupOperationResult
            {
                Status = BackupOperationStatus.Succeeded,
                PackagePath = fullDirectory,
                Message = "Backup directory configured."
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _log.TryWrite("ERROR", "backup_host", "backup directory could not be saved", exception.GetType().Name);
            return Failed("backup_directory_save_failed", exception.GetType().Name);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<BackupOperationResult> CreateManualBackupAsync(
        CancellationToken cancellationToken = default)
    {
        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Busy();
        }

        try
        {
            if (_suspended)
            {
                return Busy();
            }

            var directory = (await ReadDeviceSettingsAsync(cancellationToken).ConfigureAwait(false))
                .BackupDirectory;
            if (directory is null)
            {
                await TryWriteStatusAsync(
                        BackupRuntimeStatus.NotConfigured(Now()),
                        cancellationToken)
                    .ConfigureAwait(false);
                return new BackupOperationResult
                {
                    Status = BackupOperationStatus.NotConfigured,
                    Message = "Automatic backup is not configured."
                };
            }

            var result = await _backups.CreateAsync(
                    new BackupRequest
                    {
                        BackupDirectory = directory,
                        Type = BackupType.Manual
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            await UpdateStatusAfterBackupAsync(result, directory, cancellationToken)
                .ConfigureAwait(false);
            return result.Status == BackupStatus.Succeeded
                ? new BackupOperationResult
                {
                    Status = BackupOperationStatus.Succeeded,
                    Backup = result,
                    PackagePath = result.PackagePath,
                    Message = "Backup created."
                }
                : new BackupOperationResult
                {
                    Status = BackupOperationStatus.Failed,
                    Backup = result,
                    PackagePath = result.PackagePath,
                    ErrorCode = result.Error?.Code.ToString(),
                    Message = result.Error?.Message ?? "Backup failed."
                };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _log.TryWrite("ERROR", "backup_host", "manual backup failed", exception.GetType().Name);
            return Failed("manual_backup_failed", exception.GetType().Name);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<BackupHostCheckResult> RunScheduledCheckAsync(
        CancellationToken cancellationToken = default)
    {
        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new BackupHostCheckResult
            {
                Status = BackupHostCheckStatus.Busy,
                Message = "A backup operation is already running."
            };
        }

        try
        {
            if (_suspended)
            {
                return new BackupHostCheckResult
                {
                    Status = BackupHostCheckStatus.Busy,
                    Message = "Backup is suspended for restore."
                };
            }

            var directory = (await ReadDeviceSettingsAsync(cancellationToken).ConfigureAwait(false))
                .BackupDirectory;
            if (directory is null)
            {
                await TryWriteStatusAsync(
                        BackupRuntimeStatus.NotConfigured(Now()),
                        cancellationToken)
                    .ConfigureAwait(false);
                return new BackupHostCheckResult
                {
                    Status = BackupHostCheckStatus.NotConfigured,
                    Message = "Automatic backup is not configured."
                };
            }

            var result = await _dailyBackup
                .RunCheckAsync(directory, cancellationToken)
                .ConfigureAwait(false);
            await UpdateStatusAfterDailyAsync(result, directory, cancellationToken)
                .ConfigureAwait(false);
            return new BackupHostCheckResult
            {
                Status = BackupHostCheckStatus.Completed,
                Result = result
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _log.TryWrite("ERROR", "backup_host", "scheduled backup check failed", exception.GetType().Name);
            await TryWriteStatusAsync(
                    new BackupRuntimeStatus
                    {
                        FormatVersion = BackupRuntimeStatus.CurrentFormatVersion,
                        BackupDirectory = (await ReadDeviceSettingsAsync(CancellationToken.None)
                            .ConfigureAwait(false)).BackupDirectory,
                        LastCheckAtUtc = Now(),
                        LastFailureAtUtc = Now(),
                        LastErrorCode = exception.GetType().Name,
                        AutoBackupConfigured = true
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            return new BackupHostCheckResult
            {
                Status = BackupHostCheckStatus.Failed,
                Message = exception.GetType().Name
            };
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<BackupCatalog> ListBackupsAsync(
        CancellationToken cancellationToken = default)
    {
        var directory = (await ReadDeviceSettingsAsync(cancellationToken).ConfigureAwait(false))
            .BackupDirectory;
        if (directory is null)
        {
            return new BackupCatalog
            {
                ValidEntries = Array.Empty<BackupCatalogEntry>(),
                Issues = new[]
                {
                    new BackupCatalogIssue(
                        string.Empty,
                        "backup_directory_not_configured",
                        "Automatic backup is not configured.")
                }
            };
        }

        try
        {
            return await _catalog.ReadForSchedulingAsync(directory, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BackupException exception)
        {
            return new BackupCatalog
            {
                ValidEntries = Array.Empty<BackupCatalogEntry>(),
                Issues = new[] { new BackupCatalogIssue(directory, exception.ErrorCode, exception.Message) }
            };
        }
    }

    public async Task<BackupVerificationResult> VerifyBackupAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return Invalid("backup_package_empty", "The package path must not be empty.");
        }

        if (!File.Exists(packagePath))
        {
            return new BackupVerificationResult
            {
                Status = BackupVerificationStatus.Missing
            };
        }

        var directory = (await ReadDeviceSettingsAsync(cancellationToken).ConfigureAwait(false))
            .BackupDirectory;
        if (directory is null || !IsInsideDirectory(packagePath, directory))
        {
            return Invalid("backup_package_outside_directory", "The package is not in the configured backup directory.");
        }

        var verificationDirectory = Path.Combine(_paths.DataRoot, ".quicknotes-verify");
        try
        {
            Directory.CreateDirectory(verificationDirectory);
            var manifest = await _verifier
                .VerifyAsync(packagePath, verificationDirectory, cancellationToken)
                .ConfigureAwait(false);
            return new BackupVerificationResult
            {
                Status = BackupVerificationStatus.Valid,
                Manifest = manifest
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BackupException exception)
        {
            return Invalid(exception.ErrorCode, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Invalid("backup_package_io", exception.GetType().Name);
        }
        finally
        {
            TryDeleteDirectory(verificationDirectory);
        }
    }

    public async Task<BackupOperationResult> DeleteManualBackupAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Busy();
        }

        try
        {
            if (_suspended)
            {
                return Busy();
            }

            var directory = (await ReadDeviceSettingsAsync(cancellationToken).ConfigureAwait(false))
                .BackupDirectory;
            if (directory is null)
            {
                return new BackupOperationResult
                {
                    Status = BackupOperationStatus.NotConfigured,
                    Message = "Automatic backup is not configured."
                };
            }

            if (!IsInsideDirectory(packagePath, directory))
            {
                return new BackupOperationResult
                {
                    Status = BackupOperationStatus.InvalidTarget,
                    PackagePath = packagePath,
                    Message = "The package is not in the configured backup directory."
                };
            }

            if (!File.Exists(packagePath))
            {
                return new BackupOperationResult
                {
                    Status = BackupOperationStatus.NotFound,
                    PackagePath = packagePath,
                    Message = "The backup package does not exist."
                };
            }

            var manifest = await _verifier
                .InspectAsync(packagePath, verifyDatabaseHash: false, cancellationToken)
                .ConfigureAwait(false);
            if (manifest.BackupType != BackupType.Manual)
            {
                return new BackupOperationResult
                {
                    Status = BackupOperationStatus.InvalidTarget,
                    PackagePath = packagePath,
                    Message = "Only manual backups can be deleted."
                };
            }

            File.Delete(packagePath);
            return new BackupOperationResult
            {
                Status = BackupOperationStatus.Succeeded,
                PackagePath = packagePath,
                Message = "Manual backup deleted."
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BackupException exception)
        {
            return Failed(exception.ErrorCode, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failed("backup_delete_failed", exception.GetType().Name);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _loopCancellation = new CancellationTokenSource();
        var token = _loopCancellation.Token;
        _immediateTask = RunScheduledCheckSafeAsync(token);
        _loopTask = Task.Run(() => LoopAsync(token), CancellationToken.None);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task TriggerWakeCheckAsync(CancellationToken cancellationToken = default)
    {
        await RunScheduledCheckAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IAsyncDisposable> SuspendForRestoreAsync(
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _suspended = true;
        return new RestoreLease(this);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        _loopCancellation?.Cancel();
        if (_immediateTask is not null)
        {
            try
            {
                await _immediateTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _loopCancellation?.Dispose();
        _loopCancellation = null;
        _loopTask = null;
        _immediateTask = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _operationGate.Dispose();
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.CheckInterval);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    break;
                }

                await RunScheduledCheckSafeAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunScheduledCheckSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunScheduledCheckAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _log.TryWrite("ERROR", "backup_host", "scheduled backup loop failed", exception.GetType().Name);
        }
    }

    private async Task<DeviceSettings> ReadDeviceSettingsAsync(CancellationToken cancellationToken)
    {
        var result = await _deviceSettings.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (result.Status == DeviceSettingsReadResultStatus.Found)
        {
            return result.Settings!;
        }

        if (result.Status == DeviceSettingsReadResultStatus.Invalid)
        {
            _log.TryWrite("WARN", "backup_host", "device settings are invalid", result.ErrorCode);
        }

        return new DeviceSettings();
    }

    private async Task UpdateStatusAfterDailyAsync(
        DailyBackupResult result,
        string directory,
        CancellationToken cancellationToken)
    {
        var previous = await _statusStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        var now = Now();
        var failure = result.Outcome == DailyBackupOutcome.Failed;
        DateTimeOffset? lastSuccess = previous?.LastSuccessAtUtc;
        string? lastPackage = previous?.LastSuccessfulPackagePath;
        if (result.Outcome == DailyBackupOutcome.BackedUp
            && result.Backup?.Manifest is { } manifest)
        {
            lastSuccess = manifest.CreatedAtUtc;
            lastPackage = result.Backup.PackagePath;
        }

        var warning = result.StateWarning;
        if (warning is null && result.Retention is { Warnings.Count: > 0 })
        {
            warning = string.Join("; ", result.Retention.Warnings.Select(item => item.ErrorCode));
        }

        var status = new BackupRuntimeStatus
        {
            FormatVersion = BackupRuntimeStatus.CurrentFormatVersion,
            BackupDirectory = directory,
            LastCheckAtUtc = now,
            LastSuccessAtUtc = lastSuccess,
            LastFailureAtUtc = failure ? now : previous?.LastFailureAtUtc,
            LastErrorCode = failure ? result.Error?.Code.ToString() : previous?.LastErrorCode,
            LastWarning = warning,
            LastSuccessfulPackagePath = lastPackage,
            AutoBackupConfigured = true
        };
        await TryWriteStatusAsync(status, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateStatusAfterBackupAsync(
        BackupResult result,
        string directory,
        CancellationToken cancellationToken)
    {
        var previous = await _statusStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        var now = Now();
        var success = result.Status == BackupStatus.Succeeded;
        var status = new BackupRuntimeStatus
        {
            FormatVersion = BackupRuntimeStatus.CurrentFormatVersion,
            BackupDirectory = directory,
            LastCheckAtUtc = now,
            LastSuccessAtUtc = success && result.Manifest is { } manifest
                ? manifest.CreatedAtUtc
                : previous?.LastSuccessAtUtc,
            LastFailureAtUtc = success ? previous?.LastFailureAtUtc : now,
            LastErrorCode = success ? previous?.LastErrorCode : result.Error?.Code.ToString(),
            LastWarning = null,
            LastSuccessfulPackagePath = success
                ? result.PackagePath
                : previous?.LastSuccessfulPackagePath,
            AutoBackupConfigured = true
        };
        await TryWriteStatusAsync(status, cancellationToken).ConfigureAwait(false);
    }

    private async Task TryWriteStatusAsync(
        BackupRuntimeStatus status,
        CancellationToken cancellationToken)
    {
        try
        {
            await _statusStore.WriteAsync(status, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _log.TryWrite("WARN", "backup_host", "backup status could not be persisted", exception.GetType().Name);
        }
    }

    private DateTimeOffset Now() => _timeProvider.GetUtcNow().ToUniversalTime();

    private static BackupOperationResult Busy() => new()
    {
        Status = BackupOperationStatus.Busy,
        Message = "A backup operation is already running."
    };

    private static BackupOperationResult Failed(string code, string message) => new()
    {
        Status = BackupOperationStatus.Failed,
        ErrorCode = code,
        Message = message
    };

    private static BackupVerificationResult Invalid(string code, string message) => new()
    {
        Status = BackupVerificationStatus.Invalid,
        Error = new BackupError(BackupErrorCode.PackageVerificationFailed, message, false, code)
    };

    private static bool IsInsideDirectory(string packagePath, string directory)
    {
        try
        {
            var fullPackage = Path.GetFullPath(packagePath);
            var fullDirectory = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return fullPackage.StartsWith(
                fullDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class RestoreLease : IAsyncDisposable
    {
        private readonly BackupHostService _owner;
        private bool _disposed;

        public RestoreLease(BackupHostService owner)
        {
            _owner = owner;
        }

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                _owner._suspended = false;
                _owner._operationGate.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
