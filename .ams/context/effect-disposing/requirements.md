# Requirements — 效果 disposing 触发器（显式销毁时机）

为效果引入与主触发器并列、默认必选的 **disposing 触发器**：把"效果如何销毁"从隐式卸载链/作者自清理中显式声明出来，支持提前调用与兜底调用，防止一次性效果泄漏。

> 状态：Confirmed（R1–R11 已对齐）

## Scope

- Includes:
  - 内核 `Effect` 生命周期的销毁语义与 disposing 触发器承载；
  - 提前调用（一次性效果）与兜底调用（防泄漏）的调用时机声明；
  - 手写 disposing 承载效果额外副作用的清理；核心句柄撤销由框架最终层保证；
  - 简单效果在触发器内置 `disposing` band 内注册销毁 handler 的入口；
  - 预制体/动态效果的数据面承载与既有 C# 效果的迁移；
  - 事件流留痕与失败隔离。
- Excludes:
  - 效果"触发"语义本身（主触发器的既有事件/band 机制不改）；
  - 修饰器期限（`EffectDuration`）的到期机制；
  - 卡牌销毁链本身（`card.destroyed` 的触发点与范围）；
  - UI/投影对销毁的表现。

## Constraints

- P2：更新不允许异步——销毁调用点返回即终态（同步/顺序语义），不得引入后台清理。
- 既有公共面以加性为主；破坏性变更需显式登记并迁移测试。
- 预制体 schema 升版时必须给出旧版读出的明确策略（拒绝或迁移，不静默降级）。

## Requirement Items

### R1: disposing 触发器形态与必选性
- Status: Confirmed
- Scenario/Trigger: 任一效果（被动/主动、C#/预制体/csx）被创建时。
- Behavior: 效果恰有一个 disposing 触发器（独立 `Trigger`，与业务主触发器并列）；未显式声明销毁逻辑时由框架提供默认实现（空逻辑 + 默认兜底），使"必选"不迫使既有效果改代码。C# 被动效果不再伪造业务主触发器（`MainTrigger` 为 null，审查链标注纯注入型）；动态效果保留业务主触发器，disposing 与其并列；主动效果无业务主触发器，disposing 照常挂总线接收兜底。
- Acceptance:
  - [ ] 每个效果实例均可取得其 disposing 触发器。
  - [ ] 未声明销毁逻辑的效果仍具备完整兜底销毁路径。
  - [ ] C# 被动效果的既有行为在零代码改动下保持（装载、触发、销毁）。
  - [ ] 主动效果的 disposing 触发器参与兜底挂载，业务施放触发器仍不挂载。

### R2: 销毁语义边界
- Status: Confirmed
- Scenario/Trigger: disposing 触发器被调用时。
- Behavior: **调用即完整销毁**——执行声明的销毁逻辑、把效果从宿主容器移除、恰发一次 `effect.removed`；重复调用幂等空转。销毁不是"仅逻辑清理后仍挂着一个已清理效果"。
- Acceptance:
  - [ ] 一次成功调用后，效果不再出现在 `Card.Effects` 中。
  - [ ] 一次成功调用恰发一次 `effect.removed`（载荷＝{Card, Effect}）。
  - [ ] 第二次调用不重复执行销毁逻辑、不重复发射。

### R3: 提前调用与重入保护
- Status: Confirmed
- Scenario/Trigger: 一次性效果在自身 handler 内提前销毁；或在销毁逻辑中再次触发销毁。
- Behavior: **进入即停用**——调用入口立即置"销毁中"，先卸主触发器并撤销注入（不再响应新信号），当前 in-flight 事件跑完；再执行销毁逻辑、移除容器、发射。重复/重入调用空转。**异常仍完成销毁**（隔离记录后照常移除＋发射，终态"已销毁"）。**主动效果同一路径**（无主触发器/注入可停用，其余相同）。
- Acceptance:
  - [ ] 销毁逻辑内的重复调用不再执行逻辑、不重复发射。
  - [ ] 销毁开始后，新的总线更新不会进入该效果的主触发器。
  - [ ] 销毁逻辑抛异常时，效果仍从容器移除且恰发一次 `effect.removed`。
  - [ ] 主动效果可经同一入口销毁并完成容器移除与发射。

