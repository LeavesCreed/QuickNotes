# B5 整库恢复与回退 — 实施计划

> **状态：** 已完成（实现、测试与审查）；见 `docs/agent/B5-完成总结.md`\
> **日期：** 2026-10-28\
> **阶段：** B5\
> **基线：** `main` / `6482faa`（B4 已合并）\
> **上游依据：** PRD 11.6、11.7、11.8；Agent 对接文档 B5；B3/B4 ADR 与完成总结

## 1. 目标

把 B3 一致性备份包安全地整库恢复到当前数据库：

- 恢复前完整预检（包/校验/schema/结构/空间/权限/草稿/写入任务）。
- 在临时位置准备并校验恢复库。
- 切换前为当前库创建保护快照。
- 原子切换；切换/校验失败回退；回退失败停止进一步写入并保留原库、回退库和保护快照。
- 恢复成功后数据/关联/状态一致；不报告假成功；不跨备份合并；不覆盖设备专属设置。

## 2. 范围

**包含**

- 应用层恢复协调器、预检/结果/错误契约
- 共享写入门（保存与恢复互斥）
- 备份包提取到 staging 并完整校验
- SQLite 恢复存储：检查、准备、原子切换、回退
- 当前库保护快照（复用 B3 Manual 备份）
- 草稿检查与保留；待处理草稿默认阻止，显式允许
- 空间/权限/源/目标失败、schema 不兼容、切换/校验失败、回退失败、取消
- 单元、SQLite 集成、端到端测试

**不包含**

- UI/确认界面、设备设置/快捷键/显示器/开机启动模型（B7）
- schema 迁移/升级（B5 仅支持 schema v1；不兼容版本拒绝）
- 跨备份合并、导出、留存变更、WAL
- 真实磁盘满/权限/崩溃的系统级故障注入（B6）
- 物理删除便签、Rust FFI

## 3. 技术决策

| 编号 | 决策 |
|---|---|
| D1 | 恢复与保存共享 `IDatabaseWriteGate`；恢复独占，保存等待；回退失败调用 `BlockWrites`，后续保存抛类型化错误 |
| D2 | staging 目录放在当前数据库同卷的隐藏临时目录；`File.Replace` 原子切换并生成回退副本；不直接复制/覆盖 |
| D3 | 保护快照使用 B3 `BackupCoordinator(Manual)` 写入备份目录；保护快照失败则不切换 |
| D4 | 预检先完整验证包（B3 extractor：条目/manifest/哈希/DB 完整性/外键/schema），再检查 schema 兼容、当前库可读、空间、备份目录写权限、草稿 |
| D5 | B5 只支持 schema v1；备份/当前 schema 不等于 1 时拒绝，不做迁移；升级保护留后续 |
| D6 | 草稿不进入备份包；预检列出草稿；有草稿且 `AllowPendingDrafts=false` 时阻止；允许后恢复不触碰草稿 |
| D7 | 当前库存在 `-wal`/`-shm` 时拒绝（B5 不启用 WAL）；热 `-journal` 在恢复门内用读写连接恢复；无写入者 |
| D8 | 恢复门内依次：准备当前库 → 保护快照 → staging 提取/校验 → 原子切换 → 切换后校验；切换后使用 `CancellationToken.None`，不因随后取消谎报失败 |
| D9 | 切换/校验失败回退到回退副本；回退成功结果仍为失败但 `RolledBack=true`；回退失败 `WritesBlocked=true` 并保留保护快照/回退库/staging |
| D10 | 恢复不修改 `.quicknotes-backup-state.json`、草稿、备份目录设置、快捷键/启动/显示器设置；这些不在 B5 数据模型中或必须保留 |
| D11 | 所有失败不报告成功；错误码/诊断不含便签内容；日志只用文件名与错误码，不记录完整用户路径 |
| D12 | 当前库不存在时不执行恢复（无保护对象）；首次运行恢复留后续 |

## 4. 架构

```text
Application
  ├─ RestoreCoordinator（预检/恢复编排）
  ├─ IDatabaseWriteGate / DatabaseWriteGate（保存与恢复互斥）
  ├─ IBackupPackageExtractor（Application 契约）
  ├─ IRestoreStore（Application 契约）
  └─ IDiskSpaceProvider

Infrastructure.Backup
  ├─ BackupPackageVerifier.ExtractAsync（staging 提取 + 完整校验）
  └─ DriveInfoDiskSpaceProvider

Infrastructure.Sqlite
  └─ SqliteRestoreStore（检查/准备/File.Replace 切换/回退）

QuickNotes.Application
  └─ NoteSaveCoordinator 接入共享写入门
```

## 5. 预检

