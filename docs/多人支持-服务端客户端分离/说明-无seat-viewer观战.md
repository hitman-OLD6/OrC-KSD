# 说明：play 模式下「无 seat 的 viewer」观战

> 本文回答一个具体争议：**前端反馈「play 模式拒绝无 seat 的 viewer」**。
> 结论先说：**服务端支持，`--mode play` 不是拒绝的判据；被拒发生在另外五个位置。**
> 事实来源：`src/Orc.Server/Transport/ServerHost.cs`、`src/Orc.Protocol/SessionMessages.cs`、`src/Orc.Manager/ManagerSessions.cs`、`src/Orc.Mcp.Bridge/BridgeOptions.cs`，以及 2026-10-22 的实测脚本 `outputs/verify_omniscient_viewer.py`。

---

## 0. 一句话结论

| 问题 | 答案 |
|---|---|
| play 模式支持无 seat viewer 吗？ | **支持**。批准后状态是 `omniscient`，收全知视图帧与全量事件段。 |
| 需要占座位吗？ | **不需要**。一个 seat 可以挂 0..N 个 viewer，viewer 不参与座位占用判定。 |
| 需要什么前置？ | **必须经管理员 `conn.admit` 批准**。attach 只返回 `pending`。 |
| 为什么前端说被拒绝？ | 见 §3 的五个现场——没有一个是 `ServerMode.Play` 造成的。 |

实测输出（play 模式，两个 AI 座位，`deck-rules none`）：

```text
[ok] attach(viewer, 无 seat)        -> state=pending seat=None code=ok
[probe] pending + observe.view      -> ok=False code=not-admitted
[ok] conn.admit                     -> ok=True state=omniscient seat=None roles=['viewer']
[ok] notify/admission               -> decision=admitted roles=['viewer'] seat=None
[ok] notify/view                    -> perspective={'mode': 'omniscient'} views=2
[ok] observe.view                   -> ok=True perspective={'mode': 'omniscient'} views=2
[ok] room.info                      -> seats(deck/packages)=[(0, None, None), (1, None, None)]
```

复现命令：`python outputs/verify_omniscient_viewer.py`（会自动起一个 play 实例、走管理通道批准、跑完全链路后退出）。

---

## 1. 术语纠偏：角色（role）≠ 状态（state）

这是最容易踩错的一处。协议里有两个名字相近但**用途完全不同**的枚举：

| 枚举 | 取值 | 用途 |
|---|---|---|
| `SessionRoles`（`SessionMessages.cs:6-16`） | `controller` / `viewer` / `debug` | **只能**出现在 `session.attach` 的 `roles` 里 |
| `SessionStates`（`SessionMessages.cs:19-41`） | `connected` / `pending` / `admitted` / `viewer` / `omniscient` / `debug` / `revoked` | `session.attach` 的返回、`notify/admission` 的载荷、`connections.list` 的 `state` |

**`omniscient` 不是可申请角色，只是「viewer 且未绑 seat」被批准后的状态名。**
`ServerHost.AttachAsync` 第一件事就是过滤角色（`ServerHost.cs:474-482`）：

```csharp
var roles = request.Roles
    .Where(role => role is SessionRoles.Controller or SessionRoles.Viewer or SessionRoles.Debug)
    .Distinct(StringComparer.Ordinal)
    .ToArray();

if (roles.Length == 0)
    return AttachFailed(session, "session.attach 需要至少一个角色：controller / viewer / debug。");
```

所以 `roles:["omniscient"]` 会被**静默过滤成空集**，然后报「需要至少一个角色」。实测：

```text
[probe] attach(roles=[omniscient]) -> state=connected code=invalid-arguments
        message=session.attach 需要至少一个角色：controller / viewer / debug。
```

注意 `state` 仍是 `connected`——这次失败**没有改变连接状态**，可以立刻改声明重试。

正确写法只有一种：

```json
{"roles":["viewer"]}                  // 不传 seat  ⇒ 批准后 omniscient（全知）
{"roles":["viewer"], "seat":0}        // 传 seat    ⇒ 批准后 viewer（单视角、脱敏）
```

