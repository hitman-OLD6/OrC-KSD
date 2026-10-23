# ATTRIBUTION — 失败测试归因表

> 口径：`OrcEngine.sln` 全量 8 个项目共 2367 条，**失败 0 / 跳过 0**。
> 主体＝`artifacts/trx/s3-game.trx` 的 **175 条** `Orc.Game.Tests` 失败（S3 基线，即拆钳制 `PositionStale` 落地之后）；
> 175 口径之外的 6 条（`Orc.Packages.Tests` 2、`Orc.Mcp.Tests` 1、`Orc.Server.Tests` 3）另列于「附」。
> 「拆前失败形态」＝S3 基线的实测文本（取自 `artifacts/split/{T1,T2,T3,P}.md`），不沿用拆钳制前的消息（`implementation.md`：失败形态按拆后实际形态记录）。
> 归因依据：`requirements.md` R1 五类；中间态基线见 `PROGRESS.md`「拆钳制后基线」，逐条结论见 `PROGRESS-T1/T2/T3/P/D.md`。
> 类别取值：测试过期 / 产品缺陷 / 资产过期 / 门禁授权 / 需求级修正（本轮无需求级修正）。

## 统计

| 归因类别 | 组数 | 条数 |
| --- | --- | --- |
| 测试过期 | 9 | 163 |
| 产品缺陷 | 4 | 6 |
| 资产过期 | 1 | 4 |
| 门禁授权 | 2 | 2 |
| 需求级修正 | 0 | 0 |
| **合计（175 口径）** | **16** | **175** |
| 附：175 口径之外 | 3 | 6 |

> 分片对账：T1 57 ＋ T2 58 ＋ T3 54 ＋ P 6 ＝ 175，与 trx 失败集逐条相等（无遗漏、无多余）。
> T2 分片清单实际 58 条（`PROGRESS-T2.md` 记为 57），差异见文末「存疑」。

复核命令：

```
py -3.13 docs/测试失败清零/check_attribution.py --trx artifacts/trx-attr-all
```

## 测试过期

### T-1 插入点序号越界：布置仍按「物理 5 格槽位」取第 N 个插入点

