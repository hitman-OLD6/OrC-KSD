# 代码基线 · 事实源锚点表

> **内部资产**：供正文写作取证用，不进入 `README.md` 的全量索引（见 `implementation.md` S8）。
> 定位只用「符号名 + 文件」，行号仅作提示；代码演进后以 `grep -n "<符号名>" src` 复核，定位失配即按 S20「符号存在性检查」处理。
> 基线：2026-10-19（Part A R9 已落地）。核对口径：`dotnet build OrcEngine.sln`（0 error；`BuiltInPlugin.cs:143,149` 两个既有 `CS8604`）＋ `dotnet test OrcEngine.sln`（1635 passed / 0 failed）。
> 层归属：**内核**＝`src/Orc`；**游戏层**＝`src/Orc.Game`。

## 0. 取证口径说明

- 一个事实点只登记**一个**权威符号；同一机制出现两个符号名时，先登记为术语冲突（交 `CONTEXT.md` 裁决），不得并列入表。
- 「载荷键」以 `GameUpdates.Payload*` 常量为准，不写裸字符串。
- `Source` 语义（R9）：信号载荷键 `Source`＝**事件引发者**（可空、恒写）；视图属性 `TriggerCard`＝**当前 handler 的触发者**（帧值、链上最后者胜）。两者不可互换。

## 1. 信号（`GameUpdates` 34 ＋ 内核 `Updates` 4；另含 1 条载荷键锚点）

> 口径校正：文档 S8 记「31 条信号」＝`GameHooksJson.SignalMetadata` 的导出条数；`GameUpdates` 实际有 **34** 个信号常量，`GameHooks.Signals` 也是 34（多出 `card.dying` / `card.leaving` / `keyword.attack.finished`——同族附加信号、未入 31 条导出表）。本表按**代码全量**登记。

