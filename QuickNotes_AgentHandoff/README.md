# QuickNotes 数据与备份原型（B 阶段）

QuickNotes 是面向 Windows 11 x64 的桌面便签软件。本仓库当前是 **B 阶段数据存储与备份原型**：验证便签数据模型、事务化保存、失败语义、一致性备份、自动留存与整库恢复/回退；**不包含**完整 WPF 界面、托盘、快捷键、安装器或 Rust 集成。

## 当前状态

- B0–B7 已实现并通过自动化测试；C0 环境/PRD 基线与 C1 桌面宿主/启动安全已实现。
- C1 交付可启动的 WPF 启动/诊断宿主：单实例、正式数据目录、启动恢复状态机、恢复产物识别、缺失库/损坏库/阻断恢复的最小入口。
- 当前仍**没有**便签编辑器、完整设置、备份管理 UI、托盘/快捷键；这些属于 C2–C5。
- 当前交付物是类库 + WPF 启动诊断宿主 + 自动化测试；测试与诊断宿主是当前可执行入口。

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

## 运行 C1 启动/诊断宿主

```powershell
dotnet run --project QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/QuickNotes.Desktop.Wpf.csproj -- --data-root "$env:TEMP\QuickNotes-C1-Demo"
```

- 首次运行会创建 `notes.db`、`application-state.json`、`drafts/`、`logs/`；不提供编辑器。
- 支持 `--backup-dir <dir>` 仅用于诊断扫描保护快照；缺失库/损坏库/阻断恢复可从窗口选择已验证备份。
- 第二实例会尝试激活第一实例；同一数据根只允许一个实例。
- 只使用临时数据根，不要指向真实用户数据。

测试只使用临时目录与合成数据，不接触真实用户数据。完整结果、未运行项与环境见 [B7 测试报告](docs/agent/B7-测试报告.md) 与 [C1 测试报告](docs/agent/C1-测试报告.md)。

## 已实现能力

| 领域 | 能力 |
|---|---|
| 数据模型 | 便签、分类、标签、标签关联；生命周期（正常/归档/回收站）；版本号并发保护；schema v1 |
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
- 阶段完成总结：[B1](docs/agent/B1-完成总结.md)、[B2](docs/agent/B2-完成总结.md)、[B3](docs/agent/B3-完成总结.md)、[B4](docs/agent/B4-完成总结.md)、[B5](docs/agent/B5-完成总结.md)、[B6](docs/agent/B6-完成总结.md)、[B7](docs/agent/B7-完成总结.md)、[C1](docs/agent/C1-完成总结.md)
- C0 基线：[环境与基线报告](docs/agent/C0-环境与基线报告.md)、[PRD 覆盖表](docs/agent/C阶段-PRD覆盖表.md)、[集成设计与风险](docs/agent/C0-集成设计与风险.md)
- C1 证据：[测试报告](docs/agent/C1-测试报告.md)、[人工检查清单](docs/agent/C1-人工检查清单.md)
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
- UI 编辑器、设置持久化、首次备份目录选择、schema 迁移、导出与性能/长时间稳定性测试留 C2–C7。
