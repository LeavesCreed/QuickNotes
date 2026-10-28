# C2 最小编辑器、保存状态与草稿保护 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在已合并的 C1 宿主上实现最小便签编辑器：新建/打开/编辑/保存/重开，接入既有保存协调器与草稿存储，提供真实保存状态、草稿自动保护、冲突/结果未知处理和重启草稿恢复。

**Architecture:** `QuickNotes.Desktop.Core/Editing` 承载可测试的编辑会话：快照序号、去抖/自动保存、每便签串行保存、草稿捕获、冲突/未知结果状态；WPF `WorkspaceWindow` 仅负责列表/编辑框/状态栏/对话框。保存仍只经过 `INoteSaveCoordinator`，读取通过 `INoteRepository`；不直接执行 SQL。

**Tech Stack:** .NET 10、WPF、SQLite（Microsoft.Data.Sqlite 10.0.0）、xUnit 2.9.3、`INoteSaveCoordinator`/`INoteDraftStore`/`INoteRepository`。

**Spec:** `QuickNotes_AgentHandoff/docs/agent/Agent对接文档_C阶段.md` C2；PRD 10.1–10.3、8.4、14.2；B2 保存语义；C1 完成总结/ADR；`QuickNotes_AgentHandoff/docs/agent/C阶段-PRD覆盖表.md`。

## Global Constraints

- 正式目标平台 Windows 11 x64；不承诺 Windows 10。
- 保存成功只能在 `INoteSaveCoordinator` 返回 `Saved` 或提交未知后回读确认一致时显示；草稿落盘不等于正式保存。
- 旧保存完成不得把已有更新的编辑标为“已保存”；迟到回调不得覆盖新文字/新草稿。
- 版本冲突保留数据库版本与本地版本，不静默合并/覆盖。
- 草稿保存成功与数据库提交成功分别呈现；草稿失败必须提示“内容尚未得到本地保护”。
- 新建便签使用稳定 NoteId/DraftId；重试不得产生重复便签。
- 所有数据库写入经过 `INoteSaveCoordinator` 与共享 `IDatabaseWriteGate`；WPF 不执行 SQL。
- 沿用 B2 保存时延边界：BUSY/LOCKED 最多 3 次尝试，历史最坏约 3.2–3.4 秒；不把重试放在 UI 线程。
- 只使用合成数据/临时数据根；不访问真实用户数据。
- C2 不实现 schema v2、完整分类/标签/生命周期 UI、备份/恢复 UI、托盘、快捷键、设置页、导出或安装器。

---

### Task 1: C2 契约、列表查询与时序 ADR

**Files:**
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Application/INoteRepository.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Infrastructure.Sqlite/SqliteNoteRepository.cs`
- Modify: `QuickNotes_AgentHandoff/tests/QuickNotes.UnitTests/Fakes.cs`
- Modify: `QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/NoteSaveCoordinatorIntegrationTests.cs`（测试装饰器补接口）
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Editing/EditorSaveState.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Editing/DraftProtectionState.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Editing/EditorAutosaveOptions.cs`
- Create: `QuickNotes_AgentHandoff/docs/decisions/2026-10-28-c2-editor-save-timing.md`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/SqliteNoteRepositoryTests.cs`（追加列表测试）

**Interfaces:**
- Produces:
  - `INoteRepository.ListAsync(NoteLifecycle lifecycle, CancellationToken cancellationToken = default)` → `Task<IReadOnlyList<Note>>`，按 `modified_at_utc DESC, id` 排序。
  - `EditorSaveState`：`Clean, Modified, Drafting, Saving, Saved, SaveFailed, Conflict, OutcomeUnknown`。
  - `DraftProtectionState`：`NotNeeded, Pending, Protected, Failed`。
  - `EditorAutosaveOptions`：`DraftDebounce=300ms`、`SaveDebounce=800ms`、`MaxSaveDelay=5s`、`SaveOnFocusLoss=true`、`SaveOnNoteSwitch=true`。

- [ ] **Step 1: 写列表查询失败测试**

在 `SqliteNoteRepositoryTests` 追加：

```text
List_FiltersLifecycleAndOrdersByModifiedDesc
List_EmptyDatabase_ReturnsEmpty
List_DoesNotReturnArchivedOrTrash
```

测试创建 Active/Archived/Trash 便签、不同 `ModifiedAtUtc`，断言只返回 Active 且顺序正确。用临时目录；不接触用户数据。

- [ ] **Step 2: 扩展 Application 契约与 SQLite 实现**

```csharp
// INoteRepository
Task<IReadOnlyList<Note>> ListAsync(
    NoteLifecycle lifecycle,
    CancellationToken cancellationToken = default);
