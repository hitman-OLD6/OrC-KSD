# Requirements Grill Plan — 多人支持（服务端 / 客户端分离）

> 本轮聚焦：把"逻辑引擎做服务端、Godot 前端做客户端、仅经字符串远程协议交流、一实例一局＋房间＋seat"的边界钉死。
> 当前：需求 grill 第 2 轮；Q1/Q3/Q8 已由用户口述部分回答；Q4 为本轮主问题。
> 前端参考基线：`D:\Godot-orcKsd-main`（用户声明"可能过时，仅供参考"，见 C1）。

## 已确认的代码基线（取证，不再提问）

### 引擎侧（`d:\OrC-KSD`，本轮实测）

- 解决方案 21 个工程全 `net8.0`；`Orc` 零依赖；`Orc.Mcp` 位于依赖链顶端，引用 7 个引擎工程 → 天然进程边界（`src/Orc.Mcp/Orc.Mcp.csproj:17-23`）。
- 业务入口已是"字符串进、JSON 出"：`McpService.InvokeAsync(string toolName, JsonElement arguments)`（`src/Orc.Mcp/McpService.cs:132`），门面内含模式门禁与挂起期门禁（`:143-158`）。
- player 模式工具面被测试锁死为 **24 条**（`tests/Orc.Mcp.Tests/ToolWhitelistTests.cs:15-41`），authoring＝player＋37 条调试/编辑工具（`:44-84`）。本轮实测 `Orc.Mcp.Tests` **71 通过 / 0 失败**（4 s）。
- 单对局硬约束：`MatchExecutor.Attach` 冲突即抛（`src/Orc.Mcp/Match/MatchExecutor.cs:89-101`）；`_gate=SemaphoreSlim(1,1)`；忙＝有未完成动作 Task（`:80-86`）。
- 建局输入是**两座位显式卡组**：`MatchLaunchOptions{ PackDirectories, SeatA, SeatB, Seed, SkipMulligan, RequiredDeckSize=39 }`、`MatchSeatConfig(int Seat, IReadOnlyList<string> Deck, string? Name)`（`src/Orc.Mcp/Match/MatchLaunchOptions.cs:9-45`）；无"人类/AI"身份位、无自动构筑。
- 座位身份只有 `McpServiceOptions.AgentSeat`（`int?`，仅观察面过滤，`src/Orc.Mcp/McpServiceOptions.cs:66-70`）；无 clientId / 会话表 / 断线回收。
- 选靶线上形态已就绪：`PendingSelection(SessionId, SelectorName, Mode, Min, Max, Candidates, Identifiers)` ＋ `PendingCandidate(Index, Kind, ReferenceKey, DisplayName, IsAlive)`，全标量（`src/Orc.Mcp/Match/TargeterBridgeAdapter.cs:14-38`）；`target.submit` 用序号。
- 但宿主面泄漏活对象：`IMcpHost.ActiveMatch/TryGetActiveMatch`、`MatchLifecycleEventArgs(..., Match?)`、`TrySubmitTargeting` 内部 sync-over-async（`src/Orc.Mcp/Hosting/HostingContracts.cs:17-82`、`src/Orc.Mcp/McpService.Hosting.cs:68-71`）。
- `observe.view` **只输出 14 个标量**（turn/phase/手牌数/资源/结果标题），不产出整枚 `UiMatchView`（`src/Orc.Mcp/Tools/ObserveTools.cs:208-227`）→ 客户端无法据此渲染。
- 投影契约要求活 `Match`+活 `Player`+前端 `artLookup`/`definitionLookup` 委托（`src/Orc.Game/Ui/UiProjection.cs:16-33,39-51`、`src/Orc.UI/UiProjectionProvider.cs:66-99`）。
- 引用键：实体用 `Entity.Id`（Guid，`src/Orc/Core/Entity.cs:8-9`），Player 无 Guid、靠引用表（`src/Orc.Game/Players/Player.cs:26-31`、`src/Orc/Core/EngineReferences.cs:41-62`）；`KeyOf` 失效返回空串（`src/Orc.Mcp/McpService.cs:114-129`）。
- 活对象泄漏集中在：`Bus.Emit` payload 原样入日志（`src/Orc/Core/Bus.cs:150-151`）、`GameUpdates.cs:512-541`、`Operate.cs:113-116`；降级只保留名字（`src/Orc/Output/JsonValueWriter.cs:101-140`），且该形态被测试固化（`tests/Orc.Tests/EventStreamJsonTests.cs:154-156`）。
- AI 在进程内且需要活对象：`AiPilot` 同时依赖 `IAiMatchGateway.TryGetActiveMatch(out Match?)` ＋ `IUiProjectionProvider`（`src/Orc.Ai/AiPilot.cs:38-68`、`src/Orc.Game/Ai/AiGateway.cs:46-81`）；选靶面已是"sessionId + 序号"（`AiPilot.cs:160-185`）。
- 唯一传输实现是 `samples/Orc.Mcp.Stdio`：手写 JSON-RPC over NDJSON(stdin/stdout)，方法仅 `initialize/ping/tools.list/tools.call`，**无 session/seat/room**，零测试（`samples/Orc.Mcp.Stdio/Program.cs:51-146,239-289`）。
- 全仓零网络栈命中；序列化仅 `System.Text.Json`；现有"E2E"全部同进程（`tests/Orc.Mcp.Tests/HostIntegrationTests.cs`）。
- 沙盒与写面在服务端磁盘，`card.write/effect.write/asset.promote/editor.*` 是远端可达写面；`--csx` 开即弱沙盒任意代码执行（`src/Orc.Mcp/McpServiceOptions.cs:40-43`）。

