# B1 SQLite Persistence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build and verify the minimal domain model and transactional SQLite persistence for notes, categories, tags, lifecycle state, schema versioning, and optimistic concurrency.

**Architecture:** Domain entities have no UI or database dependencies. Application defines repository contracts and conflict semantics; Infrastructure.Sqlite implements them with Microsoft.Data.Sqlite, explicit transactions, per-connection foreign keys, and SQL-side version checks. Integration tests use synthetic data and a new temporary directory per test.

**Tech Stack:** C# 14, .NET 10.0, Microsoft.Data.Sqlite 10.0.0, xUnit 2.9.3, Microsoft.NET.Test.Sdk 17.13.0.

**Spec:** `QuickNotes_AgentHandoff/docs/agent/Agent对接文档_B阶段.md`, especially B0/B1; product behavior is constrained by `QuickNotes_AgentHandoff/docs/product/PRD_v1.0.md` and `QuickNotes_AgentHandoff/AGENTS.md`.

## Global Constraints

- Run all commands from `QuickNotes_AgentHandoff/`; do not modify the existing `WindowsWindowTrayPrototype/` project.
- Only implement B0/B1. Do not implement backup/restore, WPF UI, tray, hotkeys, installer, export, or Rust FFI.
- PRD v1.0 remains unchanged. SQLite remains a candidate validated by this prototype, not a frozen final product decision.
- Use stable GUID primary keys and round-trip UTC timestamps. Lifecycle state and display intent are independent; hidden is not archived or deleted.
- Every note has exactly one category. Seed a stable, system-owned “未分类” category.
- Enable `PRAGMA foreign_keys=ON` on every connection and use `PRAGMA user_version=1` for the initial schema.
- Updates use an expected version and an atomic `WHERE version=@expectedVersion` guard. Stale writes throw `NoteConcurrencyException`.
- Normal deletion means lifecycle `Trash`; do not add a physical note deletion API in B1.
- All association changes are transactional. A failed association write rolls back the note update and prior association changes.
- Tests use only temporary directories and synthetic content. Do not access real user notes.
- Do not create git commits unless the user explicitly requests them.

## File Structure

- `QuickNotes.DataPrototype.sln`: builds the four B1 projects without referencing the WPF prototype.
- `src/QuickNotes.Domain/`: note/category/tag entities and lifecycle/display enums.
- `src/QuickNotes.Application/`: repository contracts and `NoteConcurrencyException`.
- `src/QuickNotes.Infrastructure.Sqlite/`: connection factory, schema initialization, catalog repository, note repository.
- `tests/QuickNotes.IntegrationTests/`: temporary database fixture plus schema, CRUD, transaction, conflict, and reopen tests.

---

### Task 1: Domain Model and Versioned SQLite Schema

**Files:**
- Create: `QuickNotes.DataPrototype.sln`
- Create: `src/QuickNotes.Domain/QuickNotes.Domain.csproj`
- Create: `src/QuickNotes.Domain/NoteLifecycle.cs`
- Create: `src/QuickNotes.Domain/NoteDisplayIntent.cs`
- Create: `src/QuickNotes.Domain/Note.cs`
- Create: `src/QuickNotes.Domain/Category.cs`
- Create: `src/QuickNotes.Domain/Tag.cs`
- Create: `src/QuickNotes.Domain/NoteTag.cs`
- Create: `src/QuickNotes.Infrastructure.Sqlite/QuickNotes.Infrastructure.Sqlite.csproj`
- Create: `src/QuickNotes.Infrastructure.Sqlite/SqliteConnectionFactory.cs`
- Create: `src/QuickNotes.Infrastructure.Sqlite/SqliteSchema.cs`
- Test: `tests/QuickNotes.IntegrationTests/SqliteSchemaTests.cs`
- Test: `tests/QuickNotes.IntegrationTests/TemporaryDatabase.cs`

**Interfaces:**
- Consumes: installed .NET 10 SDK and NuGet.org source verified during B0.
- Produces: `SqliteConnectionFactory.OpenAsync(CancellationToken): Task<SqliteConnection>`, `SqliteSchema.InitializeAsync(CancellationToken): Task`, `Category.UncategorizedId`, and all domain records/enums used by later tasks.

- [ ] **Step 1: Scaffold the solution and projects**

