# B3 一致性备份快照 — 实施计划

> **状态：** D1–D10 已批准（2026-10-28），实施完成；见 `docs/agent/B3-完成总结.md`\
> **日期：** 2026-10-28\
> **阶段：** B3\
> **基线：** `main` / `8e28b8e`（B2 与 QuickNotes 改名已合并）\
> **上游依据：** `docs/product/PRD_v1.0.md` 11.2、11.4、11.5、11.6、15.2；`docs/agent/Agent对接文档_B阶段.md` B3；B2 设计第 15 节；`AGENTS.md`

## 1. 目标

为正在使用的 SQLite 数据库创建**一致性快照**，并生成带 manifest 与完整性校验的备份包。备份成功必须意味着：

- 快照对应数据库的某个一致时间点，而不是复制正在写入的主文件；
- 备份包只在完整写入并通过校验后发布为正式文件；
- 任何失败、取消或中断都不留下半成品，也不影响源数据库继续读写；
- manifest 记录格式版本、schema 版本、创建时间、备份类型、文件大小和校验值；
- 为 B4 自动留存、B5 整库恢复提供稳定、可验证的包格式。

## 2. 范围

**包含**

- Application 层备份协调器、结果/错误契约、manifest 契约
- SQLite 在线一致性快照（不复制正在使用的主 `.db`）
- 备份包写入：数据库快照 + `manifest.json` + SHA-256/大小校验
- 临时目录/临时文件、校验后原子发布、失败清理
- 取消、快照失败、包写入失败、校验失败、源库并发写入测试
- 手动备份用例（`BackupType.Manual`）；不实现调度与留存

**不包含**

- 自动每日备份、7 日 + 4 周留存（B4）
- 整库恢复、保护快照、回退（B5）
- 真实磁盘满、权限拒绝、系统崩溃的 OS 级故障注入（B6；B3 用可注入失败测试）
- WAL 启用（本计划建议不启用，待确认）
- UI、托盘、安装器、Rust FFI
- 草稿文件、窗口布局、设备专属设置进入备份包（B5/B7 处理；见 §9）
- 物理删除便签、schema 变更

## 3. 待确认技术决策

| 编号 | 决策 | 建议 | 理由 |
|---|---|---|---|
| D1 | 快照机制 | **SQLite Online Backup API**（`SqliteConnection.BackupDatabase`） | SQLite 官方一致性快照；源库可继续读写；目标为独立 `.db`，无需处理 `-wal`/`-shm`；比拼接 SQL 路径的 `VACUUM INTO` 更可控 |
| D2 | B3 是否启用 WAL | **不启用** | 在线备份 API 在 rollback journal 模式下已能保持一致快照；WAL 会引入 `-wal`/`-shm` 生命周期、恢复与留存复杂度；当前为单用户原型，收益不足。需在 B3 记录 ADR |
| D3 | 备份包格式 | **ZIP**，固定包含 `notes.db` + `manifest.json` | 符合“快照 + manifest + 校验信息”的原型方向；B5 的归档路径穿越检查也以此为对象 |
| D4 | 校验算法 | **SHA-256**（小写十六进制） | 无需额外依赖；manifest 记录快照文件字节数与哈希 |
| D5 | 发布原子性 | 在同目录写临时 ZIP → 校验 → `File.Move` 到最终名（不覆盖） | 同卷移动原子；失败时最终文件不存在 |
| D6 | 故障注入范围 | B3 用注入式快照/包存储失败 + 取消；真实磁盘满/权限/崩溃留 B6 | 与 B2 设计第 1 节一致；避免 OS 级不可控测试 |
| D7 | 备份目录 | 要求调用方提供已存在、可写的目录；B3 只在其下创建临时工作目录 | 目录配置属于后续 UI/设置阶段；B3 不修改用户设置 |
| D8 | 包内 manifest 时间 | `createdAtUtc` 必填；另存 `createdAtLocal`（含偏移）供 B4 本地日/周留存使用 | PRD 11.2 以本机当地日历日为单位；提前记录避免 B4 反推时区 |
| D9 | 备份类型 | B3 只产生 `Manual`；枚举预留 `Automatic`、`Daily`、`Weekly` | 与 B4 的留存类型衔接；B3 不生成自动备份 |
| D10 | 设置与草稿 | B3 只备份 SQLite 数据库；草稿/设备设置不进入包 | 草稿是恢复辅助数据，B5 必须保留；设置模型尚未建立，留 B7 |

