# C0+C1 桌面宿主与启动安全 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 完成 C0 环境/PRD/集成基线，并交付 C1：可启动的 WPF 宿主、单实例协调、正式数据目录、启动恢复状态机、跨重启写入阻断、保守的恢复产物识别，以及当前库缺失时从已验证备份安全重建的最小诊断界面。

**Architecture:** 新增 `QuickNotes.Desktop.Core`（net10.0，无 WPF，承载路径/状态/单实例/启动检查/重建）与 `QuickNotes.Desktop.Wpf`（net10.0-windows，薄 UI/组合根）。桌面层只调用既有 Application/Infrastructure 服务，不直接执行 SQL；启动检查在打开数据资源前先取得单实例所有权。C1 不接入编辑器、schema v2、完整设置、备份管理 UI 或周期备份；这些属于 C2–C7。

**Tech Stack:** .NET 10、C#、WPF、SQLite（Microsoft.Data.Sqlite 10.0.0）、xUnit 2.9.3、Windows Named Mutex/Named Pipe、System.Text.Json。

**Spec:** `docs/agent/Agent对接文档_C阶段.md`（C0/C1）；`docs/product/PRD_v1.0.md` 8.1–8.5、10.1–10.3、11.1、11.5、11.7、11.8、18.1；`AGENTS.md`；B5/B6/B7 ADR 与 [B7 已知限制](../../agent/B7-已知限制与后续建议.md)。

## Global Constraints

- 正式目标平台 Windows 11 x64；不承诺 Windows 10。
- PRD v1.0 是产品行为权威；不修改 PRD 或已确认规则。
- Domain/Application 不依赖 WPF；WPF 不直接执行 SQL、不绕过 `INoteSaveCoordinator`/`IRestoreCoordinator`/`IDatabaseWriteGate`。
- 只有数据库提交成功或提交未知后回读一致，才显示“已保存”；草稿落盘不等于正式保存。
- 恢复前完整校验；切换前保护当前数据；回退失败停止写入并保留证据；重启不得自动解除阻断。
- 单实例所有者取得前不打开数据库、草稿存储或后台任务。
- 首次运行、已初始化但库缺失、库损坏、状态损坏、WAL sidecar、写入阻断必须有不同且安全的路径；不能自动创建空库覆盖未知数据。
- 只使用合成数据与临时目录；不访问真实用户便签、草稿或备份。
- 未运行的人工/真实环境验证必须标记“未运行”，不得写成通过。
- C1 不启用 WAL、多实例写入、Rust FFI；不实现 C2+ 功能。

---

### Task 1: C0 环境与基线报告

**Files:**
- Create: `docs/agent/C0-环境与基线报告.md`
- Read: `AGENTS.md`、`agent.md`、`docs/product/PRD_v1.0.md`、`docs/agent/Agent对接文档_B阶段.md`、`docs/agent/Agent对接文档_C阶段.md`、B7 总结/测试报告/限制、B2–B6 ADR、A 阶段源码。

**Interfaces:**
- Produces: 环境/历史证据/本次实测的分离记录；后续 C0/C1 文档引用它。

- [ ] **Step 1: 记录仓库状态**

```bash
git branch -vv
git status --short
git log --oneline -8
git worktree list
```

记录：当前分支/提交、B7 是否已合并、C 阶段文档提交、工作区是否干净、用户未提交修改是否存在。不得把 B7 文档中的“待合并”写成当前事实。

- [ ] **Step 2: 采集 .NET/WPF 环境**

```bash
dotnet --info
dotnet --list-sdks
dotnet --list-runtimes
```

记录 OS 版本、RID、SDK、Host、WindowsDesktop runtime；没有 `global.json` 时明确记录。不得编造未采集值。

- [ ] **Step 3: 复跑 B 阶段回归与 A 原型构建**

```bash
dotnet build QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln
dotnet test QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln --no-build --no-restore
dotnet build WindowsWindowTrayPrototype/WindowsWindowTrayPrototype.csproj
```

记录各自通过/失败/警告；B7 的 245 是历史基线，必须单独记录本次实际结果。A 原型未实际启动 UI 时标记“未运行”。

- [ ] **Step 4: 检查 A/B 结合点**

汇总 A 原型：项目/目标框架、`App.xaml` 启动方式、主窗口/便签窗口、托盘 `NotifyIcon`、全局快捷键、关闭三选项、单实例缺失、数据层缺失、A-10/A-11 未测/失败项。指出 C1 只复用设计模式，不复制无数据层代码。

- [ ] **Step 5: 写 C0 报告**

按以下结构写：结论；环境；仓库/B7 状态；B 回归与 A 构建实际结果；A 阶段能力与缺口；通过/失败/未运行；已发现风险；下一步依赖。明确“历史证据”和“本次实测”分开。

- [ ] **Step 6: 提交**

