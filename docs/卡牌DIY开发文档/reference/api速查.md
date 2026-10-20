# API 速查

> 纯查表，不含叙述。列定义：**签名**（`src/` 现状）｜**层归属**（内核／游戏层）｜**失败语义**｜**数据侧可达**（csx 或 JSON 能否调用）。
> 符号定位见 [`_anchors.md`](./_anchors.md) §7–§9；动作方法的完整语义与边界见 05 章（待写）。
> 记号：`ct`＝`CancellationToken`；`source`＝可选施动卡（R9 尾参，可空）。

## 1. 动作面 `EffectRuntime`

> 解析入口：`EffectRuntime.ResolveFor(Card card)` → 未装配/脱局返回 `null`（调用方按「功能不可用」处置）。
> **`source` 规则**：只写下游信号 `Source` ＋ 作下游动作入参；**不猜测、不回退**；不传＝`null`。
> `KillAsync` / `DamageAsync` 的 `source` 是**既有形参、位于 `ct` 之前**（位置不变）；其余动作的 `source` 在 `ct` 之后。

| 方法签名 | 层归属 | 失败语义 | 数据侧可达 |
| --- | --- | --- | --- |
| `Task<EventStream> Effect.CastAsync(LogicEngine, IDictionary<string,object?>?, ct)`（主动效果施放） | 内核 | engine 为 null＝`ArgumentNullException`；事件异常按隔离记录 | 否（装配期） |
| `bool KillAsync(Card target, Card? source = null, ct = default)` | 游戏层 | target null＝`ArgumentNullException`；非单位/服务不可达＝`false` | 是 |
| `bool DamageAsync(Card target, int amount, Card? source = null, ct = default)` | 游戏层 | target null＝`ArgumentNullException`；非单位/HQ＝`false`；服务不可达＝`false`。经 `effect.damage` → `damage.apply`（R11） | 是 |
| `bool BattleAsync(Card first, Card second, Card? source = null, ct = default)` | 游戏层 | 任一方非**在场存活单位**／同一单位／服务不可达＝`false`（零副作用）。快照＋同时结算；经 `battle.damage` → `damage.apply`；`source` 仅审计 | 是 |
| `bool BuffAsync(Card target, int attack, int defense, EffectDuration duration = Permanent, ct = default, Card? source = null)` | 游戏层 | target null＝`ArgumentNullException`；HQ 且 attack≠0／非 CardBase／无变化＝`false` | 是 |
| `bool GrantAsync(Card target, string keyword, ct = default, Card? source = null)` | 游戏层 | target null／keyword 空白＝异常；无词条面＝`false`；已存在＝`false` | 是 |
| `bool GrantAsync(Card target, string keyword, int? value, ct = default, Card? source = null)` | 游戏层 | 同上；`null`＝未提供参值（≠0） | 是 |
| `bool RevokeAsync(Card target, string keyword, ct = default, Card? source = null)` | 游戏层 | 同 Grant；已死亡卡＝异常透传；不存在＝`false` | 是 |
| `bool SetKeywordValueAsync(Card target, string keyword, int? value, ct = default, Card? source = null)` | 游戏层 | 词条不存在＝异常透传；不可达＝`false`；置空合法 | 是 |
| `bool GrantWithContentAsync(Card target, string keyword, Effect? content, ct = default, Card? source = null)` | 游戏层 | 同 Grant；content 为 null＝合法静默 | 是 |
| `int DrawAsync(Card viewer, int count, ct = default, Card? source = null)` | 游戏层 | viewer null＝`ArgumentNullException`；count≤0／服务不可达＝`0`；满手走爆牌 | 是 |
| `DeckFetchResult FetchFromDeckAsync(Card target, ct = default, Card? source = null)` | 游戏层 | target null＝`ArgumentNullException`；结果三态（`Completed`/`Burned`/`Rejected(原因)`） | 是 |
| `bool CostModAsync(Card target, int delta, EffectDuration duration = Permanent, ct = default)` | 游戏层 | target null＝`ArgumentNullException`；非卡／delta＝0＝`false`（**无 source 尾参**） | 是 |
| `bool DeployCostModAsync(Card target, int delta, ct = default)` | 游戏层 | 同上（**无 source 尾参**） | 是 |
| `int AddToHandAsync(Card viewer, string cardName, int count, ct = default, Card? source = null)` | 游戏层 | 参数空＝异常；count≤0／解析失败／服务不可达＝`0` | 是 |
| `int ShuffleInAsync(Card viewer, string cardName, int count, ct = default, Card? source = null)` | 游戏层 | 同上；成功发 `deck.shuffled` | 是 |
| `bool PinAsync(Card target, ct = default, Card? source = null)` | 游戏层 | target null＝异常；非单位／资格拒绝＝`false` | 是 |
| `bool SilenceAsync(Card target, ct = default, Card? source = null)` | 游戏层 | target null＝异常；非单位＝`false` | 是 |
| `VeteranPromotionResult UpgradeAsync(Card target, ct = default, Card? source = null)` | 游戏层 | target null＝异常；非单位＝`Rejected(NotOnField)`；执行失败＝回滚＋异常上抛 | 是 |
| `RevealOutcome RevealAsync(Card target, ct = default, Card? source = null)` | 游戏层 | target null＝异常；非单位／已揭示＝`NoOp` | 是 |
| `bool MoveAsync(Card target, string zone, ct = default, Card? source = null)` | 游戏层 | target null／zone 空白＝异常；非单位／不可用＝`false` | 是 |
| `bool GainPointSlotsAsync(Card viewer, int amount, ct = default, Card? source = null)` | 游戏层 | viewer null＝异常；amount≤0／不可达＝`false` | 是 |
| `bool LosePointSlotsAsync(Card viewer, int amount, ct = default, Card? source = null)` | 游戏层 | 同上 | 是 |
| `bool GainPointsAsync(Card viewer, int amount, ct = default, Card? source = null)` | 游戏层 | 同上；实际增加才发 `point.gained`→`point.changed` | 是 |
| `bool LosePointsAsync(Card viewer, int amount, ct = default, Card? source = null)` | 游戏层 | 同上 | 是 |
| `IReadOnlyList<Card> SelectAsync(Card viewer, EffectSelector selector, ct = default)` | 游戏层 | 参数 null＝异常；判定器不可用＝空集 | 是 |
| `IReadOnlyList<Card> BeginTargetSelectionAsync(Card viewer, EffectSelector selector, ct = default)` | 游戏层 | 参数 null＝异常；无 targeter／无候选／取消＝空集 | 是 |
| `bool DeclareAuraAsync(...)`（字段/数值/过滤/ct） | 游戏层 | 宿主/服务不可达＝`false`；无 source 尾参 | 是 |
| `AuraRegistry? AurasOf(Card host)` | 游戏层 | host null＝异常；未装载/脱局＝`null` | 是 |
| `Task<bool> RerunAurasAsync(Card host, ct = default)` | 游戏层 | host null＝异常；脱局＝`false` | 是 |
| `bool Judge(string name, params object[] args)`（同步；判定器布尔求值） | 游戏层 | name 空白＝异常；判定器未注册/结果非 bool＝`false` | 是 |
| `DamagePipeline? DamagePipeline`（逃生舱：四端触发器自注册） | 游戏层 | 未装配/脱局＝`null` | 是 |

