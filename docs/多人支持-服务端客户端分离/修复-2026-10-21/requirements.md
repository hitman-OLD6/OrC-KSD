# Requirements — 多人支持安全与契约修复（2026-10-21）

> 状态：**需求已定稿**（用户 2026-10-21 明确「全面修复」）。
> 依据：`../代码审查与测试审查报告.md`（P0×3 / P1×5 / P2×12 / 文档偏差 8 项）。
> 目标：让多人协议面达到"可上线"的安全与契约水位；**不引入新玩法、不改引擎规则、不重构传输层**。

## 一句话

补上「会话 → 座位」的授权层，补齐推送闭环与客户端心跳，修掉审查报告中的全部 P0/P1/P2，并用测试把每条修复钉死。

## Scope

- Includes：
  - P0×3：跨座位视图越权与会话改绑、动作归属可伪造、房主身份可抢占。
  - P1×5：AI/断线判负不推送、断线宽限永不判负、客户端无心跳、房间态无推送、`ready` 无语义。
  - P2×12：队列 drain、订阅者异常隔离、数字 id、`IsConnected`、pending 泄漏、NDJSON 限长、SocketException、锁内枚举网卡、DeckRules 张数、注释失真、选靶异常传播、claim 空卡组。
  - 测试：新增安全/推送/心跳三组负面用例；修 4 处恒真断言与 1 处假覆盖。
  - 文档：同步 `客户端接入指南.md`、`requirements.md` 中与实现不符的声明。
- Excludes：
  - 引擎规则、卡牌数值、投影字段语义（`view`/`actions` 结构不变）。
  - 协议版本号升级（仍为 `1.0`；本批是修复，不是新能力）。
  - WebSocket、观战、多桌、账号体系。
  - `McpService` 单机/agent 场景的 `observe.view {seat}` 能力（保持可用）。

## 关键设计约束（已定）

1. **授权层放在 `ServerHost`，不下沉到 `McpService`**。`McpService` 仍服务单机 MCP/agent（无会话概念）；`ServerHost` 是唯一知道「谁坐在哪」的地方。
2. **`session.Seat` 只有一个写入点**：`room.claim` / `room.resume` 成功。其它任何工具返回值都不得改绑。
3. **未绑定座位的会话**（只连上、未 claim）只能调用只读且无座位语义的工具；动作类一律 `unauthorized`。
4. **`kind:"local"`（房主）仅允许回环来源**；非回环 claim local 直接 `unauthorized`。
5. **`HostSlot() == null` 时 `room.start` 必须 `unauthorized`**（不能免令牌开局）。
6. **推送必须在串行队列消费者上下文内产出**（`ProjectView` 依赖只读执行面），因此 AI 步骤与断线判负的推送要包进同一个 queue work。
7. **心跳语义**：静默 ≥ `HeartbeatInterval`（默认 30s）发 `ping`；发出后 `HeartbeatTimeout`（默认 15s）内无任何入站帧 → 判定断线并失败所有挂起请求。
8. **不改变现有错误码集合**，复用 `unauthorized` / `busy` / `invalid-arguments` 等。

## 修复项（可验收）

### F1 会话座位绑定与跨座位隔离（P0-1）

- 现状：`observe.view {seat}` 任意可读；`BindSeat` 无条件改绑。
- 期望：
  - `ServerHost` 仅在 `room.claim` / `room.resume` 成功时写入 `session.Seat`。
  - 已绑定会话调用 `observe.view` 时，`seat` 参数被忽略（强制会话座位）；显式传其它座位返回 `unauthorized`。
  - 未绑定会话调用 `observe.view`：允许（单机诊断），但不得因此绑定座位。
- 验收：座位 0 会话 `observe.view {seat:1}` → `ok=false, code=unauthorized`；随后推送的 `notify/view.seat == 0`。

### F2 动作归属强制（P0-2）

- 现状：`owner` / `seat` 参数由客户端指定，无校验。
- 期望：`ServerHost` 对动作类工具做统一门禁：
  - `match.concede` / `mulligan.*` / `play.*` / `command.*` 的 `owner` 必须等于 `session.Seat`。
  - `target.submit` / `target.cancel`：`pending.Seat` 必须为 `session.Seat`（`pending.Seat == null` 时按先到先得，但只有已绑定会话可提交）。
  - 未绑定会话调用动作类 → `unauthorized`。
  - `match.end_turn` / `play.unit_pre` 等无 owner 参数的工具：要求已绑定座位（引擎自行判当前方）。
- 验收：座位 0 调 `match.concede {owner:1}` → `ok=false, code=unauthorized`，对局未结束。

### F3 房主身份约束（P0-3）

- 期望：
  - `kind:"local"` 的 claim 仅接受回环远端地址；否则 `unauthorized`。
  - `HostSlot() == null` 时 `room.start` 返回 `unauthorized`（提示"房间没有房主"）。
  - 房主 lobby 断线仍按现行为释放座位（保留 `LobbySeatRetention` 语义），但释放后房间**不可开局**，需新房主重新 claim local。
- 验收：非回环来源 claim local → `unauthorized`；无 local 座位时 start → `unauthorized`。