- 拆前失败形态：`System.ArgumentOutOfRangeException : 插入点序号越界（合法范围：0..N）`。S2 前同一批多表现为 `Assert.Equal() Failure: Values differ`／`Assert.Same() Failure`，拆掉钳制后一律在取位处先抛（形态按拆后实际文本登记）
- 归因类别：测试过期
- 改动位置：`tests/Orc.Game.Tests/**`：逐用例改布置（按序铺够元素、改用合法插入点、取 `gaps[^1]` 显式末位）；`src/**` 零改动
- 真源依据：`CONTEXT.md`「位置模型」＋`requirements.md` 位置模型口径（阵线是有序列表，可插入位置＝N+1 个**临时空隙**）；`docs/游戏层-手牌选靶与前线入场/遗留问题.md` P1-6／P2-9（`At()` 属临时手段）
- commit：`b6d3036 (测试适配)`
- 说明：空线只有 1 个插入点（0..0）；支援线 HQ 是元素、位于插入点 1 之前 ⇒「支援线第一个单位」是插入点 1。原布置假设的「槽 2／槽 3」在有序列表里必须先有元素才存在。**期望值与命题一字未改**，只改铺位。本组含 `AuraSystemTests` 10 条：修完布置即绿，光环收集逻辑无缺陷（PROGRESS-T2 专项）。
- 成员（97）：
  - `AuraSystemTests::Aura_Collects_Across_Fields_And_Unregisters_By_Source`
  - `AuraSystemTests::Environment_Queries_Are_ReadOnly_And_Safe`
  - `AuraSystemTests::Modifier_And_Aura_Compose_On_Same_Field`
  - `AuraSystemTests::Scenario1_AdjacentAura_Follows_Source_Presence_Movement_And_Death`
  - `AuraSystemTests::Scenario1b_AdjacentAura_Stacks_From_Multiple_Sources`
  - `AuraSystemTests::Scenario1c_AdjacentAura_Follows_Explicit_Effect_Removal`
  - `AuraSystemTests::Scenario2_SelfCondition_FrontLine_Attack_Bonus_Follows_Position`
  - `AuraSystemTests::Scenario3_SourceCondition_Grants_Whole_Support_Line`
  - `AuraSystemTests::Scenario4_AdjacentCount_Scales_And_Falls_Back`
  - `AuraSystemTests::StatChanged_Drives_Full_Rerun_For_Cross_Card_Conditions`
  - `BattleKeywordTests::BattleKeyword_CardReads_Filter_Count_And_Set`
  - `CardServiceTests::Adjacent_Resolution_Query_Face_Matches_BattleLine_Rules`
  - `CardServiceTests::Scenario2_Context_Reachability_Via_Effect_SupportLine`
  - `CardServiceTests::Scenario2_Generate_To_SupportLine_Join_Semantics_No_Fee_No_Deployed`
  - `CardServiceTests::Scenario3_Generate_To_Adjacent_EndToEnd_Resolves_Candidates_And_Places`
  - `CardServiceTests::Scenario6_Transform_Triggers_Leave_Notification_Breaks_Pincer`
  - `CommandCombatTests::Ambush_No_Longer_Rewrites_After_Death`
  - `CommandCombatTests::Ambush_Not_Triggered_When_Condition_Fails_Falls_Back_To_Default_Damage`
  - `CommandCombatTests::Combat_Survived_Skips_Dead_Participant`
  - `CommandCombatTests::Dead_Attacker_Still_Resolves_As_Success_With_Fee_And_Flag_Updates`
  - `CommandCombatTests::Mutual_Destruction_Emits_Two_Died_With_Target_First`
  - `CommandFrontLineTests::Advance_Execution_Revalidation_Rejects_When_Enemy_On_Front`
  - `CommandFrontLineTests::Advance_Is_Blocked_While_Enemy_Occupies_Front_And_Recovers_After_Clear`
  - `CommandFrontLineTests::Bomber_Interception_Blocks_Non_Fighter_Targets_On_Fighter_Line`
  - `CommandFrontLineTests::Bomber_Interception_Requires_Living_Fighter`
  - `CommandFrontLineTests::Non_Bomber_Attackers_Ignore_Fighter_Interception`
  - `CommandImmunityTests::Artillery_Target_Counterattacks_When_Attacked_By_Infantry`
  - `CommandImmunityTests::Attacker_Artillery_Receives_No_Counterattack`
  - `CommandImmunityTests::Attacker_Bomber_Is_Countered_Only_By_Fighter_Target`
  - `CommandLockInTests::Command_Targeter_Never_Offers_Support_Line_Interior_Slots`
  - `CommandRulesTests::Guard_Does_Not_Cross_Lines_And_Requires_Physical_Adjacency`
  - `CommandRulesTests::Guard_Hq_Requires_Adjacent_Guardian_Not_Blocked_By_Gap`
  - `CommandRulesTests::Guard_Protects_Hq_And_Guardian_Itself_Is_Never_Guarded`
  - `CommandRulesTests::Guard_State_Maintained_On_Join_Move_And_Death_And_Multiple_Sources`
  - `CommandRulesTests::Non_Tank_After_Move_Cannot_Attack_Exclusive_Choice`
  - `CommandRulesTests::Range_From_Front_Line_Enemy_Support_Units_And_Hq_Are_Targets_But_Not_Same_Line`
  - `CommandRulesTests::Range_From_Support_Line_Only_Enemy_Front_Units_Are_Targets`
  - `CommandRulesTests::Range_Mixed_Type_Applies_Rules_Independently`
  - `CommandRulesTests::Range_Typeless_Behaves_As_Infantry_Baseline`
  - `CommandRulesTests::Tank_Can_Move_And_Attack_In_Same_Turn_DoubleAction`
  - `CommandSystemTests::Attack_Unit_Resolves_Mutual_Damage_And_Finalizes_Flags`
  - `CommandSystemTests::Begin_Rejects_Owner_Missing`
  - `CommandSystemTests::Interaction_Candidates_Are_Union_And_Off_Candidates_Are_Rejected_Until_Valid`
  - `CommandSystemTests::Move_Emits_PositionChanged_Once_With_Frozen_Payload`
  - `CommandUpdateTests::Attack_Without_Deaths_Emits_StatChanged_For_Both_Sides`
  - `CommandUpdateTests::Move_Emits_Only_Position_Changed`
  - `CovertMechanismTests::C12_Both_Covert_Sides_Reveal_In_Attacker_First_Order`
  - `CovertMechanismTests::C1_Covert_Unit_Is_Rejected_By_Instruction_Targeting_Through_Production_Judicator`
  - `CovertMechanismTests::C2_Attacked_Covert_Unit_Is_Revealed_Before_Damage_Settlement`
  - `CovertMechanismTests::C3_Covert_Attacker_Is_Revealed_On_Attack`
  - `CovertMechanismTests::C7c_Pincer_With_Only_Covert_Candidates_Skips_Interaction`
  - `CovertMechanismTests::C8_Attack_Candidates_Include_Covert_Unit`
  - `CovertMechanismTests::C9_Request_Level_Override_Includes_Covert_Unit`
  - `DeathrattleAndRetriggerTests::Deathrattle_Settles_Before_Cleanup_With_Observable_State`
  - `EffectRemountAndStrategyTests::Aura_Effect_Remount_Rebuilds_Managed_Cleanup_For_Second_Unmount`
  - `GameHooksTests::Zero_Callpoint_Hooks_Are_Emittable_With_Frozen_Payloads`
  - `GuardKeywordTests::Guard_DataBody_Protects_Adjacent_And_Hq_Never_Self`
  - `GuardKeywordTests::Guard_Grant_Revoke_Regrant_Drives_Maintenance`
  - `JudicatorCombatLegalityTests::Adjacent_Group_And_Typeless_Coverage`
  - `JudicatorCombatLegalityTests::Boundary_Invalid_Reference_And_Unknown_Entity_Return_False`
  - `JudicatorCombatLegalityTests::Five_Type_Coverage_Typeless_And_Qualification_Group`
  - `JudicatorCombatLegalityTests::Guard_Eligibility_Moding_Penetrates_Combinator`
  - `JudicatorCombatLegalityTests::Guard_Stage_Hq_Default_Equivalence`
  - `JudicatorCombatLegalityTests::Guard_Stage_Unit_Default_Equivalence`
  - `JudicatorCombatLegalityTests::Range_Moding_Penetrates_Combinator_And_Both_Chains`
  - `JudicatorCombatLegalityTests::Standalone_CommandManager_BuiltIn_Defaults_Consistent`
  - `JudicatorCombatLegalityTests::Target_Legal_Moding_Affects_Both_Chains`
  - `JudicatorCombatLegalityTests::Unit_Branch_Range_Default_Equivalence`
  - `KeywordBatch2Tests::Pincer_Deploy_Cancel_Leaves_No_Pair`
  - `KeywordBatch2Tests::Pincer_Deploy_Form_Pair_Both_Sides_Gain_Bonus`
  - `KeywordBatch2Tests::Pincer_Member_Death_Breaks_Pair_Other_Side_Loses_Bonus`
  - `KeywordBatch2Tests::Pincer_Paired_Members_Are_Excluded_From_New_Candidates`
  - `KeywordComponentTests::Death_Revokes_Behavior_But_Keeps_Registration_And_Value`
  - `KeywordEffectBatch2Tests::Ambush_Behavior_Revoked_On_Keyword_Revoke_And_Restored_On_Regrant`
  - `KeywordEffectBatch2Tests::Ambush_Death_Revokes_Behavior_Keeping_Registration`
  - `KeywordEffectBatch2Tests::Ambush_Rewrite_Follows_Effect_Detach_And_Reattach`
  - `KeywordEffectBatch2Tests::Runtime_Grant_Revoke_Closure_Follows_Through_Armor_And_Ambush`
  - `KeywordEffectBatch3Tests::Blitz_Behavior_Revoked_On_Keyword_Revoke_And_Restored_On_Regrant`
  - `KeywordEffectBatch3Tests::Blitz_MultiUnit_Deployment_Does_Not_Cross_Set_Flags`
  - `KeywordEffectBatch3Tests::Blitz_Runtime_Grant_Revoke_Closure_Follows_Through_Deployment`
  - `KeywordSystemTests::Fury_Tank_Moves_Once_And_Attacks_Twice`
  - `KeywordSystemTests::Keywords_Are_Registered_At_Load_And_Kept_After_Death`
  - `MatchOutcomeTests::Counter_Entrances_Are_Rejected_After_End`
  - `MatchOutcomeTests::End_Turn_And_Initialization_Are_Rejected_After_End`
  - `MatchOutcomeTests::Hq_Zero_Ends_Match_Immediately_With_Winner_And_Settles_Strike`
  - `MatchOutcomeTests::Targeter_Start_Is_Rejected_After_End`
  - `PlayChainUnitTests::Unit_Play_Direct_Drive_Rejects_Already_Unitized`
  - `PlayChainUnitTests::Unit_Play_Verification_Rejected_On_Point_Drift_Leaves_Zero_Side_Effects`
  - `PlayChainUnitTests::Unit_PrePlay_Rejected_When_No_Available_Slots`
  - `RandomEffectScenarioTests::Scenario1_Random_Elimination_Kills_Expected_Unit_Without_Interaction`
  - `RandomEffectScenarioTests::Scenario3_Random_Transfer_Applies_Damage_To_Chosen_Target`
  - `StatPortalTests::Damage_Chain_Through_Basic_Combat_Emits_StatChanged_And_Keeps_Modifiers`
  - `StatWiringTests::Attack_Modifier_Changes_Actual_Damage_Through_Basic_Combat`
  - `TriggerCardTests::Effect_Initiated_Attack_Carries_TriggerCard_Through_Attack_And_Damage`
  - `TriggerCardTests::Effect_Initiated_Damage_Directly_Carries_TriggerCard`
  - `TurnPhaseEffectTests::Delayed_Action_Executes_On_Next_Own_Turn_Start_And_Self_Cleans`
  - `VeteranMechanismTests::Failure_Precheck_Rejections_Are_Categorized_With_Zero_Side_Effects`

