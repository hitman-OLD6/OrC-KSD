# Implementation — MCP 无头对战问题修复

S1–S10 全部定案（R1–R8 逐条落到文件/行号、改动顺序与可执行测试）。

## Scope

- Target:
  - 游戏层：`src/Orc.Game/Cards/CounterCard.cs`、`src/Orc.Game/GameUpdates.cs`、`src/Orc.Game/Targeting/*`（`Owner` 与 `Action` 注入）、选靶请求构造点。
  - 服务/桥层：`src/Orc.Server/Transport/ServerHost.cs`、`src/Orc.Server/Headless/HeadlessContext.cs`、`src/Orc.Mcp.Bridge/BridgeServer.cs`、`src/Orc.Mcp.Bridge/SeatStateTracker.cs`。
  - 投影层：`src/Orc.Mcp/McpService.Projection.cs`。
  - 文档：`docs/无头沙盒-测试环境/开发指南.md` §4。
  - 测试：`tests/Orc.Game.Tests/*`、`tests/Orc.Mcp.Tests/*`。
- Excludes:
  - `Orc.UI` 客户端渲染；卡牌数值平衡与 AI 策略；协议版本与 `schemaVersion` 兼容层。
  - `outputs/agent-match/mcp-run/` 既有产物重写（仅作回归基线）。

## Implementation Steps

### S1: 反制效果门禁（回合门禁 + 归属过滤）

- Status: Todo
- Target: `src/Orc.Game/Cards/CounterCard.cs` — `CreateCounterHandler`（行 204–233）
- Approach: 在现有包装器内、调用 `effect.Handler` 之前插入两道门，**任一门不放行即直接 `return`**（不调 handler、不碰 `ctx`、不消耗、不改 `IsActive`）：
  1. **回合门禁（先）**：`CardBase.TurnPlayerProvider`（`CardBase.cs:104`，internal、同程序集可读；由 `CardLibrary.cs:200` 从 `Match.cs:490–492` 的 `() => _turnManager?.CurrentPlayer` 注入）。取值包 `try/catch (InvalidOperationException)`——`TurnManager.CurrentPlayer`（`TurnManager.cs:52`）在回合未开始时抛，异常视为「不可达」→ 跳过门禁。可达且 `ReferenceEquals(current, Owner)` → 不放行。
  2. **归属过滤（后）**：`view.Source` 是 `Orc.Cards.Card`（`OrderFlow.cs:40`），裸 `Card` 无 `Owner`（`src/Orc/Cards/Card.cs:15`）；取 `(view.Source as CardBase)?.Owner`，与反制归属**都非 null 且相等** → 不放行。任一为 null（未知）→ 放行。
  - 现有 `_consumed` / `Owner is null` 早退（行 207–215）保持在最前，语义不变。
- Acceptance:
  - [ ] 己方回合激活反制后己方打出指令：`card.played` 发出、指令效果执行、`IsConsumed == false` 且 `IsActive == true`。
  - [ ] 对手回合打出指令：handler 执行、`Stop`/`Interrupt` 生效、反制被消耗弃置。
  - [ ] 回合上下文不可达（`TurnPlayerProvider` 返回 null 或抛 `InvalidOperationException`）时，回合门禁视为放行，归属过滤照常生效。
  - [ ] 归属未知的指令在对手回合照常被反制（既有裸卡路径语义不回归）。
- Rationale: R1.2 优先于 R1.4；`TurnPlayerProvider` 已存在，无需新增跨层注入。

### S2: 回合开始自动解除 + `counter.disarmed`

