using System.Globalization;

namespace QuickNotes.C6.Harness;

/// <summary>
/// Writes checkpoint marker files consumed by the external C6 kill driver.
/// A marker only ever contains a UTC timestamp; it never contains note body,
/// draft content, or paths. The final file is published with an atomic move
/// from a uniquely named temporary file on the same volume.
/// </summary>
public sealed class CheckpointFileSink
{
    private readonly string _directory;

    public CheckpointFileSink(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("Checkpoint directory must not be empty.", nameof(directory));
        }

        _directory = Path.GetFullPath(directory);
    }

    public void Reached(string name)
    {
        ValidateName(name);
        Directory.CreateDirectory(_directory);

        var target = Path.Combine(_directory, $"{name}.reached");
        var temporary = $"{target}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(
            temporary,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        File.Move(temporary, target, overwrite: true);
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Checkpoint name must not be empty.", nameof(name));
        }

        if (name is "." or ".."
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Contains('/')
            || name.Contains('\\')
            || name != Path.GetFileName(name))
        {
            throw new ArgumentException(
                "Checkpoint name must be a simple file name without path separators.",
                nameof(name));
        }
    }
}
