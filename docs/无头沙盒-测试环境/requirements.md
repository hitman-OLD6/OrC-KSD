# Requirements — 无头沙盒测试环境（AI vs MCP Agent）

> 状态：**需求已定稿**（2026-10-21，R1–R7 全部 Confirmed）。
> 上游：`docs/多人支持-服务端客户端分离/requirements.md`（R1–R15 已验收）。
> 本文件只写**外部可观察行为**；内部结构留待实现 grill 的 `implementation.md`。

一句话：把已有的多人服务端做成**可从脚本一键拉起、无需 UI/真人**的无头对局实例——一个座位由服务端内置简单 AI 填充，另一个座位由外部 **MCP agent** 填充，引擎事件流与会话流水落盘，使引擎回归与新卡牌开发都在这种无头对局里完成。

## Scope

- Includes：
  - 无头实例的启动入口（bash / 脚本 / API 共用同一套进程内编排）。
  - 座位编排：预置 AI 与 agent 座位、令牌下发、agent 入座、实例侧自动开局。
  - 外部 MCP agent 的接入面（stdio MCP 桥），含"推送转可拉取状态"。
  - 服务端双运行模式 `play` / `sandbox`，以及调试能力的读/写拆分。
  - 引擎事件流日志与会话流水日志（落盘、可离线分析）。
  - 座位角色参数化与批量跑局（AI+AI 回归）。
  - 进程生命周期：终局自动退出、退出码语义、结果摘要。
  - 一键脚本、固定随机种子与可复现运行目录。
  - 回归验证：既有 2263 项测试不回归，新增无头链路端到端测试。
- Excludes：
  - 观战 / 回放 / 账号 / 匹配 / 排行榜（沿用多人支持 R10）。
  - 多桌并发（一实例仍只服务一场对局）。
  - WebSocket 承载（沿用 NDJSON/TCP）。
  - AI 策略重写或难度分级（"简单 AI"＝既有 `AiPilot` 启发式）。
  - 引擎规则、触发器、判定器、效果语义变更。
  - Godot/UI 客户端功能开发。

## Constraints

- **单写者串行**：一切进入引擎的调用仍经 `SerialWorkQueue` 单消费者；无头编排不得开出第二条写路径。
- **生产默认零影响**：无头能力全部由启动选项开启，默认关闭；既有"房主开房"链路行为与性能不变。
- **隐藏信息**：`play` 模式必须保持既有脱敏语义（agent 不见对手手牌/卡组顺序）；`sandbox` 模式可全知。
- **不承诺防作弊**：全知日志与 sandbox 全读对同机 agent 可见（与多人支持 R12 一致）。
- **平台**：Windows 优先（Git Bash 可执行），脚本不得依赖仅 Linux 可用的工具；Ctrl-C/异常后无残留进程与端口占用。
- **可复现**：同一卡包 + 同一种子 + 同一参数 → 可比对的事件流与终局结果。
- **不复用 UI 会话**：不依赖 `Orc.Mcp.HostSample` 或 Godot 侧任何组件。
- **既有契约**：服务端协议面（工具清单、推送语义、授权门禁）在 `play` 模式下零变更。

## Requirement Items

### R1: 无头实例的启动入口
- Status: Confirmed
- **Decision**：无头编排与日志落在现有 `Orc.Server` 内，以启动选项开启、默认关闭；不新建并行宿主工程。
- Scenario/Trigger: 开发者从 shell（或 CI/脚本）发起一次无头对局。
- Behavior: 一条命令完成"装载卡包 → 建房间 → 填座位 → 开局 → 运行至终局或外部停止"，无需 UI 与人工操作；就绪信息（端口、运行目录、令牌位置）可从进程输出稳定获取。
- Acceptance:
  - [ ] 单条命令可完成启动到就绪；退出后无残留进程。
  - [ ] 就绪输出可被脚本解析（stdout 单行 JSON，其余走 stderr）。
  - [ ] 支持指定：卡包目录、运行目录、随机种子、端口、运行模式。
  - [ ] 未开启无头选项时，服务端行为与现状完全一致（既有测试全绿）。
- Terminology: [服务端](../CONTEXT.md)、[房间](../CONTEXT.md)、[房间态](../CONTEXT.md)

