# QuickNotes

面向 Windows 11 x64 的桌面便签软件。当前处于 **B 阶段数据与备份原型**：数据层、备份、自动留存、整库恢复与故障注入已实现并通过测试；**尚无产品 UI/安装器**。

## 快速入口

- 详细运行说明、构建/测试命令与文档导航：[QuickNotes_AgentHandoff/README.md](QuickNotes_AgentHandoff/README.md)
- 产品基线：[PRD v1.0](QuickNotes_AgentHandoff/docs/product/PRD_v1.0.md)
- 阶段任务：[Agent 对接文档 B 阶段](QuickNotes_AgentHandoff/docs/agent/Agent对接文档_B阶段.md)
- B7 交付总结：[B7 完成总结](QuickNotes_AgentHandoff/docs/agent/B7-完成总结.md)
- 数据模型与备份格式：[说明](QuickNotes_AgentHandoff/docs/agent/B7-数据模型与备份格式.md)
- 测试证据：[B7 测试报告](QuickNotes_AgentHandoff/docs/agent/B7-测试报告.md)
- 限制与后续：[已知限制与后续建议](QuickNotes_AgentHandoff/docs/agent/B7-已知限制与后续建议.md)

## 构建与测试

```bash
dotnet build QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln
dotnet test QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln --no-build --no-restore
```

当前基线：245/245 测试通过（单元 120 + 集成 125）；构建 0 警告、0 错误；NuGet 无已知漏洞包。测试只使用合成数据与临时目录。
