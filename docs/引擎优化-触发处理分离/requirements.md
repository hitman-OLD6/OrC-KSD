# Requirements — 触发-处理分离与指令归一

把"一次动作"拆成**触发端**（施动者视角）与**处理端**（承受者视角），为元效果提供稳定接缝；把底层逻辑集收进独立插件；并消除「指令（Order）」在引擎层与游戏层的双轨实现。

Status: **Draft — 需求 grill 已收敛，待用户定稿确认**

---

## 0. 现状基线（代码事实，非需求）

### 0.1 已存在三套互不统一的"分离"实现

| # | 位置 | 形态 | 使用情况 |
| --- | --- | --- | --- |
| 1 | `src/Orc.Game/Damage/DamagePipeline.cs` | 3 触发端（`combat.damage`/`effect.damage`/`battle.damage`）→ 1 处理端（`damage.apply`）；处理端是唯一调用数值门户处 | **在用** |
| 2 | `src/Orc/Cards/BaseFlows.cs` | `AttackFlow`（发起方）→ `DamageFlow`（承受方） | **游戏层零使用** |
| 3 | `src/Orc/Cards/OrderFlow.cs` | `OrderFlow`（Counter → Resolve → Finalize） | **游戏层零使用** |

`AttackFlowBands` 与 `OrderFlowBands` 成员名、成员值完全相同（`Counter=1/Resolve=2/Finalize=3`）；`AttackFlowView` 与 `OrderFlowView` 只差一个 `Order` 属性——纯复制粘贴。

### 0.2 游戏层四个"流程触发器"仍是触发-处理混合体

`src/Orc.Game/Commanding/CommandManager.cs:306-315`：

- `CommandTrigger`：读判定 → 交互 → 分派 → 嵌套执行 → 外层收尾（全在 `HandleCommandFlowAsync` 一个 handler 内）
- `UnitMoveTrigger`：复验（`Validate`）＋执行段（槽位变更 + 发 `unit.position.changed`）
- `UnitAttackTrigger`：揭示 + HQ 简路/互伤分派 + 词条记账 + 冲击消耗
- `AttackDamageTrigger`：互伤结算 + 反击资格 + 死亡判定 + 存活信号

这 4 个触发器**没有 band 方案**（`new Trigger<T>(...)` 未传 `bandType`），效果连具名接缝都没有。

### 0.3 指令双轨（用户陈述第 2 点，成立）

- `CommandCard.cs:36-51` 自建 `PrePlayTrigger`/`PlayTrigger`，链＝`打出宣告(card.played)` → 主动 handler 集 → `打出收尾(扣费/离手)`。
- 指令卡效果**实际靠"被动效果监听 `card.played`"**实现（`docs/卡牌DIY开发文档/examples/01-火力支援.md` §2/§4），不经过 `OrderFlow`。
- `CommandCard.AddActiveHandler`（`:88`）**全仓无调用者**——"主动 handler 集"是空槽。
- `OrderFlow.Counter` band 没有任何指令经过 → **反制无位点**；`CounterCard` 只把 handler 注册回自己的 `UseCounterTrigger`（`CounterCard.cs:127`）。
- 术语待办已登记：`.ams/context/orc-engine/CONTEXT.md:325`。
- 数据侧**已经用 order**：`samples/card-pack-diy/cards/diy.fire-support.card.json` → `"type": ["order"]`。

### 0.4 点名动作的现状

| 动作 | 当前承载 | 分离程度 |
| --- | --- | --- |
| **加入战场** | `PlayManager.PlayUnitAsync`/`JoinUnitAsync` → `card.PlayTrigger`/`card.JoinTrigger` | 无：验证＋宣告＋入槽＋单位化＋收尾同在一个触发器 |
| **移动** | `CommandTrigger` → `UnitMoveTrigger` | 发起端在**管理器方法**里、不在触发器；承受端无独立触发器 |
| **攻击** | `CommandTrigger` → `UnitAttackTrigger` → `AttackDamageTrigger` | 部分：伤害有独立管道，攻击本身仍混结算 |
| **撤退** | 无独立动作：`KeywordProgram` 的 `retreat` op 直调 `MoveByEffectAsync` | 无 |