```

SQLite 查询复用 `NoteColumns`：

```sql
SELECT id, title, body, created_at_utc, modified_at_utc, version,
       lifecycle, category_id, is_pinned, display_intent
FROM notes
WHERE lifecycle = $lifecycle
ORDER BY modified_at_utc DESC, id;
```

更新 `FakeNoteRepository` 与测试装饰器，未使用列表时返回空数组。编译错误必须清零。

- [ ] **Step 3: 添加 C2 枚举/选项与 ADR**

ADR `2026-10-28-c2-editor-save-timing.md` 记录：

```text
草稿去抖 300ms（每次编辑后，最多 2s 内至少落盘一次）；
数据库自动保存去抖 800ms，连续编辑最长 5s 强制保存；
显式保存 Ctrl+S / 保存按钮 / 焦点离开 / 切换便签 / 关闭窗口立即触发；
每个便签最多一个保存进行中，期间新快照排队，仅保存最新快照；
草稿与数据库保存独立；数据库保存成功只清理匹配的草稿；
迟到结果只更新版本/状态，不把新编辑标为已保存；
冲突/未知结果保留本地快照与草稿，用户显式重载/另存/重试。
```

ADR 必须标明候选值、依据、后果与备选方案；不修改 PRD。

- [ ] **Step 4: 运行契约测试并提交**

```bash
dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/QuickNotes.IntegrationTests.csproj --filter FullyQualifiedName~SqliteNoteRepositoryTests
```

Expected: 全部通过；既有 B 测试不回归。

```bash
git add QuickNotes_AgentHandoff/src/QuickNotes.Application/INoteRepository.cs QuickNotes_AgentHandoff/src/QuickNotes.Infrastructure.Sqlite/SqliteNoteRepository.cs QuickNotes_AgentHandoff/tests/QuickNotes.UnitTests/Fakes.cs QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/NoteSaveCoordinatorIntegrationTests.cs QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Editing QuickNotes_AgentHandoff/docs/decisions/2026-10-28-c2-editor-save-timing.md QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/SqliteNoteRepositoryTests.cs
git commit -m "feat: 添加 C2 编辑契约、列表查询与时序 ADR"
```

### Task 2: 编辑快照、草稿捕获与状态机

**Files:**
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Editing/EditorSnapshot.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Editing/EditorSessionStatus.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Editing/NoteEditorSession.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Editing/EditorWorkspaceService.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/NoteEditorSessionTests.cs`

**Interfaces:**
- Produces:
  - `EditorSnapshot`：`Sequence, NoteId, DraftId, IsNew, Title, Body, CategoryId, TagIds, CreatedAtUtc, ModifiedAtUtc, BaseVersion`。
  - `EditorSessionStatus`：`NoteId, DraftId, IsNew, SaveState, DraftState, Sequence, BaseVersion, ErrorCode, Message, HasPendingChanges`。
  - `NoteEditorSession.ChangeText(string? title, string body)`：递增 Sequence，标记 Modified，安排草稿/自动保存。
  - `NoteEditorSession.FlushDraftAsync(CancellationToken)`：立即写当前快照草稿。
  - `NoteEditorSession.StatusChanged` 事件。
  - `EditorWorkspaceService.CreateNewAsync()` / `OpenAsync(Guid noteId)`。

