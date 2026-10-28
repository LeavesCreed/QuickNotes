# ADR：B5 整库恢复、保护快照与回退

- **日期：** 2026-10-28
- **状态：** 已接受（随 B5 实施记录）
- **范围：** B5 整库恢复与回退
- **相关：** `docs/superpowers/plans/2026-10-28-b5-restore-rollback.md`、PRD 11.6/11.7/11.8、Agent 对接文档 B5、B2/B3/B4 记录

## 背景

B3 提供一致性备份包与完整校验，B4 提供自动备份与留存。B5 需要把备份包安全地整库恢复到当前数据库：恢复前预检、临时位置准备与校验、切换前保护快照、原子切换、失败回退、回退失败停止写入，并保留草稿与设备设置。

B2 的保存没有与恢复共享写入门；恢复期间若有保存在进行，切换可能与提交竞争。B5 必须补齐这个接缝。

## 决策

1. **共享写入门。** 新增 `IDatabaseWriteGate`，保存（B2 `NoteSaveCoordinator`）与恢复共享同一个串行门。保存在整个保存操作期间持有写租约；恢复持有独占恢复租约。回退失败调用 `BlockWrites()`，后续保存返回 `StorageUnavailable` + `database_writes_blocked`，不再抛非预期异常。`NoopDatabaseWriteGate` 仅作为 B2 默认兼容；`RestoreCoordinator` 拒绝 Noop 门，必须使用真实串行门。
2. **预检。** 包存在/可读；B3 extractor 完整校验 ZIP 条目、manifest、SHA-256/大小、数据库完整性/外键/schema；备份与当前 schema 必须等于 **1**（B5 不做迁移）；当前库存在且可读；当前库无 `-wal`/`-shm`；备份目录存在且可写；空间满足 staging + 回退副本 + 保护快照；草稿可检查。
3. **草稿。** 草稿不进入备份包，恢复不触碰草稿目录。预检列出草稿；存在草稿且 `AllowPendingDrafts=false` 时阻止；调用方显式允许后继续，草稿保留。草稿检查失败阻止恢复。
4. **staging。** 在当前数据库同卷创建 `.quicknotes-restore-<guid>` 临时目录；包 DB 提取为 `restored.db`；校验 counts/schema；回退副本 `rollback.db` 也放在该目录。`File.Replace` 原子切换并生成回退副本；不直接复制覆盖。
5. **保护快照。** 切换前在备份目录创建 B3 `Manual` 保护快照；必须成功且通过 B3 校验才切换。保护快照无论后续成功/失败都保留（有效恢复点）。
6. **切换与校验。** 切换前再次检查取消；`File.Replace` 使用 `CancellationToken.None`；切换后重新打开当前库，校验 integrity/外键/schema/counts 与 staged 一致。切换后不响应取消，完成校验/回退，返回真实结果。
7. **回退。** 切换/校验失败用 `File.Replace(rollback, current, failed)` 回退；回退后校验 counts/schema 回到旧库。回退成功结果为失败但 `RolledBack=true`；回退失败 `WritesBlocked=true`，保留保护快照/回退库/staging，并给出恢复提示。
8. **当前库准备。** 恢复门内用读写连接打开当前库，恢复热 journal；若 `-wal`/`-shm` 存在则拒绝（B5 不启用 WAL）；若 journal 无法恢复或当前库不可用则中止，不切换。
9. **schema。** B5 只支持 schema v1；不兼容版本拒绝，不做迁移、不半迁移。PRD 11.8 的升级迁移保护留后续阶段。
10. **不修改设备/状态。** 恢复不修改 `.quicknotes-backup-state.json`、草稿、备份目录设置；包不包含设备设置，因此备份目录/开机启动/显示器标识不会被覆盖。快捷键/启动/显示器的完整适配留 B7。
11. **错误与结果。** 所有失败不报告成功；`RestoreStatus` 区分 Preflight/Verification/Protection/Switch/Rollback 失败；`RestoreError` 含稳定错误码/诊断；日志只用文件名与错误码，不记录便签内容或完整用户路径（恢复提示除外）。
12. **范围。** 不实现跨备份合并、WAL、B4 留存变更、UI/确认界面或真实系统级故障注入（B6）；不改 B3 包格式。

## 恢复流程

```text
预检（只读；包完整校验、schema、当前库、空间、目录、草稿）
  -> 获取恢复写入门（等待在途保存）
  -> PrepareCurrent（读写打开恢复热 journal；拒绝 WAL sidecar）
  -> B3 Manual 保护快照（验证成功）
  -> 同卷 staging：提取包 DB + 完整校验 + counts
  -> 切换前取消检查
  -> File.Replace(staged, current, rollback)
  -> 检查当前库并对比 staged counts
  -> 成功：清理 staging/回退副本；保留保护快照；释放门
  -> 失败：File.Replace(rollback, current, failed) 回退
       -> 回退成功：失败结果 + RolledBack=true
       -> 回退失败：BlockWrites + WritesBlocked=true，保留保护/回退/staging
```

## 后果

**正面**

- 恢复与保存互斥，避免切换与提交竞争。
- 预检在修改当前库前拒绝损坏/不兼容包；保护快照提供恢复点。
- 原子切换与回退副本；回退失败阻断后续写入，避免继续破坏。
- 草稿与设备设置不被恢复覆盖；状态文件保留，B4 下次检查会检测数据变化。
- 无 schema/包格式变更，复用 B3 校验/B4 备份。

**负面 / 限制**

- 当前库必须存在；首次运行恢复留后续。
- 恢复门会阻塞保存（含 staging/保护），大库可能较长时间显示“恢复中”。
- `File.Replace` 依赖同卷与平台；staging/回退副本必须同卷。
- 只校验 counts/schema/完整性/外键，不逐字段比较全部内容（staged DB 字节即包内 DB，已通过哈希）。
- 单进程写入门；无跨进程恢复锁。
- 真实磁盘满/权限/崩溃/回退失败注入留 B6；B5 用可注入失败。
- 无 UI 确认界面；预检结果供未来 UI 使用。
- B5 不迁移 schema；不兼容版本拒绝。

## 备选方案

- **直接复制包 DB 覆盖当前库：** 无原子性/回退/保护快照，拒绝。
- **先删除当前库再移动：** 中断窗口会失去当前库，拒绝。
- **不共享写入门：** 切换与保存可能竞争，拒绝。
- **草稿随恢复覆盖/删除：** 违反“不得静默丢弃未保存正文”，拒绝。
- **回退失败继续写入：** 可能破坏唯一恢复点，拒绝。
- **B5 内做 schema 迁移：** 需要完整升级保护与迁移测试，超出 B5；拒绝。

## 验证

- `RestoreCoordinatorTests`：预检全部分支、成功、保护失败、staging 失败、切换失败旧库不变、切换后校验失败回退、回退失败阻断、取消、写入门阻断、构造函数约束。
- `DatabaseWriteGateTests`：保存等待恢复、回退失败后保存被阻断。
- `SqliteRestoreStoreTests`：counts/schema、WAL 拒绝、真实 `File.Replace` 切换/回退、缺失/损坏。
- `RestoreIntegrationTests`：真实包/DB 端到端、草稿阻止/允许/保留、保护快照校验、损坏包与 schema 不兼容当前库不变。
- 全量测试/构建/漏洞审计见 B5 完成总结。

## 后续

- B6：真实磁盘满/权限/崩溃、恢复中断、回退失败注入。
- B7：UI 确认界面、设备设置/快捷键/显示器适配、恢复入口、首次运行恢复。
- 未来 schema 升级：恢复/迁移前保护快照、半迁移拒绝、迁移测试。
