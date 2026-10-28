# 示例索引（`examples/`）

> 九个可复制示例：每例一文件、六段结构（卡面 / 选型 / 完整数据或代码 / 时序 / 坑 / 证据）。
> 机制原理不在这里重复——每例第 2 段给判据，细节链到 `00`–`08` 章。
> 新增示例照 [`_template/示例模板.md`](../_template/示例模板.md) 复制，并在本表与 [总 README](../README.md) §5.4 登记。

> **⚠ 代码侧示例的编译状态（2026-10-28 核对）**：九个示例的**卡面文本、选型判据、时序与坑**全部有效；
> 但 **03 / 04 / 06 / 09** 的"完整代码/追踪"段引用了已删除的类型（`PassiveEffect`、`CardLoadout.MountEffect`、
> `ApplyFrameworkInjects`、`Effect.InjectByFramework`、旧 `band: "Rewrite"`），**照抄不能编译**。
> 各文顶部已加定制警示。代码侧现行写法见 [01 章 §6](../01-效果原理.md)；纯数据侧示例（01/02/05/07/08）不受影响。

## 1. 怎么读

| 你想干什么 | 从哪一例开始 |
| --- | --- |
| 第一次写一张纯 JSON 的指令 | [01 火力支援](./01-火力支援.md)（最小闭环：选靶 + 伤害 + 修饰） |
| 想让伤害「逐点分配」 | [02 齐射](./02-齐射.md) |
| 想按「全局已发生的事」算数值 | [03 纪奠](./03-纪奠.md)（读世界状态，不是读信号） |
| 想做「每有一个 X 就 +Y」 | [04 轰炸机压制](./04-轰炸机压制.md)（光环现算，数据侧只能静态近似） |
| 想按「谁导致的」做归因 | [05 战地救护](./05-战地救护.md)（`view.Source`，R9） |
| 想让指令可被反制 | [06 反制第二张指令](./06-反制第二张指令.md)（`OrderFlow`；G-01 已解决 P4） |
| 想禁止某单位被指令指向 | [07 指令免疫](./07-指令免疫.md)（判定器两条路径） |
| 想做「战斗 + 分支位移」的复合流程 | [08 突袭推进](./08-突袭推进.md)（`BattleAsync`，R10） |
| 想做「相邻单位持续降费 + 减伤」 | [09 相邻掩护](./09-相邻掩护.md)（光环谓词 + 三个通用判定器 + 伤害处理端注入） |

## 2. 证据等级

| 等级 | 含义 | 判定 |
| --- | --- | --- |
| **L1** | 数据可加载、预检双零 | `CardDistributionValidator.Validate` → `ErrorCount=0 / WarningCount=0` |
| **L2** | 行为可复跑 | `dotnet test` 对应用例通过（加载 → 触发 → 世界状态变化有断言） |

本目录九例均达 **L2**；数据侧五例（01/02/03/05/09）另达 **L1**（进 `samples/card-pack-diy/`）。

## 3. 总表

| # | 示例 | 需求 | 路径可用性 | 证据 | 证据用例（类.方法） | 实体 |
| --- | --- | --- | --- | --- | --- | --- |
| 01 | [火力支援](./01-火力支援.md) | R6a | **数据侧可用** | L2 + L1 | `DiyExample01_DamageAndArmorTests.火力支援_打出_单目标3伤害且本方总部加5防` | `samples/card-pack-diy/cards/diy.fire-support.card.json`<br>`samples/card-pack-diy/effects/diy.fire-support.effect.prefab.json` |
| 02 | [齐射](./02-齐射.md) | R6b | **数据侧可用** | L2 + L1 | `DiyExample02_RandomSplitTests.齐射_打出_总伤害恒为3且逐点分配`<br>`DiyExample02_RandomSplitTests.齐射_无单位时_零伤害且不抛` | `samples/card-pack-diy/cards/diy.volley.card.json`<br>`samples/card-pack-diy/effects/diy.volley.effect.prefab.json` |
| 03 | [纪奠](./03-纪奠.md) | R6c | **数据侧可用（有近似）** | L2 + L1 | `DiyExample03_DestroyedCountTests.纪奠_打出_按本局被摧毁单位数回复总部`<br>`DiyExample03_DestroyedCountTests.纪奠_零阵亡_不回复且不抛` | `samples/card-pack-diy/cards/diy.memorial.card.json`<br>`samples/card-pack-diy/effects/diy.memorial.effect.prefab.json` |
| 04 | [轰炸机压制](./04-轰炸机压制.md) | R6d | **仅代码侧可用**（数据侧只能静态近似） | L2 | `DiyExample04_BomberAuraTests.轰炸机计数光环_随敌方轰炸机入场与阵亡随动` | `tests/Orc.Game.Tests/DiyExample04_BomberAuraTests.cs` |
| 05 | [战地救护](./05-战地救护.md) | R6e（依赖 R9） | **数据侧可用**（依赖调用方传 `source`） | L2 + L1 | `DiyExample05_DiscardFetchTests.战地救护_敌方弃置友方手牌_从卡组抽取自身`<br>`DiyExample05_DiscardFetchTests.战地救护_己方弃置_不触发` | `samples/card-pack-diy/cards/diy.field-rescue.card.json`<br>`samples/card-pack-diy/effects/diy.field-rescue.effect.prefab.json` |
| 06 | [反制第二张指令](./06-反制第二张指令.md) | R6f | **仅代码侧可用** | L2 | `DiyExample06_CounterSecondOrderTests.第二张指令_被反制_首张正常结算` | `tests/Orc.Game.Tests/DiyExample06_CounterSecondOrderTests.cs` |
| 07 | [指令免疫](./07-指令免疫.md) | R6g | **仅代码侧可用 / 混合** | L2 | `DiyExample07_TargetingForbidTests.代码侧声明_被保护单位从敌方指令候选中剔除`<br>`DiyExample07_TargetingForbidTests.词条程序路径_被保护单位从敌方指令候选中剔除` | `tests/Orc.Game.Tests/DiyExample07_TargetingForbidTests.cs` |
| 08 | [突袭推进](./08-突袭推进.md) | R6h | **仅代码侧可用** | L2 | `DiyExample08_AssaultAdvanceRetreatTests.突袭_BattleAsync_存活且前线无敌人_推进并打总部3`<br>`DiyExample08_AssaultAdvanceRetreatTests.突袭_BattleAsync_存活但前线有敌人_撤退到己方支援线`<br>`DiyExample08_AssaultAdvanceRetreatTests.突袭_BattleAsync_阵亡_不推进不撤退不打总部` | `tests/Orc.Game.Tests/DiyExample08_AssaultAdvanceRetreatTests.cs` |
| 09 | [相邻掩护](./09-相邻掩护.md) | 补篇（相邻光环 + 减伤） | **数据侧可用** | L2 + L1 | `DiyExample09_AdjacentCoverTests.交叉掩护_相邻友方步兵_行动费减一且受伤减一`<br>`DiyExample09_AdjacentCoverTests.交叉掩护_相邻但非步兵_不生效`<br>`DiyExample09_AdjacentCoverTests.交叉掩护_两个来源夹住_叠加为行动费减二与受伤减二` | `samples/card-pack-diy/cards/diy.adjacent-cover.card.json`<br>`samples/card-pack-diy/effects/diy.adjacent-cover.aura.prefab.json`<br>`samples/card-pack-diy/effects/diy.adjacent-cover.mitigation.prefab.json` |

