# 执行进度 — 多人联机模型重构

- 更新时间: 2026-10-22T01:05:00+08:00
- 工具调用计数: ~150
- 当前步骤: S1–S11 全部落地
- 总体状态: 已完成（含 4 处与文档的偏差，见「偏差」；0 新增失败、0 编译错误）

## 验证结果

- `dotnet build OrcEngine.sln` → **0 错误**。
- `dotnet test OrcEngine.sln --no-build` → **2320 通过 / 0 失败 / 0 跳过**：

| 工程 | 通过 | 失败 |
|---|---|---|
| Orc.Game.Tests | 1373 | 0 |
| Orc.Packages.Tests | 356 | 0 |
| Orc.Tests | 370 | 0 |
| Orc.Mcp.Tests | 113 | 0 |
| Orc.Server.Tests | 63 | 0 |
| Orc.Manager.Tests（新） | 15 | 0 |
| Orc.Script.Tests | 17 | 0 |
| Orc.Ai.Tests | 13 | 0 |

> 基线是 2395 通过 / 2 失败。当前**0 失败**（既有 2 条 AI 红点因"进程级注册前移"修复了根因，已转绿）；
> 总数下降是因为删除了 97 条绑定已退役能力的旧用例（清单见「删除的测试」）。

## 已完成（按 S 步骤）

- **S1 管理器骨架与实例编排**：新增 `src/Orc.Manager`（`IManager`/`ServerManager`/`ManagedInstance`/`Cli`）。
  实例＝进程（U2），独立 stdin/stdout 对、独立端口/运行目录；`spawn`/`kill`/`reload`/`list`/`admin`。
  进程内 API 是真身，CLI 是同一真身的一次调用（`Cli.RunAsync` 可注入同一 `IManager`，有等价性测试）。
  `reload` ＝ drain → Kill(entireProcessTree) → WaitForExit → 新进程 → 就绪 → **端口复用断言**（在起新进程前探测），
  并把旧实例实际端口钉进重载请求（否则 port=0 会换随机端口）。有真子进程用例覆盖 spawn/kill/reload/双实例。
- **S2 父子私有通道**：`Orc.Server/Admin/AdminChannel.cs`（stdin 命令 / stdout 回执＋事件）。
  分流判据单点：**有 `id` ＝应答、无 `id` ＝事件**；未知 op 回 `ok:false` + 可读码；stdout 只出这两类 ＋ 首行就绪；
  子进程退出/超时 ⇒ 管理器侧在途请求全部失败（`PumpStdoutAsync` 的 finally 收束）；管理命令有 30s 上限。
- **S3 管理面**：`Orc.Server/Admin/AdminApi.cs`：`connections.list`/`conn.admit`/`conn.reject`/`conn.revoke`/
  `seat.bind`/`seat.unbind`/`match.start`/`room.abort`/`sandbox.reset`/`instance.info`。
  执行上下文按文档两栏落位（改动态的一律入 `SerialWorkQueue`；纯读在队列外）；drain 窗口统一 `room-rotating` + `retryAfterMs=200`，
  其中 `match.start` 失败不延后、`room.abort` 幂等成功、`conn.revoke` 允许。
  `conn.revoke` 撤销 controller 立即判负（不走宽限）、撤销 viewer 只断该 viewer、令牌立即失效。
- **S4 管理器资产职责**：`AssetLibrary`（注册 / 依赖分析 / 两份 lock 差异 / 并集计划）。
  并集＝两 seat 声明 ＋ 实例级沙盒包 → `LockMerger` → `LoadListPlanner.Plan`；致命 issue → `lock-conflict`（指名双方与版本），
  非致命 `Unregistered`/`DependencyMissing`/`DependencyInvalid`/`DependencyVersionMismatch` → `missing-assets`（点名哪一方缺什么）。
  可复现性有测试（同一声明 → 同一计划）。
