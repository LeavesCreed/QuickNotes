using QuickNotes.Application;
using QuickNotes.Desktop.Diagnostics;
using QuickNotes.Desktop.Editing;
using QuickNotes.Desktop.Paths;
using QuickNotes.Desktop.Recovery;
using QuickNotes.Desktop.SingleInstance;
using QuickNotes.Desktop.Startup;
using QuickNotes.Desktop.State;
using QuickNotes.Infrastructure.Backup;
using QuickNotes.Infrastructure.Drafts;
using QuickNotes.Infrastructure.Sqlite;

namespace QuickNotes.Desktop.Hosting;

public sealed class DesktopApplicationHost : IAsyncDisposable
{
    private readonly DesktopStartupOptions _options;
    private readonly TimeProvider _timeProvider;
    private DiagnosticLog? _log;
    private DesktopPaths? _paths;
    private SingleInstanceGuard? _guard;
    private ApplicationStateStore? _stateStore;
    private FileRestoreJournalStore? _journalStore;
    private StartupCoordinator? _startupCoordinator;
    private MissingDatabaseRebuilder? _rebuilder;
    private PersistentDatabaseWriteGate? _writeGate;
    private DesktopEditingServices? _editingServices;
    private bool _disposed;

    public DesktopApplicationHost(
        DesktopStartupOptions options,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public DesktopPaths Paths => _paths
        ?? throw new InvalidOperationException("Host has not been started.");

    public DiagnosticLog Log => _log
        ?? throw new InvalidOperationException("Host has not been started.");

    public StartupAssessment? Assessment { get; private set; }

    public IDatabaseWriteGate? WriteGate => _writeGate;

    public DesktopEditingServices? EditingServices => _editingServices;

    public event Action? ActivationRequested;

    public async Task<DesktopStartResult> StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _paths = _options.DataRoot is null
            ? DesktopPaths.CreateDefault()
            : DesktopPaths.Create(_options.DataRoot);
        _log = new DiagnosticLog(_paths.LogDirectory, _timeProvider);
        _guard = new SingleInstanceGuard(_paths.DataRoot, _log);

        var instanceResult = await _guard
            .StartAsync(TimeSpan.FromSeconds(3), cancellationToken)
            .ConfigureAwait(false);
        if (instanceResult != SingleInstanceStartResult.Owner)
        {
            await _guard.DisposeAsync().ConfigureAwait(false);
            _guard = null;
            return new DesktopStartResult
            {
                CanShowWindow = false,
                ExitCode = instanceResult switch
                {
                    SingleInstanceStartResult.ActivatedExisting => 0,
                    SingleInstanceStartResult.Unresponsive => 2,
                    _ => 1
                },
                InstanceResult = instanceResult,
                Message = instanceResult == SingleInstanceStartResult.Unresponsive
                    ? "Another QuickNotes instance is running, but it could not be activated."
                    : instanceResult == SingleInstanceStartResult.Error
                        ? "The QuickNotes instance lock could not be acquired."
                        : null
            };
        }

        _guard.ActivationRequested += () => ActivationRequested?.Invoke();
        if (_options.SingleInstanceProbe)
        {
            await DisposeAsync().ConfigureAwait(false);
            return new DesktopStartResult
            {
                CanShowWindow = false,
                ExitCode = 0,
                InstanceResult = SingleInstanceStartResult.Owner
            };
        }

        _stateStore = new ApplicationStateStore(_paths.StatePath);
        _journalStore = new FileRestoreJournalStore(_paths.RestoreJournalPath);
        _startupCoordinator = new StartupCoordinator(
            _paths,
            _stateStore,
            _journalStore,
            new StartupArtifactScanner(_paths, _options.BackupDirectory),
            new SqliteRestoreStore(),
            token => new SqliteSchema(
                new SqliteConnectionFactory(_paths.DatabasePath)).InitializeAsync(token),
            _log,
            _timeProvider);
        Assessment = await _startupCoordinator
            .StartAsync(cancellationToken)
            .ConfigureAwait(false);
        _rebuilder = new MissingDatabaseRebuilder(
            _paths,
            new BackupPackageVerifier(),
            new SqliteRestoreStore(),
            _stateStore,
            _log,
            _timeProvider);
        _writeGate = new PersistentDatabaseWriteGate(
            _stateStore,
            _log,
            timeProvider: _timeProvider,
            initiallyBlocked: Assessment.State?.WritesBlocked == true);
        RefreshEditingServices();

        return new DesktopStartResult
        {
            CanShowWindow = true,
            ExitCode = 0,
            InstanceResult = SingleInstanceStartResult.Owner,
            Assessment = Assessment
        };
    }

    public async Task<StartupAssessment> RetryStartupAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        Assessment = await _startupCoordinator!
            .StartAsync(cancellationToken)
            .ConfigureAwait(false);
        UpdateWriteGate();
        RefreshEditingServices();
        return Assessment;
    }

    public async Task<StartupAssessment> RetryInitializationAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        Assessment = await _startupCoordinator!
            .RetryInitializationAsync(cancellationToken)
            .ConfigureAwait(false);
        UpdateWriteGate();
        RefreshEditingServices();
        return Assessment;
    }

    public async Task<MissingDatabaseRebuildResult> RebuildFromBackupAsync(
        string packagePath,
        bool quarantineCorruptCurrent,
        CancellationToken cancellationToken = default,
        bool recoverBlockedWrites = false)
    {
        EnsureStarted();
        var result = await _rebuilder!
            .RebuildAsync(
                packagePath,
                quarantineCorruptCurrent,
                cancellationToken,
                recoverBlockedWrites)
            .ConfigureAwait(false);
        if (result.Status == MissingDatabaseRebuildStatus.Succeeded)
        {
            Assessment = await _startupCoordinator!
                .StartAsync(cancellationToken)
                .ConfigureAwait(false);
            UpdateWriteGate();
            RefreshEditingServices();
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_guard is not null)
        {
            await _guard.DisposeAsync().ConfigureAwait(false);
            _guard = null;
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private void EnsureStarted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_startupCoordinator is null || _stateStore is null || _rebuilder is null)
        {
            throw new InvalidOperationException("Host has not completed startup.");
        }
    }

    private void UpdateWriteGate()
    {
        if (_stateStore is null || _log is null)
        {
            return;
        }

        _writeGate = new PersistentDatabaseWriteGate(
            _stateStore,
            _log,
            timeProvider: _timeProvider,
            initiallyBlocked: Assessment?.State?.WritesBlocked == true);
    }

    private void RefreshEditingServices()
    {
        if (Assessment?.CanEnterNormalUse != true
            || _paths is null
            || _writeGate is null)
        {
            _editingServices = null;
            return;
        }

        var factory = new SqliteConnectionFactory(_paths.DatabasePath);
        var notes = new SqliteNoteRepository(factory);
        var drafts = new FileNoteDraftStore(_paths.DraftDirectory);
        var saves = new NoteSaveCoordinator(
            notes,
            drafts,
            logger: null,
            writeGate: _writeGate);
        var workspace = new EditorWorkspaceService(
            notes,
            saves,
            drafts,
            timeProvider: _timeProvider);
        var recovery = new DraftRecoveryService(_paths, notes, drafts, workspace);
        _editingServices = new DesktopEditingServices(
            notes,
            drafts,
            saves,
            workspace,
            recovery);
    }
}