### 前端侧（`D:\Godot-orcKsd-main`，可能过时）

- Godot 4.7 + C#，主工程 `net9.0`，契约/桥接 `net8.0`；`Kards.Ui.csproj:11-13` 把 `proto/ui/**` 编进 Godot 程序集并传递引用桥接层。
- 网络**零命中**（TCP/UDP/WebSocket/HttpClient/MultiplayerPeer/ENet 全部 0）→ 客户端化是"从零新增通道"。
- 引擎依赖全在 `proto/bridge`（38 文件 `using Orc`）；`proto/ui`、`proto/core`、`proto/contracts` 为 0。
- `OrcEngineHost` 是进程级静态单例（`proto/bridge/Kards.Ui.OrcBridge/OrcEngineHost.cs:19-22`）。
- 数据流三层：`OrcMatchSession`（引擎面，持 `internal Match`）→ `OrcMatchHost`（投影/段）→ `OrcMatchRunner`（Godot 面）；`Main._Process` 每帧 `runner.Pump()` 同步排空（`Main.cs:399-405`、`OrcMatchHost.cs:265-291`）。
- 契约层纯 DTO 但**无任何序列化痕迹**（`System.Text.Json/*Json*` 在 `proto/contracts` 0 命中）；`IUiTargetResponder.Complete/Cancel` 是同步 bool 接口而非 DTO（`proto/contracts/UiInteraction.cs:9-28`）。
- 存在绕过投影的旁路：`OrcMatchHost.Index` 暴露 `IReadOnlyDictionary<string, Card>`（`OrcMatchHost.cs:114-163`）。
- 客户端进程内跑引擎能力：DIY 编译（`NativeScriptHost`/`DiyCardCompiler`/`VerifiedCardPool.cs:149-153`）、V8 解析（`DesktopOrc.cs:32`）、卡包装载（`OrcEngineHost.LoadPackCards`）。
- 构建期硬绑引擎源码树：6 个 `ProjectReference` + 嵌入 `*.tpl`（`Kards.Ui.OrcBridge.csproj:23-34`、`Directory.Build.props:11-17`）。
- 参考测试证明存在"只用 `UiMatchView`/`UiBattleActions`/`UiCommand` 跑完一局"的路径（`tests/OrcMatchSessionTests.cs:14-22`），但仍同进程。