### 0.5 「卡牌移动」现有四条路径（副作用各不相同）

| 路径 | 入口 | 过 `UnitMoveTrigger` | 扣行动费 | 清行动位 | `unit.acted` |
| --- | --- | --- | --- | --- | --- |
| 被指挥移动 | `DispatchMoveAsync`（`CommandManager.cs:1050`） | ✅ | ✅ | ✅ | ✅ |
| 被控移动 | `MoveByEffectAsync`（`:1413`）→ 直调 `ApplyPositionChangeAsync` | ❌ **完全绕过** | ❌ | ❌ | ❌ |
| 无头移动 | `MoveUnitAsync`（`:1465`）→ `DispatchMoveAsync` | ✅ | ✅ **（效果移动也扣费清位）** | ✅ | ✅ |
| 撤退（词条 op） | `KeywordProgramComponent.cs:217-226` → `MoveByEffectAsync` | ❌ | ❌ | ❌ | ❌ |

**⚠ bug 级矛盾**：`MoveUnitAsync` 与 `MoveByEffectAsync` 都是"效果发起的移动"，行为相反。实现阶段必须先判明哪个才是正确语义。

### 0.6 抽牌与加入手牌的现状

- 抽牌＝`PlayerManager.DrawCard`（`:233`）；加入手牌＝`MatchCardService.PlaceToHandAsync`（`:240`）。
- 满手裁决经 `HandLimitBurn` 统一，但**爆牌发生在"加入手牌"之前**（`card.hand.add` 零次），并以 `emitDrawn` 参数区分两种口径（`HandLimitBurn.cs:15-16`）。
- 「加入手牌」另有创建入牌、回手、孵化等调用者（`ProgramKeywordComponent.cs:208/275/287`）。

### 0.7 死亡链

`ProcessDeathAsync`（`CommandManager.cs:1508`）现有**两个并列取消位**：`DeathInterceptor`（`:1515`）与 `KeywordProgramRuntime.BeforeDestroy`（`:1516`），均在 `IsDestroyed = true` 之前。

### 0.8 与用户陈述的矛盾点（已对齐）

用户陈述"把触发和执行的 handler 放在两个触发器内"。
代码事实：**多数动作目前连"动作实体"都不存在**。
→ 结论（已确认）：先引入统一的「动作实体」层。

---

## 1. Scope

### Includes

1. 8 个动作实体的两端化：加入战场、卡牌移动（撤退/被控/被指挥 3 入口）、攻击、指令施放、伤害结算、死亡、抽牌（卡组顶/指定 2 入口）、加入手牌。
2. 元效果三段接缝（发起前 / 宣告后 / 落地前）；落地后沿用既有总线信号。
3. 底层逻辑集插件化：独立包 `orc.foundation.actions`（依赖 `orc.builtin.core`，纯 public 面）。
4. 指令归一：`CommandCard`→`OrderCard`、`OrderFlow` 为唯一施放入口、反制进 `Counter` band。
5. 内部破坏性整理（R14 清单）。

### Excludes（逐条理由）

| 排除项 | 理由 |
| --- | --- |
| 老兵升级 | 两端同体（卡→卡），元效果无区别可挂；已有单一入口 `VeteranRules` |
| 揭示（Reveal） | 同上；`CovertRules.RevealAsync` 已是唯一标准动作 |
| 弃置（Discard） | 施动/承受同在"玩家手牌"一侧；已有 `HandLimitBurn.DestroyAndEmitDiscardedAsync` 单一入口 |
| 爆牌（Burn） | **不是独立动作**——是「加入手牌」的失败分支（R11） |
| 点数 / 槽位增减 | 两端同体（玩家→玩家）；已有 `ResourceManager.ChangePointsAsync` 单一入口 |
| 洗牌 / 回合推进 | 无承受方 |
| 换牌（Mulligan） | 准备相位专属，不参与对局动作流 |
| UI / 投影层 | 只消费信号与投影 |
| 效果解析器 / 卡牌数据 schema | 不改 |

## 2. Constraints