```bash
dotnet new sln -n QuickNotes.DataPrototype
dotnet new classlib -n QuickNotes.Domain -o src/QuickNotes.Domain -f net10.0
dotnet new classlib -n QuickNotes.Application -o src/QuickNotes.Application -f net10.0
dotnet new classlib -n QuickNotes.Infrastructure.Sqlite -o src/QuickNotes.Infrastructure.Sqlite -f net10.0
dotnet new xunit -n QuickNotes.IntegrationTests -o tests/QuickNotes.IntegrationTests -f net10.0
dotnet sln QuickNotes.DataPrototype.sln add src/QuickNotes.Domain/QuickNotes.Domain.csproj src/QuickNotes.Application/QuickNotes.Application.csproj src/QuickNotes.Infrastructure.Sqlite/QuickNotes.Infrastructure.Sqlite.csproj tests/QuickNotes.IntegrationTests/QuickNotes.IntegrationTests.csproj
dotnet add src/QuickNotes.Application/QuickNotes.Application.csproj reference src/QuickNotes.Domain/QuickNotes.Domain.csproj
dotnet add src/QuickNotes.Infrastructure.Sqlite/QuickNotes.Infrastructure.Sqlite.csproj reference src/QuickNotes.Domain/QuickNotes.Domain.csproj src/QuickNotes.Application/QuickNotes.Application.csproj
dotnet add tests/QuickNotes.IntegrationTests/QuickNotes.IntegrationTests.csproj reference src/QuickNotes.Domain/QuickNotes.Domain.csproj src/QuickNotes.Application/QuickNotes.Application.csproj src/QuickNotes.Infrastructure.Sqlite/QuickNotes.Infrastructure.Sqlite.csproj
dotnet add src/QuickNotes.Infrastructure.Sqlite/QuickNotes.Infrastructure.Sqlite.csproj package Microsoft.Data.Sqlite --version 10.0.0
dotnet add tests/QuickNotes.IntegrationTests/QuickNotes.IntegrationTests.csproj package Microsoft.NET.Test.Sdk --version 17.13.0
dotnet add tests/QuickNotes.IntegrationTests/QuickNotes.IntegrationTests.csproj package xunit --version 2.9.3
dotnet add tests/QuickNotes.IntegrationTests/QuickNotes.IntegrationTests.csproj package xunit.runner.visualstudio --version 2.8.2
```

Remove only scaffold-generated `Class1.cs` and `UnitTest1.cs` files. Set `<Nullable>enable</Nullable>` and `<ImplicitUsings>enable</ImplicitUsings>` in all four project files; set `<IsPackable>false</IsPackable>` in the test project.

- [ ] **Step 2: Write the failing schema tests**

Create `TemporaryDatabase.cs`; tests create `SqliteConnectionFactory` themselves so reopen uses a genuinely new connection factory:

```csharp
namespace QuickNotes.IntegrationTests;

internal sealed class TemporaryDatabase : IDisposable
{
    public TemporaryDatabase()
    {
        DatabasePath = Path.Combine(Path.GetTempPath(), "QuickNotes-B1", Guid.NewGuid().ToString("N"), "notes.db");
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
    }

    public string DatabasePath { get; }

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(DatabasePath)!;
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
```

Create `SqliteSchemaTests.cs`:

```csharp
using Microsoft.Data.Sqlite;
using QuickNotes.Domain;
using QuickNotes.Infrastructure.Sqlite;
using Xunit;

namespace QuickNotes.IntegrationTests;

public sealed class SqliteSchemaTests
{
    [Fact]
    public async Task Initialize_CreatesVersionOneSchemaAndUncategorizedCategory()
    {
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.DatabasePath);

        await new SqliteSchema(factory).InitializeAsync();

        await using var connection = await factory.OpenAsync();
        Assert.Equal(1, await ScalarIntAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(1, await ScalarIntAsync(connection, "PRAGMA foreign_keys;"));
        Assert.Equal("ok", await ScalarStringAsync(connection, "PRAGMA integrity_check;"));
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM categories WHERE id = $id AND is_system = 1;";
        command.Parameters.AddWithValue("$id", Category.UncategorizedId.ToString("D"));
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Reopen_PreservesSchemaAndReportsNoForeignKeyViolations()
    {
        using var database = new TemporaryDatabase();
        await new SqliteSchema(new SqliteConnectionFactory(database.DatabasePath)).InitializeAsync();
        var reopenedFactory = new SqliteConnectionFactory(database.DatabasePath);

        await new SqliteSchema(reopenedFactory).InitializeAsync();

        await using var connection = await reopenedFactory.OpenAsync();
        Assert.Equal(1, await ScalarIntAsync(connection, "PRAGMA user_version;"));
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.False(await reader.ReadAsync());
    }

    private static async Task<int> ScalarIntAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync())!;
    }
}
```

