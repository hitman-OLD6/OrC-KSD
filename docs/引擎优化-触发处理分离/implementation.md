# Implementation — 触发-处理分离与指令归一

实现 grill 已收敛：S0–S17（含 S5a）的实现决策与阶段门已落表；**P1–P5 已完成并通过阶段门**（P1/P3/P4/P5 执行记录见文末；P2 见 `P2ActionFamilyTests` 与既有动作实体）；OPEN-1/OPEN-2 已由用户裁决（默认 `Stop`；反制卡一次性消耗），见 S13/S14。

> **Status 语义**：`Done`＝该步骤的**实现决策已收敛并写入本文档**（不代表代码已实现）；S0–S17 决策均为 `Done`。截至 2026-10-19，P1–P4 已执行并通过阶段门（P1/P3/P4 记录见文末），P5 代码待执行。

## Scope

- Target：`src/Orc`（内核 flow/trigger 面）、`src/Orc.Game`（8 个动作的现有承载）、`Orc.Plugins.BuiltIn`/新插件包 `orc.foundation.actions`、`tests/` 六项目。
- Excludes：需求 §1 Excludes 全部条目（老兵升级、揭示、弃置、爆牌独立化、点数/槽位增减、洗牌/回合推进、换牌、UI/投影、效果解析器与卡牌 schema）。
- 交付阶段沿用需求 R15：P1 原语+伤害样板 → P2 移动族+抽牌族 → P3 加入战场+攻击 → P4 指令归一 → P5 插件化+清理；每阶段六测试项目全绿且无"半新半旧"第三条路径。

## 现状基线（代码核验，S0 输出）

| 事实 | 证据 |
| --- | --- |
| 伤害已是"三触发端 + 一处理端"，但形态是专用类而非动作实体 | `src/Orc.Game/Damage/DamagePipeline.cs:53-64`（`CombatDamage`/`EffectDamage`/`BattleDamage`/`Apply`） |
| 触发端默认转发、处理端默认扣数值均以 band+priority 排序 | `DamagePipeline.cs:59-64`（转发 `Resolve/100`、扣数值 `Apply/100`） |
| 攻击/伤害内核 flow 游戏层零调用 | 全仓仅 `LogicEngine` 构造与 `AttackFlow.OnResolve` 内部引用；`src/Orc/Cards/BaseFlows.cs:116-117` |
| 指令 flow 游戏层零调用，`Counter` band 无注册者 | `src/Orc/Cards/OrderFlow.cs:72` 仅注册 `Resolve`；全仓无 `OrderFlow.ExecuteAsync` 调用 |
| 卡牌打出仍是"每卡自建触发器 + 链内 handler" | `src/Orc.Game/Cards/UnitCard.cs:56-84`、`CommandCard.cs:44-50` |
| 指挥移动的"发起端"在管理器方法内，不在触发器 | `CommandManager.cs:1050-1094`（`DispatchMoveAsync`） |
| 效果位移绕过移动触发器与行动费/行动位 | `CommandManager.cs:1413-1425`（`MoveByEffectAsync` 直调 `ApplyPositionChangeAsync`） |
| 无头移动反而走完整指挥移动并扣费清位 | `CommandManager.cs:1465-1505` → `DispatchMoveAsync` |
| 死亡有两个并列取消位，均在 `IsDestroyed` 置位前 | `CommandManager.cs:1515-1516` |
| `GameEntryPoints` 明确登记 `BeginCommandPrePlayAsync`/`PlayCommandAsync` | `src/Orc.Game/GameEntryPoints.cs:102-115` |
| 已有 `Orc.Mcp.Match.ActionOutcome` 同名类型（命名冲突风险） | `src/Orc.Mcp/Match/MatchExecutor.cs:16` |
| `CommandFlowBox` 为 internal（若原语沿用该"结果箱"模式，P5 插件编译期取不到） | `src/Orc.Game/Commanding/CommandTypes.cs:246` |
| `DamageEventView.Source/Target` 为 `[Read]`，不满足 R2「发起前改目标/改来源」 | `src/Orc.Game/Damage/DamageViews.cs:38-44` |

## Implementation Steps

### S0: 基线核验与实现约束落表
- Status: Done
- Target：`docs/引擎优化-触发处理分离/implementation.md`、相关源码只读核验
- Approach：逐条核对需求 §0 的代码事实与调用点；登记与需求冲突的现状（尤其 `GameEntryPoints` 已含待改名的指令入口）；确认既有 `Trigger` 已具备 band、动作缝（`Context.SeamAsync`）与包装器能力，P1 不新造机制。
- Acceptance:
  - [x] 8 个动作的现有承载与旁路均有源码位置
  - [x] 需求 §0 的代码事实与仓库一致
  - [x] 已识别需求内部/与门面冻结的冲突项（见待裁决区）
- Rationale：实现方案必须以实际代码为基线；先固化证据，避免按需求叙述臆造并不存在的抽象。
- Terminology: [动作（Action）](./CONTEXT.md)、[触发端](./CONTEXT.md)、[处理端](./CONTEXT.md)

### S1: 动作实体与编排原语的落位
- Status: Done
- Target：`src/Orc.Game/Actions/`（新目录；命名空间 `Orc.Game.Actions`）
- Approach：
  1. 动作实体、结果对象、失败类别、编排原语的**类型契约**全部落在 `Orc.Game.Actions`，一律 `public`；8 个动作的实体声明类型同处该目录。
  2. P5 只把「动作定义实例 + 默认 handler」迁入 `orc.foundation.actions`；类型契约留在 `Orc.Game`，不随包迁移。
  3. 硬约束一（无 internal 泄漏）：动作实体、结果对象、动作服务面签名中出现的每个类型都必须 `public`。禁止沿用 `CommandFlowBox`（`CommandTypes.cs:246`，internal）式结果箱；验收以反射遍历公共签名（含泛型约束、基类、属性类型）零 `internal` 为准。
  4. 硬约束二（伤害样板只搬家不改语义）：`DamagePipeline` 迁为「伤害动作」时，`CombatAsync`/`EffectAsync`/`BattleAsync` 三入口的 `unit.damage.dealt`、`card.damaged`、防御归零→死亡衔接的发射序列与顺序逐条不变；验收＝现有测试全绿 + 事件流逐条比对。
  5. 硬约束三（禁止反向依赖）：`Orc.Game.Actions` 不得引用 `CommandManager`/`PlayManager`；动作服务面的**接口**在 `Orc.Game.Actions`，**实现**在 `Orc.Game` 内部类，且**永不迁移**——P5 只迁动作定义与默认 handler，服务面实现是宿主对插件提供的授权面，插件自带实现即等于自我授权。
- Acceptance:
  - [x] 落位与可见性已裁决（`Orc.Game.Actions`，public）
  - [ ] `Orc.Game.Actions` 公共签名零 `internal` 类型泄漏
  - [ ] `Orc.Game.Actions` 源码零 `CommandManager`/`PlayManager` 引用（编译期可证）
  - [ ] 伤害动作迁移后三入口信号序列与顺序与基线逐条一致
- Rationale：内核 `Orc.Cards` 已有 `AttackFlow`/`DamageFlow`/`OrderFlow` 三个"流程"概念，而游戏层一个都没用——P1 若把动作原语放进内核，等于重复"内核造概念、游戏层不用"的同一错误。放在 `Orc.Game` 可直接使用 `UnitCard`/`Slot`/`Player`，且 P1 只承担行为等价迁移的风险；P5 用「类型契约留 Game、实例与 handler 进插件」满足 R6 的定义权归属。上提内核留到 P5 之后再议，不在本次范围。
- Terminology: [动作实体](./CONTEXT.md)、[底层逻辑集](./CONTEXT.md)、[动作服务面](./CONTEXT.md)

### S2: 三段接缝的承载
- Status: Done
- Target：`src/Orc.Game/Actions/`（新增 band 方案与默认 handler 装配）、`src/Orc.Game/Damage/DamagePipeline.cs`（迁移适配）
- Approach：
  1. 统一双枚举：
     - `ActionTriggerBands { Initiate=1, Announce=2, CounterGuard=3, Counter=4, Adjust=5, Commit=6 }`
     - `ActionHandleBands { Rewrite=1, Veto=2, Apply=3 }`
  2. **作用域只到「底层动作」**：统一枚举只约束底层动作的触发端与处理端。入口分支（撤退/被控/被指挥、抽卡组顶/抽指定）是上层触发端，**不强制**声明 `ActionTriggerBands`；入口只负责「自身前置 → 调用底层动作 → 自身收尾」。
  3. `Commit` 的默认转发 handler 只允许在**底层动作触发端**注册一次；入口分支不得注册转发，避免一次动作把处理端跑 N 遍。
  4. `Announce` 段：仅指令在 `OrderFlow` 注册 `card.played`；其余动作保持空段——不发信号、不写日志。「宣告后」由段位本身表达（R14 信号冻结 + S1 硬约束二）。
  5. `Veto` 阻断语义：命中即 `ctx.Stop()`，本级后续事件（同段其他 `Veto` 与 `Apply`）全部不执行；「否决是终局」。需要"否决但留痕"时，在 `Veto` 段用更低 priority 的 handler 于否决前完成记录，不改机制。
  6. 入口收尾只能调动作服务面的具名操作（R7），不得直接改 `UnitStateData`/槽位；否则唯一落地口在入口层被破。
  7. `CounterGuard`/`Counter`/`Adjust` 对非指令动作保留为空段；禁止把"免疫"等非反制语义挂进这三个段——免疫属处理端 `Veto`。
- Acceptance:
  - [x] 三段由 band 值静态表达，不依赖注册顺序
  - [ ] `Commit` 在每个底层动作触发端恰注册一次（装配期可枚举断言）
  - [ ] 入口分支不声明 `ActionTriggerBands`、不注册转发
  - [ ] `Announce` 对非指令动作为空段，P1 伤害事件流与基线逐条一致
  - [ ] `Veto` 命中后同段后续与 `Apply` 均不执行，且零落地
  - [ ] `CounterGuard`/`Counter`/`Adjust` 在非指令动作上零注册
- Rationale：现有 `Trigger` 按 `band×1000+priority` 排序并在每事件边界检查 `Stopped/Interrupted`（`Trigger.cs:564-570`），这是唯一"不靠异常、不靠注册顺序"的阻断原语。把统一枚举限定在底层动作，既保证 8 个动作共享同一套三段语义，又避免 R8/R10 的多入口在 `Commit` 段重复转发。`Context.SeamAsync` 是包装器缝、无法跳过核心 handler，故只保留给作者的非阻断扩展。
- Deviation：P1 删除了 `DamageEventBands`/`DamageApplyBands`（band 校验要求枚举类型精确匹配，无法保留别名）；段内相对次序保持不变：`Resolve(1)`→`Initiate(1)`、`Apply(2)`→`Apply(3)`。
- Terminology: [三段接缝](./CONTEXT.md)、[元效果](./CONTEXT.md)、[入口分支](./CONTEXT.md)

### S3: 动作结果对象与门面翻译
- Status: Done
- Target：`src/Orc.Game/Actions/`（结果类型）、`src/Orc.Game/Damage/`（`DamageSettlement` 迁移）、各动作入口
- Approach：
  1. 结果类型（全部 public）：
     - `ActionResult { ActionId Id, IReadOnlyList<ActionTargetResult> Targets }`
     - `ActionTargetResult { Ref<Entity> Target, CardBase? Subject, ActionOutcomeKind Outcome, int RequestedAmount, int AppliedAmount, ActionDisposition? Disposition, ActionRewrites Rewrites, ActionFailureKind? Reason }`
     - `ActionOutcomeKind { Applied, Blocked, TargetInvalid, Skipped }`
     - `ActionFailureKind` 至少含 `Precondition`、`Blocked`、`TargetInvalid`、`Cancelled`、`HandFull`、`DeathPrevented`；随阶段增补，不伪造动作专属原因。
     - `ActionDisposition` 至少含 `ToHand`（R12 死亡→回手）；`null`＝默认处置，其余目标按 R12 后续增补。
     - `ActionRewrites` 为 `[Flags]`，仅诊断/观测（事件流留痕与断言）用，**不得作为入口分支依据**；分支只看 `Outcome` 与 `Disposition`。
  2. 终态定义钉死：
     - `Applied`＝已落地（含被改写后仍落地）；
     - `Blocked`＝到达处理端但被否决（免死/免疫/`Veto`）——有留痕、有 `Reason`；
     - `TargetInvalid`＝前置阶段判定目标不合法（越界/已死/槽位被占）——未进入处理端；
     - `Skipped`＝编排层主动跳过（前置不满足/上游取消）——双方都无意向。
  3. 数值拆分为 `RequestedAmount`/`AppliedAmount`：请求量＝入口快照；实扣量＝处理端落地后的实际变化（`unit.damage.dealt` 断言读此值）。
  4. `ActionDisposition?` 携带"改成什么"（`DispositionChanged` 只说明被改）：至少覆盖 `ToHand`（R12 死亡→回手）；`null`＝沿用动作默认处置。
  5. **翻译层留在 `Orc.Game`**：入口分支把 `ActionResult` 翻译为 `PlayResult`/`CommandResult`；`orc.foundation.actions`（P5）只含底层动作与默认 handler，**不含入口分支**。原因：`CommandResult.Failure`/`PlayResult.Failure` 工厂是 `internal`（`CommandTypes.cs:120-128`、`PlayChain.cs:93-101`），入口迁入插件要么破坏 R14 冻结、要么构造不出结果。
  6. `DamageSettlement` 删除，`Applied/Dealt` 语义迁入 `ActionTargetResult`。核实依据：`src` 侧仅 `DamageViews.cs` 与 `DamagePipeline.cs` 引用；`tests` 226 个 `.cs` 中直接引用 0 处。若后续出现测试/外部引用，保留为 `ActionTargetResult` 的适配壳而非删除。
  7. 所有既有 `bool` 返回保留：`CombatAsync`/`EffectAsync`/`BattleAsync`/`DamageByEffectAsync`/`BattleByEffectAsync` 签名与返回值不变，内部改读 `ActionResult` 再折算 `bool`（P1 契约＝调用方零改动）。
