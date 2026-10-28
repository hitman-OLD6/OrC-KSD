# 管理器 MCP 接入

> 本文只讲**怎么用**：启动顺序、MCP 配置、工具清单、典型调用序列、常见错误。
> 设计依据见 `requirements.md` R1/R10 与 `implementation.md` S1/U5（管理器的 MCP 面＝管理器的远程管理协议）。

## 1. 为什么分成"管理器 + 前端"两个进程

- **管理器**（`orc-manager serve`）必须**常驻**：它的生命周期长于任何一场对局，也长于任何一个 agent。
- **MCP 前端**（`orc-manager mcp`）是**无状态薄适配**：宿主以 `command + args` 形式拉起它，
  它把每条 `tools/call` 转发给常驻管理器。它不持有实例、不做判断。

这样"先启动管理器、后启动宿主、多个 agent 共用一台管理器"才成立——
如果反过来让管理器做 stdio 子进程，它就成了宿主的从属，随宿主退出而消失。

```
你用 start-manager.bat 起的常驻进程
   └─ 127.0.0.1:51999  (JSON-RPC over NDJSON)
          ▲                    ▲                    ▲
          │                    │                    │
   WorkBuddy 的 mcp 前端   另一个 agent 的前端   你的脚本/CLI
   (dotnet ... mcp)        (同一份配置)          (curl 不适用，用 CLI)
```

## 2. 启动顺序

```bat
:: 1) 先起管理器（前台常驻，Ctrl+C 退出）
start-manager.bat

:: 可选：换个端口 / 只自检 / 直接把 MCP 条目写进宿主配置
start-manager.bat --port 52001
start-manager.bat --check
start-manager.bat --config-mcp
```

`--config-mcp` 会调用 `tools/manager_mcp_config.py`，把 `orc-manager` 条目 upsert 进
`~/.workbuddy/mcp.json`，**并在写之前留一份 `mcp.json.bak.<时间戳>`**（该文件里还有别的条目，
不做备份的手写替换风险太高）。写入后**必须重启宿主**，MCP 列表才会重新加载。

脚本会打印这段配置，形如：

```json
"orc-manager": {
  "command": "dotnet",
  "args": [
    "D:/OrC-KSD/artifacts/slot/bin/Orc.Manager/debug/Orc.Manager.dll",
    "mcp",
    "--connect",
    "127.0.0.1:51999"
  ]
}
```

把它放进 MCP 配置的 `mcpServers` 里（WorkBuddy：`~/.workbuddy/mcp.json`），然后**再启动宿主**。

> `--connect` 的端口必须与 `serve --port` 一致。端口不存在时前端不会崩溃，`tools/call` 会返回
> `无法连接管理器……请先启动 orc-manager serve`——这是最常见的配置错误。

### 与旧脚本的关系

| 脚本 | 状态 |
|---|---|
| `start-manager.bat` | **现行**：常驻管理器（`serve`），一切从这里开始 |
| `start-sandbox-server.bat` | **已取代**：只做转发，保留名字以免旧快捷方式失效 |
| `start-agent-player-match.bat` | **已退役**：它用的 `--headless/--seat0/--wait-agent/--exit-on-end` 全在 S11 删除了 |

拉起对局不要再用 bat，改用 MCP 的 `manager.spawn`（见第 4 节）。

## 3. 工具清单（6 个）

| 工具 | 作用 | 关键参数 |
|---|---|---|
| `manager.ping` | 探活 | — |
| `manager.list` | 列出所有实例（含 `port` / `roomId` / `running`） | — |
| `manager.spawn` | 启动一台服务器（独立子进程） | `instanceId`（必填）、`packDirectories`、`seats`、`mode`、`allowDebugWrites`、`deckRules`、`port`、`seed` |
| `manager.kill` | 销毁实例（进程树回收、端口可复用） | `instanceId` |
| `manager.reload` | 热更新：换新 dll 起新子进程（管理器不重启） | `instanceId` |
| `manager.admin` | 把管理面操作透传给实例（**管理员动作**） | `instanceId`、`op`、`args` |

`manager.admin` 的常用 `op`：`connections.list`、`conn.admit`、`conn.reject`、`conn.revoke`、
`seat.bind`、`seat.unbind`、`match.start`、`room.abort`、`sandbox.reset`、`instance.info`。

## 4. 典型调用序列（拉起并打完一局）

```text
1. manager.spawn {
     instanceId: "m1",
     packDirectories: ["D:/OrC-KSD/samples/card-pack-ai"],
     seats: [{ kind: "ai" }, { kind: "agent" }],
     mode: "sandbox",
     allowDebugWrites: true,
     deckRules: "none"          // 样例包只有 5 种卡，达不到 39 张构筑
   }
   → { instanceId, port: 60132, roomId: "960df7d6", ... }

2. 玩家/调试面接入该实例（**另一条 MCP 连接**，指向实例端口而不是管理器）：
     用 Orc.Mcp.Bridge --connect 127.0.0.1:60132
     玩家面：--token-file <批准后拿到的令牌>；调试面：--no-resume

3. 玩家连接发 session.attach（兼任 controller+viewer，见下节），
   管理器侧批准：
     manager.admin { instanceId: "m1", op: "conn.admit", args: { connectionId: "..." } }

4. 开局：manager.admin { instanceId: "m1", op: "match.start" }

5. 打完/要换局：
     manager.admin { instanceId: "m1", op: "room.abort" }   // 无胜者终止（任何相位都有效）
     或等自然终局 → 服务器自动轮换出新 lobby

6. 收工：manager.kill { instanceId: "m1" }
```

