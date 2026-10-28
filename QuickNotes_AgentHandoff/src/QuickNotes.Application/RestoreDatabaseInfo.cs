namespace QuickNotes.Application;

public sealed record RestoreDatabaseInfo(long SchemaVersion, RestoreCounts Counts);
