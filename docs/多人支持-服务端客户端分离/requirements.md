# Requirements — 多人支持（服务端 / 客户端分离）

> 状态：**需求已冻结 ＋ 实现已验收**（2026-10-20）。R1–R15 全部确认；验收勾选反映**实际实现与测试状态**（`dotnet test OrcEngine.slnx` → 2147 通过 / 0 失败）。
> 实现路径见 `implementation.md`；客户端接入见 `客户端接入指南.md`；取证基线见 `requirements-grill-plan.md`。

一句话：把 OrC 逻辑引擎从客户端进程内抽出，做成**一个实例只服务一场对局**的独立服务端；服务端以「房间」承载多个「座位」，每个座位必须由一个「控制器」填充（本地玩家／远程客户端／服务端 AI）才能开局；所有客户端只经字符串形态的远程协议交换动作、投影、事件段与选靶应答。

## Scope

- Includes：
  - 房间（Room）与房间态（未开局）：服务端进程启动后先进入房间态。
  - 座位（Seat）与控制器（Controller）：座位必须被控制器填充才算就绪；控制器携带卡组等玩家信息。
  - 控制器的三种来源：本地玩家（房主）、远程客户端（MCP/远程连接声明 seat）、服务端生成 AI。
  - 房主在客户端内"开启房间"→ 客户端拉起服务端进程（服务端处于房间态）。
  - 一条字符串形态的协议（MCP 外形），承载房间/座位/动作/投影/事件段/选靶/生命周期。
  - 客户端形态无关：本地、局域网、广域网、agent；仅服务端内置 AI 不经过协议。
  - 局域网先行；广域网依赖内网穿透（不作为本阶段硬验收）。
- Excludes：
  - 多桌并发（一个服务端实例只服务一场对局，不提供 matchId 分域）。
  - 账号 / 匹配 / 排行榜 / 生涯数据。
  - 观战 / 回放。
  - 引擎内部规则变更（触发器、效果、判定器语义）。
  - 卡牌数值与平衡调整。
  - WebSocket 承载（R5 中列为可选；本阶段只交付 NDJSON/TCP）。
  - 卡组资产（R13 后续演进）。

## Constraints（实现后复核）

- `MatchExecutor` 单进程只允许一个活跃对局，`Attach` 冲突即抛——房间层独立于执行器，未复用。
- 引擎全域无锁单线程语义：跨进程入口统一经 `SerialWorkQueue` 单消费者执行（`target.*`/`session.*` 为交互优先级）。
- 选靶必须在服务端终结：`PendingSelection` 为线上真源，`Ref<Entity>` 只在服务端内存在。
- 投影必须吃活 `Match`/`Player`，只能在服务端算；`ProjectView` 经只读执行面（不取门禁、不判忙）。
- ~~动作可用性不在引擎侧~~ → **已解决**：`actions` 投影面在服务端补算（可打出/移动/攻击候选/部署空隙）。
- ~~`JsonValueWriter` 降级为 `{"$entity":名字}`~~ → **已改为** `{"$ref": GUID, "name": 名, "$alive": bool}`，不可见实体写 `{"$hidden": true}`。
- 引用键随对局生灭；`KeyOf` 对失效引用返回空串（仍成立，客户端不得长期缓存 ref）。
- ~~全仓无网络栈~~ → **已新增** `Orc.Protocol`（协议 DTO/NDJSON）、`Orc.Client`（连接/推送/子进程）、`Orc.Server`（房间/传输/宿主）；序列化仍只用 `System.Text.Json`。
- **分发形态**：安装包同时含客户端与服务端；不追求"客户端工程零引擎依赖"，但线上一律自描述 JSON，不依赖 DTO 类型名/二进制布局。
- ~~断线需心跳策略~~ → **已实现**：`ping` ＋ 服务端 45s 空闲判定；客户端崩溃由 `--parent-pid` watchdog 兜底。
- ~~建局校验只到张数~~ → **已补** `Orc.Game.Decks.DeckRules`（张数/卡池/同名 4·3·2·1/主国盟国配对），`MatchLaunchOptions` 已透传构筑配置。
- 活对象依赖是**语义问题**，不是传输问题（Nexum 不采用，见 grill plan Q15）。

