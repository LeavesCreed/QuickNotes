# C3 schema v2、设置边界与可靠变更检测 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 C2 最小编辑闭环之上完成 schema v2：补齐 PRD 11.5 所需便签外观/窗口布局/应用设置字段，建立可迁移的 v1→v2 升级路径与 v1/v2 备份恢复兼容矩阵，并用持久化变更序号替换启发式 SQL 指纹，保证备份成功基线对应包内真实快照。

**Architecture:** 可备份的产品状态进入 SQLite schema v2（`notes` 新列、`app_settings`、`database_meta`）；设备专属配置进入不参与备份的 `device-settings.json`。`Desktop.Core` 的迁移服务在启动时先验证/创建保护快照，再由 `SqliteSchemaMigrator` 事务化迁移；所有写路径在同一事务更新 `database_meta.change_sequence`，备份状态使用快照内读出的标识，而不是快照后的源库读数。

**Tech Stack:** .NET 10、C#、WPF、SQLite（Microsoft.Data.Sqlite 10.0.0）、xUnit 2.9.3；现有 `INoteRepository`、`INoteSaveCoordinator`、`INoteDraftStore`、`IBackupSnapshotProvider`、`IBackupChangeDetector`、`IRestoreStore`、`IDatabaseWriteGate`。

**Spec:** `QuickNotes_AgentHandoff/docs/agent/Agent对接文档_C阶段.md` C3；PRD 4.2、4.4、5.2、10.1、11.5、11.8、15.2、17.2；`QuickNotes_AgentHandoff/docs/agent/C阶段-PRD覆盖表.md`；B3/B4/B5/B7 ADR 与数据模型文档；C1/C2 ADR 与完成总结。

---

## Global Constraints

- 正式目标 Windows 11 x64；不承诺 Windows 10。
- 不改变已确认产品规则；PRD 未给出的默认字号、字号范围、顶栏动作集合属于候选值，必须写入 ADR 并标记“待用户确认”，不能冒充已冻结指标。
- 数据安全优先：迁移失败保持原库可用；不把半迁移库当作正常数据；未知未来 schema 拒绝降级。
- 备份成功基线只能使用包内快照的变更标识；失败/发布前取消不推进基线。
- 所有应备份内容的写入与变更序号在同一事务提交；事务回滚不得推进序号。
- 设备专属配置（备份目录、开机启动注册、快捷键实际注册状态、显示器标识/绑定）不因恢复或迁移被盲目覆盖。
- v1 备份包必须可恢复：先以原 manifest 哈希验证包内原始库，再迁移暂存库；切换后按迁移后目标哈希核对，不得拿迁移后的库直接比原包哈希。
- 不改变 ZIP 结构；manifest 格式版本保持 1，数据库 schema 版本与包格式版本分别管理。
- 只使用合成数据/临时目录；不访问真实用户便签、草稿或备份。
- 不实现 C4/C5 的备份管理 UI、托盘、全局快捷键、分类/标签管理 UI；C3 只交付数据层、迁移、设置边界与测试。
- 迁移/恢复期间不启用 WAL；沿用单进程、单实例、rollback journal 边界。

---

## 0. 执行前需要确认的候选决定

以下为按 PRD 推导的候选值；计划默认按此实施，若用户不同意应先改 ADR/计划再编码。

1. **字号**：全局默认 `16`，范围 `8–72`；便签 `font_size NULL` 表示跟随全局。PRD 19.2 明确字号范围待原型确认，C3 ADR 标“候选”。
2. **颜色**：便签 `color_mode=FollowCategory|Manual`；FollowCategory 时 `color_hex=NULL`，由分类 `default_color` 决定；Manual 时必须存 `#RRGGBB`。
3. **窗口布局**：每张便签保存逻辑 DIP 的 `left/top/width/height`（可空，四者同空或同非空）；显示器标识与坐标修正不入库、不入备份，由 C5 宿主按当前设备重新映射。负坐标允许（多屏左侧/上方）。
4. **顶栏**：全局配置存 `app_settings.top_bar_config_json`；动作集合 `Pin|Category|Color|FontSize|More`，默认 `["Pin","More"]`。C5 负责实际渲染。
5. **设置归属**：可迁移应用设置（默认字号、全局隐藏、通知开关、顶栏）进 DB `app_settings` 并进入备份；设备配置进 `device-settings.json`，格式版本独立，原子写入，不进入备份。
6. **全局隐藏**：`app_settings.global_hidden` 记录“全局隐藏”意图；单张便签 `display_intent` 不被全局隐藏改写。启动后是否恢复隐藏由 C5 读取此状态。
7. **分类/标签写入**：C3 增加数据层的重命名/删除方法（含分类删除时转移便签、删除标签只解除关联），但不做并发版本控制；单管理窗口假设，ADR 记录。重命名到同名标签的合并确认留 C5。
8. **迁移保护快照**：在数据根 `migration-protection/` 生成并校验原始 v1 DB 文件（非 ZIP 包），保留不自动清理；C4 再提供恢复入口/清理策略。
9. **恢复/迁移身份**：v1→v2 迁移和任何恢复都生成新的 `database_meta.database_id` 并递增 `change_sequence`，确保旧备份基线失效。
10. **恢复当前库要求**：C3 应用启动后当前库必须是 v2；恢复预检不接受 v1 当前库（应先由启动迁移处理），避免恢复路径与迁移路径分叉。

---

## 1. 字段策略与兼容矩阵（计划内锁定）

### 1.1 字段策略表

| 状态 | 存储位置 | 版本/并发 | 进入备份 | 恢复行为 | 跨设备/默认 |
|---|---|---|---|---|---|
| 标题/正文/分类/标签/置顶/显示意图/生命周期 | `notes`、`note_tags`（v1 已有） | `notes.version` 乐观锁 | 是 | 原样恢复 | 可迁移；标签/分类按 ID 关联 |
| 便签颜色模式/手动颜色 | `notes.color_mode`、`notes.color_hex` | `notes.version` | 是 | 原样恢复 | 可迁移；FollowCategory 由分类默认色决定 |
| 便签字号覆盖 | `notes.font_size` | `notes.version` | 是 | 原样恢复 | 可迁移；NULL=跟随全局 |
| 便签窗口布局 | `notes.window_left/top/width/height` | `notes.version` | 是 | 恢复逻辑值；宿主按当前显示器重映射（C5） | 可迁移；NULL=宿主默认放置 |
| 全局默认字号/通知开关/全局隐藏/顶栏 | `app_settings` 单行 | `version` 乐观锁 | 是 | 原样恢复 | 可迁移；缺省值见候选决定 |
| 数据库身份/变更序号 | `database_meta` 单行 | 写入事务内递增 | 是（DB 字节） | 恢复/迁移生成新身份 | 不作为用户设置 |
| 备份目录 | `device-settings.json` | 文件版本 + 原子写 | 否 | 保持不变 | 本机专属 |
| 开机启动注册状态 | `device-settings.json`（记录用户意图） | 文件版本 + 原子写 | 否 | 保持不变；实际 OS 状态重验 | 本机专属 |
| 快捷键配置/注册状态 | `device-settings.json`（配置）+ C5 实际注册状态 | 文件版本 + 原子写 | 否 | 保持不变；重新注册并报告冲突 | 本机专属 |
| 显示器标识/便签布局绑定 | `device-settings.json.display_bindings` | 文件版本 + 原子写 | 否 | 保持不变；窗口按当前显示器找回 | 本机专属 |
| 草稿 | `drafts/<draftId>.json` | 去抖串行写、格式版本 2 | 否 | 重启本地恢复 | 本机未保存内容 |
| 迁移保护快照 | `migration-protection/*.db` | 文件 | 否 | 保留供诊断/后续恢复入口 | 本机保护 |

### 1.2 版本与兼容矩阵

| 当前应用 | 当前 DB | 包内 DB schema | 结果 |
|---|---:|---:|---|
| C2（v1 代码） | 1 | 1 | 现有直接恢复 |
| C3 | 2 | 2 | 直接预检/恢复；恢复后轮换 DB 身份 |
| C3 | 2 | 1 | 验证原包哈希 → 暂存库迁移 v1→v2 → 轮换身份 → 按目标哈希切换/核对 |
| C3 | 1 | 1/2 | 正常启动必须先迁移当前库；恢复预检拒绝 v1 当前库，提示使用启动迁移 |
| 任意 | 未知 >2 | 未知 >2 | 拒绝；不降级、不半迁移 |

- manifest `formatVersion` 保持 1；`schemaVersion` 允许 1 或 2（恢复侧校验，创建侧写当前 2）。`BackupManifestSerializer` 不改字段/版本；`BackupPackageVerifier` 继续按 manifest 的 schema 校验包内库，恢复侧额外限制 1/2。
- ZIP 条目仍严格为 `manifest.json` + `notes.db`；不新增第三条目。
- 迁移保护快照是原始 DB 文件，不是 ZIP；不得被备份目录扫描/留存当作备份包。

---

## 2. 文件结构（预计）

