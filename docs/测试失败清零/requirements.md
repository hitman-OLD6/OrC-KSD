# Requirements — 失败测试清零

把 `OrcEngine.sln` 的测试全量跑绿，并且**每一条变绿都有归因**（是测试该改、是产品该改、是资产该改、是门禁该授权），不允许用 Skip／删断言／放宽产品校验换取绿色。

## Scope
- Includes: `tests/Orc.Game.Tests`、`tests/Orc.Packages.Tests`、`tests/Orc.Mcp.Tests` 三条红线的处置；随之需要修改的生产代码、生产资产（csx prefab）、样例包数据（sidecar/lock/manifest）、门禁基线。
- Excludes: 新增功能；性能优化；`Orc.Tests`／`Orc.Script.Tests`／`Orc.Ai.Tests`／`Orc.Server.Tests`（当前全绿，仅作为回归护栏）；人类 CLI 客户端（`tools/orc`）的 Python 测试。

## Constraints
- **真源顺序**：`docs/<功能>/requirements.md` 与 `CONTEXT.md` 术语 → 代码行为 → 测试断言。测试断言与真源冲突时改测试；代码行为与真源冲突时改代码。**真源本身有歧义时停下来问，不要自行选边。**
- **不得弱化门禁**：`ContractFreezeTests`、`AssetDomainTests`、`*IsAModingEntry*` 一类"架构/契约门禁"用例，只能通过「显式授权变更」（更新基线/白名单，并在文档里记录理由）变绿，不允许改成恒真断言或加 `Skip`。
- **不得删用例**：可以重写用例（改断言、改布置方式），不允许删除 `[Fact]`／用 `Skip="..."` 绕过。确实不再成立的用例必须先在此文档登记，经确认后删除。
- **位置模型口径**（本次失败的主因）：阵线是**有序列表**，元素＝HQ 与单位，顺序即位置；可插入位置＝N+1 个**临时空隙**（`FormationGap`），快照一变即失效（`IsCurrent=false`）。任何"提前持有空隙再使用"的代码/测试都是**过期快照**，必须改，不能靠"再查一次"绕过。
- **失败不静默**：过期空隙／非法目标的处置口径保持不变（调用方契约错误＝异常，玩家动作失败＝结果对象），不得为了让测试好写而统一成一种。

## 现状（2026-10-23 实测，`dotnet test OrcEngine.sln`）

| 测试项目 | 失败／总数 | 备注 |
| --- | --- | --- |
| Orc.Tests | 0 / 370 | 绿 |
| Orc.Script.Tests | 0 / 17 | 绿 |
| Orc.Ai.Tests | 0 / 13 | 绿 |
| Orc.Server.Tests | 0 / 110 | 绿 |
| Orc.Mcp.Tests | **1** / 118 | `SandboxPrimitiveToolTests.ReturnedRefIsUsableByNextCall` |
| Orc.Packages.Tests | **2** / 363 | 样例包 `card-pack-across-the-pacific` 迁移/指纹 |
| Orc.Game.Tests | **111** / 1354 | 见下表 |
| Orc.Manager.Tests | 0 / 20 | 绿（需 `--artifacts-path` 构建，见下） |

**构建环境约束**：本机有一个用户常驻的管理器实例（`dotnet Orc.Manager.dll serve --port 51999`）锁住 `src/Orc.Manager/bin/Debug/net8.0/` 下的 DLL，任何重建 `Orc.Manager` 的 `dotnet test` 会报 MSB3027／MSB3021。**禁止杀掉该进程**；统一改用 `dotnet test <proj> --artifacts-path D:\OrC-KSD\artifacts/test` 构建到独立目录。全量验收命令同理。

**全量判据**：以 `OrcEngine.sln` 为唯一口径；`OrcEngine.slnx` 是同一项目集的镜像，仅用于最终交叉核对（两者项目集若不一致，以 `.sln` 为准并登记差异）。

`Orc.Game.Tests` 111 条按证据强度分三档：