---

## 2. 正确接入（实测报文）

### 2.1 握手

```json
→ {"jsonrpc":"2.0","id":"1","method":"initialize","params":{
     "clientName":"godot","protocolVersion":"1.0","label":"omniscient"}}
← {"jsonrpc":"2.0","id":"1","result":{"protocolVersion":"1.0",...,"connectionId":"cb447e…"}}
```

> `protocolVersion` 必须是 **`1.0`**（`ProtocolInfo.ProtocolVersion`）。填 `2.0` 会得到 JSON-RPC 错误 `-32602`「线协议版本不匹配：客户端 2.0，服务端 1.0。」——现行接入指南里的 `2.0` 示例是错的，见 §8。

### 2.2 申请（立即返回，不挂起）

```json
→ {"jsonrpc":"2.0","id":"2","method":"session.attach","params":{
     "roles":["viewer"],"displayName":"观战者"}}
← {"jsonrpc":"2.0","id":"2","result":{
     "connectionId":"cb447e…","state":"pending","seat":null,
     "code":"ok","message":"申请已提交，等待管理员批准。"}}
```

**关键**：`code` 是 `ok`，`state` 是 `pending`。这不是拒绝，是「已受理、待批准」。

### 2.3 管理员批准

管理面不在业务端口上（父子私有通道 / 管理器 API / 管理器 CLI，三者同一真身）：

```json
{"id":2,"op":"conn.admit","args":{"connectionId":"cb447e…"}}
→ {"id":2,"ok":true,"payload":{"state":"omniscient","seat":null,"roles":["viewer"]}}
```

批准后服务端自己推 `notify/admission` 到**该连接**：

```json
{"jsonrpc":"2.0","method":"notify/admission","params":{
  "decision":"admitted","roles":["viewer"],"seat":null}}
```

### 2.4 观战数据

| 通道 | 形状 | 触发时机 |
|---|---|---|
| `notify/view` | `{perspective:{mode:"omniscient"}, views:[{seat:0,view,actions},{seat:1,view,actions}]}` | 每次动作成功 / 批准后立即补推 |
| `notify/segment` | `{perspective:{mode:"omniscient"}, segments:[…]}`（**全量段，不脱敏**） | 每个 work item 尾部取段后 |
| `observe.view` | 与推送同形（`details.perspective.mode = "omniscient"`） | 主动拉取时 |

视角由**角色态**决定，不由参数决定（`ObserveTools.cs:147-168`）：

```
DebugFace        -> ProjectDebugSummary()      // 调试摘要，含双方手牌明细
Seat is not null -> ProjectSeatFrame(seat)     // 单视角帧
Seat is null     -> ProjectOmniscientFrame()   // 全知帧（两份视角打包）
```

---

## 3. 五类「看起来像被拒绝」的现场

按前端实际撞到的概率排序。**逐条给出「谁拒的」**——这是本文的核心。

### 3.1 把 `omniscient` 当角色传（服务端拒绝，但理由不是 play 模式）

- **现象**：`session.attach` 返回 `code:"invalid-arguments"`，文案「需要至少一个角色」。
- **原因**：见 §1，`omniscient` 被角色过滤器丢弃。
- **修复**：`roles:["viewer"]`，不传 `seat`。

### 3.2 attach 后停在 `pending`，没走 `conn.admit`（最常被误读为「拒绝」）

- **现象**：attach 成功，但之后调 `observe.view` 拿到失败；同时收不到任何 `notify/*`。
- **实测原文**：

```text
[probe] pending + observe.view -> ok=False code=not-admitted
        message=申请尚未获批准：当前只能 ping / session.detach / room.info。
```

- **原因**：`ServerHost.Gate`（`ServerHost.cs:1023-1042`）对 `connected` / `pending` 一律 `not-admitted`，且优先级高于工具角色判定——**任何**工具（含只读的 `observe.view`）都过不去。这不是「viewer 角色不够」，是「还没批准」。
- **另一个坑**：`notify/*` 在 `pending` 阶段是**零推送**（`Allows` 在 `ServerHost.cs:2002-2006` 直接返回 false），前端会看到「连上了但没数据」，容易判成「观战不受支持」。
- **修复**：让管理员执行 `conn.admit`（管理器：`manager.admin`，op `conn.admit`）。

