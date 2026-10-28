# Implementation — 人类 CLI 对局客户端（文字游戏界面）

骨架已建立：现行协议（v2）与服务端工具面已逐条核对（基线事实 F1–F23）；D1–D16 已裁决并回填，13 个步骤的 Approach/验收/Rationale 全部落地，无遗留"it depends"。执行前请先读「已知风险与本期不做」。

## Scope

- Target：`tools/` 下的 Python 客户端（`play` 对局形态 ＋ `watch` 观战形态），默认全屏 TUI，`--plain` 行式退路；只连回环地址（`127.0.0.1`）。
- Excludes：沙盒/调试面（`card.*`/`sandbox.*`/`editor.*`/`asset.*`/`observe.snapshot`/`observe.segments`）、资产编辑、跨主机联机、图形表现、AI 策略（沿用服务端 `Orc.Ai`）。

## 基线事实（读代码所得，作为设计前提，非决策）

- **F1 传输与帧**：TCP ＋ NDJSON ＋ JSON-RPC 2.0。请求 `id` 必须是**字符串**；推送无 `id` 且**会穿插在响应之前**（`src/Orc.Server/Transport/ServerHost.cs:270-360`；`tools/verify_live_match.py:44-63` 已踩过）。
- **F2 两个连接面**：实例业务端口（`initialize`/`ping`/`session.attach`/`session.detach`/`tools.list`/`tools.call` ＋ 7 条推送）与管理器端口（方法名即工具名 `manager.ping|list|spawn|kill|reload|admin|connect|action|sessions|disconnect`，见 `src/Orc.Manager/ManagerServer.cs:175-280`）。
- **F3 管理器代持连接不转发推送**：`manager.connect/action` 走的 `OrcClient` 只对 `notify/admission` 注册了内部回调（`src/Orc.Manager/ManagerSessions.cs:62-77,189-207`），`notify/view|segment|target.request` 不会到达调用方。⇒ 界面所需的实时帧必须来自**自持的实例直连**。
- **F4 申请—批准两段**：`session.attach` 立即返回 `state:"pending"`；AI 座「配置即已批准」；`match.start` 前要求**无空座/无未批准座位**（`src/Orc.Server/Admin/AdminApi.cs:160-163`）。批准走 `manager.admin{op:"conn.admit",args:{connectionId}}`。
- **F5 座位拓扑**：`SeatBinding.PlayerIndex == Seat.Index` 恒等（`src/Orc.Server/Room/RoomContracts.cs:40-62`）；AI 座由 spawn 的 `seats[i].kind=="ai"` 声明（`src/Orc.Manager/ManagerContracts.cs:118-132`）。
- **F6 帧形状**：绑座 `{kind:"view.updated",perspective:{mode:"seat",seat:N},view,actions}`；全知 `{kind:"view.updated",perspective:{mode:"omniscient"},views:[{seat,view,actions}×2]}`（`src/Orc.Mcp/McpService.Projection.cs:31-130`）。对手手牌**只有张数**，无卡视图。
- **F7 视图内容**：`UiMatchView` 只有 `selfLine`/`enemyLine`（支援线与前线**混排**，每卡自带 `zone:"support"|"frontline"` ＋ `slotIndex`）＋ `frontLineSlotCount`/`supportLineSlotCount`；**空槽不是实体**，不出现（`src/Orc.UI/UiProjectionProvider.cs:220-233`，`src/Orc.UI.Contracts/UiViewModel.cs`）。
- **F8 `actions` 载荷**：`playableUids` / `moves[{uid,toZone,slotIndex}]` / `attackPreviews[{attackerUid,defenderUid,damageToDefender,…}]` / `deploymentGaps[{uid,gaps:[gapRef…]}]` / `canEndTurn` / `mulliganOpen`；`orderTargets` 恒空（`McpService.Projection.cs:230-367`）。`gaps` 是**阵型空隙的 GUID**，不是槽位序号。
- **F9 部署有两条服务端入口且语义不同**：`play.unit{ref,owner,slot}` 直调物理支援槽（`PlayManager.PlayUnitAsync(card, Slot)` 只校验「槽为空 ∧ 未单位化」，`src/Orc.Game/Managers/PlayManager.cs:224-276`）；`play.unit_pre` → 待决选择 → `play.unit(card, FormationGap)`（复验空隙身份并**整线重排**，`src/Orc.Game/Board/BattleLine.cs:47-65`）。HQ 初始占槽 2，插入后位置可变（`Battlefield.HqSlotIndex = 2`）。
- **F10 待决的形状（JSON 键名以代码为准）**：`pending = {sessionId, selector, mode, min, max, candidates:[{index(1 起), kind, ref, name, alive}], identifiers, seat?, action?}`（`src/Orc.Mcp/McpService.cs:310-345`；注意候选引用键的 JSON 名是 **`ref`**，对应 C# 记录里的 `ReferenceKey`，`name` 对应 `DisplayName`，见 `TargeterBridgeAdapter.cs:14-19`）。`notify/target.request` 与 `notify/target.expired` 的 `params` **就是**这个 pending 对象本身，不另包一层（`ServerHost.cs:1203-1206,1671,1685`）。换牌是同一套 target 会话（selector 名 `"mulligan"`，`min:0..max:手牌数`）（`MulliganManager.cs:150-181`）。
- **F14 待决会重复到达，且响应体里可能已带候选**：动作进入待决时执行器**提前返回** `ActionOutcome.Targeting`（`MatchExecutor.cs:336-341`），因此 `tools/call` 的响应就是 `{ok:true, code:"targeting", details:{pending}}`（`ToolResults.cs:28-34`）；同时 `PushToSession` 在**每次状态变化**时都会重推同一 pending（`ServerHost.cs:1187-1207`）。⇒ 客户端必须按 `sessionId` 合并去重，只保留一份。
- **F15 认领"我的实例"所需的读面**：`manager.list` 每条含 `instanceId/processId/port/roomId/mode/runDir/label/packDirectories/startedAt/running`（`ManagerServer.cs:353-367`）——足以凭 session.json 里的 `instanceId`＋`roomId` 判定"实例是否还在、房间是否还是同一局"。对局态 controller 断线时**宽限只在轮到该座位时计时**，到期由服务器代其认输（接入指南 §7）。
- **F16 凭令牌回座的真实路径**：`session.attach{token}` 命中令牌 ⇒ 直接置 `Admitted`（只申请 viewer 时置 `Viewer`）、**跳过 pending**，且**不校验 deck/packages**（`ServerHost.cs:493-513`）；令牌无效 ⇒ `code:"invalid-token"`；`seat-taken` 只在「房间非 Lobby 且该座已被占」时同步返回（`ServerHost.cs:553-559`）。
- **F17 轮换按 spawn 配置重建房间**：新房间由 `_options.Seats` 重建，AI 座仍是 AI 且"配置即已批准"，人类座回到 idle（`ServerHost.cs:179-186,1476-1479`）⇒ **同实例重开**只需 `attach`（不带 token）→ `conn.admit` → `match.start`，不必 kill/spawn。
- **F18 管理器 `serve` 的启动契约**：`dotnet Orc.Manager.dll serve --port N` 在 stdout 打**唯一一行** `{"ready":true,"port":N}` 后进入等待，只响应 Ctrl+C / 进程退出（**不读 stdin**，`Cli.cs:151-189`）；`--port 0`＝系统分配，实际端口由就绪行给出。反过来，服务器实例随管理器退出而退出（`src/Orc.Server/ParentWatchdog.cs`）。
- **F19 `start-manager.bat` 的 preflight 未被就绪行覆盖**：bat 检查 dotnet 在 PATH、`Orc.Manager.dll`/`Orc.Server.dll` 存在、**`Orc.Server.dll` 必须在 `Orc.Manager.dll` 同目录**、端口未被监听（`start-manager.bat:101-138`）；其中"Server.dll 同目录"缺失时管理器能正常就绪、但每次 `spawn` 都 `instance-boot-failed`（管理器MCP接入 §5）。该 bat 结尾带 `pause`，不适合被 CLI 直接当子进程调用。
- **F11 段的条目形状**：`{kind,timestamp,level,source,message,keywords,data}`；实体引用降级为 `{$ref,name,$alive}`，脱敏后的实体为 `{$hidden:true}`（`src/Orc/Output/EventStreamJson.cs:78-116`，`JsonValueWriter.cs:154-208`）。可见性过滤已在服务端完成。
- **F12 轮换**：终局 → `notify/room.rotated{roomId,seatCount,epoch}`；绑座角色退回 `Revoked`、旧 `seatToken` 失效、房间回到 `lobby`；需重新 `session.attach`（带 deck/packages）＋ `match.start`（`ServerHost.RotateAsync:1455-1536`）。
- **F13 卡包声明与卡池装配分离**：`session.attach.packages` 必填但服务端只登记（`Room.PackagesOf` 无消费方）；真正装配卡池的是 `spawn.packDirectories` 或 `match.start.args.packDirectories`（`AdminApi.ResolvePackDirectories:389-404`）。样例包 `samples/card-pack-ai`：`manifest.json` 提供 `id/version/guid`，5 张卡（infantry/armor/artillery/order/counter，infantry 为 `ai.sample.infantry`）。
- **F20 换牌相位是"双方确认才进 play"的闸门，且取消＝零副作用**：`mulligan.begin{owner}` 发 `"mulligan"` 选择器（候选＝该方手牌，`min:0`、`max:手牌数`），提交后**自动确认该方**；`mulligan.done{owner}` ＝不换牌直接确认；**取消＝不换、也不确认**（`MulliganManager.cs:203-206`），故取消后相位仍停在 mulligan。双方确认才置"进行"并执行先手第 1 回合（`MulliganManager.cs:74-83,277-280`）。AI 座直接 `mulligan.done`（不换牌，`AiPilot.cs:123-141`）。服务端选靶超时默认 **10 分钟**，超时按取消处理（`ServerOptions.cs:92`）。
- **F21 段条目里"事件类型"不在 `kind` 上**：`kind` 只有三值 `log`/`attach`/`update`（`Orc/Core/LogEntry.cs:4-9`）。**update 条目**恒为 `source:"bus"`、`message:`**更新名字面值**（如 `unit.deployed`）、`keywords:[更新名]`、`data:`载荷（`Bus.cs:150-151`）；**attach 条目**是 `level:debug` + `message:"子执行流挂载"` 的噪音（`EventStream.cs:131-141`）；**log 条目**的 `source` 是订阅者/触发器标识、`message` 是 handler 写的中文或异常文本（`Bus.cs:170-176`）。⇒ 翻译表的键只能是**更新名**（`message`/`keywords[0]`），不能是 `kind`。
- **F22 段推送只推新增**：`DrainSegments()` 用 `TakeSegments()` 取走新段后按角色分发（绑座＝脱敏、全知＝全量），每段带单调 `sequence`；缓存段不回灌成推送（`ServerHost.cs:1252-1331`）。
- **F23 引擎真实更新名与载荷（决定模板表能键在什么上）**：`unit.acted`＝`{Unit}`，**攻击与移动共用**、不含对方（`GameUpdates.cs:154`）；`unit.damage.dealt`＝`{Unit(施动方), Card(受方), Amount}`，单位互伤与 HQ 简路都发（`:162`）；`card.damaged`＝`{Card, Amount}`（只有受方，`:151`）；`card.died`/`card.dying`/`card.leaving` 有、**没有** `unit.destroyed`；移动只有 `unit.position.changed`（`:124`），**没有** `unit.moved`；回合是 `turn.start`/`turn.end`（另有 `.before`/`.after`，`:37-49`），**没有** `turn.started`/`turn.ended`；费用是 `point.gained`/`point.lost`/`point.changed`（`:240-253`），**没有** `point.spent`；反制是 `counter.triggered`/`counter.disarmed`（`:169-179`）；交战存活是 `unit.combat.survived`（`:187`，观察序＝被攻击者在前）。

## Decisions（已裁决，执行时按此实现）