### T-2 位置序号越界：用 `line[i]` 按固定格位取「第 i 个元素」

- 拆前失败形态：`System.ArgumentOutOfRangeException : 位置序号越界（合法范围：0..0／0..-1）`
- 归因类别：测试过期
- 改动位置：`tests/Orc.Game.Tests/**`：把「枚举 5 个物理格子」改为枚举插入空隙（`GetInsertionGaps()`）；负面断言改「候选不含该线任意插入点」
- 真源依据：`requirements.md` 位置模型口径；`src/Orc.Game/Board/BattleLine.cs` 索引器越界即抛（基线事实 1）——「第 i 个元素」必须先存在，5 格物理槽位在新模型无对应物
- commit：`b6d3036 (测试适配)`
- 说明：改写后断言的仍是「全部可用落点」这一原命题，只是载体由格位换成插入点。
- 成员（3）：
  - `CommandFrontLineTests::Advance_Remains_Available_When_Front_Holds_Only_Friendly_Units`
  - `CommandLockInTests::Front_Line_Unit_Has_No_Move_Candidates_And_No_Same_Line_Products`
  - `CommandLockInTests::Move_Candidates_Never_Contain_Support_Line_Interior_Slots`

### T-3 槽位实体残留：仍拿空隙／元素／阵线互比

