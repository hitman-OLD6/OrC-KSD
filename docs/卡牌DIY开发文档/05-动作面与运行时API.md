# 05 动作面与运行时 API

> **本章解决什么**：把「handler 里能调什么」列全——`EffectRuntime` 全动作、`source` 透传规则、修饰与光环的分工、
> `EngineVariables` 的跨卡共享用法、失败语义。读完你不用翻源码就能选对方法。
> **前置**：00（心智模型）、01（效果原理）、02（成员与 handler）。
> **读完你能做什么**：①按动作表选对方法并正确处理返回值/异常；②知道哪些动作会写下游 `Source`、哪些还不会；
> ③用 `BuffAsync`/`DeclareAuraAsync` 分别表达一次修饰与持续加成；④用 `EngineVariables` 做跨卡计数。
> **对应代码路径（2026-10-29 核对）**：`Orc.Game.Effects.EffectRuntime`（全动作）、`EffectRuntime.Board.cs`（加入战场／手牌选靶）、
> `Orc.Core.EngineVariables`（`LogicEngine.Variables`）、`Orc.Game.Managers.PlayerManager.DiscardCardAsync`、
> `Orc.Game.Cards.ProgramKeywordComponent`（词条操作）。
> **本章自检**：能说出 `DamageAsync`/`KillAsync` 的 `source` 在 `ct` **前**；能说出哪些动作没有 `source` 尾参；
> 能解释 `BuffAsync` 与 `DeclareAuraAsync` 的区别；能写出一个 `EngineVariables` 计数闭环。

## 状态

- 章节状态：**完成**
- 最近一次证据运行：2026-10-29（全表与 [参考层 · api速查](./reference/api速查.md) §1 同源；方法签名由 `src/Orc.Game/Effects/EffectRuntime*.cs` 直接核对）

---

## 1. 动作面是什么

handler 不直接改字段。**动作面**＝受控状态变更入口的白名单：

1. **`EffectRuntime`**：游戏层唯一门面（伤害/消灭/修饰/词条/抽牌/选靶/资源/光环/加入战场…）；
2. **词条程序的 32 个操作**：JSON 声明、`ProgramKeywordComponent` 解释执行（见 [api速查](./reference/api速查.md) §4）；
3. **领域服务**：`CommandManager`（战斗/移动）、`PlayerManager`（弃置/回迁）等——多数 `internal`，作者侧应经 `EffectRuntime`。

为什么必须走白名单：数值变化要经过修饰链才会发 `card.stat.changed`（守护/归零检查依赖它）；绕过白名单会让其它机制「看不见」你的修改。

## 2. `EffectRuntime` 全动作表

> 解析：`EffectRuntime.ResolveFor(card)` → 未装配/脱局返回 `null`（调用方按「功能不可用」处理）。
> `ct`＝`CancellationToken`；`source`＝可选施动卡；`层归属` 均为**游戏层**；`数据侧` 指 csx 能否调用（全部为「是」）。

### 2.1 核心动作（26）

