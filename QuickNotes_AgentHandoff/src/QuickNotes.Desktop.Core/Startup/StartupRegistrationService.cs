namespace QuickNotes.Desktop.Startup;

public interface IRunRegistry
{
    string? Read(string valueName);
    void Write(string valueName, string value);
    void Delete(string valueName);
}

public sealed record StartupRegistrationStatus(
    bool Registered,
    string Command,
    string? ActualValue,
    string? ErrorCode);

public sealed class StartupRegistrationService
{
    private readonly IRunRegistry _registry;
    private readonly string _valueName;
    private readonly string _command;

    public StartupRegistrationService(
        IRunRegistry registry,
        string valueName,
        string command)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        if (string.IsNullOrWhiteSpace(valueName))
        {
            throw new ArgumentException("Value name is required.", nameof(valueName));
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("Command is required.", nameof(command));
        }

        _valueName = valueName;
        _command = command;
    }

    public StartupRegistrationStatus GetStatus()
    {
        try
        {
            var actual = _registry.Read(_valueName);
            return new StartupRegistrationStatus(
                actual is not null,
                _command,
                actual,
                null);
        }
        catch (Exception exception)
        {
            return new StartupRegistrationStatus(false, _command, null, exception.GetType().Name);
        }
    }

    public StartupRegistrationStatus SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                _registry.Write(_valueName, _command);
            }
            else
            {
                _registry.Delete(_valueName);
            }

            return GetStatus();
        }
        catch (Exception exception)
        {
            return new StartupRegistrationStatus(false, _command, null, exception.GetType().Name);
        }
    }

    public static string BuildCommand(string executablePath, string? entryAssemblyPath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("Executable path is required.", nameof(executablePath));
        }

        var executableName = Path.GetFileNameWithoutExtension(executablePath);
        if (string.Equals(executableName, "dotnet", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(entryAssemblyPath))
        {
            return $"\"{executablePath}\" \"{entryAssemblyPath}\" --startup";
        }

        return $"\"{executablePath}\" --startup";
    }
}
