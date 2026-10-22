> **⚠ 本文已被取代（2026-10-21）**：以下内容是重构前的**预稿**（`seat.request`/`session.status`/`notify/room` 等已作废）。
> 现行协议请以 [`客户端接入指南.md`](./客户端接入指南.md) 为准；本文仅保留作设计沿革记录，**不得作为接入依据**。

# 前端交接：六层模型下的协议变更与 API 参考（预定稿）

> **读者**：Godot 前端 / 客户端 SDK 维护者 / agent 接入方。
> **称谓**：本文中"前端"与术语表的**客户端（Client）**同义——承载表现与输入、经协议与服务器交互的进程。
> **定位**：本文档**自包含**——只看这一份就能评估改动、改代码、联调，不需要读引擎源码。
> **状态**：**预定稿（pre-implementation）**。需求已定稿，API 形态是实现前的预定基线；实现期可微调字段名，但**语义不会变**。
> **基线**：`requirements.md`（R1–R10 全部 Confirmed）、`implementation.md`、`基线调研.md`（同目录）。
> **协议版本**：`1.0` → **`2.0`**。**破坏性重构，不保留 v1 兼容层**。

---

## 0. 标记约定（先看这一节，它决定你怎么读后面的内容）

全文每条结论都带一个标记：

| 标记 | 含义 | 你能怎么对待 |
|---|---|---|
| **[F]** | 已定稿，来自 `requirements.md` 的 R1–R10 | 当契约对待，不可协商 |
| **[F 派生]** | 由 R1–R10 直接推出、但需求文档未逐字写出的结论 | 当契约对待；如与实现冲突，以需求原文为准 |
| **[P]** | 预定形态，本文定义；实现期可能微调字段名/命名 | 可以据此开发，但**不要**把字段名写进持久化格式 |
| **[U]** | 待裁决（需求文档里的 U3/U4/U5） | 先按本文的默认假设写，裁决后按 §14 的对照改 |

**默认假设（U 未定前一律按此）**：
- **U3**（角色声明与接入许可时序）→ 采用「声明-许可-绑定」三段式，但默认策略 `auto` 让一次 `seat.request` 同时完成许可与绑定（见 §5.2）。**[P]**
- **U4**（"双方 lock 并集"的双方是谁、对账方向）→ 定为「两个 seat 的参与者各自声明的卡包集合」，对账方向＝双向并集，冲突即拒绝开局（见 §8.3 的 `room.start`）。**[P]**
- **U5**（管理器 MCP 入口本轮是否交付）→ 本轮**不交付**；管理器只提供进程内 API + CLI（见 §8.2）。**[P]**

---

## 1. 一句话与"变更接受"

**变化**：把"一进程 ＝ 一服务器 ＝ 一房间 ＝ 一局 ＝ 2 座"的竖线，拆成
**管理器 → 服务器实例 → 房间 → seat → (controller, viewer)**，管理员作为**正交于 seat** 的第三类身份。

**你（前端）要接受的四件事**：

1. **协议面不向后兼容。** 旧调用（`room.claim` / `room.resume` / `room.start` / `room.kick` / `room.add_ai` / `room.release` / `target.claim`）在本协议里**不存在**。注意失败形态有两种，不要只判一种：
   - 当**工具名**调（`tools/call {name:"room.claim"}`）→ `result.ok=false`，`code` 为 `unknown-tool` 或 `unauthorized`（先撞门禁就是后者）；
   - 当 **JSON-RPC 方法**调（`{"method":"room.start"}`）→ `-32601`。
   
   两种情况都**不会执行任何操作**。不要写"先试新名、失败再试旧名"的探测逻辑——服务端不会给兼容分支。
2. **"房主"概念废弃。** 房间控制权（开局/终止/轮换/踢人/加 AI/换卡包）全部归**管理员**，而管理员＝**进程主人**（谁拉起了这个服务器进程，谁就是）。玩家连接不再持有任何房间控制能力。
3. **服务器进程不再"一局一进程"。** 终局后服务器继续存活，自动轮换出下一个 lobby 房间；`roomId` 会变，`seatToken` 会失效，**座位必须重新申请、卡组必须重新提供**。
4. **对局内部载荷零改动。** `view`、`actions`、`UiCardView`、段条目、`target.*`、动作工具的参数与返回壳——**全部不变**。你只改"外壳三层"：会话 / 座位 / 房间。

> 如果你的目标是"最小改动先跑起来"，直接跳到 **§12 旧→新对照表**，把 7 个旧调用替换掉即可；其余章节用于处理轮换、重连和管理面。

---

## 2. 模型：前端需要知道的全部

### 2.1 对象图

```
管理器（常驻，持有资产库）
 └─ 服务器实例 ×N（每实例 = 一个独立进程，有稳定 instanceId）
      └─ 房间（同一时刻至多 1 个非终结房间）
           └─ seat ×2（恒为 2，与 1v1 引擎一一对应）
                ├─ controller（至多 1 个）：引擎的输入端
                └─ viewer（0..N 个）：引擎的输出端
```

### 2.2 权限只由"拓扑"决定 [F]

| 角色 | 绑 seat | 不绑 seat |
|---|---|---|
| **controller**（输入端） | **玩家面**：只能向该 seat 推输入 | **调试面**：直接调引擎内部调试 API |
| **viewer**（输出端） | **该 seat 视角**（脱敏） | **全知**（不经脱敏） |

- **没有独立的权限档案 / 角色表 / ACL**。想让某人有权限＝把他绑到 seat；想收回＝解绑。
- 不存在"绑了 seat 却能调调试 API"的 controller，也不存在"绑了 seat 却收全知输出"的 viewer。**[F]**

### 2.3 四条不变量（前端可以直接依赖）

| # | 不变量 | 对你的影响 |
|---|---|---|
| 1 | seat 恒为 2，编号 0/1，与 `playerIndex` 一一对应 | UI 固定画两个座位 |
| 2 | 一个连接至多绑 1 个 seat | 换座必须先 `seat.release` |
| 3 | 一个 seat 至多 1 个 controller | 第二个 controller 申请会得到 `seat-taken` |
| 4 | 角色不因副作用扩张 | 声明 `["viewer"]` 的连接永远收不到动作权限 |

### 2.4 一个连接可以兼任，但必须显式声明 [F]

```
roles: ["controller"]            → 只推输入，收不到任何输出
roles: ["viewer"]                → 只收输出，不能提交动作
roles: ["controller","viewer"]   → 兼任：同一条连接既推输入也收输出，共享同一个 seat 绑定
```

**兼任连接的 viewer 恒为脱敏视角。** 想要全知，必须是**不含 controller 角色的独立连接**（§5.3）。

> ⚠️ **最容易踩的坑**：玩家连接如果只声明 `["controller"]`，它会**完全收不到 `notify/view`**。标准玩家连接必须声明 `["controller","viewer"]`。

---

## 3. 变更范围总表

### 3.1 不变（零改动，放心复用）

| 项 | 说明 |
|---|---|
| 传输 | TCP + NDJSON（一行一帧，UTF-8，LF，单帧 ≤ 1 MiB） |
| 信封 | JSON-RPC 2.0 外形：`initialize` / `ping` / `tools/list` / `tools/call` + `notify/*` |
| 握手后的心跳 | 服务端 45s 无入站请求断连；客户端静默 ≥ 30s 发 `ping`，15s 无入站帧判定断线 |
| 业务结果壳 | `{ok, code, message, details}` |
| 动作工具 | `play.*` / `command.*` / `match.*` / `mulligan.*` / `target.*` 的**名字、参数、返回**不变 |
| 视图 DTO | `view`（`UiMatchView`）、`actions`、`UiCardView`、`UiCardDefinition` 字段表全部不变 |
| 段 | `notify/segment` 的 `segments[]`、条目结构、`$ref`/`$hidden`/`$effect`/`$truncated` 语义不变 |
| 选靶 | `notify/target.request` / `notify/target.expired` 字段与 `picks` 从 1 起不变 |
| 只读诊断 | `play.can_play` / `command.availability` / `docs.*` / `service.*` 保留 |

### 3.2 变（必须改）