```bash
git add docs/agent/C0-环境与基线报告.md
git commit -m "docs: 添加 C0 环境与基线报告"
```

### Task 2: C0 PRD 覆盖表、集成设计、ADR 与风险/性能基线

**Files:**
- Create: `docs/agent/C阶段-PRD覆盖表.md`
- Create: `docs/agent/C0-集成设计与风险.md`
- Create: `docs/decisions/2026-10-28-c1-host-and-integration.md`
- Create: `docs/decisions/2026-10-28-c1-single-instance-and-data-directory.md`
- Create: `docs/decisions/2026-10-28-c1-startup-recovery-state.md`

**Interfaces:**
- Consumes: Task 1 的基线事实。
- Produces: C 阶段工作包映射、C1 架构与状态机、风险/性能候选预算；后续任务按此实现。

- [ ] **Step 1: 写 PRD 覆盖表**

行至少覆盖 PRD 5（显示/隐藏）、6（分类标签）、7（管理中心）、8（窗口/托盘/启动/DPI）、9（快捷键）、10（自动保存/异常恢复）、11（备份/恢复/升级）、12（导出）、13（通知）、14（快捷键）、15（架构约束）、16（Windows/DPI）、17（安装升级）、18（核心验收）、19（测试矩阵）。每行列出：PRD 条款、B 已实现、C0/C1–C7 归属、待核实/延期、证据或缺口。C2–C7 只能标记计划，不能标记通过。

- [ ] **Step 2: 写集成设计**

包含：

```text
项目：QuickNotes.Desktop.Core（net10.0，无 WPF）+ QuickNotes.Desktop.Wpf（net10.0-windows，WPF）
依赖：Desktop.Wpf → Desktop.Core → Application/Infrastructure.*；Domain/Application 不引用 WPF
组合根：App.OnStartup → 单实例 → 数据路径 → 状态/日志 → 启动检查 → 最小诊断窗口
数据目录：%LOCALAPPDATA%\QuickNotes（测试/手动用 --data-root 覆盖）
  notes.db、drafts/、logs/、application-state.json；备份目录 C1 不配置；
  --backup-dir 仅用于手动扫描保护快照/临时产物；缺失库重建用文件选择器
线程：启动/恢复 async + await，UI 线程不被同步快照/保存阻塞；单实例所有权先于数据资源
测试：QuickNotes.Desktop.Tests（net10.0，合成临时目录）；WPF 只做启动冒烟/人工检查
```

同时写 A/B 复用边界：A 原型窗口/托盘/快捷键模式留 C5，不复用无数据层代码；B 服务直接复用。

- [ ] **Step 3: 写三个 C1 ADR**

- `c1-host-and-integration`：项目边界、组合根、UI 不直接 SQL、线程/生命周期、A/B 集成。
- `c1-single-instance-and-data-directory`：用户/数据目录作用域、Mutex+命名管道激活、数据目录与日志、`--data-root`、备份目录留 C4。
- `c1-startup-recovery-state`：状态机、初始化标记、持久化写入阻断、恢复产物保留策略、缺失库重建与“无当前库不伪造保护快照”。

每个 ADR 写明背景、决策、后果、备选方案、验证与后续。

- [ ] **Step 4: 写风险与性能候选预算**

风险至少：A-11 多显示器失败（C5）、首次运行/状态损坏误初始化、单实例激活失败、持久化阻断写失败、缺失库重建与 B5 分叉、WPF 线程/STA、人工 UI 验证不可自动化、B7 未运行项。

性能候选（标记“候选，待 C0 确认/实测校准”，不作为通过标准）：

```text
数据量：1,000 张历史便签；5/10/20 张可见窗口
启动到 Ready（无 UI）：p95 ≤ 2s；含窗口显示 ≤ 3s
正常保存提交：p95 ≤ 500ms，p99 ≤ 1.5s；BUSY 最坏按 B2 ADR 约 3.4s
UI 输入响应：≤ 100ms（C2 验证）
20 窗口 + 1,000 历史：工作集 ≤ 300MB（C6 验证）
备份/恢复耗时：C6 按实际数据库规模记录，不在 C1 承诺
```

记录设备条件（CPU/RAM/磁盘）或标记未知；不得在 C6 前宣称性能达标。

- [ ] **Step 5: 提交**

```bash
git add docs/agent/C阶段-PRD覆盖表.md docs/agent/C0-集成设计与风险.md docs/decisions/2026-10-28-c1-*.md
git commit -m "docs: 添加 C0 PRD 映射、集成设计与 C1 ADR"
```

### Task 3: 桌面项目与测试项目骨架