| 方法签名 | 失败语义 | `source` 位置 |
| --- | --- | --- |
| `Task<bool> KillAsync(Card target, Card? source = null, ct = default)` | target null＝`ArgumentNullException`；非单位/不可达＝`false` | **在 ct 前** |
| `Task<bool> DamageAsync(Card target, int amount, Card? source = null, ct = default)` | target null＝异常；非单位/HQ＝`false`；服务不可达＝`false` | **在 ct 前** |
| `Task<bool> BattleAsync(Card first, Card second, Card? source = null, ct = default)` | 任一方非**在场存活单位**／同一单位／服务不可达＝`false`（零副作用）；`source` 仅审计 | **在 ct 前** |
| `Task<bool> BuffAsync(Card target, int attack, int defense, EffectDuration duration = Permanent, ct = default, Card? source = null)` | target null＝异常；HQ 且 attack≠0／非卡／无变化＝`false` | 有 |
| `Task<bool> GrantAsync(Card target, string keyword, ct = default, Card? source = null)` | target null／keyword 空白＝异常；无词条面／已存在＝`false` | 有 |
| `Task<bool> GrantAsync(Card target, string keyword, int? value, ct = default, Card? source = null)` | 同上；`null`＝未提供参值（≠0） | 有 |
| `Task<bool> RevokeAsync(Card target, string keyword, ct = default, Card? source = null)` | 不存在＝`false`；已死亡卡＝异常透传 | 有 |
| `Task<bool> SetKeywordValueAsync(Card target, string keyword, int? value, ct = default, Card? source = null)` | 词条不存在＝异常透传；不可达＝`false` | 有 |
| `Task<int> DrawAsync(Card viewer, int count, ct = default, Card? source = null)` | viewer null＝异常；count≤0／不可达＝`0` | 有 |
| `Task<DeckFetchResult> FetchFromDeckAsync(Card target, ct = default, Card? source = null)` | target null＝异常；结果三态（`Completed`/`Burned`/`Rejected`） | 有 |
| `Task<bool> CostModAsync(Card target, int delta, EffectDuration duration = Permanent, ct = default)` | target null＝异常；非卡／delta=0＝`false` | **无** |
| `Task<bool> DeployCostModAsync(Card target, int delta, ct = default)` | 同上 | **无** |
| `Task<int> AddToHandAsync(Card viewer, string cardName, int count, ct = default, Card? source = null)` | 参数空＝异常；未命中／不可达＝`0` | 有 |
| `Task<int> ShuffleInAsync(Card viewer, string cardName, int count, ct = default, Card? source = null)` | 同上；成功发 `deck.shuffled` | 有 |
| `Task<bool> PinAsync(Card target, ct = default, Card? source = null)` | 非单位／拒绝＝`false` | 有 |
| `Task<bool> SilenceAsync(Card target, ct = default, Card? source = null)` | 非单位＝`false` | 有 |
| `Task<VeteranPromotionResult> UpgradeAsync(Card target, ct = default, Card? source = null)` | 非单位＝`Rejected(NotOnField)`；执行失败＝回滚＋异常上抛 | 有 |
| `Task<RevealOutcome> RevealAsync(Card target, ct = default, Card? source = null)` | 非单位／已揭示＝`NoOp` | 有 |
| `Task<bool> MoveAsync(Card target, string zone, ct = default, Card? source = null)` | zone 空白＝异常；非单位／不可用＝`false` | 有 |
| `Task<bool> GainPointSlotsAsync(Card viewer, int amount, ct = default, Card? source = null)` | amount≤0／不可达＝`false` | 有 |
| `Task<bool> LosePointSlotsAsync(Card viewer, int amount, ct = default, Card? source = null)` | 同上 | 有 |
| `Task<bool> GainPointsAsync(Card viewer, int amount, ct = default, Card? source = null)` | 同上；实际增加才发 `point.gained`→`point.changed` | 有 |
| `Task<bool> LosePointsAsync(Card viewer, int amount, ct = default, Card? source = null)` | 同上 | 有 |
| `Task<IReadOnlyList<Card>> SelectAsync(Card viewer, EffectSelector selector, ct = default)` | 参数 null＝异常；判定器不可用＝空集 | — |
| `Task<bool> DeclareAuraAsync(Card host, string field, int delta, EffectAuraFilter filter, ct = default)` | delta=0／字段非法／环境不可达＝`false` | **无** |
| `Task<bool> RerunAurasAsync(Card host, ct = default)` | 无光环／不可达＝`false` | **无** |

### 2.2 场地动作（3）

| 方法签名 | 说明 |
| --- | --- |
| `Task<ActionResult?> JoinFrontlineAsync(Card host, UnitCard unit, int? index = null, ct = default)` | 把单位加入**前线**（不扣费、不走部署词条；发 `unit.joined`）。前线存在存活敌方单位／满员＝失败（不抛） |
| `Task<ActionResult?> JoinSupportLineAsync(Card host, UnitCard unit, int? index = null, ct = default)` | 加入**支援阵线**（落位＝显式序号 ＞ 守护〔HQ 右侧〕 ＞ 默认最左） |
| `Task<HandCardPick> PickHandCardsAsync(Card viewer, int min, int max, ct = default)` | 从手牌选 `min..max` 张；`min` 大于手牌数＝`Cancelled`（零交互）；纯选择，不附带动作 |

### 2.3 查询与判定（5）

| 方法签名 | 说明 |
| --- | --- |
| `static EffectRuntime? ResolveFor(Card card)` | 卡 → 运行时；未装配／脱局＝`null` |
| `bool Judge(string name, params object[] args)` | **问一条判定规则**（如 `Judge(JudicatorNames.UnitAdjacent, a, b)`）。名字见 03 章 §2 |
| `static bool EvaluateCondition(Card? viewer, string spec)` | 求值一个条件表达式串（词条程序与 csx 共用同一套口径） |
| `IReadOnlyList<Card> OwnerUnits(CardBase host)` | 宿主归属玩家的全部单位（现收集） |
| `AuraRegistry? AurasOf(Card host)` | 宿主已声明的光环登记表（诊断/改写用） |

