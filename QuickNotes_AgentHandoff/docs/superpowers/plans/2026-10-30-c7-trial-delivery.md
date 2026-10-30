# C7 试用交付与技术评审 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 产出可构建、可运行的 Windows 11 x64 试用构建与完整交付文档，并在 C6 阻塞项未关闭的前提下给出“附条件保留、不得整体通过”的技术评审结论。

**Architecture:** C7 只做交付与验证，不修改 `src/` 生产行为，不新增功能。以当前 `feature/c7-trial-delivery`（基线 `feature/c6-fault-stability` / `0c04b0d`）为交付基线；用 Release 框架依赖发布 + 临时数据根 WPF 冒烟证明试用构建可运行；用已有 C1–C6 证据、全量回归和漏洞审计支撑报告。所有文档必须把 C6 的 2h、F01/F04/F05/F06、A-11、UI 级恢复检查保留为已知限制/阻塞，不得写“C 阶段整体通过”。

**Tech Stack:** .NET 10 SDK、WPF、SQLite（Microsoft.Data.Sqlite 10.0.0）、xUnit、PowerShell 7、Windows 11 x64。

**Spec:** [Agent 对接文档 C 阶段](../../agent/Agent对接文档_C阶段.md) 的 C7 节；[C 阶段 PRD 覆盖表](../../agent/C阶段-PRD覆盖表.md)；[C6 完成总结](../../agent/C6-完成总结.md)。

## Global Constraints

- 只在 `D:\GitHub\QuickNotes-worktrees\c7-trial-delivery` 工作；C7 分支基于 `feature/c6-fault-stability` 的 `0c04b0d`，不合并、不推送 C7，除非用户另行要求。
- 不修改 `src/`；C7 交付物是试用构建、文档、证据和技术评审。
- 试用构建与冒烟只使用 `%TEMP%\QuickNotes-C7\<RunId>` 下的合成数据；不触碰真实用户数据根、HKCU Run 或真实恢复。正常 App 冒烟会短暂创建托盘/注册默认热键，进程退出即释放；不做托盘常驻或热键人工验证。
- 不把未经执行的项写成 PASS；通过/失败/跳过/未运行必须分开列。
- 当前已验证基线：构建 0 警告/0 错误；默认全量 **660 通过 / 7 跳过 / 0 失败**；C6 聚焦 **150 通过 / 7 跳过**；UI 启动 p95 **2,787.93 ms PASS**（settle 30 s + 20 次）；2h 长稳 **NOT RUN**；C6-F01/F04/F05/F06 **NOT RUN**；A-11 **未重测**。
- C7 技术评审结论只能是“附条件保留/继续评估”，不得冻结为完整发布，除非 C6 阻塞项关闭或用户书面接受风险。
- 文档之间必须保持同一事实口径；出现“C 阶段整体通过”“C6 已通过”“C7 未开始”即为阻断性文档缺陷。

---

### Task 1: 试用构建、发布与冒烟

**Files:**
- Create (ignored evidence): `.superpowers/sdd/2026-10-30-c7-trial-delivery/task-1-publish/publish.log`, `smoke-normal/`, `smoke-startup/`
- Modify: none in `src/`
- Test: 现有全量测试（Task 6 复跑）

**Interfaces:**
- Consumes: `src/QuickNotes.Desktop.Wpf/QuickNotes.Desktop.Wpf.csproj`、`--data-root`、`--startup`
- Produces: 试用构建发布目录、normal/`--startup` 冒烟证据、运行入口与依赖结论（供 Task 2/4 文档引用）

- [ ] **Step 1: Release 框架依赖发布**

Run:
```powershell
$root = "D:\GitHub\QuickNotes-worktrees\c7-trial-delivery"
$out = "$root\.superpowers\sdd\2026-10-30-c7-trial-delivery\task-1-publish\framework-dependent"
dotnet publish "$root\QuickNotes_AgentHandoff\src\QuickNotes.Desktop.Wpf\QuickNotes.Desktop.Wpf.csproj" `
  -c Release -r win-x64 --self-contained false -o $out 2>&1 |
  Tee-Object "$root\.superpowers\sdd\2026-10-30-c7-trial-delivery\task-1-publish\publish.log"
