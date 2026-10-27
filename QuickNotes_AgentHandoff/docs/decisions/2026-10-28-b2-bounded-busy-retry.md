# ADR：B2 有界 busy 重试改用驱动的秒级 CommandTimeout

- **日期：** 2026-10-28
- **状态：** 已接受（用户确认）
- **范围：** B2 保存协调与失败语义
- **相关规格：** `docs/superpowers/specs/2026-10-27-b2-save-coordination-design.md` 第 6、7、14 节

## 背景

B2 规格要求：连接级 `busy_timeout=250ms`；最多 3 次尝试；退避 50ms、150ms；最坏约 950ms；只重试 BUSY/LOCKED；提交阶段不重试。

实现验证发现 Microsoft.Data.Sqlite 10 会在更底层自动重试：

- `SqliteCommand` 在 `sqlite3_prepare_v2` 返回 BUSY/LOCKED 时按 `CommandTimeout` 重试；
- `SqliteDataReader` 在 `sqlite3_step` 返回 BUSY/LOCKED 时按 `CommandTimeout` 重试；
- `SqliteConnectionInternal` 对隐式命令（BEGIN/COMMIT）按 `DefaultTimeout` 重试。

默认超时为 30 秒，且单位为整秒（最小非零值 1 秒）；`CommandTimeout=0` 表示无限等待。仅设置 `PRAGMA busy_timeout=250ms` 不能阻止这些上层重试。实测持续占用写锁时，单次保存尝试可阻塞约 30 秒，3 次尝试约 90 秒，违反“有界、可诊断”的目标。

## 决策

1. `SqliteConnectionFactory` 将连接字符串 `DefaultTimeout` 设为 `max(1, ceil(busyTimeoutMilliseconds / 1000))`，默认 1 秒；命令继承该值，隐式 BEGIN/COMMIT 也受其约束。
2. 保留连接打开后的 `PRAGMA foreign_keys=ON; PRAGMA busy_timeout=<ms>;`；默认 `busy_timeout=250ms` 仍是 SQLite 层的等待上限。
3. 仓储写事务显式使用 deferred（`BEGIN;`），避免 B1 默认 `BEGIN IMMEDIATE` 把锁等待消耗在事务开始阶段；锁冲突落在 INSERT/UPDATE 的 step，由 `busy_timeout` 与驱动的 1 秒上限共同约束。
4. 协调器仍按规格执行最多 3 次尝试、50ms/150ms 退避，只重试 BUSY/LOCKED；提交失败仍包装为 `SaveOutcomeUnknownException` 并回读核验。
5. 接受由此产生的实际时限偏差：单次尝试约 1 秒，3 次加退避最坏约 3.2–3.4 秒，而非约 950 毫秒。

## 后果

**正面**

- 单次尝试从约 30 秒降到约 1 秒，保存失败在可接受时间内返回。
- 不误报成功：提交未知仍走回读核验；非 BUSY/LOCKED 不自动重试；重试耗尽保留草稿。
- 不需要绕过 Microsoft.Data.Sqlite；改动局限在连接配置与事务模式。

**负面 / 限制**

- 无法满足规格中“最坏约 950ms”的字面数值；UI/日志需按约 3.4 秒上限设计“保存中”状态。
- 驱动仍会在 COMMIT 上内部重试最多约 1 秒；协调器不会重跑整次保存，但“提交阶段完全不重试”只在应用层成立。
- `busy_timeout` 参数与命令超时语义不再一一对应；参数名保留为连接级忙等上限。

## 备选方案

- **维持默认 30 秒：** 不可接受，单次可阻塞 30 秒。
- **只设置 `PRAGMA busy_timeout`：** 无效，驱动内部重试优先。
- **将 `DefaultTimeout` 设为负值以绕过驱动重试：** 依赖未公开行为，驱动升级可能失效，拒绝。
- **用 SQLitePCLRaw 原生 step / 自定义 busy handler 绕过驱动：** 可精确达到 950ms，但显著扩大范围与风险，留待后续决策。

## 验证

- 集成测试 `Create_WhenLockRemainsHeld_ExhaustsRetriesAndRetainsDraft`：持续占用锁时 3 次尝试约 3 秒后返回 `StorageUnavailable`，草稿保留。
- 集成测试 `Create_WhenLockReleasedBeforeRetry_SucceedsAfterBusy`：第二次尝试信号驱动，锁释放后成功，`Attempts=2`。
- 集成测试断言默认 `PRAGMA busy_timeout=250` 与命令 `CommandTimeout=1`。

## 后续

如果未来要求严格守住约 950ms，需要单独评估绕过 Microsoft.Data.Sqlite 内部重试的方案，并重新审视保存 UI 的时延预算；在此之前，以本 ADR 的有界行为为准。