- [ ] **Step 1: 写会话与草稿测试**

至少：

```text
CreateNew_StableNoteAndDraftIds
ChangeText_IncrementsSequenceAndMarksModified
ChangeText_CapturesDraftAfterDebounce
DraftFailure_LeavesContentAndReportsUnprotected
OpenExisting_LoadsTitleBodyVersionAndTags
NewSession_DraftHasBaseVersionZero
ExistingSession_DraftPreservesCurrentBaseVersion
Dispose_CancelsPendingDebounceWithoutDeletingDraft
```

使用 `TestTimeProvider`/短延迟（0 或 10ms）与临时草稿目录；`INoteSaveCoordinator` 用可控 fake，不在 UI 线程等待。

- [ ] **Step 2: 实现快照与会话基础状态**

```csharp
public sealed record EditorSnapshot
{
    public required long Sequence { get; init; }
    public required Guid NoteId { get; init; }
    public required Guid DraftId { get; init; }
    public required bool IsNew { get; init; }
    public string? Title { get; init; }
    public required string Body { get; init; }
    public required Guid CategoryId { get; init; }
    public required IReadOnlyCollection<Guid> TagIds { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset ModifiedAtUtc { get; init; }
    public required long BaseVersion { get; init; }
}
```

- 新建：NoteId/DraftId 固定生成一次；BaseVersion=0；Category=系统“未分类”；TagIds 空。
- 打开：从 `INoteRepository.GetAsync`+`GetTagIdsAsync` 读取；草稿 ID 为新编辑会话生成（旧草稿由恢复入口显式处理）。
- 每次文本变化捕获不可变快照；不要在后台线程读 UI 控件。
- 草稿写入失败：捕获 `DraftStoreException`，状态 `DraftProtectionState.Failed`，保留快照，后续可重试；不影响数据库保存尝试。
- 草稿去抖任务只写最新快照；旧草稿任务不得覆盖新草稿。

- [ ] **Step 3: 运行会话测试**

```bash
dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/QuickNotes.Desktop.Tests.csproj --filter FullyQualifiedName~NoteEditorSessionTests
```

Expected: 全部通过；无未观察的异步异常。

- [ ] **Step 4: 提交**

```bash
git add QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Editing QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/NoteEditorSessionTests.cs
git commit -m "feat: 实现编辑器快照、草稿捕获与状态机"
```

### Task 3: 串行保存、迟到结果、冲突与未知结果

**Files:**
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Editing/NoteEditorSession.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Editing/EditorConflict.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/NoteEditorSessionSaveTests.cs`

**Interfaces:**
- Produces:
  - `Task<SaveResult> SaveNowAsync(CancellationToken)`：立即保存最新快照；同一便签串行。
  - `Task<EditorSaveResolution> RetryAsync(CancellationToken)`：处理 OutcomeUnknown，先回读再决定保存/冲突。
  - `Task ReloadAsync()`：显式放弃本地版本并载入当前数据库版本（需调用方确认）。
  - `Task<NoteEditorSession> SaveAsNewAsync()`：本地内容复制为新便签/新草稿，不覆盖远程版本。
  - `EditorConflict`：本地快照 + 当前数据库便签/版本。

- [ ] **Step 1: 写保存队列与结果测试**

```text
Save_Success_MarksSavedAndAdvancesBaseVersion
Save_WhileNewerEditQueued_DoesNotMarkNewerEditSaved
Save_StaleResult_DoesNotClearNewerModifiedState
SecondSaveWaitsForFirst_AndUsesLatestSnapshot
Conflict_PreservesLocalSnapshotAndDraft
Conflict_Reload_LoadsRemoteVersion
Conflict_SaveAsNew_CreatesDistinctNote
OutcomeUnknown_Retry_ReadbackMatchingContentMarksSaved
OutcomeUnknown_Retry_NoStoredNote_CreatesWithoutDuplicate
OutcomeUnknown_Retry_StoredDifferentContent_ReportsConflict
SaveFailure_RetainsDraftAndState
```

使用可控 `TaskCompletionSource` fake coordinator 模拟在途保存、延迟完成、冲突、未知；fake repository 的 readback。

- [ ] **Step 2: 实现保存队列**

规则：

```text
- 每个 session 一个 _saveGate/SemaphoreSlim(1,1)
- SaveNow 获取租约后读取最新 snapshot sequence
- 保存完成时：
  - Saved：更新 BaseVersion=saved.Version；IsNew=false
    - 若 current.Sequence == sent.Sequence -> SaveState=Saved, HasPendingChanges=false
    - 若 current.Sequence > sent.Sequence -> 保持 Modified/Drafting；按需排队保存最新
  - Conflict：SaveState=Conflict，保存 CurrentNote/CurrentVersion；本地快照/草稿保留
  - OutcomeUnknown：SaveState=OutcomeUnknown；草稿保留；不自动重试
  - StorageUnavailable/ValidationFailed/NotFound：SaveFailed；保留草稿