### R2: 座位编排（预置 + 令牌 + 自动开局）
- Status: Confirmed
- **Decision**：服务端**预置两座位**（seat0 / seat1 角色由参数决定，默认 seat0=AI、seat1=agent）；两座位卡组均由 `AiDeckGenerator` 生成；agent 座位的令牌写入运行目录 `agent.token`（文件名与座位号解耦）并随就绪信息给出；agent 桥读令牌后 `room.resume` 绑定；实例在"两座位就位且 **agent 座位有绑定会话**"时**由实例侧自动开局**（系统房主身份，不改协议面的房主约束）。
- **Decision（连接语义与超时）**：预置的 agent 座位 `connected=false`，`connected` **仅作展示**、不参与开局判定；无头模式下房间态断线**不释放座位**（座位生命周期＝进程生命周期，令牌始终有效）；`--wait-agent <秒>`（默认 300）超时退出码 `3`。
- **Decision**：无头模式默认 `--deck-rules lenient`（放宽张数/同名上限，保证小卡包可开局）；`strict` 恢复生产校验。
- Scenario/Trigger: 无头实例启动后进入房间态。
- Behavior: 座位角色可参数化（`ai` / `agent`）；agent 座位等待外部连接后开局；未连接时保持房间态不提前开局。
- Acceptance:
  - [ ] 默认（ai+agent）下，无外部连接时房间态可查询、AI 座位已就位、**不提前开局**。
  - [ ] agent 连入并 `room.resume` 后自动开局，agent 无需开局权限。
  - [ ] 一座位一控制器不被破坏；agent 断线后凭令牌重连，不重开对局；**无头模式下房间态断线不释放座位**（令牌仍有效）。
  - [ ] 座位角色可配：`ai+agent`（默认）与 `ai+ai` 均可开局。
  - [ ] `--games N` 批量：每局独立运行目录，全局 `summary.json`；批量仅允许 `ai+ai`。
  - [ ] 等待 agent 连接有超时（默认值可配），超时以非零退出码结束并给出原因。
- Terminology: [座位](../CONTEXT.md)、[控制器](../CONTEXT.md)、[座位令牌](../CONTEXT.md)、[托管客户端](../CONTEXT.md)

### R3: 外部 MCP agent 接入（stdio 桥）
- Status: Confirmed
- **Decision**：新增 stdio MCP 桥进程：对外暴露 MCP（`tools/list`、`tools/call`），对内用 `Orc.Client` 连接服务端 TCP。桥**允许**新增桥专属工具（如 `seat.state`），只存在于桥的 `tools/list`，不进服务端契约。
- **关键约束**：服务端推送不会唤醒 stdio MCP 客户端，桥必须把 `notify/view`、`notify/segment`、`notify/target.request` 转成**可拉取状态**，并在状态变化时随工具结果附带。
- Scenario/Trigger: 外部 agent 作为 agent 座位行动。
- Behavior: agent 能列工具、读自己视角视图、出牌/指挥/换牌/结束回合、应答选靶；工具语义与真人客户端一致。
- Acceptance:
  - [ ] 服务端工具清单在 `play` 模式下与 player 模式一致；桥专属状态工具单独可辨。
  - [ ] agent 可完成一整局；选靶可应答；无推送通道也不挂死。
  - [ ] 断线后凭令牌恢复，不重开对局。
  - [ ] 桥不向服务端引入任何新工具、不改服务端授权语义。
  - [ ] 桥的接入配置（command/args）可由脚本直接打印，供 MCP 客户端粘贴。
- Terminology: [客户端](../CONTEXT.md)、[会话](../CONTEXT.md)、[选靶](../CONTEXT.md)

### R4: 事件流日志与会话流水
- Status: Confirmed
- **Decision**：默认**全知单流** `events.jsonl`（每行一段，含双方手牌与完整因果子树，不做 visibility 过滤）；`--log-seat-views` 额外产出 `seat0.jsonl`/`seat1.jsonl` 脱敏流。工具调用流水单独落 `calls.jsonl`（谁、调了什么、结果码、耗时）。
- **已接受代价**：全知日志对同机 agent 可读；无头环境不承诺防作弊。
- Acceptance:
  - [ ] `events.jsonl` 按 `sequence` 单调、不丢段、可逐行解析。
  - [ ] `--log-seat-views` 产出的脱敏流中对手手牌写 `$hidden`。
  - [ ] 进程异常退出时已产生的段仍可读（逐段 flush）。
  - [ ] 日志默认落在运行目录，路径可由参数覆盖。
  - [ ] `calls.jsonl` 可还原"谁在何时调了什么、结果如何"。
- Terminology: [事件流段](../CONTEXT.md)、[动作作用域](../CONTEXT.md)、[观察面](../CONTEXT.md)

### R5: 双运行模式与调试能力边界
- Status: Confirmed
- **Decision**：`--mode play`＝服务层 Player（真实规则、隐藏信息，**工具清单中不存在**调试类工具）；`--mode sandbox`＝服务层 Authoring（额外暴露调试类工具）。
- **Decision（读写拆分）**：sandbox 下
  - 读类（`observe.snapshot`、`observe.segments`、全知 `observe.view`、`sandbox.state`、`diag.*`）→ **默认注册、所有连接可见**（含 agent）；
  - 写类（`shape.*`、`card.*`、`asset.*`、`editor.*`、`sandbox.create/dispose`）→ **默认不注册**；需显式 `--allow-debug-writes` 才注册（该参数仅在 sandbox 下有效）。开启后**仅编排连接**（回环 + 未绑定座位）可见可用，已绑定座位的 agent 会话不可见且调用被拒。
