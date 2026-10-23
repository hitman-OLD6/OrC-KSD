# 进度 — P 片（S4/S5/S6）

- 更新时间: 2026-10-23T19:36:00+08:00
- 工具调用计数: ~40
- 当前步骤: 收尾
- 总体状态: 已完成

## 已完成

### S4 csx 制品与引擎模型同步（4 条全绿）
- 根因确认：`UnitStateData.Position`（原 `BoardPosition`）已被 `UnitStateData.Line`（`BattleLine?`）取代，
  csx 编译失败 → 抢修/动员 csx 制品整条不可用
- 沙箱约束（关键）：`CSharpScriptSecurityOptions.DefaultBlockedKeywords` 含 `Environment`（Ordinal 子串匹配），
  故 csx 内**不能**出现 `GameEnvironment`（`RevealRules` 的存在理由注释即同源先例）
- 处置：
  1. 新增 csx 可达读口包装 `src/Orc.Game/EffectParsing/PositionRules.IsOnFrontLine(Card?)`
     （转发 `GameEnvironment.IsOnFrontLine`，单一真源、不重建位置语义；null/无环境＝false 降级）
  2. `samples/card-pack-bulwark/effects/keyword.repair.prefab.json`：`onMoved`／`onDeployed` 的
     「position.Name.StartsWith("前线")」→ `PositionRules.IsOnFrontLine(unit)`
  3. `src/Orc.Game/Cards/KeywordPrefabs/keyword.mobilize.accrual.csx.prefab.json`：
     未在场判据 `state.Position is null` → `!state.IsOnBoard`
- 行为证据（不止"能编译"）：
  - `抢修_移动到前线即失去词条`：移动到前线后 `Keywords.Has("抢修")==false`，且 `Line==Battlefield.FrontLine`
  - `抢修_己方回合开始_未移动未攻击则回满`：`DefenseLoss 2→0`（未移动未攻击才回满）
  - `csx动员_迁移形态预演_双效果累积与受伤失去`：+1/+1 累积、受伤撤销、失去后不再累积
  - 4 条本轮用例：失败 4 → 0（过滤跑 9/9 通过）

### S5 契约基线重生成（1 条转绿）
- `ORC_WRITE_BASELINE=1` 跑 `BaselineGeneratorTests` → 写入 `tests/Orc.Game.Tests/Baselines/contract-freeze.json`
- diff ＝ 头两行（commit/generatedAt）＋ **7 条 `EffectRuntime` 条目**，无任何其它面变动（`gameUpdates`/`gameEntryPoints`/`resultShapes`/`schemaDto`/`prefabJsonDtos` 零 diff）：
  - `JoinFrontlineAsync(Card, UnitCard, int?, CancellationToken) → Task<ActionResult>`
  - `JoinSupportLineAsync(Card, UnitCard, int?, CancellationToken) → Task<ActionResult>`
  - `PickHandCardsAsync(Card, int, int, CancellationToken) → Task<HandCardPick>`
  - `get_JoinFrontlineTrigger()` / `property JoinFrontlineTrigger`
  - `get_JoinSupportLineTrigger()` / `property JoinSupportLineTrigger`
- **基线事实 4 的偏差实测不成立（如实记录）**：新头 `2797057`（S3 提交），而 `JoinFrontlineAsync` 等
  API 早在提交 `5514e35`（棋盘位置模型重构批次）就已入库——`git grep JoinFrontlineAsync HEAD` 命中，
  故本次头指向的提交**确实包含**这批 API，不存在"头指向不含该 API 的提交"的偏差。
- `PositionStale` 相关零 diff（与基线事实 8 一致：`resultShapes` 只冻结结果类型属性形状，不收录失败原因枚举）

### S6 域标记白名单（1 条转绿）
- `tests/Orc.Game.Tests/AssetDomainTests.cs` 的 `allowedFiles` 新增两项，各带一行理由：
  - `src/Orc.Manager/AssetLibrary.cs` — 资产管理面（按域枚举/对账资产条目）
  - `src/Orc.Server/Pool/PackPool.cs` — 卡池对账面（按域核对卡池装配来源）
- 未放宽任何其它位置；`hits` 自检断言未改动（仍要求 `CardBase.cs`／`CardLibrary.cs` 命中）

### 回归核对
- `Orc.Game.Tests` 全量：169 失败 / 1185 通过 —— 失败清单里**无** `BulwarkKeywordTests`／`KeywordEffectCsxTests`／
  `AssetDomainTests`／`ContractFreezeTests`；失败文本中无 `PositionRules`／`keyword.repair`／`mobilize.accrual.csx`／`IsOnBoard` 字样
  （余红均为 S2 拆钳制后的 A/C 档：`TestGapListExtensions.At` 越界抛出为主，属 S9/S10 范围）
- 本片改动面的直接消费方：`PrefabRoundTripTests`／`CardDistributionValidatorTests`／`CardPackDemoTests` 全绿
- `Orc.Packages.Tests`：363/363 全绿（本片未引入退化）

## 进行中
（无）

## 待裁决
1. **样例包 `card-pack-across-the-pacific` 的 6 个 effect prefab 仍是 `UnitStateData.Position`**（同一根因、同一改法）：
   `atp.beachhead-supply`／`atp.rapid-landing`／`atp.lvt-4.landing`／`atp.amphibious-landing`／
   `atp.mass-landing`／`atp.lvt-1.landing`。当前无任何用例编译它们（`csx制品_*` 只校验内置三件），
   故既不在本片 6 条内，也不在本片文件范围（属 S7 数据批次）。
   **建议**：S7 重建该包时按本片改法同步（`st.Position is not null` → `st.IsOnBoard`），否则该包词条运行期仍不可用。
2. （已自决，备案）`BulwarkKeywordTests.抢修_移动到前线即失去词条:169` 原断言
   `Assert.Same(front, unit.GetData<UnitStateData>().Line)` 是槽位模型残留（拿空隙比阵线）；
   改为 `Assert.Same(match.Battlefield.FrontLine, …)`，原意「单位现在在前线」不变。该用例在本片清单内，
   且该文件不在他人改动集（`git status` 未列入在途清单）。

## 改动文件清单
- `src/Orc.Game/EffectParsing/PositionRules.cs`（新增 1 个 static 类，1 个方法；csx 可达位置读口）
- `samples/card-pack-bulwark/effects/keyword.repair.prefab.json`（2 个 csx 事件体）
- `src/Orc.Game/Cards/KeywordPrefabs/keyword.mobilize.accrual.csx.prefab.json`（1 个判据）
- `tests/Orc.Game.Tests/Baselines/contract-freeze.json`（重生成：头 2 行 + 7 条 API）
- `tests/Orc.Game.Tests/AssetDomainTests.cs`（白名单 +2 行，含理由注释）
- `tests/Orc.Game.Tests/BulwarkKeywordTests.cs`（1 行断言修正）
- `docs/测试失败清零/PROGRESS-P.md`（本文件）

## 下一步
- 交父 agent 统一提交（本片未做任何 git add／commit）
