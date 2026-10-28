# QuickNotes 数据与备份原型（B 阶段）

QuickNotes 是面向 Windows 11 x64 的桌面便签软件。本仓库当前是 **B 阶段数据存储与备份原型**：验证便签数据模型、事务化保存、失败语义、一致性备份、自动留存与整库恢复/回退；**不包含**完整 WPF 界面、托盘、快捷键、安装器或 Rust 集成。

## 当前状态

- B0–B6 已实现并通过自动化测试。
- B7 交付文档已完成：README、数据模型与备份格式说明、ADR 索引、测试报告、已知限制与后续建议。
- 当前交付物是可构建的类库 + 自动化测试，没有可运行的 `.exe` 或 UI 入口；验证入口是测试套件。

## 环境要求

- Windows 11 x64（正式目标；不承诺 Windows 10）。
- .NET SDK 10.0.x。本仓库没有 `global.json`；B7 验证使用 SDK `10.0.402`、Runtime `10.0.13`。
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

测试只使用临时目录与合成数据，不接触真实用户数据。完整结果、未运行项与环境见 [B7 测试报告](docs/agent/B7-测试报告.md)。

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
│  └─ QuickNotes.Infrastructure.Backup/  ZIP 包、校验、目录、留存、状态
├─ tests/
│  ├─ QuickNotes.UnitTests/              纯单元测试
│  └─ QuickNotes.IntegrationTests/       真实 SQLite/文件系统/故障注入测试
├─ docs/                                 产品、Agent、计划、ADR、阶段总结
└─ QuickNotes.DataPrototype.sln
```

## 文档导航

- 产品基线：[PRD v1.0](docs/product/PRD_v1.0.md)
- B 阶段任务与验收：[Agent 对接文档](docs/agent/Agent对接文档_B阶段.md)
- 阶段完成总结：[B1](docs/agent/B1-完成总结.md)、[B2](docs/agent/B2-完成总结.md)、[B3](docs/agent/B3-完成总结.md)、[B4](docs/agent/B4-完成总结.md)、[B5](docs/agent/B5-完成总结.md)、[B6](docs/agent/B6-完成总结.md)、[B7](docs/agent/B7-完成总结.md)
- 数据模型与备份格式：[B7 数据模型与备份格式](docs/agent/B7-数据模型与备份格式.md)
- 技术与范围决策：[ADR 索引](docs/decisions/README.md)
- 测试证据：[B7 测试报告](docs/agent/B7-测试报告.md)
- 限制与后续：[B7 已知限制与后续建议](docs/agent/B7-已知限制与后续建议.md)
- 实施计划：[B7 交付计划](docs/superpowers/plans/2026-10-28-b7-delivery.md)（其余计划在 `docs/superpowers/plans/`）

## 安全与范围说明

- 原型按“数据正确性、可恢复性优先”设计；任何失败路径都不得报告成功或覆盖唯一有效数据。
- 备份包未加密；不要将真实生产数据直接用于本原型测试。
- B 阶段为单进程假设，不包含跨进程恢复锁或 WAL。
- UI、设置持久化、首次运行、schema 迁移、导出与性能/长时间稳定性测试留后续阶段，详见限制文档。
