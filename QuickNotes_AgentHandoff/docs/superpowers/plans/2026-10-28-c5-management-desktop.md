# C5 分类/标签、生命周期与管理中心/桌面行为 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 C4 备份/恢复闭环之上交付 C5 管理能力：分类、标签、归档/回收站/恢复、搜索/筛选/多选/撤销/副本，管理中心的完整编辑与设置，独立便签窗口、托盘、关闭/启动、全局隐藏、全局快捷键与多显示器/DPI 找回。

**Architecture:** C5 分两批交付。**C5-A** 扩展 Application 数据查询/管理服务，把 `WorkspaceWindow` 升级为管理中心并加入设置/外观/分类/标签/生命周期/搜索/多选/撤销/副本。**C5-B** 在 Desktop.Wpf 新增独立便签窗口、编辑器会话注册表、托盘/关闭/启动、全局隐藏、Win32 全局快捷键与窗口几何/DPI 映射；所有写入仍经 Application 服务/保存协调器与 C3 变更序号。

**Tech Stack:** .NET 10、WPF、WinForms `NotifyIcon`（仅托盘）、SQLite（Microsoft.Data.Sqlite 10.0.0）、xUnit 2.9.3、Win32 `RegisterHotKey`/窗口/显示器 API。

**Spec:** `QuickNotes_AgentHandoff/docs/agent/Agent对接文档_C阶段.md` C5；PRD 4.2、4.4、5、6、7、8、9、10.2、10.3、11.5、13、14、16.3、18.1、20；C3 字段策略；C4 完成总结；A 原型 README/ADR；C1 启动/单实例 ADR。

**Branch prerequisite:** C3/C4 已合并到 `main` / `origin/main`（`ec75068`）；本分支 `feature/c5-management-desktop` 基于该提交。

---

## Global Constraints

- Windows 11 x64；不承诺 Windows 10/ARM/Server。
- 所有便签、分类、标签、生命周期、外观、设置写入必须经 Application 服务/保存协调器；不得 UI 直接 SQL/绕过 C3 `change_sequence`。
- 隐藏、归档、回收站是不同状态：单张隐藏只改 `display_intent`；归档/回收站改变 lifecycle 并保持独立元数据；不得把隐藏误判为删除。
- 全局隐藏不覆盖单张 `display_intent`；全局恢复只显示原本应显示的 Active 便签；归档/回收站不得出现桌面窗口。
- 旧保存/批量结果不得覆盖更新版本；冲突保留双方；批量/撤销失败不得静默报告成功。
- 窗口几何使用逻辑 DIP，显示前按当前显示器工作区 clamp；显示器断开/DPI 变化后便签必须可找回且可操作。
- 快捷键注册失败必须明确显示，不得伪装已启用；开机启动只按用户明确设置注册并核验实际状态。
- 日志/诊断不得记录便签正文、草稿内容或完整敏感路径；错误提示提供可执行操作。
- 测试只用合成数据和临时目录；不操作用户真实便签、托盘/快捷键/开机启动副作用（测试要清理）。
- C5 不实现导出、安装器、自动更新、备份加密、云同步、OS 通知（C7/C6 范围）；不把 A 原型历史结果当作 C5 通过证据。
- 不改变 C4 备份/恢复语义；C5 新增功能必须进入后续备份/恢复或明确设备边界。

---

## 0. 范围与候选决策（需确认）