- **D1 出牌落点＝阵型插入位序号（1 起）**：出牌第二个参数语义等于 `notify/target.request` 里候选的 `index`，**不是**物理槽索引。一次出牌统一走 `play.unit_pre` → 等 `notify/target.request` → `target.submit{sessionId, picks:[序号]}`；内联参数由 CLI 自动完成两步。**禁止**调用 `play.unit{ref,owner,slot}`（`(UnitCard, Slot)` 重载只校验"槽为空"，会把居中阵型打散；canonical 路径是 `(UnitCard, FormationGap)`）。
- **D1.1 统一序号空间**：`play` / `move` / `attack` / 选靶一律「用户敲的数字＝当前候选项序号」，绝不解释为引擎内部物理索引。内联参数与 R7 选择模式共用同一序号空间，Tab 候选只有一个来源（待决候选），客户端不维护 slot↔gap 映射表。
- **D1.2 候选展示**：`kind=formation-gap` 必须给出可读标签「插入位置 k：最左 / 在 A 与 B 之间 / 最右」——算法：取视图 `selfLine` 的占据者按 `slotIndex` 升序成队列，`ordinal = index-1`（0＝最左、N＝最右，其余夹在 `occupants[ordinal-1]` 与 `occupants[ordinal]` 之间）。这是「把序号翻译成邻居名」，不做合法性判断。若候选的 `ref` 能在 `actions.deploymentGaps[uid].gaps` 里命中，用它交叉校验顺序；找不到就只显示序号，不猜。
- **D1.3 `play.join` 本期不做**：无 pre/targeting 入口，其物理 slot 参数会与候选序号语义冲突（第二个坐标系）。从指令表移除；日后若要加，必须先确认服务端提供 gap 入口。
- **D1.4 非法序号本地拒绝**：超出候选数／空候选／非数字 → 列出有效范围、不发 submit。内联参数可在 pending 到达前输入并暂存，但以候选到达时的实际数量为准；不符则取消本次动作并提示（不静默丢弃）。
- **D1.5 以 pending 为准（经 D2.2 修订）**：`play.unit_pre` 的返回与 `notify/target.request` 是两条路径。响应体里**可能**已带 `details.pending`（F14），优先用它；不得**只**依赖推送。等待 pending 必须有超时与「取消」出口。
- **D1.6 `move` 候选展示**：候选是 Slot（`kind` 可能落到 `entity`），展示时用 `slotIndex` 标「前线第 X 号槽」，但**输入仍是候选序号**；物理槽号只能作附注，不能作输入语义。

- **D2 载体＝一个 Dispatcher ＋ 一个 Session ＋ 两个 Front**：全屏与 `--plain` 共用 Dispatcher/Session/Renderer/CommandEngine/ActionRunner，分歧只在 Front。UI 只做适配，不参与协议。
- **D2.1 三条线程，不用 asyncio 统一**：① socket 读线程 ×1（`recv` → 切帧 → 分流：带 `id` 投 `_responses[id]`；`notify/target.request` 更新 `_pending` 并唤醒等待者；其余进 `push_queue` 并回调 `on_frame`；写侧 `send()` 单独一把输出锁）；② 动作 worker ×1（**所有** `tools/call` 在它上面串行排队，UI 线程只投递意图，绝不直接发网络请求）；③ UI 线程 ×1（TUI 下是 prompt_toolkit 的 loop，plain 下是主线程命令循环）。理由：Windows Proactor 对 stdin 支持差，prompt_toolkit 自带 loop，两套 loop 混用只会自找麻烦；跨平台写法＝读线程＋Future＋queue，TUI 侧用 `loop.call_soon_threadsafe(app.invalidate)`。
- **D2.2 一个 Dispatcher，两个键空间**：`call(tool,args,timeout)` 阻塞在 `_responses[id]` 的 Future；`wait_pending(session_id|None, timeout)` 阻塞在 `_pending` 的条件变量。响应与 pending 会竞争（见 F14）⇒ **内联动作不需要二次等待**：`call` 返回 `code=="targeting"` ⇒ 候选已在手，立刻 `target.submit`；返回其它 ⇒ 动作已完成。只有「响应非 targeting 但随后 pending 到达」这种反常路径才启动 `wait_pending`（短超时兜底）。
- **D2.3 Session＝单写状态仓库**：`view`/`actions`/`pending`/`log(deque,maxlen=200)`/`phase`/`turn`/`kredits`/`outcome`/连接态/admitted。写者只有读线程与动作 worker，UI 只读「快照拷贝」（`RLock` 只保护拷贝，不保护渲染）。所有"当前状态"判断都从快照读，不从 UI 控件读。
- **D2.4 两个 Front 的共用与分歧**：共用 Dispatcher、Session、`Renderer`（纯函数 `Session → list[str]`）、CommandEngine、ActionRunner、日志 deque。`TuiFront`＝prompt_toolkit `Application`+`Layout`，`HSplit([对手区, 我方区, 日志窗口, 选择区(条件显示), 输入行, 提示条])`，日志**自绘最近 N 行**（不依赖终端 scrollback），渲染由推送驱动（`on_frame` → `invalidate()`）。`PlainFront`＝同一 Renderer，每条命令后打印一块自包含快照＋自上次以来的新日志，**纯文本无 ANSI**；推送到达只追加日志 deque、不插队打印（避免切碎输出块），`--stream` 可开实时打印；**非 TTY 自动降级为 plain**（无需额外参数），逐行读 stdin、逐条执行、退出码反映最后一次失败。
- **D2.5 门禁与取消**：`pending != null` 时 CommandEngine 只接受序号 / `ok` / `x` / `log` / `help`，其余本地拒绝并提示「请先完成选择」，不发请求。`x`：pending 已就绪 ⇒ `target.cancel`；pending 未就绪但 pre 在途 ⇒ 置 `AbortIntent`，等 pre 响应回来：带 targeting 立即 cancel，已 completed 则只提示「动作已完成，无法取消」——绝不留下悬挂 pending。Ctrl-C 在 TUI 里只清空当前输入行，`quit` 才退出（退出前按 R5 处理实例归属）。
- **D2.6 可测试性是硬要求**：`IDispatcher` 接口 ＋ `FakeDispatcher`（注入任意帧序列，无需 socket、无需 TTY）。单测断言三件事：① CommandEngine 发出的工具调用序列（含 `pre → submit` 两步、非法序号**不发** submit）；② Renderer 在给定 Session 下的文本行（含中文对齐）；③ pending 期间的指令门禁。`PlainFront` 本身即端到端断言入口（喂 stdin、抓 stdout）。
- **D2.7 Renderer 只产出行**：颜色/样式由 Front 附加，`--plain` 拿到同一批行的无样式版本；中文宽字符在 Renderer 层按 display width（`east_asian_width`）算列宽，不用 `len()`——否则 TUI 与 plain 会错得不一样。
- **D3 启动判定链只依据两条可观测事实**：「实例是否还在 `manager.list` 且 running」与「令牌是否还能回座」。**不**引入任何"上次怎么退出的"落盘推断——进程被 kill 时来不及写退出原因，基于 `lastExit` 的判定在真实崩溃下必然失效。
- **D3.1 判定链（`play`）**：
  `0` 读 `session.json`（默认固定路径；`--session-file` 可换用于多开；`--new` 强制忽略记录）→
  `1` `manager.ping` 失败 ⇒ 按 R5 报错退出（附启动指引）→
  `2` 记录存在 ∧ 其 `instanceId` 在 `manager.list` 中 ∧ `running=true`：(a) 连记录的 `port`；(b) 先试回座 `session.attach{roles,seat,token,deck,packages}`——`state:"admitted"` 且 `manager.list` 的 `roomId` 与记录一致 ⇒ **回座**（跳过 spawn/`conn.admit`/`match.start`）；`code:"invalid-token"` ⇒ 转 (c)；`code:"seat-taken"` ⇒ 放弃该实例，转 `3`；(c) **同实例重开**（实例仍是 lobby）：`attach`（不带 token）→ `conn.admit` → `match.start`，**不 kill 不 spawn**，成功后把新 `roomId` 与新令牌写回记录 →
  `3` 记录不存在／实例不在清单／`running=false`／`seat-taken` ⇒ **新开**：`spawn{instanceId:"cli-<uuid8>", label:"cli:<uuid8>", mode:"play", deckRules:"none", packDirectories}` → `attach` → `conn.admit` → `match.start` → 写记录 →
  `4` 记录清理：正常退出 ∧ kill 成功 ⇒ 删除；`--keep-instance` ⇒ 保留；实例已消失 ⇒ 覆盖/删除。