- Status: Todo
- Target: `src/Orc.Game/Cards/CounterCard.cs`（既有 `engine.Subscribe(HandleUpdateAsync)`，行 79 / 311–325）、`src/Orc.Game/GameUpdates.cs`（新增 `CounterDisarmed` 常量与 `EmitCounterDisarmed`）、`src/Orc.Game/GameHooks.cs:194–212`（常量转发 + `Signals` 全量清单）、`src/Orc.Game/Output/GameHooksJson.cs`（信号元数据条目）、`docs/卡牌DIY开发文档/reference/信号表.md`（作者可见信号表）。
- Approach: 在 `HandleUpdateAsync` 中加 `turn.start` 分支：`payload[Player]` 与 `Owner` 引用相同且 `IsActive` → 解除序列（注销 `_activeRegistrations` → `IsActive = false` → `_chargedCost = null`；**不** 调 `ChangePointsAsync`、**不** 弃置）→ 发 `counter.disarmed`。载荷：`Card`（反制卡）/ `Player`（归属）/ `Source`（反制卡自身，与 `counter.triggered` 的 `view.TriggerCard ?? card` 口径一致）/ `Reason`（本次固定 `"turn-start"`）。信号在解除之后发出（读者在信号时点即读到 `IsActive == false`）。时序：反制卡在首个 `turn.start` 前已装载并订阅，故自首回合起生效。对手回合内终止对局不补发（走既有销毁/终局清理）。
  - **对外信号登记**（与 `counter.triggered` 对称，卡牌作者可 `hooks: ["counter.disarmed"]`）：`GameHooks.CounterDisarmed` 常量转发 + `GameHooks.Signals` 增一条（34→35，稳定序＝定义序，紧随 `CounterTriggered`）+ `GameHooksJson.SignalMetadata` 条目 + DIY 信号表条目；不新增 `SignalEntry` 之外的机制。
- Acceptance:
  - [ ] 对手回合未命中 → 己方回合开始时该反制 `IsActive == false`、仍在手牌、点数与解除前一致（不退点）。
  - [ ] 同时刻事件流出现 `counter.disarmed`，`Card` 指向该反制、`Reason == "turn-start"`、`Source` 为该反制卡。
  - [ ] 解除后可重新激活（再扣一次点）。
  - [ ] 己方回合内主动取消仍按实扣额退点，且**不发** `counter.disarmed`。
- Rationale: 自动解除是窗口边界的保证机制，`counter.disarmed` 只是可观测信号。登记为对外信号的理由：与 `counter.triggered` 是同一生命周期两端，一方可订阅另一方不可会迫使作者轮询补洞；`GameHooksTests` 的全等断言本就是「新增对外信号必须同步登记」的守卫，按其规则同步即遵守守卫。

### S3: 选靶归属座位 + 动作类别（R2 + R8 的 `pending.action`；合并实施、分两条验收）

- Status: Todo
- Target:
  - `src/Orc.Game/Targeting/Selector.cs` — `SelectorParameter` 新增 `Action { get; init; }`（`string?`）
  - `src/Orc.Game/Targeting/SelectorPresentation.cs` — 新增 `Action { get; internal set; }`
  - `src/Orc.Game/Targeting/TargeterSession.cs:121` — `TargeterFlow.Step` 与 `OwnerSeat` 同点写 `instance.Presentation.Action = parameter.Action`
  - `src/Orc.Mcp/Match/TargeterBridgeAdapter.cs:414–443` — `BuildPending` 读 `Presentation.Action` 写入 `PendingSelection.Action`（新增记录参数；缺省 `"unknown"`）；`src/Orc.Mcp/McpService.cs:213–250` `PendingJson` 透出
  - 构造点（`Owner` + `Action` 一次补齐）：`CommandManager.cs:662`（共用）、`CommandManager.cs:1050`、`PlayManager.cs:166`、`MulliganManager.cs:171`、`EffectRuntime.cs:824`、`PincerSystem.cs:157`、`ProgramKeywordComponent.cs:381`/`395`