1. **完整管理中心**：C5 包含 PRD 7 的基础搜索（标题/正文/标签）、分类/标签/日期筛选、排序、多选批量移动/加标签/移除标签/归档/移入回收站、批量撤销，以及副本/生命周期操作。若排期过大，建议按 C5-A/C5-B 两批执行；不可把搜索/多选/撤销标为已实现却不做。
2. **副本与撤销**：C5 实现 PRD 5.6/7.5 的副本和单次/批量撤销；撤销不覆盖撤销后发生的独立修改。
3. **共享编辑会话**：同一便签在管理中心与独立便签窗口共享一个 `NoteEditorSession`（按 NoteId 注册），避免两套会话互相覆盖/光标重置；会话生命周期由两个窗口引用计数。
4. **独立便签窗口**：在 `QuickNotes.Desktop.Wpf` 新建 `NoteWindow`，不直接引用 A 原型项目；A 的托盘/快捷键/关闭三选项作为行为参考。便签窗口 `ShowInTaskbar=false`，X 只隐藏，不参与 Alt+Tab。
5. **全局隐藏状态**：`ApplicationSettings.GlobalHidden` 持久化；隐藏期间不改单张 intent；全局恢复显示 Active + Visible 的便签；隐藏期间单独显示某便签会更新其 intent，但不恢复其他便签。启动时若 GlobalHidden=true，桌面便签保持隐藏。
6. **托盘**：使用 WinForms `NotifyIcon`（WPF 项目启用 `UseWindowsForms`），左键激活管理中心，右键菜单新建/全局隐藏恢复/管理中心/设置/退出；关闭管理中心 X 三选项。
7. **快捷键**：Win32 `RegisterHotKey`，默认 `Ctrl+Alt+N/M/H`（候选，最终按 PRD 原型确认）；配置存 `device-settings.json`；冲突/注册失败逐项提示且不显示成功；禁用/修改后实际重新注册。
8. **开机启动**：默认关闭；设置页显式开启后写 HKCU Run，带 `--startup` 参数；启动参数使管理中心隐藏、恢复应显示便签；卸载/关闭只移除本应用注册。C5 实现注册与状态核验，不要求安装器。
9. **窗口找回**：使用逻辑 DIP + `System.Windows.Forms.Screen`/Win32 工作区 clamp；显示器断开/分辨率变化后回到最近可用工作区；保存修正后的布局。多屏/DPI 人工矩阵必须实跑，A-11 历史失败不能当作已解决。
10. **通知/回收站自动清理**：OS 通知留 C7；回收站自动清理期限的完整策略留 C6/C7，C5 只提供回收站列表/恢复/永久删除。
11. **分类层级**：首版保持扁平；schema 不新增层级字段。

---

## 1. 工作批次

| 批次 | 任务 | 交付 |
|---|---|---|
| C5-A 管理中心 | Task 1–4 | 查询/管理服务、管理中心、设置/外观、分类/标签/生命周期/搜索/多选/撤销/副本 |
| C5-B 桌面行为 | Task 5–9 | 共享会话、便签窗口、托盘/关闭/启动、全局隐藏、快捷键、DPI/多屏、可访问性 |
| 交付 | Task 10 | 全量测试、报告、人工矩阵、文档、审查 |

C5-A 完成即可验收管理数据/UI；C5-B 完成后再做桌面人工矩阵。两批都必须通过全量回归，不得只在 UI 层验证。

---

## 2. 文件结构（预计）

```text
QuickNotes_AgentHandoff/
├─ src/QuickNotes.Application/
│  ├─ NoteQuery.cs / NoteSortOrder.cs          # 新：查询契约
│  ├─ INoteRepository.cs                       # 查询 + 生命周期/显示意图
│  ├─ ICatalogRepository.cs                    # 分类/标签列表、合并
│  ├─ NoteManagementService.cs                 # 管理操作/批量/副本
│  ├─ NoteManagementResult.cs                  # 逐项结果/错误
│  ├─ ManagementUndoService.cs                 # 单次/批量撤销
│  ├─ ManagementUndoToken.cs
│  └─ ...
├─ src/QuickNotes.Infrastructure.Sqlite/
│  ├─ SqliteNoteRepository.cs                  # 查询 SQL、生命周期+显示意图
│  ├─ SqliteCatalogRepository.cs               # 列表、标签合并
│  └─ ...
├─ src/QuickNotes.Desktop.Core/
│  ├─ Editing/EditorSessionRegistry.cs         # 共享会话/引用计数
│  ├─ Editing/NoteEditorSession.cs             # ChangePin/Category/Tags/...
│  ├─ Display/GlobalDisplayState.cs            # 全局隐藏/显示意图纯逻辑
│  ├─ Windows/WindowPlacementCalculator.cs     # 逻辑坐标 clamp/显示器选择
│  ├─ Hotkeys/HotkeyGesture.cs                 # 解析/校验/冲突检测
│  ├─ Hotkeys/IGlobalHotkeyRegistrar.cs        # 可测试注册契约
│  ├─ Startup/StartupRegistrationService.cs    # HKCU Run 注册/核验
│  └─ Hosting/DesktopApplicationHost.cs        # 服务组合/启动参数
├─ src/QuickNotes.Desktop.Wpf/
│  ├─ ManagementWindow.xaml/.cs                # 由 WorkspaceWindow 演进/重命名
│  ├─ NoteWindow.xaml/.cs                      # 独立便签窗口
│  ├─ NoteEditorControl.xaml/.cs               # 管理/便签共用编辑视图
│  ├─ SettingsWindow.xaml/.cs                  # 设置页
│  ├─ CategoryDialog/TagDialog/CloseChoiceWindow
│  ├─ TrayIconService.cs                       # WinForms NotifyIcon
│  ├─ GlobalHotkeyService.cs                   # Win32 注册/WM_HOTKEY
│  ├─ NoteWindowManager.cs                     # 显示/隐藏/定位/全局隐藏
│  ├─ App.xaml.cs                              # 启动/隐藏/退出
│  └─ ...
└─ tests/
   ├─ QuickNotes.UnitTests/                    # 查询/管理/撤销/几何/快捷键解析
   ├─ QuickNotes.IntegrationTests/             # 真实 SQLite 分类/标签/生命周期/批量
   └─ QuickNotes.Desktop.Tests/                # 会话共享/宿主/托盘/启动/几何
```

