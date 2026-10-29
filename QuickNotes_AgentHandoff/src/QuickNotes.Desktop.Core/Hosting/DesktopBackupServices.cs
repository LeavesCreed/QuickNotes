using QuickNotes.Application;
using QuickNotes.Desktop.Backup;
using QuickNotes.Desktop.Editing;
using QuickNotes.Infrastructure.Backup;

namespace QuickNotes.Desktop.Hosting;

public sealed record DesktopBackupServices(
    BackupHostService Host,
    IBackupCatalog Catalog,
    BackupPackageVerifier Verifier,
    IDeviceSettingsStore DeviceSettings,
    IRestoreCoordinator Restore,
    DraftRestoreBoundaryStore DraftBoundary);
