# Implementation — 一次打出双结算（指令效果结算两次）

前端反馈：`deadly_duty`（极限使命：消灭 1 个友方单位 + 抽 2 张牌）一次打出**弹两次 fieldUnit 选靶**，最终手牌/血量都对不上。
前端已排除 UI 侧（声明去重无效、关掉桌面指令桥仍复现），并怀疑 OrderFlow 的 Resolve band「priority 0 的 card.played 与 priority 100 的施放结算各跑了一次同一个 csx 效果」。

## F 事实（读码 + 实测）

- **F1｜OrderFlow 不存在 band 级双执行**：阶段 1（`_trigger`）与阶段 2（`_finalizeTrigger`）是两个独立 `Trigger` 实例，注册表互不可见；`Trigger.InvokeAsync` **不按 band 过滤**，band 只进排序键（`SnapshotSorted` → `SortKey`）。因此「p0 与 p100 各被调用一次同一 handler」这条机制不成立。
- **F2｜打出链单点注册**：`PlayManager` 只在 `Match.Initialize` 里构造一次（`Match.cs:828`），且 `Initialize` 拒绝重复；`Bus.Mount` 对同一触发器实例二次挂载会抛异常（不是静默双订阅）。
- **F3｜单声明单实例实测为一次**：新用例 `DoubleResolveGuardTests.单声明_打出只结算一次`（指令卡 + passive `card.played` + csx `GainPointSlotsAsync(1)`）打出后点槽 +1；既有的「齐射」用例（3 点伤害）同样成立。
- **F4｜能造成双结算的两条真实路径都在「装配」，不在「流程」**：
  1. `PackageRegistrar.AppendTextEffects`（`src/Orc.Packages/PackageRegistrar.cs`）把 `texts.json` 编译出的效果**追加**到该卡已有的效果声明上（旧实现 `existing >= 0 → 拼接`）。卡数据体已写了 `effects` 组件、包里又有文本 ⇒ 同一张卡两个效果实例。
  2. `CardEffectLoader`（`src/Orc.Game/Cards/EffectSystem.cs`）按 `context.PrefabDeclarations` 逐条实例化并 `AddEffect`，对**重复 id** 与**不同 id 但同一份脚本**都不设护栏（`DeclarePrefab` 只拦同一 cardId 的重复声明）。
- **F5｜症状吻合**：一张卡持两个等价被动效果时，一次 `card.played` 两个 handler 各完整跑一遍 —— 选靶弹两次、抽牌/消灭双倍；两处日志各自“正常”，是这类 bug 极难归因的原因。

## D 决策

- **D1（声明侧，根因）**：卡数据体显式声明 `effects` ⇒ 作者意图优先，`texts.json` 的文本模板**不再叠加**。改成「已声明即跳过」，并去掉旧的“追加拼接”分支。
- **D2（装载侧，护栏）**：`CardEffectLoader` 加双层去重 —— ①同一 prefab id 只装首个；②不同 id 但**事件脚本指纹相同**（`EntryName + CsxSource` 排序拼接）只装首个。两者均写 Warning 级留痕（`effect/prefab/duplicate`），便于下次一眼看出是数据重复而非流程重入。
- **D3（明确不做）**：不做“动作级幂等”。同一张卡被发两次 `play.command` / `play.command_pre` 属调用方问题，引擎吞掉会掩盖真实错误；若要排这一项，看事件流里 `card.played` 的条数即可区分（2 条＝两次调用）。
- **D4（明确不改）**：不给 `CardBase.LoadAsync` 加“第二次静默跳过”。既有用例 `CardTagDataTests.Scenario3_Repeated_Load_Keeps_Same_Id_And_Judgement_Not_Distorted` 锁定“重复加载＝组件装配 fail-fast 抛 `InvalidOperationException`”，改动会破坏该契约；且抛点（TagData 重复）先于效果装载，正常卡不会因重复加载双装效果。

## S 验证

| 层 | 用例 | 断言 |
|---|---|---|
| 引擎 | `Orc.Game.Tests/DoubleResolveGuardTests.单声明_打出只结算一次` | 点槽 +1；卡上 `DynamicPassiveEffect` 恰 1 个 |
| 引擎 | `Orc.Game.Tests/DoubleResolveGuardTests.两条声明指向同一份脚本_只装首个_打出仍只结算一次` | 点槽 +1（护栏生效） |
| 包注册 | `Orc.Packages.Tests/PackageRegistrarTests.注册_卡已显式声明效果_文本模板不再叠加` | 声明仅 1 条、为手写 prefab；`InlinePrefabs` 为空 |
| 包注册 | `Orc.Packages.Tests/PackageRegistrarTests.注册_卡无显式效果_文本模板照常生成一份` | 对照组：纯文本卡照常生成 `pack.<pkg>.<id>.effect` |

- 杀伤力验证：临时把内容去重条件短路（`if (false && …)`）后，第 2 条引擎用例转红（点槽 +2），还原后转绿 —— 证明用例真的覆盖护栏，不是空跑。

## 遗留

- 若前端复现命令里的双结算来自**两次 `play.command` 调用**（`docs/acceptance/ENGINE_64D8DB2_RECOVERY_20261023.md` 不在本仓库，无法核对），本修复不覆盖该情形；判据：事件流里 `card.played` 的条数（1 条＝装配重复，已修；2 条＝调用双发）。