---

### Task 1: 查询契约、分类/标签列表与管理服务基础

**Files:**
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Application/NoteQuery.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Application/NoteSortOrder.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Application/NoteManagementResult.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Application/NoteManagementService.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Application/INoteRepository.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Application/ICatalogRepository.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Infrastructure.Sqlite/SqliteNoteRepository.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Infrastructure.Sqlite/SqliteCatalogRepository.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/NoteQueryTests.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.UnitTests/NoteManagementServiceTests.cs`

**Interfaces:**
- `NoteQuery`:
  ```csharp
  public sealed record NoteQuery
  {
      public NoteLifecycle? Lifecycle { get; init; } // null = Active + (if IncludeArchived) Archived
      public Guid? CategoryId { get; init; }
      public IReadOnlyCollection<Guid> AnyTagIds { get; init; } = Array.Empty<Guid>();
      public string? SearchText { get; init; }
      public bool IncludeArchived { get; init; }
      public NoteSortOrder Sort { get; init; } = NoteSortOrder.ModifiedDescending;
  }
  public enum NoteSortOrder { ModifiedDescending, ModifiedAscending, CreatedDescending, TitleAscending }
  ```
- `INoteRepository` add:
  - `Task<IReadOnlyList<Note>> QueryAsync(NoteQuery query, ct)`
  - `Task<Note> SetLifecycleAndDisplayAsync(Guid id, NoteLifecycle lifecycle, NoteDisplayIntent displayIntent, long expectedVersion, DateTimeOffset modifiedAtUtc, ct)`（现有 `SetLifecycleAsync` 保留并委托为 Hidden）。
- `ICatalogRepository` add:
  - `Task<IReadOnlyList<Category>> ListCategoriesAsync(ct)`
  - `Task<IReadOnlyList<Tag>> ListTagsAsync(ct)`
  - `Task MergeTagAsync(Guid sourceTagId, Guid targetTagId, ct)`：同事务把 note_tags 从 source 改到 target（去重），删除 source tag，递增变更序号。
- `NoteManagementService` 基础方法：
  - `Task<IReadOnlyList<Note>> QueryAsync(NoteQuery, ct)`
  - `Task<Note> MoveToCategoryAsync(Guid noteId, Guid categoryId, long expectedVersion, ct)`
  - `Task<Note> AddTagsAsync(Guid noteId, IReadOnlyCollection<Guid> tagIds, long expectedVersion, ct)`
  - `Task<Note> RemoveTagsAsync(...)`
  - `Task<Note> ArchiveAsync(Guid noteId, long expectedVersion, ct)`
  - `Task<Note> UnarchiveAsync(...)`
  - `Task<Note> MoveToTrashAsync(...)`
  - `Task<Note> RestoreFromTrashAsync(...)`
  - `Task DeletePermanentlyAsync(Guid noteId, ct)`
  - `Task<Note> CreateCopyAsync(Guid noteId, ct)`
  - 每次操作加载当前 Note、保留未修改字段、用 expectedVersion 更新；分类/标签 ID 校验。
- `NoteManagementResult`（批量时逐项状态/错误，后续任务使用）。

- [ ] **Step 1: Write failing query/service tests**

```csharp
[Fact]
public async Task Query_FiltersActiveByCategoryTagAndSearch_WithOrTagLogic()
{
    // 3 notes: A(cat1,tag1,body "alpha"), B(cat1,tag2,body "beta"), C(cat2,tag1,body "alpha")
    // Query Active + cat1 + AnyTagIds={tag2} + Search "alpha" -> 仅 A（tag OR 命中 tag1, search 命中）
    // 空 tag 集合 = 不按标签过滤
}