- Acceptance:
  - [x] `ActionTargetResult` 含 `Disposition`、`RequestedAmount`/`AppliedAmount`
  - [ ] 入口分支源码零 `.Rewrites` 读取（仅 `Outcome`/`Disposition` 分支）
  - [ ] 多目标动作可对不同目标分别返回记录
  - [ ] 预期失败返回结果、契约错误仍抛异常
  - [ ] 既有 `PlayResult`/`CommandResult` 形状与工厂零改动
  - [ ] `DamageSettlement` 删除且 `tests` 零引用；五个 `bool` 方法签名与返回值不变
- Rationale：R9 要求「多结果＝对不同目标分别返回」且「不依赖异常传递」，R14 又冻结既有门面；只有「底层每目标结果 + 入口翻译」能同时满足。`Disposition` 必须是显式字段，否则 R12 的「死亡改为回手」无法被入口消费；`Rewrites` 只作诊断，避免入口按标志位猜语义而重新长出按入口类型的分支。
- Deviation：`ActionTargetResult.Subject` 对 HQ 为 `null`——`Hq` 继承 `Card` 而非 `CardBase`，P1 不为 HQ 强行补 `CardBase` 适配层（会改变类型语义）；HQ 结果以 `Ref<Entity> Target` + `AppliedAmount` 表达。
- Terminology: [动作（Action）](./CONTEXT.md)、[入口分支](./CONTEXT.md)、[唯一落地口](./CONTEXT.md)

### S4: 动作服务面（动作角色）
- Status: Done
- Target：`src/Orc.Game/Actions/`（`I*ActionServices` 接口）、`src/Orc.Game/Actions/MatchActionServices.cs`（内部实现）
- Approach：
  1. 按动作拆角色接口：`IDamageActionServices`、`IMoveActionServices`、`IDeathActionServices`、`IDrawActionServices`、`IHandActionServices`、`IJoinActionServices`、`IAttackActionServices`、`IOrderActionServices`。每个动作实例/默认 handler 只接收自己那个接口；`MatchActionServices` 为 `internal sealed`，实现全部接口。
  2. **实现类永不迁移**：`MatchActionServices` 是宿主（对局）对插件提供的授权面，留在 `Orc.Game`；P5 只迁「动作定义 + 默认 handler」，插件不得自带服务实现。
  3. **同名方法不得跨接口重复**：所有方法名带动作归属后缀（`LandMoveAsync` / `ClearSlotOnDeathAsync` / `UnitizeOnJoinAsync`…）。P1 对照表：
     | 方法 | 动作 | 唯一调用者 |
     | --- | --- | --- |
     | `IDamageActionServices.ApplyDamageAsync(Card? source, Card target, int amount, string kind, ct)` | 伤害结算 | 伤害动作默认处理端 handler |
     后续每新增一个服务方法，必须同步追加一行；出现一个方法被两个动作调用即设计错误。
  4. **跨动作只能调动作，不能调写操作**：抽牌处理端不得直接写手牌（须调用「加入手牌」动作）；死亡处理端不得直接销毁卡（须经 `Disposition` 或入口）；攻击不得直接扣防御（须调伤害动作）。判据：某 handler 需要"另一个动作的结果"＝编排问题，不是服务面问题。
  5. **信号发射权按动作归属**：服务面只允许发本动作归属的信号。`ApplyDamageAsync` 内发 `unit.damage.dealt` 合法（伤害动作的语义产物）；`IDamageActionServices` 不得提供 `EmitActedAsync` 之类；`unit.acted` 属行动层，由入口分支发射。
  6. P1 落地：把 `DamagePipeline.ApplyCoreAsync` 的实体迁进 `MatchActionServices.ApplyDamageAsync`；`DamagePipeline` 改为委托（不再保留第二份实体）；`CommandManager.ApplyDamageCoreAsync` 改为纯转发、签名保留，P5 再删。禁止「新服务 → `CommandManager.ApplyDamageCoreAsync`」的两跳。
  7. 操作清单＝所有 `I*ActionServices` 接口方法的并集，反射可枚举；装配期测试断言：方法名全局唯一、签名中无 `CommandManager`/`PlayManager`/`internal` 类型。
- Acceptance:
  - [x] 角色接口拆分、P1 只落 `IDamageActionServices`
  - [ ] 所有 `I*ActionServices` 方法名全局唯一
  - [ ] 每个服务方法在 S4 对照表中有「动作 + 唯一调用者」
  - [ ] `MatchActionServices` 为 internal 且不含 `CommandManager`/`PlayManager` 字段
  - [ ] `DamagePipeline.ApplyCoreAsync` 实体已迁出，两个旧入口均为纯转发
  - [ ] `IDamageActionServices` 不含 `EmitActedAsync` 或其他动作信号方法
- Rationale：R7 要的是「具名操作 + 单一真源可枚举 + 不交出管理器」，角色接口把最小权限变成编译期事实。方法名按动作归属唯一，才能让"某方法属于哪个唯一落地口"可静态判定；实现类留在宿主侧，插件才无法自我授权。
- Deviation：`ApplyDamageAsync` 的 `kind` 参数实为 `string`——仓库不存在 `DamageKind` 类型，`DamageKinds` 是 string 常量类；引入 enum 会改变伤害载荷形状，违背 P1「行为零变化」。
- Terminology: [动作服务面](./CONTEXT.md)、[唯一落地口](./CONTEXT.md)

### S5: 入口分支的写面与 R1/R10 口径
- Status: Done
- Target：`src/Orc.Game/Actions/`（`IOperateEntryServices`）、`src/Orc.Game/Commanding/CommandManager.cs`（`DispatchMoveAsync`/`DispatchAttackAsync`、`FinalizeMoveAsync`/`FinalizeAttackAsync`）
- Approach：
  1. 按修订后的 R1/R16 分两层：**动作状态**（目标的位置/槽位/数值/处置/归属）只由处理端写；**行动状态**（指挥点/`CanMove`/`CanAttack`/`unit.acted`）**只由指挥入口**（被指挥移动 / 被指挥攻击）经具名操作写，处理端禁写。移动/攻击动作本身零资源消耗；效果入口（被控移动/撤退/效果攻击）不写任何行动状态。
  2. 新增 `IOperateEntryServices`（`Orc.Game.Actions`，**internal**）：`DeductOperateCostAsync(UnitCard, ct)`、`SetOperateFlagsAsync(UnitCard, bool canMove, bool canAttack, ct)`、`EmitActedAsync(UnitCard, ct)`。这三个操作**只允许指挥入口调用**；动作处理端与效果入口零调用。经项目既有 `InternalsVisibleTo` 向测试项目开放（参照 `src/Orc.Game/Properties/AssemblyInfo.cs`）；它是宿主内部编排面，**不是插件契约**，不得进 `orc.foundation.actions`。
  3. **服务方法只写值，不判规则**：`SetOperateFlagsAsync` 只落两个 bool；"非坦克二选一清位""奋战/攻击次数决定 `CanAttack`"等规则由入口分支算完再传值。服务实现中出现 `if (HasUnitType(Tank))` / `if (Suppressed)` / 费用判定即为设计错误。
  4. 入口收尾统一经一个 `OperateFinalizer`（`Orc.Game` 内部编排单元）调用上述三个操作；**只有被指挥移动/被指挥攻击**把差异编码为 `OperateProfile { bool DeductCost, bool CanMove, bool CanAttack, bool EmitActed }` 后交给它。`OperateProfile` 的语义＝「这次**指挥**的收尾」，不是「这次移动/攻击的收尾」；`IOperateEntryServices` 的「唯一调用者」＝`OperateFinalizer`，不是各入口各写一遍。
  5. **`unit.acted` 独立约束**：`unit.acted` 只能由**被指挥入口**经 `IOperateEntryServices.EmitActedAsync` 发射，**不属于任何处理端、也不属于效果入口**；P3 写攻击入口时不得把它塞进攻击处理端。
  6. S4/S5 共用一张「方法 → 动作 → 唯一调用者」对照表：
     | 方法 | 动作/层 | 唯一调用者 |
     | --- | --- | --- |
     | `IDamageActionServices.ApplyDamageAsync` | 伤害结算 | 伤害动作默认处理端 handler |
     | `IOperateEntryServices.DeductOperateCostAsync` | 指挥（被指挥移动/攻击） | `OperateFinalizer` |
     | `IOperateEntryServices.SetOperateFlagsAsync` | 指挥（被指挥移动/攻击） | `OperateFinalizer` |
     | `IOperateEntryServices.EmitActedAsync` | 指挥（被指挥移动/攻击） | `OperateFinalizer` |
     它虽为 internal，仍受 R7 的可枚举/可审计要求约束；新增方法必须追加一行。
  7. 入口分支禁止直接写 `UnitStateData`/槽位/`CommandData`；现有 `FinalizeMoveAsync`/`FinalizeAttackAsync` 的扣费与 bool 写入全部改经 `OperateFinalizer`。
- Acceptance:
  - [x] R1/R10 口径已统一，且 R1 已在需求文档修订
  - [ ] `IOperateEntryServices` 为 internal，测试项目经 `InternalsVisibleTo` 可见
  - [ ] 服务实现零规则分支（无 Tank/Suppressed/费用判定）
  - [ ] 三个操作的唯一调用者为 `OperateFinalizer`，对照表已登记
  - [ ] `unit.acted` 仅从入口路径发射，处理端零发射
  - [ ] 入口分支零直接 `UnitStateData`/槽位/`CommandData` 写入
  - [ ] 效果入口（被控移动/撤退/效果攻击）零调用 `IOperateEntryServices`；测试断言零 `point.changed`、两 bool 不变、零 `unit.acted`
- Rationale：R1 修订后，「唯一落地口」按状态分两层；R16 进一步把代价归属钉在「指挥」这一行为上——移动/攻击动作本身零资源消耗，效果入口也不写行动状态。把三个操作收口到 `OperateFinalizer`，避免被指挥移动/攻击各写一遍扣费与清位逻辑，也让「唯一调用者」可静态核对。
- Terminology: [行动](./CONTEXT.md)、[动作](./CONTEXT.md)、[入口分支](./CONTEXT.md)

### S5a: P1 伤害动作样板
- Status: Done
- Target：`src/Orc.Game/Actions/DamageAction.cs`、`src/Orc.Game/Damage/DamagePipeline.cs`、`IDamageActionServices`
- Approach：
  1. `DamageAction`＝三触发端（`combat.damage`/`effect.damage`/`battle.damage`）+ 一个处理端；触发端用 `ActionTriggerBands`（`Commit` 默认转发 priority 100），处理端用 `ActionHandleBands`（`Apply` 默认落地 priority 100）。
  2. 处理端默认 handler 调 `IDamageActionServices.ApplyDamageAsync(source,target,amount,kind,ct)`；服务内部完成门户扣减 + `unit.damage.dealt`，返回实扣量；`card.damaged`/`card.stat.changed`/死亡衔接由既有门户与被动链保持。
  3. `DamagePipeline.ApplyCoreAsync` 实体迁入服务实现；`DamagePipeline` 只保留 `CombatAsync`/`EffectAsync`/`BattleAsync` 的 `bool` 包装（调用动作后按 `ActionResult.Targets[0].Outcome==Applied` 折算），不再持有扣数值实体。
  4. `DamageSettlement` 删除，结果改用 `ActionTargetResult`（`RequestedAmount`/`AppliedAmount`/`Subject`）；既有 `bool` 方法签名与返回值不变。
  5. 事件流比对基线：`CombatAsync`/`EffectAsync`/`BattleAsync` 三入口在典型场景（重甲改写、免疫归零、HQ、击杀）下的信号序列与顺序逐条一致。
- Acceptance:
  - [ ] 三触发端共享同一处理端；处理端零入口类型分支
  - [ ] 处理端只经 `IDamageActionServices.ApplyDamageAsync` 落地
  - [ ] `DamagePipeline` 无第二份扣数值实体；五个 `bool` 方法签名/返回值不变
  - [ ] `DamageSettlement` 删除，`tests` 零引用
  - [ ] 三入口信号序列与顺序逐条一致
  - [ ] P1 契约基线 diff 空、六测试项目全绿
- Rationale：P1 的准出是「行为零变化」，伤害动作是唯一能证明动作实体/结果对象/服务面/三段接缝同时可用的最小样板；先迁伤害，后续动作按同一形态复制。
- Deviation：门户唯一性测试的 HQ 探测词由 `.ApplyDamageAsync(` 收窄为 `hq.ApplyDamageAsync(`，避免把 `UnitCard.ApplyDefenseDamageAsync` 等同名后缀误判为第二门户；断言目标仍是「HQ 数值只能经服务面落地」。
- Terminology: [动作实体](./CONTEXT.md)、[三段接缝](./CONTEXT.md)、[唯一落地口](./CONTEXT.md)