```text
QuickNotes_AgentHandoff/
├─ src/QuickNotes.Domain/
│  ├─ Note.cs                              # 增加外观/布局字段
│  ├─ NoteColorMode.cs                    # 新
│  ├─ NoteWindowLayout.cs                 # 新
│  ├─ NoteRules.cs                        # 新：颜色/字号/布局校验
│  ├─ ApplicationSettings.cs              # 新
│  ├─ TopBarAction.cs / TopBarConfig.cs   # 新
│  ├─ DeviceSettings.cs / HotkeySettings.cs # 新（设备本地）
│  └─ ...
├─ src/QuickNotes.Application/
│  ├─ IApplicationSettingsRepository.cs   # 新
│  ├─ IDeviceSettingsStore.cs             # 新
│  ├─ DeviceSettingsReadResult.cs         # 新
│  ├─ SettingsConcurrencyException.cs     # 新
│  ├─ DataChangeMarker.cs                 # 新
│  ├─ ISchemaMigrator.cs / SchemaMigrationOutcome.cs # 新
│  ├─ SnapshotInfo.cs                     # 改为携带 marker
│  ├─ BackupResult.cs                     # 增加 Snapshot
│  ├─ RestorePreflightResult.cs           # 增加迁移/目标哈希信息
│  └─ ...
├─ src/QuickNotes.Infrastructure.Sqlite/
│  ├─ SqliteSchema.cs                     # v2 建库，拒绝直接初始化 v1
│  ├─ SchemaMigrationRequiredException.cs  # 新：InitializeAsync 遇 v1
│  ├─ SqliteSchemaMigrator.cs             # 新：v1→v2、身份轮换
│  ├─ SchemaMigrationException.cs          # 新：迁移失败类型化异常
│  ├─ SqliteChangeMarker.cs               # 新：读写/递增
│  ├─ SqliteChangeDetector.cs             # 改为 database_meta 指纹
│  ├─ SqliteNoteRepository.cs             # 新列 + 事务内 bump
│  ├─ SqliteCatalogRepository.cs          # 更新/删除/重命名 + 事务内 bump
│  ├─ SqliteApplicationSettingsRepository.cs # 新
│  ├─ SqliteTypeMapping.cs                # 新列映射
│  ├─ SqliteSnapshotProvider.cs           # 返回 SnapshotInfo(marker)
│  └─ SqliteRestoreStore.cs               # 目标库身份/哈希核对支持
├─ src/QuickNotes.Desktop.Core/
│  ├─ Migration/SchemaMigrationService.cs # 新：保护快照 + 迁移
│  ├─ Settings/FileDeviceSettingsStore.cs # 新：原子 JSON
│  ├─ Paths/DesktopPaths.cs               # 迁移保护目录/设备设置路径
│  ├─ Startup/StartupCoordinator.cs       # v1→v2 迁移、schema 2 Ready
│  ├─ Startup/StartupReason.cs            # 新 migration_failed
│  ├─ Startup/StartupArtifactScanner.cs   # 识别迁移保护快照
│  ├─ Recovery/MissingDatabaseRebuilder.cs# v1 包迁移 + 目标哈希
│  └─ Hosting/DesktopApplicationHost.cs   # 组合迁移/重建服务
├─ tests/
│  ├─ QuickNotes.UnitTests/               # 域校验、迁移/恢复纯单元
│  ├─ QuickNotes.IntegrationTests/        # 真实 SQLite schema/migration/change marker
│  └─ QuickNotes.Desktop.Tests/           # 启动/迁移/重命名/设备设置/宿主
└─ docs/
   ├─ agent/C阶段-字段与恢复策略.md        # 字段策略与兼容矩阵
   ├─ decisions/2026-10-28-c3-schema-v2-field-strategy.md
   ├─ decisions/2026-10-28-c3-change-marker-and-snapshot-baseline.md
   ├─ decisions/2026-10-28-c3-migration-and-restore-compatibility.md
   └─ superpowers/plans/2026-10-28-c3-schema-v2-change-detection.md
```

---

### Task 1: 领域模型、字段策略与 ADR

**Files:**
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Domain/NoteColorMode.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Domain/NoteWindowLayout.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Domain/NoteRules.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Domain/Note.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Domain/ApplicationSettings.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Domain/TopBarAction.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Domain/TopBarConfig.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Domain/HotkeySettings.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Domain/DeviceSettings.cs`
- Create: `QuickNotes_AgentHandoff/docs/agent/C阶段-字段与恢复策略.md`
- Create: `QuickNotes_AgentHandoff/docs/decisions/2026-10-28-c3-schema-v2-field-strategy.md`
- Create: `QuickNotes_AgentHandoff/docs/decisions/2026-10-28-c3-migration-and-restore-compatibility.md`
- Create: `QuickNotes_AgentHandoff/docs/decisions/2026-10-28-c3-change-marker-and-snapshot-baseline.md`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.UnitTests/NoteRulesTests.cs`

**Interfaces:**
- Produces:
  - `public enum NoteColorMode { FollowCategory = 0, Manual = 1 }`
  - `public sealed record NoteWindowLayout { double Left, Top, Width, Height }`（required init）
  - `Note` 新属性：`NoteColorMode ColorMode`、`string? ColorHex`、`int? FontSize`、`NoteWindowLayout? WindowLayout`
  - `NoteRules.MinFontSize=8`、`MaxFontSize=72`、`ValidateColor`、`ValidateFontSize`、`ValidateWindowLayout`
  - `ApplicationSettings`（`Version`、`DefaultFontSize`、`GlobalHidden`、`NotificationsEnabled`、`TopBar`、`UpdatedAtUtc`）
  - `TopBarAction { Pin, Category, Color, FontSize, More }`
  - `TopBarConfig.Default.Actions = [Pin, More]`
  - `DeviceSettings`（`FormatVersion=1`、`BackupDirectory`、`StartupRegistrationEnabled`、`Hotkeys`、`DisplayBindings`）
  - `HotkeySettings.Default`（Ctrl+Alt+N / Ctrl+Alt+M / Ctrl+Alt+H）

- [ ] **Step 1: Write failing domain tests**

```csharp
using QuickNotes.Domain;

namespace QuickNotes.UnitTests;

public sealed class NoteRulesTests
{
    [Theory]
    [InlineData("#E6E6FA", true)]
    [InlineData("#000000", true)]
    [InlineData("E6E6FA", false)]
    [InlineData("#GGGGGG", false)]
    public void ManualColor_RequiresHex(string value, bool valid)
    {
        if (valid)
        {
            NoteRules.ValidateColor(NoteColorMode.Manual, value);
        }
        else
        {
            Assert.Throws<ArgumentException>(() =>
                NoteRules.ValidateColor(NoteColorMode.Manual, value));
        }
    }

    [Fact]
    public void FollowCategory_RequiresNullColor()
    {
        NoteRules.ValidateColor(NoteColorMode.FollowCategory, null);
        Assert.Throws<ArgumentException>(() =>
            NoteRules.ValidateColor(NoteColorMode.FollowCategory, "#E6E6FA"));
    }

    [Theory]
    [InlineData(8, true)]
    [InlineData(72, true)]
    [InlineData(7, false)]
    [InlineData(73, false)]
    public void FontSize_UsesCandidateRange(int size, bool valid)
    {
        if (valid)
        {
            NoteRules.ValidateFontSize(size);
        }
        else
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                NoteRules.ValidateFontSize(size));
        }

        NoteRules.ValidateFontSize(null);
    }

    [Fact]
    public void WindowLayout_RequiresPositiveSizeAndFiniteValues()
    {
        NoteRules.ValidateWindowLayout(new NoteWindowLayout
        {
            Left = -100,
            Top = 50,
            Width = 320,
            Height = 240
        });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NoteRules.ValidateWindowLayout(new NoteWindowLayout
            {
                Left = 0,
                Top = 0,
                Width = 0,
                Height = 240
            }));
        NoteRules.ValidateWindowLayout(null);
    }

    [Fact]
    public void Defaults_AreFollowCategoryAndGlobalFont()
    {
        var note = new Note
        {
            Id = Guid.NewGuid(),
            Body = "body",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ModifiedAtUtc = DateTimeOffset.UtcNow,
            CategoryId = Category.UncategorizedId
        };

        Assert.Equal(NoteColorMode.FollowCategory, note.ColorMode);
        Assert.Null(note.ColorHex);
        Assert.Null(note.FontSize);
        Assert.Null(note.WindowLayout);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.UnitTests/QuickNotes.UnitTests.csproj --filter FullyQualifiedName~NoteRulesTests`

Expected: 编译失败（类型/属性不存在）。

- [ ] **Step 3: Implement domain types**

```csharp
namespace QuickNotes.Domain;

public enum NoteColorMode
{
    FollowCategory = 0,
    Manual = 1
}

public sealed record NoteWindowLayout
{
    public required double Left { get; init; }
    public required double Top { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
}
```

