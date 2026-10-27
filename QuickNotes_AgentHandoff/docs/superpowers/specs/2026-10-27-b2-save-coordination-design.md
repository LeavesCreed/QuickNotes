# B2 保存协调与失败语义 — 设计规格

**日期：** 2026-10-27\
**阶段：** B2\
**状态：** 设计已确认，待规格审阅\
**基线：** `main` / `6f38726`（B1 已合并）\
**上游依据：** `docs/product/PRD_v1.0.md` 5.4–5.5、10.1–10.3、`docs/agent/Agent对接文档_B阶段.md` B2、`AGENTS.md`

## 1. 目标与范围

B2 在 B1 的 SQLite 持久化之上补齐保存协调与失败语义：

- 只有事务提交成功（或提交结果经核验确认成功）后才报告保存成功。
- 错误可诊断，不吞异常，不把原始 `SqliteException` 暴露给应用层/UI。
- 对 `SQLITE_BUSY` / `SQLITE_LOCKED` 做有界重试；约束错误、冲突和提交结果未知不盲目重试。
- Commit 结果未知时回读核验，绝不误报成功。
- 提供 UI 草稿保护接口与最小文件型草稿存储，覆盖“保存成功清理草稿、失败/冲突/未知保留草稿”。
- `SaveResult` 为未来 UI 提供可持久显示的失败状态、冲突双方内容和重试入口所需信息。
- 用单元测试、SQLite 集成测试和草稿文件集成测试覆盖失败路径。

**不在 B2 范围：**

- WPF/托盘/快捷键/编辑 UI。
- WAL 模式（留到 B3 与一致性备份快照一起决定）。
- 自动备份、恢复、留存清理。
- 真实磁盘满、权限拒绝、系统崩溃等系统级故障模拟（留到 B6）。
- 草稿自动生成、自动合并、后台自动清理/保留策略。
- 物理删除便签、Rust FFI、schema 变更。

## 2. 已确认的关键决策

| 决策 | 选择 |
|---|---|
| 整体架构 | Application 保存协调器；SQLite 只做数据访问与错误翻译；新增独立文件草稿存储项目 |
| 错误契约 | 类型化异常 + `SaveResult` 结果对象 |
| 草稿范围 | Application 契约 + 最小文件型草稿存储；不做 UI |
| 重试 | `busy_timeout=250ms`；最多 3 次尝试；退避 50ms、150ms；只重试 BUSY/LOCKED；最坏约 950ms |
| 提交未知 | Commit 异常后用新连接回读核验，再决定 Saved / 失败 / OutcomeUnknown |
| WAL | B2 不启用，B3 再定 |

## 3. 架构与依赖

```text
未来 UI
  └─ INoteSaveCoordinator（Application）
       ├─ INoteRepository（Application 契约）
       ├─ INoteDraftStore（Application 契约）
       ├─ SaveRetryOptions
       └─ ILogger<NoteSaveCoordinator>（可选）

SqliteNoteRepository（Infrastructure.Sqlite）
  ├─ SqliteConnectionFactory（foreign_keys=ON、busy_timeout）
  └─ SqliteErrorTranslator（SqliteException → 类型化异常）

FileNoteDraftStore（Infrastructure.Drafts）
  └─ 原子写 JSON 草稿文件
```

依赖方向：

- UI → Application
- Application → Domain
- Infrastructure.Sqlite → Application + Domain
- Infrastructure.Drafts → Application + Domain
- 无 UI、Sqlite、Drafts 之间的横向依赖

新增项目：

- `src/QuickNotes.Infrastructure.Drafts/QuickNotes.Infrastructure.Drafts.csproj`（`net10.0`；引用 Application、Domain）
- `tests/QuickNotes.UnitTests/QuickNotes.UnitTests.csproj`（`net10.0`；引用 Application、Domain；测试包版本与 IntegrationTests 一致：xUnit 2.9.3、Microsoft.NET.Test.Sdk 17.13.0、xunit.runner.visualstudio 2.8.2、coverlet.collector 6.0.4）
- 更新 `QuickNotes.DataPrototype.sln`，加入两个项目，并让 `QuickNotes.IntegrationTests` 引用 `QuickNotes.Infrastructure.Drafts`
- `QuickNotes.Application` 增加 `Microsoft.Extensions.Logging.Abstractions` 10.0.0

不修改 B1 的 schema、表结构或 `user_version`。

## 4. 应用层契约

### 4.1 保存入口

```csharp
public interface INoteSaveCoordinator
{
    Task<SaveResult> CreateAsync(
        Note note,
        IReadOnlyCollection<Guid> tagIds,
        Guid? draftId = null,
        CancellationToken cancellationToken = default);

    Task<SaveResult> UpdateAsync(
        Note note,
        long expectedVersion,
        IReadOnlyCollection<Guid> tagIds,
        Guid? draftId = null,
        CancellationToken cancellationToken = default);

    Task<SaveResult> SetLifecycleAsync(
        Guid id,
        NoteLifecycle lifecycle,
        long expectedVersion,
        DateTimeOffset modifiedAtUtc,
        CancellationToken cancellationToken = default);
}
```