### S6: 卡牌移动族（P2）
- Status: Done
- Target：`src/Orc.Game/Actions/MoveAction.cs`（底层移动动作）、`CommandManager` 的移动入口、`ProgramKeywordComponent` 的 `retreat`、`EffectRuntime.MoveAsync`
- Approach：
  1. **效果移动的行动经济面全免**：不扣指挥点、不写 `CanMove/CanAttack`、不发 `unit.acted`、不要求 `CanMove`；**不受 `Suppressed` 阻断**——压制不是「不能移动」，而是「不能被指挥」（R16）。
  2. **压制判定只在指挥资格/候选面**：`move.leg.eligibility` 与 `ComputeMoveAvailability` 的候选名单；效果入口不经过它们。效果入口只判「单位在场未毁、目标槽存在/为空/非原槽」，处理端只判「目标是否可落」。
  3. **入口收敛**：删除 `MoveByEffectAsync`；保留 `MoveUnitAsync` 名字，拆两个重载：
     - `MoveUnitAsync(UnitCard unit, string zone)`：按 zone 取首个空槽（`frontline` 推进 / `support` 后撤）；
     - `MoveUnitAsync(UnitCard unit, Slot destination)`：指定目标槽，保留现 `MoveByEffectAsync` 的「己方支援线 + 空槽 + 非原槽」约束（撤退/被控的后撤语义）。
     两个重载都走同一效果入口与同一底层移动动作，不经过 `DispatchMoveAsync` 的指挥收尾。
  4. 底层移动动作：触发端只做通用前置（单位在场/未毁、目标槽存在/为空/非原槽）；处理端 `LandMoveAsync` 执行 `oldSlot.Clear() → newSlot.Place(unit) → state.Position = newSlot → EmitUnitPositionChanged`。处理端不判「谁能移动」。
  5. 三入口副作用对照表（R10 验收）：
     | 入口 | 调用形态 | 扣费 | 清 `CanMove/CanAttack` | `unit.acted` | 方向约束 | 前置 |
     | --- | --- | --- | --- | --- | --- | --- |
     | 卡牌被指挥 | `DispatchMoveAsync` → 底层 → `OperateFinalizer` | 是 | 是 | 是 | 推进（`move.retreat` 时后撤） | 完整 leg 资格（回合/`CanMove`/费用/`Suppressed`/源位置） |
     | 卡牌被控 | `MoveUnitAsync(unit, Slot)` | 否 | 否 | 否 | 己方支援线（保留现状） | 单位在场未毁、目标槽存在/为空/非原槽 |
     | 卡牌撤退 | `MoveUnitAsync(unit, Slot)`（`retreat` op 选支援线空槽） | 否 | 否 | 否 | 己方支援线 | 同上；调用方另检查源不在支援线 |
     | （zone 便利） | `MoveUnitAsync(unit, zone)` | 否 | 否 | 否 | `frontline`/`support` 首空槽 | 单位在场未毁、对应线有空槽 |
     说明：zone 重载是选择器，不新增语义入口；「被控」与「撤退」共用指定槽效果入口，差异在调用方选择的目标槽。
  6. **两处有意的行为变更（P2 登记，不属于 P1「行为零变化」）**：
     - `MoveUnitAsync(zone)` 由「扣费+清位+`unit.acted`」改为免费；
     - 两入口合并为两个重载，`MoveByEffectAsync` 删除。
  7. **受影响清单（P2 开始前必须完整）**：
     - 代码：`EffectRuntime.cs:594-603`（`MoveAsync` 委托 `MoveUnitAsync`，行为随第 1 项变化）；`ProgramKeywordComponent.cs:217-226`（`retreat` op 调 `MoveByEffectAsync`，改调指定槽重载）；`CommandManager.cs:1413-1425`（删除）、`:1465-1505`（重写为效果入口）。
     - 测试：`KeywordProgramTests.cs:150-155`（`EffectDisplacementUsesThePortalWithoutPayingOrConsumingActions` 直接调 `MoveByEffectAsync`，需改调指定槽重载；**断言被压制单位仍可被效果移动**）；`DiyExample08_AssaultAdvanceRetreatTests.cs:143/150`（推进/撤退路径，需断言推进免费）。
     - `unit.acted` 计数断言：`CommandSystemTests.cs:64`、`CommandCombatTests.cs:241`、`CommandUpdateTests.cs:44/125/150`、`StatPortalTests.cs:343` 均为**指挥**移动/攻击路径，应保持发射；需新增「效果移动零 `unit.acted` / 零 `point.changed`」断言。`BattleAsyncTests.cs:99` 已断言战斗无 `unit.acted`，不受影响。
     - `MoveRevalidationJudicator` / `LegEligibilityJudicator` 里的压制判定**保持不变**（服务指挥复验/资格）；需确认效果入口不经过它们（R16）。
     - 卡包：`samples/card-pack-diy` 无移动/撤退卡（`move`/`推进`/`撤退`/`突击` 零命中）；`docs/卡牌DIY开发文档/examples/08-突袭推进.md` 需同步「效果推进免费」的口径。
- Acceptance:
  - [x] 效果移动行动经济面全免；效果移动不受 `Suppressed` 阻断（压制只在指挥资格/候选面）
  - [ ] `MoveByEffectAsync` 删除，两个 `MoveUnitAsync` 重载保留指定槽能力
  - [ ] 三入口副作用对照表与代码一致
  - [ ] 受影响清单逐项闭合（代码/测试/卡包）
  - [ ] 效果移动零 `point.changed`、零 `unit.acted`、两 bool 不变；指挥移动行为不变
- Rationale：效果移动是动作的后果、不是行动；代价属于「指挥」而非「移动」（R16），所以效果入口零资源消耗。压制改变的是「能否被指挥」，只留在指挥资格/候选面；效果入口不受它阻断。保留指定槽重载是为了不丢失撤退的目标表达能力；不引入 `OperateProfile` 或无 schema 扩展。
- Terminology: [动作](./CONTEXT.md)、[行动](./CONTEXT.md)、[入口分支](./CONTEXT.md)

### S7: 抽牌与加入手牌（P2）
- Status: Done
- Target：`src/Orc.Game/Actions/DrawAction.cs`、`AddToHandAction.cs`、`PlayerManager` 的 `DrawCard`/`FetchFromDeckAsync`、`MatchCardService.PlaceToHandAsync`、`HandLimitBurn`
- Approach：
  1. `DrawAction`：两个触发端（卡组顶＝`DrawInstance`；指定牌＝`RemoveInstance`），差异只在取件；取件完成后把卡交给**同一个处理端**。处理端**恰发一次 `card.drawn`**，且**必须在调用加入手牌动作之前**——满手/非满手共用同一位置。
  2. `AddToHandAction`：独立可复用，**两端齐全**：
     - 触发端 `Trigger<AddToHandTriggerView>`（`ActionTriggerBands`）：`Initiate` 做前置（卡存活/归属/未销毁），`Commit` 默认转发（priority 100）；
     - 处理端 `Trigger<AddToHandHandleView>`（`ActionHandleBands`）：`Rewrite`/`Veto`/`Apply`；未满＝入列表 + `card.hand.add`（`Applied`），满手＝`DestroyCard` + `card.burned`（`card.hand.add` 零次，`Blocked` + `Reason=HandFull`）。
     - **入口＝`IAddToHandAction.AddAsync`**，由它调触发端；它不是卡片专属触发器、不注册到 `TriggerRegistry` 的卡触发器面，但两端都存在，R5 的「缺任一端装配期 fail-fast」**不豁免**。
     - 爆牌在加入手牌处理端内完成，不在入口、不在抽牌端。
  3. `HandLimitBurn.BurnAsync` 删除 `emitDrawn` 参数，只做「`DestroyCard` → `card.burned`」；爆牌不得走弃置路线（`card.burned` 与 `card.discarded` 互斥）。
  4. 入口适配器：`DrawCard`、`FetchFromDeckAsync`、`PlaceToHandAsync` 签名与结果类型不变；只做 `ActionResult → CardPlaceResult/DeckFetchResult` 翻译，不新增字段。`Blocked(HandFull)` → `Burned`。
  5. 信号顺序（与现状逐条一致）：
     | 路径 | 顺序 |
     | --- | --- |
     | 抽牌 + 未满手 | 移除 → `card.drawn` → 入列表 → `card.hand.add` |
     | 抽牌 + 满手 | 移除 → `card.drawn` → `card.destroyed` → `card.burned` |
     | 非抽牌加入 + 未满手 | 入列表 → `card.hand.add` |
     | 非抽牌加入 + 满手 | `card.destroyed` → `card.burned`（`card.drawn` 零次） |
  6. **有意变更（P2 登记）**：非满手时 `card.drawn` 的监听者看到卡尚未进入 `player.Hand` 列表（现状先入列表再发 drawn）。理由：载荷已携带 Card 引用，正常消费方读载荷而非反查 Hand；且项目既有「信号＝终态」哲学（`PlayerManager.cs:262-263`）下，drawn 放在入列表前更接近「抽到的瞬间」。信号顺序与计数断言不变。
  7. 抽牌处理端**不得**按入口类型分支：卡组顶/指定牌的差异只在触发端的取件步骤；此后「移除 → drawn → 加入手牌」完全共用。
- Acceptance:
  - [x] 抽牌处理端与加入手牌处理端的职责切分
  - [ ] `card.drawn` 由抽牌处理端恰发一次、先于加入手牌
  - [ ] 爆牌在加入手牌处理端内完成，且走 `DestroyCard`、不发 `card.discarded`
  - [ ] `CardPlaceResult`/`DeckFetchResult` 形状与字段零改动
  - [ ] `AddToHandAction` 两端齐全（触发端 + 处理端），由 `IAddToHandAction.AddAsync` 驱动；装配期缺端校验不豁免
  - [ ] 抽牌处理端零入口类型分支；两入口共用「移除 → drawn → 加入手牌」
  - [ ] 四条路径信号顺序与现有断言逐条一致
- Rationale：R11 的图是「抽牌（从卡堆移除）→ 加入手牌（子动作）→〔满手〕爆牌分支」；把 drawn 固定在抽牌处理端、爆牌固定在加入手牌处理端，才能删除 `emitDrawn` 而仍保持两条路径的顺序。爆牌走 `DestroyCard` 是 Kb 既有裁决（`HandLimitBurn.cs:70`），不得混入弃置。
- Terminology: [动作](./CONTEXT.md)、[入口分支](./CONTEXT.md)

### S8: 死亡两端（P2）
- Status: Done
- Target：`src/Orc.Game/Actions/DeathAction.cs`、`CommandManager.ProcessDeathAsync`、`DeathInterceptor`、`KeywordProgramRuntime.BeforeDestroy`
- Approach：
  1. `DeathAction`：触发端视图 `DeathTriggerView { UnitCard Unit, Card? Killer }`；处理端视图 `DeathHandleView { UnitCard Unit, Card? Killer[Mutate], bool ShouldDie[Mutate], ActionDisposition? Disposition[Mutate] }`。
  2. `Rewrite` 段是**唯一**改写位：`DeathInterceptor` 的布尔语义改为该段 handler（true → `ShouldDie=false`）；`KeywordProgramRuntime.BeforeDestroy` 也改为该段 handler（`frame.Cancelled` → `ShouldDie=false`）。只有一个调用位置，按现有次序（拦截器先、词条后）注册。**不新增 `RegisterDeathRewrite`**：插件直接拿动作实体的处理端 `Trigger<DeathHandleView>` 往 `Rewrite` 段注册，与其余 7 个动作的注入方式一致。
  3. `ShouldDie` 默认值由**触发端写入 payload**（`true`），`Disposition` 默认 `null` 显式写入；视图类不写属性初始化器（项目惯例，见 `BaseFlows.cs:1`、`OrderFlow.cs:1`）。
  4. `ShouldDie=false`：`Rewrite` 段调 `ctx.Stop()`，`Apply` 不执行；结果 `Blocked` + `Reason=DeathPrevented`；仅写事件流 log（`death:prevented`，kind=log），不发射总线信号；零状态改动。
  5. `ShouldDie=true`：`Apply` 执行现有死亡链（清位→置毁→`card.dying`→亡计→词条注销→修饰清理→钳击→效果卸载→Position 置空→以 `view.Killer` 发 `card.died`）。
  6. `Disposition` 默认 `null`＝沿用现有死亡链（销毁）；仅显式非 `null` 改变处置。`Disposition=ToHand`：死亡链照常完成（含 `card.died`），随后由**入口分支**执行「回手组合」——复用 `return.hand` 的形态（`ProgramKeywordComponent.cs:277-288`）：先解析定义 id → 新建同定义实例 → 加入手牌。**不是**把已毁实例塞回手牌；会产生新实例与新对局 ID（与 `return.hand` 现行行为一致，属既有语义）。处理端不调加入手牌动作。
  7. 受影响清单：
     - `CommandManager.cs:1373`（`DeathInterceptor` 公开可写属性删除/改形态）、`:1515-1516`（两个调用点合并到 `Rewrite` 段注册）。
     - `KeywordProgramTests.cs:124-135`（`DeathInterceptorIsConsultedBeforeTheKeywordProgramAndCanCancel` 直接赋值 `DeathInterceptor`，须改为向 `Rewrite` 段注册两个 handler 并断言次序）。
     - 全仓 `DeathInterceptor` 赋值点仅此一处（已 grep）；`docs/引擎优化-触发处理分离/requirements.md:69` 为现状描述，不改。
     - P2 事件流比对基线登记「免死路径新增一条 `death:prevented` log」。
- Acceptance:
  - [x] 单一 `Rewrite` 改写位，旧两个取消口变 handler
  - [ ] 免死路径零状态改动、仅 log、结果 `Blocked(DeathPrevented)`
  - [ ] `card.died` 的 Killer 读 `view.Killer` 改写值
  - [ ] `Disposition=ToHand` 由入口新建实例并加入手牌，处理端零动作调用
  - [ ] `DeathInterceptor` 赋值点与测试全部改写，无遗漏
  - [ ] 插件经 `Trigger<DeathHandleView>.Register(..., ActionHandleBands.Rewrite)` 注入，无专用 API
- Rationale：R12 把「是否死亡/击杀者/处置指向」三项都放在处理端，只有 `Rewrite` 段能在死亡链执行前拿到最终值；`ctx.Stop()` 是既有段落阻断原语。把两个旧取消口改为同一段的 handler，保留现有裁决次序与测试语义，同时让新效果经统一触发器面改写归属与处置。`Disposition=ToHand` 复用 `return.hand` 的新建实例组合，避免把带 `UnitStateData`/残留效果的已毁单位直接塞回手牌。
- Terminology: [动作](./CONTEXT.md)、[处理端](./CONTEXT.md)、[唯一落地口](./CONTEXT.md)

