# kards-diy 游戏流程分析报告

> 用途：把 `D:\kards-diy`（KARDS DIY，Electron + 纯 JS）的**完整对局流程**盘清，作为 OrC 引擎实现 KARDS 游戏层时的流程对照基线。
> 来源：`D:\kards-diy\resources\app\game\`（`js/` 引擎源码 + `data/` 规则与规范）与 `resources\app\`（Electron 宿主）。
> 对照文档：`docs/初始设计文档.md`、`docs/kards-diy-可参考语料报告.md`、`docs/kards-diy-可hook点清单报告.md`、`docs/orc-用户输入点分析报告.md`。
> 日期：2026-10-05。
> 行号说明：均引自分析时版本（`engine.js` 实测 ~2850 行、`ui.js` ~6813 行），行号以现有文件为准；若后续文件变动，**以函数名为准**。

---

## 0. 阅读范围与语料地图

| 文件 | 内容 | 本报告用途 |
|---|---|---|
| `game/js/engine.js` | 规则核心：事件分发、战斗、回合、战场、HQ、状态对象 | **流程主干** |
| `game/js/effects.js` | 效果解释器（`OPS`/`CONDS` 注册表）、触发器执行 | 流程中的效果结算 |
| `game/js/primitives.js` / `primitives-extra.js` | 中文句式 → 原语词表 | 出牌时会用到的目标/条件 |
| `game/js/compiler.js` / `parser-v2.js` | 卡面文本 → DSL | 触发前缀（部署/亡计…）的识别 |
| `game/js/ui.js` | 交互与流程编排（单机驱动层） | **UI 侧流程控制** |
| `game/js/ai.js` | 启发式 AI（与玩家共用同一套引擎 API） | AI 回合驱动 |
| `game/js/net.js` / `netsync.js` | WebSocket 传输 + 确定性锁步 | 联机流程模型 |
| `game/data/RULES_REF.md` | KARDS 官方规则（带可信度与来源） | 规则语义基准 |
| `game/data/EFFECT_DSL.md` | DSL 规范 v1（触发器语义/op 清单/选择器） | 触发时机与效果语义 |
| `game/data/SCHEMA.md` | 卡图转写 schema、词条 id 表 | 数据模型 |
| `game/data/invariants.json` | 编译不变式 + 27 项监听型触发器清单 | 触发面权威清单 |
| `game/data/_report.md` / `_effects_report.md` / `_paiq_report.md` / `_landing_blockers.md` | 卡池/效果/导入报告 | 规模与现状 |

---

## 1. 驱动模型：谁在推进流程（先看结论）

| 模式 | 驱动者 | 动作路径 | 关键位置 |
|---|---|---|---|
| **单机（对 AI）** | `ui.js` | 人类点击 → `mp*` 包装 → 直调 `engine.js` 的 `playCard/attack/move/endTurn`；人类结束回合后调 `aiTurn()` 让 AI 走一手 | `ui.js:3478 startGame`、`ui.js:3552 aiTurn`、`ui.js:3561 runAiTurnOnce` → `ai.js:84 AI.takeTurn` |
| **联机（LAN/P2P）** | 双方同引擎锁步 | 两边跑**同一份引擎 + 同种子 + 同动作序列**做确定性锁步；`mp*` 包装把「动作 + chooser 答案」录制并中继给对方回放 | `ui.js:3221/3229/3237/3257 mpPlay/mpMove/mpAttack/mpEndTurn`；`netsync.js` 指纹/回放；联机时禁用 AI（`ui.js:3554`） |
| **托管（AI 对 AI / 单人）** | `ai.js` | AI 与玩家**共用同一套引擎 API**，所有选择经 `chooser` 回调 | `ai.js:30 AI.chooser`、`ai.js:84 takeTurn` |

**确定性要点（联机锁步）**：引擎内所有随机都走 `state.rng`（seeded mulberry32）；唯一异步输入是 `chooser`（`KG.ask`）。因此重放只需 `(动作序列, chooser 答案序列)`。见 `netsync.js:1-22` 注释。

---

## 2. 对局初始化与开局

### 2.1 规则常量（`engine.js:12 RULES`）

| 常量 | 实现值 | 说明 |
|---|---|---|
| `hqHp` | 20 | 总部初始防御力 |
| `handP1` / `handP2` | 4 / 5 | 先手 / 后手起手张数 |
| `drawPerTurn` | 1 | 每回合抽牌 |
| `kreditCap` | 12 | 指挥点槽**自然**上限 |
| `kreditSlotHardCap` | 36 | 点槽**绝对**上限（卡牌可突破） |
| `deckSize` | 40 | 卡组张数 |
| `maxCopies` | 4 | 同名上限（另有稀有度配额 `KG.copyLimit`，`engine.js:436`） |
| `supportMax` / `frontlineMax` | **6 / 5** | 本实现支援线 6 格、前线 5 格（⚠ 与 `RULES_REF.md` 的「支援 4 单位＋总部」不同，见 §9） |
| `handMax` | 9 | 手牌上限，超出直接销毁 |
| `fatigue` | true | 空库抽牌＝疲劳伤害 |

### 2.2 建局链

| 步骤 | 函数 — 行号 | 语义 |
|---|---|---|
| 1 | `resetUidBase(seed)` — `engine.js:110` | 按种子归位 uid 计数器（锁步要求同种子逐字同 uid） |
| 2 | `KG.createGame(opts)` — `engine.js:445` | **建局总入口** |
| 3 | 生成 `state` — `engine.js:449` | 见 §8 状态对象 |
| 4 | `makePlayer` + `shuffleDeck` — `engine.js:467-468` | 双方玩家对象与牌库（`state.rng` 洗牌） |
| 5 | 起始牌 `startInHand / autoUse` — `engine.js:471-498` | 开局直接入手 / 直接使用的特殊卡 |
| 6 | 起手抽牌 — `engine.js:499-504` | `i===0 ? handP1 : handP2` 张逐个 `drawCard`，随后 `p.maxKredits = 0` |
| 7 | `phase='mulligan'` | 进入重调度阶段 |

- 玩家对象初值：`makePlayer` — `engine.js:375`，`hq=hqMax=20`、`kredits=0`、`maxKredits=0`。
- 洗牌：`shuffleDeck` — `engine.js:549`；抽牌：`drawCard` — `engine.js:583`（空库→疲劳递增伤害；手牌满→烧牌进弃牌堆）。
- **先后手**：`createGame` 恒 `active:0, turn:1`；由 `ui.js:3496-3505` 决定哪个牌库/名字放 index 0（index 0 = 先手）。

### 2.3 重调度（mulligan）

| 函数 — 行号 | 语义 |
|---|---|
| `KG.mulliganReplace(state,pi,handIndexes)` — `engine.js:513` | 换掉指定手牌（退回并重洗牌库，抽等量新牌）；仅 `phase==='mulligan'` 且该方未 done 时有效 |
| `KG.mulliganDone(state,pi)` — `engine.js:536` | 该方确认；**双方都确认后** `phase='play'` 并 `beginTurn(state,0,true)`（先手第 1 回合） |
| `confirmMulligan()` — `ui.js:5099` | UI 确认换牌 |
| AI | 默认不换牌，直接 `mulliganDone`（`ai.js:75-92`；单机路径 `ui.js:5119-5122`） |

- **指挥点/总部初始值**：总部 `hq=hqMax=20`；指挥点槽起始 0，先手首个 `beginTurn` 后变为 1（`maxKredits:0→1`，`engine.js:637`）。

---

## 3. 回合结构

相位只有两种：`'mulligan'`（准备）→ `'play'`（对局）。对局内是**回合交替**，没有独立的 draw/main 相位，全部内联在 `beginTurn` / `endTurn`。

### 3.1 回合开始 `beginTurn(state,idx,first)` — `engine.js:623`（同步）

按顺序执行：

| 序 | 步骤 | 行号 |
|---|---|---|
| 1 | 设 `state.active=idx`；非首回合 `state.turn++`（回合计数） | 625-626 |
| 2 | **点槽增长**：`maxKredits < kreditCap(12)` 则 +1；叠加 `nextTurnKreditSlots`；处理「无法增槽」状态 | 627-638 |
| 3 | AI 难度加成点、后手补偿 `secondPlayerBonusKredit`（默认 0） | 639-651 |
| 4 | **指挥点回满**：`p.kredits = p.maxKredits + nextTurnKredits` | 653-654 |
| 5 | 清回合计数（`__turnDrawCount`/`playedThisTurn`/`unitsDeployedThisTurn`…） | 655-662 |
| 6 | 上一回合挂起的非持久反制到期 `unsuspendCounter` | 666-670 |
| 7 | 总部临时词条/附魔到期 | 673-685 |
| 8 | 逐单位刷新：`actionsLeft`（fury/valor=2，否则 1）、`attackedThisTurn=0`、`canAct`、伏击重置、**召唤失调**（非 blitz/guerrilla 当回合单位不可动）、压制解除、**动员 +1/+1**、清临时 buff/debuff、充能计时 | 691-804 |
| 9 | 「回合结束消灭」的临时单位 | 807-809 |
| 10 | `recomputeAuras`（光环重算） | 810 |
| 11 | **抽牌**：`drawCard` 1 张（`drawOnFirstTurn` 亦抽） | 811-814 |
| 12 | `drawOnTurn` 定时抽取牌；`nextTurnDraws` 延迟额外抽牌 | 818-841 |
| 13 | 无条件 `chargeNow`（充能完毕） | 844-852 |
| 14 | 快照 `unitsLostLastTurn` | 858-864 |
| 15 | `pendingTurnStarts`（"下回合开始时"）结算 | 869-881 |
| 16 | **`runTrigger('turnStart')`** | 882 |
| 17 | `checkWin` | 883 |

### 3.2 回合结束 `KG.endTurn(state,chooser)` — `engine.js:887`（async）

| 序 | 步骤 | 行号 |
|---|---|---|
| 1 | 落雪天气：全体单位 1 点伤害 | 890-896 |
| 2 | **`runTrigger('turnEnd')`** | 899 |
| 3 | 本回合临时单位 `killAtTurnEnd` | 901-902 |
| 4 | 压制到期（`pinnedUntilTurn`）→ `unpinned` 事件 | 903-906 |
| 5 | `tempBuffs` / `dynMods` 到期 | 908-920 |
| 6 | `recomputeAuras` | 921 |
| 7 | **切换**：`beginTurn(state, 1-active, false)` | 923 |

> 指挥点「回合结束不清零、下回合回满覆盖」——X3 口径。

---

## 4. 行动阶段：可执行动作

所有动作合法性都要求 `phase==='play' && !over && state.active===pi`。

### 4.1 出牌 `KG.playCard(state,pi,handIdx,chooser,opts)` — `engine.js:1728`

| 项 | 函数 — 行号 |
|---|---|
| 合法性校验 | `KG.canPlayCard` — `engine.js:1528`（回合、花费、支援线满、目标存在；挂起反制不可再打） |
| 费用计算 | `instCost` — `engine.js:1479`（各种动态减费） |
| 目标枚举/校验 | `hasValidTargets` 1552 / `enumerateTargets` 1562 / `matchFilter` 1602 |
| **单位**分支 | 建 `makeUnit`、摆放 `insertUnitAt`、部署效果 `execEffects(...,'deploy')`、`applyDeployTraits`、`runTrigger('unitDeployed')` — 1728-1829；打断型反制拦截部署 1811-1815 |
| **反制卡**分支 | `inst.suspended=true` 挂起进 `p.counters`（不离开手牌）— 1746-1763 |
| **指令**分支 | `execEffects(...,'order')`，可被指令型反制打断 — 1830-1876 |
| 收尾 | `checkWin` / 强制结束回合 — 1878-1883 |
| UI 包装 | `mpPlay` — `ui.js:3221` |

### 4.2 移动

| 项 | 函数 — 行号 |
|---|---|
| 合法性 | `KG.canMove` — `engine.js:2436`（同线不可移、前线被对手占、支援线满、非游击不能自退回、攻击过不能再移动） |
| 结算 | `KG.move` — `engine.js:2498`（扣行动花费、`removeUnitFromBoard`+`insertUnitAt`、`movedToFrontline`/`unitMobilized` 事件、解烟幕、按 `keepsActionAfterMove` 决定是否保留行动） |
| 同线换位 | `KG.reposition` — `engine.js:2481`（纯摆放，不花行动） |
| 移动后可否再行动 | `KG.keepsActionAfterMove` — `engine.js:2162`（游击 / `canMoveAndAttack` / 坦克全局规则） |
| UI 包装 | `mpMove` — `ui.js:3229` |

### 4.3 攻击

| 项 | 函数 — 行号 |
|---|---|
| 合法性 | `KG.canAttack` — `engine.js:2069` |
| 结算 | `KG.attack` — `engine.js:2188` |
| 行动花费 | `effOpCost` — `engine.js:1887` |
| UI 包装 | `mpAttack` — `ui.js:3237`；`doAttack` — `ui.js:1421`；`resolveAttack` — `ui.js:4936` |

### 4.4 反制相关

| 项 | 函数 — 行号 |
|---|---|
| 取消挂起（返还埋设花费） | `KG.unsuspendCounter` — `engine.js:2583`；`KG.canUnsuspendCounter` — `engine.js:2620` |
| 挂起索引维护 | `findHandIndexByInst` 2547 / `syncSuspendedIndexes` 2555 / `removeSuspendedFromHand` 2566 |

### 4.5 结束回合

| 项 | 函数 — 行号 |
|---|---|
| 引擎 | `KG.endTurn` — `engine.js:887` |
| UI | `#endTurnBtn` 监听 — `ui.js:7493` → `mpEndTurn` — `ui.js:3257` |

