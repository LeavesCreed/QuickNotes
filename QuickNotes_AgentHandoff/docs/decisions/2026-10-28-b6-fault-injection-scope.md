# ADR：B6 故障注入范围与注入层级

- **日期：** 2026-10-28
- **状态：** 已接受（随 B6 实施记录）
- **范围：** B6 故障注入测试
- **相关：** `docs/superpowers/plans/2026-10-28-b6-fault-injection.md`、B2–B5 ADR、PRD 10/11/13、Agent 对接文档 B6

## 背景

B2–B5 已实现保存失败语义、一致性备份、自动备份/留存与整库恢复/回退。既有测试覆盖了失败分支与部分真实 SQLite/文件系统行为，但缺少系统化的故障矩阵：磁盘满、写入拒绝、取消、损坏包、恢复临时准备/切换/回退失败、留存清理失败、目录不可用与旧/新写入竞争。

真实耗尽磁盘卷、系统级 ACL、强杀进程等故障依赖环境与特权配置，难以在常规测试中稳定重复。B6 需要明确哪些故障用真实层注入、哪些用接口接缝注入，以及不覆盖的边界。

## 决策

1. **双层注入。** 故障矩阵分两层：
   - **真实 OS/SQLite 层**：SQLite `PRAGMA max_page_count`（磁盘满语义）、只读数据库文件、真实 `File.Replace` 被文件锁阻止、只读留存包删除失败、被文件占用的备份目录。
   - **接口接缝层**：`IBackupSnapshotProvider`、`IBackupPackageStore`、`IBackupPackageExtractor`、`IRestoreStore` 的测试装饰器，注入快照/打包磁盘满、取消、staging 失败、切换歧义与回退失败。
   - 接缝层装饰器驱动**真实协调器与真实 SQLite 数据库**，只替换单一失败点；不断言真实协调器之外的业务逻辑。
2. **生产代码最小改动。** `SqliteConnectionFactory` 增加可选 `Action<SqliteConnection>? configureConnection = null`，在连接 PRAGMA 后同步执行。默认 `null` 时行为不变；仅用于连接级配置与故障注入（如 `PRAGMA max_page_count`），不进入业务契约。
3. **SQLite 磁盘满。** 在真实连接上把 `max_page_count` 限制为当前 `page_count`，再用 512 KB 正文触发 `SQLITE_FULL`。期望保存 `StorageUnavailable`、诊断含 `sqlite_full`、不重试（`Attempts=1`）；之后用正常连接确认库仍可写且新便签不存在。
4. **写入拒绝。** 临时数据库文件设只读，保存必须返回类型化 `StorageUnavailable`（不重试），原便签保留、新便签不存在；只读属性在 `finally` 恢复。
5. **备份故障。** 快照磁盘满、包写入磁盘满、打包取消、损坏包、目录不可用分别注入；期望 B3 协调器返回对应失败/抛取消，清理临时文件与目录，不发布最终包，源库仍可读。损坏包由 B3 校验器拒绝（SHA-256 不匹配），验证目录无残留。
6. **恢复故障。**
   - **staging 失败**：extractor 预检成功、staging 提取抛 `DestinationUnavailable`；期望 `VerificationFailed/StagingUnavailable`，保护快照保留，当前库不变，staging 清理。
   - **切换失败**：包装 `IRestoreStore`，在真实切换前用 `FileShare.None` 锁住当前库，迫使 `File.Replace` 失败；期望 `SwitchFailed`、当前库不变、保护快照保留、无假成功、写入门未阻断。
   - **回退失败**：包装 `IRestoreStore`，真实切换后报告不一致 counts，且回退抛 `RollbackFailed`；期望 `RollbackFailed/WritesBlocked`，写入门阻断，保护快照/回退库/staging 保留，恢复提示包含当前库路径；后续保存返回 `StorageUnavailable` + `database_writes_blocked`。
7. **留存清理失败。** 将待删日备份 ZIP 设为只读；期望日备份仍成功、留存警告 `retention_delete_failed`、只读文件保留、其他待删文件删除、周/日/手动保留点存在、状态文件写入、源库可读。
8. **目录不可用。** 备份路径被文件占用时，日备份服务返回 `Failed(DestinationUnavailable)` 且可重试；不写状态、不修改占用文件、源库可用。
9. **写竞争。** 两个 `NoteSaveCoordinator` 以相同 `expectedVersion` 并发更新同一便签；期望恰好一个 `Saved`、一个 `Conflict`，版本只递增一次，数据库内容等于胜者，无静默覆盖。
10. **不做。** 不模拟真实耗尽卷、系统级 ACL 拒绝、强杀进程、WAL、跨进程恢复锁、UI 确认界面；这些留运行环境或后续阶段。测试只使用临时目录与合成便签，只读属性/文件锁在 `finally` 释放；除并发测试的同步起点外不使用 sleep。