- Approach:
  - `Owner`：按 `PlayManager.cs:166–170` / `EffectRuntime.cs:824–828` 既有写法补 `Owner = <发起选靶的玩家>`——攻击/移动＝`unit.Owner`；`PincerSystem`＝配对单位归属；`ProgramKeywordComponent`＝该组件 `Owner`（`ProgramKeywordComponent.cs:171`）。
  - `Action`：常量集中到 `SelectorActions` 静态类（`Attack`/`Move`/`Deploy`/`Effect`/`Mulligan`/`Command`/`Unknown`，与 `SelectorNames` 同址），全代码库不写裸字符串；缺失＝`Unknown`，不伪造类别。
  - `CommandManager` 两处：`RunSelectorAsync` 增参 `string action`——签名 `private async Task<(SelectorOutcomeKind Kind, Ref<Entity>? Selected, TargeterResult? Result)> RunSelectorAsync(UnitCard unit, IReadOnlyList<Ref<Entity>> candidates, string action, CancellationToken ct)`；内部 `new ReferenceSetParameter(candidates, 1, 1, tag: unit) { Owner = unit.Owner, Action = action }`；`BeginMoveAsync`（行 579）传 `SelectorActions.Move`、`BeginAttackAsync`（行 634）传 `SelectorActions.Attack`。助手内**不**按候选 `kind` 推断（并集/异常候选会失真且引入第二套规则）。`HandleCommandFlowAsync`（行 1050）保持独立构造、传 `SelectorActions.Command`（唯一允许的复合类别，仅用于「移动 ∪ 攻击」并集）。
  - 其余构造点：`PlayManager.cs:166` → `Deploy`；`MulliganManager.cs:171` → `Mulligan`；`EffectRuntime.cs:824`、`PincerSystem.cs:157`、`ProgramKeywordComponent.cs:381`/`395` → `Effect`。
  - 嵌套口径：`play.unit` 部署过程中由卡效果再发起的选牌/选单位一律 `Effect`，`Deploy` 只指部署空隙选择本身。
  - 后续新增共用选靶助手沿用同一约定：类别由调用方传参，助手内不推断。
  - 无需新增 `MatchExecutor` 状态面（构造处每次给准确值，多步续接不失真）。
- Acceptance:
  - [ ] （R2）对手回合的选靶请求不写入本座位桥状态；对手回合结束后 `board.pendingSessionId == null`。
  - [ ] （R2）本座位自己的选靶请求仍能发布、应答、清理。
  - [ ] （R2）`target.submit` 座位不匹配时被拒绝（`ServerHost.cs:602–604` 既有校验）。
  - [ ] （R8）`pending.action` 可区分 `attack`/`move`/`deploy`/`effect`/`mulligan`；合并型「指挥拖拽」标 `command`；未知来源为 `unknown`。
- Rationale: 类别描述「这一步在选择什么」，只有构造方能确定；与 `Owner` 同批改动减少回归轮次。R2 链路其余环节已就绪（`TargeterSession.cs:121` 写、`TargeterBridgeAdapter.cs:430–442` 读、`ServerHost.cs:1182–1185` 按座位过滤）；`MulliganManager.cs:171` 因 `tag: player` 已被 `BuildPending` 的 `Parameter as Player` 兜底，攻击/移动等 5 处才是实测恒 `null` 的根因（`tag: unit` 是 `UnitCard` 而非 `Player`）。

### S4: `attackPreviews` / `moves` 与引擎候选同源（R3）

- Status: Todo
- Target: `src/Orc.Mcp/McpService.Projection.cs` — `ComputeActions`（行 139–196）、`AttackTargets`（行 245–264）
- Approach: 投影层不再自建目标规则，改为逐单位读引擎候选：
  - `attackPreviews`：对 `OwnUnits` 每个单位取 `match.CommandManager.GetCommandAvailability(unit)`（`CommandManager.cs:727–748`），`availability.Attack.CanUse` 时以 `availability.Attack.Candidates`（＝`CollectAttackCandidates` 经 `combat.target.legal` 过滤，行 782–844）为唯一目标来源；`(attackerUid, defenderUid)` 由 `Ref.Value is Entity ? Id.ToString("N") : References.Assign(...)` 取得。删除 `AttackTargets`（行 245–264）与「敌前线为空才给 HQ」的自建规则。
  - 伤害/dies：仍由投影按 `EffectiveAttack`/`EffectiveHealth` 计算（引擎只给候选，不给伤害）；HQ 条目沿用现有「不判死」口径。
  - `moves`：同法取 `availability.Move.Candidates`（＝`CommandMovementRules.Destinations` ⋂ 空槽，行 750–780），按引用同一性定位槽位索引；`toZone` 由该槽所属线给出——前线＝`"front"`，己方支援线（后撤候选，`CommandMovementRules.cs:17–18` 在 `IsRetreat` 时返回支援线）＝`"support"`；`slotIndex` 的语义按 `toZone` 解释（`"support"` 时指己方支援线槽位），文档同步写明。删除行 150–163 的「自枚举前线空槽」。
  - 引擎候选是 `Ref<Entity>`，预览/`moves` 仍用 uid 字符串（既有字段语义不变，只扩 `toZone` 取值域）。
  - 安全性：`GetCommandAvailability` 经 `_inputAllowed`＝`Match.CanAcceptInput`（`Match.cs:297–299`）→ `ActionInputJudicator`，其 `InteractionStateReader` 为 null（无头/MCP 对局未接前端）时恒 true（`ActionInputJudicator.cs:22–27`），故预览不会因输入门禁而清空。