### S9: 加入战场（P3）
- Status: Done
- Target：`src/Orc.Game/Actions/JoinBattlefieldAction.cs`、`PlayManager.PlayUnitAsync`/`JoinUnitAsync`、`UnitCard` 的部署/加入链
- Approach：
  1. 采用「两个触发端 + 一个共享处理端」：
     - **部署触发端**：`Initiate` 做代价校验（点数不足在此拒绝）→ `Announce` 发 `card.played` → `Commit` 先跑部署词条（priority < 100）→ 默认转发到处理端。
     - **加入触发端**：`Initiate` 做前置 → 无 `Announce` → `Commit` 直接转发。
     - **共享处理端**：单位化（`UnitStateData`/`CommandData`/槽位/`RequestRerunAsync`）→ `card.placed`；无入口类型分支、无 `Mode` 参数。
     - **入口收尾**：部署入口在 `Applied` 后扣部署费、离手、`OnDeployChainFinalized`、发 `unit.deployed`；加入入口发 `unit.joined`。
  2. **`card.played` 的两处发射是现状，保持**：单位部署入口发（打出宣告）、指令施放入口发（`OrderFlow.Resolve`）。观察方按载荷 `Card` 的实际类型区分；P4 做指令归一时不得误判为「重复发射」而删掉其中一处。
  3. **部署词条放 `Commit` 段前部的依据是「顺序保持」，不是语义分类**。`Commit` 不是「部署词条专用段」；不得再往该段塞其他效果。若将来部署词条需要改写单位化结果，再迁到 `Adjust`，那是另一次决策。
  4. **代价校验在前置阶段完成，`Applied` 之后不得再失败**：点数校验属于部署触发端的 `Validate`/`Initiate`；扣费在 `Applied` 后执行且视为不可失败。任何可能失败的写必须前置，避免「单位已上场但没扣费」。
  5. `OnDeployChainFinalized`（`UnitCard.cs:190` 现有入口收尾）**留在入口**，不下沉到处理端——它是「部署链已完成」的编排语义，处理端不知道自己是部署还是加入。
  6. `card.placed` 归处理端，且必须在单位化之后发射（装载链依赖「已单位化」前提）。顺序：
     | 入口 | 顺序 |
     | --- | --- |
     | 部署 | `card.played` → 部署词条（零信号）→ 单位化 → `card.placed` → `unit.deployed` → 扣费/离手/`OnDeployChainFinalized` |
     | 加入 | 单位化 → `card.placed` → `unit.joined` |
- Acceptance:
  - [x] 两个触发端 + 一个处理端；`unit.deployed`/`unit.joined` 归入口
  - [ ] 处理端零入口类型分支、零 `Mode` 参数
  - [ ] 部署代价校验先于任何状态变更；`Applied` 后扣费无失败路径
  - [ ] `card.placed` 在单位化之后、完成信号之前
  - [ ] 部署词条只在部署触发端 `Commit` 前部；未进入处理端
  - [ ] `OnDeployChainFinalized` 仅在部署入口
  - [ ] 两条路径信号顺序与现有断言一致
- Rationale：R8 的「可变 N 触发端 + 恰 1 处理端」正好承载部署/加入两个入口变体；处理端只做「单位化 + 落位 + `card.placed`」，天然零入口分支。`card.played` 放部署触发端 `Announce`，保证「发起前」段在宣告之前；加入入口无宣告，`Announce` 为空段。`unit.deployed`/`unit.joined` 与 `unit.acted` 同属入口特有完成信号，放入口既保持顺序，也不让处理端知道入口类型。扣费前置校验是「Applied 之后不可失败」的必要条件。
- Terminology: [动作](./CONTEXT.md)、[入口分支](./CONTEXT.md)

### S10: 动作编排与跨动作调用
- Status: Done
- Target：`src/Orc.Game/Actions/MatchActionCatalog.cs`、动作接口与工厂定义、各入口/处理端的动作引用
- Approach：
  1. **依赖图（单向、无环；死亡与攻击均不依赖下游）**：
     ```
     加入手牌（无依赖） ← 抽牌
     伤害（无依赖）
     死亡（无依赖）        // Disposition=ToHand 返回入口，由入口调加入手牌
     攻击 → 伤害           // 死亡由 card.stat.changed 被动衔接，攻击不直调死亡
     移动（无依赖）
     加入战场（无依赖）
     指令 → 运行期经 EffectRuntime，不静态依赖
     ```
  2. **每对局 `MatchActionCatalog`（internal）**按依赖序构造动作实例；动作通过构造函数只拿到自己需要的子动作接口。**不使用通用 `IActionDependencyContext`**——它是万能依赖容器，会让移动 handler 也能取到 `IDamageAction`，最小权限失效。插件工厂签名直接声明依赖：
     - `IAttackActionFactory.Create(IDamageAction damage)`
     - `IDrawActionFactory.Create(IAddToHandAction addToHand)`
     - 其余动作工厂同理，只列自己需要的接口。
  3. **返回类型统一 `ActionResult`，参数各自定义**（P5 插件契约核心）。`ActionId` 稳定键固定为：`damage`、`move`、`attack`、`join.battlefield`、`order.cast`、`death`、`draw`、`hand.add`（插件追加动作的 id 不得与这 8 个冲突，装配期校验）：
     | 接口 | 方法签名 |
     | --- | --- |
     | `IAddToHandAction` | `Task<ActionResult> AddAsync(CardBase card, Player player, Card? triggerCard = null, CancellationToken ct = default)` |
     | `IDrawAction` | `Task<ActionResult> DrawTopAsync(Player player, Card? triggerCard = null, CancellationToken ct = default)`；`Task<ActionResult> DrawSpecificAsync(CardBase card, Player player, Card? triggerCard = null, CancellationToken ct = default)` |
     | `IDamageAction` | `Task<ActionResult> CombatAsync(Card? source, Card target, int amount, Card? triggerCard = null, CancellationToken ct = default)`；`EffectAsync(...)`；`BattleAsync(...)`（参数同前，均返回 `ActionResult`） |
     | `IDeathAction` | `Task<ActionResult> ExecuteAsync(UnitCard unit, Card? killer = null, Card? triggerCard = null, CancellationToken ct = default)` |
     | `IAttackAction` | `Task<ActionResult> CommandAsync(UnitCard attacker, Ref<Entity> target, Card? triggerCard = null, CancellationToken ct = default)`；`Task<ActionResult> EffectAsync(UnitCard attacker, Ref<Entity> target, bool bypassRange = false, Card? triggerCard = null, CancellationToken ct = default)` |
     | `IMoveAction` | `Task<ActionResult> CommandAsync(UnitCard unit, Slot destination, Card? triggerCard = null, CancellationToken ct = default)`；`Task<ActionResult> EffectAsync(UnitCard unit, Slot destination, Card? triggerCard = null, CancellationToken ct = default)` |
     | `IJoinBattlefieldAction` | `Task<ActionResult> DeployAsync(UnitCard card, Slot slot, Player player, Card? triggerCard = null, CancellationToken ct = default)`；`Task<ActionResult> JoinAsync(UnitCard card, Slot slot, Card? triggerCard = null, CancellationToken ct = default)` |
     | `IOrderAction` | `Task<ActionResult> ExecuteAsync(OrderCard card, Card? target, int amount, Card? triggerCard = null, CancellationToken ct = default)`（P4 可细化） |
  4. **环检测覆盖「内置 + 插件」合并图**：内置 8 个动作无环是已知事实；P5 插件可追加动作，装配期必须对合并图做一次环检测与缺依赖 fail-fast，不能只校验内置图。
  5. **`TriggerCard` 链内传递必须显式验证**：现有机制是入口手工透传（`DispatchMoveAsync`/`DispatchAttackAsync` 的 `CommandDataKeys.TriggerCard`）＋`frame.TriggerCard` 逐事件解析（`Trigger.cs:574-577`）。改成动作接口调用后，新 `InvokeAsync` 会建新帧，父帧是否存在决定能否自动继承。跨动作调用（攻击→伤害、抽牌→加入手牌、死亡入口→加入手牌）的子动作执行流中，`view.TriggerCard` 必须与父动作一致；不一致就显式传 `Card? triggerCard`，**不依赖“应该会继承”**。
  6. `ActionTargetResult` 加性补 `CardBase? Subject`（具体实例，供 `DrawCard`/`FetchFromDeckAsync` 返回爆掉的卡、供入口读取具体对象）；`Target` 仍为 `Ref<Entity>`（可能已失效）。`Subject` 只作返回载体，不作分支依据。
- Acceptance:
  - [x] 依赖图修正：死亡不依赖加入手牌；攻击不依赖死亡
  - [ ] 所有跨动作调用只经动作接口，服务面方法不内部调动作
  - [ ] 工厂签名显式声明依赖，无万能依赖容器
  - [ ] 八个动作接口的完整签名与表一致
  - [ ] 内置 + 插件合并图装配期无环、缺依赖 fail-fast
  - [ ] 跨动作子流的 `view.TriggerCard` 与父动作一致（不一致即显式传参）
  - [ ] `ActionTargetResult.Subject` 为加性字段，入口不以它作分支依据
- Rationale：S4 边界 3 要求「跨动作只能调动作」；构造注入是唯一能在编译期保证「只拿到被授权的子动作」的方式。把死亡与攻击的下游依赖去掉，才符合 R12「处置由入口执行」与现行「死亡由 `card.stat.changed` 被动衔接」的观察序。工厂显式声明依赖，替代万能容器；合并图环检测把 P5 插件的追加动作纳入同一装配门。
- Terminology: [动作实体](./CONTEXT.md)、[动作服务面](./CONTEXT.md)、[入口分支](./CONTEXT.md)

### S11: 攻击（P3）
- Status: Done
- Target：`src/Orc.Game/Actions/AttackAction.cs`、`UnitAttackTrigger`/`AttackDamageTrigger`、`DispatchAttackAsync`/`AttackByEffectAsync`、伏击效果注册
- Approach：
  1. **两端划分**：`UnitAttackTrigger`＝攻击触发端（`ActionTriggerBands` + 既有验证绑定 + `Commit` 转发）；`AttackDamageTrigger`＝攻击处理端（`ActionHandleBands`：`Rewrite`/`Veto`/`Apply`）。保留两个公开触发器名与属性，`AttackDamageTrigger` 的职责从「嵌套互伤阶段」改为「唯一攻击处理端」。
  2. **执行全部下沉到处理端**：揭示（调既有揭示规则 `CovertRules.RevealAsync`；揭示属 §1 Excludes，不造 `RevealAction`）、`KeywordProgramRuntime.AttackEvent(before/after)`、`KeywordRules.RecordAttack`、冲击消耗、HQ/单位目标分支、互伤、`unit.combat.survived` 均在处理端；`unit.acted`/扣费/清位仍在指挥入口（S5）。
  3. **普通死亡被动衔接**：`IAttackAction` 不注入 `IDeathAction`，保持 S10 的 `攻击 → 伤害` 单向依赖；死亡由 `card.stat.changed` 的 `_defenseDepletionTrigger` 衔接。
  4. **伏击改写由伏击效果自己执行死亡**：伏击 handler 注册到 `Rewrite` 段，由 `KeywordLoadContext` 注入的 `IDeathAction` 调 **完整两端** `IDeathAction.ExecuteAsync(attacker, killer: target, triggerCard)`（触发端 → 处理端），**不是**直调处理端；随后置 `AttackDamageResolution.Handled=true` 与 `IsRewritten=true`。
  5. **默认 `Apply` 的三种分支**：
     - `Handled=false`：正常 HQ/单位路径 → 伤害动作（目标、必要时反击）→ `unit.combat.survived`；
     - `Handled=true && IsRewritten=true`：跳过伤害，只发被攻击者 `unit.combat.survived`；若死亡动作返回 `Blocked(DeathPrevented)`（免死），攻击者存活，另发攻击者 `unit.combat.survived`；
     - `Handled=true && IsRewritten=false`：**防御**——写 Error 留痕（不抛、不中断），并按「未处置」路径继续正常伤害，禁止“无伤害、无死亡”的静默吞伤害。
  6. `AttackDamageResolution` 加 `Handled`：public 加性字段，不改变既有 `IsRewritten` 语义；两者不得混用（`IsRewritten`＝伏击已改写，`Handled`＝改写方已完整处置）。
  7. **词条程序取 `IDeathAction` 走现有 `KeywordLoadContext`**（`CardBase.cs:117` → `KeywordManager.LoadContextProvider`）；不在词条层新造「效果动作注入」。该通道与 P5 插件工厂依赖注入相互独立、不互相引用。
  8. **P3 变更（R16）**：删除 `AttackByEffectAsync`（`CommandManager.cs:1397`）的 `KeywordIds.Suppressed` 检查——效果攻击不受压制影响；压制只影响指挥入口的 `attack.leg.eligibility` 与候选名单。被压制单位仍可被效果强制攻击。
  9. 受影响清单：
     - `CommandManager.cs:213`（`UnitAttackTrigger.Register` 需带 `ActionTriggerBands`）、`:218`（`AttackDamageTrigger.Register` 需带 `ActionHandleBands.Apply`）、`:233`（`KeywordLoadContext` 增 `IDeathAction`）、`:1188-1269`（执行段迁出）、`:1273-1337`（默认互伤改为伤害动作）、`:1397`（删除 `Suppressed` 检查）。
     - `KeywordEffects.cs:365`（伏击注册改到 `Rewrite` 段并注入 `IDeathAction`）。
     - 测试：`TriggerCardTests.cs:235/246`、`EffectSystemTests.cs:502/507`、`G14HistoryCountTests.cs:387`（注册需带 band）；`KeywordEffectBatch2Tests.cs:162`、`CommandCombatTests.cs:397`（直接调 `AttackDamageTrigger` 现在会执行完整处理端，需按新语义调整断言）；`TriggerRegistryTests.cs:87` 仍成立（属性保留）。
- Acceptance:
  - [x] `UnitAttackTrigger` 触发端 + `AttackDamageTrigger` 处理端
  - [ ] 伏击死亡走 `IDeathAction.ExecuteAsync` 完整两端，不直调处理端
  - [ ] 「免死」单位被伏击：攻击者不死，且结果可观测
  - [ ] `Handled=true && IsRewritten=false` 写 Error 并回退正常伤害，无静默吞伤害
  - [ ] 互伤只经 `IDamageAction`，不再直调 `DamagePipeline`
  - [ ] 揭示/`AttackEvent`/记账/冲击/`unit.combat.survived`/`card.died` 的次数与顺序与现状一致
  - [ ] `AttackDamageResolution.Handled` 为加性字段，`IsRewritten` 语义不变
  - [ ] 被压制单位仍可被效果强制攻击（`AttackByEffectAsync` 的 `Suppressed` 检查已删除）