---

## 5. 战斗结算流程

`KG.attack(state,pi,attackerUid,targetRef,chooser)` — `engine.js:2188`。前置（`canAttack` 通过后）：扣 `opCost`、`actionsLeft--`、`attackedThisTurn++`、解烟幕/强磁护盾 — 2193-2198。

### 5.1 攻击总部分支（2201-2215）

`runTrigger('attack')` → `runTrigger('unitAttacked')` → `revealCovert` → `attackPowerAgainst(state,a,'hq')` → `damageHQ(...,{combat:true})` → `runTrigger('afterAttackHQ')` → `afterAttack` → `checkWin`。

**总部不反击**（无攻击力）。

### 5.2 攻击单位分支（2218-2309）

1. `runTrigger('attack')` / `'unitAttacked'`（攻方侧） — 2219-2221
2. `revealCovert` 双方；任一方已死则返回 — 2221-2222
3. `runTrigger('attacked')` / `'friendlyAttacked'`（守方侧） — 2223-2224
4. 计算 `dmg = attackPowerAgainst(a,t)`、`back = attackPowerAgainst(t,a)`（**同时互伤**） — 2226-2228
5. 防御方静态效果 `collectDefensiveEffects` — 2230（`engine.js:2313`）
6. **攻方不吃反击**判定 `attackerNoRetal`：炮兵 / `noRetal` mod / `attackerNoRetalVsType`（2176）/ 冲击首次 / 轰炸机打非战斗机 / 防御方规则 — 2232-2244
7. **守方不反击** `defenderNoRetalStatic`：炮兵（默认否）/ 轰炸机对非战斗机 — 2247-2251
8. 隐蔽被攻击即揭露 — 2256-2259
9. 硬铝弹/`ignoreCombatKw` 无视目标防御词条 — 2260-2261
10. **伏击（ambush）**：仅当守方 `ambushReady && !attackerNoRetal && !defenderNoRetalStatic` 时，守方先手对攻方造成 `back` 伤害；若攻方死则结束 — 2273-2279
11. **磁反应装甲（magnetic）**：消耗一层免疫本次攻击 — 2280-2287
12. 开批处理 `state.__combatDamageBatch=[]`；`damageUnit(t,dmg,a,{combat:true,...})` — 2288-2289
13. 反击：`if(!attackerNoRetal && !defenderNoRetal && back>0) damageUnit(a,back,t,...)` — 2294-2302
14. **`flushCombatDamage`（同时结算：先记伤害/死亡、再统一触发亡计）** — `engine.js:1229` / 调用 2303
15. `emitUnitActed`（2324）→ `friendlySurvived` 事件 — 2304-2306
16. `afterAttack(a,t)` — 2307 / `engine.js:2403`
17. `checkWin` — 2308

