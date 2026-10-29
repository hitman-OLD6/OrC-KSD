# API 速查

> 纯查表，不含叙述。列定义：**签名**（`src/` 现状，2026-10-29 核对）｜**层归属**｜**失败语义**｜**数据侧可达**（csx 或 JSON 能否调用）。
> 语义与边界见 [05 章](../05-动作面与运行时API.md)；判定器语义见 [03 章](../03-判定器与wrapper.md)。
> 记号：`ct`＝`CancellationToken`；`source`＝可选施动卡（可空）。

## 1. 动作面 `EffectRuntime`

> 解析入口：`EffectRuntime.ResolveFor(Card card)` → 未装配/脱局返回 `null`（调用方按「功能不可用」处置）。
> **`source` 规则**：只写下游信号 `Source` ＋ 作下游动作入参；**不猜测、不回退**；不传＝`null`。
> `KillAsync` / `DamageAsync` / `BattleAsync` 的 `source` 是**既有形参、位于 `ct` 之前**；其余动作的 `source` 在 `ct` 之后。

### 1.1 核心动作（26）

| 方法签名 | 层归属 | 失败语义 | 数据侧可达 |
| --- | --- | --- | --- |
| `bool KillAsync(Card target, Card? source = null, ct = default)` | 游戏层 | target null＝`ArgumentNullException`；非单位/服务不可达＝`false` | 是 |
| `bool DamageAsync(Card target, int amount, Card? source = null, ct = default)` | 游戏层 | target null＝异常；非单位/HQ／服务不可达＝`false`。经 `effect-damage` → `damage-apply` | 是 |
| `bool BattleAsync(Card first, Card second, Card? source = null, ct = default)` | 游戏层 | 任一方非在场存活单位／同一单位／服务不可达＝`false`（零副作用）。快照＋同时结算；经 `battle-damage` → `damage-apply`；`source` 仅审计 | 是 |
| `bool BuffAsync(Card target, int attack, int defense, EffectDuration duration = Permanent, ct = default, Card? source = null)` | 游戏层 | target null＝异常；HQ 且 attack≠0／非 CardBase／无变化＝`false` | 是 |
| `bool GrantAsync(Card target, string keyword, ct = default, Card? source = null)` | 游戏层 | target null／keyword 空白＝异常；无词条面／已存在＝`false` | 是 |
| `bool GrantAsync(Card target, string keyword, int? value, ct = default, Card? source = null)` | 游戏层 | 同上；`null`＝未提供参值（≠0） | 是 |
| `bool RevokeAsync(Card target, string keyword, ct = default, Card? source = null)` | 游戏层 | 不存在＝`false`；已死亡卡＝异常透传 | 是 |
| `bool SetKeywordValueAsync(Card target, string keyword, int? value, ct = default, Card? source = null)` | 游戏层 | 词条不存在＝异常透传；不可达＝`false` | 是 |
| `int DrawAsync(Card viewer, int count, ct = default, Card? source = null)` | 游戏层 | viewer null＝异常；count≤0／不可达＝`0` | 是 |
| `DeckFetchResult FetchFromDeckAsync(Card target, ct = default, Card? source = null)` | 游戏层 | target null＝异常；三态 `Completed`/`Burned`/`Rejected` | 是 |
| `bool CostModAsync(Card target, int delta, EffectDuration duration = Permanent, ct = default)` | 游戏层 | target null＝异常；非卡／delta=0＝`false`。**无 `source`** | 是 |
| `bool DeployCostModAsync(Card target, int delta, ct = default)` | 游戏层 | 同上。**无 `source`** | 是 |
| `int AddToHandAsync(Card viewer, string cardName, int count, ct = default, Card? source = null)` | 游戏层 | 参数空＝异常；未命中／不可达＝`0` | 是 |
| `int ShuffleInAsync(Card viewer, string cardName, int count, ct = default, Card? source = null)` | 游戏层 | 同上；成功发 `deck.shuffled` | 是 |
| `bool PinAsync(Card target, ct = default, Card? source = null)` | 游戏层 | 非单位／拒绝＝`false` | 是 |
| `bool SilenceAsync(Card target, ct = default, Card? source = null)` | 游戏层 | 非单位＝`false` | 是 |
| `VeteranPromotionResult UpgradeAsync(Card target, ct = default, Card? source = null)` | 游戏层 | 非单位＝`Rejected(NotOnField)`；执行失败＝回滚＋异常上抛 | 是 |
| `RevealOutcome RevealAsync(Card target, ct = default, Card? source = null)` | 游戏层 | 非单位／已揭示＝`NoOp` | 是 |
| `bool MoveAsync(Card target, string zone, ct = default, Card? source = null)` | 游戏层 | zone 空白＝异常；非单位／不可用＝`false` | 是 |
| `bool GainPointSlotsAsync(Card viewer, int amount, ct = default, Card? source = null)` | 游戏层 | amount≤0／不可达＝`false` | 是 |
| `bool LosePointSlotsAsync(Card viewer, int amount, ct = default, Card? source = null)` | 游戏层 | 同上 | 是 |
| `bool GainPointsAsync(Card viewer, int amount, ct = default, Card? source = null)` | 游戏层 | 同上；实际增加才发 `point.gained`→`point.changed` | 是 |
| `bool LosePointsAsync(Card viewer, int amount, ct = default, Card? source = null)` | 游戏层 | 同上 | 是 |
| `IReadOnlyList<Card> SelectAsync(Card viewer, EffectSelector selector, ct = default)` | 游戏层 | 参数 null＝异常；判定器不可用＝空集 | 是 |
| `bool DeclareAuraAsync(Card host, string field, int delta, EffectAuraFilter filter, ct = default)` | 游戏层 | delta=0／字段非法／环境不可达＝`false`。**无 `source`** | 是 |
| `bool RerunAurasAsync(Card host, ct = default)` | 游戏层 | 无光环／不可达＝`false`。**无 `source`** | 是 |

