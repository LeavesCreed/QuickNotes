# ADR 索引

**日期：** 2026-10-28\
**范围：** B/C 阶段技术决策记录导航\
**说明：** 本索引只做导航，不替代各 ADR 正文；状态以各文件为准。

| 主题 | ADR | 状态 | 覆盖的交付要求 |
|---|---|---|---|
| SQLite 访问方式 | [B7 SQLite 访问方式汇总](2026-10-28-b7-sqlite-access.md) | 已接受（汇总 B1–B6） | 驱动/手写 SQL、连接生命周期、PRAGMA、超时重试、deferred 事务、rollback journal、错误语义、单进程假设；细节见 [B2 ADR](2026-10-28-b2-bounded-busy-retry.md) 与 [B3 ADR](2026-10-28-b3-consistency-snapshot-and-package.md) |
| SQLite 重试/事务细节 | [B2 有界 busy 重试改用驱动的秒级 CommandTimeout](2026-10-28-b2-bounded-busy-retry.md) | 已接受（用户确认） | `busy_timeout`、命令超时、deferred 事务、BUSY/LOCKED 重试边界 |
| 备份格式 | [B3 一致性快照与备份包格式](2026-10-28-b3-consistency-snapshot-and-package.md) | 已接受（用户批准 D1–D10） | 在线备份 API、ZIP 双条目、manifest、SHA-256、临时文件与原子发布、4 GiB 上限 |
| 周备份规则 | [B4 自动备份、变更检测与留存](2026-10-28-b4-retention-and-change-detection.md) | 已接受（随 B4 实施记录） | 本机当地日历日、ISO 周（周一开始）、7 日 + 4 周并集、手动包排除、状态文件与指纹 |
| 恢复切换 | [B5 整库恢复、保护快照与回退](2026-10-28-b5-restore-and-rollback.md) | 已接受（随 B5 实施记录） | 预检、保护快照、同卷 staging、`File.Replace` 切换、回退、回退失败阻断写入、共享写入门 |
| 故障注入范围 | [B6 故障注入范围与注入层级](2026-10-28-b6-fault-injection-scope.md) | 已接受（随 B6 实施记录） | 真实 SQLite/文件系统注入与接口装饰器、故障矩阵、真实卷/崩溃边界 |
| B7 交付边界 | [B7 交付范围：文档与验证，不含 UI](2026-10-28-b7-delivery-scope.md) | 已接受（随 B7 实施记录） | README、数据模型/格式说明、测试报告、限制与后续建议；澄清历史文档中的 B7/UI 歧义 |
| C1 宿主/组合根 | [C1 桌面宿主、组合根与 A/B 集成](2026-10-28-c1-host-and-integration.md) | 已接受（随 C1 实施记录） | Core/WPF 边界、组合根、UI 不直接 SQL、线程/生命周期、C1 最小 UI |
| C1 单实例/数据目录 | [C1 单实例、数据目录与日志](2026-10-28-c1-single-instance-and-data-directory.md) | 已接受（随 C1 实施记录） | 数据根锁文件 + 命名管道、`%LOCALAPPDATA%\QuickNotes`、初始化标记、诊断日志、`--data-root`/`--backup-dir` |
| C1 启动恢复/阻断 | [C1 启动恢复状态、持久写入阻断与缺失库重建](2026-10-28-c1-startup-recovery-state.md) | 已接受（随 C1 实施记录） | 启动模式/恢复日志、恢复产物保留、跨重启阻断、缺失/损坏/阻断恢复、无保护快照如实记录 |

相关产品与阶段文档：

- [PRD v1.0](../product/PRD_v1.0.md)
- [Agent 对接文档 B 阶段](../agent/Agent对接文档_B阶段.md)
- [Agent 对接文档 C 阶段](../agent/Agent对接文档_C阶段.md)
- [B7 数据模型与备份格式](../agent/B7-数据模型与备份格式.md)
- [B7 测试报告](../agent/B7-测试报告.md)
- [B7 已知限制与后续建议](../agent/B7-已知限制与后续建议.md)
- [C0 集成设计与风险](../agent/C0-集成设计与风险.md)
- [C1 测试报告](../agent/C1-测试报告.md)
- [C1 完成总结](../agent/C1-完成总结.md)