| 机制面 | 事实点 | 符号名 | 定位 |
| --- | --- | --- | --- |
| 信号·回合 | 回合开始前；载荷 {Player,TurnNumber,Source} | `GameUpdates.TurnStartBefore` | src/Orc.Game/GameUpdates.cs:37 |
| 信号·回合 | 回合开始；载荷同上 | `GameUpdates.TurnStart` | src/Orc.Game/GameUpdates.cs:40 |
| 信号·回合 | 回合开始后；载荷同上 | `GameUpdates.TurnStartAfter` | src/Orc.Game/GameUpdates.cs:43 |
| 信号·回合 | 回合结束前；载荷同上 | `GameUpdates.TurnEndBefore` | src/Orc.Game/GameUpdates.cs:46 |
| 信号·回合 | 回合结束；载荷同上 | `GameUpdates.TurnEnd` | src/Orc.Game/GameUpdates.cs:49 |
| 信号·卡 | 打出；载荷 {Card,Player,Source} | `GameUpdates.CardPlayed` | src/Orc.Game/GameUpdates.cs:58 |
| 信号·卡 | 抽牌；载荷 {Player,Card,Source} | `GameUpdates.CardDrawn` | src/Orc.Game/GameUpdates.cs:61 |
| 信号·卡 | 数值变更；载荷 {Card,ChangedFields,Source} | `GameUpdates.CardStatChanged` | src/Orc.Game/GameUpdates.cs:68 |
| 信号·卡 | 加载；载荷 {Player,Card,Source} | `GameUpdates.CardLoad` | src/Orc.Game/GameUpdates.cs:73 |
| 信号·卡 | 进手牌；载荷 {Player,Card,Source} | `GameUpdates.CardHandAdd` | src/Orc.Game/GameUpdates.cs:80 |
| 信号·卡 | 弃置；载荷 {Card,Player,Source} | `GameUpdates.CardDiscarded` | src/Orc.Game/GameUpdates.cs:89 |
| 信号·卡 | 爆牌；载荷 {Card,Player,Source} | `GameUpdates.CardBurned` | src/Orc.Game/GameUpdates.cs:97 |
| 信号·卡 | 游戏层死亡；载荷 {Card,Killer,Source} | `GameUpdates.CardDied` | src/Orc.Game/GameUpdates.cs:104 |
| 信号·卡 | 死亡确认（卸载前）；载荷 {Card,Killer,Source} | `GameUpdates.CardDying` | src/Orc.Game/GameUpdates.cs:107 |
| 信号·卡 | 即将离场；载荷 {Card,Reason,Source} | `GameUpdates.CardLeaving` | src/Orc.Game/GameUpdates.cs:110 |
| 信号·词条 | 临时增益到期；载荷 {Card,Source} | `GameUpdates.KeywordAttackFinished` | src/Orc.Game/GameUpdates.cs:113 |
| 信号·单位 | 加入；载荷 {Unit,Position,Source} | `GameUpdates.UnitJoined` | src/Orc.Game/GameUpdates.cs:118 |
| 信号·单位 | 部署；载荷 {Unit,Position,Source} | `GameUpdates.UnitDeployed` | src/Orc.Game/GameUpdates.cs:121 |
| 信号·单位 | 位置变化；载荷 {Unit,OldPosition,NewPosition,Source} | `GameUpdates.UnitPositionChanged` | src/Orc.Game/GameUpdates.cs:124 |
| 信号·单位 | 升为老兵；载荷 {Unit,Source} | `GameUpdates.UnitUpgraded` | src/Orc.Game/GameUpdates.cs:135 |
| 信号·单位 | 被揭示；载荷 {Unit,Source} | `GameUpdates.UnitRevealed` | src/Orc.Game/GameUpdates.cs:146 |
| 信号·伤害 | 受伤；载荷 {Card,Amount,Source} | `GameUpdates.CardDamaged` | src/Orc.Game/GameUpdates.cs:151 |
| 信号·单位 | 行动后；载荷 {Unit,Source} | `GameUpdates.UnitActed` | src/Orc.Game/GameUpdates.cs:154 |
| 信号·伤害 | 造成伤害；载荷 {Unit,Card,Amount,Source} | `GameUpdates.UnitDamageDealt` | src/Orc.Game/GameUpdates.cs:162 |
| 信号·反制 | 反制触发；载荷 {Card,Player,Source} | `GameUpdates.CounterTriggered` | src/Orc.Game/GameUpdates.cs:169 |
| 信号·交战 | 交战存活；载荷 {Unit,Source} | `GameUpdates.UnitCombatSurvived` | src/Orc.Game/GameUpdates.cs:177 |
| 信号·卡组 | 洗切；载荷 {Player,Deck,Source} | `GameUpdates.DeckShuffled` | src/Orc.Game/GameUpdates.cs:187 |
| 信号·单位 | 类型变更；载荷 {Unit,AddedType,Source} | `GameUpdates.UnitTypesChanged` | src/Orc.Game/GameUpdates.cs:200 |
| 信号·资源 | 获得槽；载荷 {Player,Amount,Source} | `GameUpdates.SlotGained` | src/Orc.Game/GameUpdates.cs:209 |
| 信号·资源 | 失去槽；载荷 {Player,Amount,Source} | `GameUpdates.SlotLost` | src/Orc.Game/GameUpdates.cs:215 |
| 信号·资源 | 槽改变；载荷 {Player,OldSlots,NewSlots,Source} | `GameUpdates.SlotChanged` | src/Orc.Game/GameUpdates.cs:221 |
| 信号·资源 | 获得点数；载荷 {Player,Amount,Source} | `GameUpdates.PointGained` | src/Orc.Game/GameUpdates.cs:230 |
| 信号·资源 | 失去点数；载荷 {Player,Amount,Source} | `GameUpdates.PointLost` | src/Orc.Game/GameUpdates.cs:236 |
| 信号·资源 | 点数改变；载荷 {Player,OldPoints,NewPoints,Source} | `GameUpdates.PointChanged` | src/Orc.Game/GameUpdates.cs:243 |
| 信号·载荷键 | 引发者键（恒写、可空） | `GameUpdates.PayloadSource` | src/Orc.Game/GameUpdates.cs:306 |
| 信号·内核 | 放置驱动装载；载荷 {Card}（无 Source） | `Updates.CardPlaced` | src/Orc/Core/Updates.cs:15 |
| 信号·内核 | 引擎销毁；载荷 {Card}（无 Source） | `Updates.CardDestroyed` | src/Orc/Core/Updates.cs:18 |
| 信号·内核 | 卡牌数据变更；载荷 {Card}（无 Source） | `Updates.CardData` | src/Orc/Core/Updates.cs:21 |
| 信号·内核 | 效果移除；载荷 {Card,Effect}（无 Source） | `Updates.EffectRemoved` | src/Orc/Core/Updates.cs:28 |

## 2. 触发器与 band