- Acceptance:
  - [ ] 敌支援线单位可被攻击时出现在预览中。
  - [ ] 敌前线有单位时，预览的 HQ 条目与引擎一致（允许则含、不允许则不含）。
  - [ ] `moves` 与引擎移动候选同源（不自枚举前线空槽）。
- Rationale: `CONTEXT` 已把攻击候选锚定在 `CommandManager.CollectAttackCandidates`，且同一状态下的 `command.attack` 候选必须与预览逐一对应——复用 `GetCommandAvailability` 是唯一不产生第二套规则的做法。

### S5: 动作返回的 `state` 不落后（R4）

- Status: Todo
- Target: `src/Orc.Mcp.Bridge/BridgeServer.cs` — `HandleToolsCallAsync` 通用分支（行 287–309）、`RefreshViewAsync`（行 312–327）
- Approach: 动作类调用（复用 `SeatStateTracker.IsActionTool` 的前缀集合：`play.`/`command.`/`match.`/`mulligan.`/`target.`）在 `WriteToolResultAsync` 之前先 `RefreshViewAsync`，与 `seat.state`/`seat.board` 分支同一手法——`state` 因此取自权威视图而非旧推送帧，`turn`/`activeSide`/`yourTurn` 与 `details` 不再冲突。刷新失败不打断调用（现有 `RefreshViewAsync` 已隔离异常）。`state` 字段与既有字段语义保持不变（不删字段、不改来源）。
- Acceptance:
  - [ ] `match.end_turn` 返回 `state.turn == details.turn`。
  - [ ] 改变相位的动作返回后 `yourTurn` 正确。
- Rationale: 约束禁止删 `state` 或改其语义，故只能改「刷新时点」；`observe.view` 走服务端同一串行队列，读数即动作后状态。

### S6: `seat.board` 暴露反制武装状态（R5）

- Status: Todo
- Target: `src/Orc.Mcp.Bridge/BridgeServer.cs` — `CompactCards`（行 419–442）、`BuildBoardAsync`（行 333–417）
- Approach: `CompactCards` 增补 `isCounterArmed`（源＝`UiCardView.IsCounterArmed`，`Orc.UI/UiProjectionProvider.cs:181`，经 `view.selfHand[]` 透出）；`BuildBoardAsync` 顶层增 `handCount`（＝`view.selfHandCount`，含已武装）与 `armedCounterCount`（＝`view.selfCounterCount`，与 `observe.view` 同口径），两者分列、口径不改既有字段。
- Acceptance:
  - [ ] 激活后该卡 `isCounterArmed == true`；取消后为 `false`。
  - [ ] `armedCounterCount` 与 `observe.view` 的 `selfCounterCount` 一致。
- Rationale: 只增字段即可满足，不动 `CompactCards` 既有七字段语义。

### S7: 应答型动作在 `calls.jsonl` 可归因（R6）

- Status: Todo
- Target: `src/Orc.Server/Headless/HeadlessContext.cs` — `LogCall`（行 312–361）；`src/Orc.Server/Transport/ServerHost.cs:346/411`；`src/Orc.Mcp/Tools/TargetTools.cs` — `SubmitAsync`（行 74–88）
- Approach: 「应答记录自带归因」，不做段区间回填（`calls.jsonl` append + `AutoFlush`，回填只能延迟写或补写一行，破坏按 `id`/`at` 顺序读的既有消费者）：
  1. **发起记录**（`play.*` / `command.*` 等会进入 targeting 的调用）：从返回载荷 `details.pending.sessionId` 取 `pendingSessionId` 写入记录；无 pending 则省略该字段。
  2. **应答记录**（`target.submit` / `target.cancel`）：写 `answers`（被应答的 sessionId）、`action`（提交前捕获的 `Bridge.CurrentPending.Action`）、`picks`（原始序号数组）、`pickedRefs`（按序号映射出的候选 `ReferenceKey`；越界或 cancel 时省略）。段区间仍留在 submit 记录（它才是真实产段者）。
  3. **捕获时点**：在 `InvokeAsync` **之前**读 `Bridge.CurrentPending`（提交成功后 pending 即清空，`TargeterBridgeAdapter.cs:307–322`），在 `ServerHost.HandleToolsCallAsync` 的 work item 内完成；仅当 `pending.SessionId == 本次 sessionId` 时采纳，避免把陈旧 pending 认成本次应答。
  4. **`LogCall` 扩展**：加可空 `JsonObject? extra = null` 形参，把 `extra` 的键并入记录；后续新字段不再逐个加形参。
