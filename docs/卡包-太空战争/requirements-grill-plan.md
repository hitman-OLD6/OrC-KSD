# Requirements Grill Plan — 磁能词条（强磁护盾 / 磁反应装甲 / 海绵装甲 / 游击）

> 聚焦四个新增拓展词条的外部行为、相互耦合与卡包归属；承载路径（C# / csx / 判定器 / 新增信号）问题暂记实现阶段。

## 现有基线（代码事实，供对照）

- 既有独立卡包 `samples/card-pack-bulwark`（`id=orc.bulwark`，机动/固守/抢修，含 3 张示例卡、lock.formal/test、README、`docs/卡包-战术词条/`）。
- 卡包词条只能注册为「纯标识组件」（`CardPackKeywordLoader` → `PlainKeywordComponent` ＋ `DeclareEffectBindings`），行为一律由 csx 预制体承载，不得与内建/他包词条重名。
- 伤害管线四端已按名登记，可供预制体 `injects` 注入：`伤害处理触发器`（`DamageApplyView`，唯一扣数值处）、`对战伤害触发器` / `效果伤害触发器` / `战斗伤害触发器`（`DamageEventView`，按来源分的量改写点）；伤害种类 kind ∈ `combat` / `effect` / `battle`。
- 内建「免疫」＝把 `DamageApplyView.IsZeroed` 置 true 并清零（`ImmuneZeroingEffect`）；卡包词条可用 csx 在同端复现，但不能复用「免疫」这个名字。
- 伤害实扣为 0 时不发射 `card.damaged`（`MatchActionServices` 中 `amount <= 0` 直接返回、零发射）。
- **不存在**「被敌方指向/成为目标」的信号：`GameUpdates` 无 `unit.targeted`，`GameHooks.KardsTargetedByOrder` 仅 Deferred 占位未接线。现有可用的相近时机只有目标候选名单判定器 `targeting.candidate.eligibility`（求值＝"列出可选目标"，非"已选定"）。
- 「可以指挥回到支援阵线」＝现成程序规则 `move.retreat`（`CommandMovementRules.IsRetreat` → 目的地改为支援线）；「不能指挥移动」＝`move.leg.eligibility` 后置包装器（固守已用）。卡包词条要接 `move.retreat` 需确认承载通道（词条 prefab 当前只声明 injects / judicatorWrappers，不声明程序规则）。
- 带参词条的数据体写法：`Keyword:<已注册词条id>:<正整数>`（`CardAttributeMap.TryMap`）；形如 `磁反应装甲2` 的字面**不会**被识别（`Valued` 表只有 heavyArmor/intel）。

## Unresolved Questions

### Q1: 卡包归属 Completed
- 结论：**新建独立卡包**（不动 `orc.bulwark`）；包名/显示名留空，最后补（见 Q1b）。

### Q1b: 包身份字面 Pending
- 包目录名、`id`、显示名、guid、示例卡 id 前缀——全部最后补；补齐前以占位（`card-pack-<tbd>` / `orc.<tbd>`）落文档。

### Q2: 强磁护盾的减伤范围与失去时点 Completed（a / a1 不叠加 / a2 是）
- 三类伤害（combat / effect / battle）全 -1，下限 0；1 点伤害 → 0，不算「受到伤害」（引擎在实扣为 0 时不发射 `card.damaged`）。
- 同一单位重复获得不叠加（恒 -1）。
- 仅「玩家指挥的攻击成功完成」后失去，且失去在本攻击伤害结算之后（本次仍享受 -1）；取消/复验拒绝/未成功不失去。
- 「成功完成」＝攻击产生了伤害结算即算；目标已死导致无伤害结算也算失去。
- 与免疫的顺序仍未裁定（并入 Q3 子项）。

### Q3: 磁反应装甲的 x 语义 Completed（a；两个子项按默认）
- 带参词条：数据体 `Keyword:磁反应装甲:<n>`；范围 1–2（>2 钳到 2），未写值默认 1；`x=0` 不成立（等同无词条）。
- 每次被指向 x-1，归 0 时才移除词条（x=1 仍免疫）。
- 同一单位不叠加（重复授予取较大值）。
- 「具有免疫」＝内建免疫语义（伤害归零、不拦索敌、不防抑制）。
- 免疫先归零，与「强磁护盾」-1 不叠加。
- 待定：剩余仅在 Q4（指向时机）。

### Q4: 「被敌方指向」的语义与时机 Completed（A）
- 定义＝① 敌方**卡牌效果最终选定**本单位为目标，**或** ② 敌方**指挥攻击**指向本单位。
- 载体（用户裁定）：把「指向」做成**通用中间动作缝**（引擎级接入点，后续其它卡/效果可复用），而非私有信号。
- 明确排除：目标候选名单计算时不算（`targeting.candidate.eligibility` 不触发）；未选定/取消选择不扣层。
- 子问题 Q4a Pending：一次群体效果同时指向本单位多次、或同一动作内重复指向同一单位，是否每次各扣 1 层（默认：每次指向各扣 1）。
- 子问题 Q4b Pending：本引擎内「效果攻击」（`TriggerCard` 非空）是否算「指挥攻击」指向（默认：不算，仅玩家指挥攻击算）。

### Q5: 海绵装甲的判定与叠加 Completed（a）
- 条件型**最终封顶**：结算时单位持有「强磁护盾」→ 本次伤害最终值 ≤1（三类伤害）。
- 封顶在所有减伤/加伤之后（最后一步）；无「强磁护盾」则完全无效。
- 最终 0 不算「受到伤害」，1 正常受伤；与免疫并存时保持 0。
- 叠加顺序不影响结果（`min(1,max(0,x-1))` 与 `min(1,x)` 等价），故不单独裁定。

### Q6: 游击的移动规则 Completed（a）
- 等同程序规则 `move.retreat`：位于前线时可指挥移动到支援线**空槽**；支援线→前线照旧。
- 照常消耗行动额度与行动费；无空槽＝不可移动。
- 与「固守」同持 → 固守优先（不可指挥移动）；效果位移不受影响。

### Q7: 示例卡与展示 Completed（a；两个子项按默认：要 README + 使用指南）
- 4 张示例卡：强磁护盾步兵 / 磁反应装甲坦克（`Keyword:磁反应装甲:2`）/ 海绵装甲步兵（同时带「强磁护盾」+「海绵装甲」）/ 游击步兵。
- 阵营 `germany`、稀有度 `standard`，数值同上轮提案；包名定后写正式卡名与文档名。

### Q8: 验收与测试 Completed（a）
- 三层：数据面（manifest/预检双零/4 词条/4 卡）、行为面（每词条对局级用例）、引擎面（「指向」动作缝：效果选定 + 指挥攻击各一，候选不触发）。
- 交付门槛：全量 `dotnet test` 0 失败。
