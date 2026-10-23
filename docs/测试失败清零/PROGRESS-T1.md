# 进度 — T1 片（57 条）

- 更新时间: 2026-10-23T22:10:00+08:00
- 工具调用计数: ~95
- 已处理: 57/57（全部转绿，无 Skip、无删 `[Fact]`）
- 总体状态: 已完成

## 基线（开工实测）

- 命令：`dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --artifacts-path D:/OrC-KSD/artifacts/test-t1 --filter <T1 16 类>`
- 开工：**失败 57 / 通过 85 / 跳过 0 / 总计 142**（与 `artifacts/split/T1.md` 逐条一致）；trx：`artifacts/trx-t1/t1-base.trx`
- 收工：**失败 0 / 通过 142 / 跳过 0 / 总计 142**；trx：`artifacts/trx-t1/t1-c.trx`
- 全项目回归（同一 `--artifacts-path`）：**失败 27 / 通过 1327 / 跳过 0 / 总计 1354**（S3 基线 175）。
  与 S3 trx 逐条比对：**新增失败 0 条**，其余 148 条转绿（含本片 57 ＋ 其它片 91）。trx：`artifacts/trx-t1/t1-full.trx`

## 逐条结论（用例 → 归因 → 改了什么）

### 组 A：插入点序号越界（41 条）— 测试过期（布置按"物理 5 格槽位"写）

根因同一：旧模型 5 格阵线上"槽 2/槽 3"是**空格子**，S2 拆钳制后 `At(index)` 越界即抛。有序列表模型里"第 N 位"必须先有 N 个元素；
支援线 HQ 占位置 0，所以"支援线第一个单位"＝插入点 1，"第一个单位"之后才是 2、3。改法一律为**按序铺单位 / 改用合法插入点**，
期望值与命题未改；前线与支援线分属不同线，"第一个元素"取插入点 0。