- 请求路径非空；包存在；当前库存在；备份目录存在且可写。
- 当前库：只读检查（integrity/外键/schema/counts）；schema 必须为 1；存在 `-wal`/`-shm` 拒绝。
- 备份包：临时目录提取 → B3 校验（ZIP 条目/manifest/哈希/大小/DB 完整性/外键/schema）→ 读取 counts；manifest schema 必须为 1；包格式版本由 B3 校验。
- 空间：当前卷需要 staging 库（manifest 数据库大小）+ 回退副本（当前库大小）+ 安全余量；备份目录卷需要保护快照空间（以 manifest 数据库大小为上界）；不足 → `InsufficientSpace`。
- 草稿：`INoteDraftStore.ListAsync(null)`；读取失败 → `DraftCheckFailed`；有草稿且未允许 → `PendingDrafts`；允许则记录数量。
- `CanProceed`、manifest、备份 counts、当前 counts、草稿数、required/available。

## 6. 恢复流程

```text
预检（不修改当前库）
  -> 获取恢复写入门（等待在途保存）
  -> PrepareCurrent（读写打开恢复热 journal；拒绝 WAL sidecar；检查当前库）
  -> B3 Manual 保护快照（验证成功）
  -> 当前库同卷 staging 目录：提取包 DB + 完整校验 + counts
  -> 切换前取消检查
  -> File.Replace(staged, current, rollback)
  -> 检查当前库（integrity/外键/schema/counts）并与 staged counts 对比
  -> 成功：清理 staging/回退副本；保留保护快照；释放门
  -> 失败：File.Replace(rollback, current, failed) 回退并检查旧 counts
       -> 回退成功：失败结果 + RolledBack=true
       -> 回退失败：BlockWrites + WritesBlocked=true，保留保护/回退/staging，给出恢复提示
```

- 切换前取消 → 清理 staging、保留保护快照、抛 `OperationCanceledException`。
- 切换后不响应取消，完成校验/回退，返回真实结果。
- 保护快照无论后续成功/失败都保留（有效恢复点）。

## 7. 结果与错误

- `RestoreStatus`：Succeeded / PreflightFailed / VerificationFailed / ProtectionFailed / SwitchFailed / RollbackFailed。
- `RestoreErrorCode`：PackageNotFound、PackageInvalid、SchemaIncompatible、CurrentDatabaseUnavailable、DestinationUnavailable、StagingUnavailable、InsufficientSpace、PendingDrafts、DraftCheckFailed、ProtectionSnapshotFailed、SwitchFailed、PostRestoreVerificationFailed、RollbackFailed、WritesBlocked、ValidationFailed。
- `RestoreResult`：manifest、restored counts、保护快照路径、error、RolledBack、WritesBlocked、RecoveryHint、duration。
- 保存门被永久阻断时，`NoteSaveCoordinator` 返回 `StorageUnavailable` + `database_writes_blocked`，不抛非预期异常。

## 8. 测试

- 单元（替身）：预检成功/包无效/schema 不兼容/当前库缺失/备份目录缺失/空间不足/草稿阻止/草稿检查失败；恢复成功、保护失败、staging 失败、切换失败当前库不变、切换后校验失败回退成功、回退失败阻断写、取消、非预期异常、日志不含内容/完整路径。
- 写入门：保存与恢复互斥；恢复期间保存等待；回退失败后保存被阻断。
- SQLite 集成：检查 counts/外键/schema；准备恢复热 journal/拒绝 WAL；真实 `File.Replace` 切换/回退；中断后当前库可打开。
- 端到端：真实 DB A/备份 B、恢复后数据/关联/状态正确；保护快照可校验；草稿保留；包损坏当前库不变；schema 不兼容拒绝；保存门行为。
- 验证命令同前；全部测试/构建/漏洞审计通过。

## 9. 验收

- 损坏/校验失败备份被拒绝，当前库不变。
- 恢复成功数据/关联/状态一致；保护快照存在且可校验。
- 切换/校验失败回退成功，当前库回到旧数据；回退失败阻断后续写入并保留恢复文件。
- 待处理草稿默认阻止，允许后草稿保留。
- 不报告假成功；不修改设备设置/状态文件；不实现跨备份合并。
- 不启用 WAL/不改 schema/不改变 B3 包格式。

## 10. 风险与限制

- B5 只支持 schema v1；升级迁移/保护快照规则留后续。
- 无 UI/设置模型；设备专属设置不被覆盖只是“没有写入”，不是完整恢复适配。
- 系统时钟/路径/权限/磁盘满的真实故障注入留 B6；B5 用可注入失败。
- `File.Replace` 的原子性依赖同卷与平台；staging 与回退副本必须同卷。
- 单进程/单实例假设；写入门只在进程内有效。
- 回退失败后需要人工按恢复提示处理；B5 不自动修复。
- 恢复期间保存被阻塞；未来 UI 需显示“恢复中”。
