# 铜墙铁壁系列（card-pack-bulwark）

独立卡包，提供三个拓展词条与三张示例卡：

| 词条 | 行为 | 行为制品 |
|---|---|---|
| 机动 | 本回合首次成功行动的攻击额度消耗减一（攻击额度 +1），随后失去「机动」 | `effects/keyword.mobility.prefab.json`（`injects` → 行动额度触发器） |
| 固守 | 不能执行指挥移动（含撤退）；可被效果位移；不可被抑制 | `effects/keyword.entrench.prefab.json`（`judicatorWrappers` → `move.leg.eligibility`） |
| 抢修 | 友方回合开始、上次己方回合开始以来未移动/未攻击 → 回满；位于前线即失去 | `effects/keyword.repair.prefab.json`（`turn.start` / 位置变更 / 部署） |

## 目录

```
card-pack-bulwark/
├── manifest.json        kind=cardpack / id=orc.bulwark / version=1.0.0
├── keywords.json        三个词条 → prefab 绑定
├── effects/             三个 csx 行为制品
└── cards/               三张示例卡（各带一个词条）
```

## 约定

- 卡 id 前缀＝`pack/orc.bulwark/<原卡id>`（前端冻结契约）。
- 行为全部由 csx 承载：机动注入「行动额度触发器」，固守注册判定器包装器，抢修监听回合/位置信号。
- 依赖宿主装配 `ScriptEvaluator`（异步事件）与 `SyncScriptEvaluator`（判定器包装器）。
- 触发词条前置：先经 `CardPackAssembler.Attach` 注册效果库，再 `Match.Initialize`。