| 机制面 | 事实点 | 符号名 | 定位 |
| --- | --- | --- | --- |
| 触发器 | 触发器本体（单视图类型、band 方案、事件表、包装器、挂载态） | `Trigger<TView>` | src/Orc/Core/Trigger.cs:29 |
| 触发器 | 构造入口（kind/bandType/events/hooks/priority/owner/stableKey/registrant） | `Trigger<TView>(...)` | src/Orc/Core/Trigger.cs:59 |
| 触发器 | 触发器声明级注册者（R9 回退链第二环） | `Trigger<TView>.Registrant` | src/Orc/Core/Trigger.cs:179 |
| 触发器 | 注册（默认区段；尾随 registrant） | `Trigger<TView>.Register` | src/Orc/Core/Trigger.cs:197 |
| 触发器 | 注册（具名 band；尾随 registrant） | `Trigger<TView>.Register(..., Enum band, ...)` | src/Orc/Core/Trigger.cs:209 |
| 触发器 | 保留槽注册（恒在普通事件之后） | `Trigger<TView>.RegisterDisposal` | src/Orc/Core/Trigger.cs:226 |
| 触发器 | 执行入口（入口初值 TriggerCard 解析点） | `Trigger<TView>.InvokeAsync` | src/Orc/Core/Trigger.cs:421 |
| 触发器 | 事件循环（逐事件改写 TriggerCard ＋ 写数据面） | `Trigger<TView>.RunEventsAsync` | src/Orc/Core/Trigger.cs:562 |
| 触发器 | 审计面事件快照（执行序） | `Trigger<TView>.Events` | src/Orc/Core/Trigger.cs:764 |
| 触发器 | 排序键＝band×1000＋优先级 | `EventEntry.SortKey` | src/Orc/Core/Trigger.cs:950 |
| 触发器 | 注册句柄（可撤销/可寻址） | `TriggerRegistration` | src/Orc/Core/Trigger.cs:1131 |
| 触发器 | 包装器注册句柄 | `TriggerWrapperRegistration` | src/Orc/Core/Trigger.cs:1159 |
| 触发器 | 审计条目（注册者＋派生 source 卡） | `TriggerEventInfo` | src/Orc/Core/Trigger.cs:1189 |
| 触发器 | 审计：注册者（Card/Effect/null） | `TriggerEventInfo.Registrant` | src/Orc/Core/Trigger.cs:1264 |
| 触发器 | 审计：触发者卡牌（读取期派生、宿主销毁降级 null） | `TriggerEventInfo.SourceCard` | src/Orc/Core/Trigger.cs:1270 |
| 触发器 | 执行帧触发者（R9 帧值） | `ExecutionFrame.TriggerCard` | src/Orc/Core/Trigger.cs:1319 |
| 触发器 | 非泛型元数据面（审计链取节点） | `ITriggerMetadata` | src/Orc/Core/TriggerMetadata.cs:8 |
| 触发器 | 初始事件描述（构造期装配通道） | `TriggerEvent<TView>` | src/Orc/Core/TriggerEvent.cs:9 |
| band | 内置默认 band 方案（唯一区段 0） | `DefaultBands` | src/Orc/Core/Band.cs:7 |
| 总线 | 挂载（被动触发器接更新） | `Bus.Mount<TView>` | src/Orc/Core/Bus.cs:31 |
| 总线 | 按所有者卸载（效果销毁入口） | `Bus.UnmountOwner` | src/Orc/Core/Bus.cs:88 |
| 总线 | 订阅者名读面（诊断/断言） | `Bus.GetSubscribers` | src/Orc/Core/Bus.cs:189 |
| 引擎 | 发射更新（信号唯一出口） | `LogicEngine.Emit` | src/Orc/Core/LogicEngine.cs:107 |
| 引擎 | 即时更新订阅（旁路观察、无 TriggerCard） | `LogicEngine.Subscribe` | src/Orc/Core/LogicEngine.cs:191 |
| 事件流 | 事件树/日志载体 | `EventStream` | src/Orc/Core/EventStream.cs:26 |

## 3. handler 与装载链

| 机制面 | 事实点 | 符号名 | 定位 |
| --- | --- | --- | --- |
| 效果 | 效果基类（disposing 触发器默认必选） | `Effect` | src/Orc/Cards/Effect.cs:51 |
| 效果 | 被动效果（装载语境） | `PassiveEffect` | src/Orc/Cards/Effect.cs:640 |
| 效果 | 主动效果（施放语境、不装载） | `ActiveEffect<TView>` | src/Orc/Cards/Effect.cs:656 |
| 装载链 | 装载钩子（注入登记处） | `Effect.OnMount` | src/Orc/Cards/Effect.cs:147 |
| 装载链 | 卸载钩子（专属清理） | `Effect.OnUnmount` | src/Orc/Cards/Effect.cs:155 |
| 注入 | 装载语境注入（自动以效果为 registrant） | `Effect.Inject<TView>` | src/Orc/Cards/Effect.cs:166 |
| 注入 | 框架侧注入（数据体 injects；自动以效果为 registrant） | `Effect.InjectByFramework` | src/Orc/Cards/Effect.cs:228 |
| 装载链 | 挂 disposing → 挂主触发器 → OnMount | `Effect.ExecuteMount` | src/Orc/Cards/Effect.cs:304 |
| 装载链 | 销毁唯一入口（停用→disposing→teardown→移除→发射） | `Effect.ExecuteDispose` | src/Orc/Cards/Effect.cs:344 |
| 注入 | 宿主注入数据面（视图 Host 读取） | `Effect.WrapWithHostCore` | src/Orc/Cards/Effect.cs:206 |
| 装载链 | 添加效果（Add 即装载） | `Card.AddEffect` | src/Orc/Cards/Card.cs:109 |
| 装载链 | 移除效果（规范销毁入口） | `Card.RemoveEffect` | src/Orc/Cards/Card.cs:149 |
| 装载链 | 加载时点批量装载（幂等） | `Card.MountPassiveEffects` | src/Orc/Cards/Card.cs:188 |
| 装载链 | 统一装载原语（三入口共用） | `CardLoadout.MountEffect` | src/Orc/Cards/CardLifecycle.cs:121 |
| 装载链 | 统一销毁原语 | `CardLoadout.DisposeEffect` | src/Orc/Cards/CardLifecycle.cs:180 |
| 装载链 | 装载留痕（失败隔离） | `CardsLog.Write` | src/Orc/Cards/CardLifecycle.cs:96 |
| 动态效果 | 快照实例化入口 | `DynamicEffectFactory.Instantiate` | src/Orc/Cards/DynamicEffect.cs:547 |
| 动态效果 | 按预制体 id 实例化 | `DynamicEffectFactory.InstantiateRegistered` | src/Orc/Cards/DynamicEffect.cs:560 |
| 动态效果 | 反射构造触发器（必须补第 9 个实参 registrant） | `TriggerReflection.Create` | src/Orc/Cards/DynamicEffect.cs:276 |
| 动态效果 | 反射注册事件（可选 registrant 透传） | `TriggerReflection.Register` | src/Orc/Cards/DynamicEffect.cs:294 |
| 视图 | 卡事件通用视图（含 Source/TriggerCard） | `CardEventView` | src/Orc/Cards/CardLifecycle.cs:10 |
| 视图 | 卡牌链视图（含 Source/TriggerCard） | `CardTriggerView` | src/Orc.Game/Triggers/CardTriggerView.cs:16 |