## 代码 ↔ 陈述矛盾（需仲裁）

### C1: 前端参考副本结构性过时 【已缓解】
- 该副本绑定引擎 `750e11e`(+Beta.2 patch)，本地已到 `6d72eac` 之后（含 wrapper 硬切）；`docs/前端适配-后端变更-20261020.md` 已列出 5 类编译阻断（`CommandCard→OrderCard`、`AddActiveHandler` 删除、`CardPackLoader` 删除、`CanMove/CanAttack` 只读、动作集必须注册）。
- 影响：**不能把该副本当作改造目标仓**；本阶段只把它当"客户端形态参考"。

### C2: 两套选靶契约并存 【仍待裁决】
- 引擎服务层 `PendingSelection`（序号回传）vs 前端 `UiTargetRequest`（`UiChoiceSlot[]` + `MatchId:selectorId:counter` 令牌 + 同步 responder）。
- 二者不兼容；远程协议须选其一为真源，或明确映射层。

### C3: "客户端不跑引擎"与"前端进程内跑引擎能力"冲突 【仍待裁决 → Q2】
- DIY 编译、卡池准入、卡包装载、对手 AI 目前都在前端进程内；若保留，客户端仍须带引擎 DLL + V8 + 引擎源码树。

### C4: `observe.view` 无法驱动渲染 【新】
- 只出 14 个标量，`UiMatchView` 整枚从未上线。→ "客户端只消费投影"在当前代码里**没有可用的过线形态**，必须先补整枚投影（Q5/R6）。

### C5: "房间"与"一实例一局"的张力 【新】
- `MatchExecutor` 硬编码"一进程一活跃对局"，理由写明是"targeter 桥接无法区分多局归属"。用户要求"服务器内部维护房间、房间有 seat"——若房间先于对局存在（等人阶段无对局），则房间层必须独立于 `MatchExecutor`，否则一连接就意味着已开局。

### C6: Nexum 无法承担"活对象"职责 【新，见 Q15】
- `aizuon/nexum`：仅 `net10.0`、DotNetty 传输、`rmiId`+`NetMessage` 二进制信封、无远程对象/代理、1 star / 34 commits / 无 release / API 可能变动；另有同名 NuGet 包 `Nexum` 是 CQRS runtime。
- 与本仓 net8.0 不兼容；且**不解决**活对象问题（活对象是引擎内部对象图，传输库无法替代引用键+投影）。

> **需求 grill 已关闭**（2026-10-20）。下表全部 Completed / Not Applicable；实现路径转 `implementation-grill-plan.md`。

## Unresolved Questions

### Q4: 协议真源与分层 【Completed → 选项 a】
- 决策：单一协议＝现有 MCP 工具面；新增 `room.*`/`session.*`；服务端推送走 JSON-RPC notification；不与外部 MCP 生态做互通承诺。
- 传输：首版 NDJSON over TCP（局域网），WebSocket 可选承载；穿透交给部署层。
- 后果（进实现 grill）：`observe.view` 需整枚投影；`IMcpHost` 的活对象面不得上线；`McpToolResult` 与 JSON-RPC error 需统一错误码。

### Q4a: 传输承载 【Completed】
- NDJSON over TCP 为首版；WebSocket 为可选承载；不做内置 NAT 打洞。

### Q2: 客户端是否允许保留引擎程序集 【Completed → 选项 a ＋ 口径修正】
- 决策：a（对局客户端不跑引擎、创作宿主自带引擎）。
- **口径修正（第 4 轮）**：不做引用隔离——发行包同时带客户端与服务端，客户端可引用 `Orc.*`；只保留"边界隔离"（只传字符串、不传活对象、不直调引擎 API）。
- 后果：`UiMatchView` 等 DTO 可直接复用，I7（协议 DTO 独立化）降级为"确保线上一律自描述 JSON"；验收改为"无旁路调用"而非"无引用"。