- 拆前失败形态：`Assert.Same() Failure: Values are not the same instance`（多数）；其余为 `Assert.Equal() Failure: Values differ`、`Assert.True()/False()`、`NullReferenceException`（`Blitz_Death…`：单位死亡后 `Line` 已置空，仍取位置）
- 归因类别：测试过期
- 改动位置：`tests/Orc.Game.Tests/**`：`Assert.Same(gap, state.Line)` → 比 `BattleLine`；`Assert.Same(unit, gap)` → `line[i]`／`Members[i]`；落位断言改「线引用＋序号（`Ordinal == IndexOf(unit)`）」；`Blitz_Death…` 改生前取一次 `BoardPosition` 复用。另含测试辅助 `CardServiceTestInfrastructure.SceneOp.Transform`（原「放回原位置」实现成 `At(0)` → 改记原序号再取插入点）
- 真源依据：`CONTEXT.md`「`FormationGap` 是插入点、`BoardPosition` 是位置值，均非槽位实体」；`requirements.md` R2 第一条
- commit：`b6d3036 (测试适配)`
- 说明：`*_View`／`unit.joined` 这类**载荷**里的旧 `slot` 也一并改为 `BoardPosition` 或「线＋序号」，与 T-5 同源但落点在测试侧断言/构造。
- 成员（33）：
  - `CardDataComponentTests::UnitStateData_Defaults_And_ReadWrite`
  - `CardServiceTests::Scenario5_Copy_Is_Definition_Level_New_Instance_Source_Unaffected`
  - `CardServiceTests::Scenario6_Transform_Failure_Keeps_Source_Intact`
  - `CardServiceTests::Scenario6_Transform_Rebuilds_Instance_At_Same_Slot`
  - `CommandCombatTests::Hq_Damage_Is_Clamped_To_Zero_Without_Counterattack`
  - `CovertMechanismTests::C4_Movement_Does_Not_Reveal_Covert_Unit`
  - `DiyExample08_AssaultAdvanceRetreatTests::突袭_BattleAsync_存活但前线有敌人_撤退到己方支援线`
  - `EntryPointBeginTests::BeginMoveAsync_Cancel_Leaves_Zero_Side_Effect`
  - `EntryPointBeginTests::BeginMoveAsync_Confirms_And_Executes_Move_With_Slot_Parameter`
  - `HqEntityTests::Scene1_Hq_Entities_Place_On_The_Middle_Slot_And_Player_Holds_Reference`
  - `HqEntityTests::Scene1_Zero_Does_Not_Run_Death_Or_Destroy_Semantics_And_Keeps_Loading_Face`
  - `KeywordBatch2Tests::Pincer_Deploy_Without_Candidates_Skips_Interaction`
  - `KeywordEffectBatch3Tests::Blitz_Death_Revokes_Subscription_Keeping_Registration`
  - `KeywordProgramTests::EffectDisplacementUsesThePortalWithoutPayingOrConsumingActions`
  - `KeywordProgramTests::RetreatPermissionIsRecheckedAfterSelectionAndOrdinaryUnitsCannotRetreat`
  - `KeywordProgramTests::RetreatUsesNormalDragCostsAndMovementOpportunity`
  - `MatchHistoryReadTests::Read_Then_Transform_Rebuild_End_To_End`
  - `MatchOutcomeTests::Command_And_Play_Chain_Entrances_Are_Rejected_After_End`
  - `OfficialCardDataSmokeTests::Official_Corpus_Loads_Fully_And_One_Card_Can_Be_Deployed`
  - `OperateCostRecheckTests::Increase_Blocks_Availability_And_Revalidation`
  - `P2ActionFamilyTests::Effect_Move_Is_Free_And_Consumes_No_Operate_State`
  - `P3ActionFamilyTests::Join_And_Deploy_Share_One_Handle_End_With_No_Entry_Flag`
  - `PlayChainDeploymentTests::Deploy_Isolates_Single_Effect_Exception_And_Continues`
  - `PlayChainDeploymentTests::Deploy_Triggers_Injected_Effects_In_Registration_Order`
  - `PlayChainDeploymentTests::Deploy_Without_Effects_Still_Unitizes_And_Charges`
  - `PlayChainDeploymentTests::Join_Unitizes_Without_Fee_Or_Deployment_Keyword_Effect`
  - `PlayChainUnitTests::Unit_PrePlay_Confirm_Deploys_Through_Interaction`
  - `PlayChainUpdateTests::Deploy_Emits_CardPlayed_Then_UnitDeployed_Once_Without_Joined`
  - `PlayChainUpdateTests::Join_Emits_UnitJoined_Once_Without_Played_Or_Deployed`
  - `SpaceWarKeywordTests::游击_前线可指挥移动回支援线_无游击不可`
  - `TriggerCardTests::Command_View_Carries_TriggerCard_And_Propagates_To_Inner_Triggers`
  - `TriggerCardTests::Move_View_Leaves_TriggerCard_Empty_On_Player_Command`
  - `UnitTurnsInPlayTests::Transform_Rebuilt_Instance_Resets_Count`

### T-4 目标集未收敛：`AllRefsOf` 三面枚举在有序列表下退化为多重集

- 拆前失败形态：`Assert.Equal() Failure: Collections differ`——实际为 `[infA,tankA,infB,infA,tankA,infB]`，每个单位出现两次
- 归因类别：测试过期
- 改动位置：`tests/Orc.Game.Tests/CommandTestKit.cs`：`AllRefsOf` 按实体去重（`ReferenceEqualityComparer`）
- 真源依据：过滤器本身正常（`JudicatorTargetSelectionTests` 期望值未改）；根因是「位置元素＋在场单位＋双方 HQ」三面在有序列表里枚举同一批实体（HQ 也在支援线上）
- commit：`b6d3036 (测试适配)`
- 说明：**不是产品缺陷**：判定器与过滤器均未改，实际差的只是重复计数。
- 成员（2）：
  - `JudicatorTargetSelectionTests::Forbid_Infantry_Wrapper_Removes_Class_And_Falls_Back`
  - `JudicatorTargetSelectionTests::Full_Selection_Flow_Default_Rule_Filters_Through_Judicator`

### T-5 载荷类型错：`OldPosition`／`combat.range`／`combat.interception` 传了错的载体

- 拆前失败形态：`ArgumentException 判定器载荷第 1 个参数类型不符（实际 BattleLine）`、`（实际 FormationGap）`、`Assert.True() Failure`、`ValidationRejected`（`OldPosition` 非 `BoardPosition` 即拒）
- 归因类别：测试过期
- 改动位置：`tests/Orc.Game.Tests/**`：`OldPosition` 与 `combat.range`／`combat.interception` 第 2 参改 `new BoardPosition(unit.Line!, unit.Line!.IndexOf(unit))`；**禁用 `gap.Ordinal`**（插入点序号≠元素序号）；措辞「目标槽位」→「目标位置」
- 真源依据：基线事实 10（2026-10-23 裁决）：载荷是契约面，**生产签名零改动**；生产侧 `CombatTargetLegalJudicator` 本就自洽
- commit：`b6d3036 (测试适配)`
- 说明：requirements B 档初判「改生产契约」**不成立**（核查后降级为测试过期），不得写成「产品已修复」。
- 成员（5）：
  - `JudicatorAssemblyTests::KeywordRuleOverrideIsAModingEntryAndUnregisterRestoresTheBaseline`
  - `JudicatorCombatLegalityTests::Sub_Rule_Entries_Resolvable_And_Directly_Invocable`
  - `JudicatorValidationTests::Move_And_Attack_Recheck_Are_Independently_Modable`
  - `JudicatorValidationTests::Standalone_CommandManager_Binds_BuiltIn_Recheck_Defaults`
  - `TriggerCardTests::Move_View_Carries_TriggerCard_On_Effect_Initiated_Move`

### T-6 布置不可满足：前置门禁使目标单位静默未入场／入口本就不接受该线