**Files:**
- Create: `src/QuickNotes.Desktop.Core/QuickNotes.Desktop.Core.csproj`
- Create: `src/QuickNotes.Desktop.Wpf/QuickNotes.Desktop.Wpf.csproj`
- Create: `src/QuickNotes.Desktop.Wpf/App.xaml`
- Create: `src/QuickNotes.Desktop.Wpf/App.xaml.cs`
- Create: `tests/QuickNotes.Desktop.Tests/QuickNotes.Desktop.Tests.csproj`
- Modify: `QuickNotes.DataPrototype.sln`

**Interfaces:**
- Produces: 可构建的空壳宿主/核心/测试项目；后续任务填充类型。

- [ ] **Step 1: 创建 Core 项目**

`net10.0`，`Nullable=enable`，`ImplicitUsings=enable`；引用 Domain、Application、Infrastructure.Sqlite、Infrastructure.Drafts、Infrastructure.Backup。不引用 WPF/WindowsForms。

- [ ] **Step 2: 创建 WPF 项目**

`net10.0-windows`、`UseWPF=true`、`OutputType=WinExe`、`EnableWindowsTargeting=true`；引用 Core；`App.xaml` 不使用 `StartupUri`；`App.xaml.cs` 暂时只显示一个最小窗口（Task 8 替换为真实启动流程）。

- [ ] **Step 3: 创建 Desktop.Tests**

`net10.0`、xUnit 2.9.3、Microsoft.NET.Test.Sdk 17.13.0；引用 Core；测试只使用临时目录。不要为测试新增生产依赖。

- [ ] **Step 4: 加入解决方案并构建**

```bash
dotnet sln QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln add QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/QuickNotes.Desktop.Core.csproj QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/QuickNotes.Desktop.Wpf.csproj QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/QuickNotes.Desktop.Tests.csproj
dotnet build QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln
dotnet test QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln --no-build --no-restore
```

Expected: 0 警告；245 + 0 新测试通过；WPF 空壳可构建。

- [ ] **Step 5: 提交**

```bash
git add QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln
git commit -m "feat: 添加 C1 桌面核心与 WPF 宿主骨架"
```

### Task 4: 数据路径、应用状态与诊断日志

**Files:**
- Create: `src/QuickNotes.Desktop.Core/Paths/DesktopPaths.cs`
- Create: `src/QuickNotes.Desktop.Core/State/ApplicationState.cs`
- Create: `src/QuickNotes.Desktop.Core/State/ApplicationStateStore.cs`
- Create: `src/QuickNotes.Desktop.Core/Diagnostics/DiagnosticLog.cs`
- Test: `tests/QuickNotes.Desktop.Tests/DesktopPathsTests.cs`
- Test: `tests/QuickNotes.Desktop.Tests/ApplicationStateStoreTests.cs`
- Test: `tests/QuickNotes.Desktop.Tests/DiagnosticLogTests.cs`

**Interfaces:**
- Produces:
  - `DesktopPaths.CreateDefault()` / `DesktopPaths.Create(dataRoot)` / `EnsureDirectories()`
  - `ApplicationState`（`FormatVersion=1`、`InstallationId`、`InitializationStatus`、`SchemaVersion`、`WritesBlocked`、`WriteBlockReason`、`WriteBlockedAtUtc`、`LastStartupUtc`、`RecoverySourceSha256`、`RecoveryProtectionSnapshotCreated`、`LastRecoveryAtUtc`）
  - `ApplicationStateStore.ReadAsync` 返回 `Found/NotFound/Invalid`；`WriteAsync` 原子写；`TryMarkWritesBlocked(reason, out error)` 同步原子写
  - `DiagnosticLog.Write(level, eventName, message, errorCode?)`

- [ ] **Step 1: 写路径与状态测试**

测试名与断言：

```text
CreateDefault_UsesLocalAppDataQuickNotes
Create_WithRoot_UsesExpectedDatabaseDraftsLogsStatePaths
EnsureDirectories_CreatesAllRequiredDirectories
Read_MissingState_ReturnsNotFound
Read_InvalidJson_ReturnsInvalid
Read_UnknownFormatVersion_ReturnsInvalid
WriteThenRead_RoundTripsAllFields
Write_ReplacesExistingStateAtomically_NoTempLeftovers
TryMarkWritesBlocked_PersistsBlockAndReason
```

- [ ] **Step 2: 实现路径/状态**

状态 JSON 使用 camelCase、字符串枚举、原子临时文件 + `File.Move(overwrite:true)` + `Flush(true)`；损坏/未知版本不得当作空状态。

- [ ] **Step 3: 写日志测试与实现**

`DiagnosticLog` 追加 `logs/quicknotes-yyyyMMdd.log`，每行：UTC 时间、级别、事件名、错误码/摘要；锁内写入；不接受便签正文参数。测试断言写入、追加、目录创建、日志不包含传入的正文样例（调用方不传正文）。

- [ ] **Step 4: 运行测试**

