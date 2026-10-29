using QuickNotes.Application;
using QuickNotes.Domain;

namespace QuickNotes.UnitTests;

internal sealed class FakeCatalogRepository : ICatalogRepository
{
    public Dictionary<Guid, Category> Categories { get; } = new();
    public Dictionary<Guid, Tag> Tags { get; } = new();
    public List<(Guid Source, Guid Target)> Merges { get; } = new();

    public Task CreateCategoryAsync(Category category, CancellationToken cancellationToken = default)
    {
        Categories[category.Id] = category;
        return Task.CompletedTask;
    }

    public Task UpdateCategoryAsync(
        Guid id,
        string name,
        string defaultColor,
        CancellationToken cancellationToken = default)
    {
        if (!Categories.TryGetValue(id, out var category))
        {
            throw new CatalogNotFoundException(id, "update_category");
        }

        Categories[id] = category with { Name = name, DefaultColor = defaultColor };
        return Task.CompletedTask;
    }

    public Task DeleteCategoryAsync(
        Guid id,
        Guid reassignCategoryId,
        CancellationToken cancellationToken = default)
    {
        Categories.Remove(id);
        return Task.CompletedTask;
    }

    public Task CreateTagAsync(Tag tag, CancellationToken cancellationToken = default)
    {
        Tags[tag.Id] = tag;
        return Task.CompletedTask;
    }

    public Task UpdateTagAsync(Guid id, string name, CancellationToken cancellationToken = default)
    {
        if (!Tags.TryGetValue(id, out var tag))
        {
            throw new CatalogNotFoundException(id, "update_tag");
        }

        Tags[id] = tag with { Name = name };
        return Task.CompletedTask;
    }

    public Task DeleteTagAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Tags.Remove(id);
        return Task.CompletedTask;
    }

    public Task<Category?> GetCategoryAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Categories.TryGetValue(id, out var category) ? category : null);

    public Task<Tag?> GetTagAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Tags.TryGetValue(id, out var tag) ? tag : null);

    public Task<IReadOnlyList<Category>> ListCategoriesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Category>>(Categories.Values.ToArray());

    public Task<IReadOnlyList<Tag>> ListTagsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Tag>>(Tags.Values.ToArray());

    public Task MergeTagAsync(
        Guid sourceTagId,
        Guid targetTagId,
        CancellationToken cancellationToken = default)
    {
        Merges.Add((sourceTagId, targetTagId));
        Tags.Remove(sourceTagId);
        return Task.CompletedTask;
    }
}