1. **信号冻结**：`GameUpdates` 全部 34 个信号名与载荷键零改动。
2. **门面冻结**：`EffectRuntime.*` 公共签名、`GameEntryPoints` 清单、`PlayResult`/`CommandResult` 形状不改。
   **例外（R4 明示）**：指令改名同步更新 `GameEntryPoints` 的两条登记（`PlayCommandAsync`→`PlayOrderAsync`、`BeginCommandPrePlayAsync`→`BeginOrderPrePlayAsync`）；**条目数、类别、Owner、门禁、返回形态、顺序全部不变**。
3. **回归门**：6 个测试项目（约 1597 个测试方法）每阶段结束必须全绿。
4. **不引入不必要的机制**：只用现有 `Trigger`/band/包装器/总线；不新增 Source Generator。
   **例外（第 17 轮裁决）**：为「底层逻辑集」的可替换性，允许新增一个**动作实现注册面**（进程级、单一真源、可枚举），除此之外不新增机制。
5. **插件非安全隔离**：同进程、可反射；与"弱沙箱"口径一致。

---

## 3. Requirement Items

### R1: 动作两端与唯一落地口（Confirmed · 第 1 轮；第 5 轮修订为两层口径）
- Behavior: 一次动作由**触发端**（施动者视角）与**处理端**（承受者视角）构成。**唯一落地口按两层划分**：
  - **动作状态**＝动作目标的位置 / 槽位 / 数值 / 处置 / 归属 → **只能由处理端写**，非处理端禁写。
  - **行动状态**＝费用 / `CanMove` / `CanAttack` / `unit.acted` → **只能由入口分支经具名操作写**，处理端禁写。
  - （第 5 轮修订理由：R4 已把「行动」划为资源与次数层且不设实体；R1 原表述把「费用/行动位」混入动作状态，与 R10「差异只在入口分支表达」冲突。本次修订恢复三者一致，不改变既有决策。）
- Acceptance:
  - [ ] 两端为独立触发器，可分别注入
  - [ ] 触发端被拒绝/中断时，处理端不发生且零副作用
  - [ ] **架构不变量（动作状态）**：非处理端不得写位置/槽位/数值/处置/归属
  - [ ] **架构不变量（行动状态）**：处理端不得写费用/`CanMove`/`CanAttack`/发 `unit.acted`
  - [ ] 现存旁路全部收敛：`ApplyPositionChangeAsync`、`MoveByEffectAsync`、`MoveUnitAsync`、`LeaveBattlefieldAsync`、`state.Position` 直写

### R2: 元效果三段接缝（Confirmed · 第 13 轮）
| 段 | 位置 | 能做什么 |
| --- | --- | --- |
| **发起前** | 触发端 | 阻止发起、改代价、改目标、改来源 |
| **宣告后** | 触发端末 / 处理端之前 | 反制、重定向、复制、禁止反制 |
| **落地前** | 处理端内 | 改结果（数值/归属/处置指向）、阻止落地 |
| 落地后 | 总线信号（34 条） | 普通效果，不新增机制 |

- Acceptance:
  - [ ] 三个段可独立注入且互不干扰
  - [ ] "反制先于减伤"由段落表达，不依赖注册顺序
  - [ ] "禁止反制"作用于宣告后段且早于反制判定

### R3: 指令归一（Confirmed · 第 9 轮）
- Behavior:
  1. `CommandCard` → **`OrderCard`**；`CommandManager`/`CommandTrigger`/`CommandResult` 不改名。
  2. **`OrderFlow` 是唯一施放入口**：打出链 → `ExecuteAsync` → `Counter` → `Resolve`（先发 `card.played` 兼容被动指令效果，再执行主动效果〔若有〕）→ `Finalize`。
  3. 反制注入 `OrderFlowBands.Counter`。
  4. **反制命中的语义（第 15 轮用户裁决）**：指令**照常扣费、照常从手牌移除**，只是效果不触发。即 `Counter` 段用 `ctx.Stop()`（只停 `Resolve`），`Finalize` 的扣费/离手**照常执行**。
  5. **反制卡一次性（第 15 轮用户裁决）**：反制卡触发后即消耗——从手牌移除并走弃置路径（`card.discarded`），不退费；触发一次后不再对后续指令生效。多张激活反制中未触发者保持激活。
  6. `CommandCard.AddActiveHandler` 删除。
  7. 收敛终点：数据迁完后 `Resolve` 不再用 `card.played` 承载效果。
