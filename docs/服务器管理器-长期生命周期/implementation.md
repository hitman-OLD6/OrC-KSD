# Implementation — 多人联机模型重构（管理器-服务器-房间-seat-viewer-controller）

> 状态：**实现路径已定稿**（S1–S11 全部落位；U1–U9 全部裁决）。实施时按步骤依赖图推进。
> 需求：`requirements.md`（R1–R12 全部 Confirmed）。基线：`基线调研.md`。
> 本文只写"产品内部结构/设计"；`Approach` / `Rationale` 均来自父 agent 逐轮裁决，未留 `待裁决` 项。

## Scope

- Target：把「一进程＝一服务器＝一房间＝一局＝2 座」的竖线拆成
  **管理器 → 服务器实例 → 房间 → seat → (controller, viewer)**，并把管理员作为正交于 seat 的身份；
  服务器生命周期从"一场对局"解耦为可由管理器托管的常驻实例。
  主战场：`src/Orc.Server`（会话/房间/推送/启动链路）、新增管理器工程、`src/Orc.Packages`（资产编排）、
  `src/Orc.Mcp`（投影与动作面按角色分类）、协议面 + `Orc.Client` / `Orc.Mcp.Bridge` / 控制脚本 / 测试同步改造。
- Excludes（与需求 Excludes 一致）：`Orc.Game` 规则语义（尽力零改动）、卡牌数值、账号/匹配/排行、
  观战回放、WebSocket 承载、卡组资产化、资产实际分发/下载、管理员远程协议完整形态。

## 口径：什么叫"远程可调"（可被测试断言，后续批次复用）

- **远程 ⇔ ①网络可达 ∧ ②调用者非进程主人**。二者同时成立才算远程（R10 Behavior）。
- 管理面的唯一入口是**子进程 stdin（命令）/ stdout（回执＋事件）** ＋ 管理器进程内 API；
  `Program.Main` 的 stdout 除首行就绪报文外全程空闲，日志一律走 stderr，故该通道不引入新的可见面。
- **禁止**把管理通道做成回环端口或命名管道监听器——一旦"监听"就落在网络可达侧，判定口径立刻失效。
- 可断言（三条，直接由口径导出，不得靠人工审查保证）：
  1. 实例业务端口上的 `tools/list` **不含**任何管理 op（管理面不注册进工具表）；
  2. 业务端口上调用任何管理 op 名 ⇒ 未知方法，**不路由**到管理面；
  3. 无 stdin 通道的进程（例如手工 `dotnet Orc.Server.dll`）不具备管理面，且不因缺少管理面而拒绝服务业务协议。

## 新增/改义码表（唯一来源，两侧各一份常量并有映射测试）

| 码 | 产生侧 | 语义 |
|---|---|---|
| `lock-conflict` | 管理器 | 并集中同 id 版本冲突（指名双方与版本） |
| `missing-assets` | 管理器 | 缺口清单（点名哪一方缺哪些 `{kind,id,version}`） |
| `not-admitted` | 服务器 | `Pending` 连接调任何动作/调试/`observe.*`（不按工具区分） |
| `seat-taken` | 服务器 | 申请已被占用的座位（同步拒绝，不进 `Pending`） |
| `stale-room` | 服务器 | 调用携带旧 `roomId`（轮换后） |
| `stale-seat` | 服务器 | 用旧纪元 `SeatBinding` 解析设施 |
| `room-rotating` | 服务器 | drain 窗口内的管理 op（带 `retryAfterMs`，建议 200） |
| `already-ended` | 服务器 | 对已结束对局调 `room.abort`（幂等成功，不报错） |
| `invalid-token` | 服务器 | 重连令牌无效或已随房间终结失效 |
| `instance-boot-failed` | 管理器 | 实例进程起不来（boot 前置不满足） |

## 引擎改动清单（本轮**唯一**触碰 `Orc.Game` 的地方）

需求 R11 要求"无胜者终止"，落地为**加性**三处（不是规则变更，是终局原因补全）：

| # | 改动 | 现状证据 | 备注 |
|---|---|---|---|
| E1 | `MatchEndReason` 增加 `Aborted = 2` | 现有只有 `HqZero = 0` / `Concede = 1`（`MatchEndReason.cs:6-13`） | 纯加值 |
| E2 | `MatchLifecycle.End(Player winner, …)` 的 `winner` 放宽为 `Player?` 并去掉 `ArgumentNullException.ThrowIfNull`（或加一个无胜者重载） | `MatchLifecycle.cs:48-50`；`Winner` 属性本身已是 `Player?`（`:19`） | 父 agent 口径"一个枚举值 ＋ 一个方法"在这一点上需要多放宽一处参数，属必要配套 |
| E3 | `Match.Abort()`：走与 `Concede` **相同的终局收尾路径**，`Winner = null`、`EndReason = Aborted` | `Match.Concede` 路径见 `Match.cs:953-976` | 不得用"代某一方认输"模拟终止（会凭空产生胜者，污染战绩与回放） |

**不需要改投影**：`UiProjectionProvider` 已经按 `Winner is null` 处理（`:275-282` → `UiMatchOutcome.None`、结果标题取空），故 `Winner = null` 天然可渲染为"无胜者"。

## 决策索引（全部已定）

| # | 分支 | 状态 |
|---|---|---|
| U1 | 管理器 ↔ 服务器子进程 的管理面可达性 | **已定**（2026-10-21）：实现在子进程、入口在管理器、经父子私有通道；通道不算远程 |
| U2 | 实例＝进程是否绝对 | **已定**（随 U1）：每实例一条独立 stdin/stdout 对；实例间零共享；回收＝句柄退出 + 端口释放 |
| U3 | 会话/角色声明与接入许可的时序（连接级状态机） | **已定**（2026-10-21）：`ClientSession`（连接对象）＋ 一个角色状态机；申请—批准两段；`Pending`＝第三态（无权态） |
| U4 | 启动前置"双方 lock 并集"的「双方」与对账方向 | **已定**（2026-10-21）：拆成"进程启动"与"开局"两层；启动不预装卡池；声明走 `session.attach`；并集/冲突/缺口由**管理器**算，服务器只执行 |
| U5 | 管理器 MCP 入口本轮是否交付 | **已定**（2026-10-21）：本轮交付**进程内 API ＋ CLI** 两档，共用同一个 `IManager`；MCP 面＝远程管理协议，不本轮交付（"被其他程序携带"属进程内 API 的宿主形态，不另立入口） |
| U6 | 全知 viewer 的"全知输出"形状 | **已定**（2026-10-21）：不新增视图类型＝两份 seat 视角打包 ＋ 全量段；通道复用 `notify/view`+`notify/segment`；只看 `perspective.mode` |
| U7 | 房间轮换时服务层（`McpService` / `MatchExecutor` / 工具表 / `IRoomControl`）是"每房间重建"还是"跨房间长存 + 可重绑" | **已定**（2026-10-21）：按归属分——`SerialWorkQueue`/`ServerHost` 进程级长存；`McpService`/`IRoomControl`/工具表 房间级重建；轮换走 10 步固定序 |
| U8 | 非连接型 seat（AI 托管／空座）的卡包与卡组声明来源，及其在并集判定中的位置 | **已定**（2026-10-21）：并集**按 seat** 计；AI seat 由管理员在 spawn 时代配置（`packages` 缺省空集＝并集单位元）；卡组在装配后生成；轮换自动重建；`HeadlessContext` 整体退役 |
| U9 | seat 与对局设施的绑定形式、"房间终结即失效"的保证方式 | **已定**（2026-10-21）：seat 不持活对象，只持 `SeatBinding{ Epoch, PlayerIndex }`；失效判定＝纪元不匹配或当前无对局；不用 Dispose/GC 可达性；清理清单见 S8 |

