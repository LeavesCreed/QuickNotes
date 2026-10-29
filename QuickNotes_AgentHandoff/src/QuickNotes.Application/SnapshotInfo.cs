namespace QuickNotes.Application;

/// <summary>
/// Describes a verified snapshot. <see cref="SchemaVersion"/> is the schema
/// version observed in the snapshot; <see cref="Marker"/> is the change marker
/// observed inside the snapshot. Schema v1 protection snapshots (taken before
/// an in-place migration) have no <c>database_meta</c> table and therefore no
/// marker.
/// </summary>
public sealed record SnapshotInfo(long SchemaVersion, DataChangeMarker? Marker)
{
    public SnapshotInfo(DataChangeMarker marker)
        : this(
            (marker ?? throw new ArgumentNullException(nameof(marker))).SchemaVersion,
            marker)
    {
    }

    public bool HasChangeMarker => Marker is not null;

    /// <summary>
    /// Fingerprint of the change marker captured inside the snapshot, or
    /// <c>null</c> when the snapshot has no marker (schema v1).
    /// </summary>
    public string? ToFingerprint() => Marker?.ToFingerprint();
}
