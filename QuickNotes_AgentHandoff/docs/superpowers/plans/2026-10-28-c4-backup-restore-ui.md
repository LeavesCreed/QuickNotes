# C4 备份 UI、自动调度与恢复交互 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 C3 schema v2/迁移/确定性变更检测之上，交付可用的备份中心与整库恢复闭环：备份目录配置、手动/自动备份、列表校验与手动删除、15 分钟调度、恢复预检/确认/进度/取消、恢复后刷新与旧编辑会话失效。

**Architecture:** `Desktop.Core/Backup` 承载可测试的 `BackupHostService`（目录配置、手动备份、调度、列表/校验/删除、运行状态），WPF `BackupWindow` 只负责展示与命令；恢复由 `WorkspaceWindow` 先冻结/保存当前编辑并失效旧会话，再调用 C3 `RestoreCoordinator`（带进度回调），成功后由宿主重建服务并刷新窗口。备份目录存 `device-settings.json`，运行状态存数据根 `backup-status.json`，均不进入备份。

**Tech Stack:** .NET 10、WPF、SQLite（Microsoft.Data.Sqlite 10.0.0）、xUnit 2.9.3；既有 `DailyBackupService`、`BackupCoordinator`、`FileBackupCatalog`、`BackupPackageVerifier`、`RestoreCoordinator`、`IDatabaseWriteGate`、`IDeviceSettingsStore`。

**Spec:** `QuickNotes_AgentHandoff/docs/agent/Agent对接文档_C阶段.md` C4；PRD 11.1–11.7、13.2、18.3；C3 完成总结/字段与恢复策略；B4/B5 ADR；C1 启动恢复/阻断 ADR。

**Branch prerequisite:** C3 已合并到 `main` 与 `origin/main`（`e56cbfb`）；本 C4 分支 `feature/c4-backup-restore` 基于该提交，无需 rebase。C4 合并前不得跳过 C3 迁移/恢复契约。

---

## Global Constraints

- Windows 11 x64；不承诺 Windows 10。
- 不改变 C3/C2/B 的数据安全语义：保存只在提交成功/回读确认后成功；恢复失败不报成功；回退失败阻断写入跨重启有效。
- 自动备份仅在有配置备份目录时启用；未配置时明确提示，不静默假装已启用。
- 备份目录切换不搬移、不删除旧备份；手动备份不参与自动留存；应用内删除必须显式确认且只删除手动备份。
- 恢复前必须冻结新编辑、处理在途保存、捕获草稿；草稿落盘失败不得静默继续。
- 恢复中不允许普通写入越过恢复门；恢复成功后旧编辑会话、预期版本和异步回调必须失效，旧内容不得写回新库。
- 恢复保留的草稿标记为“恢复前来源”，不自动提交；设备设置文件不被恢复覆盖。
- 调度任务不重入；启动立即检查；默认 15 分钟（ADR 记录）；唤醒后补检查；失败后后续周期可重试。
- 进度按真实阶段显示；只有切换前阶段可取消；发布成功后不能因随后取消谎报失败。
- 所有测试使用合成数据/临时目录；不访问真实用户便签、草稿或备份。
- C4 不实现托盘、全局快捷键、分类/标签/生命周期 UI、安装器；OS 级通知延后到 C5/C7（C4 提供应用内持久状态）。

---

## 0. 执行前需要确认的候选决定

1. **调度周期**：默认 15 分钟（C4 文档建议）；启动后立即检查；系统唤醒触发一次检查；任务不重入。ADR 记录候选值。
2. **运行状态文件**：数据根 `backup-status.json`（设备本地、不进入备份），保存最近检查/成功/失败/警告与当前目录；不与 B4 `.quicknotes-backup-state.json` 合并。
3. **草稿恢复边界**：不升级草稿格式；成功恢复后写数据根 `draft-restore-boundary.json`，记录 restoreId、时间与恢复时存在的 draftId 集合；`DraftRecoveryService` 只对这些 ID 标记“恢复前草稿”。
4. **会话失效时机**：恢复执行前冻结并保存当前编辑，同时 `Invalidate` 旧会话（拒绝新保存/迟到回调）；失败/回退后重新打开当前便签的新会话。这样避免恢复门释放后旧保存写回。
5. **首启备份目录**：Ready 后若未配置，弹一次 `BackupSetupWindow`；允许“稍后配置”，明确显示自动备份未启用；不阻止进入编辑器。
6. **手动删除范围**：只允许删除目录中 `BackupType.Manual` 且校验可读的包；日备份/未知类型/损坏包不在 C4 删除（C6 清理策略）。
7. **OS 通知**：C4 只在备份中心/主窗口显示持久状态和重试入口；Windows 系统通知留 C5/C7，避免在无托盘/无包身份时伪造通知能力。
8. **恢复进度**：`RestoreCoordinator` 增加可选 `IProgress<RestoreProgress>`；只有切换前可取消，切换后显示不可取消；C4 UI 不伪造阶段。
9. **备份 UI 结构**：一个 `BackupWindow` 承载目录/状态/手动备份/列表/校验/删除/恢复；首启用轻量 `BackupSetupWindow`；不在 C4 做完整设置页。