- **S5 服务器实例与房间轮换**：`ServerBootstrap` boot 四步（脚手架 → 进程级注册 → 建 lobby → 监听），**boot 段零卡池装载**；
  `ServerHost` 支持十步轮换序（drain → 停 AI/宽限 → 拆订阅/收束 pending/释放旧 Match → 建新房新服务 → 原子换引用＋纪元递增 →
  绑座会话转 Revoked → 推 `notify/room.rotated` → 恢复队列）。`SerialWorkQueue`/`ServerHost` 跨房间不变（有断言）。
- **S6 会话/角色**：`SessionState` 七态 ＋ `session.attach`/`detach`；删除 `ApplySeatEffects`；
  `McpToolDescriptor.RequiredRole` ＋ `McpService.InferRole` 单点推断；`Pending`/`Connected` 除 `room.info` 外一律 `not-admitted`；
  兼任共享同一 seat 绑定（viewer 恒脱敏）；全知必须是"不含 controller"的独立连接。
- **S6b 断线与终止**：lobby 断线立即释放；对局态保留绑定 ＋ 宽限**只在轮到该 seat 时**计时（有测试）；
  局中不可换人（`seat-taken`）；`room.abort` → `Match.Abort()`（`Winner=null`、`EndReason=Aborted`）。
- **S6c 房间态可见性**：`room.info` 按调用者角色**构造**（`RoomViewRequest`）；`notify/room.seats` delta（不含 deck/packages）；
  申请已占座位同步 `seat-taken`；全知 viewer 也读不到任何 deck/packages（有测试）。
- **S7 输出分发**：`ProjectSeatFrame`/`ProjectOmniscientFrame`/`ProjectDebugSummary`；帧形状只留 `perspective` ＋ `view`/`views` ＋ `actions`；
  同一 seat 多 viewer 复用同一份计算结果；段按视角分别导出（全知＝`EventSegmentJson.Serialize(segment)` 全量）。
- **S8 设施归属 seat**：`SeatBinding{Epoch,PlayerIndex}`（构造断言 seat==playerIndex，有测试）；纪元在原子交换同一动作里递增；
  轮换清理清单逐项落位（AI 循环、宽限计时、pending 选靶 `FailAll("room-rotated")`、段缓存随服务销毁、待发帧清空、队列 drain）。
- **S9 调试面**：`session.attach{roles:["debug"]}` 申请即生效（回环判据零变更）；不绑 seat、跨房间长存（有测试）；
  能力两层（资产层 `card.write`/`effect.write` 在 lobby 可用；对局层 `card.load`/`sandbox.*` 需活跃对局 → `not-ready`）；
  `AllowDebugWrites`/`SandboxRoot` 改为 spawn 参数（走管理面），CLI 不再隐式传递。
- **S10 卡池重装**：卡池按房间构建，轮换即生效点；**没有待生效队列**；
  装配失败按"最后一次成功的包集合"重试一次，仍失败才拒绝开局（房间留 lobby）；完整结果经 `sandbox.state.pool` ＋ 通道事件可观测，
  **不进** `room.info`。
- **S11 协议面与入口**：6 个请求方法 ＋ 7 条通知；`Orc.Client` 保存 `connectionId`、`attach` 立即 pending；
  `Orc.Mcp.Bridge` 重写为 attach ＋ 等 `notify/admission`（令牌经批准响应下发并可 `--token-out` 落盘）；
  `sandbox-server-ctl.py`/`start-sandbox-server.bat` 改为"管理器 spawn ＋ 前台管理窗口"；
  新增 `docs/服务器管理器-长期生命周期/客户端接入指南.md`（现行协议），旧预稿加作废横幅。

### 刻意改变的行为 12 条逐条核对

