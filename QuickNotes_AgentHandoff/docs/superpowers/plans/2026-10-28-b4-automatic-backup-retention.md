# B4 自动备份与留存 — 实施计划

> **状态：** 已完成（实现、测试与审查）；见 `docs/agent/B4-完成总结.md`\
> **日期：** 2026-10-28\
> **阶段：** B4\
> **基线：** `main` / `4d30816`（B3 已合并）\
> **上游依据：** `docs/product/PRD_v1.0.md` 11.2、11.3、11.4、11.5；`docs/agent/Agent对接文档_B阶段.md` B4；B3 完成总结与 ADR

## 1. 目标

在 B3 一致性备份包之上实现自动日备份与留存：

- 每日有数据变化时创建一份自动日备份；当日成功一份后不再重复。
- 无数据变化时不生成不必要的日备份。
- 备份失败不算当天成功，后续检查可重试。
- 成功备份后执行留存：最近 7 份日备份 + 4 个周代表备份；手动备份不参与自动清理。
- 软件跨越日期、重启、备份目录不可用、状态损坏时行为安全、可诊断。
- 不改变 B3 备份包格式，不启用 WAL，不做 schema 变更。

## 2. 产品规则（不可更改）

- 每日有数据变化时备份。
- 保留 7 份日备份和 4 份周备份。
- 手动备份默认不参与自动清理。
- 当日无变化时不生成不必要的重复日备份。
- 备份失败不算当天成功。
- 新备份通过校验后才可清理超额自动备份。
- 同一物理文件兼任日/周备份时不得重复计数。
- 不实现跨备份合并、恢复（B5）或 UI。

## 3. 技术决策

| 编号 | 决策 | 说明 |
|---|---|---|
| D1 | 自动日备份使用 `BackupType.Daily` | `BackupCoordinator` 从 B3 的“仅 Manual”扩展为接受 Manual + Daily；文件名 `-daily.zip`；`Automatic`/`Weekly` 保留 |
| D2 | 变更检测：SQL 指纹 + 状态文件 | 指纹包含 schema 版本、notes/note_tags/categories/tags 计数、标签/分类名称长度和、`MAX(notes.modified_at_utc)`；成功后把备份前指纹写入备份目录的 `.quicknotes-backup-state.json`；缺失/损坏状态按“需要备份”保守处理 |
| D3 | 调度：宿主无关的 `DailyBackupService` | `RunCheckAsync` 单次检查；`RunPeriodicAsync(interval)` 立即检查后按间隔循环；推荐 15 分钟；启动时由宿主立即调用；每次检查按本机当前本地日期判断，不为停机期间补造多天 |
| D4 | 留存：7 个本地日期 + 4 个 ISO 周代表 | 本地日历日、ISO 周（周一开始）；每个日期保留最新有效日备份；最近 4 个有有效日备份的不同 ISO 周各保留该周最新日备份；同文件兼作日/周只算一次；周代表复用日备份文件，不复制 |
| D5 | 留存安全 | 只删除“校验有效的自动日备份”中不在保留集的包；手动、无效、未知类型不自动删除；仅在新日备份成功且通过 B3 校验后执行；新包始终保护；删除失败只记录警告 |
| D6 | 目录扫描分两档 | 调度检查只读 manifest/条目元数据；留存清理校验 DB 条目大小与 SHA-256（不解压 SQLite；创建时 B3 已做完整校验） |
| D7 | 状态文件 | `.quicknotes-backup-state.json` 原子写、非备份包、留存扫描忽略；损坏/缺失视为无基线，下一次检查备份（安全优先） |
| D8 | 时间与时区 | 用 manifest 记录的 `CreatedAtLocal` 判定本地日期/ISO 周；检查时用当前系统本地时间；系统时钟/时区变化可能移动日/周边界，不为停机期补造；记录为 B4 ADR |
| D9 | 备份目录不可用/状态写入失败 | 目录不可用 → 检查失败、不写状态、不清理，下一次重试；状态写入失败 → 备份成功但返回警告，下一次可能重复备份（安全） |
| D10 | 不改变 B3 包格式 | manifest 格式版本仍为 1；不新增 schema；不启用 WAL |

> 详细取舍与限制见 B4 ADR；D1–D10 为技术决策，产品规则不变。

## 4. 架构

```text
Application
  ├─ DailyBackupService（调度与编排）
  ├─ BackupRetentionPolicy（7 日 + 4 周纯策略）
  ├─ IBackupChangeDetector / IBackupCatalog / IBackupStateStore / IBackupRetentionStore
  └─ 扩展 BackupCoordinator（接受 Daily）

Infrastructure.Sqlite
  └─ SqliteChangeDetector（只读 SQL 指纹）

Infrastructure.Backup
  ├─ BackupPackageVerifier.InspectAsync（manifest/条目/可选哈希，不解压）
  ├─ FileBackupCatalog（扫描/轻量/校验两档）
  ├─ FileBackupStateStore（原子 JSON 状态）
  └─ FileBackupRetentionStore（安全删除 + 警告）
```

依赖方向保持：Application → Domain；Infrastructure.Sqlite / Infrastructure.Backup → Application + Domain；无横向依赖。

## 5. 关键流程

### 5.1 `DailyBackupService.RunCheckAsync`

