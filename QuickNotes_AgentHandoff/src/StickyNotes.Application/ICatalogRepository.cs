using StickyNotes.Domain;

namespace StickyNotes.Application;

public interface ICatalogRepository
{
    Task CreateCategoryAsync(Category category, CancellationToken cancellationToken = default);
    Task CreateTagAsync(Tag tag, CancellationToken cancellationToken = default);
    Task<Category?> GetCategoryAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Tag?> GetTagAsync(Guid id, CancellationToken cancellationToken = default);
}