| # | 变更 | 落位 |
|---|---|---|
| 1 | 删除"play 模式拒未绑座读观察面"分支 | `ServerHost.Gate` 不再读 `ServerMode`；全知由批准决定 |
| 2 | 移除 `notify/view` 扁平标量 | `McpService.Projection.cs` 三个投影方法只出 `perspective`+`view(s)`+`actions` |
| 3 | 退役 `observe.view{panel}` | `ObserveTools.View` 只按角色分流；schema 无 `panel` |
| 4 | 删除 `ApplySeatEffects` | 角色只由 `session.attach`/管理面改，工具副作用不改态 |
| 5 | 删除 boot 卡池预装 | `ServerBootstrap` 只校验 manifest 可解析（可索引），零 `MatchCardAssembly.Register` |
| 6 | 删除 `PackFingerprint`/`PackPolicy` | `Room`/`RoomSnapshot` 无这两个字段；`ComputePackFingerprint` 已删 |
| 7 | 删 `HeadlessContext` 与 8 个 CLI 参数 | `HeadlessCategory` 文件已删；`Program` 对退役参数报参数错误（不保留"已弃用但可用"） |
| 8 | 心跳判据改"无任何入站帧" | `HandleClientAsync` 每帧 `session.Touch()`（含 ping） |
| 9 | 删 `notify/room` | `ProtocolMethods.NotifyRoom`/`BroadcastRoom` 已删；快照只经 `room.info` |
| 10 | 进程级注册前移到 boot | `ServerBootstrap` 调用；`CreateMatchAsync` 不再调用（只留一次留痕） |
| 11 | 退役 `sandbox.create`/`dispose` | `SandboxTools` 只剩 `sandbox.state`；建/毁局走管理面 |
| 12 | `card.write`/`effect.write` 入串行队列 | 工具调用默认入队；仅 `IsQueueFreeRead` 白名单（`room.info`/`card.list`/`pack.info`/`file.*`/`service.*`/`docs.*`）在队列外 |

## 补丁批 1（2026-10-22）：沙盒包真正进入装配链 ＋ 断线重连覆盖

### 已修：偏差 1（沙盒包无 lock ⇒ 被整包剔除）

- `SandboxEnvironment.EnsurePackageMetadata()`（新增）：用 `AssetMigrationTool.Plan(force:false)` ＋ `Apply`
  为沙盒卡包的每个源文件补齐 `*.meta.json` sidecar 并生成两域 lock；幂等（既有 sidecar 一律 Skip）；
  源文件不可解析时只记 `Obstacles` 并跳过该文件，**不写坏既有 sidecar、不抛**。
- 三个调用点：`ServerBootstrap.EnsureScaffold`（boot）、`AdminApi.TryAssemble`（每局装配前）、
  `AdminApi.ResetSandboxAsync`（重置后）。
- 端到端验证：`SandboxPoolTests.MatchStart_IncludesSandboxPack_WithoutFallback` 断言
  `match.start` 的 `fallback == false` 且 `packs` 含 `pack/sandbox/`——即沙盒包**不再被降级剔除**。
- 单元验证：`SandboxPackageMetadataTests`（5 条）——生成 lock/sidecar、幂等、装配可读、新写卡进池、坏卡只报障碍。

### 已补：断线重连（R11）

- `ReconnectTests`（2 条）：对局中 controller 断线后凭 `seatToken` 经 `session.attach` 恢复到
  `admitted` ＋ 原 seat，**不经 pending、不需管理员再批**；无效令牌落回 `connected` 并回 `invalid-token`。

### 已补：背压与选靶超时（原「仍未补」两项）

- **慢查看者（背压的可观测后果）**：`BackpressureTests.SlowViewer_DoesNotStallEngine_AndCatchesUpAfterRecovery`。
  构造方式是真行为：`OrcClient.NotificationReceived` 在读循环内**同步**分发，让 handler 阻塞就等于客户端停止读 socket。
  断言：积压期间其他参与者仍能完成换牌确认（引擎不被拖住）；恢复后能追到**当前**回合。
  **明确不覆盖**：每会话有界队列的 `DropOldest` 细节（要复现需产生 256+ 条推送，端到端成本不成比例）。