## 4. 判定器（23 名 ＝ 22 内置 ＋ 1 示范）

> 口径校正：文档 S8 记「21 名」；`JudicatorNames` 实有 23 个常量，`GameHooks.BuiltInJudicatorNames` 22 条、`ExternalJudicatorNames` 1 条（`deck.top.tag`）。下表按代码全量登记。

| 机制面 | 事实点 | 符号名 | 定位 |
| --- | --- | --- | --- |
| 判定器 | 名称常量表（唯一字面值来源） | `JudicatorNames` | src/Orc.Game/Judicators/JudicatorNames.cs:11 |
| 判定器·准入 | `action.input.allowed` | `ActionInputJudicator` | src/Orc.Game/Judicators/ActionInputJudicator.cs:15 |
| 判定器·准入 | `action.turn.owner` | `ActionTurnOwnerJudicator` | src/Orc.Game/Judicators/ActionTurnOwnerJudicator.cs:11 |
| 判定器·验证 | `validation.cost.check` | `CostCheckJudicator` | src/Orc.Game/Judicators/CostCheckJudicator.cs:13 |
| 判定器·验证 | `validation.counter.use` | `CounterUseJudicator` | src/Orc.Game/Judicators/CounterUseJudicator.cs:16 |
| 判定器·验证 | `validation.move.recheck` | `MoveRevalidationJudicator` | src/Orc.Game/Judicators/MoveRevalidationJudicator.cs:22 |
| 判定器·验证 | `validation.attack.recheck` | `AttackRevalidationJudicator` | src/Orc.Game/Judicators/AttackRevalidationJudicator.cs:20 |
| 判定器·示范 | `deck.top.tag`（外部装配） | `DeckTopTagJudicator` | src/Orc.Game/Judicators/DeckTopTagJudicator.cs:20 |
| 判定器·选靶 | `targeting.candidate.eligibility` | `TargetEligibilityJudicator` | src/Orc.Game/Judicators/TargetEligibilityJudicator.cs:19 |
| 判定器·交战 | `combat.target.legal` | `CombatTargetLegalJudicator` | src/Orc.Game/Judicators/CombatTargetLegalJudicator.cs:20 |
| 判定器·交战 | `combat.range` | `CombatRangeJudicator` | src/Orc.Game/Judicators/CombatRangeJudicator.cs:17 |
| 判定器·交战 | `combat.guard.eligibility` | `CombatGuardEligibilityJudicator` | src/Orc.Game/Judicators/CombatGuardEligibilityJudicator.cs:12 |
| 判定器·交战 | `combat.interception` | `CombatInterceptionJudicator` | src/Orc.Game/Judicators/CombatInterceptionJudicator.cs:17 |
| 判定器·交战 | `combat.counter.eligibility` | `CombatCounterEligibilityJudicator` | src/Orc.Game/Judicators/CombatCounterEligibilityJudicator.cs:20 |
| 判定器·交战 | `combat.ambush.condition` | `CombatAmbushConditionJudicator` | src/Orc.Game/Judicators/CombatAmbushConditionJudicator.cs:14 |
| 判定器·移动 | `move.leg.eligibility` | `LegEligibilityJudicator` | src/Orc.Game/Judicators/LegEligibilityJudicator.cs:51 |
| 判定器·攻击 | `attack.leg.eligibility`（与 move leg 同一类承载两名） | `LegEligibilityJudicator` | src/Orc.Game/Judicators/LegEligibilityJudicator.cs:51 |
| 判定器·移动 | `move.frontline-enemy` | `MoveFrontlineEnemyJudicator` | src/Orc.Game/Judicators/MoveFrontlineEnemyJudicator.cs:16 |
| 判定器·效果 | `effect.target.resolve` | `EffectTargetResolveJudicator` | src/Orc.Game/Judicators/EffectTargetResolveJudicator.cs:15 |
| 判定器·资源 | `resource.slot.increment` | `PointSlotIncrementJudicator` | src/Orc.Game/Judicators/PointSlotIncrementJudicator.cs:12 |
| 判定器·资源 | `resource.slot.gain` | `PointSlotGainAmountJudicator` | src/Orc.Game/Judicators/PointSlotGainAmountJudicator.cs:12 |
| 判定器·资源 | `resource.slot.lose` | `PointSlotLoseAmountJudicator` | src/Orc.Game/Judicators/PointSlotLoseAmountJudicator.cs:12 |
| 判定器·资源 | `resource.point.gain` | `PointGainAmountJudicator` | src/Orc.Game/Judicators/PointGainAmountJudicator.cs:12 |
| 判定器·资源 | `resource.point.lose` | `PointLoseAmountJudicator` | src/Orc.Game/Judicators/PointLoseAmountJudicator.cs:12 |
| 判定器 | 注册表（按名解析/包装器改写） | `JudicatorRegistry` | src/Orc/Core/JudicatorRegistry.cs:16 |