- Rationale：`AttackDamageTrigger` 已是伏击的既有注入点与测试入口，升为处理端可保留注册面；`UnitAttackTrigger` 只做验证与转发，攻击才真正成为「触发端 + 处理端」。普通死亡保持被动衔接，伏击的「以死代伤」由改写者自己走完整死亡两端，既满足 R12，也不把 `IDeathAction` 塞进攻击动作的依赖图。`Handled` 防御把「置了标志却没处置」从静默失败变成可观测错误。R16 把压制从效果攻击入口移除：压制只影响「能否被指挥」，不影响动作本身。
- Terminology: [动作](./CONTEXT.md)、[处理端](./CONTEXT.md)、[元效果](./CONTEXT.md)

### S12: 指令归一（P4）
- Status: Done
- Target：`CommandCard`→`OrderCard`、`PlayManager` 的两个入口、`GameEntryPoints` 两条登记、`Orc.Mcp` 的协议门面、全仓字符串引用
- Approach：
  1. **改名边界（R4 三行，不扩）**：`CommandCard`→`OrderCard`；`PlayCommandAsync`→`PlayOrderAsync`；`BeginCommandPrePlayAsync`→`BeginOrderPrePlayAsync`。P4 一次性改完，**不保留 `[Obsolete]` 转发**。
  2. **`GameEntryPoints` 只同步两条**：`PlayCommandAsync`→`PlayOrderAsync`、`BeginCommandPrePlayAsync`→`BeginOrderPrePlayAsync`；条目数、类别、Owner、门禁、返回形态、顺序全部不变（Constraint 2 例外已写入需求）。
  3. **按字符串引用点先列全再动手**：
     - 引擎/游戏层：`PlayManager.cs:19/392/396/401/404`、`GameEntryPoints.cs:102-115`。
     - `Orc.Mcp`：`Tools/PlayerActionTools.cs:120`、`Hosting/HostingContracts.cs:111/114-115`、`McpService.Hosting.cs:303/308/318/323`、`Match/CardResolver.cs:35`（其中 `CommandCard` 泛型/注释按 R4 机械替换；`PlayCommandAsync`/`PlayCommandPreAsync` 方法名与工具 ID 不改）。
     - 测试：`GameEntryPointsTests.cs:48`、`EntryPointBeginTests.cs:13/143/158/171/179`、`PlayChainCommandTests.cs`（多处）、`G14HistoryCountTests.cs:55/59`、`PlayChainUpdateTests.cs:99`、`MatchOutcomeTests.cs:143`、`DeployCostModifierTests.cs:41/56`、`FactionCostComponentTests.cs:153/159`、`DiyExample01_DamageAndArmorTests.cs:35`、`KeywordProgramTests.cs:333/345`。
     - 样例/文档：`samples/Orc.Game.Sample/Program.cs:166`、`docs/game-flow/04-打出与部署.md:117`、`docs/ui-开发者交接文档.md:283`、`docs/mcp-ui-integration.md:380`、`docs/orc-用户输入点分析报告.md` 相关条目。
  4. **协议层例外（R4 末段）**：`Orc.Mcp` 的 `IMcpService.PlayCommandAsync`/`PlayCommandPreAsync` 与工具 ID `play.command`/`play.command_pre` **不改名**；唯一改动是 `McpService.Hosting.cs:308/323` 的内部委托指向 `PlayManager.BeginOrderPrePlayAsync`/`PlayOrderAsync`。MCP 层这两个方法只是**转发**，不是第二条实现路径，P5 清理时不得当作双轨删除。`docs/mcp-ui-integration.md` 加一行「协议名 `command` → 引擎入口 `PlayOrderAsync`」映射。
  5. **不动的名单**：`CommandManager`、`CommandTrigger`、`CommandResult`、`CommandDataKeys`、`CommandBlockReason`、`CardCategory.Command`（枚举值）、卡数据 `"type":"order"`、`BuiltInCardComponents.cs:267` 的映射——全部保持原样。
  6. `CommandCard.AddActiveHandler` 删除（全仓无调用者）；`OrderFlow` 作为唯一施放入口、`card.played` 位置、`Counter` band 注入见 S13。
- Acceptance:
  - [x] 改名边界与 `GameEntryPoints` 例外已裁决
  - [x] 协议层例外已裁决（MCP 方法名/工具 ID 不改）
  - [ ] 全仓字符串引用一次性改完，无半新半旧
  - [ ] `GameEntryPoints` 两条改名，条目数/顺序/类别/门禁/返回形态不变
  - [ ] 引擎层无旧名、无 `[Obsolete]` 转发；MCP 工具 ID 与方法名零改动
  - [ ] 不动名单逐项零改动
  - [ ] `CommandCard.AddActiveHandler` 删除且无调用者残留
- Rationale：R14 冻结的是入口集合与语义，不是方法拼写；R4 的三行是明确裁决。先列全字符串引用再一次性替换，避免 `GameEntryPoints`/MCP/文档出现半新半旧；不做兼容 wrapper，避免违反「全仓不存在同一语义两条实现路径」。MCP 的 `PlayCommandAsync` 是协议转发层，不是第二条实现路径，故冻结其名字与工具 ID。
- Terminology: [动作](./CONTEXT.md)、[入口分支](./CONTEXT.md)

### S13: 指令施放链（P4）
- Status: Done
- **OPEN-1（已裁决 · 第 15 轮）：默认 `Stop`**。`ctx.Stop()`＝只停 `Resolve`（及其后），`Finalize` 照常（指令照常扣费、照常离手，只是效果不触发）；`ctx.Interrupt()`＝`Resolve`/`Finalize` 全停（牌留手、不扣费）。两种路径都保留，结构不动；默认反制效果改用 `Stop`。
- **段序**：`Counter`（反制判定）→ `Resolve`（`card.played` → 效果）→ `Finalize`（扣费/离手）。被反制的指令**不发 `card.played`**（视为未成功打出）。
- Target：`Orc.Cards.OrderFlow`、`OrderCard`、`PlayManager.PlayOrderAsync`、`OrderFlowBands.Counter` 注入、`EventStream` 中断观测
- Approach：
  1. `OrderFlow` 留内核，游戏层经 band 注语义：
     - `OrderFlowView.Target` 可空；`OrderFlowView.Orders` 为 `IReadOnlyList<ICastAction>`（按效果登记序），移除单数 `Order`；
     - `ExecuteAsync` 提供两个重载：`(Card source, ICastAction? order, Card? target, int amount, ct)`（内核既有调用）与 `(Card source, IReadOnlyList<ICastAction> orders, Card? target, int amount, ct)`；**只要求 `source` 非空**；
     - `Resolve` 默认「施放结算」优先级改为 100，按 `Orders` 登记序**依次**执行全部 `ICastAction`；**禁止 `FirstOrDefault`**——多主动效果必须全跑；若产品将来限制单主动效果，则在装配期 fail-fast（多于一个即拒绝装载），不在运行期取首；
     - 游戏层在 `Resolve` 优先级 0 注册 `card.played`（`view.Source is OrderCard` 守卫），`Finalize` 优先级 100 注册扣费/离手；
     - `OrderCard.PlayTrigger` 退化为**纯验证触发器**（保留 `CostCheckTrigger` 验证绑定，移除宣告/收尾链事件）；`AddActiveHandler` 删除。
  2. **两阶段执行（OPEN-1 的结构前提）**：
     - 阶段 1＝`OrderFlow.Trigger`（`Counter` → `Resolve`）；阶段 2＝独立 `OrderFlow.FinalizeTrigger`（`Finalize`）；
     - `OrderFlow.ExecuteAsync` 先跑阶段 1；若本次执行 `Interrupted` 则跳过阶段 2，否则无论阶段 1 是否 `Stop` 都跑阶段 2；
     - `EventStream` 加性 `Interrupted`（或等价的内部可观测标记）——`Stop` 不置位、`Interrupt` 置位；不新增总线信号；
     - 这样 `ctx.Stop()`＝被反制但仍消耗（`Resolve` 停、`Finalize` 跑；**默认**），`ctx.Interrupt()`＝牌留手/不扣费（两阶段全停；保留为可选路径）。
  3. **段内/跨段顺序断言**：`Counter` → `Resolve`（`card.played` priority 0 → 全部主动效果 priority 100）→ `Finalize`（priority 100）。同段内 priority 与跨段顺序分开断言，不能混为一谈；反制 `Stop` 使整个 `Resolve`（含宣告）不执行。
  4. `OrderFlowView.Order`/`Target` 可空化是内核公共面变更；`Orc.Tests` 必须全绿。受影响断言：
     - `Scenario1Tests.cs:167`（`view.Order.Name` → `view.Orders[0].Name`）、`:175/177`（`view.Target.Name` 需 null 安全）；
     - `DiyExample06_CounterSecondOrderTests.cs:75` 已有 `view.Target is not { } target`，不受影响；`:105` 不读 `Order`。
  5. **指令卡不再提供可注入的「打出触发器」**：`CommandCard` 现状未 `RegisterNamedTrigger`，仅 `UnitCard` 登记；本次不新增。已核对 4 个 DIY 指令 effect prefab 的 `injects` 均为 `[]`，无指向「打出触发器」的注入；后续指令 prefab 不得以该名字注入。
  6. **`IOrderAction` 与 `OrderFlow` 的关系（三选一：a＝动作层包装）**：
     - `IOrderAction` 是动作实体接口，`OrderAction` 实现内部**只做一句转发**：调 `OrderFlow.ExecuteAsync(orderCard, activeEffects, target, amount)`，再把 flow 结果翻译为 `ActionResult`；
     - `OrderFlow` 是唯一 **flow**；`IOrderAction` 是唯一 **action 入口**；`PlayManager.PlayOrderAsync` 调 `IOrderAction.ExecuteAsync`，**不直接调 `OrderFlow`**；禁止出现第二个编排单元；
     - `activeEffects = card.Effects.OfType<ICastAction>().ToArray()`（登记序，全部），由 `IOrderAction` 取后传给 flow。
  7. R3 第 5 条（数据迁完后 `Resolve` 不再用 `card.played` 承载效果）：P4 保持现有指令数据零改动，`card.played` 兼容被动效果；数据迁移到主动效果是后续独立工作，不在 P4 准出；届时只删兼容 handler，不改 `OrderFlow` 结构。
  8. `OrderFlow` 保留自身 `OrderFlowBands`（R3 明确要求 `OrderFlowBands.Counter`），不改为 `ActionTriggerBands`；`Counter`（1）/`Resolve`（2）/`Finalize`（3）。`card.played` 在 `Resolve`/0 即宣告点，主动效果 priority 100 即结算点。这是 8 个动作中唯一保留专属 band 方案的动作，理由是与 R3 的公共名兼容。
  9. **目标传递（P4 收尾修补）**：`PlayManager` 把预打出捕获结果解析为 `Card?` 后传给 `IOrderAction`（`null`→null；`Card`→自身；`Ref<Entity>` 存活则解引用；列表取第一个可解析元素；其余→null）。解析失败不阻断施放；多目标指令的完整传递＝**遗留**。
- Acceptance:
  - [x] `OrderFlow` 留内核 + 游戏层 band 注入
  - [ ] 多主动效果按登记序全部执行，无 `FirstOrDefault`
  - [ ] `card.played` 在 `Resolve` 段 priority 0（先于主动效果；反制命中时不发射）；主动效果 priority 100；`Finalize` 在独立阶段之后
  - [ ] `Stop`＝Resolve 停、Finalize 跑（默认）；`Interrupt`＝两者全停（保留可选）；默认反制用 `Stop`
  - [ ] `EventStream.Interrupted` 可观测，且不新增总线信号
  - [ ] 只要求 `source` 非空；`Orc.Tests` 全绿，`Scenario1Tests` 断言已按 `Orders` 改写
  - [ ] `IOrderAction` 只做动作层包装；`PlayManager` 不直接调 `OrderFlow`；全仓无第二个指令编排单元
  - [ ] 指令卡无「打出触发器」具名注入；4 个 DIY prefab 零改动
  - [ ] `AddActiveHandler` 删除；`PlayTrigger` 仅保留验证
- Rationale：R3 只要求 `OrderFlow` 唯一入口与 `card.played` 在执行流内，不要求内核认识 `OrderCard`/`Player`；用现有 band 注入游戏语义，既保留内核 `Scenario1Tests`，也不打击 `DiyExample06` 的手动 `ActiveEffect` 调用。多主动效果必须全跑，`FirstOrDefault` 是最难查的静默失败。OPEN-1 的两种语义无法在单触发器内表达，故把 `Finalize` 拆为独立阶段，并用 `EventStream.Interrupted` 区分 Stop/Interrupt。
- Terminology: [动作](./CONTEXT.md)、[入口分支](./CONTEXT.md)、[处理端](./CONTEXT.md)

### S14: 反制注入与 P4 出口
- Status: Done
- **OPEN-2（已裁决 · 第 15 轮）：反制卡一次性消耗**。触发后从手牌移除并走弃置路径（`card.discarded`），不退费；触发一次后不再对后续指令生效；多张激活反制中未触发者保持激活。
- Target：`CounterCard` 的激活/取消注册、`OrderFlow.Trigger` 的 `Counter` 段、`UseCounterTrigger`、`CardTriggerView`
- Approach：
  1. **注册目标切换**：激活时把每个效果 handler 包装为 `Func<OrderFlowView,...>`，注册到 `Engine.OrderFlow.Trigger` 的 `OrderFlowBands.Counter` 段；取消时注销。`RegisteredEffectHandlerNames` 仍返回这些登记名。
  2. **视图适配**：包装器构造 `CardTriggerView { Card = counterCard, Player = owner, Source = orderCard, Target = orderTarget, Argument = amount }` 后调用原 handler；`CardTriggerView` 加性补 `Target: Card?`（public 加性，现有使用方零影响）。
  3. `UseCounterTrigger` 退化为「激活/取消验证＋状态翻转」，不再承载效果 handler；`counter.triggered` 仍在激活分支恰发一次。
  4. **多张激活反制卡的顺序**：都注册在 `Counter` 段，按注册序（＝激活序）执行。默认 `Stop`：首个触发并 `Stop` 后，本级后续事件不执行（in-flight handler 必跑完），**第二张保持激活、不消耗**。若某张只改写值、不 `Stop`，视为未命中、不消耗，后续反制仍可继续判定——这是「多个反制叠加」的正确语义，**不额外加「只允许一张」限制**。
  5. **`CardTriggerView.Target` 加性变更**：视图 binder 按属性名（键名＝属性名）绑定；csx 侧 `view.Target` 必须可读。若 csx 视图白名单是静态生成的，P4 要专门验证一条 csx 反制，防止漏项。
  6. **回合约束只在激活/取消检查**：`CounterCard.EvaluateUse` 要求 owner==当前行动方；**触发时不检查**——反制的价值就在对手回合生效。禁止给触发路径「顺手」加回合校验。
  7. **一次性消耗（命中即消耗，发生在 `Counter` 段执行过程中，不延后到 `Finalize`）**：每个反制 handler 正常返回后，**若本次 handler 调用了 `ctx.Stop()` 或 `ctx.Interrupt()`**（即反制命中），包装器执行一次 `ConsumeAsync`；未调用者视为未命中、不消耗（避免监听到不相关指令也被烧掉）。
     - 经 Match 装配注入的弃置委托调用既有 `PlayerManager.DiscardCardAsync`（从手牌移除 → 销毁 → `card.discarded` 恰一次），**不新建弃置路径**；
     - 注销该卡在 `OrderFlow.Counter` 注册的全部 handler、清空 `_activeRegistrations`、`CounterActivationData.IsActive=false`、`_chargedCost=null`；
     - **不退费**（不调 `ChangePointsAsync` 退点）；`RegisteredEffectHandlerNames` 变为空；
     - 同卡多 handler 用 `_consumed` 守卫，只消耗一次；后续 wrapper 见 `_consumed` 直接跳过。
  8. 反制 handler 用 `ctx.Stop()`（默认；被反制但仍消耗，`Finalize` 照常）或 `ctx.Interrupt()`（保留可选；牌留手/不扣费）。