- **选靶超时**：`TargetingTimeoutTests.PendingTargeting_TimesOut_PushesExpired_AndExecutorLeavesBusy`。
  走真实链路：样例卡包的单位卡打出需要选部署槽位 ⇒ `play.unit_pre` 返回 `targeting` 并发布待决；
  用例**故意不应答**，`TargetSelectionTimeout=2s` 后断言收到 `notify/target.expired`、
  且 `MatchExecutor.IsBusy` 归位（不会永久挂起）。
  - 顺带修正一处测试认知：**纯 controller 收不到任何输出**，连 `observe.view` 都会因角色不足被拒
    （`unauthorized: 工具 'observe.view' 需要 viewer 角色`）。真实玩家的形态是**兼任**
    `roles:[controller, viewer]`，本批新增的用例都按此构造。

### 已修：偏差 2（换牌/准备期无法终止）

- `MatchLifecycle.End` 去掉"仅进行态"门禁（只挡已终局）；`Match.Abort` 同步去掉 `IsActionAllowed` 判定。
  **`Concede` 保持原门禁**——认输是玩家行为，仍要求进行相位；管理员终止才是全相位有效。
- 新增 `RoomRotationTests.Abort_DuringMulligan_EndsWithAborted`：开局后（`Mulligan` 相位）直接终止，
  断言 `State=Ended`、`Winner=null`、`EndReason=Aborted`。
- 这是本轮**第四处引擎改动**（E4），与 E1–E3 同属"终局原因补全"，不触及规则语义。

### 顺带修：`Orc.Packages.Tests` 的偶发红（测试并行污染）

- 现象：全量跑时 `MatchCardAssemblyTests.In_Match_Card_Resolution_Goes_Through_Bridge_Only` 偶发失败
  （`KeywordEffectAssembly.VerifyBindings`：词条 → 效果预制体绑定核验不过）；单跑该用例恒绿。
- 根因：该工程有 8 个测试类直接操作**进程级静态面**（`KeywordRegistry`、`BuiltInPlugin` 的一次性注册闩锁、
  `CardPackKeywordLoader.KeywordOwner`），这些状态不随测试结束复位，并行跑就是"谁先注册词条"的竞态。
- 修法：新增 `tests/Orc.Packages.Tests/AssemblyInfo.cs`，`[assembly: CollectionBehavior(DisableTestParallelization = true)]`。
  代价：该工程 5s → 7s；收益：连跑 3 次 + 全量 2 次均稳定全绿。
- 注：这是**测试基础设施**问题，不改产品代码。

## 补丁批 3（2026-10-22）：管理器的 MCP 面（U5 的"后续批次"）

### 新增

- `ManagerServer`（`src/Orc.Manager/ManagerServer.cs`）：管理器**常驻回环 TCP 服务**，JSON-RPC 2.0 over NDJSON，
  方法 `manager.ping/list/spawn/kill/reload/admin`；多连接并发；绑定 `127.0.0.1`，不设鉴权（管理权＝进程所有权）。
- `ManagerClient`：前端侧长连接 ＋ **串行调用**（管理器操作本就该按序执行，串行省掉 id 匹配），断线自动重建一次。
- `ManagerMcpFront`：**stdio MCP 前端**，`initialize` / `ping` / `tools/list` / `tools/call`，6 个工具。
  管理器不在线时返回可读提示（含 `orc-manager serve` 字样），不崩、不静默。
- CLI 两个新命令：`serve [--port]`（常驻，stdout 一行就绪 JSON）、`mcp --connect <host:port>`（stdio 前端）。
- `start-manager.bat`：双击启动常驻管理器，并打印可直接粘贴的 `mcp.json` 片段（纯 ASCII、CRLF）。
- `docs/服务器管理器-长期生命周期/管理器MCP接入.md`：启动顺序、工具清单、典型调用序列、常见错误表。

### 为什么是"常驻 TCP ＋ stdio 前端"而不是别的

- 用户要求"先启动管理器，再启动宿主，多个 agent 共用" ⇒ 管理器必须是独立常驻进程，**不能**做宿主的 stdio 子进程。
- 而宿主（WorkBuddy）的 MCP 配置是 `command + args`（stdio）形态 ⇒ 需要一个无状态前端做转接。
- 结论：`serve`（常驻）＋ `mcp`（前端）两个命令。与 `Orc.Mcp.Bridge` 同构，只是对端从"实例"变成"管理器"。

