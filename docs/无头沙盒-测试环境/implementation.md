# Implementation — 无头沙盒测试环境（AI vs MCP Agent）

> 状态：**已执行完成**（11 个决策点全部裁决；S1–S8 全部落地，验收映射见文末；执行记录见 [`PROGRESS.md`](./PROGRESS.md)）。
> 上游需求：[`requirements.md`](./requirements.md)（R1–R7 全部 Confirmed）。
> 上游实现基线：[`../多人支持-服务端客户端分离/implementation.md`](../多人支持-服务端客户端分离/implementation.md)（S1–S10 Done）、[`../多人支持-服务端客户端分离/修复-2026-10-21/requirements.md`](../多人支持-服务端客户端分离/修复-2026-10-21/requirements.md)。
>
> 本文只写**内部结构/设计**（落点、拆分、顺序、取舍）；对外可观察行为以 `requirements.md` 为准。

## Scope

- Target：在既有 `Orc.Server` 内补出「无头编排 + 运行目录 + 双模式 + 事件流/流水日志 + 生命周期」，新增 stdio MCP 桥进程（`src/Orc.Mcp.Bridge`，对外 MCP、对内 `Orc.Client`），并提供一键脚本与无头端到端测试。
- Excludes：
  - 观战/回放/账号/匹配/排行榜；多桌并发；WebSocket 承载；AI 策略与难度；引擎规则语义变更；Godot/UI 功能开发（沿用上游 `requirements.md` 的 Excludes）。
  - 复用 `Orc.Mcp.HostSample` 或 Godot 侧任何组件（R Constraints「不复用 UI 会话」）。
  - 进程内多局（多局＝脚本拉起多个进程，见 S6）。
  - 桥的"打完整局"覆盖（由直连假 agent 用例间接保证，见 S8）。
- 已确认的边界基线（引上游实现，不重新决策）：
  - 单写者：进入引擎的一切调用经 `SerialWorkQueue`（`src/Orc.Server/Execution/SerialWorkQueue.cs`）单消费者；无头编排不得新开第二条写路径。
  - 生产默认零影响：无头能力由启动选项开启、默认关闭（R1 Decision）。
  - `play` 模式协议面零变更：工具清单、推送语义、授权门禁（R Constraints「既有契约」）。
  - 隐藏信息判定只有一份实现：`Orc.Game.Ui.MatchEventVisibility`（上游 S5）。

## 步骤顺序与依赖

```
S1 启动入口与选项 ──→ S2 座位编排与自动开局 ──→ S2b 卡组规则三档
        │                     │
        │                     └──→ S3 双模式与读写边界 ──→ S4 日志
        │                                   │                │
        └───────────────────────────────────┴──→ S5 stdio 桥
                                                             │
S6 生命周期/退出码/摘要 ←── S4 ──→ S7 一键脚本 ←── S5 ───────┘
                                     │
                                     └──→ S8 无头 e2e 与回归
```

执行顺序：**S1 → S2 → S2b → S3 → S4 → S6 → S5 → S7 → S8**。
（S2b 紧跟 S2：AI 座位卡组生成与房间态校验同源；S6 在 S4 之后：退出码依赖 drain 与摘要；S7 在 S5 之后：要打印桥片段。）

里程碑：

| 里程碑 | 包含步骤 | 可验证的结果 |
|---|---|---|
| M1 无头可开局 | S1+S2+S2b | 一条命令拉起 → 预置两座位 → 桥/假 agent `resume` → 自动开局 |
| M2 可观测 | +S3+S4 | `events.jsonl`/`calls.jsonl` 落盘、双模式读写边界生效 |
| M3 可自动化 | +S6+S7 | 终局自退、退出码语义、`summary.json`、脚本一键跑批 |
| M4 agent 可用 | +S5 | 外部 MCP agent 经 stdio 桥打完整局 |
| M5 可回归 | +S8 | 新增用例全绿、2263 基线无回归、验收映射表逐条可指 |

## Implementation Steps

### S1: 无头启动入口与启动选项
- Status: Done（已落地；执行记录见 [`PROGRESS.md`](./PROGRESS.md)）
- Target: `src/Orc.Server/Program.cs`、`src/Orc.Server/ServerOptions.cs`、`src/Orc.Server/ServerBootstrap.cs`
- Approach:
  - **两个正交维度**：`--headless`（缺省 `false`）是**唯一**开关，开启无头编排（预置座位、自动开局、日志、令牌文件、`--exit-on-end`）；`--mode play|sandbox`（缺省 `play`）只选服务层权限面，**不隐含** `--headless`，也不因它开启任何编排。
    - 推论：Godot 链路（`Orc.Client/ServerProcess` 拼的参数不含新参数）绝对落回现状；`--mode sandbox` 但未开 `--headless` 时只是"服务层 Authoring 模式"，无预置座位/无自动开局/无日志（不在本次验收范围）。
  - **无头专属参数**（`--run-dir`、`--seat0/--seat1`、`--seed`、`--deck-rules`、`--exit-on-end`、`--log-seat-views`、`--wait-agent`）在未开 `--headless` 时**报参数错误并退出 `1`**；校验必须落在**参数解析阶段、早于任何目录创建**，不得静默忽略。
  - **就绪行＝增量契约（显式不变量）**：无头模式下 stdout 单行仍以 `{"ready":true,...}` 开头并**追加**字段；非无头模式保持现状只输出 `{"ready":true,"port":N}`，不加任何字段。**以后再加字段不得改动既有字段语义**；消费者（`ServerProcess.WaitForReadyAsync`）只认 `ready`/`port`，其余字段被忽略。
    ```json
    {"ready":true,"port":51234,"headless":true,"mode":"play","runDir":"D:/tmp/r1",
     "agentSeat":1,"agentTokenPath":"D:/tmp/r1/agent.token","eventsPath":"D:/tmp/r1/events.jsonl"}
    ```
  - **`ai+ai` 时就绪行省略 `agentSeat` 与 `agentTokenPath`**（整个字段省略，**不写 `null`**）；脚本按"字段是否存在"判断是否打印 MCP 桥配置。
  - **`--run-dir` 与 `--sandbox` 并存、语义不混**：
    - `--run-dir`＝运行目录（日志、令牌、摘要），缺省 `<cwd>/.orc-run/<UTC yyyyMMdd-HHmmss>`。
    - `--sandbox` 保留原语义＝沙盒写根（`McpServiceOptions.SandboxRoot`）；无头模式下未显式给出则派生 `<run-dir>/sandbox`，显式给出以显式值为准；非无头模式保持现状（临时目录）。
    - 日志一律落 `--run-dir` 根（批量落 `runs/run-XXX/`），与沙盒写根**无关**；`--log-seat-views` 产出一并在同一目录。
  - **`--seed` 只有一份真值**：直接灌 `McpServiceOptions.Seed`，经既有链路（`McpService.MatchLaunch.cs:128`）进 `Match(seed:)`；不引入"运行级种子"。`summary.json` 写**引擎实际使用的种子**（`match.Seed`）而非参数原值（不传时引擎自生成，摘要必须据此可复现）。批量：指定时 `run-i` 的种子＝`--seed + (i-1)`；未指定则各局自生成、摘要逐局记录实际值。
  - **座位角色任意排列**（含 `seat0=agent`）：令牌文件名与座位号**解耦**，统一 `agent.token`（1v1 只有一个 agent 座位），就绪行用 `agentSeat` 指明是哪个座位。R6 原文的 `seat1.token` 以本条为准（父 agent 同步修需求）。
- Acceptance:
  - [ ] 单条命令完成启动到就绪；未开 `--headless` 时行为与现状一致（既有测试全绿）。
  - [ ] 就绪信息（端口、运行目录、令牌路径）可从 stdout 单行 JSON 稳定解析，其余走 stderr。
  - [ ] 可指定：卡包目录、运行目录、随机种子、端口、运行模式。
  - [ ] 非无头模式下就绪行仍是 `{"ready":true,"port":N}`（无追加字段）。
  - [ ] 无头专属参数在非无头下报参数错误退出 `1`，且不创建任何目录。
