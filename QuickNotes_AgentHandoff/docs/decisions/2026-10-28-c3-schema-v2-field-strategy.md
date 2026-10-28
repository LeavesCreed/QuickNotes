# ADR：C3 schema v2 字段归属、默认值与设备边界

- **日期：** 2026-10-28
- **状态：** 已接受（随 C3 实施记录）；其中默认字号、顶栏默认值、快捷键默认组合为候选/待产品确认
- **范围：** C3 完整状态模型、设置归属、字段默认值与校验
- **相关：** `docs/agent/Agent对接文档_C阶段.md` C3、PRD 4.1–4.4、5.1–5.3、9、10.1、11.1、11.5–11.7、13.4、14.1、17.1–17.2、20、[字段与恢复策略](../agent/C阶段-字段与恢复策略.md)、[迁移与恢复兼容 ADR](2026-10-28-c3-migration-and-restore-compatibility.md)、[变更标识与快照基线 ADR](2026-10-28-c3-change-marker-and-snapshot-baseline.md)、[C2 编辑保存 ADR](2026-10-28-c2-editor-save-timing.md)

## 背景

PRD 11.5 要求备份至少包含便签颜色、字号及默认/覆盖模式、显示/隐藏/置顶/位置/尺寸、全局隐藏恢复状态、应用设置及顶栏配置；PRD 11.5/11.7 同时要求备份目录、开机启动注册状态和显示器标识等本机配置不得跨设备盲目覆盖。现有 v1 模型只有标题、正文、分类、标签、置顶、显示意图与生命周期，缺少上述状态。C3 需要先明确每个状态的归属、默认值、并发方式和是否进入备份，避免重复建模或把设备状态写入可恢复数据。

## 决策

1. **便签外观与布局属于便签数据，进入备份。**
   - `Note.ColorMode` 为 `NoteColorMode.FollowCategory=0` / `Manual=1`，默认 `FollowCategory`。
   - `FollowCategory` 时 `ColorHex` 必须为 null，由分类 `default_color` 决定；`Manual` 时必须为 `#RRGGBB`（6 位十六进制，不支持透明度；PRD 4.4 第一版不支持透明度调节）。
   - `Note.FontSize` 为 `int?`，null 表示跟随全局默认字号；非 null 时范围 **8–72（候选/待产品确认）**。
   - `Note.WindowLayout` 为可空的逻辑 DIP 值 `left/top/width/height`，四者同空或同非空；宽度/高度必须为正且有限，`left/top` 允许负值（多屏左侧/上方）。显示器标识与坐标修正不入库、不入备份。
   - 便签写入使用 `NoteRules` 校验，非法值抛参数异常。
2. **可迁移应用设置进入数据库并进入备份。**
   - 使用单行 `app_settings`：`version`、`default_font_size`、`global_hidden`、`notifications_enabled`、`top_bar_config_json`、`updated_at_utc`。
   - 默认值由 `ApplicationSettings.CreateDefault(now)` 生成：`Version=1`、`DefaultFontSize=16`、`GlobalHidden=false`、`NotificationsEnabled=true`、`TopBar=TopBarConfig.Default`、`UpdatedAtUtc=now`。
   - **全局默认字号 16、范围 8–72 为候选/待产品确认**（PRD 4.2、20 明确字号范围待技术原型确认）。
   - 设置更新使用 `version` 乐观锁；并发冲突必须显式失败，不静默覆盖。
3. **顶栏配置是全局应用设置，不是每便签字段。**
   - 动作集合为 `TopBarAction { Pin, Category, Color, FontSize, More }`；默认 `TopBarConfig.Default.Actions = [Pin, More]`。
   - **动作集合与默认 `[Pin, More]` 为候选/待产品确认**（PRD 4.4 仅要求全局统一配置和“更多”可添加，未冻结集合与默认）。
   - 校验：最多 8 个、不得重复、必须是已定义动作；超限或非法拒绝保存。上限 8 是技术保护值，不是 PRD 指标。
4. **设备专属配置进入独立文件 `device-settings.json`，不进入备份。**
   - 内容：`backupDirectory`、`startupRegistrationEnabled`、`hotkeys`、`displayBindings`；格式版本 `1`，原子写入。
   - 备份目录为 null（首次可跳过，PRD 11.1）或绝对路径；恢复不修改。
   - 开机启动默认 false；PRD 17.1 不强制开启，实际 OS 注册状态以 C5 本机核验为准。
   - 快捷键默认 Ctrl+Alt+N / Ctrl+Alt+M / Ctrl+Alt+H 为**候选/待产品确认**（PRD 9/14.1 待 Windows 原型验证）；单项 null 表示禁用。实际注册状态是 C5 运行时结果，不持久化为“已启用”事实。
   - 显示器绑定按便签 ID → 显示器标识保存；恢复不修改，窗口由宿主按当前显示器重映射。