### 修的 bug（真实存在，不是重构遗留）

- `Orc.Manager/bin` 里**没有 `Orc.Server.dll`**：`ServerManager` 默认以"同目录下的 `Orc.Server.dll`"为子进程程序集，
  而不引用服务器工程的 Manager 根本拿不到它 ⇒ **每一次 `manager.spawn` 都是 `instance-boot-failed`**。
  修法：`Orc.Manager.csproj` 增加对 `Orc.Server` 的 `ProjectReference`（只为复制 dll，不引用其类型）。

### 实测（端到端，非单测）

`start-manager.bat 51999` → MCP `manager.spawn{smoke1}` → `port=60132, roomId=960df7d6` →
`manager.list` 见 `running=true` → `manager.admin{instance.info}` 见 `Lobby/epoch0/seatCount2` →
`manager.kill` → 实例清单归零、`tasklist` 中 `Orc.Server` 计数 0。

### 测试

`tests/Orc.Manager.Tests/ManagerMcpTests.cs`（4 条）：握手＋工具目录、工具调用穿透到 `IManager`、
未知工具报错、**管理器离线时给出可操作提示**。为此给 `Orc.Manager` 加了 `InternalsVisibleTo("Orc.Manager.Tests")`
（传输层保持 internal，只让测试直接驱动）。

全量：**2335 通过 / 0 失败 / 0 跳过**（2331 → +4）。

### 脚本收口（取代旧的启动服务器脚本）

| 文件 | 变化 |
|---|---|
| `start-manager.bat` | **重写为完整版**：`--port` / `--check` / `--config-mcp`，preflight 增加「`Orc.Server.dll` 是否在 `Orc.Manager.dll` 旁边」（缺了必 `instance-boot-failed`） |
| `start-sandbox-server.bat` | **降为转发**（保留文件名，旧快捷方式不失效）；原实现是"单次 spawn ＋ 手输 NDJSON"，与常驻 MCP 模型已不同 |
| `start-agent-player-match.bat` | **退役**：它用的 `--headless/--seat0/--seat1/--wait-agent/--exit-on-end` 在 S11 全部删除，原样运行必然失败 |
| `tools/manager_mcp_config.py` | 新增：把 `orc-manager` 条目 upsert 进 `~/.workbuddy/mcp.json`，**写前留 `mcp.json.bak.<时间戳>`** |
| `tools/verify_manager_mcp.py` | 新增：扮演标准 MCP 客户端跑完整序列（11 项断言），不依赖宿主 |

**已写入宿主配置**：`~/.workbuddy/mcp.json` 现含 `orc-seat1` / `orc-orch` / `orc-manager` 三条
（备份在 `mcp.json.bak.20261022-183652`）。注意：MCP 列表在宿主启动时固定，**必须重启宿主**才生效。

**冒烟结果**（`tools/verify_manager_mcp.py`，真进程）：

```
[PASS] initialize            serverInfo={'name': 'orc-manager', 'version': '1.0.0'}
[PASS] tools/list count == 6
[PASS] manager.ping / list empty
[PASS] manager.spawn         port=54963, roomId=a951d1de
[PASS] manager.list count == 1
[PASS] admin instance.info   roomState=Lobby
[PASS] manager.kill / list 归零
RESULT: all checks passed
```

## 补丁批 5（2026-10-22）：MCP 面补齐"代持连接"＋ 零代码上手指南

### 问题

宿主（WorkBuddy）的 MCP 列表在**启动时固定**，运行中无法新增连接。若"玩家接入"必须再配一条指向实例端口的 MCP，
任何已经在会话里工作的 agent 都做不到"零代码开一局"——它改不了宿主配置。用户明确要求：**实例拉起后直接可用、不重启宿主**。

### 做法：不是让工具变多，而是让工具能指向任意实例

