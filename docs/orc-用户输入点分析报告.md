# OrC 引擎用户输入点分析报告

> 用途：盘清 `d:/OrC-KSD`（C# 卡牌引擎：内核 `src/Orc` + KARDS 游戏层 `src/Orc.Game`）中**所有「用户/玩家的主动输入点」**——即由外部（UI / 玩家 / AI）发起的动作入口 API，并把每个入口同时作为 **hook**（可被判定器 / 修饰器 / 触发器 / 交互桥接介入或被对外信号观察）标注。
> 关联：`docs/kards-diy-游戏流程分析报告.md`（对照流程）、`docs/kards-diy-可hook点清单报告.md`（对照 hook 面）、`CONTEXT.md`（术语表）。
> 日期：2026-10-05。行号以分析时版本为准，**以符号名为准**。

---

## 0. 两层结构（先看结论）

- **引擎内核 `src/Orc`**：通用机制层——触发器（`Trigger`）、事件总线（`Bus`/`Emit`）、流程（`AttackFlow`/`DamageFlow`/`OrderFlow`）、动作作用域与事件流（`ActionScope`/`EventStream`）、判定器（`Judicator`）、脚本注入（`Scripting`）、预制体（`PrefabManager`）。
- **游戏层 `src/Orc.Game`**：KARDS 规则层——**所有玩家动作入口都在这里**（Manager 群 + Match），内核只提供通用机制面。
- **三类「接线」途径**（把游戏层/UI 接进引擎的方式）：
  1. **被动触发器**：`Bus.Mount` 订阅更新（`Trigger` 声明 `hooks`）。
  2. **效果注入**：`Effect.Inject<TView>` 注入流程触发器的具名 band。
  3. **判定器替换**：`JudicatorRegistry.RegisterModing` 做全局逻辑替换。

---

# 第一部分：用户 / 玩家输入点

> 说明：OrC 目前**没有独立 UI 层**；「输入点」＝游戏层对外暴露的动作入口方法（UI/玩家/AI 调用）。每条同时给出它「作为 hook」的介入面。

## A. 对局生命周期 / 卡组（输入）与 hook

| 入口 | 文件 — 成员 | 行号 | 语义 | 作为 hook |
|---|---|---|---|---|
| 创建对局 | `src/Orc.Game/Match/Match.cs` — `Match.Match(...)` | 80 | 注入卡组×2、定义集、种子、先手、options、桥接、效果/部署注册表、构筑配置、`judicatorAssembly`（判定器装配段） | 构造注入点 80-92：`targeterBridge`87 / `effectRegistry`88 / `deploymentLogicRegistry`89 / `deckConfigForPlayerA/B`90-91 / `judicatorAssembly`92 |
| 初始化对局 | `Match.Initialize(ct)` | 360 | 建管理器群→洗切→逐张加载→起手→先手回合；只允许一次 | 接线：装载完成动作 372、管理器装配 380-465、判定器注册段 470-489、底层触发器登记 509-512、回合恢复钩子 557 |
| 洗切卡组 | `Match.ShuffleDeckAsync(player,ct)` | 304 | 统一洗切动作面，发 `deck.shuffled` | 信号 `deck.shuffled`；玩家级、恰一次 |
| 结束回合 | `Match.EndTurn(ct)` | 568 | 转发 `TurnManager.EndTurn`（仅「进行」态） | 发 `turn.end.before`/`turn.end` 等 |
| 注册卡定义 | `src/Orc.Game/Cards/CardLibrary.cs` — `Register(id,definition)` | 65 | **代码注册**（无 JSON 载入） | 提供器 42-59（回合上下文/词条/对局ID/效果/部署逻辑/验证判定器解析） |
| 实例化卡 | `CardLibrary.Instantiate(id)` | 127 | id→卡实例 | — |
| 加载单卡 | `src/Orc.Game/Cards/CardBase.cs` — `LoadAsync(owner,ct)` | 166 | 归属→ID→元数据→部署逻辑→词条→效果→发 `card.load` | 发 `card.load`；装载链钩子见第二部分 ⑦ |