```csharp
public sealed class NoteSaveCoordinator : INoteSaveCoordinator
{
    public NoteSaveCoordinator(
        INoteRepository notes,
        INoteDraftStore drafts,
        SaveRetryOptions? retryOptions = null,
        ILogger<NoteSaveCoordinator>? logger = null);
}
```

- `notes` / `drafts` 为 null 时构造失败（`ArgumentNullException`）；`retryOptions` 无效时构造失败（`ArgumentException`）；logger 为 null 时使用 `NullLogger<NoteSaveCoordinator>.Instance`。
- 冲突时使用 `CancellationToken.None` 尽力回读当前便签；回读失败不改变 `Conflict` 结果，只记录诊断。
- `SetLifecycleAsync` **不接受草稿 ID、也不删除草稿**：归档/移入回收站不会保存正文，删除草稿会造成静默内容丢失，违反 PRD 5.5/10.1。
- `CreateAsync` 与 `UpdateAsync` 的 `draftId` 表示“本次保存内容对应的草稿”。协调器只在保存成功后，确认草稿内容与已保存内容一致，才删除草稿。
- `UpdateAsync` 只更新内容字段；输入 `Note.Lifecycle`、`CreatedAtUtc`、`Version` 被忽略，生命周期变更必须走 `SetLifecycleAsync`。
- `CreateAsync` 忽略输入 `Note.Version`，仓储固定写入 Version=1、Lifecycle=Active。
- 协调器无实例可变状态，可被并发调用；并发安全依赖仓储与草稿存储的契约。

### 4.2 结果与错误类型

```csharp
public enum SaveStatus
{
    Saved,
    Conflict,
    NotFound,
    ValidationFailed,
    StorageUnavailable,
    OutcomeUnknown
}

public enum SaveErrorCode
{
    None = 0,
    NoteNotFound,
    ConcurrencyConflict,
    ValidationFailed,
    StorageBusy,
    StorageLocked,
    StorageUnavailable,
    CommitFailedNotSaved,
    CommitOutcomeUnknown,
    DraftCleanupFailed,
    DraftMismatch
}

public sealed record SaveError(
    SaveErrorCode Code,
    string Message,
    bool IsRetryable = false,
    string? Diagnostics = null);

public enum DraftDisposition
{
    NotProvided,
    DeletedAfterSave,
    AlreadyAbsent,
    Retained,
    CleanupFailed
}

public sealed record SaveResult
{
    public required SaveStatus Status { get; init; }
    public required Guid NoteId { get; init; }
    public Note? SavedNote { get; init; }
    public Note? CurrentNote { get; init; }
    public long? CurrentVersion { get; init; }
    public SaveError? Error { get; init; }
    public Guid? DraftId { get; init; }
    public DraftDisposition DraftDisposition { get; init; }
    public SaveError? DraftError { get; init; }
    public int Attempts { get; init; }
}
```

语义：

- `SavedNote` 仅在 `Saved` 时有值，且是仓储/回读返回的权威行，不是调用方传入对象。
- `CurrentNote` / `CurrentVersion` 用于 `Conflict`，在回读成功时填充；`OutcomeUnknown` 时尽力填充；其他状态为 null。
- `Attempts` 表示实际发起的仓储写入尝试次数；输入校验失败为 0；单次为 1；重试耗尽为 `MaxAttempts`（默认 3）。核验读取、冲突回读和草稿清理不计入。
- `DraftId` 原样回显调用方提供的草稿 ID；未提供或生命周期操作为 null。
- `DraftDisposition` 表示协调器对调用方提供草稿的处理结果：`NotProvided`（未提供）、`DeletedAfterSave`（协调器确认内容一致并删除成功）、`AlreadyAbsent`（保存成功时草稿已不存在）、`Retained`（协调器没有删除该草稿）、`CleanupFailed`（保存成功但读取/删除草稿失败）。
- `DraftError` 只用于保存已成功但草稿未按预期清理的情况（`DraftMismatch` / `DraftCleanupFailed`）；保存失败的主错误放 `Error`。
- 保存失败时 `SavedNote` 为 null，`Error` 非 null；`Saved` 时 `Error` 为 null。
- 取消通常抛出 `OperationCanceledException`：协调器在入口、每次尝试前和重试延迟中检查取消。例外：取消发生在提交开始之后时，提交继续完成并返回真实保存结果；见 7.1。

### 4.3 异常体系（Application）

