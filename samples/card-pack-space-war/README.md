# 太空战争系列（card-pack-space-war）

独立卡包，提供四个拓展词条与四张示例卡。

| 词条 | 行为 | 行为制品 |
|---|---|---|
| 强磁护盾 | 受到的伤害 -1（对战/效果/战斗三类）；玩家指挥攻击成功完成后失去（本次攻击仍减伤） | `effects/keyword.magnetic-shield.prefab.json`（`injects` → 「伤害处理触发器」`Rewrite`/10 ＋「单位攻击触发器」`Commit`/200） |
| 磁反应装甲x | 具有免疫（伤害归零）；被敌方指向后 x-1（x 归 0 失去）；最多 2，不叠加 | `effects/keyword.magnetic-reaction.prefab.json`（`injects` → 「伤害处理触发器」`Rewrite`/20 ＋「指向触发器」） |
| 海绵装甲 | 若同时具有「强磁护盾」，受到的伤害最终至多 1；否则无效 | `effects/keyword.sponge-armor.prefab.json`（`injects` → 「伤害处理触发器」`Rewrite`/30） |
| 游击 | 可以指挥移动回支援阵线空槽（等同 `move.retreat`）；与「固守」同持时固守优先 | `keywords.json` 的 `program`（`rules: move.retreat`） |

## 目录

```
card-pack-space-war/
├── manifest.json        kind=cardpack / id=orc.space-war / name=太空战争 / version=内容指纹
├── lock.formal.json     正式 lock（本包资产 GUID）
├── lock.test.json       测试 lock
├── keywords.json        四个词条 → prefab 绑定（游击＝程序词条）
├── effects/             三个 csx 行为制品（各带 .meta.json sidecar）
└── cards/               四张示例卡（各带 .meta.json sidecar）
```

## 约定

- 卡应用层 id 前缀＝`pack/orc.space-war/`（前端冻结契约）；示例卡局部 id 以 `space-war.` 开头。
- 带参词条在卡数据体里的写法：`"Keyword:磁反应装甲:2"`（`CardAttributeMap.TryMap`）。
- 行为全部由 csx 或词条程序承载：三条伤害规则挂在伤害处理端的 `Rewrite` 段（priority 10 → 20 → 30，默认扣值在 `Apply` 段恒最后）。
- 「指向」＝引擎通用接入点：被指向方（单位／HQ）的具名「指向触发器」，默认 handler 声明具名动作缝 `指向后`；词条经 `injects` 订阅。
- 依赖宿主装配 `ScriptEvaluator`（异步事件）与 `SyncScriptEvaluator`（判定器包装器）。
