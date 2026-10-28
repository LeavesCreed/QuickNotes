using QuickNotes.Application;
using QuickNotes.Desktop.Diagnostics;
using QuickNotes.Desktop.Paths;
using QuickNotes.Desktop.State;

namespace QuickNotes.Desktop.Startup;

public sealed class StartupCoordinator
{
    private const long SupportedSchemaVersion = 2;

    private readonly DesktopPaths _paths;
    private readonly ApplicationStateStore _stateStore;
    private readonly IRestoreJournalStore _journalStore;
    private readonly StartupArtifactScanner _artifactScanner;
    private readonly IRestoreStore _restoreStore;
    private readonly Func<CancellationToken, Task> _initializeSchema;
    private readonly DiagnosticLog _log;
    private readonly TimeProvider _timeProvider;

    public StartupCoordinator(
        DesktopPaths paths,
        ApplicationStateStore stateStore,
        IRestoreJournalStore journalStore,
        StartupArtifactScanner artifactScanner,
        IRestoreStore restoreStore,
        Func<CancellationToken, Task> initializeSchema,
        DiagnosticLog log,
        TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _journalStore = journalStore ?? throw new ArgumentNullException(nameof(journalStore));
        _artifactScanner = artifactScanner ?? throw new ArgumentNullException(nameof(artifactScanner));
        _restoreStore = restoreStore ?? throw new ArgumentNullException(nameof(restoreStore));
        _initializeSchema = initializeSchema ?? throw new ArgumentNullException(nameof(initializeSchema));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<StartupAssessment> StartAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        StartupArtifactInventory artifacts;
        try
        {
            artifacts = _artifactScanner.Scan();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Assessment(
                StartupMode.ReadOnlyDiagnostic,
                StartupReason.UnverifiedArtifacts,
                "Startup artifacts could not be inspected safely.",
                errorCode: exception.GetType().Name);
        }

        var stateResult = await _stateStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        var journalResult = await _journalStore.ReadAsync(cancellationToken).ConfigureAwait(false);

        if (stateResult.Status == ApplicationStateReadStatus.Invalid)
        {
            return Assessment(
                StartupMode.ReadOnlyDiagnostic,
                StartupReason.StateInvalid,
                "The application state is invalid; startup is read-only.",
                errorCode: stateResult.Error,
                artifacts: artifacts);
        }

        if (journalResult.Status == RestoreJournalReadStatus.Invalid)
        {
            return Assessment(
                StartupMode.ReadOnlyDiagnostic,
                StartupReason.RestoreJournalInvalid,
                "The restore journal is invalid; recovery state cannot be verified.",
                errorCode: journalResult.Error,
                state: stateResult.State,
                artifacts: artifacts);
        }

        var journal = journalResult.Journal;
        if (stateResult.Status == ApplicationStateReadStatus.Found
            && stateResult.State!.WritesBlocked)
        {
            return Assessment(
                StartupMode.WriteBlocked,
                StartupReason.WritesBlocked,
                "Database writes are blocked after a previous recovery failure.",
                errorCode: "database_writes_blocked",
                state: stateResult.State,
                artifacts: artifacts);
        }

        if (journal is not null && !journal.IsTerminal)
        {
            return Assessment(
                StartupMode.NeedsRecovery,
                StartupReason.RestoreJournalIncomplete,
                "A previous restore did not reach a terminal stage; recovery is required.",
                state: stateResult.State,
                journal: journal,
                artifacts: artifacts);
        }

        if (stateResult.Status == ApplicationStateReadStatus.NotFound)
        {
            if (File.Exists(_paths.DatabasePath) || artifacts.HasDataEvidence)
            {
                return Assessment(
                    StartupMode.ReadOnlyDiagnostic,
                    File.Exists(_paths.DatabasePath)
                        ? StartupReason.StateMissingWithDatabase
                        : StartupReason.UnverifiedArtifacts,
                    "Existing data was found without a valid application state; startup is read-only.",
                    artifacts: artifacts);
            }

            return await InitializeFirstRunAsync(now, artifacts, cancellationToken).ConfigureAwait(false);
        }

        var state = stateResult.State!;
        if (state.InitializationStatus == ApplicationInitializationStatus.Initializing)
        {
            return Assessment(
                StartupMode.ReadOnlyDiagnostic,
                StartupReason.InitializationIncomplete,
                "Initialization did not complete; retry or inspect the data directory.",
                state: state,
                artifacts: artifacts);
        }

        if (!File.Exists(_paths.DatabasePath))
        {
            return Assessment(
                StartupMode.NeedsRecovery,
                StartupReason.DatabaseMissing,
                "The initialized database is missing; recovery is required.",
                state: state,
                artifacts: artifacts);
        }

        if (HasWalSidecars())
        {
            return Assessment(
                StartupMode.NeedsRecovery,
                StartupReason.WalSidecarPresent,
                "The database has WAL sidecar files; this build does not support WAL recovery.",
                errorCode: RestoreErrorCodes.WalUnsupported,
                state: state,
                artifacts: artifacts);
        }

        try
        {
            var databaseInfo = await _restoreStore
                .PrepareCurrentAsync(_paths.DatabasePath, cancellationToken)
                .ConfigureAwait(false);
            if (databaseInfo.SchemaVersion != SupportedSchemaVersion)
            {
                return Assessment(
                    StartupMode.NeedsRecovery,
                    StartupReason.SchemaUnsupported,
                    "The database schema version is not supported.",
                    errorCode: $"schema={databaseInfo.SchemaVersion}",
                    state: state,
                    databaseInfo: databaseInfo,
                    artifacts: artifacts);
            }

            try
            {
                await _stateStore.WriteAsync(
                        state with { LastStartupUtc = now },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException or ArgumentException)
            {
                _log.TryWrite(
                    "WARN",
                    "startup",
                    "last startup time could not be persisted",
                    exception.GetType().Name);
            }

            _log.TryWrite("INFO", "startup", "ready");
            return Assessment(
                StartupMode.Ready,
                StartupReason.None,
                "The database is ready.",
                state: state,
                databaseInfo: databaseInfo,
                artifacts: artifacts);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RestoreException exception)
        {
            var reason = exception.ErrorCode == RestoreErrorCodes.WalUnsupported
                ? StartupReason.WalSidecarPresent
                : StartupReason.DatabaseCorrupt;
            _log.TryWrite("ERROR", "startup", "database preparation failed", exception.ErrorCode);
            return Assessment(
                StartupMode.NeedsRecovery,
                reason,
                exception.ErrorCode == RestoreErrorCodes.WalUnsupported
                    ? "The database has WAL sidecar files; this build does not support WAL recovery."
                    : "The database could not be prepared for use.",
                errorCode: exception.ErrorCode,
                state: state,
                artifacts: artifacts);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.TryWrite("ERROR", "startup", "database preparation failed", exception.GetType().Name);
            return Assessment(
                StartupMode.NeedsRecovery,
                StartupReason.DatabaseCorrupt,
                "The database could not be prepared for use.",
                errorCode: exception.GetType().Name,
                state: state,
                artifacts: artifacts);
        }
    }

    public async Task<StartupAssessment> RetryInitializationAsync(
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var artifacts = _artifactScanner.Scan();
        var stateResult = await _stateStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (stateResult.Status != ApplicationStateReadStatus.Found
            || stateResult.State!.InitializationStatus != ApplicationInitializationStatus.Initializing)
        {
            return Assessment(
                StartupMode.ReadOnlyDiagnostic,
                StartupReason.InitializationIncomplete,
                "There is no incomplete initialization to retry.",
                state: stateResult.State,
                artifacts: artifacts);
        }

        if (File.Exists(_paths.DatabasePath) || artifacts.HasDataEvidence)
        {
            return Assessment(
                StartupMode.ReadOnlyDiagnostic,
                StartupReason.InitializationIncomplete,
                "Initialization left data behind; manual inspection is required.",
                state: stateResult.State,
                artifacts: artifacts);
        }

        return await InitializeCoreAsync(stateResult.State, now, artifacts, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<StartupAssessment> InitializeFirstRunAsync(
        DateTimeOffset now,
        StartupArtifactInventory artifacts,
        CancellationToken cancellationToken)
    {
        var initializing = ApplicationState.CreateInitializing(Guid.NewGuid(), now);
        try
        {
            await _stateStore.WriteAsync(initializing, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.TryWrite("ERROR", "startup", "initial state could not be written", exception.GetType().Name);
            return Assessment(
                StartupMode.ReadOnlyDiagnostic,
                StartupReason.InitializationIncomplete,
                "The application state could not be initialized.",
                errorCode: exception.GetType().Name,
                artifacts: artifacts);
        }

        return await InitializeCoreAsync(initializing, now, artifacts, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<StartupAssessment> InitializeCoreAsync(
        ApplicationState initializing,
        DateTimeOffset now,
        StartupArtifactInventory artifacts,
        CancellationToken cancellationToken)
    {
        try
        {
            _paths.EnsureDirectories();
            await _initializeSchema(cancellationToken).ConfigureAwait(false);
            var databaseInfo = await _restoreStore
                .PrepareCurrentAsync(_paths.DatabasePath, cancellationToken)
                .ConfigureAwait(false);
            if (databaseInfo.SchemaVersion != SupportedSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"Initialized schema version {databaseInfo.SchemaVersion} is not supported.");
            }

            var ready = initializing.MarkReady(
                (int)databaseInfo.SchemaVersion,
                now);
            await _stateStore.WriteAsync(ready, cancellationToken).ConfigureAwait(false);
            _log.TryWrite("INFO", "startup", "first run initialized");
            return Assessment(
                StartupMode.FirstRunInitialized,
                StartupReason.Initialized,
                "First-run initialization completed.",
                state: ready,
                databaseInfo: databaseInfo,
                artifacts: artifacts);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _log.TryWrite("ERROR", "startup", "initialization failed", exception.GetType().Name);
            return Assessment(
                StartupMode.ReadOnlyDiagnostic,
                StartupReason.InitializationIncomplete,
                "Initialization failed; the incomplete state was preserved.",
                errorCode: exception.GetType().Name,
                state: initializing,
                artifacts: artifacts);
        }
    }

    private bool HasWalSidecars() =>
        File.Exists(_paths.DatabasePath + "-wal") || File.Exists(_paths.DatabasePath + "-shm");

    private static StartupAssessment Assessment(
        StartupMode mode,
        StartupReason reason,
        string message,
        string? errorCode = null,
        ApplicationState? state = null,
        RestoreJournal? journal = null,
        RestoreDatabaseInfo? databaseInfo = null,
        StartupArtifactInventory? artifacts = null) => new()
        {
            Mode = mode,
            Reason = reason,
            Message = message,
            ErrorCode = errorCode,
            State = state,
            RestoreJournal = journal,
            DatabaseInfo = databaseInfo,
            Artifacts = artifacts ?? new StartupArtifactInventory(Array.Empty<StartupArtifact>())
        };
}
