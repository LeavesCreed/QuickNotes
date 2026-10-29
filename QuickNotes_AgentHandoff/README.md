# QuickNotes（C 阶段桌面与数据原型）

QuickNotes 是面向 Windows 11 x64 的桌面便签软件。当前仓库包含数据/备份层、C1 桌面宿主/启动安全、C2 最小编辑闭环与 C3 schema v2/迁移/确定性变更检测；**不包含**备份管理 UI、分类/标签/生命周期 UI、托盘/全局快捷键、设置页或安装器（C4–C7）。

## 当前状态

- B0–B7、C0–C3 已实现并通过自动化测试；C3 完成 schema v2、v1→v2 迁移保护、v1/v2 备份恢复兼容与持久化变更序号。
- C1 交付可启动的 WPF 启动/诊断宿主：单实例、正式数据目录、启动恢复状态机、恢复产物识别、缺失库/损坏库/阻断恢复的最小入口。
- C2 交付最小工作区：Active 便签列表、标题/正文编辑、自动保存与 Ctrl+S、保存/草稿状态、冲突/未知结果处理、重启草稿恢复。
- C3 交付 schema v2（颜色/字号/逻辑窗口布局、应用设置、数据库身份/变更序号）、启动迁移与保护快照、v1 包迁移恢复、快照基线。
- 当前仍**没有**备份/恢复 UI、设置页、分类/标签/生命周期 UI、托盘/全局快捷键；这些属于 C4–C5。
- 当前交付物是类库 + WPF 工作区/诊断宿主 + 自动化测试；测试与 WPF 冒烟是当前可执行入口。

## 环境要求

- Windows 11 x64（正式目标；不承诺 Windows 10）。
- .NET SDK 10.0.x（含 WindowsDesktop runtime）。本仓库没有 `global.json`；C1 验证使用 SDK `10.0.402`、Runtime `10.0.13`。
- 首次构建需要访问 nuget.org 还原包。
- SQLite 原生库由 `SQLitePCLRaw.lib.e_sqlite3` 随包提供，不需要单独安装 SQLite。

## 构建与测试

在仓库根目录执行：

```bash
dotnet build QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln
dotnet test QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln --no-build --no-restore
dotnet list QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln package --vulnerable --include-transitive
```

B7 基线结果：

- 构建：0 警告，0 错误。
- 测试：245/245 通过（单元 120 + 集成 125），0 失败、0 跳过。
- 漏洞审计：7 个项目均无已知漏洞包。

C1 追加指标（含桌面测试）：测试总计 **313/313**（单元 120 + 集成 125 + 桌面 68），构建 0 警告；详见 [C1 测试报告](docs/agent/C1-测试报告.md)。

C2 追加指标：测试总计 **353/353**（单元 120 + 集成 128 + 桌面 105），构建 0 警告；WPF 冒烟通过；人工检查 M01–M12、M15、M16 通过，M13/M14 未执行，C5/C6 未运行；详见 [C2 测试报告](docs/agent/C2-测试报告.md)。

C3 追加指标：测试总计 **444/444**（单元 138 + 集成 178 + 桌面 128），构建 0 警告、0 错误；漏洞审计 10 个项目无已知漏洞包；WPF 冒烟首次初始化状态 `schemaVersion=2`；详见 [C3 测试报告](docs/agent/C3-测试报告.md)。

## 运行桌面宿主

```powershell
dotnet run --project QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/QuickNotes.Desktop.Wpf.csproj -- --data-root "$env:TEMP\QuickNotes-C3-Demo"
```

- 数据层 Ready 时直接进入最小工作区；首次运行、恢复、阻断或只读诊断时显示启动/诊断窗口。
- 工作区支持 Active 便签列表、新建、标题/正文编辑、自动保存（停止输入 800ms、最长 5s）、保存按钮/Ctrl+S、焦点离开/切换/关闭前保存。
- 草稿自动落盘（300ms 去抖），“草稿恢复”窗口可查看、恢复或显式丢弃；恢复不自动提交。
- 首次运行会创建 schema v2 的 `notes.db`、`application-state.json`、`drafts/`、`logs/`；v1 数据根首次启动会先在 `migration-protection/` 生成并校验保护快照，再事务化迁移到 v2。
- 支持 `--backup-dir <dir>` 仅用于诊断扫描保护快照；缺失库/损坏库/阻断恢复可从窗口选择已验证备份。
- 第二实例会尝试激活第一实例；同一数据根只允许一个实例。
- 只使用临时数据根，不要指向真实用户数据。

测试只使用临时目录与合成数据，不接触真实用户数据。完整结果、未运行项与环境见 [B7 测试报告](docs/agent/B7-测试报告.md)、[C1 测试报告](docs/agent/C1-测试报告.md)、[C2 测试报告](docs/agent/C2-测试报告.md)、[C3 测试报告](docs/agent/C3-测试报告.md)。

## 已实现能力