### 3.3 用 `Orc.Mcp.Bridge` 的 `--role viewer` 却不带 `--seat`（**客户端自己拒的**）

`BridgeOptions.Parse`（`BridgeOptions.cs:109-116`）把 `viewer` 定义为「绑座 viewer」，强制要求 `--seat`：

```csharp
if (role is "controller" or "both" or "viewer")
{
    if (seat is not (0 or 1))
    {
        error = $"--role {role} 需要 --seat 0|1（seat 绑定决定视角与输入归属）。";
        return null;
    }
}
```

- **现象**：桥进程**根本没发出 `session.attach`** 就退出，stderr 是上面那句。
- **原因**：桥的 CLI 语义里 `viewer` ≠ `omniscient`。无 seat 必须显式用 `--role omniscient`：

```csharp
"omniscient" => new[] { SessionRoles.Viewer },   // BridgeOptions.cs:185
```

且 `--role omniscient` **禁止**带 `--seat`（`BridgeOptions.cs:118-122`）。
- **修复**：`--role omniscient`；或直连协议时用 `roles:["viewer"]` 且不传 seat。

### 3.4 `manager.connect` 不传 `roles` 也不传 `seat`（**管理器缺省成 debug 面**）

`ManagerSessions.ConnectAsync`（`ManagerSessions.cs:47-55`）：

```csharp
var roles = ReadStrings(args["roles"]);
if (roles.Count == 0)
{
    // 缺省：给了 seat 就是兼任的玩家（否则收不到自己的棋盘），没给就是调试面。
    roles = seat is null
        ? [SessionRoles.Debug]
        : [SessionRoles.Controller, SessionRoles.Viewer];
}
```

- **现象 A（远程前端）**：`session.attach` 返回「调试面只接受回环来源的编排连接（当前来源不是回环）。」→ 看起来就是「无 seat 被拒绝」。
- **现象 B（本机前端）**：debug 面申请即生效（**不走批准**），但 `debug` **不收任何对局推送**（`PushViewsAsync` 只分发给 `Omniscient` 与绑座 `Viewer`），只有 `observe.view` 能拿到「调试摘要」。前端若按「有 `views` 就是全知帧」判断，会一直等不到。
- **修复**：显式传 `roles:["viewer"]`、不传 `seat`；管理器会自动 `conn.admit`（`ManagerSessions.cs:90-105`）。

### 3.5 房间轮换后被 `revoke`

- **现象**：一局结束后，观战连接再调工具返回 `not-admitted`「连接许可已被撤销（请重新 session.attach 申请）。」
- **原因**：`RotateAsync`（`ServerHost.cs:1490-1507`）的放行条件是
  `session.Seat is null && !session.IsController && !session.IsViewer` → `continue`。
  `omniscient` 虽然没有 seat，但 `IsViewer == true`，会落进下面的 revoke 分支，被清空角色并置 `revoked`。注释写的是「绑定 seat 的角色 → Revoked」，实际把无 seat viewer 也带上了。
- **修复**：轮换后重新 `attach` + `admit`；或者按 §9 修服务端（需产品决策）。

---

## 4. 为什么会被归因到「play 模式」

因为**现象确实发生在 play 实例上**，但因果链是「未批准 / 角色写错 / 客户端 CLI 限制」，不是 `ServerMode.Play`。

`ServerHost` 里对 `Options.Mode` 的引用数量是 **0**（门禁、状态机、推送都不看 mode）。mode 只影响两件事：

| 维度 | `--mode play` | `--mode sandbox` |
|---|---|---|
| 服务层工具面 | `Player`：**不注册**调试类工具 | `Authoring`：注册调试读类（写类需 `--allow-debug-writes`） |
| `observe.view` 视角 | 同上分流（绑座=单视角 / 无座=全知） | **完全相同**（无差异） |
| 无 seat viewer | **支持**（`omniscient`） | 支持（`omniscient`） |