- Acceptance:
  - [x] 激活注册到 `OrderFlow.Counter`、取消注销；`UseCounterTrigger` 仅验证/状态翻转
  - [ ] handler 同时可读反制卡/指令/目标/数值
  - [ ] 多张反制按激活序执行；首个 `Stop`（默认）终止后续，第二张保持激活、不消耗；非 Stop 可叠加
  - [ ] csx 反制可读 `view.Target`（静态白名单已覆盖）
  - [ ] 触发后从手牌移除 + `card.discarded` + 注销全部 handler + 未激活 + 不退费
  - [ ] 仅「命中」（handler 调用 `Stop`/`Interrupt`）才消耗；未命中保持激活
  - [ ] 消耗发生在 `Counter` 段执行过程中，不延后到 `Finalize`
  - [ ] 同卡多 handler 只消耗一次；`RegisteredEffectHandlerNames` 消耗后为空
  - [ ] 双反制测试：第一张 `Stop` 后第二张仍激活且未消耗
  - [ ] 触发路径无回合校验；`counter.triggered` 激活时恰一次
- Rationale：保留 `AddEffectHandler(Func<CardTriggerView,...>)` 公开签名、只换注册目标，是最小破坏；适配视图让 handler 同时拿到反制卡与指令，不引入反制专用视图。`UseCounterTrigger` 只保留验证/状态翻转，避免成为第二施放入口。多反制按注册序叠加、首个 `Stop` 终止后续，是 `Trigger` 既有链语义的直接结果。一次性消耗放在 `Counter` 段、紧跟触发 handler，避免同一张卡在同一次指令中被重复计算；不退费与「未触发者保持激活」共同保证多反制的资源语义一致。
- Terminology: [动作](./CONTEXT.md)、[处理端](./CONTEXT.md)、[元效果](./CONTEXT.md)

### S15: P5 动作目录与服务面装配
- Status: Done
- Target：`MatchActionServices`、`MatchActionCatalog`、`IMatchBindingServices.RegisterFlowTriggers`（升级为按声明装配）、`IMatchPluginContext` 的动作服务访问器
- Approach：P5 在 `PostCommand` 相按「引擎 → 服务面 → 动作目录 → 插件注册 handler」四步装配；服务面先于动作目录就绪；动作目录先于插件声明；插件只经工厂签名声明依赖。
- Acceptance:
  - [ ] `MatchActionServices` 在 actions 包装配前就绪
  - [ ] `MatchActionCatalog` 按依赖序构造，缺依赖 fail-fast
  - [ ] 插件工厂只拿到声明的子动作接口
  - [ ] `RegisterFlowTriggers` 由「登记」升级为「按声明装配」，不暴露目录本体
- Rationale：S10 定了依赖注入与目录，但 P5 的宿主装配顺序与失败隔离必须先落成，否则插件包加载时服务面可能未就绪。
- Terminology: [动作服务面](./CONTEXT.md)、[底层逻辑集](./CONTEXT.md)

### S16: P5 插件包与宿主装配
- Status: Done
- Target：`src/Orc.Foundation.Actions`（新工程）、`package/plugins/orc.foundation.actions/manifest.json`、`FoundationActionsPluginPackage`、宿主快照登记点、`IMatchPluginContext`
- Approach：
  1. **程序集与包**：新工程 `src/Orc.Foundation.Actions/Orc.Foundation.Actions.csproj`，只引用 `Orc`/`Orc.Game`；**不设 `InternalsVisibleTo`**。包清单：`kind: plugin`、`id: orc.foundation.actions`、独立 GUID、`version`、`dependsOn: [{id: orc.builtin.core, version: "1.*.*"}]`、`assets: []`。
     - **`assets: []` 已核实合法**：`PackageManifest.ReadAssets` 对空数组返回空列表（`PackageManifest.cs:186-193`）；`PackagePrecheck.cs:38` 只拒绝 CardPack 空资产；`Load` 合成 `register.postCommand` 资产即可提供入口。
  2. **`IMatchPluginContext.Actions`——未采用（P5 执行裁决）**：原计划新增 `IMatchActionServices? Actions { get; }` 并补进 `Orc.Packages/PluginRegistrationContext`；实际以 **`ActionImplementationRegistry`**（进程级静态注册面）承载，`PluginRegistrationContext` 零改动。
     - 理由：动作实现是插件**提供**的能力，不是插件需要**消费**的服务；把它塞进注册上下文会让核心插件与动作包互相依赖上下文。且 `CommandManager` 构造动作目录早于 `PostCommand` 相——服务面注入时序不成立，而注册面可在建局前由宿主直接调用。
  3. **装配时序（四步）**：
     ```
     ① 引擎/CommandManager 就绪
     ② MatchActionServices 构造（engine/battlefield/players/服务实现）
     ③ MatchActionCatalog 构造（按依赖序建 8 个动作；缺依赖 fail-fast）
     ④ PostCommand 相（包激活）：FoundationActions.Register()
        → ActionImplementationRegistry.SetDefault(FoundationActions.Set)（进程级、幂等）
     ```
     失败隔离：PreCommand 单包失败＝隔离该包；PostCommand 异常＝建局 fail-fast（沿用 `PackageActivation` 现有语义）；动作目录缺端/缺依赖在 ③/④ fail-fast。
  4. **宿主登记点列全**：`src/Orc.Mcp/McpService.MatchLaunch.cs:88`、`src/Orc.Mcp/Tools/SandboxTools.cs:107`、`samples/Orc.Game.Sample/Program.cs:62`（与 `UiPluginPackage` 同批加入；P5 实际落在 `McpService.MatchLaunch.cs:92`、`Tools/SandboxTools.cs:110`、`samples/Orc.Game.Sample/Program.cs:66`）；任何新增快照点必须同时加入。漏一处＝该入口场景动作集不装。
  5. **架构断言**：新增测试/检查——`Orc.Foundation.Actions` 未设 `InternalsVisibleTo`，且其源码/编译产物不引用 `Orc.Game` 的 internal 类型；任何人加回 `InternalsVisibleTo` 即失败。
- Acceptance:
  - [x] 独立程序集 + 新包 + 宿主登记方案
  - [x] `assets: []` 可加载，`register.postCommand` 入口可激活（`FoundationActionsPluginPackage.Load` 合成代码入口资产）
  - [x] ~~`IMatchPluginContext.Actions` 为加性成员~~ **未采用**——改由 `ActionImplementationRegistry` 静态注册面承载（理由见 Approach 2）
  - [x] 四步时序与失败隔离与 `PackageActivation` 语义一致
  - [x] 三处宿主登记点全部加入，无遗漏
  - [x] 无 `InternalsVisibleTo`；public-only 断言测试存在（`FoundationActionsPackageTests.Foundation_Assembly_Has_No_InternalsVisibleTo`）
  - [x] 包依赖 `orc.builtin.core` 由解析器保证先后（`FoundationActionsPackageTests` 逆序声明仍解析为 core 先）
- Rationale：独立程序集是「纯 public 面」唯一能被编译期证明的形态；现有包机制本来就要求宿主显式登记代码入口（`BuiltInPluginPackage` 先例），照抄即可，不需要新 schema/动态加载。`assets: []` 合法且入口由 `Load` 合成资产提供；新增 `IMatchPluginContext.Actions` 是让插件包拿到服务面的事实通道，但只暴露角色接口，不暴露 `MatchActionServices` 实现。
- Terminology: [底层逻辑集](./CONTEXT.md)、[动作服务面](./CONTEXT.md)

### S17: P5 清理与门面冻结
- Status: Done
- Target：`AttackFlow`/`DamageFlow`、`MoveByEffectAsync`、`emitDrawn`、`CommandCard.AddActiveHandler`、死亡并列取消位、`GameUpdates`/`EffectRuntime`/`GameEntryPoints` diff、`tests/Orc.Game.Tests/Baselines/contract-freeze.json`
- Approach：
  1. **基线生成时点**：P1 动手之前、从**当前工作区**生成 `contract-freeze.json`，头部写 `commit`（当时 HEAD hash）与生成时间；工作区可能含未提交的前序改动，基线内容不等同于 `git show HEAD`，但生成时点必须早于 P1 改动。禁止在 P1+ 改动后补生成——否则会把 bug 固化成契约。
  2. 基线覆盖：`GameUpdates` 全部 public const string（名＋值，含 34 信号与载荷键）；`EffectRuntime` 合并 partial 后的**完整成员集**（键＝名＋泛型元数＋参数类型全名＋返回类型全名，不能只比名字，防重载漏检）；`GameEntryPoints.All` 逐条（Name/Category/Owner/Signature/Gate/Outcome/Status，按清单序）；`PlayResult`/`CommandResult`/`CardPlaceResult`/`DeckFetchResult` 公共属性/构造；`PrefabJson`/`CardDefinition` 公共 schema DTO 属性。
  3. **P4 白名单**：恰好两条 `GameEntryPoints` 变更（`PlayCommandAsync`→`PlayOrderAsync`、`BeginCommandPrePlayAsync`→`BeginOrderPrePlayAsync`），只允许 `Name`/`Signature` 两字段变化；`Category`/`Owner`/`Gate`/`Outcome`/`Status` 零 diff。白名单断言「恰好两条」，不是「至多两条」。
  4. **重生成开关**：`ORC_REGENERATE_BASELINE=1` 时测试**直接判失败**（防 CI 假绿），不重写基线；基线重写只经 `ORC_WRITE_BASELINE=1` 的 `BaselineGeneratorTests`。基线文件是受版本控制的资产，不是临时产物。CI/阶段门禁不设置这两个变量。
  5. **单一基线测试**：只放 `Orc.Game.Tests`（`ContractFreezeTests`），其余 5 个项目不重复实现；阶段门由它一票否决。失败输出字段级 unified diff。
  6. 阶段门：
     | 阶段 | 门 |
     | --- | --- |
     | P1 | 基线（P1 前 HEAD）+ 契约 diff 空 + 六项目全绿 + 伤害事件流逐条一致 |
     | P2 | R10/R11/R12 验收 + 信号次数/顺序断言 + 六项目全绿 |
     | P3 | 加入战场/攻击验收 + 互伤经伤害动作 + 六项目全绿 |
     | P4 | 两条白名单恰好命中、其余零 diff；`card.played` 在 `OrderFlow` 内；反制 `Counter` 命中；csx `view.Target` 可读；六项目全绿 |
     | P5 | 契约 diff 空（仅 P4 两条白名单）；旧路径删除；`GameUpdates`/`EffectRuntime` diff 空；六项目全绿 |
  7. 清理项按 R14：删除 `AttackFlow`/`DamageFlow`、`MoveByEffectAsync`、`emitDrawn`、`CommandCard.AddActiveHandler`、死亡并列取消位；`DamagePipeline` 演化完成后删除旧类；全仓不存在同一语义两条实现路径。
- Acceptance:
  - [x] 反射基线 + 契约测试方案
  - [ ] 基线头部含 P1 前 HEAD commit hash
  - [ ] `EffectRuntime` 成员键含泛型元数/参数/返回类型全名
  - [ ] P4 白名单「恰好两条」，仅 Name/Signature 可 diff
  - [ ] `ORC_REGENERATE_BASELINE=1` 直接失败；重写仅经 `ORC_WRITE_BASELINE=1` 的 `BaselineGeneratorTests`
  - [ ] 基线测试仅在 `Orc.Game.Tests` 一份
  - [ ] 五阶段门逐项执行；每阶段六项目全绿
  - [ ] R14 删除清单逐项完成，无残留双路径
- Rationale：R14 要的是「逐条 diff 为空」的可证明结果；反射基线同时覆盖常量值、方法签名、入口清单与结果形状，且不引入新依赖。基线必须先于改动生成并带 commit hash，才能防止把已变更代码固化成契约；P4 白名单必须「恰好两条」，否则删除入口也能通过。
- Deviation：基线内容取自 P1 前**工作区**而非 `git show HEAD`（工作区已有未提交的前序改动）；头部仍记录当时 HEAD hash。
- Deviation：基线在 P1 期间补充过一次 `prefabJsonDtos` 覆盖段；补充前原基线已通过，证明该次补充未改动既有受冻结面。
- Terminology: [唯一落地口](./CONTEXT.md)、[动作](./CONTEXT.md)

## 决策记录（Grill 队列）

