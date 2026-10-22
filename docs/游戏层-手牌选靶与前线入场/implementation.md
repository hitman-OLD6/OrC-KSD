# Implementation — 游戏层：手牌选靶与前线入场

实现盘问进行中。需求（requirements.md）已全部对齐：R1/R2/R2a/R2b 手牌选靶、R3/R3a/R3b/R3c 加入战场两触发端、R4/R4a 阵线改成有序单位列表（`Slot` 退场）。以下步骤按依赖排序，Approach 逐轮填入。

## Scope

- Target: `src/Orc.Game/Board/*`（阵线模型）、`src/Orc.Game/Actions/*`（加入战场）、`src/Orc.Game/Managers/PlayManager.cs`、`src/Orc.Game/Judicators/*`、`src/Orc.Game/Effects/EffectRuntime*.cs`、`src/Orc.Game/Targeting/*`、外围 `Orc.UI` / `Orc.Mcp` / `Orc.Game/Sandbox` / `Orc.Ai` 适配。
- Excludes: 前端视觉实现；效果 DSL 新 op；`Orc.Server` 座位/资源槽（与位置无关的同名项）。

## Implementation Steps

### S1: 阵线模型改为有序单位列表

- Status: Done（Board 层）／连带改造进行中
- Target: `Board/BattleLine.cs`、`Board/Battlefield.cs`、`Board/Slot.cs`（删除）、`Board/FormationGap.cs`
- Approach: `BattleLine` 内部改为 `List<CardBase>`；`Capacity = 5`（元素数上限，HQ 计入）；`Insert(index, card)` / `Remove(card)` / `IndexOf(card)`；删除 `Slot` 类型；`FormationGap` 保持 `Entity`（targeter 候选需要 `Ref<Entity>`），承载「线 ＋ 插入序号 ＋ 快照代数」；`GetInsertionGaps()` 产出 `N+1` 个空隙（含首尾）。初始 HQ 位于列表第 0 位。
- Acceptance:
  - [ ] HQ 在最左、右侧 4 个单位＝合法；元素数 5 时 `GetInsertionGaps()` 为空。
  - [ ] 插入后其他元素**不**重排、序号按列表顺序连续。
  - [ ] 旧候选（快照被打破）不可用于落地。
- Rationale: 需求 R4 定案"阵线＝有序列表，插入不居中"；`FormationGap` 已是"临时空隙"语义的自然载体。
- Terminology: [阵线](../CONTEXT.md)

### S2: 位置表示法改造

- Status: In Progress（`UnitStateData.Line` / `HqStateData.Line` / `Hq.Line` / `GameEnvironment` / `BoardPosition` 已改；其余调用点待改）
- Target: `Cards/DataComponents.cs`（`UnitStateData.Position`）、`Players/HqComponents.cs`（`HqStateData.Position`）、`Board/GameEnvironment.cs`、`Actions/MatchActionServices.cs`
- Approach: `Position` 改存**所在线**（`BattleLine?`）；序号一律 `line.IndexOf(card)` 现算；落地＝`line.Insert(gap.Ordinal, card)`；`state.Position?.Occupant` 类读法改为"经线查询该单位是否仍在该线"；`FormationGap` 仍是 Entity（候选需 `Ref<Entity>`）。相邻判定（`GameEnvironment.GetAdjacentUnits`）、守护（`CommandManager.MaintainHqGuard`）、范围/拦截（`Combat*Judicator`）统一改为"列表相邻（序号差 1）／线归属现算"。
- Acceptance:
  - [ ] 单位/HQ 的"所在线"与列表内容始终一致（无过期位置）。
  - [ ] 相邻/守护/范围/拦截在插入、移除、跨线移动后仍正确（列表相邻，非槽位下标）。
  - [ ] 序列化/读取路径不再出现"存量序号"。
- Rationale: 列表是唯一真源；n≤5 使反查零成本；避免"临时却可引用"的位置对象（Q5 已否）。
- Terminology: [阵线](../CONTEXT.md)

### S3: 部署候选改由判定器产出

- Status: Todo
- Target: `Judicators/`（新判定器＋注册）、`JudicatorNames.cs`、`GameHooks.cs`、`Match/Match.cs`（注册段）、`Managers/PlayManager.cs`
- Approach: 新增受控判定器 `deploy.position.candidates`，签名 `(Player owner, UnitCard unit) → IReadOnlyList<FormationGap>`（跨线整体，含"能否上前线"决策）；`PlayManager.DeploymentLines` 退场；三处同步（`JudicatorNames` 常量 / `GameHooks` 转发常量＋`_names` / `Match.Initialize` 固定注册段）。
- Acceptance:
  - [ ] 候选数＝N+1（N<5）、满员＝0。
  - [ ] 判定器可被包装器改写且生效。
- Rationale: 需求 R4a。
- Terminology: [判定器](../CONTEXT.md)

### S4: 指挥移动改为插入语义

- Status: Todo
- Target: `Commanding/CommandMovementRules.cs`、`Commanding/CommandManager.cs`、`Actions/IMoveAction.cs`、`src/Orc.Foundation.Actions/MoveAction.cs`、`Actions/MatchActionServices.cs`（`LandMoveAsync`）
- Approach: 移动候选＝目标线的**插入空隙**（含首尾）；落地＝`Remove` → `Insert(gap.Ordinal)`；`Destination`/`OldPosition`/`NewPosition` 载荷由槽位引用换成"线＋序号"（或 gap）；移动后右侧元素顺移，`unit.position.changed` 照常发。
- Acceptance:
  - [ ] 移动可插到任意空隙；右侧元素序号顺移且不再有"空槽"残留。
  - [ ] 旧候选（快照失效）被拒绝。
  - [ ] 推进入口/复验用同一候选来源（无第二真源）。