| # | 项 | 旧 | 新 |
|---|---|---|---|
| 1 | 协议版本 | `1.0` | `2.0`（不匹配即拒绝） |
| 2 | `capabilities` | `["room","push","targeting"]` | `["room","session","seat","push","targeting","viewer"]` |
| 3 | `initialize` 回执 | 三字段 | 多一个 `server` 段（`instanceId` / `mode` / `rotationIndex`） |
| 4 | 占座 | `room.claim`（自己占，房主可踢人） | `seat.request`（申请绑定，管理员授予） |
| 5 | 重连 | `room.resume {seatToken}` | `seat.request {seatToken}` |
| 6 | 释放 | `room.release` | `seat.release` |
| 7 | 开局 | `room.start {seatToken}`（房主） | **管理面** `room.start`（无参，经管理器） |
| 8 | 踢人 / 加 AI | `room.kick` / `room.add_ai`（房主） | **管理面** `seat.unbind` / `seat.bind{controller:{kind:"ai"}}` |
| 9 | 房间快照 | `seats[].controllerKind: "local"\|"remote"\|"ai"` | `seats[].controller: {kind:"player"\|"ai", …}` + `viewers[]` |
| 10 | 终局后 | 无（进程默认退出或停在 `in-match`） | 房间轮换：`roomId` 变、座位清空、`seatToken` 失效 |
| 11 | `notify/view` 的 `seat` | 恒为 0/1 | 绑座 viewer＝0/1；全知 viewer＝`null`，并带 `perspective:"omniscient"` |
| 12 | `tools/list` | 只按"是否绑座"过滤调试写类 | 按**角色**过滤（未声明/纯 viewer/controller/调试面各不同） |
| 13 | 错误码 | 12 个业务码 | 新增 8 个，`unauthorized` 语义收窄（§9） |
| 14 | 大厅断线 | 立即释放座位（`LobbySeatRetention=0`） | **座位保留**（`controller.connected:false`），靠 `seatToken` 重连或管理员解绑 **[F 派生]** |

### 3.3 新增

| 项 | 作用 |
|---|---|
| `seat.request` | 客户端唯一的"入座/重绑"入口 |
| `seat.release` | 客户端主动放弃绑定 |
| `session.attach` | 不绑 seat 的连接入口：全知 viewer / 调试面 controller |
| `session.status` | 查本连接的角色、绑定、房间 |
| `notify/session` | 会话状态变化（被许可 / 待批 / 已绑定 / 被解绑 / 被撤销） |
| `notify/pool` | 卡池代次变化（前端刷新卡表用） |
| 管理面（11 个 op） | `seat.bind` / `seat.unbind` / `conn.permit` / `conn.revoke` / `room.start` / `room.abort` / `room.rotate` / `pool.reload` / `drain` / `admin.state` / `admin.ping`（+ 通道事件） |
| 管理器（5 组） | `manager.spawn` / `kill` / `reload` / `list` / `admin` |

### 3.4 删除（调用必失败）

> 失败形态：当**工具名**调 → `ok:false` + `code:"unknown-tool"`（或先撞门禁得 `unauthorized`）；当 **JSON-RPC 方法**调 → `-32601`。两种都不会执行。

| 旧调用 | 替代 |
|---|---|
| `room.claim` | `seat.request` |
| `room.resume` | `seat.request {seatToken}` |
| `room.release` | `seat.release` |
| `room.start` | 管理面 `room.start` |
| `room.kick` | 管理面 `seat.unbind` |
| `room.add_ai` | 管理面 `seat.bind` + `controller.kind:"ai"` |
| `target.claim` | 无（早已退役，本轮彻底删除） |

---

## 4. 连接与握手

### 4.1 握手

```json
// → 请求
{"jsonrpc":"2.0","id":1,"method":"initialize",
 "params":{"clientName":"godot","protocolVersion":"2.0"}}

// ← 响应
{"jsonrpc":"2.0","id":1,"result":{
  "protocolVersion":"2.0",
  "schemaVersion":7,
  "capabilities":["room","session","seat","push","targeting","viewer"],
  "server":{"instanceId":"s1","mode":"play","rotationIndex":1}
}}
```

- `protocolVersion` **可以省略**（省略＝接受服务端版本）；一旦提供且不等于 `"2.0"`，直接 `-32602` 拒绝。
- `schemaVersion` 是对局契约版本，**数值以服务端为准**（示例中的 `7` 仅示意，不要硬编码；前端只在需要区分契约差异时读它）。
- `server.instanceId` 是服务器实例的稳定标识（由管理器分配）。前端可用它区分"我连的是哪一台"，尤其在多实例或多开场景。
- `server.rotationIndex` 是当前房间在**该实例存活期内**的序号（从 1 起）。掉线重连后可据此判断"我离开期间是否已经轮换过一局"。

### 4.2 能力标记含义

| capability | 含义 |
|---|---|
| `room` | 有房间概念（快照/推送） |
| `session` | 支持连接级角色声明（`session.*`） |
| `seat` | 支持座位申请/释放（`seat.*`） |
| `push` | 支持服务端主动推送 |
| `targeting` | 支持异步选靶 |
| `viewer` | 支持 viewer 角色（含全知） |

> 管理面**不在** `capabilities` 里，因为它**不注册进业务端口的工具表**（§8.6）。

### 4.3 连接的三段状态（前端状态机的基础）

```
        连接建立
           │
           ▼
      [未声明] ──session.attach{roles}──> [已声明·未绑座]  （全知 viewer / 调试面）
           │                                     │
           └──seat.request{seat,roles}──> [待批准] ──管理员许可──> [已绑定]
                                              │                      │
                                              └──── 撤销/解绑 ────────┘
```

- **未声明**：只能调 `initialize` / `ping` / `room.info` / `session.status` / `service.*` / `docs.*`。
- **已声明·未绑座**：按声明角色获得能力（全知 viewer 收全知推送；调试面 controller 可调调试 API）。
- **待批准**：`seat.request` 返回 `code:"pending-admission"`，等管理员 `conn.permit` + `seat.bind`。
- **已绑定**：按角色工作。

---

## 5. 会话与座位（客户端面）

### 5.1 我是什么身份 → 调什么

| 你想做 | 调用 | 备注 |
|---|---|---|
| 打牌（人类玩家） | `seat.request {seat, roles:["controller","viewer"], deck,…}` | 一次到位；保存 `seatToken` |
| 只看（观战/教练，全知） | `session.attach {roles:["viewer"]}` | **不要**带 seat |
| 只看（某座位视角） | `seat.request {seat, roles:["viewer"]}` | 不需要卡组 |
| agent 造卡/调试（不占座） | `session.attach {roles:["controller"]}` | 仅回环 + sandbox 模式（§5.6） |
| 断线重连 | `seat.request {seatToken}` | 不需要 seat / deck |
| 主动让座 | `seat.release {seat}` | 房间态/对局中都可（对局中会让位但不断线） |

### 5.2 `seat.request` —— 唯一的入座入口 [P]

**参数**

```json
{
  "seat": 0,
  "roles": ["controller","viewer"],
  "name": "玩家甲",
  "deck": ["card-id-1", "card-id-2"],
  "deckConfig": {"mainFaction":"de","allyFaction":"us"},
  "packs": [{"packageId":"core","version":"1.2.0","guid":"9f2c1a7b"}],
  "seatToken": null
}
```

| 字段 | 必填 | 说明 |
|---|---|---|
| `seat` | 首次绑定必填；带 `seatToken` 时可省略 | 0 或 1 |
| `roles` | 是（带 `seatToken` 时可省略，沿用令牌内的角色） | `["controller"]` / `["viewer"]` / `["controller","viewer"]` |
| `name` | 否 | 显示名，缺省 `座位{n}` |
| `deck` | `roles` 含 `controller` 时必填 | 卡牌 id 数组（服务端校验张数/卡池/同名上限/配国） |
| `deckConfig` | 否 | 主国/盟国 |
| `packs` | 否 | **该参与者声明的卡包引用**（`{packageId, version, guid}`）。省略＝"使用本实例卡池"（服务端按当前卡池的 lock 计入并集）。显式给出时参与"双方 lock 并集"对账 **[P]** |
| `seatToken` | 重连时必填 | 管理员上次下发的凭据（§5.5） |

**回执 A：自动许可（`admission:"auto"`，单机默认）**

```json
{"ok":true,"code":"ok","message":"","details":{
  "seat":0,
  "roles":["controller","viewer"],
  "admission":"auto",
  "seatToken":"a1b2c3d4e5f6…",
  "roomId":"r-0001",
  "rotationIndex":1
}}
```

**回执 B：待管理员批准（`admission:"manual"`）**

```json
{"ok":true,"code":"pending-admission","message":"等待管理员许可","details":{
  "requestId":"rq-7f3a",
  "seat":0,
  "roles":["controller","viewer"]
}}
```

- `pending-admission` 是**成功态**（和 `targeting` 一样），不是错误。前端应显示"等待房主/服务器批准"，并等 `notify/session`。
- 批准后推 `notify/session {state:"bound", requestId:"rq-7f3a", …}`，其中带 `seatToken`（**必须保存**）。`requestId` 用于把批准/拒绝对应回你发起的那次申请（同一连接可能连续申请多个 seat）。
- `requestId` 只在 `admission:"manual"` 时出现；`auto` 策略下 `seat.request` 直接返回 `ok`，没有这一层。