- 拆前失败形态：`Expected [False] / Actual [True]`、`Expected Success / Actual Cancelled`／`Failed`、`InvalidOperationException : 单位 '…' 尚未单位化（缺单位数据组件）`、`Assert.Contains() Failure: Filter not matched`
- 归因类别：测试过期
- 改动位置：`tests/Orc.Game.Tests/**`：敌方单位改落**敌方支援线**（避开「前线被敌方存活单位占据 ⇒ 加入前线被拒」）；部署入口改 `GetSupportLine(playerA)`（默认部署线＝支援线，不得直接上前线）；打出用例补 `toHand: true`；`Illegal_Join_Target…` 改为「先取空隙、再让阵线变化」构造过期目标
- 真源依据：`JoinFrontLine.ValidateFrontlineAsync` 的前线敌对门禁；`EffectRuntime` 部署入口只接受部署线；`PlayUnitAsync` 要求卡在手；与基线事实 6 同形状（布置错伪装成产品行为差异）
- commit：`b6d3036 (测试适配)`
- 说明：`Counter_Eligibility_Moding…` 因容量约束把 `infTarget3` 改为「art4 阵亡、前线清空后再上前线」，**五步断言与期望值一字未改**。
- 成员（9）：
  - `DeathrattleAndRetriggerTests::Deploy_Retrigger_Shares_Single_Source_With_Play_Chain`
  - `EffectRemountAndStrategyTests::Card_Placed_Emitted_After_Unitize_Before_Completion_Signal`
  - `EffectRemountAndStrategyTests::Strategy3_Direct_Mount_Fires_On_Own_Deploy_Chain_Only`
  - `JudicatorCombatCounterAmbushTests::Counter_Eligibility_Default_Terms_One_To_Five_Entries`
  - `JudicatorCombatCounterAmbushTests::Counter_Eligibility_Moding_Syncs_Default_Damage_And_Ambush_Qualification`
  - `MatchHistoryReadTests::Last_Died_Friendly_Unit_Read_Filters_Recent_And_Owned`
  - `P3ActionFamilyTests::Illegal_Join_Target_Is_Target_Invalid_With_Zero_Side_Effects`
  - `TriggerSourceSignalTests::Card_Played_Carries_Played_Card_As_Source`
  - `TriggerSourceSignalTests::Unit_Deployed_Carries_Unit_As_Source`

### T-7 过期语义改判：原断「被占用」，实际是「快照过期」

- 拆前失败形态：`Assert.Equal() Failure: Values differ`（Expected `TargetSlotOccupied` / Actual `PositionStale`）；另两条在 S3 基线上仍是 `插入点序号越界（0..0）`，因原布置靠「直接取 0 号空隙」造「被占」
- 归因类别：测试过期
- 改动位置：`tests/Orc.Game.Tests/**`：布置改成「同一空隙再用＝过期」，断言改 `PositionStale`（两条为 `FormationGapUnavailable`）；**方法名保留**（`…_Slot_Occupied`），便于与分片清单逐条对账
- 真源依据：`requirements.md` R2 第三条（过期须是独立可区分原因）；有序列表里插入空隙按定义＝可插入位置，「既存在又被占」只能靠持有过期快照构造
- commit：`b6d3036 (测试适配)`
- 说明：命题（放置期失败 ⇒ 实例回收、无半放置态／零副作用）不变，改的只是失败原因这一项。
- 成员（4）：
  - `C2ReferenceAndFailureTests::Scenario3_Created_Instance_Recycled_When_Target_Slot_Occupied`
  - `CardServiceTests::Failure_Slot_Occupied_Rejected_And_Instance_Recycled_Silently`
  - `PlayChainDeploymentTests::Join_Rejects_Occupied_Slot`
  - `PlayChainUnitTests::Unit_Play_Direct_Drive_Rejects_Occupied_Slot`

### T-8 计数断言过期：低级流程触发端 20→21

- 拆前失败形态：`Assert.Equal() Failure: Values differ`（期望 20，实际 21）
- 归因类别：测试过期
- 改动位置：`tests/Orc.Game.Tests/TriggerRegistryTests.cs`：计数 20→21（两处），补 `JoinFrontline` 的 `Assert.Contains`，注释改「加入战场 4（3 触发端＋1 处理端）」
- 真源依据：装配动作目录是注册真源（`Assert.Equal(expectedEnds, …)` 已证明「注册表＝目录逐条展开」）；差的 1 项来自 `JoinFrontlineAsync` 批次引入的 `JoinFrontline` 触发端
- commit：`b6d3036 (测试适配)`
- 说明：属测试侧计数过期，不是导出面漏项（导出面漏项见 C-3）。
- 成员（1）：
  - `TriggerRegistryTests::Match_Registry_Is_Per_Match_And_LowLevel_Flow_Triggers_Are_Registered`

### T-9 沙盒 slot 语义过期：`slot` 是插入空隙序号，不是物理格位

- 拆前失败形态：`Assert.Equal() Failure: Values differ`（`slot=4` 实际落到 1；`slot=2` 实际落到 0）、`Assert.False()`、`Assert.Same()`、`ArgumentOutOfRangeException 插入点序号越界`
- 归因类别：测试过期
- 改动位置：`tests/Orc.Game.Tests/Sandbox/**`：先铺够元素再请求 N 号插入点；落位改 `line[i]`；「目标已占位」在新模型无对应物 → 改断「线满 ⇒ `slot-out-of-range`」或「同槽位 ⇒ `same-slot`」（同为硬门槛、零副作用）
- 真源依据：`docs/沙盒调试原语/requirements.md:74`「可指定精确槽位」＋`implementation.md` 把 `slot-out-of-range` 列为入口失败原因
- commit：`b6d3036 (测试适配)`
- 说明：**主因＝测试按旧固定槽位写**；次因＝同批修掉了 `TryResolveSlot` 的越界钳制（见 C-4，钳制曾在背后把 `slot=4` 蒙成 1）。本组用例在修复钳制前已改用合法 slot，故单独移除产品修复仍绿。
- 成员（9）：
  - `SandboxPrimitiveSignalEquivalenceTests::Unitize_MatchesEffectCardSequence`
  - `SandboxPrimitiveTests::HardGateTouched_ReturnsReason_AndSnapshotUnchanged`
  - `SandboxPrimitiveTests::Instantiate_ToFrontlineSlot_ExactSlot`
  - `SandboxPrimitiveTests::Instantiate_ToSupportSlot_ExactSlot`
  - `SandboxPrimitiveTests::Move_OccupiedTarget_Rejected`
  - `SandboxPrimitiveTests::Remove_OnFieldUnit_DeathChain`
  - `SandboxPrimitiveTests::Unitize_FromHandCard_EmitsPlacedAndJoined`
  - `SandboxPrimitiveTests::Unitize_OpponentSlot_Rejected`
  - `SandboxPrimitiveTests::Unitize_ToFrontlineSlot_Allowed`

