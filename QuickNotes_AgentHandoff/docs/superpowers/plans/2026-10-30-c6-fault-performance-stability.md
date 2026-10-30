# C6 真实故障环境、性能与稳定性实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在隔离、可销毁的 Windows 测试环境中验证 QuickNotes 的真实故障边界、重启恢复、稳定性和性能，输出可复现的 C6 证据，不把注入式测试或“没有异常”当成真实环境通过。

**Architecture:** 新增一个只用于测试的 `QuickNotes.C6.Harness` 控制台宿主，复用真实 `DesktopApplicationHost`、SQLite、备份、恢复、迁移和保存服务；通过可配置检查点文件，让外部 PowerShell 驱动在指定阶段终止进程。真实卷/ACL 故障由隔离 VHD 或临时 ACL 脚本提供；性能/稳定性由同一 Harness 和 WPF UI 自动化脚本采集。测试不向生产代码加入测试专用分支。

**Tech Stack:** .NET 10、WPF、C#、SQLite（Microsoft.Data.Sqlite 10.0.0）、xUnit 2.9.3、PowerShell 7/Windows PowerShell、Windows VHD/`Mount-DiskImage`、`icacls`、Win32 进程/句柄 API。

**Spec:** `QuickNotes_AgentHandoff/docs/agent/Agent对接文档_C阶段.md` C6；PRD 19.1、19.2、19.3、10.3、11.7、18.1；C0 性能候选预算；C4/C5 完成总结与人工矩阵。

**Branch prerequisite:** C5 已合并到 `main`（`2f766e5`）并修复焦点（`9019fe7`）；C5 Task10 文档（`e590701`）需随基线一起冻结。建议新建 `feature/c6-fault-stability` 工作树，不在用户日常数据根执行真实故障。

---

## Global Constraints

- Windows 11 x64；不承诺 Windows 10/ARM/Server。
- 真实空间/ACL 故障只允许发生在临时 VHD 或已确认可销毁的临时目录；不得填满系统卷、修改真实用户 ACL、强杀用户正在使用的进程。
- 所有测试使用合成便签与临时数据根；清理脚本只删除本次运行创建且带 C6 运行 ID 的资源。
- 已确认保存的数据必须可验证找回；不得以“没有抛异常”、空库、自动清空或自动重建当作通过。
- 强杀进程不等于真实断电；虚拟卷不等于所有硬件故障；未运行组合必须在报告中单独列出。
- 真实环境测试前必须关闭/隔离现有 QuickNotes 实例，避免第二实例与默认 WPF `bin` 锁干扰。
- 日志不得记录便签正文、草稿内容或完整敏感路径；报告只记录阶段、错误码、计数、耗时和环境。
- 生产代码不得加入测试专用分支或 checkpoint 调用；checkpoint 由 Harness 装饰的接口实现写入文件。
- 测试快捷键/开机启动/托盘副作用必须由 Harness 隔离或脚本显式清理；正常回归不得写 HKCU Run。
- C6 不实现导出、安装器、自动更新、备份加密、云存储、WAL 或多实例写入；这些明确不在 C6 范围。
- 性能预算以 C0 候选为初始值，**必须在 Task 1 由用户确认后才能作为 C6 通过标准**；未确认时 Task 5 只记录基线，不判定通过。
- 每个 Task 先写失败测试/验证脚本，再实现；提交独立、可回滚；通过/失败/未运行分开报告。
- A-11 断屏历史失败不因 C6 的其他通过项自动视为修复；C5-M 未执行项必须在 C6 报告中单独列出。

---

## 0. 决策与门禁

1. **C5-M 门禁**
   - C6 自动化可先开发；但 C6 最终验收前，C5-M 真实托盘、快捷键、开机启动、全局隐藏、多屏/DPI、20 窗口必须完成，或有用户书面接受的风险例外。
   - 未完成时 C6 只能标记为“自动化/性能部分完成”，不能标记 C6 通过。