- [ ] **Step 3: Run the tests and confirm the expected failure**

Run: `dotnet test tests/QuickNotes.IntegrationTests/QuickNotes.IntegrationTests.csproj --filter FullyQualifiedName~SqliteSchemaTests`

Expected: FAIL at compilation because `SqliteConnectionFactory`, `SqliteSchema`, and domain types do not exist.

- [ ] **Step 4: Implement the domain model**

Use these exact enums and records:

```csharp
namespace QuickNotes.Domain;

public enum NoteLifecycle { Active = 0, Archived = 1, Trash = 2 }
```

```csharp
namespace QuickNotes.Domain;

public enum NoteDisplayIntent { Hidden = 0, Visible = 1 }
```

```csharp
namespace QuickNotes.Domain;

public sealed record Note
{
    public required Guid Id { get; init; }
    public string? Title { get; init; }
    public required string Body { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset ModifiedAtUtc { get; init; }
    public long Version { get; init; } = 1;
    public NoteLifecycle Lifecycle { get; init; } = NoteLifecycle.Active;
    public required Guid CategoryId { get; init; }
    public bool IsPinned { get; init; }
    public NoteDisplayIntent DisplayIntent { get; init; } = NoteDisplayIntent.Visible;
}
```

```csharp
namespace QuickNotes.Domain;

public sealed record Category
{
    public static readonly Guid UncategorizedId = Guid.Parse("6f2a98f2-20de-4f4e-a2f4-8a570c4625d7");
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string DefaultColor { get; init; }
    public bool IsSystem { get; init; }
}
```

```csharp
namespace QuickNotes.Domain;

public sealed record Tag
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
}
```

```csharp
namespace QuickNotes.Domain;

public sealed record NoteTag
{
    public required Guid NoteId { get; init; }
    public required Guid TagId { get; init; }
}
```

- [ ] **Step 5: Implement the connection factory and schema**

`SqliteConnectionFactory` stores a connection string built with `DataSource`, `Mode=ReadWriteCreate`, and `Pooling=False`. `OpenAsync` opens a new `SqliteConnection`, executes `PRAGMA foreign_keys=ON;`, and returns the open connection; on failure it disposes the connection before rethrowing.

`SqliteSchema.InitializeAsync` opens a connection and transaction, reads `PRAGMA user_version`, returns without changes for version 1, throws `NotSupportedException` for versions greater than 1, and rejects unexpected nonzero versions. For version 0 it executes this DDL in the transaction, seeds `Category.UncategorizedId` with name `未分类`, color `#E6E6FA`, `is_system=1`, then sets `PRAGMA user_version=1` and commits:

```sql
CREATE TABLE categories (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL COLLATE NOCASE UNIQUE,
    default_color TEXT NOT NULL,
    is_system INTEGER NOT NULL CHECK (is_system IN (0, 1))
);
CREATE TABLE tags (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL COLLATE NOCASE UNIQUE
);
CREATE TABLE notes (
    id TEXT PRIMARY KEY,
    title TEXT NULL,
    body TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    modified_at_utc TEXT NOT NULL,
    version INTEGER NOT NULL CHECK (version >= 1),
    lifecycle INTEGER NOT NULL CHECK (lifecycle IN (0, 1, 2)),
    category_id TEXT NOT NULL,
    is_pinned INTEGER NOT NULL CHECK (is_pinned IN (0, 1)),
    display_intent INTEGER NOT NULL CHECK (display_intent IN (0, 1)),
    FOREIGN KEY (category_id) REFERENCES categories(id) ON DELETE RESTRICT,
    CHECK (lifecycle = 0 OR display_intent = 0)
);
CREATE TABLE note_tags (
    note_id TEXT NOT NULL,
    tag_id TEXT NOT NULL,
    PRIMARY KEY (note_id, tag_id),
    FOREIGN KEY (note_id) REFERENCES notes(id) ON DELETE CASCADE,
    FOREIGN KEY (tag_id) REFERENCES tags(id) ON DELETE CASCADE
);
CREATE INDEX ix_notes_category_id ON notes(category_id);
CREATE INDEX ix_notes_modified_at_utc ON notes(modified_at_utc DESC);
CREATE INDEX ix_note_tags_tag_id ON note_tags(tag_id);
```

