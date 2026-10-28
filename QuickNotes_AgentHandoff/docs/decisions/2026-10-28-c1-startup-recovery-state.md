# ADR：C1 启动恢复状态、持久写入阻断与缺失库重建

- **日期：** 2026-10-28
- **状态：** 已接受（随 C1 实施记录）
- **范围：** C1 启动安全、跨重启阻断、恢复产物识别、缺失库重建
- **相关：** `docs/agent/Agent对接文档_C阶段.md` C1、PRD 10.3/11.6/11.7/11.8、B5 ADR、`docs/agent/C0-集成设计与风险.md`

## 背景

B5 的恢复只在当前库存在时工作，`BlockWrites()` 仅内存；重启/重建服务对象会丢失阻断。B5 不持久化恢复阶段，崩溃后无法从状态判断切换歧义。C1 必须在打开数据库后、进入正常使用前判断：首次运行、正常、需要恢复、写入阻断、只读诊断；并支持当前库缺失时从已验证备份安全重建。不得靠目录名/时间戳猜恢复来源，也不得自动建空库。

## 决策

1. **应用状态文件。** `application-state.json`（格式版本 1）记录：`installationId`、`initializationStatus`（Initializing/Ready）、`schemaVersion`、`writesBlocked`、`writeBlockReason`、`writeBlockedAtUtc`、`lastStartupUtc`、`recoverySourceSha256`、`recoveryProtectionSnapshotCreated`、`lastRecoveryAtUtc`。原子写、`Flush(true)`、临时文件切换；缺失/损坏/未知版本分别处理，不把损坏当空状态。
2. **首次初始化。** 只有状态缺失、数据库缺失、无草稿且无任何恢复/临时产物时，才进入首次初始化：先写 `Initializing`，创建 schema，校验数据库，再写 `Ready`。初始化失败不写 `Ready`；下次启动看到 `Initializing` 且无证据时进入只读诊断/显式重试，不自动重复建库。
3. **启动模式。** `FirstRunInitialized`、`Ready`、`NeedsRecovery`、`WriteBlocked`、`ReadOnlyDiagnostic`；原因区分数据库缺失/损坏/schema 不支持、状态缺失但库存在、状态损坏、初始化未完成、WAL sidecar、持久阻断、恢复日志未完成/损坏。只有 Ready 才准备当前库并进入正常使用；其他模式只读诊断。
4. **恢复日志。** `recovery-journal.json`（Application 契约，C1 读取；C4 在真实恢复阶段写入）：`restoreId`、`stage`（Staging/ProtectionCreated/Switching/Verifying/Rollback/Completed/Failed）、当前库/staging/保护/回退路径、起止时间。未完成阶段→NeedsRecovery；损坏/未知→ReadOnlyDiagnostic；保留相关路径，不自动清理。
5. **恢复产物策略。** 扫描 `.quicknotes-restore-*`、回退库、保护快照、`.quicknotes-tmp-*`、`.quicknotes-package-*.tmp`；C1 **只识别、默认全部保留**。清理必须证明归属、引用关系、当前库和恢复状态；C1 不自动删除，避免误删唯一恢复点；清理能力留 C6/C7/显式用户操作。
6. **持久写入阻断。** `PersistentDatabaseWriteGate` 包装 `DatabaseWriteGate`；`BlockWrites()` 先原子写 `writesBlocked=true` + 原因/时间，再置内存阻断。状态写失败记录错误但仍在内存阻断。启动读到阻断状态进入 `WriteBlocked`，不自动清除。解除只能通过显式恢复入口：选择已验证备份，隔离当前库，完整校验并发布，写 `Ready` 且清除阻断；重启本身永不解除。
7. **缺失库/阻断恢复。** 当前库缺失、损坏或写入阻断且用户选择已验证备份时：同卷 staging 提取 + B3 完整校验 + schema/counts/完整性/外键 → 当前库缺失用 `File.Move` 发布，当前库存在则隔离为 quarantine 后用 `File.Replace` 发布；发布后校验 SHA-256/manifest/schema/counts；成功后写 `Ready`、恢复来源哈希、`ProtectionSnapshotCreated=false`，并在阻断恢复时清除 `writesBlocked`。无当前库时记录“未创建保护快照”，不得伪造成功。失败保留 staging/quarantine，不报成功。
8. **不做。** C1 不自动选择恢复来源、不自动覆盖损坏库、不清理 staging/回退/保护、不清除阻断、不实现 schema v2/真实恢复 UI。

## 后果

**正面**

- 首次运行/缺失/损坏/未知/WAL/阻断/恢复日志有明确安全路径。
- 写入阻断跨重启可诊断，不会被重启自动解除。
- 缺失库重建不伪造保护快照，失败保留证据。
- 恢复产物保守保留，降低误删唯一恢复点风险。

**负面 / 限制**

- C1 会保留临时/恢复产物，磁盘可能增长；清理留后续显式策略。
- 缺失库重建与 B5 恢复是两条路径，C4 必须统一安全边界/结果语义。
- 恢复日志阶段写入在 C4 才接入 B5；C1 只能识别已有/模拟日志。
- 当前库损坏在 C1 只提供诊断与手动选择已验证备份，不自动修复。
- 无当前库重建没有保护快照，仅能依赖已验证备份与发布后校验。

## 备选方案

- **重启自动清除阻断：** 会绕过回退失败保护，拒绝。
- **状态缺失时自动新建空库：** 可能覆盖/掩盖数据丢失，拒绝。
- **按目录名/时间戳选恢复来源：** 不能证明一致性，拒绝。
- **启动自动删除 staging/tmp：** 可能删除回退/保护证据，拒绝。
- **缺失库重建也要求保护快照：** 无当前库可保护，伪造成功违反数据安全规则；拒绝。

## 验证

- 启动状态机测试：首次初始化、初始化失败、Ready、缺失/损坏/WAL/状态缺失/状态损坏/阻断/恢复日志未完成。
- 恢复日志测试：阶段往返、损坏/未知版本、未完成/已完成。
- 重建测试：有效包成功、损坏包失败、当前库存在拒绝、损坏库隔离、发布后校验失败不报成功、无保护快照记录。
- 持久阻断测试：跨新实例读取、拒绝写入、状态写失败仍内存阻断。
- 产物扫描测试：恢复目录/保护快照/临时文件保留。
- 全量结果见 `C1-测试报告.md`；人工/真实故障未运行项单独列出。

## 后续

- C4：将恢复日志接入 B5 RestoreCoordinator 各阶段；统一缺失库/普通恢复；恢复成功后旧编辑会话失效。
- C6：真实强杀/断电验证恢复日志与阻断跨重启；清理策略在证明安全后实现。
- C3：schema v2 迁移复用保护快照/启动阻断边界。