| 用例 | 改动 |
| --- | --- |
| `JudicatorCombatLegalityTests::Unit_Branch_Range_Default_Equivalence` | 敌前线 1→0；敌支援 2→1 |
| `JudicatorCombatLegalityTests::Guard_Stage_Unit_Default_Equivalence` | A 支援 infantry 2→1（artillery 保留 2＝现在合法） |
| `JudicatorCombatLegalityTests::Guard_Stage_Hq_Default_Equivalence` | A 支援 artillery 2→1 |
| `JudicatorCombatLegalityTests::Five_Type_Coverage_Typeless_And_Qualification_Group` | 敌支援 2→1 |
| `JudicatorCombatLegalityTests::Adjacent_Group_And_Typeless_Coverage` | 敌前线 1→0；敌支援 2→1 |
| `JudicatorCombatLegalityTests::Range_Moding_Penetrates_Combinator_And_Both_Chains` | 敌支援 2→1 |
| `JudicatorCombatLegalityTests::Guard_Eligibility_Moding_Penetrates_Combinator` | A 支援 infantry 2→1 |
| `JudicatorCombatLegalityTests::Target_Legal_Moding_Affects_Both_Chains` | 敌支援 2→1 |
| `JudicatorCombatLegalityTests::Boundary_Invalid_Reference_And_Unknown_Entity_Return_False` | 敌前线 1→0 |
| `JudicatorCombatLegalityTests::Sub_Rule_Entries_Resolvable_And_Directly_Invocable` | 见组 E（含 `combat.range` 载荷） |
| `JudicatorCombatLegalityTests::Standalone_CommandManager_BuiltIn_Defaults_Consistent` | 敌前线 1→0；敌支援 2→1 |
| `CommandRulesTests::Range_From_Support_Line_Only_Enemy_Front_Units_Are_Targets` | 敌前线 1→0；敌支援 2→1 |
| `CommandRulesTests::Range_From_Front_Line_Enemy_Support_Units_And_Hq_Are_Targets_But_Not_Same_Line` | 敌支援 2→1 |
| `CommandRulesTests::Range_Typeless_Behaves_As_Infantry_Baseline` | 敌支援 2→1 |
| `CommandRulesTests::Range_Mixed_Type_Applies_Rules_Independently` | 敌支援 2→1 |
| `CommandRulesTests::Tank_Can_Move_And_Attack_In_Same_Turn_DoubleAction` | 敌支援 2→1 |
| `CommandRulesTests::Non_Tank_After_Move_Cannot_Attack_Exclusive_Choice` | 敌支援 2→1 |
| `CommandRulesTests::Guard_Protects_Hq_And_Guardian_Itself_Is_Never_Guarded` | A 支援 artillery 2→1；注释按新拓扑重述 |
| `CommandRulesTests::Guard_Does_Not_Cross_Lines_And_Requires_Physical_Adjacency` | 重述布置：守护者位置 1、近邻位置 2、远位位置 3（"隔空"改为"隔一个元素"，判定式"索引差 1"未变） |
| `CommandRulesTests::Guard_Hq_Requires_Adjacent_Guardian_Not_Blocked_By_Gap` | 重述布置：HQ(0)＋中间单位(1)＋守护者(2) ⇒ 与 HQ 差 2 → 不获守护（原"落槽 4 隔空槽"无对应物） |
| `CommandRulesTests::Guard_State_Maintained_On_Join_Move_And_Death_And_Multiple_Sources` | 重述布置：X 先入场(0) → G1 插到左侧(0) → G2 追加(2) = [G1,X,G2]；后半 [Y,H,HQ]：Y(0)→H(1) |
| `CommandCombatTests::Mutual_Destruction_Emits_Two_Died_With_Target_First` | 敌支援 2→1 |
| `CommandCombatTests::Combat_Survived_Skips_Dead_Participant` | 敌支援 2→1 |
| `CommandCombatTests::Dead_Attacker_Still_Resolves_As_Success_With_Fee_And_Flag_Updates` | 敌支援 2→1 |
| `CommandCombatTests::Ambush_Not_Triggered_When_Condition_Fails_Falls_Back_To_Default_Damage` | 敌支援 2→1 |
| `CommandCombatTests::Ambush_No_Longer_Rewrites_After_Death` | 敌支援 2→1；探针 A 支援 3→1 |
| `CommandFrontLineTests::Advance_Is_Blocked_While_Enemy_Occupies_Front_And_Recovers_After_Clear` | mover 2→1、beast 1→2；候选断言改为"前线全部插入空隙" |
| `CommandFrontLineTests::Advance_Execution_Revalidation_Rejects_When_Enemy_On_Front` | 敌前线 1→0；oldSlot 改为 `BoardPosition`；`Assert.Same(mover, support[1])`；`Assert.Empty` → `DoesNotContain(mover, front.Members)`（见组 C） |
| `CommandFrontLineTests::Bomber_Interception_Blocks_Non_Fighter_Targets_On_Fighter_Line` | 敌支援 2→1 |
| `CommandFrontLineTests::Bomber_Interception_Requires_Living_Fighter` | A 支援 bomber 2→1 |
| `CommandFrontLineTests::Non_Bomber_Attackers_Ignore_Fighter_Interception` | A 支援 fighter 2→1 |
| `CommandImmunityTests::Artillery_Target_Counterattacks_When_Attacked_By_Infantry` | 敌支援 2→1 |
| `CommandImmunityTests::Attacker_Artillery_Receives_No_Counterattack` | 敌支援 2→1 |
| `CommandImmunityTests::Attacker_Bomber_Is_Countered_Only_By_Fighter_Target` | 敌支援 2→1 |
| `CommandSystemTests::Attack_Unit_Resolves_Mutual_Damage_And_Finalizes_Flags` | 攻方前线 1→0；敌支援 2→1 |
| `CommandSystemTests::Begin_Rejects_Owner_Missing` | `JoinUnitAsync` 插入点 3→1（紧随 HQ） |
| `CommandSystemTests::Interaction_Candidates_Are_Union_And_Off_Candidates_Are_Rejected_Until_Valid` | 敌前线 3→0；敌支援 2→1 |
| `CommandSystemTests::Move_Emits_PositionChanged_Once_With_Frozen_Payload` | 目标空隙 2→0 |
| `CommandUpdateTests::Attack_Without_Deaths_Emits_StatChanged_For_Both_Sides` | 敌支援 2→1 |
| `CommandUpdateTests::Move_Emits_Only_Position_Changed` | 目标空隙 3→0 |
| `CommandLockInTests::Command_Targeter_Never_Offers_Support_Line_Interior_Slots` | 敌支援 2→1；允许集/负面断言改为"前线空隙"＋"不含任何支援线插入点" |
| `BattleKeywordTests::BattleKeyword_CardReads_Filter_Count_And_Set` | victim 支援 2→1 |
| `JudicatorAssemblyTests::KeywordRuleOverrideIsAModingEntryAndUnregisterRestoresTheBaseline` | 敌前线 1→0；见组 E |
| `JudicatorCombatCounterAmbushTests::Counter_Eligibility_Default_Terms_One_To_Five_Entries` | 见组 F（敌单位改落敌方支援线） |
| `JudicatorCombatCounterAmbushTests::Counter_Eligibility_Moding_Syncs_Default_Damage_And_Ambush_Qualification` | 见组 F |