### A 档：异常/失败信息直接指向"旧模型"（约 53 条，测试侧为主）
| 类 | 条数 | 典型消息 | 初判 |
| --- | --- | --- | --- |
| 断言引用错位 | 36 | `Assert.Same() Values are not the same instance`（`Assert.Same(gap, state.Line)`、`Assert.Same(unit, gap)`） | 测试按"槽位实体"写，改测试 |
| 位置序号越界 | 7 | `位置序号越界（合法范围：0..-1）`（`line[1]`／`front[1]`） | 测试按固定格位取元素，改测试 |
| 准备单位失败 | 6 | `准备单位失败：TargetSlotOccupied` | **待查**：可能是测试辅助持过期空隙，也可能是产品判定 |
| 空隙不在线内 | 4 | `目标槽位 'Formation gap 1' 不在玩家 #0 的支援线内` | 测试先取空隙、再插单位导致快照失效，改测试 |

### B 档：产品侧或资产侧（8 条）
| 用例 | 根因 | 初判 |
| --- | --- | --- |
| `BulwarkKeywordTests::抢修_*`（2）、`KeywordEffectCsxTests::csx*`（2） | csx prefab 里 `unit.GetData<UnitStateData>().Position` —— `Position` 属性已被 `Line` 取代，编译失败 → 词条效果整条不可用 | **改生产资产**（`samples/card-pack-bulwark/effects/keyword.repair.prefab.json`、`src/Orc.Game/Cards/KeywordPrefabs/keyword.mobilize.accrual.csx.prefab.json`） |
| `JudicatorAssemblyTests::KeywordRuleOverride...`、`JudicatorCombatLegalityTests::Sub_Rule_Entries...` | `CombatRangeJudicator` 声明载荷第 1 参为 `BoardPosition`，实际调用传 `BattleLine`／`FormationGap` → fail-fast | **改生产代码契约**（判定器签名与调用方对齐） |
| `AssetDomainTests::No_Gameplay_Logic_Reads_Asset_Domain` | `src/Orc.Manager/AssetLibrary.cs`、`src/Orc.Server/Pool/PackPool.cs` 读了 `AssetDomain` | **待裁决**：白名单授权 or 改写法 |
| `ContractFreezeTests::Contract_Baseline_Is_Unchanged` | `EffectRuntime` 新增 `JoinFrontlineAsync`/`JoinSupportLineAsync`/`PickHandCardsAsync`/`Join*Trigger` | **待裁决**：重新生成基线（需确认这些 API 是已定稿功能） |

### C 档：需要逐条判定（约 50 条，含真实行为差异）
| 现象 | 代表用例 | 说明 |
| --- | --- | --- |
| 数值偏小 | `AuraSystemTests` 4 条（期望 6/4，实际 4/2） | **先拆测试侧钳制再重判**：`TestGapListExtensions.At` 越界钳到末位，已证实 `Scenario1b` 的源被钳到错误位置（右源隔了一个单位）——很可能不是产品缺陷 |
| 目标集未收敛 | `JudicatorTargetSelectionTests` 2 条（期望 2 项，实际整条阵线） | 过滤器疑似失效 |
| 护卫判定变宽 | `CommandRulesTests` 3 条 | `Guard_Hq_Requires_Adjacent_Guardian_Not_Blocked_By_Gap` 由 False 变 True |
| 索引越界/空引用 | `KeywordProgramTests` 2、`CardServiceTests::Scenario6_Transform_Triggers...`、`KeywordEffectBatch3Tests::Blitz_Death...` | 疑似产品缺陷 |
| 其余值/集合差异 | `PlayChain*`、`SandboxPrimitiveTests`、`Trigger*`、`EffectRemount*` 等 | 逐条判定 |

## Requirement Items

