# Implementation — 效果 disposing 触发器

实现完成：内核 `Trigger` 保留槽、`Effect` 销毁状态机、销毁入口收敛、动态/预制体数据面与游戏层迁移均已落地；`Orc.Tests` 304 全绿、`Orc.Game.Tests` 1132 全绿。

## Scope

- Target:
  - `src/Orc/Core/Trigger.cs`（保留 band 槽、注册面、排序、审查面）
  - `src/Orc/Core/Context.cs`（静默停止）
  - `src/Orc/Core/Orchestration.cs`（disposing 节点进入审查图）
  - `src/Orc/Cards/Effect.cs`（状态机、disposing 触发器、teardown 分层）
  - `src/Orc/Cards/CardLifecycle.cs`（销毁入口收敛、生命周期触发器退场）
  - `src/Orc/Cards/Card.cs`（Add/Remove/静默回滚）
  - `src/Orc/Cards/DynamicEffect.cs`（业务主触发器与 disposing 并列、OneShot）
  - `src/Orc/Cards/Prefabs.cs` / `PrefabJson.cs`（数据面、schema 3）
  - `src/Orc.Game/Cards/EffectSystem.cs`（托管清理改名与接线）
  - `Orc.Tests` / `Orc.Game.Tests`（迁移与新增）
- Excludes:
  - 修饰器期限机制；
  - 卡牌销毁链本身的触发点；
  - UI 投影；
  - `match.ended` 更新（后续待办）。

## Implementation Steps

### S1: Trigger 保留 band 槽
- Status: Done
- Target: `src/Orc/Core/Trigger.cs`
- Approach: `EventEntry.IsDisposalSlot`；`RegisterDisposal`；排序键 `(IsDisposalSlot, SortKey, Seq)`；`TriggerEventInfo.IsDisposalSlot`；撤销复用 `Unregister`。
- Acceptance:
  - [x] 默认方案与专门方案下均可注册保留槽事件。
  - [x] 保留槽事件恒在全部普通事件之后执行。
  - [x] 普通 `Register` 无法写入保留槽。
  - [x] `Unregister` 可撤销保留槽注册且幂等。
  - [x] 保留槽内多事件按优先级＋注册序执行。
- Rationale: 不动既有 band 二选一校验与上限约束，保留槽"默认存在且不可覆写"。
- Terminology: Band、事件边界

### S2: Effect 销毁状态机与 disposing 触发器
- Status: Done
- Target: `src/Orc/Cards/Effect.cs`
- Approach: `DisposalState { NotMounted, Mounted, Disposing, Disposed }`；`DisposalSource { Early, Fallback, Signal }`；`DisposingTrigger` 固定 `Trigger<CardEventView>`，内建"守卫／兜底驱动／默认销毁"三事件；`ExecuteDispose` 进入即停用（`UnmountOwner`＋撤销注入）→ 事件链 → 最终 teardown → 容器移除 → 发射；`AddTeardownRegistration` 承载核心撤销；`OneShot` 装配期校验；复装复位。
- Acceptance:
  - [x] 状态依次 NotMounted→Mounted→Disposing→Disposed，复装回 Mounted。
  - [x] 重复/重入调用空转（Debug 留痕）。
  - [x] 异常仍完成最终 teardown、移除与发射。
  - [x] 守卫静默过滤无关卡更新（`dispose:fallback` 但只命中宿主）。
  - [x] 事件流关键词区分 `dispose:early` / `dispose:fallback` / `dispose:signal`。
- Rationale: 销毁单一入口、单一状态机。
- Terminology: 卸载、注入

### S3: 销毁入口收敛
- Status: Done
- Target: `src/Orc/Cards/CardLifecycle.cs`、`src/Orc/Cards/Card.cs`
- Approach: `CleanupEffect` 删除并并入 `DisposeEffect`；`RemoveEffect` → `DisposeEffect(Early)`；`RemoveEffectSilently` 对已装载走静默销毁、未装载仅容器面；`effect.removed` 驱动为 `Signal` 幂等兜底；`card.destroyed` 处理器只做 `UnregisterCard`，批量销毁由各 disposing 默认兜底完成。
- Acceptance:
  - [x] 所有销毁路径只触发一次 disposing。
  - [x] 装载失败不执行 disposing、不发 `effect.removed`。
  - [x] `card.destroyed` 批量销毁逐效果恰发一次 `effect.removed`。
  - [x] `effect.removed` 驱动对已 Disposing/Disposed 效果静默跳过。
  - [x] 未装载效果的 `RemoveEffect` 仅容器面（detach 留痕）。