```csharp
namespace QuickNotes.Domain;

public static class NoteRules
{
    public const int MinFontSize = 8;
    public const int MaxFontSize = 72;
    private static readonly int[] HexDigits = ...; // 使用 Uri.IsHexDigit 或 char 检查

    public static void ValidateColor(NoteColorMode mode, string? colorHex)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentException("Color mode must be defined.", nameof(mode));
        }

        if (mode == NoteColorMode.FollowCategory)
        {
            if (colorHex is not null)
            {
                throw new ArgumentException(
                    "A follow-category note must not store a manual color.",
                    nameof(colorHex));
            }

            return;
        }

        if (colorHex is not { Length: 7 }
            || colorHex[0] != '#'
            || colorHex.Skip(1).Any(c => !Uri.IsHexDigit(c)))
        {
            throw new ArgumentException(
                "A manual color must use #RRGGBB.",
                nameof(colorHex));
        }
    }

    public static void ValidateFontSize(int? fontSize)
    {
        if (fontSize is { } value && (value < MinFontSize || value > MaxFontSize))
        {
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        }
    }

    public static void ValidateWindowLayout(NoteWindowLayout? layout)
    {
        if (layout is null)
        {
            return;
        }

        if (!double.IsFinite(layout.Left)
            || !double.IsFinite(layout.Top)
            || !double.IsFinite(layout.Width)
            || !double.IsFinite(layout.Height)
            || layout.Width <= 0
            || layout.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(layout));
        }
    }
}
```

`Note` 增加：

```csharp
public NoteColorMode ColorMode { get; init; } = NoteColorMode.FollowCategory;
public string? ColorHex { get; init; }
public int? FontSize { get; init; }
public NoteWindowLayout? WindowLayout { get; init; }
```

`ApplicationSettings.CreateDefault(DateTimeOffset now)` 与 `TopBarConfig` 校验：

```csharp
public sealed record TopBarConfig
{
    public static TopBarConfig Default { get; } = new()
    {
        Actions = new[] { TopBarAction.Pin, TopBarAction.More }
    };

    public required IReadOnlyList<TopBarAction> Actions { get; init; }

    public void Validate()
    {
        if (Actions.Count > 8
            || Actions.Distinct().Count() != Actions.Count
            || Actions.Any(action => !Enum.IsDefined(action)))
        {
            throw new ArgumentException("Top bar actions are invalid.", nameof(Actions));
        }
    }
}
```

`ApplicationSettings.CreateDefault` 使用 `Version=1`、`DefaultFontSize=16`、`GlobalHidden=false`、`NotificationsEnabled=true`、`TopBarConfig.Default`。

- [ ] **Step 4: Write field strategy and ADRs**

`C阶段-字段与恢复策略.md` 写入本计划 1.1/1.2 的表格，并逐条注明 PRD 条款、候选值状态、C4/C5 后续接线点。三份 ADR 分别记录：字段归属/默认值、迁移与恢复兼容、变更标识/快照基线；状态均为“已接受（随 C3 实施记录）”，候选字号/顶栏默认值明确标“待产品确认”。

- [ ] **Step 5: Run tests and commit**

```bash
dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.UnitTests/QuickNotes.UnitTests.csproj --filter FullyQualifiedName~NoteRulesTests
git add QuickNotes_AgentHandoff/src/QuickNotes.Domain QuickNotes_AgentHandoff/tests/QuickNotes.UnitTests/NoteRulesTests.cs QuickNotes_AgentHandoff/docs/agent/C阶段-字段与恢复策略.md QuickNotes_AgentHandoff/docs/decisions/2026-10-28-c3-*.md
git commit -m "feat: 添加 C3 领域模型、字段策略与 ADR"
```

Expected: 新测试全部通过；既有测试因 `Note` 默认属性不回归。

---

### Task 2: schema v2、便签/设置仓储与设备设置文件

**Files:**
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Infrastructure.Sqlite/SqliteSchema.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Infrastructure.Sqlite/SqliteTypeMapping.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Infrastructure.Sqlite/SqliteNoteRepository.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Infrastructure.Sqlite/SqliteChangeMarker.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Infrastructure.Sqlite/SchemaMigrationRequiredException.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Application/IApplicationSettingsRepository.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Application/SettingsConcurrencyException.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Infrastructure.Sqlite/SqliteApplicationSettingsRepository.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Application/IDeviceSettingsStore.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Application/DeviceSettingsReadResult.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Settings/FileDeviceSettingsStore.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Paths/DesktopPaths.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/SqliteSchemaTests.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/SqliteNoteRepositoryTests.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/SqliteApplicationSettingsRepositoryTests.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/FileDeviceSettingsStoreTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `Note`/`ApplicationSettings`/`DeviceSettings`/`NoteRules`。
- Produces:
  - `SqliteSchema.CurrentVersion = 2`；`InitializeAsync` 只创建/接受 v2；v1 抛 `SchemaMigrationRequiredException`。
  - `NoteRules` 在仓储写入/草稿写入前校验颜色、字号、布局；非法值抛参数异常。
  - `SqliteSchemaMigrator` 的 DDL 常量（Task 4 使用）：`notes` 新列、`database_meta`、`app_settings`。
  - `SqliteChangeMarker.ReadAsync(connection, transaction, ct)` → `(Guid DatabaseId, long ChangeSequence)`；`BumpAsync(connection, transaction, ct)`。
  - `IApplicationSettingsRepository.GetAsync` / `UpdateAsync(ApplicationSettings settings, long expectedVersion, ct)` → `ApplicationSettings`；版本不符抛 `SettingsConcurrencyException`。
  - `IDeviceSettingsStore.ReadAsync` → `DeviceSettingsReadResult`（`Found/NotFound/Invalid`）；`WriteAsync(DeviceSettings, ct)`。
  - `DesktopPaths.DeviceSettingsPath`、`DesktopPaths.MigrationProtectionDirectory`。

- [ ] **Step 1: Write failing schema/repository tests**

```csharp
[Fact]
public async Task Initialize_CreatesVersionTwoWithSettingsAndMeta()
{
    using var database = new TestDatabase();
    await new SqliteSchema(database.Factory).InitializeAsync();

    Assert.Equal(2, await ReadUserVersionAsync(database.Factory));
    Assert.Equal(1, await ScalarLongAsync(database.Factory, "SELECT COUNT(*) FROM database_meta;"));
    Assert.Equal(1, await ScalarLongAsync(database.Factory, "SELECT COUNT(*) FROM app_settings;"));
    Assert.Equal(16, await ScalarLongAsync(database.Factory, "SELECT default_font_size FROM app_settings WHERE id=1;"));
}
```

```csharp
[Fact]
public async Task Note_AppearanceAndLayout_RoundTrips()
{
    using var database = new TestDatabase();
    await new SqliteSchema(database.Factory).InitializeAsync();
    var repository = new SqliteNoteRepository(database.Factory);
    var now = DateTimeOffset.UtcNow;
    var note = new Note
    {
        Id = Guid.NewGuid(),
        Title = "title",
        Body = "body",
        CreatedAtUtc = now,
        ModifiedAtUtc = now,
        CategoryId = Category.UncategorizedId,
        ColorMode = NoteColorMode.Manual,
        ColorHex = "#FFCC00",
        FontSize = 20,
        WindowLayout = new NoteWindowLayout
        {
            Left = -120,
            Top = 80,
            Width = 360,
            Height = 280
        }
    };

    await repository.CreateAsync(note, Array.Empty<Guid>());
    var loaded = await repository.GetAsync(note.Id);

    Assert.Equal(NoteColorMode.Manual, loaded!.ColorMode);
    Assert.Equal("#FFCC00", loaded.ColorHex);
    Assert.Equal(20, loaded.FontSize);
    Assert.Equal(note.WindowLayout, loaded.WindowLayout);
}
```

```csharp
[Fact]
public async Task ApplicationSettings_Update_IncrementsVersionAndRejectsStale()
{
    using var database = new TestDatabase();
    await new SqliteSchema(database.Factory).InitializeAsync();
    var repository = new SqliteApplicationSettingsRepository(database.Factory);
    var current = await repository.GetAsync();

    var updated = await repository.UpdateAsync(
        current with { GlobalHidden = true, DefaultFontSize = 18 },
        current.Version);

    Assert.Equal(current.Version + 1, updated.Version);
    Assert.True(updated.GlobalHidden);
    await Assert.ThrowsAsync<SettingsConcurrencyException>(() =>
        repository.UpdateAsync(current, current.Version));
}
```

```csharp
[Fact]
public async Task DeviceSettings_RoundTripsAtomically_AndRejectsUnknownVersion()
{
    using var temporary = new TestTemporaryDirectory();
    var path = Path.Combine(temporary.RootPath, "device-settings.json");
    var store = new FileDeviceSettingsStore(path);
    var settings = new DeviceSettings
    {
        FormatVersion = DeviceSettings.CurrentFormatVersion,
        BackupDirectory = Path.Combine(temporary.RootPath, "backups"),
        StartupRegistrationEnabled = false,
        Hotkeys = HotkeySettings.Default,
        DisplayBindings = new Dictionary<Guid, string> { [Guid.NewGuid()] = "DISPLAY1" }
    };

    await store.WriteAsync(settings);
    var read = await store.ReadAsync();

    Assert.Equal(DeviceSettingsReadResultStatus.Found, read.Status);
    Assert.Equal(settings.BackupDirectory, read.Settings!.BackupDirectory);
    Assert.Empty(Directory.GetFiles(temporary.RootPath, "*.tmp"));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/QuickNotes.IntegrationTests.csproj --filter "FullyQualifiedName~SqliteSchemaTests|FullyQualifiedName~SqliteApplicationSettingsRepositoryTests"`

Expected: 编译失败（新类型/表不存在）。

- [ ] **Step 3: Implement v2 schema DDL**

`SqliteSchema` 建立：

