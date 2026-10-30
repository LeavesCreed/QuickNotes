# C6-M 人工检查清单（跨重启阻断、恢复保护与第二实例）

**日期：** 2026-10-30（Task 6 复核）\
**范围：** C6-M01–C6-M06；失败回退后的写入阻断、恢复证据保留、旧会话失效与第二实例竞争\
**环境：** Windows 11 x64 `10.0.26300`、.NET SDK 10.0.402、PowerShell 7.6.7、临时数据根 `%TEMP%\QuickNotes-C6\<RunId>`；非提权\
**说明：** Agent 无法观察真实 WPF 恢复窗口、托盘、全局热键或系统唤醒事件。下表中“通过（自动化）”只代表对应进程/服务级自动化证据已执行；需要真实 UI 或设备的项目保持“待执行/未运行”，不得改写为通过。

**相关报告：** [C6 测试报告](C6-测试报告.md)、[C6 故障矩阵](C6-故障矩阵.md)、[C6 性能与稳定性报告](C6-性能与稳定性报告.md)、[C6 完成总结](C6-完成总结.md)。

## 0. 前置条件与结论

| 项目 | 状态 | 说明 |
|---|---|---|
| C5-M 人工矩阵（`C5-设备与人工矩阵.md`） | **未运行** | C5-M11–C5-M25（托盘、关闭三选项、快捷键、HKCU 开机启动、输入法、20 窗口、多屏/DPI、休眠唤醒、真实强杀/断电/卷满/ACL）仍未在实机执行。C6 最终验收仍以 C5-M 完成或书面风险接受为前提。 |
| C6-M06（C5-M 未执行项复核） | **未运行** | 依赖 C5-M；本轮不能判定通过。 |
| C6 自动化（C6-M01–C6-M05） | 服务/进程级通过 | 详见第 1 节；不包含真实 UI 视觉检查。 |
| C6 存储故障矩阵 | **F02/F03 PASS；F01/F04/F05/F06 NOT RUN** | 非提权/无 Hyper-V `New-VHD`；Task 3 后场景体已具备但未重跑存储满变体。见 [C6 故障矩阵](C6-故障矩阵.md)。 |
| C6 检查点强杀 | **K01–K09 全部 PASS** | Task 3 执行，Task 4 回归；见 [C6 故障矩阵](C6-故障矩阵.md)。 |
| C6 性能/长稳 | **聚合 FAIL；2h NOT RUN** | F-C6-PERF-01（UI 启动 p95 4,506.73/4,411.15 ms vs 3,000 ms）；3 分钟 smoke 不是长稳证明。见 [C6 性能与稳定性报告](C6-性能与稳定性报告.md)。 |
| C6 整体验收 | **不通过/条件未满足** | 即使 M01–M05 自动化通过，C6 仍因 F-C6-PERF-01、2h NOT RUN、F01/F04/F05/F06 NOT RUN、C5-M/C6-M06 NOT RUN 不能标记 PASS。 |
| 真实用户数据 | 未触碰 | 全部数据位于 `%TEMP%\QuickNotes-C6`；未写 HKCU、未注册热键、未创建托盘图标、未启动真实便签窗口。 |

## 1. 自动化/半自动证据