Use invariant `"O"` formatting for later timestamp reads/writes; schema stores timestamps as TEXT.

- [ ] **Step 6: Run schema tests and verify they pass**

Run: `dotnet test tests/QuickNotes.IntegrationTests/QuickNotes.IntegrationTests.csproj --filter FullyQualifiedName~SqliteSchemaTests`

Expected: PASS, 2 tests; no integrity or foreign-key failures.

---

### Task 2: Transactional Catalog and Note Creation/Reading

**Files:**
- Create: `src/QuickNotes.Application/ICatalogRepository.cs`
- Create: `src/QuickNotes.Application/INoteRepository.cs`
- Create: `src/QuickNotes.Application/NoteConcurrencyException.cs`
- Create: `src/QuickNotes.Infrastructure.Sqlite/SqliteCatalogRepository.cs`
- Create: `src/QuickNotes.Infrastructure.Sqlite/SqliteNoteRepository.cs`
- Create: `src/QuickNotes.Infrastructure.Sqlite/SqliteTypeMapping.cs`
- Test: `tests/QuickNotes.IntegrationTests/SqliteNoteRepositoryTests.cs`

**Interfaces:**
- Consumes: Task 1 domain types, factory, initialized schema.
- Produces: `ICatalogRepository.CreateCategoryAsync`, `CreateTagAsync`, `GetCategoryAsync`, `GetTagAsync`; `INoteRepository.CreateAsync`, `GetAsync`, `GetTagIdsAsync`, plus update contracts implemented in Task 3.

- [ ] **Step 1: Write failing create/read and persistence tests**

Create `TestDatabase.cs` and `TestData.cs`:

```csharp
using QuickNotes.Application;
using QuickNotes.Infrastructure.Sqlite;

namespace QuickNotes.IntegrationTests;

internal sealed class TestDatabase : IDisposable
{
    private readonly TemporaryDatabase _temporary = new();

    private TestDatabase() { }

    public SqliteConnectionFactory Factory { get; private set; } = null!;
    public ICatalogRepository Catalog { get; private set; } = null!;
    public INoteRepository Notes { get; private set; } = null!;

    public static async Task<TestDatabase> CreateAsync()
    {
        var database = new TestDatabase();
        database.Reopen();
        await new SqliteSchema(database.Factory).InitializeAsync();
        return database;
    }

    public void Reopen()
    {
        Factory = new SqliteConnectionFactory(_temporary.DatabasePath);
        Catalog = new SqliteCatalogRepository(Factory);
        Notes = new SqliteNoteRepository(Factory);
    }

    public void Dispose() => _temporary.Dispose();
}
```

```csharp
using QuickNotes.Domain;

namespace QuickNotes.IntegrationTests;

internal static class TestData
{
    public static Note NewNote()
    {
        var timestamp = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        return new Note
        {
            Id = Guid.NewGuid(),
            Title = "title",
            Body = "body",
            CreatedAtUtc = timestamp,
            ModifiedAtUtc = timestamp,
            CategoryId = Category.UncategorizedId
        };
    }
}
```

Add these tests to `SqliteNoteRepositoryTests.cs` with `Microsoft.Data.Sqlite`, `QuickNotes.Domain`, and `Xunit` imported:

