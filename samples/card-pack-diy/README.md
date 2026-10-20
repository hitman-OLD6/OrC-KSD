# DIY 示例卡包（数据侧）

> 纯数据分发：`cards/*.card.json` ＋ `effects/*.prefab.json`。四个效果全部是**效果库引用**（卡上 `effects.prefabs`），
> 不含内联快照；行为由 csx 承载，装载在 `Orc.Script` 求值器中编译执行。
> 用途：示例 1/2/3/5 的**数据侧证据**。代码侧示例 4/6/7/8 见 [`examples/README.md`](../../docs/卡牌DIY开发文档/examples/README.md)。

## 0. 对应示例

| 卡 | 示例 | 路径可用性 | 证据 |
| --- | --- | --- | --- |
| 火力支援 | [示例 01 · 火力支援](../../docs/卡牌DIY开发文档/examples/01-火力支援.md) | 数据侧可用 | L1 + L2 |
| 齐射 | [示例 02 · 齐射](../../docs/卡牌DIY开发文档/examples/02-齐射.md) | 数据侧可用 | L1 + L2 |
| 纪奠 | [示例 03 · 纪奠](../../docs/卡牌DIY开发文档/examples/03-纪奠.md) | 数据侧可用（有近似） | L1 + L2 |
| 战地救护 | [示例 05 · 战地救护](../../docs/卡牌DIY开发文档/examples/05-战地救护.md) | 数据侧可用（依赖 `source`） | L1 + L2 |

## 1. 目录

```
samples/card-pack-diy/
├── README.md
├── manifest.json                      包清单 v2（id=diy / metadata[]；S10a 迁移补齐）
├── lock.formal.json / lock.test.json  两 lock（S10a 迁移生成）
├── cards/
│   ├── diy.fire-support.card.json     火力支援（指令，1 费）——示例 1
│   ├── diy.volley.card.json           齐射（指令，2 费）——示例 2
│   ├── diy.memorial.card.json         纪奠（指令，1 费）——示例 3
│   └── diy.field-rescue.card.json     战地救护（单位 1/2，1 费）——示例 5
└── effects/
    ├── diy.fire-support.effect.prefab.json
    ├── diy.volley.effect.prefab.json
    ├── diy.memorial.effect.prefab.json
    └── diy.field-rescue.effect.prefab.json
```

> 每个资产源文件同目录另有 `<源文件名>.meta.json` 资产元数据 sidecar（S10a 迁移生成，初始版本 `1.0.0`）。
> 本包 `manifest.json` 的 `id=diy` 由目录名去 `card-pack-` 前缀派生，`guid` 由 `id` 确定性派生。

## 2. 卡表

| 卡 id | 名称 | 类别 | 费用 | 卡面文本 | 触发点 | 动作面 | 示例 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `diy.fire-support` | 火力支援 | 指令 | 1 | 对一个敌方单位造成 3 点伤害；本方总部 +5 防御 | `card.played`（本卡） | `SelectAsync` + `DamageAsync` + `BuffAsync` | 01 |
| `diy.volley` | 齐射 | 指令 | 2 | 将 3 点伤害随机分配给场上单位（可重复命中） | `card.played`（本卡） | `SelectAsync("random")` ×3 + `DamageAsync` | 02 |
| `diy.memorial` | 纪奠 | 指令 | 1 | 本方总部回复等同于本局已被摧毁单位数的生命 | `card.played` ＋ `card.died`（计数） | `BuffAsync`（HQ 生命） | 03 |
| `diy.field-rescue` | 战地救护 | 单位 | 1 | 友方因敌方弃牌时，从卡组抽取此牌 | `card.discarded`（读 `Source`） | `FetchFromDeckAsync(self)` | 05 |

> 「纪奠」的计数为**对局级共享键**（`EngineVariables["diy.example3.destroyed"]`），自该卡效果装载起算；
> 多张同名卡共享计数（数据侧近似）。代码侧版本可用效果实例字段（见 07 章「词条程序 vs csx」的对照思路）。

## 3. 预检（必须双零）

```bash
dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --filter "FullyQualifiedName~DiyCardPack"
```

等价的 API 调用：

```csharp
var report = CardDistributionValidator.Validate(
    new[] { "samples/card-pack-diy/cards" },
    new[] { "samples/card-pack-diy/effects" });
```

最近一次实测（2026-10-19）：

```
SuccessfulCardCount=4
SuccessfulLibrarySnapshotCount=4
SuccessfulInlineSnapshotCount=0
UnresolvedReferenceCount=0
ErrorCount=0
WarningCount=0
HasNoErrors=true

Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1
```

## 4. 装配方式（与示例证据一致）

```csharp
// ① 读卡（纯数据）
var load = CardDataLoader.LoadDirectory("samples/card-pack-diy/cards");

// ② 声明每卡的效果 id（注册先于卡加载）
var registry = new CardEffectRegistry();
foreach (var (cardId, prefabIds) in load.EffectDeclarations)
{
    registry.DeclarePrefab(cardId, prefabIds);
}

// ③ 装载效果库（注册进 PrefabManager）
match.Engine.Prefabs.LoadDirectory("samples/card-pack-diy/effects");

// ④ 建局：Match(..., effectRegistry: registry, ...) → Initialize()
```

## 5. 边界

- 效果行为依赖 `Orc.Script` 求值器；未装配求值器＝`evaluator-missing`（结构化失败，不影响预检）。
- 预检只校验**结构**（schema/引用/内联），不编译 csx；行为证据见 `DiyExample01/02/03/05`。
- 「纪奠」计数口径与「自装载起算」的限制见 §2 注。