```bash
dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/QuickNotes.Desktop.Tests.csproj --filter "FullyQualifiedName~DesktopPathsTests|FullyQualifiedName~ApplicationStateStoreTests|FullyQualifiedName~DiagnosticLogTests"
```

Expected: 全部通过。

- [ ] **Step 5: 提交**

```bash
git add QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Paths QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/State QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Diagnostics QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests
git commit -m "feat: 添加桌面数据路径、应用状态与诊断日志"
```

### Task 5: 单实例协调与第二实例激活

**Files:**
- Create: `src/QuickNotes.Desktop.Core/SingleInstance/SingleInstanceGuard.cs`
- Create: `src/QuickNotes.Desktop.Core/SingleInstance/SingleInstanceStartResult.cs`
- Test: `tests/QuickNotes.Desktop.Tests/SingleInstanceGuardTests.cs`

**Interfaces:**
- Produces:
  - `SingleInstanceGuard(string dataRoot, DiagnosticLog log)`，`StartResult` 为 `Owner/ActivatedExisting/Unresponsive/Error`
  - `event Action? ActivationRequested`
  - `Task<SingleInstanceStartResult> StartAsync(TimeSpan activationTimeout, CancellationToken)`
  - `DisposeAsync/Dispose` 释放数据根锁文件/管道；数据根 `.quicknotes-instance.lock` 以 `FileShare.None` 持有

- [ ] **Step 1: 写失败测试**

```text
SecondInstance_WhenOwnerRunning_ActivatesOwner
SecondInstance_WhenActivationPipeMissing_ReturnsUnresponsive
Dispose_ReleasesOwnership_NextInstanceBecomesOwner
TwoDifferentDataRoots_DoNotConflict
```

测试用唯一临时数据根与短超时；先启动 owner，再启动 second；用 `TaskCompletionSource` 等激活事件；finally 释放两个 guard。测试不得访问真实用户目录。

- [ ] **Step 2: 实现数据根锁文件 + Named Pipe**

```text
锁文件：<dataRoot>\.quicknotes-instance.lock（FileMode.OpenOrCreate, FileShare.None）
管道：QuickNotes.Activate.<hash>（用户身份 + 数据根 SHA-256 前 16 位）
StartAsync：
1. 创建数据根目录；打开锁文件排他句柄；失败（IOException）视为已有实例
2. 已有实例：连接管道发送 ACTIVATE，成功 -> ActivatedExisting；超时/失败 -> Unresponsive
3. 成功：持有锁句柄，启动管道服务器，返回 Owner
4. 任何失败：不打开 DB/草稿/日志写入
选择锁文件而非 Mutex：Mutex 线程亲和/可递归，异步启动/退出与测试不可靠；OS 释放锁句柄可处理崩溃遗留。
```

- [ ] **Step 3: 运行测试**

```bash
dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/QuickNotes.Desktop.Tests.csproj --filter FullyQualifiedName~SingleInstanceGuardTests
```

Expected: 全部通过；无遗留进程/管道。

- [ ] **Step 4: 提交**

```bash
git add QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/SingleInstance QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/SingleInstanceGuardTests.cs
git commit -m "feat: 实现单实例协调与第二实例激活"
```

### Task 6: 启动状态机与恢复产物扫描

**Files:**
- Create: `src/QuickNotes.Desktop.Core/Startup/StartupMode.cs`
- Create: `src/QuickNotes.Desktop.Core/Startup/StartupReason.cs`
- Create: `src/QuickNotes.Desktop.Core/Startup/StartupAssessment.cs`
- Create: `src/QuickNotes.Desktop.Core/Startup/StartupArtifactScanner.cs`
- Create: `src/QuickNotes.Desktop.Core/Startup/StartupCoordinator.cs`
- Create: `src/QuickNotes.Application/RestoreJournal.cs`
- Create: `src/QuickNotes.Application/IRestoreJournalStore.cs`
- Create: `src/QuickNotes.Desktop.Core/Recovery/FileRestoreJournalStore.cs`
- Test: `tests/QuickNotes.Desktop.Tests/StartupCoordinatorTests.cs`
- Test: `tests/QuickNotes.Desktop.Tests/StartupArtifactScannerTests.cs`
- Test: `tests/QuickNotes.Desktop.Tests/RestoreJournalTests.cs`

