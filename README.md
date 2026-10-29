# QuickNotes

面向 Windows 11 x64 的桌面便签软件。当前处于 **C 阶段**：B0–B7、C0–C5 已实现并合并；C5 包含管理中心、分类/标签/生命周期、独立便签窗口、托盘/启动、全局隐藏、全局快捷键、窗口找回与设置页。C6（真实故障/性能/稳定性）和 C7（试用交付/评审）尚未开始；OS 通知、导出、安装器仍未实现。

## 快速入口

- 详细运行说明、构建/测试命令与文档导航：[QuickNotes_AgentHandoff/README.md](QuickNotes_AgentHandoff/README.md)
- 产品基线：[PRD v1.0](QuickNotes_AgentHandoff/docs/product/PRD_v1.0.md)
- 阶段任务：[B 阶段](QuickNotes_AgentHandoff/docs/agent/Agent对接文档_B阶段.md)、[C 阶段](QuickNotes_AgentHandoff/docs/agent/Agent对接文档_C阶段.md)
- 阶段总结：[B7](QuickNotes_AgentHandoff/docs/agent/B7-完成总结.md)、[C1](QuickNotes_AgentHandoff/docs/agent/C1-完成总结.md)、[C2](QuickNotes_AgentHandoff/docs/agent/C2-完成总结.md)、[C3](QuickNotes_AgentHandoff/docs/agent/C3-完成总结.md)、[C4](QuickNotes_AgentHandoff/docs/agent/C4-完成总结.md)、[C5](QuickNotes_AgentHandoff/docs/agent/C5-完成总结.md)
- 数据模型与备份格式：[说明](QuickNotes_AgentHandoff/docs/agent/B7-数据模型与备份格式.md)、[C3 字段与恢复策略](QuickNotes_AgentHandoff/docs/agent/C阶段-字段与恢复策略.md)
- C1 证据：[测试报告](QuickNotes_AgentHandoff/docs/agent/C1-测试报告.md)、[人工检查清单](QuickNotes_AgentHandoff/docs/agent/C1-人工检查清单.md)
- C2 证据：[测试报告](QuickNotes_AgentHandoff/docs/agent/C2-测试报告.md)、[人工检查清单](QuickNotes_AgentHandoff/docs/agent/C2-人工检查清单.md)
- C3 证据：[测试报告](QuickNotes_AgentHandoff/docs/agent/C3-测试报告.md)
- C4 证据：[测试报告](QuickNotes_AgentHandoff/docs/agent/C4-测试报告.md)、[人工检查清单](QuickNotes_AgentHandoff/docs/agent/C4-人工检查清单.md)、[错误 UX 清单](QuickNotes_AgentHandoff/docs/agent/C4-错误UX清单.md)
- C5 证据：[测试报告](QuickNotes_AgentHandoff/docs/agent/C5-测试报告.md)、[完成总结](QuickNotes_AgentHandoff/docs/agent/C5-完成总结.md)、[设备与人工矩阵](QuickNotes_AgentHandoff/docs/agent/C5-设备与人工矩阵.md)、[错误 UX 清单](QuickNotes_AgentHandoff/docs/agent/C5-错误UX清单.md)
- 限制与后续：[B7 已知限制](QuickNotes_AgentHandoff/docs/agent/B7-已知限制与后续建议.md)

## 构建、测试与运行

```powershell
dotnet build QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln
dotnet test QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln --no-build --no-restore
dotnet run --project QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/QuickNotes.Desktop.Wpf.csproj -- --data-root "$env:TEMP\QuickNotes-C5-Demo"
```

当前基线：510/510 测试通过（单元 160 + 集成 187 + 桌面 163）；构建 0 警告、0 错误；NuGet 10 个项目无已知漏洞包。C5 代码/自动化与 UI 自动化已覆盖管理、同步、生命周期、顶栏与新建焦点；C5-M 真实托盘/快捷键/开机启动/多屏/DPI 与 C4 人工检查仍未执行，C6/C7 未开始。测试、冒烟与宿主均只使用临时数据根。
