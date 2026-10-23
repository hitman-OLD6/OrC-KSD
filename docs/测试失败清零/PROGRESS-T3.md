# 进度 — T3 片（54 条）

- 更新时间: 2026-10-23T21:20:00+08:00
- 工具调用计数: ~86
- 已处理: 54/54（T3 过滤跑 **失败 0 / 通过 217**）
- 总体状态: 已完成（含 3 处产品侧修复待裁决归口）

## 基线实测

- 命令：`dotnet test tests/Orc.Game.Tests/... --artifacts-path "D:/OrC-KSD/artifacts/test-t3" --filter "<T3 的 20 个测试类>"`
- 基线：**失败 54 / 通过 163 / 跳过 0 / 总计 217**（`artifacts/trx-t3/t3-base.trx`），与 `artifacts/split/T3.md` 逐条一致。
- 收尾：**失败 0 / 通过 217 / 跳过 0**（`artifacts/trx-t3/t3-after.trx`）。
- **全量回归**（`t3-full.trx`）：`Orc.Game.Tests` **失败 10 / 通过 1344 / 跳过 0 / 总计 1354**。
  - 与开工前基线（`artifacts/trx/s3-game.trx`，175 红）对比：**新冒出的红＝0**，转绿 165。
  - 剩余 10 条全部落在 `Sandbox/SandboxPrimitiveTests.cs` / `SandboxPrimitiveSignalEquivalenceTests`（**T2 片在途文件**，不属 T3）。

## 逐条结论（用例 → 归因 → 改了什么）

### A. 测试过期——按序铺位／现取现用（44 条）