**Interfaces:**
- Produces:
  - `StartupMode`：`FirstRunInitialized/Ready/NeedsRecovery/WriteBlocked/ReadOnlyDiagnostic`
  - `StartupReason`：`None/Initialized/DatabaseMissing/DatabaseCorrupt/SchemaUnsupported/StateMissingWithDatabase/StateInvalid/InitializationIncomplete/WalSidecarPresent/WritesBlocked/RestoreJournalIncomplete/RestoreJournalInvalid/UnverifiedArtifacts`
  - `StartupAssessment`：Mode、Reason、Message、数据库信息、状态、artifact inventory、restore journal
  - `StartupCoordinator.StartAsync`：单实例取得后执行评估/首次初始化；`RetryInitializationAsync` 仅由显式用户动作调用
  - Application 的 `RestoreJournal`：`FormatVersion=1`、`RestoreId`、`Stage`（`Staging/ProtectionCreated/Switching/Verifying/Rollback/Completed/Failed`）、`StagingDirectory`、`ProtectionSnapshotPath`、`RollbackPath`、`CurrentDatabasePath`、`StartedAtUtc`、`UpdatedAtUtc`；`IRestoreJournalStore` 读写契约；`FileRestoreJournalStore` 原子读写、损坏/未知版本返回 Invalid。C4 可把 store 注入 RestoreCoordinator 写阶段，C1 只读识别。
  - 构造函数注入 `Func<CancellationToken, Task> initializeSchema`（生产为 `SqliteSchema.InitializeAsync`，测试可注入失败）与 `IRestoreStore`；扫描器接受可选 backupDirectory

- [ ] **Step 1: 写状态机测试**

至少：

```text
FirstRun_NoStateNoDatabase_CreatesStateAndDatabase
FirstRun_InitializationFailure_DoesNotWriteReadyState
Ready_WithStateAndValidDatabase
StateReadyWithoutDatabase_NeedsRecovery_DoesNotCreateDatabase
DatabaseWithoutState_ReadOnlyDiagnostic_DoesNotDeleteOrInit
StateInvalid_ReadOnlyDiagnostic_PreservesState
InitializationIncomplete_ReadOnlyDiagnostic
BlockedState_CrossesNewCoordinator_StaysBlocked
WalSidecar_NeedsRecovery_DoesNotDeleteSidecar
CorruptDatabase_NeedsRecovery_PreservesFile
IncompleteRestoreJournal_NeedsRecovery_DoesNotCleanArtifacts
RestoreJournalCorrupt_ReadOnlyDiagnostic
RestoreJournalCompleted_DoesNotBlock
```

- [ ] **Step 2: 写产物扫描测试**

```text
Scan_RestoreDirectoryWithRollback_IsRetained
Scan_BackupTempAndPackageTemp_AreRetainedInC1
Scan_ProtectionSnapshot_IsNotDeleted
Scan_NoArtifacts_ReturnsEmpty
```

C1 策略：**扫描并保留**所有恢复/回滚/保护/临时产物；不自动删除任何可能有恢复价值的东西。清理决策留给 C6/C7 或显式用户动作。

- [ ] **Step 2b: 写恢复日志测试/实现**

```text
RestoreJournal_RoundTripsAllStages
RestoreJournal_UnknownVersion_ReturnsInvalid
RestoreJournal_CorruptJson_ReturnsInvalid
Startup_JournalStageSwitching_NeedsRecovery
Startup_JournalStageCompleted_DoesNotBlock
Startup_JournalCorrupt_ReadOnlyDiagnostic
```

C1 只建立日志读写与启动识别；C4 在真实恢复流程写入各阶段。日志不得用于凭文件名猜恢复来源；存在未完成日志时保留 staging/protection/rollback，进入安全模式。

- [ ] **Step 3: 实现状态机**

```text
无状态 + 无数据库/草稿/恢复产物 -> 首次初始化：
  写 Initializing 状态 -> 创建目录 -> SqliteSchema.InitializeAsync -> Prepare/Inspect -> 写 Ready
状态 Initializing + 无数据库/无证据 -> ReadOnlyDiagnostic（提供重试初始化）
状态 Ready + 数据库缺失 -> NeedsRecovery（不建空库）
状态 Ready + 数据库损坏/不兼容 -> NeedsRecovery（保留原文件）
状态 Ready/任何 + WAL sidecar -> NeedsRecovery（不删 sidecar）
状态缺失 + 数据库/草稿/产物存在 -> ReadOnlyDiagnostic（不初始化）
状态损坏 -> ReadOnlyDiagnostic（不初始化）
WritesBlocked=true -> WriteBlocked（不自动清除）
恢复日志未完成（非 Completed/Failed） -> NeedsRecovery（保留 staging/保护/回退）
恢复日志损坏/未知版本 -> ReadOnlyDiagnostic（不清理、不猜来源）
否则 -> Ready（PrepareCurrent 恢复热 journal；失败 -> NeedsRecovery）
```

数据库检查用 `SqliteRestoreStore.PrepareCurrentAsync`/`InspectAsync`，不新增 SQL；准备后再次检查 sidecar。所有失败只改变结果，不删除当前库/保护文件。

- [ ] **Step 4: 运行测试**

```bash
dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/QuickNotes.Desktop.Tests.csproj --filter "FullyQualifiedName~StartupCoordinatorTests|FullyQualifiedName~StartupArtifactScannerTests"
```

Expected: 全部通过。

- [ ] **Step 5: 提交**