### 1.2 场地动作（3）

| 方法签名 | 层归属 | 失败语义 | 数据侧可达 |
| --- | --- | --- | --- |
| `ActionResult? JoinFrontlineAsync(Card host, UnitCard unit, int? index = null, ct = default)` | 游戏层 | 前线有存活敌方单位／满员＝失败结果（不抛）；未装配＝`null` | 是 |
| `ActionResult? JoinSupportLineAsync(Card host, UnitCard unit, int? index = null, ct = default)` | 游戏层 | 落位＝显式序号 ＞ 守护 ＞ 默认最左；未装配＝`null` | 是 |
| `HandCardPick PickHandCardsAsync(Card viewer, int min, int max, ct = default)` | 游戏层 | `min` 大于手牌数＝`Cancelled`（零交互）；`max` 收敛到手牌数 | 是 |

### 1.3 查询与判定（5）

| 方法签名 | 层归属 | 说明 | 数据侧可达 |
| --- | --- | --- | --- |
| `static EffectRuntime? ResolveFor(Card card)` | 游戏层 | 卡 → 运行时；未装配／脱局＝`null` | 是 |
| `bool Judge(string name, params object[] args)` | 游戏层 | 问一条判定规则（名字见 §2）。判定器缺失＝`false` | 是 |
| `static bool EvaluateCondition(Card? viewer, string spec)` | 游戏层 | 求值条件表达式串（与词条程序同口径） | 是 |
| `IReadOnlyList<Card> OwnerUnits(CardBase host)` | 游戏层 | 宿主归属玩家的全部单位（现收集） | 是 |
| `AuraRegistry? AurasOf(Card host)` | 游戏层 | 宿主已声明的光环登记表 | 是 |

### 1.4 成员地址属性

| 属性 | 值 | 说明 |
| --- | --- | --- |
| `MemberAddress? JoinFrontlineTrigger` | `engine#join.frontline` | 未装配＝`null` |
| `MemberAddress? JoinSupportLineTrigger` | `engine#join.support` | 同上 |

### 1.5 伤害四端（改写挂哪一层）

| 成员地址 | 视图字段 | 典型改写 |
| --- | --- | --- |
| `engine#combat-damage` | `Source`/`Target`/`Amount`(可写)/`Kind` | 重甲（只减对战） |
| `engine#effect-damage` | 同上 | 「受到效果伤害 +1」 |
| `engine#battle-damage` | 同上 | 「战斗伤害翻倍」 |
| `engine#damage-apply` | 同上 ＋ `IsZeroed`(可写) | 免疫（归零）、通用减伤 |