- Rationale: 把"是否编排"与"权限面"拆成两个正交开关，是同时满足 R1「未开启无头选项时行为与现状完全一致」与 R5「双模式」的唯一不冲突形状；就绪行选增量扩展而非新协议，是因为既有消费者 `ServerProcess` 只读两个字段，追加字段零成本、且不需要改客户端。
- Terminology: [服务端](../../CONTEXT.md)、[房间](../../CONTEXT.md)、[房间态](../../CONTEXT.md)、[沙盒模式](../../CONTEXT.md)

### S2: 座位编排与自动开局
- Status: Done（已落地；执行记录见 [`PROGRESS.md`](./PROGRESS.md)）
- Target: `src/Orc.Server/Room/Room.cs`、`src/Orc.Server/Room/RoomControl.cs`、`src/Orc.Server/ServerBootstrap.cs`、`src/Orc.Server/Transport/ServerHost.cs`、**新增 `src/Orc.Server/Headless/`**（预置座位、自动开局判定）
- Approach:
  - **系统房主＝`Room` 内部旁路入口**：新增仅供进程内调用的 `StartBySystemAsync(ct)`，语义＝"以系统身份开局"。它**复用**现有 `Lobby` 状态检查与"座位已满"检查，**跳过** `HostSlot()` 与 `seatToken` 两道校验（`Room.cs` 的 `StartAsync` 保持原样）。系统房主**不进入房间状态**——不是座席、不是身份对象，只存在于调用栈上。
  - **可见性零变更**：`RoomSnapshot` 的座位数组恒为 `SeatCount`（=2），`room.info` 不新增字段、不暴露"系统"概念；`BootstrapTests` 的两座位/`lobby` 断言与 `RoomTests` 的房主用例不受影响。
  - **不变量（无头模式）**：座位由服务端预置，外部无处置权——`room.release` / `room.kick` / `room.add_ai` 对任何外部连接仍是 `unauthorized`；要换卡组＝重启实例。**不得**把系统身份塞进 `HostSlot()`（其返回值保持"仅 `Kind==Local`"原语义）。
  - **协议面零变更**：不注册任何新工具；外部 `room.start` 仍走 `StartAsync(seatToken)`，因无 `local` 座位而必然 `unauthorized`——这正好同时满足"agent 无需开局权限"与"agent 不可开局"。**不得**为这次拒绝新增特判或新错误码。
  - **预置座位**走 `Room` 的新增内部方法 `PresetSeat(...)`（**不经过 `Claim`**，返回强类型 token）：只做"校验空座 + 房间态"检查 → 建 `SeatSlot{ Kind, DisplayName, Deck, DeckConfig, SeatToken = 新 token, Connected = false }`。`Claim`/`AddAi`/`Resume` 的既有行为**一律不动**（非无头路径零变更）。token 生成与 `Claim` 同源（`Guid.NewGuid().ToString("N")`），保持"令牌只在进程内存在、随进程退出失效"的既有语义。
    - AI 座位用 `AddAi`（`Connected = true`——它由服务端驱动、确实可用）；agent 座位 `Connected = false`。
    - 启动链路取出 agent 座位的 token 写 `agent.token`。
  - **`room.info` 的显示**：预置但未连接 → `controllerKind:"remote"`、`displayName:"agent"`、`connected:false`；AI 座位 → `connected:true`。`RoomSnapshot` 已有 `connected` 字段，**不加字段**，只要求如实反映。
  - **自动开局的判据只认"会话确实绑定到了 agent 座位"**，不认房间态的 `Connected`：
    - 触发点：`ServerHost.BindSeat(session, seat)` 成功后，若 `seat == agentSeat` 且 `room.State == Lobby` 且两座位均已填充 → 入队一次 `StartBySystemAsync`（经 `SerialWorkQueue`，`WorkPriority.Command`）。
    - **不变量**：`Connected` **只用于展示**，不参与任何开局判定——两个真值来源必然漂移，只留一个。`Room.SeatSlot.Connected` 的三处写入点（`Claim`/`AddAi`/`Resume`）**不属于开局判据**。
    - 入队的 work item 执行时**再确认一次**"该 agent 座位当前仍有绑定会话"；不满足则不开局、复位 `_autoStartIssued`，等下一次绑定（桥"绑定后立刻断开重试"不会把对局开成无人在场的局）。
    - **不变量（无头模式的座位生命周期）**：座位生命周期＝进程生命周期；座位只能由进程退出而消失。
  - **无头模式必须关闭"房间态断线即释放座位"**：`ServerHost.MarkSeatDisconnected` 在 `Lobby` 时目前会调 `Room.DropSeat(seat)`，它会丢掉预置座位 → `room.resume` 找不到令牌 → 落盘 `agent.token` 永久失效。无头模式：断线只 `MarkDisconnected`（`Connected=false`），**不 DropSeat**、不销毁 token，桥可反复重试 `room.resume`；非无头模式保持现状（既有房主开房语义）。
  - **token 失效语义**：只在"实例已重启"时发生。桥拿到 `unauthorized` → stderr 说明"实例可能已重启，请重新读取 `agent.token` 并重启桥" → 退出码 `2`。**不做**自动重连或自动重新领取座位（那会让桥猜错实例）。
  - **等待 agent 超时**：`--wait-agent <秒>`（默认 `300`），计时起点＝**实例进入房间态且预置完成**（不是进程启动）；房间态计时，超时退出码 `3`，写 `summary.json{aborted:true, reason:"wait-agent-timeout"}`。判定与自动开局检查同处。
  - **自动开局必须经 `SerialWorkQueue` 入队执行**：触发点在 `ServerHost.ApplySeatEffects → BindSeat` 之后（会话线程、**不在**消费者上下文）；直接调 `StartBySystemAsync` 会绕过单写者语义。做法＝入队一个 `WorkPriority.Command` 工作项，在消费者上下文里调开局入口。
  - **触发条件与去重**：`房间态 && 两座位均已填充 && agent 座位有绑定会话` → 入队一次（判据见上，**不含 `Connected`**）；用一次性标志（如 `_autoStartIssued`）保证同一局只发一次；**开局失败或执行期二次确认不通过要复位标志并记录原因**（否则房间永久卡在 `lobby`）。等待 agent 连接的超时（R2 验收）由同一处判定。
- Acceptance:
  - [ ] 默认 `ai+agent`：无外部连接时房间态可查询、AI 座位已就位、不提前开局（`room.info`：seat0 `ai/connected:true`，seat1 `remote/connected:false`）。
  - [ ] agent `room.resume` 后自动开局，agent 自身无需开局权限（且 agent 调 `room.start` 仍被拒）。
  - [ ] 等待连接超时（`--wait-agent`，默认 300s）以退出码 `3` 结束并写 `summary.json{aborted:true, reason:"wait-agent-timeout"}`。
  - [ ] 一座位一控制器不被破坏；agent 断线凭令牌重连不重开对局；无头模式下房间态断线**不释放座位**（token 仍有效）。
  - [ ] `ai+ai` 可开局。
  - [ ] 外部连接的 `room.release`/`room.kick`/`room.add_ai` 仍返回 `unauthorized`（不变量有守卫用例）。
  - [ ] 桥"绑定后立刻断开重试"时不把对局开成无人在场的局（执行期二次确认有守卫用例）。
- Rationale: 用调用栈上的旁路入口而非"隐式房主席位"，是因为房主席位一旦进入房间状态就会污染 `RoomSnapshot` 与既有的房主用例、并让外部获得处置权；旁路入口把"开局权"限定在进程内，协议面因此零变更（R Constraints「既有契约」）。
- Terminology: [座位](../../CONTEXT.md)、[控制器](../../CONTEXT.md)、[座位令牌](../../CONTEXT.md)、[房主](../../CONTEXT.md)、[托管客户端](../../CONTEXT.md)