## B. 打出链（`src/Orc.Game/Managers/PlayManager.cs`）

| 入口 | 行号 | 语义 | 作为 hook |
|---|---|---|---|
| `BeginUnitPrePlayAsync(card,ct)` | 73 | 单位预打出（验证＋交互选空槽；确认自动衔接打出链） | 动作作用域 `BeginAction`85；预打出 `PrePlayTrigger`88（验证判定器 `validation.cost.check`）；targeter 交互 117-118（桥接完成/取消驱动） |
| `PlayUnitAsync(card,target,ct)` | 149 | 单位部署打出链（部署路径） | 发 `card.played` + `unit.deployed`；部署词条/效果 |
| `JoinUnitAsync(card,target,ct)` | 200 | 加入路径（不扣费、不走部署词条） | 发 `unit.joined` |
| `PlayOrderAsync(card,ct)` | 253 | 指令打出（预打出＋打出一次链） | 发 `card.played`；`OrderFlow` |
| `UseCounterAsync(card,ct)` | 323 | 反制单入口状态翻转（激活↔取消，仅己方回合） | 反制 `UseCounterTrigger`；验证 `validation.counter.use` |
| 结果对象 | `PlayResult` | — | 不抛；失败类别化；取消=`Cancelled`；拒绝/失败/取消零副作用 |

## C. 指挥 / 移动 / 攻击（`src/Orc.Game/Commanding/CommandManager.cs`）

| 入口 | 行号 | 语义 | 作为 hook |
|---|---|---|---|
| `BeginCommandAsync(unit,ct,triggerCard?)` | 312 | **一次拖拽＝一次调用链**，内部经 `CommandTrigger` 分派到移动/攻击（**唯一公开的动作入口**） | 验证判定器 `validation.move.recheck` / `validation.attack.recheck` |
| `LeaveBattlefieldAsync(unit,ct)` | 256 | 非死亡离场受控入口（转换组合用） | — |
| `GetCommandAvailability(unit)` | 366 | 动作可用性聚合判定（纯查询） | — |
| `RefreshActionStates(player)` | 749 | 回合恢复（注入钩子） | `TurnManager.ActionStateRefresher` 40（internal 注入，Match.Initialize 557 接线） |
| `IsUnitGuarded` / `IsHqGuarded` | 650 / 658,666 | 守护判定查询 | — |
| 公开的内置流程触发器（可外部 `InvokeAsync`） | `CommandTrigger`223、`UnitMoveTrigger`226、`UnitAttackTrigger`229、`AttackDamageTrigger`232、`DefenseDepletionTrigger`293、`HqZeroTrigger`297 | — | 可作为直接触发点 |
| 内部流程 | `HandleCommandFlowAsync`806 → `DispatchMoveAsync`895 / `DispatchAttackAsync`934 | — | ⚠ **无独立公开 `MoveUnitAsync`/`AttackAsync`** |

> 发 `unit.position.changed`（移动执行段，槽位变更后、收尾段之前）。

## D. 手牌 / 资源 / 抽弃（输入）

| 入口 | 文件 — 成员 | 行号 | 语义 |
|---|---|---|---|
| 起手装载 | `Managers/PlayerManager.cs` — `LoadOpeningHand(player,count)` | 126 | ⚠ 静默装载、无交互（无 mulligan 入口） |
| 抽牌 | `PlayerManager.DrawCard(player,ct)` | 148 | 发 `card.drawn` → `card.hand.add` |
| 弃牌 | `PlayerManager.DiscardCardAsync(player,card,ct)` | 181 | 发 `card.discarded` |
| 回卡组顶 / 回卡组 | `PlayerManager.ReturnToDeckTop` / `ReturnToDeck` | 205 / 214 | — |
| 是否在构筑之外 | `PlayerManager.IsOutsideDeck` | 103 | ID 水位线判定 |
| 结算 / 加点 | `Managers/ResourceManager.cs` — `Settle(player)` / `AddPoints(player,amount)` | 34 / 48 | 指挥点 |
| 回合 | `Managers/TurnManager.cs` — `EndTurn(ct)` / `StartFirstTurn`(internal) | 71 / 54 | 发 turn 五连 |

