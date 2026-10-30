using System.Globalization;
using QuickNotes.Application;
using QuickNotes.Domain;
using QuickNotes.Infrastructure.Sqlite;

namespace QuickNotes.C6.Harness;

/// <summary>
/// Creates synthetic C6 data through the real schema and repositories. The
/// factory never writes a hand-made database file: <see cref="SqliteSchema"/>
/// creates and validates the schema, and every note/tag/category goes through
/// the production repository contracts.
/// </summary>
public static class C6DataFactory
{
    public const string SyntheticBodyMarker = "C6-SYNTHETIC";

    private const int CategoryIdKind = 1;
    private const int TagIdKind = 2;
    private const int NoteIdKind = 3;

    private static readonly DateTimeOffset TimestampBase =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly (string Name, string Color)[] UserCategories =
    {
        ("C6 Work", "#DDEBF7"),
        ("C6 Life", "#E2EFDA"),
        ("C6 Ideas", "#FCE4D6")
    };

    private static readonly string[] TagNames =
    {
        "c6-tag-alpha",
        "c6-tag-beta",
        "c6-tag-gamma",
        "c6-tag-delta",
        "c6-tag-epsilon"
    };

    /// <summary>
    /// Seeds <paramref name="notes"/> notes into a fresh data root, of which
    /// the first <paramref name="visible"/> are active
    /// <see cref="NoteDisplayIntent.Visible"/> windows. The remaining history
    /// carries a fixed category, tag, lifecycle, appearance, and pin
    /// distribution so performance and restart probes are reproducible.
    /// </summary>
    public static async Task<SeedResult> SeedAsync(
        string dataRoot,
        int notes,
        int visible,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Data root must not be empty.", nameof(dataRoot));
        }

        if (notes < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(notes),
                notes,
                "Note count must be at least 1.");
        }

        if (visible < 0 || visible > notes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(visible),
                visible,
                "Visible note count must be between 0 and the note count.");
        }

        var fullDataRoot = Path.GetFullPath(dataRoot);
        var databasePath = Path.Combine(fullDataRoot, "notes.db");
        if (File.Exists(databasePath))
        {
            throw new InvalidOperationException(
                "The C6 data root already contains notes.db. Seed a fresh run directory.");
        }

        Directory.CreateDirectory(fullDataRoot);
        var connectionFactory = new SqliteConnectionFactory(databasePath);
        await new SqliteSchema(connectionFactory).InitializeAsync(cancellationToken);
        var catalog = new SqliteCatalogRepository(connectionFactory);
        var noteRepository = new SqliteNoteRepository(connectionFactory);

        // SqliteSchema seeds the system category; user categories are added
        // through the catalog repository so the fixed distribution is visible
        // to normal catalog queries.
        var categoryIds = new List<Guid>(UserCategories.Length + 1)
        {
            Category.UncategorizedId
        };
        for (var index = 0; index < UserCategories.Length; index++)
        {
            var category = new Category
            {
                Id = DeterministicId(CategoryIdKind, index),
                Name = UserCategories[index].Name,
                DefaultColor = UserCategories[index].Color,
                IsSystem = false
            };
            await catalog.CreateCategoryAsync(category, cancellationToken);
            categoryIds.Add(category.Id);
        }

        var tagIds = new Guid[TagNames.Length];
        for (var index = 0; index < TagNames.Length; index++)
        {
            var tag = new Tag
            {
                Id = DeterministicId(TagIdKind, index),
                Name = TagNames[index]
            };
            await catalog.CreateTagAsync(tag, cancellationToken);
            tagIds[index] = tag.Id;
        }

        var noteIds = new List<Guid>(notes);
        var visibleNoteIds = new List<Guid>(visible);
        for (var index = 0; index < notes; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isVisible = index < visible;
            var note = CreateNote(index, isVisible, categoryIds);
            await noteRepository.CreateAsync(
                note,
                SelectTagIds(index, tagIds),
                cancellationToken);
            var lifecycle = SelectLifecycle(index, visible);
            if (lifecycle != NoteLifecycle.Active)
            {
                // CreateAsync only inserts active notes; archived/trash rows
                // are produced through the real lifecycle repository method.
                await noteRepository.SetLifecycleAsync(
                    note.Id,
                    lifecycle,
                    expectedVersion: 1,
                    modifiedAtUtc: note.ModifiedAtUtc,
                    cancellationToken: cancellationToken);
            }

            noteIds.Add(note.Id);
            if (isVisible)
            {
                visibleNoteIds.Add(note.Id);
            }
        }

        return new SeedResult(noteIds.AsReadOnly(), visibleNoteIds.AsReadOnly());
    }

    private static Note CreateNote(
        int index,
        bool isVisible,
        IReadOnlyList<Guid> categoryIds)
    {
        var modifiedAtUtc = TimestampBase.AddMinutes(index * 17L);
        return new Note
        {
            Id = DeterministicId(NoteIdKind, index),
            Title = $"C6 synthetic note {index:D4}",
            Body = $"{SyntheticBodyMarker} body {index:D4}",
            CreatedAtUtc = modifiedAtUtc.AddSeconds(-(index % 60)),
            ModifiedAtUtc = modifiedAtUtc,
            Lifecycle = NoteLifecycle.Active,
            CategoryId = categoryIds[index % categoryIds.Count],
            IsPinned = !isVisible && index % 73 == 0,
            DisplayIntent = isVisible ? NoteDisplayIntent.Visible : NoteDisplayIntent.Hidden,
            ColorMode = index % 4 == 0 ? NoteColorMode.Manual : NoteColorMode.FollowCategory,
            ColorHex = index % 4 == 0 ? "#FFE699" : null,
            FontSize = index % 3 == 0 ? 16 : null,
            WindowLayout = isVisible ? CreateWindowLayout(index) : null
        };
    }

    private static NoteLifecycle SelectLifecycle(int index, int visibleCount)
    {
        if (index < visibleCount)
        {
            return NoteLifecycle.Active;
        }

        return (index % 100) switch
        {
            < 3 => NoteLifecycle.Trash,
            < 9 => NoteLifecycle.Archived,
            _ => NoteLifecycle.Active
        };
    }

    private static NoteWindowLayout CreateWindowLayout(int index)
    {
        const double width = 320;
        const double height = 240;
        const double gapX = 20;
        const double gapY = 20;
        const double originX = 40;
        const double originY = 40;
        var column = index % 5;
        var row = index / 5;
        return new NoteWindowLayout
        {
            Left = originX + column * (width + gapX),
            Top = originY + row * (height + gapY),
            Width = width,
            Height = height
        };
    }

    private static Guid[] SelectTagIds(int index, IReadOnlyList<Guid> tagIds) =>
        (index % 5) switch
        {
            0 => Array.Empty<Guid>(),
            1 => new[] { tagIds[0] },
            2 => new[] { tagIds[1], tagIds[2] },
            3 => new[] { tagIds[2], tagIds[4] },
            _ => new[] { tagIds[0], tagIds[3], tagIds[4] }
        };

    private static Guid DeterministicId(int kind, int index) =>
        Guid.Parse(
            string.Create(
                CultureInfo.InvariantCulture,
                $"c6000000-{kind:x4}-0000-0000-{index:x12}"));
}

public sealed record SeedResult(
    IReadOnlyList<Guid> NoteIds,
    IReadOnlyList<Guid> VisibleNoteIds);