- Acceptance:
  - [ ] 任意指令施放都经过 `OrderFlow`，无旁路
  - [ ] 反制在 `Counter` 拦截后，指令效果不发生且留痕；**扣费与离手照常发生**
  - [ ] 反制卡触发后从手牌移除、发 `card.discarded`、不退费，且不再对后续指令生效
  - [ ] 现有指令卡数据零改动可运行
  - [ ] `card.played` 发射点位于 `OrderFlow` 执行流内

### R4: 命名与术语统一（Confirmed · 第 9/14 轮）
- Behavior:
  - `CommandCard`→`OrderCard`、`PlayCommandAsync`→`PlayOrderAsync`、`BeginCommandPrePlayAsync`→`BeginOrderPrePlayAsync`。
  - `CommandManager`/`CommandTrigger`/`CommandDataKeys`/`CommandResult` **保留**（指指挥）。
  - **「行动」（operate）＝攻击与移动这两个指挥操作的统称**，是资源与次数的计量单位（行动费/行动位/`unit.acted`），**不设显式声明与实体**。
  - **「动作」（action）＝上位词**。行动 ⊂ 动作，二者不混用。
  - **协议层例外**：`Orc.Mcp` 的 `IMcpService.PlayCommandAsync` 与工具 ID `play.command`/`play.command_pre` 属**外部协议契约**，不改名；仅内部委托改为 `PlayOrderAsync`。它只是转发，不是第二条实现路径。
- Acceptance:
  - [ ] 文与码中「行动」不指动作、「动作」不指行动费/行动位
  - [ ] `unit.acted` 信号名不变
  - [ ] MCP 工具 ID 与 `IMcpService` 方法名零改动

### R5: 动作实体（Confirmed · 第 6 轮）
- Behavior: 每个动作有显式实体（id、视图类型、两端触发器、结果对象）；**不设「单位行动」触发器**。
- Acceptance:
  - [ ] 8 个动作实体可寻址、可由插件声明与实例化
  - [ ] 缺任一端的动作在装配期 fail-fast

### R6: 底层逻辑集（插件承载）（Confirmed · 第 2/12 轮）
- Behavior:
  - 动作两端触发器与默认 handler 的定义权归插件；对局按声明实例化。
  - 包＝`orc.foundation.actions`（独立包，依赖 `orc.builtin.core`，纯 public 面）。
  - 触发器实例**每对局一份**；插件只持工厂与 handler 引用。
  - handler 不得持有 `CommandManager`/`PlayManager`。
- Acceptance:
  - [ ] 插件声明动作及两端触发器，对局按声明装配
  - [ ] `RegisterFlowTriggers` 由"登记"升级为"按声明装配"
  - [ ] 该包不引用 `internal`

### R7: 动作服务面（Confirmed · 第 2 轮）
- Behavior: 受控**具名写操作**（扣费/移动落地/清行动位/改数值…），不交出管理器实例；清单为单一真源、可枚举。
- Acceptance:
  - [ ] handler 不持有管理器
  - [ ] 写面为具名操作，无万能句柄
  - [ ] 操作清单可从代码枚举

### R8: 动作构成（Confirmed · 第 4 轮）
- Behavior: **可变 N 触发端 + 恰 1 处理端**，有序链；每环可阻断后续。
- Acceptance:
  - [ ] 触发器序列可装配期枚举与校验
  - [ ] 任一环阻断后续时零落地

### R9: 编排与结果（Confirmed · 第 6 轮）
- Behavior: **分支在入口、结果在底层**。入口是完整分支（自身前置 → 调底层 → 自身收尾）；底层只做统一前置 + 处理端落地，返回显式结果对象（成功/被阻止/目标失效/被改写）；多结果＝对不同目标分别返回。
- Acceptance:
  - [ ] 底层动作内无"按入口类型分支"
  - [ ] 多目标能分别返回结果
  - [ ] 结果对象不依赖异常传递