## E. 卡牌生成 / 放置（`src/Orc.Game/Match/MatchCardService.cs`）

| 入口 | 行号 |
|---|---|
| `CreateAsync` / `PlaceToHandAsync` / `PlaceToSupportLineAsync` / `PlaceToDeckTopAsync` / `PlaceIntoDeckShuffledAsync` | 165 / 204 / 231 / 267 / 284 |
| `GetAdjacentEmptySlots` | 310 |
| 组合入口 `CreateAndPlaceToHandAsync` / `...ToSupportLineAsync` / `...ToDeckTopAsync` / `...IntoDeckShuffledAsync` / `...ToAdjacentAsync` | 320 / 333 / 351 / 360 / 381 |

> 效果运行期与 UI 皆可用（供「生成衍生物/加手牌/上场」类效果）。

## F. 目标选择（UI 交互应答——输入点）

| 入口 | 文件 — 成员 | 行号 | 语义 |
|---|---|---|---|
| 发起 | `Targeting/TargeterManager.cs` — `RunAsync(flow,param)` | — | 发起 targeter（流程函数，FIFO 排队） |
| 发起 | `Targeting/ITargeterFlow.cs` — `Step(selector,param)` | — | 流程内产出选择器并等待应答 |
| **应答** | `Targeting/TargetingInteraction.cs` — `Complete(requestId,refsBySlot)` / `Complete(requestId,selectionsBySlot)` / `Cancel(requestId)` | 73 / 101 / 116 | **确认/取消手势由桥接驱动** |
| 桥接契约 | `Targeting/ITargeterBridge.cs` | — | 前端实现此接口 |

> 这是「引擎向玩家索取输入」的统一形态（对应 kards-diy 的 `chooser`/`KG.ask`）。

## G. 单位 / HQ 数值门户（受控改写入口 = hook）

| 入口 | 文件 — 成员 | 行号 | 语义 |
|---|---|---|---|
| 受防御伤害 | `Cards/UnitCard.cs` — `ApplyDefenseDamageAsync(amount,ct)` | 191 | 门户组件唯一改写入口 |
| 修复防御 | `UnitCard.RepairDefenseAsync(ct)` | 219 | — |
| 增补类型 | `UnitCard.AddUnitTypeAsync(type,ct)` | 244 | 发 `unit.types.changed`（增量、无变化零发射） |
| HQ 受伤 | `Players/Hq.cs` — `ApplyDamageAsync(amount,ct)` | 154 | 毒/伤统一路径 |
| **伤害改写挂钩** | `Hq.AddDamageRewriter/RemoveDamageRewriter` | 84 / 97 | 可改写伤害 |
| **致命介入挂钩** | `Hq.AddLethalIntervention/RemoveLethalIntervention` | 105 / 118 | 可介入致命 |
| 按来源清理挂钩 | `Hq.RemovePipelineHooksBySource` | 129 | — |
| 触发器重发 | `Cards/RetriggerSystem.cs` — `RequestAsync` / `RequestDeployAsync` / `RequestDeathrattleAsync` | 124 / 270 / 283 | 重发部署/亡计 |

## H. 卡上公开触发器（可直接 `InvokeAsync` 的接线点）