2. **性能预算（候选，Task 1 确认）**
   - 历史数据：1,000 张便签。
   - 可见窗口：5、10、20 张。
   - 启动：无 UI 到 Ready p95 ≤ 2s；含窗口显示 p95 ≤ 3s。
   - 保存提交：p95 ≤ 500ms，p99 ≤ 1.5s；BUSY 最坏按 B2 ADR。
   - UI 输入响应：≤ 100ms。
   - 20 窗口 + 1,000 历史工作集：≤ 300MB。
   - 备份/恢复：记录实际耗时；在测得基线前不写死通过阈值。
   - 长稳：2 小时编辑/空闲/显示隐藏循环；无未处理异常、无持续句柄/内存增长；建议追加门禁为内存增长 ≤15%、句柄增长 ≤50（需用户确认）。
   - 记录的测量方法：每项至少 10 次启动/50 次保存/100 次输入，报告 p50/p95/p99、最大/最小、环境与样本数。

3. **故障环境**
   - 磁盘满：只在临时 VHD（建议初始 512MB–2GB，按测试需求选择）上制造；绝不使用系统盘。
   - 权限：只在临时目录上对当前测试用户添加/恢复 ACL；若无法安全恢复，立即停止并保留诊断，不修改真实用户目录。
   - 备份目录离线：卸载/重命名临时 VHD 或临时目录，不使用真实备份目录。
   - 强杀：Process.Kill(entireProcessTree: true) 仅针对带 C6 运行 ID 的 Harness 子进程。
   - 第二实例：使用 Harness 的临时数据根；真实进程探针带 `--single-instance-probe`。

4. **测试数据**
   - 1,000 张历史便签通过仓储批量填充，至少包含分类、标签、可见/隐藏、归档/回收站分布。
   - 20 张可见便签有独立窗口；不得用同一窗口重复计数。
   - 所有便签正文包含 C6 运行 ID，便于验证找回；报告中不记录正文。

5. **范围边界**
   - C6 不修复产品功能需求；只修复 C6 发现的故障安全/性能/稳定性问题。
   - C5-M 未执行项不因 C6 通过而自动变成通过。
   - 导出、安装器、OS 通知留在后续阶段；若 C6 期间发现其阻断首版，只记录风险，不夹带实现。

---

## 1. 文件结构（预计）

```text
QuickNotes_AgentHandoff/
├─ tests/
│  ├─ QuickNotes.C6.Harness/
│  │  ├─ QuickNotes.C6.Harness.csproj
│  │  ├─ Program.cs                        # CLI 解析与退出码
│  │  ├─ HarnessOptions.cs                 # scenario/data-root/checkpoint
│  │  ├─ CheckpointFileSink.cs             # 原子 marker 文件
│  │  ├─ Scenarios/
│  │  │  ├─ BackupScenario.cs
│  │  │  ├─ RestoreScenario.cs
│  │  │  ├─ MigrationScenario.cs
│  │  │  └─ SaveScenario.cs
│  │  ├─ FaultDecorators.cs                # 只存在于 Harness 的接口装饰器
│  │  ├─ C6DataFactory.cs                  # 1000 历史/可见便签种子
│  │  └─ MetricsCollector.cs
│  └─ QuickNotes.IntegrationTests/
│     └─ C6/
│        ├─ CheckpointFileSinkTests.cs
│        ├─ C6DataFactoryTests.cs
│        ├─ C6BudgetTests.cs
│        ├─ ProcessKillHarnessTests.cs     # 可安全并行的 fake harness
│        └─ FaultDecoratorTests.cs
├─ scripts/c6/
│  ├─ New-C6TestVolume.ps1
│  ├─ Remove-C6TestVolume.ps1
│  ├─ Invoke-C6StorageFaults.ps1
│  ├─ Invoke-C6AclFaults.ps1
│  ├─ Invoke-C6ProcessKill.ps1
│  ├─ Invoke-C6Performance.ps1
│  └─ Clear-C6Artifacts.ps1
├─ docs/agent/
│  ├─ C6-测试报告.md
│  ├─ C6-故障矩阵.md
│  ├─ C6-性能与稳定性报告.md
│  ├─ C6-完成总结.md
│  └─ C6-M-人工检查清单.md
└─ docs/superpowers/plans/2026-10-30-c6-fault-performance-stability.md
```