## 5. targeter 与选择器

| 机制面 | 事实点 | 符号名 | 定位 |
| --- | --- | --- | --- |
| 选择器 | 选择器实例（槽位声明载体） | `SelectorInstance<TResult>` | src/Orc.Game/Targeting/Selector.cs:215 |
| 选择器 | 候选参数（引用集＋数量＋域校验＋tag） | `ReferenceSetParameter` | src/Orc.Game/Targeting/Selector.cs:18 |
| 选择器 | 框架模板（手牌起始/战场单位等） | `SelectorTemplates` | src/Orc.Game/Targeting/Selectors.cs:75 |
| 选择器 | 无头选靶描述（sel/side/zone/type/keyword/count） | `EffectSelector` | src/Orc.Game/Effects/EffectRuntime.cs:20 |
| targeter | 玩家侧交互管理器（一次请求＝一次 Step 流程） | `TargeterManager` | src/Orc.Game/Targeting/TargeterManager.cs:13 |
| targeter | 前端桥接契约 | `ITargeterBridge` | src/Orc.Game/Targeting/ITargeterBridge.cs:9 |
| targeter | 流程契约（Step/Retry/取消） | `ITargeterFlow` | src/Orc.Game/Targeting/ITargeterFlow.cs:8 |
| targeter | 三态结果（Ok/Cancelled/Failed） | `TargeterResult` | src/Orc.Game/Targeting/TargeterResult.cs:45 |
| targeter | 单步结果（值/失败原因） | `SelectorResult<TResult>` | src/Orc.Game/Targeting/SelectorResult.cs:33 |

## 6. wrapper（包装器）与动作缝

| 机制面 | 事实点 | 符号名 | 定位 |
| --- | --- | --- | --- |
| wrapper | before 包装器（可具名缝；LIFO） | `Trigger<TView>.RegisterBeforeWrapper` | src/Orc/Core/Trigger.cs:277 |
| wrapper | after 包装器（栈式回程） | `Trigger<TView>.RegisterAfterWrapper` | src/Orc/Core/Trigger.cs:290 |
| wrapper | replace 包装器（纯替换 core） | `Trigger<TView>.RegisterReplaceWrapper` | src/Orc/Core/Trigger.cs:295 |
| wrapper | 包装器工厂（按视图类型适配） | `Wrapper` | src/Orc/Core/WrapperFactory.cs:54 |
| 动作缝 | handler 内调用具名缝 | `Context.SeamAsync` | src/Orc/Core/Context.cs:80 |
| wrapper | 效果注入项读面（目标/事件/band/句柄） | `EffectInjection` | src/Orc/Cards/Effect.cs:716 |

## 7. 动作面 `EffectRuntime`