```csharp
[Fact]
public async Task CreateAndGet_PersistsNoteCategoryAndTags()
{
    using var database = await TestDatabase.CreateAsync();
    var tag = new Tag { Id = Guid.NewGuid(), Name = "work" };
    await database.Catalog.CreateTagAsync(tag);
    var note = TestData.NewNote();

    var created = await database.Notes.CreateAsync(note, new[] { tag.Id });
    var loaded = await database.Notes.GetAsync(note.Id);
    var tagIds = await database.Notes.GetTagIdsAsync(note.Id);

    Assert.Equal(note with { Version = 1 }, created);
    Assert.Equal(created, loaded);
    Assert.Equal(new[] { tag.Id }, tagIds);
}

[Fact]
public async Task CloseAndReopen_PreservesNoteAndAssociations()
{
    using var database = await TestDatabase.CreateAsync();
    var tag = new Tag { Id = Guid.NewGuid(), Name = "persistent" };
    await database.Catalog.CreateTagAsync(tag);
    var note = TestData.NewNote();
    await database.Notes.CreateAsync(note, new[] { tag.Id });

    database.Reopen();

    Assert.Equal(note, await database.Notes.GetAsync(note.Id));
    Assert.Equal(new[] { tag.Id }, await database.Notes.GetTagIdsAsync(note.Id));
}

[Fact]
public async Task ForeignKeys_RejectNoteWithMissingCategory()
{
    using var database = await TestDatabase.CreateAsync();
    var note = TestData.NewNote() with { CategoryId = Guid.NewGuid() };

    await Assert.ThrowsAsync<SqliteException>(() => database.Notes.CreateAsync(note, Array.Empty<Guid>()));
    Assert.Null(await database.Notes.GetAsync(note.Id));
}
```

- [ ] **Step 2: Run the tests and confirm the expected failure**

Run: `dotnet test tests/QuickNotes.IntegrationTests/QuickNotes.IntegrationTests.csproj --filter FullyQualifiedName~SqliteNoteRepositoryTests`

Expected: FAIL at compilation because repository contracts and implementations do not exist.

- [ ] **Step 3: Define application contracts and conflict semantics**

```csharp
using QuickNotes.Domain;

namespace QuickNotes.Application;

public interface ICatalogRepository
{
    Task CreateCategoryAsync(Category category, CancellationToken cancellationToken = default);
    Task CreateTagAsync(Tag tag, CancellationToken cancellationToken = default);
    Task<Category?> GetCategoryAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Tag?> GetTagAsync(Guid id, CancellationToken cancellationToken = default);
}
```

```csharp
using QuickNotes.Domain;

namespace QuickNotes.Application;

public interface INoteRepository
{
    Task<Note> CreateAsync(Note note, IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken = default);
    Task<Note?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>> GetTagIdsAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Note> UpdateAsync(Note note, long expectedVersion, IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken = default);
    Task<Note> SetLifecycleAsync(Guid id, NoteLifecycle lifecycle, long expectedVersion, DateTimeOffset modifiedAtUtc, CancellationToken cancellationToken = default);
}
```

`NoteConcurrencyException` derives from `Exception` and exposes `NoteId`, `ExpectedVersion`; its constructor sets a message containing both values.

- [ ] **Step 4: Implement catalog persistence and shared type mapping**

`SqliteTypeMapping` provides `GuidString(Guid)`, `UtcString(DateTimeOffset)` using `value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)`, and `ReadNote(SqliteDataReader)` using ordinal-based getters and `DateTimeOffset.Parse(..., CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)`.

`SqliteCatalogRepository` opens a factory connection per method. Create methods insert all fields and propagate SQLite constraint exceptions. Get methods query by GUID text, return `null` when no row exists, and otherwise materialize the complete domain record. No catalog method catches and discards exceptions.

- [ ] **Step 5: Implement transactional note creation and reads**

`CreateAsync` opens one connection and transaction, inserts the note with `version=1`, then inserts every `(note_id, tag_id)` association. Duplicate input tag IDs are made distinct before insertion. Any missing tag/category, duplicate primary key, or association failure rolls back the transaction. Commit before returning `note with { Version = 1, Lifecycle = NoteLifecycle.Active }`.

Use this note column list consistently for insert and select:

```text
id, title, body, created_at_utc, modified_at_utc, version, lifecycle, category_id, is_pinned, display_intent
```

`GetAsync` returns `null` for a missing ID and otherwise uses `SqliteTypeMapping.ReadNote`. `GetTagIdsAsync` selects tag IDs ordered by `tag_id` and returns a read-only list. Every async method accepts and forwards its cancellation token. For this red-green cycle only, `UpdateAsync` and `SetLifecycleAsync` contain `throw new NotImplementedException();` so the create/read tests compile and run before Task 3 replaces both bodies.