- 迟到结果不得覆盖当前文本/快照；只允许安全更新 BaseVersion
```

- `CreateAsync` 对新建便签使用同一 NoteId；重试未知结果前用 `INoteRepository.GetAsync` 回读：
  - 存在且内容/版本匹配 → 视为已保存；
  - 存在但内容不同 → Conflict；
  - 不存在 → 再次 Create（稳定 ID，不会产生重复）。
- 更新未知结果：回读当前版本/content；匹配则确认；不匹配则 Conflict；不存在则 SaveFailed/NotFound。
- `SaveAsNewAsync` 生成新的 NoteId/DraftId，复制本地内容；旧草稿不删除（显式恢复/丢弃入口处理）。

- [ ] **Step 3: 运行保存测试**

```bash
dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/QuickNotes.Desktop.Tests.csproj --filter FullyQualifiedName~NoteEditorSessionSaveTests
```

Expected: 全部通过；无竞态/未观察异常。

- [ ] **Step 4: 提交**

```bash
git add QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Editing QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/NoteEditorSessionSaveTests.cs
git commit -m "feat: 实现编辑器串行保存、冲突与未知结果处理"
```

### Task 4: 工作区、草稿恢复与宿主 Ready 服务

**Files:**
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Editing/EditorWorkspaceService.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Editing/DraftRecoveryService.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Editing/DraftRecoveryItem.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Hosting/DesktopEditingServices.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Hosting/DesktopApplicationHost.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/EditorWorkspaceServiceTests.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/DraftRecoveryServiceTests.cs`

**Interfaces:**
- Produces:
  - `EditorWorkspaceService.ListActiveAsync()`、`CreateNewAsync()`、`OpenAsync(Guid)`、`OpenFromDraftAsync(NoteDraft)`。
  - `DraftRecoveryService.ListAsync()` → `DraftRecoveryInventory`（items + issues）；`DiscardAsync(Guid)`。
  - `DesktopEditingServices`：`INoteRepository Notes, INoteDraftStore Drafts, INoteSaveCoordinator Saves, EditorWorkspaceService Workspace, DraftRecoveryService DraftRecovery`。
  - `DesktopApplicationHost.EditingServices`：仅在 `Assessment.CanEnterNormalUse` 后创建；Ready/首次初始化/恢复成功后可进入编辑器。

- [ ] **Step 1: 写工作区/草稿恢复测试**

