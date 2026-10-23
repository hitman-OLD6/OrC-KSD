# 执行进度 — 失败测试清零

- 更新时间: 2026-10-23T19:40:00+08:00
- 工具调用计数: ~80
- 当前步骤: S2 + S3 完成，两个 commit 已落
- 总体状态: 已完成（本批范围）

## 已完成
- 读完 `requirements.md`（R1–R7、裁决结论、实施纪律）与 `implementation.md`（Constraints 1–11、S2/S3、基线事实）。
- **S2 前基线**：`dotnet test tests/Orc.Game.Tests --artifacts-path artifacts/test` → **失败 111 / 通过 1243 / 跳过 0 / 总计 1354**（与 requirements 现状表一致）。名单：`artifacts/trx/s2-before-failed.txt`，trx：`artifacts/trx/s2-before.trx`。
- **S2（拆测试侧取位钳制）已落地**：
  1. `tests/Orc.Game.Tests/TestGapListExtensions.cs`：`At(index)` 去钳制，越界（含满员=0 个插入点）抛 `ArgumentOutOfRangeException`，与 `BattleLine` 索引器同口径。
  2. `CommandTestInfrastructure.PrepareOnSupportAsync`：删除"可部署槽 / 自动跳过 HQ 占位槽"钳制与负索引归零；`PrepareOnFrontAsync` 统一走 `At()`（同抛）。注释改为「第 index 个插入点（HQ 与单位同为元素，N 元素共 N+1 个）」。
  3. 删除 `tests/Orc.Game.Tests/TestPositionCompat.cs`（`Card.IsEmpty()` 恒假 / `FormationGap.IsEmpty()`），23 处 `IsEmpty()` 调用点全部按「位置元素」语义改写（`Contains/DoesNotContain(Members)`、`Assert.Single/Empty`、`IndexOf(unit)==Ordinal`），期望值未改。
- **S2 后实测**：`dotnet test tests/Orc.Game.Tests --artifacts-path artifacts/test --logger trx;LogFileName=s2.trx` → **失败 175 / 通过 1179 / 跳过 0 / 总计 1354**（111 ＋ 66 新红 － 2 新绿）。trx：`artifacts/trx/s2.trx`，机器可比对 diff：`artifacts/trx/s2-diff.json`。

## 进行中
- 无。

## 未开始
- 无（本批范围之外：S4 及之后）。

## 提交

| commit | 范围 | 内容 | 路径核对 |
| --- | --- | --- | --- |
| `db01a3f` | ① S2 | 拆测试侧取位钳制（18 个 `tests/**` 文件，含删除 `TestPositionCompat.cs`） | `git show --name-only` 全为 `tests/**` |
| `2797057` | ② S3 | `PositionStale` 三入口（5 个 `src/Orc.Game/**` 文件） | `git show --name-only` 全为 `src/**` |

- 提交前均跑过对应测试项目（带 `--artifacts-path`），见上两节实测数。
- 混合文件的在途内容：`MatchCardService.cs`（昨日 `ContainsSlot` 改写）、`SandboxPrimitives.cs`（昨日两条落位判定收尾）随 ② 一并提交，按 implementation 备注 3 处理；本次在这两个文件里只各加了"过期⇒结果对象/`position-stale`"的分类，判定逻辑未动。
- 未提交：`docs/测试失败清零/PROGRESS.md`（本批过程产物，按 implementation 备注 1 留给收尾的 docs 提交）；`artifacts/`（本轮测试产物目录，未跟踪，含 `artifacts/trx/*.trx` 与失败清单，作为归因证据保留）。
- 卡池批次文件（`src/Orc.Mcp/Pool/`、`PoolTools.cs`、`src/Orc.Server/Admin/*`、`ServerBootstrap.cs`、`MatchCardAssembly.cs`、`LoadListPlanner.cs`、`tools/orc/transport.py`）与在途测试文件（`CommandSystemTests.cs`、`Sandbox/SandboxPrimitiveTests.cs`）内容未变，仍在工作区。

## S3（过期语义独立化）结果