- [ ] **Step 6: Run create/read tests and verify they pass**

Run: `dotnet test tests/QuickNotes.IntegrationTests/QuickNotes.IntegrationTests.csproj --filter "FullyQualifiedName~SqliteNoteRepositoryTests.CreateAndGet|FullyQualifiedName~SqliteNoteRepositoryTests.CloseAndReopen|FullyQualifiedName~SqliteNoteRepositoryTests.ForeignKeys"`

Expected: PASS, 3 tests; reopening returns identical content and tag associations.

---

### Task 3: Atomic Updates, Lifecycle Changes, Rollback, and Conflict Protection

**Files:**
- Modify: `src/QuickNotes.Infrastructure.Sqlite/SqliteNoteRepository.cs`
- Test: `tests/QuickNotes.IntegrationTests/SqliteNoteRepositoryTests.cs`

**Interfaces:**
- Consumes: all Task 2 repository methods and `NoteConcurrencyException`.
- Produces: fully implemented `UpdateAsync` and `SetLifecycleAsync`; no later B2/B3 APIs.

- [ ] **Step 1: Write failing update, lifecycle, rollback, and stale-write tests**

Add these exact behavioral tests (using the existing `TestDatabase` and `TestData` helpers):

```csharp
[Fact]
public async Task Update_ChangesMutableFieldsAndIncrementsVersion()
{
    using var database = await TestDatabase.CreateAsync();
    var firstTag = new Tag { Id = Guid.NewGuid(), Name = "first" };
    var secondTag = new Tag { Id = Guid.NewGuid(), Name = "second" };
    await database.Catalog.CreateTagAsync(firstTag);
    await database.Catalog.CreateTagAsync(secondTag);
    var note = TestData.NewNote();
    await database.Notes.CreateAsync(note, new[] { firstTag.Id });
    var changed = note with { Title = null, Body = "changed", IsPinned = true, DisplayIntent = NoteDisplayIntent.Hidden, ModifiedAtUtc = note.ModifiedAtUtc.AddMinutes(1) };

    var updated = await database.Notes.UpdateAsync(changed, 1, new[] { secondTag.Id });

    Assert.Equal(2, updated.Version);
    Assert.Equal(changed with { Version = 2 }, await database.Notes.GetAsync(note.Id));
    Assert.Equal(new[] { secondTag.Id }, await database.Notes.GetTagIdsAsync(note.Id));
}

[Fact]
public async Task Update_WithStaleVersion_DoesNotOverwriteNewerContent()
{
    using var database = await TestDatabase.CreateAsync();
    var note = TestData.NewNote();
    await database.Notes.CreateAsync(note, Array.Empty<Guid>());
    await database.Notes.UpdateAsync(note with { Body = "newer", ModifiedAtUtc = note.ModifiedAtUtc.AddSeconds(1) }, 1, Array.Empty<Guid>());

    await Assert.ThrowsAsync<NoteConcurrencyException>(() => database.Notes.UpdateAsync(note with { Body = "stale" }, 1, Array.Empty<Guid>()));

    Assert.Equal("newer", (await database.Notes.GetAsync(note.Id))!.Body);
    Assert.Equal(2, (await database.Notes.GetAsync(note.Id))!.Version);
}

[Fact]
public async Task Update_WhenAssociationInsertFails_RollsBackContentVersionAndTags()
{
    using var database = await TestDatabase.CreateAsync();
    var originalTag = new Tag { Id = Guid.NewGuid(), Name = "original" };
    await database.Catalog.CreateTagAsync(originalTag);
    var note = TestData.NewNote();
    await database.Notes.CreateAsync(note, new[] { originalTag.Id });

    await Assert.ThrowsAsync<SqliteException>(() => database.Notes.UpdateAsync(note with { Body = "must roll back", ModifiedAtUtc = note.ModifiedAtUtc.AddSeconds(1) }, 1, new[] { Guid.NewGuid() }));

    Assert.Equal(note, await database.Notes.GetAsync(note.Id));
    Assert.Equal(new[] { originalTag.Id }, await database.Notes.GetTagIdsAsync(note.Id));
}

[Fact]
public async Task SetLifecycle_ArchivesHidesAndIncrementsVersion()
{
    using var database = await TestDatabase.CreateAsync();
    var note = TestData.NewNote();
    await database.Notes.CreateAsync(note, Array.Empty<Guid>());
    var modified = note.ModifiedAtUtc.AddMinutes(2);

    var archived = await database.Notes.SetLifecycleAsync(note.Id, NoteLifecycle.Archived, 1, modified);

    Assert.Equal(NoteLifecycle.Archived, archived.Lifecycle);
    Assert.Equal(NoteDisplayIntent.Hidden, archived.DisplayIntent);
    Assert.Equal(2, archived.Version);
    Assert.Equal(modified, archived.ModifiedAtUtc);
    Assert.Equal(archived, await database.Notes.GetAsync(note.Id));
}

[Fact]
public async Task Update_HiddenActiveNote_DoesNotChangeLifecycle()
{
    using var database = await TestDatabase.CreateAsync();
    var note = TestData.NewNote();
    await database.Notes.CreateAsync(note, Array.Empty<Guid>());

    await database.Notes.UpdateAsync(note with { DisplayIntent = NoteDisplayIntent.Hidden }, 1, Array.Empty<Guid>());

    var loaded = await database.Notes.GetAsync(note.Id);
    Assert.Equal(NoteLifecycle.Active, loaded!.Lifecycle);
    Assert.Equal(NoteDisplayIntent.Hidden, loaded.DisplayIntent);
}
```

