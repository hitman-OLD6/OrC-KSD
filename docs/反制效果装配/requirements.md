# Requirements — 反制效果装配

让数据侧（卡包 JSON 的 `effects` 组件）定义的反制效果，在反制**激活**后真正挂到 `OrderFlow` 的 `Counter` 段，成为"能拦住指令"的 handler——而不是现在这样挂到事件总线上、等指令结算完才响应。

> 背景：`CounterCard.HandleUseCounterAsync` 的实现本来就是用户的原始设计——**激活时在流程位点注册 handler、取消时注销、命中即消耗**。缺的不是机制，是"谁往 `_effectHandlers` 里装"：`CounterCard.AddEffectHandler` 在 `src` 内零调用者，数据侧 `effects` 全部走了被动效果的总线挂载。
>
> 上游：`docs/沙盒调试原语/requirements.md`（R6 反制链因本缺口只能锁到"激活成功"）。

## Scope

- Includes：
  - 反制卡装载时，把其数据效果的 handler 装配进 `CounterCard` 的效果 handler 集（**不挂主触发器／otherTriggers**）。
  - 激活 / 取消 / 一次性消耗三态下的注册与注销（沿用既有状态机，不改其语义）。
  - 命中语义：效果内 `ctx.Stop()` / `ctx.Interrupt()` 作用于**当前指令的流程层**。
  - 沙盒端到端：用调试原语造反制卡 → 激活 → 对手打指令 → 断言拦截与消耗。
- Excludes：
  - `CounterCard` 的激活/取消/消耗状态机本身（`_activeRegistrations` / `_chargedCost` / `_consumed` 逻辑不动）——**唯一例外**是"激活中被销毁"的清理（R2 末条：注销＋置未激活＋清实扣额＋不退费），它复用既有收口、不新增状态。
  - `OrderFlow` 的段序（Counter → Resolve → Finalize）与 `Stop`/`Interrupt` 语义。
  - 新增卡包字段或新的效果声明形态（沿用现有 `effects` 组件）。
  - 普通卡（单位/指令）的效果装载行为。
  - "反制卡在未激活时响应 `card.played`"——该行为被本需求**取代**。

## Constraints

- **内核改动优先为零**：若无需改 `Orc.Core` 即可达成 R3，则不改；确需改动时只允许加性且默认关闭。**本批实际改动**：`Orc`（非 `Orc.Core`）挂载链加一个布尔参数 `mountMainTriggers`（默认 `true`＝既有行为；只有反制卡装载路径传 `false`），跳过范围仅 `MountMainTrigger` 一行。
- **无双触发**：反制效果不得"既在 Counter 段拦截、又在总线响应"。
- **零回归**：既有 2310 项 ＋ 沙盒原语 56 项全绿；`P4OrderFlowTests` / `PlayChainCounterTests` 等程序化装配用例行为不变。
- **失败口径**：装载期装配失败（handler 取不到、类型不符）＝隔离记录、不阻断加载（沿用 `CardEffectLoader` 既有隔离口径），但必须在事件流留 Error 级痕迹。

## Requirement Items

### R1: 效果装配位点

- Status: Confirmed
- **Decision（用户 2026-10-21）**：反制是"**在合适的位置注册 handler、直接改流程**"，不是"监听信号"。因此反制卡的数据效果必须装配为 `Counter` 段 handler，而不是被动挂到总线。
- Scenario/Trigger: 装载一张 `category = Counter` 且带 `effects` 的卡。
- Behavior: 该效果的 handler 进入 `CounterCard` 的效果 handler 集（`AssembledEffectHandlerNames` 可观测，激活后经 `RegisteredEffectHandlerNames` 可观测），主触发器与 otherTriggers **不**在事件总线上注册（`injects` 显式指向总线触发器属作者显式声明，除外）。
- Acceptance:
  - [ ] 装载带一条效果的反制卡后，未激活时 `AssembledEffectHandlerNames.Count` ＝ 产出 handler 的效果数、`RegisteredEffectHandlerNames` 为空；激活后两者逐项相等（同序），取消后 `Registered` 复归为空而 `Assembled` 不变（handler 集已就绪但未注册到流程位点）。
  - [ ] 同一张卡在整个装载过程中，其效果的主触发器与 otherTriggers **没有**被挂到总线（以 `card.played` 不触发该效果为判据）；`injects` 显式指向某个总线触发器属作者显式声明，不计入本判据。
  - [ ] 装载失败（效果 handler 取不到）时，加载不中断，事件流有 Error 级记录，且该卡仍可正常打出/激活（效果缺席）。