| 编号 | 检查项 | 结果 | 证据 |
|---|---|---|---|
| C6-M01 | 第二实例同时启动：只一个 owner，另一个激活或明确失败 | 通过（自动化） | `C6SecondInstanceTests.ConcurrentStart_ExactlyOneOwnerPerDataRoot`；`SecondInstance_WhileOwnerIsRunning_ActivatesWithoutWriting`；`SecondInstance_WhenActivationServerIsMissing_ReturnsClearFailure`（Unresponsive、退出码 2、无 DB/state 写入） |
| C6-M02 | 恢复暂停时第二实例启动：无竞争写入，状态可诊断 | 通过（自动化，真实进程） | `C6SecondInstanceTests.PausedRestore_SecondInstanceActivatesWithoutCompetingWrite`（opt-in，`C6_RUN_REAL_PROCESS_CASES=1`）：Harness 在 `restore.before_switch` 暂停并持有实例锁，测试在暂停期间断言 journal=Switching 且非终态；第二实例返回 `ActivatedExisting`、无窗口、无编辑/备份服务，`notes.db`/`application-state.json`/`recovery-journal.json` SHA-256 前后一致；释放后恢复成功且 journal=Completed。暂停超时由 `PauseTimeout_ReportsDistinctFailureAndNeverPassesAsWritesBlocked` 证明返回 `pause_timeout`/失败 |
| C6-M03 | 回退失败重启：写入门仍阻断，保护快照/回退文件保留 | 通过（自动化 + 真实进程） | `C6RecoveryInvariantTests.FailedRollback_PersistsWriteBlockBeforeHarnessEvaluatesScenario`（coordinator 返回后立即读取持久化状态，未使用 harness 重标记；`writeBlockPersisted=true`、原因 `restore_rollback_failed`）；`RestartAfterFailedRollback_RemainsWriteBlocked`；`RestartAfterFailedRollback_RetainsProtectionRollbackAndFailedJournal`（保护包可校验、`rollback.db` 可读、journal=Failed、`WritesBlocked=true`）；opt-in `FailedRollback_ProcessRestart_ReportsWriteBlockedWithRetainedEvidence`（独立进程 restore→restart，`startupMode=WriteBlocked`） |
| C6-M04 | 旧会话失效：重启后旧编辑不可提交 | 通过（自动化） | `RestartAfterFailedRollback_HonorsDraftBoundaryAndRejectsOldSessionSave`：恢复前的草稿/边界仍在且标记为 pre-restore；旧会话 `NoteSaveCoordinator.UpdateAsync` 返回 `StorageUnavailable`/`database_writes_blocked`、草稿保留、当前库未被改写；`host.WriteGate.EnterWriteAsync` 抛 `DatabaseWriteBlockedException` |
| C6-M05 | 安全清理：不删除保护/回退/未完成恢复证据 | 通过（自动化） | 同上测试在重启后逐一校验保护包、`rollback.db`、journal、草稿边界、草稿文件的 SHA-256，以及 `.quicknotes-restore-*` staging 目录的存在性（目录不做递归哈希）；仅显式 `Clear-C6Artifacts.ps1 -RunId` 删除一次性 run root，保留证据在 run root 之外 |
| C6-M06 | C5-M 未执行项复核 | **未运行** | C5-M 人工矩阵未执行；不得以本轮自动化替代托盘/热键/真实休眠等实机项 |

补充负向验证：`RecoveryVerifier_RejectsReadyAssessmentWhenDiskStateIsWriteBlocked` 与 `RecoveryVerifier_RejectsReadOnlyDiagnosticWithoutAnExplicitReason` 证明 `C6RecoveryVerifier` 会拒绝自相矛盾的启动评估，而不是无条件通过。

## 2. 人工检查表（真实 UI/设备）

| 编号 | 检查项 | 结果 | 备注 |
|---|---|---|---|
| C6-M01 | 同一数据根同时启动两个真实 WPF 实例：仅一个窗口/托盘。 | 待执行 | 自动化已覆盖锁/激活/失败码；真实窗口激活动画与托盘交互需人工。 |
| C6-M02 | 在“备份与恢复”执行恢复，进度到切换前时启动第二个实例：第二实例不写库，提示/激活明确。 | 待执行 | 自动化使用真实实例锁与激活管道；未观察真实恢复窗口与第二个 WPF 窗口。 |
| C6-M03 | 构造回退失败（故障注入包/受控环境），重启后界面明确显示写入阻断、数据目录与保护快照入口。 | 待执行 | 自动化已证明持久化状态与保护/回退文件；UI 文案与诊断入口需人工。 |
| C6-M04 | 恢复前保持未提交编辑，恢复/重启后旧编辑不可写回，草稿可恢复且标记为恢复前。 | 待执行 | 自动化在服务层验证；真实编辑器窗口需人工确认。 |
| C6-M05 | 重启、清理、退出后保护包/回退库/未完成恢复目录仍在；不得手工删除证据。 | 待执行 | 自动化已验证启动不删除；人工确认 UI/清理入口不会误删。 |
| C6-M06 | 逐项复核 `C5-设备与人工矩阵.md` 中 C5-M11–C5-M25。 | **未运行** | 见第 4 节；C6 最终验收前置条件。 |

## 3. 复现步骤（仅临时数据根）

```powershell
# 1) 构建
dotnet build QuickNotes_AgentHandoff\QuickNotes.DataPrototype.sln --nologo -v:q

# 2) 默认安全用例（不启动真实恢复进程、不强杀）
dotnet test QuickNotes_AgentHandoff\tests\QuickNotes.IntegrationTests\QuickNotes.IntegrationTests.csproj `
  --filter "FullyQualifiedName~C6RecoveryInvariantTests|FullyQualifiedName~C6SecondInstanceTests" --nologo

