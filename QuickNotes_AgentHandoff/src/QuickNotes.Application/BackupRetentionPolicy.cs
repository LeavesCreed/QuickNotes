namespace QuickNotes.Application;

public sealed class BackupRetentionPolicy : IBackupRetentionPolicy
{
    public const int DefaultDailyKeepCount = 7;
    public const int DefaultWeeklyKeepCount = 4;

    private readonly int _dailyKeepCount;
    private readonly int _weeklyKeepCount;

    public BackupRetentionPolicy(
        int dailyKeepCount = DefaultDailyKeepCount,
        int weeklyKeepCount = DefaultWeeklyKeepCount)
    {
        if (dailyKeepCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dailyKeepCount),
                dailyKeepCount,
                "Daily keep count must be positive.");
        }

        if (weeklyKeepCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(weeklyKeepCount),
                weeklyKeepCount,
                "Weekly keep count must be positive.");
        }

        _dailyKeepCount = dailyKeepCount;
        _weeklyKeepCount = weeklyKeepCount;
    }

    public BackupRetentionPlan CreatePlan(
        IReadOnlyList<BackupCatalogEntry> entries,
        IReadOnlyCollection<string> protectedPackagePaths)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(protectedPackagePaths);

        var protectedPaths = new HashSet<string>(
            protectedPackagePaths,
            StringComparer.OrdinalIgnoreCase);
        var dailyEntries = entries
            .Where(entry => entry.Manifest.BackupType == BackupType.Daily)
            .ToList();
        if (dailyEntries.Count == 0)
        {
            return new BackupRetentionPlan(
                Array.Empty<BackupCatalogEntry>(),
                Array.Empty<BackupCatalogEntry>());
        }

        var latestPerDate = dailyEntries
            .GroupBy(entry => entry.LocalDate)
            .Select(group => group
                .OrderByDescending(entry => entry.Manifest.CreatedAtUtc)
                .ThenBy(entry => entry.PackagePath, StringComparer.Ordinal)
                .First())
            .OrderByDescending(entry => entry.LocalDate)
            .ToList();
        var dailyKeep = latestPerDate.Take(_dailyKeepCount).ToList();
        var weeklyKeep = latestPerDate
            .GroupBy(entry => (entry.IsoWeekYear, entry.IsoWeek))
            .Select(group => group
                .OrderByDescending(entry => entry.Manifest.CreatedAtUtc)
                .ThenBy(entry => entry.PackagePath, StringComparer.Ordinal)
                .First())
            .OrderByDescending(GetWeekStart)
            .ThenByDescending(entry => entry.Manifest.CreatedAtUtc)
            .Take(_weeklyKeepCount)
            .ToList();

        var keepByPath = new Dictionary<string, BackupCatalogEntry>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var entry in dailyKeep.Concat(weeklyKeep))
        {
            keepByPath[entry.PackagePath] = entry;
        }

        foreach (var entry in dailyEntries.Where(entry => protectedPaths.Contains(entry.PackagePath)))
        {
            keepByPath[entry.PackagePath] = entry;
        }

        var keep = keepByPath.Values
            .OrderByDescending(entry => entry.LocalDate)
            .ThenBy(entry => entry.PackagePath, StringComparer.Ordinal)
            .ToList();
        var delete = dailyEntries
            .Where(entry => !keepByPath.ContainsKey(entry.PackagePath))
            .OrderBy(entry => entry.LocalDate)
            .ThenBy(entry => entry.PackagePath, StringComparer.Ordinal)
            .ToList();
        return new BackupRetentionPlan(keep, delete);
    }

    private static DateOnly GetWeekStart(BackupCatalogEntry entry)
    {
        var date = entry.LocalDate;
        var daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-daysSinceMonday);
    }
}