### 玩家必须兼任 viewer

`controller` 只推输入、**收不到任何输出**——只申请 controller 的连接连自己的手牌都读不到
（`observe.view` 会返回 `unauthorized: 需要 viewer 角色`）。真实玩家连接应申请：

```json
{ "roles": ["controller", "viewer"], "seat": 0, "deck": [...], "packages": [...] }
```

### 实测通过的完整序列（2026-10-22，真进程）

```text
WorkBuddy → MCP manager.spawn{instanceId:"agent-live", seats:[ai,agent], mode:sandbox, deckRules:"none"}
   → port 56097, room bfb480cb

脚本扮演玩家直连 56097：
   initialize                      → connectionId
   session.attach{roles:[controller,viewer], seat:1, deck:39张, packages:[样例包]}
                                   → state=pending（立即返回，不挂起）
   （期间先收到一条 notify/room.seats：seat1 idle→pending）

管理员经管理器：
   manager.admin{op:"conn.admit", args:{connectionId}}  → ok
   → 玩家连接收到 notify/admission{decision:admitted, roles:[controller,viewer], seat:1, seatToken}

   manager.admin{op:"match.start"} → definitions=6, fallback=false
   （definitions=6 = 样例包 5 张 + 沙盒包占位卡 1 张；fallback=false 说明沙盒包没被降级剔除）

玩家再拉视角：
   tools/call{observe.view} → perspective={mode:seat, seat:1}, phase=mulligan, turn=0, hand=5
```

两个容易踩的点，脚本里都踩过：

- **`id` 必须是字符串**：服务器的 `RpcRequest.Id` 是 `string`，传数字会被判为"不是合法的 JSON-RPC 请求对象"。
- **推送会穿插在响应之前**：`session.attach` 会先推一条 `notify/room.seats`，客户端必须按 `id` 匹配响应、跳过无 `id` 的帧。
- **`protocolVersion` 是 `"1.0"`**（`ProtocolInfo` 的线协议版本），不是 MCP 的日期版本号。
- **载荷字段叫 `details`**（服务器 `tools/call`），不是 `payload`（那是管理器的叫法）。

复现命令：

```bat
python tools\verify_live_match.py --port <实例端口> --manager 51999 --seat 1
```

## 5. 常见错误

| 现象 | 原因 | 处理 |
|---|---|---|
| `无法连接管理器……请先启动 orc-manager serve` | 管理器没起，或 `--connect` 端口与 `serve --port` 不一致 | 先起 `start-manager.bat`，核对端口 |
| `instance-boot-failed`，提示"服务端进程在就绪前退出" | 管理器输出目录里缺 `Orc.Server.dll`，或 `--packs` 指向的目录不存在 | 重新 `dotnet build src/Orc.Manager/Orc.Manager.csproj`（工程已引用 `Orc.Server`）；核对 `packDirectories` |
| `lock-conflict` / `missing-assets` | 两个 seat 声明的包版本冲突，或某方缺资产 | 读返回的 `payload` 清单，统一版本或补齐资产 |
| `room-rotating` | 正在轮换窗口 | 按 `retryAfterMs` 重试 |
| `seat-taken` | 该座位已被占用 | 换座位或先 `conn.revoke` |

## 6. 自检与冒烟

```bat
:: 环境自检：dotnet / 两个 dll / 「dll 是否在管理器旁边」/ 卡包 / 端口占用 / mcp.json 条目
start-manager.bat --check
```

其中「`Orc.Server.dll` 是否在 `Orc.Manager.dll` 旁边」这一项最容易忽略：
管理器默认从**自己的输出目录**取服务器程序集，缺了它每次 `spawn` 都会是 `instance-boot-failed`。

管理器起来后，跑一次不依赖宿主的端到端冒烟（扮演标准 MCP 客户端跑完整序列）：

```bat
python tools\verify_manager_mcp.py
```

它断言 11 项：握手、工具目录、ping、list 空、spawn 拿到端口、list 有 1 条、
`instance.info` 为 `Lobby`、kill、kill 后 list 归零。全部通过会打印 `RESULT: all checks passed`。

## 7. 与旧配置的关系

旧的 `orc-seat1` / `orc-orch` 条目是**直连某台服务器实例**的桥，仍然有效，
但它们缺少"管理器"这一层：实例得有人先起、端口得人工对齐。
接入 `orc-manager` 之后，实例的起停与端口由管理器统一给出，旧条目只在你已经知道端口时才有用。