- Acceptance:
  - [ ] `target.submit` 记录含 `answers` / `action` / `picks` / `pickedRefs` 与段区间；发起记录含 `pendingSessionId`。
  - [ ] 给定一次攻击，可仅凭 `calls.jsonl` + `events.jsonl` 建立「发起 → 应答 → 段区间」的对应关系。
- Rationale: 双向关联比时间窗推断确定，且不改既有字段语义；`action` 复用 S3 的单一真源，不引入第二套标签。

### S8: `errorCount` 口径（R7）

- Status: Todo
- Target: `src/Orc.Server/Headless/HeadlessContext.cs` — `LogCall`（行 325–328）、`WriteSummaryAsync`（行 445–476）
- Approach: `LogCall` 增一个「是否基础设施错误」的判定（不改 `!ok` 的调用点）：`code` ∈ {`not-connected`（`BridgeServer.cs:230` 字面量）、`unauthorized`（`McpCodes.Unauthorized`）、`internal`（`McpCodes.Internal`）} → `errorCount++`；其余 `ok == false` → `failedCalls++`。`summary.json` 同时出现 `errorCount` 与 `failedCalls`，且**在字段注释与实现文档中标注「`errorCount` 口径变更」**（由「一切 `!ok`」收窄为「仅基础设施错误」）。门禁拒绝（`ServerHost.cs:346`，`code: Unauthorized`）仍计入 `errorCount`。
- Acceptance:
  - [ ] 一次 `target.claim` 的 `pending-targeting` 不增加 `errorCount`，只增加 `failedCalls`。
  - [ ] `not-connected` / `unauthorized` / `internal` 仍计入 `errorCount`。
- Rationale: 只增字段、不改既有字段名；白名单制避免「未知业务码」误报为基础设施故障。

### S9: 注释与剩余接口语义（R8 余项）

- Status: Todo
- Target:
  - `src/Orc.Mcp/McpService.Projection.cs:91–99` 注释（声称「不预测伤害与死亡」，实现行 182–192 已算四项）
  - `src/Orc.Mcp.Bridge/BridgeServer.cs` `CompactCards`（手牌 `slot` → `handIndex`；战线仍 `slot`）
  - `src/Orc.Game/Cards/CounterCard.cs:149–153`（已消耗/已销毁再点 `play.counter` 走 `ctx.Stop()`，对外仍 `ok`）
  - `docs/无头沙盒-测试环境/开发指南.md` §4（行 107–158）