### F4 推送闭环（P1-1）

- 期望以下三处在状态变更后推送 `notify/view` + `notify/segment`：
  1. AI 每一步实际执行后（`Performed == true`）。
  2. 断线宽限判负成功后。
  3. 选靶超时（`PendingExpired`）导致动作按取消推进后。
- 约束：推送必须在 `SerialWorkQueue` 的消费者上下文内执行（投影需要只读执行面）。
- 验收：AI 完成一回合期间，客户端收到的 `notify/view` 数量 ≥ AI 实际动作步数；判负后收到 `phase:"over"` 的 view。

### F5 断线宽限时序（P1-2）

- 期望：断线记录只在**真正认输成功后**移除；若断线方尚未轮到自己，保留记录等待下一轮检查。
- 验收：断线发生在对手回合 → 对手结束回合后，宽限到期即判负（`phase == "over"`）。

### F6 客户端心跳（P1-3）

- 期望：`OrcClient` 内置空闲计时器；静默 ≥ `HeartbeatInterval` 发 `ping`；`HeartbeatTimeout` 内无入站帧则关闭连接、`FailAllPending`、`IsConnected=false`。
- 默认值改为文档口径：`HeartbeatInterval = 30s`、`HeartbeatTimeout = 15s`。
- 验收：服务端心跳超时设为 2s、客户端不主动发 ping 时，`CallAsync` 在超时后抛 `IOException`（不是 `TimeoutException`）；正常对局空闲 40s 不掉线。

### F7 房间态推送与 ready 语义（P1-4 / P1-5）

- 期望：
  - `room.claim` / `release` / `kick` / `add_ai` / 断线释放后，向所有会话广播 `notify/room`（载荷＝`room.info` 的 details）。
  - **删除 `seats[].ready` 字段**（无就绪语义，避免误导）；`RoomSeatSnapshot` 与文档同步移除。
- 验收：客人 claim 后，房主在 1s 内收到 `notify/room` 且 `seats[1].controllerKind == "remote"`。

### F8–F19 P2 收敛

| 编号 | 内容 | 验收 |
|---|---|---|
| F8 | `SerialWorkQueue.DisposeAsync` drain 队列并 fail 所有排队项 | 关闭时排队请求抛 `ObjectDisposedException` 而非挂起 |
| F9 | `NotificationReceived` 逐订阅者隔离异常 | 一个 handler 抛异常后，连接仍可用、其余 handler 仍被调用 |
| F10 | 客户端容忍数字 `id`（不抛异常，未知 id 忽略） | 发送 `{"id":1,...}` 的响应不导致读循环退出 |
| F11 | `IsConnected` 纳入 `_closedReason` | 服务端关闭后 `IsConnected == false` |
| F12 | `CallRawAsync` 写失败清理 pending 条目 | 写失败后 `_pending` 不残留 |
| F13 | `NdjsonReader` 增量限长（读到上限即抛，不先缓冲整行） | 发送 2 MiB 无换行数据 → 服务端断开且内存不随报文增长 |
| F14 | `ServerHost` 捕获 `SocketException` | 连接重置不产生未观察异常 |
| F15 | `Room.Snapshot()` 锁外枚举网卡 | 快照与座位状态一致且不持锁做 IO |
| F16 | `DeckRulesValidator` 使用 `ServerOptions.RequiredDeckSize` | 配置 30 张时，31 张卡组被拒 |
| F17 | `McpMatchLauncher` 注释与实现一致（不声称强制 true） | 代码注释如实描述 |
| F18 | `TargeterBridgeAdapter.DriveAsync` 异常传播为动作失败 | 选靶期间注入异常 → 动作返回 `engine-rejected`，不再永久 `busy` |
| F19 | `Room.Claim` 在无校验器时仍拒绝空卡组（已满足，补测试） | 空卡组 → `deck-invalid` |

### F20 测试补齐与去伪

- 新增 `SecurityTests`：F1/F2/F3 的负面用例（各 2–3 例）。
- 新增 `PushDeliveryTests`：F4/F5（AI 推送、判负推送、断线时序）。
- 新增 `ClientHeartbeatTests`：F6。
- 修 `EndToEndTests:77-79`、`ServerProcessTests:56`、`ResilienceTests:34-38/62-66`、`SkeletonSmokeTests:34-40`。
- 验收：全量测试 ≥ 2148 且 0 失败；新增用例在修复前必须失败（红→绿可回溯）。

### F21 文档同步

- `客户端接入指南.md`：§4.3 心跳、§5.2 `observe.view` 座位约束、§6.2 删除 `ready`、§8.5/§12 修正 `match.ended`、§11 路径、§14.1 推送方法。
- `requirements.md`：修正"心跳已实现"等与实现不符的表述。
- 验收：文档中每条契约都能在代码中定位到实现或测试。

## 非目标（明确不做）

- 不引入 TLS / 鉴权令牌体系（局域网先行，房主令牌即凭据）。
- 不做速率限制（除既有队列容量）。
- 不改变 `notify/view` / `view` / `actions` 的字段结构。
- 不为 `room.info` 增加访问控制（房间发现是产品需求）。