### 组 B：位置序号越界（`line[i]` 取元素越界，8 条）— 测试过期

"第 i 个元素"必须先存在；旧断言枚举"5 个物理格子"在新模型无对应物，改为**插入空隙面**（语义等价：全部可用落点）。

| 用例 | 改动 |
| --- | --- |
| `CommandFrontLineTests::Advance_Remains_Available_When_Front_Holds_Only_Friendly_Units` | `front[1..4]` → `front.GetInsertionGaps().Select(g=>g.Ref)` |
| `CommandLockInTests::Move_Candidates_Never_Contain_Support_Line_Interior_Slots` | 同上；`aSupport[0/2/3]`/`bSupport[0/2]` → "候选不含任何支援线插入点" |
| `CommandLockInTests::Front_Line_Unit_Has_No_Move_Candidates_And_No_Same_Line_Products` | `front[1]/front[2]` → "候选不含前线自身插入点"（候选为空已蕴含） |
| `EntryPointBeginTests::BeginMoveAsync_Confirms_And_Executes_Move_With_Slot_Parameter` | `front[0]` → 前线插入点 0；见组 C |
| `CommandCombatTests::Hq_Damage_Is_Clamped_To_Zero_Without_Counterattack` | `Assert.Same(hq, line.Gaps.At(0))` → `Assert.Same(hq, line[0])`（组 C） |
| `CommandFrontLineTests::Advance_Execution_Revalidation_...`、`CommandRulesTests::...`、`Judicator...` | 见组 A |

### 组 C：`Assert.Same` 槽位实体错位（4 条）— 测试过期（S2 已定型的 A-1 模式）

`UnitStateData.Line` 是 `BattleLine`；旧断言拿"槽位/空隙"与它比。
- `EntryPointBeginTests::BeginMoveAsync_Cancel_Leaves_Zero_Side_Effect`：`Assert.Same(gap, ...Line)` → `Assert.Same(supportLine, ...Line)`
- `EntryPointBeginTests::BeginMoveAsync_Confirms_And_Executes_Move_With_Slot_Parameter`：`Assert.Same(front[0], ...Line)` → `Assert.Same(front, ...Line)`
- `OperateCostRecheckTests::Increase_Blocks_Availability_And_Revalidation`：同上
- `CommandCombatTests::Hq_Damage_Is_Clamped_To_Zero_Without_Counterattack`：同上（HQ 占位者）

### 组 D：目标集未收敛（2 条）— 测试辅助过期（`AllRefsOf` 超集重复）

`JudicatorTargetSelectionTests::Full_Selection_Flow_Default_Rule_Filters_Through_Judicator`、
`JudicatorTargetSelectionTests::Forbid_Infantry_Wrapper_Removes_Class_And_Falls_Back`：
实参与期望差在"每个单位出现两次"（`[infA,tankA,infB,infA,tankA,infB]`）。
根因：`CommandTestKit.AllRefsOf` 的"位置元素＋在场单位＋双方 HQ"三面枚举在有序列表模型下枚举的是**同一批实体**（HQ 也在支援线上），
超集变成多重集。**过滤器本身正常**（非产品缺陷）。改：`AllRefsOf` 按实体去重（`ReferenceEqualityComparer`）。