## Implementation Steps

依赖关系：

```
S1 管理器骨架 ─┬→ S2 父子私有通道 ─→ S3 管理面（子进程内）
               ├→ S4 管理器资产职责
               └→ S5 服务器实例与房间轮换 ─→ S6 会话/角色模型 ─┬→ S6b 断线与终止（R11）
                                                              ├→ S6c 房间态可见性（R12）
                                                              ├→ S7 输出分发
                                                              ├→ S8 设施归属
                                                              └→ S9 调试面
S5 + S6 ─→ S10 卡池重装（轮换点）　　S6..S10 ─→ S11 协议面与入口/测试改造
```

### S1: 管理器骨架与实例编排
- Status: Todo
- Target: 新增管理器工程（进程内 API ＋ CLI 入口共用同一语义）；`tests/` 对应测试工程；`Orc.Client/ServerProcess.cs`（复用其进程治理）
- Approach:
  - 管理器对外提供五组操作（对应 R10）：`manager.spawn` / `manager.kill` / `manager.reload` / `manager.list` / `manager.admin`（把管理面透传给指定实例）。
  - 实例清单按**实例标识**（稳定字符串）索引，每实例持有：`Process` 句柄 ＋ stdin writer ＋ stdout 读循环 ＋ 分配到的业务端口 ＋ 运行目录 ＋ 当前卡池摘要。
  - **入口分层**：进程内 API 是真身；CLI 是同一真身的一次调用，两者逐字等价（R1 验收按**已交付两档**断言：同一操作在进程内与 CLI 下结果一致；MCP 面属后续批次的远程管理协议）。逐实例操作一律以实例标识为第一参数，不接受"当前实例"隐式默认。
  - 管理器**不持有** `Match`/`Player`/`Room` 活对象（R2）；对实例的一切读取都来自通道回执或事件。
  - 管理器承担**唯一权威的资产判定**：`spawn` 时把包索引/资产视图交给实例（实例只校验"可索引"）；**批准申请**与**发起开局**两处由管理器算并集/冲突/缺口，并把"本局加载计划"下发给实例（S4）。
  - 启停语义：`reload` ＝ `drain`（可选）→ `Kill(entireProcessTree:true)` → `WaitForExit` → 用新 dll 起新进程 → 等首行就绪报文 → 端口复用断言。超时与"是否强制结束在局对局"由实例配置决定，默认不强制（R1 最后一条）。
- Acceptance:
  - [ ] 无对局时管理器持续存活；≥2 实例并行开局互不影响；单实例销毁/重载不影响其余（R1）
  - [ ] 重载后无残留子进程、端口可复用（句柄退出 ＋ 端口 bind 两处都断言）
  - [ ] 管理器对同一实例的资产判定结果可复现（同一声明 → 同一并集/同一缺口清单）
- Rationale: 实例＝进程是 U2 的定稿结果，因此"编排"只能是进程级动作；把管理面调用统一收敛到 `manager.admin`，避免管理器为每类管理操作各造一条路径，从而保证"进程主人在哪、判定就在哪"只有一处。资产判定放在管理器，是因为资产库只存在于管理器进程（R2），服务器侧无论怎么算都只是二手副本。

### S2: 父子私有通道
- Status: Todo
- Target: `src/Orc.Server/Program.cs`（stdin 读循环）、新增 `src/Orc.Server/Admin/`（通道编解码）、管理器侧通道客户端
- Approach:
  - 首行就绪报文**保持现状**（`{"ready":true,"port":N,...}`，字段语义不变）；此后 stdout 每行一类报文：
    - 命令（父→子）：`{"id":N,"op":"seat.bind","args":{...}}`
    - 回执（子→父，必有 `id`）：`{"id":N,"ok":bool,"code":...,"message":...,"payload":...}`
    - 事件（子→父，无 `id`）：`{"event":"...","payload":{...}}`——命名沿用现有 `notify/*` 习惯，不另造一套
  - 分流判据单点：**有 `id` ＝应答、无 `id` ＝事件**；未知 `op` 回 `ok:false` 且带可读 `code`，不得静默丢弃。
  - stderr 保持纯日志（现约定不变）；stdout 不得混入任何日志（含异常栈）。
  - 错误码**不直穿**：子进程码表先映射为管理器码表，再由管理器对外返回；两侧码表各自有常量源与一致性测试。
  - 事件至少覆盖：轮换点重装结果、对局终局、seat 绑定变化（S3/S10 的结论要回到管理器，不能只有请求-应答）。
- Acceptance:
  - [ ] 单条 NDJSON 行可解析出 op/id 或 event；未知 op 有可读错误
  - [ ] stdout 除就绪行与上述两类报文外无其它输出（有断言）
  - [ ] 子进程异常/退出时，管理器侧所有在途请求以失败结束而非挂起（对应现有 `OrcClient._closedReason` 的同类修复）
- Rationale: 复用 stdout（现在只写一行）+ stdin（现在完全不读），不新增端口/管道，是唯一既满足"仅创建者可写"又不触碰"网络可达"的形态；事件与应答共用一条流，避免管理器侧出现第二个读循环。