### S2b: 卡组规则三档与 AI 卡组尽力生成
- Status: Done（已落地；执行记录见 [`PROGRESS.md`](./PROGRESS.md)）
- Target: `src/Orc.Game/Decks/DeckRules.cs`、`src/Orc.Server/Room/DeckRulesValidator.cs`、`src/Orc.Server/Room/AiDeckGenerator.cs`、`src/Orc.Server/ServerOptions.cs`、`src/Orc.Server/ServerBootstrap.cs`、新增摘要组件（卡组指纹）
- Approach:
  - **三档语义（分项放宽，绝不"整体关闭"）**：

    | 校验项 | `strict` | `lenient`（无头默认） | `none`（仅既有测试夹具） |
    |---|---|---|---|
    | 张数（`requiredSize`） | 校验（39） | **放宽**（`RequiredDeckSize = 0`） | 跳过 |
    | `blank-id`（空白卡 id） | 校验 | **保留校验** | 跳过 |
    | `unknown-card`（卡池存在性） | 校验 | **保留校验**（上游 R12 硬边界，不得破） | 跳过 |
    | 同名上限（稀有度 4/3/2/1） | 校验 | **放宽** | 跳过 |
    | 主国/盟国配对（`ValidateFaction`） | 校验 | **放宽** | 跳过 |

    - `strict` ＝ 与生产校验**逐项等同**（不是"更严的新口径"）；`lenient` 只能放宽、绝不禁用卡池校验。
    - 既有 `ServerOptions.EnforceDeckRules` **保留不动**（`false` 仍映射到 `none`）→ 既有测试零改动；无头链路改用新的三档枚举，两者同时给出即参数错误。无头 `--deck-rules` **不接受** `none`。
  - **落点：新增三档枚举并同时驱动 `RequiredDeckSize` 与各校验项**，不把一个 bool 掰成两半用。
    - `DeckRules` 增加**可选规则档位参数，默认＝`strict`**，保证既有 `DeckRulesTests` 行为不变。
    - `room.claim` 的房间态校验与 `room.start` 的建局期校验**必须共用同一个规则对象实例**（同一个实例传入两处），禁止各算一套——否则会出现"声明通过、开局失败"。
    - `DeckRulesValidator` 现有 `_enforce == false` 时仍拒绝空卡组（`deck.Count == 0` → `deck-invalid`），该行为保留。
  - **`AiDeckGenerator` 在 `lenient` 下改为"尽力而为"**：返回能凑到的**最大**卡组，**绝不返回空**。
    - 下限＝1 张（满足 `Match`"卡组名单非空"契约）；**不用占位卡凑数**（占位卡会进牌库污染对局语义）。
    - 两副卡组张数允许不同；可复现的锚点是"同卡包 ＋ 同种子 ＋ 同生成规则"，不是"两副等长"。
    - 生成结果必须**确定**（同卡池同规则每次相同）；若引入随机，必须由 `--seed` 驱动并在摘要中记录。
  - **`PlayerDeckConfiguration` 可为 `null`**：`DeckRules.ValidateFaction(null)` 通过（`DeckRules.cs:86` "未提供配置＝通过"），`MatchSeatConfig.DeckConfiguration` 是可空 `PlayerDeckConfiguration?`（`McpMatchLauncher.ToSeat` 现有写法即传 `null`）。因此 lenient 放宽配对后**不需要伪造默认 config**；但若生成器给出了 config，该 config 必须与卡池自洽（不允许"校验放过一个矛盾配置"）。
  - **摘要补记可复现锚点**：`summary.json` 记录 `packFingerprint` ＋ **实际 `seed`** ＋ **每副卡组的张数与内容指纹**（排序后卡 id 列表的 hash）。三者齐备即同参数可比对，张数不同不构成不可比。
- Acceptance:
  - [ ] 三档语义与上表逐项一致；`strict` 与既有生产校验无行为差异（`DeckRulesTests` 零改动通过）。
  - [ ] `lenient` 下未知卡仍被拒（`unknown-card` 用例），张数/同名上限/配对不再拒绝。
  - [ ] `AiDeckGenerator` 在小卡包下返回非空且确定；同一 `--seed` 重跑生成同一卡组。
  - [ ] `room.claim` 与 `room.start` 对同一卡组给出相同判定（共用同一规则实例，有守卫用例）。
  - [ ] `summary.json` 含 `packFingerprint`、`seed`、每副卡组张数与内容指纹。
- Rationale: 用"分项放宽"而不是沿用单一 bool，是因为 `EnforceDeckRules=false` 会连带放过 `unknown-card`，破掉上游 R12 的卡池权威边界；把三档写死成一张表，是为了后续新增校验项时有唯一参照（新增项必须显式声明在三档下的行为）。
- Terminology: [卡包](../../CONTEXT.md)、[卡组资产](../../CONTEXT.md)、[控制器](../../CONTEXT.md)

### S3: 双运行模式与调试能力边界
- Status: Done（已落地；执行记录见 [`PROGRESS.md`](./PROGRESS.md)）
- Target: `src/Orc.Server/ServerBootstrap.cs`、`src/Orc.Server/Transport/ServerHost.cs`（`HandleToolsList`/`Gate`）、`src/Orc.Server/Transport/SeatScopeRegistry.cs`、`src/Orc.Mcp/McpService.cs`（`AddTool` 维度）、`src/Orc.Mcp/Tools/*.cs`、`tests/Orc.Server.Tests/SecurityTests.cs`
- Approach:
  - **不变量（明示不设防）**：写类调试工具的暴露面用于**防止 agent 在自己的座位上顺手改写对局状态**，它**不**阻止同机进程有意绕过（另开一条不 `room.resume` 的回环连接即可满足"回环+未绑定"）；真正的隔离只能靠 OS 用户/容器，不在本需求范围。与 R4「不承诺防作弊」同级。
  - **默认暴露面压到最小——写类默认不注册**：
    - `--mode sandbox` 默认只提供**读类**调试工具：`observe.snapshot`、`observe.segments`、全知 `observe.view`、`sandbox.state`、`diag.*`。
    - 写类（`shape.*`、`card.*`、`asset.*`、`editor.*`、`sandbox.create/dispose`）需显式 `--allow-debug-writes` 才注册；该参数**只在 `--mode sandbox` 下有效**，在 `play` 下给出即参数错误退出 `1`（与 S1 的参数校验阶段一致）。
    - 推论：默认 sandbox 局没有任何写面；"边跑边改卡"必须显式开启。`--allow-debug-writes` 需进脚本参数表与 `--help`。
  - **`tools/list` 改为按会话过滤**（`HandleToolsList` 需注入 session）：
    - 未绑定座位的会话 → 返回当前已注册全集（读类 ＋ 已开启的写类）。
    - 已绑定座位的会话（含 agent 桥）→ **不列出写类**，即使 `--allow-debug-writes` 已开启。
    - 调用侧保留**第二道拒绝**（不可见 ≠ 不可调）；可见性与门禁**共用同一判定函数**，避免漂移。
    - 这样 R5「写类对 agent 不可见」字面成立，无需降级为"可见但拒绝"。
  - **`SeatScopeRegistry` 新增第五类 `OrchestratorOnly`**（不塞进既有四类）：判据＝回环来源（`SeatScopePolicy.IsTrustedLocal`）＋ 未绑定座位；两者缺一即 `unauthorized`。
  - **清单单一来源**：把调试类工具（读类/写类、普通/`OrchestratorOnly`）的登记集中在**同一处名单**，供"注册"、"门禁"、"守卫测试"三处共用，避免三份清单互相漂移。
  - **守卫测试按模式参数化**：`SecurityTests.cs:24`（宣告即须登记）与 `:40`（表内无死条目）以"**当前进程模式下已注册的工具全集**"为输入集，而非固定全集（否则 `play` 模式必然报"表内死条目"）。
  - **`observe.view` 未绑定放行收敛**：
    - `play`：未绑定会话调用 → **拒绝**（`unauthorized`）。依据是上游 R10「无第三方只读连接」与 R3/R5 隐藏信息语义；现有 `GateSpecial` 的 `return null` 是单机诊断沙盒的遗留，不得外溢到 play。
    - `sandbox`：未绑定会话 → 放行且**全知**（不强制 `seat`，可读任意座位摘要）。
    - 已绑定会话：两模式都只能读自己的座位（保留现有改写逻辑）。
    - **分流依据是 `Options.Mode`，不是"有没有绑定座位"**——不得用绑定状态反推模式。
