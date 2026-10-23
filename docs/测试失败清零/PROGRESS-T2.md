# 进度 — T2 片（58 条）

- 更新时间: 2026-10-23T02:20:00+08:00
- 工具调用计数: ~66
- 已处理: 57/57（分片清单实为 57 条；标题 58 与清单不符，已登记）
- 总体状态: 已完成

## 复跑结果

| 命令 | 结果 |
| --- | --- |
| 17 个归属类的过滤跑 | 失败 0 / 通过 163 / 跳过 0 |
| `Orc.Game.Tests` 全量（`--artifacts-path artifacts/test-t2`） | 失败 0 / 通过 1354 / 跳过 0 |
| `Orc.Mcp.Tests` 全量（产品改动回归，`artifacts/test-t2-mcp`） | 失败 0 / 通过 118 / 跳过 0 |

- 无新增 `Skip`、无删除 `[Fact]`（diff 自检：`[Fact]` 增删均为 0）。

## 逐条结论（用例 → 测试过期/产品缺陷/需求级歧义 → 改了什么）

### AuraSystemTests（10 条，全绿）— 全部「测试过期（布置）」
| 用例 | 改了什么 |
| --- | --- |
| `Scenario1_AdjacentAura_Follows_Source_Presence_Movement_And_Death` | 前线空线被 `At(2)` fail-fast；改为先铺一个参照单位（gap 0）→ 受益单位 gap 1；源加入 gap 2、移动 gap 1 / gap 0 |
| `Scenario1b_AdjacentAura_Stacks_From_Multiple_Sources` | 受益单位 gap 0；左源 gap 0（插入后位于左邻）、右源 gap 2 |
| `Scenario1c_AdjacentAura_Follows_Explicit_Effect_Removal` | 受益单位 gap 0、源 gap 1 |
| `Scenario2_SelfCondition_FrontLine_Attack_Bonus_Follows_Position` | 前线空线：移动目标由 gap 2 改 gap 0 |
| `Scenario3_SourceCondition_Grants_Whole_Support_Line` | 前线先放参照单位；源到前线 gap 1、换位 gap 0、复位 gap 1 |
| `Scenario4_AdjacentCount_Scales_And_Falls_Back` | 计数单位 gap 0；友邻 gap 0；敌方邻位受「前线敌对门禁」限制，改机制级落位；离场改移回支援线 |
| `Environment_Queries_Are_ReadOnly_And_Safe` | 前线 0 / 间隔单位 1 / far 2；支援线 HQ 后 1、2 |
| `Modifier_And_Aura_Compose_On_Same_Field` | 受益单位 gap 0、源 gap 1 |
| `Aura_Collects_Across_Fields_And_Unregisters_By_Source` | 受益单位 gap 0、源 gap 1 |
| `StatChanged_Drives_Full_Rerun_For_Cross_Card_Conditions` | 观察者 gap 0、邻位 gap 1 |