数据侧：`wraps[{ "target": "engine#damage-apply", "phase": "before", "handler": "<本资产某成员的 handler.id>" }]`。
代码侧：`runtime.Wraps.Register(WrapDeclaration.ForMember("engine#damage-apply", WrapperPhase.Before, id), handler, order)`。
段名一律取 `BandSchemes`（`Initiate`／`Commit`／`Rewrite`／`Veto`／`Apply`…），不写裸字符串。

### 1.6 相关数据类型

| 类型 | 取值 / 形态 |
| --- | --- |
| `EffectSelector` | `(string Sel, string? Side, string? Zone, string? UnitType, string? Keyword, int? Count, EffectThreshold? Threshold, bool IncludeCovert)`；`Sel` ∈ `all`/`self`/`random`/`one`/`any` |
| `EffectThreshold` | `(string Field, string Op, int Value)`；`Op` ∈ `gte`/`lte`/`gt`/`lt`/`eq` |
| `EffectAuraFilter` | `(string? Side, string? Faction, string? UnitType, string? Keyword, bool ExcludeSelf, string? Zone)` |
| `EffectDuration` | `Permanent` / `TurnEnd` / `NextOwnerTurnStart` |
| `DeckFetchResult` | `Completed` / `Burned` / `Rejected` |
| `VeteranPromotionResult` | 成功 / `Rejected(NotOnField)` 等 |
| `RevealOutcome` | `Ok` / `NoOp` |
| `HandCardPick` | `Status`（`Ok`/`Cancelled`/`Failed`）＋ `Cards` ＋ `Reason` |
| `ActionResult?` | 动作结局；`null`＝未装配（功能不可用） |

## 2. 判定器名（28）

| 组 | 名字 |
| --- | --- |
| 准入／验证（7） | `action.input.allowed`、`action.turn.owner`、`validation.cost.check`、`validation.counter.use`、`validation.move.recheck`、`validation.attack.recheck`、`inhibit.leg.eligibility` |
| 示范（1） | `deck.top.tag` |
| 选靶（1） | `targeting.candidate.eligibility` |
| 交战（6） | `combat.target.legal`、`combat.range`、`combat.guard.eligibility`、`combat.interception`、`combat.counter.eligibility`、`combat.ambush.condition` |
| 移动／攻击（2） | `move.leg.eligibility`、`attack.leg.eligibility` |
| 移动（1） | `move.frontline-enemy` |
| 部署（1） | `deploy.position.candidates` |
| 效果（1） | `effect.target.resolve` |
| 资源（5） | `resource.slot.increment`、`resource.slot.gain`、`resource.slot.lose`、`resource.point.gain`、`resource.point.lose` |
| 通用谓词（3） | `unit.adjacent`、`unit.type`、`unit.side` |

字面值唯一来源：`Orc.Game.Judicators.JudicatorNames`。数据侧可用 `runtime.Judge("<名>", args...)` 消费；
注册/包装判定器＝代码侧（`JudicatorRegistry`）或 `judge-wraps[]`（数据侧包装）。

## 3. 选择器（targeter）

### 3.1 `SelectorTemplates`（11 个）

| 模板 | 交互 | 典型用途 |
| --- | --- | --- |
| `FieldUnit` | 拖拽 | 选场上单位 |
| `HandOrigin` | 点击 | 手牌起始指向 |
| `UnitHandDrag` | 拖拽 | 单位手牌拖到空槽 |
| `AfterPlacement` | 点击 | 放置后指向 |
| `Slot` | 点击 | 选槽位 |
| `TargetPoint` | 点击 | 选目标点 |
| `Hand` | 点击 | 选手牌 |
| `Mulligan` | 点击 | 换牌 |
| `Option` | 点击 | 选选项 |
| `CardPickerList` | 点击 | 列表选卡 |
| `CardPickerSet` | 点击 | 集合选卡 |

### 3.2 参数与结果

| 类型 | 形态 |
| --- | --- |
| `ReferenceSetParameter` | `(candidates, min, max, domainValidator?, tag?)`；候选＝后端快照 |
| `SelectorResult<TResult>` | `Ok` / `Cancelled` / `Failed`（`InvalidSelection`/`RetryLimitExceeded`/`Fault`） |
| `TargeterResult` | `Ok` / `Cancelled` / `Failed` |
| `ITargeterFlow` | `Step<T>(selector, parameter)` / `Retry<T>()` / `RetryCount` |
| `TargeterManager` | `ResolveFor(card)` → `RunAsync(flow)` |

### 3.3 `EffectSelector` 的 `sel` 三分类

