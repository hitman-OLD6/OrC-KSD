# Agent 操作手册：无头沙盒（起服务 → 打一局 → 做卡）

> **这份文档是自包含的**：读完这一篇，你就能起服务、接 MCP、打完一局、并在**对局进行中**写出新卡新效果。
> 不要求你读其它文档；需要深挖原理时，末尾给了入口。
>
> 适用对象：**AI agent**（人和脚本同样适用）。所有步骤都是"调用 → 期望返回 → 失败怎么办"。

---

## 0. 三十秒版

```text
① 双击 start-sandbox-server.bat（保持窗口）→ 等一行 {"ready":true,...}
② 启动/重启宿主（WorkBuddy）→ 工具表出现 orc-seat1 与 orc-orch
③ mulligan.done → 循环 { seat.board → play.unit / command.attack → match.end_turn }
④ 做卡：card.write → card.load → sandbox.instantiate → play.unit → 读事件流断言
```

---

## 1. 三个必须先知道的事实

| # | 事实 | 后果 |
|---|---|---|
| 1 | 桥在**宿主启动时**拉起，立刻探测端口；端口没人听就退出，工具永不注册 | **必须先起服务端，再起/重启宿主** |
| 2 | 调试写类工具（`card.write`/`card.load`/`effect.write`/`sandbox.*`）只对「回环 + **未绑定座位**」的连接可见可用 | 必须配**两条桥**：一条绑座打牌，一条不绑座写卡 |
| 3 | 宿主工具表是**启动时的快照**（`tools.listChanged=false`） | 改完 `mcp.json` / 重编服务端，都要**重启宿主** |

这三条是硬约束，绕不过去。踩了会表现为"工具不存在"或"工具全部 Not connected"，而不是报权限错误。

---

## 2. 起服务器

### 2.1 一条命令

```bat
D:\OrC-KSD\start-sandbox-server.bat
```

窗口会保持占用（**关窗即停服**）。等它打出这一行再继续：

```json
{"ready":true,"port":51888,"headless":true,"mode":"sandbox","runDir":"...","agentSeat":1,"agentTokenPath":"...\agent.token",...}
```

`ready` 行出现 = 实例已在监听，但**对局还没开**——headless 会一直等 agent 入座（默认 1800 秒），入座瞬间自动开局。

其它两个入口：

```bat
start-sandbox-server.bat --check    :: 只看环境（dotnet/dll/卡包/端口/mcp.json），不启动
start-sandbox-server.bat --detach   :: 后台起（依赖 python；沙盒环境里可能被回收）
```

`start-sandbox-server.bat` 是**给人双击的启动器**（前台窗口 = 服务在跑，关窗即停）。
后台实例、停、查、重启走另一个脚本：

```text
python sandbox-server-ctl.py start            # 后台起（默认子命令；--foreground 常驻前台）
python sandbox-server-ctl.py status           # 端口/pid/就绪状态
python sandbox-server-ctl.py stop             # 按 pid 停（校验 dotnet 映像，绝不盲杀）
python sandbox-server-ctl.py restart
```

> ⚠ `start` 的 detached 启动在 **Job Object 内不可靠**（agent 沙盒 / 部分编辑器 / CI 会拒绝
> `CREATE_BREAKAWAY_FROM_JOB`，实例随父任务回收）。这类环境用 `--foreground`，或让人双击 bat。
> 反过来说，`stop` / `status` / `restart` 在**任何**环境都该用这个脚本——bat 只能关窗，管不了后台实例。

### 2.2 它到底跑的是什么

```text
dotnet src\Orc.Server\bin\Debug\net8.0\Orc.Server.dll
  --headless --mode sandbox --allow-debug-writes
  --packs samples\card-pack-demo
  --seat0 ai --seat1 agent
  --port 51888 --run-dir outputs\agent-match\mcp-run
  --deck-rules lenient --wait-agent 1800
```

| 参数 | 含义 | 改了会怎样 |
|---|---|---|
| `--mode sandbox --allow-debug-writes` | 开沙盒 + 注册写类工具 | 去掉就只剩 read-only 工具面 |
| `--packs <dir[;dir]>` | 启动期卡池（可多目录，`;` 分隔） | 影响双方卡组生成与已注册定义 |
| `--seat0 ai / --seat1 agent` | 谁占座 | agent 座位号变了，桥的 token 仍按座位发 |
| `--port 51888` | 监听端口 | **必须与 `mcp.json` 一致** |
| `--run-dir` | 日志/令牌/沙盒根 | 沙盒写根 = `<run-dir>/sandbox/` |
| `--deck-rules lenient` | 卡组不足 39 张时允许重复填充 | `strict` 下小卡包会直接开不了局 |
| `--wait-agent 1800` | 等 agent 入座上限（秒） | 超时进程退出，退出码 3 |