### 其余 47 条
| 用例/组 | 归因 | 改了什么 |
| --- | --- | --- |
| `CardDataComponentTests::UnitStateData_Defaults_And_ReadWrite` | 测试过期（槽位实体残留） | `Assert.Same(line.GetInsertionGaps().At(1), state.Line)` → `Assert.Same(line, state.Line)`（原断言跨类型、永不可能成立） |
| `StatWiringTests::Attack_Modifier_Changes_Actual_Damage_Through_Basic_Combat`、`StatPortalTests::Damage_Chain_Through_Basic_Combat_...` | 测试过期（布置） | 敌方目标支援线 `At(2)` → `At(1)`（空位假设） |
| `MatchHistoryReadTests::Last_Died_Friendly_Unit_Read_Filters_Recent_And_Owned` | 测试过期（布置） | 敌方死者改放自家支援线（前线敌对门禁） |
| `MatchHistoryReadTests::Read_Then_Transform_Rebuild_End_To_End` | 测试过期（槽位实体残留） | 落位断言改「阵线引用＋序号」 |
| `MatchOutcomeTests`（5 条） | 测试过期（布置＋槽位实体残留） | 巨炮 `At(2)`→`At(1)`；HQ 占位改 `Members[0]`；打出/加入 `At(3)`→`At(0)`；移动触发器 OldPosition 改 `BoardPosition`；「槽位不变」改「位置不变（记录序号）」 |
| `OfficialCardDataSmokeTests::Official_Corpus_Loads_Fully_And_One_Card_Can_Be_Deployed` | 测试过期（槽位实体残留） | `Assert.Same(unit, gaps.At(1))` → `Assert.Same(unit, supportLine[1])` |
| `P2ActionFamilyTests::Effect_Move_Is_Free_And_Consumes_No_Operate_State` | 测试过期（槽位实体残留） | 落位断言改「线引用＋序号 0」 |
| `P3ActionFamilyTests::Join_And_Deploy_Share_One_Handle_End_With_No_Entry_Flag` | 测试过期（槽位实体残留） | `Assert.Same(joinSlot, state.Line)` → `Assert.Same(supportLine, state.Line)` |
| `P3ActionFamilyTests::Illegal_Join_Target_Is_Target_Invalid_With_Zero_Side_Effects` | 测试过期（布置） | 非法目标＝过期空隙（先取 gap、再让阵线变化）；断言 TargetInvalid/零副作用不变 |
| `PlayChainDeploymentTests`（5 条） | 测试过期（槽位实体残留＋布置） | 目标空隙捕获后比较触发视图；落位断言改 `line[n]`；`unit.joined` 载荷改 `BoardPosition`；「已占位」改「同一空隙再用＝PositionStale」 |
| `PlayChainUnitTests`（5 条） | 测试过期（布置＋槽位实体残留） | 满线布置补实（原为空循环）；候选选中位置改 0 号位；`At(3)`→`At(0)`；「已占位」改过期空隙→`FormationGapUnavailable`；重复单位化前放回手牌以穿过手牌门禁 |
| `PlayChainUpdateTests`（2 条） | 测试过期（槽位实体残留） | 部署时点探针 `ReferenceEquals(position, unit)` → 「位置线上含该单位且序号一致」；`unit.joined` 载荷改 `BoardPosition` |
| `RandomEffectScenarioTests`（2 条） | 测试过期（布置） | 友方 2 名改置支援线（不占前线）；敌方前线单位 `At(3)`→`At(0)`；候选枚举序与 `ChosenIndex=0` 锚点不变 |
| `CardServiceTests`（8 条） | 测试过期（布置/槽位实体残留）＋1 处辅助代码 | `At(3)`/`At(4)` 改有效插入点（`[^1]` 或铺满到 4 元素）；`unit.joined` 载荷改 `BoardPosition`；转换落位改「原序号」；复制源位置改 `[1]`；相邻解析候选改「全部插入点」；「已占位」改 `PositionStale` |
| `CardServiceTestInfrastructure`（辅助） | 辅助代码过期 | `SceneOp.Transform` 原「放回原位置」实现为 `At(0)`（错位）→ 改为离场前记下原序号、按原序号取插入点 |
| `SandboxPrimitiveTests`（9 条）、`SandboxPrimitiveSignalEquivalenceTests`（1 条） | 测试过期（slot 语义）＋**产品缺陷 1 处** | 见下节 |

## 产品缺陷（本片唯一一处，已改 src）

**`SandboxPrimitives.TryResolveSlot` 越界静默钳制**

- 症状：`sandbox.instantiate/unitize/move` 传越界 `slot` 时被 `Math.Min(Math.Max(index,0), count-1)` 钳到末位并返回 ok；`slot=4` 报告 `slot=1`，`slot=99` 也成功。
- 真源依据：`docs/沙盒调试原语/requirements.md:74`「**可指定精确槽位**」；`docs/沙盒调试原语/implementation.md:181,183` 把 `slot-out-of-range` 列为 `instantiate→line`/`unitize` 的入口失败原因；同批 `Orc.Mcp.Tests::SandboxPrimitiveToolTests.ReturnedRefIsUsableByNextCall` 已写明「slot＝该线当前快照的插入空隙序号，要让 2 号成为合法插入点，先铺 2 个单位」——钳制会让这句话失去意义，并让 AI/脚本调用「以为落在请求位置」。
- 改动侧：**产品**（最小局部，4 行判定）。
- 位置：`src/Orc.Game/Sandbox/SandboxPrimitives.cs::TryResolveSlot`（越界＝`slot-out-of-range`，不再钳制；满员同样报此原因）。
- 影响面核对：`Orc.Mcp.Tests`（46 条 Sandbox 用例）全绿；`Orc.Game.Tests` 沙盒 39 条全绿。