---

## 1. 文件结构（预计）

```text
QuickNotes_AgentHandoff/
├─ src/QuickNotes.Application/
│  ├─ BackupRuntimeStatus.cs               # 新：运行状态
│  ├─ IBackupRuntimeStatusStore.cs         # 新
│  ├─ BackupOperationResult.cs             # 新：手动备份/目录/删除结果
│  ├─ BackupVerificationResult.cs          # 新：单包校验结果
│  ├─ RestoreProgress.cs / RestoreProgressStage.cs # 新
│  └─ IRestoreCoordinator.cs               # 增加 progress 重载
├─ src/QuickNotes.Desktop.Core/
│  ├─ Backup/BackupHostService.cs          # 新：调度/手动/列表/校验/删除
│  ├─ Backup/BackupHostOptions.cs          # 新：15 分钟候选
│  ├─ Backup/BackupDirectoryValidator.cs   # 新：目录可用性/同数据根检查
│  ├─ Backup/FileBackupRuntimeStatusStore.cs # 新：原子运行状态
│  ├─ Editing/DraftRestoreBoundary.cs      # 新
│  ├─ Editing/DraftRestoreBoundaryStore.cs  # 新
│  ├─ Editing/DraftRecoveryItem.cs         # 增加 IsPreRestore
│  ├─ Editing/DraftRecoveryService.cs      # 读取边界并标记
│  ├─ Editing/NoteEditorSession.cs         # Invalidate/迟到保存拒绝
│  ├─ Hosting/DesktopBackupServices.cs     # 新
│  ├─ Hosting/DesktopApplicationHost.cs    # 备份服务/恢复刷新接线
│  ├─ Paths/DesktopPaths.cs                # backup-status/draft-boundary 路径
│  └─ Startup/StartupCoordinator.cs        # 恢复后刷新由宿主调用（不改变启动语义）
├─ src/QuickNotes.Desktop.Wpf/
│  ├─ BackupWindow.xaml/.cs                # 新：备份中心+恢复
│  ├─ BackupSetupWindow.xaml/.cs           # 新：首启目录
│  ├─ RestoreProgressWindow.xaml/.cs       # 新：真实阶段/取消
│  ├─ WorkspaceWindow.xaml/.cs             # 增加入口、冻结/恢复失败重开
│  └─ App.xaml.cs                          # 恢复完成路由/宿主事件
├─ tests/
│  ├─ QuickNotes.UnitTests/                # 状态/校验/进度/会话失效
│  ├─ QuickNotes.IntegrationTests/         # 真实备份/恢复/调度/边界
│  └─ QuickNotes.Desktop.Tests/            # 宿主/备份服务/E2E
└─ docs/
   ├─ decisions/2026-10-28-c4-backup-scheduling-and-status.md
   ├─ decisions/2026-10-28-c4-restore-interaction-and-session-invalidation.md
   ├─ agent/C4-测试报告.md
   ├─ agent/C4-完成总结.md
   ├─ agent/C4-人工检查清单.md
   └─ agent/C4-错误UX清单.md
```

---

### Task 1: C4 契约、运行状态、草稿恢复边界与 ADR

**Files:**
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Application/BackupRuntimeStatus.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Application/IBackupRuntimeStatusStore.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Application/BackupOperationResult.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Application/BackupVerificationResult.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Application/RestoreProgress.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Application/RestoreProgressStage.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Application/IRestoreCoordinator.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Editing/DraftRestoreBoundary.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Editing/DraftRestoreBoundaryStore.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Editing/DraftRecoveryItem.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Editing/DraftRecoveryService.cs`
- Create: `QuickNotes_AgentHandoff/docs/decisions/2026-10-28-c4-backup-scheduling-and-status.md`
- Create: `QuickNotes_AgentHandoff/docs/decisions/2026-10-28-c4-restore-interaction-and-session-invalidation.md`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.UnitTests/BackupRuntimeStatusTests.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/DraftRestoreBoundaryStoreTests.cs`

**Interfaces:**
- `BackupRuntimeStatus`: `FormatVersion=1`、`BackupDirectory`、`LastCheckAtUtc`、`LastSuccessAtUtc`、`LastFailureAtUtc`、`LastErrorCode`、`LastWarning`、`LastSuccessfulPackagePath`、`AutoBackupConfigured`。
- `IBackupRuntimeStatusStore`: `Task<BackupRuntimeStatus?> ReadAsync(ct)`、`Task WriteAsync(BackupRuntimeStatus, ct)`（原子写；损坏/未知版本返回 null，不覆盖）。
- `BackupOperationStatus`: `Succeeded, NotConfigured, Busy, InvalidDirectory, Failed, NotFound, InvalidTarget`。
- `BackupOperationResult(Status, BackupResult? Backup, string? PackagePath, string? ErrorCode, string? Message)`。
- `BackupVerificationResult(Status, BackupManifest? Manifest, BackupError? Error)`；`BackupVerificationStatus`: `Valid, Invalid, Missing`。
- `RestoreProgressStage`: `Preflight, ProtectionSnapshot, Staging, Migrating, Switching, Verifying, Completed, Failed, RolledBack`；`RestoreProgress(Stage, Message, IsCancelable)`。
- `IRestoreCoordinator` 增加重载：
  `Task<RestoreResult> RestoreAsync(RestoreRequest request, IProgress<RestoreProgress>? progress = null, CancellationToken ct = default)`；保留旧调用兼容（可选参数）。