- Acceptance:
  - [ ] `play` 下调试类工具不在 `tools/list` 且调用被拒。
  - [ ] `sandbox` 默认：读类对 agent 可见；写类不存在（未开 `--allow-debug-writes`）。
  - [ ] `sandbox` + `--allow-debug-writes`：未绑定会话可见可用写类，已绑定会话（agent）**不可见**且调用被拒。
  - [ ] `sandbox` 下可在对局进行中注册新卡并观察到效果（需先开 `--allow-debug-writes`）。
  - [ ] `play` 下未绑定会话调 `observe.view` 被拒；`sandbox` 下同一调用放行且可读任意座位。
  - [ ] 两模式共用同一套房间/座位/日志机制。
- Rationale: 把"是否编排"与"权限面"正交后，权限面本身还要再切一刀——**注册面**（默认最小）与**可见面**（按会话）分开，才能让"默认零写面"与"agent 不可见写类"同时成立；`OrchestratorOnly` 独立成类而非复用 `Agnostic`/`Bind`，是因为它的判据是"来源 + 未绑定"的组合，塞进既有四类会让门禁语义不可读。
- Terminology: [沙盒模式](../../CONTEXT.md)、[调试 API](../../CONTEXT.md)、[卡包](../../CONTEXT.md)、[会话](../../CONTEXT.md)

### S4: 事件流与会话流水日志
- Status: Done（已落地；执行记录见 [`PROGRESS.md`](./PROGRESS.md)）
- Target: **新增 `src/Orc.Server/Headless/`**（编排、日志、摘要；与 `Room`/`Transport` 同层，不新建工程）、`src/Orc.Server/Transport/ServerHost.cs`（取段点重构 + `calls.jsonl` 写入）、`src/Orc.Server/Program.cs`（停止请求）、`src/Orc/Output/EventSegmentJson.cs`（只调用，不改语义）
- Approach:
  - **把"取段"从推送路径剥离**：段队列单消费者，**唯一取用者仍是 `ServerHost`**，日志与推送共用同一次 `TakeSegments()`。新增 `DrainSegments()`：
    1. `match.Engine.TakeSegments()`
    2. 写 `events.jsonl`（全知 `EventSegmentJson.Serialize(segment, indented:false)`）
    3. 按需写 `seat0.jsonl` / `seat1.jsonl`（脱敏序列化，对手手牌 `$hidden`）
    4. `_service.IngestSegments(...)`（保持观察面语义）
    5. 逐会话分发 `notify/segment`
    - 调用点固定在 **work item 尾部、与动作成败无关**：`HandleToolsCallAsync` 的 `EnqueueAsync` lambda 内 `InvokeAsync` 返回之后；`DriveAiAsync` 的单步 work item 内；`OnActionSettled` 入队的 work item 内。
    - 由此"动作失败但引擎产生了段"、"`--keep-alive` 静置期 AI 推进"都不会滞留段。`PushViews` 仍是"动作成功才推视图"，但**不再承担取段职责**。
    - `DrainSegments()` 对"无活跃对局""空段队列"**零成本短路**（否则每次工具调用都变慢）。
    - **段的分发（脱敏）与日志（全知）分开生成**，不复用同一份 JSON 字符串（否则一次格式化错误同时污染两边）。
  - **终局时序：终局检测只置标志**，不做任何写盘与退出——引擎在终局时**不抛** `MatchChanged(Ended)`，因此由 work item 尾部读 `match.State` 检测终局，记录 `_matchEnded = true` 与终局信息（winner / endReason / turn）后立即返回。
    - 真正的收尾在**当前 work item 尾部**：`DrainSegments()` → `MaybeFinalizeAsync()`（幂等，`_finalized` 保护）。
    - `MaybeFinalizeAsync` 顺序固定：drain 已完成 → 写 `summary.json` → `Flush(true)` → 发出**停止请求**（不直接杀进程）。
    - `Program` 收到停止请求：`await host.DisposeAsync()`（让会话写队列把最后一击的响应送出）→ 返回 `0`。200ms 内响应未送出可放弃，但**不得先杀进程再写盘**。
    - `--keep-alive`：同样执行 drain 与 `summary.json`，但**不发停止请求**；终局后若还有段（不应有）继续按同一路径落盘。
  - **文件形态与对齐键**：
    - `events.jsonl`：每行**一整段**（保留 `entries` 与 `children`）；拆成条目会丢因果结构。
    - `--log-seat-views`：`seat0.jsonl` / `seat1.jsonl` **两个文件**，同样每行一整段（脱敏）。
    - 对齐键＝**段 `sequence`**：三份流同一次 drain 的段序号一致（脱敏流内容可能被遮蔽，但序号相同）。
    - `calls.jsonl` 与段的对齐**不能只靠时间戳**：每条调用记录本次 drain 到的区间 `segmentsFrom` / `segmentsTo`（可空＝本次无段）。固定字段：`id`（自增）、`at`、`sessionId`、`seat`（可空）、`remote`、`tool`、`ok`、`code`、`durationMs`、`queueMs`（可选）、`segmentsFrom`、`segmentsTo`、`phase`（`gate`/`exec`）。
  - **`calls.jsonl` 覆盖面与"谁"**：
    - **门禁拒绝也要记**（回答"谁试图做什么被拒"）：`phase:"gate"`、`ok:false`、`code:"unauthorized"`、`durationMs:0`，不记 `segments*`。
    - 未绑定座位的编排连接：`seat: null`，同时记 `sessionId` 与 `remote`（回环地址）——三者可唯一定位调用源。
    - 耗时口径：`durationMs` 从**通过门禁、准备入队时**起算到响应生成（含排队）；另记 `queueMs`（入队到消费者开始执行），两者分开才能区分"引擎慢"与"排队慢"。
  - **flush 口径与诚实的保证边界**：
    - `StreamWriter { AutoFlush = true }`，每写一行即 `Flush()`（用户态缓冲落到 OS）；终局额外 `Flush(true)`（fsync）。
    - **强杀（`kill -9`）下最多丢"最后一段尚未 flush 的行"；断电/内核崩溃不承诺**——文档不得宣称"绝不丢"。
    - 三份写入（全知流、两份脱敏流）按"段"为单位顺序写，避免三份文件交错出半段。
- Acceptance:
  - [ ] `events.jsonl` 按 `sequence` 单调、不丢段、可逐行解析（每行一整段）。
  - [ ] `--log-seat-views` 的脱敏流中对手手牌写 `$hidden`；与全知流的 `sequence` 对齐。
  - [ ] 进程异常退出时已产生的段仍可读（逐行 flush）；强杀场景的丢行边界写进文档。
  - [ ] `calls.jsonl` 可还原「谁在何时调了什么、结果如何」，含门禁拒绝与被拒来源（`sessionId`＋`seat`/`remote`）。
  - [ ] 终局动作（`command.attack` 打死 HQ）的落盘次序：段落盘 → 脱敏流 → `summary.json` → `Flush(true)` → 停止请求 → 进程退出 `0`。
  - [ ] `DrainSegments` 在无活跃对局/空队列时无额外开销（无热路径退化）。
- Rationale: 把 drain 从推送路径剥离，是因为"推送只在动作成功时发生"这条既有语义会让失败动作与静置期的段滞留在引擎队列里，而日志的承诺是"不丢段"；让 `OnMatchChanged(Ended)` 只置标志，是因为该回调发生在 `InvokeAsync` 内部、早于 `PushViewsAsync`，在那里启动退出会把最后一击的段与摘要顺序弄反（终局段丢失）。
- Terminology: [事件流段](../../CONTEXT.md)、[动作作用域](../../CONTEXT.md)、[观察面](../../CONTEXT.md)、[对局执行器](../../CONTEXT.md)