| 机制面 | 事实点 | 符号名 | 定位 |
| --- | --- | --- | --- |
| 动作面 | 卡 → 运行时解析（未装配＝null） | `EffectRuntime.ResolveFor` | src/Orc.Game/Effects/EffectRuntime.cs:156 |
| 动作面 | 主动效果施放入口 | `ActiveEffect<TView>.CastAsync` | src/Orc/Cards/Effect.cs:699 |
| 动作面·消灭 | 死亡链（source 作 Killer；**形参在 ct 前**） | `EffectRuntime.KillAsync` | src/Orc.Game/Effects/EffectRuntime.cs:170 |
| 动作面·伤害 | 单位/HQ 伤害（source 归属；**形参在 ct 前**） | `EffectRuntime.DamageAsync` | src/Orc.Game/Effects/EffectRuntime.cs:185 |
| 动作面·修饰 | 攻防增减（duration→到期相位） | `EffectRuntime.BuffAsync` | src/Orc.Game/Effects/EffectRuntime.cs:212 |
| 动作面·词条 | 授予（无参值） | `EffectRuntime.GrantAsync` | src/Orc.Game/Effects/EffectRuntime.cs:257 |
| 动作面·词条 | 授予（带参值） | `EffectRuntime.GrantAsync(..., int? value, ...)` | src/Orc.Game/Effects/EffectRuntime.cs:272 |
| 动作面·词条 | 撤销（完整卸载） | `EffectRuntime.RevokeAsync` | src/Orc.Game/Effects/EffectRuntime.cs:287 |
| 动作面·词条 | 参值改写 | `EffectRuntime.SetKeywordValueAsync` | src/Orc.Game/Effects/EffectRuntime.cs:302 |
| 动作面·词条 | 内容型词条授予 | `EffectRuntime.GrantWithContentAsync` | src/Orc.Game/Effects/EffectRuntime.cs:322 |
| 动作面·抽牌 | 抽牌（含满手爆牌裁决） | `EffectRuntime.DrawAsync` | src/Orc.Game/Effects/EffectRuntime.cs:333 |
| 动作面·卡组 | 定向取卡（结果三态） | `EffectRuntime.FetchFromDeckAsync` | src/Orc.Game/Effects/EffectRuntime.cs:365 |
| 动作面·修饰 | 行动费修饰（无 source 尾参） | `EffectRuntime.CostModAsync` | src/Orc.Game/Effects/EffectRuntime.cs:411 |
| 动作面·修饰 | 部署费修饰（无 source 尾参） | `EffectRuntime.DeployCostModAsync` | src/Orc.Game/Effects/EffectRuntime.cs:432 |
| 动作面·手牌 | 按卡名加入手牌 | `EffectRuntime.AddToHandAsync` | src/Orc.Game/Effects/EffectRuntime.cs:464 |
| 动作面·卡组 | 按卡名洗入卡组 | `EffectRuntime.ShuffleInAsync` | src/Orc.Game/Effects/EffectRuntime.cs:488 |
| 动作面·状态 | 压制 | `EffectRuntime.PinAsync` | src/Orc.Game/Effects/EffectRuntime.cs:512 |
| 动作面·状态 | 抑制 | `EffectRuntime.SilenceAsync` | src/Orc.Game/Effects/EffectRuntime.cs:520 |
| 动作面·状态 | 升为老兵（三态） | `EffectRuntime.UpgradeAsync` | src/Orc.Game/Effects/EffectRuntime.cs:532 |
| 动作面·状态 | 揭示（二态） | `EffectRuntime.RevealAsync` | src/Orc.Game/Effects/EffectRuntime.cs:547 |
| 动作面·移动 | 无头移动（frontline/support） | `EffectRuntime.MoveAsync` | src/Orc.Game/Effects/EffectRuntime.cs:557 |
| 动作面·资源 | 获得指挥点槽 | `EffectRuntime.GainPointSlotsAsync` | src/Orc.Game/Effects/EffectRuntime.cs:574 |
| 动作面·资源 | 失去指挥点槽 | `EffectRuntime.LosePointSlotsAsync` | src/Orc.Game/Effects/EffectRuntime.cs:590 |
| 动作面·资源 | 获得点数 | `EffectRuntime.GainPointsAsync` | src/Orc.Game/Effects/EffectRuntime.cs:606 |
| 动作面·资源 | 失去点数 | `EffectRuntime.LosePointsAsync` | src/Orc.Game/Effects/EffectRuntime.cs:622 |
| 动作面·选靶 | 无头选靶（判定器求值） | `EffectRuntime.SelectAsync` | src/Orc.Game/Effects/EffectRuntime.cs:635 |
| 动作面·选靶 | 交互式选靶（targeter 请求） | `EffectRuntime.BeginTargetSelectionAsync` | src/Orc.Game/Effects/EffectRuntime.cs:659 |
| 动作面·光环 | 光环声明（受益谓词） | `EffectRuntime.DeclareAuraAsync` | src/Orc.Game/Effects/EffectRuntime.cs:752 |
| 动作面·枚举 | 修饰期限（Permanent/TurnEnd/NextOwnerTurnStart） | `EffectDuration` | src/Orc.Game/Effects/EffectRuntime.cs:60 |
| 动作面·选靶 | 目标阈值过滤 | `EffectThreshold` | src/Orc.Game/Effects/EffectRuntime.cs:36 |
| 动作面·光环 | 光环受益谓词描述 | `EffectAuraFilter` | src/Orc.Game/Effects/EffectRuntime.cs:48 |

## 8. 词条与词条程序

| 机制面 | 事实点 | 符号名 | 定位 |
| --- | --- | --- | --- |
| 词条 | 词条标识常量集（20 个） | `KeywordIds` | src/Orc.Game/Cards/KeywordSystem.cs:29 |
| 词条 | 全量读面（数量以本数组为准） | `KeywordIds.All` | src/Orc.Game/Cards/KeywordSystem.cs:125 |
| 词条程序 | 词条程序定义（skills/rules/events） | `KeywordProgram` | src/Orc.Game/Cards/KeywordProgram.cs:7 |
| 词条程序 | 规则点 11 条 | `KeywordProgram.RuleNames` | src/Orc.Game/Cards/KeywordProgram.cs:34 |
| 词条程序 | 事件 17 条 | `KeywordProgram.EventNames` | src/Orc.Game/Cards/KeywordProgram.cs:35 |
| 词条程序 | 主体 6 条（self/friendly/enemy/any/owner/opponent） | `KeywordProgram.Subjects` | src/Orc.Game/Cards/KeywordProgram.cs:36 |
| 词条程序 | 操作 33 条 | `KeywordProgram.Operations` | src/Orc.Game/Cards/KeywordProgram.cs:37 |
| 词条程序 | 目标 14 条 | `KeywordProgram.Targets` | src/Orc.Game/Cards/KeywordProgram.cs:38 |
| 词条程序 | 操作分发执行（33 个 case） | `ProgramKeywordComponent.Execute` | src/Orc.Game/Cards/ProgramKeywordComponent.cs:183 |
| 词条程序 | 词条程序运行时（匹配/临时态/攻击事件） | `KeywordProgramRuntime` | src/Orc.Game/Cards/ProgramKeywordComponent.cs:430 |
| 词条 | 词条规则静态门面（HasKeyword/RecordAttack/OnDeployChainFinalized） | `KeywordRules` | src/Orc.Game/Cards/KeywordSystem.cs:225 |
| 词条 | 词条管理面（授予/撤销/参值） | `KeywordManager` | src/Orc.Game/Cards/KeywordManager.cs:20 |

