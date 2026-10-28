using QuickNotes.Domain;

namespace QuickNotes.Application;

public interface ICatalogRepository
{
    Task CreateCategoryAsync(Category category, CancellationToken cancellationToken = default);
    Task UpdateCategoryAsync(Guid id, string name, string defaultColor, CancellationToken cancellationToken = default);
    Task DeleteCategoryAsync(Guid id, Guid reassignCategoryId, CancellationToken cancellationToken = default);
    Task CreateTagAsync(Tag tag, CancellationToken cancellationToken = default);
    Task UpdateTagAsync(Guid id, string name, CancellationToken cancellationToken = default);
    Task DeleteTagAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Category?> GetCategoryAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Tag?> GetTagAsync(Guid id, CancellationToken cancellationToken = default);
}