```csharp
public abstract class NoteStorageException : Exception
{
    protected NoteStorageException(
        string operation,
        Guid? noteId,
        string errorCode,
        string message,
        Exception? inner = null);

    public Guid? NoteId { get; }
    public string Operation { get; }   // create/update/set_lifecycle/get/get_tags/open
    public string ErrorCode { get; }   // 稳定诊断码，不是 SaveErrorCode
}

public sealed class NoteNotFoundException : NoteStorageException
{
}
// errorCode = note_not_found

public sealed class NoteConcurrencyException : NoteStorageException
{
    public long ExpectedVersion { get; }
    public long? ActualVersion { get; }
}
// errorCode = concurrency_conflict

public sealed class NoteConstraintException : NoteStorageException
{
    public string ConstraintKind { get; } // foreign_key/check/not_null/unique/primary_key/generic
}
// errorCode = constraint_violation

public sealed class StorageUnavailableException : NoteStorageException
{
    public bool IsRetryable { get; }
}
// errorCode = sqlite_busy/sqlite_locked/sqlite_readonly/sqlite_io/
//             sqlite_corrupt/sqlite_full/sqlite_cantopen/sqlite_notadb/
//             sqlite_interrupt/sqlite_error

public sealed class SaveOutcomeUnknownException : NoteStorageException
{
    public long ExpectedVersion { get; } // Create 为 0
}
// errorCode = commit_outcome_unknown
```

- 所有异常保留 inner exception；不得吞异常。
- `NoteConcurrencyException` 保留 B1 的 `ExpectedVersion`，新增可空 `ActualVersion`；仓储在版本失败时查询当前版本并填充；若行已不存在则改为抛 `NoteNotFoundException`。
- `NoteNotFoundException` 替换 B1 写入路径中的 `KeyNotFoundException`。
- `SaveOutcomeUnknownException` 只由仓储在 **Commit 抛异常** 时抛出；普通 SQL 执行失败走错误翻译。Create 的 `ExpectedVersion` 为 0。
- 调用方取消不被包装为存储异常。唯一例外是 Commit 阶段（使用 `CancellationToken.None`）驱动抛出的异常：此时提交状态未知，按 `SaveOutcomeUnknownException` 处理并由协调器核验。
- 异常消息可包含 operation、note ID、错误码、约束类型；**不得包含标题、正文或标签内容**。

## 5. 错误翻译

`SqliteErrorTranslator`（`QuickNotes.Infrastructure.Sqlite` 内部）把 `SqliteException` 按主错误码和扩展错误码翻译：

| SQLite 错误 | 结果异常 | 可重试 |
|---|---|---|
| `SQLITE_BUSY` (5，含所有扩展码) | `StorageUnavailableException`（`sqlite_busy`） | 是 |
| `SQLITE_LOCKED` (6，含所有扩展码) | `StorageUnavailableException`（`sqlite_locked`） | 是 |
| `SQLITE_INTERRUPT` (9) 且调用方已取消 | 不翻译，抛 `OperationCanceledException` | — |
| `SQLITE_INTERRUPT` (9) 其他情况 | `StorageUnavailableException`（`sqlite_interrupt`） | 否 |
| `SQLITE_READONLY` (8，含扩展码) | `StorageUnavailableException`（`sqlite_readonly`） | 否 |
| `SQLITE_IOERR` (10，含扩展码) | `StorageUnavailableException`（`sqlite_io`） | 否 |
| `SQLITE_CORRUPT` (11，含扩展码) | `StorageUnavailableException`（`sqlite_corrupt`） | 否 |
| `SQLITE_FULL` (13，含扩展码) | `StorageUnavailableException`（`sqlite_full`） | 否 |
| `SQLITE_CANTOPEN` (14，含扩展码) | `StorageUnavailableException`（`sqlite_cantopen`） | 否 |
| `SQLITE_NOTADB` (26，含扩展码) | `StorageUnavailableException`（`sqlite_notadb`） | 否 |
| `SQLITE_CONSTRAINT` (19) + FOREIGNKEY (787) | `NoteConstraintException`（`foreign_key`） | 否 |
| `SQLITE_CONSTRAINT` (19) + CHECK (275) | `NoteConstraintException`（`check`） | 否 |
| `SQLITE_CONSTRAINT` (19) + NOTNULL (1299) | `NoteConstraintException`（`not_null`） | 否 |
| `SQLITE_CONSTRAINT` (19) + UNIQUE (2067) | `NoteConstraintException`（`unique`） | 否 |
| `SQLITE_CONSTRAINT` (19) + PRIMARYKEY (1555) | `NoteConstraintException`（`primary_key`） | 否 |
| 其他 `SQLITE_CONSTRAINT` | `NoteConstraintException`（`generic`） | 否 |
| 其他 `SqliteException` | `StorageUnavailableException`（`sqlite_error`） | 否 |

规则：