Harness 不属于生产运行时；`QuickNotes.DataPrototype.sln` 增加 Harness 项目，但不把真实故障脚本加入默认 `dotnet test`，避免普通开发机误触发卷/ACL 操作。

---

### Task 1: Harness、检查点、数据工厂与预算确认门禁

**Files:**
- Create: `QuickNotes_AgentHandoff/tests/QuickNotes.C6.Harness/QuickNotes.C6.Harness.csproj`
- Create: `QuickNotes_AgentHandoff/tests/QuickNotes.C6.Harness/Program.cs`
- Create: `QuickNotes_AgentHandoff/tests/QuickNotes.C6.Harness/HarnessOptions.cs`
- Create: `QuickNotes_AgentHandoff/tests/QuickNotes.C6.Harness/CheckpointFileSink.cs`
- Create: `QuickNotes_AgentHandoff/tests/QuickNotes.C6.Harness/C6DataFactory.cs`
- Create: `QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/C6/CheckpointFileSinkTests.cs`
- Create: `QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/C6/C6DataFactoryTests.cs`
- Create: `QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/C6/C6BudgetTests.cs`
- Create: `QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/C6/C6FaultFakes.cs`
- Modify: `QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/QuickNotes.IntegrationTests.csproj`（引用 Harness）
- Modify: `QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln`

**Interfaces:**
- Consumes: `SqliteConnectionFactory`, `SqliteSchema`, `INoteRepository`/`ICatalogRepository`, existing `DesktopApplicationHost`.
- Produces:
  - `HarnessOptions.Parse(string[] args)` → `HarnessOptions(Scenario, DataRoot, BackupDirectory, CheckpointDirectory, NoteCount, VisibleCount)`.
  - `CheckpointFileSink.Reached(string name)` → creates `<checkpointDirectory>/<name>.reached` atomically; never contains note body.
  - `C6DataFactory.SeedAsync(string dataRoot, int notes, int visible, CancellationToken)` → returns `SeedResult(NoteIds, VisibleNoteIds)`.
  - `C6Budget` constants for C0 candidate values.

- [ ] **Step 1: Write failing checkpoint/data-factory/budget tests.**

```csharp
[Fact]
public void CheckpointFileSink_WritesMarkerAtomically()
{
    using var temp = new TestTemporaryDirectory();
    var sink = new CheckpointFileSink(temp.RootPath);
    sink.Reached("restore.before_switch");
    var path = Path.Combine(temp.RootPath, "restore.before_switch.reached");
    Assert.True(File.Exists(path));
    Assert.Empty(Directory.GetFiles(temp.RootPath, "*.tmp"));
}

[Fact]
public async Task DataFactory_SeedsRequestedHistoryAndVisibleNotes()
{
    using var temporary = new TestTemporaryDirectory();
    var result = await C6DataFactory.SeedAsync(temporary.RootPath, notes: 1000, visible: 20);
    Assert.Equal(1000, result.NoteIds.Count);
    Assert.Equal(20, result.VisibleNoteIds.Count);
}
```

- [ ] **Step 2: Run RED.**

Run: `dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/QuickNotes.IntegrationTests.csproj --filter "FullyQualifiedName~C6"`

Expected: FAIL because `CheckpointFileSink`, `C6DataFactory`, and Harness project do not exist.

- [ ] **Step 3: Create Harness project and CLI contract.**

```csharp
// Program.cs
var options = HarnessOptions.Parse(args);
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(30));
return await HarnessRunner.RunAsync(options, cancellation.Token);
```

Supported scenarios in first implementation:

```text
save       --data-root <dir> --checkpoint-dir <dir> [--notes 1000]
backup     --data-root <dir> --backup-dir <dir> --checkpoint-dir <dir>
restore    --data-root <dir> --backup-dir <dir> --package <zip> --checkpoint-dir <dir>
migrate    --data-root <dir> --checkpoint-dir <dir> --source-schema 1
perf       --data-root <dir> --notes 1000 --visible 20 --iterations 30 --output <json>
```