### 5.3 伤害结算 `damageUnit(state,u,amount,source,opts)` — `engine.js:1124`

免疫/隐蔽/无视指令/无视单位效果/磁反应层 → 各种翻倍与加成 → 护甲 `armorAgainst`（1108）→ **偏转护盾**（致命伤害转移给对方随机单位，1174-1187）→ 扣防御 → `mobilize` 移除 → 派发 `friendlyDamaged`/`damaged`（1205-1206）→ `if(defense<=0) killUnit`（批处理期入 `__combatDeaths`）。

### 5.4 死亡处理与亡计 `killUnit(state,u,source)` — `engine.js:1249`

- 批处理期只登记 — 1251-1254
- 标 `dead`、离场 `removeUnitFromBoard`、累计击杀统计、日志 — 1255-1262
- 击杀方：`kills++`、打捞 `salvage`、`tryVeteran`、`afterKill`、`enemyKilled` — 1263-1285
- **亡计（deathrattle）**：`trigger==='death'` 效果；可被反制 `consumeInterrupt('deathrattle')` 抑制 — 1286-1299
- 进弃牌堆、`friendlyDeath`、`recomputeAuras`、`checkWin` — 1300-1304
- 离场邻居快照 `__adjacentUids`（亡计"对相邻单位"用） — 951-964

