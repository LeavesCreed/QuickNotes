namespace QuickNotes.Desktop.Hosting;

public sealed record DesktopStartupOptions
{
    public string? DataRoot { get; init; }
    public string? BackupDirectory { get; init; }
    public bool SingleInstanceProbe { get; init; }
    public bool StartOnLogin { get; init; }

    public static DesktopStartupOptions Parse(IReadOnlyList<string> arguments)
    {
        string? dataRoot = null;
        string? backupDirectory = null;
        var probe = false;
        var startOnLogin = false;
        for (var index = 0; index < arguments.Count; index++)
        {
            switch (arguments[index])
            {
                case "--data-root":
                    dataRoot = ReadValue(arguments, ref index, "--data-root");
                    break;
                case "--backup-dir":
                    backupDirectory = ReadValue(arguments, ref index, "--backup-dir");
                    break;
                case "--single-instance-probe":
                    probe = true;
                    break;
                case "--startup":
                case "--background":
                    startOnLogin = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {arguments[index]}.");
            }
        }

        return new DesktopStartupOptions
        {
            DataRoot = dataRoot,
            BackupDirectory = backupDirectory,
            SingleInstanceProbe = probe,
            StartOnLogin = startOnLogin
        };
    }

    private static string ReadValue(
        IReadOnlyList<string> arguments,
        ref int index,
        string option)
    {
        if (index + 1 >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index + 1]))
        {
            throw new ArgumentException($"Option {option} requires a value.");
        }

        index++;
        return arguments[index];
    }
}