- **产品侧改动**：
  - `src/Orc.Game/Cards/PlayChain.cs`：`PlayFailureReason` 新增 `PositionStale`（保留 `TargetSlotOccupied`）。
  - `src/Orc.Game/Managers/PlayManager.cs`：`JoinUnitAsync` 的 `!target.IsCurrent` → `PlayResult.Failure(PositionStale)`，**仍不抛**；入口注释同步。
  - `src/Orc.Game/Match/MatchCardService.cs`：`CardPlaceFailureReason` 新增 `PositionStale`；`PlaceToSupportLineAsync` 拆两条判定——`!ReferenceEquals(slot.Line, line)` ⇒ 保留 `ArgumentException`（真契约错误）；同线但 `!ContainsSlot(...)` ⇒ `CardPlaceResult.Rejected(PositionStale, card)`（不抛）。`ContainsSlot` 判定式（同线 && `IsCurrent`）**未改**。
  - `src/Orc.Game/Sandbox/SandboxFailure.cs`：新增 `PositionStale = "position-stale"`。
  - `src/Orc.Game/Sandbox/SandboxPrimitives.cs:891`：`!destination.IsCurrent` → `SandboxFailure.PositionStale`（本文件仅此一处改动）。
- **实测（均带 `--artifacts-path`）**：
  - `Orc.Game.Tests`：**失败 175 / 通过 1179 / 跳过 0**（与 S2 后同数，无新红/新绿；仅失败文本变化）。trx：`artifacts/trx/s3-game.trx`。
  - `Orc.Mcp.Tests`：**失败 1 / 通过 117 / 跳过 0**（与基线同一条 `SandboxPrimitiveToolTests::ReturnedRefIsUsableByNextCall`）。trx：`artifacts/trx/s3-mcp.trx`。
- **失败文本由误导性原因变为 `PositionStale` 的用例**：
  | 用例 | S2 后 | S3 后 |
  | --- | --- | --- |
  | `C2ReferenceAndFailureTests::Scenario3_Created_Instance_Recycled_When_Target_Slot_Occupied` | `ArgumentException 不在玩家 #0 的支援线内` | `Expected: TargetSlotOccupied / Actual: PositionStale` |
  | `CardServiceTests::Failure_Slot_Occupied_Rejected_And_Instance_Recycled_Silently` | 同上 | 同上 |
  | `MatchHistoryReadTests::Read_Then_Transform_Rebuild_End_To_End` | 同上（gap 1） | `Expected: Placed / Actual: Rejected`（原因＝`PositionStale`，结果对象） |
  | `UnitTurnsInPlayTests::Transform_Rebuilt_Instance_Resets_Count` | 同上（gap 0） | `Expected: Placed / Actual: Rejected`（原因＝`PositionStale`） |
  | `KeywordEffectChannelTests::GrantWithContent_Direct_Gate_Full_Lifecycle` | `准备单位失败：TargetSlotOccupied` | `准备单位失败：PositionStale` |
- **解读**：
  - 前两条＝测试断言"被占用"，实际场景是**过期**——产品分类修正后暴露真实差异（测试侧待 S9 决定"制造真被占"还是改断 `PositionStale`）。
  - 后两条＝测试"先取空隙、再执行会改变阵线的操作、再放置"，正是 R2 禁止的过期快照用法；S3 起失败从异常变为可读的结果对象（`Placed`→`Rejected`），S9 按"现取现用"改成放置前重取。
  - `KeywordEffectChannel` 那条证实 A 档"准备单位失败：TargetSlotOccupied"实为测试辅助持过期空隙，不是产品判定错误。
- **未做（按指令限定范围）**：`SandboxPrimitives` 的 `MapPlaceFailure`／`MapPlayFailure` 未加 `PositionStale` 映射（指令要求该文件只改 `:891`）。故沙盒 `place`／`join` 原语遇到过期仍会落到 `placement-rejected`；若后续要求沙盒三条原语都吐 `position-stale`，需在 S8/S11 的"对外串"单元追加。

## 拆钳制后基线（新红/新绿/形态变化）

> 口径：`s2-before.trx`（111 红）vs `s2.trx`（175 红）。全部新红与形态变化均属**测试过期**（按固定格位/旧槽位模型取位），归 S9 重写；本步未修任何一条，也未改任何期望值。

### 新红（66 条，S2 前绿 / S2 后红）

全部形态＝`ArgumentOutOfRangeException: 插入点序号越界（合法范围：0..N）`——`At(index)` 不再钳制到末位，越界先抛。