### 组 E：复验/子规则载荷类型错（3 条）— 测试过期（载荷口径）

`OldPosition` 现为 `BoardPosition`（已成事实的位置）；`combat.range`／`combat.interception` 第 2 参同为 `BoardPosition`。**生产签名零改动**（基线事实 10）。
- `JudicatorValidationTests::Move_And_Attack_Recheck_Are_Independently_Modable`：`oldSlot` 空隙 → `new BoardPosition(supportLine, IndexOf(unit))`
- `JudicatorValidationTests::Standalone_CommandManager_Binds_BuiltIn_Recheck_Defaults`：同上
- `JudicatorCombatLegalityTests::Sub_Rule_Entries_Resolvable_And_Directly_Invocable`：C2/C4 目标改 `new BoardPosition(unit.Line!, unit.Line!.IndexOf(unit))`（**未**用 `gap.Ordinal`）；措辞"目标槽位"→"目标位置"
- `JudicatorAssemblyTests::KeywordRuleOverrideIsAModingEntryAndUnregisterRestoresTheBaseline`：同上（原传 `BattleLine`）

### 组 F：布置不可满足导致的"看起来像产品错"（2 条）— 测试过期（布置错）

`JudicatorCombatCounterAmbushTests::Counter_Eligibility_Default_Terms_One_To_Five_Entries`（拆前就已红，形态 `Expected [False] / Actual [True]`）、
`::Counter_Eligibility_Moding_Syncs_Default_Damage_And_Ambush_Qualification`（拆前形态 `Expected Success / Actual Cancelled`）：
旧布置把**双方单位都放前线**，而产品门禁「前线被敌方存活单位占据 ⇒ 加入前线被拒绝」使敌单位**静默未入场**
（`JoinFrontLine.ValidateFrontlineAsync`），于是条款①/③读不到类型 → 误判；攻击指令也因无合法目标被取消。
**属布置错，不是产品缺陷**（与基线事实 6 同形状）。改：敌方单位落敌方支援线；Moding 用例因容量约束把 `infTarget3`
改为"art4 阵亡、前线清空后再上前线"，**五步断言与期望值一字未改**。

### 组 G：过期被报成"被占用"（1 条）— 测试过期（语义对应物变更）

`C2ReferenceAndFailureTests::Scenario3_Created_Instance_Recycled_When_Target_Slot_Occupied`：
原断言 `TargetSlotOccupied`，实际 `PositionStale`。有序列表模型下支援线"插入空隙"永不"被占用"（只有快照失效），
`TargetSlotOccupied` 在该路径不可达；命题（**放置期失败 ⇒ 实例回收、无半放置态**）不变，改断 `PositionStale` 并注明。
（方法名保留，便于与分片清单逐条对账。）

## 已改 src 文件

**无**（本片 57 条全部判定为测试过期／测试辅助过期；生产代码与资产零改动）。

## 待裁决

1. `CommandRulesTests` 三条守护用例里的"隔空槽不相邻"在新模型无对应物（有序列表没有空格），已按"隔一个元素"重述布置
   （判定式"位置序号差 1"未变）。若产品口径要求"守护者与 HQ 之间不许有单位"之类新规则，需另立需求——本轮未改产品。
2. `Scenario3_Created_Instance_Recycled_When_Target_Slot_Occupied` 的方法名仍写 `Target_Slot_Occupied`，与现行可达原因
   （`PositionStale`）不符；改名会破坏分片清单/归因表的名称追溯，故暂留，建议收尾统一改名。

## 阻塞

无。

## 附：全项目剩余 27 条（非本片）

均属 T2/T3 归属类（`CardServiceTests`／`Sandbox/*`／`Trigger*`／`HqEntityTests`／`GameHooksTests`／
`VeteranMechanismTests`／`DiyExample08`），与 S3 基线逐条同名，**本轮未新增任何红**。