### S3: 管理面（服务器进程内实现）
- Status: Todo
- Target: 新增 `src/Orc.Server/Admin/AdminApi.cs`（＋通道路由）、`ServerHost` / `Room` 接线
- Approach:
  - **执行上下文（两栏，实现者不再逐条推）**：
    - **入 `SerialWorkQueue`**：`match.start`、`room.abort`、`seat.bind`、`seat.unbind`、`conn.revoke`、`sandbox.reset`，以及资产层的 `card.write`/`effect.write`（改的是装配输入，见刻意变化第 12 条）。`seat.bind` 入队的理由不是"改引擎"，而是"绑定后要重推 pending 与初始视图"——它要读引擎状态，不能与在途动作并发。
    - **不入队**：`connections.list`（纯读）、`conn.admit` 的**校验阶段**（只读资产库与活状态快照；批准动作本身才入队）、`instance.*`（管理器级，不进服务器队列）、所有只读工具（`card.list`/`pack.info`/`file.read`/`room.info`）。
  - **`conn.revoke` 立即生效、不给宽限**（R11）：撤销 controller（对局中）⇒ 入队 → 标记 `revoked` → **立即判负**（等价 `Concede(seat)`）→ 对局结束 → 断开连接 → 推 `notify/admission{decision:"revoked",roles,seat,reason:"admin-revoked"}` → 令牌立即失效；撤销 controller（lobby）⇒ 断开 ＋ 释放绑定 ＋ 同一条通知；撤销 viewer ⇒ 入队（仅与推送序列化）→ 断开该 viewer，对局不受影响。**不走 `DisconnectGrace`**——宽限是给意外掉线的补偿，管理员主动收回是确定意图。
  - **drain 窗口（`room-rotating`）内一律拒绝、不排队**，统一回 `room-rotating` ＋ `retryAfterMs`（建议 200）。三处例外/细化：`match.start` 窗口内＝`room-rotating`（**失败，不延后**）；`room.abort` 窗口内＝幂等成功（此时已无活跃对局，空操作，不报错）；`conn.revoke` 窗口内＝**允许**（只动会话、不动旧栈），但其"绑定释放"部分记为**待新房间建立后清理**，避免碰正在销毁的旧 `Room`。
  - 由此的必然结果：撤销 controller 直接终结对局，紧随其后的 `room.abort` 返回 `already-ended`（幂等成功），不是 bug。
  - **一切资产判定不在这里做**：并集/冲突/缺口由管理器算（S4），本层只接收"本局加载计划"（包集合 ＋ 期望 lock）并执行；失败时把管理器的码**原样**上报，不另造码表。
  - `conn.admit` 在**子进程内**校验"活状态"：seat 存在、controller 槽位未被占、角色集合允许该组合（全知不得含 controller）；资产类拒绝由管理器在调用**之前**给出，故不存在"批了却开不了局"。
  - `seat.bind` / `seat.unbind` 与 `conn.admit` 是同一件事的两半：许可＝允许该连接以该角色接入，绑定＝把角色挂到 seat；两者都不读 `SeatScope`、不读 `McpServiceMode`。
  - `pool.reload` **不再是独立 op**：卡池按房间构建（R3），"提交变更"退化为改磁盘包目录，下一次开局自动生效（S10）；因此不设"待生效队列"，U1 答复里那条 `{"accepted":true,"effectiveAt":"rotation"}` 回执随之取消。
  - `drain`：拒绝新连接与新手入座，等在局对局自然终局或到超时；是否强杀由管理器决定（默认不强杀）。
  - 与调试面严格分离：`AdminApi` 不注册进工具表；调试面也不得调 `AdminApi`（R10 acceptance 第 4、6 条）。
- Acceptance:
  - [ ] 管理员不占 seat、不出现在 seat 快照；绑定即授权、解绑即收回（R10）
  - [ ] 可撤销已许可连接；撤销后令牌失效、连接收到 `decision:"revoked"` 并被断开（R10/R11）
  - [ ] 对局中撤销 controller 立即判负，不走宽限（R11）
  - [ ] 管理面不暴露任何引擎调试 API；调试面不暴露任何管理能力
  - [ ] 管理 API 不依赖 seat 令牌
  - [ ] 开局装配失败时房间回到 lobby 且服务器进程存活（R3）
  - [ ] drain 窗口内 `match.start` 回 `room-rotating` 且带 `retryAfterMs`；`room.abort` 幂等成功
- Rationale: 管理操作的全部**活状态**校验条件（seat/session/room）只存在于子进程内存，放在子进程是唯一不需要把活状态复制出去的落点；**资产**判定相反——权威在管理器的资产库（R2），放子进程只会产生二手副本与两套码表。二者按"活状态 / 资产"切开，而不是按"管理面 / 非管理面"切开。

### S4: 管理器资产职责（注册 / 依赖分析 / 同步分析）
- Status: Todo
- Target: `src/Orc.Packages`（复用 `LockMerger.Merge` / `LoadListPlanner.Plan` / `LockConflict`）＋ 管理器资产库
- Approach:
  - 资产库 = 对包目录的**索引/注册结果**（只增不减、同 id 多版本并存，D2）。管理器不建 `Match`/`Player`/`Room`。
  - **并集判定**：收集两个 seat 声明的 `{packageId,version,guid}` ＋ **实例级沙盒包（强制加入，不由玩家声明）** → 在资产库定位 → `LockMerger.Merge` 合并为一份 lock → `LoadListPlanner.Plan` 产计划。
  - **沙盒包非法时的降级**：开局装配失败先**剔除沙盒包重试一次**；成功则开局并告警"本次调试产物不可用"，仍失败才拒绝开局（R3）。
  - **码表（管理器侧唯一）**：`IsFatal` 的 issue → `lock-conflict`（指名双方与各自版本）；非致命的 `Missing`/`DependencyMissing` → `missing-assets`（缺口清单，点名"哪一方缺哪些 `{kind,id,version}`"）。**不新写冲突判定**，一律映射现有 issue。
  - 产出两样东西：`rejection`（码＋清单）或 `plan`（本局加载计划＝包集合 ＋ 期望 lock），后者经私有通道交给服务器执行。
  - 卡组合法性校验（`DeckRules`）**也挪到批准这一刻**：因为服务器启动时不装卡池（S5），`room.claim` 阶段没有卡池可校验；用并集算出的计划临时构建池视图再校验。
- Acceptance:
  - [ ] 给出"资产注册结果"，服务器据其加载自己的卡池（R2）
  - [ ] 依赖分析结论（可用/不可用及原因）
  - [ ] 两份 lock 的差异清单（缺失资产、版本不一致），可定位到"哪一方缺什么"
  - [ ] 多实例共享资产库、各持独立运行中卡池
  - [ ] `lock-conflict` / `missing-assets` 的用例都能落到现有 `LoadListPlanner` 的具体 issue 类型上（有映射测试）
- Rationale: 权威只有一处——资产库在管理器进程，若让子进程也算，同一份声明会在两个进程里得出两份清单，码表与顺序都可能漂移；而 `LoadListPlanner` 已经是 fatal/非 fatal 分好的，直接映射比新写一套判定安全得多。卡组校验一并前移，是因为"没有卡池"这个前提使原位置无法成立。