```bash
git add QuickNotes_AgentHandoff/src/QuickNotes.Application/RestoreJournal.cs QuickNotes_AgentHandoff/src/QuickNotes.Application/IRestoreJournalStore.cs QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Startup QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Recovery/FileRestoreJournalStore.cs QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/StartupCoordinatorTests.cs QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/StartupArtifactScannerTests.cs QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/RestoreJournalTests.cs
git commit -m "feat: 实现启动状态机、恢复日志识别与恢复产物保守扫描"
```

### Task 7: 缺失库安全重建与跨重启写入阻断

**Files:**
- Create: `src/QuickNotes.Desktop.Core/Recovery/MissingDatabaseRebuilder.cs`
- Create: `src/QuickNotes.Desktop.Core/Recovery/MissingDatabaseRebuildResult.cs`
- Create: `src/QuickNotes.Desktop.Core/State/PersistentDatabaseWriteGate.cs`
- Test: `tests/QuickNotes.Desktop.Tests/MissingDatabaseRebuilderTests.cs`
- Test: `tests/QuickNotes.Desktop.Tests/PersistentDatabaseWriteGateTests.cs`

**Interfaces:**
- Produces:
  - `MissingDatabaseRebuilder.RebuildAsync(packagePath, quarantineCorruptCurrent, CancellationToken)`
  - `MissingDatabaseRebuildResult`：`Succeeded/Failed`、错误码、消息、隔离/保留路径、恢复计数
  - `PersistentDatabaseWriteGate : IDatabaseWriteGate`，包装 `DatabaseWriteGate` + `ApplicationStateStore`

- [ ] **Step 1: 写重建测试**

```text
Rebuild_MissingDatabase_FromVerifiedPackage_Succeeds
Rebuild_CorruptPackage_LeavesDatabaseMissingAndPreservesStaging
Rebuild_WithValidCurrentDatabase_Refuses
Rebuild_WithCorruptCurrent_QuarantinesAndPublishes
Rebuild_PostPublishVerificationFailure_DoesNotReportSuccess
Rebuild_RecordsNoProtectionSnapshot
```

测试用真实 B3 包/真实 SQLite；合成便签；断言 schemas/counts/hash、状态文件、保护快照为“未创建”。

- [ ] **Step 2: 实现缺失库重建**

```text
1. 校验包路径；当前库存在且有效 -> 拒绝（交给 C4/B5 恢复）
2. 当前库损坏且 quarantineCorruptCurrent=false -> 拒绝
3. 在数据根同卷 .quicknotes-recovery-<guid>/ 提取并完整校验 notes.db
4. Inspect 暂存库 schema=1/完整性/外键/counts
5. 当前库不存在：确认无 sidecar -> File.Move(staged, current)
   当前库损坏且已确认：File.Replace(staged, current, quarantine)
6. 重新打开当前库，校验 hash 等于 manifest.DatabaseSha256 + schema/counts
7. 成功写 Ready 状态：RecoverySourceSha256、ProtectionSnapshotCreated=false、LastRecoveryAtUtc
8. 失败：保留 staging/quarantine；不写成功状态；不删除原库
```

不得调用 B5 的“保护快照必须成功”路径来伪造保护；日志明确 `recovery.no_current_database`。失败清理只删除明确无价值的临时文件。

- [ ] **Step 3: 写持久阻断测试/实现**

```text
BlockWrites_PersistsStateAcrossNewGateInstance
NewGate_WithPersistedBlock_RejectsEnterWrite
Startup_WithPersistedBlock_ReportsWriteBlocked
BlockWrites_StateWriteFailure_LogsAndStillBlocksInMemory
```

`PersistentDatabaseWriteGate.BlockWrites()` 先写状态再置内存阻断；状态写失败记录错误但仍在内存阻断。启动时读到阻断状态必须进入 `WriteBlocked`，不自动清除。

- [ ] **Step 4: 运行测试**

```bash
dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/QuickNotes.Desktop.Tests.csproj --filter "FullyQualifiedName~MissingDatabaseRebuilderTests|FullyQualifiedName~PersistentDatabaseWriteGateTests"
```

Expected: 全部通过。

- [ ] **Step 5: 提交**

```bash
git add QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Recovery QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/State/PersistentDatabaseWriteGate.cs QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/MissingDatabaseRebuilderTests.cs QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/PersistentDatabaseWriteGateTests.cs
git commit -m "feat: 实现缺失库安全重建与跨重启写入阻断"
```

### Task 8: WPF 启动/诊断窗口与组合根