- `DraftRestoreBoundary(Guid RestoreId, DateTimeOffset RestoredAtUtc, IReadOnlyList<Guid> DraftIds)`；`DraftRecoveryItem` 增加 `bool IsPreRestore`。

- [ ] **Step 1: Write failing tests**

```csharp
[Fact]
public void BackupRuntimeStatus_RoundTripsUnknownAsNull()
{
    var store = new InMemoryRuntimeStatusStore();
    Assert.Null(store.ReadAsync().GetAwaiter().GetResult());
    var status = new BackupRuntimeStatus
    {
        FormatVersion = BackupRuntimeStatus.CurrentFormatVersion,
        BackupDirectory = @"C:\backups",
        LastCheckAtUtc = DateTimeOffset.UtcNow,
        AutoBackupConfigured = true
    };
    store.WriteAsync(status).GetAwaiter().GetResult();
    Assert.Equal(status, store.ReadAsync().GetAwaiter().GetResult());
}
```

```csharp
[Fact]
public async Task DraftRecovery_MarksOnlyIdsInBoundary()
{
    // 写两个草稿 A/B；边界只含 A；ListAsync 后 A.IsPreRestore=true、B=false。
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.UnitTests/QuickNotes.UnitTests.csproj --filter FullyQualifiedName~BackupRuntimeStatusTests`
Run: `dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/QuickNotes.Desktop.Tests.csproj --filter FullyQualifiedName~DraftRestoreBoundaryStoreTests`
Expected: 编译失败（类型/属性不存在）。

- [ ] **Step 3: Implement contracts**

```csharp
public sealed record BackupRuntimeStatus
{
    public const int CurrentFormatVersion = 1;
    public required int FormatVersion { get; init; }
    public string? BackupDirectory { get; init; }
    public DateTimeOffset? LastCheckAtUtc { get; init; }
    public DateTimeOffset? LastSuccessAtUtc { get; init; }
    public DateTimeOffset? LastFailureAtUtc { get; init; }
    public string? LastErrorCode { get; init; }
    public string? LastWarning { get; init; }
    public string? LastSuccessfulPackagePath { get; init; }
    public bool AutoBackupConfigured { get; init; }
}
```

`RestoreProgress`/`Stage` 只做数据契约；`RestoreCoordinator` 的 progress 重载在 Task 3 实现，Task 1 先改接口并为旧调用提供默认实现（`progress = null`）。

`DraftRestoreBoundaryStore`：
- 路径 `DesktopPaths.DraftRestoreBoundaryPath`（数据根 `draft-restore-boundary.json`，不在 drafts 目录内，避免被草稿枚举）。
- `ReadAsync` 缺失/损坏/未知版本返回 null；`WriteAsync` 原子写。
- `DraftRecoveryService.ListAsync` 读取边界，`DraftIds.Contains(item.Draft.DraftId)` 设置 `IsPreRestore`；不删除、不自动提交。

- [ ] **Step 4: Write ADRs**

- 调度 ADR：15 分钟候选、启动立即检查、唤醒补检查、非重入、失败重试、目录切换不搬移、运行状态文件位置。
- 恢复交互 ADR：冻结/保存/草稿捕获顺序、先 Invalidate 再恢复、失败后重开新会话、恢复进度/取消边界、草稿边界标记、设备设置不恢复。
- 明确 OS 通知与托盘留 C5/C7，C4 只做应用内状态。

- [ ] **Step 5: Run tests and commit**

```bash
dotnet build QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln
dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.UnitTests/QuickNotes.UnitTests.csproj --filter FullyQualifiedName~BackupRuntimeStatusTests
dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/QuickNotes.Desktop.Tests.csproj --filter FullyQualifiedName~DraftRestoreBoundaryStoreTests
git add QuickNotes_AgentHandoff/src/QuickNotes.Application QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Editing QuickNotes_AgentHandoff/docs/decisions/2026-10-28-c4-*.md QuickNotes_AgentHandoff/tests/QuickNotes.UnitTests/BackupRuntimeStatusTests.cs QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/DraftRestoreBoundaryStoreTests.cs
git commit -m "feat: 添加 C4 备份状态、恢复进度与草稿边界契约"
```

---

### Task 2: BackupHostService、目录配置、调度与运行状态