换句话说：sandbox 下如果同样停在 `pending`，`observe.view` 一样返回 `not-admitted`。

### 文档层面的两个误导源

1. **`docs/多人支持-服务端客户端分离/客户端接入指南.md` 是 v1 协议且未标作废**。它讲的是 `room.claim` / `room.resume` / `room.kick`（现行协议已删除），第 235 行还写着：

   > `observe.view`：… 未绑定 → 放行（单机诊断），且不绑定座位。

   现行实现是「未绑定 = `pending`/`connected` → `not-admitted`；无 seat 但**已批准** → `omniscient` 放行」。两者结论相反，照 v1 实现必然对不上。
   （v2 的现行指南在 `docs/服务器管理器-长期生命周期/客户端接入指南.md`，第 4 节明确写了「只 `viewer` 且不带 `seat` ⇒ 全知（仍需管理员批准）」。）

2. **`docs/无头沙盒-测试环境/requirements.md` R5 的旧决策**：

   > `play` 下未绑定座位的会话调用 `observe.view` → **拒绝**；`sandbox` 下未绑定会话 → 放行且全知。

   这条写在 omniscient 概念引入之前，所指的「未绑定会话」= 今天的 `connected`/`pending`。`Session.Allows` 落地后，它的语义已被「批准与否」取代，「mode」不再是判据。该条若继续被当现行依据，就会直接推出「play 模式拒绝无 seat 观战」的错误结论。

---

## 5. 全知 viewer 的能力边界

| 能力 | 全知 viewer | 说明 |
|---|---|---|
| `notify/view` 全知帧 | ✅ | 一次给两份座位视角（`views[2]`），因此**双方手牌都可见**（各自视角里是自己的手牌） |
| `notify/segment` | ✅ 全量段 | 不做 visibility 过滤 |
| `observe.view` | ✅ | 与推送同形 |
| `room.info` | ✅ | 但 `deck` / `packages` 一律 `null` |
| `play.*` / `command.*` / `match.*` / `mulligan.*` / `target.*` | ❌ | 需 `ToolRole.Controller` 且绑 seat（`ServerHost.cs:2011`） |
| 调试工具（`shape.*` / `card.*` / `sandbox.*` …） | ❌ | play 模式不注册；且需 `debug` 面 |
| 影响对局 | ❌ | viewer 断开不触发宽限/认输（`ServerHost.cs:856-861`） |

**「全知」的边界是构筑隐私。**

`Room.Snapshot`（`Room.cs:135-143`）：

```csharp
var own = request.SeeAllDeclarations || request.Seat == i;
seats[i] = new RoomSeatSnapshot(..., own ? slot.Deck : null, own ? ToPackageSnapshots(slot.Packages) : null);
```

`omniscient` 的 `Seat` 是 `null`，`SeeAllDeclarations` 只在 `debug` 面为真 → 两个座位的 `deck`/`packages` 都是 `null`。实测：

```text
[ok] room.info -> seats(deck/packages)=[(0, None, None), (1, None, None)]
```

这是 R12 的显式设计（全知是对局输出，不是构筑隐私），不是缺陷。需要看构筑只能用 `debug` 面。

---

## 6. 前端自查判定树

按顺序走，第一个「否」就是被拒的位置：

```text
1. initialize 成功？            ── 否 → protocolVersion 必须是 "1.0"（不是 2.0）
2. attach 返回 code=ok 且        ── 否 → roles 写错（是否传了 "omniscient"？）
   state=pending？
3. 收到 notify/admission        ── 否 → 管理员没批准：connections.list 看该连接是否仍 pending
   decision=admitted？
4. 回执 payload.state           ── 否 → 批准时给了 seat（变成 viewer 单视角），或 roles 不是 viewer
   = omniscient？
5. 对局是否 in-match？          ── 否 → lobby 阶段无 notify/view，只能 room.info（这不是拒绝）
6. 是否刚经历过 room.rotated？  ── 是 → 被 revoked，重新 attach + admit
```

补充：`connections.list` 的每一项都有 `state` / `roles` / `seat`，前端自检时优先看它，比猜推送更直接。