### 2.4 成员地址属性（2）

| 属性 | 值 | 用途 |
| --- | --- | --- |
| `JoinFrontlineTrigger` | `engine#join.frontline` 的 `MemberAddress`（未装配＝null） | 想挂到「加入前线」触发端时用它，别写裸字符串 |
| `JoinSupportLineTrigger` | `engine#join.support` 的 `MemberAddress` | 同上 |

> 同一张表（含列定义与数据侧可达标记）见 [参考层 · api速查](./reference/api速查.md) §1。两处不一致时以代码为准，先改 api速查 再改本章。
> **伤害类动作不是直扣**：`DamageAsync` 走 `effect-damage`、`BattleAsync` 走 `battle-damage`、指挥对战走 `combat-damage`，
> 三者都由 `damage-apply` 统一扣数值（见 §7.1）。

## 3. `source` 规则

**只做两件事**：

1. 写入下游信号的 `Source` 载荷（如 `card.discarded`、`unit.damage.dealt` 的发射）；
2. 作为下游动作的显式入参（伤害来源、击杀者、弃置施动方）。

**三个不做**：

- 不猜测：不拿 `target`/`viewer` 当 source；
- 不回退：`null` 就是 `null`（无引发者）；
- 不加前置条件：`source` 永不导致动作失败。

```csharp
// 效果引发弃置：敌方这张卡的弃置由 host 造成
await players.DiscardCardAsync(targetPlayer, card, ct, source: host);
// 下游：card.discarded 载荷 Source = host，监听方 view.Source 读到 host
```

**现状（重要）**：`source` 尾参已就位，但**多数动作的下游服务还没接线**——目前真正会写非空 `Source` 的信号只有：

| 信号 | 谁写 |
| --- | --- |
| `card.played` | 指令施放链 `Resolve` 段 / 部署链宣告（卡自身注册 ⇒ 被打出卡）；被反制时不发 |
| `unit.joined` / `unit.deployed` | 加入/部署链（卡自身注册） |
| `counter.triggered` | 使用反制（卡自身注册） |
| `card.died` | `ProcessDeathAsync(..., killer)` → `source: killer` |
| `card.discarded` | `DiscardCardAsync(..., source)`（调用方传才非空） |

其余信号（如 `card.drawn`、`unit.position.changed`、资源类信号）目前 `Source` 恒为 `null`。
归因时优先读载荷里已有的对象键（例：`unit.damage.dealt.Unit`＝施动方），或转代码侧直接调带 `source` 尾参的发射助手。
逐信号现状见 [信号表](./reference/信号表.md)。

## 4. 修饰器 vs 光环：`BuffAsync` 与 `DeclareAuraAsync`

| 维度 | `BuffAsync` | `DeclareAuraAsync` |
| --- | --- | --- |
| 语义 | **一次性**加/减攻防（可带期限） | **持续**加成：对谓词筛出的受益者生效 |
| 目标 | 显式 `target` | `EffectAuraFilter` 现收集 |
| 期限 | `EffectDuration.Permanent/TurnEnd/NextOwnerTurnStart` | 随效果存续（效果销毁时撤销） |
| 集合变化 | 不随动 | 受益方进场/离场自动随动（重跑时重收集） |
| 重算 | — | `RerunAurasAsync(host)` 手动触发一次重跑 |
| 典型 | 「本回合 +2 攻击」 | 「所有友方步兵 +1 攻击」 |
| 失败 | 无变化＝`false` | delta=0/字段非法/环境不可达＝`false` |

```csharp
var runtime = EffectRuntime.ResolveFor(host);
if (runtime is null) return;

// 一次性修饰：本回合结束到期
await runtime.BuffAsync(target, attack: 2, defense: 0, duration: EffectDuration.TurnEnd, ct: ct, source: host);

// 持续加成：所有友方步兵（不含自己）+1 攻击
await runtime.DeclareAuraAsync(
    host,
    field: "attack",
    delta: 1,
    filter: new EffectAuraFilter(Side: "friendly", UnitType: "infantry", ExcludeSelf: true),
    ct: ct);
```

`field` 合法值：`attack` / `defense` / `opCost` / `deployCost`（其余＝`false`）。

> **动态计数**（「每有一个敌方轰炸机 +1」）不是「一个固定 delta 的光环」能直接表达的：
> 要么在 `unit.deployed`/`card.died` 时重建修饰（`BuffAsync` + 记录），要么为每个计数源登记一条光环并逐条撤销。
> 选择前先读 [缺口表](./reference/缺口表.md) 的计数相关条目。