```sql
CREATE TABLE categories (...同 v1...);
CREATE TABLE tags (...同 v1...);
CREATE TABLE notes (
    id TEXT PRIMARY KEY,
    title TEXT NULL,
    body TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    modified_at_utc TEXT NOT NULL,
    version INTEGER NOT NULL CHECK (version >= 1),
    lifecycle INTEGER NOT NULL CHECK (lifecycle IN (0, 1, 2)),
    category_id TEXT NOT NULL,
    is_pinned INTEGER NOT NULL CHECK (is_pinned IN (0, 1)),
    display_intent INTEGER NOT NULL CHECK (display_intent IN (0, 1)),
    color_mode INTEGER NOT NULL DEFAULT 0 CHECK (color_mode IN (0, 1)),
    color_hex TEXT NULL CHECK (color_hex IS NULL OR length(color_hex) = 7),
    font_size INTEGER NULL CHECK (font_size IS NULL OR (font_size BETWEEN 8 AND 72)),
    window_left REAL NULL,
    window_top REAL NULL,
    window_width REAL NULL CHECK (window_width IS NULL OR window_width > 0),
    window_height REAL NULL CHECK (window_height IS NULL OR window_height > 0),
    FOREIGN KEY (category_id) REFERENCES categories(id) ON DELETE RESTRICT,
    CHECK (lifecycle = 0 OR display_intent = 0)
);
CREATE TABLE note_tags (...同 v1...);
CREATE TABLE database_meta (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    database_id TEXT NOT NULL,
    change_sequence INTEGER NOT NULL CHECK (change_sequence >= 0)
);
CREATE TABLE app_settings (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    version INTEGER NOT NULL CHECK (version >= 1),
    default_font_size INTEGER NOT NULL CHECK (default_font_size BETWEEN 8 AND 72),
    global_hidden INTEGER NOT NULL CHECK (global_hidden IN (0, 1)),
    notifications_enabled INTEGER NOT NULL CHECK (notifications_enabled IN (0, 1)),
    top_bar_config_json TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);
-- 索引同 v1
```

初始化种子：系统分类；`database_meta = (1, new Guid, 0)`；`app_settings = ApplicationSettings.CreateDefault(now)`（`version=1`、字体 16、全局隐藏 false、通知 true、`["Pin","More"]`）；`PRAGMA user_version=2`。

`SqliteSchema.InitializeAsync` 逻辑：

```csharp
if (version == 2) return;
if (version == 1) throw new SchemaMigrationRequiredException();
if (version != 0) throw new NotSupportedException(...);
// create v2 + seed + user_version=2 in one transaction
```

- [ ] **Step 4: Implement mapping/repository/settings/device store**

`SqliteTypeMapping`：

```csharp
public static Note ReadNote(SqliteDataReader reader) => new()
{
    // ...v1 ordinals...
    ColorMode = (NoteColorMode)reader.GetInt32(10),
    ColorHex = reader.IsDBNull(11) ? null : reader.GetString(11),
    FontSize = reader.IsDBNull(12) ? null : reader.GetInt32(12),
    WindowLayout = reader.IsDBNull(13)
        ? null
        : new NoteWindowLayout
        {
            Left = reader.GetDouble(13),
            Top = reader.GetDouble(14),
            Width = reader.GetDouble(15),
            Height = reader.GetDouble(16)
        }
};
```

`NoteColumns` 扩展为 17 列；Create/Update SQL 写新列；在 `CommitAsync` 前调用 `SqliteChangeMarker.BumpAsync(connection, transaction, ct)`。Create/Update 读取后仍返回完整 Note。

`SqliteApplicationSettingsRepository`：
- `GetAsync`: `SELECT version, default_font_size, global_hidden, notifications_enabled, top_bar_config_json, updated_at_utc FROM app_settings WHERE id=1;` 反序列化 `TopBarConfig`（JSON 字符串数组，`JsonStringEnumConverter`）。
- `UpdateAsync`: 单事务 `UPDATE ... WHERE id=1 AND version=$expected`；`affected==0` 抛 `SettingsConcurrencyException`；调用 `BumpAsync`；返回更新后值（version+1）。
- `SettingsConcurrencyException` 至少包含 `ExpectedVersion`、`CurrentVersion`（如可读）和错误码 `settings_concurrency_conflict`。

`FileDeviceSettingsStore`：
- 路径 `DesktopPaths.DeviceSettingsPath`；JSON `camelCase`、字符串枚举、`WriteIndented`。
- 读取：NotFound → `NotFound`；JSON/字段/版本非法 → `Invalid(errorCode)`；成功 → Found。
- 写入：临时文件 + `Flush(flushToDisk:true)` + `File.Move(overwrite:true)`；失败清理临时文件。
- `DeviceSettings.Validate`：FormatVersion=1；BackupDirectory 若存在必须绝对路径；DisplayBindings 的 key 非空、value 非空；Hotkeys 可为 null（禁用）。
- `DesktopPaths` 增加：
```csharp
public string DeviceSettingsPath => Path.Combine(DataRoot, "device-settings.json");
public string MigrationProtectionDirectory => Path.Combine(DataRoot, "migration-protection");
```

- [ ] **Step 5: Update existing tests/helpers and run**

- `TestDatabase`/`SqliteSchemaTests` 改断言 `user_version=2`；新增 `CreateVersionOneAsync` 测试夹具（原始 v1 DDL + 种子），供 Task 4/6 使用。
- `SqliteNoteRepositoryTests` 现有断言补新列默认值（FollowCategory/null）。
- 新增 `SqliteApplicationSettingsRepositoryTests`、`FileDeviceSettingsStoreTests`。
- 既有 `SqliteSchema` 初始化处无需改调用，但 `InitializeAsync` 对 v1 的预期要改为抛 `SchemaMigrationRequiredException`（新增测试）。

Run:

```bash
dotnet build QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln
dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/QuickNotes.IntegrationTests.csproj --filter "FullyQualifiedName~SqliteSchemaTests|FullyQualifiedName~SqliteNoteRepositoryTests|FullyQualifiedName~SqliteApplicationSettingsRepositoryTests"
dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/QuickNotes.Desktop.Tests.csproj --filter FullyQualifiedName~FileDeviceSettingsStoreTests
```

- [ ] **Step 6: Commit**

```bash
git add QuickNotes_AgentHandoff/src/QuickNotes.Application QuickNotes_AgentHandoff/src/QuickNotes.Infrastructure.Sqlite QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Paths QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Settings QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests
git commit -m "feat: 实现 schema v2、应用设置与设备设置存储"
```

---

### Task 3: 持久化变更序号、分类/标签写入与确定性指纹

**Files:**
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Application/ICatalogRepository.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Infrastructure.Sqlite/SqliteCatalogRepository.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Infrastructure.Sqlite/SqliteChangeDetector.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Application/DataChangeMarker.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/SqliteCatalogRepositoryTests.cs`（新建或现有文件）
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/SqliteChangeDetectorTests.cs`

**Interfaces:**
- Consumes: `SqliteChangeMarker`、v2 `database_meta`。
- Produces:
  - `ICatalogRepository` 增加：
    - `Task UpdateCategoryAsync(Guid id, string name, string defaultColor, CancellationToken ct = default)`
    - `Task DeleteCategoryAsync(Guid id, Guid reassignCategoryId, CancellationToken ct = default)`
    - `Task UpdateTagAsync(Guid id, string name, CancellationToken ct = default)`
    - `Task DeleteTagAsync(Guid id, CancellationToken ct = default)`
  - `SqliteChangeDetector` 返回 `schema=2;db=<guid>;seq=<n>`；`database_meta` 缺失抛 `BackupException`（错误码 `change_marker_unavailable`），不静默回退到旧指纹。

- [ ] **Step 1: Write failing tests**

```csharp
[Fact]
public async Task SameLengthCategoryRename_ChangesFingerprint()
{
    using var database = new TestDatabase();
    await new SqliteSchema(database.Factory).InitializeAsync();
    var catalog = new SqliteCatalogRepository(database.Factory);
    var detector = new SqliteChangeDetector(database.DatabasePath);
    var category = new Category
    {
        Id = Guid.NewGuid(),
        Name = "工作",
        DefaultColor = "#E6E6FA",
        IsSystem = false
    };
    await catalog.CreateCategoryAsync(category);
    var before = await detector.GetFingerprintAsync();

    await catalog.UpdateCategoryAsync(category.Id, "生活", category.DefaultColor);

    var after = await detector.GetFingerprintAsync();
    Assert.NotEqual(before, after);
}
```

```csharp
[Fact]
public async Task RolledBackWrite_DoesNotAdvanceChangeSequence()
{
    // 使用 v2 DB，读取 meta.sequence；
    // 在事务中 INSERT note + Bump，然后 Rollback；
    // 断言 sequence 未变，且 notes 为空。
}
```

```csharp
[Fact]
public async Task TagRelationAndColorChanges_AdvanceSequence()
{
    // 创建 note/tag，记录 fingerprint；
    // UpdateAsync 替换 tag 关联、修改 ColorHex；
    // 断言 fingerprint 改变且序号递增。
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/QuickNotes.IntegrationTests.csproj --filter FullyQualifiedName~SqliteChangeDetectorTests`

Expected: 失败（方法/指纹未实现）。

- [ ] **Step 3: Implement catalog writes and change detector**