- 主错误码 = `SqliteExtendedErrorCode & 0xFF`；扩展码保留在诊断中，用于区分 BUSY/LOCKED 的具体变体。
- 翻译应用于 `SqliteNoteRepository` 的 Create/Update/SetLifecycle/Get/GetTagIds 以及连接打开路径。
- 连接工厂翻译时 `NoteId = null`，`Operation = "open"`；仓储操作携带对应 operation 与 note ID。
- Commit 调用有独立的 try/catch，优先包装为 `SaveOutcomeUnknownException`，不能被通用翻译抢先转成 `StorageUnavailableException`。
- `SqliteSchema.InitializeAsync` 的启动错误保持原样，不纳入保存路径翻译。
- 诊断/消息只包含错误码、扩展码、约束类型和 operation，不包含正文、标题或标签内容。
- 协调器把异常映射为结果：

| 异常 | SaveStatus | SaveError.Code | IsRetryable | Attempts |
|---|---|---|---|---|
| `NoteNotFoundException` | `NotFound` | `NoteNotFound` | 否 | ≥1 |
| `NoteConcurrencyException` | `Conflict` | `ConcurrencyConflict` | 否 | ≥1 |
| `NoteConstraintException` | `ValidationFailed` | `ValidationFailed` | 否 | ≥1 |
| `StorageUnavailableException`（可重试耗尽） | `StorageUnavailable` | `StorageBusy` / `StorageLocked` | 是 | `MaxAttempts`（默认 3） |
| `StorageUnavailableException`（不可重试） | `StorageUnavailable` | `StorageUnavailable` | 否 | ≥1 |
| Commit 核验为未写入 | `StorageUnavailable` | `CommitFailedNotSaved` | 是 | ≥1 |
| Commit 核验不一致/失败 | `OutcomeUnknown` | `CommitOutcomeUnknown` | 否 | ≥1 |

- `ValidationFailed` 的 `Diagnostics` 对数据库约束错误包含 `constraint=<kind>`；输入校验失败只包含稳定原因码（如 `note_id_empty`）。
- `CommitFailedNotSaved` 的 `IsRetryable=true` 表示调用方可以手动重试；协调器不自动重试。
- 非预期异常（非 `NoteStorageException`、非 `OperationCanceledException`）不映射为结果：记录 Error 后原样抛出，避免把编程错误伪装成存储失败。

## 6. 重试策略

```csharp
public sealed record SaveRetryOptions
{
    public int MaxAttempts { get; init; } = 3;
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } =
        new[] { TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(150) };
}
```

默认值（可在构造时覆盖，测试用 0 延迟）：

| 配置 | 默认 |
|---|---|
| `busy_timeout`（连接级） | 250ms |
| 最多尝试次数 | 3（初始 1 次 + 2 次重试） |
| 重试间隔 | 50ms、150ms |
| 最坏耗时 | 约 3 × 250ms + 200ms ≈ 950ms |

规则：

- `SaveRetryOptions` 校验：`MaxAttempts >= 1`；`RetryDelays` 非 null 且数量至少为 `MaxAttempts - 1`；每个延迟非负。多余延迟忽略，不足构造失败。
- 只重试 `StorageUnavailableException.IsRetryable == true`（BUSY/LOCKED）。
- 约束错误、NotFound、Conflict、`SaveOutcomeUnknownException`、非可重试存储错误都不自动重试。
- 每次重试重新调用仓储；仓储每次重新打开连接和事务，不复用失败的事务。
- 第 n 次尝试失败后使用 `RetryDelays[n - 1]`；延迟可取消，取消立即抛出 `OperationCanceledException`。
- 重试耗尽后返回 `StorageUnavailable`，`SaveError.Code` 使用最后一次错误（`StorageBusy` / `StorageLocked`），`IsRetryable = true`，草稿保留。
- 提交阶段不重试；Commit 异常进入“提交结果未知”核验。
- `SqliteConnectionFactory` 增加 `int busyTimeoutMilliseconds = 250` 构造参数，非负；每个连接打开后执行 `PRAGMA foreign_keys=ON; PRAGMA busy_timeout=<ms>;`。默认值保持 B1 调用方不需改动。

## 7. 提交结果未知

### 7.1 仓储行为

- 在提交前检查调用方取消；随后以 `CancellationToken.None` 调用 `CommitAsync`，避免把已提交误报为取消。
- Commit 调用单独 try/catch；任何异常（包括驱动抛出的取消异常）包装为 `SaveOutcomeUnknownException`，携带 operation、NoteId、ExpectedVersion（Create 为 0）和 inner。
- 提交前的命令异常仍走普通错误翻译；取消在事务开始前、命令之间和提交前生效。
- 若取消发生在提交前检查之后、提交过程中，提交继续完成；协调器返回真实结果（通常是 `Saved`），不抛出取消。
- Commit 成功后，Create/Update/SetLifecycle 在事务内读取并返回权威行；Create 不再直接返回调用方对象。

### 7.2 协调器回读核验

提交未知后使用**新连接**回读（`CancellationToken.None`，避免取消导致误判）：