**回执 C：失败**

| code | 触发 |
|---|---|
| `seat-taken` | 该 seat 已有 controller（`roles` 含 controller 时） |
| `insufficient-seat` | 申请 viewer 但该 seat 尚无 controller（viewer 必须挂在一个已有 controller 的 seat 上） |
| `deck-invalid` | 卡组不合法（`message` 含具体卡 id/违规类型） |
| `unauthorized` | `seatToken` 无效/已过期（跨房间一定过期） |
| `room-started` | 房间已开局，不能再新绑 controller（viewer 仍可加入，见下） |
| `role-conflict` | 角色组合非法（`roles` 为空 / 含未知角色 / 含不能绑座的组合） |
| `not-admitted` | 该连接已被撤销许可 |

> **viewer 的加入时机**：`room-started` 只拦 **controller**；viewer 在 `in-match` 阶段仍可申请绑定（这是观战能力的来源）。**[P]**

### 5.3 `session.attach` —— 不绑 seat 的入口 [P]

**参数**

```json
{"roles":["viewer"],"name":"教练"}
```

| `roles` | 结果 |
|---|---|
| `["viewer"]` | **全知 viewer**：收双方手牌明细的推送 |
| `["controller"]` | **调试面 controller**：不占座；可调调试写类工具（仅回环 + `--mode sandbox` + `--allow-debug-writes`，判据与今天完全一致） |
| `["controller","viewer"]` | **拒绝** `role-conflict`（全知 viewer 不得含 controller 角色） |

**回执**

```json
{"ok":true,"code":"ok","details":{
  "roles":["viewer"],
  "perspective":"omniscient",
  "admission":"auto",
  "roomId":"r-0001"
}}
```

**约束**

- 已绑定 seat 的连接调 `session.attach` → `already-bound`（必须 `seat.release` 后才行）。
- `session.attach` 在一个连接生命周期内只能成功一次；改角色须断开重连。**[P]**
- `admission:"manual"` 的实例上，`session.attach` 同样返回 `pending-admission`，等 `conn.permit` 后由 `notify/session {state:"admitted"}` 告知结果。**[P]**

### 5.4 角色合法性表

| 操作 \ 当前角色 | 未声明 | viewer（全知） | viewer（绑座） | controller（绑座） | controller+viewer（绑座） | controller（调试面） |
|---|---|---|---|---|---|---|
| `seat.request` | ✅ | ❌ `already-declared` | ❌ | ❌ | ❌ | ❌ |
| `session.attach` | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| `seat.release` | ❌ | ❌ | ✅ | ✅ | ✅ | ❌ |
| `observe.view` | ❌ | ✅ 任意 seat | ✅ 仅自己 | ❌ `role-forbidden` | ✅ 仅自己 | ❌ |
| 动作工具（`play.*`…） | ❌ | ❌ `role-forbidden` | ❌ `role-forbidden` | ✅ | ✅ | ❌ |
| `target.submit/cancel` | ❌ | ❌ | ❌ | ✅ | ✅ | ❌ |
| 调试写类工具 | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ |
| `room.info` / `session.status` / `service.*` / `docs.*` / `ping` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

> `activePlayerSide` / 回合判定仍由引擎负责；前端只按 `actions` 与 `view.activePlayerSide` 渲染。

### 5.5 `seatToken` 规则（**必须持久化**）

| 规则 | 说明 |
|---|---|
| 来源 | **只有管理员**下发（自动许可时由服务器代管理员签发），客户端不能自造 |
| 内容 | 不透明字符串；绑定"房间 + seat + 角色集合" |
| 有效期 | **房间生命周期内**；房间轮换后立即失效 |
| 用途 | 断线重连时重新绑定**同一座位**，无需管理员再次批准 |
| 失效后果 | `seat.request {seatToken}` 回 `unauthorized` → 清掉本地 token，走首次绑定流程 |
| 不可转让 | 换连接可以，换 seat 不行（想换座先 `seat.release` 再重新申请） |

**前端必须做**：登录态里持久化 `seatToken`＋`roomId`。重连时若 `roomId` 与当前房间不一致 → token 直接丢弃，不要发出去。

### 5.6 调试面的三条旧判据不变 [F]

调试写类工具（`card.load` / `card.write` / `effect.write` / `sandbox.*` 等）的可用条件**与今天逐条相同**：

1. 来源是**回环**（严格 `IPAddress.IsLoopback`，`192.168.x.x` 不算）；
2. 该连接**未绑定 seat**；
3. 实例以 `--mode sandbox --allow-debug-writes` 启动。

新模型只是给这种连接一个正式名字：**调试面 controller**。多个调试面 controller 可以同时存在，互不影响，也不要求独占。**[F]**

### 5.7 `seat.release` / `session.status`

```json
// seat.release
{"seat":0}          // seat 可省略＝释放本连接绑定的座位
→ {"ok":true,"code":"ok","details":{"seat":0}}

// session.status（无参）
→ {"ok":true,"code":"ok","details":{
     "sessionId":"c-7f3a",
     "roles":["controller","viewer"],
     "seat":0,
     "perspective":"seat",
     "admitted":true,
     "roomId":"r-0001",
     "rotationIndex":1
   }}
```

**`seat.release` 的边界**

- 只能解绑**本连接自己**；解绑别人只能由管理员 `seat.unbind`。
- 对局中释放 ＝ 该 seat 变成"无 controller"（等价于断线），**对局不会因此中止**；后续胜负由引擎的宽限判负/认输规则处理。
- 释放后连接仍在、角色声明仍在（可以重新 `seat.request` 同一或另一座位）。

**断线不等于释放** **[F 派生]**

房间/座位不因连接断开而销毁（seat 的生命周期＝房间的生命周期）。断线后该 seat 的 `controller.connected` 变 `false`，但**座位仍被占**：

- 该玩家可以用 `seatToken` 重连回同一座位；
- 管理员可以 `seat.unbind` 强制收回；
- 其他玩家看到的不是"空座"，而是"离线玩家"。

> 这与今天"大厅断线即释放座位"的行为不同，是前端必须改的 UI 逻辑之一（§11.3）。

### 5.8 会话事件 `notify/session` [P]

```json
{"state":"bound","seat":0,"roles":["controller","viewer"],
 "seatToken":"…","roomId":"r-0001","rotationIndex":1}
```

| `state` | 触发 | 前端动作 |
|---|---|---|
| `pending` | `seat.request` 进入待批 | 显示"等待批准"，禁用操作 |
| `admitted` | `conn.permit` 通过（尚未绑座） | 可继续 `seat.request` |
| `bound` | 绑定成功 | 保存 token，开始/恢复渲染 |
| `unbound` | 被 `seat.unbind` / 自己 `seat.release` / 座位被回收 | 关闭对局面板，回座位面板 |
| `revoked` | `conn.revoke` | 显示原因 → 断开重连（重连也会被拒，需联系管理员） |
| `rejected` | 申请被拒 | 显示 `reason`，回座位面板 |

---

## 6. 房间与房间轮换

### 6.1 `room.info` 新快照 [P]

```json
{
  "instanceId": "s1",
  "roomId": "r-0003",
  "rotationIndex": 3,
  "state": "lobby",
  "seats": [
    {
      "seat": 0,
      "controller": {"kind":"player","sessionId":"c-7f3a","displayName":"玩家甲","connected":true},
      "viewers": [{"sessionId":"c-9b21","connected":true}],
      "viewerCount": 1,
      "deck": {"provided":true,"cardCount":39}
    },
    {
      "seat": 1,
      "viewers": [],
      "viewerCount": 0,
      "deck": {"provided":false,"cardCount":0}
    }
  ],
  "pool": {"generation":4,"fingerprint":"9f2c1a7b3d4e5f60","packPolicy":"loaded"},
  "addresses": [{"ip":"192.168.1.7","port":54321}]
}
```

> **空座不写 `controller` 字段**（沿用现有 `WhenWritingNull` 序列化口径），不是写 `null`、更不是 `"controllerKind":""`。判空用"字段缺失"。

**字段表**