| 用例 | 拆后形态 | 改法 |
| --- | --- | --- |
| `CovertMechanismTests::C1_…Instruction_Targeting…` | `插入点序号越界 0..1`（`index=2`） | 对照单位 `PrepareOnSupportAsync(…,2→1)`（A 支援线只含 HQ） |
| `CovertMechanismTests::C9_Request_Level_Override_Includes_Covert_Unit` | 同上 | 同上（2→1） |
| `CovertMechanismTests::C2_Attacked_Covert_…` | `0..0`（`index=1`） | 空前线只有 1 个插入点：`PrepareOnFrontAsync(…,1→0)` |
| `CovertMechanismTests::C3_Covert_Attacker_Is_Revealed_On_Attack` | 同上 | 同上 |
| `CovertMechanismTests::C8_Attack_Candidates_Include_Covert_Unit` | 同上 | 同上 |
| `CovertMechanismTests::C12_Both_Covert_Sides_Reveal…` | 同上 | 同上 |
| `CovertMechanismTests::C4_Movement_Does_Not_Reveal_Covert_Unit` | `Assert.Same` 元素 vs 空隙 | `Assert.Same(unit, FrontLine.Members[0])`（"移动已发生"期望不变） |
| `CovertMechanismTests::C7c_Pincer_With_Only_Covert_Candidates…` | `0..2`（`index=3`） | 部署 `At(3→2)`（末位），落位断言改 `Members[^1]` |
| `KeywordProgramTests::RetreatUsesNormalDragCostsAndMovementOpportunity` | `Assert.Same(gap, state.Line)` | 比 `PlayerASupportLine`（线≠插入点） |
| `KeywordProgramTests::RetreatPermissionIsRechecked…` | 同上 | 比 `FrontLine` |
| `KeywordProgramTests::EffectDisplacementUsesThePortal…` | 同上＋第二段 `At(1)` 越界 | 比 `PlayerASupportLine`；二次移动目标 `At(1→0)`（此时前线已空） |
| `KeywordEffectBatch2Tests::Ambush_Rewrite_Follows_Effect_Detach…` | `0..1`（`index=2`） | 受击者已阵亡＝线回到 `[HQ]`：2→1 |
| `KeywordEffectBatch2Tests::Ambush_Behavior_Revoked…` | 同上 | 2→1 |
| `KeywordEffectBatch2Tests::Ambush_Death_Revokes_Behavior…` | 同上 | 2→1 |
| `KeywordEffectBatch2Tests::Runtime_Grant_Revoke_Closure…` | `0..0`（`index=1`，前线） | 坦克每轮被反击致死：四个 `PrepareActiveTankAsync(0/1/2/3→0)` |
| `KeywordEffectBatch3Tests::Blitz_Behavior_Revoked…` | `0..2`（`index=3`） | 第二次部署 `At(3→2)`（末位） |
| `KeywordEffectBatch3Tests::Blitz_MultiUnit_Deployment…` | 同上 | 同上 |
| `KeywordEffectBatch3Tests::Blitz_Runtime_Grant_Revoke_Closure…` | 同上 | 同上 |
| `KeywordEffectBatch3Tests::Blitz_Death_Revokes_Subscription…` | `NullReferenceException` | 死亡后 `Line` 置空（模型事实）：生前取一次 `BoardPosition` 并复用 |
| `KeywordBatch2Tests::Pincer_Deploy_Form_Pair…` | `0..1`（`index=2`） | friend 2→1 |
| `KeywordBatch2Tests::Pincer_Deploy_Cancel_Leaves_No_Pair` | 同上＋`Assert.Same(pincer, gap)` | friend 2→1；落位断言 `Members[1]` |
| `KeywordBatch2Tests::Pincer_Member_Death_Breaks_Pair…` | 同上 | friend 2→1 |
| `KeywordBatch2Tests::Pincer_Deploy_Without_Candidates…` | `Assert.Same(pincer, gap)` | `Assert.Contains(pincer, supportLine.Members)` |
| `KeywordBatch2Tests::Pincer_Paired_Members_Are_Excluded…` | `0..1`（`index=3`） | friend 3→1（后续 `At(3)` 复归合法） |
| `TriggerCardTests::Effect_Initiated_Attack_Carries_TriggerCard…` | `0..1`（`index=2`） | effectHost 2→1 |
| `TriggerCardTests::Effect_Initiated_Damage_Directly_Carries_TriggerCard` | 同上 | 同上 |
| `TriggerCardTests::Move_View_Leaves_TriggerCard_Empty_On_Player_Command` | `Assert.Same(mover, gap)` | `Members[0]` |
| `TriggerCardTests::Command_View_Carries_TriggerCard…` | 同上 | 同上 |
| `TriggerCardTests::Move_View_Carries_TriggerCard_On_Effect_Initiated_Move` | `ValidationRejected` | `OldPosition` 载荷在位置模型＝`BoardPosition`（判定器 `refs[1].Value is not BoardPosition` 即拒）；改用 `new BoardPosition(line, IndexOf(unit))`，落位断言改 `Members[0]` |
| `EffectRemountAndStrategyTests::Aura_Effect_Remount_Rebuilds_Managed_Cleanup…` | `0..0`（`index=2`） | 受益单位 2→0，光环源 `At(1)`（相邻） |
| `EffectRemountAndStrategyTests::Strategy3_Direct_Mount_Fires_On_Own_Deploy_Chain_Only` | `0..0`（`index=1`）＋`FormationGapUnavailable` | 部署入口只接受部署线（默认＝支援线）：改 `GetSupportLine(playerA)`，`other`=At(1)、`unit`=At(0) |
| `EffectRemountAndStrategyTests::Card_Placed_Emitted_After_Unitize…` | `FormationGapUnavailable`（期望 Success） | 同上：部署目标改支援线（默认部署不得到前线） |
| `DeathrattleAndRetriggerTests::Deathrattle_Settles_Before_Cleanup…` | `0..0`（`index=1`） | killer 1→0；`LineReadable` 观测项改成比"所在线"（原比插入点，模型下恒假） |
| `DeathrattleAndRetriggerTests::Deploy_Retrigger_Replays_Injected_Effects…` | `Assert.All`（`view.Position`） | 重放上下文位置＝当前快照里"该单位位置序号"的插入点：改断 `Position.Line`＋`Position.Ordinal == IndexOf(unit)`（产品侧同步修，见 B-2） |
| `DeathrattleAndRetriggerTests::Deploy_Retrigger_Shares_Single_Source_With_Play_Chain` | 期望 Success／实际 Failed | `PlayUnitAsync` 要求卡在手：补 `toHand: true`（意图＝从手牌打出） |
| `GuardKeywordTests::Guard_DataBody_Protects_Adjacent_And_Hq_Never_Self` | `0..1`（`index=4`） | 前线炮 4→1；并把"被守护步兵/守护者"落位改成 `[步兵, 守护者, HQ]`（相邻＝序号连续） |
| `GuardKeywordTests::Guard_Grant_Revoke_Regrant_Drives_Maintenance` | `IsUnitGuarded=false` | 同上：邻位改 `[HQ, 授予者, 邻位]`（授予者与 HQ、邻位均相邻）。**判定未变宽**——是布置把 HQ 夹在了中间 |
| `KeywordSystemTests::Fury_Tank_Moves_Once_And_Attacks_Twice` | `0..1`（`index=2`） | 敌方支援线 2→1 |
| `KeywordSystemTests::Keywords_Are_Registered_At_Load_And_Kept_After_Death` | `0..0`（`index=4`） | killer 前线 4→0；伏击兵 2→1 |
| `SpaceWarKeywordTests::游击_前线可指挥移动回支援线_无游击不可` | `Assert.Same(gap, state.Line)` | 比 `PlayerASupportLine` |
| `KeywordComponentTests::Death_Revokes_Behavior_But_Keeps_Registration_And_Value` | `0..0`（`index=1`） | killer 前线 1→0 |
| `KeywordEffectChannelTests::GrantWithContent_Direct_Gate_Full_Lifecycle` | `准备单位失败：PositionStale` | **产品缺陷 B-1 修复后自然转绿**（取空隙→装载→加入之间发生"移除后再插"，旧快照被复用） |
| `VeteranMechanismTests::Failure_Precheck_Rejections…` | `0..0`（`index=1/2/3/4`） | 前线单位依次 1→0、2→1、3→2、4→3（逐个阵亡/排布后现取） |
| `TurnPhaseEffectTests::Delayed_Action_Executes…` | `0..2`（`index=3`）＋两处 `Assert.Same(unit, gap)` | 移位目标 3→0；四处断言改 `Ordinal == IndexOf(unit)` / 比线 |
| `HqEntityTests::Scene1_Hq_Entities_Place_On_The_Middle_Slot…` | `Assert.Same(line[0], Hq.Line)` | 比 `lineA/lineB`（HQ 所在线） |
| `HqEntityTests::Scene1_Zero_Does_Not_Run_Death_Or_Destroy…` | 同上式 | `Assert.Same(hq, PlayerBSupportLine.Members[0])`（占位保留） |
| `TriggerSourceSignalTests::Unit_Deployed_Carries_Unit_As_Source` | 期望 Success／实际 Failed | 部署改支援线（默认部署线） |
| `TriggerSourceSignalTests::Card_Played_Carries_Played_Card_As_Source` | 同上 | 同上 |
| `DiyExample08::突袭_…_撤退到己方支援线` | `Assert.Contains` 用线比元素 | 比 `GetSupportLine(playerA)`＋断言单位在线内 |
| `GameHooksTests::Zero_Callpoint_Hooks_Are_Emittable_With_Frozen_Payloads` | `0..0`（`index=1`） | 空 `BattleLine(4)` 只有一个插入点：改为直接构造 `new BoardPosition(line, 0/1)`（载荷本就是位置值） |

