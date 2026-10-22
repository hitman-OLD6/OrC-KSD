# Agent 零代码上手指南（MCP 打 AI / 沙盒做卡）

> 面向**只会调 MCP 工具、不读代码**的 agent。
> 全程只用一个 MCP 连接：`orc-manager`。不需要改宿主配置、不需要重启宿主、不需要连实例端口。

## 0. 三十秒版

```
manager.spawn       → 拿 port（实例起来了）
manager.connect     → 拿 sessionId ＋ 该会话能用的工具清单
manager.action      → 用 sessionId 调实例工具（下棋 / 造卡）
manager.kill        → 收工
```

记住一句话：**`spawn` 起服务器，`connect` 拿身份，`action` 干活。**

## 1. 前提

管理器已在运行（`127.0.0.1:51999`）。自检：

```
manager.ping   → {"ok": true, "payload": {"version": 1}}
```

如果返回 `无法连接管理器……请先启动 orc-manager serve`，说明管理器没起——**这不是你该修的**，
把这句话原样转告操作者，然后停在这里。

## 2. 路径 A：打 AI（play 模式）

### A1. 起一台服务器

```
manager.spawn
{
  "instanceId": "ai-1",
  "packDirectories": ["D:/OrC-KSD/samples/card-pack-ai"],
  "seats": [ { "kind": "ai", "displayName": "电脑" }, { "kind": "agent" } ],
  "mode": "play",
  "deckRules": "none",
  "label": "打一局"
}
```

返回：

```json
{ "ok": true,
  "payload": { "instanceId": "ai-1", "port": 57187, "roomId": "5f3c...", "runDir": "..." } }
```

要点：
- `seats` 恒为 2 个。`ai` 由服务端托管，配置完即视为已批准，不用你管。
- `deckRules: "none"` 只在你用的样例包凑不满 39 张时才用；正式卡包用 `"strict"`。

### A2. 以玩家身份接入

```
manager.connect
{
  "instanceId": "ai-1",
  "seat": 1,
  "deck": ["ai.sample.infantry", "... 共 39 张"],
  "packages": [
    { "id": "ai.sample", "version": "1c7bccffaa8c9657c2dc7bf21c91ba49",
      "guid": "b0070000-0000-4000-8000-0000000000a2" }
  ]
}
```

返回：

```json
{ "ok": true,
  "payload": {
    "sessionId": "s1",
    "seat": 1,
    "roles": ["controller", "viewer"],
    "tools": [ { "name": "observe.view", ... }, { "name": "match.end_turn", ... }, ... ] } }
```

要点：
- **`seat` 是 0 或 1**，别和 AI 抢同一个座位。
- **不传 `roles` 时自动兼任 `controller + viewer`**。这是必须的：只当 controller 收不到任何输出，
  连自己的手牌都读不到。
- **`packages` 必须给**（玩家座）：服务端要"卡包 + 卡组共同决定这一局能不能开"，缺了会被直接拒绝
  （`invalid-arguments: controller 申请必须同时提交卡包声明（packages）`）。
  值从卡包的 `manifest.json` 里取 `id` / `version` / `guid` 三个字段。
- **`deck` 与 `packages` 的关系**：`packages` 决定"这局用哪些包"，`deck` 决定"我抽什么"。两者都要。
- 返回里的 **`tools` 就是你这个身份能用的全部工具**，后续只从这里挑名字，别猜。

### A3. 开局

```
manager.admin { "instanceId": "ai-1", "op": "match.start", "args": {} }
```

成功返回 `definitions`（卡池里的卡数）与 `fallback`（`false` 表示没走降级）。

若返回 `room-not-ready`：还有座位空着或没被批准——回到 A2。

### A4. 看棋盘、走一步

```
manager.action { "sessionId": "s1", "tool": "observe.view" }
```

返回的 `payload` 就是你的视角帧：

```json
{ "kind": "view.updated",
  "perspective": { "mode": "seat", "seat": 1 },
  "view": { "phase": "mulligan", "turn": 0, "selfHand": [ ... 5 张 ... ] },
  "actions": { ... } }
```

换牌确认与结束回合：

```
manager.action { "sessionId": "s1", "tool": "mulligan.done", "arguments": { "owner": 1 } }
manager.action { "sessionId": "s1", "tool": "match.end_turn" }
```