### R10: 卡牌移动族（Confirmed · 第 5/6 轮）

```
卡牌撤退 ┐
卡牌被控 ├→ 卡牌移动（前置）→ 移动处理端（唯一落地）
卡牌被指挥┘
```
- Behavior: 三入口走同一底层分支；差异（扣费/清位/发 `unit.acted`/方向约束）只在入口分支表达；处理端零 if。
- **前提（第 16 轮修订）**：代价只属于「被指挥」这一入口——移动动作本身不消耗任何资源；压制只影响"能否被指挥"，不阻断动作本身（见 R16）。
- Acceptance:
  - [ ] 三入口副作用差异有显式对照表
  - [ ] 处理端无"按入口类型分支"
  - [ ] 4 条路径收敛为 1 条落地
  - [ ] 效果移动（撤退/被控）不扣指挥点、不动行动位、不因被压制而失败

### R11: 抽牌与加入手牌（Confirmed · 第 7/8 轮）

```
抽卡组顶 ┐
抽指定牌 ┴→ 抽牌（从卡堆移除）→ 加入手牌（子动作）→〔满手〕爆牌分支
```
- Behavior: 两入口共用同一抽牌处理端；差异只在触发端。「加入手牌」是独立可复用动作。爆牌＝其失败分支；`emitDrawn` 双口径取消。
- Acceptance:
  - [ ] 两入口走同一抽牌处理端
  - [ ] 加入手牌可被非抽牌路径复用
  - [ ] 满手时 `card.hand.add` 与 `card.burned` 发射次数有断言

### R12: 死亡两端（Confirmed · 第 8 轮）
- Behavior: 处理端可改写**是否死亡**、**击杀者归属**、**处置指向 `Disposition`**；处置由**入口分支**执行，处理端不调其他动作。两个并列取消位收敛为一个改写位。
- Acceptance:
  - [ ] "免死"有留痕、零状态改动
  - [ ] "死亡改为回手"经 `Disposition` 返回，由入口分支调「加入手牌」
  - [ ] 击杀者改写反映在 `card.died` 载荷
  - [ ] 不存在两个并列取消口

### R13: 覆盖范围（Confirmed · 第 7/8 轮）
- Behavior: 纳入 8 个动作实体；排除项见 §1 Excludes。
- Acceptance:
  - [ ] 每个纳入项有触发端与处理端，或明确为"单端 + 结果分支"
  - [ ] 每个排除项写明"已有等价接缝"

### R14: 删除策略与门面冻结（Confirmed · 第 10 轮）
- 删：`AttackFlow`/`DamageFlow`、`MoveByEffectAsync`、`emitDrawn`、`CommandCard.AddActiveHandler`、死亡并列取消位之一
- 演化：`DamagePipeline` → 伤害动作实现
- 冻结：`EffectRuntime.*` 签名、`GameEntryPoints`、卡牌 schema、`GameUpdates` 信号与载荷、结果对象形状
  - 例外：R4 明示的两条指令改名同步登记（见 Constraints 2）
- Acceptance:
  - [ ] 全仓不存在"同一语义两条实现路径"
  - [ ] `GameUpdates` 逐条 diff 为空
  - [ ] `EffectRuntime` 公共签名 diff 为空

### R15: 交付节奏（Confirmed · 第 11 轮）
| 阶段 | 内容 | 准出 |
| --- | --- | --- |
| **P1 原语 + 伤害样板** | 动作实体、结果对象、失败类别、编排原语；`DamagePipeline` 迁为"伤害动作"（**行为零变化**） | 全绿 + 信号 diff 空 |
| **P2 移动族 + 抽牌族** | 卡牌移动（3 入口）、死亡两端、加入手牌、抽牌（2 入口+爆牌分支） | R10/R11/R12 验收 |
| **P3 加入战场 + 攻击** | 部署/加入、攻击（互伤下沉到伤害动作） | 同上 |
| **P4 指令归一** | `OrderCard`、`OrderFlow` 施放链、反制进 `Counter` | R3/R4 验收 |
| **P5 插件化 + 清理** | 底层逻辑集迁出到 `orc.foundation.actions`、动作服务面、R14 删除 | R6/R7/R14 验收 |