## 故障矩阵

| 故障 | 注入层 | 期望结果 |
|---|---|---|
| 数据库磁盘满 | 真实 SQLite `max_page_count` | `StorageUnavailable`/`sqlite_full`，不重试，无新便签，库之后可写 |
| 数据库写入拒绝 | 真实只读文件 | 类型化 `StorageUnavailable`，原数据保留 |
| 备份快照磁盘满 | 快照提供者装饰器 | `SnapshotFailed`，临时清理，无最终包 |
| 备份包写入磁盘满 | 包存储装饰器 | `StorageUnavailable/PackageWriteFailed`，无最终包 |
| 备份取消 | 包存储装饰器 | `OperationCanceledException`，无最终包/临时残留 |
| 备份损坏 | 真实包篡改 | 校验失败；恢复拒绝，当前库不变 |
| 恢复 staging 失败 | 提取器装饰器 | `VerificationFailed/StagingUnavailable`，保护/当前库保留 |
| 恢复切换失败 | 真实文件锁 | `SwitchFailed`，当前库不变，保护保留 |
| 恢复回退失败 | 恢复存储装饰器 | `RollbackFailed/WritesBlocked`，恢复文件保留，后续写入阻断 |
| 留存清理失败 | 真实只读包 | 备份成功 + `retention_delete_failed`，文件保留 |
| 备份目录不可用 | 真实文件占用路径 | `Failed(DestinationUnavailable)`，可重试，源库可用 |
| 旧/新写入竞争 | 真实并发协调器 | 一 `Saved` 一 `Conflict`，版本递增一次，无覆盖 |

## 后果

**正面**

- 失败语义在真实 SQLite/文件系统与真实协调器上得到系统验证，而非仅 mock 分支。
- 注入可重复、无需特权/环境配置；只读/锁/临时目录均清理。
- 生产改动仅一个可选连接钩子，默认行为不变。
- 覆盖“不报告假成功、不留半成品、不覆盖原数据、回退失败阻断写入”的核心不变量。

**负面 / 限制**

- 注入式磁盘满验证错误映射与清理，不等价于真实卷耗尽；真实卷/崩溃矩阵留后续。
- 只读属性与 `FileShare.None` 阻止 `File.Replace` 的行为依赖 Windows；目标平台 Windows 11 x64。
- 回退失败使用包装存储模拟切换歧义；真实 `File.Replace` 回退成功由 B5 集成测试覆盖。
- 并发测试接受任一写入者获胜，只断言恰好一个成功/一个冲突与无丢失更新。
- 不覆盖跨进程、WAL、进程崩溃、系统级 ACL 与 UI 行为。

## 备选方案

- **只做接口 mock：** 无法验证真实 SQLite 错误翻译、`File.Replace` 锁冲突与只读删除失败，拒绝。
- **真实耗尽磁盘卷/强杀进程：** 需要特权与环境编排，不可稳定重复，B6 拒绝；留 B7/运行环境。
- **为故障注入引入生产配置开关：** 扩大生产接口面，拒绝；使用测试侧连接钩子与装饰器。
- **等待删除失败时中断留存：** 会阻塞后续清理并可能误报备份失败，拒绝；逐文件警告并继续。
- **并发测试允许两个都成功：** 会掩盖丢失更新，拒绝。

## 验证

- `FaultInjectionSqliteTests`：SQLITE_FULL、只读库、并发更新竞争。
- `FaultInjectionBackupTests`：快照磁盘满、包磁盘满、取消、损坏包校验、目录不可用。
- `FaultInjectionRestoreTests`：staging 失败、切换锁定、回退失败阻断写入。
- `FaultInjectionDailyBackupTests`：留存删除失败、备份目录不可用。
- 全量测试/构建/漏洞审计见 B6 完成总结。

## 后续

- B7：UI 确认/恢复入口、设备设置/快捷键/显示器适配、首次运行恢复、崩溃遗留 staging 清理。
- 运行环境阶段：真实卷耗尽、ACL、进程崩溃/强杀、跨进程锁与 WAL 故障矩阵。
