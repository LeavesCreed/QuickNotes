namespace QuickNotes.C6.Harness;

/// <summary>
/// Hard guard for the realized C6 scenarios. Data, drafts, checkpoints, and
/// backup destinations must all live under
/// <c>%TEMP%\QuickNotes-C6</c>; a mistyped path can therefore never point the
/// fault harness at a real QuickNotes data directory.
/// </summary>
public static class C6HarnessPaths
{
    public static string RunRoot =>
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), C6DataFactory.RunRootSegment));

    public static string ResolveRunId(HarnessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.IsNullOrWhiteSpace(options.RunId))
        {
            return options.RunId;
        }

        var segments = Path.GetFullPath(options.DataRoot).Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (string.Equals(
                    segments[index],
                    C6DataFactory.RunRootSegment,
                    StringComparison.OrdinalIgnoreCase))
            {
                return segments[index + 1];
            }
        }

        return segments.Length == 0 ? C6DataFactory.DefaultTestRunId : segments[^1];
    }

    public static string RequireUnderRunRoot(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path must not be empty.", parameterName);
        }

        var fullPath = Path.GetFullPath(path);
        var root = RunRoot;
        if (!IsStrictDescendant(fullPath, root))
        {
            throw new ArgumentException(
                $"C6 scenario paths must live under '{root}'. Received '{fullPath}'.",
                parameterName);
        }

        return fullPath;
    }

    private static bool IsStrictDescendant(string path, string root)
    {
        var normalizedRoot = root.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                path.TrimEnd(Path.DirectorySeparatorChar),
                normalizedRoot.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
    }
}