### Q11: 本地形态的进程拓扑 【Completed → 选项 a（用户第 4 轮确认）】
- 决策：房主在客户端内"开启房间"→ 客户端拉起服务端子进程，服务端进入房间态；本地与远程走同一条 loopback/网络路径。
- 待实现 grill 细化：端口分配（建议监听 0 端口回报实际端口）、子进程回收、日志采集。

### Q5: 投影形态 【Completed → 选项 a】
- 决策：整枚 `UiMatchView` 直出；只填引擎侧字段；`ArtPath`/`Text` 留空，客户端按 `cardId` 查本地表；全量（不增量）。

### Q6: 座位绑定与选靶归属 【Completed → 选项 a】
- 决策：声明 seat 成功即下发 `seatToken`，重连凭令牌；选靶由 seat→controller 路由，`target.claim` 退役。

### Q7: 断线重连与对局恢复 【Completed → 选项 a】
- 决策：房间态断线立即释放；对局态保留至终局，宽限 60s 后 `Concede`；不补历史段；pending 不取消。

### Q8: 外围能力范围 【部分完成】
- 房间已纳入；账号/匹配/排行/生涯默认排除，待收束确认。

### Q9: 引擎侧改动授权 【Completed → 选项 a】
- 决策：允许改 `Orc.Mcp`（改＋加），`Orc.Game` 只做最小改动（段导出加 viewer 入参、投影补字段）；`MatchExecutor` 的单局约束保留，房间层加在其前面。

### Q10: 卡组与卡包来源 【部分完成】
- 已定：卡组由控制器携带；AI 卡组由房主可选、缺省服务端生成（Q20）。
- 仍待定（默认值已给，待收束确认）：卡包由房主在"开启房间"时通过服务端启动参数指定（`--packs <dir>`），客户端不上传卡包内容；两端以卡包指纹对账。

### Q16: 控制器的数据契约与卡组校验归属 【Completed → 选项 a】
- 决策：controller = `{kind, seat, displayName, deck[], deckConfig{mainFaction, allyFaction}, ready}`；服务端全权校验（张数/已装载卡池/同名上限/主国盟国配对），填充时校验、开局前复检；`MatchLaunchOptions` 需补构筑配置字段。
- 遗留：服务端没有"准入/认证池"概念（`VerifiedCardPool` 在前端），权威依据待 Q19 裁决。

### Q19: 卡池权威边界与威胁模型 【Completed → 选项 a】
- 决策：已装载卡池为唯一权威；不承诺防作弊（房主自托管）；协议保留 `packPolicy`（首版 `loaded`）；房间快照带卡包内容指纹（暂定）。

### Q20: AI 控制器的卡组与难度 【Completed → 选项 b（c 为后续）】
- 决策：`room.add_ai { seat, deck?, deckConfig? }`，与人类同契约，缺省服务端生成；难度单档（复用 `AiPilot`）。
- 后续演进（不在本阶段）：引入「卡组资产」——依赖卡牌资产、纯数据容器，届时作为卡组来源。

### Q21: 卡组资产 【Not Applicable — 本阶段不做】
- 用户明确"现在先不做"。本阶段禁止新增资产种类。

### Q22: 动作可用性投影的归属 【Completed → 选项 a】
- 决策：服务端新建 `actions` 投影面；`view` 与 `actions` 同一条消息、同一快照；客户端零规则判断。
- 待实现 grill：actions 异步计算的时序（宁可延后一拍，不可先推 view）。

### Q23: 推送与拉取模型 【Completed → 选项 a】
- 决策：纯推送（`view.updated{seq, view, actions}`），无 ack，按 controller 逐个生成（非广播）；重连拉全量、不补历史段；view 幂等只留最新。

### Q24: 选靶契约真源与应答者路由 【Completed → 选项 a】
- 决策：`PendingSelection` 为真源；应答者由 seat→controller 路由；`target.claim` 退役；超时推 `target.expired` 后 cancel；断线保留 pending 并重推。