### R1: 全量绿且归因完整
- Status: Confirmed
- Scenario/Trigger: 开发者改完一处，需要相信"测试全绿"意味着没有回归。
- Behavior: `dotnet test OrcEngine.sln` 在干净构建下失败数为 0；每条被修改的用例在提交说明或实现文档里标注归因类别（测试过期／产品缺陷／资产过期／门禁授权）。
- Acceptance:
  - [ ] `OrcEngine.sln` 全量失败 0、跳过 0（本机实测跳过基线为 0；若出现环境型 `Skip`（如 `ReparsePointFactAttribute` 无法创建重解析点），须在报告中单列并说明非本次工作引入，不得为凑"跳过 0"改写该属性）
  - [ ] 每条改写用例可追溯到一条归因
  - [ ] 无 `[Fact(Skip=...)]` 新增，无删除的 `[Fact]`
  - [ ] `Orc.Manager.Tests`（基线 0/20）保持全绿，作为护栏纳入验收

### R2: 位置模型口径统一
- Status: Confirmed
- Behavior: 所有"取位"路径统一走「当前快照的 `GetInsertionGaps()` 现取现用」；持有跨越变更的空隙引用＝过期，按既有语义拒绝。
- Acceptance:
  - [ ] 生产代码中不再出现"缓存 `FormationGap` 跨动作复用"的写法
  - [ ] 测试辅助（`CommandTestKit.PrepareOnSupportAsync` 等）在取空隙与使用之间不做会改变阵线的操作
  - [ ] 过期空隙的拒绝语义一致——**统一的是语义、不是形态**：过期必须是**独立可区分的原因**（`PositionStale`），不得伪装成"被占用"或"不在线内"；返回形态按各入口既有契约（`JoinUnitAsync`＝结果对象、`PlaceToSupportLineAsync`＝结果对象、沙盒＝`position-stale` 串），**「不在线内」仍是异常**（真契约错误）

### R3: 生产资产与引擎模型同步
- Status: Confirmed
- Behavior: 卡包/内置词条的 csx prefab 只引用现行 API；资产加载失败视为缺陷而非"环境问题"。
- Acceptance:
  - [ ] `keyword.repair`、`keyword.mobilize.accrual.csx` 可编译
  - [ ] 全部样例包的 prefab 在 `csx制品_*` 校验用例下通过
  - [ ] 词条效果实际生效（不只是"能编译"）

### R4: 门禁类变更显式授权
- Status: Confirmed
- Behavior: 契约基线、架构白名单的变更必须显式记录"为什么这是合法变更"，不得顺手改。
- Acceptance:
  - [ ] 契约基线经 `ORC_WRITE_BASELINE=1` 重生成，且 diff 内容＝已定稿功能新增的 API
  - [ ] 域标记白名单的每一处新增都有理由（该位置确实属于"资产管理"合法消费方）
  - [ ] 若某处违规无法辩解，则改代码而非加白名单

### R5: 样例包数据自洽
- Status: Confirmed
- Acceptance:
  - [ ] `card-pack-across-the-pacific` 每个源文件都有 sidecar，manifest 为 v2，两份 lock 一致
  - [ ] 内容指纹重算与 manifest 一致
  - [ ] 重建后该包仍能通过 `pool.assembled` 与开局校验

### R6: 不回归
- Status: Confirmed
- Acceptance:
  - [ ] 昨日修复的移动链三处（`MoveRevalidationJudicator`、`MatchCardService.ContainsSlot`、`SandboxPrimitives` 空隙判定）**判定逻辑**不变
  - 注：`ContainsSlot` 的判定（同线 && `IsCurrent`）不变，但**失败分类**按 R2 第三条改为「过期⇒结果对象 `PositionStale`，不在线内⇒异常」——这是本次明确授权的行为修正，不计作回归
  - [ ] `Orc.Server.Tests`／`Orc.Tests`／`Orc.Script.Tests`／`Orc.Ai.Tests` 保持全绿
  - [ ] 卡池注册注销（未提交改动）相关用例不退化