## 产品缺陷

> C 档例外：每条用例单独成行，写全五列（症状／真源依据／改动侧／改动位置／commit）。
> 6 条用例、4 处真缺陷；同一缺陷的产品改动与随动测试合并为一个提交单元（产品改了测试不改则中间态必红）。

### C-1 `BattleLine` 空隙快照未随元素变更作废（B-1）

- 归因类别：产品缺陷
- 主因/次因：主因＝本处缺陷；次因＝（无）
- 改动位置：`src/Orc.Game/Board/BattleLine.cs`：`TryInsert`／`TryInsertAt`／`TryRemove`／`AppendInitial` 四个元素变更点追加 `_gapSnapshot = null`（强制下次重建，旧实例照常销毁）

| 用例 | 症状 | 真源依据 | 改动侧 | 改动位置 | commit |
| --- | --- | --- | --- | --- | --- |
| `KeywordProgramTests::HuShiConvertsSurvivingAttackedEnemyIntoRoutedArmy` | `replace` 整条失效：原单位已离场、替换单位未入场，事件流留「替换入场失败」；表面形态 `Index was out of range` | `CONTEXT.md`／`docs` 位置模型「快照一变即失效，任何入口现取现用」＋`BattleLine` 类注释「占用变化＝旧实例销毁并重建」；本次是**实现缺陷**，不是分类调整 | 产品 | `src/Orc.Game/Board/BattleLine.cs` | `8af7b79` |
| `KeywordProgramTests::ZhanlveTransformsIntoRoutedArmyInsteadOfDying` | 同上：`replace` 失败，`Zhanlve` 单位直接阵亡而非转为溃军 | `CONTEXT.md`／`docs` 位置模型「快照一变即失效，任何入口现取现用」＋`BattleLine` 类注释「占用变化＝旧实例销毁并重建」；本次是**实现缺陷**，不是分类调整 | 产品 | `src/Orc.Game/Board/BattleLine.cs` | `8af7b79` |
| `KeywordEffectChannelTests::GrantWithContent_Direct_Gate_Full_Lifecycle` | `InvalidOperationException : 准备单位失败：PositionStale`——取空隙→装载→加入之间发生「移除后再插」，元素集合回到历史快照，`GetInsertionGaps()` 复用了那批旧代际的死空隙 | `CONTEXT.md`／`docs` 位置模型「快照一变即失效，任何入口现取现用」＋`BattleLine` 类注释「占用变化＝旧实例销毁并重建」；本次是**实现缺陷**，不是分类调整 | 产品 | `src/Orc.Game/Board/BattleLine.cs` | `8af7b79` |

### C-2 部署重放丢失位置（B-2）

- 归因类别：产品缺陷
- 主因/次因：主因＝本处缺陷；次因＝（无）
- 改动位置：`src/Orc.Game/Cards/RetriggerSystem.cs`：`InvokeDeployKeywordAsync` 不再传 `null`，改传 `line.GetInsertionGaps().FirstOrDefault(g => g.Ordinal == line.IndexOf(unit))`（现取现用；满员时退化为 null，与旧防御一致）

| 用例 | 症状 | 真源依据 | 改动侧 | 改动位置 | commit |
| --- | --- | --- | --- | --- | --- |
| `DeathrattleAndRetriggerTests::Deploy_Retrigger_Replays_Injected_Effects_In_Registration_Order` | `Assert.All() Failure: 2 out of 2 items did not pass`——`view.Position` 恒为 `null`（位置模型重构把该实参改成了 `null`，迁移遗漏） | `docs/game-flow/05-指挥与交战.md`「部署重放＝重放部署效果段（不重做单位化/入槽）」——重放不**新增**插入点，但「该单位此刻在哪」是有效上下文（旧实现即传当前位置）；`FormationGap.Ordinal` 的定义＝插入后位置序号 | 产品 | `src/Orc.Game/Cards/RetriggerSystem.cs` | `8af7b79` |

### C-3 判定器导出面漏掉 `deploy.position.candidates`（B-3）

- 归因类别：产品缺陷
- 主因/次因：主因＝本处缺陷；次因＝（无）
- 改动位置：`src/Orc.Game/GameHooks.cs`：新增 `JudicatorDeployPositionCandidates` 常量；`JudicatorNameList`／`BuiltInJudicatorNames` 各补 1 项（列表序与常量声明序一致）；注释计数同步为 28 全量／27 内置。随动测试：断言 27→28、26→27

| 用例 | 症状 | 真源依据 | 改动侧 | 改动位置 | commit |
| --- | --- | --- | --- | --- | --- |
| `GameHooksTests::GameHooks_Reexports_All_JudicatorNames_And_TriggerLayers` | `Assert.Equal() Failure: Values differ`（期望 27，实际 28） | `Match.cs:814` 已把 `deploy.position.candidates` 注册进内置固定注册段（R4a 起生产在用），但 `GameHooks` 两份清单都没有它、也无转发常量 → 「reexports all」契约被破坏。**属产品侧漏同步**，不是「测试计数该更新」 | 产品（含随动测试） | `src/Orc.Game/GameHooks.cs`＋`tests/Orc.Game.Tests/GameHooksTests.cs` | `8af7b79` |