### 1.1 伤害三层的注册口（R11）

| 触发器 | 访问路径 | band 枚举 | 典型改写 |
| --- | --- | --- | --- |
| 对战伤害端 | `match.CommandManager.DamagePipeline.CombatDamage` | `ActionTriggerBands.Initiate` | 重甲（只减对战） |
| 效果伤害端 | `match.CommandManager.DamagePipeline.EffectDamage` | `ActionTriggerBands.Initiate` | 「受到效果伤害 +1」 |
| 战斗伤害端 | `match.CommandManager.DamagePipeline.BattleDamage` | `ActionTriggerBands.Initiate` | 「战斗伤害翻倍」 |
| 处理端 | `match.CommandManager.DamagePipeline.Apply` | `ActionHandleBands.Rewrite` / `Apply` | 免疫（`IsZeroed = true`）、通用减伤（`Amount - N`） |

**数据侧注入**（预制体 `injects`，撤销随效果托管）——对局级具名触发器名：

| 名字 | 目标触发器 | 视图 |
| --- | --- | --- |
| `对战伤害触发器` | `DamagePipeline.CombatDamage` | `DamageEventView` |
| `效果伤害触发器` | `DamagePipeline.EffectDamage` | `DamageEventView` |
| `战斗伤害触发器` | `DamagePipeline.BattleDamage` | `DamageEventView` |
| `伤害处理触发器` | `DamagePipeline.Apply` | `DamageApplyView` |

- 视图：`DamageEventView { Source, Target, Amount, Kind, Settlement }`、`DamageApplyView { …, IsZeroed }`。
- 白名单：`ApplyDefenseDamageAsync` / `ApplyDamageAsync` 为 `internal`，**只有 `DamagePipeline` 调**（外部程序集编译期被挡）。

### 1.1 相关数据类型