**Files:**
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Backup/BackupHostOptions.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Backup/BackupDirectoryValidator.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Backup/FileBackupRuntimeStatusStore.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Backup/BackupHostService.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Hosting/DesktopBackupServices.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Paths/DesktopPaths.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Hosting/DesktopApplicationHost.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/BackupDirectoryValidatorTests.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/FileBackupRuntimeStatusStoreTests.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/BackupHostServiceTests.cs`

**Interfaces:**
- `BackupHostOptions { TimeSpan CheckInterval = 15m; }`（测试可注入短周期）。
- `BackupDirectoryValidationResult`：`Valid`、`ErrorCode`、`Message`。
- `BackupHostService`：
  - `Task<BackupRuntimeStatus> GetStatusAsync(ct)`
  - `Task<BackupOperationResult> SetBackupDirectoryAsync(string? directory, ct)`
  - `Task<BackupOperationResult> CreateManualBackupAsync(ct)`
  - `Task<DailyBackupResult> RunScheduledCheckAsync(ct)`
  - `Task<BackupCatalog> ListBackupsAsync(ct)`
  - `Task<BackupVerificationResult> VerifyBackupAsync(string packagePath, ct)`
  - `Task<BackupOperationResult> DeleteManualBackupAsync(string packagePath, ct)`
  - `Task StartAsync(ct)` / `Task StopAsync(ct)` / `Task TriggerWakeCheckAsync(ct)`
  - `Task<IAsyncDisposable> SuspendForRestoreAsync(ct)`：暂停调度并等待/阻止手动备份。
- `DesktopBackupServices(BackupHostService Host, IRestoreCoordinator Restore, IBackupCatalog Catalog, BackupPackageVerifier Verifier, IDeviceSettingsStore DeviceSettings)`。
- `DesktopPaths.BackupRuntimeStatusPath`、`DesktopPaths.DraftRestoreBoundaryPath`。

- [ ] **Step 1: Write failing tests**

```csharp
[Fact]
public async Task Start_ImmediatelyChecks_WhenDirectoryConfigured()
{
    var daily = new FakeDailyBackupService();
    var service = CreateHost(daily, configuredDirectory: temp.Backups);
    await service.StartAsync(TestContext.Current.CancellationToken);
    await daily.WaitForCallAsync();
    Assert.Equal(1, daily.Calls);
}

[Fact]
public async Task NoConfiguredDirectory_StatusIsNotConfigured_AndNoBackup()
{
    var daily = new FakeDailyBackupService();
    var service = CreateHost(daily, configuredDirectory: null);
    var status = await service.RunScheduledCheckAsync(ct);
    Assert.Equal(0, daily.Calls);
    Assert.False(status.AutoBackupConfigured);
}

[Fact]
public async Task ManualAndScheduledChecks_AreNotReentrant()
{
    // 门控 DailyBackupService：检查进行中再次 RunScheduledCheck/CreateManual 返回 Busy。
}