### S5: 服务器实例与房间轮换
- Status: Todo
- Target: `src/Orc.Server/ServerBootstrap.cs`、`Room/`、`Program.cs`
- Approach:
  - **启动链拆两段**：`ServerBootstrap.BootAsync`（进程启动：只建 host ＋ 首个 lobby 房间 ＋ 2 个空 seat，**不装卡池**）与 `ServerBootstrap.RotateAsync`（轮换：排空 → 换栈 → 开新房间）。`rotate` 是同一段代码的复用以"开局装配"之后的收尾路径。
  - **每局开局时装配**：按管理器给出的加载计划调一次 `MatchCardAssembly.Register`，成功才建局；失败回退到 lobby，进程存活、不污染（`MatchCardAssembly` 每次新建，失败即丢弃）。轮换时用"最后一次成功的包集合"缓存重装；期间目录变了就用新的，失败则按缓存回退一次（S10）。
  - 卡组校验不再依赖预装池：卡组规则校验前移到管理器批准（S4）。
  - **进程启动只建房间，不装卡池**：删掉 `MatchCardAssembly.Register` 那次预装（`ServerBootstrap.cs:59`），只校验"包索引可构建"。
  - **boot 段固定四步**：① `Sandbox.EnsureScaffold()`（生成 `<SandboxRoot>/pack/…`，幂等；顺带做一次"manifest 可解析"自检，失败**不阻断 boot**，只记日志——由开局剔除降级兜住）；② `BuiltInPlugin.RegisterProcessDefaults()` ＋ `FoundationActions.Register()`（进程级注册前移，见刻意变化第 10 条）；③ 建 lobby 房间 ＋ 2 个空 seat；④ 起监听并回报就绪行。**boot 段不装任何卡池。**
  - **seat 由 spawn 配置决定形态**：`seats: [{kind:"ai", packages:[], deck?}, {kind:"agent"}]`。AI seat **配置即视为已批准**（无需连接、无需管理员再批一次）；agent/human seat 留空等 `attach`。空 seat 不能开局（引擎 1v1 需要两个 controller）。
  - **开局装配顺序固定**：并集校验（管理器）→ 按并集装配本局卡池 → 生成本局 AI 卡组 → 建局。`AiDeckGenerator` 因此从"boot 用预装池构造的进程级对象"改为"**每局用本局已装配卡池构造的临时对象**"；AI 显式给了 `deck` 时跳过生成但仍用本局卡池做一次合法性校验。
  - **轮换后 AI seat 自动重建**：按 spawn 时的 AI 配置重新入座、重新从新池生成卡组（AI 没有"人"来重新申请；不自动重建则轮换必然缺 seat，管理员每局都得手动补）。换 AI 配置属"重配实例"，不是"轮换"。
  - **`HeadlessContext` 整体退役**（见"刻意改变的行为"第 7 条）：boot 后只有空 seat，AI seat 由"spawn 配置 ＋ 轮换重建"填充。
  - **生命周期归属**（U7 定稿）：`SerialWorkQueue`＝进程级（长存）；`ServerHost`（TCP/会话表）＝进程级（长存，`_service`/`_room` 改为可换引用）；`McpService`（含 `MatchExecutor`/`TargeterBridgeAdapter`/`SegmentCache`/工具表）＝房间级（销毁重建）；`IRoomControl`＝房间级（薄适配，随 `McpService` 重建）。**不把 `McpServiceOptions.MatchId` 改成可变属性**——那会让"选项"失去意义。
  - **轮换严格顺序**（两条不可交换：2 在 3 之前、7 在 9 之前）：
    1. 终局检测，`Match` 标 `Ended`；
    2. `SerialWorkQueue` drain：拒绝新入队（回 `room-rotating`），等已在途项完成（给上限，超时也**让它完成**，不强杀）；
    3. 销毁旧 `McpService`（连带 `MatchExecutor`/`Bridge`/`SegmentCache`/工具表/`RoomControl`）；
    4. `Detach` 并释放旧 `Match`；
    5. 建新 `Room`（新 roomId、2 个空 seat、新令牌生命周期）；
    6. 建新 `McpService`（`MatchId`＝新 roomId，注入新 `RoomControl`）；
    7. **原子交换** `ServerHost._service` / `_room`；
    8. **绑定 seat 的角色** → `Revoked`（**不含调试面**，见 S9），逐连接推 `notify/room.rotated { roomId, seatCount }`；
    9. drain 结束，队列恢复接单；
    10. 等各连接重新 `session.attach` 申请。
  - **Revoked 会话重新申请**不需要重新握手、不需要重拉 `tools/list`（描述符集合跨房间稳定，重拉幂等）；旧 `seatToken` 随房间终结失效；带旧 `roomId` 的调用以 `stale-room` 拒绝（不静默按新房间处理）。
  - 工具表随 `McpService` 重建实例，但**描述符集合内容不变**（只由 mode 与 `AllowDebugWrites` 决定，与房间无关）。
- Acceptance:
  - [ ] 进程启动不因"双方尚未声明卡包/卡组"而失败；启动后 lobby 房间的 seat 为空且可申请（R3）
  - [ ] 终局后服务器存活并产出新 lobby；旧房间保持 `Ended`（R3）
  - [ ] 同一实例内同时最多一个非终结房间（R4）
  - [ ] 启动时零卡池装载（断言 boot 阶段 `MatchCardAssembly.Register` 调用次数为 0）
  - [ ] 轮换全程 `ServerHost` 与 `SerialWorkQueue` 实例不变、TCP 不断（有断言）
  - [ ] drain 期间新请求回 `room-rotating`；轮换后带旧 `roomId` 的请求回 `stale-room`
- Rationale: 判据是"这东西的单线程语义属于谁"——队列是引擎单写者语义的唯一入口，随房间重建会在轮换瞬间产生两个消费者，等于短暂并发写引擎；而 `McpService` 被 `MatchId`、`RoomControl`、唯一 `_match` 三重钉在"一房间一局"上，按房间重建比给三个字段加可变性干净。工具表跟着重建但描述符不变，因此客户端缓存不失效——这是"重建实例、稳定契约"的分界。

### S6: 会话 / 角色 / 接入许可
- Status: Todo
- Target: `ServerHost.cs`（`ClientSession` → 连接对象 ＋ 角色状态机）、`Room/`（令牌改由批准签发）
- Approach:
  - **一个连接对象 ＋ 一个角色状态机**，不拆成多种连接类型。态：`Connected` / `Pending` / `DebugFace` / `PlayerFace` / `Viewer(seat)` / `Omniscient` / `Revoked`。
  - 迁移只允许**两方**触发：连接自己（申请 / 撤回申请 / 断线）、管理员（批准 / 拒绝 / 撤销）。**引擎与工具副作用不得改角色态**——`ApplySeatEffects`（`ServerHost.cs:759-781`）整条删除。
  - 能力（按态，逐态写死；**`room.info` 是显式例外**）：`Connected` 只有 `initialize`/`ping`/`session.attach` **＋ `room.info`**；`Pending` **只有** `ping`/`session.detach` **＋ `room.info`**，且**零推送**；`DebugFace`＝现有调试工具集（收不到对局推送）；`PlayerFace`＝该 seat 的动作工具；`Viewer(seat)`＝该 seat 脱敏推送；`Omniscient`＝全知推送且不含 controller 角色；`Revoked`＝无，可降回 `Connected` 重新申请。
  - **申请报文一条带齐**：`session.attach { roles, seat?, deck, deckConfig?, packages[] }`；`packages` 为 `{packageId,version,guid}` 引用数组。不新开"提交卡包"方法——管理员批准时必须同时看到"要哪个 seat、什么卡组、要哪些包"，否则得批两次。
  - `Pending` 拒绝码统一为 `not-admitted`，**不按工具区分**（避免"某些工具没拦住"）；申请被拒 → 回 `Connected` 让连接改声明重申请（不留在 `Pending` 挂 rejected 标记）。
  - "申请—批准两段"（源：父 agent 裁决）：R7 的"显式声明"＝连接的**申请**；CONTEXT「连接许可」的"以何角色接入"＝管理员的**批准**。调试面是唯一例外：**申请即生效**（判据仍是回环 ＋ 未绑座），但仍须显式申请。
  - **AI seat 的 controller 属玩家面**：进程内驱动、不占连接、不作为"调试面"；其 `{packages, deck}` 声明由管理员在 spawn 时代配置（`packages` 缺省空集）。并集判定**按 seat** 计，两个 seat 各有一份声明，空 seat 不能开局（U8）。
  - `connectionId` 由服务器在 TCP 接入时分配（沿用 `ClientSession.Id`），在 `initialize` 响应里返回；客户端不可自选；可附自由文本 `label`（不参与鉴权）。管理员侧：`connections.list` / `connection.admit|reject`。
  - 座位令牌：**由服务器在批准那一刻签发**，**经该连接自己的批准响应**下发（不经过第三方连接）；用途收窄为断线重连恢复；重连 `session.attach{token}` 跳过 `Pending` 直接恢复；令牌无效落回 `Connected`；随房间终结失效、不跨房间复用。
  - 房间终结：**绑定 seat 的角色** → `Revoked`（调试面不受影响），连接**不**自动断开（可留待下一房间）。
  - **U3 时间线的适用域**：`t9`"宽限超时 → 绑定释放、槽位空出"**只适用于 lobby 态**；对局态宽限到期是**自动判负**（S6b），不在局中释放绑定。
  - `tools/list` 与 Gate 判据：工具描述符增加 `RequiredRole`（`None|Controller|Viewer|Debug|Admin`），判定＝"连接角色集合 ⊇ 工具要求"；`SeatScopeRegistry` 保留但降级为"旧五类 → 新角色"的映射来源（仅供迁移与守卫测试），不再直接读 `session.Seat`。