- **D3.2 `session.json`（`schemaVersion:1`）字段**：`cliSessionId`、`instanceId`(`cli-<uuid8>`)、`label`(`cli:<uuid8>`)、`manager`(`<host:port>`，见 D4.8)、`port`、`roomId`、`seat`、`seatToken`、`packDirectories`、`deck`、`updatedAt`。**不存**退出原因／是否 keep／上次是否正常。`instanceId` 是程序化认领键，`label` 是 `manager.list` 里给人看的归属标记（补管理器缺归属字段的缺口）。
- **D3.3 认领规则**：只认记录里的 `instanceId`；**绝不**按"最新创建／端口最大"之类启发式猜；认不出就当没有记录。别的 agent 起的实例一律不碰。
- **D3.4 写入时机**：spawn 成功后立即写（含 port/roomId/deck）；每次拿到新令牌更新 `seatToken`；帧处理后只更新 `updatedAt`（节流 ≥2s，不必每条落盘）。
- **D3.5 边界确定行为**：① 上次 `--keep-instance` 退出、实例在、令牌有效、当时轮到 AI ⇒ 直接回座，立刻按当前帧渲染；AI 由服务端独立驱动，客户端不催；别人的实例不动（D3.3）。② 终端被关、实例在但已轮换（`roomId` 已变）⇒ 令牌必然 `invalid-token` ⇒ 走「同实例重开」，写回新 roomId 与新令牌，**不 kill、不 spawn**。③ 实例仍在 match 中但令牌失效（被 revoke）⇒ **不得强行接管**，报「该实例的对局已不属于本会话」，提示用 `--new` 新开或 `--instance <id>` 以 watch 旁观。
- **D3.6 `--keep-instance` 与"重连"是两条独立路径**，共享同一份记录：前者只改"退出是否 kill"，后者只在"启动发现实例还在"时发生；四种组合都必须有定义，且不得互相推断。`--new` 与已有记录同时出现时**不 kill 旧实例**（可能是另一个窗口在玩），只新建并把记录指向新实例。
- **D3.7 `watch` 不写 `session.json`**：只读 `--instance` 或记录里的 port，其退出不得影响任何记录。
- **D3.8 关闭是唯一出口**：`quit`、Ctrl-D、未捕获异常、终局选择"退出"都走同一 shutdown 函数（kill 或保留 → 更新记录 → 关闭 socket）。若 `manager.kill` 失败，仍按"实例是否还存在"决定记录去留——不因 kill 报错就留下指向死实例的记录。
- **D4 `--launch-manager` 拉起的是 `serve` 进程本身，不经 bat；就绪判据＝`manager.ping` 成功**（不是就绪行、也不是进程存活）。
- **D4.1 启动契约**：`dotnet <repo>/artifacts/slot/bin/Orc.Manager/debug/Orc.Manager.dll serve --port <port>`，`cwd`＝仓库根（管理器按相对路径找实例目录、把 `.orc-manager/` 落在这里）。端口默认 `51999`；`--manager <host:port>` 显式覆盖（**同时决定连谁与拉谁**）。**不支持 `--port 0`**——端口要写进 `session.json`、也要让别的 agent 的 MCP 对得上，随机端口只会制造不可复现的地址。
- **D4.2 进程形态＝detached**（Windows `DETACHED_PROCESS`，POSIX `start_new_session=True`），stdout/stderr 追加重定向到 `.orc-manager/logs/manager-<port>.log`。这样 CLI 被 Ctrl-C／被杀／终端关闭都不连带管理器，也避免"CLI 退出后管道关闭、管理器写 stdout 报错"。**不解析 `{"ready":true,…}` 就绪行**（它只证明端口在听，且重定向后读不到）。
- **D4.3 就绪判定**：每 200ms `manager.ping` 一次，上限 20s；成功 ⇒ 可用；超时或进程已退 ⇒ 失败并把日志尾部 30 行贴给用户。
- **D4.4 管理器绝不 kill**（它可能托管别人的实例）：只在日志记一行「管理器由本次会话拉起，继续常驻：<port>」。
- **D4.5 与已有管理器相遇（与是否带 `--launch-manager` 无关的统一顺序）**：先 `ping(--manager 或默认 51999)`——成功 ⇒ 复用（不拉第二个、不打噪音日志）；失败 ⇒ 带 `--launch-manager` 则按 D4.1 拉起并等 ping，否则按 R5 报错（附启动指引）退出。端口被**非管理器**程序占用（connect 成功但不是 JSON-RPC，或 connection refused 之外的异常）⇒ 报"该端口不可用"并提示用 `--manager` 换端口，**不自动换端口**。
- **D4.6 preflight 只复刻会真正致命的三项**（拉起**之前**检查，失败即报错且不启动）：`dotnet` 可执行（`shutil.which`）、`Orc.Manager.dll` 存在、**`Orc.Server.dll` 与 `Orc.Manager.dll` 同目录**。不检查端口空闲（D4.5 的 ping 已覆盖）、不检查 `mcp.json`、不写任何 MCP 配置。修复提示写具体：`dotnet build src/Orc.Manager/Orc.Manager.csproj`。
- **D4.7 失败时机与清理**：preflight 失败 ⇒ 不启动并报「缺少 X，实例无法启动」；ping 成功但首次 `spawn` 失败 ⇒ **保留管理器**，原样呈现 `code/message` 并附「管理器已在 :port 常驻，可修复后重试」；拉起进程在 ping 超时前退出 ⇒ 读日志尾部并报「管理器启动失败（exit=<code>）」；**任何情况下都不因 spawn 失败去 kill** 一个不是本次拉起的/已被复用的管理器。
- **D4.8 `session.json` 增补 `"manager": "<host:port>"`**：`watch` 与下次 `play` 默认复用它，命令行显式参数优先；`--manager` 的解析与写入必须是同一字符串形式（统一 `127.0.0.1:<port>`，不混用 `localhost`）。
- **D4.9 日志文件按端口命名**（不按时间戳，避免无限增长），写入前做一次 5MB 截断（保留尾部）。
- **D5 卡组生成＝「全部卡种」按 id 字典序循环填到 39 张**（R9 原措辞"主卡种"作废）。
- **D5.1 卡种发现（唯一口径）**：`manifest.metadata` 非空 ⇒ 逐个读 sidecar 取 `kind=card` 的资产 id；否则 ⇒ **递归**扫描包目录 `**/*.card.json`（显式排除 `*.meta.json`）。两路结果合并去重后按**资产 id 字典序升序**排序。必须递归（`card-pack-demo` 的卡分布在 `cards/` 与 `cards-support/`）；必须用 id 字典序（`manifest.metadata` 是路径列表、顺序随编辑变动，只有 id 序能保证"同一卡包每次开局手牌分布可复现"）。effect/trigger 不进卡组。
- **D5.2 填充规则**：`deck = [ids[i % len(ids)] for i in range(39)]`；目标张数**恒为 39、与卡包大小无关**（2 张的包就是 20/19）。39 是客户端侧的安全约定（`deckRules:"none"` 下服务端不校验长度）。`ids` 为空 ⇒ 启动即报错「该卡包没有可用的卡」，不 spawn。`len(ids) > 39` ⇒ 取前 39，并日志提示「卡包卡种数超过 39，仅取前 39」。
- **D5.3 `--deck` 覆盖**：**JSON 数组**，元素为卡 id 字符串，相对路径按**当前工作目录**解析；CLI **不做**长度与合法性本地校验（服务端是唯一裁决者），但启动时提示「自定义卡组 X 张，服务端将按 deckRules 校验」；给了 `--deck` 就不再自动生成；`--pack` 仍用于 `packages` 声明与卡池装配。
- **D5.4 `deckConfig` 不声明（传 null）**：`tier=none` 下服务端跳过阵营校验，主动声明只会在未来切 strict 时引入失败点。
- **D5.5 `packages` 与 `packDirectories`**：`packages` 取 manifest 的 `{id,version,guid}`（**不要**从目录名或 lock 猜）；`packDirectories` 传包**绝对路径**，`spawn` 与 `match.start` 用同一个值；`--pack` 为相对路径时按 cwd 解析后转绝对，避免管理器子进程 cwd 不同导致找不到包。
- **D5.6 卡组要落盘**：生成的卡组原样写入 `session.json`，回座时必须提交同一副牌——否则"回座"会变成"换了副牌的新局"。
- **D6 换牌相位不自动弹选择器，由玩家显式下指令；最短开局路径是单独一条 `keep`。**
- **D6.1 四条映射**：`keep`/`保留`/`不换` → `mulligan.done{owner}`（该方确认，双方齐则进 play 并跑先手第 1 回合）；`mulligan`/`换牌` → `mulligan.begin{owner}`（产生 `"mulligan"` 选择器，候选＝手牌）；选择模式内 `ok` → `target.submit`（提交后服务端**自动确认该方**）；选择模式内 `x` → `target.cancel`（**仅"不换"，不确认**，相位仍停在 mulligan）。
- **D6.2 相位门禁（本地拦截，不发服务端）**：`phase=="mulligan"`（或 `actions.mulliganOpen==true`）时，**仅** `keep`/`mulligan`/`log`/`help`/`quit`/`concede` 以及选择模式内的 `ok`/`x`/序号 放行；`play`/`order`/`attack`/`move`/`end` 一律本地拒绝并提示「换牌阶段尚不能出牌，请输入 keep 直接开始或 mulligan 选择换牌」。`end`/`结束` **不改派**成 `mulligan.done`（同一个词在两种相位下产生两种后果，最容易误判状态）。`concede` 放行——认输在任何相位都应可用。〔已裁决：上一版"六个动词之外的一切"作废。〕
- **D6.3 `x` 之后的状态文案**：必须仍表现为"仍在换牌相位"——提示条写 `[换牌阶段] 已放弃本次换牌，仍需 keep 才开始`，不得表现得像已经操作过。
- **D6.4 换牌相位固定提示条**：`[换牌阶段] keep=直接开始 · mulligan=选择换牌 · 手牌 N 张`。
- **D6.5 兜底提示**：进入该相位 60s 内无任何动作，追加**一次**（仅一次）日志行 `提示：输入 keep 即可开始对局`；不重复刷、不自动替玩家确认。
- **D6.6 漏判兜底**：服务端返回 `phase-blocked` 之类结果时，翻译成同一句人话，并强制刷新一次视图帧（以服务端相位为准）。
- **D6.7 无 pending 时敲 `x`**：不调 `target.cancel`，只回 `当前没有待选择的请求；换牌阶段请用 keep 开始`，服务端状态不变。
- **D6.8 换牌候选展示沿用 D1.2 的候选渲染规则**（序号＋中文卡名＋费用/身材），不另造一套格式。
- **D6.9 `--plain` 自动化路径必须跑通**：`printf 'keep\nquit\n' | python tools/orc_play.py play --plain` 作为 S13 端到端用例之一。
- **D6.10 换牌相位退出**：按 D3 处理实例归属；**不得**为了"帮他把相位推进"而偷偷调 `mulligan.done`（会在 `--keep-instance` 回座时留下"我没操作过却已经开始"的假象）。
- **D7 `play`/`出牌` 保持单一动词，按卡类型分派**（R6 的验收按此修正为「**当动作需要额外选择时**进入引导；不需要选择时直接完成」——不再有"所有出牌都必须引导"的读法）。
- **D7.1 分派表**：`cardType=unit` → `play.unit_pre{ref}`（必然进选择模式）；`cardType=order` → `play.command_pre{ref}`（可能直接结算、也可能进选择模式；`play.command` 与它同一实现，统一用 `_pre`）；`cardType=counter` → `play.counter{ref}`（直接结算、**无选择器**，不要给它造假的引导步骤）。
- **D7.2 分派依据**：路径只看 `view.selfHand[i].definition.cardType`（取值 `"unit"|"order"|"counter"`，`StaticCardProjection.cs:39-40`）。
- **D7.3 本地预检只用手牌可打出性，且仅在可信时**：当 `actions.handPlayabilityKnown == true` 且该 uid **不在** `actions.playableUids` 中 ⇒ 拒绝发送并说明原因（有 `blockedReasons` 就引用，否则「当前不可打出（费用或相位限制）」）；`handPlayabilityKnown == false` ⇒ **不预检**，直接发请求交服务端裁决（避免用过期快照拦掉合法动作）。〔事实：当前投影恒发 `true`（`McpService.Projection.cs:359`），此分支是防御性保留。〕
- **D7.4 补全与 `hand` 展示用**手牌全集**，不用 `playableUids` 过滤**：不可打出的卡照常列出，但标注原因并排在后面（玩家要看到"我有什么、为什么不能用"）。
- **D7.5 不预判"会不会进选择"**：一律以响应为准——`status=targeting` ⇒ 进选择模式；`status=completed` ⇒ 直接渲染结果。`--plain` 的自动化脚本不能假设固定步数，必须读输出再决定下一条（S13 给 `hand` → 判断 → `play` 的示例）。
- **D7.6 `play` 的第二参数语义**：单位卡＝插入位序号（D1）；指令/反制卡上候选来自服务端的 target 请求，因此 `play 4 2` 解释为"打出第 4 张，并预选候选 2"；若最终没有选择器，该额外参数**被忽略并给一条警告**（不报错）。
- **D7.7 序号统一 1 起**，越界信息必须回显「可用序号 1..N」（不让玩家去数手牌）。
- **D7.8 `help` 只列一个 `play/出牌` 条目**：说明写「打出第 N 张手牌：单位会让你选插入位置，指令可能直接结算也可能选目标，反制直接生效；不可打出的卡会先被拒绝并说明原因」。
- **D8 战斗日志：先丢噪音，再翻译，只在本段内折叠**（R8「以原始 `kind` 降级」作废，改为**打印原始事件名**）。
- **D8.1 过滤**：`kind=attach` 丢弃；`level=debug` 丢弃；`kind=log` 且 `level=info/warning` **原样透传** `message`（handler 已写中文，前缀 `· `）；`level=error` 保留并标注 `⚠ <message>`（`keywords` 带 `exception:` 则附类型名）；`kind=update` 且载荷已脱敏（`{$hidden:true}`）⇒ 固定文案 `对手：一次不可见的变化`（只陈述"发生了但你看不到"，不猜内容）；其余 `kind=update` 走模板，未命中则降级。
- **D8.2 折叠只在同一段内**（段＝一次动作的产物，不跨段、不打散段边界），其余一律不合并：① 同段内"对同一目标 T 的伤害" ＋ "T 的死亡" ⇒ 合成 `X 受到 N 点伤害并被摧毁`；② 同段内"攻击主句" ＋ 上述伤害/死亡 ⇒ 合成 `A 攻击 T → 造成 N 点伤害（目标被摧毁）`。只做"主句 + 固定后缀"的模板拼接，不做自由文本生成（保证重放文本一致）。
- **D8.3 `card.stat.changed` 不单独成行**：仅当同段内没有任何更强事件时，压成一行 `X 的属性发生变化`。
- **D8.4 未覆盖事件降级格式**：`[<原始事件名>] <载荷摘要>`，摘要截断 120 字符（保证不丢失、不污染主叙事）。
- **D8.5 回看与容量**：`deque(maxlen=200)`；`log N`（默认 20）只从 deque 取、不请求服务端；`--plain` 每命令块只附"自上次输出以来的新行"，不重复整段历史。
- **D8.6 行动者取名纪律**：`source` 恒为 `"bus"`，**不得**当成"谁做的"；行动者与对象必须从载荷/模板参数取，取不到就写"某单位/某玩家"，**不要**猜成"你"。
- **D8.7 可追溯**：`--plain --verbose` 时在行尾附 `(seq=N)`，默认不显示。
- **D8.8 空段不占位**：一个段若只有被丢弃的条目（attach/debug），不产生任何日志行，也不打"（无事件）"。
- **D8.9 覆盖清单的键名已由 D9 校正**：上一版答复里的 `unit.attacked`／`unit.moved`／`unit.destroyed`／`turn.started`／`turn.ended`／`point.spent`／`command.attack` **在引擎里都不存在**（真实名见 F23）；"攻击/移动/摧毁/回合/费用"这五类语义分别落到 `unit.damage.dealt`／`unit.position.changed`／`card.died`／`turn.start|end`／`point.gained|lost` 上，模板表以 **D9.1** 为准。
- **D9 模板表一律键 `GameUpdates` 的真实名**（D8.9 已裁决；表见下）。
- **D9.1 模板表**：`card.played` → `打出 <卡名>`（可被后续事件升级）；`unit.deployed` → `部署 <单位名> 到<线>`（线名取载荷位置，取不到就"战场"）；`unit.damage.dealt` → `<施动> 对 <受方> 造成 N 点伤害`（施动为空则省略主语）；`unit.acted` → **不单独成行**（仅作升级条件）；`unit.position.changed` → `<单位> 移动到 <槽>`（同段有 `unit.deployed`/`card.played` 时**全部抑制**）；`card.died`/`card.dying` → `<卡名> 被摧毁`（与同目标伤害合并）；`card.leaving` → `<卡名> 离场`；`card.drawn` → `抽了 1 张牌`；`point.gained`/`point.lost` → `指挥点 +N`/`指挥点 -N`（同类合并成一行）；`point.changed` → `指挥点 变为 N`（与上两者同段时抑制）；`turn.start`/`turn.end` → `—— 第 N 回合开始 ——`/`回合结束`（单独成行、空行包围）；`card.stat.changed` → 不单独成行（仅段内无更强事件时压成 `X 的属性发生变化`）。**表中不得出现** `unit.attacked`/`command.attack`/`unit.moved`/`unit.destroyed`/`turn.started`/`point.spent`。
- **D9.2 攻击 vs 移动＝叙述层共现判定（只有一条规则）**：同段有 `unit.acted{Unit=A}` **且**有以 A 为施动的 `unit.damage.dealt` ⇒ 把该 damage 行升级为 `<A> 攻击 <B>，造成 N 点伤害`；同段有 `unit.acted{Unit=A}` 但**无** A 的 damage ⇒ A 视为"移动/行动"（有 `unit.position.changed` 就用移动模板，否则 `<A> 行动`）。只在拼文本时使用，不参与裁决、不影响发送内容——实现时写成一行 `if` 并注释「仅用于措辞，不影响状态」，**不得**为此引入第二套事件模型。
- **D9.3 追加两条折叠**：③ 同段存在 `unit.deployed` 或 `card.played` 时抑制该段内**全部** `unit.position.changed`（整线重排是布局副作用，不是玩家可见的移动）；④ 同段多条 `point.gained`/`point.lost` 合并成一行净额 `指挥点 +a-b`，并抑制同段 `point.changed`。
- **D9.4 三处确定输出**：① 互攻未死（两条 damage + 两个 `unit.acted`）⇒ 两行、主语不同、不合并（`你：A 攻击 B，造成 2 点伤害` / `对手：B 反击 A，造成 1 点伤害`）；② 部署整线重排 ⇒ 一行 `你：打出 A 并部署到支援线`，位置变化全抑制；③ 效果伤害（`unit.damage.dealt{Unit=null}`、无 `unit.acted`）⇒ 一行 `你：对 B 造成 3 点伤害`，**没有"攻击"字样**。
- **D9.5 `--verbose` 追溯粒度**：默认折叠行尾附 `(seq=N)`（N＝段 `sequence`）；`--verbose` 时为 `(seq=N.k1,k2)`（k＝段内条目下标，折叠涉及的条目全部列出）。
- **D9.6 两条取值纪律**：`card.damaged` 与 `unit.damage.dealt` 同段同目标时只保留后者（信息更少的前者丢弃），仅当后者缺失才回落 `<卡名> 受到 N 点伤害`；日志行按段追加，**不得**跨段合并（否则 `(seq=N)` 失真）。
- **D10 我方在场单位按「当前帧的面板顺序」编号（1 起），作为 `attack`/`move`/`leave` 的第一个参数**——它是"屏幕上的编号"，不是跨帧稳定 ID。
- **D10.1 编号定义（唯一来源）**：`view.selfLine` 中 `ownerSide=="self"` 的项，按「zone：支援线在前、前线在后」→ `slotIndex` 升序排列，编号＝下标+1。**HQ 不参与编号**（我方 HQ 不作自身动作对象；攻敌 HQ 走关键字 `hq`）。**敌方单位不编号**（目标一律走服务端候选，D1.1）。
- **D10.2 面板闭环**：编号必须画在**与单位同一行、同一位置**（如 `1. 步兵第 3 团 3/3 ⚔`），不做两栏对齐；否则窗口滚动后编号与单位脱节。
- **D10.3 可行动性用标记表达、不改编号**：可攻击 `⚔`、可移动 `→`、两者都有 `⚔→`；不可行动则编号保留、行尾给原因（如 `（指挥点不足）`），且**仍可被 `leave` 选中**。不用「可行动集合」编号——那会随费用/门禁无视觉线索地跳动。
- **D10.4 参数语法（实体编号 + 候选序号）**：`attack 2`（进选择模式选目标）／`attack 2 1`（内联目标候选 1）／`move 2`／`move 2 1`／`leave 2`（无候选、直接执行）。第一参数永远是"我方面板编号"，第二参数永远是"服务端候选序号"。`command.begin` **不作为玩家指令暴露**（语义模糊），`help` 只出现 `attack`/`move`/`leave`。
- **D10.5 两套数字同现时的措辞**：提示条 `攻击 2 → 选择目标`；选择模式标题 `选择目标（来源：我方 2 号 <名>）`。
- **D10.6 `move` 的候选**：按视图 `slotIndex` 附注「前线第 X 号槽」，但**输入仍是候选序号**（重申 D1.6）。
- **D10.7 编号漂移与越界**：编号每帧重算（部署/移动/重排后变化是预期行为）；执行前必须用**当前 Session 的最新 `selfLine`** 把编号解析成 uid（不是用敲键那一刻的旧帧）；越界 ⇒ 拒绝并提示 `战场已刷新：我方单位共 N 个（可用 1..N），请重新选择`。
- **D10.8 执行前回显**：把解析结果回显一次 `攻击：2 号 <名> → 敌方 <名>`；不做二次确认弹窗（回显是唯一防误指手段）。
- **D10.9 只读指令 `units`/`战场`**：**不产生任何网络请求**，纯从 Session 快照渲染（`我方单位（3）`＋每行 `序号 名称 位置 攻/血 标记/原因`）；与 `hand` 并列进 `help`；TUI 下只把同样内容打进日志区、不改布局。
- **D11 超时与错误码：一张表、集中定义、按码分支**（禁止靠 `message` 判断；超时值集中在 `timeouts.py` 常量，S13 可调小）。
- **D11.1 超时表**：TCP 连接 5s（报"连不上 <host:port>"，走 R5/D4 指引）；`initialize` 5s；读类调用（`tools/list`/`observe.view`/`room.info`）10s + 重试 1 次（再失败仅提示、不改动作状态）；**动作类调用 30s**（`play.*`/`command.*`/`end`/`mulligan.*`/`target.*`）⇒ 标记「结果未知」，**不重发、不自动取消**；等 `notify/target.request` 兜底 5s；选择模式等玩家 **12 分钟**（服务端 10 分钟按取消处理，客户端 12 分钟只是兜底）；心跳＝空闲 20s 发 `ping`、两次无响应（>10s）判断线 → R11 重连（带 `seatToken`）。心跳必须做：服务端 45s 无入站帧即判死，而玩家选靶时可能静坐数分钟。
- **D11.2 `call` 超时 ≠ 动作取消**：服务端收下动作后客户端没有任何撤销手段（`target.cancel` 只对 pending 有效），超时后只能"标记未知 + 对齐"，不能假装没发生。
- **D11.3 错误码处置矩阵**（按码分支）：`ok` 正常；`not-admitted` 停止当前动作并进入重连/重新申请（D3）；`seat-taken` 停当前动作、走 D3 的"新开/换实例"分支；`stale-room` 刷新界面并走 R11 轮换；`stale-seat` 视同 `invalid-token`；`room-rotating` **自动重试**（最多 3 次、按 `retryAfterMs` 缺省 200ms、总预算 2s，期间不打扰，最终失败才提示）；`already-ended` **视为成功**（幂等）并拉一次视图显示终局；`invalid-token` 走 D3.1 2b/2c；`unauthorized` 停止且不改状态（管理类结果额外说明权限来源）；`pending-targeting` **不报错**——从响应 `details.pending`（或等推送）切到选择模式并提示"请先完成当前选择"；`deck-invalid` 停在开局前并展示 message 与违规项（提示用 `--deck`）；`busy` 2s 后**自动重试一次**，再忙才提示；`lock-conflict`/`missing-assets` 原样展示服务端 message 并停止本次开局；`instance-boot-failed` 展示原因与实例日志路径且**不 kill 管理器**；未知码原样打印 `code`+`message` 并写日志，**不猜、不重试**。
- **D11.4 唯二不能当错误处理的码**：`pending-targeting`（正常状态转移）与 `already-ended`（幂等成功）——把它们做成弹错是最容易让人误判对局的坑。
- **D11.5 「结果未知」是必须实现的新界面态**：动作调用超时（或等待中连接异常）后 ① 动作队列**暂停**（不叠新动作）；② 提示条显示 `⟳ 结果未知：等待服务端刷新`；③ 等下一次 `notify/view`，若 `turn`/`phase`/`selfKredits`/`selfHand` 任一与动作前快照不同 ⇒ 判定已生效、清 unknown、恢复输入；④ 3s 内无帧 ⇒ 主动 `observe.view`（读类 10s）对齐一次；仍无变化 ⇒ 保持 unknown 并提示 `服务端无响应，可重试或退出`（给 `retry`/`quit` 两个选择，**不自动重发**）。
- **D11.6 「已发出/未发出」必须分开**：连接层失败（连不上/写失败）＝**未发出**，可直接重试；等待超时＝**已发出**，只能对齐、不能重试。
- **D11.7 三处确定行为**：① `end` 因 AI 连打效果链 25s 才返回 ⇒ 未到 30s 继续等，提示条 `等待服务端…（25s）`，输入行可编辑但提交会排队（提示"上一个动作进行中"）；② `play 3` 的 pre 已进 targeting 但客户端在 pending 到达前超时 ⇒ 标 unknown；若随后 `notify/target.request` 到达则**清 unknown 并进入选择模式**（pending 是状态事实，优先于超时判定）；5s 内既无响应也无 pending ⇒ 提示"未收到选择请求"并 `observe.view` 对齐；③ `end` 撞轮换窗口返回 `room-rotating{retryAfterMs:200}` ⇒ 静默重试 200→400→800（最多 3 次、总预算 2s），第 2 次成功则用户无感；3 次都失败才提示"房间即将轮换，请稍后"，随后 `notify/room.rotated` 到达时按 R11 走终局流程。
- **D11.8 重试白名单**：只允许 `room-rotating` 与 `busy` 两处自动重试，其它任何码都不得重试——重试一个非幂等动作（出牌）会产生两个动作，文字界面里极难发现。
- **D11.9 文案中英一致**：`help` 里能查到的词与提示里出现的词必须相同；所有超时/错误文案都要两种语言一致。
- **D12 终局＝结果横幅 ＋ 保留的最终战场，不清屏**（玩家要能回看最后一步）。
- **D12.1 终局取值（全部来自最后一帧 `view`）**：主标题用 `resultTitle`（有就直接用、不自己造词）；兜底用 `outcome`（`Win`→胜利、`Lose`→失败、`None`+`aborted`→"对局终止（无胜者）"）；副标题用 `resultReason`（`hq-zero`→"总部被摧毁"、`concede`→"因认输"、`aborted`→"对局被管理员终止"）；`concedingSide` 与 `concede` 组合出"你认输/对方认输"；附注 `finalTurn` → `第 N 回合`。一行式：`—— 失败 · 因认输 · 第 12 回合 ——`。`outcome=None` 时**不写胜利/失败**，只写"对局终止"；若 `resultReason` 也缺失就只显示"对局终止"，**不补"平局"**（引擎没这么说）。
- **D12.2 日志按局分段（这是「清除旧对局内容」的边界）**：`Session.logs` 由单 deque 改为 `{roomId: deque(200)}`，当前局＋上一局各留一份、更早丢弃。轮换时清空战场面板、选择器、pending、unknown，日志切到新 roomId 的新 deque。`log N` 默认当前局；`log --prev` 取上一局；`log --all` 交替输出并带 `[上一局]` 前缀。—— 清的是一屏状态，留的是叙事历史（与 R8 不冲突）。
- **D12.3 `restart` 的动作集**：① 必须已收到 `notify/room.rotated`（否则提示"等待房间重建…"，指令不排队）；② `session.attach{roles:[controller,viewer],seat:0,deck:<记录>,packages:<记录>}`（**不带 token**）；③ `manager.admin conn.admit`；④ `manager.admin match.start{packDirectories:<记录>}`。**永不 spawn 新实例**（轮换后实例仍是 lobby，D3.5②）。
- **D12.4 `restart` 的三条失败分支（不扩）**：`deck-invalid` ⇒ 按 D5 用**当前卡包**重新生成 39 张、写回 `session.json`、重试**一次**，成功则日志提示"卡组已按新卡池重新生成"，再失败则提示换 `--pack` 并回菜单（不退出进程）；`missing-assets` ⇒ **不重生成**（不是卡组问题），展示缺失清单、提示换包、回菜单；`room-rotating` ⇒ 按 D11 白名单重试（200/400/800ms、最多 3 次）。
- **D12.5 竞态（顺序不定但结果必须一致）**：终局帧先到 ⇒ 立即显示结果横幅、退出选择模式、保留最终战场，此时 `restart` **不可用**（提示"等待房间重建…"）；`room.rotated` 先到 ⇒ 清面板/选择器并进入终局菜单，若随后终局帧到达仍**补显示**结果（菜单不挡结果）；收到 `room.rotated` 后 `restart` 才可用。
- **D12.6 终局时的收尾**：正在选择模式 ⇒ **直接清 pending、不调 `target.cancel`**（服务端已 `FailAll("room-rotated")`，连接已被 revoke，调用只会换来 `unauthorized`）；处于「结果未知」态 ⇒ 直接清 unknown（终局是确定性终态，不需再对齐）。
- **D12.7 终局菜单门禁**：只放行 `restart`/`重开`、`quit`/`退出`、`log`、`help`；其余本地拒绝并提示 `对局已结束：restart 重开 / quit 退出`。不做"输入任意键继续"这类隐式行为。
- **D12.8 纪律**：终局后**不得**自动调用 `match.start` 或任何"帮你再来一局"的动作，重开必须玩家显式输入；`restart` 期间界面显示 `正在重开…` 并锁住动作队列，失败要**回菜单**而不是回"结果未知"；新一局必须把新 `roomId` 与新令牌写回 `session.json`，旧 roomId 的日志**只留内存**（进程退出即丢，不写盘以免无限增长）。
- **D13 布局＝包 `tools/orc/` ＋ 薄入口 `tools/orc_play.py` ＋（可选）自举脚本 `orc_play.bat`/`.sh`**。核心与 plain **零第三方依赖**：只有 `front_tui.py` import `prompt_toolkit`。
- **D13.1 文件清单（执行时按此建）**：`tools/orc/__init__.py`、`__main__.py`（`python -m orc` 亦可）、`cli.py`（参数解析＋模式选择＋能力探测）、`transport.py`（Dispatcher，D2）、`session.py`（Session＋按 roomId 分段日志）、`commands.py`（CommandEngine：别名/门禁/引导）、`render.py`（Renderer 纯函数＋中文宽度）、`manager.py`（探活/spawn/admit/start＋`session.json`）、`timeouts.py`（全部超时常量，D11）、`front_tui.py`、`front_plain.py`、`requirements.txt`、`tools/orc/tests/`（`unittest`，不引 pytest）、薄入口 `tools/orc_play.py`、自举 `tools/orc_play.bat`（＋可选 `.sh`）。
- **D13.2 能力探测（顺序固定）**：① stdin/stdout 非 TTY ⇒ plain（不尝试 TUI）；② 显式 `--plain` ⇒ plain；③ `import prompt_toolkit` 失败 ⇒ plain ＋**一条**提示 `未检测到 prompt_toolkit，已使用 --plain 界面；如需全屏界面请运行 tools\orc_play.bat`；④ 否则 TUI。`--tui` 可强制 TUI（导入失败则报错而非降级）。**绝不因缺 TUI 库拒绝启动**。
- **D13.3 venv 自举只发生在 bat/sh 且失败不阻塞**：① `tools/orc/.venv/Scripts/python.exe` 不存在 ⇒ 用 `py -3.13`（找不到则 `python`）建 venv；② `pip install -r tools/orc/requirements.txt`（已装则跳过，`--disable-pip-version-check` 静音）；③ 用该 venv 解释器运行 `tools/orc_play.py`；④ 任一步失败 ⇒ **不退出**，回退用当前解释器跑 plain 并打印失败原因与「全屏界面不可用」。venv 落 `tools/orc/.venv/` 并进 `.gitignore`；bat **不带 `pause`**（要被反复调用）。直接 `python tools/orc_play.py` 也能跑（走能力探测）。
- **D13.4 版本策略**：语法基线 **3.9**（不用 `match`、不用 `ExceptionGroup`），保证老解释器也能跑 plain；venv 优先 `py -3.13` → 退 `python`；不满足 3.13 不报错，只在日志记一行实际版本。R1 的 Constraint 按「隔离 venv（优先 3.13，缺省用当前解释器）」执行（需求侧同步放宽）。
- **D13.5 依赖与仓库改动边界**：`requirements.txt` 只写 `prompt_toolkit>=3.0` 一条（**不加** `rich`/`wcwidth`——宽度自己按 East Asian Width 算）；`.gitignore` 追加 `tools/orc/.venv/` 与 `__pycache__/`；**不改** sln/csproj。测试只用 `unittest` ＋ `FakeDispatcher`；TUI 层不写自动测试（人工冒烟）。
- **D13.6 入口双形态**：`tools/orc_play.py` 同时支持"直接执行"与 `python -m orc`（`if __name__ == "__main__":` ＋ 相对导入保护）。
- **D13.7 两个边界确定行为**：① 只有 3.11 无外网 ⇒ `python tools/orc_play.py play` 走 plain 正常开局并提示一次；`orc_play.bat` 建 venv 成功但 pip 失败 ⇒ 回退 plain 并打印原因；两者都能打完一局。② `printf 'keep\nquit\n' | python tools/orc_play.py play --plain` ⇒ stdin 非 TTY 已判 plain、不 import TUI 模块、正常输出与退出码（S13 基线用例）。
- **D14 指令表是唯一真源**（S5 的别名表按此写；共 16 条）：
  `play`/`出牌` `play <手牌#> [候选#]`（play 相位）；`attack`/`攻击` `attack <单位#> [候选#]`（play）；`move`/`移动` `move <单位#> [候选#]`（play）；`leave`/`离开` `leave <单位#>`（play）；`end`/**`结束回合`** `end`（play）；`concede`/`认输` `concede`＋**二次确认**（play/mulligan）；`keep`/`保留` `keep`（mulligan）；`mulligan`/`换牌` `mulligan`（mulligan）；`ok`/`确定`（选择模式）；`x`/`取消`（选择模式）；`hand`/`手牌`、`units`/`单位`、`log`/`日志` `log [N] [--prev|--all]`、`help`·`?`/`帮助` `help [指令]`（任意）；`restart`/`重开`（终局菜单）；`quit`/`退出` ＋**对局中二次确认**（任意）。
- **D14.1 不出现的动词**：`order`/`counter`/`join`/`begin`/`command`——出牌统一为 `play`（按 `cardType` 分派，D7），避免三位一体的近义动词。
- **D14.2 中文别名全表覆盖、不设例外**（含只读类与选择模式：`ok`→`确定`、`x`→`取消`），否则 `help` 里"每条都有中英两套"不成立。
- **D14.3 `end` 的中文是「结束回合」、不是「结束」**（后者太容易被读成"退出"）。
- **D14.4 二次确认**：`quit`（对局中）与 `concede`（对局中）都要再敲一次**同词**（`quit` 或 `退出` 任一，但必须真的再输入一次）；任何其它输入＝取消并显示"已取消"。`--keep-instance` 时确认文案改为"实例将保留，可用 play 回座"。**换牌相位与终局菜单下的 `quit` 不确认**（没有可毁的对局）。确认提示示例：`再输入一次 quit 确认退出（当前对局将结束，实例将被销毁）`。
- **D14.5 一词多义的相位矩阵**：play 相位 `结束回合`→`match.end_turn`、`退出`→确认后退出；mulligan 相位 `结束回合`→**拒绝**「换牌阶段不能结束回合；请输入 keep 开始」、`退出`→直接退出；终局菜单 `结束回合`→**拒绝**「对局已结束；restart 重开 / quit 退出」、`退出`→直接退出。拒绝时**必须顺带给出正确动词**（把误操作变成教学）。
- **D14.6 `help` 输出形态**：四组（`对局动作`/`选择`/`只读`/`系统`）、一条一行、固定四列（英文/中文/语法/说明），中文列按**显示宽度**对齐；`help <指令>` 单条查询，接受中英任一词（`help 出牌` ≡ `help play`）。
- **D14.7 R12 验收的落点**：`--help`（usage）与 `help` 全表**都不得**出现 `opponent`/`token`/`connect` 之类与对手相关的选项；允许出现的选项全集＝`--manager`、`--pack`、`--deck`、`--keep-instance`、`--launch-manager`、`--plain`、`--tui`、`--session-file`、`--new`、`--instance`。
- **D14.8 二次确认是实现成"一次输入状态"**（挂在输入行提示上），不是模态弹窗；`--plain` 下同样读下一行比对，脚本因此能断言"确认前不退出"。
- **D14.9 匹配细则**：别名匹配大小写不敏感（`Play`/`PLAY` 均可）；中文别名只收简体；`log --prev`/`--all` 是 `log` 的旗标、**不**各自造动词；`?` 是 `help` 的符号别名、不计入"中英两套"（`help` 里注明即可）。
- **D14.10 两处边界确定行为**：① 未开 `--keep-instance` 时误敲 `退出` ⇒ 进入二次确认（**不立即退出**）并顺带提示"若想结束回合请输入 end / 结束回合"；敲别的即取消、对局继续。② 换牌相位敲 `结束` ⇒ 本地拒绝并回「`结束回合` 仅在对局进行中可用；退出请用 quit / 退出」，不发任何请求、相位不变。
- **D15 观战＝单一中性战场 ＋ 两侧手牌，不并排两份视角帧**。
- **D15.1 布局（自上而下）**：标题（`对局 room:<id> · 第 N 回合 · 轮到 B`）／B 区（HQ＋指挥点＋手牌数＋支援线）／**共享前线（只画一次）**／A 区（同上，含手牌明细）／日志／输入行。标题里**不出现"我/我方/敌方"**，一律 `A（seat 0）`/`B（seat 1）`——姿态中性是观战模式的核心；轮到谁用 `activePlayerSide` 折算成 A/B。
- **D15.2 数据合成**：`views[0].view.selfLine` 中 `zone=support` 的项＝A 支援线，`views[1]` 同理；前线取**任一侧**的前线投影（两侧 `slotIndex` 是同一物理战线），按 `slotIndex` **去重**后只画一次；两侧手牌明细都展开（全知的意义即此）。
- **D15.3 以 `selfLine` 为准、丢弃 `enemyLine`**（两份视图里对方的 `selfLine` 与自己的 `enemyLine` 是同一批实体，否则支援线/前线会重复）。
- **D15.4 `hand`/`units` 增加可选座位参数**（覆盖 D14 表中这两条的语法）：`hand [0|1]` / `units [0|1]`；**play 缺省＝自己（seat 0）**，**watch 缺省＝双方都列**。watch 下清单与面板一律用 `A1…An`/`B1…Bn` 前缀编号（play 下仍是纯数字，D10.1）。
- **D15.5 watch 白名单**：放行 `hand`/`units`/`log`/`help`/`quit`；其余一切（`play`/`attack`/`move`/`leave`/`end`/`concede`/`keep`/`mulligan`/`restart`/`ok`/`x`）**本地拒绝、不发请求**，提示 `观战模式只读：可查看 hand / units / log，输入 quit 退出`。
- **D15.6 watch 下 `quit` 不二次确认**（没有可毁的对局）。
- **D15.7 单侧数据退化**：只拿到一份 view（异常帧）⇒ 单侧渲染并在标题标 `（单侧数据）`，**不**拼出半个战场还装作完整。
- **D15.8 `--plain` 观战用同一套中性布局**，输出纯文本块（可重定向），标题同样不带"我"。
- **D15.9 三条硬约束各有验收**：watch 不写 `session.json`、不 kill 实例、不影响对局。
- **D16 Tab 补全是 TUI 专属；plain／管道用「引导打印候选」等价满足**（R6 那条验收按此拆成两句，需求侧同步改）。
- **D16.1 两形态的目的相同**：TUI＝输入前缀后按 Tab 出候选（条目含中文卡名与关键数值）；plain／管道＝参数缺失时**打印候选表并等下一行输入**（即 D1.4 的引导），信息量与 TUI 一致。管道里 Tab 只是字符，不能把终端能力当协议能力。
- **D16.2 候选来源与粒度**：第 1 个词（动词）＝中英别名前缀匹配，条目 `play / 出牌`；`play` 第 1 参＝手牌**全集**（可打出的在前），条目 `3. 步兵第 3 团  1费 3/3`，不可打出附 `（指挥点不足）`；`attack`/`move`/`leave` 第 1 参＝我方面板编号，条目 `2. 装甲第 2 连  2/5  ⚔`；**第二参数不预取**（此时尚未发 pre、服务端没有候选），只显示占位提示 `[候选#] 留空进入选择`；`log`＝旗标 `--prev`/`--all`（`--prev 上一局`）；`hand`/`units`＝`0`/`1`（仅 watch，`0 座位 A`/`1 座位 B`）。
- **D16.3 第二参数不做补全**：`attack 2 1` 的内联候选必须来自一次真实的服务端候选，凭空给建议等于编造候选。
- **D16.4 Tab 只填参、回车才执行**：唯一候选时也只补全文本、不自动执行（`play 3<Tab>` 直接出牌的误触成本太高且不可撤销）；补全结果是"可编辑的完整命令"。
- **D16.5 引导与补全共用 `candidates_for(context)`**：一个函数同时服务 TUI 补全、plain 引导打印、`hand`/`units` 清单，三者条目格式一致（S13 断言"引导打印的候选 == `hand` 输出的前 N 条"）。
- **D16.6 plain 引导必须打印候选**，不能静默等待（否则 `printf 'play\n' | …` 看起来像卡死）。
- **D16.7 管道 EOF 且有 pending** ⇒ 先 `target.cancel` 清理，再以**非零**退出码结束（不把 10 分钟的悬挂选择器留给服务端）。
- **D16.8 显示上限**：TUI 补全候选最多 12 条，其余折叠为 `… 还有 N 条（输入 hand 查看全部）`；plain 引导**全量打印**（它是可重定向文本）。

- **D17 父 agent 复核后的微调（执行完成、跑通端到端后发现，已落地）**：
- **D17.1 手牌编号＝位置，不重排**：覆盖 D7.4 的"不可打出的排后面"——重排会让显示次序变成 `2 3 4 1`，与"序号＝手牌位置"直接冲突。改为**固定按位置 1..N 排列**，不可打出的只在行尾标注原因。
- **D17.2 叙述层降噪**：装配类 `log`（以 `已挂载触发器`/`已装载`/`已装配`/`已注入`/`已注册` 开头，或含 `放置初始化完成`）与装配类 `update`（`card.load`/`card.created`/`card.initialized`/`card.placed`/`deck.shuffled`/`effect.loaded`/`trigger.attached`）默认**不显示**，`--verbose` 可见。这是 D8.1「info 原样透传」的例外：开局曾一次性刷出十余行装配日志，直接破坏文字游戏观感。未识别事件（其他名字）仍按 D8.4 降级显示，"不丢失"依然成立。
- **D17.3 脱敏行去计数**：同一段内 `对手：一次不可见的变化` 只保留一条且**不带 `×N`**（计数本身泄漏对手动作量，且曾出现 `×39`）。
- **D17.4 plain 块内不重复日志**：块内日志区置空（`log_lines=None`），新日志由 `new_log_lines()` 单独输出——落实 D8.5「只附自上次以来的新行」，此前同一行会随每次重绘重复出现。
- **D17.5 数量文案**：`min..max` 一律经 `need_text()` 渲染为「需选 1 个」/「需选 1..2 个」；`选 1..1` 会被误读成候选范围。选择区标题同时给出「共 N 个候选」。
- **D17.6 watch 的空态按座位**：`units` 在观战下不再输出"（双方无在场单位）"，由 `units 0|1` 的座位标题承担上下文。

## 已知风险与本期不做（准出时对照）

- **已发动作不可撤销**（D11.2）：动作超时后只能"标记未知＋对齐"，客户端没有任何撤销手段；`target.cancel` 只对 pending 有效。
- **叙述层的"攻击/移动"是共现推断**（D9.2）：若某段同时含 `unit.acted{A}` 与 A 的 `unit.damage.dealt`（例如效果造成的伤害恰好同段），措辞会偏向"攻击"；这是措辞风险、不影响裁决，但重放文本会随之变化。
- **未覆盖的更新名一律降级**（D8.4）：新加引擎事件不会自动获得中文叙述，会显示 `[<事件名>] 摘要`。
- **卡池重装后的卡组兼容**（D12.4）：`deck-invalid` 只"重生成 39 张＋重试一次"，仍失败只提示换 `--pack`，不自动切包。
- **服务端 pending 超时 10 分钟 vs 客户端 12 分钟兜底**：两者之间若服务端先按取消收束，客户端会先看到 `target.expired`；若客户端先到点，则按 D11.5 对齐。
- **实例认领依赖 `session.json`**（D3.3）：记录被误删/换路径（`--session-file`）后不会自动找回，会走"新开"。
- **观战合成的隐含依赖**（D15.2/D15.3）：依赖全知帧里两份 `selfLine` 各自完整；只拿到一份时按 D15.7 退化标注。
- **TUI 缺库即降级**（D13.2）：无 `prompt_toolkit` 时不提供全屏界面，只有 plain。
- **需求侧待同步的措辞**（由父 agent 改 `requirements.md`）：R6 的「支援线槽 2」→「插入位」、R6 的「参数缺失进入引导」→「需要额外选择时进入引导」＋ Tab/plain 两句拆分、R8 的「以原始 `kind` 降级」→「以原始事件名降级」、R9 的「主卡种」→「全部卡种」、R1 的 Python 3.13 硬约束→「优先 3.13、缺省用当前解释器」。

## Implementation Steps

### S1: 传输层与 Dispatcher（读线程 / 响应 Futures / pending 条件变量 / 推送队列）
- Status: Todo
- Target: `tools/orc/transport.py`（帧编解码 ＋ `IDispatcher` 实现）；超时常量在 `tools/orc/timeouts.py`
- Approach: 按 D2.1/D2.2：单条 socket 读线程切帧并分流；`call()` 阻塞在 `_responses[id]`（`id` 全用字符串）；`notify/target.request` 按 `sessionId` 合并去重后写 `_pending` 并唤醒 `wait_pending()`；其余推送入 `push_queue` 并回调 `on_frame`；写侧 `send()` 持一把输出锁。**超时与错误按 D11**：超时值集中定义（`timeouts.py`），读类 10s＋重试 1 次、动作类 30s 后转「结果未知」态（D11.5），**只对 `room-rotating`/`busy` 自动重试**（D11.8），心跳空闲 20s 发 `ping`（D11.1）。
- Acceptance:
  - [ ] 推送穿插在响应之前时响应仍能正确配对（F1）。
  - [ ] 同一 `sessionId` 的 pending 被重推多次时只保留一份（F14、D2.2）。
  - [ ] 断线、半帧、服务端 `error` 帧都转为可读异常，不静默丢帧。
  - [ ] 所有 `tools/call` 都由动作 worker 串行发出，UI 线程不直接写 socket（D2.1）。
  - [ ] 超时值与重试白名单集中在一处，且行为符合 D11.1/D11.8。
  - [ ] 动作超时后进入「结果未知」态并暂停动作队列（D11.5）。
  - [ ] 心跳按 D11.1 实现，断线触发 R11 重连。
- Rationale: 读线程独占 `recv` 才能在任意时刻接住推送；动作串行化与服务端单写者模型同构，天然避免两个动作把 pending 弄乱（D2.1/D2.2）。超时集中定义＋白名单重试，才能保证"不重发非幂等动作"这条硬约束（D11.2/D11.8）。

### S2: 管理器面（探活 / 拉起 / spawn / admin / kill）
- Status: Todo（D4 已裁决，可执行）
- Target: `tools/orc/manager.py`（连接、ping 轮询、preflight、detached 拉起、日志截断、`session.json` 读写）
- Approach: 按 D4.1–D4.9：统一顺序「ping → 复用／（`--launch-manager` 则拉起等 ping，否则报错退出）」；拉起＝detached `dotnet <repo>/artifacts/slot/bin/Orc.Manager/debug/Orc.Manager.dll serve --port <port>`（`cwd`＝仓库根、stdout/stderr → `.orc-manager/logs/manager-<port>.log`），就绪只认 `manager.ping`（200ms/次，上限 20s）；preflight 仅三项（dotnet／Manager.dll／Server.dll 同目录）；**绝不 kill 管理器**。`spawn` 携带 `instanceId:"cli-<uuid8>"`、`label:"cli:<uuid8>"`，并显式传 `mode:"play"`、`deckRules:"none"`（管理器缺省是 strict）。
- Acceptance:
  - [ ] `manager.spawn` 的 `mode` 恒为 `play`，`deckRules` 恒为 `none`（R4/R9）。
  - [ ] `session.attach` 的 `roles` 只含 `controller` 与 `viewer`（R4）。
  - [ ] 管理器未运行且未带 `--launch-manager` 时，报错含启动指引、不静默失败（R5、D4.5）。
  - [ ] `spawn` 的 `label` 与 `instanceId` 同源可读，`manager.list` 里能据此认出本会话的实例（D3.2/D3.3）。
  - [ ] `--launch-manager` 拉起的常驻管理器在 CLI 退出后仍存活，且 CLI 从不 kill 管理器（R5、D4.2/D4.4）。
  - [ ] 已有一个管理器在跑时 `--launch-manager` 只复用、不拉第二个、不报错（D4.5）。
  - [ ] preflight 三缺一时不启动管理器并给出具体修复命令（D4.6）。
  - [ ] `--manager` 写入记录与连接用的是同一个 `host:port` 字符串（D4.8）。
- Rationale: 就绪只认 `ping`、preflight 只查致命项、管理器永不 kill——三者合起来把"启动失败"暴露在最早、最可解释的那一步（D4.3/D4.6/D4.7）。

### S3: 新开实例路径（spawn → attach → admit → match.start）
- Status: Todo
- Target: `tools/orc/cli.py`（编排入口，与 S9 共用同一状态机）
- Approach: 只承担 D3.1 第 `3` 步（新开）：`spawn`（`seats:[{kind:"agent"},{kind:"ai"}]`，人类＝seat0）→ `attach{roles:["controller","viewer"],seat:0,deck,packages}` → 等 `notify/admission` 记录 `seatToken` → `manager.admin{op:"conn.admit"}` → `match.start{packDirectories}`；任一步失败按 R5 清理（kill）或打印实例标识；成功后写 `session.json`（D3.4）。回座与同实例重开由 S9 负责，本步骤不重复实现。
- Acceptance:
  - [ ] 成功路径无需用户手敲管理命令（R5）。
  - [ ] `seats[0].kind="agent"`、`seats[1].kind="ai"`，人类以 `seat:0` 申请并获批（R2）。
  - [ ] 开局后首回合行动方是人类（R2），`phase` 为 `mulligan` 或 `play`（R5）。
  - [ ] 中途失败不留半开实例，或明确告知实例标识（R5）。
  - [ ] 首次 `spawn` 失败时不 kill 管理器，原样呈现 `code/message` 并说明管理器仍在 :port 常驻（D4.7）。
- Rationale: 新开只是三条出口之一，且必须与"回座／同实例重开"在同一处判定（S9），否则三条路径各自 spawn 会立刻产生实例泄漏与重复 `session.json` 写入（D3.1/D3.5）。

### S4: Session（单写状态仓库）
- Status: Todo
- Target: `tools/orc/session.py`
- Approach: 按 D2.3：字段＝`view`/`actions`/`pending`/`log`/**`logs{roomId: deque(200)}`**/`phase`/`turn`/`kredits`/`outcome`/连接态/admitted/`roomId`+`epoch`/终局横幅/`unknown`。写者仅读线程与动作 worker；UI 取「快照拷贝」（`RLock` 只护拷贝）。帧类型按 `perspective.mode`/`seat` 判别（不做形状嗅探）。日志按局分段（D12.2）：当前局＋上一局，轮换时切换。
- Acceptance:
  - [ ] 绑座与全知两种帧都能喂进同一仓库，`--plain` 与 TUI 共用同一数据源。
  - [ ] `notify/target.expired` 会清空待决并留下可读提示（R7）。
  - [ ] 渲染期不持锁、不原地改动仓库（取快照）（D2.3）。
  - [ ] `logs` 按 roomId 分段，`log`/`log --prev`/`log --all` 行为符合 D12.2。
