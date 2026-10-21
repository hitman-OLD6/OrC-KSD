# Implementation — 反制效果装配

把 `effects` 组件声明的反制卡效果从"被动挂事件总线"改为"装配进 `CounterCard` 的效果 handler 集（激活时进 `OrderFlow` Counter 段）"。全部口径已裁定，可执行。

## Scope

- Target：
  - `src/Orc.Game/Cards/EffectSystem.cs`（`CardEffectLoader.LoadAsync` 的预制体装载段 ③b／兜底段 ④／残留清理段 ⑤）
  - `src/Orc.Game/Cards/CounterCard.cs`（`AddEffectHandler` 装配面、新增 `AssembledEffectHandlerNames` 只读面、销毁清理）
  - `src/Orc.Game/Cards/CardBase.cs`（效果装载入口 `LoadEffectsAsync`）
  - `src/Orc/Cards/Card.cs` / `CardLifecycle.cs` / `Effect.cs`（**唯一允许的内核加性改动**：挂载链的"跳过主触发器"参数，默认值＝既有行为）
  - `tests/Orc.Mcp.Tests/SandboxPrimitiveChainTests.cs`（R4 端到端用例升级）
  - 承载反制卡效果的样例/卡包数据（`samples/card-pack-ai/cards/ai.sample.counter.card.json`）
- Excludes：
  - `CounterCard` 的激活／取消／消耗状态机（`_activeRegistrations` / `_chargedCost` / `_consumed`）
  - `OrderFlow` 的段序与 `Stop`/`Interrupt` 语义
  - 普通卡（单位／指令）的效果装载路径
  - 新增卡包字段或新效果声明形态

## 关键规则（执行者只需读本节 ＋ 各 S 的 Approach）

