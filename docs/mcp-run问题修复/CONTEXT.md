# CONTEXT — MCP 无头对战问题修复

本文件登记本次修复涉及的术语与既有代码位置的对应关系；实现阶段的用语一律以此为准。

## 术语表

| 术语 | 定义 | 代码锚点 |
|---|---|---|
| 反制 | 反制卡（countermeasure），激活/取消两态；激活时把效果 handler 注册到 `OrderFlow` 的 Counter 段，命中后一次性消耗。 | `src/Orc.Game/Cards/CounterCard.cs` |
| 反制归属 | 反制卡的所有者（`Card.Owner`），即「谁挂的这张反制」。 | `CardBase.Owner` |
| 指令 | 指令卡（order），打出时经 `OrderFlow`（Counter → Resolve → Finalize）结算。 | `src/Orc/OrderFlow.cs`、`OrderCard` |
| 指令归属 | 指令卡的所有者，即「谁打的这张指令」。 | `CardBase.Owner` |
| 反制归属过滤 | 反制 handler 执行前，比较「反制归属」与「指令归属」；相等则跳过（不自伤），不等或未知则照常执行。本次新增概念。 | 拟落 `CounterCard.CreateCounterHandler` |
| 反制武装窗口 | 反制的有效期：己方回合激活（扣点）→ 覆盖对手的整个回合 → 己方下个回合开始时自动解除（不退点）。命中则提前结束时窗并消耗。本次新增概念。 | 拟落 `CounterCard`（`turn.start` 订阅 + Counter 段门禁） |
| 自动解除 | 己方回合开始时，对处于激活态的反制执行「取消激活」：注销 handler、清实扣额、不退点、不消耗、卡留手牌；发 `counter.disarmed`。区别于玩家主动取消（退点、不发信号）。 | 拟落 `CounterCard` |
| `counter.disarmed` | 反制自动解除信号（`GameUpdates` 新条目）；载荷 `Card` / `Player` / `Reason:"turn-start"`。与 `counter.triggered`（武装）成对，供事件流、UI、回归断言使用。 | `src/Orc.Game/GameUpdates.cs`、`GameHooks.cs` |
| 待决请求 | 选靶挂起期间的协议形态（`PendingSelection`），由 `notify/target.request` 推送。 | `src/Orc.Mcp/Match/TargeterBridgeAdapter.cs` |
| 归属座位 | 待决请求所属的玩家座位（`PendingSelection.Seat`），由 `SelectorParameter.Owner.Index` 注入。 | `SelectorParameter.Owner`、`TargeterFlow.Step` |
| 攻击候选 | 引擎给出的合法攻击目标集合（敌支援线 → 前线 → 敌 HQ，逐项经 `combat.target.legal`）。 | `CommandManager.CollectAttackCandidates` |
| 状态快照 | 桥内的 `state`（turn / activeSide / handCount 等），来源是推送帧缓存，必要时用 `observe.view` 校正。 | `SeatStateTracker.Snapshot` |
| 反制武装 | 反制已激活（`CounterActivationData.IsActive`）的对外呈现；投影字段 `IsCounterArmed`。 | `UiProjectionProvider.Read` |
| 应答型动作 | 先进入 `targeting` 待决、再由 `target.submit` / `target.cancel` 落地的动作（攻击、移动、部分效果）。 | `MatchExecutor.SubmitAsync` |
| `pending.action` | 待决请求的动作类别标注：`attack` / `move` / `deploy` / `effect` / `mulligan` / `command`（后者＝指挥拖拽的移动+攻击候选并集，发布时无法确定单一类别）。本次新增字段。 | `SelectorPresentation`、`PendingSelection` |
| 基础设施错误 | 传输/授权/内部故障（`not-connected`、`unauthorized`、`internal`），区别于业务否定。 | `McpCodes`、`HeadlessContext.LogCall` |
| 手牌索引 | 手牌在 `hand[]` 数组中的序号（0 起），与战线槽位（`slotIndex`）不同域。本次拟改名为 `handIndex`。 | `BridgeServer.CompactCards` |

## 已确认的既有事实（来自事件流核对，非推断）

1. 本局 `call 14`（turn 4，座位 1）`play.counter` 激活反制 `5dec11f3`；`call 17`（turn 6，座位 1）`play.command` 打出指令 `0b134ab2`，该指令在 `events.jsonl` seq 14 被 `5dec11f3` 的 handler `Stop` —— **己方反制拦截己方指令**。
2. 同一局 AI 侧（座位 0）亦复现：`turn 9` 激活 `8579feeb`，`turn 15` 其指令 `acd79481` 被同一张反制拦截（seq 55）。
3. 反制只在**己方回合**可激活（`CounterCard.EvaluateUse`），而指令也只在己方回合可打出 —— 因此在当前实现下，「己方反制拦己方指令」不是边缘情况，而是必然路径。
4. `attackPreviews`（seq 14 的 turn 14 返回）只含 HQ 两条；同回合 `command.attack` 候选为「支援线 3 个火炮 + HQ」，两者不同源。
5. `PendingSelection.Seat` 在实战中恒为 `null`：`ReferenceSetParameter` 构造时未注入 `Owner`，导致 `TargeterFlow.Step` 写入的 `OwnerSeat` 为空，服务端因此把请求广播给所有会话。
