# C4 错误 UX 清单

**日期：** 2026-10-28\
**范围：** C4 备份、调度与恢复交互\
**原则：** 任何失败不得显示为成功；失败要持续可见、可重试、可诊断；不删除唯一有效恢复点。

| 场景 | 用户提示 | 可执行操作 | 状态/证据 |
|---|---|---|---|
| 备份目录未配置 | “自动备份未配置；请选择备份目录” | 选择目录/稍后配置 | `BackupRuntimeStatus.AutoBackupConfigured=false` |
| 目录同数据根/不存在/不可写 | 明确原因（overlaps/missing/not_writable） | 重新选择 | `BackupDirectoryValidatorTests` |
| 备份目录离线 | 备份检查失败，保留上次状态 | 重试检查 | `BackupHostService` 失败状态 |
| 手动备份失败 | 显示错误码/消息，不显示成功 | 重试/检查目录 | `BackupOperationResult.Failed` |
| 自动检查失败 | 最近失败时间/错误码；下一周期重试 | 重试检查 | 运行状态持久化 |
| 当日无变化 | “未创建重复备份” | 无需操作 | `DailyBackupOutcome.NoChanges` |
| 包损坏/校验失败 | 校验失败原因，不提供成功恢复 | 选择其他备份 | `BackupVerificationResult.Invalid` |
| 手动删除日备份 | 明确提示“只能删除手动备份” | 取消/选择手动包 | `InvalidTarget` |
| 恢复预检失败 | 包/版本/空间/草稿/目录原因，原库不变 | 返回列表/检查 | `RestorePreflightResult.Error` |
| 当前编辑保存/草稿失败 | “恢复前无法保存，恢复已取消” | 处理保存/冲突后重试 | `WorkspaceWindow.PrepareForRestoreAsync=false` |
| 恢复取消（切换前） | 当前库未替换 | 继续编辑/重新选择 | `OperationCanceledException` |
| 恢复切换失败并回退 | “恢复失败，已回退” | 查看保护快照/重试 | `RestoreResult.RolledBack=true` |
| 回退失败 | “数据库写入已阻断”，显示保护快照/数据目录 | 重启进入诊断/恢复入口 | `WritesBlocked=true`，跨重启 |
| 恢复后旧会话迟到保存 | 拒绝写入，旧窗口关闭 | 使用新工作区 | `NoteEditorSession.Invalidate` |
| 草稿为恢复前来源 | 草稿恢复窗口标签“恢复前”；不自动提交 | 查看/另存/丢弃 | `DraftRecoveryItem.IsPreRestore` |
| 备份/恢复状态文件损坏 | 显示未知状态，不阻止启动 | 重新检查/配置 | 状态存储读取 null |
| 备份期间关闭窗口 | 禁止关闭或提示操作进行中 | 等待/取消（恢复切换前） | `BackupWindow.OnClosing` |

## 未覆盖

- Windows 系统通知与托盘视觉（C5/C7）。
- 真实卷满、ACL、强杀/断电（C6）。
- 多屏/DPI 恢复窗口位置（C5/C6）。
