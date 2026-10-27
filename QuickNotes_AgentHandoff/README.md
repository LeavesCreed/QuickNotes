# Windows 桌面便签软件 --- Agent 交接包

## 文件

-   `AGENTS.md`：仓库级编码约束。
-   `agent.md`：项目背景、当前状态、第一条任务。
-   `docs/agent/Agent对接文档_B阶段.md`：B
    阶段任务顺序、验收标准和可复制提示词。
-   `docs/product/PRD_v1.0.md`：产品需求基线。
-   `docs/decisions/`：技术决策记录目录。

## 使用方式

1.  将文件放入项目仓库根目录；如有同名文件，先合并审阅，不要直接覆盖。
2.  让编码 Agent 先阅读 `AGENTS.md`、`agent.md` 和 B 阶段交接文档。
3.  第一轮只执行 B0、B1。
4.  每一步运行测试并记录真实结果。

本包只是 Agent 工作契约，不代表 B 阶段代码已经实现或测试通过。当前不引入
Rust FFI。