- `AuraSystemTests::Aura_Collects_Across_Fields_And_Unregisters_By_Source` — `0..0`
- `AuraSystemTests::Modifier_And_Aura_Compose_On_Same_Field` — `0..0`
- `AuraSystemTests::Scenario1c_AdjacentAura_Follows_Explicit_Effect_Removal` — `0..0`
- `AuraSystemTests::Scenario2_SelfCondition_FrontLine_Attack_Bonus_Follows_Position` — `0..0`
- `AuraSystemTests::Scenario3_SourceCondition_Grants_Whole_Support_Line` — `0..0`
- `AuraSystemTests::StatChanged_Drives_Full_Rerun_For_Cross_Card_Conditions` — `0..0`
- `BattleKeywordTests::BattleKeyword_CardReads_Filter_Count_And_Set` — `0..1`
- `CommandCombatTests::Ambush_No_Longer_Rewrites_After_Death` — `0..1`
- `CommandCombatTests::Ambush_Not_Triggered_When_Condition_Fails_Falls_Back_To_Default_Damage` — `0..1`
- `CommandCombatTests::Combat_Survived_Skips_Dead_Participant` — `0..1`
- `CommandCombatTests::Dead_Attacker_Still_Resolves_As_Success_With_Fee_And_Flag_Updates` — `0..1`
- `CommandCombatTests::Mutual_Destruction_Emits_Two_Died_With_Target_First` — `0..1`
- `CommandFrontLineTests::Bomber_Interception_Blocks_Non_Fighter_Targets_On_Fighter_Line` — `0..1`
- `CommandFrontLineTests::Bomber_Interception_Requires_Living_Fighter` — `0..1`
- `CommandFrontLineTests::Non_Bomber_Attackers_Ignore_Fighter_Interception` — `0..1`
- `CommandImmunityTests::Artillery_Target_Counterattacks_When_Attacked_By_Infantry` — `0..1`
- `CommandImmunityTests::Attacker_Artillery_Receives_No_Counterattack` — `0..1`
- `CommandImmunityTests::Attacker_Bomber_Is_Countered_Only_By_Fighter_Target` — `0..1`
- `CommandRulesTests::Guard_Protects_Hq_And_Guardian_Itself_Is_Never_Guarded` — `0..1`
- `CommandRulesTests::Non_Tank_After_Move_Cannot_Attack_Exclusive_Choice` — `0..1`
- `CommandRulesTests::Range_From_Front_Line_Enemy_Support_Units_And_Hq_Are_Targets_But_Not_Same_Line` — `0..1`
- `CommandRulesTests::Range_From_Support_Line_Only_Enemy_Front_Units_Are_Targets` — `0..0`
- `CommandRulesTests::Range_Mixed_Type_Applies_Rules_Independently` — `0..1`
- `CommandRulesTests::Range_Typeless_Behaves_As_Infantry_Baseline` — `0..1`
- `CommandRulesTests::Tank_Can_Move_And_Attack_In_Same_Turn_DoubleAction` — `0..1`
- `CommandSystemTests::Attack_Unit_Resolves_Mutual_Damage_And_Finalizes_Flags` — `0..0`
- `CommandSystemTests::Begin_Rejects_Owner_Missing` — `0..1`
- `CommandSystemTests::Interaction_Candidates_Are_Union_And_Off_Candidates_Are_Rejected_Until_Valid` — `0..0`
- `CommandSystemTests::Move_Emits_PositionChanged_Once_With_Frozen_Payload` — `0..0`
- `CommandUpdateTests::Attack_Without_Deaths_Emits_StatChanged_For_Both_Sides` — `0..1`
- `CommandUpdateTests::Move_Emits_Only_Position_Changed` — `0..0`
- `CovertMechanismTests::C12_Both_Covert_Sides_Reveal_In_Attacker_First_Order` — `0..0`
- `CovertMechanismTests::C1_Covert_Unit_Is_Rejected_By_Instruction_Targeting_Through_Production_Judicator` — `0..1`
- `CovertMechanismTests::C2_Attacked_Covert_Unit_Is_Revealed_Before_Damage_Settlement` — `0..0`
- `CovertMechanismTests::C3_Covert_Attacker_Is_Revealed_On_Attack` — `0..0`
- `CovertMechanismTests::C8_Attack_Candidates_Include_Covert_Unit` — `0..0`
- `CovertMechanismTests::C9_Request_Level_Override_Includes_Covert_Unit` — `0..1`
- `EffectRemountAndStrategyTests::Aura_Effect_Remount_Rebuilds_Managed_Cleanup_For_Second_Unmount` — `0..0`
- `JudicatorCombatLegalityTests::Adjacent_Group_And_Typeless_Coverage` — `0..0`
- `JudicatorCombatLegalityTests::Boundary_Invalid_Reference_And_Unknown_Entity_Return_False` — `0..0`
- `JudicatorCombatLegalityTests::Five_Type_Coverage_Typeless_And_Qualification_Group` — `0..1`
- `JudicatorCombatLegalityTests::Guard_Eligibility_Moding_Penetrates_Combinator` — `0..1`
- `JudicatorCombatLegalityTests::Guard_Stage_Hq_Default_Equivalence` — `0..1`
- `JudicatorCombatLegalityTests::Guard_Stage_Unit_Default_Equivalence` — `0..1`
- `JudicatorCombatLegalityTests::Range_Moding_Penetrates_Combinator_And_Both_Chains` — `0..1`
- `JudicatorCombatLegalityTests::Standalone_CommandManager_BuiltIn_Defaults_Consistent` — `0..0`
- `JudicatorCombatLegalityTests::Target_Legal_Moding_Affects_Both_Chains` — `0..1`
- `JudicatorCombatLegalityTests::Unit_Branch_Range_Default_Equivalence` — `0..0`
- `KeywordBatch2Tests::Pincer_Deploy_Form_Pair_Both_Sides_Gain_Bonus` — `0..1`
- `KeywordBatch2Tests::Pincer_Member_Death_Breaks_Pair_Other_Side_Loses_Bonus` — `0..1`
- `KeywordBatch2Tests::Pincer_Paired_Members_Are_Excluded_From_New_Candidates` — `0..1`
- `KeywordComponentTests::Death_Revokes_Behavior_But_Keeps_Registration_And_Value` — `0..0`
- `KeywordEffectBatch3Tests::Blitz_Behavior_Revoked_On_Keyword_Revoke_And_Restored_On_Regrant` — `0..2`
- `KeywordEffectBatch3Tests::Blitz_MultiUnit_Deployment_Does_Not_Cross_Set_Flags` — `0..2`
- `KeywordEffectBatch3Tests::Blitz_Runtime_Grant_Revoke_Closure_Follows_Through_Deployment` — `0..2`
- `KeywordSystemTests::Fury_Tank_Moves_Once_And_Attacks_Twice` — `0..1`
- `KeywordSystemTests::Keywords_Are_Registered_At_Load_And_Kept_After_Death` — `0..0`
- `MatchOutcomeTests::Counter_Entrances_Are_Rejected_After_End` — `0..1`
- `MatchOutcomeTests::End_Turn_And_Initialization_Are_Rejected_After_End` — `0..1`
- `MatchOutcomeTests::Targeter_Start_Is_Rejected_After_End` — `0..1`
- `PlayChainUnitTests::Unit_Play_Verification_Rejected_On_Point_Drift_Leaves_Zero_Side_Effects` — `0..2`
- `RandomEffectScenarioTests::Scenario3_Random_Transfer_Applies_Damage_To_Chosen_Target` — `0..0`
- `StatPortalTests::Damage_Chain_Through_Basic_Combat_Emits_StatChanged_And_Keeps_Modifiers` — `0..1`
- `StatWiringTests::Attack_Modifier_Changes_Actual_Damage_Through_Basic_Combat` — `0..1`
- `TriggerCardTests::Effect_Initiated_Attack_Carries_TriggerCard_Through_Attack_And_Damage` — `0..1`
- `TriggerCardTests::Effect_Initiated_Damage_Directly_Carries_TriggerCard` — `0..1`