```text
ListActive_ReturnsActiveNotesOnly
CreateNew_UsesUncategorizedAndEmptyTags
Open_LoadsTags
DraftRecovery_ListsValidDraftsWithExistingNoteFlag
DraftRecovery_CorruptDraft_ReturnsIssueAndOtherDrafts
DraftRecovery_NewNoteDraft_OpensNewSessionWithStableIds
DraftRecovery_ExistingNoteDraft_PreservesBaseVersion
DiscardDraft_DeletesOnlyRequestedDraft
EditingServices_NotCreatedWhenWriteBlocked
EditingServices_CreatedWhenReady
```

- [ ] **Step 2: 实现草稿恢复扫描**

- 枚举 `DesktopPaths.DraftDirectory/*.json`，按文件名解析 DraftId；逐个 `INoteDraftStore.GetAsync`。
- 单个损坏草稿记录 `DraftRecoveryIssue`（文件名/DraftId/错误码），不阻断其它草稿；不删除损坏文件。
- 既有便签草稿：`DraftRecoveryItem.NoteExists=true`；新便签草稿：false。
- 恢复只载入编辑器，不自动提交；丢弃必须显式调用并二次确认（UI 层）。
- 草稿恢复失败/损坏不得阻止 C1 启动或进入 Ready。

- [ ] **Step 3: 宿主接线**

- Ready 后创建 `SqliteNoteRepository`、`FileNoteDraftStore(paths.DraftDirectory)`、`NoteSaveCoordinator`（共享 `PersistentDatabaseWriteGate`）。
- `DesktopEditingServices` 只创建一次；恢复/阻断状态不创建编辑器服务。
- 恢复成功/`RetryStartupAsync` 变为 Ready 时刷新 `EditingServices`。
- 不改变 C1 启动/恢复语义；不直接打开 SQL。

- [ ] **Step 4: 运行测试**

```bash
dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/QuickNotes.Desktop.Tests.csproj --filter "FullyQualifiedName~EditorWorkspaceServiceTests|FullyQualifiedName~DraftRecoveryServiceTests|FullyQualifiedName~DesktopApplicationHostTests"
```

Expected: 全部通过；Ready 宿主暴露编辑服务，阻断宿主不暴露。

- [ ] **Step 5: 提交**

```bash
git add QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Editing QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Hosting QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/EditorWorkspaceServiceTests.cs QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/DraftRecoveryServiceTests.cs QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/DesktopApplicationHostTests.cs
git commit -m "feat: 实现编辑器工作区、草稿恢复与宿主接线"
```

### Task 5: WPF 最小工作区与关闭/退出流程