- Terminology: [反制卡](../../CONTEXT.md), [触发器（Trigger）](../../CONTEXT.md)

### R2: 激活 / 取消 / 消耗三态

- Status: Confirmed
- Scenario/Trigger: 反制卡在使用态之间切换。
- Behavior: 激活＝把效果 handler 注册到 `OrderFlow.Trigger` 的 `Counter` 段；取消＝注销并退点；命中消耗＝注销、置未激活、从手牌移除并走弃置链。三态行为与现有 `HandleUseCounterAsync` 完全一致。
- Acceptance:
  - [ ] 激活后 `RegisteredEffectHandlerNames` 非空且条数＝**产出 handler 的效果数**（主触发器无普通事件的效果不产 handler、不计入）；再次调用（取消）后 `RegisteredEffectHandlerNames` 为空。
  - [ ] 取消后点数按实扣额退还（既有语义）。
  - [ ] 命中消耗后 `IsConsumed = true`、`IsActive = false`、`RegisteredEffectHandlerNames` 为空（`AssembledEffectHandlerNames` 保持装配态不变），且再次激活被拒。
  - [ ] 激活中被销毁（如满手爆牌）⇒ 注销全部 Counter 段 handler、置未激活、清实扣额记录、**不退费**；销毁后不可再激活。
- Terminology: [反制卡](../../CONTEXT.md)

### R3: 命中语义

- Status: Confirmed（用户选定 S1）
- **Decision（S1）**：效果内写 `ctx.Stop()` 即命中——停住**当前指令流程层**的剩余阶段；写 `ctx.Interrupt()` 为强反制。csx 作者无需理解"层"的概念。
- Scenario/Trigger: 对手打出指令卡，`OrderFlow` 进入 `Counter` 段。
- Behavior:
  - `ctx.Stop()` → 本次指令的 `Resolve` 段不执行（**不发 `card.played`**、主动效果不触发），`Finalize` 照常（扣费、离手）。
  - `ctx.Interrupt()` → 两阶段全停（牌留手、不扣费）。
  - 两者都不调 → 未命中，反制保持激活、不消耗。
- Acceptance:
  - [ ] Stop 用例：指令未离手前的 `card.played` 计数为 0、指令效果未触发、指令仍扣费并离手、反制卡已弃置（`card.discarded`）。
  - [ ] Interrupt 用例：指令留在手牌、点数未扣、反制卡已弃置。
  - [ ] 未命中用例：指令正常结算、反制卡仍在手牌且仍激活。
- Terminology: [指令卡](../../CONTEXT.md), [反制卡](../../CONTEXT.md)

### R4: 沙盒端到端

- Status: Confirmed
- Scenario/Trigger: 在无头沙盒里用调试原语验证反制链。
- Behavior: 造反制卡 → `sandbox.instantiate` 入手 → `match.end_turn` 切到持有方 → `play.counter` 激活 → `match.end_turn` 切对手 → 对手 `play.command` → 观测拦截。
- Acceptance:
  - [ ] `CounterChain_PrimitiveBuiltCounter_StopsOrder` 由现版本（只锁"激活成功＋指令照常结算"）升级为锁定 `Stop` ＋ `card.discarded`。
  - [ ] 原语摆局面段（`sandbox.instantiate` / `sandbox.points`）不改动。
- Terminology: [沙盒模式](../../CONTEXT.md)

### R5: 零回归

- Status: Confirmed
- Acceptance:
  - [ ] `dotnet test OrcEngine.slnx` 全绿（基线 2310 ＋ 沙盒原语 56 ＋ 本批新增）。
  - [ ] `P4OrderFlowTests` / `PlayChainCounterTests` 等程序化 `AddEffectHandler` 用例行为不变。
  - [ ] 非反制卡（单位/指令）的效果装载路径逐字节不变。
