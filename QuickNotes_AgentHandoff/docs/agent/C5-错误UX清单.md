# C5-错误 UX 清单

> 状态：C5-B 实现与 Task 10 文档/UI 自动化已完成；真实人工检查仍未执行，见 [C5 设备与人工矩阵](C5-设备与人工矩阵.md)。
> 原则：错误提示说明发生了什么、数据是否保留、下一步能做什么；不记录便签正文、草稿内容或完整敏感路径。

| 场景 | 用户看到什么 | 可执行操作 | 实现位置 |
|---|---|---|---|
| 分类删除失败 | 对话框错误信息 | 重试；系统分类不可删除 | `CategoryManagerWindow` |
| 标签合并冲突/失败 | 错误信息 | 重新选择目标；可先重命名再合并 | `TagManagerWindow` |
| 批量部分失败 | 每个 NoteId/状态/错误码 | 逐项重试；已成功的项不回滚 | `WorkspaceWindow.ShowBatchResult` |
| 撤销不完整 | 逐项 Stale/NotFound/NeedsConfirmation | 关闭提示；对副本可选择确认撤销 | `WorkspaceWindow.UndoButton_Click` |
| 保存冲突 | 冲突面板、数据库版本预览 | 重新载入、另存为新便签 | `WorkspaceWindow` |
| 关闭时保存失败 | 草稿仍保留的说明 | 取消关闭、重试保存 | `WorkspaceWindow.CloseFlowAsync` |
| 永久删除 | 不可恢复确认 | 取消或确认 | `WorkspaceWindow.DeleteButton_Click` |
| 快捷键冲突/无效 | 逐项动作与 Win32/重复错误码 | 修改快捷键、留空禁用 | `App.ApplyHotkeysAsync`、`SettingsWindow` |
| 开机启动注册失败 | 实际注册状态与错误码 | 重新保存；管理员权限/策略检查 | `SettingsWindow`、`StartupRegistrationService` |
| 窗口布局找回 | 便签被 clamp 到最近工作区；失败写诊断日志 | 拖动到目标显示器；布局会重新保存 | `WindowPlacementCalculator`、`NoteWindow` |
| 全局隐藏 | 所有桌面便签隐藏，单张 `display_intent` 不变 | 托盘“恢复桌面便签”或快捷键 | `App.ToggleGlobalHiddenAsync` |
| 单张隐藏 | 仅当前便签隐藏 | 管理中心“显示便签”或双击定位 | `WorkspaceWindow.ToggleDisplayButton_Click` |
| 恢复前保存失败 | 恢复取消提示 | 先处理保存失败/冲突，再重试恢复 | `WorkspaceWindow.PrepareForRestoreAsync` |

## 键盘与焦点

| 按键 | 行为 |
|---|---|
| `Ctrl+N` | 管理中心新建便签 |
| `Ctrl+F` | 聚焦搜索框并全选 |
| `Ctrl+S` | 保存当前便签 |
| `Ctrl+W` | 独立便签窗口隐藏（保留草稿与保存状态） |
| `Esc` | 关闭菜单/清空搜索/离开文本框焦点 |
| `Ctrl+Z` | 仅正文/标题 TextBox 的本地撤销，不映射管理撤销 |

人工检查项：

- [ ] 仅用键盘完成新建、保存、切换便签、打开设置、关闭三选项。
- [ ] Tab 顺序覆盖搜索、筛选、列表、编辑区、生命周期/批量操作。
- [ ] 焦点可见，按钮/文本框有可读名称（AutomationProperties.Name）。
- [ ] 错误对话框至少有一个可执行按钮，不能只有无操作提示。
- [ ] 空列表显示空状态说明。
- [ ] 日志中不出现便签正文、草稿内容或完整敏感路径。