### S5: stdio MCP 桥
- Status: Done（已落地；执行记录见 [`PROGRESS.md`](./PROGRESS.md)）
- Target: **新增生产工程 `src/Orc.Mcp.Bridge`**（零引擎依赖，依赖 `Orc.Client`＋`Orc.Protocol`；注册进 `OrcEngine.slnx`；放 `src/` 因为它是长期基础设施——脚本与文档都引用它，`samples/` 保持"示例/参考"定位）、`src/Orc.Server/Transport/ServerHost.cs`（无改动，只消费其协议面）
- Approach:
  - **进程形态**：独立进程；对外 stdio 讲 MCP（`tools/list`、`tools/call`），对内用 `OrcClient` 连服务端 TCP（`CallRawAsync`＋`NotificationReceived`）。现无任何 stdio MCP 实现可复用（`samples/Orc.Mcp.HostSample` 是**进程内**宿主，且 R Constraints 明令不复用）。
  - **启动形态（硬约束）**：`dotnet <绝对路径>/Orc.Mcp.Bridge.dll`。**禁止 `dotnet run`**——其还原/构建输出会落到 stdout，MCP 客户端读到第一行不是 JSON-RPC 帧就崩。不要求发布原生 exe（已依赖 .NET 8 runtime，`dotnet <dll>` 足够，且免去"发布产物是否最新"的额外状态）；片段里一律用**绝对路径**。
  - **stdout 纯净性**：桥内所有诊断走 stderr，`Console.Out` 只允许协议帧；桥工程**不得引用任何会 `Console.WriteLine` 的第三方库**（当前零依赖，保持）。
    - 自检做不到就退化为"代码约束 ＋ 测试"：e2e 必须 spawn 桥、发 `initialize`/`tools/list`，断言 stdout **每一行**都能 `JsonDocument.Parse` 成功。
    - 文献约定：**不要用 `dotnet run` 拉起桥，构建噪声会污染 stdout**；片段是唯一推荐入口。
  - **桥只连已就绪实例，不拉起服务端**。参数表：
    | 参数 | 必填 | 语义 |
    |---|---|---|
    | `--connect <host:port>` 或 `<port>` | 是 | 服务端地址；缺省 host＝`127.0.0.1`（允许显式局域网地址，不作验收项）；纯 `port` 形态必须接受（避免脚本拼接歧义） |
    | `--token-file <path>` | 是 | 座位令牌文件（`agent.token`） |
  - **启动序列**：连接 → `initialize` → 读 token → `room.resume` → 进入 stdio 循环。任一步失败：原因写 stderr、向 MCP 客户端返回结构化错误、**以非零码退出**（不半死不活地挂着）。
  - **退出码表**：

    | 码 | 语义 |
    |---|---|
    | `0` | 正常收尾（含"服务端退出/端口关闭 → 当前与后续 `tools/call` 一律 `not-connected` → 桥退出"） |
    | `2` | token 失效 / `room.resume` 被拒（`unauthorized`）——与"服务端没了"区分 |
    | 其它非零 | 启动失败（连接失败、参数错误等） |

    - 服务端退出后桥**不留孤儿进程**。
  - **桥专属工具只有两个**：
    - `seat.state`：读当前精简状态（见下）。
    - `seat.segments { sinceSequence? }`：读桥缓冲（只读，最多 200 段），返回段 JSON 与 `dropped` 标记；超出按 `sinceSequence` 截断并返回 `truncated:true`（避免一次调用打爆上下文）。
    - 桥**不向服务端引入任何新工具**、不改授权语义；服务端清单原样透传（`play` 下与 player 模式一致）。
  - **`seat.state` 固定字段（<15 个）**：`stateVersion`、`phase`、`turn`、`activeSide`（`self`/`opponent`）、`yourTurn`、`pendingSessionId?`、`handCount`、`enemyHandCount`、`segments{buffered, dropped, lastSequence}`。全量视图仍由服务端 `observe.view` 提供。
    - **每次 `tools/call` 的结果都附精简 state**，不做"仅变化时附"（agent 决策依赖最新态；省 token 抵不上"基于旧态决策"的代价）。
    - 动作结果里已有的 `details.pending` **保留**（"该动作引起的结果"）；state 的 `pendingSessionId` 只作当前态索引，**不重复 pending 候选明细**——两者语义不同，不算冗余。
  - **state 是当前态快照，不是增量流**：`stateVersion` 单调递增，任何入站帧（`notify/view`、`notify/segment`、`notify/target.request`、`notify/target.expired`、`notify/match.started`、`notify/match.ended`）＋1。AI 连推多回合而 agent 静置时，桥**只保留最后一条 view**；中间变化用两个信号表达：`stateVersion` 跨过多个值（变了）、`dropped > 0`（有段没收到）。**不把中间变化压进一份 state**（那会让 state 变成伪事件流）。
  - **段对 agent ＝ best-effort，可丢**；完整性的唯一承诺在 `events.jsonl`（S4）。桥侧段缓冲**定长 200**（与 `SegmentCache` 同量级），超出丢最旧并计数。
    - **不为 `play` 新开段拉取工具**（会破坏"play 协议面零变更"），也不让桥做无界缓冲。
    - **丢段必须可感知**：显式给 `dropped` 计数，**不要**用"序号跳跃"让 agent 自己猜。
    - 桥侧的推送接收队列同样有界 ＋ `DropOldest`，但计算 `dropped` 时**以段为单位**；view 的丢弃不计入（view 是幂等快照，丢旧的无害）。
  - **断线恢复由桥内部自动 `room.resume`**（读 `agent.token`），**不暴露成 agent 可调工具**——agent 不该管连接细节。**服务端退出/端口关闭** → 返回 `not-connected`（桥退出 `0`）；**token 失效**（实例已重启）→ 返回 `unauthorized`（桥退出 `2`，提示重新读取 `agent.token`）。
  - **未连接/未绑定**调 `seat.state` → `ok:false, code:"not-connected"`，**不返回空状态**（空状态会被 agent 误读成"对局未开始"）。
  - **职责边界**：桥**不**解析段内容做决策，只做缓存与转发；"这段意味着什么"的判断属于 agent。
  - **测试**：桥的用例并入 `tests/Orc.Mcp.Tests`（直接 `ProjectReference` 引 `Orc.Mcp.Bridge`，**不为测试复制逻辑**）；只做最小断言（见 S8）。
  - **不挂死底线**：即使段全丢，`seat.state` 仍必须能告诉 agent"现在轮到你、无待决"。
- Acceptance:
  - [ ] `play` 下桥暴露的服务端工具清单与 player 模式一致；桥专属状态工具单独可辨（仅 `seat.state`/`seat.segments`）。
  - [ ] agent 可完成一整局；选靶可应答；无推送通道也不挂死（静置后 `seat.state` 仍给出 `yourTurn`）。
  - [ ] 断线后凭令牌自动恢复，不重开对局；`token` 失效返回 `unauthorized`（桥退出码 `2`），服务端退出返回 `not-connected`（桥退出码 `0`）。
  - [ ] 段超 200 时 `dropped` 递增且可感知；`seat.segments` 超限返回 `truncated:true`。
  - [ ] 桥不向服务端引入任何新工具、不改服务端授权语义。
  - [ ] 桥的接入配置（command/args）可由脚本直接打印；片段为 `mcpServers` 形态、键名 `orc-seat{N}`、`command:"dotnet"` ＋ 绝对 dll 路径。
  - [ ] stdout 每一行都能被 `JsonDocument.Parse` 解析（spawn 桥 + `initialize`/`tools/list` 的 e2e 断言）。
- Rationale: 选"每次结果附精简 state"而非"仅变化时附"，是因为 MCP 的 stdio 侧没有服务端推送唤醒能力，任何"变化通知"都必须由 agent 的下一次调用承载；把 state 压到固定少数字段是让这份常附带成本可接受的手段。段退化为 best-effort 而日志保持权威，是因为 play 模式下不存在段拉取面，而新增拉取面会破"协议面零变更"。
- Terminology: [客户端](../../CONTEXT.md)、[会话](../../CONTEXT.md)、[事件流段](../../CONTEXT.md)、[观察面](../../CONTEXT.md)

