# QuickNotes

面向 Windows 11 x64 的桌面便签软件。当前处于 **C 阶段**：B0–B7、C0–C5 已实现并合并；C5-M 人工矩阵已由用户实机确认通过；C6（真实故障/性能/稳定性）自动化与故障矩阵已完成，但**验收不通过/条件未满足**（UI 启动 p95 PASS、性能聚合 NOT RUN、2h 长稳未运行、4 项存储故障未运行、A-11 未重测）；C7（试用交付/评审）尚未开始。OS 通知、导出、安装器仍未实现。

## 快速入口

- 详细运行说明、构建/测试命令与文档导航：[QuickNotes_AgentHandoff/README.md](QuickNotes_AgentHandoff/README.md)
- 产品基线：[PRD v1.0](QuickNotes_AgentHandoff/docs/product/PRD_v1.0.md)
- 阶段任务：[B 阶段](QuickNotes_AgentHandoff/docs/agent/Agent对接文档_B阶段.md)、[C 阶段](QuickNotes_AgentHandoff/docs/agent/Agent对接文档_C阶段.md)
- 阶段总结：[B7](QuickNotes_AgentHandoff/docs/agent/B7-完成总结.md)、[C1](QuickNotes_AgentHandoff/docs/agent/C1-完成总结.md)、[C2](QuickNotes_AgentHandoff/docs/agent/C2-完成总结.md)、[C3](QuickNotes_AgentHandoff/docs/agent/C3-完成总结.md)、[C4](QuickNotes_AgentHandoff/docs/agent/C4-完成总结.md)、[C5](QuickNotes_AgentHandoff/docs/agent/C5-完成总结.md)、[C6](QuickNotes_AgentHandoff/docs/agent/C6-完成总结.md)
- 数据模型与备份格式：[说明](QuickNotes_AgentHandoff/docs/agent/B7-数据模型与备份格式.md)、[C3 字段与恢复策略](QuickNotes_AgentHandoff/docs/agent/C阶段-字段与恢复策略.md)
- C1 证据：[测试报告](QuickNotes_AgentHandoff/docs/agent/C1-测试报告.md)、[人工检查清单](QuickNotes_AgentHandoff/docs/agent/C1-人工检查清单.md)
- C2 证据：[测试报告](QuickNotes_AgentHandoff/docs/agent/C2-测试报告.md)、[人工检查清单](QuickNotes_AgentHandoff/docs/agent/C2-人工检查清单.md)
- C3 证据：[测试报告](QuickNotes_AgentHandoff/docs/agent/C3-测试报告.md)
- C4 证据：[测试报告](QuickNotes_AgentHandoff/docs/agent/C4-测试报告.md)、[人工检查清单](QuickNotes_AgentHandoff/docs/agent/C4-人工检查清单.md)、[错误 UX 清单](QuickNotes_AgentHandoff/docs/agent/C4-错误UX清单.md)
- C5 证据：[测试报告](QuickNotes_AgentHandoff/docs/agent/C5-测试报告.md)、[完成总结](QuickNotes_AgentHandoff/docs/agent/C5-完成总结.md)、[设备与人工矩阵](QuickNotes_AgentHandoff/docs/agent/C5-设备与人工矩阵.md)、[错误 UX 清单](QuickNotes_AgentHandoff/docs/agent/C5-错误UX清单.md)
- C6 证据：[测试报告](QuickNotes_AgentHandoff/docs/agent/C6-测试报告.md)、[故障矩阵](QuickNotes_AgentHandoff/docs/agent/C6-故障矩阵.md)、[性能与稳定性报告](QuickNotes_AgentHandoff/docs/agent/C6-性能与稳定性报告.md)、[完成总结](QuickNotes_AgentHandoff/docs/agent/C6-完成总结.md)、[C6-M 人工检查清单](QuickNotes_AgentHandoff/docs/agent/C6-M-人工检查清单.md)
- 限制与后续：[B7 已知限制](QuickNotes_AgentHandoff/docs/agent/B7-已知限制与后续建议.md)

## 构建、测试与运行

```powershell
dotnet build QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln
dotnet test QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln --no-build --no-restore
dotnet run --project QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/QuickNotes.Desktop.Wpf.csproj -- --data-root "$env:TEMP\QuickNotes-C5-Demo"
```

当前基线（C6 分支 `feature/c6-fault-stability`，代码 HEAD `f4c3031`）：**660 通过 / 7 跳过 / 0 失败**（单元 160 + 集成 337 + 桌面 163）；构建 0 警告、0 错误；NuGet 12 个项目无已知漏洞包。C6 已完成 Harness、隔离存储故障、检查点强杀、恢复不变量与性能/长稳门禁；C6-F02/F03 PASS、C6-K01–K09 PASS、C6-M01–M05 自动化 PASS、C6-M06 用户实机确认 PASS、C5-M11–M25 用户实机确认通过，但 **C6-F01/F04/F05/F06 NOT RUN、2h 长稳 NOT RUN、A-11 未重测**；UI 启动 p95 已 PASS（2,787.93 ms，F-C6-PERF-01 关闭），性能聚合仍为 NOT RUN，因此 **C6 未通过验收，C7 未开始**。测试、冒烟与宿主均只使用临时数据根。

## C6 状态与 C7 入口

C6 不是“全部通过”：UI 启动 p95 已 PASS（settle 30 s + 20 次，2,787.93 ms vs 3,000 ms；F-C6-PERF-01 关闭为门禁方法缺陷），但 2 小时长稳未运行，存储 F01/F04/F05/F06 未运行，A-11 未重测，性能聚合仍为 NOT RUN。C5-M/C6-M06 已由用户实机确认通过，不再是入口阻塞项。C7 开始前必须关闭或书面风险接受剩余事项：2h 长稳、F01/F04/F05/F06、A-11、UI 级 C6-M02/M03。任何生产代码变更后需重跑全量测试、漏洞审计、WPF 冒烟与相关 C6 门禁。详见 [C6 完成总结](QuickNotes_AgentHandoff/docs/agent/C6-完成总结.md)。