> 进入实施前请确认 D1–D10，尤其是 D1、D2、D6、D10。

## 4. 架构与依赖

```text
Application
  └─ IBackupCoordinator / BackupCoordinator
       ├─ IBackupSnapshotProvider（Application 契约）
       ├─ IBackupPackageStore（Application 契约）
       ├─ BackupManifest / BackupResult / BackupError
       └─ ILogger<BackupCoordinator>（可选）

Infrastructure.Sqlite
  └─ SqliteSnapshotProvider
       ├─ SqliteConnection.BackupDatabase → 临时独立 .db
       └─ integrity_check / foreign_key_check / user_version

Infrastructure.Backup
  └─ ZipBackupPackageStore
       ├─ manifest 序列化与严格校验
       ├─ SHA-256 / 大小计算
       ├─ ZIP 写入与重新打开校验
       └─ 原子发布 + 临时文件清理
```

依赖方向：

- Application → Domain
- Infrastructure.Sqlite → Application + Domain
- Infrastructure.Backup → Application + Domain
- 无 UI、Sqlite、Backup 之间的横向依赖；协调器在 Application 组合两者。

新增项目：

- `src/QuickNotes.Infrastructure.Backup/QuickNotes.Infrastructure.Backup.csproj`（`net10.0`；引用 Application、Domain）
- 更新 `QuickNotes.DataPrototype.sln`；`QuickNotes.UnitTests` 与 `QuickNotes.IntegrationTests` 引用 Backup 项目

不修改 B1/B2 schema、`user_version`、WPF 原型。

## 5. 应用层契约

```csharp
public enum BackupType
{
    Manual = 0,
    Automatic = 1, // B4
    Daily = 2,     // B4
    Weekly = 3     // B4
}

public enum BackupStatus
{
    Succeeded,
    ValidationFailed,
    SnapshotFailed,
    VerificationFailed,
    StorageUnavailable
}

public enum BackupErrorCode
{
    None = 0,
    ValidationFailed,
    SnapshotFailed,
    SnapshotVerificationFailed,
    PackageWriteFailed,
    PackageVerificationFailed,
    DestinationUnavailable,
    SourceUnavailable
}

public sealed record BackupRequest
{
    public required string BackupDirectory { get; init; }
    public BackupType Type { get; init; } = BackupType.Manual;
}

public sealed record BackupManifest
{
    public required int FormatVersion { get; init; }
    public required BackupType BackupType { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset CreatedAtLocal { get; init; }
    public required long SchemaVersion { get; init; }
    public required string DatabaseFileName { get; init; }
    public required long DatabaseSizeBytes { get; init; }
    public required string DatabaseSha256 { get; init; }
}

public sealed record BackupResult
{
    public required BackupStatus Status { get; init; }
    public string? PackagePath { get; init; }
    public BackupManifest? Manifest { get; init; }
    public BackupError? Error { get; init; }
    public long DurationMilliseconds { get; init; }
}

public sealed record BackupError(
    BackupErrorCode Code,
    string Message,
    bool IsRetryable = false,
    string? Diagnostics = null);

public sealed class BackupException : Exception
{
    public string Operation { get; }   // snapshot/verify/package/publish
    public string ErrorCode { get; }   // 稳定诊断码
    public bool IsRetryable { get; }
}

public interface IBackupSnapshotProvider
{
    Task<SnapshotInfo> CreateAsync(
        string destinationDatabasePath,
        CancellationToken cancellationToken = default);
}

public sealed record SnapshotInfo(long SchemaVersion);

public interface IBackupPackageStore
{
    Task<BackupPackageInfo> PublishAsync(
        string snapshotDatabasePath,
        long schemaVersion,
        BackupRequest request,
        DateTimeOffset createdAtUtc,
        DateTimeOffset createdAtLocal,
        CancellationToken cancellationToken = default);
}

public sealed record BackupPackageInfo(string PackagePath, BackupManifest Manifest);

public interface IBackupCoordinator
{
    Task<BackupResult> CreateAsync(
        BackupRequest request,
        CancellationToken cancellationToken = default);
}
```