## R7: 落盘纪律（不侵占在途工作）
- Status: Confirmed
- Scenario/Trigger: 工作区有 45 项未提交改动（含「卡池运行期注册注销」在途批次、昨日移动链修复、样例包数据），本次要改的多个文件与它们重叠。
- Behavior:
  - **绝对禁止 `git add -A` / `git add .` / `git checkout --` / `git restore`**。只允许逐路径 `git add <path>`，且每次 add 前先 `git diff -- <path>` 核对内容确属本次工作。
  - 卡池批次文件（`src/Orc.Mcp/Pool/`、`src/Orc.Mcp/Tools/PoolTools.cs`、`src/Orc.Server/Admin/*`、`src/Orc.Server/ServerBootstrap.cs`、`src/Orc.Packages/Loading/MatchCardAssembly.cs`、`src/Orc.Packages/LoadListPlanner.cs`、`tools/orc/transport.py` 等）**只读不写、不提交**，除非本次修复必须改它（届时单独说明）。
  - 混合文件（同一文件既有在途改动又有本次改动，如 `MatchCardService.cs`）允许叠加，但提交说明必须分段写清哪部分属于哪批。
  - 提交按**语义单元**切分，保证任一单元可独立 `git revert`；首批固定 5 个（钳制／`PositionStale`／产品侧 csx+基线+白名单／数据协议／测试适配），**C 档若查出真缺陷，每个独立缺陷再单开一个 commit**（数量不设上限，不为凑"5 个"牺牲回退粒度）。**同一缺陷同时改产品与测试⇒合并为同一个 commit**（产品改了测试不改，中间态必红，违反"提交前已绿"）；**需求级歧义（原语义在新模型无对应物）不提交**，先上报裁决，裁决后单开并标注"需求级修正"。
  - **每个 commit 前该测试项目必须跑过**，不许提交未验证状态。
  - 未跟踪的 `scripts/`、`dist/`、`samples/card-pack-space-war.zip` 原样不动，不在本次清理范围内。
- Acceptance:
  - [ ] 全程无 `git add -A`
  - [ ] `git log` 能看出本次工作切分为若干可回退单元
  - [ ] 卡池批次文件在 `git diff HEAD` 里的内容与本轮开工前一致（即未被本次工作改动）
  - [ ] 每个 commit 对应的测试项目当时为绿

## 裁决结论（2026-10-23 用户确认）

1. **修复权限边界：允许同时改测试、生产代码、生产资产、门禁基线。** 判据＝「以 `docs/` 定稿需求与 `CONTEXT.md` 术语为真源」：产品行为偏离真源⇒改产品；测试断言偏离真源⇒改测试。**禁止**为了让测试变绿而放宽产品校验、弱化断言或新增 `Skip`。
2. **范围：三个项目一起清零。** `Orc.Game.Tests`（111）＋`Orc.Packages.Tests`（2，样例包 sidecar/lock 重建）＋`Orc.Mcp.Tests`（1），不做半套。
3. **契约基线：允许重新生成。** `EffectRuntime` 新增的 `JoinFrontlineAsync`/`JoinSupportLineAsync`/`PickHandCardsAsync`/`Join*Trigger` 认定为「手牌选靶与前线入场」批次的已定稿功能，经 `ORC_WRITE_BASELINE=1` 重生成基线；diff 必须逐条对得上这批 API，出现无关变动即视为夹带、须回退排查。
4. **域标记白名单：允许放行两处。** `src/Orc.Manager/AssetLibrary.cs`、`src/Orc.Server/Pool/PackPool.cs` 读 `AssetDomain` 认定为「资产管理/卡池对账」合法消费方，加入白名单并注明理由。除此之外新增的裸读一律按违规处理。
5. **C 档行为差异若查明是产品缺陷，本次一并修。** 不受"未提交的卡池改动"牵连限制，但改动须与卡池改动分区、可独立回退。

## 实施纪律（由裁决推出）
- 分档处理顺序：B 档（产品/资产真坏）→ A 档（测试过期）→ C 档（逐条判定）。先修好产品或资产，避免把测试改成"适配错误行为"。
- 每改完一档重跑对应测试项目，记录「前/后失败数」，写入 `PROGRESS.md`。
- 任何"C 档查明是产品缺陷"的案例，必须在归因表里登记「症状／真源依据／改的是哪一侧」。

