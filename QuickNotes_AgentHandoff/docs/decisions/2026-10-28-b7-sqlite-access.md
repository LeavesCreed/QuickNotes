# ADR：B7 SQLite 访问方式汇总

- **日期：** 2026-10-28
- **状态：** 已接受（汇总 B1–B6 既有实现与决策）
- **范围：** 数据原型 SQLite 访问方式
- **相关：** [B2 ADR](2026-10-28-b2-bounded-busy-retry.md)、[B3 ADR](2026-10-28-b3-consistency-snapshot-and-package.md)、[B6 ADR](2026-10-28-b6-fault-injection-scope.md)、`docs/agent/B7-数据模型与备份格式.md`

## 背景

B1–B6 已确定 SQLite 的 schema、连接、事务、快照、恢复与故障注入实现，但访问方式散落在 B2（busy/超时/事务）、B3（不启用 WAL、只读快照）和代码约定中。B7 交付要求提供“SQLite 访问方式”ADR；本记录汇总既有决策，不改变 B1–B6 的行为。

## 决策

1. **驱动与数据访问。** 使用 `Microsoft.Data.Sqlite` 10.0.0 + `SQLitePCLRaw.lib.e_sqlite3` 2.1.12；仓储使用手写 SQL/ADO.NET，不引入 EF Core 或其它 ORM。数据层不依赖 WPF。
2. **连接生命周期。** 每次操作打开独立连接，连接字符串 `Pooling=false`；不共享长生命周期连接。正常读写使用 `ReadWriteCreate`；变更检测、快照源、恢复检查使用 `ReadOnly`；快照/恢复目标要求新文件并检查 sidecar。
3. **连接级 PRAGMA。** 通过 `SqliteConnectionFactory` 打开的连接执行 `PRAGMA foreign_keys=ON; PRAGMA busy_timeout=250;`；快照/校验/变更检测等直接打开的只读或目标连接单独设置 `busy_timeout`，目标验证执行 `integrity_check`、`foreign_key_check` 与 `user_version` 检查。外键由写入连接强制。
4. **超时与重试。** 连接字符串 `DefaultTimeout = max(1, ceil(busy_timeout/1000))`（默认 1 秒），限制 Microsoft.Data.Sqlite 对 prepare/step/隐式 BEGIN/COMMIT 的内部重试；保存协调器另做最多 3 次 BUSY/LOCKED 应用层尝试、50ms/150ms 退避。提交阶段不重跑整次保存，提交未知回读核验。
5. **事务。** 写事务使用 deferred `BEGIN`，隔离级别 `Serializable`；更新/状态变更带 `expectedVersion` 条件，成功后版本 `+1`。不使用 `BEGIN IMMEDIATE`，避免锁等待消耗在事务开始阶段。
6. **日志模式。** 使用 SQLite rollback journal，**不启用 WAL**。备份使用 SQLite Online Backup API；当前库存在 `-wal`/`-shm` 时恢复拒绝；快照/切换检查 `-journal`/`-wal`/`-shm` sidecar。
7. **错误语义。** SQLite 错误翻译为类型化异常：BUSY/LOCKED 可重试；FULL/READONLY/IO/CORRUPT/CANTOPEN/NOTADB 不可重试；约束冲突单独分类；取消导致的 INTERRUPT 转为 `OperationCanceledException`。协调器只在安全范围重试。
8. **测试接缝。** `SqliteConnectionFactory` 可选 `configureConnection` 钩子仅用于连接级配置/故障注入（如 `PRAGMA max_page_count`），默认 `null`；业务层不依赖该钩子。
9. **单进程假设。** 当前无跨进程锁；保存与恢复通过进程内 `DatabaseWriteGate` 串行化。多进程/WAL 需另行决策。

## 后果

**正面**

- 访问方式、超时、事务与错误语义一致，保存/备份/恢复共享同一套连接约定。
- 不引入 ORM 与 WAL 的额外生命周期，原型范围可控。
- 只读连接与 sidecar 检查降低备份/恢复读写竞争与半写风险。

**负面 / 限制**

- 手写 SQL 重复度高于 ORM；schema 变更需同步仓储/测试。
- 单次保存最坏约 3.2–3.4 秒（受驱动内部重试影响），不是 950ms。
- 无 WAL，读写并发仍可能 BUSY；单进程假设限制多实例。
- 无跨进程锁；崩溃可能遗留临时文件/staging。

## 备选方案

- **EF Core/ORM：** 增加映射与迁移抽象，原型阶段对 SQL/锁行为控制不足；拒绝。
- **共享连接或连接池：** 锁/事务边界不清晰，易产生不可预测的 BUSY；拒绝。
- **启用 WAL：** 可改善并发，但增加 sidecar/备份/恢复复杂度；当前不启用，留后续评估。
- **直接 SQLitePCLRaw 原生调用：** 可精确控制 step/busy handler，但扩大范围与风险；拒绝。
- **复制主 `.db` 文件当备份：** 不能保证一致快照；拒绝（见 B3 ADR）。

## 验证

- `SqliteNoteRepositoryTests`、`SqliteErrorHandlingTests`：CRUD、外键、版本冲突、错误翻译、只读/BUSY。
- `SqliteSnapshotProviderTests`、`ZipBackupPackageStoreTests`、`BackupCoordinatorIntegrationTests`：只读源、在线快照、包校验。
- `SqliteRestoreStoreTests`、`RestoreIntegrationTests`：WAL 拒绝、`File.Replace` 切换/回退、恢复校验。
- `FaultInjectionSqliteTests`：`max_page_count` 磁盘满、只读库、并发更新竞争。
- 全量结果见 [B7 测试报告](../agent/B7-测试报告.md)。

## 后续

- C 阶段：宿主/UI 接入、设置持久化、首次运行、schema v2 迁移。
- 运行环境阶段：WAL/多实例评估、跨进程锁、真实卷耗尽/ACL/崩溃故障矩阵。