### B. 产品缺陷（3 处，改 `src/Orc.Game/**`）

- **B-1 `src/Orc.Game/Board/BattleLine.cs`：空隙快照缓存复用了已失效实例**
  - 症状：`HuShiConverts…`／`ZhanlveTransforms…` 的 `replace` 整条失效（原单位已离场、替换单位未入场，事件流留 `替换入场失败`）；`KeywordEffectChannel::GrantWithContent…` 报 `PositionStale`。
  - 根因：`TryInsert`／`TryInsertAt`／`TryRemove`／`AppendInitial` 只递增 `_generation`、未作废 `_gapSnapshot`。元素集合一旦回到某历史快照（A→A+B→A），`GetInsertionGaps()` 直接返回那批**旧代际**空隙——调用方"现取现用"拿到的却是 `IsCurrent == false` 的死空隙。
  - 依据：`CONTEXT.md`／`docs` 位置模型「快照一变即失效；任何入口现取现用」；`BattleLine` 类注释「占用变化＝旧实例销毁并重建」。
  - 改动：四个元素变更点追加 `_gapSnapshot = null`（强制下次重建，旧实例照常销毁）。
  - 归因类别：产品缺陷（R2 语义方向，但本次是实现缺陷，不是分类调整）。

- **B-2 `src/Orc.Game/Cards/RetriggerSystem.cs`：部署重放上下文丢失位置**
  - 症状：`Deploy_Retrigger_Replays…` 的 `view.Position` 恒为 `null`。
  - 根因：位置模型重构（`5514e35`）把 `InvokeDeployKeywordAsync(owner, state.Position, …)` 改成了 `(…, null, …)`——旧"槽位"无对应物，直接丢参。
  - 依据：`05-指挥与交战.md`「部署重放＝重放部署效果段（不重做单位化/入槽）」——重放没有**新增**插入点，但"该单位此刻在哪"是有效上下文（旧实现即传当前位置）；`FormationGap.Ordinal` 的定义＝插入后位置序号，可用以表达位置序号。
  - 改动：取 `line.GetInsertionGaps().FirstOrDefault(g => g.Ordinal == line.IndexOf(unit))` 传入（现取现用；整线满员时退化为 null，与旧防御一致）。
  - 归因类别：产品缺陷（迁移遗漏）。