```text
1. 串行化检查（单实例 SemaphoreSlim）
2. 读取调度档目录（manifest/条目元数据）
3. 若今日已有有效 Daily 备份 → AlreadyBackedUpToday（不写状态、不清理）
4. 计算当前 SQL 指纹
5. 读取状态；若状态存在且指纹相同 → NoChanges（不生成备份）
6. 调用 BackupCoordinator(Create, Type=Daily)
7. 失败 → Failed（不写状态、不清理；下一次重试）
8. 成功（B3 已验证包）：
   a. 把备份前指纹原子写入状态；写入失败记录警告
   b. 读取校验档目录
   c. 计算 7 日 + 4 周保留集，保护新包
   d. 删除超额有效自动日备份；删除失败记录警告
   e. 返回 BackedUp + Backup/Retention 结果
```

### 5.2 留存策略

- 有效 Daily 按 `LocalDate` 分组，每个日期取 `CreatedAtUtc` 最新的一份作为该日代表；同日旧包进入删除候选。
- 日保留：最近 7 个本地日期代表。
- 周保留：按 ISO 周（周一）分组，取最近 4 个不同周，每周取该周最新的日代表；与日保留并集。
- 删除：有效 Daily 中不在保留集且不是新包的文件；手动/无效/未知类型永不进入删除。
- 若没有有效 Daily 或保留集为空 → 不删除任何文件。
- 同文件兼作日/周：并集天然去重。

### 5.3 状态文件

```json
{
  "formatVersion": 1,
  "lastSuccessfulFingerprint": "schema=1;notes=5;...",
  "lastSuccessfulDailyAtUtc": "2026-10-28T09:30:00.0000000+00:00",
  "lastSuccessfulDailyLocalDate": "2026-10-28"
}
```

- 只在成功日备份后写入；备份失败/当日已有日备份/无变化不写。
- 指纹取备份前值；备份期间发生的新写入会让下次检查视为变化（安全，不丢备份）。
- 缺失/损坏/不可读 → 视为无基线；下一次无今日日备份时执行备份。
- 状态写入失败不改变备份成功结果，返回警告。

## 6. 测试策略

### 6.1 单元

- 留存策略：7 日、4 周、同日重复、周边界（周一/ISO 年界）、少于 7/4、手动排除、保护新包、无效/未知不删除、并集去重。
- DailyBackupService（替身）：首跑、今日已有、无变化、变化、备份失败、状态写失败、留存失败、目录/源失败、取消、日志不含内容/完整路径。
- 变更检测/目录/状态/删除的集成测试见下。

### 6.2 集成

- SQLite 指纹：初始、创建/更新/标签关联变化、无变化、源缺失。
- 包目录：有效 Daily/Manual、无效 JSON/额外条目/哈希不符、轻量 vs 校验档、本地日期/ISO 周。
- 状态文件：往返、缺失、损坏、原子写、临时文件清理。
- 留存存储：只删目录内有效 Daily、路径安全、删除失败警告、手动不删。
- 端到端：真实 DB + 备份目录 + 状态 + 目录 + 留存；首跑、同日、无变化、跨日变化、同日变更次日补、手动排除、7+4 清理、失败不写状态/不清理、目录不可用、取消。

### 6.3 验证命令

```bash
dotnet restore QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln
dotnet build QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln --no-restore
dotnet test QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln --no-build --no-restore
dotnet list QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln package --vulnerable --include-transitive
```

通过标准：全部测试通过、0 警告/0 错误、无已知漏洞包；未运行项如实标注。

## 7. 任务拆分

1. Application 契约：Daily 结果、变更检测/目录/状态/留存接口、retention policy。
2. `BackupRetentionPolicy` + 单元测试。
3. `SqliteChangeDetector` + 集成测试。
4. `BackupPackageVerifier.InspectAsync`、`FileBackupCatalog`、`FileBackupStateStore`、`FileBackupRetentionStore` + 测试。
5. `DailyBackupService` + 单元测试；扩展 `BackupCoordinator` 接受 Daily。
6. 端到端/失败/并发/取消测试；ADR、完成总结、全量验证、审查与提交。

## 8. 验收标准

- 每日有变化且今日无成功日备份时创建一份 Daily；同日不重复。
- 无变化不生成日备份；失败不算成功，下次检查重试。
- 新日备份成功且通过 B3 校验后才清理。
- 最近 7 个本地日期 + 4 个 ISO 周代表保留；手动备份不删；同日旧包清理；同文件不重复计数。
- 状态/目录损坏、备份目录不可用、状态写入失败、清理失败均不误报成功、不删除唯一有效恢复点。
- 不启用 WAL、不改 schema、不改 B3 包格式；全部测试/构建/漏洞审计通过。

## 9. 风险与待决

- SQL 指纹对未来的“标签/分类重命名/删除”可能不敏感；当前 B 阶段没有这些 API，B7 前需扩展或改为 schema 变更计数器。
- 调度频率由宿主决定；推荐 15 分钟，启动立即检查；无 UI/后台宿主集成（B7）。
- 目录扫描/哈希校验在包多/库大时有 IO 成本；留存档每天一次，调度档只读 manifest。
- 系统时钟/时区变化可能移动日/周边界；按记录本地日期判定，不为停机期补造。
- 真实磁盘满/权限/崩溃留 B6；B4 用可注入失败与 IO 随机失败测试。
- 状态文件与备份目录同处；备份目录不可用时无法记录状态，下一次重试。
- 单进程/单实例假设；无跨进程备份锁。
