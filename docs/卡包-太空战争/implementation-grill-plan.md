# Implementation Grill Plan — 磁能词条卡包

> 需求已定稿（见 `requirements.md`）。本阶段聚焦承载路径：伤害规则顺序、「指向」动作缝、`move.retreat` 接线、包身份占位、测试。

## 代码基线（已核实）

- wrapper/动作缝机制**已落地**：`Trigger.Register(..., seams)` 声明缝、`RegisterBeforeWrapper(target, seam, wrapper)` 挂缝、`Context.SeamAsync(seam)` 调用（`src/Orc/Core/Trigger.cs:203-329/585-591`）。
- 事件排序键＝`band 值 × 1000 + priority`，**升序**执行（`Trigger.cs:955-956`）。
- 伤害处理端 `DamageApplyView` 的 band 方案＝`ActionHandleBands`（`Rewrite=1` → `Veto=2` → `Apply=3`）；默认扣值 handler 在 `Apply`/priority 100（`DamageAction.cs:47-56`）。
- 预制体 `injects` **支持 band 名**与 priority（`InjectPrefab(TargetTriggerName, BandName, EventId, Priority)`，`Prefabs.cs:288`；落地 `EffectSystem.cs:351-356`），且目标触发器可按名回退到对局级（`MatchNamedTriggers` 已登记「伤害处理触发器」等四端，`Match.cs:659-664`）。
- 卡包词条只能注册 `PlainKeywordComponent` ＋ prefab 绑定，行为一律 csx。

## Unresolved Questions

### I1: 三条伤害规则的挂载点与顺序 Completed（A）
- 全部注入 `伤害处理触发器` 的 `Rewrite` 段：priority 10（强磁 -1）→ 20（磁反应装甲免疫，`IsZeroed`）→ 30（海绵封顶）。
- 默认扣值在 `Apply` 段恒最后；与「重甲」的相对顺序＝重甲（触发端）先、强磁（处理端）后。
- 引擎零改动，纯卡包 csx。

### I2: 「指向」动作缝的形态 Completed（A）
- **机制约束（已核实）**：包装器只能挂到**同一触发器**的 handler 注册项（`Trigger.cs:312-315` 跨触发器拒绝）；卡包 prefab 的 `wrappers[]` 只能挂本效果内事件（`DynamicEffect.cs:162-166`）。
- 结论：每张卡一个具名「指向触发器」——引擎在卡构造时创建，默认 handler 声明具名缝（`指向后`）；卡包词条用 `injects` 订阅**自己卡**的指向触发器；C#/插件可挂缝包装器。
- 调用点：效果最终选定目标处＋指挥攻击目标确定处，对每个目标卡各触发一次。

### I3: 磁反应装甲订阅方式 Completed（随 I2-A）
- prefab `injects: { target:"指向触发器", event:<handler>, priority }` + csx：
  - 来源为空或来源与宿主同阵营 → 不扣；
  - `x = host.Keywords.GetValue("磁反应装甲") ?? 1`；`x-1 == 0` → `RevokeAsync`，否则 `SetValue(x-1)`；
  - 无副作用（不打断指向动作）。

### I4: 游击接 `move.retreat` 的通道 Completed（A）
- 卡包 `keywords.json` 增加可选 `program` 字段（`KeywordProgram` JSON）；有＝注册 `ProgramKeywordComponent`，无＝`PlainKeywordComponent`。
- 游击声明 `rules: [{ rule: "move.retreat", value: "1", scope: "self" }]`。
- 改动面：`CardPackKeyword` 记录、`PackageScanner.ReadKeywords`、`CardPackKeywordLoader`、预检校验 program 合法性。

### I5: 包身份 Completed（正式名）
- 显示名「太空战争」；目录 `samples/card-pack-space-war`；`id=orc.space-war`；`name=太空战争`；guid 用新固定值（不与 `orc.bulwark` 冲突）。
- 卡应用层 id 前缀＝`pack/orc.space-war/<局部ID>`；示例卡局部 id 以 `space-war.` 开头。
- 文档目录＝`docs/卡包-太空战争/`（原「卡包-磁能词条」已迁移）。

### I6: 「指向」的触发时点与归属 Completed（A + HQ）
- 归属：`UnitCard` ＋ `Hq`（两者共用一份「指向触发器」承载；`Hq : Card` 不走 `CardBase` 的具名触发面，需抽公共承载）。
- 时点：目标**最终确定**即触发——指挥攻击在复验通过、动作真正发起时；效果选靶在最终名单确定时；每个目标各一次；后续失败/反制**不回滚**已扣层。

### I6b: HQ 的能力边界 Completed（a）
- HQ 只预留「指向触发器」（抽 `UnitCard`/`Hq` 共用的具名触发承载）；四个词条仍只在单位上生效。

### I7: 测试放置 Completed
- 数据面 `tests/Orc.Packages.Tests/SpaceWarCardPackTests.cs`；行为面 `tests/Orc.Game.Tests/SpaceWarKeywordTests.cs`（含「指向」用例）。

### I8: 文档 Completed
- 包内 `README.md` ＋ `docs/卡包-太空战争/使用指南.md`（玩家向）。

### I9: 强磁护盾失去的挂载点 Completed
- 订阅宿主「单位攻击触发器」的 `Commit` 段（priority 200，晚于默认转发 100）：`Attacker == host` 且 `TriggerCard == null`（玩家指挥攻击）→ 移除「强磁护盾」。
- 攻击取消/复验失败不会进入该段；目标已死无伤害结算也会进入，符合「成功完成」口径。