| 卡类 | 触发器 | 行号 |
|---|---|---|
| `Cards/UnitCard.cs` | `PrePlayTrigger`63 / `PlayTrigger`66 / `DeployTrigger`69 / `JoinTrigger`72 / `UnitizeTrigger`75 | |
| `Cards/OrderCard.cs` | `PrePlayTrigger`51 / `PlayTrigger`54 | |
| `Cards/CounterCard.cs` | `UseCounterTrigger`59 | |

---

# 第二部分：引擎 hook / 扩展机制

## ① Hook 标识注册表（最底层词汇表）

`src/Orc/Core/HookRegistry.cs`
- `Register(hookName)` 19：hook 名↔`HookId`；**开放集合**、同名幂等、碰撞记录（`HookIdCollision`78）。
- `TryGetId`43 / `TryGetName`50 / `Names`63 / `Collisions`66 / `Count`69。
- 引擎挂点：`LogicEngine.Hooks`61；构造期预登记 4 个内置更新 41-44；`Bus.Mount`64 首次出现即登记；`Orchestration.Register`50。

## ② 总线订阅 / 派发（被动触发器挂载）

`src/Orc/Core/Bus.cs`
- `Mount<TView>(trigger)`31：只挂 Passive 且 hooks 非空的触发器。
- `UnmountOwner(owner)`88：按所有者（引用相等）卸载。
- `Emit(updateType,payload,ct)`136：写 update 条目 → 通知外部观察者 → 按「挂载优先级升序 → 注册序」await 订阅者（`SnapshotFor`250 排序）。
- `GetSubscribers`189 / `EnumerateHooks`212 / `HookSubscriptions`320。
- 触发器侧声明：`Trigger<TView>` 构造参数 `hooks`/`priority`/`owner`（`Trigger.cs:56-64`）；**`Hooks` 是构造期声明（无运行期注册 API）**。

## ③ Trigger / TriggerEvent 订阅与派发

`src/Orc/Core/Trigger.cs`
- 构造 56（name/kind/bandType/events/hooks/priority/owner/stableKey）
- `Register(name,handler,priority,downstream)`184 / 带 band 版 195：注册事件，返回 `TriggerRegistration`（可 `Unregister`209）。
- **moding（逻辑替换）**：`RegisterModing(target,moding)`241 / `UnregisterModing`270（栈语义、纯替换、句柄即权限）。
- **验证判定器绑定**：`BindValidation(binding,subjectProvider)`337；`Validate`300 / `EvaluateValidation`312（每次 `InvokeAsync` 固定先调用，391）。
- 统一执行入口 `InvokeAsync(engine,data,ct)`368：验证→绑视图→按 band/优先级执行事件链→产出 `EventStream`。
- 构造期句柄 `InitialRegistrations`177（初始事件可寻址，供 moding/撤销）。
- `TriggerEvent<TView>`（`TriggerEvent.cs`13/23 构造、`Name`34/`Handler`37/`Band`40/`Priority`43）；`ITriggerMetadata`（`TriggerMetadata.cs:8`，非泛型只读面，审查链用）。

## ④ Judicator 判定器（可被改写/替换的无状态服务）

- `Core/Judicator.cs`：`Judicator`11（`Invoke`19 / `Unpack<T>`30）；`Judicator<TDelegate>`63（`Adapt`72）。
- `Core/JudicatorRegistry.cs`：`Register`29（重复拒绝）、`Resolve`52（fail-fast）、`Invoke`73、`RegisterModing`89/117（双形态）、`UnregisterModing`143；统一解析点 `JudicatorEntry.Invoke`228（每次取 moding 栈顶）。
- `Core/JudicatorBinding.cs`：`FromRegistration`29（对局路径，可 moding）/ `FromStandalone`43（独立默认）；`InvokeValidation`58。
- **游戏层接线**：`Game/Judicators/JudicatorNames.cs` 常量 11（`validation.cost.check`16、`validation.counter.use`19、`validation.move.recheck`22、`validation.attack.recheck`25）；实现 `CostCheckJudicator`/`CounterUseJudicator`/`MoveRevalidationJudicator`/`AttackRevalidationJudicator`；装配点 `Match.Initialize` 475-489。

