# ADR：B3 一致性快照与备份包格式

- **日期：** 2026-10-28
- **状态：** 已接受（用户批准 D1–D10）
- **范围：** B3 一致性备份快照
- **相关：** `docs/superpowers/plans/2026-10-28-b3-consistency-backup-snapshot.md`、PRD 11.2/11.4/11.5/11.6、B2 设计第 15 节

## 背景

PRD 要求备份基于一致的数据快照，不得仅复制正在使用的 SQLite 主文件；备份必须包含完整数据、格式/schema 版本与完整性校验信息，并且只有在临时文件校验成功后才能成为正式备份。B2 设计把 WAL、快照方式和 `-wal`/`-shm` 处理留给 B3 决定。

## 决策

1. **D1 快照机制：SQLite Online Backup API。** 使用 `SqliteConnection.BackupDatabase` 把源库复制到独立临时 `.db`；源库以只读连接打开，复制期间允许继续读写。目标文件是独立 SQLite 数据库，不包含 `-wal`/`-shm`。目标路径必须是新文件：若目标或 `-journal`/`-wal`/`-shm` 已存在，直接失败且不删除既有文件，避免误毁有效数据。
2. **D2 B3 不启用 WAL。** 在线备份 API 在 rollback journal 模式下已能提供一致快照；WAL 会引入 `-wal`/`-shm` 生命周期、备份/恢复与留存复杂度，当前单用户原型的并发收益不足。未来若启用 WAL，必须重新评估备份、恢复与留存策略并更新本 ADR。
3. **D3 备份包格式：ZIP，固定两个条目。** `notes.db` + `manifest.json`；写入端只产生这两个条目，校验端拒绝额外条目、重复条目、绝对路径、`..` 与反斜杠，为 B5 解压安全打基础。
4. **D4 校验：SHA-256。** manifest 记录数据库文件字节数与小写十六进制 SHA-256；包写入后重新打开 ZIP、读取 manifest、校验条目/大小/哈希。
5. **D5 临时文件 + 原子发布。** 在备份目录写临时 ZIP，完整校验后 `File.Move` 到最终名；不覆盖已有包，命名冲突追加随机后缀。发布前再次检查取消；发布后视为操作完成，不因随后取消谎报失败。
6. **D6 双重校验与大小上限。** 快照阶段执行 `integrity_check`、`foreign_key_check`、`user_version`；包阶段重新打开 ZIP、校验 manifest/哈希/大小，并解压数据库再次执行 `integrity_check`、`foreign_key_check`、schema 版本比对。校验器默认限制解压数据库不超过 4 GiB（可配置），超过上限在解压前/解压中失败，降低压缩炸弹耗盘风险；B5 还需结合可用空间做预检。
7. **D7 故障注入范围。** B3 使用可注入的快照/包存储失败与取消测试；真实磁盘满、权限拒绝、系统崩溃留 B6。临时文件清理失败通过可选 logger 记录 Warning（仅文件类型/异常类型），不改变主结果。
8. **D8 备份目录。** 调用方必须提供已存在且可写的目录；协调器只在其下创建临时工作目录，不修改用户设置。
9. **D9 时间记录。** manifest 同时记录 `createdAtUtc` 与 `createdAtLocal`（含本地偏移），两者必须是同一时间点；B4 可据此做本地日/周判定。
10. **D10 备份类型。** B3 只生成 `Manual`；枚举预留 `Automatic`/`Daily`/`Weekly`。协调器拒绝 B3 未实现的类型。
11. **D11 备份内容。** B3 只备份 SQLite 数据库（正常/归档/回收站便签、分类、标签、关联）；草稿、窗口布局与设备设置不进入包。B5 恢复必须保留未保存草稿；B7 再补齐设置/草稿策略。
12. **D12 结果契约。** 在计划草案的 `BackupStatus` 上增加 `ValidationFailed`，用于空目录/不支持的备份类型；避免把输入错误伪装成存储错误。