- [ ] **Step 4: Implement atomic checkpoint and deterministic test data.**

```csharp
public sealed class CheckpointFileSink
{
    private readonly string _directory;
    public CheckpointFileSink(string directory) => _directory = directory;

    public void Reached(string name)
    {
        Directory.CreateDirectory(_directory);
        var target = Path.Combine(_directory, $"{name}.reached");
        var temporary = $"{target}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, DateTimeOffset.UtcNow.ToString("O"));
        File.Move(temporary, target, overwrite: true);
    }
}
```

`C6DataFactory` must create the real schema, the system category, 1,000 notes through repositories, 20 `DisplayIntent.Visible` notes, and a fixed tag/category distribution. It must not write a hand-made SQLite file that bypasses schema validation.

- [ ] **Step 5: Add budget gate and make it explicit.**

```csharp
public static class C6Budget
{
    public const int HistoricalNotes = 1000;
    public const int VisibleWindows = 20;
    public static readonly TimeSpan StartupReadyP95 = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan StartupWithUiP95 = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan SaveP95 = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan SaveP99 = TimeSpan.FromMilliseconds(1500);
    public static readonly TimeSpan InputP95 = TimeSpan.FromMilliseconds(100);
    public const long WorkingSetBytes = 300L * 1024 * 1024;
}
```

`C6BudgetTests` asserts values are positive and distinct; it must not treat candidate values as user-approved acceptance. Task 1 completes only after the user confirms or explicitly accepts “baseline only” mode.

- [ ] **Step 6: Run GREEN and commit.**

Run: `dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/QuickNotes.IntegrationTests.csproj --filter "FullyQualifiedName~C6"`

Expected: checkpoint/data/budget tests PASS.

Commit: `git commit -m "test: 添加 C6 故障 Harness 与数据工厂"`.

---

### Task 2: 隔离存储故障：磁盘满、权限拒绝、备份离线