### C-4 沙盒 `TryResolveSlot` 把越界 slot 静默钳到末位（产品缺陷）

- 归因类别：产品缺陷
- 主因/次因：主因＝本处缺陷；次因＝（无）
- 改动位置：`src/Orc.Game/Sandbox/SandboxPrimitives.cs`：`TryResolveSlot` 越界（含满员）改报 `slot-out-of-range`，不再 `Math.Min(Math.Max(index,0), count-1)` 钳到末位；方法注释与实现恢复一致

| 用例 | 症状 | 真源依据 | 改动侧 | 改动位置 | commit |
| --- | --- | --- | --- | --- | --- |
| `SandboxPrimitiveTests::Unitize_MissingSlot_ReturnsReason` | `Assert.False() Failure`——请求 `slot=99` 本应失败，钳制后回 `Ok`（越界被静默吞掉） | `docs/沙盒调试原语/requirements.md:74`「**可指定精确槽位**」；`docs/沙盒调试原语/implementation.md:181,183` 把 `slot-out-of-range` 列为 `instantiate→line`／`unitize` 的入口失败原因。钳制会让 AI／脚本「以为落在请求位置」 | 产品 | `src/Orc.Game/Sandbox/SandboxPrimitives.cs` | `8af7b79` |

## 资产过期

### D-1 csx 制品引用已删除的 `UnitStateData.Position`

- 拆前失败形态：`InvalidOperationException : 词条 '抢修'／'动员' 的数据壳效果 '…' 实例化失败（compile）：“UnitStateData”未包含“Position”的定义`
- 归因类别：资产过期
- 改动位置：`samples/card-pack-bulwark/effects/keyword.repair.prefab.json`（`onMoved`／`onDeployed` 改 `PositionRules.IsOnFrontLine(unit)`）、`src/Orc.Game/Cards/KeywordPrefabs/keyword.mobilize.accrual.csx.prefab.json`（`state.Position is null` → `!state.IsOnBoard`）、`src/Orc.Game/EffectParsing/PositionRules.cs`（新增 csx 可达读口，转发 `GameEnvironment.IsOnFrontLine`，单一真源）
- 真源依据：`requirements.md` R3（csx 制品只引用现行 API；加载失败视为缺陷）＋位置模型硬切：`UnitStateData.Position` 已被 `Line`／`BoardPosition` 取代
- commit：`8af7b79 (资产过期)`
- 说明：沙箱约束：`CSharpScriptSecurityOptions.DefaultBlockedKeywords` 含 `Environment`，csx 内不能直接出现 `GameEnvironment`，故经 `PositionRules` 转发（先例：`RevealRules`）。效果生效有独立证据：`抢修_移动到前线即失去词条` 断 `Line==FrontLine` 且词条消失；`csx动员_…` 断 +1/+1 累积与受伤撤销，不止「能编译」。
- 成员（4）：
  - `BulwarkKeywordTests::抢修_己方回合开始_未移动未攻击则回满`
  - `BulwarkKeywordTests::抢修_移动到前线即失去词条`
  - `KeywordEffectCsxTests::csx制品_三样本源可编译且按视图绑定`
  - `KeywordEffectCsxTests::csx动员_迁移形态预演_双效果累积与受伤失去`

## 门禁授权

### E-1 契约基线重生成（显式授权）

- 拆前失败形态：`契约面发生未授权变更：…`
- 归因类别：门禁授权
- 改动位置：`tests/Orc.Game.Tests/Baselines/contract-freeze.json`：`ORC_WRITE_BASELINE=1` 重生成；diff＝头两行（commit／generatedAt）＋ **7 条 `EffectRuntime` 条目**（`JoinFrontlineAsync`／`JoinSupportLineAsync`／`PickHandCardsAsync`／`get_/property JoinFrontlineTrigger`／`get_/property JoinSupportLineTrigger`），其余面零 diff
- 真源依据：`requirements.md` 裁决 3＋R4：新增 API 认定为「手牌选靶与前线入场」批次已定稿功能，允许重生成；出现无关变动即视为夹带、须回退
- commit：`fdadc1f (门禁授权)`
- 说明：基线事实 4 的偏差实测**不成立**：新头 `2797057` 指向的提交已包含这批 API（`git grep JoinFrontlineAsync HEAD` 命中）。`PositionStale` 相关零 diff，与基线事实 8 一致（`resultShapes` 只冻结结果类型属性形状，不收录失败原因枚举）。
- 成员（1）：
  - `ContractFreezeTests::Contract_Baseline_Is_Unchanged`

### E-2 域标记白名单放行两处（显式授权）

- 拆前失败形态：`域标记被非授权位置读取（域的合法消费方只有构筑/存档、UI 展示、诊断报告）：src/Orc.Manager/AssetLibrary.cs、src/Orc.Server/Pool/PackPool.cs`
- 归因类别：门禁授权
- 改动位置：`tests/Orc.Game.Tests/AssetDomainTests.cs`：`allowedFiles` 新增两项，各带一行理由；`hits` 自检断言未改（仍要求 `CardBase.cs`／`CardLibrary.cs` 命中）
- 真源依据：`requirements.md` 裁决 4＋R4：`src/Orc.Manager/AssetLibrary.cs`＝资产管理面（按域枚举/对账资产条目）、`src/Orc.Server/Pool/PackPool.cs`＝卡池对账面（按域核对装配来源），认定为合法消费方；除此之外新增裸读一律按违规处理
- commit：`fdadc1f (门禁授权)`
- 说明：未放宽其它位置；不带理由的白名单项不成立（R4「每一处新增都有理由」）。
- 成员（1）：
  - `AssetDomainTests::No_Gameplay_Logic_Reads_Asset_Domain`

## 附：175 口径之外（同批收口，不计入 175）

### F-1 旧 slot 语义（`Orc.Mcp.Tests`，175 口径之外）

