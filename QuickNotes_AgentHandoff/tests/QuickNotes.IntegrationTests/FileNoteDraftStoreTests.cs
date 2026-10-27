using QuickNotes.Application;
using QuickNotes.Domain;
using QuickNotes.Infrastructure.Drafts;
using Xunit;

namespace QuickNotes.IntegrationTests;

public sealed class FileNoteDraftStoreTests
{
    [Fact]
    public async Task SaveGetListDelete_RoundTripsAllFields()
    {
        using var temporary = new TemporaryDirectory();
        var store = new FileNoteDraftStore(temporary.DraftDirectory);
        var noteId = Guid.NewGuid();
        var firstTag = Guid.NewGuid();
        var secondTag = Guid.NewGuid();
        var capturedAt = new DateTimeOffset(2026, 10, 27, 18, 30, 0, TimeSpan.FromHours(5.5));
        var first = new NoteDraft
        {
            DraftId = Guid.NewGuid(),
            NoteId = noteId,
            BaseVersion = 3,
            Title = null,
            Body = string.Empty,
            CategoryId = Category.UncategorizedId,
            TagIds = new[] { firstTag, secondTag },
            IsPinned = true,
            DisplayIntent = NoteDisplayIntent.Hidden,
            CapturedAtUtc = capturedAt
        };
        var second = first with
        {
            DraftId = Guid.NewGuid(),
            Title = "second",
            Body = "another body",
            IsPinned = false,
            DisplayIntent = NoteDisplayIntent.Visible,
            CapturedAtUtc = capturedAt.AddMinutes(5)
        };

        await store.SaveAsync(first);
        await store.SaveAsync(second);

        var loadedFirst = await store.GetAsync(first.DraftId);
        var loadedSecond = await store.GetAsync(second.DraftId);
        var listed = await store.ListAsync(noteId);

        Assert.NotNull(loadedFirst);
        AssertDraftEqual(first, loadedFirst);
        Assert.Equal(capturedAt.Offset, loadedFirst.CapturedAtUtc.Offset);
        AssertDraftEqual(second, loadedSecond!);
        Assert.Equal(2, listed.Count);
        Assert.Equal(second.DraftId, listed[0].DraftId);
        Assert.Equal(first.DraftId, listed[1].DraftId);

        await store.DeleteAsync(first.DraftId);

        Assert.Null(await store.GetAsync(first.DraftId));
        Assert.Single(await store.ListAsync(noteId));
    }