- Acceptance:
  - [ ] 一个 seat 至多 1 controller、0..N viewer，各自独立断开（R5）
  - [ ] controller 与 viewer 绑定互不影响：任一断开不改变另一方能力（R5）
  - [ ] 兼任连接共享同一 seat 绑定，故其 viewer 恒脱敏；全知 viewer 必须不含 controller 角色（R7）
  - [ ] `Pending` 连接调动作/调试/`observe.*` 一律 `not-admitted`，且零推送（R6）
  - [ ] 非管理员不得自行绑定；批准即授权、撤销即收回（R5/R10）
  - [ ] 不存在"既绑 seat 又能调调试 API"的 controller（R6）
  - [ ] 申请声明缺失 `packages` 或 `deck` 时在批准处被拒，且原因可读（R3）
- Rationale: 角色若是一个"态"而不是"若干布尔字段的组合"，混合态在类型上就不存在（Constraint 的"不得出现混合态"由结构保证，而不是靠 Gate 逐条检查）；令牌经批准响应下发，是因为批准动作虽然由管理员发起，应答必须回到被批准的那条连接——否则"玩家如何拿到令牌"需要再开一条转达路径；卡包与卡组同报文，是因为二者共同决定"这一局能不能开"，拆两条必然出现"批了座位却发现包不对"。

### S6b: 连接中断与对局终止（R11）
- Status: Todo
- Target: `ServerHost` 心跳/宽限循环、`Match.cs`（`Abort`）、`MatchLifecycle`、`MatchEndReason`、`AdminApi.room.abort`
- Approach:
  - **lobby 态断线**：立即释放绑定与座位（语义不变，`LobbyDisconnect_ReleasesSeatImmediately` 保留）。
  - **对局态断线**：绑定保留（`seatToken` 仍有效），标记 `disconnected`；`DisconnectGrace`（默认 60s）**只在轮到该 seat 时推进**（保留现有语义）；到期 → 服务器代该 seat `Concede` → 对局结束 → 随后轮换，绑定随轮换释放。
  - **局中不释放绑定、不允许换 controller**：引擎 1v1 需要两个 `Player`，无"暂停"概念；该 seat 的 `Player` 已绑定卡组与局内不可变的卡池（R9），换人会出现"新人卡组与已装配卡池不匹配"。管理员要换人 ⇒ `room.abort` → 轮换 → 新人重新 `attach`。
  - **管理员终止＝无胜者终止**：`AdminApi.room.abort` → `Match.Abort()`（E1–E3）；终止后 `State=Ended`、`Winner=null`、`EndReason=Aborted`，其余收尾（停 AI、停宽限计时、写摘要）与正常终局一致。
  - **断线期间 viewer 完全不受影响**：绑座 viewer 继续收该 seat 视角、全知 viewer 继续收全知输出；只有对局终结才让 viewer 收到终局视图。
  - 边界：60s 内一直没轮到该 seat（对手长考／对手也断线）⇒ 宽限不推进、不判负。
- Acceptance:
  - [ ] lobby 断线立即释放座位；对局态断线保留绑定并在轮到该 seat 时计时（R11）
  - [ ] 宽限到期自动判负，对局进入 `Ended`（`EndReason=Concede`）
  - [ ] 局中无法换 controller（新连接无法接管未结束对局的 seat）
  - [ ] `room.abort` 后 `Winner=null`、`EndReason=Aborted`、`State=Ended`，双方投影 `outcome` 均为 `None`
  - [ ] controller 断线期间绑座/全知 viewer 的推送不中断
  - [ ] 未轮到断线方时不计时、不判负
- Rationale: 局中保留绑定是为了让"原 controller 重连即可恢复"成为唯一出路——一旦允许接管，就必须回答"新人的卡组/卡包与已装配卡池不匹配"这个问题，而卡池在局内不可变，这个矛盾无解。无胜者终止则补上了"管理员终止"在引擎里的合法出口，避免用代认输伪造胜者。

### S6c: 房间态的信息可见性（R12）
- Status: Todo
- Target: `RoomContracts.cs`（`RoomSnapshot`/`RoomSeatSnapshot` 按角色构造）、`RoomTools.room.info`、`SeatScopeRegistry`、`ServerHost` 推送
- Approach:
  - **房间态也是视角**：能看"谁坐了哪儿"，永远看不到"对手带什么"。
  - `room.info` 的 `RequiredRole = None`——`Connected` 与 `Pending` 都可调（S6 已标为例外）。理由：未申请者要先看到空位才能决定申请；`Pending` 者要确认自己的申请还在。
  - 快照按**调用者角色构造**（不是"构造全量再裁剪"，后者新增字段必漏）：
    - 公开：`seat`、`occupied`、`state`（`idle|pending|admitted|disconnected`）、`displayName`；
    - 只给自己：自己的 `deck` 与 `packages`（引用形态）；
    - 永不公开：对手的 `deck`/`packages`——**全知 viewer 也不给**（全知是对局输出，不是构筑隐私）；
    - 全量：调试面（管理员本就进程内直读对象，不走快照）。
  - `displayName` 判定为**公开**；"匿名对手"是新需求，本轮不做（不要用"可空＝匿名"偷偷实现）。
  - 新快照**不保留** `PackPolicy`/`PackFingerprint`（已在刻意变化第 6 条删除）。
  - 新增极简推送 `notify/room.seats`：`{ roomId, seats:[{seat,occupied,state,displayName}] }`，**不含 deck/packages**；触发＝任一 seat 状态迁移（`idle→pending→admitted`、`admitted→disconnected`、`admitted→revoked`）。它与被删的 `notify/room` 不冲突：删的是"整份快照推送"（会形成两个真源），加的是"座位状态 delta 事件"。
  - 申请已占用座位 ⇒ `attach` **同步**返回 `seat-taken`，不进 `Pending`。