### Q25: 段的隐藏信息过滤 【Completed → 选项 a】
- 决策：段按 seat 生成过滤版；隐藏实体替换为 `{"$hidden":true}`（不带名字、不带引用键）；可见性判定投影与段共用同一实现。
- 事实：`card.drawn` / `card.hand.add` 载荷＝{玩家, 卡牌实例}（`PlayerManager.cs:88`、`MatchActionServices.cs:161-166`），`JsonValueWriter` 降级为 `{"$entity": 卡名}` → 对手抽牌会把卡名写进段。
- 遗留（实现 grill）：`EventSegmentJson.Serialize` 无 viewer 参数，需新增可见性入参。

### Q26: 断线后的对局推进 【Completed → 选项 a】
- 决策：房间态断线立即释放；对局态保留座位至终局，轮到断线方宽限 60s 后服务端代 `Concede`；重连凭 `seatToken`；房主可 `room.kick`；断线靠应用层心跳判定。

### Q27: 收束确认（R10 外围 + 术语登记） 【In Progress — 本轮主问题】
- R10：观战/回放、账号/匹配/排行/生涯数据默认排除，是否成立。
- Q14：房间(Room)/房间态(Lobby)/座位(Seat)/控制器(Controller)/会话(Session)/房主(Host Player)/托管客户端(Hosted Client) 是否写入 `CONTEXT.md`。

### Q17: 开局触发 【Pending — 新】
- 座位全部就绪后自动开局，还是房主显式 `room.start`（影响连接时序与错误处理）。

### Q18: 房主座位 【Pending — 新】
- 房主是否自动占 seat 0，或开房后仍需显式占座。

### Q12: agent 客户端的接入面 【Pending — 新】
- agent 是"远程协议客户端"（与 Godot 同权）还是"服务端内置 AI 的另一形态"；若同权，agent 是否需要完整投影还是只需动作+候选。

### Q13: 观战/回放 【Pending】
- 默认排除是否成立。

### Q14: 术语登记 【Pending】
- 房间/座位/会话/服务端/客户端/托管客户端 是否写入 `CONTEXT.md`（当前 CONTEXT 无这些词，且"段""投影""引用键"已有权威定义，不得另造同义词）。

### Q15: Nexum 的定位 【Completed — 裁定为不采用】
- 事实：net10-only、RMI+二进制、无远程对象能力、极早期（1 star/34 commits/无 release）、README 自述"默认非前向安全，需 pinning 或 TLS/VPN"。
- 裁定：**不作为依赖引入**。活对象问题的正解是"服务端持有活对象 + 线上只传引用键/投影"，与传输库无关。若未来需要 UDP/内网穿透，评估 `LiteNetLib`（netstandard2.1、成熟）或 TCP/WebSocket over 内网穿透。

## 实现类问题（暂记，待实现 grill）

- I1：协议信封、版本协商、错误码统一（`McpToolResult` vs JSON-RPC `error`）。
- I2：服务端唯一串行写者（网络回调 → Channel → 单消费者 → 引擎），禁止 sync-over-async（现 `TrySubmitTargeting` 即为反例）。
- I3：`room/seat/session` 状态机（连接、声明、就绪、开局、终局、断线保留）。
- I4：整枚 `UiMatchView` 上线 + 枚举序列化 + `JsonSerializerContext`。
- I5：`LogEntry.Data` 引用降级改为 `$ref` GUID（现形态被测试固化，需同步改测试）。
- I6：`IMcpHost` 去活对象化（或新建纯协议宿主面，保留旧面给同进程测试）。
- I7：客户端工程与引擎源码树解耦（`Orc.UI.Contracts` 独立化 / 协议侧 DTO）。
- I8：AI 决策面（"仅投影"网关 vs 服务端内置）。
- I9：多进程/跨端测试形态（现在零进程边界测试）。