- **B-3 `src/Orc.Game/GameHooks.cs`：判定器导出面漏掉 `deploy.position.candidates`**
  - 症状：`GameHooks_Reexports_All_JudicatorNames_And_TriggerLayers` 期望 27 实际 28。
  - 判定：`Match.cs:814` 已把它注册进**内置固定注册段**（R4a 起生产在用），但 `GameHooks.JudicatorNameList`／`BuiltInJudicatorNames` 都没有它，也没有对应 `Judicator*` 转发常量 → "reexports all" 契约被破坏。**属产品侧漏同步**，不是"测试计数该更新"。
  - 改动：新增 `JudicatorDeployPositionCandidates` 常量；两份清单各补 1 项（列表序与常量声明序一致）；同步注释计数（28 全量 / 27 内置）。测试侧断言 27→28、26→27 并补 `JoinFrontline` 之外的说明。
  - 归因类别：产品缺陷（导出面漏项）。

### C. 计数断言更新（1 条，属测试过期）

- `TriggerRegistryTests::Match_Registry_Is_Per_Match_And_LowLevel_Flow_Triggers_Are_Registered`（期望 20 实际 21）
  - 判定：**测试计数过期**。动作目录是装配真源，`Assert.Equal(expectedEnds, lowLevel…)` 已证明"注册表＝目录逐条展开"；实际差 1 项来自「加入战场」多了 `JoinFrontline` 触发端（`JoinFrontlineAsync` 批次引入，接口本就是 3 触发端＋1 处理端）。
  - 改动：计数 20→21（两处），补 `JoinFrontline` 的 `Assert.Contains`，注释改为「加入战场 4（3 触发端＋1 处理端）＝21」。

## 已改 src 文件

| 文件 | 内容 | 归因 |
| --- | --- | --- |
| `src/Orc.Game/Board/BattleLine.cs` | 4 个元素变更点作废 `_gapSnapshot` | 产品缺陷 B-1 |
| `src/Orc.Game/Cards/RetriggerSystem.cs` | 部署重放传当前位置插入点（不再传 null） | 产品缺陷 B-2 |
| `src/Orc.Game/GameHooks.cs` | 补 `JudicatorDeployPositionCandidates` 常量＋两份清单＋注释计数 | 产品缺陷 B-3 |

> 未改任何被冻结/他人文件：`TestGapListExtensions.cs`、`TestInfrastructure.cs`、`CommandTestInfrastructure.cs`、`TargeterTestInfrastructure.cs`、`C2ChainTestKit.cs`、`AuraTestInfrastructure.cs`、`CardServiceTestInfrastructure.cs`、`PlayChainTestInfrastructure.cs`、`ModifierTestInfrastructure.cs`、卡池批次文件。

## 待裁决

1. **B-1 影响面**：`BattleLine` 修复会让"取空隙→阵线变化→再用"的路径从"静默失效"变成"正常"，可能让其它分片里若干 `PositionStale`／`准备单位失败` 用例一并转绿。建议 T1/T2 以本修复为前提复跑，避免重复归因。
2. **B-2 语义口径**：重放的"位置"用「该单位当前的位置序号对应的插入点」表达（`FormationGap.Ordinal == IndexOf(unit)`）。若后续把 `CardTriggerView.Position` 改为 `BoardPosition`，本处应随之调整；当前保持既有字段类型不变。
3. **B-3 归口**：三处产品修复各自应单开 C 档缺陷 commit（`src/Orc.Game/**` ＋随动测试），本片按纪律**未 commit**，待主控决定提交单元。