| 操作 | 判定为 Saved | 判定为明确未写入 | 其他情况 |
|---|---|---|---|
| Create | ID 存在；Version=1；Lifecycle=Active；Title/Body/CategoryId/IsPinned/DisplayIntent 与输入一致；CreatedAtUtc/ModifiedAtUtc 与输入为同一时间点；标签集合一致 | ID 不存在 | ID 存在但状态不一致 → OutcomeUnknown |
| Update | Version=expected+1；Title/Body/CategoryId/IsPinned/DisplayIntent 与输入一致；ModifiedAtUtc 与输入为同一时间点；标签集合一致；不比较 Lifecycle/CreatedAtUtc | Version=expected | Version 其他值或内容不一致 → OutcomeUnknown；ID 不存在 → NotFound |
| SetLifecycle | Version=expected+1；Lifecycle=请求值；DisplayIntent=Hidden；ModifiedAtUtc 与输入为同一时间点 | Version=expected | Version 其他值或状态不一致 → OutcomeUnknown；ID 不存在 → NotFound |

- 标签集合比较忽略顺序与重复（与 B1 仓储 `Distinct()` 行为一致）。
- `DateTimeOffset` 按时间点比较，不要求时区偏移字符串相同。
- 核验先用 `GetAsync`，再用 `GetTagIdsAsync`；任一读取抛异常或结果不一致，按 OutcomeUnknown 处理。
- 核验为 Saved：返回 `Saved`，`SavedNote` 使用回读行，记录一条 Warning（Commit 报错但状态已确认），继续草稿清理。
- 核验为明确未写入：返回 `StorageUnavailable`，`SaveError.Code=CommitFailedNotSaved`，`IsRetryable=true`，草稿保留；不自动重试。
- 核验无法完成或状态不一致：返回 `OutcomeUnknown`，`SaveError.Code=CommitOutcomeUnknown`，`IsRetryable=false`，草稿保留；绝不报告成功。`CurrentNote`/`CurrentVersion` 尽力填充。
- 核验读取失败时把异常类型/错误码放入 `Diagnostics`，不向上抛原始 SQLite 异常，不包含内容。
- 核验读取不计入 `Attempts`。

## 8. 输入校验

协调器在调用仓储前校验以下内容；失败返回 `ValidationFailed`，`Attempts=0`，不写数据库、不读写草稿：

- `note is not null`
- `note.Id != Guid.Empty`
- `note.CategoryId != Guid.Empty`
- `note.Body is not null`（空字符串允许；PRD 允许空正文）
- `note.DisplayIntent` 是已定义枚举值
- `tagIds is not null`，且每个标签 ID 非空；重复标签允许，仓储会 `Distinct()`
- `draftId` 非 null 时必须非空 Guid
- Create：`note.Lifecycle == Active`；输入 `Version` 忽略
- Update / SetLifecycle：`expectedVersion >= 1`
- SetLifecycle：`id != Guid.Empty`；`lifecycle` 是已定义枚举值

数据库约束错误（外键、CHECK、NOT NULL、UNIQUE、主键）由仓储翻译为 `NoteConstraintException`，协调器映射为 `ValidationFailed`。非 Active 便签以 Visible 意图更新时触发的 CHECK 也走该路径，不再暴露原始 SQLite 异常。

- Create 的 ID 已存在 → 主键约束 → `ValidationFailed`（`primary_key`），不映射为版本 `Conflict`。
- Update 忽略输入 `Note.Lifecycle`、`CreatedAtUtc`、`Version`；若输入携带非 Active 生命周期，不会改变数据库中的生命周期。
- 约束失败时 `Attempts=1`（仓储已尝试），与输入校验失败的 `Attempts=0` 区分。

## 9. 草稿保护

### 9.1 契约

`INoteDraftStore`、`NoteDraft`、`DraftStoreException` 位于 `QuickNotes.Application`：

```csharp
public interface INoteDraftStore
{
    Task SaveAsync(NoteDraft draft, CancellationToken cancellationToken = default);
    Task<NoteDraft?> GetAsync(Guid draftId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NoteDraft>> ListAsync(Guid? noteId = null, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid draftId, CancellationToken cancellationToken = default);
}

public sealed record NoteDraft
{
    public required Guid DraftId { get; init; }
    public required Guid NoteId { get; init; }
    public long BaseVersion { get; init; } // 新便签为 0
    public string? Title { get; init; }
    public required string Body { get; init; }
    public required Guid CategoryId { get; init; }
    public IReadOnlyCollection<Guid> TagIds { get; init; } = Array.Empty<Guid>();
    public bool IsPinned { get; init; }
    public NoteDisplayIntent DisplayIntent { get; init; } = NoteDisplayIntent.Visible;
    public required DateTimeOffset CapturedAtUtc { get; init; }
}

public sealed class DraftStoreException : Exception
{
    public DraftStoreException(
        string operation,      // save/get/list/delete
        Guid? draftId,
        string errorCode,      // draft_io/draft_corrupt/draft_unsupported_version
        string message,
        Exception? inner = null);

    public Guid? DraftId { get; }
    public string Operation { get; }
    public string ErrorCode { get; }
}
```