- Rationale: 单写者＋快照读，才能让"三条时间线并发"下没有读写竞态，也让 Renderer 保持纯函数可断言（D2.3/D2.6）。日志按局分段是唯一能同时满足 R8「可回看」与 R11「清除旧内容」的结构（D12.2）。

### S5: 指令集与门禁（中英别名 / 内联参数 / 引导 / Tab 补全）
- Status: Todo（D1、D2、D7、D10、D14、D16 已裁决，可执行）
- Target: `tools/orc/commands.py`（CommandEngine：别名表、门禁、引导、`candidates_for(context)`）
- Approach: 每条指令编译成「一到多步协议调用」的序列。**`play`/`出牌` 是单一动词，按 `cardType` 分派**到 `play.unit_pre` / `play.command_pre` / `play.counter`（D7/D7.1/D7.2），第二参数在单位上＝插入位序号（D1）、在指令/反制上＝"预选候选 N"（D7.6）。动作类指令统一为「前置调用 → 取 pending（优先用响应体里的 `details.pending`，D2.2）→ 按候选序号 `target.submit`」两段式；**是否进选择模式一律以响应 `status` 为准，不预判**（D7.5）。内联参数与引导走同一执行路径（D1/D1.1/F14）。号码一律是候选序号、1 起，越界回显「可用序号 1..N」（D1.4/D7.7）。本地预检仅用可信的手牌可打出性（D7.3）；补全用**手牌全集**（D7.4）。指令表**不含** `play.join`（D1.3）。**换牌相位指令**：`keep`/`保留`/`不换`、`mulligan`/`换牌`（D6.1），并按 D6.2 做相位门禁。**门禁**：`pending != null` 时只接受序号 / `ok` / `x` / `log` / `help`（D2.5）。`help` 只列一个 `play/出牌` 条目（D7.8）。
- Acceptance:
  - [ ] `help`/`?` 列出中英两套写法与一句话说明（R6、D7.8）。
  - [ ] 中英写法共用同一执行路径与错误提示（R6）。
  - [ ] **当动作需要额外选择时**进入引导、否则直接完成；任何情况下不发不完整请求（R6 修正口径、D7.5）。
  - [ ] 非法序号本地拒绝并回显「可用序号 1..N」（R6、D7.7）。
  - [ ] `play` 对 unit/order/counter 分别命中 `play.unit_pre`/`play.command_pre`/`play.counter`；反制卡不产生任何选择步骤（D7.1）。
  - [ ] `handPlayabilityKnown==true` 且 uid 不在 `playableUids` 时不发请求并说明原因；为 `false` 时不拦（D7.3）。
  - [ ] 补全与 `hand` 展示含不可打出的卡（标注原因、排后面）（D7.4）。
  - [ ] `play 4 2` 在"最终没有选择器"时只给警告、不报错（D7.6）。
  - [ ] 不存在调用 `play.unit{ref,owner,slot}` 的路径，也不存在 `play.join` 指令入口（D1、D1.3）。
  - [ ] 响应 `code=="targeting"` 时不再二次等待即提交；仅反常路径才走 `wait_pending` 短超时兜底（D2.2）。
  - [ ] pending 期间除序号/`ok`/`x`/`log`/`help` 外的指令被本地拒绝并提示「请先完成选择」（D2.5）。
  - [ ] 换牌相位下除 `keep`/`mulligan`/`log`/`help`/`quit`/`concede` 外一律本地拒绝，`end` 不改派（D6.2）。
  - [ ] `attack`/`move`/`leave` 的第一参数按 D10.1 的面板顺序解析；越界回显「我方单位共 N 个（可用 1..N）」（D10.7）。
  - [ ] 两套数字同现时提示条/标题写明角色（`攻击 2 → 选择目标` / `来源：我方 2 号 <名>`）（D10.5）。
  - [ ] 执行前回显解析结果（D10.8）；`command.begin` 不在指令表中（D10.4）。
  - [ ] `units`/`hand` 为纯本地只读指令、不发网络请求，且都在 `help` 里（D10.9）。
  - [ ] 指令表与别名完全按 D14 的 16 条实现，`help` 每条都带中英两套写法（D14/D14.2）。
  - [ ] `结束回合` 与 `退出` 在三种相位下的命运符合 D14.5，拒绝时给出正确动词（D14.5）。
  - [ ] `quit`（对局中）与 `concede` 走二次确认；换牌相位/终局菜单的 `quit` 不确认（D14.4）。
  - [ ] `--help` 与 `help` 全表都不出现对手连接/令牌类选项（R12、D14.7）。
  - [ ] 候选来源与条目格式按 D16.2；第二参数不预取、不给补全（D16.2/D16.3）。
  - [ ] Tab 只填参不执行，唯一候选也不自动执行（D16.4）。
  - [ ] 引导、补全、`hand`/`units` 清单共用 `candidates_for(context)`（D16.5）。
  - [ ] plain 引导打印候选全量、不静默等待；管道 EOF 且有 pending 时 `target.cancel` 后非零退出（D16.6/D16.7）。