```
Expected: exit 0；输出含 `QuickNotes.Desktop.Wpf.exe`/`.dll`；记录目录大小与 `dotnet --list-runtimes` 的 WindowsDesktop 版本。

- [ ] **Step 2: 普通启动冒烟（临时数据根）**

Run:
```powershell
$runId = "c7-smoke-normal-" + [guid]::NewGuid().ToString("N")
$data = Join-Path $env:TEMP ("QuickNotes-C7\" + $runId)
$exe = "D:\GitHub\QuickNotes-worktrees\c7-trial-delivery\.superpowers\sdd\2026-10-30-c7-trial-delivery\task-1-publish\framework-dependent\QuickNotes.Desktop.Wpf.exe"
$p = Start-Process -FilePath $exe -ArgumentList @("--data-root", $data) -PassThru
Start-Sleep -Seconds 12
$p.Refresh()
[pscustomobject]@{
  alive = -not $p.HasExited
  title = $p.MainWindowTitle
  state = Get-Content (Join-Path $data "application-state.json") -Raw
} | ConvertTo-Json -Depth 4 | Tee-Object "D:\GitHub\QuickNotes-worktrees\c7-trial-delivery\.superpowers\sdd\2026-10-30-c7-trial-delivery\task-1-publish\smoke-normal.json"
Stop-Process -Id $p.Id -Force
```
Expected: `alive=true`；`title` 为 QuickNotes；`state` 含 `"initializationStatus": "Ready"`、`"schemaVersion": 2`；日志无 ERROR。

- [ ] **Step 3: `--startup` 冒烟（临时数据根）**

Run: 同上，但参数为 `--data-root <data> --startup`，输出 `smoke-startup.json`。Expected: `alive=true`；无管理中心窗口标题；state Ready/schema 2；日志无 ERROR。

- [ ] **Step 4: 记录自包含发布口径（不强制执行）**

Run（若网络/运行时包可用则执行，否则只记录命令与“未运行”原因）:
```powershell
dotnet publish "<QuickNotes.Desktop.Wpf.csproj>" -c Release -r win-x64 --self-contained true `
  -o "<...>\task-1-publish\self-contained"
```
Expected: 文档写明框架依赖与自包含两种入口、运行时要求、目录大小；未执行时明确标记 NOT RUN，不写成可用。

- [ ] **Step 5: 清理临时数据根**

Run:
```powershell
Remove-Item -LiteralPath $data -Recurse -Force
```
Expected: 不删除 `.superpowers` 下的冒烟证据；不触碰真实数据根。

### Task 2: README 与试用/恢复指南

**Files:**
- Modify: `README.md`（根）
- Modify: `QuickNotes_AgentHandoff/README.md`
- Create: `QuickNotes_AgentHandoff/docs/agent/C7-试用与恢复指南.md`
- Test: 文档命令逐条核对（Task 6）

**Interfaces:**
- Consumes: Task 1 的发布路径、入口、运行时依赖、冒烟结论
- Produces: 用户可按 README 构建/运行/试用的入口；诊断与安全恢复指引

- [ ] **Step 1: 更新根 README**

写清：C7 当前状态（条件交付、C6 阻塞未关闭）、试用构建命令（框架依赖/自包含）、运行入口、临时数据根要求、核心试用步骤、备份/恢复入口、已知限制链接。删除“C7 未开始”并禁止写“C 阶段整体通过”。

- [ ] **Step 2: 更新项目 README**

补充：C7 验证结果/限制、`dotnet publish` 命令、`--data-root`/`--startup` 入口、诊断日志位置、恢复/阻断的安全操作、测试与 C6 证据链接。

- [ ] **Step 3: 新建试用与恢复指南**

章节至少包含：环境要求、构建/发布、首次运行、核心使用闭环（新建/编辑/管理/归档恢复）、备份目录与手动备份、恢复前检查、恢复中断/回退失败阻断的处理、诊断日志与数据目录、清理临时数据、不得操作真实数据根。

- [ ] **Step 4: 命令核对**

Run:
```powershell
dotnet build QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln -c Release
dotnet run --project QuickNotes_AgentHandoff/src/QuickNotes.Desktop.Wpf/QuickNotes.Desktop.Wpf.csproj -c Release -- --data-root "$env:TEMP\QuickNotes-C7\readme-check"
```
Expected: 文档命令可执行；只使用临时数据根；退出/清理后不保留真实副作用。

### Task 3: 数据模型、备份格式兼容与字段策略更新

**Files:**
- Modify: `QuickNotes_AgentHandoff/docs/agent/C阶段-字段与恢复策略.md`
- Modify: `QuickNotes_AgentHandoff/docs/agent/B7-数据模型与备份格式.md`（只加 C7 状态注记）
- Create: none
- Test: 文档矩阵与 C3/C4/C5/C6 证据逐项对照（Task 6）

**Interfaces:**
- Consumes: C3 字段策略/兼容矩阵、B7 数据模型/备份格式、C4 恢复、C5 设备设置、C6 故障矩阵
- Produces: C7 交付时的数据模型/备份格式/迁移/草稿边界/设置包含策略统一说明

- [ ] **Step 1: 更新字段策略状态**

把 `C4/C5 接线未实现`、`真实强杀/卷满/ACL/性能/多屏未运行` 等过期状态改为 C7 当前事实：C4/C5 已实现；C6 已执行/未运行项按实际列出；设备设置不进备份、草稿不进备份、保护快照本地保留。

- [ ] **Step 2: 更新兼容矩阵与备份格式注记**

保留 schema v1/v2 兼容矩阵，补 C7 结论：当前应用 schema 2；包 manifest format 1/schema 1–2；未来版本拒绝；恢复轮换数据库身份；WAL 不支持；备份不加密；设备设置/草稿/迁移保护快照不进入备份。B7 文档加 C7 注记指向字段策略与 C7 报告，不重写历史 B 阶段内容。

- [ ] **Step 3: 文档交叉检查**

Run:
```powershell
rg -n "C4/C5 接线（备份 UI、设置页、托盘、多屏） \| 未实现|真实强杀/卷满/ACL/性能/多屏 \| 未运行" QuickNotes_AgentHandoff/docs
```
Expected: 无过期状态；所有“未运行”都能对应 C6 报告的真实 NOT RUN 项。

### Task 4: C7 测试报告、已知限制与 C 阶段完成总结

**Files:**
- Create: `QuickNotes_AgentHandoff/docs/agent/C7-测试报告.md`
- Create: `QuickNotes_AgentHandoff/docs/agent/C7-已知限制与风险.md`
- Create: `QuickNotes_AgentHandoff/docs/agent/C阶段-完成总结.md`
- Modify: `QuickNotes_AgentHandoff/docs/agent/C阶段-PRD覆盖表.md`
- Modify: `QuickNotes_AgentHandoff/docs/agent/Agent对接文档_C阶段.md`（C7 状态块）
- Test: Task 6 全量回归与文档一致性

**Interfaces:**
- Consumes: C1–C6 测试报告/矩阵/性能报告、Task 1 试用构建冒烟、Task 3 数据兼容说明
- Produces: C7 交付事实、PRD 覆盖、C 阶段完成总结、下一阶段建议

- [ ] **Step 1: 写 C7 测试报告**

必须分表列出：构建/发布、全量测试（660/7/0）、C6 聚焦（150/7）、漏洞审计、WPF normal/`--startup` 冒烟、C5-M 用户确认、C6 自动化/真实故障/性能/长稳。每项写命令、结果、证据路径、PASS/FAIL/SKIP/NOT RUN。UI、注入式故障、真实故障、性能、人工验证分开列，不合并。

- [ ] **Step 2: 写已知限制与风险**

至少覆盖：2h 长稳 NOT RUN；F01/F04/F05/F06 NOT RUN；A-11 未重测；C6-M02/M03 UI 视觉未执行；首次冷启动 5,406.84 ms 诊断风险；C5-M 逐项日志未入库；无导出/安装器/OS 通知/自动更新/云存储；无 WAL/多实例写入；备份未加密；单进程假设；恢复阻断需人工；真实断电/所有硬件未覆盖。每项写明影响、当前处置、C7/下一阶段建议。

- [ ] **Step 3: 写 C 阶段完成总结**

按 C1–C7 逐阶段列交付与状态；明确 C 阶段整体状态为“条件交付/未整体通过”；给出下一阶段建议（关闭 C6 2h/真实 VHD/A-11/UI 人工项，再考虑冻结或发布；安装器/导出/加密/WAL 另立范围）。不得用“已完成”吞掉 NOT RUN。

- [ ] **Step 4: 更新 PRD 覆盖表与 Agent 文档**

C7 状态写“条件交付、技术评审附条件保留、C 阶段未整体通过”；C6 性能 UI PASS/2h NOT RUN、存储 F01/F04-F06 NOT RUN、A-11 未重测保持；链接 C7 报告/总结/ADR。

### Task 5: 技术评审 ADR 与索引

**Files:**
- Create: `QuickNotes_AgentHandoff/docs/decisions/2026-10-30-c7-trial-delivery-and-technical-review.md`
- Modify: `QuickNotes_AgentHandoff/docs/decisions/README.md`
- Test: ADR 索引链接与结论一致性（Task 6）

**Interfaces:**
- Consumes: Task 4 的报告/限制/总结
- Produces: `.NET 10 + WPF + SQLite` 的阶段技术结论与冻结条件

- [ ] **Step 1: 写 C7 ADR**

决策：**附条件保留（conditional retain）**，不是冻结、不是完整发布。依据：核心数据/恢复闭环与 C6 自动化证据成立；C6 2h/F01/F04-F06/A-11/UI 级恢复检查未关闭；首次冷启动尾部超 3 s。写明冻结条件：C6 阻塞关闭或书面风险接受、2h PASS、真实 VHD/ACL 补跑、A-11 重测、UI 级 M02/M03 人工通过、冷启动预算决策。写明后果：C7 只交付试用构建和文档，不宣称发行级质量。

- [ ] **Step 2: 更新 ADR 索引**

新增 C7 行，状态“附条件保留/未冻结”；同步 C6 行状态为“UI 启动 PASS、2h 与真实存储故障未关闭”。

- [ ] **Step 3: 文档链接检查**

Run:
```powershell
rg -n "C7|附条件|条件交付|整体通过" QuickNotes_AgentHandoff/docs/decisions/README.md QuickNotes_AgentHandoff/docs/agent/C7-*.md QuickNotes_AgentHandoff/docs/agent/C阶段-完成总结.md
```
Expected: 结论一致；无“已冻结/整体通过”。

### Task 6: 全量验证、双轴审查与提交

**Files:**
- Modify: none（只允许修复审查发现的 C7 文档/计划）
- Test: 全量套件、漏洞审计、Release 构建、WPF 冒烟、文档一致性

**Interfaces:**
- Consumes: Task 1–5 全部交付
- Produces: 可复现的 C7 验证日志、双轴审查结论、本地提交

- [ ] **Step 1: Release 构建与全量测试**

Run:
```powershell
dotnet build QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln -c Release --nologo -v:q
dotnet test QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln --no-build -c Release --nologo -v:q
```
Expected: 0 警告/0 错误；660 通过/7 跳过/0 失败。若 Release 测试因环境失败，保留输出并按实际状态记录，不改写为 PASS。

- [ ] **Step 2: 漏洞审计与 C6 聚焦**

Run:
```powershell
dotnet list QuickNotes_AgentHandoff/QuickNotes.DataPrototype.sln package --vulnerable --include-transitive
dotnet test QuickNotes_AgentHandoff/tests/QuickNotes.IntegrationTests/QuickNotes.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~C6" -v:q
```
Expected: 12 个项目无已知漏洞包；C6 聚焦 150 通过/7 跳过/0 失败。

- [ ] **Step 3: 文档一致性审计**

Run:
```powershell
rg -n "C 阶段整体通过|C6 已通过|C7 未开始|C5-M 未运行|性能聚合 FAIL|4,506.73/4,411.15" README.md QuickNotes_AgentHandoff/README.md QuickNotes_AgentHandoff/docs
```
Expected: 无禁止表述；历史报告中的失败数字只能出现在“历史失败保留”语境。

- [ ] **Step 4: 双轴审查**

标准轴：文档命令可执行、无虚假 PASS、无生产代码变更、证据路径可追溯。规格轴：C7 八项工作/验收逐项对照；C6 开放项没有被吞掉。发现问题先修复并重跑相关验证。

- [ ] **Step 5: 提交**

```bash
git add README.md QuickNotes_AgentHandoff/README.md \
  QuickNotes_AgentHandoff/docs/agent/C7-*.md \
  QuickNotes_AgentHandoff/docs/agent/C阶段-完成总结.md \
  QuickNotes_AgentHandoff/docs/agent/C阶段-PRD覆盖表.md \
  QuickNotes_AgentHandoff/docs/agent/C阶段-字段与恢复策略.md \
  QuickNotes_AgentHandoff/docs/agent/B7-数据模型与备份格式.md \
  QuickNotes_AgentHandoff/docs/agent/Agent对接文档_C阶段.md \
  QuickNotes_AgentHandoff/docs/decisions/2026-10-30-c7-trial-delivery-and-technical-review.md \
  QuickNotes_AgentHandoff/docs/decisions/README.md \
  QuickNotes_AgentHandoff/docs/superpowers/plans/2026-10-30-c7-trial-delivery.md
git commit -m "docs: 完成 C7 试用交付与技术评审"
```
Expected: 本地提交成功；不推送、不合并，除非用户另行要求。

## 自审

- **Spec 覆盖：** 试用构建/运行依赖/入口 → Task 1–2；README/试用/诊断/恢复 → Task 2；数据模型/备份格式/兼容/迁移/草稿/设置 → Task 3；PRD 覆盖/测试报告/限制/C 阶段总结/建议 → Task 4；技术评审/冻结结论 → Task 5；全量验证/审查/提交 → Task 6。
- **状态口径：** 全篇保持 C6 2h/F01/F04-F06/A-11/UI 级检查未关闭；C7 只条件交付。
- **无生产代码变更：** `src/` 不在任何任务的修改列表。
- **证据：** 试用构建与冒烟写 `.superpowers/.../task-1-publish/`；验证日志写 `.superpowers/.../task-6-verification/`。