| 领域 | 能力 |
|---|---|
| 数据模型 | 便签、分类、标签、标签关联；生命周期（正常/归档/回收站）；版本号并发保护；schema v2 |
| schema v2（C3） | 便签颜色/字号/逻辑窗口布局；应用设置与顶栏；数据库身份 + 变更序号；设备设置独立文件 |
| 迁移（C3） | v1→v2 保护快照、事务迁移、失败保留 v1、未来版本拒绝、启动接线 |
| 变更检测（C3） | 持久化 `change_sequence` 同事务推进；快照内基线；恢复/迁移轮换身份 |
| 恢复兼容（C3） | v1/v2 包恢复；v1 原哈希验证→暂存迁移→目标哈希切换；缺失库重建同样支持 |
| 编辑（C2） | Active 列表；新建/打开/编辑；自动保存与显式保存；真实保存状态；迟到结果不误报；Ctrl+S/焦点离开/切换/关闭前保存 |
| 草稿（C2） | 去抖自动落盘；草稿状态与失败提示；重启恢复/查看/显式丢弃；不自动提交；损坏草稿保留证据 |
| 保存 | 事务提交后才报告成功；BUSY/LOCKED 有界重试；提交未知时回读核验；草稿保留 |
| 备份 | SQLite Online Backup API 一致性快照；ZIP + manifest + SHA-256；临时包校验后原子发布 |
| 自动备份 | SQL 指纹变更检测；本地日备份；7 日 + 4 周留存；手动备份不自动清理 |
| 恢复 | 预检、保护快照、同卷 staging、`File.Replace` 切换、失败回退；回退失败阻断写入 |
| 故障测试 | 磁盘满、只读库、取消、损坏包、staging/切换/回退失败、留存删除失败、目录不可用、写入竞争 |

## 仓库结构

```text
QuickNotes_AgentHandoff/
├─ src/
│  ├─ QuickNotes.Domain/                 实体、枚举、业务约束
│  ├─ QuickNotes.Application/            保存/备份/恢复用例与契约
│  ├─ QuickNotes.Infrastructure.Sqlite/  schema、仓储、快照、恢复存储
│  ├─ QuickNotes.Infrastructure.Drafts/  草稿文件存储
│  ├─ QuickNotes.Infrastructure.Backup/  ZIP 包、校验、目录、留存、状态
│  ├─ QuickNotes.Desktop.Core/           路径/单实例/启动状态/恢复/持久阻断
│  └─ QuickNotes.Desktop.Wpf/            WPF 启动诊断宿主
├─ tests/
│  ├─ QuickNotes.UnitTests/              纯单元测试
│  ├─ QuickNotes.IntegrationTests/       真实 SQLite/文件系统/故障注入测试
│  └─ QuickNotes.Desktop.Tests/          桌面宿主/单实例/启动恢复测试
├─ docs/                                 产品、Agent、计划、ADR、阶段总结
└─ QuickNotes.DataPrototype.sln
```

## 文档导航

- 产品基线：[PRD v1.0](docs/product/PRD_v1.0.md)
- B 阶段任务与验收：[Agent 对接文档](docs/agent/Agent对接文档_B阶段.md)
- 阶段完成总结：[B1](docs/agent/B1-完成总结.md)、[B2](docs/agent/B2-完成总结.md)、[B3](docs/agent/B3-完成总结.md)、[B4](docs/agent/B4-完成总结.md)、[B5](docs/agent/B5-完成总结.md)、[B6](docs/agent/B6-完成总结.md)、[B7](docs/agent/B7-完成总结.md)、[C1](docs/agent/C1-完成总结.md)、[C2](docs/agent/C2-完成总结.md)、[C3](docs/agent/C3-完成总结.md)
- C0 基线：[环境与基线报告](docs/agent/C0-环境与基线报告.md)、[PRD 覆盖表](docs/agent/C阶段-PRD覆盖表.md)、[集成设计与风险](docs/agent/C0-集成设计与风险.md)
- C1 证据：[测试报告](docs/agent/C1-测试报告.md)、[人工检查清单](docs/agent/C1-人工检查清单.md)
- C2 证据：[测试报告](docs/agent/C2-测试报告.md)、[人工检查清单](docs/agent/C2-人工检查清单.md)
- C3 证据：[测试报告](docs/agent/C3-测试报告.md)、[字段与恢复策略](docs/agent/C阶段-字段与恢复策略.md)
- 数据模型与备份格式：[B7 数据模型与备份格式](docs/agent/B7-数据模型与备份格式.md)
- 技术与范围决策：[ADR 索引](docs/decisions/README.md)
- 测试证据：[B7 测试报告](docs/agent/B7-测试报告.md)
- 限制与后续：[B7 已知限制与后续建议](docs/agent/B7-已知限制与后续建议.md)
- 实施计划：[B7 交付计划](docs/superpowers/plans/2026-10-28-b7-delivery.md)（其余计划在 `docs/superpowers/plans/`）

## 安全与范围说明

- 原型按“数据正确性、可恢复性优先”设计；任何失败路径都不得报告成功或覆盖唯一有效数据。
- 备份包未加密；不要将真实生产数据直接用于本原型测试。
- C1 为单进程假设，不包含跨进程写入锁或 WAL；单实例只防止同一用户/数据根的第二实例打开数据。
- C1 恢复产物默认保留、不自动清理；阻断状态必须通过显式验证/恢复入口解除，重启不会自动解除。
- C2 草稿只保证已成功落盘的内容；强杀/断电可能丢失最后约 300ms 输入，真实故障边界留 C6。
- C3 迁移保护快照与备份包未加密；设备设置文件不进入备份；C4/C5 才提供备份/恢复 UI、设置页与多屏适配。
- 备份目录/恢复 UI、分类标签与生命周期 UI、托盘/全局快捷键、导出与性能/长时间稳定性测试留 C4–C7。