## Requirement Items

### R1: 房间与房间态
- Status: **Confirmed**
- Scenario/Trigger: 房主在客户端内选择"开启房间"。
- Behavior:
  - 客户端拉起服务端进程；服务端启动后处于**房间态**（无对局、可查询房间）。
  - 服务端实例内只有一个房间，房间持有多个座位（当前 1v1：seat 0/1）。
  - 房间态下可查询房间快照（每个 seat 的控制器类型/显示名/就绪状态）。
  - 房间从房间态进入对局态由显式动作触发（`room.start`）；对局态不可回到房间态。
- Acceptance:
  - [x] 服务端启动后无活跃对局（`room.info.state == "lobby"`，`BootstrapTests`）。
  - [x] 协议可查询房间快照，且快照为纯 DTO（裸 socket 测试）。
  - [x] 座位未全部就绪时 `room.start` 被拒（`RoomTests.Start_RequiresAllSeatsFilled`）。
  - [x] 对局后二次 `room.start` 被拒（`RoomTests.Start_Twice_IsRejected`）。

### R2: 座位与控制器
- Status: **Confirmed**
- Scenario/Trigger: 房间态内填充/查询座位。
- Behavior:
  - 每个 seat 必须被恰好一个控制器填充；未填充＝空座，不可开局。
  - 控制器携带卡组、显示名、主国/盟国构筑配置与就绪标志。
  - 一个 seat 只有一个控制器，填充后需先释放才能换人（或房主强制释放）。
- Acceptance:
  - [x] 任一 seat 为空时无法开局（`RoomTests.Start_RequiresAllSeatsFilled`）。
  - [x] 同一 seat 二次填充被拒（`RoomTests.Claim_SameSeatTwice...`）。
  - [x] 控制器可查询为纯 JSON（`RoomSnapshot`）。
  - [x] 释放后快照立即反映为空座（`RoomTests.Release_ByHostToken_ClearsSeat`）。

### R3: 控制器类型与 AI 托管
- Status: **Confirmed**
- Scenario/Trigger: 填充座位时。
- Behavior:
  - 控制器类型三种：`local`（房主）、`remote`（远程客户端）、`ai`（服务端托管）。
  - 远程控制器绑定连接会话；断开后按 R9 处理。
  - AI 由服务端进程内直接驱动，**不经过协议**；与远程客户端共用同一执行门禁与选靶状态机。
  - 房主可在房间态要求服务端生成简单 AI 占座。
- Acceptance:
  - [x] 三种控制器都能让座位就绪（`RoomTests`）。
  - [x] AI 座位人类客户端无法再声明（`Room.AddAi` 占座后 `Claim` 返回 `seat-taken`）。
  - [x] AI **无需任何远程连接即可行动**（`AiSeat_TakesItsTurn_WithoutAnyRemoteClient`：AI 自行确认换牌并推进回合）。
  - [x] AI 每步经 `SerialWorkQueue` 执行，与远程动作串行（同门禁）。

### R4: 客户端形态无关
- Status: **Confirmed**
- Behavior:
  - **不承诺引用隔离**：发行包同时带客户端与服务端，客户端可引用 `Orc.*`。
  - **承诺边界隔离**：只交换字符串协议载荷，不传活对象、不共享内存、不直调引擎 API。
  - 本地/局域网/广域网/agent 走同一套协议语义；只有服务端 AI 不走协议。
- Acceptance:
  - [ ] 同一报文在本地/局域网/agent 三种客户端结果一致：**只验证了 loopback**；agent 侧未接真实客户端。
  - [x] 客户端侧无直读引擎旁路（`Orc.Client` 不含业务规则，只有 DTO 与连接）。
  - [x] 协议载荷无引擎类型（裸 socket 手写 JSON 测试）。
  - [x] 服务端断开后客户端降级为不可用（`ClientCall_Fails_WhenServerCloses`，不回落本地引擎）。