### S6: 生命周期、退出码、结果摘要与批量跑局
- Status: Done（已落地；执行记录见 [`PROGRESS.md`](./PROGRESS.md)）
- Target: `src/Orc.Server/Program.cs`、`src/Orc.Server/ServerBootstrap.cs`、`src/Orc.Server/Transport/ServerHost.cs`（终局回调）、新增单局摘要组件
- Approach:
  - **一进程一局**：服务端**不实现**进程内多局。依据是既有四处契约——`Room` 单向状态机（`InMatch`/`Ended` 不可回 `Lobby`）、`MatchExecutor.Attach` 拒绝第二个活跃对局（`MatchExecutor.cs:132`）、`McpServiceOptions` 全 `init`、`ServerHost._service/_room` `readonly`。批量＝**脚本循环拉起 N 个独立进程**（首版串行，不做 `--jobs` 并行）。
  - **落点差异（相对 R2/R7 字面）**：「批量模式每局结束继续下一局」的**落点从服务端移到脚本**；服务端只负责"一局跑完→写摘要→按退出码退出"。
  - **日志与序号**：每局独立文件，`sequence` 每局从 0 重新开始（新 `Match`＝新引擎＝新段队列）。
    - 单局（不带 `--games`）：`<run-dir>/events.jsonl`、`calls.jsonl`、`summary.json`、`agent.token`。
    - 批量：`<run-dir>/runs/run-001/{...}`（与 R6 已列结构一致）。**不跨局续写同一文件**。
  - **全局 `summary.json` 的写入者＝脚本**，不是服务端。服务端只写单局摘要；脚本在全部结束（或 fail-fast 中止）后读各局摘要聚合，字段至少含：`games`、`completed`、`failed`、`perRun[]`（run 目录、seed、winner、turns、endReason、exitCode）。
  - **端口每局重开**：每局 `--port 0` 由系统分配，脚本从各局就绪行解析；不复用同一端口（避免上一局 `TIME_WAIT` 让下一局随机失败）。R6「启动成功后打印端口」按「打印当前局端口」理解。
  - **批量退出码（脚本映射）**：全部成功 `0`；某局启动失败 `1`、运行期异常 `2`、等 agent 超时 `3`。默认 **fail-fast**：某局非零即停止后续局、透传该局退出码，并把已完成部分与失败局写入全局摘要。`--keep-going` 为可选参数，首版可不实现。
  - **`--keep-alive` 只延长进程存活，不重置房间**：终局后进程不退出、房间停在 `Ended`，可继续查询/导出日志与观察面；**不支持再来一局**，此时 `room.start` 被拒（"房间已开局"）。这就是 R7「或明确拒绝并说明」的落地。
  - **修既有缺陷**：`Room.MarkEnded()` 全仓无调用者（`Room.cs:395` 仅定义），终局后 `room.State` 实际停在 `InMatch`——在 `ServerHost.OnMatchChanged(Ended)` 里调 `room.MarkEnded()`，让 `--keep-alive` 与日志的终局判定有真实依据。`MatchExecutor.Detach()` 在单局进程模型下**不调用**，保持现状。
  - **参数互斥/校验（脚本侧）**：`--games N`（N>1）与 `--keep-alive` 互斥，参数校验阶段直接报错退出 `1`（不留给运行时）；`--games N>1` 且任一 seat 为 `agent` 同样报参数错误，**不静默降级**（批量仅 `ai+ai`）。
- Acceptance:
  - [ ] 终局后进程自行退出，退出码 0（启动失败 1 / 运行期异常 2 / 等待 agent 超时 3）。
  - [ ] `summary.json` 含胜负、回合数、终局原因、种子、卡包指纹、日志路径、错误计数。
  - [ ] `--games N` 每局独立目录与独立 `sequence`，全部结束写总 `summary.json` 后退出 0；批量仅允许 `ai+ai`。
  - [ ] `--keep-alive` 下终局后进程存活；`room.start` 被明确拒绝并说明原因。
  - [ ] `ServerHost.OnMatchChanged(Ended)` 后 `room.info` 的 `state` 为 `ended`（新守卫用例）。
- Rationale: 进程隔离让日志、端口、种子、令牌、崩溃互不污染，批量的确定性高于"进程内重建对局域"；代价是每局一次进程启动开销（可接受，首版串行）。
- Terminology: [房间态](../../CONTEXT.md)、[房间](../../CONTEXT.md)

### S7: 一键脚本与运行目录
- Status: Done（已落地；执行记录见 [`PROGRESS.md`](./PROGRESS.md)）
- Target: `scripts/headless_sandbox.py`（主实现）、`scripts/headless.sh`（薄包装）
- Approach:
  - **主实现＝Python 单文件**（Python 3 标准库：`subprocess`、`json`、`socket`、`signal`）；`scripts/headless.sh` 只是三行 bash：`exec python scripts/headless_sandbox.py "$@"`。
    - 它满足"bash 可拉起"，但**不承载逻辑**——不把流程写成 shell（JSON 聚合、端口探测、进程树回收都要靠 `jq`/`taskkill` 拼字符串，正是最易翻车处）。
    - 复用 `scripts/` 现有 Python 风格；**不新增 C# 工具**（`ServerProcess` 在进程回收上优势不大，却多一个工程与一次构建）。
  - **脚本职责**：参数校验（先于任何目录创建与构建）→ 构建（默认 `dotnet build`，`--no-build` 可跳过；构建输出转 stderr 或仅失败时打印）→ 逐局拉起进程（串行、每局独立 run 目录与 `--port 0`）→ 解析就绪行 → 打印接入信息 → 等终局/中断 → 回收 → 聚合全局 `summary.json` → 映射退出码（fail-fast）。
  - **参数面**：R6 全部开关（`--mode`、`--packs`、`--seat0/--seat1`、`--games`、`--seed`、`--port`、`--run-dir`、`--deck-rules`、`--log-seat-views`、`--keep-alive`）＋ `--allow-debug-writes`（S3）＋ `--no-build`。
  - **进程回收＝双保险**：
    - 启动子进程时：POSIX `start_new_session=True`；Windows `creationflags=CREATE_NEW_PROCESS_GROUP`；并**始终**传 `--parent-pid <脚本自身 pid>`（复用上游 `ParentWatchdog` 的"父死子退"作兜底）。
    - 终止：Windows `taskkill /F /T /PID <pid>`；POSIX 先 `os.killpg(pid, SIGTERM)`，2 秒未退再 `SIGKILL`。
    - **端口释放判定**：轮询 `socket.connect_ex(("127.0.0.1", port)) != 0`，超时 3 秒即判失败并以非零码退出（**不静默继续下一局**）。
    - `signal` 覆盖 `SIGINT`/`SIGTERM`：先终止服务端进程树再退出，退出码 `130`。
  - **就绪行解析用 `json.loads`**（不依赖 `jq`）：读子进程 stdout 第一行（跳过空行），解析 `ready`/`port`/`agentSeat`/`agentTokenPath`；解析失败时打印该行原文＋最近 stderr 尾部，退出 `1`。
  - **"无残留进程"的责任范围（拆分验收）**：脚本**只对服务端（及其子进程）负责**，桥不在其内——桥由 MCP 客户端拉起，脚本无法也不应知道其 pid；按 S5 裁决，端口关闭后桥自行退出 `0`。
    - `脚本负责`：服务端 pid 消失 ＋ 端口可重新绑定。
    - `桥负责`：连接断开后自行退出（由桥的测试覆盖）。
    - **不得**写"全局进程扫描/按名字杀进程"——那会误伤同机其它实例。
  - **输出分层**：脚本自身 stdout 只用 `[headless]` 前缀行；构建输出、`dotnet` 噪声、服务端 stderr 一律转脚本 stderr 或写 `<run-dir>/server.stderr.log`；批量时每局单独落 `<run-dir>/runs/run-XXX/server.stderr.log`（便于定位"第 3 局为什么起不来"）。JSON 片段单独成块并加起止标记。
  - **中断时的全局摘要**：已完成局写入 ＋ 追加 `aborted: true` 标记，退出 `130`；若 2 秒后端口仍被占用，**必须报错**（说明有异常残留）而不是假装成功。
- Acceptance:
  - [ ] 脚本参数覆盖 R6 列出的全部开关（含 `--allow-debug-writes`、`--no-build`）。
  - [ ] 启动成功后打印端口、运行目录、令牌文件路径、可直接粘贴的 MCP 桥配置片段。
  - [ ] Ctrl-C / 异常后服务端 pid 消失且端口可重新绑定（`130` 退出，全局摘要带 `aborted`）。
  - [ ] 干净目录可重复执行；运行目录结构稳定（`agent.token`/`events.jsonl`/`calls.jsonl`/`summary.json`/`server.stderr.log`/可选 `seatN.jsonl`/`runs/run-XXX/`）。
  - [ ] 就绪行解析失败时报错含原文与 stderr 尾部（不吞错）。