**Files:**
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/WorkspaceWindow.xaml`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/WorkspaceWindow.xaml.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/DraftRecoveryWindow.xaml`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/DraftRecoveryWindow.xaml.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/App.xaml.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/StartupWindow.xaml.cs`
- Create: `QuickNotes_AgentHandoff/docs/agent/C2-人工检查清单.md`

**Interfaces:**
- Consumes: `DesktopApplicationHost.EditingServices`、`EditorWorkspaceService`、`DraftRecoveryService`、`NoteEditorSession`。
- Produces: 可运行最小工作区：新建/打开/编辑/保存/草稿恢复/冲突/未知/退出。

- [ ] **Step 1: 实现工作区布局**

`WorkspaceWindow`：

```text
左侧：Active 便签 ListBox（标题/正文摘要/修改时间）
右侧：标题 TextBox + 正文 TextBox
顶部：新建、刷新、保存
底部/侧栏：保存状态、草稿保护状态、错误/冲突/未知结果操作
菜单/按钮：草稿恢复
```

- 新建后立即创建 session（稳定 ID），不等首次保存。
- 列表刷新只读 Active；不实现分类/标签/归档/回收站 UI。
- 现有便签的标签/分类保持不变；新便签默认未分类、无标签。
- 所有按钮命令 async；保存期间禁用保存按钮，但 TextBox 仍可输入。

- [ ] **Step 2: 状态与草稿呈现**

- SaveState：Clean/Modified/Drafting/Saving/Saved/SaveFailed/Conflict/OutcomeUnknown，分别显示中文状态。
- DraftState：Protected/Pending/Failed；失败显示“内容尚未得到本地保护”，提供重试草稿按钮。
- `Saved` 只对应 `SaveResult.Saved` 且快照序号匹配；迟到结果不误报。
- 冲突弹窗/面板：显示本地与数据库版本摘要，提供“重新载入数据库版本”和“另存为新便签”；不自动合并。
- OutcomeUnknown：显示“保存结果未知”，提供重试（回读）与保留草稿；不得显示成功。
- 草稿恢复窗口：列出时间/来源/是否有对应便签，提供查看、恢复、显式丢弃；损坏草稿显示问题但不阻塞。

- [ ] **Step 3: 关闭/退出与切换**

- 窗口 `Closing`：若有未保存修改/在途保存，`e.Cancel=true` 并启动异步流程：
  1. 先 `FlushDraftAsync`；
  2. 尝试 `SaveNowAsync`；
  3. 成功 → 关闭；
  4. 失败/未知 → 显示“重试/保留草稿退出/取消”；
  5. 选择保留草稿退出 → 关闭；取消 → 保持窗口。
- 切换便签：先按同一流程保存/保护；未成功则提示，不静默丢弃。
- UI 不阻塞等待 B2 重试；保存任务在后台，窗口保持响应。
- 系统会话结束/强制终止只做已调度的草稿落盘；未落盘窗口边界在人工清单中注明。

- [ ] **Step 4: App 路由与恢复入口**

- `App.StartAsync`：`Assessment.CanEnterNormalUse && EditingServices != null` → `WorkspaceWindow`；否则 `StartupWindow`。
- `StartupWindow` 在恢复成功/重试 Ready 后显示“打开编辑器”，创建 WorkspaceWindow 并关闭自身。
- 关闭 WorkspaceWindow 时释放编辑器 session/取消去抖；App 仍负责 host/单实例释放。
- 第二实例激活复用现有 WorkspaceWindow/StartupWindow；不创建第二个编辑窗口。

- [ ] **Step 5: 构建与冒烟**

```bash
dotnet build QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln
dotnet run --project QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/QuickNotes.Desktop.Wpf.csproj -- --data-root "$env:TEMP\QuickNotes-C2-Demo"
```

记录：新建、输入、自动保存、显式保存、关闭重开、草稿恢复。无法观察的视觉/交互项写入人工清单，标未运行。

- [ ] **Step 6: 提交**

```bash
git add QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf QuickNotes_AgentHandoff/docs/agent/C2-人工检查清单.md
git commit -m "feat: 实现 C2 最小编辑工作区与退出保护"
```

### Task 6: C2 集成测试、文档、审查与提交

**Files:**
- Create: `QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/EditorPersistenceIntegrationTests.cs`
- Create: `QuickNotes_AgentHandoff/docs/agent/C2-测试报告.md`
- Create: `QuickNotes_AgentHandoff/docs/agent/C2-完成总结.md`
- Modify: `QuickNotes_AgentHandoff/docs/decisions/README.md`
- Modify: `README.md`、`QuickNotes_AgentHandoff/README.md`
- Modify: `QuickNotes_AgentHandoff/docs/agent/C阶段-PRD覆盖表.md`

- [ ] **Step 1: 写真实 SQLite/草稿集成测试**

```text
NewNote_EditSaveReopen_RoundTrips
SaveWhileEditing_NewerEditNotReportedSaved
DraftSurvivesRestart_AndCanBeRestored
DraftRecovery_DoesNotAutoSubmit
Conflict_TwoSessions_PreservesBoth
UnknownOutcome_ReadbackPreventsDuplicateNote
CloseWithFailedSave_RetainsDraftAndContent
```

使用临时数据根、真实 `SqliteNoteRepository`/`FileNoteDraftStore`/`NoteSaveCoordinator`；不访问真实用户数据。

- [ ] **Step 2: 写测试报告**

分开记录通过/失败/未运行：B 313 回归、C2 单元/桌面/集成、WPF 人工；未运行项至少包括托盘/快捷键/多屏/真实强杀/真实卷/ACL/性能/长时间、schema v2、备份 UI、导出。

- [ ] **Step 3: 写完成总结**

按 C 文档 6.3：结论/范围/前置；变更文件与接口；PRD 证据；实际命令/环境/通过/失败/未运行；数据保护/草稿/冲突/未知结果验证；ADR/审查发现；限制与 C3 入口；分支/提交状态。

- [ ] **Step 4: 更新索引/README/PRD 覆盖**

- ADR 索引加入 C2 时序 ADR。
- README 加入工作区启动/使用说明、C2 范围。
- PRD 覆盖表更新 10.x、8.4、18.1 的 C2 状态；未实现项不得标记通过。

- [ ] **Step 5: 全量验证与双轴审查**

```bash
dotnet build QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln
dotnet test QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln --no-build --no-restore
dotnet list QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln package --vulnerable --include-transitive
```

标准轴：UI 不直接 SQL、保存只经协调器、异步/取消/资源释放、快照/迟到结果、草稿清理安全、无死链。
规格轴：逐条对照 C2 工作/验收/交付；确认快速输入、保存中编辑、迟到回调、草稿失败、冲突、未知结果、新建重试、退出有证据；确认未实现 C3+。

- [ ] **Step 6: 提交**

```bash
git add QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/EditorPersistenceIntegrationTests.cs QuickNotes_AgentHandoff/docs/agent/C2-测试报告.md QuickNotes_AgentHandoff/docs/agent/C2-完成总结.md QuickNotes_AgentHandoff/docs/decisions/README.md QuickNotes_AgentHandoff/docs/agent/C阶段-PRD覆盖表.md README.md QuickNotes_AgentHandoff/README.md
git commit -m "docs: 完成 C2 测试报告与交付总结"
```

## Self-Review

**Spec coverage**

- C2 新建/打开/编辑/重开 → Task 2/4/5/6。
- 只经 `INoteSaveCoordinator` → Task 1/3/4；UI 无 SQL。
- 自动保存去抖/取消/每便签串行 → Task 2/3 + 时序 ADR。
- 快照序号/迟到结果不误报 → Task 3。
- 草稿自动捕获/失败提示/重启恢复 → Task 2/4/5/6。
- 新建稳定 ID/重试不重复 → Task 3。
- 冲突保留双方/比较/另存/重载 → Task 3/5。
- 关闭/退出/系统结束处理 → Task 2/5。
- 最小使用闭环/人工清单 → Task 5/6。
- C3+ 明确未实现。

**Placeholder scan**

- 无 TBD/TODO；未运行项必须逐项记录。时序数字为 C2 ADR 候选值，不是 PRD 已确认指标。

**Type consistency**

- 统一类型：`EditorSnapshot`、`EditorSessionStatus`、`EditorSaveState`、`DraftProtectionState`、`NoteEditorSession`、`EditorWorkspaceService`、`DraftRecoveryService`、`DesktopEditingServices`。
- 复用既有接口：`INoteRepository`（新增 ListAsync）、`INoteSaveCoordinator`、`INoteDraftStore`、`IDatabaseWriteGate`；不新建保存/恢复语义。

## 执行前需要确认

- C2 是否按本计划实施；是否接受时序候选值（草稿 300ms、保存 800ms、最大 5s）。
- 最小工作区布局是否接受（左侧列表 + 右侧标题/正文 + 草稿恢复面板），不实现分类/标签/生命周期 UI。
- 冲突处理是否按“重载数据库版本 / 另存为新便签”实现，不做自动合并。
- 关闭窗口时采用“先草稿、再保存、失败则重试/保留草稿退出/取消”的交互。