| `sel` | 归谁 | 是否产生 targeter | 无前端时 |
| --- | --- | --- | --- |
| `all` / `self` | 判定器（候选集即答案） | 否 | 正常工作 |
| `random` | 引擎随机服务（需 `MatchOptions.AllowRandomTargeting`） | 否 | 生产对局**明确报错** |
| `one` / `any` | 前端（提交通道） | 是 | **明确失败**（引擎不代选） |

## 4. 词条

### 4.1 词条标识（`KeywordIds`，19 条）

| 标识 | 说明 |
| --- | --- |
| `闪击` | 部署即可行动 |
| `奋战` | 攻击后可再行动一次 |
| `烟幕` | 部署当回合不可被指定 |
| `伏击` | 攻击时先手结算 |
| `被压制` | 状态词条（不可行动） |
| `被抑制` | 状态词条（无词条行为） |
| `动员` | 部署/加入时触发 |
| `钳击` | 相邻协同 |
| `预报` | **骨架**：仅标识（G-09） |
| `免疫` | 伤害归零（挂 `damage-apply`） |
| `重甲` | 对战伤害 -N（挂 `combat-damage`） |
| `情报` | 触发结构就位，执行面未接线（G-10） |
| `无法被压制` | 状态免疫 |
| `无法被抑制` | 状态免疫 |
| `亡计` | 死亡时触发内容效果 |
| `老兵` | 可升为老兵 |
| `隐蔽` | 不可被指定，攻击/移动后揭示 |
| `守护` | 相邻 HQ 受击时转移 |
| `冲击` | 行动时附带效果 |

### 4.2 词条程序四表

| 表 | 条数 | 内容 |
| --- | --- | --- |
| `KeywordProgram.RuleNames` | 11 | `move.allowed`、`move.retreat`、`attack.allowed`、`combat.range`、`combat.counter.eligibility`、`combat.guard.eligibility`、`combat.interception`、`combat.ambush.condition`、`deploy.frontline`、`attack.limit`、`targeting.forbid` |
| `KeywordProgram.EventNames` | 17 | `unit.acted`、`turn.start.before`、`turn.end.before`、`card.played`、`unit.deployed`、`unit.joined`、`card.placed`、`turn.start`、`turn.end`、`card.damaged`、`card.dying`、`card.leaving`、`unit.attack.before`、`unit.attack.after`、`unit.destroy.before`、`keyword.ready`、`keyword.consumed` |
| `KeywordProgram.Subjects` | 6 | `self`、`friendly`、`enemy`、`any`、`owner`、`opponent` |
| `KeywordProgram.Targets` | 14 | `self`、`event`、`linked`、`attack.target`、`enemy.hq`、`friendly.hq`、`enemy.units`、`friendly.units`、`enemy.frontline`、`friendly.frontline`、`random.enemy.frontline`、`choose.unit`、`choose.enemy.unit`、`choose.friendly.unit` |

### 4.3 词条操作（`KeywordProgram.Operations`，32 条）

`state.set`、`state.add`、`capture`、`link`、`unlink`、`move.set`、`attack.set`、`attack`、`replace`、`retreat`、
`damage`、`buff`、`stat.add`、`stat.set`、`heal`、`kill`、`keyword.grant`、`keyword.remove`、`emit`、
`death.cancel`、`health.set`、`copy.hand`、`return.hand`、`create.hand`、`develop`、`move`、`draw`、`points`、
`attach.prefab`、`targeting.forbid`、`targeting.allow`、`if`

## 5. 来源两面（视图属性）

| 面 | 类型 | 谁写 | 语义 | 可空 |
| --- | --- | --- | --- | --- |
| `view.Source` | `Card?` | 发射方（`Emit*(..., source:)`） | 事件引发者 | 是（键恒在） |
| `view.TriggerCard` / `ctx.TriggerCard` | `Card?` | 框架按边归属解析 | 当前 handler 的触发者（链上最后者胜） | 是 |
| `view.Host` | `object?` | 框架回填（承载对象） | 效果宿主（卡牌实例） | 是 |

**归因三选一**：`Source`（事件引发者）｜载荷里已有的对象键（如 `unit.damage.dealt.Unit`＝施动方）｜`TriggerCard`（当前触发者）。
不要用 `TriggerCard` 代替 `Source` 做「敌方所致」判定。逐信号现状见 [信号表](./信号表.md) §7。