- Rationale: 选 Python 而非 shell，是因为这里真正难的三件事（就绪行 JSON 解析、端口释放探测、Windows 进程树回收）在 shell 里都要靠外部命令拼字符串；保留三行 bash 包装只为满足"bash 可拉起"。把回收做成"脚本主动杀 + `--parent-pid` 兜底"，是因为 Windows 上单靠脚本的 `kill` 不可靠、而单靠 watchdog 有 2 秒检测窗口。
- Terminology: [服务端](../../CONTEXT.md)、[客户端](../../CONTEXT.md)、[房间态](../../CONTEXT.md)

### S8: 无头端到端测试与回归
- Status: Done（已落地；执行记录见 [`PROGRESS.md`](./PROGRESS.md)）
- Target: `tests/Orc.Server.Tests/`（无头编排/日志/摘要/真进程用例）、`tests/Orc.Mcp.Tests/`（桥用例，`ProjectReference` 引 `Orc.Mcp.Bridge`）；**不新增测试工程**
- Approach:
  - **两条接入面都要，分工固定**：
    - **直连 TCP 的假 agent**（`Orc.Client`）＝主力：覆盖协议面、座位门禁、动作推进、段推送、断线重连；快且不依赖构建产物，**所有"整局"用例都走它**。
    - **stdio 桥**＝只做最小断言、不跑整局：`initialize` → `tools/list`（断言含桥专属工具、不含写类）→ `tools/call observe.view` → `seat.state` 一次 → 断言 stdout 每行可 `JsonDocument.Parse`。桥的"打完整局"能力由直连用例间接保证（同一服务端语义），不重复覆盖。
  - **进程内与真进程两层都要，各管一半**：
    - 进程内 `ServerBootstrap`（主力）：自动开局、`PresetSeat`/`Connected` 语义、`DrainSegments` 与 `events.jsonl` 段连续性、`summary.json` 字段、`--mode sandbox` 读写拆分、`observe.view` 分流。
    - 真进程（少量 2–3 个）：`--headless` 就绪行字段（含 `agentSeat`/`agentTokenPath`，`ai+ai` 时省略）、`--exit-on-end` 退出码 `0`、`--wait-agent` 超时退出码 `3`、无头专属参数在非无头下调用的退出码 `1`。
  - **真进程用例打 `[Trait("Category","Process")]`**：默认参与 `dotnet test`，允许 `--filter "Category!=Process"` 跳过；**不给"重试一次"的魔法**（掩盖真实竞态）——用放大超时（60s）＋固定 `--port 0`。
  - **`OrcEngine.slnx` 只新增 `src/Orc.Mcp.Bridge` 一个生产工程**。
  - **R7「全绿」口径**：默认 `dotnet test OrcEngine.slnx`（含 Process 类）全绿；若某环境确有不稳定，必须在本文档记录过滤命令与原因，**不得默默跳过**。
  - **边界用例拆两层**（各自只断言自己能负责的那一半）：
    - 用例 A（进程内）：跑到终局 → 读 `events.jsonl` → 断言 `sequence` 单调连续（引擎序号自 1 起）、脱敏流同序号对齐。
    - 用例 B（真进程）：`--headless --exit-on-end` ＋ 直连假 agent 打一局 → 断言退出码 `0`、`summary.json` 存在且 `winner`/`endReason` 非空、`events.jsonl` 存在（**不**做段连续性断言，避免把进程层的脆弱叠到引擎层）。
- Acceptance:
  - [ ] `dotnet test OrcEngine.slnx` 全绿，无既有用例回归（基线 2263 通过 / 0 失败）。
  - [ ] 新增 e2e：拉起无头实例 → 假 agent 占座 → 开局 → 推进若干回合 → 断言日志与摘要（用例 A＋B）。
  - [ ] 桥的 stdout 每行可解析；`tools/list` 区分"服务端工具"与"桥专属工具"（避免桥以后加工具时测试含义漂移）。
  - [ ] 映射表（下）中每条 R 的每个验收点都能指到具体用例。

### 验收覆盖映射（R1–R7 → 用例层）

> 用途：防止"验收项看似覆盖、实际没人测"。新增/修改用例时必须更新本表。

| 需求验收点 | 层 | 用例/断言落点 |
|---|---|---|
| R1 单命令启动到就绪、退出后无残留 | 真进程 | `ProcessHeadlessTests.HeadlessReadyLine` / 脚本级 `headless_sandbox.py` 冒烟 |
| R1 就绪输出可脚本解析（stdout 单行 JSON） | 真进程 | `ProcessHeadlessTests.HeadlessReadyLine`（字段集合 + 解析成功） |
| R1 未开无头时行为与现状一致 | 进程内＋既有 | 既有 2263 用例全绿；`ProcessHeadlessTests.NonHeadlessReadyLineUnchanged` |
| R1 无头专属参数在非无头下报错退出 1 | 真进程 | `ProcessHeadlessTests.HeadlessOnlyArgsRejected` |
| R1 显式 `--sandbox` 以显式值为准（不被 `<run-dir>/sandbox` 覆盖） | 真进程 | `ProcessHeadlessTests.ExplicitSandboxRootWins` |
| R2 无外部连接不提前开局（`room.info` 显示） | 进程内 | `HeadlessTests.AutoStartNotTriggeredWithoutAgent` |
| R2 agent `resume` 后自动开局、`room.start` 仍被拒 | 进程内 | `HeadlessTests.AutoStartOnResume` / `HeadlessTests.AgentStartRejected` |
| R2 一座位一控制器、断线凭令牌重连、房间态不释放座位 | 进程内 | `HeadlessTests.LobbyDisconnectKeepsSeat` / `HeadlessTests.ReconnectWithToken` |
| R2 角色可配 `ai+ai` | 进程内 | `HeadlessTests.AiAiStarts` |
| R2 无头默认 `lenient` 三档分项放宽（卡池权威保留） | 进程内 | `DeckRuleTierTests.Lenient_RelaxesSizeAndCopyLimits_ButKeepsCardPoolAuthority` / `DeckRuleTierTests.Lenient_SkipsFactionPairing_ButStrictRejects` |
| R2 AI 卡组尽力生成且确定（小卡包非空） | 进程内 | `DeckRuleTierTests.AiDeckGenerator_BestEffort_ReturnsNonEmptyAndDeterministic` |
| R2 `room.claim` 与 `room.start` 同档位判定 | 进程内 | `DeckRuleTierTests.Claim_UsesLenientTier_SmallDeckAccepted_UnknownCardRejected` |
| R2 `ai+ai` 跑到终局（无 agent 路径的终局自退） | 进程内 | `HeadlessTests.AiAiReachesEndAndWritesSummary` |
| R2 等待 agent 超时→退出码 3＋原因 | 真进程 | `ProcessHeadlessTests.WaitAgentTimeout` |
| R2 `--games N` 每局独立目录、全局 summary、仅 `ai+ai` | 脚本 | `headless_sandbox.py` 冒烟（脚本级用例，非 xunit） |
| R3 桥工具清单与 player 模式一致、桥专属工具可辨 | 桥（`Orc.Mcp.Tests`） | `BridgeTests.ToolsListContract` |
| R3 agent 可完一整局、选靶可应答、无推送不挂死 | 直连 TCP | `HeadlessTests.FullGameWithFakeAgent` / `BridgeTests.SeatStateAfterIdle` |
| R3 断线凭令牌恢复、不重开对局 | 桥＋直连 | `BridgeProcessTests.BridgeStdoutIsJsonPerLineAndListsBridgeTools` / `HeadlessTests.ReconnectWithToken` |
| R3 桥不引入新服务端工具、不改授权语义 | 桥 | `BridgeTests.ToolsListContract`（对比直连 `tools/list`） |
| R3 桥配置可由脚本打印 | 脚本 | `headless_sandbox.py` 冒烟输出断言 |
| R4 `events.jsonl` `sequence` 单调不丢段（引擎自 1 起） | 进程内 | `HeadlessTests.EventsSequenceContiguous`（用例 A） |
| R4 `--log-seat-views` 对手手牌 `$hidden` | 进程内 | `HeadlessTests.SeatViewHidden` |
| R4 异常退出已产生段可读（逐行 flush） | 真进程 | `ProcessHeadlessTests.AbruptKillKeepsFlushedLines` |
| R4 日志路径可覆盖 | 进程内 | `HeadlessTests.RunDirOverride` |
| R4 `calls.jsonl` 还原谁/何时/什么/结果 | 进程内 | `HeadlessTests.CallsJsonlIncludesGateDenials` |
| R5 `play` 下调试工具不存在且调用被拒 | 进程内 | `ModeTests.PlayHidesDebugTools` |
| R5 `sandbox` 读类可见、写类默认不注册 | 进程内 | `ModeTests.SandboxReadOnlyByDefault` |
| R5 `--allow-debug-writes` 下写类对未绑定可见、对 agent 不可见 | 进程内 | `ModeTests.DebugWritesOrchestratorOnly` |
| R5 `sandbox` 下对局中注册新卡并观察效果 | 进程内 | `ModeTests.SandboxHotCardRegistration` |
| R5 未绑定 `observe.view`：play 拒绝 / sandbox 全知 | 进程内 | `ModeTests.ObserveViewUnboundPlayRejected` / `ModeTests.ObserveViewUnboundSandboxOmniscient` |
| R5 两模式共用同一套房间/座位/日志机制 | 进程内 | `ModeTests.SharedRoomAndLogPath` |
| R6 脚本参数面、启动打印、Ctrl-C 无残留 | 脚本 | `headless_sandbox.py` 冒烟＋`ProcessHeadlessTests.ScriptHelp`（Ctrl-C 回收为手动/脚本级验收） |
| R6 运行目录结构稳定 | 真进程＋脚本 | `ProcessHeadlessTests.RunDirLayout` |
| R7 终局自动退出、退出码 0/1/2/3 | 真进程 | `ProcessHeadlessTests.ExitCodeZeroWithSummary`（用例 B）＋ `ProcessHeadlessTests.WaitAgentTimeout`(3) / `ProcessHeadlessTests.HeadlessOnlyArgsRejected`(1) |
| R7 `summary.json` 字段（胜负/回合/原因/种子/指纹/日志路径/错误计数） | 进程内 | `HeadlessTests.SummaryFields` |
| R7 `--keep-alive` 终局后存活、`room.start` 被拒 | 真进程 | `ProcessHeadlessTests.KeepAliveRejectsStart` |
| R7 `dotnet test` 全绿无回归 | 全量 | CI 口径＝含 `Category=Process` |
| R7 新增 e2e（用例 A＋B） | 进程内＋真进程 | 见上两处 |