**Files:**
- Modify: `src/QuickNotes.Desktop.Wpf/App.xaml`
- Modify: `src/QuickNotes.Desktop.Wpf/App.xaml.cs`
- Create: `src/QuickNotes.Desktop.Wpf/StartupWindow.xaml`
- Create: `src/QuickNotes.Desktop.Wpf/StartupWindow.xaml.cs`
- Create: `src/QuickNotes.Desktop.Core/Hosting/DesktopApplicationHost.cs`
- Create: `src/QuickNotes.Desktop.Core/Hosting/DesktopStartupOptions.cs`
- Create: `docs/agent/C1-人工检查清单.md`
- Test: `tests/QuickNotes.Desktop.Tests/DesktopApplicationHostTests.cs`
- Test: `tests/QuickNotes.Desktop.Tests/DesktopStartupOptionsTests.cs`

**Interfaces:**
- Consumes: Task 4–7 的路径/状态/单实例/启动/重建/gate。
- Produces: 可启动 C1 宿主；`--data-root <dir>`、可选 `--backup-dir <dir>`；最小诊断 UI；宿主逻辑无 WPF 依赖、可在 Desktop.Tests 驱动。

- [ ] **Step 1: 写宿主启动测试**

```text
Start_WhenNotOwner_DoesNotOpenDatabase
Start_WhenFirstRun_ReturnsReadyAndCreatesState
Start_WhenBlocked_ReturnsWriteBlocked
Start_WhenDatabaseMissing_ReturnsNeedsRecovery
Start_WithDataRootArgument_UsesGivenRoot
Start_WithBackupDirectoryArgument_ScansProtectionSnapshots
SecondProcess_ActivatesOwner
```

`SecondProcess_ActivatesOwner` 必须启动真实第二进程：测试持有 Core 的 owner guard，启动已构建的 `QuickNotes.Desktop.Wpf` 的 `--single-instance-probe --data-root <temp>` 模式，断言测试进程收到激活事件且探针退出码为“已激活”。探针模式在创建窗口/打开数据库前返回，不写数据。若无法在 CI 启动 WPF，标记“未运行”，不得用同进程两个 guard 冒充第二进程测试。

- [ ] **Step 2: 实现 App 生命周期**

`App.xaml` 删除 `StartupUri`；`App.xaml.cs` 使用显式 `OnStartup`：

```text
解析 --data-root（可选）、--backup-dir（可选，仅诊断扫描）、--single-instance-probe（仅第二进程测试）
single-instance-probe：只执行单实例协调，输出结果并退出；不创建窗口/不打开数据库
创建 DesktopApplicationHost
await host.StartAsync()
Owner 失败 -> 尝试激活/明确提示 -> Shutdown
Owner 失败且已有实例 -> 不打开数据
Owner 成功 -> 创建 StartupWindow，ShutdownMode=OnMainWindowClose
异常 -> 记录日志 + 错误窗口/消息 -> Shutdown(1)
```

禁止在 UI 线程同步等待数据库/管道。

- [ ] **Step 3: 实现 StartupWindow**

显示：模式（首次初始化/正常/需恢复/写入阻断/只读诊断）、原因、数据目录、日志目录、schema/计数、恢复产物数量。按钮：

- `打开数据目录`：`explorer.exe` 打开数据根。
- `从已验证备份重建`：仅 NeedsRecovery/缺失库时启用；`OpenFileDialog` 选择 ZIP；确认后调用 `MissingDatabaseRebuilder`；显示真实结果。
- `重试检查`：重新执行启动评估；**不自动清除阻断**。
- `退出`：关闭宿主管道/Mutex 并退出。

C1 不提供编辑器、设置页、备份列表、托盘或快捷键；Ready 状态显示“数据层已就绪，编辑功能将在 C2 接入”。

- [ ] **Step 4: 诊断日志接线**

日志文件路径 `logs/quicknotes-yyyyMMdd.log`；记录启动模式/原因、单实例结果、初始化/重建结果、错误码；不记录正文/草稿内容。日志失败不阻止启动，但 UI 显示日志不可用警告。

- [ ] **Step 5: 构建与启动冒烟**

```bash
dotnet build QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/QuickNotes.Desktop.Wpf.csproj
dotnet run --project QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/QuickNotes.Desktop.Wpf.csproj -- --data-root "%TEMP%\QuickNotes-C1-Smoke"
```

检查：首次启动创建 `notes.db`/`application-state.json`/logs；退出后再启动不重复初始化；第二进程不创建第二个数据写入者；记录结果。若无法观察 UI，标记“UI 人工检查未运行”，只记录状态文件/日志证据。

- [ ] **Step 6: 写人工检查清单**

列出：首次运行、正常重开、第二实例激活、标记文件缺失/DB 缺失、DB 损坏、状态损坏、WAL sidecar、写入阻断跨重启、恢复产物保留、选择备份重建、打开数据/日志目录、退出。每项记录通过/失败/未运行与环境。

- [ ] **Step 7: 提交**

```bash
git add QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Hosting QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/DesktopApplicationHostTests.cs QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/DesktopStartupOptionsTests.cs docs/agent/C1-人工检查清单.md
git commit -m "feat: 实现 C1 WPF 启动与诊断宿主"
```