- 服务端早已有 `ManagerSessions`（代连 + 自动批准 + 转发），但 **MCP 工具表从未暴露**这四个 op。
  本次把 `manager.connect` / `manager.action` / `manager.sessions` / `manager.disconnect` 接入 MCP 面。
- `manager.connect` 在管理器进程内建立到实例的真连接（`session.attach` → 管理器自动 `conn.admit` → 等 `notify/admission`），
  返回 `sessionId` ＋ **该连接实际可用的工具清单**（`OrcClient.ListToolsAsync`）。agent 拿到就知道能调什么，不用猜。
- `manager.action { sessionId, tool, arguments }` 转发 `tools/call`。
- `capabilities.tools.listChanged` 改为 `true`；spawn/kill/reload/connect/disconnect 之后发一次
  `notifications/tools/list_changed`。**但工具表本身恒定（10 个）**，所以不支持该通知的宿主也照常可用——
  这是"不重启宿主"的底线，动态通知只是让支持的宿主刷新。

### 端到端实测（`tools/verify_zero_code.py`，13 项断言，全程只经 orc-manager 一条 MCP）

```
A. 打 AI
  spawn(play)            → port 57187
  connect(seat=1)        → sessionId=s5，返回 25 个工具
  match.start            → definitions=6
  observe.view           → hand=5, phase=mulligan
  match.end_turn         → ok
B. 沙盒做卡
  spawn(sandbox)         → port 61168
  connect(调试面)         → sessionId=s6，工具含 card.write
  card.write             → ok
  asset.validate         → ok
  manager.sessions       → count=1
  kill / list            → 归零
RESULT: 零代码链路全通
```

### 文档

新增 `agent零代码上手指南.md`：面向**不读代码**的 agent，30 秒版 + 两条路径（打 AI / 沙盒做卡）
+ 工具速查 + 常见错误自愈表 + 两个字段陷阱 + "为什么不用重启宿主"。

### 这次改动的代价（必须说明）

替换 `Orc.Manager.dll` 时，常驻管理器与宿主的 MCP 前端（`dotnet ... mcp`）都会锁住文件，**必须停掉两者**。
前端被停后宿主不会自动重连（实测 `Not connected`），因此**本次需要重启一次宿主**。
这是"改代码"的代价，与"运行时增删实例"无关——后者全程不需要重启。

## 偏差（文档 vs 实现，已在实现中显式注释）

1. **~~沙盒包无法真正进入装配链~~（已修，见「补丁批 1」）**（R3 的"强制入并集"原只在降级路径上成立）。
   证据：`SandboxEnvironment.EnsureScaffold()` 只写 `manifest.json` ＋ 占位卡（`SandboxEnvironment.cs:87-113`），
   **不生成 `lock.formal.json`，也不生成 sidecar**；而 `MatchCardAssembly.Register` 用 lock 驱动装载，
   且要求"lock 至少覆盖一个卡资产"（`MatchCardAssembly.cs:165-178`）。
   当前实现：装配先带沙盒包 → 失败 → **剔除沙盒包重试一次**（文档要求的降级，已实现并上报 `sandbox-invalid`），
   成功则开局并告警。因此"调试面造出的卡能进对局"这一条**当前不成立**；未自行改 `Orc.Packages` 的 lock 语义（属第四处改动）。
2. **~~`Match.Abort()` 保留引擎相位门禁~~（已修，见「已修：偏差 2」）**：`MatchLifecycle.End` 原在非 `InProgress` 时直接返回，
   `Match.Abort` 同门禁。因此**准备/换牌期**调 `room.abort` 无法产出 `EndReason=Aborted`（服务器仍终结房间并轮换，但 `endReason` 为空）。
   E4 已放宽：`End` 只挡已终局，`Abort` 去掉相位判定；`Concede` 保持原门禁。
3. **卡组合法性校验时点**：落在 `match.start`（服务器侧，用本局装配池），而非"管理员批准那一刻"。
   原因：批准在子进程内执行，此刻还没有卡池；管理器资产层只做并集/冲突/缺口（唯一权威）。
   S4 的目标（"不能拿预装池校验"）已满足——校验用的是**并集算出的本局池**。