> 数据侧五例的完整卡包（含预检命令与实测输出）见 [`samples/card-pack-diy/README.md`](../../../samples/card-pack-diy/README.md)。

## 4. 一键复跑

```bash
# 九个示例 + 卡包预检（共 19 条）
dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --filter "FullyQualifiedName~Diy"

# 单例
dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --filter "FullyQualifiedName~DiyExample01"
```

实测输出（2026-10-20）：

```text
Passed!  - Failed: 0, Passed: 19, Skipped: 0, Total: 19
```

## 5. 能力边界（不达标的四例，别当成数据侧能力）

| 示例 | 边界 | 为什么 | 替代 / 取舍 | 缺口 |
| --- | --- | --- | --- | --- |
| 04 | 数据侧只能 `DeclareAuraAsync(delta: 常量)` | 静态 delta 表达不了「每有一个」 | 自定义 `AuraDeclaration`，`transform` 里现数（本示例） | G-11 |
| 06 | 反制效果仍**只能代码侧**实现（G-01 已解决，P4） | 数据侧没有「注册 Counter 段 handler」的声明面；原生指令卡本身已可被反制 | 反制卡走 `CounterCard.AddEffectHandler`；命中默认 `ctx.Stop()`（指令照常扣费离手），需要「牌留手、不扣费」时用 `ctx.Interrupt()` | G-01（已解决） |
| 07 | 必须有一个代码侧注册动作 | 数据侧不能声明判定器，也不能改候选域 | A：`TargetingDeclaration`；B：词条程序 `targeting.forbid` + 一行注册 | G-05 |
| 08 | 撤退仍无动作面 | 战斗已由 `BattleAsync` 收口（R10）；撤退只有 `CommandManager.MoveUnitAsync(unit, slot)`（S6 起免费） | 战斗直接 `BattleAsync`；撤退先找己方支援线空槽再移 | G-02（战斗半条已关闭） |
| 09 | 减伤写的是**伤害量**，不改防御上限 | 修饰链只有 `OperateCost/Attack/Defense/DeployCost/HqHealth`，没有“受伤”维度 | 在 `damage.apply` 的 `Rewrite` 段改 `Amount`（每次结算各减） | 无（本批已补齐管线注入面） |

其余边界（`Source` 只 6 条信号非空、事件 `band` 只接受 `default`/`disposing`、`Subscribe` 拿不到 `TriggerCard`）见 [08 章](../08-能力边界.md) 与 [缺口表](../reference/缺口表.md)。

## 6. 示例与章节的对应

| 示例 | 主要机制 | 先读 |
| --- | --- | --- |
| 01 / 02 / 03 / 05 | 预制体 + csx + `CardEventView` | [01 效果原理](../01-效果原理.md)、[05 动作面](../05-动作面与运行时API.md) |
| 04 | 光环（Aura） | [05 动作面](../05-动作面与运行时API.md) §4 |
| 06 | `OrderFlow` + 反制 band | [03 判定器与 wrapper](../03-判定器与wrapper.md) |
| 07 | 判定器包装 / 词条程序 | [03 判定器与 wrapper](../03-判定器与wrapper.md)、[07 词条](../07-词条与预制效果.md) |
| 08 | 流程编排 + 位移动作 | [05 动作面](../05-动作面与运行时API.md)、[08 能力边界](../08-能力边界.md) |
| 09 | 光环谓词 + 判定器组合 + 伤害处理端注入 | [01 效果原理](../01-效果原理.md) §光环、[03 判定器与 wrapper](../03-判定器与wrapper.md)、[05 动作面](../05-动作面与运行时API.md) |
