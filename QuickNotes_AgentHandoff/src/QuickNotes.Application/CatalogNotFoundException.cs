namespace QuickNotes.Application;

public sealed class CatalogNotFoundException : NoteStorageException
{
    public CatalogNotFoundException(Guid id, string operation, Exception? inner = null)
        : base(
            operation,
            id,
            "catalog_not_found",
            $"Catalog entity {id} was not found during {operation}.",
            inner)
    {
    }
}