| 字段 | 类型 | 说明 |
|---|---|---|
| `instanceId` | string | 服务器实例标识（新增） |
| `roomId` | string | **每次轮换都会变**（新增语义：不再终身不变） |
| `rotationIndex` | int | 该实例第几个房间，从 1 起（新增） |
| `state` | string | `lobby` / `in-match` / `ended`（内部 `starting` 映射为 `in-match`，**不过线**） |
| `seats[].seat` | int | 0 / 1 |
| `seats[].controller` | object? | **空座时该字段缺失**（省流口径）；`kind` 只有 `player` / `ai`（**`local`/`remote` 已废弃**） |
| `seats[].controller.sessionId` | string? | 玩家面 controller 的连接 id；AI 为 `null` |
| `seats[].controller.displayName` | string? | 显示名 |
| `seats[].controller.connected` | bool | 连接在否（AI 恒 `true`） |
| `seats[].viewers` | array | 该座位的 viewer 列表（**新**，0..N） |
| `seats[].viewerCount` | int | 便捷计数 |
| `seats[].deck.provided` | bool | 卡组是否已提供（**新**：跨房间不继承） |
| `seats[].deck.cardCount` | int | 张数（仅展示，合法性以服务端校验为准） |
| `pool.generation` | int | 卡池代次，`pool.reload` 生效后 +1（**新**） |
| `pool.fingerprint` | string? | 卡包指纹（原 `packFingerprint`，挪进 `pool`） |
| `pool.packPolicy` | string | 原 `packPolicy` |
| `addresses` | array | 非回环 IPv4 ＋ 端口（不变） |

**与旧字段的映射**

| 旧 | 新 |
|---|---|
| `seats[].controllerKind` | `seats[].controller.kind`（值域从 `local/remote/ai` → `player/ai`） |
| `seats[].displayName` | `seats[].controller.displayName` |
| `seats[].connected` | `seats[].controller.connected` |
| `packPolicy` | `pool.packPolicy` |
| `packFingerprint` | `pool.fingerprint` |

> 前端最常见的误读：空座**没有 `controller` 字段**（不是 `null`，也不是 `"controllerKind": ""`）。判空请判"字段缺失 / 反序列化为 `null`"。

### 6.2 房间轮换：前端必须重置 [F]

```
对局终局（view.phase == "over"）
   ↓ 服务端销毁对局、旧房间置 ended
   ↓ notify/match.ended  {roomId, outcome, reason}
notify/room {roomId:"r-0004", rotationIndex:4, state:"lobby", seats:[空,空]}
   ↓ 前端必须：清空 seatToken、清空对局面板、回到座位面板
玩家重新 seat.request + 提供卡组
管理员 room.start
notify/match.started {roomId:"r-0004"}
```

**硬规则**：

- 跨房间**不共享 seat**（`seatToken` 失效、卡组要重新提供）。**[F]**
- 旧房间保持 `ended`，**不可回到 `lobby`**。前端不要尝试"复用上一局的状态"。
- 判定"轮换了"的唯一依据：`notify/room` 里的 `roomId` 变了。**不要**用 `rotationIndex` 自增来推断（可能连续多次重装）。
- 同一实例内**同时最多 1 个非终结房间**，所以不存在"房间列表"UI。

### 6.3 服务器不再因为终局退出 [F]

| 形态 | 旧 | 新 |
|---|---|---|
| 普通（非无头） | 进程常驻，但房间停在 `in-match` | 进程常驻，终局后轮换新房间 |
| 无头 | 默认 `--exit-on-end` 退出 | 默认常驻轮换；`--exit-on-end` 降级为"一次性脚本/测试"开关 |

**对前端的影响**：`ServerProcess` 拉起服务器后，**不要**再假设"对局结束→进程退出→自动清理"。退出必须显式 `manager.kill`（或进程主人退出）。窗口关闭 / 场景切换时要主动杀进程，否则会留下孤儿进程占用端口。

---

## 7. 对局面：不变的部分（重点，别多改）

### 7.1 动作工具（名字/参数/返回全不变）

| 工具 | 角色要求 | 变化 |
|---|---|---|
| `mulligan.begin` / `mulligan.done` | controller（绑座） | `owner` 变可选（省略＝本连接座位） |
| `match.end_turn` | controller（绑座） | 无 |
| `match.concede` | controller（绑座） | `owner` 变可选 |
| `play.unit_pre` / `play.unit` | controller（绑座） | `unit` 的 `owner` 变可选 |
| `play.command_pre` / `play.command` | controller（绑座） | 无 |
| `play.counter` / `play.join` | controller（绑座） | `join` 的 `owner` 变可选 |
| `command.begin/move/attack/leave` | controller（绑座） | 无 |
| `play.can_play` / `command.availability` | 任意 | 无（只读） |
| `target.submit` / `target.cancel` | controller（绑座） | **参数完全不变**（`sessionId` + `picks` 从 1 起） |

> **建议**：新代码**不要**再传 `owner`。座位由绑定唯一确定，传错反而会 `unauthorized`。传了就校验（必须等于绑定座位），不传就由服务器补齐。

### 7.2 推送过滤矩阵（**新**，直接决定前端收不收得到）

| 推送 | 未声明 | 全知 viewer | 绑座 viewer | controller（绑座） | 调试面 controller |
|---|---|---|---|---|---|
| `notify/room` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `notify/session` | ✅（仅自己） | ✅ | ✅ | ✅ | ✅ |
| `notify/match.started` / `notify/match.ended` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `notify/view` | ❌ | ✅ 全知 | ✅ 自己视角 | ❌ | ❌ |
| `notify/segment` | ❌ | ✅ 全知（不脱敏） | ✅ 自己视角 | ❌ | ❌ |
| `notify/target.request` / `.expired` | ❌ | ❌ | ❌ | ✅ 仅自己座位 | ❌ |
| `notify/pool` | ✅ | ✅ | ✅ | ✅ | ✅ |

> **controller-only 连接收不到 `notify/view`** 是设计意图，不是 bug。玩家连接请声明 `["controller","viewer"]`。

### 7.3 `notify/view` 的字段变化

```json
{"kind":"view.updated",
 "seat":0,                       // 全知 viewer 为 null
 "perspective":"seat",           // "seat" | "omniscient"（新增）
 "roomId":"r-0003",              // 新增（跨房间区分）
 "matchId":"a1b2c3d4",
 "view":{ /* UiMatchView，字段零改动 */ },
 "actions":{ /* 零改动 */ },
 "turn":3,"phase":"play","activePlayerSide":"self"}
```

- 绑座连接：`seat` 仍为 0/1，`perspective:"seat"`，其余与今天完全一致。
- 全知连接：`seat` 为 `null`，`perspective:"omniscient"`，`view.self*` 指 seat 0、`view.enemy*` 指 seat 1，并额外带 `enemyHand` 明细（§7.4）。
- 旧的扁平标量（`turn` / `phase` / `activePlayerSide` / `selfHandCount`…）**保留**，但新代码只读 `view` 与 `actions`。

### 7.4 全知视图的定义 [P]

```json
{"kind":"view.updated",
 "seat":null,
 "perspective":"omniscient",
 "view":{ "...": "与 UiMatchView 同 shape，self=seat0，enemy=seat1" },
 "enemyHand":[ /* UiCardView[]，对手手牌明细——绑座视角里不存在这个字段 */ ],
 "actions":{ "...": "与座位无关的空 actions（全知是只读）" }}
```

**约束**：

- 全知 viewer **不能**提交动作（`actions` 里的可操作性一律为 false/空）。
- 全知视图**不是**"两个视角拼在一起"，而是"以 seat 0 为 self 的完整投影 + 对手手牌明细"。观战 UI 不在本轮验收范围，前端只需不做崩溃处理即可。
- 全知 viewer 调 `observe.view {seat:0|1}` 也可以拿到该座位的脱敏视图（诊断用）。

### 7.5 `observe.view` 的调用者分流

| 调用者 | `seat` 参数 | 结果 |
|---|---|---|
| 绑座 viewer | 省略或等于自己 | 自己视角（其余 → `unauthorized`） |
| 全知 viewer | 省略 | 全知视图 |
| 全知 viewer | 0 / 1 | 该座位的脱敏视图 |
| controller-only | — | `role-forbidden` |
| 未声明 | — | `not-admitted`（play 模式） |

---

## 8. 管理面（前端也必须懂，因为单机时你就是"管理员"）

### 8.1 为什么前端要懂

管理权来源＝**进程所有权**：谁拉起并持有这个服务器进程，谁就是管理员。

| 部署形态 | 谁持管理权 | 前端怎么开局 |
|---|---|---|
| **A. 自托管（单机/局域网房主）** | 前端进程（它 spawn 了服务器） | 通过**管理器 API** 调 `room.start` |
| **B. 专用服务器** | 运维 / agent（CLI 或 MCP） | 前端只能等 `notify/match.started` |
| **C. 纯客户端（加入别人的局）** | 别人 | 前端只能 `seat.request` + 等 |

**形态 A 是这次变化里前端最容易漏的一块**：`room.start` 不再是"房主客户端的工具"，而是"管理器对服务器实例的一次管理调用"。

### 8.2 管理器 API [P]

四种驱动形态共享同一套语义 **[F]**，本轮交付**进程内 API + CLI**，MCP 入口不做 **[U5 默认]**。

