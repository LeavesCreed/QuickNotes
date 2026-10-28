# B6 故障注入测试 — 实施计划

> **状态：** 已完成（实现、测试与审查）；见 `docs/agent/B6-完成总结.md`\
> **日期：** 2026-10-28\
> **阶段：** B6\
> **基线：** `main` / `e58151f`（B5 已合并）\
> **上游依据：** Agent 对接文档 B6；PRD 10、11、13；B2–B5 完成总结与 ADR

## 1. 目标

在真实 SQLite/文件系统与可注入接缝上系统验证失败语义：

- 数据库写入失败/磁盘满/写入拒绝不报告成功、不留半写。
- 备份取消/中断/损坏/校验失败不发布半成品，源库可用。
- 恢复临时准备失败、切换失败、回退失败不误报成功；回退失败阻断后续写入并保留恢复文件。
- 留存清理失败不影响备份成功与源数据；手动/唯一恢复点不被误删。
- 备份目录不可用可诊断；旧/新写入竞争不静默覆盖。
- 所有故障路径可重复、使用临时目录/合成数据，不接触真实用户数据。

## 2. 故障矩阵

| 故障 | 注入方式 | 期望 |
|---|---|---|
| 数据库磁盘满 | 真实 SQLite 连接上设置 `PRAGMA max_page_count=<当前页数>`（工厂连接配置钩子） | 保存 `StorageUnavailable`（`sqlite_full`，不重试），无新便签，源库之后可写 |
| 数据库写入拒绝 | 临时数据库文件设为只读 | 保存类型化 `StorageUnavailable`，无新便签，原数据可读 |
| 备份快照磁盘满 | 注入式 `IBackupSnapshotProvider` 写部分文件后抛 `BackupException(SnapshotFailed)` | B3 协调器 `SnapshotFailed`，临时目录清理，无最终包，源库可用 |
| 备份包写入磁盘满 | 注入式 `IBackupPackageStore` 抛 `BackupException(PackageWriteFailed)` | `StorageUnavailable`，无最终包/临时残留，源库可用 |
| 备份取消 | 注入式包存储写部分临时文件后抛 `OperationCanceledException` | `OperationCanceledException`，无最终包/临时残留 |
| 备份损坏/校验失败 | 真实包篡改 DB 条目/哈希；B3 校验器与 B5 恢复预检 | 校验失败；恢复拒绝，当前库不变 |
| 恢复临时准备失败 | 包装 extractor：预检成功、staging 提取抛磁盘满 `BackupException` | `VerificationFailed/StagingUnavailable`，保护快照保留，当前库不变，staging 清理 |
| 恢复切换失败 | 包装 `IRestoreStore`：真实 `File.Replace` 前锁住当前 DB | `SwitchFailed`，当前库不变，保护快照保留，无假成功 |
| 恢复回退失败 | 包装 `IRestoreStore`：真实切换后返回不一致状态，回退抛异常 | `RollbackFailed/WritesBlocked`，保留保护/回退/staging；后续保存被阻断 |
| 留存清理失败 | 将一个待删日备份 ZIP 设为只读 | 日备份成功 + 清理警告；文件保留；源库不受影响 |
| 备份目录不可用 | 路径被文件占用/目录删除 | `Failed(DestinationUnavailable)`；不写状态/不清理；源库可用 |
| 旧/新写入竞争 | 两个协调器并发以同一 expectedVersion 更新同一便签 | 恰好一个 `Saved`、一个 `Conflict`；版本递增一次；无静默覆盖 |

## 3. 注入层级与边界

- **真实 OS/SQLite 层**：SQLite `max_page_count`（磁盘满语义）、只读 DB 文件、真实 `File.Replace` 被文件锁阻止、只读保留包删除失败、缺失/被占用的目录。
- **接口接缝层**：快照提供者、包存储、包提取器、恢复存储的装饰器，注入磁盘满/取消/回退失败；用于驱动真实协调器/真实 DB，避免依赖环境特定的 VHD/ACL/进程崩溃。
- **不做**：真实耗尽磁盘卷、系统级 ACL 拒绝、强杀进程；这些需要环境相关/特权配置，B6 用等价的可重复注入覆盖失败语义；真实崩溃/中断故障矩阵留后续运行环境或 B7 故障宿主。
- 所有测试使用临时目录与合成便签，结束后清理；只读属性/文件锁在 `finally` 中释放。

## 4. 测试清单

1. `SqliteFull_DuringSave_ReturnsStorageUnavailableAndKeepsDatabaseUsable`
2. `ReadOnlyDatabase_DuringSave_ReturnsStorageUnavailableWithoutChange`
3. `Backup_SnapshotDiskFull_CleansTempAndLeavesSourceUsable`
4. `Backup_PackageDiskFull_ReturnsStorageUnavailableAndNoFinalPackage`
5. `Backup_CancelledDuringPackaging_ThrowsAndLeavesNoFinalPackage`
6. `Restore_StagingDiskFull_PreservesProtectionAndCurrentDatabase`
7. `Restore_SwitchLocked_ReturnsSwitchFailedAndCurrentUnchanged`
8. `Restore_RollbackFailure_BlocksWritesAndPreservesRecoveryFiles`
9. `Retention_DeleteFailure_KeepsBackupAndReportsWarning`
10. `BackupDirectoryUnavailable_ReturnsDestinationUnavailable`
11. `ConcurrentUpdates_OneWinsOneConflictsWithoutLostUpdate`
12. `CorruptPackage_RestoreRejectedAndCurrentUnchanged`（B5 已有；B6 保留真实校验覆盖）

## 5. 生产代码变更

- `SqliteConnectionFactory` 增加可选 `Action<SqliteConnection>? configureConnection`，在连接 PRAGMA 后执行；仅用于连接级配置/故障注入，默认不影响行为。
- 其余为测试装饰器/辅助类；不改 schema、包格式或业务规则。

## 6. 验收

- 矩阵全部测试通过；失败路径不报告成功；原库/源库/保护快照保持可用。
- 测试确定性：无长时间 sleep/外部环境依赖；只读/锁在 finally 清理；全部临时目录。
- 全量测试、0 警告、无已知漏洞包；未运行项如实标注。

## 7. 风险

- 注入式磁盘满验证协调器映射与清理，不等价于真实卷耗尽；真实卷/崩溃矩阵留后续。
- 文件锁/只读行为依赖 Windows；正式目标 Windows 11 x64。
- 回退失败注入使用包装存储，真实 `File.Replace` 回退成功由 B5 集成测试覆盖。
- 并发竞争依赖 SQLite 锁与重试；测试接受恰好一个 Saved/一个 Conflict。
