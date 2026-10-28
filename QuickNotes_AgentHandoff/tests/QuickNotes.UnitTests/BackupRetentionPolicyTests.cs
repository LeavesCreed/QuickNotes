using System.Globalization;
using QuickNotes.Application;
using Xunit;

namespace QuickNotes.UnitTests;

public sealed class BackupRetentionPolicyTests
{
    private static readonly DateOnly WeekBoundarySunday = new(2026, 10, 4);

    [Fact]
    public void CreatePlan_FewerThanLimits_KeepsAll()
    {
        var entries = new[]
        {
            Entry("daily-01", new DateOnly(2026, 10, 1)),
            Entry("daily-02", new DateOnly(2026, 10, 2)),
            Entry("daily-03", new DateOnly(2026, 10, 3))
        };

        var plan = new BackupRetentionPolicy().CreatePlan(entries, Array.Empty<string>());

        Assert.Equal(3, plan.Keep.Count);
        Assert.Empty(plan.Delete);
    }

    [Fact]
    public void CreatePlan_KeepsSevenMostRecentDates_AndDeletesOlder()
    {
        var entries = Enumerable.Range(1, 8)
            .Select(day => Entry($"daily-{day:00}", new DateOnly(2026, 10, day)))
            .ToArray();

        var plan = new BackupRetentionPolicy().CreatePlan(entries, Array.Empty<string>());

        Assert.Equal(7, plan.Keep.Count);
        var deleted = Assert.Single(plan.Delete);
        Assert.Equal("daily-01", deleted.PackagePath);
        Assert.All(plan.Keep, entry => Assert.NotEqual("daily-01", entry.PackagePath));
    }

    [Fact]
    public void CreatePlan_KeepsFourWeeklyRepresentativesOutsideSevenDaily()
    {
        var dates = new[]
        {
            new DateOnly(2026, 9, 1),
            new DateOnly(2026, 9, 8),
            new DateOnly(2026, 9, 15),
            new DateOnly(2026, 9, 22),
            new DateOnly(2026, 9, 29),
            new DateOnly(2026, 9, 30),
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 2),
            new DateOnly(2026, 10, 3),
            new DateOnly(2026, 10, 4),
            new DateOnly(2026, 10, 5),
            new DateOnly(2026, 10, 6)
        };
        var entries = dates.Select(date => Entry($"daily-{date:yyyyMMdd}", date)).ToArray();

        var plan = new BackupRetentionPolicy().CreatePlan(entries, Array.Empty<string>());