> **为什么保留 CLI**：客户端如果不想把引擎程序集拖进自己的进程（Godot/Unity 的 C# 宿主引用 `Orc.Manager` 会连带 `Orc.Game` 等），就改用 CLI 形态——把 `orc-manager` 当子进程跑，用 `--json` 读回执。两条路径语义逐字等价。

| 操作 | 参数 | 回执 |
|---|---|---|
| `manager.spawn` | `{instanceId, packs[], mode, port?, admission?, runDir?, drainTimeoutMs?, forceAbortOnReload?}` | `{instanceId, port, state:"ready", pool:{…}, room:{roomId, state, rotationIndex}}` |
| `manager.kill` | `{instanceId, force?, timeoutMs?}` | `{instanceId, state:"stopped"}` |
| `manager.reload` | `{instanceId, drainTimeoutMs?, force?}` | `{instanceId, port, ready:{…}}`（换进程，端口复用） |
| `manager.list` | `{}` | `[{instanceId, port, state, pool:{generation,fingerprint}, room:{roomId,state,rotationIndex}, pending:[…]}]` |
| `manager.admin` | `{instanceId, op, args}` | 该 op 的 `payload`（错误码经管理器码表映射后返回） |

**`manager.spawn` 参数**

| 字段 | 必填 | 说明 |
|---|---|---|
| `instanceId` | 是 | 稳定字符串（如 `"local"` / `"table-1"`），后续所有操作的第一参数 |
| `packs` | 是 | 卡包目录数组（原 `--packs`）。**管理器先注册进资产库，再把它交给实例**——服务器不再自己扫目录 |
| `mode` | 是 | `"play"` / `"sandbox"` |
| `port` | 否 | `0`＝系统分配（推荐） |
| `admission` | 否 | `"auto"`（默认，单机） / `"manual"`（严格，需管理员逐条许可） |
| `runDir` | 否 | 运行目录（日志/临时文件） |
| `drainTimeoutMs` | 否 | 重载/停止时等待在局对局自然终局的上限，默认 30000 |
| `forceAbortOnReload` | 否 | 超时后是否强杀在局对局，默认 `false`（**不腰斩对局**） |

**进程内 API（C#，前端自托管时用这个）**

```csharp
await using var manager = await OrcManager.StartAsync(new ManagerOptions {
    AssetDirs = [packDir],          // 资产库来源（只增不减）
    LogDir    = logDir,
});

var inst = await manager.SpawnAsync(new InstanceSpec {
    InstanceId = "local",
    Packs      = [packDir],
    Mode       = "play",
    Port       = 0,
    Admission  = AdmissionPolicy.Auto,
});
// inst.Port / inst.Room.RoomId / inst.Pool.Generation

// 管理调用（唯一透传入口）
await manager.AdminAsync("local", "room.start");
await manager.AdminAsync("local", "seat.bind", new JsonObject {
    ["seat"] = 1,
    ["controller"] = new JsonObject { ["kind"] = "ai", ["name"] = "AI" },
});
await manager.AdminAsync("local", "pool.reload", new JsonObject {
    ["packs"] = new JsonArray(packDir),
});
```

**CLI（运维/脚本用）**

```bash
orc-manager spawn  --id local --packs ./packs --mode play --port 0 --admission auto
orc-manager list   --json
orc-manager admin  --id local --op room.start
orc-manager admin  --id local --op seat.bind --args '{"seat":1,"controller":{"kind":"ai"}}'
orc-manager admin  --id local --op room.rotate --args '{"force":false}'
orc-manager reload --id local --drain-timeout 30
orc-manager kill   --id local
orc-manager assets register --packs ./packs
orc-manager assets analyze  --packs ./packs-a --packs ./packs-b
orc-manager assets diff     --left a.lock --right b.lock
```

### 8.3 服务器实例内的管理 op（11 个）[P]

> 这些 op **不在** `tools/list` 里，也不注册进工具表。两条调用路径都不可达：
> - 当**工具名**调（`tools/call {name:"seat.bind"}`）→ 门禁拒绝（`unauthorized`）或 `unknown-tool`，**不路由**；
> - 当 **JSON-RPC 方法**调（`{"method":"seat.bind"}`）→ `-32601`。
>
> 它们只能经**父子私有通道**（服务器子进程的 stdin/stdout）由管理器投递。

| op | args | payload | 说明 |
|---|---|---|---|
| `seat.bind` | 见下 | `{seat, roles, seatToken?, controllerKind, viewerCount}` | 绑定即授权（唯一授予手段） |
| `seat.unbind` | 见下 | `{seat, released:[roles]}` | 解绑即收回 |
| `conn.permit` | `{sessionId, roles[]}` | `{sessionId, roles}` | 允许某连接以何角色接入 |
| `conn.revoke` | `{sessionId, reason?}` | `{sessionId}` | 撤销（先推 `notify/session{state:"revoked"}`，宽限 200ms 后断开） |
| `room.start` | `{}` | `{roomId, matchId}` | 开局；前置见下 |
| `room.abort` | `{reason?}` | `{roomId, outcome:"None"}` | 终止在局对局（双方 `outcome=None`）→ 进入轮换 |
| `room.rotate` | `{force?}` | `{roomId, rotationIndex}` | 强制轮换；`in-match` 需 `force:true`（等价 abort+rotate） |
| `pool.reload` | `{packs?[]}` | `{accepted:true, effectiveAt:"rotation"}` | **不在调用栈生效**，登记待生效 |
| `drain` | `{timeoutMs?, force?}` | `{drained:bool, timedOut:bool}` | 拒绝新连接/新入座，等在局终局 |
| `admin.state` | `{}` | 会话表＋绑定拓扑＋待生效队列 | 管理观测（排障用） |
| `admin.ping` | `{}` | `{pong:true, uptimeMs}` | 通道探活 |

**`seat.bind`**

```json
// 绑玩家连接
{"seat":0,"sessionId":"c-7f3a","roles":["controller","viewer"],
 "deck":["…"],"deckConfig":{"mainFaction":"de"},"name":"玩家甲"}

// 绑 AI（托管客户端，不走连接）
{"seat":1,"controller":{"kind":"ai","name":"AI","deck":null}}

// 只加一个 viewer
{"seat":0,"sessionId":"c-9b21","roles":["viewer"]}
```

失败：`seat-taken`（已有 controller）、`insufficient-seat`（seat 无 controller 却要绑 viewer）、`deck-invalid`、`role-conflict`、`room-started`。

**`seat.unbind`**

```json
{"seat":0}                                // 全座解绑（controller + 全部 viewer）
{"seat":0,"sessionId":"c-9b21"}           // 只解这条连接
{"seat":0,"roles":["viewer"]}             // 只解 viewer 角色，controller 保留
```

**`room.start` 的前置条件 [F]**

1. 房间态为 `lobby`；
2. 两个 seat 都有 controller（`player` 或 `ai`）；
3. `roles` 含 controller 的 seat 都已提供卡组（AI 可以缺省，由服务端生成）；
4. **两个 seat 声明的卡包并集可用且一致**（同 id 版本不同＝冲突；未声明 `packs` 的 seat 按实例卡池 lock 计入）；
5. 并集里的资产在服务器卡池中全部存在。

失败码：`room-not-ready`（1/2/3）、`deck-invalid`、`lock-conflict`（4）、`asset-missing`（5，`details.gaps` 给缺口清单）。

**`pool.reload` 的语义 [F]**

- 调用立刻回 `{"accepted":true,"effectiveAt":"rotation"}`，**不立即生效**；
- 有活跃对局时，等到该局终结的轮换点才应用；
- 应用失败（lock 冲突等）→ **保持原卡池可用**，不进半装载态，失败原因经事件回报；
- 应用成功 → `pool.generation +1`，推 `notify/pool` 给所有连接。

### 8.4 父子通道报文 [P]

服务器子进程的 **stdin（命令）** / **stdout（回执＋事件）**，NDJSON，一行一条。stdout 除首行就绪报文外只承载这两类：

```json
// 命令（父 → 子）
{"id":12,"op":"seat.bind","args":{"seat":0,"sessionId":"c-7f3a","roles":["controller","viewer"]}}

// 回执（子 → 父；必有 id）
{"id":12,"ok":true,"code":"ok","message":"","payload":{"seat":0,"roles":["controller","viewer"]}}

// 事件（子 → 父；无 id）
{"event":"room.rotated","payload":{"roomId":"r-0004","rotationIndex":4}}
```

**分流判据只有一条**：**有 `id` ＝应答，无 `id` ＝事件**。

**事件清单**