- `BackupCoordinator` 构造注入快照提供者、包存储、可选 logger；依赖为 null 时 `ArgumentNullException`。
- 预期存储/校验失败映射为 `BackupResult`；取消抛 `OperationCanceledException`；非预期异常记录 Error 后原样抛出。
- 日志字段：operation、backupType、durationMs、status、errorCode、包文件名；**不记录便签正文/标题/标签内容**，也不记录完整用户路径（只记录文件名）。
- 备份成功后才返回 `Succeeded` 与最终包路径；任何失败都不得把临时包当作成功结果。

## 6. 备份包格式

ZIP 固定两个条目，顺序不限：

```text
manifest.json
notes.db
```

`manifest.json`（camelCase、UTF-8 无 BOM、枚举字符串、时间用 `"O"`）：

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

规则：

- 最终文件名：`quicknotes-<yyyyMMdd-HHmmss-fff>-manual.zip`（UTC）；冲突时追加 `-<4位随机>`，不覆盖已有包。
- ZIP 内条目名必须精确为 `manifest.json`、`notes.db`；拒绝重复条目、绝对路径、`..`、反斜杠或额外文件。B3 写入端自身只产生这两个条目，读取校验同样严格，为 B5 解压安全打基础。
- `databaseSizeBytes` 是 `notes.db` 未压缩字节数；`databaseSha256` 是该文件字节的 SHA-256。
- `schemaVersion` 来自快照的 `PRAGMA user_version`；格式版本从 1 开始，未知版本拒绝。
- manifest 写入后，包校验必须重新打开 ZIP、读取 manifest、核对条目与哈希；不得只依赖写入时的内存对象。
- 可选 sidecar `<package>.sha256` 不在 B3 强制；若增加，必须作为发布后校验的附加信息，不能替代 manifest。

## 7. 创建流程

```text
1. 校验请求：目录非空、存在、可写；Type 为 Manual（B3）
2. 在备份目录下创建临时工作目录 .quicknotes-tmp-<guid>
3. 快照提供者：
   a. 打开源库连接（只读或现有 factory；不修改源库）
   b. 目标连接指向临时目录/notes.db
   c. SqliteConnection.BackupDatabase(destination)
   d. 关闭目标连接；打开快照，执行
      PRAGMA integrity_check; PRAGMA foreign_key_check; PRAGMA user_version;
   e. 校验失败 → 删除临时快照 → 抛 BackupException(snapshot_verification_failed)
4. 包存储：
   a. 计算快照字节数与 SHA-256
   b. 构造 manifest（UTC/本地时间、schemaVersion）
   c. 写临时 ZIP（.zip.<guid>.tmp），条目固定
   d. 重新打开临时 ZIP 校验条目、manifest、哈希
   e. 解压快照到临时验证文件，再次 integrity_check/foreign_key_check/user_version
   f. 校验失败 → 删除临时 ZIP/临时验证文件 → 抛 BackupException(package_verification_failed)
5. 原子发布：File.Move(tempZip, finalPath)，不覆盖
6. 清理临时工作目录；返回 Succeeded + 最终路径 + manifest
```

失败/取消时：

- 源数据库不变；源库连接始终只读或仅由备份 API 读取。
- 临时目录/临时 ZIP/临时解压文件尽力删除；清理失败记录诊断，不改变主失败结果。
- 取消：入口、快照前、快照后、包写入前、发布前检查；驱动备份 API 不支持中途取消，取消在边界生效；若取消发生在发布后，按已完成处理（不谎报失败）。
- 发布前任何失败都不产生最终包；发布后返回成功。
- 目标目录不可写/不存在 → `DestinationUnavailable`，不创建最终文件。

## 8. 错误与日志

| 场景 | BackupStatus | BackupErrorCode | 可重试 |
|---|---|---|---|
| 源库打开/快照失败 | `SnapshotFailed` | `SourceUnavailable` / `SnapshotFailed` | 视 SQLite 错误；BUSY/LOCKED 可标记 true |
| 快照 integrity/foreign_key/schema 失败 | `VerificationFailed` | `SnapshotVerificationFailed` | 否 |
| 临时 ZIP 写入失败 | `StorageUnavailable` | `PackageWriteFailed` / `DestinationUnavailable` | 视 IO 错误 |
| 包重新打开/哈希/manifest 校验失败 | `VerificationFailed` | `PackageVerificationFailed` | 否 |
| 备份目录不可用 | `StorageUnavailable` | `DestinationUnavailable` | 是（用户修复目录后） |
| 非预期异常 | — | — | 原样抛出，记录 Error |