- Acceptance:
  - [ ] 每阶段 6 个测试项目全绿
  - [ ] 阶段之间不产生"半新半旧"的第三条路径

### R16: 代价与压制的归属（Confirmed · 第 16 轮）
- Behavior:
  1. **代价属于「指挥」，不属于「移动/攻击」动作**。指挥点、行动位（`CanMove`/`CanAttack`）、`unit.acted` 都是"玩家指挥单位"这一行为的产物；移动与攻击动作本身不消耗任何资源。
  2. **压制（`Suppressed`）改变的是"单位能否被指挥"**，落在指挥入口的资格/候选判定（`move.leg.eligibility` / `attack.leg.eligibility` 及候选名单），**不阻断动作本身**：效果发起的移动/攻击不受压制影响。
- Acceptance:
  - [ ] 效果移动、效果攻击均零指挥点、零行动位改动、零 `unit.acted`，且不被压制阻断
  - [ ] 被压制单位的指挥入口被拒（移动与攻击两动作同时不可用），拒绝原因可辨识
  - [ ] 压制判定只出现在指挥资格/候选面，不出现在动作的触发端或处理端
- 修订说明：本轮推翻第 6 轮「效果移动检查 `Suppressed`」的裁决——压制不是"不能移动"，而是"不能被指挥"。

### R17: 底层逻辑集的可替换性（Confirmed · 第 17 轮）
- Behavior: 8 个动作的**实现**可被整体或在单个动作粒度上替换，且替换不触及调用方（入口、服务面、信号、结果对象）。
  1. **整包替换**：提供一个完整实现集，作为进程级默认。
  2. **单动作替换**：按 `ActionId` 覆盖某一个动作，其余仍用默认。
  3. **装配期校验**：缺实现、依赖缺失、依赖成环、两端不完整 → fail-fast（不静默降级）。
  4. **可观测**：每个 `ActionId` 当前生效的实现来源可查询（默认集 / 覆盖 / 来源标识）。
  5. **契约不变**：替换实现不得要求调用方变化；`I*Action` 接口、服务面、结果对象、信号均为替换的固定边界。
- Acceptance:
  - [ ] 不注册任何实现集时，装配期明确失败（错误信息指出缺哪个动作）
  - [ ] 注册默认集后可正常建局；覆盖单动作后，只有该动作使用新实现
  - [ ] 单动作覆盖一个依赖他人的动作时，依赖仍指向未覆盖的实现
  - [ ] 来源查询能指出每个动作的实现来源
  - [ ] 替换实现后 6 个测试项目仍全绿（内置集作为默认）
- Terminology: 底层逻辑集、动作实体、动作实现集、实现注册面

---

## 4. 定稿清单

| 决策 | 结论 | 轮次 |
| --- | --- | --- |
| 处理端定位 | **唯一落地口** | 1 |
| 插件形态 | 独立包 `orc.foundation.actions`，纯 public 面 | 2 / 12 |
| 骨架 | **不建通用骨架**，多触发器（C） | 3 / 4 |
| 动作构成 | 可变 N 触发端 + 1 处理端 | 4 |
| 撤退/行动 | 撤退＝移动入口；行动无实体 | 5 / 14 |
| 编排 | 入口分支 + 底层具名结局 | 6 |
| 覆盖范围 | 8 个动作 | 7 / 8 |
| 死亡改写 | 是否死亡 + 击杀者 + 处置指向 | 8 |
| 指令 | `OrderFlow` 唯一入口 + 渐进收敛 | 9 |
| 删除策略 | 内部破坏性 + 门面冻结 | 10 |
| 交付 | 5 阶段，每阶段全绿 | 11 |
| 元效果接缝 | 三段式 | 13 |
| 用词 | 行动＝资源/次数；动作＝上位词 | 14 |
| 代价与压制 | 代价归指挥；压制＝能否被指挥 | 16 |