| event | payload |
|---|---|
| `room.rotated` | `{roomId, rotationIndex}` |
| `match.ended` | `{roomId, matchId, outcome, reason}` |
| `pool.reload` | `{accepted, appliedAt?, generation, packages:[{packageId,version,result,reason?}]}` |
| `seat.bound` / `seat.unbound` | `{seat, sessionId?, roles[]}` |
| `conn.permitted` / `conn.revoked` | `{sessionId, roles?}` |
| `drained` | `{drained, timedOut}` |

> 前端**不需要**读这条通道（它是管理器与服务器之间的私有通道）。列出来是为了让你理解"管理调用为什么不能用 WebSocket/HTTP 打过去"。

### 8.5 业务端口上为什么调不到管理

三条可直接断言的规则 **[F]**：

1. 业务端口 `tools/list` **不包含**任何管理 op；
2. 业务端口调用任何管理 op 名 ⇒ **不路由到管理面**（作为 JSON-RPC 方法 ⇒ `-32601`；作为工具名 ⇒ 门禁拒绝/`unknown-tool`）；
3. 无 stdin 通道的进程（例如手工 `dotnet Orc.Server.dll`）**不具备管理面**，且不因缺少管理面而拒绝服务业务协议。

口径：**远程 ⇔ ①网络可达 ∧ ②调用者非进程主人**。父子通道两条都不满足，所以不算远程。

> 前端不要设计"HTTP/WS 管理后台"打业务端口——那会被当作未知方法。要在远程管服务器，等后续批次的"远程管理协议"（接在管理器上）。

### 8.6 前端最常用的三个管理调用

```csharp
// 开局（座位齐备 + 卡组合法之后）
await manager.AdminAsync("local", "room.start");

// 加个 AI 对手
await manager.AdminAsync("local", "seat.bind", new JsonObject {
    ["seat"] = 1,
    ["controller"] = new JsonObject { ["kind"] = "ai", ["name"] = "AI" },
});

// 换卡包（下一局生效）
await manager.AdminAsync("local", "pool.reload", new JsonObject {
    ["packs"] = new JsonArray(newPackDir),
});
```

---

## 9. 错误码

### 9.1 分层（不变）

| 情况 | 位置 |
|---|---|
| 报文非法 / 未知方法 / 参数形状错 | `error.code`（-32700 / -32601 / -32602 / -32600） |
| **业务失败** | `result.ok = false` ＋ `result.code` |

**只看 `ok` 与 `code`，不要解析 `message`。**

### 9.2 新增码

| code | 含义 | 前端处理 |
|---|---|---|
| `pending-admission` | **成功态**：等管理员许可 | 显示等待，禁用操作，等 `notify/session` |
| `not-admitted` | 连接未被许可 / 已被撤销 | 停止重试，提示联系管理员 |
| `role-conflict` | 角色组合非法（如未绑座却声明 controller+viewer） | 修正 `roles` |
| `role-forbidden` | 当前角色不能做此操作（viewer 提交动作 / controller 读输出） | 修正声明 |
| `already-declared` | 已声明过角色，重复声明 | 忽略；要改角色请重连 |
| `already-bound` | 已绑 seat，不能 `session.attach` | 先 `seat.release` |
| `insufficient-seat` | 该 seat 无 controller，不能挂 viewer | 等 controller 先入座 |
| `lock-conflict` | 双方卡包 lock 冲突（同 id 版本不同） | 显示冲突包清单，回构筑 |
| `asset-missing` | 卡池缺资产，拒绝开局 | 显示 `details.gaps` 缺口清单 |
| `room-rotating` | 轮换窗口内，操作暂不可用 | 200–500ms 后重试一次 |
| `pool-reloading` | 卡池重装窗口内，暂不可开局 | 同上 |

### 9.3 语义变更 / 保留

| code | 变化 |
|---|---|
| `unauthorized` | **收窄**为"seatToken 无效/过期"或"owner 与绑定座位不符"；不再表示"你不是房主" |
| `seat-taken` | 语义扩展为"该 seat 已有 controller" |
| `room-not-ready` | 语义扩展为"seat 缺 controller 或缺卡组" |
| `room-started` | 保留；只拦**新 controller**，不拦新 viewer |
| `busy` / `deck-invalid` / `invalid-arguments` / `unknown-tool` / `not-ready` / `engine-rejected` / `internal` / `targeting` / `cancelled` | 不变 |

### 9.4 管理器侧码表

管理器对外返回自己的码表（子进程码先映射再返回），新增：

| code | 含义 |
|---|---|
| `instance-not-found` | `instanceId` 不存在 |
| `instance-busy` | 该实例正在重载/停止 |
| `instance-not-ready` | 实例已启动但未就绪（等就绪报文超时） |
| `spawn-failed` | 子进程启动失败（`message` 含 stderr 摘要） |
| `pool-invalid` | 卡包注册/依赖分析失败 |

---

## 10. 时序

### 10.1 单机：拉起 + 开一局 + 轮换（形态 A）

```
前端（进程主人）
  │ manager.spawn {instanceId:"local", packs, mode:"play", port:0, admission:"auto"}
  │ ← {port:54321, state:"ready", room:{roomId:"r-0001", rotationIndex:1, state:"lobby"}}
  │
  ├─ client.initialize {protocolVersion:"2.0"}
  │    ← {server:{instanceId:"local", rotationIndex:1}}
  │
  ├─ seat.request {seat:0, roles:["controller","viewer"], name, deck, deckConfig}
  │    ← {code:"ok", details:{seatToken:"…"}}        // 保存
  │
  ├─ seat.request {seat:1, …}（另一个客户端 或 管理面 seat.bind{kind:"ai"}）
  │
  ├─ manager.admin("local", "room.start")
  │    ← notify/match.started {roomId:"r-0001"}
  │    ← notify/view {phase:"mulligan"}               // 收到是因为声明了 viewer
  │
  ├─ mulligan.done / play.* / command.* / match.end_turn …
  │    ← notify/view / notify/segment / notify/target.*
  │
  ├─ 终局：notify/view {phase:"over", outcome:"Win"}
  │    ← notify/match.ended {roomId:"r-0001", outcome:"Win"}
  │    ← notify/room {roomId:"r-0002", rotationIndex:2, seats:[空,空]}
  │
  └─ 前端：丢掉旧 seatToken → 重新 seat.request → 等管理员再 room.start
```

### 10.2 远程加入（形态 C）

```
client.initialize {protocolVersion:"2.0"}
client.seat.request {seat:1, roles:["controller","viewer"], name, deck}
   ├─ admission:"auto"  → {code:"ok", seatToken}          （服务器策略允许）
   └─ admission:"manual"→ {code:"pending-admission"} → 等 notify/session{state:"bound"}
client 等 notify/match.started（开局由服务器侧管理员触发）
```

### 10.3 断线重连

```
检测断开（请求抛异常 / 心跳失败）
  → 显示遮罩
  → 新建连接 + initialize
  → session.status（可选；看 roomId 是否还是你离开时那个）
      ├─ roomId 相同 → seat.request {seatToken}   → 重新绑定，observe.view 拉全量
      └─ roomId 不同 → 旧 token 已失效，走首次 seat.request（不带 token）
  → 若仍在选靶，服务端会重推 notify/target.request
```

> 重连**不要**先 `room.info` 再决定——`session.status` 更直接（它带 `roomId` 与 `seat`）。`room.info` 仍然可用，但它描述的是"房间现在长什么样"，不是"我是谁"。

### 10.4 换卡包（不重启进程）

```
manager.admin("local", "pool.reload", {packs:[…]})
  ← {accepted:true, effectiveAt:"rotation"}
  （若在局中：不生效，等这一局打完）
  → 终局 → 轮换点
  ← notify/pool {generation:5, fingerprint:"…"}
  ← notify/room {roomId:"r-0004", pool:{generation:5, …}}
前端：刷新 cardId → 美术/文本本地表（按新 generation 缓存）
```

### 10.5 热更（换 dll，不重启管理器）

```
manager.reload {instanceId:"local", drainTimeoutMs:30000, force:false}
  → drain：拒绝新连接/新入座，等在局自然终局或超时
  → Kill(entireProcessTree) → WaitForExit → 用新 dll 起新进程
  ← {port:54321, ready:{…}}        // 端口复用
前端：连接会断 → 走重连流程；旧 seatToken 失效（新进程新房间）
```

---

## 11. 适配清单（逐条打勾）

### 11.1 必改 10 条