**Files:**
- Create: `QuickNotes_AgentHandoff/scripts/c6/New-C6TestVolume.ps1`
- Create: `QuickNotes_AgentHandoff/scripts/c6/Remove-C6TestVolume.ps1`
- Create: `QuickNotes_AgentHandoff/scripts/c6/Invoke-C6StorageFaults.ps1`
- Create: `QuickNotes_AgentHandoff/scripts/c6/Invoke-C6AclFaults.ps1`
- Create: `QuickNotes_AgentHandoff/tests/QuickNotes.C6.Harness/Scenarios/BackupScenario.cs`
- Create: `QuickNotes_AgentHandoff/tests/QuickNotes.C6.Harness/Scenarios/SaveScenario.cs`
- Create: `QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/C6/FaultDecoratorTests.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/C6/C6BudgetTests.cs`（补充预算确认状态）
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/C6/C6FaultFakes.cs`（保存/恢复故障装饰器）

**Interfaces:**
- `New-C6TestVolume.ps1 -SizeMB <n> -RunId <id>` → JSON `{ RunId, MountPath, VolumeId, CreatedByC6 }`; validates volume is not C: and path under `%TEMP%\QuickNotes-C6`.
- `Remove-C6TestVolume.ps1 -RunId <id>` → dismount/detach/delete only a matching C6 volume; refuses if `CreatedByC6` is absent.
- `Invoke-C6StorageFaults.ps1 -RunId <id> -Scenario save|backup|restore|migration` → writes `result.json` with environment, expected, actual, restart result, evidence paths.
- Harness `--fault-disk-full` and `--fault-offline` flags only affect test composition; no production flags.

- [ ] **Step 1: Write failing decorator tests (safe, no VHD).**

```csharp
[Fact]
public async Task Save_DiskFull_ReturnsStorageUnavailableAndKeepsDraft()
{
    var inner = new FaultingNoteSaveCoordinator(_ => throw new IOException("disk full"));
    var result = await inner.CreateAsync(TestData.NewNote(), Array.Empty<Guid>());
    Assert.Equal(SaveStatus.StorageUnavailable, result.Status);
    Assert.NotEqual(SaveStatus.Saved, result.Status);
}
```

Run: `dotnet test ... --filter "FullyQualifiedName~FaultDecoratorTests"` — expect FAIL until the fault decorator contract is implemented.

- [ ] **Step 2: Implement safe VHD script with hard guards.**

```powershell
param([int]$SizeMB = 1024, [string]$RunId = $(throw "RunId required"))
$root = Join-Path $env:TEMP "QuickNotes-C6"
if ($SizeMB -lt 256 -or $SizeMB -gt 4096) { throw "Unsafe size" }
$vhdx = Join-Path $root "$RunId.vhdx"
New-VHD -Path $vhdx -SizeBytes ($SizeMB * 1MB) -Dynamic | Out-Null
$disk = Mount-DiskImage -ImagePath $vhdx -PassThru
# initialize/format with a unique label; assign a free drive letter
# write JSON with RunId, MountPath, VolumeId, CreatedByC6=true
```

Do not format or attach an existing volume. Refuse if `$vhdx` exists or if any target drive is `C:`/system. Cleanup must tolerate partial creation.

- [ ] **Step 3: Add ACL script with restoration guarantee.**

```powershell
$target = Join-Path $env:TEMP "QuickNotes-C6\$RunId\acl-root"
New-Item -ItemType Directory -Force $target | Out-Null
$user = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$oldSddl = (Get-Acl $target).Sddl
try { icacls $target /deny "$user:(OI)(CI)W" | Out-Null; ... }
finally { $acl = Get-Acl $target; $acl.SetSecurityDescriptorSddlForm($oldSddl); Set-Acl $target $acl }
```

If ACL restoration fails, stop and print the SDDL/repair instructions; never leave a denied ACL on a user directory.

- [ ] **Step 4: Execute the fault matrix on a disposable VHD / temp ACL root.**

Scenarios:

| Case | Injection | Expected |
|---|---|---|
| C6-F01 | VHD free space exhausted during autosave | save not reported success; draft remains; restart can recover |
| C6-F02 | temp ACL denies writes to draft directory | draft failure visible; retry after ACL restore |
| C6-F03 | backup destination becomes unwritable/offline before publish | no final package, no false success; existing backup remains |
| C6-F04 | migration staging volume full | original v1/current DB remains; migration not marked success |
| C6-F05 | restore staging full | protection/current DB retained; no switch |
| C6-F06 | backup directory offline during restore preflight | restore rejected with actionable error; writes not bypassed |

- [ ] **Step 5: Verify and report.**

`Invoke-C6StorageFaults.ps1` writes `result.json` per case; a case is PASS only if:
1. no success status on failure;
2. original readable DB/only protected data remains;
3. restart result is recorded;
4. cleanup succeeds.

Run cleanup and confirm no `QuickNotes-C6` VHD/ACL remains:

`.\scripts\c6\Clear-C6Artifacts.ps1 -RunId <id>`.

Commit: `git commit -m "test: 完成 C6 隔离存储故障矩阵"`.

---

### Task 3: 可控检查点强杀：备份、迁移、恢复与保存

**Files:**
- Create: `QuickNotes_AgentHandoff/tests/QuickNotes.C6.Harness/Scenarios/RestoreScenario.cs`
- Create: `QuickNotes_AgentHandoff/tests/QuickNotes.C6.Harness/Scenarios/MigrationScenario.cs`
- Create: `QuickNotes_AgentHandoff/scripts/c6/Invoke-C6ProcessKill.ps1`
- Create: `QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/C6/ProcessKillHarnessTests.cs`
- Create: `QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/C6/ProcessHarness.cs`
- Modify: `QuickNotes_AgentHandoff/tests/QuickNotes.C6.Harness/FaultDecorators.cs`

**Interfaces:**
- `FaultDecorators` wrap `IBackupPackageStore.PublishAsync`, `IRestoreStore`/restore storage, `ISchemaMigrator.MigrateToCurrentAsync`, and `NoteSaveCoordinator` with `CheckpointFileSink`.
- Marker names:
  - `backup.before_publish`, `backup.after_publish`
  - `restore.before_switch`, `restore.after_switch`, `restore.rollback`
  - `migration.before`, `migration.after`
  - `save.after_draft`, `save.before_commit`, `save.after_commit`
- External killer waits for marker with timeout, kills only the Harness PID tree, then runs a startup/restart probe.

- [ ] **Step 1: Write failing process-harness test using a harmless fake child.**

```csharp
[Fact]
public async Task KillAtMarker_TerminatesOnlyHarnessProcessTree()
{
    var markerDir = CreateTempMarkerDirectory();
    var child = StartFakeCheckpointChild(markerDir); // prints PID, writes marker
    await WaitForMarkerAsync(Path.Combine(markerDir, "test.reached"), 5.Seconds());
    child.Kill(entireProcessTree: true);
    await child.WaitForExitAsync();
    Assert.True(child.HasExited);
}
```

Run RED with the helper absent.

- [ ] **Step 2: Implement checkpoint decorators and scenario exit codes.**

Each scenario prints one JSON line on stdout:

```json
{"scenario":"restore","status":"running","pid":1234,"checkpoint":"restore.before_switch"}
```

Only stage names, IDs and durations are emitted; never note body or full sensitive paths.

- [ ] **Step 3: Add real kill matrix.**

| Case | Kill point | Restart assertion |
|---|---|---|
| C6-K01 | `backup.before_publish` | no final package; prior backups intact |
| C6-K02 | `backup.after_publish` | package is either valid or safely absent; no corrupt final |
| C6-K03 | `save.after_draft` | draft exists; restart offers recovery |
| C6-K04 | `save.before_commit` | no false saved status; draft retained |
| C6-K05 | `restore.before_switch` | current DB still usable; restore marked interrupted |
| C6-K06 | `restore.after_switch` | restart runs recovery; no unverified/empty DB |
| C6-K07 | `restore.rollback` | backout result is safe; blocked state if rollback fails |
| C6-K08 | `migration.before` | v1/current source preserved |
| C6-K09 | `migration.after` | migration state verified; no half-switch |

- [ ] **Step 4: Verify restart behavior for every case.**

`Invoke-C6ProcessKill.ps1` after each kill starts `QuickNotes.C6.Harness restart --data-root ...` and requires a JSON result containing `startupMode`, `writesBlocked`, `noteCount`, `draftCount`, and `recoveryArtifacts`—never body content. A case is FAIL if the DB is silently empty, success is reported after a kill, or protection files disappeared.

- [ ] **Step 5: Commit.**

Run all C6-K cases; attach result JSON; clean temp roots.

Commit: `git commit -m "test: 完成 C6 检查点强杀与重启矩阵"`.

---

### Task 4: 跨重启阻断、恢复保护与第二实例竞争

**Files:**
- Create: `QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/C6/C6RecoveryInvariantTests.cs`
- Create: `QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/C6/C6SecondInstanceTests.cs`
- Modify: `QuickNotes_AgentHandoff/tests/QuickNotes.C6.Harness/Scenarios/RestoreScenario.cs`
- Create: `QuickNotes_AgentHandoff/docs/agent/C6-M-人工检查清单.md`

**Interfaces:**
- `C6RecoveryVerifier` reads startup assessment/journal/state and asserts:
  - valid current DB or explicit `NeedsRecovery`/`WriteBlocked`/`ReadOnlyDiagnostic`;
  - protection snapshot/rollback data retained;
  - blocked writes remain blocked after restart;
  - draft boundary and old session invalidation are honored.
- `C6SecondInstanceTests` starts a Harness restore paused at `restore.before_switch`, then launches the normal probe and asserts no competing write / clear activation result.

- [ ] **Step 1: Write failing invariants tests.**

```csharp
[Fact]
public async Task RestartAfterFailedRollback_RemainsWriteBlocked()
{
    var fixture = await C6RecoveryFixture.CreateBlockedAsync();
    await using var host = new DesktopApplicationHost(fixture.Options);
    var result = await host.StartAsync();
    Assert.Equal(StartupMode.WriteBlocked, result.Assessment!.Mode);
    Assert.True(result.Assessment.State!.WritesBlocked);
}
```

- [ ] **Step 2: Implement restart probes and second-instance scenarios.**

C6-M checklist (manual/automated partial):

```text
C6-M01 第二实例同时启动：只一个 owner，另一个激活/明确失败
C6-M02 恢复暂停时第二实例启动：无竞争写入，状态可诊断
C6-M03 回退失败重启：写入门仍阻断，保护快照/回退文件保留
C6-M04 旧会话失效：重启后旧编辑不可提交
C6-M05 安全清理：不删除保护/回退/未完成恢复证据
C6-M06 C5-M 未执行项复核
```

- [ ] **Step 3: Run and report; mark unrun items explicitly.**

Run service-level probes without touching real user state. Sleep/wake and real monitor disconnect remain C5/C6 manual items until executed.

Commit: `git commit -m "test: 覆盖 C6 恢复阻断与第二实例竞争"`.

---

### Task 5: 性能、长时间稳定与 1,000/20 数据量

**Files:**
- Create: `QuickNotes_AgentHandoff/scripts/c6/Invoke-C6Performance.ps1`
- Create: `QuickNotes_AgentHandoff/tests/QuickNotes.C6.Harness/MetricsCollector.cs`
- Create: `QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/C6/C6PerformanceTests.cs`
- Create: `QuickNotes_AgentHandoff/docs/agent/C6-性能与稳定性报告.md`（Task 6 完成内容）
- Modify: `QuickNotes_AgentHandoff/tests/QuickNotes.C6.Harness/Program.cs`

**Interfaces:**
- `MetricsCollector.SampleAsync(string name, Func<CancellationToken, Task>)` → `MetricSample(Name, P50Ms, P95Ms, P99Ms, MaxMs, AllocatedBytes, WorkingSetBytes, HandleCount)`; uses `Process.GetCurrentProcess()` and `GC.GetTotalAllocatedBytes`.
- `MetricsCollector.FromMilliseconds(string name, IReadOnlyList<double> values)` → `MetricSample`; handles empty input with an explicit `no_samples` error, not a fake pass.
- Test helper `ProcessHarness.StartAsync(string harnessAssembly, HarnessOptions options)` → child process with redirected stdout/stderr; `WaitForMarkerAsync(path, timeout)`; `KillTreeAsync()`; used only by C6 tests.
- `C6PerformanceTests`/Harness `perf` output JSON with no note body.
- Budget assertions only run when `C6_BUDGETS_CONFIRMED=1`; otherwise output `budgetStatus=unconfirmed` and never claim pass.

- [ ] **Step 1: Write failing metrics/serialization tests.**

```csharp
[Fact]
public void MetricsCollector_ReportsPercentilesAndNeverText()
{
    var samples = new[] { 10d, 20d, 30d, 40d, 100d };
    var metric = MetricsCollector.FromMilliseconds("save", samples);
    Assert.Equal(30, metric.P50Ms, 0.1);
    Assert.Equal(100, metric.P99Ms, 0.1);
    Assert.DoesNotContain("body", JsonSerializer.Serialize(metric), StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Implement service-level measurements.**

Measure at least:
- Host startup until `StartAsync` returns (10 iterations, p95).
- WPF show-to-UI automation ready via `Invoke-C6Performance.ps1` (10 launches, p95).
- 50 saves (`NoteSaveCoordinator`/session) and 100 UI input probes.
- backup/restore durations for database sizes 100/1,000 notes; record both UI response and background completion.
- process working set/handles at 5/10/20 visible windows with 1,000 history.
- repeated show/hide cycles, 2-hour soak (`Invoke-C6Performance.ps1 -DurationMinutes 120`).

- [ ] **Step 3: Apply budget gate.**

If `$env:C6_BUDGETS_CONFIRMED -ne "1"`:
- write `budgetStatus=unconfirmed`;
- report raw metrics;
- do not mark C6 performance as passed.

If confirmed:
- startup/save/input/memory values compared to `C6Budget`;
- any over-budget result is FAIL and requires a C6 finding or explicit user acceptance; no threshold changes after seeing results.

- [ ] **Step 4: Soak and leak criteria.**

For 2h:
- sample working set and handle count every 60s;
- fail on unhandled exception, crash, unrecoverable dialog loop, unbounded growth above confirmed budget;
- record 5/10/20 window behavior and repeated hide/show;
- separate service-level pass from UI/manual pass.

- [ ] **Step 5: Commit.**

Commit: `git commit -m "test: 完成 C6 性能与稳定性采集"`.

---

### Task 6: 报告、双轴审查与 C6 交付

**Files:**
- Create: `QuickNotes_AgentHandoff/docs/agent/C6-测试报告.md`
- Create: `QuickNotes_AgentHandoff/docs/agent/C6-故障矩阵.md`
- Create: `QuickNotes_AgentHandoff/docs/agent/C6-性能与稳定性报告.md`
- Create: `QuickNotes_AgentHandoff/docs/agent/C6-完成总结.md`
- Create: `QuickNotes_AgentHandoff/docs/agent/C6-M-人工检查清单.md`
- Modify: `QuickNotes_AgentHandoff/README.md`
- Modify: `README.md`
- Modify: `QuickNotes_AgentHandoff/docs/agent/C阶段-PRD覆盖表.md`
- Modify: `QuickNotes_AgentHandoff/docs/decisions/README.md`
- Modify: `QuickNotes_AgentHandoff/docs/agent/Agent对接文档_C阶段.md`（仅状态/入口，不改需求）

- [ ] **Step 1: Re-run the full solution and vulnerability audit.**

```bash
dotnet build QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln
dotnet test QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln --no-build --no-restore
dotnet list QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln package --vulnerable --include-transitive
```

Record build/test counts separately; real-fault scripts and soak are separate evidence.

- [ ] **Step 2: Write reports with explicit status.**

Every fault case table:
`ID | environment | fault injection | expected | actual | restart result | status (PASS/FAIL/NOT RUN) | evidence`.

Every performance row:
`metric | samples | p50 | p95 | p99 | max | budget | status | environment`.

C6 completion summary must state:
- PASS/FAIL/NOT RUN counts;
- C5-M status and A-11 retest status;
- remaining risks and C7 entry;
- no claim of power-loss or all-hardware coverage.

- [ ] **Step 3: Dual-axis review.**

Standard axis: no direct SQL from UI, no write path bypass, no false success, no leaked handles, no body logging, cleanup and async exception handling.

Spec axis: compare C6 work/acceptance/delivery items one-by-one; explicitly list skipped cases, mixed DPI, real power loss, C5-M pending, and export/installer out of scope.

Fix any Critical/Important issue before marking C6 done; add regression test.

- [ ] **Step 4: Update README/PRD/ADR and commit.**

Commit: `git commit -m "docs: 完成 C6 故障、性能与稳定性交付"`.

---

## Self-Review

**Spec coverage:**
- Real disk/permission/backup-offline faults → Task 2.
- Controlled checkpoint kill/restart → Task 3.
- Cross-restart blocking, protection, old session invalidation, second instance → Task 4.
- Performance/soak/1,000 notes/20 windows → Task 5.
- Reports/PRD/ADR/dual-axis → Task 6.
- C5-M prerequisite and A-11 → Global Constraints and Task 4/6.

**Placeholder scan:** No TBD/TODO; budget values are explicit candidate values with a documented confirmation gate.

**Type consistency:** Harness uses `CheckpointFileSink`, `HarnessOptions`, `C6DataFactory`; Task 3 wrappers use checkpoint names defined in Task 1; Task 5 consumes the same `C6Budget` constants defined in Task 1.

## Execution Gate

Before Task 1:
1. Confirm C6 performance budgets or accept baseline-only mode.
2. Confirm permission to create/remove temporary VHDs and ACL-denied temp directories.
3. Confirm C5-M manual matrix status; if unrun, accept that C6 final acceptance cannot be marked complete.
4. Create a dedicated C6 worktree from the C5-completed `main`; do not run real faults against the user’s C5 demo data.