- 原始 `SqliteException` 不越过 Application；快照提供者翻译为 `BackupException` 或 `StorageUnavailableException` 语义（B2 异常可用于源库错误，需明确复用或独立封装）。
- 诊断只包含 operation、文件名字、SQLite 错误码/扩展码、阶段；不包含便签正文/标题/标签内容。
- 备份失败不写 manifest 到最终位置，不产生“伪成功”记录。

## 9. 与 B4/B5 的边界

- B4：自动每日备份、7 日 + 4 周留存、周代表备份选择、手动备份排除清理。B3 的 manifest 已包含 `createdAtUtc`、`createdAtLocal`、`backupType`，B4 可据此做本地日/周判定；B4 需写 ADR 确定周起始日与代表性规则。
- B5：整库恢复前检查包/ manifest/哈希/schema/结构与空间；恢复前创建保护快照；恢复时保留草稿目录，不得覆盖未保存草稿。B3 的包校验逻辑可复用，但 B5 必须重新执行完整预检，不能信任“曾经校验过”。
- B3 不把草稿、窗口布局、设备设置放入备份包；PRD 11.5 的完整备份范围在后续阶段补齐。当前原型的数据层只有便签/分类/标签，B3 先保证数据库快照可靠。

## 10. 测试策略

### 10.1 单元测试

- 请求/目录校验；未知备份类型拒绝（B3 只接受 Manual）。
- manifest 序列化/反序列化、未知 formatVersion、缺失字段、非法 SHA-256、负大小、非法时间。
- 包存储：固定条目、额外条目/重复条目/路径穿越条目拒绝、哈希不匹配、截断 ZIP、目标已存在不覆盖、临时文件清理。
- 协调器：快照失败、包写入失败、校验失败映射；取消；非预期异常；失败时不发布；日志不含内容。
- 用替身快照提供者/包存储验证“仅校验成功才发布”的顺序。

### 10.2 SQLite 集成测试

- 创建含 Active / Archived / Trash、分类、标签、关联关系的数据库，备份后打开快照，逐表核对内容、schemaVersion、foreign_key_check、integrity_check。
- 备份时并发写入（后台写入循环/第二连接）：源库写入不被破坏，备份完成；备份后源库仍可写；快照可读且关联完整。
- schemaVersion=1 记录正确；空数据库备份。
- 快照 API 失败（目标路径不可写/源库关闭）→ 无最终包、临时清理。
- 取消在快照前/包写入前 → 无最终包、源库可用；发布后取消不谎报失败。
- 最终 ZIP 条目、manifest 字段、SHA-256/大小与快照一致；无 `.tmp` 残留。

### 10.3 故障注入

- 装饰器使快照在写入一半后抛异常：最终包不存在、临时目录清理、源库可读。
- 装饰器使 ZIP 写入/校验失败：不发布、不覆盖已有包。
- 篡改临时 ZIP 内 `notes.db` 或 manifest：校验失败，不发布。
- 真实磁盘满、权限拒绝、系统崩溃：B6；B3 只验证可注入的 IO 失败路径（待确认 D6）。

### 10.4 验证命令

```bash
dotnet restore QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln
dotnet build QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln --no-restore
dotnet test QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln --no-build --no-restore
dotnet list QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln package --vulnerable --include-transitive
```

通过标准：全部测试通过、0 警告/0 错误、无已知漏洞包；未运行的测试如实标注；测试仅使用临时目录与合成数据。

## 11. 任务拆分（TDD）

### Task 1：Application 备份契约与 manifest 模型

**文件**：`src/QuickNotes.Application/Backup*.cs`、`IBackupSnapshotProvider.cs`、`IBackupPackageStore.cs`、`IBackupCoordinator.cs`

- 先写 manifest/请求/结果校验测试（UnitTests，编译失败为红）。
- 实现记录与枚举、稳定错误码、`BackupException`。
- 类型检查通过；单文件测试通过。

### Task 2：SQLite 一致性快照提供者