### R5: 远程协议（字符串）
- Status: **Confirmed**
- Behavior:
  - 唯一协议面＝MCP 外形（JSON-RPC 2.0 ＋ `tools/*` ＋ notification），不追求外部 MCP 生态互通。
  - `room.*` 承载房间/座位/控制器；服务端主动推送走无 id 的 notification。
  - 传输与协议解耦：首版 NDJSON over TCP；**WebSocket 为可选承载（本阶段未实现）**。
  - 所有过线载荷可 JSON 化。
- Acceptance:
  - [x] 报文可被第三方工具解析（`RawProtocolContractTests` 手写 JSON）。
  - [x] 含版本号与握手拒绝路径（`VersionMismatch_IsRejected`）。
  - [x] 可主动推送（`notify/view`/`notify/segment`/`notify/target.request`/`notify/target.expired`）。
  - [ ] 同一报文在 NDJSON/TCP 与 WebSocket 下一致：**WebSocket 未实现**（Windows `HttpListener` 需 URL ACL/管理员，成本高于收益）。

### R6: 投影过线
- Status: **Confirmed**
- Behavior:
  - 服务端直出整枚 `UiMatchView`，只填引擎侧字段。
  - `ArtPath`/`Text` 留空，客户端按 `cardId` 查本地表。
  - 客户端只消费、不组装、不判断。
- Acceptance:
  - [x] 单条消息可渲染战场/手牌/资源/结果（`EndToEndTests` 断言整枚 view）。
  - [x] 两个 seat 分别脱敏（双视角 e2e）。
  - [x] 投影不含磁盘绝对路径。

### R7: 选靶请求-应答
- Status: **Confirmed**
- Behavior:
  - 真源＝`PendingSelection`（会话 id ＋ 候选序号 ＋ 标识），客户端以序号回传。
  - 应答者由选择器所属 seat 的 controller 决定。
  - 超时：推 `target.expired` 后 `CancelEvent`。
  - 断线：controller 保留；重连时若 pending 存活则重推。
- Acceptance:
  - [x] 请求含候选序号/种类/引用键/展示名/存活。
  - [x] 非应答者提交被拒（AI 座位校验 + 按 seat 路由）。
  - [x] 超时不永久挂起（`notify/target.expired` e2e）。
  - [x] 跨座位路由：归属座位 1 的选靶只推给座位 1（`CrossSeatTargetingTests`）。
  - [x] 重连重推 pending（`BindSeat` 内重推）。

### R8: 隐藏信息
- Status: **Confirmed**
- Behavior:
  - 投影前按 viewer 过滤；段按 seat 生成过滤版，隐藏实体写 `{"$hidden":true}`（不带名字/引用键）。
  - 可见性判定只有一份实现（`MatchEventVisibility`）。
- Acceptance:
  - [x] 任一座位报文不含对手手牌明细（双视角 e2e）。
  - [x] 段中对手抽牌只暴露"发生了一次抽牌"（e2e 断言 `$hidden`、无 `$entity`）。
  - [x] 判定不依赖客户端遮蔽（服务端导出时完成）。

### R9: 断线重连
- Status: **Confirmed**
- Behavior:
  - 座位归属控制器，重连凭 `seatToken`。
  - 房间态断线：立即释放座位。
  - 对局态断线：座位保留至终局；轮到断线方时宽限（默认 60s）后服务端代其 `Concede`。
  - 断线判定依赖应用层心跳。
- Acceptance:
  - [x] 重连后不重启对局即可恢复渲染（`Reconnect_WithSeatToken`）。
  - [x] 无令牌无法接管（错误令牌被拒）。
  - [x] 断线期间选靶有确定归属（超时取消 + `target.expired`）。
  - [x] 长期不回来有终局路径（`DisconnectedSeat_LosesAfterGrace`）。
  - [x] 房间态断线立即释放（`LobbyDisconnect_ReleasesSeatImmediately`）。
  - [x] 心跳超时断连（`IdleConnection_IsClosed_ByHeartbeatTimeout`）。

### R10: 外围能力范围
- Status: **Confirmed**
- Behavior: 观战/回放/账号/匹配/排行榜/生涯数据全部排除。
- Acceptance:
  - [x] 协议无观战者角色、无第三方只读连接。
  - [x] 协议无账号/身份/匹配字段。