出牌：先从 `observe.view` 的 `view.selfHand[].uid` 取引用，再：

```
manager.action { "sessionId": "s1", "tool": "play.unit_pre", "arguments": { "ref": "<uid>" } }
```

若返回 `code = "targeting"`，说明要选目标，用 `target.pick` / `target.cancel`（名字同样从 `tools` 里取）。

### A5. 收工

```
manager.disconnect { "sessionId": "s1" }
manager.kill       { "instanceId": "ai-1" }
```

## 3. 路径 B：沙盒做卡（sandbox 模式）

### B1. 起一台沙盒实例

```
manager.spawn
{
  "instanceId": "sb-1",
  "packDirectories": ["D:/OrC-KSD/samples/card-pack-ai"],
  "seats": [ { "kind": "ai" }, { "kind": "agent" } ],
  "mode": "sandbox",
  "allowDebugWrites": true,
  "deckRules": "none"
}
```

`mode: "sandbox"` ＋ `allowDebugWrites: true` 才会注册 `card.write` 这类写工具。

### B2. 以调试面接入（不占座）

```
manager.connect { "instanceId": "sb-1" }
```

**不给 `seat`** 就是调试面，申请即生效（不用等批准）。返回的 `tools` 里应该有：

```
service.info, service.tools, room.info, sandbox.state, card.load, card.list,
card.write, effect.write, asset.validate, ...
```

### B3. 造一张卡

```
manager.action
{
  "sessionId": "s2",
  "tool": "card.write",
  "arguments": {
    "path": "pack/cards/my.card.json",
    "json": "{\"schemaVersion\":1,\"id\":\"my.card\",\"name\":\"我的卡\",\"components\":[...]}",
    "overwrite": true
  }
}
```

要点：
- `json` 是**字符串**（不是嵌套对象）。
- 同名文件第二次写要 `overwrite: true`，否则回 `validation-failed`。
- `card.write` 只落盘，**不需要活跃对局**；`card.load`（装进运行中的引擎）才需要。

### B4. 自检与收工

```
manager.action { "sessionId": "s2", "tool": "asset.validate" }   → 错误/警告分列
manager.disconnect { "sessionId": "s2" }
manager.kill       { "instanceId": "sb-1" }
```

造出来的卡会随沙盒包进入**下一次开局**的卡池（沙盒包是实例级固定包，强制入并集）。

## 4. 工具速查

| 工具 | 用途 | 必填 |
|---|---|---|
| `manager.ping` | 探活 | — |
| `manager.list` | 列出实例（含 port / roomId / running） | — |
| `manager.spawn` | 起服务器（独立子进程） | `instanceId` |
| `manager.kill` | 销毁实例（进程树回收） | `instanceId` |
| `manager.reload` | 换新 dll 重启该实例（管理器不重启） | `instanceId` |
| `manager.admin` | 管理面透传：`conn.admit` / `match.start` / `room.abort` / `connections.list` / `instance.info` / `sandbox.reset` | `instanceId`, `op` |
| `manager.connect` | 代持一条实例连接，返回 `sessionId` ＋ **可用工具清单** | `instanceId` |
| `manager.action` | 在该连接上调实例工具 | `sessionId`, `tool` |
| `manager.sessions` | 列出代持连接 | — |
| `manager.disconnect` | 断开代持连接 | `sessionId` |

## 5. 常见错误与自愈

| 返回 | 意思 | 怎么办 |
|---|---|---|
| `无法连接管理器……请先启动 orc-manager serve` | 管理器没起 | 转告操作者，别自己重启宿主 |
| `instance-boot-failed` | 服务器子进程起不来 | 多半是 `packDirectories` 路径不存在；核对后重试 |
| `invalid-arguments`（connect） | 玩家座没给 `packages`（或 `deck`） | 从 `manifest.json` 取 `id`/`version`/`guid` 一起提交 |
| `unauthorized: owner=N 与会话座位 M 不符` | 你想操作别人的座位 | 只操作自己 `seat` 对应的 `owner` |
| `unknown-instance` | 实例标识写错，或已被 kill | `manager.list` 看当前实例 |
| `room-not-ready` | 还有空座 / 未批准 | 先 `manager.connect` 补上座位 |
| `seat-taken` | 该座位已有人 | 换 `seat` |
| `room-rotating` | 正在轮换 | 按 `retryAfterMs` 重试 |
| `not-ready` | 操作需要活跃对局（如 `card.load`） | 先 `match.start` |
| `not-admitted` | 连接还在待许可 | 等批准，或重新 `connect` |
| `validation-failed`（`card.write`） | 卡 JSON 不合法或文件已存在 | 检查 JSON；加 `overwrite: true` |
| `unauthorized: 需要 viewer 角色` | 你只申请了 controller | `connect` 时带上 `seat`（自动兼任 viewer） |