## 5. `EngineVariables`：跨卡共享计数

`LogicEngine.Variables`（`EngineVariables`）是**对局级** `key → object` 数据袋：一张卡写、另一张卡读；不进快照、不发信号、不参与回滚。

| API | 语义 | 失败 |
| --- | --- | --- |
| `Set(string key, object value)` | 写入（同 key 同类型＝覆盖） | key 空白＝`ArgumentException`；value null＝`ArgumentNullException`；同 key **不同类型**＝`InvalidOperationException` |
| `TryGet<T>(string key, out T value)` | 读（缺失/类型不符＝false） | 不抛 |
| `Get<T>(string key)` | 读（强校验） | 缺失＝`KeyNotFoundException`；类型不符＝`InvalidCastException` |
| `GetOrCreate<T>(string key)` | 读或创建（仅引用类型） | 已存在但类型不符＝`InvalidCastException` |
| `Contains(key)` / `Keys` / `Count` | 诊断读面 | — |

```csharp
// 写：某处累计「本局被摧毁的单位数」
var variables = ctx.Engine!.Variables;
variables.TryGet<int>("diy.destroyed.units", out var current);
variables.Set("diy.destroyed.units", current + 1);

// 读：另一张卡按计数结算
if (ctx.Engine!.Variables.TryGet<int>("diy.destroyed.units", out var total) && total > 0)
{
    await runtime.DamageAsync(target, total, host, ct);
}
```

约定：key 用命名空间前缀（`diy.`）避免冲突；**不要存可变实例再原地改**（同 key 同类型是「覆盖」语义，共享实例会绕过写入检查）；类型一旦确定不要换（换类型会被拒绝）。

## 6. 失败语义总则

| 类别 | 表现 | 例子 |
| --- | --- | --- |
| 参数层错误 | 抛 `ArgumentNullException` / `ArgumentException` / `ArgumentOutOfRangeException` | `target == null`、`keyword` 空白 |
| 不可用/不适用 | 返回 `false` / `0` / 空集（**不抛**） | 服务不可达（脱局）、非单位卡用 `SilenceAsync` |
| 结果化 | 返回结果对象（三态/四态） | `DeckFetchResult`、`VeteranPromotionResult`、`RevealOutcome`、`HandCardPick`、`ActionResult?` |
| 状态非法 | 异常透传（不吞） | 对已死亡卡 `RevokeAsync`、词条不存在 `SetKeywordValueAsync` |
| 下游信号 | 有变化才发；`Source` 恒写（可空） | `card.stat.changed` 无变化＝零发射 |

**调用方要做的**：先判返回值/结果，再决定是否继续；不要依赖「抛异常」表达业务失败。

## 7. 选型速查

| 我要 | 用 |
| --- | --- |
| 对单位/HQ 造成伤害 | `DamageAsync(target, amount, source, ct)` |
| 直接消灭（走死亡链、带击杀者） | `KillAsync(target, source, ct)` |
| 本回合/永久加攻防 | `BuffAsync(..., duration, ct, source)` |
| 持续加成（随受益集合） | `DeclareAuraAsync(host, field, delta, filter, ct)`；改完手动重算 `RerunAurasAsync(host)` |
| 授予/撤销词条（带参值） | `GrantAsync` / `RevokeAsync` / `SetKeywordValueAsync` |
| 抽牌 / 定向取卡 | `DrawAsync` / `FetchFromDeckAsync` |
| 加/减点数、槽 | `GainPointsAsync` / `LosePointsAsync` / `GainPointSlotsAsync` / `LosePointSlotsAsync` |
| 压制/抑制/升级/揭示 | `PinAsync` / `SilenceAsync` / `UpgradeAsync` / `RevealAsync` |
| 选目标（不需要玩家点） | `SelectAsync(viewer, new EffectSelector("all", ...))` / `SelectAsync(..., "self")` |
| 选目标（要玩家点） | `SelectAsync(viewer, new EffectSelector("one", ...))`（需前端）／`PickHandCardsAsync` 选手牌 |
| 把单位塞进战场 | `JoinFrontlineAsync` / `JoinSupportLineAsync` |
| 问一条判定规则 | `Judge("unit.adjacent", a, b)` 等（03 章 §2 的 28 个名字） |
| 跨卡计数 | `LogicEngine.Variables`（`EngineVariables`） |
| 战斗 / 撤退 | `BattleAsync(first, second, source, ct)` / `MoveAsync(target, zone, ct, source)` |
| 改写伤害（免疫/减伤/倍伤） | `wraps[]` 挂 `engine#damage-apply`（全局）或 `engine#combat-damage`／`effect-damage`／`battle-damage`（按来源） |