| 类型 | 用途 | 关键成员 |
| --- | --- | --- |
| `EffectSelector` | 无头选靶描述 | `Sel`（all/one/any/random/self）、`Side`、`Zone`、`UnitType`、`Keyword`、`Count`、`Threshold`、`IncludeCovert` |
| `EffectDuration` | 修饰期限 | `Permanent` / `TurnEnd` / `NextOwnerTurnStart` |
| `EffectThreshold` | 目标阈值 | `Field`（attack/defense/opCost/deployCost）、`Op`（gte/lte/gt/lt/eq）、`Value` |
| `EffectAuraFilter` | 光环受益谓词 | `Side`/`Faction`/`UnitType`/`Keyword`/`ExcludeSelf`/`Zone` |

## 2. 判定器名（27 名 ＝ 26 内置 ＋ 1 示范）

> 注册：`JudicatorRegistry.Register(name, factory)`；改写：`RegisterBeforeWrapper` / `RegisterAfterWrapper` / `RegisterReplaceWrapper`。
> 判定器是**代码侧**能力；数据侧只能「消费」既有判定结果（不能新增/改写）。

| 名称 | 用途 | 层归属 | 数据侧可达 |
| --- | --- | --- | --- |
| `action.input.allowed` | 输入准入（拖拽/指挥） | 游戏层 | 否 |
| `action.turn.owner` | 回合归属准入（三入口同源） | 游戏层 | 否 |
| `validation.cost.check` | 费用检查（预打出/打出） | 游戏层 | 否 |
| `validation.counter.use` | 反制使用检查 | 游戏层 | 否 |
| `validation.move.recheck` | 移动复验 | 游戏层 | 否 |
| `validation.attack.recheck` | 攻击复验 | 游戏层 | 否 |
| `deck.top.tag` | 卡组顶特点判定（示范、外部装配） | 游戏层 | 否 |
| `targeting.candidate.eligibility` | 目标候选合法性（玩家域） | 游戏层 | 否 |
| `combat.target.legal` | 战斗目标合法性（组合判定） | 游戏层 | 否 |
| `combat.range` | 射程矩阵 | 游戏层 | 否 |
| `combat.guard.eligibility` | 被守护攻击资格 | 游戏层 | 否 |
| `combat.interception` | 轰炸机拦截 | 游戏层 | 否 |
| `combat.counter.eligibility` | 反击资格（豁免表） | 游戏层 | 否 |
| `combat.ambush.condition` | 伏击条件 | 游戏层 | 否 |
| `move.leg.eligibility` | 移动 leg 资格（复验消重） | 游戏层 | 否 |
| `attack.leg.eligibility` | 攻击 leg 资格（复验消重） | 游戏层 | 否 |
| `move.frontline-enemy` | 推进前置（前线存活敌方） | 游戏层 | 否 |
| `effect.target.resolve` | 效果无头选靶 | 游戏层 | 是（经 `SelectAsync` 间接触发） |
| `resource.slot.increment` | 回合开始槽递增 | 游戏层 | 否 |
| `resource.slot.gain` | 额外获得槽数包裹 | 游戏层 | 否 |
| `resource.slot.lose` | 失去槽数包裹 | 游戏层 | 否 |
| `resource.point.gain` | 额外获得点数包裹 | 游戏层 | 否 |
| `resource.point.lose` | 失去点数包裹 | 游戏层 | 否 |
| `unit.adjacent` | 相邻关系（同线、索引差 1） | 游戏层 | 是（经 `EffectRuntime.Judge`） |
| `unit.type` | 兵种判定（参数化兵种标识） | 游戏层 | 是（经 `EffectRuntime.Judge`） |
| `unit.side` | 归属判定（是否同一玩家） | 游戏层 | 是（经 `EffectRuntime.Judge`） |

## 3. 选择器（targeter）

| 名称 | 用途 | 层归属 | 数据侧可达 |
| --- | --- | --- | --- |
| `SelectorTemplates.FieldUnit` | 战场单位（引用集） | 游戏层 | 是（csx 选靶） |
| `SelectorTemplates.HandOrigin` | 手牌起始指向 | 游戏层 | 是（卡声明） |
| `SelectorTemplates.UnitHandDrag` | 单位手牌拖拽→空槽 | 游戏层 | 是（卡声明） |
| `SelectorTemplates.AfterPlacement` | 放置后指向 | 游戏层 | 是 |
| `SelectorTemplates.Slot` | 槽位 | 游戏层 | 是 |
| `SelectorTemplates.TargetPoint` | 目标点 | 游戏层 | 是 |
| `SelectorTemplates.Hand` | 手牌 | 游戏层 | 是 |
| `SelectorTemplates.Mulligan` | 换牌 | 游戏层 | 是（初始流程） |
| `SelectorTemplates.Option` | 选项 | 游戏层 | 是 |
| `SelectorTemplates.CardPickerList` | 列表选卡 | 游戏层 | 是 |
| `SelectorTemplates.CardPickerSet` | 集合选卡 | 游戏层 | 是 |
| `ReferenceSetParameter` | 候选引用集＋数量＋域校验＋tag | 游戏层 | 是（`BeginTargetSelectionAsync` 内部） |
| `TargeterManager.RunAsync(...)` | 一次交互请求的流程承载 | 游戏层 | 否（内部） |
| `TargeterResult`（Ok/Cancelled/Failed） | 三态结果 | 游戏层 | 是（读结果） |