退出码：`0` 正常终局 / `1` 启动失败 / `2` 运行时异常 / `3` 等 agent 超时。

### 2.3 产物在哪

```
<run-dir>/
  agent.token            座位令牌（每次实例重启都会变）
  events.jsonl           全知事件流，每行一整段，sequence 从 1 起
  calls.jsonl            工具调用流水（谁/什么工具/成败码/耗时/段区间）
  summary.json           终局摘要（winner/turns/endReason/seed/errorCount/failedCalls）
  sandbox/pack/          沙盒卡包（**你写卡就落这里**）
    cards/*.card.json
    effects/*.prefab.json
```

`--exit-on-end` 是默认行为：对局结束 → 写 summary → 进程退出。想连着测多局，就重新起实例。

---

## 3. 接 MCP

`~/.workbuddy/mcp.json` 必须是**两条**：

```json
{
  "mcpServers": {
    "orc-seat1": {
      "command": "dotnet",
      "args": [
        "D:/OrC-KSD/src/Orc.Mcp.Bridge/bin/Debug/net8.0/Orc.Mcp.Bridge.dll",
        "--connect", "127.0.0.1:51888",
        "--token-file", "D:/OrC-KSD/outputs/agent-match/mcp-run/agent.token"
      ]
    },
    "orc-orch": {
      "command": "dotnet",
      "args": [
        "D:/OrC-KSD/src/Orc.Mcp.Bridge/bin/Debug/net8.0/Orc.Mcp.Bridge.dll",
        "--connect", "127.0.0.1:51888",
        "--no-resume"
      ]
    }
  }
}
```

| 桥 | 身份 | 能用什么 |
|---|---|---|
| `orc-seat1` | `--token-file` → `room.resume` **绑座** | `play.*` `command.*` `match.*` `mulligan.*` `target.*` `seat.*` |
| `orc-orch` | `--no-resume` **不绑座** | `card.*` `effect.write` `sandbox.*` `asset.validate` `diag.*` `file.*` |

两条桥连**同一个实例**，互不抢占。
硬性注意：路径必须绝对；只用 `dotnet <dll>`（`dotnet run` 会把构建噪声写进 stdout，MCP 客户端直接崩）；服务端必须在宿主**之外**的窗口里跑。

**症状对照**：能打牌但工具表里没有 `card.write` / `sandbox.instantiate` ⇒ 你只配了 `orc-seat1`。

---

## 4. 打一局

### 4.1 决策循环（照抄）

```text
loop:
  board = orc-seat1.seat.board          # 1–2 KB 精简面板，别用 observe.view 做常规决策
  if board.phase == "over": break
  if board.phase == "mulligan": mulligan.done(owner=1); continue
  if board.yourTurn == false: 等 200ms; continue

  # 出牌：只用 emptySupportSlots 里的槽（HQ 已排除）
  for uid in board.playable:
      if uid 在 board.hand 且是单位:
          play.unit(ref=uid, owner=1, slot=board.emptySupportSlots[0])
          若返回 targeting → target.submit(sessionId=details.pending.sessionId, picks=[候选 index])

  # 攻击：只打划算的（预览已含 dies 判定）
  for pv in board.attackPreviews where pv.defenderDies and not pv.attackerDies:
      command.attack(ref=pv.attackerUid) → target.submit(picks=[该 defender 的 index])

  match.end_turn
```

### 4.2 `seat.board` 字段

```json
{
  "turn": 4, "phase": "play", "activeSide": "self", "yourTurn": true,
  "kredits": {"self": 2, "max": 2},
  "hq": {"self": 20, "enemy": 20},
  "handCount": 9, "armedCounterCount": 0,
  "hand":    [{"uid":"…","name":"掷弹兵","cost":1,"atk":2,"hp":3,"handIndex":0,"zone":"hand","isCounterArmed":false}],
  "selfLine":[{…,"slot":3,"zone":"support"}],
  "enemyLine":[{…,"slot":0,"zone":"frontline"}],
  "emptySupportSlots": [0,1], "emptyFrontSlots": [0,1,2,3,4],
  "playable": ["uid…"],
  "attackPreviews": [{"attackerUid":"…","defenderUid":"…","damageToDefender":3,"damageToAttacker":2,"defenderDies":true,"attackerDies":false}],
  "moves": [{"uid":"…","toZone":"front","slotIndex":3}],
  "pendingSessionId": null
}
```

