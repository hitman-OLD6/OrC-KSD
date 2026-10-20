# Implementation — 太空战争卡包（强磁护盾 / 磁反应装甲 / 海绵装甲 / 游击）

实现完成：S1–S6 全部 Done；全量测试 **2066 通过 / 0 失败**（基线 2052 → 新增 14 项）。包身份：`samples/card-pack-space-war` / `id=orc.space-war` / `name=太空战争`。

## Scope
- Target: 「指向」通用接入点（每卡具名「指向触发器」+ `指向后` 缝，覆盖单位与 HQ）、三条伤害规则（伤害处理端 `Rewrite` 段 10/20/30）、强磁护盾失去点、`keywords.json.program` 通道（游击）、卡包与示例卡、三层测试、README/使用指南。
- Excludes: 卡面美术、本地化、平衡数值；HQ 词条效果注入通道（本版不补）。

## Implementation Steps

### S1: 「指向」承载（引擎）— Done
- Target: `src/Orc.Game/Cards/{TargetingView,TargetingRules,NamedTriggerTable}.cs`、`UnitCard`、`Players/Hq.cs`、`Effects/EffectRuntime.cs`、`Commanding/CommandManager.cs`
- Approach:
  - `TargetingView{Source,Target,Kind,TriggerCard,Host}`；`TargetingRules`（触发器名「指向触发器」、缝名「指向后」、kind＝`effect`/`command`、`NotifyAsync`）。
  - 具名触发面抽出 `NamedTriggerTable`，`CardBase` 与 `Hq` 共用；两处各有 `TargetingTrigger`，默认 handler 声明具名缝。
  - 触发点：无头选靶 `EffectRuntime.SelectAsync` 最终名单后、交互落定 `BeginTargetSelectionAsync` 返回前、指挥攻击 `CommandManager.DispatchAttackAsync` 成功后。
  - 每个目标各一次；候选枚举/取消/复验失败不触发；后续失败不回滚。
- 实现记录：`UnitAttackTriggerView` 增补只读 `IsCommandAttack`（`AttackAction.CommandAsync` 写 true、`EffectAsync` 写 false）——`TriggerCard` 在指挥链上会被帧继承填充，无法作为「玩家指挥 vs 效果攻击」判据。

### S2: 三条伤害规则（卡包 csx）— Done
- `keyword.magnetic-shield`：`injects` → 「伤害处理触发器」`Rewrite`/10（`Amount = max(0, Amount-1)`）＋「单位攻击触发器」`Commit`/200（`IsCommandAttack` 且 `Attacker==宿主` → 移除词条）。
- `keyword.magnetic-reaction`：`Rewrite`/20（`IsZeroed=true; Amount=0`）＋「指向触发器」（敌方来源 → x-1 / 归零移除）。
- `keyword.sponge-armor`：`Rewrite`/30（宿主有「强磁护盾」时 `Amount=min(1,Amount)`）。

### S3: 磁反应装甲订阅「指向」— Done
- `injects` → 宿主「指向触发器」默认区段；来源为空/同阵营不扣；`x=GetValue ?? 1`，`x-1<=0` → `RevokeAsync`，否则 `SetValue`。

### S4: `keywords.json.program` 通道（游击）— Done
- `CardPackKeyword.Program`（可选）；`PackageScanner.ReadProgram`（对象或字符串，`KeywordProgram.Parse` 校验）；零绑定时要求有 program。
- `CardPackKeywordLoader`：有 program → `ProgramKeywordComponent.Register`，否则 `PlainKeywordComponent`；绑定非空才声明。
- 游击 program：`{"schema":1,"rules":[{"scope":"self","rule":"move.retreat","value":"1"}]}`。

### S5: 卡包与示例卡 — Done
- `samples/card-pack-space-war/`：manifest（schemaVersion 2 / guid 派生 / version 内容指纹）、keywords（4）、effects（3 + sidecar）、cards（4 + sidecar）、两 lock、README。
- 侧车 GUID 按 `AssetIdentity.DeriveAssetGuid` 派生；`AssetMigrationTool --apply` 规范化 manifest/lock（幂等，`IsFullySkipped`）。

### S6: 测试与文档 — Done
- `tests/Orc.Packages.Tests/SpaceWarCardPackTests.cs`（3）：清单/诊断/内容/词条 program/prefab 声明。
- `tests/Orc.Game.Tests/SpaceWarKeywordTests.cs`（8）：强磁 -1（三类）、1 点归零零信号、攻击后失去、磁反应装甲免疫+扣层、海绵封顶/无护盾无效、游击可撤退/无词条不可。
- `tests/Orc.Game.Tests/TargetingRulesTests.cs`（3）：指挥攻击触发一次、候选收集不触发、效果选定逐目标各一次。
- 包内 `README.md` ＋ `docs/卡包-太空战争/使用指南.md`。

## Acceptance（对照 requirements）
- R1：包合法、预检无诊断、4 卡 4 词条、README/使用指南 — 通过。
- R2：三类伤害 -1、1→0 不受伤、攻击后失去、取消不失去 — 通过。
- R3：x=2→1→0、免疫、不叠加、敌方指向扣层、友方/候选不扣 — 通过。
- R4：有强磁封顶 1、无强磁无效、与免疫并存保持 0 — 通过。
- R5：前线可撤、无词条不可撤 — 通过（固守同持时固守优先：由既有 `move.leg.eligibility` 后置包装器保证）。