## 4. 词条

### 4.1 词条标识（`KeywordIds.All`，19 条）

| 常量 | 字面值 | 常量 | 字面值 |
| --- | --- | --- | --- |
| `Blitz` | 闪击 | `Fury` | 奋战 |
| `SmokeScreen` | 烟幕 | `Ambush` | 伏击 |
| `Suppressed` | 被压制 | `Inhibited` | 被抑制 |
| `Mobilize` | 动员 | `Pincer` | 钳击 |
| `Forecast` | 预报 | `Immune` | 免疫 |
| `Armor` | 重甲 | `Intelligence` | 情报 |
| `CannotBeSuppressed` | 无法被压制 | `CannotBeInhibited` | 无法被抑制 |
| `Deathrattle` | 亡计 | `Veteran` | 老兵 |
| `Covert` | 隐蔽 | `Guard` | 守护 |
| `Shock` | 冲击 | — | — |

### 4.2 词条程序四表（数量以常量数组为准）

| 表 | 常量 | 条数 | 用途 |
| --- | --- | --- | --- |
| 规则点 | `KeywordProgram.RuleNames` | 11 | 改写判定链的挂点（move/attack/combat/deploy/targeting） |
| 事件 | `KeywordProgram.EventNames` | 17 | 词条程序可响应的信号名（含 `unit.attack.before` 等） |
| 操作 | `KeywordProgram.Operations` | **32** | 词条程序可执行的动作白名单 |
| 目标 | `KeywordProgram.Targets` | 14 | 操作的作用对象选择式（self/event/linked/…/choose.*） |
| 主体 | `KeywordProgram.Subjects` | 6 | 事件归属过滤（self/friendly/enemy/any/owner/opponent） |

> 口径校正：需求 R7 记「操作 33 条」，`KeywordProgram.Operations` 实为 **32**（`if` 计入；无第 33 项）。

### 4.3 词条操作（32 条）

| 操作 | 含义 | 数据侧可达 | 操作 | 含义 | 数据侧可达 |
| --- | --- | --- | --- | --- | --- |
| `state.set` | 置行为态 | 是 | `state.add` | 叠加行为态 | 是 |
| `capture` | 记录数值 | 是 | `link` | 关联单位 | 是 |
| `unlink` | 解除关联 | 是 | `move.set` | 置可移动 | 是 |
| `attack.set` | 置可攻击 | 是 | `attack` | 效果发起攻击 | 是 |
| `replace` | 替换入场 | 是 | `retreat` | 撤退到支援线 | 是 |
| `damage` | 造成伤害 | 是 | `buff` | 攻防修饰 | 是 |
| `stat.add` | 数值增减 | 是 | `stat.set` | 数值设定 | 是 |
| `heal` | 修复防御 | 是 | `kill` | 消灭 | 是 |
| `keyword.grant` | 授予词条 | 是 | `keyword.remove` | 撤销词条 | 是 |
| `emit` | 发信号 | 是 | `death.cancel` | 取消死亡 | 是 |
| `health.set` | 置剩余防御 | 是 | `copy.hand` | 复制并入手 | 是 |
| `return.hand` | 回手 | 是 | `create.hand` | 新建入手 | 是 |
| `develop` | 部署 | 是 | `move` | 移动 | 是 |
| `draw` | 抽牌 | 是 | `points` | 点数增减 | 是 |
| `attach.prefab` | 挂预制体效果 | 是 | `targeting.forbid` | 禁被指向 | 是 |
| `targeting.allow` | 解除禁指向 | 是 | `if` | 条件分支 | 是 |

## 5. 来源两面（视图属性）

| 属性 | 语义 | 值来源 | 可空 | 数据侧可达 |
| --- | --- | --- | --- | --- |
| `view.Source` | **事件引发者**（这条事件是谁引发的） | 信号载荷键 `Source` | 是 | 是（csx 直读） |
| `view.TriggerCard` | **当前 handler 的触发者**（链上最后者胜） | 执行帧（注册者派生） | 是 | 是（csx 直读） |
| `ctx.TriggerCard` | 同上（C# handler 读取面） | 执行帧 | 是 | 否（仅 C#） |

> 再发信号：`EmitXxx(..., source: view.TriggerCard)`——把「当前触发者」继续往下传；判「敌方所致」读 `view.Source`。