要点（都是实测踩过的）：

- `emptySupportSlots` 已排除 HQ 槽（通常 2 号）——别按 `0..4` 硬填。
- **手牌序号是 `handIndex`，战线序号是 `slot`**，两个域不能混。
- `attackPreviews.dies` 是**裸伤害**判定（不含词条/效果改写），只用来筛"这击划不划算"。
- 攻击候选**只有一部分敌人**（对面支援线的单位通常打不到）——预览里没有的目标，`command.attack` 也不会给你选。
- `pendingSessionId` 非空才需要 `target.submit`；动作结束会自动清空。
- `phase: "over"` 即终局，去读 `summary.json`。

### 4.3 规则速记

1. 支援线 5 槽，**HQ 占其一**；出牌只能用空支援槽。
2. 单位每回合**只能攻击一次**，且**当回合刚出场不能攻击**。
3. **移动后不能再攻击**（同回合）。
4. 出牌与攻击共用点数池；攻击扣单位的行动花费。
5. 牌库抽空会让 `end_turn` 返回 `not-ready`（`lenient` 卡组一般撞不上）。
6. 想提前收尾：`match.concede(owner=1)` → 立刻 `Ended`，`endReason=Concede`。

---

## 5. 做一张新卡（对局进行中）

### 5.1 五步管道

```text
card.write(path, json)          写盘即校验：JSON 非法 / components 为空 / 缺 typeCategory ⇒ 拒绝落盘
  ↓
card.load(files?[])             注册进**当前对局**（只追加不覆盖）；顺带装载 pack/effects/*.prefab.json
  ↓
sandbox.instantiate(definitionId, owner, zone)   造一张到手上/场上，返回 ref
  ↓
play.unit(ref, owner, slot)     正常打出（走费用/相位/手牌校验）
  ↓
断言：读 events.jsonl 末尾段，看 card.played / unit.deployed / card.drawn / card.damaged
```

要点：

- **改一张已注册的卡必须换新 id**（`card.load` 只追加）；要复用 id 就 `sandbox.dispose` + 重建对局。
- **AI 卡组不会自动包含新卡**。新卡只有两条路出场：`sandbox.instantiate`，或者写进 `--packs` 的卡包后重启实例（同种子可复现）。
- 沙盒包（`<run-dir>/sandbox/pack/`）**不需要 `manifest.json`**；`--packs` 指的正式卡包才需要。
- 同一卡目录里**两张卡引用同一个效果 id 会软隔离**（warning，不影响首次注册生效）；要共享效果就跨卡目录放置。

### 5.2 模板 A：单位卡 + 内联效果（部署抽 2）

`card.write` 的 `path` = `pack/cards/lab.drummer.card.json`，`json` = 下面这段的字符串形式：

```json
{
  "schemaVersion": 1,
  "id": "lab.drummer",
  "name": "沙盒鼓手",
  "components": [
    { "component": "typeCategory", "type": ["infantry"] },
    { "component": "factionCost", "faction": "Germany", "kredits": 1 },
    { "component": "battleStats", "operationCost": 1, "attack": 2, "defense": 2 },
    { "component": "tagData", "rarity": "Standard" },
    {
      "component": "effects",
      "inline": [
        {
          "schemaVersion": 2,
          "root": {
            "id": "lab.drumbeat",
            "version": 1,
            "mainTrigger": {
              "id": "t.main",
              "stableKey": "沙盒实验·鼓点（部署抽两张）",
              "kind": "passive",
              "viewType": "Orc.Cards.CardEventView",
              "hooks": ["unit.deployed"],
              "events": [{
                "id": "onUnitDeployed",
                "entry": "HandleAsync",
                "csx": "Func<Orc.Cards.CardEventView, Context, CancellationToken, Task> HandleAsync = async (view, ctx, ct) =>\n{\n    if (view.Unit is not Orc.Cards.Card deployed || !object.ReferenceEquals(deployed, view.Host))\n    {\n        return;\n    }\n\n    var runtime = Orc.Game.Effects.EffectRuntime.ResolveFor(deployed);\n    if (runtime is null)\n    {\n        return;\n    }\n\n    await runtime.DrawAsync(deployed, 2, ct);\n};\n",
                "version": 1
              }],
              "version": 1
            },
            "otherTriggers": [],
            "wrappers": [],
            "injects": []
          }
        }
      ]
    }
  ]
}
```

