# Requirements — MCP 无头对战问题修复

修复 `outputs/agent-match/mcp-run/problem-report.md` 记录的问题，其中「反制拦截己方指令」改变对局结果，属最高优先级；其余为观察面（bridge / calls.jsonl / summary）与引擎候选同源问题。

## Scope

- Includes:
  - 反制（`CounterCard`）对**指令归属**的判定：己方指令不得被己方反制拦截。
  - `seat.board.pendingSessionId` 的座位归属（对手回合的选靶请求不得记到本座位）。
  - `attackPreviews` 与引擎攻击候选集合同源。
  - 动作类调用返回的 `state` 与同载荷 `details` 一致（不落后一帧）。
  - `seat.board` 暴露反制的「已武装 / 未武装」区别。
  - `calls.jsonl` 对「应答型动作」的可归因性（哪个动作、选了谁、产生哪些段）。
  - `summary.errorCount` 的口径（基础设施错误 vs 业务否定）。
  - 相关注释腐化与三处接口语义不齐（`hand[].slot`、`pending.selector`、已消耗反制再点）。
- Excludes:
  - 前端（`Orc.UI` 客户端）渲染改动；仅保证投影载荷字段可用。
  - 卡牌数值平衡、AI 决策策略质量。
  - 协议版本号、`schemaVersion` 升级与向后兼容层。
  - `outputs/agent-match/mcp-run` 既有产物的重写（只作为回归基线）。

## Constraints

- 反制语义改动不得破坏既有跨方反制用例（`P4OrderFlowTests` / `PlayChainCounterTests` / `CounterEffectAssemblyTests`）。
- 内核 `OrderFlow` 不得认识具体卡类型（`CounterCard` 属游戏层）——归属判定下沉位置需符合既有分层。
- 观察面改动只增字段不改既有字段语义（除 `hand[].slot` 需明确裁决）。
- `calls.jsonl` / `summary.json` 的新字段对既有消费者（agent 提示词、回归脚本）保持可解析。

## Requirement Items

### R1: 反制的武装窗口与归属过滤

- Status: Confirmed
- Scenario/Trigger: 反制卡已激活（`CounterActivationData.IsActive = true`）时，任意玩家打出指令卡（`OrderCard`）经 `OrderFlow` 结算；以及回合切换到反制归属玩家的时刻。
- Behavior:
  1. **归属过滤**：指令卡归属与反制卡归属为同一玩家时，反制的效果 handler 不得执行（不得 `ctx.Stop()`/`ctx.Interrupt()`），指令照常结算、照常扣费离手；反制保持激活、不消耗、不退费。
  2. **仅在敌方回合生效**：反制只在「当前行动方 ≠ 反制归属」时生效；当前行动方是反制归属时一律不生效（与 1 互为双保险）。
  3. **回合开始自动解除**：回合切换到反制归属玩家时（己方回合开始），该玩家处于激活态的反制自动取消激活——置 `IsActive = false`、注销 Counter 段 handler、清实扣额记录、**不返还指挥点**、不消耗、卡留在手牌；并发一条 `counter.disarmed`（载荷含 `Card`、`Player`、`Reason:"turn-start"`）。
  4. 归属未知（裸卡/脱局构造）时不做过滤，保持既有行为。
- Acceptance:
  - [ ] 己方回合激活反制后，己方打出指令：`card.played` 发出、指令效果执行、反制 `IsConsumed == false` 且 `IsActive == true`。
  - [ ] 对手回合打出指令：反制 handler 执行、`Stop` 生效、反制被消耗并弃置（既有跨方用例不回归）。
  - [ ] 对手回合未命中 → 己方回合开始时该反制 `IsActive == false`、仍在手牌、玩家点数与解除前一致（不退点）。
  - [ ] 同上时刻事件流出现 `counter.disarmed`，且 `Card` 指向该反制、`Reason == "turn-start"`。
  - [ ] 自动解除后，下一次己方回合可重新激活（再扣一次点）。
  - [ ] 己方回合内主动取消（再点一次）仍按实扣额退点（既有语义不回归），且不发 `counter.disarmed`。
- Terminology: [反制](../CONTEXT.md)、[反制武装窗口](../CONTEXT.md)、[指令](../CONTEXT.md)

### R2: 待决选靶按座位归属

- Status: Confirmed
- Scenario/Trigger: 任一座位发起需要选靶的动作（攻击 / 移动 / 部署 / 效果选靶 / 换牌），其他座位正连着同一服务端。
- Behavior: 选靶请求携带归属座位；服务端只向该座位的会话推送 `notify/target.request`；桥的 `seat.board.pendingSessionId` 只反映本座位的待决请求。
- Acceptance:
  - [ ] 对手回合的选靶请求不写入本座位桥状态；对手回合结束后 `board.pendingSessionId == null`。
  - [ ] 本座位自己的选靶请求仍能正常发布、应答、清理。
  - [ ] `target.submit` 在座位不匹配时被拒绝（既有 S6 校验生效）。