[Fact]
public async Task DirectoryValidation_RejectsDataRootAndUnwritable()
{
    // 同数据根 / 不存在 / 只读目录 -> 对应错误码；有效目录 -> Valid。
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/QuickNotes.Desktop.Tests.csproj --filter "FullyQualifiedName~BackupHostServiceTests|FullyQualifiedName~BackupDirectoryValidatorTests|FullyQualifiedName~FileBackupRuntimeStatusStoreTests"`

Expected: 编译失败（类型不存在）。

- [ ] **Step 3: Implement directory/status/host**

`BackupDirectoryValidator.ValidateAsync(directory, dataRoot, ct)`：
- 空 → `directory_empty`；非绝对 → `directory_not_absolute`；`Path.GetFullPath` 与 dataRoot 相同/包含关系 → `directory_is_data_root`；不存在 → `directory_missing`；不可写 → `directory_not_writable`（在目录创建 `.quicknotes-write-test-*` 后删除）；成功 → Valid。
- 不自动创建目录；UI 引导选择已有目录或由用户在资源管理器创建。

`FileBackupRuntimeStatusStore`：数据根 `backup-status.json`，原子写；读取缺失/损坏/未知格式返回 null；实现 `IBackupRuntimeStatusStore`。

`BackupHostService` 构造依赖：
```csharp
public BackupHostService(
    IDeviceSettingsStore deviceSettings,
    IBackupRuntimeStatusStore statusStore,
    IDailyBackupService dailyBackup,
    IBackupCoordinator backupCoordinator,
    IBackupCatalog catalog,
    BackupPackageVerifier verifier,
    BackupHostOptions options,
    TimeProvider timeProvider,
    DiagnosticLog log)
```
- `_operationGate = new SemaphoreSlim(1,1)`；`_loopCts`；`_started`。
- `RunScheduledCheckAsync`：`WaitAsync(0)` 失败 → 返回 Busy；读取设备设置；无目录 → 写 NotConfigured 状态；有目录 → `daily.RunCheckAsync(dir)`；映射 `BackedUp/AlreadyBackedUpToday/NoChanges` 为成功或警告，`Failed` 为失败；状态写回。
- `StartAsync`：立即 `RunScheduledCheckAsync`，然后 `PeriodicTimer(options.CheckInterval)` 循环；`StopAsync` 取消并等待；循环异常记录日志并继续。
- `CreateManualBackupAsync`：同一 gate，目录未配置返回 NotConfigured；调用 `backupCoordinator.CreateAsync(Manual)`；成功后刷新状态 `LastSuccessAtUtc/PackagePath`；失败写 LastFailure/ErrorCode。
- `SetBackupDirectoryAsync`：验证 → 读/写 DeviceSettings（保留其他字段）→ 状态 `AutoBackupConfigured`；不移动/删除旧目录；若服务运行，触发一次检查。
- `TriggerWakeCheckAsync`：同 `RunScheduledCheckAsync`，不清空状态。
- `SuspendForRestoreAsync`：设置 `_restoreSuspended`，等待 `_operationGate` 获取并持有直到 lease 释放；调度/手动在此期间返回 Busy 或等待；恢复完成释放后重新允许。lease 为 `IAsyncDisposable`。
- 手动/调度失败均记录 `DiagnosticLog`，不含正文/路径以外的敏感内容（备份目录允许记录文件名，不记录便签正文）。

- [ ] **Step 4: Host wiring**

`DesktopApplicationHost`：
- 在 `StartAsync` 创建 `FileDeviceSettingsStore(_paths.DeviceSettingsPath)`、`FileBackupRuntimeStatusStore(_paths.BackupRuntimeStatusPath)`、`FileBackupCatalog`、`BackupPackageVerifier`、`SqliteChangeDetector`、`SqliteSnapshotProvider`、`ZipBackupPackageStore`、`BackupCoordinator`、`DailyBackupService`、`BackupHostService`。
- `DesktopBackupServices` 仅当 `CanEnterNormalUse` 时创建；暴露 `BackupServices`。
- `StartAsync` 后 `await BackupHost.StartAsync()`；`DisposeAsync` 先 `StopAsync`。
- `RestoreApplied` 后重建备份服务（Task 3）。
- 注意：`DailyBackupService` 依赖 `IBackupRetentionPolicy/Store/StateStore`；复用现有类型；不新建备份算法。
- 目录配置来自 `DeviceSettings.BackupDirectory`；首次运行 null → NotConfigured。
- 第一实例/启动安全语义不变；备份服务只在 Ready 后启动。

- [ ] **Step 5: Run tests and commit**

```bash
dotnet build QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln
dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/QuickNotes.Desktop.Tests.csproj --filter "FullyQualifiedName~BackupHostServiceTests|FullyQualifiedName~BackupDirectoryValidatorTests|FullyQualifiedName~FileBackupRuntimeStatusStoreTests"
dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/QuickNotes.IntegrationTests.csproj --filter "FullyQualifiedName~DailyBackupServiceIntegrationTests|FullyQualifiedName~BackupCoordinatorIntegrationTests"
git add QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Backup QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Hosting QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Paths QuickNotes_AgentHandoff/tests
git commit -m "feat: 实现备份宿主服务、目录配置与 15 分钟调度"
```

---

### Task 3: 恢复编排、进度、编辑冻结、会话失效与草稿边界

**Files:**
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Application/RestoreCoordinator.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Application/IRestoreCoordinator.cs`（Task 1 已加重载）
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Editing/NoteEditorSession.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Editing/EditorWorkspaceService.cs`（如需重开）
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Editing/DraftRecoveryService.cs`（读取边界）
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Hosting/DesktopApplicationHost.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/WorkspaceWindow.xaml.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/App.xaml.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.UnitTests/RestoreCoordinatorTests.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/EditorSessionInvalidationTests.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/RestoreHostIntegrationTests.cs`

**Interfaces:**
- `NoteEditorSession.Invalidate()` / `bool IsInvalidated`：取消去抖/lifetime，后续 Change/Save/Flush/Retry/Reload/SaveAsNew 拒绝；已在途保存完成后不得改变新库。
- `RestoreCoordinator` 在 Preflight/ProtectionSnapshot/Staging/Migrating/Switching/Verifying/Completed/Failed/RolledBack 阶段报告 `RestoreProgress`；切换后 `IsCancelable=false`。
- `DesktopApplicationHost.RestoreApplied` 事件与 `RefreshAfterRestoreAsync(ct)`：停止备份/旧服务，重新启动 StartupCoordinator，刷新 Assessment/EditingServices/BackupServices，触发事件。
- `WorkspaceWindow.PrepareForRestoreAsync()`：
  1. 冻结编辑控件；
  2. `FlushDraftAsync`；
  3. 有未保存内容则 `SaveNowAsync`；
  4. 失败/冲突/未知/仍有 pending → 解冻并返回 false；
  5. `session.Invalidate()`，返回 true。
- `DraftRestoreBoundaryStore` 在恢复成功后写入现有 draftId 集合；`DraftRecoveryService` 标记 `IsPreRestore`。

- [ ] **Step 1: Write failing tests**

```csharp
[Fact]
public async Task Restore_Progress_ReportsStagesAndDisallowsCancelAfterSwitch()
{
    // fake coordinator/store; 收集 progress；断言 Switching 之后 IsCancelable=false；
    // 断言 Completed/Failed 与实际结果一致。
}

[Fact]
public async Task Invalidate_RejectsLateSave_AndDoesNotWriteAfterRestore()
{
    // 使用可控 coordinator，在途 save 完成前 Invalidate；
    // 释放后调用 SaveNow/ChangeText 抛 ObjectDisposed/InvalidOperation；
    // fake repository 没有新写入。
}

[Fact]
public async Task RestoreHost_FreezeFailure_DoesNotStartRestore()
{
    // 当前会话草稿失败/保存失败 -> PrepareForRestoreAsync false；
    // RestoreCoordinator 未被调用。
}

[Fact]
public async Task RestoreSuccess_MarksDraftsAsPreRestore_AndRefreshesServices()
{
    // 恢复前有草稿；成功后 boundary 包含草稿；
    // DraftRecovery.List 标记 IsPreRestore；host Refresh 被调用。
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.UnitTests/QuickNotes.UnitTests.csproj --filter FullyQualifiedName~RestoreCoordinatorTests`
Run: `dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/QuickNotes.Desktop.Tests.csproj --filter "FullyQualifiedName~EditorSessionInvalidationTests|FullyQualifiedName~RestoreHostIntegrationTests"`

- [ ] **Step 3: Implement progress and invalidation**

- `RestoreCoordinator` 在现有阶段边界 `progress?.Report(new RestoreProgress(...))`；切换前 `IsCancelable=true`，`switchStarted=true` 后 false；取消在 switch 前清理 staging；发布成功后不再报告 Failed。
- `NoteEditorSession.Invalidate` 在锁内设置 `_invalidated=true`，取消 `_lifetime`/去抖；`ThrowIfDisposed` 改为同时检查 invalidated；`DisposeAsync` 可调用 Invalidate。
- `WorkspaceWindow` 新增 `_restoreFrozen`、`PrepareForRestoreAsync`、`UnfreezeAfterRestoreFailureAsync`；冻结时 `TitleBox/BodyBox.IsReadOnly=true`、按钮禁用；失败后重新 `OpenAsync(currentNoteId)` 并绑定新 session（旧 session 已失效），恢复编辑。
- `DesktopApplicationHost` 在恢复成功后：
  - `await BackupHost.StopAsync()`；
  - `Assessment = await StartupCoordinator.StartAsync()`；
  - `UpdateWriteGate()`；`RefreshEditingServices()`；重建 `BackupHostService`；
  - 写入 `DraftRestoreBoundary`（`Drafts.ListAsync` 得到 ID，写 boundary）；
  - `RestoreApplied?.Invoke()`。
- `App` 监听 `RestoreApplied`：关闭旧 `WorkspaceWindow`，创建新 `WorkspaceWindow`（或 `StartupWindow` 若非 Ready），设置 `MainWindow`。
- `WorkspaceWindow` 在恢复成功/失败后处理窗口生命周期；失败时解冻并重开会话；阻断状态显示 C1 诊断入口，不继续普通写入。

- [ ] **Step 4: Backup scheduler pause**

`BackupWindow` 在 restore 执行期间使用 `await using var lease = await BackupHost.SuspendForRestoreAsync(ct)`；恢复成功后 `RefreshAfterRestoreAsync` 重建 host；失败后恢复调度。确保恢复期间手动备份返回 Busy。

- [ ] **Step 5: Run tests and commit**

```bash
dotnet build QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln
dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.UnitTests/QuickNotes.UnitTests.csproj --filter FullyQualifiedName~RestoreCoordinatorTests
dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/QuickNotes.Desktop.Tests.csproj --filter "FullyQualifiedName~EditorSessionInvalidationTests|FullyQualifiedName~RestoreHostIntegrationTests|FullyQualifiedName~DesktopApplicationHostTests"
git add QuickNotes_AgentHandoff/src/QuickNotes.Application/RestoreCoordinator.cs QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/WorkspaceWindow.xaml.cs QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/App.xaml.cs QuickNotes_AgentHandoff/tests
git commit -m "feat: 实现恢复进度、编辑冻结、会话失效与草稿边界"
```

---

### Task 4: WPF 备份中心、首启目录与恢复 UI

**Files:**
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/BackupWindow.xaml`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/BackupWindow.xaml.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/BackupSetupWindow.xaml`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/BackupSetupWindow.xaml.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/RestoreProgressWindow.xaml`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/RestoreProgressWindow.xaml.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/WorkspaceWindow.xaml`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/WorkspaceWindow.xaml.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/App.xaml.cs`
- Create: `QuickNotes_AgentHandoff/docs/agent/C4-人工检查清单.md`
- Create: `QuickNotes_AgentHandoff/docs/agent/C4-错误UX清单.md`

**Interfaces:**
- Consumes `DesktopBackupServices`、`BackupHostService`、`RestoreCoordinator`、`WorkspaceWindow.PrepareForRestoreAsync`。
- `BackupWindow` 仅负责 UI：目录选择/状态/手动备份/列表/校验/删除/恢复确认；所有业务调用宿主服务。
- `BackupSetupWindow`：首启目录选择；选择 → `SetBackupDirectoryAsync`；稍后 → 显示自动备份未启用。
- `RestoreProgressWindow`：阶段文本、可取消状态、取消按钮；切换后取消禁用；结果/回退/阻断提示。

- [ ] **Step 1: Implement `BackupWindow` layout**

```text
顶部：备份目录路径 + [选择目录] [验证]
状态：最近成功 / 最近失败 / 警告 / 自动备份状态
操作：[立即手动备份] [刷新列表] [重试检查]
列表：时间/类型/格式/schema/大小/文件/状态（有效/问题）
      选中项：[校验] [删除手动备份] [从此备份恢复]
底部：提示“手动备份不参与自动清理；日/周备份由留存规则管理”
```

- 首启：`WorkspaceWindow.Loaded` 后若 `BackupHost.GetStatusAsync().AutoBackupConfigured == false`，打开 `BackupSetupWindow`（可跳过）；不阻塞工作区。
- 列表：`BackupHost.ListBackupsAsync` 返回 valid/issues；问题包显示不可恢复/不可删除；校验按钮调用 `VerifyBackupAsync`。
- 手动备份：调用 `CreateManualBackupAsync`；显示成功/失败；成功后刷新列表/状态。
- 删除：仅 `BackupType.Manual`；二次确认目标路径；调用 `DeleteManualBackupAsync`；失败显示错误。
- 恢复：选中 valid 包 → `RestoreCoordinator.PreflightAsync` → 展示确认详情（时间/来源文件/类型/format/schema/备份计数/当前计数/是否迁移/整库替换/设备设置策略）；用户确认 → `WorkspaceWindow.PrepareForRestoreAsync` → `BackupHost.SuspendForRestoreAsync` → `RestoreProgressWindow` + `RestoreAsync(progress)`；取消仅切换前；成功后 `_host.RefreshAfterRestoreAsync` + 关闭；失败/回退/阻断显示真实结果。
- 恢复确认缺信息显示“未知”，不根据文件名编造来源。
- 所有按钮 async；恢复/备份期间禁用冲突按钮；UI 不阻塞。

- [ ] **Step 2: Restore progress/cancel UI**

- `RestoreProgressWindow` 订阅 `IProgress<RestoreProgress>`（在 UI 线程构造 `Progress<T>`）。
- 阶段文本：预检/保护快照/暂存/迁移/切换/校验/完成/失败/已回退。
- 取消按钮仅 `IsCancelable` 时可用；切换后显示“正在切换，不可取消”；取消后等待真实结果，不显示成功。
- 结果展示 `RestoreStatus`、`RolledBack`、`WritesBlocked`、`Error.Code/Message`、保护快照路径；阻断时提供“打开数据目录/重启进入诊断”提示。

- [ ] **Step 3: Workspace/App integration**

- `WorkspaceWindow` 工具栏增加“备份与恢复”按钮，打开 `BackupWindow`（Owner=this）。
- `BackupWindow` 在恢复前调用 `Owner.PrepareForRestoreAsync`；失败不进入恢复。
- 恢复成功后 `App` 重建 `WorkspaceWindow`（或 StartupWindow 若 Assessment 不 Ready）；恢复失败后 WorkspaceWindow 解冻并重开当前便签。
- 首启 `BackupSetupWindow` 通过 `WorkspaceWindow` 在 Loaded 后非模态/模态打开；跳过不写目录。
- 恢复/退出/关闭期间无未处理异常；按钮禁用状态由实际结果驱动。

- [ ] **Step 4: Manual checklist / error UX**

`C4-人工检查清单.md` 覆盖：首启选择/跳过、目录不可写、手动备份成功、日备份/无变化、7+4 可见、列表校验、手动删除确认、恢复确认/迁移/取消/回退/阻断、恢复后窗口刷新、旧会话不写回、草稿标记、重启后恢复检查。未运行项标注。

`C4-错误UX清单.md`：目录未配置/不可用、包损坏、空间不足、草稿保存失败、恢复预检失败、取消、切换失败回退成功、回退失败阻断、备份失败/重试、通知限制。

- [ ] **Step 5: Build and WPF smoke**

```bash
dotnet build QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln
dotnet run --project QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/QuickNotes.Desktop.Wpf.csproj -- --data-root "$env:TEMP\QuickNotes-C4-Demo"
```

记录：首启目录提示、手动备份、列表、恢复确认/进度、错误提示；无法自动化的视觉项写入人工清单。

- [ ] **Step 6: Commit**

```bash
git add QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf QuickNotes_AgentHandoff/docs/agent/C4-人工检查清单.md QuickNotes_AgentHandoff/docs/agent/C4-错误UX清单.md
git commit -m "feat: 实现 C4 备份中心与恢复交互界面"
```

---

### Task 5: 端到端测试、文档、双轴审查与交付

**Files:**
- Create: `QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/BackupRestoreHostIntegrationTests.cs`
- Create: `QuickNotes_AgentHandoff/docs/agent/C4-测试报告.md`
- Create: `QuickNotes_AgentHandoff/docs/agent/C4-完成总结.md`
- Modify: `QuickNotes_AgentHandoff/docs/decisions/README.md`
- Modify: `QuickNotes_AgentHandoff/docs/agent/C阶段-PRD覆盖表.md`
- Modify: `README.md`、`QuickNotes_AgentHandoff/README.md`

- [ ] **Step 1: E2E tests**

```text
FirstRun_NoBackupDirectory_AutoBackupNotConfigured
SetDirectory_PersistsAndStartsScheduling
ManualBackup_ListVerifyDeleteManualOnly
Scheduler_ImmediateAndPeriodic_NonReentrant
BackupFailure_RecordedAndRetriedNextCycle
Restore_Success_RefreshesServices_AndInvalidatesOldSession
Restore_FailureRolledBack_UnfreezesEditor
Restore_RollbackFailure_BlocksWritesAcrossRestart
Restore_PendingDraft_MarkedPreRestore_NotAutoSubmitted
Restore_DeviceSettingsNotOverwritten
```
使用临时数据根、真实 SQLite/备份包/文件草稿；不访问真实用户数据。

- [ ] **Step 2: Write reports/summary**

C4 测试报告：构建/测试数量、WPF 冒烟、通过/失败/未运行；调度、目录、手动备份、列表/校验/删除、恢复进度/取消/回退/阻断、旧会话失效、草稿边界。C4 完成总结按 C 文档 6.3 八节。

- [ ] **Step 3: Update indexes/README/PRD**

- ADR 索引加入两份 C4 ADR。
- README：备份中心/首启目录/调度/恢复交互、运行说明、测试数。
- PRD 覆盖表：11.1/11.2/11.3/11.4/11.6/11.7、13.2、18.3 状态；未实现项（OS 通知/托盘/导出/安装器）不得标通过。

- [ ] **Step 4: Full verification and dual-axis review**

```bash
dotnet build QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln
dotnet test QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln --no-build --no-restore
dotnet list QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln package --vulnerable --include-transitive
```

- 标准轴：UI 不直接 SQL/包逻辑；异步/取消/资源释放；调度非重入；恢复门/会话失效；错误 UX 不误报成功；日志不含正文。
- 规格轴：逐条 C4 工作/验收/交付；7+4、手动排除、同日无变化、目录不可用、重试、恢复确认/取消/回退/阻断、旧会话不写回、草稿边界、设备设置不覆盖；确认 C5+ 未实现。
- 修复发现后重跑相关测试；未运行项如实记录。

- [ ] **Step 5: Commit**

```bash
git add QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/BackupRestoreHostIntegrationTests.cs QuickNotes_AgentHandoff/docs README.md QuickNotes_AgentHandoff/README.md
git commit -m "docs: 完成 C4 测试报告与交付总结"
```

---

## Self-Review

**Spec coverage**
- 备份目录选择/可用性/最近成功失败/重试 → Task 2 + Task 4。
- 手动备份/列表/校验/手动删除 → Task 2 + Task 4。
- 启动立即检查/15 分钟/唤醒/非重入/失败重试 → Task 2。
- 恢复确认字段/整库替换/设备策略 → Task 3 + Task 4。
- 冻结编辑/在途保存/草稿/写入门 → Task 3 + Task 4。
- B5 预检/保护/暂存/切换/回退 + C1 缺库 + C3 迁移 → 复用现有 `RestoreCoordinator`/`MissingDatabaseRebuilder`，Task 3 进度接线。
- 旧会话失效/刷新 → Task 3。
- 恢复前草稿标记/不自动提交 → Task 1 + Task 3。
- 设备设置不覆盖 → C3 已有 + Task 5 测试。
- 调度基线恢复后失效 → C3 已实现身份轮换；Task 2 状态/刷新，Task 5 测试。

**Placeholder scan**
- 无 TBD/TODO；候选值集中在第 0 节。
- 所有新增类型/方法名在后续任务中一致：`BackupRuntimeStatus`、`BackupHostService`、`BackupOperationResult`、`RestoreProgress`、`DraftRestoreBoundary`、`PrepareForRestoreAsync`、`RefreshAfterRestoreAsync`。

**Type consistency**
- `BackupHostService` 返回 `BackupCatalog`/`BackupResult`/`DailyBackupResult`/`BackupVerificationResult` 均为现有或 Task 1 定义类型。
- `RestoreCoordinator` 重载保留旧调用；`IProgress<RestoreProgress>` 为 .NET 标准类型。
- 设备设置仍用 C3 `DeviceSettings`；不新增 schema。

## 执行前需要确认

1. 是否接受 15 分钟候选周期与启动/唤醒检查。
2. 是否接受运行状态文件 `backup-status.json` 位于数据根、不进入备份。
3. 是否接受草稿恢复边界使用数据根 sidecar（不升级草稿格式）。
4. 是否接受恢复前 Invalidate 旧会话、失败后重开当前便签；以及 OS 通知留 C5/C7。
5. 是否接受 C4 只删除手动备份、日/周/无效包不提供应用内删除。
6. 是否接受首启目录提示可跳过、跳过时自动备份保持未配置。