### R4: 兜底调用时机声明
- Status: Confirmed
- Scenario/Trigger: 效果未提前自我销毁，但已失去存续条件（宿主销毁/对局结束等）。
- Behavior: 兜底时机＝**与主触发器 hooks 同构的更新名字符串集合**；disposing 触发器为 Passive 触发器，挂载到总线、owner＝效果，更新到达时经"针对本效果/宿主"过滤后调用销毁。提前调用＝直接调用该触发器。**兜底 hooks 为空时框架强制补 `card.destroyed`**（不可移除）；`effect.removed` 被拒绝用作兜底 hook（配置期 fail-fast）。`match.ended` 记为后续待办。**装载失败回滚不属于兜底**（不执行销毁逻辑、不发射）。显式销毁入口（RemoveEffect／effect.removed／card.destroyed 驱动）今后委托 disposing，不再直连旧清理模板。
- Acceptance:
  - [ ] disposing 触发器与主触发器使用同一 hooks 声明形态与挂载/卸载机制。
  - [ ] 未声明兜底 hooks 的效果仍随宿主销毁被兜底销毁。
  - [ ] 把 `effect.removed` 写进兜底 hooks 时，配置/装配期明确失败。
  - [ ] 无关卡的更新不会触发本效果的兜底销毁。
  - [ ] 装载失败回滚不产生 `effect.removed`、不执行销毁逻辑。
  - [ ] 兜底调用与提前调用走同一销毁路径（仅留痕来源不同）。

### R5: 注册注销责任迁移
- Status: Confirmed
- Scenario/Trigger: 效果装载期注册的注入 handler、修饰器、光环等落地物；以及效果自身的额外副作用。
- Behavior: **双层责任**——①**核心句柄撤销必须执行**（注入登记、托管清理等由框架在销毁终态执行，幂等、不可绕过，不依赖 disposing 是否完整）；②**手写 disposing 只处理效果的额外副作用**（自建监听、延迟调度、临时数据等），可提前、可部分执行。两者使用同一批幂等注销 API，disposing 已完成的项在最终层自然 no-op。
- Acceptance:
  - [ ] disposing 逻辑为空/早退/抛异常时，核心句柄撤销仍完成。
  - [ ] disposing 中提前执行的注销，在最终层不重复生效、不报错。
  - [ ] 最终层在 disposing 之后、容器移除之前执行。

### R6: 销毁 band（默认最后、可前置、不可覆写）
- Status: Confirmed
- Scenario/Trigger: 明确只生效一次的简单效果；或需要把销毁 handler 排在业务 handler 之前/之后的特殊效果。
- Behavior: 效果触发器内置一个**保留 band**专门承载销毁 handler；该 band 默认位于最后（全部业务 band 之后），有特殊需要可由作者把销毁 handler 注册到普通 band 以获得更早位置；作者不得覆写保留 band 的定义/位置/值。**空保留 band 不改变常驻效果的存活语义**（没有注册就不销毁）。
- Acceptance:
  - [ ] 保留 band 在触发器构造期即存在，无需作者声明。
  - [ ] 默认顺序：全部业务 band 之后才执行销毁 handler。
  - [ ] 作者可把销毁 handler 注册到普通 band，使其先于部分/全部业务 handler 执行。
  - [ ] 作者无法重定义/覆盖保留 band 的值或顺序。
  - [ ] 未在该 band 注册销毁 handler 的效果不因该 band 被销毁。
  - [ ] 保留 band 只接受销毁 handler（普通业务事件注册被拒绝）。
  - [ ] 声明 `OneShot` 的效果由框架在装载期自动向保留 band 注册"调用 disposing"。
  - [ ] 保留 band 在声明了专门 band 方案的触发器上同样存在（内部保留槽，不改变现有二选一校验）。
  - [ ] `OneShot` 仅在有业务主触发器时可用（无主触发器声明＝装配期失败）；主动效果 `OneShot` ＝施放完成即销毁。