## 9. 卡数据与预制体

| 机制面 | 事实点 | 符号名 | 定位 |
| --- | --- | --- | --- |
| 卡数据 | `*.card.json` schema 版本（＝1） | `CardDataJson.SchemaVersion` | src/Orc.Game/Cards/Data/CardDataJson.cs:20 |
| 卡数据 | 反序列化（警告不抛） | `CardDataJson.Deserialize` | src/Orc.Game/Cards/Data/CardDataJson.cs:40 |
| 卡数据 | 目录加载（预检入口） | `CardDataLoader.LoadDirectory` | src/Orc.Game/Cards/Data/CardDataLoader.cs:38 |
| 卡数据 | 数据体（id/name/components） | `CardDataBody` | src/Orc.Game/Cards/Data/CardDataBody.cs:9 |
| 卡数据 | 分发预检（0 error 门禁） | `CardDistributionValidator.Validate` | src/Orc.Game/Cards/Data/CardDistributionValidator.cs:123 |
| 卡数据·组件 | `typeCategory`（类型/兵种） | `TypeCategoryDefinition` | src/Orc.Game/Cards/Data/Components/BuiltInCardComponents.cs:138 |
| 卡数据·组件 | `factionCost`（阵营＋花费） | `FactionCostDefinition` | src/Orc.Game/Cards/Data/Components/BuiltInCardComponents.cs:6 |
| 卡数据·组件 | `battleStats`（攻防/行动费） | `BattleStatsDefinition` | src/Orc.Game/Cards/Data/Components/BuiltInCardComponents.cs:48 |
| 卡数据·组件 | `tagData`（稀有度） | `TagDataDefinition` | src/Orc.Game/Cards/Data/Components/BuiltInCardComponents.cs:92 |
| 卡数据·组件 | `keywords`（词条声明行） | `KeywordsDefinition` | src/Orc.Game/Cards/Data/Components/BuiltInCardComponents.cs:285 |
| 卡数据·组件 | `effects`（内联/引用预制体） | `EffectsDefinition` | src/Orc.Game/Cards/Data/Components/BuiltInCardComponents.cs:538 |
| 预制体 | 快照 schema 版本（写出恒 3；读入接受 2/3） | `EffectSnapshot.SchemaVersion` | src/Orc/Cards/Prefabs.cs:503 |
| 预制体 | `*.prefab.json` 反序列化（结构化失败） | `PrefabJson.Deserialize` | src/Orc/Cards/PrefabJson.cs:71 |
| 预制体 | 试解析（不抛） | `PrefabJson.TryDeserialize` | src/Orc/Cards/PrefabJson.cs:102 |
| 预制体 | 预制体管理器（注册/按 id 取） | `PrefabManager` | src/Orc/Cards/PrefabManager.cs:14 |
| 预制体 | op 模板目录（csx 渲染源） | `OpTemplateCatalog` | src/Orc.Game/EffectParsing/Compilation/OpTemplateCatalog.cs:27 |
| 预制体 | 单 op 渲染 | `OpTemplateCatalog.Render` | src/Orc.Game/EffectParsing/Compilation/OpTemplateCatalog.cs:133 |

## 10. R9 来源两面（Part A 新面）

| 机制面 | 事实点 | 符号名 | 定位 |
| --- | --- | --- | --- |
| 来源 | 归一化唯一入口（Card/Ref/失效/错型/null） | `SourceNormalizer.ToCard` | src/Orc/Cards/SourceNormalizer.cs:21 |
| 来源 | 载荷键（与视图属性同名） | `TriggerPayloadKeys.TriggerCard` | src/Orc/Core/TriggerPayloadKeys.cs:11 |
| 来源 | 执行期触发者读面 | `Context.TriggerCard` | src/Orc/Core/Context.cs:71 |
| 来源 | 卡事件视图：事件引发者 | `CardEventView.Source` | src/Orc/Cards/CardLifecycle.cs:60 |
| 来源 | 卡事件视图：当前触发者 | `CardEventView.TriggerCard` | src/Orc/Cards/CardLifecycle.cs:68 |
| 来源 | 卡牌链视图：事件引发者 | `CardTriggerView.Source` | src/Orc.Game/Triggers/CardTriggerView.cs:62 |
| 来源 | 卡牌链视图：当前触发者 | `CardTriggerView.TriggerCard` | src/Orc.Game/Triggers/CardTriggerView.cs:69 |
| 来源 | 指挥/移动/攻击/伤害视图：触发者（`Card?`） | `CommandTriggerView.TriggerCard` 等 4 处 | src/Orc.Game/Commanding/CommandViews.cs:49 |
| 来源 | 数据键（字面值＝`TriggerPayloadKeys.TriggerCard`） | `CommandDataKeys.TriggerCard` | src/Orc.Game/Commanding/CommandTypes.cs:239 |
| 来源 | X1 入口（写入点＝唯一归一化语句，方法声明见 390） | `CommandManager.BeginCommandAsync` | src/Orc.Game/Commanding/CommandManager.cs:390 |
| 来源 | 弃置动作尾随 source | `PlayerManager.DiscardCardAsync` | src/Orc.Game/Managers/PlayerManager.cs:268 |
| 来源 | 弃置原语（透传到 `card.discarded`） | `HandLimitBurn.DestroyAndEmitDiscardedAsync` | src/Orc.Game/HandLimitBurn.cs:40 |
| 来源 | 信号导出器信号表（31 条 `SignalEntry`） | `GameHooksJson.SignalMetadata` | src/Orc.Game/Output/GameHooksJson.cs:100 |
| 来源 | 对外载荷键转发（18 条含 Source） | `GameHooks.PayloadSource` | src/Orc.Game/GameHooks.cs:268 |