- Rationale: 把"协议/引擎断言"留在进程内、把"CLI/退出码/回收"放真进程，是因为这两类断言的失败模式完全不同——前者要能断点看状态，后者要能看进程行为；混在一层会让进程层的脆弱（端口、时序）污染引擎层结论。桥只做最小断言而不跑整局，是因为桥与直连共用同一服务端语义，"整局能力"由直连用例覆盖即可，重复覆盖只会让桥的测试变成"服务端测试的第二份拷贝"。
- Terminology: [服务端](../../CONTEXT.md)、[客户端](../../CONTEXT.md)、[事件流段](../../CONTEXT.md)

## 待决策清单（grill 完成）

| # | 决策点 | 关联 | 状态 |
|---|---|---|---|
| Q1 | 自动开局的「系统房主」如何在 `HostSlot()==null` 的房主约束下成立 | S2 / R2 | **已裁决**（Room 内部旁路 `StartBySystemAsync`；不变量：外部无处置权） |
| Q2 | 多局承载边界（批量/keep-alive） | S6 / S7 / R2 / R7 | **已裁决**（一进程一局；批量与全局摘要移到脚本；`--games`×`--keep-alive` 互斥） |
| Q3 | 启动选项面与就绪行契约 | S1 / R1 / R5 | **已裁决**（`--headless` 与 `--mode` 正交；就绪行增量扩展；`--run-dir`/`--sandbox` 并存；`--seed` 单一真值；`agent.token`） |
| Q4 | sandbox 读/写可见性口径与落点 | S3 / R5 | **已裁决**（明示不设防；写类默认不注册＋`--allow-debug-writes`；`tools/list` 按会话过滤；新增 `OrchestratorOnly`；`observe.view` 未绑定在 play 拒绝、sandbox 全知） |
| Q5 | 桥的可拉取状态模型与桥专属工具面 | S5 / R3 | **已裁决**（段 best-effort＋`dropped` 计数；每次结果附精简 state；state 为快照非增量流；仅 `seat.state`/`seat.segments`；断线桥内自动 `room.resume`；未连接返回 `not-connected`） |
| Q6 | 日志挂载点、终局时序与文件形态 | S4 / R4 | **已裁决**（`DrainSegments` 剥离出推送路径、work item 尾部 drain、`MarkEnded` 只置标志、`MaybeFinalizeAsync` 幂等收尾、每行整段＋`sequence` 对齐、`calls.jsonl` 含门禁拒绝、flush 边界诚实声明） |
| Q7 | `--deck-rules lenient` 的放宽口径与落点 | S2b / R2 / R6 | **已裁决**（strict/lenient/none 三档分项放宽；lenient 保留 blank-id 与 unknown-card；`AiDeckGenerator` 尽力生成且不返回空；两处校验共用同一规则实例；摘要补卡组指纹） |
| Q8 | 桥的拉起形态与打印片段契约 | S5 / S7 / R3 / R6 | **已裁决**（`dotnet <绝对 dll 路径>`、禁 `dotnet run`、`--connect`/`--token-file`、退出码 0/2/其它、`mcpServers` 片段键名 `orc-seat{N}`、`ai+ai` 打印纯文本） |
| Q9 | 脚本形态与回收机制 | S7 / R6 | **已裁决**（Python 单文件＋bash 薄包装；`taskkill /T /F` / `killpg`＋`--parent-pid` 双保险；端口探测 3 秒；`json.loads` 解析就绪行；脚本只对服务端负责） |
| Q10 | 预置座位的 `Connected` 语义与自动开局触发判据 | S2 / R2 | **已裁决**（`PresetSeat` 且 `Connected=false`；`Connected` 仅展示；触发只认绑定事实＋执行期二次确认；无头不 DropSeat；token 失效仅因重启；`--wait-agent` 默认 300s/退出码 3） |
| Q11 | 无头 e2e 的覆盖分工与用例落点 | S8 / R7 | **已裁决**（直连 TCP 为主＋桥只做最小断言；进程内管引擎断言、真进程管 CLI/退出码/回收；不新增测试工程；`Category=Process` 可过滤；映射表进文档） |

无遗留「it depends」项。

## 跨步骤不变量（实现期不得违反）

1. **单写者**：进入引擎的一切调用（含自动开局、AI 步、drain）必须经 `SerialWorkQueue` 单消费者；会话线程不得直接调引擎或房间开局入口。
2. **协议面零变更**：`play` 模式下工具清单、推送语义、授权门禁与上游一致；不为无头/桥新增任何服务端工具，不为"agent 调 `room.start` 被拒"新增特判或错误码。
3. **段单消费者**：`TakeSegments()` 只有一个取用者（`ServerHost.DrainSegments`）；日志、观察面缓存、推送分发共用同一次取用。
4. **`Connected` 只用于展示**：开局判据只认"会话绑定到 agent 座位"这一事实，且执行期二次确认。
5. **无头座位生命周期＝进程生命周期**：座位只能由进程退出而消失；房间态断线不释放、不销毁 token。
6. **就绪行是增量契约**：`{"ready":true,"port":N}` 的既有字段语义不可变；消费者只认 `ready`/`port`。
7. **日志完整性只在 `events.jsonl`**：agent 侧段为 best-effort，必须显式暴露 `dropped`，不得用"序号跳跃"让 agent 猜。
8. **调试写面故意不设防**：`OrchestratorOnly` 判据（回环＋未绑定）只防误触，不阻止同机进程有意绕过；隔离靠 OS 用户/容器，不在本需求范围。
9. **桥的 stdout 只承载 JSON-RPC 帧**：诊断一律 stderr；片段生成器永不产出 `dotnet run`。
10. **一进程一局**：不在进程内重建对局域；批量与全局摘要由脚本承担。