1. **抽取看预制体、不看实例**：handler 经 `PrefabManager.ResolveHandler(EventPrefab, Type)`（`src/Orc/Cards/PrefabManager.cs:114`）从**预制体事件**解析，不以效果实例为源——因此不看 `kind` 是 active 还是 passive，也不需为 `DynamicActiveEffect` 补 handler 取面。实例只提供快照数据（`Snapshot`）与名字。
2. **实例只做清理承载**：效果实例照常构造、照常进 `card.Effects`，承担 disposing 兜底 → 完整销毁 → `effect.removed` → teardown 的清理链；实例不承担"被调用"的职责。原因：`card.destroyed` 不遍历效果（`CardLoadoutProcessor.OnCleanup` 的 destroyed 分支只 `UnregisterCard`，`src/Orc/Cards/CardLifecycle.cs:286-322`），效果销毁完全依赖各自 disposing 触发器的兜底 hook——不挂 disposing 就等于卡销毁后效果永不销毁。
3. **Counter 宿主不挂主触发器**：段 ④ 对 `CounterCard` 宿主走"挂 disposing、跳过主触发器"的挂载（active 形态本就只挂 disposing，无需特判）。这是"无双触发"的唯一落点。落点形态＝既有挂载链加一个布尔分叉：`MountPassiveEffects(bool mountMainTriggers = true)` → `MountEffect(card, effect, mountMainTriggers)` → `ExecuteMount(bus, mountMainTriggers)`；**默认 `true`、既有调用点一个都不改**，只有反制卡装载路径传 `false`；`IsMounted = true`、`OnMount`、装载完成动作照常跑（`injects` 可撤销的前提），跳过的**只有 `MountMainTrigger` 这一行调用**。
4. **"不挂总线"的判据**：不挂**主触发器**、不挂 **otherTriggers**；`injects` 若显式指向某个总线触发器，属作者显式声明，不算违反本判据。
5. **一效果一 handler、内部串行**：一条效果产出**恰好一个** handler；handler 内部按列表序**串行 `await`** 该效果主触发器里 `band ≠ disposing` 的全部事件，每个事件用**同一个外层 `ctx`**。某事件 Resolve 失败／签名不符 ⇒ 该事件跳过、记 Error、继续下一条；`ctx.Stopped || ctx.Interrupted` 已置位 ⇒ 后续事件不再执行（与引擎"Stop 停本级剩余事件"同义）。一条效果不拆成多个可独立 Stop 的片段。
6. **disposing 事件只保兜底挂载、不进 handler**：`band: disposing` 的事件不参与反制 handler 求值，但 disposing 触发器的总线挂载必须保留（理由见规则 2）。
7. **空效果不产 handler**：主触发器无普通事件（只有 disposing、events 为空、或非动态形态无快照）⇒ 该效果不产 handler、记 **Warn**（正常用法："这条效果不承担拦截职责"）、不阻断加载、不计入条数。
8. **统一按 `CardTriggerView` 求值**：`ResolveHandler` 的目标视图类型恒为 `Orc.Game.Triggers.CardTriggerView`。声明其它 `viewType` 的＝隔离记录（Error 级，含效果 id 与声明类型）、该条缺席、其余照常。理由：`CardEventView` 无 `Target`／`Amount`（`src/Orc/Cards/CardLifecycle.cs:10-69`），反制要读"被反制指令卡＋施放目标＋数值"，只有 `CardTriggerView` 有（`src/Orc.Game/Triggers/CardTriggerView.cs:62-70`）。
9. **无需 adapter 层**：`ResolveHandler` 的返回值即 `Func<CardTriggerView, Context, CancellationToken, Task>`，与 `AddEffectHandler` 签名一致，**直接喂入**；`CounterCard` 侧不写转换层、不手工构造视图——`CreateCounterHandler` 已构造 `{ Card＝反制卡, Player＝持有者, Source＝被反制指令, Target＝施放目标, Argument＝数值 }` 并原样透传外层 `ctx`（`src/Orc.Game/Cards/CounterCard.cs:187-216`）。这是 R3「`ctx.Stop()` 停住当前指令流程层」成立的全部前提。
10. **禁止 `DynamicInvoke` 兜底**：`ResolveHandler` 返回 `Delegate`，强转 `Func<CardTriggerView, Context, CancellationToken, Task>` 失败即按 `signature` 隔离；`DynamicInvoke` 会把参数类型错误推迟到运行期，更难查。
11. **数据侧硬要求（csx）**：csx 入口须为具名变量、类型为 `Func<Orc.Game.Triggers.CardTriggerView, Context, CancellationToken, Task>`；不符＝求值器 `signature` 失败（`src/Orc.Script/CSharpScriptEvaluator.cs:116-122`），按隔离处理，**不是**加载失败。
12. **反制效果只承认 handler 面**：`injects` 照常生效（段 ③b 不变，销毁可撤销）；`otherTriggers` 非空 ⇒ 该条**不装配**、记 Error（`unsupported-other-triggers`）；`wrappers` 非空 ⇒ 该条**不装配**、记 Error（`unsupported-wrappers`）。两者都是"静默降级比缺席更危险"的场景，按单条隔离（其余效果照常、不整卡 fail-fast）。
13. **多效果并存**：一张反制卡声明多条效果时全部进 `_effectHandlers`、按**声明序**注册（不是优先级）；任一条 `Stop` 即触发消耗，`_consumed` 守卫保证只消耗一次（`src/Orc.Game/Cards/CounterCard.cs:190-215`）。
14. **命名与可观测面**：`AddEffectHandler` 的 `name` 传 **`effect.Name`**（不引入 prefab id／`root.id` 等新标识体系）；新增公开只读面 `AssembledEffectHandlerNames`（`IReadOnlyList<string>`）暴露**已装配集合**——装载完成即固定，激活与否不影响它，不进 `_activeRegistrations`、不参与注册/注销逻辑。它与 `RegisteredEffectHandlerNames`（`_activeRegistrations` 投影）同源同构，故"激活后两者逐项相等"可作强断言。
15. **禁自加内核开关**：不得添加"Stop 上溯"之类开关；发现 ctx 会被吞掉／替换即停下上报。

## 现状基线（已核查）