### 5.5 相关辅助

`attackPowerAgainst` 1930 / `armorOf`1086 `armorAgainst`1108 / `revealCovert`1220 / `damageHQ`1408 / `healUnit`1241 `healHQ`1431 / 效果解释器 `KG.runTrigger`2662、`KG.execEffects`2799 → `FX.exec`(`effects.js:3029`) / `consumeInterrupt`2644。

---

## 6. 胜负判定与对局结束

| 函数 — 行号 | 语义 |
|---|---|
| `checkWin(state)` — `engine.js:1437` | 双方 `hq<=0`：双爆=平局 `winner=-1`；某方 ≤0 → 另一方 `winner=0/1`；置 `over=true` |
| 调用点 | `damageHQ`1427、`killUnit`1304、`beginTurn`883、`attack`2214/2308、`playCard`1878 |
| `state.over` / `state.winner` | 终局标志（`engine.js:458-459`） |
| UI | `mpCheckOver` — `ui.js:3114` |

---

## 7. 关键状态对象

### 全局 `state`（`engine.js:449-464`）

```
seed, rng(mulberry32), pool, turn, active, players[], frontline[],
log[], winner, over, pending, version,
phase: 'mulligan' | 'play', mulligan: { done:[false,false] }
```

运行期附加：`weather`（落雪/狂风/薄雾）、`eventCounts/turnEventCounts`、`pendingTurnStarts`、`aiBonusKredit/aiBonusEvery/aiBonusFromTurn`、`burnPops`、`lastCombatDamage`、`forceEndTurn`、`__combatDamageBatch/__combatDeaths`（战斗批处理）、`__ctxUnit/__compositionCtx`（效果上下文）。