- 拆前失败形态：`sandbox.move {ref, line:"frontline", slot:2}` 返回 `Ok` 但 `slot=0`；`Assert.Equal(2, 0)`
- 归因类别：测试过期
- 改动位置：`tests/Orc.Mcp.Tests/SandboxPrimitiveToolTests.cs`：先铺 2 个单位到前线（并断言其 slot 为 0／1），使 2 号成为合法插入点，再 move 到 2；**原断言 `slot==2` 保留**
- 真源依据：`docs/沙盒调试原语/requirements.md:74`；`slot`＝该线当前快照的插入空隙序号，空前线只有 0 号一个空隙
- commit：`624aa90 (数据协议)`
- 说明：ref 本身可用（`unitize`／`move` 两步都成功），不是产品缺陷。
- 成员（1）：
  - `SandboxPrimitiveToolTests::ReturnedRefIsUsableByNextCall`

### F-2 样例包 `card-pack-across-the-pacific` 生成工具失真（`Orc.Packages.Tests`，175 口径之外）

- 拆前失败形态：内容指纹与 manifest 不一致；sidecar／manifest v2／两份 lock 不自洽
- 归因类别：资产过期
- 改动位置：`samples/card-pack-across-the-pacific/_tools/build.py`：指纹改为复现 `PackageVersion.ComputeContent`（SHA-256(包 id＋各 sidecar 规范化文本＋段长前缀)）、`lock.test.json` 写空、lock 条目按 `(kind, appId, version)` 排序、manifest/lock 写窄且不带尾换行；用 `tools/Orc.Tools.Migrate` 重建后 manifest／两份 lock／sidecar 逐字节一致
- 真源依据：`requirements.md` R5（每源文件有 sidecar、manifest v2、两 lock 一致、指纹重算一致）；数据由工具生成，手改 JSON 必然对不上指纹
- commit：`624aa90 (数据协议)`
- 说明：磁盘数据本就收敛（dry-run：`Create 0｜Skip 19｜Overwrite 0｜障碍 0`），真正失真在生成器；被 `.gitignore:29` 的 `**/_tools/` 规则忽略，故该修复**不入库**。重建后再 dry-run 仍 `Skip 19｜Overwrite 0`，`git diff --stat` 与重建前一致（44/101/18 行），未产生新增漂移；开局校验另发 `pool.assembled`（`definitions=10`、`fallback=false`）且 `state=InMatch`。
- 成员（2）：
  - `AssetMigrationToolTests::样例包_每个源文件都有sidecar且清单为v2与两lock一致`
  - `PackageVersionTests::样例包_重算内容指纹与manifest一致`

### F-3 用例未被执行：`[Theory]` 被写在注释行里（`Orc.Server.Tests`，175 口径之外）

- 拆前失败形态：无失败形态——3 个 `InlineData` 从未执行（`[Theory]` 属性被注释吞掉，整个方法不参与发现）
- 归因类别：测试过期（用例未被执行）
- 改动位置：`tests/Orc.Server.Tests/PackPoolTests.cs:256`：把误注释的 `[Theory]`／`InlineData` 恢复为活代码（不含 `src/**` 改动）
- 真源依据：`requirements.md` R1「全绿须意味着没有回归」：被注释的用例是**假绿**，恢复后 `Orc.Server.Tests` 从 109 → 112 全绿即为证明
- commit：`未提交（PackPoolTests.cs 属卡池批次未跟踪文件，见报告）`
- 说明：性质是「测试假绿的修复」，不属 175 条失败面，单列于此；补回后仍全绿，说明三条断言本身成立。
- 成员（3）：
  - `PackPoolTests::MatchStart_Rejects_PackDirectories_Key(shape: "empty")`
  - `PackPoolTests::MatchStart_Rejects_PackDirectories_Key(shape: "null")`
  - `PackPoolTests::MatchStart_Rejects_PackDirectories_Key(shape: "string")`

## 存疑

1. UnitTurnsInPlayTests::Transform_Rebuilt_Instance_Resets_Count —— 在 T2 分片清单内，但 `PROGRESS-T1/T2/T3/P` 均未单列该条的归因
   （`PROGRESS.md` 的 S3 节只记了它的失败文本由「不在支援线内」变为 `Placed→Rejected`，原因 `PositionStale`）。
   现按「转换重建后落位断言仍按旧槽位」归入 **T-3 槽位实体残留**；若主控认为主因是「先取空隙再变阵线」的过期快照用法（T-6），只需换组，不影响总数。
2. T2 分片条数：清单 58 条，`PROGRESS-T2.md` 记为 57（其表内 `CardServiceTests` 记 8 实为 9，且漏 `UnitTurnsInPlayTests` 1 条）。
   本表以清单为准；57＋58＋54＋6 ＝ 175，与 trx 失败集逐条相等。
3. `PlayChainDeploymentTests` 5 条、`MatchOutcomeTests` 5 条、`CardServiceTests` 9 条只得到**类级**归因
   （如 `PROGRESS-T2.md`「`PlayChainDeploymentTests`（5 条）测试过期（槽位实体残留＋布置）」），条级归属按各自改写动作推定，属「组级明确、条级推定」。

## 未纳入本表的改动（说明）

- `tools/orc/commands.py` 的 `PositionStale` 中文文案、`docs/game-flow/04-打出与部署.md` 的失败原因清单：**对外串同步**，不改变任何用例的绿/红，不在归因表成员内。
- `samples/card-pack-across-the-pacific` 的 6 个 effect prefab 仍用 `UnitStateData.Position`（`atp.beachhead-supply`／`atp.rapid-landing`／`atp.lvt-4.landing`／`atp.amphibious-landing`／`atp.mass-landing`／`atp.lvt-1.landing`）：当前无用例编译它们（`csx制品_*` 只校验内置三件），
  既不属 175 条也不在本轮文件范围（`PROGRESS-P.md` 待裁决 1），未登记为已完成。
- `samples/card-pack-across-the-pacific/_tools/build.py` 的修复被 `.gitignore:29`（`**/_tools/`）忽略，不入库；仓库内数据生成真源是 `tools/Orc.Tools.Migrate`。