- [ ] **Step 2: Run the tests and confirm update methods fail**

Run: `dotnet test tests/QuickNotes.IntegrationTests/QuickNotes.IntegrationTests.csproj --filter FullyQualifiedName~SqliteNoteRepositoryTests`

Expected: the new tests FAIL because update methods are not implemented; prior create/read tests remain PASS.

- [ ] **Step 3: Implement atomic update and version conflict behavior**

In `UpdateAsync`, use one connection and transaction. Update only title, body, modified UTC, category, pinned, and display intent; preserve created UTC and lifecycle. Use this guard:

```sql
UPDATE notes
SET title = $title,
    body = $body,
    modified_at_utc = $modifiedAtUtc,
    category_id = $categoryId,
    is_pinned = $isPinned,
    display_intent = $displayIntent,
    version = version + 1
WHERE id = $id AND version = $expectedVersion;
```

If affected rows are zero, query whether the note ID exists inside the same transaction. Throw `KeyNotFoundException` when absent; otherwise throw `NoteConcurrencyException`. After a successful guarded update, delete existing `note_tags`, insert the distinct supplied tag IDs, and read the stored note inside the transaction so preserved lifecycle/created fields cannot come from a stale input object. Commit, then return that stored note with `Version = expectedVersion + 1`. Any exception before commit rolls back the entire transaction.

- [ ] **Step 4: Implement lifecycle changes**

`SetLifecycleAsync` uses the same atomic ID/version guard and sets:

```sql
lifecycle = $lifecycle,
display_intent = 0,
modified_at_utc = $modifiedAtUtc,
version = version + 1
```

This applies to Active, Archived, and Trash transitions: archive/trash hide the note, while later unarchive/restore returns to Active but remains hidden by default as required by the PRD. Use the same missing-ID/conflict behavior as `UpdateAsync`, commit before returning, and preserve content, category, tags, pinned state, and created timestamp.

- [ ] **Step 5: Run all B1 tests and verify the complete suite**

Run: `dotnet test QuickNotes.DataPrototype.sln`

Expected: PASS, 10 tests total (2 schema + 8 repository); zero failures. Then run:

```bash
dotnet build QuickNotes.DataPrototype.sln --no-restore
```

Expected: PASS with 0 warnings and 0 errors. Inspect `git status --short` and confirm only the new B1 solution/source/test/plan files are present, with no PRD or WPF prototype modifications.

- [ ] **Step 6: Report actual verification evidence and remaining limits**

Report the exact build/test commands, number of passed/failed/not-run tests, changed files, and these limits: no backup/restore, no B2 save coordinator, no UI, no real-user data access, and no claim that SQLite is the final frozen product choice. Do not claim Windows UI behavior was retested by the data-layer suite.