- Acceptance:
  - [ ] `Connected`/`Pending` 可调 `room.info`（R12）
  - [ ] 玩家只能看到自己的 `deck`/`packages`；对手的永不出现在载荷里（R12）
  - [ ] 全知 viewer 看不到任何 `deck`/`packages`；调试面可见全量（R12）
  - [ ] seat 状态迁移触发 `notify/room.seats`，载荷不含 deck/packages（R12）
  - [ ] 申请已占用座位同步返回 `seat-taken`，不产生 `Pending` 记录（R12）
- Rationale: 按角色构造快照而不是事后裁剪，是因为"新增字段"是未来的常态，裁剪式写法每加一个字段都要记得补一刀；`notify/room.seats` 只表达 delta，因此不会重新引入"快照与推送不同步"的第二真源——它与 `room.info` 不是同一类东西（一个是状态事件，一个是查询结果）。

### S7: 输出分发（viewer 侧）
- Status: Todo
- Target: `ServerHost` 推送链路（`PushViewsAsync:986-1007`、`DrainSegments:1016-1062`）、`McpService.Projection.cs`、`ObserveTools`
- Approach:
  - 分发判据从 `session.Seat is not null` 改为角色态：`Viewer(seat)` → 单视角帧；`Omniscient` → 全知帧；`Pending`/`Connected`/`DebugFace` → 不推对局数据。
  - **通道不新增**：全知与绑座走同样两条 `notify/view` ＋ `notify/segment`；差别只在帧形状与视角标识。
  - 单视角帧：`{kind:"view.updated", perspective:{mode:"seat", seat:N}, view:{UiMatchView}, actions:{...}}`。
  - 全知帧：`{kind:"view.updated", perspective:{mode:"omniscient"}, views:[{seat:0,view,actions},{seat:1,view,actions}]}`——实现就是调两次 `provider.Project(m, m.Players[i])` 各配一份 `ComputeActions` 后打包；`UiMatchView` / `Orc.UI.Contracts` / 投影提供者**不动**。
  - 段：全知走 `EventSegmentJson.Serialize(segment, indented)`（`EventSegmentJson.cs:21`，不传 visibility）；帧同样带 `perspective`。
  - 客户端区分只看 `perspective.mode` / `perspective.seat`，**不做形状嗅探**（"有没有 `views` 字段"不算判据）——写入接入文档。
  - 全知合法性只由管理员批准的拓扑决定，`ServerMode` 不再参与输出面判定（见"刻意改变的行为"第 1 条）。
  - `observe.view` 保留为"重连拉全量"的拉取面，返回与推送**同形**的帧：绑座 → 单视角帧；全知 viewer → 全知帧；调试面 → 现有摘要（`players[]` 含双方手牌，供诊断中间态）；`Pending` → `not-admitted`。
  - 同一批段**只取一次**（引擎段取用是单消费者），按 viewer 各自导出；同一 seat 的多个 viewer 复用同一份计算结果；多一个全知 viewer 不得让两名玩家的帧变大。
- Acceptance:
  - [ ] 绑 seat 的 viewer 投影与段不含对手手牌明细（R7）
  - [ ] 不绑 seat 的 viewer 收到 `perspective.mode="omniscient"` 的帧，含双方手牌明细（R7）
  - [ ] 同一 seat 多 viewer、以及 controller/viewer 各自断开互不影响（R5）
  - [ ] play 模式下全知 viewer 合法（不再被 `ModeDenied` 拒绝）
- Rationale: 复用"两份 seat 视角打包"而不是新造无视角视图——`UiMatchView` 的字段天然是 self/enemy 相对的，无视角变体等于重写投影契约与 Godot 渲染，收益只是少传一半字节；保留 `actions` 是因为 `ComputeActions` 现成，且教练视图需要高亮"选手现在能打什么"。

### 刻意改变的行为（非回归，评审时不得当作 bug）

| # | 变更 | 依据 | 位置 |
|---|---|---|---|
| 1 | 删除"play 模式拒未绑座读观察面"分支：全知改由拓扑授权，`ServerMode` 收窄为"是否注册调试类工具" | R7（全知合法性不受运行模式限制） | `ServerHost.cs:671-683`（含 `:677-681` 的 `ModeDenied`） |
| 2 | `notify/view` 载荷**移除**扁平标量兼容字段（`turn`/`selfHandCount`/`enemyHandCount`/`selfKredits`/… 见 `McpService.Projection.cs:52-64`）：帧形状只保留 `perspective` ＋ `view`/`views` ＋ `actions` 一套真源 | R8（破坏性变更）＋ 避免两套真源 | `McpService.Projection.cs` |
| 3 | 退役 `observe.view {panel:true}` 参数旁路：视角由角色决定，不由参数决定 | R8 | `ObserveTools` |
| 4 | 删除 `ApplySeatEffects`：角色不再由工具副作用扩张 | CONTEXT「会话」＋ R5 | `ServerHost.cs:759-781` |
| 5 | 删除 `ServerBootstrap` 启动时的卡池预装（`MatchCardAssembly.Register(options.PackDirectories)`）：**连带影响**＝`Room` 的卡组校验不能再用预装卡池，必须改走管理器批准时的校验（`DeckRulesValidator` 的卡池来源随之改变） | R3（启动不预装）＋ Constraint（资产注册不可撤销） | `ServerBootstrap.cs:57-64`、`DeckRulesValidator` |
| 6 | 删除 `Room.PackFingerprint` / `RoomSnapshot.PackPolicy` 等**整机口径**字段：卡包变成 per-match 声明，"整机同一份卡包"这个概念不复存在（指纹若仍需要，只能重新定义为"本局并集指纹"） | R3（seat 级包声明） | `RoomContracts.cs:48-54`、`ServerBootstrap.ComputePackFingerprint` |
| 7 | **删除 `HeadlessContext` 整个类**。四项能力的承接者：① boot 预置座位 → spawn 的 `seats` 配置 ＋ 轮换自动重建；② `agent.token` 文件 → 令牌只能由批准签发并经该连接应答下发（S6）；③ `--wait-agent` → 管理器侧计时，不绑在服务器进程上；④ `--exit-on-end` → 退役，服务器长存（R1/R3）。同时**从 CLI 参数表删除** `--headless`/`--run-dir`/`--seat0`/`--seat1`/`--wait-agent`/`--exit-on-end`/`--keep-alive`/`--log-seat-views`（不保留"已弃用但可用"，否则"服务器长存"会出现例外） | R3（boot 空 seat）＋ R1（长存）＋ S6（令牌只能批准签发） | `Headless/HeadlessContext.cs`、`Program.cs:19-23`、`:81-115` |
| 8 | `HeartbeatTimeout` 判据从"45s 无**请求**"改为"45s 无任何**入站帧**（含 `ping`）"：原判据会把长期停在 `Pending` 的连接误判为死连接 | 协议面（attach 立即 pending）＋ R6（Pending 可长期存在） | `ServerHost` 心跳循环、`ServerOptions.HeartbeatTimeout` |
| 9 | 删除 `notify/room` 通知：房间快照改由 `room.info` **拉取**，推送它等于制造"快照与推送不同步"的第二真源（座位状态 delta 另由 `notify/room.seats` 承载，见 S6c） | 协议面（7 条通知清单） | `ProtocolMethods.NotifyRoom`、`ServerHost.BroadcastRoom` |
| 10 | **进程级注册时点前移**：`BuiltInPlugin.RegisterProcessDefaults()` 与 `FoundationActions.Register()` 从 `McpService.CreateMatchAsync`（每局都调）迁到 **boot 段**（紧随脚手架、早于第一个 `McpService`），并从 `CreateMatchAsync` **删除**（避免两处都调的双真源） | 沙盒脚手架改由 boot 生成 | `McpService.MatchLaunch.cs`、`SandboxTools.CreateAsync:69-71` |
| 11 | **退役 `sandbox.create` / `sandbox.dispose`**（不留别名）：建局/毁局只走管理面 `match.start` / `room.abort`；其四项附带职责改由 ① 脚手架→boot `Sandbox.EnsureScaffold()`（幂等）② 进程级注册→boot 段（第 10 条）③ 沙盒包卡定义装载→开局装配 ④ 建局→管理面承接。`Orc.Mcp/Docs/sandbox/workflow.md` 的"先 create 再写入"流程整段作废，改为"boot 已就绪 → 直接 write → 开局即可用" | R10（房间生命周期归管理员）＋ R8 | `SandboxTools.cs:21-40`、`Docs/sandbox/workflow.md:20` |
| 12 | **`card.write` / `effect.write` 入串行队列**：它们只写磁盘，但写的是"装配输入"，与开局装配同队列才能真正互斥（不另设磁盘锁）。**反直觉且改变现有 sandbox 工具的延迟特征**（写文件也要排队），这是刻意的取舍；读类（`card.list`/`pack.info`/`file.read`）不得入队 | R10（共用同一串行队列） | `SandboxTools`、`CardTools`、`EffectTools`、`SerialWorkQueue` |