### 5.3 模板 B：效果库 prefab（部署抽 1 + 敌单位 1 伤）

`effect.write` 的 `path` = `pack/effects/lab.overwatch.prefab.json`：

```json
{
  "schemaVersion": 2,
  "root": {
    "id": "lab.overwatch",
    "version": 1,
    "mainTrigger": {
      "id": "t.main",
      "stableKey": "沙盒实验·监军（部署抽牌＋压血）",
      "kind": "passive",
      "viewType": "Orc.Cards.CardEventView",
      "hooks": ["unit.deployed"],
      "events": [{
        "id": "onUnitDeployed",
        "entry": "HandleAsync",
        "csx": "Func<Orc.Cards.CardEventView, Context, CancellationToken, Task> HandleAsync = async (view, ctx, ct) =>\n{\n    if (view.Unit is not Orc.Cards.Card deployed || !object.ReferenceEquals(deployed, view.Host))\n    {\n        return;\n    }\n\n    var runtime = Orc.Game.Effects.EffectRuntime.ResolveFor(deployed);\n    if (runtime is null)\n    {\n        return;\n    }\n\n    await runtime.DrawAsync(deployed, 1, ct);\n\n    var targets = await runtime.SelectAsync(deployed, new Orc.Game.Effects.EffectSelector(\"one\", \"enemy\"));\n    foreach (var target in targets)\n    {\n        await runtime.DamageAsync(target, 1, deployed, ct);\n    }\n};\n",
        "version": 1
      }],
      "version": 1
    },
    "otherTriggers": [],
    "wrappers": [],
    "injects": []
  }
}
```

引用它的卡只需把 `effects` 组件换成：

```json
{ "component": "effects", "prefabs": ["lab.overwatch"] }
```

> `schemaVersion` 写 2 可用（本项目 DIY 样例用 3，同样被接受）。

### 5.4 模板 C：指令卡（打出时生效）

```json
{ "component": "typeCategory", "type": ["order"] },
{ "component": "factionCost", "faction": "Germany", "kredits": 1 },
{ "component": "battleStats", "operationCost": 0, "attack": 0, "defense": 0 },
{ "component": "tagData", "rarity": "Standard" },
{ "component": "effects", "inline": [ /* 同结构，hooks: ["card.played"]，csx 里用 view.Card 而非 view.Unit */ ] }
```

指令卡**必须带 effects**（引擎的 D40 约束），空效果也要写一个 `Task.CompletedTask` 的 handler。
csx 取值用 `view.Card`，过滤 `!ReferenceEquals(played, view.Host)`。

打出用 `play.command(ref)`（只需 `ref`，玩家由会话座位决定，没有 `owner` 参数）；它走完整动作链，费用/相位/在手牌都得满足。

### 5.5 模板 D：反制卡（拦截指令）

```json
{ "component": "typeCategory", "type": ["countermeasure"] },
{ "component": "factionCost", "faction": "Germany", "kredits": 1 },
{ "component": "battleStats", "operationCost": 0, "attack": 0, "defense": 0 },
{ "component": "tagData", "rarity": "Standard" },
{
  "component": "effects",
  "inline": [{
    "schemaVersion": 2,
    "root": {
      "id": "lab.veto",
      "version": 1,
      "mainTrigger": {
        "id": "t.main",
        "stableKey": "沙盒实验·否决（硬拦截）",
        "kind": "passive",
        "viewType": "Orc.Game.Triggers.CardTriggerView",
        "hooks": ["card.played"],
        "events": [{
          "id": "onPlayed",
          "entry": "HandleAsync",
          "csx": "Func<Orc.Game.Triggers.CardTriggerView, Context, CancellationToken, Task> HandleAsync = (view, ctx, ct) => { ctx.Stop(); return Task.CompletedTask; };",
          "version": 1
        }],
        "version": 1
      },
      "otherTriggers": [],
      "wrappers": [],
      "injects": []
    }
  }]
}
```

反制卡三条硬规则（踩了就整条效果不装配，且只在 Error 留痕里体现）：

1. `viewType` **必须是** `Orc.Game.Triggers.CardTriggerView`（写成 `CardEventView` 会被判类型不符而隔离）；
2. **不能声明 `otherTriggers` / `wrappers`**（反制只承认 handler 面）；
3. 命中语义：`ctx.Stop()`＝拦截但照常扣费离手；`ctx.Interrupt()`＝强反制（牌留手、不扣费）；都不调＝未命中，反制保持激活。