- `state.frontline` 是**双方共享的一条线**（混排）；`frontlineOf` 928 / `unitsInZone`934 / `allUnitsOf`940。

### 玩家对象（`makePlayer` — `engine.js:375-415`）

`idx, name, hq, hqMax, hqArmor, hqSlot, kredits, maxKredits, deck[], hand[], support[], discard[], removed[], counters[], hqEnchants[], cardMods{}, fatigue, hqKws{}, costMod, opCostModGlobal, playedThisTurn, unitsDeployedThisTurn, ordersPlayedThisTurn/Game, intelCardsPlayed, unitsLostThisGame, countersPlayedThisTurn/Game, nextTurnKredits, nextTurnKreditSlots, nextTurnDraws, noDrawNextTurn, deployToFrontline, intelSeen[], flags{}`

### 单位对象（`makeUnit` — `engine.js:245-288`）

`uid, cardId, def, name, owner, baseAttack, baseDefense, attack, defense, maxDefense, permAtk, permDef, auraAtk, auraDef, tempBuffs[], cardType, unitType, extraTypes[], kws{}, kwValues{}, mods{}, zone('support'|'frontline'), canAct, actionsLeft, summonedTurn, attackedThisTurn, pinnedTurns, pinImmuneTurns, ambushReady, shieldReady, magneticCharges, revealed, dead, kills, isVeteran, silenced, grantedEffects[], opCostPaidTurns[]`