对应测试侧改写（同一缺陷随动，按 R2「slot＝插入空隙序号」）：
- `Instantiate_ToSupportSlot_ExactSlot`：先铺到 4 元素再精确请求 4 号插入点（原 `slot=4` 靠钳制蒙对）。
- `Instantiate_ToFrontlineSlot_ExactSlot`：先铺 1 个再请求 1 号。
- `Unitize_FromHandCard_EmitsPlacedAndJoined`：`slot 4`→`1`（追加位）+ 落位按 `[1]` 核对。
- `Unitize_ToFrontlineSlot_Allowed`：`slot 3`→`0`（空前线唯一插入点）。
- `Unitize_MissingSlot_ReturnsReason`：`99` 现在如预期得到 `slot-out-of-range`（用例未改）。
- `Move_SupportToFrontline_Allowed`：`slot 2`→`0`，删去「宽容 clamp」注释。
- `Instantiate_NonUnitToLine_Rejected`：`slot 3`→`0`（保持 not-a-unit-card 判定不被越界抢先）。
- `Unitize_OpponentSlot_Rejected`：`Assert.Same(card, gap)` → `Assert.Same(card, line[0])`。
- `Remove_OnFieldUnit_DeathChain`：`Assert.Equal(0, line.Size)` → 「不含该单位 + 只余 HQ」。
- `HardGateTouched_...`：原「放到已占位槽 ⇒ target-slot-occupied」在新模型无对应物（插入点无「被占」形态）→ 改「线满 ⇒ slot-out-of-range（同为硬门槛、零副作用）」。
- `Move_OccupiedTarget_Rejected`：同上无对应物 → 改「同槽位 ⇒ same-slot」。
- `SandboxPrimitiveSignalEquivalenceTests::Unitize_MatchesEffectCardSequence`：两条路径同用 `slot 1`，落位按线＋序号核对。

## AuraSystemTests 专项（布置 vs 产品 的判定依据）

1. 拆钳制后的失败形态全为 `ArgumentOutOfRangeException：插入点序号越界（合法范围：0..0）`，抛出点＝`PrepareOnFrontAsync`/`frontLine...At(2)`——**前线初始为空线**，旧 `At` 把 2 钳到 0，掩盖了「测试假设前线有 3 个格位」这一过期前提。
2. 只改布置（绝对格位 → 「按序铺单位后的相对位置」），**未改任何期望值**：`Scenario1b` 仍断言 6 → 4 → 2，修完即绿。故 4/2 的「数值偏小」确系钳制扭曲布置的假象（基线事实 6），**光环收集逻辑无缺陷**。
3. `AuraSystemTests` 10/10 通过、`src/Orc.Game/**` 未因光环改动。
4. 顺带记录的产品现象（未改、未作为本片缺陷）：`PlayManager.JoinUnitAsync` 经「前线敌对门禁」被拒时 `PlayResult` 仍报 Success（`TargetInvalid` 只在 `ActionResult` 面）。

## 已改 src 文件

- `src/Orc.Game/Sandbox/SandboxPrimitives.cs` —— `TryResolveSlot` 越界不再钳制（唯一一处产品改动；该文件其余在途内容未动）。

## 待裁决

1. 分片清单标题 58 条、实际列 57 条（逐条点数：10+1+8+2+5+1+1+2+5+5+2+2+1+9+1+1+1＝57），按清单执行、未自行补漏。
2. 沙盒「目标位置已被占用」在新位置模型下无对应物（插入点无「被占」形态）：`HardGateTouched_...` / `Move_OccupiedTarget_Rejected` 两条按「目标不可用→拒绝＋零副作用」改验 `slot-out-of-range` / `same-slot`。若产品仍需要「被占」语义（例如按元素位而非插入点表达），需另定协议。
3. 需确认是否允许随本片提交 `src/Orc.Game/Sandbox/SandboxPrimitives.cs`（产品缺陷独立单元，与本片测试改动分开）。
