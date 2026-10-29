# ADR：C4 恢复交互、编辑冻结与旧会话失效

- **日期：** 2026-10-28
- **状态：** 已接受（C4 实施前候选策略，用户已确认进入 C4 构建）
- **范围：** C4 整库恢复 UI/宿主交互、草稿边界、恢复后刷新
- **相关：** `docs/agent/Agent对接文档_C阶段.md` C4、PRD 11.6–11.7、10.3、B5/C1/C2/C3 ADR、C4 计划

## 背景

B5 已实现恢复预检、保护快照、暂存/切换/回退和写入阻断；C1 提供启动恢复入口；C3 提供 v1/v2 包兼容与迁移。C4 需要在 UI 中安全地协调当前编辑器、在途保存、草稿、进度、取消和恢复后刷新，避免旧内容写回新库。

## 决策

1. **恢复前冻结**：用户确认恢复后，`WorkspaceWindow` 先冻结编辑控件并停止新编辑；调用 `FlushDraftAsync`；若有未保存内容则 `SaveNowAsync`。草稿或保存失败、冲突、结果未知或仍有 pending 时取消恢复并显示原因。
2. **先失效旧会话**：保存/草稿完成后、调用恢复前，`NoteEditorSession.Invalidate()` 取消去抖/lifetime 并拒绝后续 Change/Save/Flush/Retry/Reload/SaveAsNew。恢复失败/回退后，`WorkspaceWindow` 重新打开当前便签的新会话；旧会话不得再写入。
3. **写入门**：`RestoreCoordinator` 继续在预检后取得共享恢复写入门；C4 的冻结/保存发生在恢复执行前，写入门等待任何在途保存。恢复期间自动/手动备份暂停。
4. **草稿**：恢复前已落盘草稿保留；恢复成功后写数据根 `draft-restore-boundary.json`（restoreId、时间、恢复时存在的 draftId 集合），`DraftRecoveryService` 只对这些 ID 标记 `IsPreRestore`；不自动提交、不删除；恢复失败不写边界。
5. **进度**：`RestoreCoordinator` 通过 `IProgress<RestoreProgress>` 报告 Preflight/ProtectionSnapshot/Staging/Migrating/Switching/Verifying/Completed/Failed/RolledBack；切换前可取消，切换后不可取消；发布成功后取消不谎报失败。
6. **成功后刷新**：宿主重新运行 `StartupCoordinator`，重建写入门/编辑/备份服务并刷新 Assessment；旧窗口关闭，新工作区窗口使用新服务。恢复后调度基线由 C3 数据库身份轮换/变更序号失效，不沿用旧基线。
7. **设备设置**：`device-settings.json` 不恢复；备份目录、开机启动、快捷键实际注册、显示器绑定保持本机。
8. **失败/阻断**：回退成功显示“恢复失败且已回退”；回退失败保持 C1 写入阻断，提供诊断/保护快照/数据目录入口；重启仍遵循 C1 检查。OS 通知留 C5/C7。

## 后果

- 恢复中不再有旧编辑/迟到保存覆盖新库；用户需在失败后重新打开当前便签（内容来自当前库/草稿）。
- 草稿标记使用边界 sidecar，不升级草稿格式；新编辑产生的新草稿不在边界集合中。
- 进度真实反映阶段；切换后取消按钮禁用。

## 备选方案

- 恢复成功后再失效会话：存在恢复门释放与失效之间的迟到写入窗口；拒绝。
- 恢复失败后复用旧会话：旧会话已失效，复用会破坏“不得写回”的保证；改为重开。
- 用草稿时间戳判断恢复前来源：时钟/时区不可靠；使用持久化 draftId 集合。
- 升级草稿格式：C3 刚升级 v2，C4 无必要再改格式；sidecar 更小、可回退。

## 验证

- `EditorSessionInvalidationTests`、`RestoreHostIntegrationTests`、`DraftRestoreBoundaryStoreTests`。
- `RestoreCoordinatorTests` 进度阶段/取消边界；E2E 恢复成功后旧会话无法写入。
- 恢复失败/回退/阻断/重启专项测试。