### Task 9: C1 文档、全量验证、双轴审查与提交

**Files:**
- Create: `docs/agent/C1-测试报告.md`
- Create: `docs/agent/C1-完成总结.md`
- Modify: `docs/decisions/README.md`
- Modify: `README.md`、`QuickNotes_AgentHandoff/README.md`（桌面项目/运行说明）
- Verify: C0/C1 文档、ADR、测试报告、人工清单

**Interfaces:**
- Consumes: Task 1–8 的实现与证据。
- Produces: C1 交付记录与待合并分支。

- [ ] **Step 1: 写 C1 测试报告**

分开记录：通过（构建、单元/集成、启动状态机、单实例、重建、持久阻断）、失败（如有）、未运行（WPF 视觉/多显示器/真实强杀/真实卷/ACL/性能/长时间）。记录实际命令、环境、测试数量；不得把 C0/B7 结果当 C1 结果。

- [ ] **Step 2: 写 C1 完成总结**

按 C 文档 6.3 格式：结论/范围/前置；变更文件与接口；PRD 条款证据；命令/环境/通过/失败/未运行；数据保护与恢复验证；ADR/兼容性/审查发现；限制与 C2 进入条件；分支/工作区/提交状态。

- [ ] **Step 3: 更新文档索引/README**

在 ADR 索引加入三个 C1 ADR；README 增加桌面项目构建/启动与“C1 仅启动/诊断，无编辑器”的说明；链接 C0/C1 报告。

- [ ] **Step 4: 全量验证**

```bash
dotnet build QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln
dotnet test QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln --no-build --no-restore
dotnet list QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln package --vulnerable --include-transitive
```

Expected: 0 警告；既有 245 + 新增 C1 测试全部通过；无漏洞包。实际数字写入报告。

- [ ] **Step 5: 双轴审查**

标准轴：代码规范、异常/取消/资源释放、UI 线程阻塞、路径/日志、测试隔离、无直接 SQL/无绕过写入门。
规格轴：逐条对照 C1 工作/验收/交付；确认首次运行、缺失库、损坏库、状态损坏、WAL、阻断跨重启、恢复产物保留、重建失败不报成功均有证据；确认未实现 C2+。

- [ ] **Step 6: 修复并复验**

只修改 C1 范围内问题；若发现必须改 B5 语义/PRD 冲突，停止并报告具体冲突，不擅自扩大范围。复验失败项。

- [ ] **Step 7: 提交**

```bash
git add docs/agent/C1-测试报告.md docs/agent/C1-完成总结.md docs/decisions/README.md README.md QuickNotes_AgentHandoff/README.md
git commit -m "docs: 完成 C1 启动安全与恢复交付"
git status --short
```

Expected: 工作区干净；报告分支/提交、测试结果、未运行项与 C2 入口。

## Self-Review

**Spec coverage**

- C0：环境/仓库/B7 状态、B 回归、A 检查、PRD 映射、集成设计、ADR 清单、风险、性能候选 → Task 1–2。
- C1：WPF 宿主/组合根 → Task 3、8；单实例先于数据资源 → Task 5、8；数据目录/初始化标记/诊断日志 → Task 4、8；首次/缺失/损坏/未知/WAL/阻断状态/恢复日志 → Task 6；恢复产物保守识别 → Task 6；跨重启阻断 → Task 7；缺失库安全重建/不伪造保护 → Task 7、8；同用户第二进程与最小诊断 UI/人工检查 → Task 5、8；C1 文档/审查 → Task 9。
- C2–C7 明确未实现，仅 PRD 覆盖表标记归属。

**Placeholder scan**

- 无 TBD/TODO；未运行项要求逐项列出。性能数字标记候选，不冒充已达成。

**Type consistency**

- 项目/类型名在 Task 3–8 保持一致：`DesktopPaths`、`ApplicationStateStore`、`StartupCoordinator`、`SingleInstanceGuard`、`MissingDatabaseRebuilder`、`PersistentDatabaseWriteGate`、`DesktopApplicationHost`。
- 复用既有接口：`IDatabaseWriteGate`、`IRestoreStore`、`IBackupPackageExtractor`、`SqliteSchema`、`SqliteRestoreStore`；不新增重复保存/恢复语义。

## 执行前需要确认

- C0/C1 是否按本计划继续；C1 首个可用版本只提供启动/诊断窗口，不提供编辑器。
- 单实例命名管道激活、数据根 `%LOCALAPPDATA%\QuickNotes` 是否接受；如不接受，在 Task 2 ADR 前确定替代。
- “当前库损坏”在 C1 只提供安全诊断与手动选择已验证备份重建，不自动替换；确认是否接受。
- 性能候选预算是否作为 C2/C6 的初始目标；C1 不据此判定通过。