### 手牌实例（`makeHandInst` — `engine.js:348`）

`{id, uid, costMod, opCostMod, mods, revealed, suspended, suspendedTurn, counterOnly, playEffects, ...}`；挂起反制条目形如 `{cardId, inst, effects, handIndexAtSet, suspended, paidCost, persistent}`（1749-1755, 1869-1870）。

### 视图/序列化

`KG.view(state,pi)` — 2821；`KG.serialize(state)` — 2843。

---

## 8. 单机全流程时序（总览）

1. `ui.startGame()`（`ui.js:3478` 起）→ `KG.createGame` → `phase='mulligan'`
2. `renderMulligan`（`ui.js:5083`）/ `confirmMulligan`（5099）；AI 在 `runAiTurnOnce`（`ui.js:3566-3569`）确认
3. 双方 done → `mulliganDone` → `beginTurn(state,0,true)` → 先手第 1 回合
4. 人类回合：`mpPlay/mpMove/mpAttack` →（点「结束回合」）`mpEndTurn` → `KG.endTurn` → `beginTurn(1,false)`
5. `ui.js:7506` 调 `aiTurn()` → `KG.ai.takeTurn`（出牌/攻击/移动循环，`ai.js:103-118`）→ 结束后交回人类
6. 任一方 `hq<=0` → `checkWin` 置 `over/winner`，UI 显示终局

---

## 9. 与 OrC 流程落点的对照（供实现参考）

| kards-diy 流程环节 | OrC 现状落点 | 备注 |
|---|---|---|
| 建局/初始化 | `Match.Match` + `Match.Initialize`（`Orc.Game/Match/Match.cs`） | 已有 |
| 洗切 | `Match.ShuffleDeckAsync` → 发 `deck.shuffled` | 已有 |
| 起手 / 抽牌 | `PlayerManager.LoadOpeningHand` / `DrawCard` | 已有 |
| **重调度 mulligan** | **缺**（无入口） | 需新增（见报告 3 缺口） |
| 回合开始/结束 | `TurnManager.StartFirstTurn`（internal）/ `EndTurn`；信号 `turn.start.*` / `turn.end.*` | 已有；相位细分需对齐 |
| 点槽成长/回满 | `ResourceManager.Settle` / `AddPoints` | 已有；曲线参数需对齐（X3） |
| 出牌 | `PlayManager.BeginUnitPrePlayAsync/PlayUnitAsync/PlayOrderAsync/UseCounterAsync` | 已有 |
| 移动/攻击 | `CommandManager.BeginCommandAsync`（唯一公开入口） | 已有；无独立 API |
| 战斗结算 | `Orc/Cards/BaseFlows.cs`（`AttackFlow.ExecuteAsync` / `DamageFlow.ResolveAsync`） | 已有；交战词条多数待接线 |
| 死亡/亡计 | `RetriggerSystem.RequestDeathrattleAsync` + `card.died` | 已有 |
| 视野/序列化 | `Orc/Output/*`（`EventSegment` / `EventStreamJson` / `SnapshotJson`） | 已有；与 `KG.view/serialize` 不同模型 |
| **认输** | **缺** | 需新增 |

> ⚠ **规则参数差异（必须先裁决再谈对拍）**：kards-diy 实现为支援 6 / 前线 5；`RULES_REF.md` 官方口径为支援 4 单位（含总部共 5 位）/ 前线 5。OrC 侧现行容量口径为 4/5/4。对拍前须先统一。