### 新绿（2 条，S2 前红 / S2 后绿）

- `P3ActionFamilyTests::Deploy_Rejects_At_Initiate_When_Points_Shortage_With_Zero_Side_Effects`
- `PlayChainDeploymentTests::Join_Rejects_Already_Unitized`

> 成因：原断言 `Assert.False(gap.IsEmpty())`（恒假助手的取反＝恒真，实际变成"永远失败"）与 `front[1]`（固定槽取元素越界）；S2 改写为 `DoesNotContain(unit, Members)` / `Assert.Single(Members)` 的同义断言，期望值未改。

### 形态变化（40 条，S2 前后都红、失败文本变了）

共同点：原先卡在断言/旧异常上，拆钳制后**更早**在取位处抛 `插入点序号越界`。

**拆前：`Assert.Same() Failure: Values are not the same instance`**（11 条）
- `CardDataComponentTests::UnitStateData_Defaults_And_ReadWrite`
- `CardServiceTests::Scenario2_Context_Reachability_Via_Effect_SupportLine`
- `CardServiceTests::Scenario2_Generate_To_SupportLine_Join_Semantics_No_Fee_No_Deployed`
- `CommandFrontLineTests::Advance_Execution_Revalidation_Rejects_When_Enemy_On_Front`
- `CovertMechanismTests::C7c_Pincer_With_Only_Covert_Candidates_Skips_Interaction`
- `GameHooksTests::Zero_Callpoint_Hooks_Are_Emittable_With_Frozen_Payloads`
- `KeywordBatch2Tests::Pincer_Deploy_Cancel_Leaves_No_Pair`
- `MatchOutcomeTests::Hq_Zero_Ends_Match_Immediately_With_Winner_And_Settles_Strike`
- `Sandbox.SandboxPrimitiveSignalEquivalenceTests::Unitize_MatchesEffectCardSequence`
- `Sandbox.SandboxPrimitiveTests::Unitize_FromHandCard_EmitsPlacedAndJoined`
- `TurnPhaseEffectTests::Delayed_Action_Executes_On_Next_Own_Turn_Start_And_Self_Cleans`