    [Fact]
    public async Task Save_WritesVersionedCamelCaseJsonWithoutBom()
    {
        using var temporary = new TemporaryDirectory();
        var store = new FileNoteDraftStore(temporary.DraftDirectory);
        var draft = NewDraft();

        await store.SaveAsync(draft);

        var path = Path.Combine(temporary.DraftDirectory, $"{draft.DraftId:D}.json");
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        var json = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"formatVersion\": 1", json);
        Assert.Contains("\"displayIntent\": \"Visible\"", json);
        Assert.DoesNotContain("DraftId", json);
    }

    [Fact]
    public async Task Save_ReplacesExistingDraftWithoutTemporaryFiles()
    {
        using var temporary = new TemporaryDirectory();
        var store = new FileNoteDraftStore(temporary.DraftDirectory);
        var draft = NewDraft();

        await store.SaveAsync(draft);
        var replacement = draft with { Body = "replacement", Title = null };
        await store.SaveAsync(replacement);

        AssertDraftEqual(replacement, (await store.GetAsync(draft.DraftId))!);
        var files = Directory.GetFiles(temporary.DraftDirectory);
        Assert.Single(files);
        Assert.EndsWith(".json", files[0], StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(temporary.DraftDirectory, "*.tmp"));
    }

    [Fact]
    public async Task Save_CreatesDraftDirectoryOnDemand()
    {
        using var temporary = new TemporaryDirectory();
        var store = new FileNoteDraftStore(temporary.DraftDirectory);
        var draft = NewDraft();

        Assert.False(Directory.Exists(temporary.DraftDirectory));
        await store.SaveAsync(draft);

        Assert.True(Directory.Exists(temporary.DraftDirectory));
        AssertDraftEqual(draft, (await store.GetAsync(draft.DraftId))!);
    }

    [Fact]
    public async Task List_MissingDirectory_ReturnsEmpty()
    {
        using var temporary = new TemporaryDirectory();
        var store = new FileNoteDraftStore(temporary.DraftDirectory);

        Assert.Empty(await store.ListAsync());
        Assert.Empty(await store.ListAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Delete_MissingDraftAndDirectory_IsIdempotent()
    {
        using var temporary = new TemporaryDirectory();
        var store = new FileNoteDraftStore(temporary.DraftDirectory);

        await store.DeleteAsync(Guid.NewGuid());

        var draft = NewDraft();
        await store.SaveAsync(draft);
        await store.DeleteAsync(draft.DraftId);
        await store.DeleteAsync(draft.DraftId);

        Assert.Null(await store.GetAsync(draft.DraftId));
    }

    [Fact]
    public async Task Get_CorruptJson_ThrowsDraftCorrupt()
    {
        using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(temporary.DraftDirectory);
        var draftId = Guid.NewGuid();
        var path = Path.Combine(temporary.DraftDirectory, $"{draftId:D}.json");
        await File.WriteAllTextAsync(path, "{ this is not json");
        var store = new FileNoteDraftStore(temporary.DraftDirectory);

        var exception = await Assert.ThrowsAsync<DraftStoreException>(() => store.GetAsync(draftId));

        Assert.Equal("draft_corrupt", exception.ErrorCode);
        Assert.Equal(draftId, exception.DraftId);
        Assert.Equal("get", exception.Operation);
    }

    [Fact]
    public async Task Get_UnsupportedFormatVersion_ThrowsDraftUnsupportedVersion()
    {
        using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(temporary.DraftDirectory);
        var draftId = Guid.NewGuid();
        var noteId = Guid.NewGuid();
        var path = Path.Combine(temporary.DraftDirectory, $"{draftId:D}.json");
        await File.WriteAllTextAsync(path, $$"""
            {
              "formatVersion": 2,
              "draftId": "{{draftId:D}}",
              "noteId": "{{noteId:D}}",
              "baseVersion": 0,
              "title": null,
              "body": "body",
              "categoryId": "{{Category.UncategorizedId:D}}",
              "tagIds": [],
              "isPinned": false,
              "displayIntent": "Visible",
              "capturedAtUtc": "2026-10-27T12:00:00+00:00"
            }
            """);
        var store = new FileNoteDraftStore(temporary.DraftDirectory);

        var exception = await Assert.ThrowsAsync<DraftStoreException>(() => store.GetAsync(draftId));

        Assert.Equal("draft_unsupported_version", exception.ErrorCode);
    }

    [Fact]
    public async Task Get_DraftIdMismatch_ThrowsDraftCorrupt()
    {
        using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(temporary.DraftDirectory);
        var draftId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var path = Path.Combine(temporary.DraftDirectory, $"{draftId:D}.json");
        await File.WriteAllTextAsync(path, $$"""
            {
              "formatVersion": 1,
              "draftId": "{{otherId:D}}",
              "noteId": "{{Guid.NewGuid():D}}",
              "baseVersion": 0,
              "title": null,
              "body": "body",
              "categoryId": "{{Category.UncategorizedId:D}}",
              "tagIds": [],
              "isPinned": false,
              "displayIntent": "Visible",
              "capturedAtUtc": "2026-10-27T12:00:00+00:00"
            }
            """);
        var store = new FileNoteDraftStore(temporary.DraftDirectory);

        var exception = await Assert.ThrowsAsync<DraftStoreException>(() => store.GetAsync(draftId));

        Assert.Equal("draft_corrupt", exception.ErrorCode);
    }

    [Fact]
    public async Task Get_InvalidDraftContent_ThrowsDraftCorrupt()
    {
        using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(temporary.DraftDirectory);
        var draftId = Guid.NewGuid();
        var path = Path.Combine(temporary.DraftDirectory, $"{draftId:D}.json");
        await File.WriteAllTextAsync(path, $$"""
            {
              "formatVersion": 1,
              "draftId": "{{draftId:D}}",
              "noteId": "{{Guid.NewGuid():D}}",
              "baseVersion": 0,
              "title": null,
              "body": null,
              "categoryId": "{{Category.UncategorizedId:D}}",
              "tagIds": [],
              "isPinned": false,
              "displayIntent": "Visible",
              "capturedAtUtc": "2026-10-27T12:00:00+00:00"
            }
            """);
        var store = new FileNoteDraftStore(temporary.DraftDirectory);

        var exception = await Assert.ThrowsAsync<DraftStoreException>(() => store.GetAsync(draftId));

        Assert.Equal("draft_corrupt", exception.ErrorCode);
    }

    [Fact]
    public async Task List_SortsByCapturedTimeThenDraftIdAndIgnoresOtherFiles()
    {
        using var temporary = new TemporaryDirectory();
        var store = new FileNoteDraftStore(temporary.DraftDirectory);
        var baseTime = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        var earlierA = NewDraft() with { CapturedAtUtc = baseTime };
        var earlierB = NewDraft() with { CapturedAtUtc = baseTime };
        var later = NewDraft() with { CapturedAtUtc = baseTime.AddMinutes(1) };
        await store.SaveAsync(earlierA);
        await store.SaveAsync(earlierB);
        await store.SaveAsync(later);
        await File.WriteAllTextAsync(Path.Combine(temporary.DraftDirectory, "notes.txt"), "ignored");
        await File.WriteAllTextAsync(Path.Combine(temporary.DraftDirectory, $"{Guid.NewGuid():N}.json.tmp"), "ignored");

        var listed = await store.ListAsync();

        Assert.Equal(3, listed.Count);
        Assert.Equal(later.DraftId, listed[0].DraftId);
        var earlierIds = listed.Skip(1).Select(draft => draft.DraftId).ToArray();
        Assert.Equal(earlierIds.OrderBy(id => id).ToArray(), earlierIds);
        Assert.Equal(
            new[] { earlierA.DraftId, earlierB.DraftId }.OrderBy(id => id).ToArray(),
            earlierIds);
    }

    [Fact]
    public async Task List_CorruptDraft_Fails()
    {
        using var temporary = new TemporaryDirectory();
        var store = new FileNoteDraftStore(temporary.DraftDirectory);
        await store.SaveAsync(NewDraft());
        await File.WriteAllTextAsync(
            Path.Combine(temporary.DraftDirectory, $"{Guid.NewGuid():D}.json"),
            "not json");

        var exception = await Assert.ThrowsAsync<DraftStoreException>(() => store.ListAsync());

        Assert.Equal("draft_corrupt", exception.ErrorCode);
    }

    [Fact]
    public async Task Save_WhenCancelled_KeepsExistingDraft()
    {
        using var temporary = new TemporaryDirectory();
        var store = new FileNoteDraftStore(temporary.DraftDirectory);
        var original = NewDraft();
        await store.SaveAsync(original);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var replacement = original with { Body = "replacement" };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.SaveAsync(replacement, cancellation.Token));

        AssertDraftEqual(original, (await store.GetAsync(original.DraftId))!);
        Assert.Empty(Directory.GetFiles(temporary.DraftDirectory, "*.tmp"));
    }

    [Fact]
    public async Task Delete_WhenCancelled_ThrowsWithoutDeleting()
    {
        using var temporary = new TemporaryDirectory();
        var store = new FileNoteDraftStore(temporary.DraftDirectory);
        var draft = NewDraft();
        await store.SaveAsync(draft);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.DeleteAsync(draft.DraftId, cancellation.Token));

        AssertDraftEqual(draft, (await store.GetAsync(draft.DraftId))!);
    }

    [Fact]
    public async Task SaveAndList_UseOnlyFilesInsideDraftDirectory()
    {
        using var temporary = new TemporaryDirectory();
        var store = new FileNoteDraftStore(temporary.DraftDirectory);
        var draft = NewDraft();

        await store.SaveAsync(draft);

        var files = Directory.GetFiles(temporary.DraftDirectory);
        Assert.Single(files);
        var name = Path.GetFileNameWithoutExtension(files[0]);
        Assert.Equal(draft.DraftId.ToString("D"), name);
        Assert.Empty(Directory.GetFiles(temporary.RootPath, "*.json"));
    }

    [Fact]
    public async Task InvalidInput_ThrowsArgumentException()
    {
        using var temporary = new TemporaryDirectory();
        var store = new FileNoteDraftStore(temporary.DraftDirectory);
        var valid = NewDraft();

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(valid with { DraftId = Guid.Empty }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(valid with { NoteId = Guid.Empty }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(valid with { CategoryId = Guid.Empty }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(valid with { Body = null! }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(valid with { BaseVersion = -1 }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(valid with { TagIds = null! }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(valid with { TagIds = new[] { Guid.Empty } }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(valid with { DisplayIntent = (NoteDisplayIntent)42 }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync(Guid.Empty));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync(Guid.Empty));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ListAsync(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new FileNoteDraftStore(" "));
    }

    private static NoteDraft NewDraft() => new()
    {
        DraftId = Guid.NewGuid(),
        NoteId = Guid.NewGuid(),
        BaseVersion = 2,
        Title = "title",
        Body = "body",
        CategoryId = Category.UncategorizedId,
        TagIds = Array.Empty<Guid>(),
        CapturedAtUtc = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero)
    };

    private static void AssertDraftEqual(NoteDraft expected, NoteDraft actual)
    {
        Assert.Equal(expected.DraftId, actual.DraftId);
        Assert.Equal(expected.NoteId, actual.NoteId);
        Assert.Equal(expected.BaseVersion, actual.BaseVersion);
        Assert.Equal(expected.Title, actual.Title);
        Assert.Equal(expected.Body, actual.Body);
        Assert.Equal(expected.CategoryId, actual.CategoryId);
        Assert.Equal(expected.TagIds.OrderBy(id => id), actual.TagIds.OrderBy(id => id));
        Assert.Equal(expected.IsPinned, actual.IsPinned);
        Assert.Equal(expected.DisplayIntent, actual.DisplayIntent);
        Assert.Equal(expected.CapturedAtUtc, actual.CapturedAtUtc);
        Assert.Equal(expected.CapturedAtUtc.Offset, actual.CapturedAtUtc.Offset);
    }
}