- 同一便签允许多个草稿，按 `DraftId` 区分；支持未来两个窗口同时编辑时保留各自草稿。
- B2 不自动生成草稿；未来 UI 在编辑/自动保存时调用 `SaveAsync`。
- B2 不自动应用或合并草稿；`BaseVersion` 与正式便签版本一起保留，供未来 UI 判断过期并保留双方内容。
- `DraftStoreException` 不继承 `NoteStorageException`；它是草稿存储的诊断异常，可由未来 UI 直接处理。

### 9.2 保存成功后的草稿清理

仅 `CreateAsync` / `UpdateAsync` 且调用方提供 `draftId` 时执行：

1. 保存成功、状态为 `Saved` 后，读取草稿（`CancellationToken.None`，清理不被调用方取消打断）。
2. 草稿存在时，比较以下字段与已保存内容：
   - `Draft.NoteId == SavedNote.Id`
   - `Title` 序数相等（null 与 null 相等）
   - `Body` 序数相等
   - `CategoryId` 相等
   - `IsPinned`、`DisplayIntent` 相等
   - 草稿 `TagIds` 与本次保存的 `tagIds` 集合相等（忽略顺序与重复）
3. 结果处理：
   - 完全一致且删除成功 → `DraftDisposition=DeletedAfterSave`。
   - 不一致 → **保留草稿**，`DraftDisposition=Retained`，`DraftError.Code=DraftMismatch`；保存本身仍为 `Saved`。
   - 草稿不存在 → `DraftDisposition=AlreadyAbsent`（不声称协调器删除过）。
   - 读取失败 → 保留，`DraftDisposition=CleanupFailed`，`DraftError.Code=DraftCleanupFailed`；保存仍为 `Saved`。
   - 删除失败 → `DraftDisposition=CleanupFailed`，`DraftError.Code=DraftCleanupFailed`；保存仍为 `Saved`。
4. 保存失败/冲突/未知 → 不删除、不读取草稿；`DraftDisposition=Retained`（若有 draftId），草稿按调用方最后一次写入保留。
5. 未提供 `draftId` → `DraftDisposition=NotProvided`。
6. `SetLifecycleAsync` 永远不删除草稿，返回 `DraftDisposition=NotProvided`。
7. 清理阶段的异常（`DraftStoreException` 或非取消异常）不改变 `Saved` 状态；记录 Error/Warning 后转为 `CleanupFailed`。清理使用 `CancellationToken.None`，不会产生取消分支。

草稿内容比较用于防止“保存了较旧内容却删掉较新草稿”的静默丢失；它是 B2 的数据安全边界，不是可选优化。比较过程不得记录草稿内容。

冲突时 `CurrentNote` 与保留的草稿共同为未来 UI 提供双方内容；B2 不自动合并、不静默覆盖。

### 9.3 文件存储

`FileNoteDraftStore`（`QuickNotes.Infrastructure.Drafts`）：

- 构造函数接收草稿目录路径（非空字符串）；目录不存在时由 `SaveAsync` 创建。
- 每个草稿一个 JSON 文件：`<draftId:D>.json`；文件名不含标题/正文，不接受用户自定义路径。
- JSON `formatVersion = 1`，camelCase 属性名，UTF-8（无 BOM），`System.Text.Json`，枚举以字符串序列化。
- `SaveAsync` 按 `DraftId` 覆盖写入（upsert）。
- 原子写入：同目录临时文件 `<draftId:D>.json.<Guid:N>.tmp` → 写入 → `Flush(flushToDisk: true)` → 检查取消 → `File.Move(temp, target, overwrite: true)`；失败时尽力删除临时文件，旧文件保持不变。
- 读取缺失文件 → `null`；损坏 JSON、未知 `formatVersion`、文件内 `draftId` 与文件名不一致 → `DraftStoreException`（`draft_corrupt` / `draft_unsupported_version`），不静默忽略。反序列化后按 `SaveAsync` 相同规则校验，失败视为 `draft_corrupt`。
- `ListAsync` 只枚举 `<guid>.json` 文件，忽略 `*.tmp` 和其他非草稿文件；按 `CapturedAtUtc` 降序、再按 `DraftId` 排序；目录不存在返回空；枚举后文件被并发删除则跳过；`noteId` 非 null 时不得为空 Guid。
- `DeleteAsync` 幂等；文件或目录不存在视为成功。
- 单个存储实例内用 `SemaphoreSlim` 串行化操作；B2 假设单进程/单实例，不做跨进程文件锁。
- 输入校验：DraftId/NoteId/CategoryId 非空、Body 非 null、BaseVersion ≥ 0、TagIds 非 null 且标签 ID 非空、DisplayIntent 是已定义枚举；违反者抛 `ArgumentException`。I/O/格式问题抛 `DraftStoreException`。
- 取消：写临时文件、读取、删除可取消；取消不转为 `DraftStoreException`；取消发生在 move 之前时目标文件不变，move 之后操作已完成。
- 草稿目录的最终应用数据位置、加密与跨进程锁不在 B2 决定。