### R11: 控制器契约与卡组校验
- Status: **Confirmed**
- Behavior:
  - 控制器契约：`{ kind, seat, displayName, deck[], deckConfig{mainFaction, allyFaction}, ready }`。
  - 填充时服务端完整校验：张数 39、卡 id 在已装载卡池、同名上限（standard 4 / limited 3 / special 2 / elite 1）、主国/盟国配对。
  - `room.start` 前复检。
  - 客户端自检只作体验优化。
- Acceptance:
  - [x] 未装载/跨包 id 被拒（`UnknownCard_IsRejected`）。
  - [x] 张数/同名/配国违规被拒（`DeckRulesTests` + `OverCopyLimit` + `MissingMainFaction`）。
  - [x] `MatchLaunchOptions` 已补构筑配置字段。
  - [ ] "现有 demo 卡组修正到符合上限"：**未改数据**。示例卡包仅 5 种卡（同名上限下最多 20 张），无法组成 39 张合法构筑；诊断/AI 夹具显式关闭规则。生产卡包必须满足。
  - [x] 校验不依赖客户端自检。

### R12: 卡池权威边界与威胁模型
- Status: **Confirmed**
- Behavior:
  - 合法卡唯一依据＝已装载卡池；不承诺防作弊（房主自托管）。
  - 协议保留 `packPolicy`（首版 `loaded`）；房间快照带卡包内容指纹。
- Acceptance:
  - [x] 未装载 id 被拒且不进 `Match` 构造。
  - [x] 服务端不依赖任何前端认证产物。
  - [x] 快照含 `packPolicy` 与指纹（SHA-256 前 16 位），不含绝对路径。

### R13: AI 控制器的卡组与难度
- Status: **Confirmed**
- Behavior:
  - `room.add_ai { seat, deck?, deckConfig? }`：与人类同契约；缺省由服务端生成合法构筑。
  - 难度单档（复用 `AiPilot` 贪心策略）。
  - 后续演进：卡组资产（本阶段不做）。
- Acceptance:
  - [x] 无配置时生成合法 AI 控制器（`AiDeckGenerator`；容量不足返回空并报 `deck-invalid`）。
  - [x] AI 与人类 controller 在快照中同构可见。
  - [x] 本阶段未新增资产种类。

### R14: 动作可用性投影（battle actions）
- Status: **Confirmed**
- Behavior:
  - 服务端 `actions` 承载：可打出集合、可移动槽、可攻击候选、部署空隙、能否结束回合、换牌开关。
  - `view` 与 `actions` 永远同一条消息、同一快照。
  - 客户端不得复制规则判断。
  - **例外**：指令目标为交互式提供（拖出即 `play.command_pre`），不做静态预览；取消零副作用。
- Acceptance:
  - [x] 客户端零规则判断即可高亮（`actions` 三组候选 + 状态位）。
  - [x] 同拍（同一对象内构造）。
  - [x] 不出现"先推 view 后补 actions"。

### R15: 推送与拉取
- Status: **Confirmed**
- Behavior:
  - 纯推送（`notify/view` + `notify/segment`），客户端不轮询。
  - 按 controller 逐个生成，非广播。
  - 无 ack，依赖 TCP 可靠有序。
  - 重连拉全量，不补历史段。
  - 背压：有界推送队列（丢最旧）。
- Acceptance:
  - [x] 视图与段按序到达（同一 socket，段紧随 view）。
  - [x] 重连取全量（`observe.view`）。
  - [x] 慢客户端不导致服务端内存线性增长（有界 256 + `DropOldest`）。

## Terminology

已登记至 `CONTEXT.md`（第 401 行起）：**服务端（Server）**、**客户端（Client）**、**房间（Room）**、**房间态（Lobby）**、**座位（Seat）**、**控制器（Controller）**、**会话（Session）**、**座位令牌（Seat Token）**、**房主（Host Player）**、**托管客户端（Hosted Client）**。
沿用既有术语：投影、段、引用键、动作作用域、观察面、接入点。