## 7.1 伤害四端（改写挂哪一层）

`DamageAsync` / `BattleAsync` / 指挥对战都**不直接扣血**，而是走两条：

```
对战：unit.attack 流程 ─→ combat-damage ─┐
效果：DamageAsync     ─→ effect-damage ─┼─→ damage-apply ─→ 数值门户（单位防御 / HQ 生命）
战斗：BattleAsync     ─→ battle-damage ─┘
```

| 成员地址 | 视图字段 | 典型改写 |
| --- | --- | --- |
| `engine#combat-damage` | `Source`/`Target`/`Amount`(可写)/`Kind` | 重甲（只减对战） |
| `engine#effect-damage` | 同上 | 「受到效果伤害 +1」类 |
| `engine#battle-damage` | 同上 | 「战斗伤害翻倍」类 |
| `engine#damage-apply` | 同上 ＋ `IsZeroed`(可写) | 免疫（全伤害归零）、通用减伤 |

**数据侧注册**（推荐；撤销随组件托管）——`wraps[]` 指向该成员，`phase: before`：

```jsonc
"wraps": [
  { "target": "engine#damage-apply", "phase": "before", "handler": "diy.adjacent-cover.mitigation#onDamageApply" }
]
```

完整样例见 [示例 09](./examples/09-相邻掩护.md) 与 `samples/card-pack-diy/effects-v4/diy.adjacent-cover.mitigation.component.json`。

**代码侧注册**（`band` 决定这条边落在哪一段）：

```csharp
using Orc.Core.Model;
using Orc.Game.Model;

var wraps = engine.Model!.Wraps;

wraps.Register(
    WrapDeclaration.ForMember("engine#damage-apply", WrapperPhase.Before, "重甲·示例"),
    handlers.Register("重甲·示例", (view, ctx, ct) =>
    {
        // 只减「打在本卡身上」的那一向（数据面字段名＝damage-apply 的视图字段）
        dynamic v = view;
        if (!ReferenceEquals(v.Target, host))
        {
            return Task.CompletedTask;
        }

        v.Amount = Math.Max(0, v.Amount - 2);   // 触发端只改 Amount
        return Task.CompletedTask;
    }),
    new DeclarationOrder(0, 0, 0));
```

> - 段名一律取 `BandSchemes` 的常量（`Initiate`／`Commit`／`Rewrite`／`Veto`／`Apply`…），**不写裸字符串**；
>   账本见 `EngineProcesses.ActionEndPositions`（21 端逐条给出段序方案与自有边段名）。
> - **不要**在 handler 里调 `ApplyDefenseDamageAsync` / `ApplyDamageAsync`：两门户是 `internal`，
>   只有 `damage-apply` 的落地边能调（外部程序集编译期即被挡住）。

## 8. 边界

| 缺口 | 层级 | 替代 | 需改 |
| --- | --- | --- | --- |
| 无「撤退」专用入口 | 游戏层未接线 | `MoveAsync(target, zone, ct, source)`（效果移动免费） | 提升为动作面 |
| `source` 下沉不完整 | 部分可覆盖 | 读现有载荷键；或代码侧调用带 `source` 尾参的发射助手 | 下游服务接线（见 §3） |
| 数据侧改不了判定/流程 | 内核/游戏层 | 代码侧包装（见 03） | — |
| 数据侧改不了外来边的段名 | 资产 schema 无字段 | 用 `wraps[]`，或转代码侧 | — |

> **战斗入口已收口**：`BattleAsync` 提供「快照攻击力＋同时结算；无射程/守护/伏击改写/反击资格」。

完整清单与编号见 [参考层 · 缺口表](./reference/缺口表.md)。

## 9. 本章自检

- [ ] 能说出 `DamageAsync`/`KillAsync` 的 `source` 位置（`ct` 前）与其余动作的区别
- [ ] 能列出 4 个「没有 `source` 尾参」的动作（`CostModAsync`/`DeployCostModAsync`/`DeclareAuraAsync`/`RerunAurasAsync`）
- [ ] 能解释 `BuffAsync` 与 `DeclareAuraAsync` 的适用差异（一次性 vs 持续/集合随动）
- [ ] 能写出 `EngineVariables` 的写入+读取闭环，并说出三种失败
- [ ] 能说出「动作返回 false」与「动作抛异常」分别代表什么
- [ ] 能说出伤害四端的成员地址，以及为什么改写要挂 `wraps[]` 而不是 `mounts[]`