## 10. 日志与诊断

- Application 增加 `Microsoft.Extensions.Logging.Abstractions` 10.0.0。
- `NoteSaveCoordinator` 接受可选的 `ILogger<NoteSaveCoordinator>`；未提供时使用 `NullLogger`。
- 记录字段：operation、noteId、attempt、delay、durationMs、status、errorCode、异常类型/摘要。
- **禁止记录**：标题、正文、标签内容、草稿文件内容。
- 预期失败记录 Warning；意外异常记录 Error 并保留异常；成功记录 Information；重试记录 Warning。
- `SaveError.Diagnostics` 与 `DraftStoreException` 消息同样不得包含便签正文、标题或标签内容。
- 草稿损坏时只记录 `DraftStoreException.ErrorCode` 和异常类型，不记录 inner message 或 JSON 片段，避免把正文写入日志。

## 11. 测试策略

### 11.1 Application 单元测试（`QuickNotes.UnitTests`）

替身 `INoteRepository` / `INoteDraftStore`，重试延迟设为 0：

- 成功 → `Saved`、`Attempts=1`、草稿删除、`DeletedAfterSave`。
- 成功但草稿内容不匹配（内容、NoteId 或标签集合）→ `Saved` + `Retained` + `DraftMismatch`，草稿未删除。
- 成功但草稿读取/删除失败 → `Saved` + `CleanupFailed` + `DraftCleanupFailed`。
- 成功但草稿不存在 → `Saved` + `AlreadyAbsent`。
- 成功但未提供草稿 → `NotProvided`、不调用草稿存储。
- 冲突 → `Conflict`、`CurrentNote`/`CurrentVersion`、草稿保留、`Attempts=1`。
- NotFound / ValidationFailed / 不可重试存储错误 → 对应结果、草稿保留、不重试。
- BUSY 两次失败后成功 → `Saved`、`Attempts=3`。
- BUSY 三次失败 → `StorageUnavailable`、`Attempts=3`、`StorageBusy`、`IsRetryable=true`。
- LOCKED 耗尽 → `StorageLocked`。
- 约束/冲突/提交未知不自动重试（首次尝试即发生时 `Attempts=1`；此前 BUSY 重试仍计数）。
- 提交未知 + 回读一致 → `Saved`、`SavedNote` 为回读行。
- 提交未知 + 回读明确未写入 → `StorageUnavailable` + `CommitFailedNotSaved`，不报成功。
- 提交未知 + 回读不一致/读取失败 → `OutcomeUnknown`，不报成功。
- 冲突回读失败时仍返回 `Conflict`，`CurrentVersion` 来自异常，`CurrentNote` 为 null。
- 取消在首次尝试前/重试间隔中 → `OperationCanceledException`，无成功结果。
- Create 非 Active、空 ID、空分类、空标签 ID、空 draftId、非法枚举、null note/tagIds → `ValidationFailed`，仓储未被调用，`Attempts=0`。
- 重复标签 → 通过校验，标签按集合传递。
- SetLifecycle 成功 → 不调用草稿存储，`NotProvided`。
- 非预期异常 → 原样抛出，不被映射为 `StorageUnavailable`。
- 日志捕获：不包含标题/正文/标签内容。

### 11.2 SQLite 集成测试（`QuickNotes.IntegrationTests`，临时库）

- 连接 `PRAGMA busy_timeout` 等于配置值；默认 250ms。
- 缺失分类 → 仓储抛 `NoteConstraintException`（更新 B1 原 `SqliteException` 断言）；经协调器 → `ValidationFailed`。
- 关联标签失败回滚 → 仓储/协调器报告约束错误，内容/版本/旧标签不变（更新 B1 测试）。
- 过期版本 → 仓储抛 `NoteConcurrencyException` 且 `ActualVersion` 正确；经协调器 → `Conflict`。
- 缺失便签执行 Update / SetLifecycle → `NotFound`（新增测试；替换 B1 写入路径的 `KeyNotFoundException`）。
- 非 Active 便签以 Visible 更新 → `ValidationFailed`（关闭 B1 的 M1）。
- 第二连接持有 `BEGIN IMMEDIATE`：短时释放 → 重试成功；持续占用 → 3 次后 `StorageUnavailable`，草稿保留。测试使用短 `busy_timeout` 与短重试延迟，避免长等待和计时抖动。
- 测试装饰器在真实提交后抛 `SaveOutcomeUnknownException` → 协调器回读 → `Saved`。
- 测试装饰器在提交前抛未知且未写入 → 回读 → `StorageUnavailable`，不报成功。
- 测试装饰器提交未知且回读内容被改动 → `OutcomeUnknown`。
- 真实 Create/Update 成功后，匹配草稿文件被删除；不匹配草稿保留。
- 生命周期操作成功不删除草稿文件。