用法：`play.counter(ref)` 激活（扣费、牌留手且标 `isCounterArmed`）；轮到对手打指令时观察 `card.played` 是否为 0——
为 0 说明拦住了（指令仍扣费离手、反制进弃置）。`play.counter` 同样只要 `ref`。

---

## 6. 数据格式速查

### 6.1 卡组件（顺序建议照抄，重复组件会被拒）

| 组件 | 字段 | 说明 |
|---|---|---|
| `typeCategory` | `type: []` | 单位：`["infantry"]`（另有 `tank`/`artillery`/`fighter`/`bomber`，可多个）；指令：`["order"]`；反制：`["countermeasure"]`。类别词**必须独占**列表 |
| `factionCost` | `faction`, `kredits` | 部署费 |
| `battleStats` | `operationCost`, `attack`, `defense` | 指令/反制卡填全 0 |
| `tagData` | `rarity`, `tags[]` | 例：`Standard` |
| `keywords` | `attributes[]` | 英文标识；未映射项只留痕不报错 |
| `effects` | `inline[]` 和/或 `prefabs[]` | 内联快照 / 效果库 id 列表 |

写入校验（`card.write`）：`schemaVersion` 必须为 1；`id`/`name` 必填；**`components` 必须非空且含 `typeCategory`**。

### 6.2 效果快照

```text
{ schemaVersion: 2 | 3,
  root: {
    id, version, oneShot?,
    mainTrigger: { id, stableKey, kind: "passive", viewType, hooks: [...], events: [{ id, entry, csx, version }], version },
    otherTriggers: [], wrappers: [], injects: []
  } }
```

- `viewType`：卡事件用 `Orc.Cards.CardEventView`；反制用 `Orc.Game.Triggers.CardTriggerView`。
- `hooks` 常用：`unit.deployed`、`unit.joined`、`card.played`、`card.drawn`、`card.discarded`、`card.died`、`damage.apply`。
- `csx` 是**入口委托定义**（不是裸语句）：必须声明 `HandleAsync` 这个名字。

### 6.3 csx 里能用的动作面

```csharp
var runtime = Orc.Game.Effects.EffectRuntime.ResolveFor(host);   // 取效果运行时，null 就直接 return
await runtime.DrawAsync(host, 1, ct);                            // 抽牌（含满手爆牌裁决）
var targets = await runtime.SelectAsync(host, new Orc.Game.Effects.EffectSelector("one", "enemy"));
foreach (var t in targets) await runtime.DamageAsync(t, 2, host, ct);
await runtime.BuffAsync(target, 0, 5, Orc.Game.Effects.EffectDuration.Permanent, ct, host);
await runtime.FetchFromDeckAsync(host, ct);                      // 从卡组定向抽取本卡
```

选择器第一个参数是数量语义（`one` / `random` / `all` 等），第二个是阵营（`enemy` / `self` / …），其余参数可省。

---

## 7. 调试原语（`orc-orch`，摆局面用）

语义等价于"一张卡做同样的事"：该发的信号全发、该触发的监听全触发，**只跳过校验层**（费用/回合/相位/手牌/行动次数）。

| 工具 | 参数 | 语义 |
|---|---|---|
| `sandbox.instantiate` | `definitionId, owner, zone(hand/deck_top/support/frontline), slot?` | 造卡并落到指定去向（可精确指定槽位） |
| `sandbox.unitize` | `ref, line(support/frontline), slot` | 把单位卡单位化到精确槽，发 `card.placed`+`unit.joined` |
| `sandbox.move` | `ref, line, slot` | 挪到另一空槽，不扣费、不消耗行动 |
| `sandbox.remove` | `ref` | 手牌→弃置链，在场→死亡链（结算亡计） |
| `sandbox.points` | `owner, points` | 点数设为**绝对值**（可下调；超槽数返回 warning） |
| `sandbox.draw` | `owner, count?` | 抽 N 张（`count` 缺省 1；满手＝爆牌，算成功） |

**保留的硬门槛**（触到即失败、零状态变更）：阵线容量 5、槽位被占、单位已单位化、实例唯一归属、已销毁实例不可放置。

---

## 8. 断言与验收

写完卡跑完一局，按这四条自查：

