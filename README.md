# QuickNotes

面向 Windows 11 x64 的桌面便签软件。当前处于 **C 阶段**：B 数据/备份层、C0/C1 桌面宿主/启动安全、C2 最小编辑/保存/草稿闭环已实现；**尚无 schema v2、完整设置、备份管理 UI、托盘或安装器**。

## 快速入口

- 详细运行说明、构建/测试命令与文档导航：[QuickNotes_AgentHandoff/README.md](QuickNotes_AgentHandoff/README.md)
- 产品基线：[PRD v1.0](QuickNotes_AgentHandoff/docs/product/PRD_v1.0.md)
- 阶段任务：[B 阶段](QuickNotes_AgentHandoff/docs/agent/Agent对接文档_B阶段.md)、[C 阶段](QuickNotes_AgentHandoff/docs/agent/Agent对接文档_C阶段.md)
- 阶段总结：[B7 完成总结](QuickNotes_AgentHandoff/docs/agent/B7-完成总结.md)、[C1 完成总结](QuickNotes_AgentHandoff/docs/agent/C1-完成总结.md)、[C2 完成总结](QuickNotes_AgentHandoff/docs/agent/C2-完成总结.md)
- 数据模型与备份格式：[说明](QuickNotes_AgentHandoff/docs/agent/B7-数据模型与备份格式.md)
- C1 证据：[测试报告](QuickNotes_AgentHandoff/docs/agent/C1-测试报告.md)、[人工检查清单](QuickNotes_AgentHandoff/docs/agent/C1-人工检查清单.md)
- C2 证据：[测试报告](QuickNotes_AgentHandoff/docs/agent/C2-测试报告.md)、[人工检查清单](QuickNotes_AgentHandoff/docs/agent/C2-人工检查清单.md)
- 限制与后续：[B7 已知限制](QuickNotes_AgentHandoff/docs/agent/B7-已知限制与后续建议.md)

## 构建、测试与启动诊断宿主

```powershell
dotnet build QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln
dotnet test QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln --no-build --no-restore
dotnet run --project QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/QuickNotes.Desktop.Wpf.csproj -- --data-root "$env:TEMP\QuickNotes-C2-Demo"
```

当前基线：353/353 测试通过（单元 120 + 集成 128 + 桌面 105）；构建 0 警告、0 错误；NuGet 无已知漏洞包。Ready 时直接进入最小工作区（列表、编辑、自动保存、草稿恢复）；未 Ready 时显示启动/诊断/恢复窗口。C2 人工检查 M01–M12、M15、M16 已通过；M13/M14 未执行；C5/C6 未运行。测试、冒烟与宿主均只使用临时数据根。