## 包格式

```text
quicknotes-YYYYMMDD-HHmmss-fff-manual.zip
├─ manifest.json
└─ notes.db
```

```json
{
  "formatVersion": 1,
  "backupType": "Manual",
  "createdAtUtc": "2026-10-28T09:30:00.0000000+00:00",
  "createdAtLocal": "2026-10-28T17:30:00.0000000+08:00",
  "schemaVersion": 1,
  "databaseFileName": "notes.db",
  "databaseSizeBytes": 123456,
  "databaseSha256": "64位小写十六进制"
}
```

- 格式版本从 1 开始；未知版本拒绝。
- 最终文件名使用 UTC；冲突追加 `-<4位随机>`。
- ZIP 不加密；最终目录与权限策略不在 B3 决定。

## 校验流程

```text
源库（只读） --BackupDatabase--> 临时 notes.db
  -> integrity_check / foreign_key_check / user_version
  -> 计算大小与 SHA-256，写 manifest
  -> 写临时 ZIP（manifest.json + notes.db）
  -> 重新打开 ZIP：条目白名单、重复检查、manifest 严格校验、哈希/大小
  -> 解压数据库：integrity_check / foreign_key_check / schema 版本
  -> 原子发布为最终 ZIP
  -> 清理临时目录/临时 ZIP/临时解压文件
```

失败/取消时最终包不存在，已有备份不被覆盖，源库保持可用；取消在边界生效（驱动在线备份 API 不支持中途取消）。

## 后果

**正面**

- 不必停止应用即可获得一致快照；不依赖复制主 `.db` 文件。
- 包可独立校验，B5 可复用 verifier；manifest 为 B4 留存与 B5 恢复提供稳定字段。
- 不引入 WAL 的额外文件生命周期；B3 范围保持可控。

**负面 / 限制**

- 备份耗时随数据库增长；ZIP 压缩增加 CPU；B3 无进度 UI，只记录耗时。
- 在线备份 API 同步且无中途取消；取消只保证不发布半成品。
- 校验需要额外解压磁盘空间与时间。
- 包未加密；单进程假设；无跨进程备份锁。
- 不包含设置、窗口布局、草稿；B5/B7 需要补齐或保留策略。
- 真实磁盘满/权限/崩溃未在 B3 注入；B6 覆盖。

## 备选方案

- **直接复制正在使用的主 `.db`：** 不能保证一致快照，拒绝。
- **`VACUUM INTO`：** 也能产生一致快照，但需要拼接 SQL 路径，控制点较少；在线备份 API 更直接。
- **启用 WAL：** 可改善读写并发，但增加 `-wal`/`-shm` 生命周期与备份/恢复复杂度；当前无必要。
- **目录型备份包：** 简单但不便跨设备搬运；ZIP 更符合备份包与 B5 归档检查的方向。

## 后续

- B4：使用 manifest 的 UTC/本地时间与类型实现每日备份、7 日 + 4 周留存；周规则需单独 ADR。
- B5：恢复前重新执行完整预检，不信任“曾经校验过”；恢复前创建保护快照并保留草稿目录。
- B6：真实系统级故障注入。
- 若未来启用 WAL，需重新评估本 ADR 的快照与恢复策略。

## 验证

- `QuickNotes.IntegrationTests.SqliteSnapshotProviderTests`：内容/生命周期/标签/schema、并发写入、源库可用、取消、缺失源、目标目录、目标已存在保护、`user_version=0`。
- `QuickNotes.IntegrationTests.ZipBackupPackageStoreTests`：包格式、manifest/哈希、额外条目/重复条目/路径穿越/哈希不符/截断 ZIP/schema 不符/解压大小上限、目标不覆盖、校验失败不发布、清理。
- `QuickNotes.IntegrationTests.BackupCoordinatorIntegrationTests`：端到端、空库、并发写入、快照/包失败清理、取消。
- 全量测试、构建与漏洞审计见 B3 完成总结。