1. **数据形态两条路都不进 Counter 段**：被动形态走段 ④ `MountPassiveEffects` 挂总线（`src/Orc.Game/Cards/EffectSystem.cs:273-274`）；主动形态由 `DynamicActiveEffect` 承载，不挂总线、不装载、除 `CastAsync` 外无调用者（`src/Orc/Cards/DynamicEffect.cs:546-584`）。
2. **`AddEffectHandler` 在 `src` 内零调用者**（`src/Orc.Game/Cards/CounterCard.cs:93`）。
3. **挂载链结构**：`Card.MountPassiveEffects`（`src/Orc/Cards/Card.cs:188-201`）对被动效果调 `CardLoadout.MountEffect`（`CardLifecycle.cs:124-175`）＝`MountDisposal` → `ExecuteMount`（`MountDisposal` 幂等 → `MountMainTrigger` → `IsMounted=true` → `OnMount`，`Effect.cs:327-342`）→ 装载完成动作；对主动效果只调 `MountDisposal`。三处（`MountDisposal`／`CardLoadout`／`ExecuteMount`）均 `internal`，`Orc.Game` 无 `InternalsVisibleTo`（`src/Orc.Game/Orc.Game.csproj` 仅向 `Orc.Plugins.BuiltIn` 开放）。
4. **"只挂 disposing 但不置已装载"有实质缺陷**：`ExecuteDispose` 的"被动且未装载"分支只做容器移除＋发 `effect.removed`，**跳过 `RollbackInjections` 与 teardown**（`src/Orc/Cards/Effect.cs:384-402`）⇒ `injects`／`judicatorWrappers` 撤销动作永不执行（总线残留）。
5. **`ResolveHandler` 按传入 viewType 求值 csx**（`injects` 路径先例：`src/Orc.Game/Cards/EffectSystem.cs:330-344`），"声明的 `viewType`"与"用哪个类型求值"是两件事。
6. **跳过的连带面**：`DynamicPassiveEffect.MountMainTrigger` 是"主触发器＋otherTriggers"一并挂载（`src/Orc/Cards/DynamicEffect.cs:520-539`）；`injects` 由段 ③b 单独施加、不经挂载（`EffectSystem.cs:267-270`）；`wrappers` 施加在触发器注册项上（`DynamicEffect.cs:148-171`），不经该触发器调用就失效——归属见规则 12。
7. **快照可取**：`DynamicPassiveEffect.Snapshot` 与 `DynamicActiveEffect.Snapshot` 均为 public（`src/Orc/Cards/DynamicEffect.cs:507`、`565`），故"遍历 `card.Effects` 同时拿到名字与预制体"可行。

## Implementation Steps

### S1: 装载分支落点（判据与位置）
- Status: Todo
- Target: `src/Orc.Game/Cards/EffectSystem.cs`（`CardEffectLoader.LoadAsync`）
- Approach: 以宿主类型为判据——`card is CounterCard` 时：实例化与 `AddEffect` 照常；段 ④ 改调 `card.MountPassiveEffects(mountMainTriggers: false)`；随后按 `card.Effects` 列表序装配 handler。判据只看宿主类，不看 `typeCategory`、不看效果 `kind`。
- Acceptance:
  - [ ] 反制卡效果的主触发器在装载后不响应 `card.played`（R1 第二条验收）——用例 `Counter_CardEffect_NotMountedOnBus_NotTriggeredByCardPlayed`
  - [ ] 非反制卡（单位／指令）路径逐字节不变——用例 `Order_And_Unit_EffectLoading_Unchanged`
- Rationale: `CardLibrary` 按 `CardCategory.Counter` 产出 `CounterCard`（`src/Orc.Game/Cards/CardLibrary.cs:196`），宿主类是对局侧唯一稳定的语义判据。

### S2: 一条效果 → 一个 handler
- Status: Todo
- Target: `CardEffectLoader`（预制体段）
- Approach: 装载结束时遍历 `card.Effects`（列表序＝声明序，`CardEffectLoader` 已保证），对每个效果取其快照（`Snapshot`）→ 先判形态：`Root.OtherTriggers` 非空或 `Root.Wrappers` 非空 ⇒ 该条不装配（Error）；否则结算"普通事件"＝`Root.MainTrigger.Events` 中 `Band != "disposing"` 者（列表序）；无普通事件／无快照 ⇒ Warn、跳装配。有则构造**一个** handler：**装配期一次性**按序 `ResolveHandler(ev, typeof(CardTriggerView))` 并做强转校验，把已解析的 `(事件名, handler)` 数组捕获进闭包（解析失败的条已在装配期剔除并记 Error，不进入数组）；运行期该 handler 只按序 `await` 已解析的条目，共用外层 `ctx`，`ctx.Stopped || ctx.Interrupted` 后短路后续条目 → `AddEffectHandler(effect.Name, 该 handler)`。
- Acceptance:
  - [ ] 每条产 handler 的效果恰产出一个 handler；条数＝产出 handler 的效果数（R2 口径）——用例 `Counter_CardEffect_OneEffectOneHandler`
  - [ ] 同一 handler 内事件按列表序串行、共用同一个 `ctx` 实例——用例 `Counter_CardEffect_SerialEvents_SharedContext`
  - [ ] `otherTriggers`／`wrappers` 非空的效果不产 handler——用例 `Counter_CardEffect_UnsupportedFaces_NotAssembled`