        var keptPaths = plan.Keep.Select(entry => entry.PackagePath).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(9, keptPaths.Count);
        Assert.Contains("daily-20260915", keptPaths);
        Assert.Contains("daily-20260922", keptPaths);
        Assert.DoesNotContain("daily-20260901", keptPaths);
        Assert.DoesNotContain("daily-20260908", keptPaths);
        Assert.DoesNotContain("daily-20260929", keptPaths);
        Assert.Equal(3, plan.Delete.Count);
        Assert.Equal(
            new[] { "daily-20260901", "daily-20260908", "daily-20260929" },
            plan.Delete.Select(entry => entry.PackagePath).ToArray());
    }

    [Fact]
    public void CreatePlan_SameDayDuplicates_KeepsLatestAndDeletesOlder()
    {
        var entries = Enumerable.Range(1, 8)
            .Select(day => Entry($"daily-{day:00}", new DateOnly(2026, 10, day)))
            .ToList();
        entries.Add(Entry(
            "daily-05-early",
            new DateOnly(2026, 10, 5),
            createdAtUtc: new DateTimeOffset(2026, 10, 5, 1, 0, 0, TimeSpan.Zero)));
        entries.Add(Entry(
            "daily-05-late",
            new DateOnly(2026, 10, 5),
            createdAtUtc: new DateTimeOffset(2026, 10, 5, 23, 0, 0, TimeSpan.Zero)));

        var plan = new BackupRetentionPolicy().CreatePlan(entries, Array.Empty<string>());

        var keptPaths = plan.Keep.Select(entry => entry.PackagePath).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("daily-05-late", keptPaths);
        Assert.DoesNotContain("daily-05-early", keptPaths);
        Assert.Contains(plan.Delete, entry => entry.PackagePath == "daily-05-early");
    }

    [Fact]
    public void CreatePlan_ExcludesManualBackupsFromKeepAndDelete()
    {
        var entries = new[]
        {
            Entry("daily-01", new DateOnly(2026, 10, 1)),
            Entry("daily-02", new DateOnly(2026, 10, 2)),
            Entry("manual-01", new DateOnly(2026, 10, 1), BackupType.Manual)
        };

        var plan = new BackupRetentionPolicy().CreatePlan(entries, Array.Empty<string>());

        Assert.DoesNotContain(plan.Keep, entry => entry.PackagePath == "manual-01");
        Assert.DoesNotContain(plan.Delete, entry => entry.PackagePath == "manual-01");
    }

    [Fact]
    public void CreatePlan_ProtectsNewPackageOutsideKeepSet()
    {
        var dates = new[]
        {
            new DateOnly(2026, 9, 1),
            new DateOnly(2026, 9, 8),
            new DateOnly(2026, 9, 15),
            new DateOnly(2026, 9, 22),
            new DateOnly(2026, 9, 29),
            new DateOnly(2026, 9, 30),
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 2),
            new DateOnly(2026, 10, 3),
            new DateOnly(2026, 10, 4),
            new DateOnly(2026, 10, 5),
            new DateOnly(2026, 10, 6)
        };
        var entries = dates.Select(date => Entry($"daily-{date:yyyyMMdd}", date)).ToArray();

        var plan = new BackupRetentionPolicy().CreatePlan(
            entries,
            new[] { "daily-20260929" });

        Assert.Contains(plan.Keep, entry => entry.PackagePath == "daily-20260929");
        Assert.DoesNotContain(plan.Delete, entry => entry.PackagePath == "daily-20260929");
        Assert.Equal(2, plan.Delete.Count);
    }

    [Fact]
    public void CreatePlan_WeekBoundaryUsesIsoMondayAndKeepsSundayRepresentative()
    {
        var dates = new[]
        {
            WeekBoundarySunday,
            new DateOnly(2026, 10, 5),
            new DateOnly(2026, 10, 6),
            new DateOnly(2026, 10, 7),
            new DateOnly(2026, 10, 8),
            new DateOnly(2026, 10, 9),
            new DateOnly(2026, 10, 10),
            new DateOnly(2026, 10, 11)
        };
        var entries = dates.Select(date => Entry($"daily-{date:yyyyMMdd}", date)).ToArray();

        var plan = new BackupRetentionPolicy().CreatePlan(entries, Array.Empty<string>());

        Assert.Contains(plan.Keep, entry => entry.PackagePath == "daily-20261004");
        Assert.Empty(plan.Delete);
        Assert.Equal(8, plan.Keep.Count);
    }

    [Fact]
    public void CreatePlan_KeepsWeeklyRepresentativeAcrossIsoYearBoundary()
    {
        var entries = new List<BackupCatalogEntry>
        {
            Entry("daily-20261228", new DateOnly(2026, 12, 28))
        };
        for (var day = 4; day <= 10; day++)
        {
            entries.Add(Entry(
                $"daily-202701{day:00}",
                new DateOnly(2027, 1, day)));
        }

        var plan = new BackupRetentionPolicy().CreatePlan(entries, Array.Empty<string>());

        Assert.Contains(plan.Keep, entry => entry.PackagePath == "daily-20261228");
        Assert.Empty(plan.Delete);
    }

    [Fact]
    public void CreatePlan_NoDailyEntries_ReturnsEmpty()
    {
        var plan = new BackupRetentionPolicy().CreatePlan(
            new[] { Entry("manual-01", new DateOnly(2026, 10, 1), BackupType.Manual) },
            Array.Empty<string>());

        Assert.Empty(plan.Keep);
        Assert.Empty(plan.Delete);
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(-1, 4)]
    [InlineData(7, 0)]
    [InlineData(7, -1)]
    public void Constructor_InvalidCounts_Throw(int dailyKeepCount, int weeklyKeepCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BackupRetentionPolicy(dailyKeepCount, weeklyKeepCount));
    }

    private static BackupCatalogEntry Entry(
        string path,
        DateOnly localDate,
        BackupType type = BackupType.Daily,
        DateTimeOffset? createdAtUtc = null)
    {
        var local = new DateTimeOffset(
            localDate.ToDateTime(new TimeOnly(12, 0)),
            TimeSpan.FromHours(8));
        var manifest = new BackupManifest
        {
            FormatVersion = BackupManifest.CurrentFormatVersion,
            BackupType = type,
            CreatedAtUtc = createdAtUtc ?? local.ToUniversalTime(),
            CreatedAtLocal = local,
            SchemaVersion = 1,
            DatabaseFileName = BackupManifest.DatabaseEntryName,
            DatabaseSizeBytes = 4096,
            DatabaseSha256 = new string('a', 64)
        };
        return new BackupCatalogEntry
        {
            PackagePath = path,
            Manifest = manifest,
            LocalDate = localDate,
            IsoWeekYear = ISOWeek.GetYear(local.DateTime),
            IsoWeek = ISOWeek.GetWeekOfYear(local.DateTime)
        };
    }
}