### S8: 对局设施归属 seat
- Status: Todo
- Target: `Room/SeatBinding`（新增值类型）、`ServerHost`（`CurrentEpoch` / `CurrentService`）、`McpService`（`CurrentMatch` 解析）、`Room`
- Approach:
  - **seat 不持活对象**：`SeatBinding { int Epoch; int PlayerIndex; }`；所有设施访问走 `host.CurrentService.CurrentMatch` ＋ `PlayerIndex` 解析。既有的"活对象隔离"约束（`IRoomControl` 入参出参不得出现 `Match`/`Player`）保持不变。
  - **失效判定＝纪元不匹配 或 当前无对局**：`IsLive = (binding.Epoch == host.CurrentEpoch) && host.CurrentService.HasMatch`；`Epoch` 在 S5 第 7 步那次原子交换的**同一个动作**里递增。不用"引用置空"、不用显式 `Dispose`、不用 GC 可达性。
  - **失败形态**：用旧 `SeatBinding` 解析设施 ⇒ 返回 `stale-seat`，**不返回旧对象**。
  - `seat.Index ↔ PlayerIndex` 在 `SeatBinding` **构造处断言**（恒等式，越早失败越好），`Room.BindSeat` 再断言一次（防御未来有人想支持错位映射）；测试夹具不必逐条断言。
  - **轮换清理清单**（按 S5 步序落位；"停失败的现象"就是验收断言名）：

| 持有者 | 停在哪一步 | 由谁停 | 停失败的现象（可观测） | 断言名 |
|---|---|---|---|---|
| 在途动作（`MatchExecutor._active`） | 第 2 步 drain | 队列等待自然完成 | 新局第一个动作被 `pending-targeting` 拒；`IsBusy` 恒真 | `NewMatch_FirstActionNotBlockedByOldInFlight` |
| AI 驱动循环 | 第 2 步末（drain 后、销毁前） | 取消 `_aiCts` | 旧 AI 继续调 `BeginAttackAsync`，作用到**新** `Match` ⇒"幽灵动作" | `NewMatch_NoGhostActionsFromOldAiDriver` |
| 宽限计时器 | 第 2 步末 | 取消计时 ＋ 清 `_disconnectedSeats` | 旧计时器对新房间 seat 触发 `Concede` ⇒ 新局刚开局即判负 | `NewMatch_NotConcededByOldGraceTimer` |
| `TargeterBridgeAdapter` 的 pending TCS | 第 3 步（销毁前） | `FailAll("room-rotated")` 收束 | 客户端收到永不完成的 `notify/target.request`；动作 Task 永久挂起 | `OldPendingTargeting_FailsWithRoomRotated` |
| `SegmentCache` | 第 3 步（随 service 销毁） | 无（纯缓存） | 新局第一帧带上旧局的段 | `NewMatch_FirstSegmentDoesNotContainOldMatch` |
| 推送闭包／会话写队列待发帧 | 第 8 步（推 `notify/room.rotated` 之前） | 清空待发队列，只保留终局帧与轮换通知 | 客户端先收旧局 `notify/view` 再收 `room.rotated` ⇒ 画面回跳 | `NoStaleViewFramesAfterRoomRotated` |
| `SerialWorkQueue` 排队项 | 第 2 步 drain | 拒绝新入队 ＋ 等待既有 | 新工作项拿到旧 service ⇒ `stale-room` | `NewWorkItem_NeverSeesOldService` |

  - 两条顺序约束与 S5 一致且同样不可交换：**drain 先于销毁**、**引用交换先于恢复队列**。
  - **宽限计时器回调必须自检 `match.State == Ended` 后直接返回**，不能只依赖取消令牌的时序（取消与回调可能同时发生）。
- Acceptance:
  - [ ] 房间终结后旧 `SeatBinding` 的 `IsLive == false`，且用它解析设施返回 `stale-seat` 而非旧对象（R5）
  - [ ] 上表七条断言全部成立（R5 新增验收"轮换旧持有者全部停止"）
  - [ ] `SeatBinding` 构造即断言 `PlayerIndex == seat.Index`
  - [ ] 房间终结不需显式 `Dispose` 任何引擎对象即满足"设施失效"
- Rationale: 一旦 seat 持有 `Player`，它就成为"跨轮换的可达路径"，失效只能靠"记得置空"——这是最不可靠的一类不变量。纪元判定是纯数据比较，可断言、可测试；而"是否泄漏内存"这种说法写不成断言，只能靠评审肉眼，因此清理清单统一用"停失败会看到什么现象"来表达验收。