4. **`stale-seat` 码保留但无线上载体**：`SeatBinding` 是服务器内部数据（不出线），因此该码在当前协议面没有触发点。

其它说明：

- `notify/match.started`/`notify/match.ended` 已按"通知 7 条"删除（S11 清单），
  客户端改看 `notify/view` 的相位/结果与 `notify/room.rotated`。
- 新增 `McpService.CreateSandboxMatchAsync()` / `DisposeSandboxMatch()`：**进程内**入口（非工具、不进工具表），
  供宿主/示例/测试构造"空场沙盒局"；`sandbox.create`/`dispose` 工具本身已退役。
- CLI 的 `admin` 只在**创建者进程**内可用（父子私有通道的必然结果，与 R10 的"远程可调"口径一致）；已在脚本与文档写明。
- R1 的 MCP 驱动形态本轮未交付（与 implementation S1/U5 一致：属后续批次的远程管理协议）。

## 删除的测试（原因：被测能力已退役）

服务端旧套件 18 个文件（约 97 条用例）——全部直连已不存在的 `room.claim`/`room.resume`/`room.start`/`notify/room`/
`--headless` 链路，按新模型重写成本高于价值：

`HeadlessTests`、`HeadlessAttributionTests`、`ProcessHeadlessTests`、`RoomTests`、`RoomPushTests`、`SecurityTests`、
`EndToEndTests`、`ModeTests`、`CrossSeatTargetingTests`、`PushDeliveryTests`、`AiAndDisconnectTests`、`TargetingFaultTests`、
`ResilienceTests`、`RawProtocolContractTests`、`TransportTests`、`BootstrapTests`、`BridgeProcessTests`、`ServerProcessTests`。

替代覆盖（新增 63 条服务端用例 ＋ 15 条管理器用例）：

- `SessionModelTests`（11）：pending 零能力 / 工具表过滤 / 批准与令牌下发 / seat-taken / 调试面即时生效 /
  玩家面禁调试 / 房间态隐私 / lobby 断线释放 / 撤销断开与令牌失效 / `SeatBinding` 断言。
- `RoomRotationTests`（13）：开局＋终止（无胜者）/ 幂等 `already-ended` / 轮换换房间且回收角色＋进程级对象不变 /
  新房间接纳申请 / viewer 视角分发（seat vs 全知）/ AI 座位配置即批准＋卡组由本局池生成 / 空座拒绝开局 /
  drain 窗口（`room-rotating` ＋ `already-ended`）/ 管理 op 不在工具表 / 全知看不到构筑 /
  多 viewer 各自独立 / 对局态断线保留绑定且 viewer 不断流 / 撤销即判负 / 调试面跨轮换存活。
- `AdminChannelTests`（2）：回执/事件分流 ＋ 未知 op ＋ stdout 纯度。
- `Orc.Manager.Tests`（15）：资产注册/依赖分析/缺口点名/版本冲突/可复现计划/差异清单/码表映射/CLI 与进程内等价/
  真进程 spawn·双实例·reload（端口复用＋旧进程回收）·boot 失败码。
- Orc.Mcp.Tests 中删除 1 条 `RoomResume_Is_Not_Blocked_While_Targeting`（`room.resume` 退役，挂起期门禁不再有该例外）。

## 阻塞 / 风险（供后续批次）

1. 沙盒包 lock 缺口（偏差 1）——建议下一批给 `SandboxEnvironment` 生成 `lock.formal.json` ＋ sidecar，或在装配链加"无 lock 包"的显式旁路。
2. 换牌期终止语义（偏差 2）——需要引擎放宽 `MatchLifecycle.End` 的相位门禁，属第四处引擎改动，须新裁决。
3. `conn.revoke` 在 drain 窗口内的"绑定释放延后到新房间建立后"只做了"不动旧栈"的一半：当前实现直接改会话态，
   未登记待清理项（旧 `Room` 即将销毁，实际无副作用）；若未来 drain 变长，需要补一张待清理表。