## ⑤ 修饰器 / 修饰链 / 光环（游戏层接线）

- `Game/Cards/ModifierSystem.cs`：`CardStatFields`30、`ModifierExpiry`71（自订阅相位到期自注销）、`ModifierMountContext`91（`RequestSelfUnmountAsync`115）、修饰器基类 `OnMount`/`OnUnmount`/`Apply` 链节。
- `Game/Cards/CardModifierComponent.cs`：链/检测/管线载体。
- `Game/Cards/AuraSystem.cs`：`AuraDeclaration`43（host/field/transform/source/predicate，注册进场级收集面）。
- `Game/Board/GameEnvironment.cs`：`CollectAuras`170（现收集）、`RerunAllCardsAsync`214（全域重跑显式驱动）、`Auras` 属性、`ResolveFor`263。
- `Game/Cards/StatUpdateDetection.cs`（更新检测接口）+ `GameUpdates.EmitCardStatChanged`（集中触发 `card.stat.changed`）。

## ⑥ 脚本注入（Scripting / 接口倒置）

- `Core/Scripting.cs`：`ScriptRequest`21（csx 源/入口/视图类型/超时）、`IScriptEvaluator`71（内核定义、satellite 实现）、`ScriptHandlerAdapter`82（擦除委托绑回泛型）。
- 挂点：`LogicEngine.ScriptEvaluator`73；预制体经 `PrefabManager.ResolveHandler`114 解析（assembly 来源 / csx 来源）。

## ⑦ 效果装载链 / 预制体（内核扩展点）

- `Cards/Effect.cs`：`OnMount`63 / `OnUnmount`68（作者钩子）；`Inject<TView>`79（注入流程触发器具名 band，卸载自动撤销）；`Injections`110；`AddUnmountCleanup`121；`CastAsync`358。
- `Cards/CardLifecycle.cs`：`CardEventView`10 / `PayloadKeys`22 / `CardLoadout.MountEffect`71（装载链：挂主触发器→OnMount→装载完成动作）。
- 挂点：`LogicEngine.RegisterCardMountCompletedAction`134（装载管线扩展点）。
- `Cards/PrefabManager.cs`：`RegisterHandler`35 / `RegisterPrefab`56 / `LoadDirectory`78 / `ResolveHandler`114；`PrefabJson.cs` / `Prefabs.cs`（`EffectSnapshot`/`BuildLoadPlan`356）。
- `Cards/DynamicEffect.cs`：`Instantiate`370 / `InstantiateRegistered`383 / `CastAsync`339。
- 声明效果 / 预制体 / 动态效果 = 三类装载来源。

## ⑧ ActionScope 作用域切段 + 事件流

- `Core/ActionScope.cs`：`ActionScope`10（`Dispose`33 幂等）。
- `Core/LogicEngine.cs`：`BeginAction`253（嵌套合并，最外层产段）、`EndAction`266（零发射不产段）、`TakeSegments`293 / `TryTakeSegment`306。
- `Core/EventStream.cs`：树形因果记录；`EntriesInRange`145 / `ChildrenInRange`168 / `AttachTo`119 / `ExecutionOutcome`7。
- 输出侧：`Output/EventSegment.cs`、`EventStreamJson.cs`、`SnapshotJson.cs`、`AuditChainJson.cs`。

## ⑨ 编排 / 审查链（声明期图）+ Updates 常量

- `Core/Orchestration.cs`：`Register(trigger,origin,host)`33、`TriggersWithHook`72、`TriggersWithKey`93、`BuildAuditChain`113、`QueryChain`129；`AuditChain`270 / `TriggerNode`290。
- `LogicEngine.Orchestration`67 / `ExportAuditChainJson`83 / `ExportChainText`87 / `ExportChainJson`91。
- `Core/Updates.cs`（内核更新常量 `card.placed`/`card.destroyed`/`card.data`/`effect.removed`）；`GameUpdates.cs`（游戏层 17 条，见 §第三部分）。

