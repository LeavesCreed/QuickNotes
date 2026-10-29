# C5 共享会话与桌面行为决策

**日期：** 2026-10-30\
**状态：** 已接受（随 C5 实施记录）\
**范围：** C5 管理中心、独立便签窗口、托盘/启动、全局隐藏、快捷键与窗口找回

## 决策

1. **共享编辑会话**
   - 同一 NoteId 在管理中心与独立便签窗口共享一个 `NoteEditorSession`。
   - `EditorSessionRegistry` 负责引用计数；最后一个引用释放时才 Dispose。
   - 新建未落库会话由 `NoteWindowSyncPlanner`/`NoteWindowManager` 保留窗口；保存后转为普通窗口。
   - 恢复前 `WorkspaceWindow` 与 `NoteWindowManager` 都必须先保存/落草稿；宿主刷新时销毁旧 Registry。

2. **独立便签窗口**
   - `NoteWindow` 使用 `ShowInTaskbar=false` + `WS_EX_TOOLWINDOW`，不进入任务栏/Alt+Tab。
   - 激活后必须显式把键盘焦点移到正文编辑框；“窗口激活”不等于“可键盘输入”。
   - 顶部动作按 `ApplicationSettings.TopBar` 全局配置显示；分类/颜色/字号可直接显示，也保留在“更多”菜单。
   - 设置变更后必须刷新已有窗口，不能只影响新建窗口。

3. **窗口生命周期与共享会话**
   - 便签窗口 X/Ctrl+W 只隐藏单张便签：更新 `display_intent=Hidden` 并保存。
   - 归档/回收站不显示桌面窗口；恢复默认 Active+Hidden。
   - 窗口同步定时器保留未落库新便签窗口；完整保存或隐藏后才转为普通生命周期。

4. **托盘、关闭与启动**
   - 使用 WinForms `NotifyIcon`；左键/双击打开管理中心，右键菜单提供新建/管理中心/全局隐藏/设置/退出。
   - 管理中心 X 三选项：退出应用、缩小到托盘、取消；退出前保存当前便签。
   - `--startup`/`--background` 不显示管理中心；普通启动显示。
   - 开机启动默认关闭，只有用户显式设置才写 HKCU Run；通过抽象注册表提供可测状态，失败不伪装成功。

5. **全局隐藏**
   - `ApplicationSettings.GlobalHidden` 只控制桌面窗口可见性，不修改单张 `display_intent`。
   - 可见条件为 `!GlobalHidden && lifecycle=Active && display_intent=Visible`；全局恢复只恢复原本 Visible。

6. **快捷键**
   - 使用 Win32 `RegisterHotKey`；动作固定为新便签/管理中心/全局隐藏。
   - `HotkeyGesture` 在 Core 解析；`GlobalHotkeyRegistrationManager` 统一处理禁用、重复与注册失败。
   - 配置存 `device-settings.json`；修改/禁用后重新注册；失败逐项显示，不显示虚假成功。

7. **窗口几何与设备边界**
   - 便签布局以逻辑 DIP 保存；显示时经 `WindowPlacementCalculator` clamp 到工作区。
   - 显示器缺失时按窗口中心距离选择最近工作区；屏幕变化后重新应用并允许再次保存。
   - `DisplayBindings` 属于设备设置，不进入备份；跨设备恢复不覆盖本机显示绑定。

## 后果与限制

- 共享会话使两处编辑即时可见，但会话/窗口释放顺序必须严格；恢复和全局隐藏前需要保存草稿。
- 托盘、HKCU Run、真实快捷键冲突、输入法、多屏/混合 DPI 与断屏矩阵需要实机人工证据；当前 C5-M 仍未执行，不能以自动化代替。
- 20 窗口、1,000 历史便签、强杀/断电/卷满/权限故障和性能预算属于 C6。
- OS 通知、导出、安装器/卸载仍不在 C5 代码交付范围。

## 证据

- [C5 实现计划](../superpowers/plans/2026-10-28-c5-management-desktop.md)
- [C5 测试报告](../agent/C5-测试报告.md)
- [C5 完成总结](../agent/C5-完成总结.md)
- [C5 设备与人工矩阵](../agent/C5-设备与人工矩阵.md)