- Rationale: 卡类型是视图里的数据、不是玩家的输入，所以动词必须单一；而落点/目标这些"引擎给的候选"必须由服务端产出，所以执行路径统一成"前置调用 + 候选应答"（D1/D7.1）。实体编号锚在"面板怎么画"上，玩家才可能输入正确；锚在服务端集合上会无视觉线索地跳动（D10.1/D10.3）。

### S6: 选择模式与取消（`notify/target.request` 应答）
- Status: Todo
- Target: `tools/orc/commands.py`（选择模式的输入语法与校验）、`tools/orc/render.py`（候选列表渲染）
- Approach: 收到待决即切「选择」模式：序号列表（名称/类型/血量/描述）、已选计数与 `min..max`、`ok` 提交 / `x` 取消；多选支持逗号或空格分隔；越界即拒并提示。换牌复用同一界面（selector `"mulligan"`）。候选标签按 D1.2（formation-gap 用邻居名）与 D1.6（slot 用「前线第 X 号槽」作附注）渲染。取消按 D2.5 两条分支处理（pending 已就绪 ⇒ `target.cancel`；pre 在途 ⇒ `AbortIntent`），绝不留下悬挂 pending。
- Acceptance:
  - [ ] 立即切模式且输入行语法随之切换（R7）。
  - [ ] 超 `max`／少于 `min` 拒绝并提示（R7）。
  - [ ] `formation-gap` 候选不出现空标签，显示为「插入位置 k：…」（D1.2）。
  - [ ] 取消后回到普通输入，且服务端待决状态被清（不留悬挂 pending）（R7、D2.5）。
  - [ ] `x` 在 pre 在途时的两种结局（立即 cancel / 提示"已完成无法取消"）都被覆盖（D2.5）。
  - [ ] 换牌选择器上 `x` 只"放弃本次换牌"、不确认，且提示条仍表明"仍在换牌相位"（D6.1/D6.3）。
  - [ ] 无 pending 时敲 `x` 不发 `target.cancel`，只给提示（D6.7）。
  - [ ] 收到 `notify/target.expired` 后提示已过期并清除候选（R7）。