## ⑩ 游戏层「接线」扩展点（装配注入面）

- `Game/Triggers/TriggerRegistry.cs`：`Register(trigger,layer)`23 / `GetByLayer`45；`TriggerLayer`（`LowLevel`/`External`）。
- `Game/Cards/EffectSystem.cs`：`CardEffectRegistry.Register`43 / `Declare`62 / `DeclarePrefab`102。
- `Game/Cards/KeywordRegistry.cs`：`Register(keyword,factory,isBattleKeyword)`65（进程级词条工厂，开放扩展）。
- `Match` 构造注入点 80-92；`Initialize` 接线：装载完成动作 372、管理器装配 380-465、判定器注册段 470-489、底层触发器登记 509-512、回合恢复钩子 557。
- `CardLibrary` 42-59 六个延迟读取提供器（回合上下文/词条/对局ID/效果/部署逻辑/验证判定器解析）。

---

# 第三部分：对外信号契约（UI 观察 hook）

`src/Orc.Game/GameUpdates.cs` —— 17 条对外信号（字面值 = 订阅契约，冻结、ordinal、大小写敏感），统一经 `LogicEngine.Emit`：

| 组 | 信号 | 载荷 |
|---|---|---|
| 回合五连 | `turn.start.before` / `turn.start` / `turn.start.after` / `turn.end.before` / `turn.end` | `{Player, TurnNumber}` |
| 通用三项 | `card.played`（W4-1 载荷化）/ `card.drawn` / `card.stat.changed`（W2a 载荷化） | `{Card,Player}` / `{Player,Card}` / `{Card,ChangedFields}` |
| 卡牌加载与手牌 | `card.load` / `card.hand.add` / `card.discarded`(G7) | `{Card,Player}` |
| 单位系列 | `card.died` / `unit.joined` / `unit.deployed` / `unit.position.changed` | `{Card}` / `{Unit,Position}` / `{Unit,OldPosition,NewPosition}` |
| 洗切 | `deck.shuffled`(W4-1) | `{Player,Deck}` |
| 类型变更 | `unit.types.changed`(S9) | `{Unit,AddedType}` |

- 载荷键常量（`GameUpdates` 126 区）：`Player/TurnNumber/Card/Unit/Position/OldPosition/NewPosition/ChangedFields/Deck/AddedType`。
- 发射助手（可选便捷层）：`EmitCardPlayed`162、`EmitDeckShuffled`180、`EmitUnitTypesChanged`201、`EmitCardStatChanged`222、`EmitCardDrawn`245、`EmitCardPlaced`266、`EmitCardLoad`277、`EmitCardHandAdd`293、`EmitCardDiscarded`310、`EmitCardDied`327、`EmitUnitJoined`338、`EmitUnitDeployed`354、`EmitUnitPositionChanged`370。
- 另：`LogicEngine.Subscribe`165 / `OnImmediateUpdate`324（**即时更新监听**，非阻塞 UI 反馈）；段轮询 `TakeSegments`293 / `TryTakeSegment`306（**拉取式**事件流段）。

---

# 第四部分：缺口清单（据代码注释与结构分析）

