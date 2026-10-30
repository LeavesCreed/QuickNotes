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
    private readonly string? _holdMarker;
    private readonly TimeSpan _holdTimeout;
    private int _held;

    /// <summary>
    /// Creates a marker sink. When <paramref name="holdMarker"/> is supplied
    /// the harness blocks after writing that one marker until the external
    /// process-kill driver terminates it. The hold is what makes a checkpoint
    /// kill deterministic instead of racing a fast scenario; it is enabled
    /// only by the opt-in kill driver and never by normal scenario runs.
    /// </summary>
    public CheckpointFileSink(
        string directory,
        string? holdMarker = null,
        TimeSpan? holdTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("Checkpoint directory must not be empty.", nameof(directory));
        }

        _directory = Path.GetFullPath(directory);
        _holdMarker = string.IsNullOrWhiteSpace(holdMarker) ? null : holdMarker;
        _holdTimeout = holdTimeout ?? TimeSpan.FromMinutes(2);
        if (_holdTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(holdTimeout),
                _holdTimeout,
                "Checkpoint hold timeout must be positive.");
        }
    }

    public string Directory => _directory;

    public void Reached(string name)
    {
        ValidateName(name);
        System.IO.Directory.CreateDirectory(_directory);

        var target = Path.Combine(_directory, $"{name}.reached");
        var temporary = $"{target}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(
            temporary,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        File.Move(temporary, target, overwrite: true);

        if (_holdMarker is not null
            && string.Equals(name, _holdMarker, StringComparison.Ordinal)
            && Interlocked.Exchange(ref _held, 1) == 0)
        {
            HoldForKiller(name);
        }
    }

    private void HoldForKiller(string name)
    {
        var deadline = DateTime.UtcNow + _holdTimeout;
        while (DateTime.UtcNow < deadline)
        {
            // The external driver watches for <name>.reached and kills the
            // harness process tree. If no driver arrives the scenario fails
            // instead of hanging forever.
            Thread.Sleep(50);
        }

        throw new TimeoutException(
            $"The checkpoint '{name}' was not consumed by a process-kill driver within {_holdTimeout}.");
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