| # | 改什么 | 具体动作 |
|---|---|---|
| 1 | 协议版本 | `initialize` 传 `"2.0"`；解析 `server.instanceId` / `rotationIndex` |
| 2 | 占座 | `room.claim` → `seat.request`（`roles` 必填） |
| 3 | 重连 | `room.resume` → `seat.request {seatToken}`；先判 `roomId` |
| 4 | 让座 | `room.release` → `seat.release` |
| 5 | 开局 | 删除前端 `room.start`；改调管理器 `room.start`（形态 A）或等推送（形态 B/C） |
| 6 | 踢人 / 加 AI | 改调管理器 `seat.unbind` / `seat.bind{kind:"ai"}`（仅形态 A） |
| 7 | 座位面板 | 读 `seats[].controller`（可为 `null`）+ `seats[].viewers[]` + `deck.provided` |
| 8 | 终局 | 处理 `notify/room` 的 `roomId` 变化 → 清 token、清面板、重新入座 |
| 9 | 错误码 | 按 §9 更新 switch；把 `pending-admission` 当成功态 |
| 10 | 进程生命周期 | 关窗/切场景显式 `manager.kill`；不要依赖"终局自动退出" |

### 11.2 前端状态机（新版）

```
Disconnected
   │ connect + initialize(2.0)
   ▼
Connected ──room.info──> RoomState(roomId, rotationIndex, state)
   │
   ├─ seat.request ──> PendingAdmission ──notify/session{bound}──> Bound(seat, seatToken, roles)
   ├─ session.attach{roles:["viewer"]} ──> OmniscientViewer
   │
   Bound ──notify/match.started──> InMatch(phase)
   │        ├─ mulligan：换牌界面
   │        ├─ play：出牌/指挥/选靶
   │        └─ over：终局面板
   │
   ├─ notify/room{roomId 变} ──> Rotated（清 token、回 RoomState）
   ├─ notify/session{unbound|revoked} ──> 回 Connected
   └─ 连接断开 ──> Disconnected（保留 token + roomId，用于判断能否重绑）
```

### 11.3 座位面板改法

**旧**

```csharp
foreach (var s in info.Seats) {
    var kind = s.ControllerKind;         // "local" / "remote" / "ai" / ""
    var name = s.DisplayName;
    var connected = s.Connected;
    bool occupied = kind != "";
}
```

**新**

```csharp
foreach (var s in info.Seats) {
    var ctrl = s.Controller;             // 字段缺失/反序列化为 null = 空座
    bool occupied = ctrl is not null;
    var kind = ctrl?.Kind;               // "player" / "ai"
    var name = ctrl?.DisplayName;
    var connected = ctrl?.Connected ?? false;
    int viewers = s.ViewerCount;
    bool deckReady = s.Deck.Provided;     // 开局前必须 true
}
```

**UI 要点**

- 空座：显示"申请入座"（玩家控制器）或"添加 AI"（仅形态 A 有管理权）。
- **有 controller 但 `connected == false`：显示"离线"，不是空座**（座位不会因断线自动释放，见 §5.7）。
- 有 controller 的 seat：显示 `viewers` 数量（"2 人观战"）。
- "开始"按钮：**仅形态 A 显示**，且条件＝两 seat 都有 controller 且 `deck.provided == true`。
- 不要显示 `ready` 字段（从未存在）；不要用 `controllerKind == "local"` 判断房主（概念已删除）。

### 11.4 终局 / 轮换改法

```csharp
void OnNotifyRoom(RoomSnapshot room) {
    if (room.RoomId != _currentRoomId) {
        // 轮换了
        _seatToken = null;          // 旧令牌已失效
        _currentRoomId = room.RoomId;
        CloseMatchPanel();
        ShowSeatPanel(room);
        return;
    }
    UpdateSeatPanel(room);
}
```

```csharp
void OnNotifyMatchEnded(MatchEndedPayload p) {
    ShowResultPanel(p.Outcome);     // "Win" / "Lose" / "None"
    // 不要在这里重新入座：等 notify/room 带来新 roomId
}
```

**注意**：`notify/match.ended` 与 `notify/room`(新 roomId) 是**两条**推送，顺序上先 ended 后 room。不要只处理其中一条。

### 11.5 重连改法

```csharp
async Task ReconnectAsync() {
    await _client.DisposeAsync();
    _client = await OrcClient.ConnectAsync(new ClientOptions { Host = _host, Port = _port });
    await _client.InitializeAsync(new InitializeParams { ClientName = "godot", ProtocolVersion = "2.0" });

    var st = await _client.CallAsync("session.status");
    var sameRoom = st.Details?["roomId"]?.GetValue<string>() == _currentRoomId;

    var req = new JsonObject { ["roles"] = new JsonArray("controller", "viewer") };
    if (sameRoom && _seatToken is not null) {
        req["seatToken"] = _seatToken;
    } else {
        req["seat"] = _seat;
        req["deck"] = _deck;
    }

    var r = await _client.CallAsync("seat.request", req);
    if (r.Code == "unauthorized" && req.ContainsKey("seatToken")) {
        // token 失效 ⇒ 房间已轮换，退回首次绑定（seat + deck 必须在手）
        req.Remove("seatToken");
        req["seat"] = _seat;
        req["deck"] = _deck;
        _seatToken = null;
        r = await _client.CallAsync("seat.request", req);
    }
    if (r.Ok && r.Code == "ok") _seatToken = r.Details?["seatToken"]?.GetValue<string>();

    await PullFullViewAsync();                      // observe.view 拉全量
}
```

---

## 12. 旧→新对照表（逐调用 / 逐字段）

### 12.1 调用

| 旧 | 新 | 备注 |
|---|---|---|
| `room.info` | `room.info` | 载荷 shape 变（§6.1） |
| `room.claim {seat, kind, name, deck, deckConfig}` | `seat.request {seat, roles:["controller","viewer"], name, deck, deckConfig, packs?}` | 删除 `kind`；`packs` 为可选新增 |
| `room.resume {seatToken}` | `seat.request {seatToken}` | `seat` 可省略 |
| `room.release {seat, seatToken}` | `seat.release {seat}` | 不需要 token（绑定身份已确定） |
| `room.start {seatToken}` | 管理面 `room.start {}` | 前端不再能调 |
| `room.kick {seat, seatToken}` | 管理面 `seat.unbind {seat}` | 仅形态 A |
| `room.add_ai {seat, name, deck}` | 管理面 `seat.bind {seat, controller:{kind:"ai",…}}` | 仅形态 A |
| `observe.view {seat}` | `observe.view {seat?}` | 未声明连接不再放行 |
| `target.claim` | — | 删除 |
| `service.info` / `service.tools` | 不变 | 建议加读 `instanceId` |
| `docs.list` / `docs.read` | 不变 | — |

### 12.2 字段

| 旧字段 | 新字段 |
|---|---|
| `room.packPolicy` | `room.pool.packPolicy` |
| `room.packFingerprint` | `room.pool.fingerprint` |
| `room.seats[].controllerKind` | `room.seats[].controller.kind`（值域 `player`/`ai`） |
| `room.seats[].displayName` | `room.seats[].controller.displayName` |
| `room.seats[].connected` | `room.seats[].controller.connected` |
| —（无） | `room.seats[].viewers[]` / `viewerCount`（新增） |
| —（无） | `room.seats[].deck.provided` / `cardCount`（新增） |
| —（无） | `room.instanceId` / `roomId`（轮换会变）/ `rotationIndex` / `pool.generation`（新增） |
| `notify/view.seat` 恒 0/1 | 绑座仍是 0/1；全知为 `null` |
| —（无） | `notify/view.perspective` / `roomId`（新增） |

### 12.3 服务启动参数

| 旧 | 新 |
|---|---|
| `--packs <dir;dir>` 由服务器自己扫 | **由管理器注册进资产库并交给实例**（`manager.spawn` 的 `packs`） |
| `--parent-pid` 由 `ServerProcess` 自动加 | 由管理器维护（子进程生命周期与实例绑定） |
| `--exit-on-end` 无头默认 | 默认常驻轮换；`--exit-on-end` 仅为一次性脚本保留 |
| `--seat0 ai\|agent` / `--seat1` | 保留（无头夹具用）；常规路径改用 `seat.bind` |
| `--mode play\|sandbox` / `--allow-debug-writes` | 保留（调试面判据不变） |

---

## 13. 不要做的事（反模式）

1. **不要写 v1 兼容探测**：先试 `room.claim` 失败再试 `seat.request`——旧的只会失败，且失败码有两种（`unauthorized` / `unknown-tool`），永远不会有成功分支。
2. **不要用 `controllerKind == "local"` 判断谁能开局**：房主已废弃；能开局的只有持管理器的一方。
3. **不要假设终局后服务器退出**：它活着，还会推新 `roomId`。
4. **不要复用旧 `seatToken`**：跨房间必失效，`unauthorized` 是正常现象。
5. **不要把 `pending-admission` 当错误**：它是成功态，重试只会重复申请。
6. **不要只声明 `["controller"]` 还指望收 `notify/view`**：收不到是设计。
7. **不要在业务端口调管理 op**（`room.start` / `seat.bind` / `pool.reload`）：作为方法＝`-32601`，作为工具名＝门禁拒绝；两条都没有兼容入口。
8. **不要用 `roomId` 做长期持久化键**（会变）；用 `instanceId + roomId` 组合，或干脆不持久化。
9. **不要轮询 `observe.view` 探测状态**：所有状态变化都有推送（`notify/room` / `notify/session` / `notify/view`）。
10. **不要在同一连接上"一边打牌一边全知"**：`role-conflict` 直接拒绝。