5. **数据库身份与变更标识属于技术元数据。**
   - `database_meta(database_id, change_sequence)`；迁移/恢复轮换身份并递增序号，不作为用户设置，也不暴露给设置 UI。
6. **全局隐藏是全局设置，不覆盖单张便签意图。**
   - `app_settings.global_hidden` 只表达全局隐藏状态；单张便签的 `display_intent` 不被全局隐藏改写。全局恢复只显示原本 `Visible` 的便签（PRD 5.2），启动恢复由 C5 读取该状态。
7. **草稿与迁移保护快照保持本机专属。**
   - 草稿仍位于本地草稿目录，不进入备份；C3 Task 7 升格式版本 2，增加外观字段并以 FollowCategory/null 兼容 v1 草稿。
   - 迁移保护快照是 `migration-protection/` 下的原始 DB 文件，不是备份包，不参与备份留存；C4 再提供恢复入口与清理策略。

## 后果

**正面**

- 备份内容覆盖 PRD 11.5 所需状态，恢复后外观、字号、布局、全局隐藏与顶栏一致。
- 本机配置与可迁移状态分离，跨设备恢复不会盲目覆盖备份目录、快捷键注册或显示器绑定。
- 默认值与校验集中在 Domain，仓储/草稿/UI 复用同一规则，非法值尽早在写入前失败。
- 候选数值与 schema 分离：顶栏默认值若调整只需改领域常量和测试；字号范围若调整，除 `NoteRules` 与测试外，还必须同步更新 schema v2 的 `font_size` CHECK 约束并执行相应迁移（已有 v2 库需重建约束），不是“无 schema 变更”。

**负面 / 限制**

- 默认字号 16、范围 8–72、顶栏动作集合/默认、快捷键默认组合仍是候选/待产品确认，不是冻结指标。
- 手动颜色只支持 `#RRGGBB`，不支持 alpha、颜色名或 3 位简写；PRD 第一版不支持透明度。
- 顶栏最多 8 个动作是技术上限；产品若要求更多需改 ADR。
- 设备配置不进入备份：设备丢失/更换后需重新配置备份目录、快捷键和开机启动；这是 PRD 11.5/11.7 的有意取舍。
- `DeviceSettings` 中集合属性的记录相等性按引用比较；需要值比较的调用方应逐字段比较（C4/C5 接线时注意）。
- 分类/标签写入在 C3 采用单写者假设，未加版本列；并发管理窗口编辑可能后写覆盖（C5 前需保持单管理窗口或另行评估）。

## 备选方案

- **可迁移设置放 JSON 文件而不入库：** 拒绝。PRD 11.5 要求设置进入备份，且变更检测需与业务写入同事务。
- **窗口布局放设备设置：** 拒绝。PRD 11.5 把位置/尺寸列为备份内容；只有显示器标识属于本机。
- **字号总是存非空整数：** 拒绝。会丢失“跟随全局”与“单张覆盖”的区别（PRD 4.4）。
- **颜色存 ARGB 整数或允许 3 位十六进制：** 拒绝。第一版无透明度，`#RRGGBB` 可读、可校验、可跨格式迁移。
- **设备设置入库并随恢复覆盖：** 拒绝。违反 PRD 11.5/11.7“不得盲目覆盖本机配置”。
- **全局隐藏直接改写每张便签的显示意图：** 拒绝。违反 PRD 5.2，无法区分全局恢复与单张隐藏。
- **顶栏配置按便签存储：** 拒绝。PRD 4.4 要求全局统一配置。

## 验证

- `NoteRulesTests`：手动颜色 `#RRGGBB`、FollowCategory 必须 null、字号边界 8/72/7/73、窗口布局正值与非有限值（`NaN`/`PositiveInfinity`）拒绝、`Note` 默认 FollowCategory/null——本 worktree 11/11 通过（2026-10-28）。
- 应用设置、顶栏、设备设置的存储/往返测试属于 C3 Task 2，尚未实现、未运行；本 ADR 不宣称这些验证已完成。
- 字段与 PRD 条款映射见 [C 阶段字段与恢复策略](../agent/C阶段-字段与恢复策略.md)。
- 候选值变更需同时更新本 ADR、字段策略文档与对应测试；字号范围变更还必须同步更新 schema v2 的 CHECK 约束与迁移。