- Rationale: 内联参数与本模式共用同一序号空间（D1.1），故两者是同一渲染器与同一校验器的两个入口，而不是两套逻辑。

### S7: 战斗日志翻译
- Status: Todo（D8/D9 已裁决，可执行）
- Target: `tools/orc/render.py` 内的叙述层（`narrate` 函数族；不单独成模块，避免与 Renderer 分叉）
- Approach: 过滤按 D8.1；模板表按 D9.1（键＝真实更新名）；攻击/移动的区分按 D9.2 的叙述层共现判定（一行 `if` ＋注释，不引入第二套事件模型）；折叠按 D8.2 ＋ D9.3（共四条）；三处确定输出按 D9.4；降级按 D8.4；`--verbose` 追溯按 D9.5；数值回落按 D9.6。行动者/对象名一律取自载荷 `{$ref,name}`，取不到写"某单位/某玩家"（D8.6）。
- Acceptance:
  - [ ] 一次出牌/攻击至少 1 条叙述，含行动者与对象名（R8）。
  - [ ] 未识别事件以原始**事件名**降级显示（R8、D8.4）。
  - [ ] 表中不出现 D9.1 列出的六个不存在名字（D9.1）。
  - [ ] `attach`/debug 条目被丢弃且不留占位（D8.1/D8.8）。
  - [ ] 脱敏条目只输出 `对手：一次不可见的变化`（D8.1）。
  - [ ] 四条折叠规则各自生效，且跨段不合并（D8.2/D9.3/D9.6）。
  - [ ] `unit.acted` 从不单独成行（D9.1/D9.2）。
  - [ ] `log N` 只从 deque 取（D8.5）；`log --prev`/`--all` 按 D12.2 取对应局的 deque。