```bash
# ① 终局摘要非空
cat <run-dir>/summary.json          # winner / turns / endReason / seed

# ② 事件流段号连续、无断号
python -c "
import json
s=[json.loads(l)['sequence'] for l in open(r'<run-dir>/events.jsonl',encoding='utf-8') if l.strip()]
print(len(s), s[0], s[-1], s==list(range(1,len(s)+1)))"

# ③ 调用流水没有系统性失败
python -c "
import json,collections
c=collections.Counter()
for l in open(r'<run-dir>/calls.jsonl',encoding='utf-8'):
    if l.strip():
        o=json.loads(l)
        if not o.get('ok'): c[(o['tool'],o['code'])]+=1
print(dict(c))"
```

④ 效果是否真的触发，**只看事件流**（不要看返回码，也不要看 UI）：

```bash
python -c "
import json
seg=json.loads(open(r'<run-dir>/events.jsonl',encoding='utf-8').read().splitlines()[-1])
def walk(es,d=0):
    for e in es:
        if e.get('kind')=='update' and str(e.get('message','')).startswith(('card.','unit.')):
            print(' '*d, e['message'])
        walk(e.get('children') or [], d+1)
walk(seg.get('entries') or [])"
```

一次成功的"新卡部署效果"长这样：

```text
card.played → card.placed → unit.deployed → card.drawn → card.hand.add → card.damaged → unit.damage.dealt
```

如果只有 `card.played / card.placed / unit.deployed` 就没了——**效果没装配**，查 §9 前三行。

---

## 9. 排障（全是实测踩过的）

| 现象 | 原因 | 处理 |
|---|---|---|
| 工具表里没有 `orc-seat1` | 宿主先于服务端启动，桥探测失败即退出 | 先起服务端 → 重启宿主 |
| 能打牌，但没有 `card.write` / `sandbox.instantiate` | 只配了绑座的 `orc-seat1` | 加 `orc-orch`（`--no-resume`）→ 重启宿主 |
| `seat.board` 面板全空 | 服务端 `observe.view` 在 Authoring 下只回摘要 | 已修（`panel:true`）；确认服务端与桥 dll 同批编译 |
| 新卡打出后**毫无反应、无报错** | ① 效果 `viewType` 写错；② 反制卡声明了 `otherTriggers`/`wrappers`；③ 引用型效果但 `card.load` 没装载 `effects/` | 对齐 §5.4/§5.5；`card.load` 返回里看 `effectsLoaded`/`effectsFailures` |
| `card.load` 返回 `alreadyRegistered` | 同 id 已在当前对局注册过 | 换新 id（`card.load` 只追加不覆盖） |
| `card.write` 说 components 为空 / 缺 typeCategory | 分发级结构校验 | 补齐组件；空壳卡进不了对局 |
| 写出来的卡只剩 `id`/`name` | 旧版本在无对局时组件表未注册，规范化写出丢组件 | 已修（写前幂等注册组件表） |
| `play.unit` 返回 `TargetSlotOccupied` | 槽被 HQ 或已有单位占用 | 只用 `seat.board.emptySupportSlots` |
| `command.attack` 后攻击报 `NoActionAvailable` | 移动与攻击同回合互斥 | 先攻击，或下回合再攻击 |
| `end_turn` 返回 `not-ready`（卡组名单为空） | 牌库抽空 | 换大卡包；确认 `--deck-rules lenient` |
| 桥报 `unauthorized` | 实例重启过，`agent.token` 变了 | 重启宿主（桥会重读 token） |
| 桥报 `not-connected` 且服务端明明在跑 | 服务端重启时桥已退出，宿主不会自动重连 | 只能重启宿主 |

**代价提醒**：重启服务端 = 两条桥全掉线 = 必须重启宿主。所以服务端侧改动要攒成一批再做。

---

## 10. 深入阅读（本文不展开）

| 想知道 | 看 |
|---|---|
| 沙盒环境全貌、README 之外的参数细节 | `docs/无头沙盒-测试环境/开发指南.md` |
| 效果原理、触发器、wrapper、判定器 | `docs/卡牌DIY开发文档/01~03` |
| 目标选择 targeter、动作面 API 全集 | `docs/卡牌DIY开发文档/04`、`05` |
| 词条与预制效果、能力边界 | `docs/卡牌DIY开发文档/07`、`08` |
| 现成的 DIY 卡包（5 张卡 + 6 个效果库 prefab） | `samples/card-pack-diy/`（含 README 卡表） |
| 反制/指令/单位卡的可运行最小样例 | `samples/card-pack-ai/cards/`、`samples/card-pack-demo/cards/` |