### S9: 调试面 controller
- Status: Todo
- Target: `SeatScopeRegistry` / `ServerHost.Gate` / `McpServiceMode` / `SandboxTools` / `Sandbox`
- Approach:
  - 调试面＝不绑 seat 的 controller，**与房间解耦、跨房间长存**：轮换只把"绑定 seat 的角色"打回 `Revoked`，`DebugFace` 保持（S5 第 8 步已按此修正）。
  - 判据零变更：回环 ＋ 未绑座 ＋ 写类工具（R6）；申请即生效（`session.attach {roles:["debug"]}`）。
  - **能力按两层切**：**资产层**（`card.write`/`effect.write`，只写 `<SandboxRoot>/pack/`，lobby 可用）；**对局层**（`card.load` 装进运行中 `Match`、`sandbox.*` 塑造，需活跃对局，无对局回 `not-ready`——沿用现有码，不新造）。lobby 态**不**为 `card.load` 造"预装载缓存"。
  - 解析路径固定为 `连接 → host.CurrentService → 当前 Match`（S5 第 7 步原子交换后自然指向新栈）；**不**把调试面绑定到 attach 时的房间。
  - 调试产物落在磁盘沙盒包，随"沙盒包强制入并集"进每一局卡池；`sandbox.reset` 是唯一清空手段（删除并重建脚手架），不是开局失败的救命手段（开局失败走剔除重试）。
  - `AllowDebugWrites` / `SandboxRoot` 载体改为 spawn 参数；服务器进程不再解析这两个开关。
  - `McpServiceMode` 职责收窄为"是否注册调试类工具"（输出面判定已删，见刻意变化第 1 条）。
- Acceptance:
  - [ ] 现有调试判据（回环 ＋ 未绑座 ＋ 写类工具）零变更（R6）
  - [ ] ≥2 个调试面 controller 并存互不影响，任一断开不影响其余（R6）
  - [ ] 玩家面 controller 调调试 API 被拒、原因可读（R6）
  - [ ] 轮换后调试面仍是 `DebugFace`，无需重新 attach；`sandbox.*` 在轮换窗口回 `not-ready`，开局后恢复并作用于新 `Match`
  - [ ] lobby 态 `card.write` 可用、`card.load` 回 `not-ready`
  - [ ] 沙盒包被写坏时开局剔除重试成功；`sandbox.reset` 后沙盒包为空且服务器未重启
- Rationale: 调试面的生命周期由"它是谁"决定（不绑 seat 的连接），不由"它在哪个房间"决定；而它调用的 API 必须指向当前服务栈，否则轮换后每次都要重新挂接一遍。把能力切成"写盘 / 进引擎"两层，是为了让 lobby 态仍有可做的事，同时不为 `card.load` 发明一个"预装载缓存"这种新机制。

### S10: 卡池重装（轮换点生效）
- Status: Todo
- Target: 服务器卡池装配链路（`MatchCardAssembly.Register` 的调用点）＋ 房间轮换窗口
- Approach:
  - **没有"待生效队列"**：卡池按房间构建（S5），"提交变更"＝改磁盘包目录，下一次开局自动用新内容；轮换点天然就是生效点（R3/R9）。
  - **回退**：服务器缓存**最后一次成功的包集合 ＋ lock**；本局装配失败时用它重试一次，仍失败则拒绝开局（房间留 lobby、进程存活、不进半装载态）。`MatchCardAssembly` 每次新建，失败即丢弃，不触碰进程级静态面。
  - **可观测**：装配结果（成功时的包集合与资产数、失败时的包/资产与原因）经通道事件上报管理器，并经调试面的 `sandbox.state` 可读；**不进 `room.info` 快照**（S6c 已把 `PackPolicy`/`PackFingerprint` 删除，避免两处表达同一事实）。
- Acceptance:
  - [ ] 不重启进程即可生效（R9）；有活跃对局时本次变更不打断对局，只影响下一房间（R9）
  - [ ] 装配失败时回退到最后一次成功卡池，仍失败则拒绝开局且不进入半装载态（R9）
  - [ ] 结果可观测（哪些包/资产被重装、哪些失败及原因）
- Rationale: "按房间构建"已经把重装点唯一化，若再引入待生效队列，就会出现"队列里排着的变更"与"磁盘上的当前内容"两份真源；回退之所以缓存"最后一次成功的包集合"而不是"上一次的卡池对象"，是因为 `AssetRegistry` 不可撤销、无法在原地恢复旧卡池，只能按旧集合重装一次。

### S11: 协议面破坏性改造与入口/测试同步
- Status: Todo
- Target: `Orc.Protocol`、`Orc.Client`、`Orc.Mcp.Bridge`、`sandbox-server-ctl.py`、`start-sandbox-server.bat`、`客户端接入指南.md`、`tests/Orc.Server.Tests`
- Approach:
  - **请求方法 6 个**（父 agent 答复标题写"5 个"，以清单为准）：`initialize`（版本协商 ＋ **唯一返回 `connectionId` 与 `label`** 处）、`ping`、`session.attach`（申请角色＋seat＋deck＋deckConfig＋packages，**立即返回 `{state:"pending"}`**）、`session.detach`、`tools/list`（按角色过滤）、`tools/call`。`hello` 不是方法名，新协议里不出现。
  - **通知 7 条**：保留 `notify/view`、`notify/segment`、`notify/target.request`、`notify/target.expired`；新增 `notify/admission`（`decision:"admitted"|"rejected"|"revoked"`）、`notify/room.rotated`、`notify/room.seats`（座位状态 delta，见 S6c）；**删除 `notify/room`**（整份快照只经 `room.info` 拉取）。
  - **不做长等**：attach 不算"批准等待"，服务端**不设 attach 超时**；`Pending` 清理交由管理员按 `connections.list` 的年龄决定，服务端不自动踢。
  - 心跳改为"无入站帧"判据（见"刻意改变的行为"第 8 条），保证 30 分钟 `Pending` 不会被误杀；客户端继续 30s `ping`。
  - `OrcClient`：`initialize` 后保存 `connectionId`；`attach` 按"立即 pending ＋ `notify/admission` 到达"改造，**不得**为等批准而让请求 TCS 长期挂起（现有"断线后请求失败而非挂起"的修复必须保持）。
  - `Orc.Mcp.Bridge`、`sandbox-server-ctl.py`、`start-sandbox-server.bat` 一律按新链路重写：启动走管理器（`manager.spawn`）、接入走 `attach` ＋ 管理员批准、不再有 `--packs/--headless/--exit-on-end` 等服务器参数。
  - 客户端接入文档时序图按"**attach 立即 pending → notify/admission**"画，不得画成同步批准。
- Acceptance:
  - [ ] 新协议面无 v1 兼容分支/翻译层（R8）
  - [ ] 客户端、桥、脚本、Agent 操作手册全部有对应新用法
  - [ ] 全量测试 0 新增失败，不新增跳过/禁用（基线 2395 通过 / 2 既有红）
  - [ ] `Pending` 连接在 30 分钟无动作（仅 ping）后仍存活，收到 `notify/admission` 后即可用
- Rationale: 立即 pending 而不是长等，同时绕开 TCS 超时、心跳判活、"管理员不在场时客户端以为请求丢了"三个坑；`connectionId` 放在 `initialize` 而不是 attach，是因为管理员必须能指代"还没申请角色的连接"；删 `notify/room` 是因为房间快照已有拉取面，再推一份必然不同步。