`SqliteCatalogRepository` 每个写方法使用 `OpenAsync` + deferred transaction，执行 SQL，调用 `SqliteChangeMarker.BumpAsync`，提交；失败翻译复用 `SqliteErrorTranslator`（如需）。

分类更新：

```sql
UPDATE categories SET name=$name, default_color=$color WHERE id=$id;
```

分类删除（单事务）：

```sql
UPDATE notes SET category_id=$reassign, version=version+1, modified_at_utc=$now WHERE category_id=$id;
DELETE FROM categories WHERE id=$id AND is_system=0;
```

若分类不存在或 `is_system=1`，抛 `NoteNotFoundException`/`InvalidOperationException`（测试断言明确错误码/异常）。标签删除由 `note_tags` 外键 CASCADE 解除关联；更新标签名遇 UNIQUE 冲突抛出既有 `Translate` 的类型化异常。

`SqliteChangeDetector`：

```csharp
private static async Task<DataChangeMarker> ReadMarkerAsync(...)
{
    var schemaVersion = ...;
    await using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = "SELECT database_id, change_sequence FROM database_meta WHERE id = 1;";
    await using var reader = ...;
    if (!await reader.ReadAsync(ct))
    {
        throw new BackupException("change_detection", "change_marker_unavailable", "database_meta is missing.");
    }

    return new DataChangeMarker(
        schemaVersion,
        Guid.Parse(reader.GetString(0)),
        reader.GetInt64(1));
}
```

`DataChangeMarker.ToFingerprint()` 为唯一格式化入口：

```csharp
public string ToFingerprint() =>
    string.Create(CultureInfo.InvariantCulture, $"schema={SchemaVersion};db={DatabaseId:D};seq={ChangeSequence}");
```

`GetFingerprintAsync` 返回 `marker.ToFingerprint()`；删除旧 SQL 计数/长度/MAX 查询。

- [ ] **Step 4: Run tests and commit**

```bash
dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/QuickNotes.IntegrationTests.csproj --filter "FullyQualifiedName~SqliteChangeDetectorTests|FullyQualifiedName~SqliteCatalogRepositoryTests"
dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/QuickNotes.IntegrationTests.csproj
git add QuickNotes_AgentHandoff/src/QuickNotes.Application/ICatalogRepository.cs QuickNotes_AgentHandoff/src/QuickNotes.Infrastructure.Sqlite/SqliteCatalogRepository.cs QuickNotes_AgentHandoff/src/QuickNotes.Infrastructure.Sqlite/SqliteChangeDetector.cs QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests
git commit -m "feat: 用持久化变更序号替换启发式备份指纹"
```

---

### Task 4: v1→v2 迁移、保护快照与启动接线