**文件**：`src/QuickNotes.Infrastructure.Sqlite/SqliteSnapshotProvider.cs`、集成测试

- 先写真实数据库快照测试：内容/关联/schema/integrity/foreign_key。
- 实现 `BackupDatabase` → 临时独立 `.db`；关闭目标连接；验证；失败删除临时文件。
- 加并发写入测试：源库备份期间可写、备份后可写、快照一致。
- 加取消/目标不可写测试。

### Task 3：ZIP 包存储、manifest、哈希与原子发布

**文件**：`src/QuickNotes.Infrastructure.Backup/*`、集成测试

- 先写包格式失败测试：额外条目、重复条目、路径穿越、哈希不符、截断 ZIP、目标存在。
- 实现 manifest 严格序列化、SHA-256、临时 ZIP、重新打开校验、原子 `File.Move`、临时清理。
- 验证包内 `notes.db` 可再次通过 integrity/foreign_key/schema 检查。
- 更新 sln/测试引用。

### Task 4：备份协调器与失败语义

**文件**：`src/QuickNotes.Application/BackupCoordinator.cs`、单元测试

- 先写替身测试：成功、快照失败、包写入失败、校验失败、取消、非预期异常、临时清理顺序。
- 实现结果映射、日志、临时工作目录生命周期。
- 明确“只有发布成功才返回 Succeeded”。

### Task 5：端到端、故障注入与全量验证

- 组合真实 SQLite + 包存储 + 协调器：备份、校验、再次打开；并发写入；取消；装饰器失败。
- 执行 restore/build/test/漏洞审计；更新 B3 完成总结与 ADR（WAL、快照方式、故障注入范围）。
- 代码审查（标准轴 + 规格轴）后提交到新的 B3 分支，等待手动合并。

## 12. 验收标准

- 不直接复制正在使用的 SQLite 主文件；使用 SQLite 支持的一致性快照机制。
- 备份包包含 `notes.db` + `manifest.json`，manifest 至少记录格式版本、schema 版本、创建时间、备份类型、文件大小、SHA-256。
- 先在临时位置写入并完成 ZIP/manifest/哈希/数据库完整性校验，之后才发布为最终包。
- 失败/取消/中断不产生最终包、不覆盖已有备份、临时文件清理、源库保持可用。
- 源库在备份期间和之后可继续读写；快照可独立打开且关联完整。
- 原始 SQLite 异常不越过 Application；错误码可诊断且日志不含便签内容。
- 不启用 WAL（若 D2 确认）、不实现 B4/B5 范围、不改 schema、不引入 UI/Rust FFI。
- 全部测试、构建、漏洞审计通过；未运行项如实报告。

## 13. 风险与限制

- 在线备份 API 是同步/无进度回调的驱动方法；取消在边界生效，不能中断已开始的页复制。
- 备份耗时随数据库增大；B3 不做进度 UI，需在日志记录耗时。
- ZIP 压缩增加 CPU/耗时；是否使用 `CompressionLevel.Optimal` 或 `NoCompression` 可在实施时测量后确定。
- 单进程/单实例假设；无跨进程备份锁。
- 备份包未加密；最终目录与权限策略未定。
- 设置、窗口布局、草稿未进入 B3 包；B5/B7 需补齐或明确保留策略。
- 真实磁盘满/权限/崩溃留 B6；B3 只做可注入失败。
- 不启用 WAL 时读写并发仍可能遇到 BUSY；B2 的有界保存语义继续适用。
- SQLite 仍是候选技术，未最终冻结。

## 14. 决策确认

- [x] D1 快照机制：Online Backup API
- [x] D2 B3 不启用 WAL
- [x] D3 ZIP + 固定两条目
- [x] D4 SHA-256
- [x] D5 临时文件 + 原子发布、不覆盖
- [x] D6 OS 级故障注入留 B6，B3 用注入式失败
- [x] D7 备份目录必须已存在且可写
- [x] D8 manifest 同时记录 UTC 与本地时间
- [x] D9 B3 仅 Manual 类型
- [x] D10 B3 只备份数据库，不含草稿/设置
- [x] 新增 `QuickNotes.Infrastructure.Backup` 项目

以上决策已于 2026-10-28 全部批准；实施、测试与验证结果见 `docs/agent/B3-完成总结.md`。