### R7: 数据面承载
- Status: Confirmed
- Scenario/Trigger: 效果预制体/快照声明 disposing 触发器、兜底 hooks、OneShot 与保留 band 内的销毁 handler。
- Behavior: 快照新增 `root.disposingTrigger`（与 `mainTrigger` 同构：`hooks`＝兜底时机、`events`＝销毁逻辑）与 `root.oneShot`（效果级）；`EventPrefab` 新增 `band` 字段，本批只接受 `"default"`（前置/同区段）与 `"disposing"`（保留 band，最后）。schema 升 **3**；读入接受 2（缺 `disposingTrigger` ＝空逻辑＋默认 `card.destroyed` 兜底，缺 `oneShot` ＝ false），写出恒为 3。预制体本批不支持任意专门 band 名称，代码侧可声明任意专门 band。
- Acceptance:
  - [ ] 快照可往返导出/读回 disposingTrigger、oneShot 与 band 字段。
  - [ ] schema 2 的快照可读入并按默认值补齐，写出为 3。
  - [ ] 非法 band 值（非 default/disposing）读入时明确失败。
  - [ ] 审查链/装载计划能识别 disposing 触发器与保留 band 步骤。

### R8: 既有生命周期触发器的收敛
- Status: Confirmed
- Scenario/Trigger: `PassiveEffect` 现持有的 `_lifecycleTrigger`（hooks＝effect.removed/card.destroyed）。
- Behavior: 生命周期触发器被 disposing 触发器取代（不再作为主触发器存在）；其信号过滤与"各入口收敛到同一清理模板"的逻辑移入 disposing 的框架事件。动态被动效果的业务主触发器保留并与 disposing 并列；主动效果的施放触发器不挂载、disposing 挂载以接收兜底。
- Acceptance:
  - [ ] 销毁只有一条路径：所有移除/销毁信号最终都进入 disposing 触发器。
  - [ ] `card.destroyed` 到达时不会出现旧清理模板与新销毁路径各执行一次。
  - [ ] 动态效果的业务主触发器挂载/卸载行为不变。

### R9: 失败与异常语义
- Status: Confirmed
- Scenario/Trigger: 销毁逻辑抛异常、提前 return、最终层撤销异常、重复调用。
- Behavior: 销毁逻辑异常＝Error 记录、隔离、仍完成移除与发射；最终层句柄撤销异常＝Error 记录、继续完成移除与发射、不重试；**早退不可判别，不提供"销毁完成度"标记**（框架只保证核心句柄撤销必达，业务副作用由作者自负）；重复/重入调用空转并写 Debug。
- Acceptance:
  - [ ] 销毁逻辑异常后效果仍被移除且恰发一次 `effect.removed`。
  - [ ] 最终层撤销异常不阻止容器移除与发射。
  - [ ] 重复调用只产生 Debug 留痕，不产生 Error。
  - [ ] 不暴露任何"未完成/完成度"状态。

### R10: 可观测性
- Status: Confirmed
- Scenario/Trigger: 销毁被提前/兜底调用。
- Behavior: 公开只读销毁状态（`Mounted`／`Disposing`／`Disposed`）；销毁开始、完成各写一条 Info；重复/重入写 Debug；失败写 Error；关键词以 `dispose:early`／`dispose:fallback`／`dispose:signal` 区分调用来源。**偏差记录**：`dispose:fallback` 未附更新名——总线 Emit 契约不向订阅者注入 updateType（框架不注入保留键），如需附名需扩展 Bus 契约，记为后续待办。
- Acceptance:
  - [ ] 销毁前/中/后读到的状态依次为 Mounted／Disposing／Disposed。
  - [ ] 提前调用与兜底调用在事件流关键词上可区分。
  - [ ] 重复调用可经 Debug 条目排查，且不产生新发射。

### R11: 兼容与迁移
- Status: Confirmed
- Scenario/Trigger: 既有 C# 效果、动态效果、预制体与测试。
- Behavior: 允许破坏性整理，不接受旧隐式清理路径与新 disposing 路径并存。具体：`OnUnmount` 保留但改为 disposing 触发器内置的默认作者事件（调用点只有 disposing）；`ExecuteUnmount` → `ExecuteDispose`；`AddUnmountCleanup` → `AddTeardownRegistration`；`CleanupEffect` 删除并入 disposing 入口；`PassiveEffect._lifecycleTrigger` 删除。既有 C# 效果代码零改动；游戏层与测试随上述改名/删除迁移。
- Acceptance:
  - [ ] 旧隐式清理路径全部退场，销毁只经 disposing。
  - [ ] 既有 C# 效果源码不改即可编译并通过原有行为断言。
  - [ ] 游戏层与测试完成迁移，构建与测试全绿。