- Rationale: 模板键在引擎真实名上，叙述才与引擎事实同源；"攻击/移动"只在措辞层用共现关系判定，避免把叙述推断渗进裁决（D9.1/D9.2）。

### S8: 渲染（Renderer ＋ TuiFront ＋ PlainFront）
- Status: Todo
- Target: `tools/orc/render.py`（Renderer 纯函数）、`tools/orc/front_tui.py`（唯一 import `prompt_toolkit` 的文件）、`tools/orc/front_plain.py`
- Approach: 按 D2.4/D2.7：`Renderer(snapshot) → list[str]` 是唯一布局源，含**两套布局**——play 视角版（终局横幅、对手区、我方区与手牌、日志窗格、选择区条件显示、提示条）与 watch 中性版（D15.1）；Front 只附加样式与承接输入。`TuiFront` 用 prompt_toolkit `Application`+`Layout`（`HSplit`），日志**自绘最近 N 行**，渲染由 `on_frame` → `loop.call_soon_threadsafe(app.invalidate)` 驱动；输入行在整个等待期可编辑。提示条的「对手行动中」派生自 `phase=="play"` 且 `activePlayerSide=="enemy"`（视图字段，无需额外裁决）。`PlainFront` 纯文本无 ANSI，每条命令后打印自包含块＋自上次以来的新日志，推送只入 deque；非 TTY 自动降级为 plain；`--stream` 开实时打印。终局画面按 D12.1/D12.5（横幅＋保留战场、不清屏）；未知态按 D11.5 的提示条文案。CJK 列宽按 `east_asian_width` 在 Renderer 层算。
- Acceptance:
  - [ ] 默认模式画面不整体上滚，日志窗格不依赖终端 scrollback（R3、D2.4）。
  - [ ] 输入行与提示条常驻，含「对手行动中」（R3）。
  - [ ] `--plain` 每条命令输出一块自包含战场文本，可完整重定向到文件（R3）。
  - [ ] 非 TTY 自动降级为 plain，无需额外参数（D2.4）。
  - [ ] TUI 与 plain 输出的行内容一致（仅样式不同）（D2.7）。
  - [ ] 战场面板把编号与单位画在同一行，并标 `⚔`/`→`/`⚔→` 与不可行动原因（D10.2/D10.3）。
  - [ ] 换牌相位提示条与 `x` 后文案按 D6.3/D6.4 渲染。
  - [ ] 「结果未知」态在提示条显示 `⟳ 结果未知：等待服务端刷新`（D11.5）。
  - [ ] watch 中性布局与 play 视角布局共用同一 Renderer（D15.1/D2.7）。
  - [ ] `hand [0|1]`/`units [0|1]` 在两种模式下缺省行为符合 D15.4。