**Files:**
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Application/ISchemaMigrator.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Application/SchemaMigrationOutcome.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Infrastructure.Sqlite/SqliteSchemaMigrator.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Infrastructure.Sqlite/SchemaMigrationException.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Migration/SchemaMigrationService.cs`
- Create: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Migration/SchemaMigrationResult.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Startup/StartupCoordinator.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Startup/StartupReason.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Startup/StartupArtifactScanner.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Hosting/DesktopApplicationHost.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Paths/DesktopPaths.cs`（Task 2 已加目录，若未加在此补）
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/SqliteSchemaMigratorTests.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/SchemaMigrationServiceTests.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/StartupCoordinatorTests.cs`（追加）

**Interfaces:**
- Produces:
  - `public sealed record SchemaMigrationOutcome(long FromSchemaVersion, long ToSchemaVersion, Guid DatabaseId, bool Migrated, bool IdentityRotated);`
  - `public interface ISchemaMigrator { Task<SchemaMigrationOutcome> MigrateToCurrentAsync(string databasePath, bool rotateIdentity, CancellationToken ct = default); }`
  - `SchemaMigrationResult`（`Status`、`SchemaVersion`、`ProtectionSnapshotPath`、`ErrorCode`、`Message`），并提供 `AlreadyCurrent(long)`、`Unsupported()`、`Failed(...)` 静态工厂。
  - `StartupReason.SchemaMigrationFailed`。
  - `DesktopPaths.MigrationProtectionDirectory`。

- [ ] **Step 1: Write failing tests**

```csharp
[Fact]
public async Task Migrate_V1ToV2_CreatesMetaAndDefaults()
{
    using var temporary = new TestTemporaryDirectory();
    var path = Path.Combine(temporary.RootPath, "notes.db");
    await V1DatabaseFixture.CreateAsync(path);
    var migrator = new SqliteSchemaMigrator();

    var outcome = await migrator.MigrateToCurrentAsync(path, rotateIdentity: true);

    Assert.Equal(1, outcome.FromSchemaVersion);
    Assert.Equal(2, outcome.ToSchemaVersion);
    Assert.True(outcome.Migrated);
    using var database = new TestDatabase(path);
    Assert.Equal(2, await database.ReadUserVersionAsync());
    Assert.Equal(1, await database.ScalarLongAsync("SELECT COUNT(*) FROM database_meta;"));
    Assert.Equal(1, await database.ScalarLongAsync("SELECT COUNT(*) FROM app_settings;"));
    Assert.Equal("#E6E6FA", await database.ScalarStringAsync("SELECT default_color FROM categories WHERE id = '6f2a98f2-20de-4f4e-a2f4-8a570c4625d7';"));
}
```

```csharp
[Fact]
public async Task StartupCoordinator_MigratesV1_ToReadyV2_AndUpdatesState()
{
    // 准备 v1 DB + Ready state(schemaVersion=1)
    // 构造真实 SqliteSchemaMigrator + SchemaMigrationService + StartupCoordinator
    // StartAsync 断言 Ready、DB user_version=2、state.SchemaVersion=2、
    // 保护快照存在且文件名可诊断
}
```

```csharp
[Fact]
public async Task StartupCoordinator_MigrationFailure_NeedsRecovery_KeepsV1()
{
    // 使用 fake ISchemaMigrator 抛异常；断言 NeedsRecovery、SchemaMigrationFailed、
    // DB 仍 v1、无初始化/空库
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/QuickNotes.IntegrationTests.csproj --filter FullyQualifiedName~SqliteSchemaMigratorTests`

Expected: 编译失败（类型不存在）。

- [ ] **Step 3: Implement migrator**

`SqliteSchemaMigrator`：

```csharp
public async Task<SchemaMigrationOutcome> MigrateToCurrentAsync(
    string databasePath,
    bool rotateIdentity,
    CancellationToken cancellationToken = default)
{
    await using var connection = await new SqliteConnectionFactory(databasePath).OpenAsync(cancellationToken);
    await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
    var from = ReadUserVersion(connection, transaction);
    if (from is not (1 or 2))
    {
        throw new SchemaMigrationException("schema_unsupported", $"Schema version {from} is not supported.");
    }

    if (from == 1)
    {
        // ALTER TABLE notes x7 + CREATE database_meta + CREATE app_settings + seed
        // database_id = Guid.NewGuid(); change_sequence = 1
        // PRAGMA user_version=2
    }
    else if (rotateIdentity)
    {
        // UPDATE database_meta SET database_id=$new, change_sequence=change_sequence+1 WHERE id=1
    }

    var databaseId = ReadDatabaseId(connection, transaction);
    await VerifyAsync(connection, transaction); // integrity_check / foreign_key_check / user_version
    await transaction.CommitAsync(cancellationToken);
    return new SchemaMigrationOutcome(from, 2, databaseId, from == 1, from == 2 && rotateIdentity);
}
```

迁移 DDL（必须与 Task 2 的 v2 建库列/约束一致）：

```sql
ALTER TABLE notes ADD COLUMN color_mode INTEGER NOT NULL DEFAULT 0 CHECK (color_mode IN (0,1));
ALTER TABLE notes ADD COLUMN color_hex TEXT NULL CHECK (color_hex IS NULL OR length(color_hex) = 7);
ALTER TABLE notes ADD COLUMN font_size INTEGER NULL CHECK (font_size IS NULL OR (font_size BETWEEN 8 AND 72));
ALTER TABLE notes ADD COLUMN window_left REAL NULL;
ALTER TABLE notes ADD COLUMN window_top REAL NULL;
ALTER TABLE notes ADD COLUMN window_width REAL NULL CHECK (window_width IS NULL OR window_width > 0);
ALTER TABLE notes ADD COLUMN window_height REAL NULL CHECK (window_height IS NULL OR window_height > 0);
CREATE TABLE database_meta (...);
CREATE TABLE app_settings (...);
INSERT INTO database_meta (id, database_id, change_sequence) VALUES (1, $newGuid, 1);
INSERT INTO app_settings (...) VALUES (1, 1, 16, 0, 1, '["Pin","More"]', $now);
PRAGMA user_version=2;
```

迁移在单个事务内完成；`PRAGMA integrity_check` 和 `foreign_key_check` 在提交前执行；失败自动回滚，不写 `user_version=2`。

`SchemaMigrationService`：

```csharp
public async Task<SchemaMigrationResult> EnsureCurrentAsync(
    long currentSchemaVersion,
    CancellationToken cancellationToken = default)
{
    if (currentSchemaVersion == 2) return AlreadyCurrent();
    if (currentSchemaVersion != 1) return Unsupported();

    var protectionPath = Path.Combine(
        _paths.MigrationProtectionDirectory,
        $"notes-v1-{_timeProvider.GetUtcNow():yyyyMMdd-HHmmss-fff}.db");
    try
    {
        Directory.CreateDirectory(_paths.MigrationProtectionDirectory);
        var snapshot = await new SqliteSnapshotProvider(_paths.DatabasePath).CreateAsync(protectionPath, cancellationToken);
        if (snapshot.SchemaVersion != 1) return Failed("unexpected_protection_schema", protectionPath);
        await _restoreStore.InspectAsync(protectionPath, cancellationToken); // 完整性/外键
        var outcome = await _migrator.MigrateToCurrentAsync(_paths.DatabasePath, rotateIdentity: true, cancellationToken);
        return new SchemaMigrationResult(SchemaMigrationStatus.Migrated, 2, protectionPath, null, "migration_succeeded");
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        _log.TryWrite("ERROR", "migration", "schema migration failed", exception.GetType().Name);
        return new SchemaMigrationResult(SchemaMigrationStatus.Failed, currentSchemaVersion, protectionPath, "migration_failed", exception.GetType().Name);
    }
}
```

- 保护快照创建/校验失败：不调用 migrator；返回 Failed。
- migrator 抛异常：事务已回滚；返回 Failed；保护文件保留。
- 不自动删除保护文件。

- [ ] **Step 4: Wire startup/host**

`StartupCoordinator` 增加 `ISchemaMigrationService _migration` 构造参数；在 `PrepareCurrentAsync` 之后：

```csharp
if (databaseInfo.SchemaVersion is 1 or 2)
{
    var migration = databaseInfo.SchemaVersion == 1
        ? await _migration.EnsureCurrentAsync(1, cancellationToken)
        : SchemaMigrationResult.AlreadyCurrent(2);
    if (migration.Status == SchemaMigrationStatus.Failed)
    {
        return Assessment(StartupMode.NeedsRecovery, StartupReason.SchemaMigrationFailed,
            "The database schema migration failed; the original database and protection snapshot were preserved.",
            errorCode: migration.ErrorCode, state: state, databaseInfo: databaseInfo, artifacts: artifacts);
    }

    if (migration.Status == SchemaMigrationStatus.Unsupported)
    {
        return Assessment(..., StartupReason.SchemaUnsupported, ...);
    }

    databaseInfo = await _restoreStore.InspectAsync(_paths.DatabasePath, cancellationToken);
}
if (databaseInfo.SchemaVersion != 2) { ... SchemaUnsupported ... }
var updatedState = state with
{
    SchemaVersion = 2,
    LastStartupUtc = now
};
await _stateStore.WriteAsync(updatedState, cancellationToken);
// 后续 Ready 使用 updatedState；写状态失败只记警告，DB 已 v2，下次启动会再次校准
```

- 首次初始化：`SqliteSchema.InitializeAsync` 创建 v2，`MarkReady(2)`。
- `StartupReason` 增加 `SchemaMigrationFailed`；`StartupAssessment` 详情仍显示 schema。
- `StartupArtifactScanner`：把 `migration-protection/*.db` 识别为 `MigrationProtectionSnapshot`，默认保留；测试确认不删除。
- `DesktopApplicationHost`：构造 `SqliteSchemaMigrator`、`SchemaMigrationService`，传入 `StartupCoordinator`；`MissingDatabaseRebuilder` 构造参数在 Task 6 调整。

- [ ] **Step 5: Run tests and commit**

```bash
dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/QuickNotes.IntegrationTests.csproj --filter FullyQualifiedName~SqliteSchemaMigratorTests
dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/QuickNotes.Desktop.Tests.csproj --filter "FullyQualifiedName~SchemaMigrationServiceTests|FullyQualifiedName~StartupCoordinatorTests|FullyQualifiedName~StartupArtifactScannerTests"
git add QuickNotes_AgentHandoff/src/QuickNotes.Application/ISchemaMigrator.cs QuickNotes_AgentHandoff/src/QuickNotes.Application/SchemaMigrationOutcome.cs QuickNotes_AgentHandoff/src/QuickNotes.Infrastructure.Sqlite/SqliteSchemaMigrator.cs QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Migration QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Startup QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Hosting QuickNotes_AgentHandoff/tests
git commit -m "feat: 实现 v1→v2 迁移、保护快照与启动接线"
```

---

### Task 5: 快照变更标识与成功备份基线

**Files:**
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Application/DataChangeMarker.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Application/SnapshotInfo.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Application/BackupResult.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Application/BackupCoordinator.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Application/DailyBackupService.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Infrastructure.Sqlite/SqliteSnapshotProvider.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.UnitTests/BackupCoordinatorTests.cs`（或现有）
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.UnitTests/DailyBackupServiceTests.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/SqliteSnapshotProviderTests.cs`

**Interfaces:**
- Produces:
  - `SnapshotInfo(DataChangeMarker? Marker)`，`SchemaVersion`、`HasChangeMarker`、`ToFingerprint()`（无 marker 时返回 `null`）。v1 迁移保护快照允许 marker 为 null；当前 v2 库的快照必须有 marker。
  - `BackupResult.Snapshot`（`SnapshotInfo?`）。
  - `DailyBackupService` 成功时用 `backup.Snapshot?.ToFingerprint()` 写 `LastSuccessfulFingerprint`；若返回 null 则 `stateWarning="snapshot_marker_missing"` 且不写状态（不推进基线）。

- [ ] **Step 1: Write failing tests**

```csharp
[Fact]
public async Task SuccessfulBackup_WritesSnapshotFingerprint_NotPreBackupFingerprint()
{
    // detector 返回 "schema=2;db=...;seq=10"（备份前）
    // snapshot provider 返回 marker seq=12（快照内）
    // 断言 state.LastSuccessfulFingerprint 以 seq=12 结尾
}
```

```csharp
[Fact]
public async Task FailedOrCanceledBackup_DoesNotWriteState()
{
    // 备份返回失败 / 抛 OperationCanceledException
    // 断言 stateStore.WriteCalls == 0
}
```

```csharp
[Fact]
public async Task EditDuringSnapshot_DoesNotMarkNewEditAsBackedUp()
{
    // 门控 snapshot provider：在返回 marker 前修改 DB 使 current seq=13
    // 快照 marker=12；备份成功后 state=12；下一次 fingerprint=13 -> 会备份
}
```

```csharp
[Fact]
public async Task MissingBaselineOrUnknownState_ForcesBackup_NotSkip()
{
    // 状态文件缺失 / 旧启发式 fingerprint / 未知格式版本：
    // 按 B4 规则保守处理；无有效今日备份时创建备份，不因无法比较而跳过
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.UnitTests/QuickNotes.UnitTests.csproj --filter "FullyQualifiedName~DailyBackupServiceTests|FullyQualifiedName~BackupCoordinatorTests"`

Expected: 失败（`Snapshot`/指纹来源未实现）。

- [ ] **Step 3: Implement snapshot marker and baseline**

- `SqliteSnapshotProvider.VerifySnapshotAsync` 在验证 schema 后读取 `database_meta`：schema=2 且表缺失时抛 `BackupException("snapshot", "change_marker_unavailable", ...)`；schema=1（迁移保护快照）返回 `Marker=null`。返回 `SnapshotInfo(marker)`。
- `BackupCoordinator` 在 `package = PublishAsync(...)` 后设置 `Snapshot = snapshot`；`BackupResult` 增加可选属性。
- `SchemaMigrationService` 创建 v1 保护快照时允许 marker 为 null；迁移成功后现场库是 v2，后续备份快照必须有 marker。
- `DailyBackupService` 成功分支：

```csharp
var snapshotFingerprint = backup.Snapshot?.ToFingerprint();
if (snapshotFingerprint is null)
{
    stateWarning = "snapshot_marker_missing";
}
else
{
    await _stateStore.WriteAsync(backupDirectory, new BackupScheduleState
    {
        FormatVersion = BackupScheduleState.CurrentFormatVersion,
        LastSuccessfulFingerprint = snapshotFingerprint,
        LastSuccessfulDailyAtUtc = backup.Manifest!.CreatedAtUtc,
        LastSuccessfulDailyLocalDate = DateOnly.FromDateTime(backup.Manifest.CreatedAtLocal.DateTime)
    }, cancellationToken);
}
```

- 失败/取消路径不写状态；失败/取消后不清理保护文件。
- 旧状态文件（旧格式 fingerprint）与当前 marker 不相等 → 正常触发一次备份；不修改 `BackupScheduleState.CurrentFormatVersion`。
- 更新所有 `new SnapshotInfo(...)`、`new BackupResult` 测试初始化；旧测试若只关心 schema，改用 `new SnapshotInfo(new DataChangeMarker(2, Guid.NewGuid(), 0))`。

- [ ] **Step 4: Run tests and commit**

```bash
dotnet test QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln --no-build --no-restore
git add QuickNotes_AgentHandoff/src/QuickNotes.Application QuickNotes_AgentHandoff/src/QuickNotes.Infrastructure.Sqlite/SqliteSnapshotProvider.cs QuickNotes_AgentHandoff/tests
git commit -m "feat: 备份基线使用包内快照变更标识"
```

---

### Task 6: v1/v2 备份恢复与缺失库重建兼容

**Files:**
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Application/RestoreCoordinator.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Application/RestorePreflightResult.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Application/RestoreResult.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Recovery/MissingDatabaseRebuilder.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Recovery/MissingDatabaseRebuildResult.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Application/IRestoreStore.cs`（如需目标哈希/身份方法）
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Infrastructure.Sqlite/SqliteRestoreStore.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/RestoreIntegrationTests.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/MissingDatabaseRebuilderTests.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/RestoreTests.cs`（如有）

**Interfaces:**
- Consumes: `ISchemaMigrator`；`SqliteChangeDetector`/`SnapshotInfo` marker。
- Produces:
  - `RestoreCoordinator` 构造增加 `ISchemaMigrator migrator`；`SupportedSourceSchemaVersions = {1,2}`，`TargetSchemaVersion=2`。
  - `RestorePreflightResult` 增加：`bool RequiresMigration`、`long TargetSchemaVersion`、`string? TargetDatabaseSha256`、`string? SourceManifestSha256`。
  - `RestoreResult` 增加：`bool MigratedFromV1`、`long? TargetSchemaVersion`、`string? TargetDatabaseSha256`。
  - `MissingDatabaseRebuilder` 接受 v1/v2 包；v1 暂存迁移后发布 v2。

- [ ] **Step 1: Write failing compatibility tests**

```csharp
[Fact]
public async Task Restore_V1Package_ToV2App_MigratesAndSwitchesWithTargetHash()
{
    // 创建 v1 DB + B3 v1 manifest/package（schemaVersion=1，原哈希 H1）
    // 当前库 v2（由 v1 启动迁移或直接建 v2）
    // Preflight：RequiresMigration=true，TargetSchemaVersion=2，SourceManifestSha256=H1
    // ExecuteAsync 成功；当前库 user_version=2、内容保留、HasForeignKeys、hash=TargetDatabaseSha256
    // 当前库哈希 != H1，证明切换未拿迁移库直接对原包哈希
}
```

```csharp
[Fact]
public async Task MissingDatabaseRebuild_V1Package_PublishesV2()
{
    // 删除当前库；用 v1 包重建；断言发布后 schema=2、内容/计数保留、
    // 新 database_id 不同于包内 identity，且 state.SchemaVersion=2
}
```

```csharp
[Fact]
public async Task Restore_V2Package_RestoresSettingsAndNotes()
{
    // v2 包内 app_settings.global_hidden=true、default_font_size=18、顶栏动作；
    // 恢复后读取 app_settings 与便签内容一致。
}
```

```csharp
[Fact]
public async Task Restore_V1Package_SeedsDefaultSettings()
{
    // v1 包内无 app_settings；迁移后 global_hidden=false、font=16、默认顶栏；
    // 不把全局隐藏误设为 true。
}
```

```csharp
[Fact]
public async Task FutureSchema_IsRejected_WithoutTouchingCurrentDatabase()
{
    // manifest/schema=3；断言预检/重建失败，当前库哈希和内容不变，无切换
}
```

```csharp
[Fact]
public async Task Restore_DoesNotModifyDeviceSettings()
{
    // 创建 device-settings.json 并记录字节；恢复成功后字节不变
}
```

```csharp
[Fact]
public async Task Restore_ChangesDatabaseIdentity_InvalidatesBackupBaseline()
{
    // 恢复前后 SqliteChangeDetector fingerprint 的 db GUID 不同；
    // 旧 LastSuccessfulFingerprint 不再匹配，下一次 DailyBackup 不会误判无变化。
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/QuickNotes.IntegrationTests.csproj --filter FullyQualifiedName~RestoreIntegrationTests`

Expected: 失败（v1 包被拒绝/迁移未实现）。

- [ ] **Step 3: Implement restore compatibility**

`RestoreCoordinator.PreflightAsync` 规则：

```csharp
if (currentInfo.SchemaVersion != 2)
{
    return FailedPreflight(RestoreErrorCode.SchemaIncompatible,
        "The current database must be migrated to schema 2 before restore.",
        diagnostics: $"current_schema={currentInfo.SchemaVersion}");
}

if (manifest.SchemaVersion is not (1 or 2))
{
    return FailedPreflight(RestoreErrorCode.SchemaIncompatible,
        "The backup schema is not supported.",
        diagnostics: $"backup_schema={manifest.SchemaVersion}", manifest);
}

if (backupInfo.SchemaVersion != manifest.SchemaVersion) { PackageInvalid; }

var requiresMigration = manifest.SchemaVersion == 1;
string? targetHash = null;
if (requiresMigration)
{
    var outcome = await _migrator.MigrateToCurrentAsync(preflightDatabase, rotateIdentity: true, ct);
    var migratedInfo = await _restoreStore.InspectAsync(preflightDatabase, ct);
    if (migratedInfo.SchemaVersion != 2) { PackageInvalid; }
    targetHash = await _restoreStore.TryGetDatabaseHashAsync(preflightDatabase, ct);
}

// v2 package also rotates identity at execution time; target hash computed after rotation.
```

执行阶段：

```csharp
var extracted = await _extractor.ExtractAsync(packagePath, stagedPath, ct); // 原包哈希校验
var migration = await _migrator.MigrateToCurrentAsync(stagedPath, rotateIdentity: true, ct);
var stagedInfo = await _restoreStore.InspectAsync(stagedPath, ct);
var targetHash = await _restoreStore.TryGetDatabaseHashAsync(stagedPath, ct);
// 记录 SourceManifestSha256 = manifest.DatabaseSha256
switch // File.Replace
verify: current hash == targetHash && schema=2 && counts match stagedInfo.Counts
```

`RestoreResult.TargetDatabaseSha256` 记录目标哈希；`RestoreResult.Manifest` 仍保留原 manifest（原哈希）。失败回退/阻断逻辑不变；切换前取消不推进任何基线。

`MissingDatabaseRebuilder`：
- 当前库检查改为 schema 2（v1 当前库提示先启动迁移；v2 当前库有效时仍拒绝非阻断路径）。
- 提取后：`manifest.SchemaVersion` 1/2；先 `InspectAsync` 确保包内 schema=manifest；调用 migrator 旋转身份（v1 同时迁移）；重新 inspect schema=2；计算 targetHash；切换；发布后核对 targetHash + schema/counts；写 state `MarkReady(2)`（或保留现有恢复记录逻辑）。
- 失败保留 staging/quarantine；不报成功；v1 包校验原哈希由 extractor 完成，迁移失败不修改包。

`IRestoreStore` 增加（或使用现有 `TryGetDatabaseHashAsync`）：
```csharp
Task<string?> TryGetDatabaseHashAsync(string databasePath, CancellationToken ct = default);
```
已有 `TryGetDatabaseHashAsync`，无需新增；确认 `SqliteRestoreStore` 对目标 DB 计算 SHA-256。

- [ ] **Step 4: Update constructor call sites and run tests**

- `RestoreCoordinatorTests`/`RestoreIntegrationTests`/`MissingDatabaseRebuilderTests` 构造注入真实 `SqliteSchemaMigrator` 或 fake。
- `DesktopApplicationHost` 将 `SqliteSchemaMigrator` 传入 `MissingDatabaseRebuilder`。
- 更新 C1/C2 恢复测试中 schema=1 的预期：v1 备份恢复成功但当前库变 2；v2 包直接恢复；未来版本拒绝。
- 运行：

```bash
dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/QuickNotes.IntegrationTests.csproj --filter "FullyQualifiedName~Restore|FullyQualifiedName~Backup"
dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/QuickNotes.Desktop.Tests.csproj --filter "FullyQualifiedName~MissingDatabaseRebuilder|FullyQualifiedName~DesktopApplicationHost"
```

- [ ] **Step 5: Commit**

```bash
git add QuickNotes_AgentHandoff/src/QuickNotes.Application/RestoreCoordinator.cs QuickNotes_AgentHandoff/src/QuickNotes.Application/RestorePreflightResult.cs QuickNotes_AgentHandoff/src/QuickNotes.Application/RestoreResult.cs QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Recovery QuickNotes_AgentHandoff/src/QuickNotes.Infrastructure.Sqlite/SqliteRestoreStore.cs QuickNotes_AgentHandoff/tests
git commit -m "feat: 支持 v1 备份迁移恢复与 v2 目标校验"
```

---

### Task 7: 草稿 v2 与编辑器外观/布局元数据

**Files:**
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Application/NoteDraft.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Infrastructure.Drafts/FileNoteDraftStore.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Editing/EditorSnapshot.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Editing/NoteEditorSession.cs`
- Modify: `QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Editing/EditorWorkspaceService.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/NoteEditorSessionTests.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/NoteEditorSessionSaveTests.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/EditorPersistenceIntegrationTests.cs`
- Test: `QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/DraftStoreTests.cs`（或现有）

**Interfaces:**
- Produces:
  - `NoteDraft` 增加 `NoteColorMode ColorMode`、`string? ColorHex`、`int? FontSize`、`NoteWindowLayout? WindowLayout`。
  - `FileNoteDraftStore.CurrentFormatVersion = 2`；v1 草稿仍可读（缺省 FollowCategory/null）；未知 >2 拒绝。
  - `EditorSnapshot` 同步字段；`NoteEditorSession.ChangeAppearance(NoteColorMode, string?, int?)`、`ChangeLayout(NoteWindowLayout?)`；均递增 Sequence、标记 Modified、排草稿/保存。
  - `EditorWorkspaceService` 新建默认值为 FollowCategory/null/null/null；打开/恢复草稿保留字段。

- [ ] **Step 1: Write failing tests**

```csharp
[Fact]
public async Task ChangeAppearance_PersistsColorAndFont()
{
    var (workspace, notes, _, _) = CreateWorkspace();
    var note = EditorTestData.NewNote();
    notes.Add(note);
    var session = (await workspace.OpenAsync(note.Id))!;

    session.ChangeAppearance(NoteColorMode.Manual, "#FFCC00", 20);
    await session.SaveNowAsync();

    var saved = notes.Notes[note.Id];
    Assert.Equal(NoteColorMode.Manual, saved.ColorMode);
    Assert.Equal("#FFCC00", saved.ColorHex);
    Assert.Equal(20, saved.FontSize);
}
```

```csharp
[Fact]
public async Task DraftV1_StillReadsWithDefaults()
{
    // 写入旧格式 JSON（formatVersion=1，无 color/font/layout 字段）
    // GetAsync 成功；ColorMode=FollowCategory；FontSize=null；WindowLayout=null
}
```

```csharp
[Fact]
public async Task DraftV2_RoundTripsAppearanceAndLayout()
{
    // 写入 v2 草稿，读取后字段相等
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/QuickNotes.Desktop.Tests.csproj --filter "FullyQualifiedName~ChangeAppearance|FullyQualifiedName~DraftV"`

Expected: 编译失败/测试失败。

- [ ] **Step 3: Implement draft/editor fields**

- `NoteDraft` 增加字段；`FileNoteDraftStore` 的 `DraftDocument` 增加可选字段，`CurrentFormatVersion=2`；读取 `FormatVersion == 1` 时默认 `FollowCategory/null/null/null`，`FormatVersion == 2` 时验证字段；`>2` 抛 `draft_unsupported_version`。
- `EditorSnapshot` 增加字段；`NoteEditorSession.ToNote/ToDraft/Reload/BuildStatus` 保留字段；`ChangeText` 不变。
- 新方法：

```csharp
public void ChangeAppearance(NoteColorMode colorMode, string? colorHex, int? fontSize)
{
    NoteRules.ValidateColor(colorMode, colorHex);
    NoteRules.ValidateFontSize(fontSize);
    lock (_sync)
    {
        ThrowIfDisposed();
        if (_snapshot.ColorMode == colorMode
            && _snapshot.ColorHex == colorHex
            && _snapshot.FontSize == fontSize)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        _snapshot = _snapshot with
        {
            Sequence = _snapshot.Sequence + 1,
            ColorMode = colorMode,
            ColorHex = colorHex,
            FontSize = fontSize,
            ModifiedAtUtc = now
        };
        MarkModifiedLocked(now);
    }

    RaiseStatusChanged();
}
```

`ChangeLayout` 同模式，调用 `NoteRules.ValidateWindowLayout`。`MarkModifiedLocked` 抽取现有 ChangeText 的状态/调度逻辑，避免重复。

- `EditorWorkspaceService` 新建快照带默认值；`FromNote`/`OpenFromDraft` 复制字段。
- C2 现有测试更新构造/断言；`EditorPersistenceIntegrationTests` 增加外观/布局往返测试。
- 不添加 WPF 控件；C5 负责颜色/字号/布局 UI。

- [ ] **Step 4: Run tests and commit**

```bash
dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.Desktop.Tests/QuickNotes.Desktop.Tests.csproj
dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/QuickNotes.IntegrationTests.csproj
git add QuickNotes_AgentHandoff/src/QuickNotes.Application/NoteDraft.cs QuickNotes_AgentHandoff/src/QuickNotes.Infrastructure.Drafts QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Core/Editing QuickNotes_AgentHandoff/tests
git commit -m "feat: 草稿 v2 与编辑器外观/布局元数据"
```

---

### Task 8: 全量验证、文档、双轴审查与交付

**Files:**
- Create: `QuickNotes_AgentHandoff/docs/agent/C3-测试报告.md`
- Create: `QuickNotes_AgentHandoff/docs/agent/C3-完成总结.md`
- Modify: `QuickNotes_AgentHandoff/docs/agent/C阶段-字段与恢复策略.md`
- Modify: `QuickNotes_AgentHandoff/docs/agent/B7-数据模型与备份格式.md`（标注 C3 扩展）
- Modify: `QuickNotes_AgentHandoff/docs/decisions/README.md`
- Modify: `QuickNotes_AgentHandoff/docs/agent/C阶段-PRD覆盖表.md`
- Modify: `README.md`、`QuickNotes_AgentHandoff/README.md`
- Test: 全量测试、漏洞审计、WPF 冒烟

- [ ] **Step 1: Write C3 test report**

记录：v1→v2 成功/失败/重复启动/取消/未来版本；v1 包恢复、v2 包恢复、目标哈希、身份轮换；变更序号覆盖便签/分类/标签/关联/设置；回滚不推进；快照期间编辑；失败/取消不推进基线；旧状态文件；设备设置不被恢复覆盖；草稿 v1/v2 兼容。分开记录通过/失败/未运行（C5/C6 项目、真实强杀、真机 ACL/卷满）。

- [ ] **Step 2: Write C3 completion summary**

按 C 文档 6.3 八节：范围/前置、变更文件与接口、PRD 条款与证据、实际命令/环境/结果、故障与数据保护、ADR/兼容性/审查发现、限制与 C4 入口、分支/提交状态。

- [ ] **Step 3: Update indexes/README/PRD coverage**

- ADR 索引加入三份 C3 ADR。
- 根 README/Handoff README：schema v2、字段策略、v1 备份兼容、迁移保护目录、当前测试数。
- PRD 覆盖表：11.5/11.8、10.1（字段自动保存）、15.2、17.2 状态更新；未实现项（C4/C5 UI）不得标通过。
- `B7-数据模型与备份格式.md` 增加“C3 扩展”说明，保留历史 v1 格式记录。

- [ ] **Step 4: Full verification and dual-axis review**

```bash
dotnet build QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln
dotnet test QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln --no-build --no-restore
dotnet list QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln package --vulnerable --include-transitive
```

- 标准轴：事务/取消/资源释放、迁移原子性、变更序号同事务、旧数据兼容、无静默吞异常、日志不含正文、UI 不直接 SQL。
- 规格轴：逐条对照 C3 工作/验收/交付；确认字段策略表、迁移保护、v1 备份路径、确定性变更检测、快照基线、设备边界；确认未实现 C4+。
- 修复发现后重跑相关测试；记录未运行项。

- [ ] **Step 5: Commit**

```bash
git add QuickNotes_AgentHandoff/docs README.md QuickNotes_AgentHandoff/README.md
git commit -m "docs: 完成 C3 测试报告与交付总结"
```

---

## Self-Review

**Spec coverage**
- PRD 11.5 字段：便签颜色/字号/布局、全局隐藏、应用设置/顶栏 → Task 1/2/7；设备专属 → Task 1/2。
- 字段策略表/版本/恢复行为 → Task 1 文档 + Task 2 schema。
- v1→v2 迁移保护/失败保留/未知版本拒绝 → Task 4。
- v1 备份恢复先验证原哈希再迁移暂存 → Task 6。
- 变更序号覆盖分类/标签重命名、关系、外观、设置 → Task 3/7。
- 快照基线对应包内快照、失败/取消不推进 → Task 5。
- 旧状态文件/恢复后身份失效 → Task 3/5/6。
- 设备设置不盲目覆盖 → Task 6 测试。
- C3 不实现 C4/C5 UI → Global Constraints。

**Placeholder scan**
- 无 TBD/TODO；候选值集中列在“执行前需要确认”，不是占位。
- 所有新增类型/接口/表名与后续任务引用一致：`DataChangeMarker`、`SnapshotInfo.Marker`、`ISchemaMigrator`、`SchemaMigrationOutcome`、`IApplicationSettingsRepository`、`IDeviceSettingsStore`、`database_meta`、`app_settings`。

**Type consistency**
- `NoteWindowLayout` 使用 `double`；`font_size` 使用 `int?`；`ApplicationSettings.Version` 为 `long`；`DataChangeMarker.DatabaseId` 为 `Guid`；`SnapshotInfo` 只携带 `DataChangeMarker` + `SchemaVersion` 计算属性。
- `ICatalogRepository` 新方法与 Task 3 实现一致；`ISchemaMigrator` 方法在 Task 4/6 使用一致参数 `(string databasePath, bool rotateIdentity, CancellationToken)`。

## 执行前需要确认

1. 是否接受候选字号默认/范围（16；8–72）与顶栏默认动作（Pin、More）。
2. 是否接受应用设置进 DB、设备设置进 `device-settings.json` 的分界。
3. 是否接受迁移保护快照为本地原始 DB 文件（非 ZIP），C3 不提供恢复 UI。
4. 是否接受分类/标签数据层写入不做并发版本控制（单管理窗口假设），C5 再评估合并/并发。
5. 是否接受恢复/迁移都轮换数据库身份，并保持 manifest 格式版本 1、ZIP 结构不变。