---

## 7. 验证与回归

| 方式 | 命令 | 覆盖点 |
|---|---|---|
| 端到端实测 | `python outputs/verify_omniscient_viewer.py` | attach→pending→admit→omniscient→全知帧→observe.view→声明不可见；附角色陷阱与绑座对照 |
| 单测（服务端） | `dotnet test tests/Orc.Server.Tests --filter "FullyQualifiedName~RoomRotationTests"` | `ViewerDistribution_SeatVsOmniscient_FramesUsePerspectiveMode`、`ViewerOnly_Omniscient_CannotSeeOpponentDeclarations`（均以 `ServerMode.Play` 启动） |

实测脚本的完整断言（当前全部 PASS）：

1. `attach(viewer, 无 seat)` → `state=pending`、`code=ok`；
2. `pending` + `observe.view` → `not-admitted`（记录真实拒绝点）；
3. `conn.admit` → `state=omniscient`、`seat=null`、`roles=["viewer"]`；
4. `notify/admission` → `decision=admitted`；
5. `notify/view` → `perspective.mode=omniscient`、`views` 长度 2；
6. `observe.view` → 同上；
7. `room.info` → 双方 `deck`/`packages` 均 `null`；
8. `roles:["omniscient"]` → `invalid-arguments`（状态不变）；
9. `viewer + seat=0` → `state=viewer`、`observe.view` 返回 `mode=seat` 且**没有** `views` 字段。

---

## 8. 相关文档勘误（建议尽快修）

| 文件 | 位置 | 问题 | 应为 |
|---|---|---|---|
| `docs/多人支持-服务端客户端分离/客户端接入指南.md` | 全文 | v1 协议（`room.claim`/`room.resume`），且第 235 行「未绑定 → 放行」与现行相反 | 顶部加「已作废，见 `docs/服务器管理器-长期生命周期/客户端接入指南.md`」横幅 |
| `docs/服务器管理器-长期生命周期/客户端接入指南.md` | §3 握手示例 | `"protocolVersion":"2.0"` | `"1.0"`（实测 2.0 直接 `-32602`） |
| `docs/无头沙盒-测试环境/requirements.md` | R5「observe.view 分流」 | 「play 下未绑定 → 拒绝」按 mode 归因，已被 `Session.Allows` 取代 | 改为「未批准（pending/connected）→ `not-admitted`；已批准的 `omniscient` → 放行」 |

---

## 9. 可选的后端改进（需产品决策，未实施）

1. **让 `roles:["omniscient"]` 直接报可读错误**，而不是先过滤成空集再说「需要至少一个角色」。当前文案会让前端以为是角色清单问题，而不是「omniscient 不是角色」。
2. **`BridgeOptions` 的 `--role viewer` 文案补一句**：「无 seat 观战请用 `--role omniscient`」，避免用户以为是服务端拒绝。
3. **`RotateAsync` 不要 revoke 无 seat viewer**：把放行条件从
   `Seat is null && !IsController && !IsViewer` 改为 `Seat is null && !IsController`（或显式排除 `Omniscient`），让观战端跨局存活。当前行为与注释不一致，属于实现与设计意图的偏差。
4. **`manager.connect` 增加 `role` 别名**：目前 `{seat:null}` 被缺省解释成 `debug`，容易让「想观战」的调用方拿到调试面。

---

## 附：一页速查

```text
想全知观战（play / sandbox 都一样）：
  1. initialize { protocolVersion: "1.0" }
  2. session.attach { roles: ["viewer"] }        // 不带 seat
  3. 等 notify/admission { decision: "admitted", seat: null }
  4. 看 notify/view 的 perspective.mode == "omniscient"，或调 observe.view
  5. 管理员动作：conn.admit { connectionId }

不要做：
  ✗ roles: ["omniscient"]        （不是角色）
  ✗ 桥 --role viewer 不带 --seat  （桥 CLI 拒绝，应 --role omniscient）
  ✗ manager.connect 只给 instanceId（缺省是 debug 面，非回环会被拒）
  ✗ protocolVersion: "2.0"       （握手就失败）
```