### 11.3 草稿文件集成测试（`QuickNotes.IntegrationTests`，临时目录）

- 保存/读取/按便签列出/删除往返；覆盖 null 标题、空正文、多标签、非 UTC 偏移时间。
- 覆盖已有草稿的原子替换；替换后无临时文件残留。
- 保存自动创建目录；目录不存在时 `ListAsync` 返回空、`DeleteAsync` 幂等。
- 损坏 JSON、未知格式版本、文件内 DraftId 不匹配 → `DraftStoreException` 对应错误码。
- `ListAsync` 排序正确，忽略 `*.tmp`/非草稿文件，目录中损坏草稿使 `ListAsync` 失败。
- 取消写临时文件不破坏旧草稿。
- Guid 文件名无路径穿越风险；只操作草稿目录内文件。
- 非法输入（空 ID、null Body、负 BaseVersion、空标签 ID）→ `ArgumentException`。

### 11.4 验证命令

```bash
dotnet restore QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln
dotnet build QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln --no-restore
dotnet test QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln --no-build --no-restore
dotnet list QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln package --vulnerable --include-transitive
```

通过标准：全部测试通过、0 警告/0 错误、无已知漏洞包、未运行项如实标注。新 worktree 没有 `obj/bin`，必须先 restore；不得沿用 B1 的 `--no-restore`。

## 12. 兼容性与迁移

- 不修改 SQLite schema 或 `user_version`；不需要数据迁移。
- `busy_timeout` 是连接级 PRAGMA，对既有数据库无破坏；默认 250ms 会改变锁竞争时的等待行为。
- B1 的 `KeyNotFoundException` 改为 `NoteNotFoundException`；相关测试同步更新。
- B1 的 `NoteConcurrencyException` 构造签名扩展为携带 operation/ActualVersion；相关测试同步更新。
- B1 的外键/关联失败测试由 `SqliteException` 断言改为 `NoteConstraintException`。
- B1 的 Create 返回调用方对象改为返回事务内回读行；字段值语义不变，时间戳归一化为 UTC。
- 草稿文件格式从 v1 开始；未知版本拒绝解析，未来升级需显式迁移或新版本支持。
- 新增项目和解决方案条目；不影响既有 WPF 原型。

## 13. 验收标准

- 保存只有在提交成功或提交异常后回读确认为一致时才返回 `Saved`。
- BUSY/LOCKED 有界重试按 3 次/50ms/150ms 执行；其他错误不盲目重试。
- Commit 未知返回 `Saved`、`StorageUnavailable(CommitFailedNotSaved)` 或 `OutcomeUnknown`，三者有测试。
- 原始 `SqliteException` 不再越过 Application；错误包含可诊断上下文且不含内容。
- 保存成功但草稿内容不匹配时保留草稿；失败/冲突/未知时保留草稿；草稿已不存在时报告 `AlreadyAbsent`；生命周期操作不删草稿。
- 草稿文件原子替换、损坏可诊断、临时目录测试通过。
- 全部测试、构建、漏洞审计通过；未运行的测试如实报告。
- 不引入 WAL、UI、备份/恢复、Rust FFI 或 schema 变更。

## 14. 风险与限制

- 提交核验是保守的：提交后若有其他写入者推进版本，可能返回 `OutcomeUnknown`，即使本次写入已落盘。
- 草稿清理以“本次保存的内容”为准；保存成功后若另一写入者用最新版本刻意覆盖，旧草稿可能已被删除。这依赖乐观并发避免静默覆盖，但仍属于未来 UI 需要处理的冲突窗口。
- 草稿文件与正式数据分离；B2 不自动恢复或合并，未来 UI 必须处理 `BaseVersion` 冲突。
- 单进程假设；没有跨进程草稿锁。
- 草稿文件未加密，最终目录和权限策略未在 B2 决定。
- 真实磁盘满、权限拒绝、系统崩溃的故障注入留到 B6。
- 重试总时长最坏约 950ms，保存 UI 需要显示“保存中”。
- 新增 `Microsoft.Extensions.Logging.Abstractions` 依赖；需纳入漏洞审计。
- 不启用 WAL，读写并发仍可能遇到 BUSY。

## 15. 后续阶段接口

- B3：一致性备份快照需要决定 WAL、快照方式与 `-wal`/`-shm` 处理；本设计不冻结这些决策。
- B5：整库恢复不得覆盖未保存草稿；恢复前需要检查草稿目录并保留。
- B7：正式日志宿主、应用数据目录、草稿保留策略与 UI 恢复入口。