- Rationale: 一条效果是完整行为单元；拆成多个可独立 Stop 的片段会让"谁停的"不可解释，也让 R2 的条数口径失去意义。遍历 `card.Effects` 而非声明列表，可同时拿到 `Name` 与快照，不需要第二套配对逻辑。

### S3: 挂载拆分（唯一允许的内核加性改动）
- Status: Todo
- Target: `src/Orc/Cards/Card.cs`、`CardLifecycle.cs`（`CardLoadout.MountEffect`）、`Effect.cs`（`ExecuteMount`）
- Approach: 采用"过滤参数"形态而非"仅挂 disposing"入口——`MountPassiveEffects(bool mountMainTriggers = true)` → `MountEffect(card, effect, mountMainTriggers)` → `ExecuteMount(bus, mountMainTriggers)`。
  实现纪律两条：①**默认值 `true`，既有调用点一个都不改**，新参数只在反制卡装载路径传 `false`；②`IsMounted = true`、`OnMount`、装载完成动作照常跑（`injects` 可撤销的前提），跳过的**只有 `MountMainTrigger` 这一行调用**——不得顺手多跳。
- Acceptance:
  - [ ] `mountMainTriggers: false` 时 disposing 已挂、主触发器未挂、`IsMounted == true`——用例 `Counter_CardEffect_MainTriggerNotMounted_DisposalMounted`
  - [ ] 卡销毁 ⇒ 完整销毁链（`effect.removed` 恰一次、injects 撤销、teardown 执行）——用例 `Counter_CardEffect_Destroyed_DisposesFully`
  - [ ] 默认参数下既有装载路径行为不变（R5）——用例 `MountPassiveEffects_DefaultBehavior_Unchanged`
- Rationale: 只挂 disposing 会让 `IsMounted` 停在 false，`ExecuteDispose` 的"未装载"短路分支跳过注入撤销与 teardown（基线第 4 条），等于把"清理承载"变成"清理残留"；而 `IsMounted=true` ＋ 不挂主触发器既满足"不响应总线"，又让完整销毁链可用。此改动是挂载链上的一个布尔分叉，不新造第三条挂载路径（基线第 3 条的封装限制也排除了游戏层自建）。

### S4: 失败与留痕分级
- Status: Todo
- Target: `CardEffectLoader`
- Approach: 沿用 `engine.RootStream.WriteLog`，**三档不混**：
  - **Error**＝声明了不支持的形态或求值失败：`unsupported-other-triggers`／`unsupported-wrappers`／声明 `viewType` 非 `CardTriggerView`／`ResolveHandler` 结构化失败（含 `ErrorCategory`）／强转 `signature` 不符／预制体未注册／单事件失败。
  - **Warn**＝效果本身没有可调用事件（空效果，正常用法："这条效果不承担拦截职责"）。
  - **Info**＝成功装配一条（写清效果名与折叠进去的事件数）。
  均不抛、不阻断，该条（或该事件）缺席。
- Acceptance:
  - [ ] 三类隔离各自留下对应级别的记录（含效果 id／预制体 id 与原因）——用例 `Counter_CardEffect_UnsupportedFaces_NotAssembled`、`Counter_CardEffect_NoCallableEvent_WarnsWithoutHandler`、`Counter_CardEffect_Assembled_LeavesInfoTrace`
  - [ ] 装载不中断，失败卡仍可正常激活（效果缺席）（R1 第三条验收）——用例 `Counter_CardEffect_ResolveFailure_CardStillActivatable`
- Rationale: 对齐 `CardEffectLoader` 既有隔离口径，不新造失败通道；三档分开是为了让"作者写错了"（Error）与"作者合法地写了个空效果"（Warn）在事件流里可区分。