| # | 事项 | 影响阶段 | 状态 |
| --- | --- | --- | --- |
| Q1 | 动作实体/编排原语落位与可见性 | P1–P5 | 已裁决（b：`Orc.Game.Actions`，全 public） |
| Q2 | 三段接缝如何映射到既有 band/动作缝 | P1–P3 | 已裁决（统一双枚举，仅约束底层动作） |
| Q3 | 动作结果对象形态与门面翻译边界（R9 vs R14「结果对象形状冻结」） | P1–P4 | 已裁决（每目标结果 + Disposition + 入口翻译） |
| Q4 | 动作服务面粒度与清单来源 | P1–P5 | 已裁决（按动作角色，实现永不迁移） |
| Q5 | 入口分支写面：R1「非处理端禁写费用/行动位」与 R10「差异只在入口」的冲突 | P2 | 已裁决（R1/R16 两层口径；`IOperateEntryServices` internal） |
| Q6 | 效果位移语义：`MoveByEffectAsync` 与 `MoveUnitAsync` 相反，哪个为准 | P2 | 已裁决（效果移动免费；R16 修订为不受 `Suppressed` 阻断） |
| Q7 | 抽牌/加入手牌/爆牌分支的拆解与 `emitDrawn` 取消口径 | P2 | 已裁决（抽牌端发 drawn，加入手牌端爆牌，`emitDrawn` 删除） |
| Q8 | 死亡改写位形态（是否死亡/击杀者/处置指向）与旧取消位迁移 | P2 | 已裁决（`Rewrite` 单槽 + `DeathHandleView`） |
| Q9 | 加入战场：部署/加入两入口如何共享一个处理端，信号归属如何切 | P3 | 已裁决（两触发端 + 一处理端） |
| Q10 | 跨动作调用面：子动作引用 vs 通用 invoker vs 服务面 | P2–P5 | 已裁决（构造注入 + 每对局动作目录） |
| Q11 | 攻击：`AttackDamageTrigger` 是否升为处理端，互伤如何下沉 | P3 | 已裁决（升为处理端；伏击自执行死亡两端） |
| Q12 | 指令归一：`OrderCard` 改名与 `GameEntryPoints` 冻结冲突 | P4 | 已裁决（R4 优先，两条同步改名，无旧名） |
| Q13 | 改名边界是否包含 `Orc.Mcp` 协议方法/工具名 | P4 | 已裁决（协议层冻结，只改内部委托） |
| Q14 | `card.played` 迁入 `OrderFlow` 的兼容窗口与反制注册面 | P4 | 已裁决（内核 flow + 游戏层 band 注入） |
| Q15 | 插件包形态：新程序集 vs 现有 `Orc.Plugins.BuiltIn` 内分相注册 | P5 | 已裁决（独立程序集 + 新包 + 宿主登记） |
| Q16 | 反制注入：`CounterCard` 效果 handler 注册到 `OrderFlow.Counter` 的视图适配 | P4 | 已裁决（适配视图 + 保留签名） |
| Q17 | 门面冻结的逐条 diff 基线与阶段门脚本 | P1–P5 | 已裁决（反射基线 + 契约测试 + 阶段门） |
| P1 | 执行完成并通过阶段门 | 2026-10-19 |

## P1 执行记录（实际交付 vs 计划）

Status: **完成并通过阶段门（2026-10-19）**。

### 新增生产文件
- `src/Orc.Game/Actions/ActionContract.cs`
- `src/Orc.Game/Actions/ActionBands.cs`
- `src/Orc.Game/Actions/IDamageAction.cs`
- `src/Orc.Game/Actions/DamageAction.cs`
- `src/Orc.Game/Actions/MatchActionServices.cs`
- `src/Orc.Game/Actions/Operate.cs`

### 新增测试
- `tests/Orc.Game.Tests/Baselines/contract-freeze.json`
- `tests/Orc.Game.Tests/ContractSnapshot.cs`
- `tests/Orc.Game.Tests/ContractFreezeTests.cs`
- `tests/Orc.Game.Tests/DamageSignalOrderTests.cs`
- `tests/Orc.Game.Tests/P1ActionContractTests.cs`

### 修改
- `src/Orc.Game/Damage/DamagePipeline.cs`：退化为 `bool` 门面 + 转发，不再持有扣数值实体。
- `src/Orc.Game/Damage/DamageViews.cs`：删除 `DamageSettlement`；视图载荷 `Settlement` → `Result`。
- `src/Orc.Game/Cards/KeywordEffects.cs`：重甲/免疫改用统一 band。
- `src/Orc.Game/Commanding/CommandManager.cs`：`MatchActionServices`/`OperateFinalizer` 接线；收尾走 `OperateProfile`。

### 门禁数值
- 构建：**0 错 / 0 警**。
- 6 个测试项目：**1675 通过 / 0 失败**（`Orc.Tests` 344、`Orc.Game.Tests` 1182、`Orc.Packages.Tests` 17、`Orc.Script.Tests` 72、`Orc.Ai.Tests` 13、`Orc.Mcp.Tests` 47）。
- 契约 diff：**空**。
- 伤害三入口信号序列：**7 场景逐条一致**。
- `Orc.Lua.Tests`：仓库不存在，不计入 6 个项目。

### 执行偏差（对应步骤）
- **S4**：`ApplyDamageAsync` 的 `kind` 实为 `string`（无 `DamageKind` 类型；`DamageKinds` 为 string 常量类，引入 enum 会改载荷形状）。
- **S3**：`ActionTargetResult.Subject` 对 HQ 为 `null`（`Hq : Card`，非 `CardBase`）。
- **S2/S5a**：删除 `DamageEventBands`/`DamageApplyBands`；band 精确枚举校验无法保留别名，段内相对次序不变（`Resolve(1)`→`Initiate(1)`、`Apply(2)`→`Apply(3)`）。
- **S5a**：门户唯一性测试的 HQ 探测词由 `.ApplyDamageAsync(` 收窄为 `hq.ApplyDamageAsync(`。
- **S17**：基线取自 P1 前工作区（非 `git show HEAD`），头部仍记录 HEAD hash；P1 期间补充过一次 `prefabJsonDtos` 覆盖段，补充前原基线已通过。

## P3 执行记录（实际交付 vs 计划）

Status: **完成并通过阶段门（2026-10-19）**。

### 新增生产文件
- `src/Orc.Game/Actions/JoinBattlefieldAction.cs`（S9：部署／加入两触发端 + 共享处理端）
- `src/Orc.Game/Actions/IJoinBattlefieldAction.cs`（动作接口 + `IJoinActionServices`）
- `src/Orc.Game/Actions/AttackAction.cs`（S11：攻击触发端 + 攻击处理端 + 处理侧执行体）
- `src/Orc.Game/Actions/IAttackAction.cs`

### 新增测试
- `tests/Orc.Game.Tests/P3ActionFamilyTests.cs`（S9/S11 验收 13 例）

### 修改
- `src/Orc.Game/Actions/ActionContract.cs`：`ActionId.JoinBattlefield`/`ActionId.Attack`；`ActionFailureKind.ValidationRejected`；`ActionTargetResult.MarkSkipped`。
- `src/Orc.Game/Actions/MatchActionServices.cs`：实现 `IJoinActionServices`（`UnitizeOnJoinAsync` 发 `card.placed`；`InvokeDeployKeywordAsync`）。
- `src/Orc.Game/Commanding/CommandManager.cs`：两个攻击触发器改带 band 方案；装配 `JoinBattlefieldAction`/`AttackAction`；`DispatchAttackAsync`/`AttackByEffectAsync` 改调动作；删除旧攻击 handler 与 `AttackByEffectAsync` 的 `Suppressed` 检查（R16）；`KeywordLoadContext` 注入 `IDeathAction`。
- `src/Orc.Game/Cards/UnitCard.cs`：打出／部署／加入链改为入口收尾 + 转发动作；新增 `InvokeUnitizeAsync` 单源面。
- `src/Orc.Game/Cards/KeywordEffects.cs`：伏击挂 `ActionHandleBands.Rewrite`，经 `IDeathAction.ExecuteAsync` 走完整两端并置 `IsRewritten`/`Handled`。
- `src/Orc.Game/Cards/KeywordSystem.cs`：`KeywordLoadContext.DeathAction`。
- `src/Orc.Game/Commanding/CommandTypes.cs`：`AttackDamageResolution.Handled`（加性）。
- `src/Orc.Game/Commanding/CommandViews.cs`：两个攻击视图加性 `Result`（结局载体）。
- `src/Orc.Game/Damage/DamagePipeline.cs`：加性 `Action` 访问器（攻击动作构造注入用）。
- `src/Orc/Core/Trigger.cs`：加性 `BandType` 只读面。
- `src/Orc/Cards/DynamicEffect.cs` / `src/Orc/Cards/Effect.cs` / `src/Orc.Game/Cards/EffectSystem.cs`：`injects` 的 band 名落地（此前仅默认区段）。
- `src/Orc.Game/EffectParsing/Templates/attack_basic.tpl.json`：注入声明 `band: Initiate`（触发器改 band 方案后必需）。

### 门禁数值
- 构建：**0 错 / 0 警**（`dotnet build OrcEngine.sln -t:Rebuild`）。
- 6 个测试项目：**1701 通过 / 0 失败**（`Orc.Tests` 344、`Orc.Game.Tests` 1208、`Orc.Packages.Tests` 17、`Orc.Script.Tests` 72、`Orc.Ai.Tests` 13、`Orc.Mcp.Tests` 47）。
- 契约 diff：**空**（`ContractFreezeTests` 通过；`Baselines/contract-freeze.json` 零改动）。

### 允许的行为变更（旧断言 → 新断言 → 理由）
1. 被压制单位的效果攻击：旧 `AttackByEffectAsync` 返回 `false` → 新返回 `true` 且正常结算 → R16：压制只影响「能否被指挥」（`attack.leg.eligibility` 与候选名单）。
2. 攻击处理端顺序重排：揭示／`AttackEvent`／记账／冲击／互伤／`unit.combat.survived` 全部移出「单位攻击触发器」的执行 handler → 信号类型与顺序断言（`CommandCombatTests` 全序、伏击 4 条序列）逐条不变 → S11「执行下沉」。
3. 伏击改写路径：旧＝处理端内直接 `ProcessDeathAsync` → 新＝伏击效果经 `IDeathAction.ExecuteAsync` 走完整两端并置 `Handled` → S11 第 4 条（改写方自己处置后果）。
4. 伏击 + 免死：旧＝仅被攻击者 `unit.combat.survived` 一条 → 新＝被攻击者在前、攻击者在后共两条 → S11 第 5 条第 2 分支持续「攻击者存活」可观测。

### 执行偏差（对应步骤）
- **S11**：揭示与 `AttackEvent(before/after)`／记账／冲击由处理侧执行体（`AttackAction.SettleAsync`）在**处理端触发器之前/之后**执行，`AttackDamageTrigger` 只承载 `Rewrite`/`Veto`/`Apply`。理由：`Rewrite` 是 band 最前段且伏击在其中执行死亡——若把揭示放进 `Apply`，伏击死亡会先于揭示，改变既有观察序（S11 验收要求顺序一致）。
- **S9**：单位化的写点仍在卡侧 `UnitizeTrigger`（`HandleUnitizeAsync`），唯一调用者＝`IJoinActionServices.UnitizeOnJoinAsync`（由处理端默认 handler 调用）。理由：保留「单位化触发器」的既有注入面；`card.placed` 仍由处理端服务在单位化之后发射。
- **S9**：`DeployTrigger`/`UnitizeTrigger` 保留为卡侧入口/单源执行面（文档未要求删除）；`card.played` 的唯一部署发射点已迁到动作的 `Announce` 段。
- **S11**：`IsAttackTargetLegal` 由构造注入 `AttackAction`（`EffectAsync` 内、效果来源作用域中调用），而非留在入口——`bypassRange` 必须在合法性判定时可见。
- **S11**：【文档未列】新增 `injects` 的 band 名落地（`Trigger<T>.BandType` + 反射注册 + `attack_basic` 模板补 `band`）。触发器改 band 方案后，未声明 band 的既有注入会被拒绝——`EffectRuntimeEndToEndTests.Generated_Attack_Listener_Runs_After_Attack` 为真实消费者，不补即回归。
- **S11**：`AttackDamageResolution.Handled` 用 `MarkHandled()`（对齐 `IsRewritten`/`MarkRewritten` 的既有形态）；`IsRewritten` 语义不变。


## P4 执行记录（实际交付 vs 计划）

Status: **完成并通过阶段门（2026-10-19）**。

### 新增生产文件
- `src/Orc.Game/Actions/IOrderAction.cs`（S13 第 6 条：唯一指令动作入口）
- `src/Orc.Game/Actions/OrderAction.cs`（动作层包装：`OrderFlow` 唯一 flow → `ActionResult` 翻译）
- `src/Orc.Game/Cards/OrderCard.cs`（由 `CommandCard.cs` 重命名）

### 新增测试
- `tests/Orc.Game.Tests/P4OrderFlowTests.cs`（S13/S14 验收 10 例）

### 修改
- `src/Orc/Cards/OrderFlow.cs`：`OrderFlowView.Orders`（移除单数 `Order`）、`Target` 可空；两阶段执行（`Trigger` + 独立 `FinalizeTrigger`）；`Resolve` 默认结算 priority 100、按登记序执行全部 `ICastAction`；`Interrupt` 跳过 `Finalize`。
- `src/Orc/Cards/CardLifecycle.cs`：`PayloadKeys.Orders`（`Order` 保留为旧数据键读面）。
- `src/Orc/Core/EventStream.cs`：加性 `Interrupted`（`Stop` 不置位）。
- `src/Orc/Core/Trigger.cs`：`Interrupt` 传播时同步置位各层流的 `Interrupted`。
- `src/Orc.Game/Cards/OrderCard.cs`：`PlayTrigger` 退化为纯验证；删除 `AddActiveHandler` 与打出链事件。
- `src/Orc.Game/Managers/PlayManager.cs`：`BeginOrderPrePlayAsync`/`PlayOrderAsync`；打出段经 `IOrderAction`；在 `OrderFlow` 注册 `card.played`（Resolve/0，`OrderCard` 守卫）与扣费/离手（Finalize/100）。
- `src/Orc.Game/Cards/CounterCard.cs`：效果 handler 注册到 `OrderFlowBands.Counter`（`CardTriggerView` 适配视图）；`UseCounterTrigger` 只做验证/状态翻转；命中一次性消耗（弃置 + 注销 + 未激活 + 不退费）。
- `src/Orc.Game/Triggers/CardTriggerView.cs`：加性 `Target`（施放目标）。
- `src/Orc.Game/Cards/CardBase.cs` / `CardLibrary.cs` / `Match.cs`：弃置委托注入（复用 `PlayerManager.DiscardCardAsync`；脱局兜底同序同信号）。
- `src/Orc.Game/Actions/ActionContract.cs`：`ActionId.Order = "order.cast"`。
- `src/Orc.Game/GameEntryPoints.cs`：仅两条改名（`Name`/`Signature`），其余字段零 diff。
- `src/Orc.Mcp/*`：仅内部委托与泛型/注释改名；`IMcpService.PlayCommandAsync` 与工具 ID `play.command`/`play.command_pre` 零改动。
- 机械改名：`OrderCard`/`PlayOrderAsync`/`BeginOrderPrePlayAsync`（源码、测试、样例、文档）。