## R10 / R11：伤害三层与战斗（2026-10-19 取证）

| 类别 | 事实 | 符号 | file:line |
| --- | --- | --- | --- |
| 伤害 | 种类常量（combat/effect/battle） | `DamageKinds` | src/Orc.Game/Damage/DamageKinds.cs:7 |
| 伤害 | 触发端视图（Source/Target/Amount/Kind） | `DamageEventView` | src/Orc.Game/Damage/DamageViews.cs:35 |
| 伤害 | 处理端视图（+IsZeroed） | `DamageApplyView` | src/Orc.Game/Damage/DamageViews.cs:65 |
| 伤害 | 管道（三端＋处理端） | `DamagePipeline` | src/Orc.Game/Damage/DamagePipeline.cs:37 |
| 伤害 | 对战伤害端 | `DamagePipeline.CombatDamage` | src/Orc.Game/Damage/DamagePipeline.cs:68 |
| 伤害 | 效果伤害端 | `DamagePipeline.EffectDamage` | src/Orc.Game/Damage/DamagePipeline.cs:71 |
| 伤害 | 战斗伤害端 | `DamagePipeline.BattleDamage` | src/Orc.Game/Damage/DamagePipeline.cs:74 |
| 伤害 | 处理端 | `DamagePipeline.Apply` | src/Orc.Game/Damage/DamagePipeline.cs:77 |
| 伤害 | 处理端核心（唯一门户调用处） | `DamagePipeline.ApplyCoreAsync` | src/Orc.Game/Damage/DamagePipeline.cs:129 |
| 伤害 | 对局级持有 | `CommandManager.DamagePipeline` | src/Orc.Game/Commanding/CommandManager.cs:321 |
| 伤害 | 效果伤害转接 | `CommandManager.DamageByEffectAsync` | src/Orc.Game/Commanding/CommandManager.cs:1440 |
| 战斗 | 战斗转接（快照＋两次 battle.damage） | `CommandManager.BattleByEffectAsync` | src/Orc.Game/Commanding/CommandManager.cs:1447 |
| 战斗 | 公开入口（R10） | `EffectRuntime.BattleAsync` | src/Orc.Game/Effects/EffectRuntime.cs:211 |
| 效果 | 伤害动作（改走 effect.damage） | `EffectRuntime.DamageAsync` | src/Orc.Game/Effects/EffectRuntime.cs:189 |
| 门户 | 单位防御门户（已降 internal） | `UnitCard.ApplyDefenseDamageAsync` | src/Orc.Game/Cards/UnitCard.cs:584 |
| 门户 | HQ 生命门户（已降 internal） | `Hq.ApplyDamageAsync` | src/Orc.Game/Players/Hq.cs:157 |
| 门户 | 友元声明（测试程序集） | `InternalsVisibleTo("Orc.Game.Tests")` | src/Orc.Game/Properties/AssemblyInfo.cs:6 |
| 词条 | 重甲挂 combat.damage | `ArmorReductionEffect.AttachHandlers` | src/Orc.Game/Cards/KeywordEffects.cs:520 |
| 词条 | 免疫挂 damage.apply | `ImmuneZeroingEffect.AttachHandlers` | src/Orc.Game/Cards/KeywordEffects.cs:578 |

### 门户调用白名单（R11「旁路为零」）

`grep -rn "\.ApplyDefenseDamageAsync(\|\.ApplyDamageAsync(" src --include=*.cs` 的完整结果**只允许**这三行：

| 调用点 | 用途 | file:line |
| --- | --- | --- |
| `DamagePipeline.ApplyCoreAsync`（来源游标分支） | 单位伤害 + Killer 归属 | src/Orc.Game/Damage/DamagePipeline.cs:159 |
| `DamagePipeline.ApplyCoreAsync`（无来源分支） | 单位伤害（无归属） | src/Orc.Game/Damage/DamagePipeline.cs:164 |
| `DamagePipeline.ApplyCoreAsync`（HQ 分支） | HQ 生命扣减 | src/Orc.Game/Damage/DamagePipeline.cs:180 |

定义处（不是调用）：`UnitCard.cs:584`、`Hq.cs:157`。`Orc.UI` / `Orc.Mcp` / `Orc.Ai` / `samples` 为 0 调用；
`Orc.Plugins.BuiltIn` 为 0 调用（虽为友元）。测试程序集经友元保留既有 85 处调用。