### S5: 已装配集合可观测面
- Status: Todo
- Target: `src/Orc.Game/Cards/CounterCard.cs`
- Approach: 新增 `public IReadOnlyList<string> AssembledEffectHandlerNames`＝`_effectHandlers.Select(h => h.Name)` 的只读投影（装载完成即固定；不进 `_activeRegistrations`、不参与注册/注销逻辑）。`AddEffectHandler` 的 `name` 传 `effect.Name`。
- Acceptance:
  - [ ] 未激活时 `AssembledEffectHandlerNames.Count` ＝ 产出 handler 的效果数、`RegisteredEffectHandlerNames` 为空——用例 `Counter_AssembledHandlers_ObservableBeforeActivation`
  - [ ] 激活后 `RegisteredEffectHandlerNames` 与 `AssembledEffectHandlerNames` **逐项相等**（同序）——同用例
  - [ ] 取消后 `Registered` 复归为空、`Assembled` 不变——同用例
- Rationale: `RegisteredEffectHandlerNames` 是 `_activeRegistrations` 投影（`src/Orc.Game/Cards/CounterCard.cs:80-81`），未激活恒空，承担不了 R1 第一条"已就绪未注册"的验收；补一个同构的装配面即可让"未激活＝已装配未注册、激活＝两者相等"成为可执行断言。

### S6: 激活中卡被销毁的清理（R2 隐含要求）
- Status: Todo
- Target: `src/Orc.Game/Cards/CounterCard.cs`
- Approach: `CounterCard` 增补卡销毁清理：卡销毁 ⇒ 注销 `_activeRegistrations`、置未激活、清 `_chargedCost`，**不退费**（与一次性消耗同口径）；销毁后不得再激活。
- Acceptance:
  - [ ] 激活中的反制卡被销毁后，Counter 段不再残留其 handler——用例 `Counter_DestroyedWhileActive_UnregistersHandlers`
  - [ ] 点数不因销毁退还；销毁后再激活被拒——同用例断言
- Rationale: 现状无 `card.destroyed` 监听，激活中的卡被销毁（如满手爆牌）会留下悬空注册。

### S7: 沙盒端到端用例升级
- Status: Todo
- Target: `tests/Orc.Mcp.Tests/SandboxPrimitiveChainTests.cs`
- Approach: 现用例 `CounterChain_PrimitiveBuiltCounter_ActivatesAndOrderResolves` 更名为 `CounterChain_PrimitiveBuiltCounter_StopsOrder` 并升级断言：锁定 `Stop`（`card.played` 计数 0、指令效果未触发、指令仍扣费离手）＋ `card.discarded` ＋ 反制卡弃置；原语摆局面段（`sandbox.instantiate`／`sandbox.points`）不改动。用例内 `StopCounterCardJson` 已是 `viewType: CardTriggerView` ＋ `Func<CardTriggerView, ...>`，与统一契约一致，**数据原样可用**。
- Acceptance:
  - [ ] 用例更名并锁定 `Stop` ＋ `card.discarded`——用例 `CounterChain_PrimitiveBuiltCounter_StopsOrder`
  - [ ] `sandbox.instantiate`／`sandbox.points` 段零改动——同用例内对照

### S8: 既有反制数据/样例的语义归属
- Status: Todo
- Target: `samples/card-pack-ai/cards/ai.sample.counter.card.json`
- Approach: 该样例现声明 `viewType: Orc.Cards.CardEventView` ＋ `Func<CardEventView, ...>` 空效果，按统一契约会被判类型不符而隔离。改为 `viewType: Orc.Game.Triggers.CardTriggerView` ＋ 对应 csx 签名（仍为空效果体）。
- Acceptance:
  - [ ] 样例卡在新语义下行为可预期（不静默失效、不双触发、不被隔离）——用例 `SampleCounterCard_AssemblesWithoutIsolation`
- Rationale: 官方样例是可复制模板，留一个"会被隔离"的样例等于把坑发给所有作者。

### S9: 零回归验证
- Status: Todo
- Target: `dotnet test OrcEngine.slnx`
- Approach: 先取基线（2310 ＋ 沙盒原语 56）为绿，再逐条改动后复跑；重点盯 `P4OrderFlowTests` / `PlayChainCounterTests`（程序化 `AddEffectHandler`）与单位／指令装载路径。本条不新增用例，以既有全量套件为验收面。
- Acceptance:
  - [ ] 全绿；程序化装配用例行为不变（R5）——既有套件
- Rationale: 程序化路径不经装载链分支，理应逐字节不变；用基线对照证明。