- Approach:
  - 注释（`McpService.Projection.cs:91–99`）改为「含基础伤害与死亡判定（`damageToDefender`/`damageToAttacker`/`defenderDies`/`attackerDies`），不含词条与效果改写」；`AttackTargets` 的自建规则描述随 S4 删除。
  - `CompactCards` 增 `indexField` 参数：手牌传 `handIndex`、两条战线传 `slot`（`selfLine`/`enemyLine` 字段名不变）；`docs/无头沙盒-测试环境/开发指南.md` §4 的 `hand`/`selfLine`/`enemyLine` 示例、`moves[].toZone`、`attackPreviews` 语义、`armedCounterCount`/`handCount`、`pending.action` 一并同步。
  - **已消耗/已销毁反制**：新增 `PlayFailureReason.CounterUnavailable`（`PlayChain.cs:22–63`，注释写明「卡已消耗或已销毁，不可再激活/取消；不可重试」）；`CounterCard` 增公开只读 `public bool IsDestroyed => _destroyed;`（`IsConsumed` 已有）；`PlayManager.UseCounterAsync` 在 `CheckAdmission`（行 597）之后、`BeginAction` 之前加守卫 `if (card.IsConsumed || card.IsDestroyed) return PlayResult.Failure(PlayFailureReason.CounterUnavailable);`——零副作用（不扣点、不翻转、不发信号），且必须早于 `BeginAction`。
  - 抛点定位（决定守卫位置的事实）：`CounterEffectAssemblyTests.cs:489` 的异常并非来自 `CheckAdmission`（其不检查存活），而是来自 `CounterUseJudicator.cs:27` 的 `!subject.IsAlive` → `throw new ArgumentException("…须为存活反制卡引用（fail-fast）")`，位于 `BeginAction` 之后的验证段；入口守卫因此能先于该 fail-fast 拦截。
  - `CounterCard.HandleUseCounterAsync` 的 `_consumed || _destroyed` 分支保留 `ctx.Stop()` 作内部防御，注释改为「入口已拦截；此分支仅为触发器被直接驱动时的兜底」。
  - `CounterUnavailable` 只覆盖 `consumed`/`destroyed`，**不覆盖** `counter.disarmed`（自动解除后卡完好，可再次激活）；`CounterUseRejection` 三项映射不变（该值在入口直接返回，不进触发器验证）。
  - MCP 侧无需改：`EngineResultMapper` 把所有 `PlayResult.Failure` 透传为 `ok:false` + `code:"engine-rejected"` + `details.failureReason`（`EngineResultMapper.cs:98–101`）。开发指南/DIY 的失败原因表补 `counter-unavailable`（若表按枚举名生成则实施时确认无需改）。
- Acceptance:
  - [ ] 注释不再声称「不预测伤害与死亡」。
  - [ ] `seat.board.hand[]` 序号字段名为 `handIndex`；`selfLine`/`enemyLine` 仍 `slot`；开发指南 §4 示例同步。
  - [ ] 已消耗反制 `play.counter` 返回 `Failed` + `CounterUnavailable`，不得静默成功。
  - [ ] 已销毁反制 `play.counter` 同样返回 `Failed` + `CounterUnavailable`（不再抛异常）。
- Rationale: `CounterRejected` 的注释语义是「未提供可辨识类别时的降级缺省」，让一个已知且可命名的条件复用它会使降级语义失效；agent 也需要区分「不可重试的缺席」与「可换条件重试的拒绝」（点数不足/非己方回合）。

### S10: 回归与验收（跨步骤）

- Status: Todo
- Target: `tests/Orc.Game.Tests/`、`tests/Orc.Mcp.Tests/`
- Approach:
  - **用例随规则修正 #1**：`CounterEffectAssemblyTests.Counter_CardEffect_SerialEvents_SharedContext`（行 142–180）——`OrderFlow.ExecuteAsync` 前加 `await match.EndTurn()` 让对手成为行动方，保留原断言（串行、共享 ctx、`Stop` 后余下不执行、命中即消耗）。**不计为行为回归**：用例意图与回合归属无关，原写法恰好构造了被 R1.2 禁止的场景。
  - **用例随规则修正 #2**：`MatchOutcomeTests.Counter_Entrances_Are_Rejected_After_End`（行 180–219）——把反制激活移到两次 `EndTurn` 之后、HQ 终局打击之前（回合 3 A 有 2 点，反制费 1），保持「终局后激活/取消两入口都被拒」的原意。**不计为行为回归**：原断言跨越了 R1.3 明确取消的回合循环。
  - 其余反制用例已核对不触发新规则（`P4OrderFlowTests` 全部、`PlayChainCounterTests`、`CommandLockInTests`、`DeployCostModifierTests` 两条、`JudicatorValidationTests`、`TriggerSourceSignalTests`），**但以全量 `Orc.Game.Tests` 实跑为准**，清单不作替代。
  - **用例随规则修正 #3（同步登记与契约基线，非口径放宽）**：
    - `GameHooksTests.cs:181–202`：`Assert.Equal(34, signals.Count)` → `35`，`.WithMessage`/注释补「原 31 + card.dying + card.leaving + keyword.attack.finished + counter.disarmed」；`GameHooks.Signals` 同步新增 `CounterDisarmed`（与 `GameUpdates` 常量集合保持全等）。**不** 引入「内部信号」例外集合。
    - `ContractFreezeTests`（`tests/Orc.Game.Tests/ContractFreezeTests.cs`）：本批引入公共面变更（`GameUpdates.CounterDisarmed`、`SelectorParameter.Action`、`SelectorPresentation.Action`、`PendingSelection.Action`、`PlayFailureReason.CounterUnavailable`、`CounterCard.IsDestroyed`），基线 `tests/Orc.Game.Tests/Baselines/contract-freeze.json` 必须用 `ORC_WRITE_BASELINE=1` 运行 `BaselineGeneratorTests` **重生成**（`ContractFreezeTests` 自身永不重写基线）。预期 diff 面：`gameUpdates` 段新增一条 `CounterDisarmed`，其余段落预期不变（`resultShapes` 只快照属性与构造函数，`ContractSnapshot.cs:164–178`，枚举成员不入基线）；重生成后须核对实际 diff，若 `gameEntryPoints` 等处出现额外条目，需在变更说明里逐条列出。全量再跑一次，确认 diff 只落在预期字段，并在变更说明里列为「受控公共面变更」。
  - **用例随规则修正 #4**：`CounterEffectAssemblyTests.cs:485–492` 的「销毁后再使用」断言，由「抛异常」改为 `PlayResultStatus.Failed` + `PlayFailureReason.CounterUnavailable`。理由：`PlayManager` 既有契约是「无效驱动（未加载归属等装配性错误）＝异常；玩家动作级失败＝结果对象（不抛）」（`PlayManager.cs:29`），原断言与契约相悖——且实际抛点 `CounterUseJudicator.cs:27` 是「载荷须为存活卡引用」的 fail-fast，本不该由玩家动作（点一张已没的卡）触发。
  - 新增断言按各步骤 Acceptance 逐条落地。