| # | 缺口 | 证据 | 影响 |
|---|---|---|---|
| 1 | **卡组/卡牌无 JSON 载入** | `CardLibrary.cs` 头注 8「本批无 JSON 载入」——只能代码 `Register` | 无法直接吃 kards-diy 的 `nations/*.json`；仅效果预制体支持 `*.prefab.json`（`PrefabManager.PrefabFilePattern`17 / `LoadDirectory`78） |
| 2 | **无独立公开 `MoveAsync`/`AttackAsync`** | 移动/攻击只能经 `BeginCommandAsync`（312）拖拽链，或直接 `Invoke` `UnitMoveTrigger`/`UnitAttackTrigger` | UI 若需「分按钮」而非「一次拖拽」，需新增入口 |
| 3 | **无认输/投降、无换手牌（mulligan）入口** | 起手由 `PlayerManager.LoadOpeningHand`（126）静默装载；无 surrender/concede | 开局阶段与终局阶段缺交互 |
| 4 | **`CardEffectRegistry` 需外部装配** | `EffectSystem.cs`；缺省 null = 卡上无声明效果 | 效果声明不可用 |
| 5 | **无「效果覆盖层」类外部改写通道** | 无对位 `KG_EFFECT_OVERLAY` | 数据层动态改效果需新设计 |
| 6 | **判定器/触发器映射表未集中导出** | `invariants.json` 式权威清单在 OrC 侧无对位 | 需建「信号↔触发器」一致性核对 |

---

# 第五部分：kards-diy 输入点/流程 → OrC 落点映射

| kards-diy 输入点 / 流程 | OrC 落点 | 状态 |
|---|---|---|
| 出牌（`KG.playCard` / `mpPlay`） | `PlayManager.BeginUnitPrePlayAsync` / `PlayUnitAsync` / `PlayOrderAsync` | 已有 |
| 反制（`inst.suspended` / `mpUnsuspend`） | `PlayManager.UseCounterAsync`（激活↔取消） | 已有 |
| 移动（`KG.move` / `mpMove`） | `CommandManager.BeginCommandAsync` → `UnitMoveTrigger` | 已有（无独立 API） |
| 攻击（`KG.attack` / `mpAttack`） | `CommandManager.BeginCommandAsync` → `UnitAttackTrigger`；`AttackFlow`/`DamageFlow` | 已有 |
| 结束回合（`#endTurnBtn` / `KG.endTurn`） | `Match.EndTurn` / `TurnManager.EndTurn` | 已有 |
| 选目标 / 抉择 / 开发（`uiChooser` / `KG.ask` / `renderPrompt`） | `TargeterManager.RunAsync` + `ITargeterBridge.BeginTargeting`（会话逐个取选择器 + 语义事件提交） | 已有（结构不同） |
| 重调度 mulligan（`mulliganReplace`/`mulliganDone`） | **缺** | 需新增 |
| 拖拽放置（`applyDrop`） | `BeginUnitPrePlayAsync` 的 targeter 选空槽（117-118） | 已有（形态不同） |
| 认输 / 重开 | **缺** | 需新增 |
| 卡组构筑 / 卡组库 | `Match` 构造 `deckConfigForPlayerA/B`（90-91） | 部分 |
| 网络/AI 输入（`mp*`/`AI.chooser`） | 由宿主实现 `ITargeterBridge` + 订阅 `GameUpdates` | 未接线（无 UI 层） |

---

## 附：复刻者关键结论

- **动作入口 = 游戏层**：玩家动作（出牌/指挥/结束回合/洗切/抽弃）全在 `Orc.Game` 的 Manager 上；内核 `Orc` 只提供通用机制面。
- **唯一公开的动作入口是少数几个**：出牌走 `PlayManager`，移动/攻击走 `CommandManager.BeginCommandAsync`，回合走 `Match.EndTurn`，应答走 `TargetingInteraction`。
- **三类接线途径**：①`Bus.Mount` 订阅更新；②`Effect.Inject` 注入流程 band；③`JudicatorRegistry.RegisterModing` 全局逻辑替换。
- **可插拔装配点**：`Match` 构造参数 + `CardEffectRegistry`/`DeploymentLogicRegistry`/`KeywordRegistry`/`JudicatorRegistry`/`PrefabManager`（均「注册即配置、重复拒绝」）。
- **观察 UI 的两种面**：即时 `Subscribe`/`OnImmediateUpdate`（反馈）；拉取 `TakeSegments`（表现播放）。
