using QuickNotes.Application;
using QuickNotes.Domain;

namespace QuickNotes.UnitTests;

public sealed class NoteManagementServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Archive_UsesHiddenIntentAndExpectedVersion()
    {
        var note = TestData.NewNote();
        NoteLifecycle? recordedLifecycle = null;
        NoteDisplayIntent? recordedIntent = null;
        long? recordedVersion = null;
        var notes = new FakeNoteRepository
        {
            GetHandler = (_, _) => Task.FromResult<Note?>(note),
            GetTagIdsHandler = (_, _) => Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>()),
            SetLifecycleAndDisplayHandler = (id, lifecycle, intent, version, modifiedAt, _) =>
            {
                recordedLifecycle = lifecycle;
                recordedIntent = intent;
                recordedVersion = version;
                return Task.FromResult(note with
                {
                    Id = id,
                    Lifecycle = lifecycle,
                    DisplayIntent = intent,
                    Version = version + 1,
                    ModifiedAtUtc = modifiedAt
                });
            }
        };
        var service = new NoteManagementService(
            notes,
            new FakeCatalogRepository(),
            new DatabaseWriteGate(),
            TimeProvider.System);

        var result = await service.ArchiveAsync(note.Id, 3);

        Assert.Equal(NoteLifecycle.Archived, recordedLifecycle);
        Assert.Equal(NoteDisplayIntent.Hidden, recordedIntent);
        Assert.Equal(3, recordedVersion);
        Assert.Equal(4, result.Version);
    }

    [Fact]
    public async Task AddTags_MissingTag_ThrowsCatalogNotFound()
    {
        var note = TestData.NewNote();
        var notes = new FakeNoteRepository
        {
            GetHandler = (_, _) => Task.FromResult<Note?>(note)
        };
        var service = new NoteManagementService(
            notes,
            new FakeCatalogRepository(),
            new DatabaseWriteGate(),
            TimeProvider.System);

        await Assert.ThrowsAsync<CatalogNotFoundException>(() =>
            service.AddTagsAsync(note.Id, new[] { Guid.NewGuid() }, 1));
    }

    [Fact]
    public async Task CreateCopy_CopiesContentTagsAppearanceAndOffsetsLayout()
    {
        var note = TestData.NewNote() with
        {
            Title = "原便签",
            ColorMode = NoteColorMode.Manual,
            ColorHex = "#FFCC00",
            FontSize = 18,
            WindowLayout = new NoteWindowLayout
            {
                Left = 100,
                Top = 200,
                Width = 300,
                Height = 200
            }
        };
        var tagId = Guid.NewGuid();
        Note? created = null;
        var notes = new FakeNoteRepository
        {
            GetHandler = (_, _) => Task.FromResult<Note?>(note),
            GetTagIdsHandler = (_, _) => Task.FromResult<IReadOnlyList<Guid>>(new[] { tagId }),
            QueryHandler = (_, _) => Task.FromResult<IReadOnlyList<Note>>(Array.Empty<Note>()),
            CreateHandler = (copy, tags, _) =>
            {
                created = copy;
                return Task.FromResult(copy with { Version = 1 });
            }
        };
        var service = new NoteManagementService(
            notes,
            new FakeCatalogRepository(),
            new DatabaseWriteGate(),
            TimeProvider.System);

        var copy = await service.CreateCopyAsync(note.Id);

        Assert.NotNull(created);
        Assert.NotEqual(note.Id, copy.Id);
        Assert.Equal("原便签（副本）", copy.Title);
        Assert.Equal(note.Body, copy.Body);
        Assert.Equal(NoteColorMode.Manual, copy.ColorMode);
        Assert.Equal(note.FontSize, copy.FontSize);
        Assert.Equal(128, copy.WindowLayout!.Left);
        Assert.Equal(228, copy.WindowLayout.Top);
    }
}