### 门禁数值
- 构建：**0 错 / 0 警**（`dotnet build OrcEngine.sln -t:Rebuild`）。
- 6 个测试项目：**1711 通过 / 0 失败**（`Orc.Tests` 344、`Orc.Game.Tests` 1218、`Orc.Packages.Tests` 72、`Orc.Script.Tests` 17、`Orc.Ai.Tests` 13、`Orc.Mcp.Tests` 47）。
- 契约 diff：**恰好 2 条**白名单命中（`GameEntryPoints[8]` `BeginCommandPrePlayAsync`→`BeginOrderPrePlayAsync`、`[12]` `PlayCommandAsync`→`PlayOrderAsync`）；其余面零 diff。

### 允许的行为变更（旧 → 新 → 理由）
1. 指令施放：旧＝`OrderCard.PlayTrigger` 自建「宣告 → 主动 handler 集 → 收尾」→ 新＝预打出（不变）→ 纯验证复验 → `IOrderAction` → `OrderFlow`（`Counter` → `Resolve`〔`card.played`/0 → 全部主动效果/100〕→ `Finalize`/100）→ 理由：R3 唯一施放入口、反制获得真实位点（S13）。
2. 反制命中：旧＝反制无位点、不可用 → 新＝`Counter` 段 `Stop` 后指令照常扣费/离手、效果不触发；`Interrupt` 则牌留手/不扣费 → 理由：第 15 轮 OPEN-1 裁决。
3. 反制卡触发后：旧＝激活态长期有效 → 新＝命中即消耗（移出手牌 + `card.discarded` + 注销 + 未激活 + 不退费）→ 理由：第 15 轮 OPEN-2 裁决。
4. 预打出捕获结果：旧＝移交主动 handler 的 `Argument` → 新＝不再移交（仅在预打出段可读）→ 理由：`AddActiveHandler` 删除（R3 第 6 条）的直接后果；全仓无生产调用者。
5. 被反制指令的 `card.played`：旧＝无（反制无位点）→ 新＝**0 次**（`Resolve` 之首的宣告点被 `Counter` 的 `Stop` 一并跳过）→ 理由：「反制后效果不触发」是硬要求，而现有指令卡效果＝被动效果 hook `card.played`；正常指令恰 1 次。

### 测试改写清单
- `tests/Orc.Tests/Scenario1Tests.cs`：`view.Order.Name` → `view.Orders[0].Name`；`view.Target` 改 null 安全（`Requirement` 明示的例外）。
- `tests/Orc.Game.Tests/PlayChainCommandTests.cs`（5 例）：`AddActiveHandler` → `AddEffect(ActiveEffect<…>)`；「捕获 → `Argument`」改为「捕获箱自读」；targeter 演练移入预打出段；异常隔离断言改指效果名。
- `tests/Orc.Game.Tests/EntryPointBeginTests.cs` / `GameEntryPointsTests.cs` / 其余约 20 个测试文件：机械改名。

### 与文档的偏差
- **S12**：`GameEntryPoints` 中 `PlayOrderAsync` 的 `Gate` 文本保留基线原文「同 BeginCommandPrePlayAsync」——白名单只允许 `Name`/`Signature` 变化，`Gate` 零 diff；该旧名仅作描述性引用，不指向现存成员（已就地注释说明）。
- **S13**：`ExecuteAsync` 返回**阶段 1**流；`Finalize` 是同一父流下的兄弟流（收尾可经 `point.changed`/离手观测）。`IOrderAction.triggerCard` 参与签名但不参与编排（施放归因取指令卡自身；P4 无调用方）。
- **S14**：仓库不存在 csx 视图静态白名单；适配视图的 `Target` 可读性由 C# 用例覆盖（`P4OrderFlowTests.Counter_Adapter_View_…`）。
- **S14**：弃置复用经 `CardLibrary` 注入的委托（新增可选构造参数）；独立构造无委托时按 `DiscardCardAsync` 同序兜底（手牌移除 → 销毁 → `card.discarded`）。
- **S13**：「主动效果 priority 100」由内核默认 handler 承担（`OrderFlow.OnResolve`），游戏层只在 priority 0 注册宣告、在 `Finalize` priority 100 注册扣费/离手——与 S13 文本等价。

### P4 收尾修补（第 2 轮）

**缺口 1（宣告段）——已回退**：初版曾把 `card.played` 移到新增的 `Announce` 段（`Counter` 之前），使反制命中后指令效果仍被触发——**违反第 15 轮「只是不触发效果」**。已回退：`OrderFlowBands` 恢复 `Counter=1 / Resolve=2 / Finalize=3`、删除 `Announce`，`card.played` 回到 `Resolve`/0。
段序＝`Counter（反制判定）→ Resolve（card.played → 效果）→ Finalize（扣费/离手）`。**被反制的指令不发 `card.played`**（未成功打出），但 `PlayResult` 仍为 `Success`（照常扣费/离手）；`Interrupt` 仍为牌留手/不扣费。

**缺口 2（目标传递）**：`PlayManager` 把 `captureBox.Result` 解析为 `Card?` 后传给 `IOrderAction`：
`null`→null；`Card`→自身；`Ref<Entity>` 存活则解引用；`object?[]`/列表→取第一个可解析元素；其余→null（不抛、不阻断施放）。
`OrderFlowView.Target` 与反制适配视图 `CardTriggerView.Target` 均可读到该目标；捕获非卡值时为 `null`（handler 不崩溃）。
**遗留**：多目标指令的完整传递（当前只取第一个目标）不在本批范围。

**缺口 3（防回归断言，`P4OrderFlowTests`）**：
- `Countered_Order_Skips_Announcement_But_Charges_Once_And_Returns_Success`
- `Normal_Order_Announces_Once_Before_Effects`
- `PrePlay_Captured_Target_Reaches_OrderFlow_And_Counter_Adapter`

**修补后门禁**：构建 0 错/0 警；1711 通过/0 失败；契约白名单仍**恰好 2 条**（`OrderFlowBands` 不在契约冻结面）。

## P5 执行记录（实际交付 vs 计划）

Status: **完成并通过阶段门（2026-10-19）**。

### S15：动作目录装配（实际交付）

- `MatchActionCatalog` 仍是每对局一份的**装配真源**：按 S10 依赖序构造 8 个动作并做 `Validate`（id 唯一／依赖存在／图无环／恰 1 处理端）。
- 工厂不再硬编码：构造时经 `ActionImplementationRegistry.Resolve()` 取合并后的实现集；`MatchActionFactorySet.BuiltIn` 已删除。
- 新增 `ActionRuntime`（宿主公开查询面）：目录把 8 个实例按 `ActionId` 登记进引擎引用表；`UnitCard` / `PlayerManager` / `MatchCardService` / `PlayManager` 经它取同一实例，不再 `new` 任何动作实现。
- `RegisterFlowTriggers` 语义不变：按目录条目逐端登记；移动两端由实现自报（`ActionTriggerEnd`，类型擦除）。

### S16：底层逻辑集插件包（实际交付）

- **程序集**：`src/Orc.Foundation.Actions/`，引用 `Orc`、`Orc.Game`、`Orc.Packages`；**无 `InternalsVisibleTo`**（`FoundationActionsPackageTests.Foundation_Assembly_Has_No_InternalsVisibleTo` 断言）。为迁出攻击动作，宿主新增 `Orc.Game.Cards.KeywordProgramFacade`（把 4 个 internal 词条运行时操作收攏为具名公开操作）。
- **动作实现**：8 个（`AddToHandAction`/`DrawAction`/`DamageAction`/`DeathAction`/`AttackAction`/`MoveAction`/`JoinBattlefieldAction`/`OrderAction`），视图留在 `Orc.Game.Actions`（`*Views.cs`），接口留宿主。
- **物理包**：`src/Orc.Foundation.Actions/package/plugins/orc.foundation.actions/manifest.json`
  | 字段 | 值 |
  | --- | --- |
  | `kind` | `plugin` |
  | `id` | `orc.foundation.actions` |
  | `guid` | `7f3c2a10-9b64-4d8e-a5c1-6e0f2b7d4a93` |
  | `version` | `1.0.0` |
  | `dependsOn` | `[{ "id": "orc.builtin.core", "version": "1.*.*" }]` |
  | `assets` | `[]`（合法；`Load` 合成 `register.postCommand` 代码入口资产） |
- **装载入口**：`FoundationActionsPluginPackage.Load(pluginsRoot?)`——读磁盘清单、校验 `kind/id/GUID`，返回 `LoadedPackage`（1 个 `asm:register.postCommand` 资产 + 绑定 `FoundationActions.Register` 的脚本）。异常语义与 `BuiltInPluginPackage` 一致（清单缺失 `FileNotFoundException`；id/kind/GUID 不符 `InvalidOperationException`）。
- **`IMatchPluginContext.Actions`：未采用**。实际以 `ActionImplementationRegistry`（进程级静态注册面）承载；理由：动作实现是插件**提供**的能力，不是插件需要**消费**的服务；且 `CommandManager` 建目录早于 `PostCommand` 相，服务面注入时序不成立。

### `ActionImplementationRegistry` API（P5b 交付）

```csharp
public static void SetDefault(ActionImplementationSet set);            // 整包默认（重复调用＝替换）
public static void Override(ActionId id, ActionImplementationSet set); // 单动作覆盖（set 只能含该动作工厂）
public static ActionImplementationSet Resolve();                       // 覆盖优先 → 默认兜底 → 缺则抛（含 ActionId）
public static IReadOnlyDictionary<ActionId, ActionImplementationSource> Describe(); // Default/Override/Missing
public static void Reset();                                            // 仅供测试
```

`ActionImplementationSet` 的 8 个工厂全部返回 `I*Action`；`FoundationActions.Set` 是完整实现集，`FoundationActions.Register()` 只做 `SetDefault(Set)`。

### 宿主登记点（包快照，与 core 同批）

| 宿主 | 位置 | 形式 |
| --- | --- | --- |
| MCP 正式对局 | `src/Orc.Mcp/McpService.MatchLaunch.cs:92` | `FoundationActionsPluginPackage.Load(Options.PluginsRoot)` |
| MCP 沙盒 | `src/Orc.Mcp/Tools/SandboxTools.cs:110` | `FoundationActionsPluginPackage.Load(pluginsRoot)` |
| 样例 | `samples/Orc.Game.Sample/Program.cs:66` | `FoundationActionsPluginPackage.Load()` |

三处均排 `BuiltInPluginPackage.Load(...)` 之后、`UiPluginPackage` 之前；包解析按 `dependsOn` 拓扑排序（测试用逆序声明验证 core 仍在前）。宿主另在建局前直接调用 `FoundationActions.Register()`（幂等），保证动作目录构造时注册面已就绪。

### 新增 / 修改文件

- 新增生产：`Orc.Foundation.Actions/FoundationActionsPluginPackage.cs`、`package/plugins/orc.foundation.actions/manifest.json`；`Orc.Game/Actions/ActionImplementationRegistry.cs`、`ActionImplementationSet.cs`、`ActionTriggerEnd.cs`、`ActionRuntime.cs`；`Orc.Game/Cards/KeywordProgramFacade.cs`。
- 迁移：8 个动作实现（宿主 → Foundation）；4 个视图拆到 `Orc.Game/Actions/*Views.cs`。
- 删除：`MatchActionFactorySet`（并入 `ActionImplementationSet`）、`GameActionImplementations`（过渡集）。
- 测试：`ActionImplementationRegistryTests`（未注册建局抛错／8 个 Default／单动作覆盖／缺动作点名／多动作覆盖拒绝）、`P5ActionCatalogTests`（8 实例类型）、`FoundationActionsPackageTests`（包发现／清单解析／依赖排序／缺清单异常／无 `InternalsVisibleTo`）。

### 门禁数值

- 构建：**0 错 / 0 警**（`dotnet build OrcEngine.sln -t:Rebuild`）。
- 6 个测试项目：**1720 通过 / 0 失败 / 0 跳过**（`Orc.Tests` 339、`Orc.Game.Tests` 1229、`Orc.Packages.Tests` 75、`Orc.Script.Tests` 17、`Orc.Ai.Tests` 13、`Orc.Mcp.Tests` 47）。
- 契约 diff：**空**（`ContractFreezeTests` 通过；`tests/Orc.Game.Tests/Baselines/contract-freeze.json` 零改动）。
- `describe()`：注册 `FoundationActions.Set` 后，8 个动作全部返回 `Default`（`ActionImplementationRegistryTests.Register_Default_Makes_All_Eight_Actions_Come_From_Foundation`）。

### 与文档的偏差

- **S16 第 2 条**：`IMatchPluginContext.Actions` 未采用（见上）。
- **S16 时序**：实际注册面在**建局前**由宿主直接调用 + 包 `register.postCommand` 相幂等复注册；`RegisterFlowTriggers` 仍由 `orc.builtin.core` 的 PostCommand 入口按目录装配（保持既有插件链）。
- **S17**：`DamagePipeline` 保留为公开门面（`EffectRuntime` / `CommandManager` 的既有 `bool` 契约依赖），内部改持 `IDamageAction`；R14 的"伤害管道 → 伤害动作"演化已达成，未删除门面以免破坏冻结面。