- Acceptance:
  - [ ] 全量 `Orc.Game.Tests` 通过（含上述四条修正；契约基线已按受控流程重生成并对过 diff）。
  - [ ] `Orc.Mcp.Tests` 通过，并为 R3–R7 各补一条同 seed 重放或夹具断言。
- Rationale: 逐步骤落地时细化断言；清单与既有用例核对只作定位线索，最终判据是实跑结果。

### S11: 端到端验证发现的两个缺陷（真机对局补齐，不在原 S1–S10 内）

- Status: Done
- 发现方式：以 `--headless --mode play --seat0 ai --seat1 agent` 起真机实例，用 `scripts/verify-counter-window.py` 经 MCP 桥打完整局。
- 缺陷 1（对局死锁，R2 的副作用）：`McpService.AiGateway.SubmitAsync` 的归属判定方向写反——`ownerSeat != AgentSeat` 即拒绝。
  `AgentSeat` 是 **agent 占用的座位**、AI 驱动其余座位；R2 给 `PendingSelection.Seat` 填上真实值后，AI 自己发起的选靶（Seat＝AI 座位）也满足「≠ AgentSeat」被拒 ⇒ AI 卡在选靶上不再动作，对局停摆。
  修法：判据改为 `ownerSeat == AgentSeat` 才拒绝（属于 agent 座位，AI 不得代答）；并补回归 `AiGateway_AnswersRequestsOwnedByAiSeat`。
- 缺陷 2（重连死锁）：桥在待决期间断开后，`room.resume` 被 `McpService.InvokeAsync` 的「挂起期门禁」拒成 `pending-targeting`；而应答待决又必须先绑定座位 ⇒ 谁也进不来（实测：重连日志 `[bridge] room.resume 被拒：pending-targeting`）。
  修法：忙门禁放行 `room.resume`（绑定座位不改对局状态，`BindSeat` 之后会把该座位的待决请求重推一次）；并补回归 `RoomResume_Is_Not_Blocked_While_Targeting`。
- 真机结果（`outputs/verify-counter-fix/`，seed 1006108646，19 回合）：
  - `counter.triggered` 23、`counter.disarmed` 17、`card.played` 17、`card.discarded` 3；
  - 两次「同回合武装反制 + 打指令」后，反制仍在手且仍武装（自伤消失）；
  - 每个己方回合开始 `isCounterArmed` 转 false、点数未回升（不退点）；
  - 对手回合 `pendingSessionId` 恒 null；
  - `calls.jsonl`：`command.move` 带 `pendingSessionId`，对应 `target.submit` 带 `answers` / `action=move` / `picks=[1]` / `pickedRefs=[…]` / 段区间；
  - `summary.json`：`errorCount=0`、`failedCalls=0`。