- Rationale: 需求 I7＝a；列表模型下"空槽"概念不存在。
- Terminology: [前线](../CONTEXT.md)

### S5: 加入战场动作与两触发端改造

- Status: Todo
- Target: `Actions/IJoinBattlefieldAction.cs`、`JoinBattlefieldViews.cs`、`src/Orc.Foundation.Actions/JoinBattlefieldAction.cs`、`MatchActionServices.cs`、`Managers/PlayManager.cs`、`Match/Match.cs`、`Match/MatchCardService.cs`
- Approach: 动作层把 `Join` 拆为 `JoinFrontline` / `JoinSupportLine`（共享 `Handle`）；视图载荷 `Slot` → `FormationGap`（线＋插入序号＋快照代数，失效即拒绝）；落地改为列表插入；落位规则＝显式序号 > 守护（HQ 右侧）> 默认最左；失败一律不抛（敌方占线 Error / 满员 Warning）；三处 `Join` 调用点同步。
- Acceptance:
  - [ ] 守护单位落 HQ 右侧；显式序号优先；越界失败。
  - [ ] 敌方占线＝Error 留痕＋失败；满员＝Warning 留痕＋失败；均不抛。
- Rationale: 需求 R3/R3a/R3b/R3c。
- Terminology: [加入战场](../CONTEXT.md)

### S5: 效果侧两个加入战场触发端

- Status: Todo
- Target: `Effects/EffectRuntime*.cs`（新分部文件）
- Approach: 暴露「加入前线」与「加入支援阵线」两个具名触发端，内部复用 join.battlefield 动作；csx 侧提供便捷调用。
- Acceptance:
  - [ ] 两触发端可调用；发 `unit.joined`、不发 `unit.deployed`/`card.played`；不扣费。
  - [ ] 已单位化＝失败、零副作用。
- Rationale: 需求 R3。
- Terminology: [触发器](../CONTEXT.md)

### S6: 手牌选靶（两层）

- Status: Todo
- Target: `Targeting/`（新公共辅助）、`Effects/EffectRuntime*.cs`
- Approach: 公共辅助走 `SelectorTemplates.Hand`＋`ReferenceSetParameter`（候选＝手牌快照、域判定"仍在手牌"）；`min > 手牌数` ⇒ 直接 `Cancelled`（零交互）；`min == 手牌数` 仍发起交互；`max` 收敛为手牌数；`EffectRuntime` 层返回**结果对象**（三态 `Ok/Cancelled/Failed` ＋ 牌列表，`Ok + 空列表`＝空选成功），纯选择不附带弃牌。
- Acceptance:
  - [ ] 超量＝`Cancelled` 且未产出选择器。
  - [ ] `min == 手牌数` 仍等玩家提交/取消。
  - [ ] 越界/非法＝可重试；取消零副作用。
- Rationale: 需求 R1/R2/R2a/R2b。
- Terminology: [目标选择](../CONTEXT.md)

### S7: 外围适配与测试基线

- Status: Todo
- Target: `Orc.UI`（`UiCardView.SlotIndex` 现算）、`Orc.Mcp`（`slotIndex` 语义＝当前序号）、`Sandbox/*`、`Orc.Ai/*`、`tests/Orc.Game.Tests/*`（`SlotModelTests` 重写）
- Approach: 对外序号统一改为"当前列表序号"（写入时是插入后序号）；投影/协议字段名不变、语义改为序号；失效候选拒绝。
- Acceptance:
  - [ ] 全量 `dotnet test OrcEngine.sln` 通过。
  - [ ] `SlotModelTests` 改为列表语义锚点（HQ 可最左、N+1 空隙、满员无候选）。
- Rationale: 保持前端/协议字段不破坏，只改语义。
- Terminology: [投影](../CONTEXT.md)

## 进度快照（2026-10-22）

- 已完成（生产代码全绿：dotnet build OrcEngine.sln 中 src/ 与 samples/ 零错误）：
  - S1 阵线有序列表（BattleLine/FormationGap/Battlefield 重写，删除 Slot）
  - S2 位置表示（UnitStateData.Line/HqStateData.Line/Hq.Line/GameEnvironment/新增 BoardPosition）
  - S4 加入战场动作拆两端（JoinFrontline/JoinSupportLine/JoinAtAsync，视图载荷 FormationGap）
  - S4b 移动改插入语义（LandMoveAsync/MoveAction/CommandMovementRules/移动复验）
  - S7 外围适配（UI 投影线宽与位置、MCP slotIndex/CardResolver/ShapeTools/KindOf、沙盒、示例桥接）
- 未完成（测试迁移）：
  - `tests/Orc.Game.Tests` 仍按旧槽位模型编写：17 处 `Slot` 类型错误修正后会暴露数百处语义假设（`GetSupportLine(x)[i]` 返回元素而非槽位、`state.Position`、`slot.IsEmpty`、`FindEmptySupportSlot` 等），需逐文件改写，非机械替换可解。
  - 当前测试目录已恢复为版本库状态（不残留半改代码），全量 `dotnet test` 未执行。
  - 完成进度：S1/S2/S3/S4/S4b/S5/S6/S7(生产侧) 已完成；仅测试迁移未完成。