**拆前：`Assert.Equal() Failure: Values differ`**（10 条）
- `AuraSystemTests::Scenario1_AdjacentAura_Follows_Source_Presence_Movement_And_Death`
- `AuraSystemTests::Scenario1b_AdjacentAura_Stacks_From_Multiple_Sources`
- `AuraSystemTests::Scenario4_AdjacentCount_Scales_And_Falls_Back`
- `EffectRemountAndStrategyTests::Strategy3_Direct_Mount_Fires_On_Own_Deploy_Chain_Only`
- `JudicatorCombatCounterAmbushTests::Counter_Eligibility_Moding_Syncs_Default_Damage_And_Ambush_Qualification`
- `P3ActionFamilyTests::Illegal_Join_Target_Is_Target_Invalid_With_Zero_Side_Effects`
- `PlayChainDeploymentTests::Join_Rejects_Occupied_Slot`
- `PlayChainUnitTests::Unit_Play_Direct_Drive_Rejects_Already_Unitized`
- `PlayChainUnitTests::Unit_Play_Direct_Drive_Rejects_Occupied_Slot`
- `RandomEffectScenarioTests::Scenario1_Random_Elimination_Kills_Expected_Unit_Without_Interaction`

**拆前：`Assert.Equal() Failure: Collections differ`**（3 条）
- `CardServiceTests::Adjacent_Resolution_Query_Face_Matches_BattleLine_Rules`
- `CardServiceTests::Scenario3_Generate_To_Adjacent_EndToEnd_Resolves_Candidates_And_Places`
- `JudicatorCombatCounterAmbushTests::Counter_Eligibility_Default_Terms_One_To_Five_Entries`

**拆前：`Assert.True() Failure`**（3 条）
- `CommandRulesTests::Guard_State_Maintained_On_Join_Move_And_Death_And_Multiple_Sources`
- `DeathrattleAndRetriggerTests::Deathrattle_Settles_Before_Cleanup_With_Observable_State`
- `GuardKeywordTests::Guard_DataBody_Protects_Adjacent_And_Hq_Never_Self`

**拆前：`Assert.False() Failure`**（2 条）
- `AuraSystemTests::Environment_Queries_Are_ReadOnly_And_Safe`
- `CommandRulesTests::Guard_Hq_Requires_Adjacent_Guardian_Not_Blocked_By_Gap`

**拆前：`Assert.IsType() Failure: Value is not the exact type`**（1 条）
- `Sandbox.SandboxPrimitiveTests::Unitize_ToFrontlineSlot_Allowed`

**拆前：`NullReferenceException`**（1 条）
- `CardServiceTests::Scenario6_Transform_Triggers_Leave_Notification_Breaks_Pincer`

**拆前：`ArgumentOutOfRangeException 位置序号越界（0..-1）`**（1 条）
- `CommandFrontLineTests::Advance_Is_Blocked_While_Enemy_Occupies_Front_And_Recovers_After_Clear`

**拆前：`ArgumentOutOfRangeException 位置序号越界（0..0）`**（1 条）
- `CommandLockInTests::Command_Targeter_Never_Offers_Support_Line_Interior_Slots`

**拆前：`ArgumentException 判定器载荷第 1 个参数类型不符（实际 BattleLine）`**（1 条）
- `JudicatorAssemblyTests::KeywordRuleOverrideIsAModingEntryAndUnregisterRestoresTheBaseline`