- Terminology: [待决请求](../CONTEXT.md)

### R3: 攻击预览与引擎候选同源

- Status: Confirmed
- Scenario/Trigger: `observe.view` 产出 `actions.attackPreviews`，同一对局状态下发起 `command.attack`。
- Behavior: `attackPreviews` 的 `(attackerUid, defenderUid)` 集合与引擎 `command.attack` 返回的待决候选集合逐一对应（同一 attacker 下）。
- Acceptance:
  - [ ] 敌支援线单位可被攻击时出现在预览中。
  - [ ] 敌前线存在单位时，若引擎允许攻击 HQ，则预览含 HQ 条目；若不允许则不含。
  - [ ] `moves` 与引擎移动候选同源（不自行枚举前线空槽）。
- Terminology: [攻击候选](../CONTEXT.md)

### R4: 动作返回的 state 不落后

- Status: Confirmed
- Scenario/Trigger: 任一动作类工具调用（`play.*` / `command.*` / `match.*` / `mulligan.*` / `target.*`）返回。
- Behavior: 返回载荷中的 `state.turn` / `state.activeSide` / `state.yourTurn` 与同载荷 `details` 描述的动作结果一致；不得出现同一载荷两个真相。
- Acceptance:
  - [ ] `match.end_turn` 返回 `state.turn == details.turn`。
  - [ ] 改变相位的动作返回后 `yourTurn` 正确。
- Terminology: [状态快照](../CONTEXT.md)

### R5: board 暴露反制武装状态

- Status: Confirmed
- Scenario/Trigger: 玩家激活 / 取消反制后读取 `seat.board`。
- Behavior: `hand[]` 条目含 `isCounterArmed`；board 顶层含已武装反制数（与 `view.selfCounterCount` 同口径）；手牌总数与武装数分列。
- Acceptance:
  - [ ] 激活后该卡 `isCounterArmed == true`；取消后为 `false`。
  - [ ] `armedCounterCount` 与 `observe.view` 的 `selfCounterCount` 一致。
- Terminology: [反制武装](../CONTEXT.md)

### R6: 应答型动作可归因

- Status: Confirmed
- Scenario/Trigger: `command.attack` / `command.move` 进入 `targeting`，随后 `target.submit` 落地并产生事件段。
- Behavior: `calls.jsonl` 能唯一确定「应答的是哪个动作、选了哪个候选、产生了哪些段」。
- Acceptance:
  - [ ] `target.submit` 记录含动作类别与所选项标识（或段区间回填到发起动作记录）。
  - [ ] 给定一次攻击，可仅凭 `calls.jsonl` + `events.jsonl` 建立对应关系。
- Terminology: [应答型动作](../CONTEXT.md)

### R7: errorCount 口径

- Status: Confirmed
- Scenario/Trigger: 对局中出现业务否定（`pending-targeting`、`not-ready`、`NotYourTurn`、`TargetSlotOccupied` 等）。
- Behavior: `summary.errorCount` 只统计基础设施错误（传输断开 / 未授权 / 内部故障）；业务否定另计 `failedCalls`。
- Acceptance:
  - [ ] 一次 `target.claim` 的 `pending-targeting` 不增加 `errorCount`，只增加 `failedCalls`。
  - [ ] `not-connected` / `unauthorized` / `internal` 仍计入 `errorCount`。
- Terminology: [基础设施错误](../CONTEXT.md)

### R8: 注释与接口语义一致

- Status: Confirmed
- Scenario/Trigger: 阅读 `McpService.Projection.ComputeActions` 注释；agent 读 `seat.board.hand` 与 `pending.selector`；对已消耗反制再次 `play.counter`。
- Behavior: 注释描述与实际实现一致；手牌索引字段改名为 `handIndex`（战线条目仍用 `slot`）；待决请求声明动作类别（`pending.action`）；已消耗/已销毁反制再使用返回明确失败。开发指南 §4 的示例同步更新。
- Acceptance:
  - [ ] 注释不再声称「不预测伤害与死亡」。
  - [ ] `seat.board.hand[]` 的序号字段名为 `handIndex`，`selfLine`/`enemyLine` 仍为 `slot`；`docs/无头沙盒-测试环境/开发指南.md` §4 示例同步。
  - [ ] `pending.action` 可区分 `attack` / `move` / `deploy` / `effect` / `mulligan`；合并型「指挥拖拽」请求（移动+攻击候选并集）标 `command`（发布时无法确定单一类别）。
  - [ ] 已消耗反制 `play.counter` 返回失败（或明确 no-op 语义），不得静默成功。
- Terminology: [手牌索引](../CONTEXT.md)