# 3) 真实进程 opt-in（恢复暂停 + 第二实例；失败回退 + 独立进程重启）
$env:C6_RUN_REAL_PROCESS_CASES = '1'
dotnet test QuickNotes_AgentHandoff\tests\QuickNotes.IntegrationTests\QuickNotes.IntegrationTests.csproj `
  --filter "FullyQualifiedName~FailedRollback_ProcessRestart|FullyQualifiedName~PausedRestore_SecondInstance" --nologo
Remove-Item Env:\C6_RUN_REAL_PROCESS_CASES

# 4) 清理一次性 run root（保留的证据在 run root 之外）
pwsh -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File QuickNotes_AgentHandoff\scripts\c6\Clear-C6Artifacts.ps1 -RunId <RunId>
```

- 手工复现恢复暂停（不执行强杀）：

```powershell
$runId = 'c6-m02-manual-' + [guid]::NewGuid().ToString('N')
$root = Join-Path $env:TEMP ('QuickNotes-C6\' + $runId)
dotnet QuickNotes_AgentHandoff\tests\QuickNotes.C6.Harness\bin\Debug\net10.0\QuickNotes.C6.Harness.dll `
  restore --data-root (Join-Path $root 'data') --backup-dir (Join-Path $root 'backups') `
  --package <临时包路径> --checkpoint-dir (Join-Path $root 'checkpoints') --run-id $runId `
  --pause-at restore.before_switch --pause-seconds 90 `
  --pause-release (Join-Path $root 'restore.release') --single-instance
# 看到 checkpoints\restore.before_switch.reached 后，再启动第二个探针/实例；
# 完成后写入 $root\restore.release，进程继续并输出一行 JSON。
```

- 仅使用 `%TEMP%\QuickNotes-C6` 下的合成数据；不访问真实数据根、不写 HKCU、不注册热键、不创建托盘图标。
- 真实恢复窗口/第二实例窗口的视觉检查需人工执行，未执行项保持“待执行”。

## 4. 未运行（NOT RUN）

| 项目 | 状态 | 原因 |
|---|---|---|
| C5-M11–C5-M14：关闭三选项、托盘、托盘退出 | 未运行 | 需真实系统托盘与 WPF 窗口操作 |
| C5-M15–C5-M19：全局隐藏、快捷键、输入法 | 未运行 | 需真实窗口、键盘/输入法与热键注册 |
| C5-M20：HKCU Run 开机启动 | 未运行 | 不得修改真实注册表 |
| C5-M21–C5-M25：关闭保存失败、20 窗口、1,000 历史、强杀/断电/卷满/ACL、C4 备份恢复人工项 | 未运行 | 需实机/人工；C6 仅覆盖服务级与隔离故障 |
| 多显示器/DPI 矩阵、断屏找回、A-11 重测 | 未运行 | 需真实显示器与系统显示设置变化 |
| 真实休眠/唤醒与显示器断开 | 未运行 | 需人工与 Windows 电源/显示事件 |
| C6-M02/M03 的真实 WPF 恢复窗口视觉与提示文案 | 待执行 | 自动化只验证进程、锁、状态与服务行为 |
| C6-M06 | 未运行 | C5-M 前置未完成 |
| C6-F01/F04/F05/F06 真实存储满/离线 | 未运行 | 非提权/无 `New-VHD`；Task 3 后未重跑存储满变体 |
| C6 2 小时长稳 | 未运行 | 3 分钟 smoke 不能替代 120 分钟门禁 |
| C6 性能聚合 | FAIL（F-C6-PERF-01） | UI 启动 p95 在保留运行中两次超 3,000 ms；不是“未运行” |

## 5. 记录

执行人：__________ 日期：__________ 设备/系统：__________\
显示器/缩放矩阵：__________________________________________\
未通过项与诊断日志路径：__________________________________\
C5-M 完成情况：____________________________________________

## 6. Task 6 复核记录（2026-10-30）

- 本清单第 1 节的 M01–M05 自动化结论未变化，证据仍为 Task 4 的默认测试与 opt-in TRX；Task 6 未重跑真实进程用例。
- 新增状态：C6 存储 F02/F03 PASS、F01/F04/F05/F06 NOT RUN；强杀 K01–K09 PASS；性能聚合 FAIL（F-C6-PERF-01）、2h NOT RUN；C5-M/C6-M06 仍 NOT RUN。
- 因此 **C6 整体验收不通过**；本清单中 M01–M05 的自动化 PASS 不能被引用为 C6 全部通过，也不能替代第 2 节的真实 UI/设备人工项。
- 最终交付状态见 [C6 完成总结](C6-完成总结.md)。