- **Decision（observe.view 分流）**：`play` 下未绑定座位的会话调用 `observe.view` → **拒绝**；`sandbox` 下未绑定会话 → 放行且全知。已绑定会话两模式都只能读自己的座位。
- **Decision（不设防边界）**：写类的"回环 + 未绑定"判据只防误触，不阻止同机进程另开连接绕过；真正隔离靠 OS 用户/容器，不在本需求范围（与 R4 同级）。
- **Decision（建局入口）**：sandbox 模式下仍走 1v1 房间局（`room.start`）；`sandbox.create` 在该模式的服务端入口禁用（单执行器只允许一个活跃对局）。
- Acceptance:
  - [ ] `play` 下调试类工具在 `tools/list` 中不存在，且调用被拒。
  - [ ] `sandbox` 下读类工具对 agent 可见；写类默认不注册，开启 `--allow-debug-writes` 后对 agent 不可见且调用被拒。
  - [ ] `sandbox` 下可在对局进行中注册新卡并观察到其效果（需开启 `--allow-debug-writes`）。
  - [ ] `play` 下未绑定会话调 `observe.view` 被拒；`sandbox` 下同一调用放行且可读任意座位。
  - [ ] 两种模式共用同一套房间/座位/日志机制。
- Terminology: [沙盒模式](../CONTEXT.md)、[调试 API](../CONTEXT.md)、[卡包](../CONTEXT.md)

### R6: 一键脚本与运行目录
- Status: Confirmed
- Scenario/Trigger: 开发者需要快速开始一次无头对局。
- Behavior: 单一脚本入口封装"可选构建 → 拉起实例 → 解析就绪 → 打印接入信息 → 等待终局/中断 → 清理"。
- Acceptance:
  - [ ] 脚本参数覆盖：`--mode`、`--packs`、`--seat0/--seat1`、`--games`、`--seed`、`--port`、`--run-dir`、`--deck-rules`、`--wait-agent`、`--log-seat-views`、`--keep-alive`、`--allow-debug-writes`、`--no-build`。
  - [ ] 启动成功后打印：端口、运行目录、令牌文件路径、**可直接粘贴的 MCP 桥配置片段**。
  - [ ] Ctrl-C / 异常后无残留进程与端口占用（trap 清理）。
  - [ ] 干净目录可重复执行，不依赖上次残留。
  - [ ] 运行目录结构稳定：`agent.token`、`events.jsonl`、`calls.jsonl`、`summary.json`、`server.stderr.log`、（可选）`seat0.jsonl`/`seat1.jsonl`、`runs/run-XXX/`（批量）。
- Terminology: —

### R7: 生命周期、退出码与回归
- Status: Confirmed
- **Decision**：默认 `--exit-on-end`：终局 → flush → 写 `summary.json` → 退出 `0`；启动失败 `1`；运行期异常 `2`；等待 agent 超时 `3`。`--keep-alive` 关闭自动退出（进程存活、可查询/导出，但**不支持再来一局**）。
- Behavior: 多局＝**每局一个进程，由脚本循环拉起**（服务端不实现进程内多局）；每局独立目录，全部结束由脚本写总 `summary.json` 后退出 `0`。
- Acceptance:
  - [ ] 单局终局后进程自行退出，退出码符合上述约定。
  - [ ] `summary.json` 含：胜负、回合数、终局原因、种子、卡包指纹、日志路径、错误计数。
  - [ ] `--keep-alive` 下终局后进程存活；`room.start` 被明确拒绝并说明原因（不支持再来一局）。
  - [ ] `dotnet test OrcEngine.slnx` 全绿，无既有用例回归。
  - [ ] 新增端到端测试：拉起无头实例 → 假 agent 占座 → 开局 → 推进若干回合 → 断言日志与摘要。
- Terminology: —

## Decision Ledger

| # | 决策点 | 结论 | 来源 |
|---|---|---|---|
| 1 | 工程落点 | 扩展 `Orc.Server`，无头默认关闭 | 用户选 A |
| 2 | agent 接入 | 新增 stdio MCP 桥，推送转可拉取状态，允许桥专属工具 | 用户选 A |
| 3 | 日志口径 | 全知 `events.jsonl` ＋ 可选脱敏双流；接受同机可见 | 用户选 A |
| 4 | 运行模式 | 双模式 `play` / `sandbox` | 用户指定 |
| 5 | sandbox 权限 | 读类全开、写类仅编排连接；sandbox 仍走 1v1 房间局 | 用户选 A ＋"所有信息可读" |
| 6 | 座位与开局 | 预置座位＋令牌落盘＋`room.resume`＋实例侧自动开局；默认 lenient 卡组 | 用户选 A |
| 7 | 座位组合 | 角色参数化，`--games N` 批量；AI+AI 用于回归 | 用户选 A |
| 8 | 生命周期 | 默认 `--exit-on-end`，退出码 0/1/2/3 | 用户选 A |
