using QuickNotes.Desktop.Startup;
using QuickNotes.Desktop.SingleInstance;

namespace QuickNotes.Desktop.Hosting;

public sealed record DesktopStartResult
{
    public required bool CanShowWindow { get; init; }
    public required int ExitCode { get; init; }
    public required SingleInstanceStartResult InstanceResult { get; init; }
    public StartupAssessment? Assessment { get; init; }
    public string? Message { get; init; }
}