- Rationale: 消除新旧双轨。
- Terminology: 驱动信号

### S4: 动态效果与 OneShot
- Status: Done
- Target: `src/Orc/Cards/DynamicEffect.cs`
- Approach: 业务主触发器保留并与 disposing 并列；纯注入型＋OneShot 实例化失败（`one-shot-no-trigger`）；OneShot 在构造期向主触发器保留槽注册销毁调用（被动/主动统一）；disposing 事件以普通 `Register` 注册（执行在默认 OnUnmount 之前）；`MainTrigger` 对动态被动效果返回业务触发器。
- Acceptance:
  - [x] 动态被动效果业务触发器与 disposing 均随 owner 卸载。
  - [x] 纯注入型动态效果无 OneShot 时正常挂兜底。
  - [x] 纯注入型 + OneShot 实例化失败且不装配。
  - [x] 主动效果 OneShot 在施放后销毁。
- Rationale: OneShot 只在有确定"一次触发"时成立。
- Terminology: 主触发器

### S5: 预制体数据面与 schema 3
- Status: Done
- Target: `src/Orc/Cards/Prefabs.cs`、`src/Orc/Cards/PrefabJson.cs`
- Approach: `EffectPrefab.DisposingTrigger`（缺省归一化为默认 card.destroyed 兜底）与 `OneShot`；`EventPrefab.Band ∈ {default, disposing}`（`disposing` 经 `TriggerReflection.RegisterDisposalHandler` 落入保留槽）；`EffectSnapshot.SchemaVersion = 3`；读入接受 2 并归一化为 3、写出恒 3；`BuildLoadPlan` 新增 `EffectLoadStepKind.Dispose`。
- Acceptance:
  - [x] 快照往返保真（含 disposingTrigger/oneShot/band）。
  - [x] schema 2 读入补默认且写出 3。
  - [x] 非法 band 值明确失败。
  - [x] 未声明 disposingTrigger 的领域对象仍有默认 disposing。
- Rationale: 领域不变量在构造期成立，序列化入口只做版本容忍。
- Terminology: 效果快照、模板效果

### S6: 游戏层迁移
- Status: Done
- Target: `src/Orc.Game/Cards/EffectSystem.cs` 及相关调用点
- Approach: `AddUnmountCleanup`→`AddTeardownRegistration`；`ExecuteUnmount`→`ExecuteDispose`；`CleanupEffect` 调用点改 `DisposeEffect`；`OnUnmount` 作者代码不变。
- Acceptance:
  - [x] 解决方案（Orc/Orc.Game/测试项目）构建零错误。
  - [x] 既有 C# 效果源码零改动。
- Rationale: 破坏性整理限定在生命周期内部面。
- Terminology: 托管清理

### S7: 测试迁移与新增
- Status: Done
- Target: `tests/Orc.Tests`、`tests/Orc.Game.Tests`
- Approach: 迁移订阅者/留痕断言（`EffectRemoved`→`CardDestroyed`＋`{name}/dispose`、schema 2→3、装载计划新增 Dispose）；新增 `EffectDisposalTests`（显式销毁/幂等/异常/兜底/误伤隔离/复装/状态）与 `EffectDisposalGameTests`（schema 2 归一化、非法 band、OneShot、disposing 事件往返与执行、保留 band 顺序）。
- Acceptance:
  - [x] `Orc.Tests` 304 全绿。
  - [x] `Orc.Game.Tests` 1132 全绿。
  - [x] 新增用例覆盖 R2/R3/R4/R6/R7/R9/R10 的验收项。
- Rationale: 以行为断言锁住新语义。
- Terminology: 事件流
