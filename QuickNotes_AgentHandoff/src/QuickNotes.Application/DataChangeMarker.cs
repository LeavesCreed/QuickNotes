using System.Globalization;

namespace QuickNotes.Application;

public sealed record DataChangeMarker(
    long SchemaVersion,
    Guid DatabaseId,
    long ChangeSequence)
{
    public string ToFingerprint() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"schema={SchemaVersion};db={DatabaseId:D};seq={ChangeSequence}");
}