## 6. 两个字段陷阱（实测踩过）

1. **载荷字段名分两层**：
   - `manager.*` 的结果在 **`payload`**；
   - 实例工具的载荷（经 `manager.action` 转发后）也在 **`payload`**（管理器把实例的 `details` 放进了 `payload`）。
   直接连实例端口时才是 `details`。**用 MCP 时统一读 `payload`。**
2. **`tools` 会变，但工具表不变**：`manager.connect` 返回的 `tools` 是**该身份**能用的工具；
   不同座位 / 调试面看到的清单不同。别缓存第一次拿到的清单跨实例复用。

## 7. 为什么不用重启宿主

宿主（WorkBuddy 等）在启动时固定 MCP 连接，运行中**不能新增连接**。所以实例级工具不可能靠"再配一条 MCP"获得。

这里的做法是：**实例通过管理器的一条既有连接被代理**——
`manager.connect` 在管理器进程内建立到实例的真连接，`manager.action` 把调用转发过去。
于是：

- 拉起服务器、接入、开局、造卡、销毁，**全在一条 MCP 连接内完成**；
- 新增/销毁实例后管理器会发 `notifications/tools/list_changed`，支持它的宿主会自动刷新工具表；
  不支持的宿主也不受影响，因为**工具表本身是恒定的 10 个**。

这就是"不需要重启宿主"的实现方式：不是让工具变多，而是让工具**能指向任意实例**。

## 8. 一页纸检查清单

- [ ] `manager.ping` 通
- [ ] `manager.spawn` 拿到 `port`
- [ ] `manager.connect` 拿到 `sessionId` 与 `tools`（玩家座带 `deck` ＋ `packages`）
- [ ] `manager.admin{match.start}` 成功（打 AI）
- [ ] `manager.action{observe.view}` 能看到自己的手牌；对手只有数量、没有明细
- [ ] 出牌：`play.unit_pre` → 拿到 `pending` → `target.submit{picks:[1]}`
- [ ] 收工：`manager.disconnect` ＋ `manager.kill`
- [ ] `manager.list` 归零，无残留进程

## 9. 实测记录（WorkBuddy 内侧，2026-10-22）

以下是用 MCP 工具**逐次真调**走出来的，可作为预期形状的参照：

```
manager.spawn   → port 63730, room 3713a9d5
manager.connect → sessionId=s1, 25 个工具
                  （缺 packages 时被拒：invalid-arguments "controller 申请必须同时提交卡包声明"）
manager.admin{match.start} → definitions=6, fallback=false,
                  packs=["pack/ai.sample/","pack/sandbox/"]
manager.action{observe.view} → perspective={seat,1}, phase=mulligan,
                  selfHand=5, enemyHandCount=4（对方只有数量）
manager.action{mulligan.done, owner:1} → ok
manager.action{mulligan.done, owner:0} → 拒绝：owner=0 与会话座位 1 不符（座位隔离生效）
manager.action{play.unit_pre, ref:<uid>} → code=targeting,
                  pending.candidates=[{index:1,...},{index:2,...}]
manager.action{target.submit, picks:[1], sessionId:<pending.sessionId>} → ok
manager.action{match.end_turn} → turn 3
manager.action{observe.view} → turn=4, phase=play,
                  selfLine 有 1 个单位（canAttack:true），enemyLine 有 2 个（AI 自己铺的）
manager.disconnect / manager.kill → 归零
```

两个只有真跑才知道的细节：

- `target.submit` 要带 **`pending.sessionId`**（不是 `manager.connect` 的 `sessionId`）。
- AI 座位是**真在动**的：它会自己换牌、自己铺场，不需要你代它操作。