**拆前：`ArgumentException 判定器载荷第 1 个参数类型不符（实际 FormationGap）`**（1 条）
- `JudicatorCombatLegalityTests::Sub_Rule_Entries_Resolvable_And_Directly_Invokable`

**拆前：`InvalidOperationException 准备单位失败：TargetSlotOccupied`**（5 条）
- `KeywordEffectBatch2Tests::Ambush_Behavior_Revoked_On_Keyword_Revoke_And_Restored_On_Regrant`
- `KeywordEffectBatch2Tests::Ambush_Death_Revokes_Behavior_Keeping_Registration`
- `KeywordEffectBatch2Tests::Ambush_Rewrite_Follows_Effect_Detach_And_Reattach`
- `KeywordEffectBatch2Tests::Runtime_Grant_Revoke_Closure_Follows_Through_Armor_And_Ambush`
- `VeteranMechanismTests::Failure_Precheck_Rejections_Are_Categorized_With_Zero_Side_Effects`

### 归因备注（供 S9 直接用）
- 66 条新红里绝大多数是 `PrepareOnFrontAsync(..., 1)` 在**空前线**取第 2 个插入点：空线只有 1 个插入点，原语义「先占位再放第 2 个」必须显式多放一个元素才成立——属**位置换了**（不是命题不成立），S9 按「现取现用 + 必要的前置元素」重写。
- 5 条 `准备单位失败：TargetSlotOccupied` 的形态变化证实基线事实 7 的前半段：这些用例原先拿的是**跨动作过期**的空隙；拆钳制后先撞越界。S3 之后它们会回到 `TargetSlotOccupied`/`PositionStale`，需在 S3 复跑名单里对照。

## 待裁决

1. **`PlayFailureReason.TargetSlotOccupied` 在 S3 后没有生产出口**。`JoinUnitAsync` 是它唯一的产生点（基线事实 7 第 1 条），已按 S3 改为 `PositionStale`；`MatchCardService.cs:361` 的 `PlayFailureReason.TargetSlotOccupied → CardPlaceFailureReason.TargetSlotOccupied` 映射随之不可达。按指令**未删**该成员（`CardPlaceFailureReason`/`SandboxFailure` 同名项仍被沙盒 slot-index 路径使用，见下）。请裁决：保留为动作面预留，还是需要在某入口重建"真被占"判定。
   - 证据：`grep TargetSlotOccupied src` 只剩枚举定义、`MatchCardService.cs:361` 映射、`SandboxPrimitives.cs:859/867/891` 三处映射；`SandboxPrimitiveTests::Move_OccupiedTarget_Rejected` 走的是 slot-index 语义（请求 `support, Size` ＝越出位置序号范围）→ 由 `:891` 的 `: TargetSlotOccupied` 分支产出，仍然活跃。
2. **4 条"槽位被占"用例在新模型下命题可能不成立**（有序列表里"空隙"按定义＝可插入位置，无法既存在又被占；能构造出"被占"的唯一方式是持有过期空隙）。S9 前不改期望值，等裁决：改断 `PositionStale`（承认两因归一），还是重建"真被占"的入口/布置。
   - `PlayChainDeploymentTests::Join_Rejects_Occupied_Slot`（当前 `At(0)` 直接越界）
   - `PlayChainUnitTests::Unit_Play_Direct_Drive_Rejects_Occupied_Slot`（同上）
   - `CardServiceTests::Failure_Slot_Occupied_Rejected_And_Instance_Recycled_Silently`（S3 后实测 `Actual: PositionStale`）
   - `C2ReferenceAndFailureTests::Scenario3_Created_Instance_Recycled_When_Target_Slot_Occupied`（同上）
3. **沙盒 `place`/`join` 原语的失败原因映射未加 `PositionStale`**（按指令 `SandboxPrimitives.cs` 只改 `:891`），过期会落到 `placement-rejected`。若要求三条原语对外串一致（`position-stale`），需在后续"对外串"单元追加，不属本批。

## 阻塞 / 风险
- 禁止杀 PID 21968；所有测试带 `--artifacts-path "D:/OrC-KSD/artifacts/test"`。
- S2 后失败数 175 属**预期中间态**（拆掉掩盖层），不代表回归；不得据此调断言换绿。

## 下一步
- 交给下一批：S4 起（csx 制品 / 契约基线 / 域白名单 / 样例包 / MCP 用例 / A/C 档适配）。S9 直接用上面三类清单。
- 注意：本批只做地基，`Orc.Game.Tests` 仍红 175（中间态），不要把它当"回归"或"已完成"。