- Rationale: 渲染由推送驱动才能满足"AI 行动期间界面仍活"；日志自绘才能让"不滚屏"与"可重定向"共用一份实现（D2.4）；两套布局共用同一 Renderer 才能保证 TUI 与 plain 只差样式（D15.1）。

### S9: 启动判定链、断线重连与房间轮换
- Status: Todo（D3 已裁决，可执行）
- Target: `tools/orc/session.py`（记录读写 ＋ 判定链）、`tools/orc/cli.py`（三条出口的编排）
- Approach: 按 D3.1 实现三条互斥出口（回座 / 同实例重开 / 新开），判定只用「实例在不在清单」与「令牌是否可回座」两条事实（D3/F15/F16/F17）。记录 schema 与写入时机按 D3.2/D3.4；认领只认 `instanceId`（D3.3）。轮换（`notify/room.rotated`）后按 D12 处理：清面板/选择器/pending/unknown、日志切新 roomId 的 deque、进入终局菜单（`restart`/`quit`/`log`/`help` 放行）；`restart` 四步与三条失败分支按 D12.3/D12.4；令牌失效但实例仍在 match ⇒ 按 D3.5③ 报「不属于本会话」。
- Acceptance:
  - [ ] 重运行能凭令牌回到原座位并跳过 spawn/admit/start（R11、D3.1 2b）。
  - [ ] 令牌失效时给出可读提示并回退到重新申请（R11、D3.1 2b/c）。
  - [ ] 轮换后清面板/选择器/pending/unknown、显示终局横幅（`—— 失败 · 因认输 · 第 12 回合 ——` 形式）并给出 `restart`/`quit` 二选一（R11、D12.1/D12.2/D12.5/D12.7）。
  - [ ] 日志按局分段，`log --prev`/`--all` 可回看上一局（R8＋R11、D12.2）。
  - [ ] `restart` 只做「无 token attach → conn.admit → match.start」，永不 spawn（D12.3）。
  - [ ] `deck-invalid` 走"重生成 39 张＋重试一次"、`missing-assets` 只提示不重生成（D12.4）。
  - [ ] 终局先于/后于 `room.rotated` 到达两种顺序下结果一致（D12.5）。
  - [ ] 终局时清 pending 但**不**调 `target.cancel`，并清 unknown（D12.6）。
  - [ ] 实例在、已轮换 ⇒ 复用同实例重开，不 kill 不 spawn，写回新 roomId 与新令牌（D3.5②/F17）。
  - [ ] 实例在 match 中但令牌失效 ⇒ 不接管，报"不属于本会话"并给 `--new`／`--instance` 指引（D3.5③）。
  - [ ] 不按"最新创建/端口最大"等启发式认领实例；别的实例一律不碰（D3.3）。
  - [ ] `--new` 不复用记录、也不 kill 旧实例（D3.6）。
- Rationale: 只用可观测事实判定，崩溃场景也能走通；把"上次怎么退出"排除在状态之外，是为了让判定链在进程被杀、断电、Ctrl-C 之间没有分叉（D3）。轮换的界面流程把"一屏状态"与"叙事历史"分开处置，才让 R11 与 R8 同时成立（D12.2）。

### S10: `watch` 观战模式
- Status: Todo（D15 已裁决，可执行）
- Target: `tools/orc/cli.py`（模式选择）＋ `tools/orc/session.py`（全知帧入库）＋ `tools/orc/render.py`（中性观战布局，与 play 布局并列的第二套）＋ `tools/orc/commands.py`（watch 白名单）
- Approach: 按 D15：单一中性战场（标题／B 区／共享前线只画一次／A 区含手牌明细／日志／输入行），标题只用 `A（seat 0）`/`B（seat 1）`；`views[i].view.selfLine` 合成两条支援线、前线按 `slotIndex` 去重、以 `selfLine` 为准丢弃 `enemyLine`（D15.1–D15.3）；`hand [0|1]`/`units [0|1]` watch 缺省列双方且带 `A1/B1` 前缀（D15.4）；白名单 `hand`/`units`/`log`/`help`/`quit`，其余本地拒绝且**不发请求**（D15.5）；`quit` 不二次确认（D15.6）；单侧数据退化并在标题标 `（单侧数据）`（D15.7）。实例与管理器来源同 play（`--instance` / 记录），**不写 `session.json`**、不 kill 实例、不影响对局（D3.7/D15.9）。
- Acceptance:
  - [ ] 连上已有实例并显示双方手牌明细（R10、D15.4）。
  - [ ] 动作指令被拒并提示，且**不产生任何 `tools/call`**（R10、D15.5）。
  - [ ] 退出不影响对局与其它连接（R10、D15.9）。
  - [ ] `watch` 全程不改写 `session.json`、不 kill 实例（D3.7/D15.9）。
  - [ ] 前线只画一次、标题不出现"我/我方/敌方"（D15.1–D15.3）。
  - [ ] 只拿到一份 view 时退化为单侧渲染并标注（D15.7）。
- Rationale: 观战没有"我"，任何带视角的措辞都会让观众误判谁在打谁；把两份视角合成一份中性战场，才能让"前线是共享的"这一事实在画面上成立（D15.1/D15.2）。

### S11: 卡包与卡组来源
- Status: Todo（D5 已裁决，可执行）
- Target: `tools/orc/manager.py`（`packages`/`packDirectories` 准备与 `session.json` 落盘）＋ `tools/orc/cli.py`（`--pack`/`--deck` 参数）
- Approach: 按 D5.1/D5.2 发现卡种并生成 39 张（`manifest.metadata` → `kind=card` 的 id；无 metadata 则递归扫 `**/*.card.json` 排除 `*.meta.json`；合并去重后按 id 字典序）；`packages` 取 manifest 三元组、`packDirectories` 取绝对路径（D5.5）；`--deck` 用 JSON 数组覆盖且不做本地校验（D5.3）；`deckConfig` 传 null（D5.4）；卡组写入 `session.json`（D5.6）；`deck-invalid` 翻译为可读提示并指出问题项。
- Acceptance:
  - [ ] 无参数启动即可开局（自动选包 ＋ 39 张）（R9、D5.2）。
  - [ ] `--pack` 生效于卡池装配、`--deck` 原样用于 `session.attach`（R9、D5.3/D5.5）。
  - [ ] 同一卡包重复开局时生成同一副牌（id 字典序、非 metadata 顺序）（D5.1）。
  - [ ] `card-pack-demo` 的 `cards-support/` 下卡片不会漏（递归）（D5.1）。
  - [ ] 空卡包在本机报错且不 spawn（D5.2）。
  - [ ] `deck-invalid` 指出问题项（R9）。
- Rationale: 唯一可复现的取卡序只能来自资产 id；张数固定 39 与包大小解耦，才能让"换包"不影响开局可行性（D5.1/D5.2）。

### S12: 退出清理与实例归属
- Status: Todo
- Target: `tools/orc/cli.py`（唯一 shutdown 函数）
- Approach: 按 D3.8：`quit`／Ctrl-D／未捕获异常／终局选「退出」都走同一 shutdown（kill 或保留 → 更新记录 → 关 socket）；`--keep-instance` 保留并打印实例标识与端口；**管理器一律不杀**（即使由本会话拉起，D4.4），只在日志留一行；`manager.kill` 失败时按"实例是否还存在"决定记录去留（D3.1 `4`）。
- Acceptance:
  - [ ] 正常退出后实例被销毁且记录被删除；`--keep-instance` 时实例保留、记录保留并打印标识与端口（R5、D3.1 `4`）。
  - [ ] 由本会话拉起的管理器在 CLI 退出后仍存活，且日志留有「继续常驻：<port>」（R5、D4.4）。
  - [ ] kill 失败时不留下指向死实例的记录（D3.8）。
  - [ ] 所有退出路径都经同一函数（D3.8）。
- Rationale: 单一 shutdown 出口是"实例归属"（R5）与"记录去留"（D3.8）能成立的前提；分散的退出路径必然留下指向死实例的记录，下一次启动就会走进错误的判定分支。

### S13: 可测试性（FakeDispatcher ＋ 三组断言）
- Status: Todo
- Target: `tools/orc/tests/`（`unittest`，不引 pytest）
- Approach: 按 D2.6：抽出 `IDispatcher` 接口，提供 `FakeDispatcher` 注入任意帧序列（不起 socket、不起 TTY）。断言三件事：① CommandEngine 发出的工具调用序列；② Renderer 在给定 Session 下的文本行；③ pending 期间的指令门禁。`PlainFront` 兼作端到端断言入口。
- Acceptance:
  - [ ] `pre → submit` 两步调用序列可断言；非法序号**不发** submit（D1.4、D2.6）。
  - [ ] 同一 Session 快照渲染出的文本行可断言，含中文列对齐（D2.6/D2.7）。
  - [ ] pending 期间非白名单指令被拒（D2.5）。
  - [ ] 端到端用例喂 stdin / 抓 stdout，无需真实服务端（D2.6）。
  - [ ] `printf 'keep\nquit\n' | python tools/orc_play.py play --plain` 能跑通（D6.9）。
  - [ ] 超时值与错误码分支可确定性触发（常量可在测试里调小）（D11.1/D11.3）。
  - [ ] 终局 → `restart` → 新局日志干净（旧局在 `log --prev` 里）（D12.2/D12.3）。
  - [ ] watch 下动作指令被拒且**零 `tools/call`**（D15.5）。
  - [ ] 引导打印的候选与 `hand` 输出的前 N 条一致（D16.5）。
  - [ ] Tab 补全不执行动作（只填参）（D16.4）。
- Rationale: 把协议交互压到 `IDispatcher` 后面，才可能对"三条时间线"的客户端做确定性断言；否则每次回归都要起管理器＋实例＋AI（D2.6）。