[Fact]
public async Task Query_IncludeArchived_DoesNotMixTrash()
{
    // Active/Archived/Trash 各一；IncludeArchived=true 返回 Active+Archived，不返回 Trash。
}

[Fact]
public async Task Archive_SetsArchivedAndHidden_UnarchiveReturnsActiveHidden()
{
    // 备份/恢复元数据不丢失；Unarchive 后 display_intent=Hidden。
}

[Fact]
public async Task CreateCopy_CopiesContentCategoryTagsAppearance_WithNewIds()
{
    // 标题追加“(副本)”；新 NoteId/version=1；布局错位；原便签不变。
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/QuickNotes.IntegrationTests.csproj --filter FullyQualifiedName~NoteQueryTests`
Run: `dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.UnitTests/QuickNotes.UnitTests.csproj --filter FullyQualifiedName~NoteManagementServiceTests`
Expected: 编译失败/断言失败。

- [ ] **Step 3: Implement query and service**

`QueryAsync` SQL 要点：
- `Lifecycle` 显式时按该 lifecycle；否则（正常列表）`lifecycle=Active`；`IncludeArchived` 时 `lifecycle IN (0,1)`；永不包含 Trash。
- `CategoryId` 参数化；`AnyTagIds` 用 `EXISTS (SELECT 1 FROM note_tags nt WHERE nt.note_id=notes.id AND nt.tag_id IN (...))`，OR 逻辑。
- `SearchText` 对 `title LIKE $pattern ESCAPE '\' OR body LIKE ... OR EXISTS(tag name LIKE ...)`；转义 `%`/`_`/`\`；大小写按 SQLite NOCASE/Unicode 约定；空文本不过滤。
- 排序：修改时间降序/升序、创建时间降序、标题升序；同序按 `id` 稳定。
- 旧 `ListAsync(lifecycle)` 保留并委托 Query（兼容 C3/C4）。
- `SetLifecycleAndDisplayAsync` 在 WHERE id/version 中更新 lifecycle、display_intent、modified_at、version+1，更新 failure 走既有冲突/未找到翻译；递增 C3 marker。
- `NoteManagementService` 每次先 Get + GetTagIds，验证；Archive/Unarchive/Trash/Restore 使用 lifecycle+display；复制创建新 Note 并复制标签/外观/布局（布局偏移 +28 DIP，至少 clamp 前的合理值）。
- 所有写路径经 `INoteRepository`/`INoteSaveCoordinator` 或管理服务的统一写入门；不新增直接 SQL 到 UI。

- [ ] **Step 4: Run tests and commit**

```bash
dotnet build QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln
dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/QuickNotes.IntegrationTests.csproj --filter FullyQualifiedName~NoteQueryTests
dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.UnitTests/QuickNotes.UnitTests.csproj --filter FullyQualifiedName~NoteManagementServiceTests
git add QuickNotes_AgentHandoff/src/QuickNotes.Application QuickNotes_AgentHandoff/src/QuickNotes.Infrastructure.Sqlite QuickNotes_AgentHandoff/tests
git commit -m "feat: 实现 C5 查询与管理服务基础"
```

---

### Task 2: 批量操作、撤销与副本语义

**Files:**
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Application/ManagementUndoToken.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Application/ManagementUndoService.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Application/NoteManagementService.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Application/BatchOperationResult.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.UnitTests/ManagementUndoServiceTests.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/NoteManagementBatchTests.cs`

**Interfaces:**
- 批量方法：
  - `Task<BatchOperationResult> MoveToCategoryAsync(IReadOnlyCollection<Guid> noteIds, Guid categoryId, ct)`
  - `AddTagsAsync/RemoveTagsAsync/ArchiveAsync/MoveToTrashAsync` 批量
  - 返回每项 `NoteManagementItemResult(NoteId, Status, ErrorCode, Note?)`；全部成功才 `Succeeded`；部分失败保留逐项原因，不覆盖未成功项。
- `ManagementUndoToken`：操作类型、逐项 prior snapshot（lifecycle/display/category/tags/appearance/layout/version）、是否可撤销、描述。
- `ManagementUndoService.TryUndoAsync(token, ct)`：按快照逆操作；使用当前版本校验；遇到后续独立修改返回 `Stale`，不覆盖该便签；批量逐项结果。
- 副本撤销：未修改副本移入回收站；已修改需 UI 确认后移入回收站（服务提供 `PrepareUndoCopy`/标记）。

- [ ] **Step 1: Write failing tests**

```csharp
[Fact]
public async Task BatchMove_PartialFailure_ReturnsPerItemAndDoesNotClaimSuccess()
{
    // 两项，第二项版本冲突 -> Overall=PartialFailed；第一项更新；第二项保留原值。
}

[Fact]
public async Task Undo_BatchMove_RestoresEachCategory_ButRespectsIndependentChanges()
{
    // 撤销前修改第二项分类 -> Undo 对第二项 Stale，第一项恢复。
}

[Fact]
public async Task CreateCopy_UndoUnmodified_MovesCopyToTrash()
```

- [ ] **Step 2–4: Implement, test, commit**

批量执行使用 `NoteManagementService` 单次方法；每项独立版本；不伪造全成功。撤销 token 只保留必要快照（不含正文正文副本，正文由版本 ID 恢复；快照包含 note id/version/lifecycle/intent/category/tagIds/appearance/layout）。提交：`feat: 实现 C5 批量操作、撤销与副本语义`。

---

### Task 3: 管理中心 UI（导航/搜索/筛选/多选/编辑面板）

**Files:**
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/WorkspaceWindow.xaml` / `.xaml.cs`（重命名为 `ManagementWindow` 或保留类名，内部从最小工作区升级）
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/NoteEditorControl.xaml` / `.xaml.cs`（管理中心/便签窗口共用编辑视图）
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/CategoryDialog.xaml` / `.xaml.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/TagDialog.xaml` / `.xaml.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Hosting/DesktopEditingServices.cs`（暴露 Catalog/Settings/Management）
- Test: 管理服务/查询已覆盖；WPF 人工清单在 Task 10

**Interfaces:**
- `DesktopEditingServices` 增加 `ICatalogRepository Catalog`、`IApplicationSettingsRepository Settings`、`NoteManagementService Management`、`ManagementUndoService Undo`。
- 管理中心布局：左侧导航（全部/各分类/各标签/归档/回收站）、顶部搜索/包含归档/分类标签筛选/排序、中间多选列表、右侧共用编辑器、底部批量工具栏/撤销。
- 列表项显示标题（无标题用正文首行/“无标题便签”）、摘要、修改时间、归档标记；归档/回收站视觉区分。
- 搜索/筛选变化清理不可见选择；编辑期间暂缓重排；保存/切换前处理未保存内容。
- 分类/标签操作：
  - 新建/重命名/默认颜色；删除分类需选择目标分类（默认未分类）；标签重命名冲突需确认合并；删除标签只解除关联；批量加/移除标签。
- 生命周期：
  - 归档/取消归档；移入回收站（无二次确认，提供即时撤销）；恢复（Active+Hidden）；永久删除需明确确认；批量操作。
- 键盘：Ctrl+N 新建、Ctrl+F 聚焦搜索、Esc 关闭菜单/取消、Ctrl+Z 正文撤销不与管理撤销混用。
- 复制按钮：创建副本并立即在管理中心选中；未修改副本撤销进回收站，修改后撤销提示确认。

- [ ] **Step 1: Implement layout and data binding**
  - Use `NoteQuery` and management service; no SQL in code-behind.
  - Load categories/tags from catalog; rebuild nav.
  - Multi-select: ListBox `SelectionMode=Extended` or checkboxes; selection changes update batch buttons; filter changes prune invalid selection.
  - Right editor binds shared session; save status/draft status; title/body/appearance controls.
- [ ] **Step 2: Category/tag dialogs and actions**
  - Validate names/colors; merge confirmation; delete category target; all calls async service.
- [ ] **Step 3: Lifecycle/batch/undo UI**
  - Archive/trash/restore/permanent delete; undo prompt for modified copies; update list after results; partial failure display per item.
- [ ] **Step 4: Build and manual smoke**
  - `dotnet build...`; run with temp root; record CP5-M UI items in checklist.
- [ ] **Step 5: Commit**
  `feat: 实现 C5 管理中心、分类标签与生命周期 UI`

---

### Task 4: 设置、外观与全局显示状态

**Files:**
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/SettingsWindow.xaml` / `.xaml.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Hosting/DesktopApplicationHost.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Hosting/DesktopEditingServices.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/ApplicationSettingsUiContractTests.cs`（服务层）
- Test: 管理 UI 人工清单

**Interfaces:**
- 设置页：默认字号（候选 8–72）、通知开关、备份目录（调用 C4 `BackupHost.SetBackupDirectoryAsync`/打开备份中心）、顶栏动作（`TopBarConfig`，最多 8/不重复）、全局快捷键编辑、开机启动开关（Task 7 服务）、关于/版本。
- 外观控件：颜色 FollowCategory/Manual + `#RRGGBB`、字号 override/global、顶栏动作；通过 `NoteEditorSession.ChangeAppearance`/`ChangePin`/`ChangeCategory`/`ChangeTags` 保存。
- 全局隐藏：设置页/托盘/快捷键切换 `ApplicationSettings.GlobalHidden`；不改单张 intent；管理器和便签窗口按状态显示。
- 设置写入用 `IApplicationSettingsRepository.UpdateAsync` 版本校验；设备设置用 `IDeviceSettingsStore` 原子写；失败提示具体。

- [ ] **Steps:** service contract tests → implement settings/display state → UI → manual smoke → commit `feat: 实现 C5 设置、外观与全局隐藏状态`.

---

### Task 5: 共享编辑会话与独立便签窗口

**Files:**
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Editing/EditorSessionRegistry.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Editing/NoteEditorSession.cs`（`ChangePin`/`ChangeCategory`/`ChangeTags`/引用计数可选）
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Editing/EditorWorkspaceService.cs`（通过 registry 获取会话）
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/NoteWindow.xaml` / `.xaml.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/NoteEditorControl.xaml` / `.xaml.cs`（Task 3 创建的共用编辑视图）
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/NoteWindowManager.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/ManagementWindow`/App
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/EditorSessionRegistryTests.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/NoteWindowManagerTests.cs`（可测核心逻辑）

**Interfaces:**
- `EditorSessionRegistry.GetOrOpenAsync(Guid noteId)`：同一 NoteId 返回同一 `NoteEditorSession`；引用计数；`ReleaseAsync(noteId)` 最后一个引用释放；管理/便签窗口共享文本/状态。
- `NoteEditorSession` 新增 `ChangePin`, `ChangeCategory(Guid)`, `ChangeTags(IReadOnlyCollection<Guid>)`，复用快照序号/串行保存/草稿。
- `NoteWindow`：`ShowInTaskbar=false`、不参与 Alt+Tab（工具窗口/owner 策略需实测）、无边框/自定义顶栏、标题/正文、置顶、颜色/字号、保存状态、X 只隐藏、定位高亮。
- `NoteWindowManager`：按 Active+Visible 便签创建/更新/隐藏窗口；全局隐藏/恢复；定位/激活；关闭隐藏；应用窗口布局；不显示 Archived/Trash。
- 管理窗口双击定位便签窗口；单个便签隐藏/显示更新 `display_intent`。

- [ ] **Steps:** registry tests → metadata session APIs → NoteWindow/window manager → management integration → manual smoke → commit `feat: 实现 C5 共享编辑会话与独立便签窗口`.

---

### Task 6: 托盘、关闭三选项、启动模式与全局隐藏

**Files:**
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/TrayIconService.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/CloseChoiceWindow.xaml` / `.xaml.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Startup/StartupRegistrationService.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Hosting/DesktopStartupOptions.cs`（`--startup`/`--background`）
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/App.xaml.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/ManagementWindow`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/StartupRegistrationServiceTests.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/GlobalDisplayStateTests.cs`

**Interfaces:**
- `TrayIconService`（WinForms NotifyIcon）：左键激活管理中心；右键菜单 新建/全局隐藏恢复/管理中心/设置/退出；唯一图标；退出释放图标。
- 管理中心 X：三选项关闭应用/缩小到托盘/取消；最小化到任务栏；关闭不退出。
- `--startup`：登录启动时不显示管理中心；恢复应显示便签；手动启动显示管理中心。设置页显式注册 HKCU Run，命令带 `--startup`；实际注册状态核验；关闭/卸载清理本应用项。
- `GlobalDisplayController`：`GlobalHidden` app setting + per-note display intent；Active+Visible 才显示便签；隐藏期间单独显示单张不恢复其他；全局恢复只显示原本 Visible；管理窗口始终可打开。
- 测试：启动参数解析、注册服务（临时注册表/抽象 registry）、全局隐藏状态逻辑、关闭三选项（可测服务/人工）。

- [ ] **Steps:** options/global state tests → startup registry abstraction → tray/close UI → app startup routing → manual smoke → commit `feat: 实现 C5 托盘、关闭/启动与全局隐藏`.

---

### Task 7: 全局快捷键与配置

**Files:**
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Hotkeys/HotkeyGesture.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Hotkeys/IGlobalHotkeyRegistrar.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/GlobalHotkeyService.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/SettingsWindow`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Hosting/DesktopApplicationHost.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.UnitTests/HotkeyGestureTests.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/GlobalHotkeyServiceTests.cs`（用 fake registrar）

**Interfaces:**
- `HotkeyGesture.Parse(string)`/`TryParse`：Ctrl/Alt/Shift/Win + key；禁用=null；重复组合检测。
- `IGlobalHotkeyRegistrar`：`Register(id, gesture)`/`Unregister(id)`/`ReleaseAll`；实现用 `RegisterHotKey`/`WM_HOTKEY`。
- 启动后按 DeviceSettings.Hotkeys 注册；注册失败逐项显示 Win32 错误码/冲突，不写入“已启用”假状态；设置页修改/禁用后重新注册并核验；不捕获普通输入/中文输入法。
- 快捷键动作：新便签/管理中心/全局隐藏恢复；仅在应用运行/单实例所有者注册。

- [ ] **Steps:** gesture tests → fake registrar registration/conflict tests → Win32 service/WM hook → settings UI → manual hotkey check (including conflict) → commit `feat: 实现 C5 全局快捷键与冲突提示`.

---

### Task 8: 窗口几何、DPI 与多显示器找回

**Files:**
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Windows/WindowPlacementCalculator.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/WindowPlacementService.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/NoteWindow.xaml.cs`（move/resize 保存）
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/NoteWindowManager.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/App.xaml.cs`（DisplaySettingsChanged）
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.UnitTests/WindowPlacementCalculatorTests.cs`
- Test: 人工矩阵（100/125/150/200%、多屏、断屏）

**Interfaces:**
- `WindowPlacementCalculator.EnsureVisible(layout, workAreas, primary)` → clamped layout；无 layout → 默认错位位置；原显示器缺失 → 最近工作区；宽度/高度受工作区限制；负坐标允许但 clamp 到可操作范围。
- `WindowPlacementService` 读取 WPF 逻辑 DIP 工作区（`SystemParameters.WorkArea`/WinForms Screen DPI 适配），在显示/显示器变化时应用；NoteWindow move/resize 去抖 `session.ChangeLayout` + save；显示器标识写 `DeviceSettings.DisplayBindings`（本机，不备份）。
- 不改变正文/分类/置顶；布局保存失败不阻断编辑，显示诊断。
- 人工矩阵记录设备/缩放/断屏结果；未测组合明确标注。

- [ ] **Steps:** pure clamp tests → service/monitor enumeration → NoteWindow wiring → manual matrix (single/multi/100–200%/disconnect) → commit `feat: 实现 C5 窗口几何与 DPI/多屏找回`.

---

### Task 9: 键盘、焦点、可访问性与错误 UX

**Files:**
- Modify: `ManagementWindow`/`NoteWindow`/dialogs XAML
- Create/Modify: `QuickNotes_AgentHandoff/docs/agent/C5-错误UX清单.md`
- Test: 人工键盘/焦点清单

**Interfaces:**
- Ctrl+N 新建、Ctrl+F 聚焦搜索、Esc 关闭菜单/取消、Ctrl+Z 仅正文编辑撤销（管理撤销独立按钮）。
- 所有交互可键盘到达；Tab/焦点可见；错误对话框有可执行按钮；空状态说明。
- 日志不含正文/草稿；关闭/隐藏/归档/删除提示按产品状态区分。
- 错误 UX：分类删除失败、标签合并冲突、批量部分失败、快捷注册失败、启动注册失败、窗口布局找回失败、永久删除确认、全局隐藏单张显示。

- [ ] **Steps:** UI tab order/labels → error UX doc → manual checklist → commit `feat: 补充 C5 键盘、焦点与错误 UX`.

---

### Task 10: 全量测试、文档、双轴审查与交付

**Files:**
- Create: `QuickNotes_AgentHandoff/docs/agent/C5-测试报告.md`
- Create: `QuickNotes_AgentHandoff/docs/agent/C5-完成总结.md`
- Create: `QuickNotes_AgentHandoff/docs/agent/C5-设备与人工矩阵.md`
- Modify: `docs/decisions/README.md`、`C阶段-PRD覆盖表.md`、READMEs
- Test: 全量解决方案、漏洞审计、WPF 冒烟、人工矩阵

- [ ] **Step 1: E2E/回归**
  - 管理服务/批量/撤销/副本真实 SQLite；会话共享/托盘/启动/全局隐藏/快捷键 fake；恢复后关系一致；C4 备份恢复回归。
- [ ] **Step 2: 报告/总结/矩阵**
  - 通过/失败/未运行分开；C5-M 清单；A-11 断屏历史失败需重测；混合 DPI、20 窗口、托盘/快捷键副作用清理。
- [ ] **Step 3: 索引/README/PRD**
  - ADR 索引、PRD 覆盖 4.2/4.4/5/6/7/8/9/10.2/13/14/16.3/18.1；未实现项不标通过。
- [ ] **Step 4: 全量验证与双轴审查**
  - build/test/vulnerable；标准轴（UI 无 SQL、写入协调、批量/撤销/会话、资源释放、日志无正文）；规格轴（C5 工作/验收/交付；A 历史证据不替代；C5+ 不越界）。
- [ ] **Step 5: 提交 docs。**

---

## Self-Review

**Spec coverage**
- 分类/标签/归档/回收站/恢复 → Task 1–3。
- 管理操作事务/版本/统一写入 → Task 1/2/3。
- 托盘/窗口/新建/显示隐藏/置顶/全局隐藏/退出/快捷键 → Task 5–7。
- 外观/窗口几何/顶栏 → Task 4/5/8。
- 快捷键冲突/注册失败/配置回退 → Task 7。
- 开机启动显式/核验 → Task 6/7。
- 多屏/DPI/断屏 → Task 8 + 人工矩阵。
- 全局隐藏与归档/回收站隔离 → Task 6。
- 错误提示/键盘/焦点/日志 → Task 9。
- 管理 UI 搜索/筛选/排序/多选/撤销/副本 → Task 1–3。

**Placeholder scan**
- 无 TBD/TODO；候选值集中在第 0 节。
- 类型名/方法名在任务间一致：`NoteQuery`、`NoteManagementService`、`ManagementUndoService`、`EditorSessionRegistry`、`GlobalDisplayController`、`HotkeyGesture`、`WindowPlacementCalculator`。

**Type consistency**
- 查询返回现有 `Note`；管理操作使用 `expectedVersion`；UI 通过 `DesktopEditingServices` 组合服务；共享会话按 NoteId 注册。
- `DeviceSettings.Hotkeys`/`ApplicationSettings.GlobalHidden` 使用 C3 既有字段，不新增 schema。

## 执行前需要确认

1. 是否接受 C5-A/C5-B 两批执行，并包含 PRD 7 的搜索/筛选/排序/多选/撤销/副本（否则明确裁剪并调整验收）。
2. 是否接受共享 `NoteEditorSession`（管理窗口与便签窗口同一会话）及引用计数生命周期。
3. 是否接受 `GlobalHidden` 持久化、单张显示更新 intent、启动时保持隐藏的语义。
4. 是否接受托盘使用 WinForms `NotifyIcon`、快捷键 Win32 `RegisterHotKey`、开机启动 HKCU Run。
5. 是否接受窗口找回使用逻辑 DIP + 当前工作区 clamp；A-11 断屏失败必须重测。
6. 是否接受 C5 不做 OS 通知/回收站自动清理期限/导出/安装器。