---

## 14. 待裁决项对前端的影响

| # | 待裁决 | 本文默认假设 | 若裁决不同，前端改哪里 |
|---|---|---|---|
| U3 | 角色声明与接入许可的时序 | 三段式，但默认 `admission:"auto"` 让 `seat.request` 一次完成许可+绑定 | 若默认改 `manual`：所有首次入座都会先收 `pending-admission`，需加"等待批准"UI 与 `notify/session` 处理（§5.2 回执 B） |
| U4 | "双方 lock 并集"的双方与对账方向 | 两个 seat 的参与者各自声明的卡包集合，双向并集 | 只影响 `room.start` 的失败码与 `details.gaps` 形状；前端展示逻辑不变 |
| U5 | 管理器 MCP 入口本轮是否交付 | 不交付（只有进程内 API + CLI） | 若交付：形态 A 可以用 MCP 远程调管理面，前端可多一条部署路径；不影响客户端协议 |
| — | 全知视图的具体 shape | 复用 `UiMatchView`（self=seat0）+ `enemyHand` | 只影响观战 UI；本轮不在验收范围 |

---

## 15. 附录

### 15.1 协议常量

- `jsonrpc`: `"2.0"`
- 线协议版本：`"2.0"`
- 帧格式：一行一个 JSON（UTF-8，LF），单帧上限 1 MiB（按字节计；超过即断开）
- 业务推送：`notify/room`、`notify/session`、`notify/match.started`、`notify/view`、`notify/segment`、`notify/target.request`、`notify/target.expired`、`notify/match.ended`、`notify/pool`
- 管理事件（仅父子通道）：`room.rotated`、`match.ended`、`pool.reload`、`seat.bound`、`seat.unbound`、`conn.permitted`、`conn.revoked`、`drained`

### 15.2 字段表：`notify/session`

| 字段 | 类型 | 说明 |
|---|---|---|
| `state` | string | `pending` / `admitted` / `bound` / `unbound` / `revoked` / `rejected` |
| `sessionId` | string | 连接 id |
| `requestId` | string? | 对应 `seat.request` 的 `requestId`（仅在 manual 策略的申请流程里出现） |
| `seat` | int? | 绑定的座位；未绑为 `null` |
| `roles` | string[] | `controller` / `viewer` |
| `perspective` | string? | `seat` / `omniscient` |
| `seatToken` | string? | 仅 `bound` 时有值 |
| `roomId` | string | 当前房间 |
| `rotationIndex` | int | 当前房间序号 |
| `reason` | string? | `unbound` / `revoked` / `rejected` 的原因 |

### 15.3 字段表：`notify/pool`

| 字段 | 类型 | 说明 |
|---|---|---|
| `generation` | int | 新代次（单调递增） |
| `fingerprint` | string? | 新指纹 |
| `effectiveAt` | string | `"rotation"`（固定） |
| `appliedAt` | string? | 实际生效时间（ISO 8601） |

> 包级明细（哪些包成功/失败）只在管理面 `pool.reload` 的事件里，不下发客户端——前端只需要知道"卡池换代了，刷新本地卡表"。

### 15.4 参考实现（本仓）

| 内容 | 路径 |
|---|---|
| 协议信封 / 错误码 / NDJSON | `src/Orc.Protocol/` |
| 客户端库 | `src/Orc.Client/`（`OrcClient` / `ServerProcess`） |
| 服务端宿主（分发/推送/门禁） | `src/Orc.Server/Transport/ServerHost.cs` |
| 座位作用域（将改造为角色门禁） | `src/Orc.Server/Transport/SeatScopeRegistry.cs` |
| 房间状态机 | `src/Orc.Server/Room/Room.cs` |
| 房间快照 DTO | `src/Orc.Mcp/Room/RoomContracts.cs`、`RoomSnapshotJson.cs` |
| 投影 + 动作可用性 | `src/Orc.Mcp/McpService.Projection.cs` |
| 投影 DTO（字段权威） | `src/Orc.UI.Contracts/UiViewModel.cs` |
| 端到端示例 | `tests/Orc.Server.Tests/EndToEndTests.cs` |
| 协议契约（原始报文） | `tests/Orc.Server.Tests/RawProtocolContractTests.cs` |

### 15.5 客户端 API 速查（一页，可直接打印）

**协议方法**

| 方法 | 用途 |
|---|---|
| `initialize` | 握手（`protocolVersion:"2.0"`） |
| `ping` | 心跳 |
| `tools/list` | 取当前角色可见的工具（按角色过滤） |
| `tools/call` | 调工具（`{name, arguments}`） |

**会话 / 座位**

| 工具 | 参数要点 |
|---|---|
| `seat.request` | `{seat?, roles, name?, deck?, deckConfig?, packs?, seatToken?}` |
| `seat.release` | `{seat?}` |
| `session.attach` | `{roles:["viewer"]\|["controller"], name?}` |
| `session.status` | 无参 |

**房间 / 观察 / 动作 / 诊断**

| 工具 | 备注 |
|---|---|
| `room.info` | 房间快照（新 shape，§6.1） |
| `observe.view` | `{seat?}`；绑座只能读自己 |
| `play.unit_pre` / `play.unit` / `play.command_pre` / `play.command` / `play.counter` / `play.join` | 动作（`owner` 可省略） |
| `command.begin` / `move` / `attack` / `leave` | 动作 |
| `match.end_turn` / `match.concede` | 动作 |
| `mulligan.begin` / `mulligan.done` | 动作 |
| `target.submit` / `target.cancel` | `{sessionId, picks[]}` / `{sessionId}` |
| `play.can_play` / `command.availability` | 只读 |
| `service.info` / `service.tools` / `docs.list` / `docs.read` | 诊断 |

**推送**

| 推送 | 谁收 |
|---|---|
| `notify/room` | 全部连接 |
| `notify/session` | 该连接自己 |
| `notify/match.started` / `notify/match.ended` | 全部连接 |
| `notify/view` / `notify/segment` | 仅 viewer 角色 |
| `notify/target.request` / `notify/target.expired` | 仅该 seat 的 controller |
| `notify/pool` | 全部连接 |

### 15.6 术语速查（自包含）

| 术语 | 一句话 |
|---|---|
| **管理器** | 常驻的对局环境管理器，持有资产库，托管多个服务器实例；不持有对局活对象 |
| **实例标识** | 管理器内唯一标识一台服务器的稳定字符串（不是端口/进程号） |
| **服务器** | 承载引擎、房间与唯一对局的进程；同实例内至多 1 个非终结房间 |
| **房间** | 服务器的对局容器，恒 2 个 seat；终局不可回退，靠"房间轮换"续服 |
| **房间轮换** | 终局后销毁旧房间、建新 lobby 房间；座位重建、卡组重新提供、token 失效 |
| **seat** | 房间内的玩家位；持 1 个 controller + 0..N viewer，承载该玩家的对局设施 |
| **controller** | 引擎输入端，只推输入不接输出；绑座＝玩家面，不绑＝调试面 |
| **viewer** | 引擎输出端，只收推送不输入；绑座＝该座视角（脱敏），不绑＝全知 |
| **管理员** | 服务器进程的启动者，正交于 seat；管绑定拓扑、连接许可、房间生命周期、卡池重装 |
| **进程主人** | 持有服务器进程控制权的主体；本项目唯一授权来源 |
| **绑定拓扑** | controller/viewer 与 seat 的绑定关系；绑定即授权，解绑即收回 |
| **连接许可** | 管理员决定某连接能否接入、以何角色接入并可撤销的机制 |
| **卡池重装** | 轮换窗口内用变更后的卡包重建卡池；失败保持原卡池 |
| **资产库** | 管理器持有的进程级只读资产集合，只增不减 |

---

## 16. 一句话交付口径

> **对局载荷不动，外壳换掉；开局权归管理员，座位靠申请，终局靠轮换，重连靠 token。**
>
> 前端改动集中在 10 个点（§11.1）。渲染、输入、动画、卡面查表、`view`/`actions` 消费逻辑**不需要动**；要动的是"连接之后怎么入座、什么时候能开局、终局之后怎么办"这三段外壳。
