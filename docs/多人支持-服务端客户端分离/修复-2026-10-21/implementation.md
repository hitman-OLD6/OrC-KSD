# Implementation — 多人支持安全与契约修复（2026-10-21）

> 状态：**实现 grill 完成**（S1–S15 全部落定，可交执行子 agent；Q1/Q2/Q5 已裁决，其余未决点降级为文末「残余风险」并给出默认取舍）。
> 需求真源：[`requirements.md`](./requirements.md)（F1–F21，逐条映射见下表）；缺陷证据：[`../代码审查与测试审查报告.md`](../代码审查与测试审查报告.md)。
> 基线：`dotnet test OrcEngine.slnx` → 2148 通过 / 0 失败（修复前）。
> 已裁决要点：四类座位作用域白名单 ＋ 默认拒绝（S1）；绑定不变量与 `UnbindSeat` 五点（S1）；`target.submit` 的 `pending.Seat == null` 六行裁决（S2）；三处推送的包裹形态 ＋ 执行器 `Settled` 事件（S5）。

## Scope

- Target：`src/Orc.Server/Transport/ServerHost.cs`（授权层 ＋ 推送闭环 ＋ 宽限时序）、`src/Orc.Server/Room/*`（房主约束、快照、卡组校验口径）、`src/Orc.Server/Execution/SerialWorkQueue.cs`（drain）、`src/Orc.Client/OrcClient.cs`（心跳 ＋ 读循环健壮性）、`src/Orc.Protocol/NdjsonFraming.cs`（增量限长）、`src/Orc.Mcp/Match/*`（选靶异常传播）、`tests/Orc.Server.Tests/*`（新增三组测试 ＋ 修 4 处弱断言）。
- Excludes：引擎规则、卡数值、`view`/`actions` 字段结构、协议版本号（仍 `1.0`）、WebSocket、观战/多桌/账号、`McpService` 单机/agent 场景的 `observe.view {seat}` 能力。

## 实际行为基线（交叉验证结论，供后续步骤引用）

| 位置 | 实际行为 | 与需求的关系 |
|---|---|---|
| `ServerHost.cs:327-357 BindSeat` | 任何 `Ok` 且 `details.seat` 为数值的工具返回都会写 `session.Seat`；`room.release` / `room.kick` / `room.add_ai` / `observe.view` 均返回该字段 | 需求约束 2 的违反面比报告所述更宽（不止 `observe.view`） |
| `ServerHost.cs:280-318 HandleToolsCallAsync` | 无任何座位校验；`IsMatchAction` 只用于决定「是否推送」 | F2 的落点 |
| `Room.cs:233-236 / :292-295` | `HostSlot()==null` 时 `StartAsync` 与 `Kick` **都**跳过令牌校验 | F3 只点了 `start`；`kick` 是同类旁路 |
| `ServerHost.cs:380-423 CheckDisconnectGrace` | 先 `TryRemove` 再判回合归属；不轮到时直接 `return false` | F5 |
| `ServerHost.cs:296-309` | `PushViewsAsync` 唯一调用点：`IsMatchAction && Ok` | F4 需补三个触发点 |
| `TargeterBridgeAdapter.cs:394-411` | `DriveAsync` 异常只写 `_fault`，`MatchExecutor` 从不读 → `_active` 永不完结、`IsBusy` 恒真 | F18 |
| `OrcClient.cs` | 无定时器、无 `ping`；`ClientOptions.HeartbeatInterval` 无人消费（默认 5s 与文档 30s 不符） | F6 |
| `ObserveTools.cs:189-203` | `seat` 直接取自调用参数 | F1 |

## 需求覆盖映射（F1–F21 → 步骤）

| 需求项 | 内容 | 落点 | 备注 |
|---|---|---|---|
| F1 | 会话座位绑定与跨座位隔离 | **S1**（绑定白名单/不变量）＋ **S2**（`observe.view` 分支） | 绑定面比报告更宽：`release`/`kick`/`add_ai` 也返回 `seat` |
| F2 | 动作归属强制 | **S1**（OwnerScoped/SeatRequired 分类）＋ **S3**（判定细节）＋ **S2**（`target.*`） | 含"未绑定即 unauthorized" |
| F3 | 房主身份约束 | **S4** | 并入 `Kick`/`Release` 的 `HostSlot()==null` 旁路 |
| F4 | 推送闭环（AI 步/判负/选靶超时） | **S5** | 第③处用执行器 `Settled` 事件 |
| F5 | 断线宽限时序 | **S6** | 与 S5② 的"成功后才移除记录"同处 |
| F6 | 客户端心跳 | **S8** | 默认值改 30s/15s |
| F7 | 房间态推送与 `ready` 语义 | **S7** | 删 `Ready` 字段 |
| F8 | `SerialWorkQueue` drain | **S10** | — |
| F9 | 订阅者异常隔离 | **S9①** | — |
| F10 | 数字 `id` 容忍 | **S9②** | — |
| F11 | `IsConnected` 纳入 `_closedReason` | **S9③** | — |
| F12 | 写失败清理 pending | **S9④** | — |
| F13 | NDJSON 增量限长 | **S12** | — |
| F14 | 捕获 `SocketException` | **S11①** | — |
| F15 | `Snapshot()` 锁外枚举网卡 | **S11②** | — |
| F16 | `DeckRulesValidator` 用 `RequiredDeckSize` | **S11③** | — |
| F17 | `McpMatchLauncher` 注释如实 | **S11④** | — |
| F18 | 选靶异常传播 | **S13** | — |
| F19 | `claim` 空卡组（已满足，补测试） | **S13**（测试）＋ **S14** | — |
| F20 | 测试补齐与去伪 | **S14** | `SecurityTests` 10 例 / `PushDeliveryTests` 5 例 / `ClientHeartbeatTests` ≥3 例 |
| F21 | 文档同步 | **S15** | — |

> 校验结论：F1–F21 全部有落点，无缺项；S1 是横切骨架（不单独对应某个 F 编号，但为 F1/F2 的前置）。
> 执行顺序：**S1 → S2 → S3 → S4**（授权层，必须先红后绿）→ **S5 → S6 → S7**（推送与房间态）→ **S8 → S9**（客户端）→ **S10 → S11 → S12 → S13**（健壮性收敛）→ **S14 → S15**（测试与文档）。

## Implementation Steps

### S1: 座位作用域分类表与统一门禁（授权层骨架）
- Status: Done
- Target: `src/Orc.Server/Transport/ServerHost.cs`（新增 `SeatScope` 枚举 ＋ `ToolScopes` 静态表 ＋ `GateAsync` 前置裁决；删除 `BindSeat`）、`ClientSession.Seat`（唯一写入点收窄）
- Approach:

  **① 分类为四类＋两个专门分支（决策：集中白名单 ＋ 默认拒绝 ＋ 守卫测试）**

  ```csharp
  internal enum SeatScope
  {
      Bind,         // 唯一写入 session.Seat 的工具（成功后绑定）
      OwnerScoped,  // 参数带 owner，必须 == session.Seat（属性：参数名 "owner"）
      SeatRequired, // 无 owner 参数，引擎自行判当前方；仅要求已绑定
      Agnostic,     // 房间/诊断/只读：不看座位，且严禁绑定
  }

  private static readonly FrozenDictionary<string, SeatScope> ToolScopes = new Dictionary<string, SeatScope>(StringComparer.Ordinal)
  {
      // ---- Bind（session.Seat 的唯一写入点）----
      ["room.claim"]        = SeatScope.Bind,
      ["room.resume"]       = SeatScope.Bind,

      // ---- OwnerScoped（owner 必须等于会话座位）----
      ["match.concede"]     = SeatScope.OwnerScoped,
      ["mulligan.begin"]    = SeatScope.OwnerScoped,
      ["mulligan.done"]     = SeatScope.OwnerScoped,
      ["play.unit"]         = SeatScope.OwnerScoped,
      ["play.join"]         = SeatScope.OwnerScoped,

      // ---- SeatRequired（仅要求已绑定）----
      ["match.end_turn"]    = SeatScope.SeatRequired,
      ["play.unit_pre"]     = SeatScope.SeatRequired,
      ["play.command_pre"]  = SeatScope.SeatRequired,
      ["play.command"]      = SeatScope.SeatRequired,
      ["play.counter"]      = SeatScope.SeatRequired,
      ["command.begin"]     = SeatScope.SeatRequired,
      ["command.move"]      = SeatScope.SeatRequired,
      ["command.attack"]    = SeatScope.SeatRequired,
      ["command.leave"]     = SeatScope.SeatRequired,

      // ---- Agnostic（房间/诊断/只读；严禁绑定座位，鉴权靠工具自身令牌）----
      ["room.info"]         = SeatScope.Agnostic,
      ["room.release"]      = SeatScope.Agnostic,
      ["room.kick"]         = SeatScope.Agnostic,
      ["room.add_ai"]       = SeatScope.Agnostic,
      ["room.start"]        = SeatScope.Agnostic,
      ["service.info"]      = SeatScope.Agnostic,
      ["service.tools"]     = SeatScope.Agnostic,
      ["docs.list"]         = SeatScope.Agnostic,
      ["docs.read"]         = SeatScope.Agnostic,
      ["play.can_play"]     = SeatScope.Agnostic,  // 纯读（按 ref 查可打出性）
      ["command.availability"] = SeatScope.Agnostic, // 纯读（按 ref 查指挥可用性）
  };

  // 专门分支：语义无法用四类表达，单独实现，但仍算"已登记"（守卫测试认可）
  private static readonly FrozenSet<string> SpecialCasedTools =
      new[] { "observe.view", "target.submit", "target.cancel", "target.claim" }.ToFrozenSet(StringComparer.Ordinal);
  ```

  **② `BindSeat` 删除**：调用侧改为 `ApplyBinding(session, toolName, result)`，仅当 `ToolScopes[toolName] == Bind && result.Ok && result.Details["seat"]` 为整数时转交 `BindSeat(session, seat)`（下 ②′）。Agnostic/SeatRequired/OwnerScoped 的成功返回**一律不改绑**（`room.release` / `room.kick` / `room.add_ai` / `room.start` / `observe.view` 是重点回归面）。

  **②′ 不变量与解绑（父 agent 裁决：双向收敛）**

  > 不变量：**一个座位最多绑定一个活动会话；一个会话最多绑定一个座位。**

  实现为三个单点方法（`session.Seat =` 全仓只出现在 `BindSeat` / `UnbindSeat` / `UnbindSeatOf` 内，且后两者只写 `null`）：

  ```csharp
  // 绑定（Bind 类工具成功后，且在 queue 消费者上下文内）
  private void BindSeat(ClientSession session, int seat)
  {
      UnbindSeat(seat, except: session);            // ⑤ 同座位其它会话解绑（半开连接 + 新连接 resume）
      UnbindSeatOf(session);                        // 一个会话最多一个座位（旧座位一并拆除）
      session.Seat = seat;
      _disconnectedSeats.TryRemove(seat, out _);
      _room?.MarkConnected(seat);
      // 本座位待决选靶重推（保留现行为）
  }

  // 座位消失：把绑到该座位的一切会话置空
  private void UnbindSeat(int seat, ClientSession? except = null)
  {
      foreach (var s in _sessions.Values)
          if (!ReferenceEquals(s, except) && s.Seat == seat) s.Seat = null;
  }

  // 会话改绑前：拆掉它的旧座位
  private void UnbindSeatOf(ClientSession session) => session.Seat = null;
  ```

  `UnbindSeat(seat)` 的**五个调用点**（缺一即漏洞，逐条列进验收）：

  | # | 触发 | 代码位置 | 说明 |
  |---|---|---|---|
  | ① | `room.release` 成功 | `ServerHost` 工具返回后 | 释放座位后原持有会话立即失去动作权与推送 |
  | ② | `room.kick` 成功 | 同上 | 被踢会话立即失去动作权 |
  | ③ | `room.add_ai` 成功 | 同上 | 仅当该座位存在悬空绑定时生效 |
  | ④ | lobby 断线 `DropSeat` | `MarkSeatDisconnected`（`room.State == Lobby`）分支 | 断线释放座位后，其它绑到该座位的会话一并解绑 |
  | ⑤ | Bind 类工具成功 | `BindSeat` 内首行 | 同座位其它会话解绑（不变量"一座一会话"） |

  **③ 门禁顺序固定（不可调换）**：

  ```
  解析 name/arguments
    → 分类：ToolScopes.TryGetValue(name) 命中四类，或命中 SpecialCasedTools
        未命中 ⇒ 直接返回 ok=false, code="unauthorized"（不解析参数、不入队、不碰引擎）
    → 座位校验（Bind 类跳过；Agnostic 跳过；OwnerScoped/SeatRequired/专门分支按各自规则）
        失败 ⇒ 直接返回 unauthorized（不入队）
    → _queue.EnqueueAsync(...)  →  执行 → ApplyBinding(仅 Bind 类) → 响应
  ```

  **④ 守卫测试（两道，缺一不可）**

  ```csharp
  // 守卫 1（正向）：每个对外宣告的工具都必须有归属，否则打印工具名并失败
  [Fact]
  public async Task EveryAdvertisedTool_HasSeatScopeRegistry()
  {
      await using var host = await ServerBootstrap.StartAsync(PlayerModeOptions());
      await using var client = await ConnectAsync(host.Port);
      var advertised = (await client.ListToolsAsync()).Select(t => t.Name).ToArray();

      var unregistered = advertised.Where(name => !ServerHost.IsToolClassified(name)).ToArray();

      Assert.True(unregistered.Length == 0,
          "以下工具未登记座位作用域（新工具必须显式登记到四类或专门分支）：" + string.Join(", ", unregistered));
  }

  // 守卫 2（反向）：表里不得有过期/拼错的工具名（否则会掩盖"默认拒绝"的实际失效）
  [Fact]
  public async Task SeatScopeRegistry_HasNoDeadEntries()   // 断言 ToolScopes.Keys ∪ SpecialCasedTools ⊆ advertised
  ```

  配套诊断探针：`ServerHost.IsToolClassified(string name)` 与 `ServerHost.SeatScopeOf(string name)`（`public static`，只读；不暴露表本身）。

  **⑤ 默认拒绝的行为守卫**（证明"未登记即 unauthorized"不是空话）：用 `McpService.AddTool` 临时登记一个假工具，经真 socket 调用 → 期望 `ok=false, code=unauthorized`；同一假工具在未绑定会话亦 `unauthorized`。

- Acceptance:
  - [ ] `tools/list`（Player 模式）中每个工具名都能在四类或两个专门分支中找到归属，找不到即测试失败并打印工具名
  - [ ] 分类表无死条目（表内工具名全部出现在 `tools/list`）
  - [ ] 未登记工具经真 socket 调用 → `ok=false, code=unauthorized`
  - [ ] 未绑定会话调 `OwnerScoped` / `SeatRequired` → `unauthorized`
  - [ ] `room.release` / `room.kick` / `room.add_ai` / `room.start` 成功后，会话座位**不被写入**（回归用例各 1 条）
  - [ ] `UnbindSeat` 五个调用点全部落地：①`room.release` ②`room.kick` ③`room.add_ai` ④lobby `DropSeat` ⑤`BindSeat` 内的同座位解绑（每条一个用例）
  - [ ] **SecurityTests 第 4 例（悬空凭证，一等公民）**：`A` claim 座位 0 → `A` `room.release {seat:0, seatToken:A的令牌}` → `B` claim 座位 0 → 开局 → `A` 调 `match.concede {owner:0}` 断言 `ok=false && code=="unauthorized"`，且 `A` 不再收到 `notify/view`（`A` 的 `Seat` 已为 null）
  - [ ] 半开连接场景：`A`（未断开）持座位 0，`A2` 用同一 `seatToken` `room.resume` 成功 → `A` 的后续 `match.end_turn` 为 `unauthorized`，`A2` 正常（不变量"一座一会话"）
  - [ ] 代码中 `session.Seat =` 只出现在 `BindSeat` / `UnbindSeat` 两处（`grep` 可验证）
  - [ ] 被拒绝的请求不进入 `SerialWorkQueue`（可断言 `PendingCount` 不变 / 引擎状态不变）
- Rationale: 需求「关键设计约束 1/2/3」；报告 P0-1 的二次伤害（推送流被劫持）源自 `BindSeat` 的宽绑定条件，故绑定面收窄为工具名白名单而非只处理 `observe.view`。四类划分把"是否写入座位""是否校验 owner""是否只要求已绑定"三件事正交化，专门分支只留给语义确实特异的 `observe.view` 与 `target.*`。解绑的五点是"绑定悬空"的结构性补丁：只有 claim/resume 写、没有对应拆除，就会在"释放后换人"路径上把 P0-1/P0-2 完整复活。
- Risk: 白名单是纯字符串表，工具改名即掉出登记（表现为 `unauthorized` 而非编译错误）——由守卫测试 1 兜住；`observe.view` / `target.*` 的专门分支与四类表是两套判定代码，后续新增「类 observe」工具时需人工判断归哪一侧（守卫测试只保证「有归属」，不保证「归得对」）。
- Terminology: 待登记「授权层（Authorization Gate）」「座位作用域（Seat Scope）」

### S2: 视图与选靶的专门分支（F1 的落点）
- Status: Done
- Target: `ServerHost.GateAsync` 的 `observe.view` / `target.*` 分支
- Approach:
  - `observe.view`：已绑定 → `seat` 缺省或等于会话座位时把参数改写为会话座位（"忽略"语义）；显式传其它座位 → `unauthorized`。未绑定 → 放行（单机诊断），且不写 `session.Seat`。
  - `target.submit` / `target.cancel`（**裁决表**，按需求 F2 + 不变量落定）：

    | `pending` 状态 | 会话状态 | 裁决 |
    |---|---|---|
    | 无 pending（`Bridge.CurrentPending == null`） | 任意 | **门禁放行**，交服务层按现状回 `invalid-arguments`（"当前没有待决的选择请求"）。理由：门禁不抢业务错误码，且 pending 可能在入队后消失 |
    | `pending.Seat == s` | 已绑定且 `session.Seat == s` | 放行 |
    | `pending.Seat == s` | 已绑定但 `session.Seat != s` | `unauthorized`（pending 保持存活，归属座位仍可应答） |
    | `pending.Seat == s` | 未绑定 | `unauthorized` |
    | `pending.Seat == null` | 已绑定 | 放行 → 先到先得由 `TargeterBridgeAdapter.Submit` 的 `_submit` 置空语义天然保证（首个提交胜出，后到者 `invalid-arguments`） |
    | `pending.Seat == null` | 未绑定 | `unauthorized` |

    即：**"先到先得"只在已绑定会话之间成立**——它回答"谁能先答"，不回答"谁能答"；后者由绑定决定。
  - `target.claim`：按残余风险 R2 **保持可用**（本批不改协议面工具集合），不做座位校验（它不推进引擎，只写认领表）；在接入指南标注"已退役"。
- Acceptance:
  - [ ] 座位 0 会话 `observe.view {seat:1}` → `ok=false, code=unauthorized`，且后续 `notify/view.seat == 0`
  - [ ] 座位 0 会话 `observe.view`（不带 seat）→ `ok=true` 且 `details.seat == 0`
  - [ ] 未绑定会话 `observe.view {seat:0}` → `ok=true`（沙盒/诊断不受影响）
  - [ ] 座位 0 会话 `target.submit` 座位 1 的 pending → `unauthorized`，pending 仍存活（可被座位 1 应答）
  - [ ] 未绑定会话 `target.submit` 任意 pending → `unauthorized`
  - [ ] `pending.Seat == null` 时两个已绑定会话先后提交 → 首个 `ok=true`，后到者 `ok=false, code=invalid-arguments`（先到先得未被本层改写）
- Rationale: 需求 F1/F2；隐藏信息边界（R8）在多人场景下的唯一保证点。
- Risk: `observe.view` 的参数改写会覆盖客户端传入值，若端上依赖回显原始入参会看到差异；`target.*` 门禁读取 `Bridge.CurrentPending`，与选靶状态机同源但非同一时刻快照——pending 在判定与入队之间被应答时，最终错误由服务层给出（`invalid-arguments`），与门禁的 `unauthorized` 可能先后出现。
- Terminology: 观察面、选靶

### S3: 动作归属校验的可验收面（F2）
- Status: Done
- Target: `ServerHost.GateAsync`（`OwnerScoped` / `SeatRequired` 两类的具体判定）
- Approach: 分类（S1）已定"哪些工具校验什么"，本步只落定判定细节：`OwnerScoped` 读 `arguments.owner`，缺失/非整数/不等于 `session.Seat` → `unauthorized`（缺失即拒绝，**不**替客户端补座位号；见残余风险 R1）；`SeatRequired` 只要求 `session.Seat != null`，引擎自行判当前方（因此"错误座位发 end_turn"由引擎门禁拒绝，仍回 `not-ready`/`engine-rejected`，与本层无关）；未绑定会话命中两类 → `unauthorized`。
- Acceptance:
  - [ ] 座位 0 `match.concede {owner:1}` → `unauthorized`，对局未结束
  - [ ] 座位 0 `mulligan.done {owner:1}` / `play.unit {owner:1}` → `unauthorized`，手牌/场面不变
  - [ ] 未绑定会话 `match.end_turn` → `unauthorized`（现为 `not-ready`，属行为变更）
  - [ ] 座位 1 会话 `match.end_turn`（不是当前方）→ 仍由引擎拒绝，错误码不在本层改写
- Rationale: 需求 F2；报告 P0-2。
- Risk: `owner` 缺失一律 `unauthorized` 会让「省略 owner 的旧客户端」直接失败（有意收紧，需在接入指南标注）；`play.unit` 的 `owner` 与 `ref` 可能指向不同座位（ref 属对手手牌），本层只校验 owner——越权 ref 仍由引擎「卡不在该玩家手牌」拒绝，错误码是 `engine-rejected` 而非 `unauthorized`。
- Terminology: 座位、控制器

### S4: 房主身份约束（F3）
- Status: Done
- Target: `Room.StartAsync` / `Room.Release` / `Room.Kick`（`HostSlot()==null` 分支）、`ServerHost`（回环判定）
- Approach: ①`kind:"local"` 的 `room.claim` 由 `ServerHost` 依会话远端地址判定，非回环 → `unauthorized`；②`HostSlot()==null` 时 `room.start` → `unauthorized`（提示「房间没有房主」）；③`HostSlot()==null` 时 `room.release` / `room.kick` 同样 `unauthorized`（补报告未列的 `Kick` 旁路）；④房主 lobby 断线仍立即释放座位（保留 `LobbySeatRetention` 语义），释放后房间不可开局。回环判定口径按残余风险 R3（严格 `IPAddress.IsLoopback`）。
- Acceptance:
  - [ ] 非回环来源 `room.claim {kind:"local"}` → `unauthorized`
  - [ ] 无 local 座位时 `room.start` / `room.release` / `room.kick` → `unauthorized`
  - [ ] 房主断线后座位释放、开局被拒；新房主 claim local 后可开局（同一进程内）
  - [ ] 既有 `RoomTests.Release_ByHostToken_ClearsSeat` / `Kick_ByHostToken_ClearsSeat` 仍绿（有房主时不受影响）
- Rationale: 需求 F3；报告 P0-3；`Kick` 旁路由父 agent 确认纳入本批。
- Risk: `HostSlot()==null` 时禁用 `Release` 意味着「两个远程座位、无房主」的房间无法自行释放座位（只能重启进程）——为堵旁路接受的代价；回环判定若取严格 `IPAddress.IsLoopback`，房主从本机局域网地址（如 `192.168.x.x`）连入时 claim `local` 会被拒（Q4 残余）。
- Terminology: 房主（Host Player）

### S5: 推送闭环（F4）
- Status: Done
- Target: `ServerHost`（`PushViewsAsync` 的三个新增调用点）、`MatchExecutor`（`Settled` 事件 ＋ `_handedOff` 标志）
- Approach: 三处一律遵守同一条形态——**变更与推送在同一个 queue work 内**（`PushViewsAsync` 内部走 `Executor.ReadOnly`，硬要求 `ConsumerContext.IsActive`）。

  **① AI 每步（`Performed == true`）**：把推送包进 AI 步自身的 work item，而不是另起一个：

  ```csharp
  // DriveAiAsync：现状是 EnqueueAsync(Command, token => pilot.StepAsync(token), ct)
  var result = await _queue.EnqueueAsync(WorkPriority.Command, async token =>
  {
      var step = await pilot.StepAsync(token).ConfigureAwait(false);
      if (step.Performed)
      {
          await PushViewsAsync(token).ConfigureAwait(false);   // 消费者上下文内；含 PushSegments
      }
      return step;
  }, ct).ConfigureAwait(false);
  ```
  要点：`Performed == false`（未轮到 / 无合法动作）**不推**，避免 AI 空转把推送队列刷满（有界 256 + DropOldest 会挤掉真实 view）；节奏仍由现有 150/300ms 延时控制。

  **② 断线宽限判负成功**：`CheckDisconnectGrace` 的 work item 内、认输返回 `StatusCompleted` 之后推送，并且**推送成功后才移除断线记录**（与 S6 的顺序绑定）：

  ```csharp
  _ = _queue.EnqueueAsync(WorkPriority.Command, async token =>
  {
      if (!IsItsTurn(seat)) return false;                 // 不轮到自己：记录保留（S6）
      var outcome = await _service.ConcedeAsync(seat, token).ConfigureAwait(false);
      if (outcome.Status != ActionOutcome.StatusCompleted) return false;
      _disconnectedSeats.TryRemove(seat, out _);          // 真正认输成功后才移除
      await PushViewsAsync(token).ConfigureAwait(false);  // 客户端才看得到 phase:"over"
      return true;
  }, _shutdown.Token);
  ```

  **③ 选靶超时按取消推进后**（裁决：执行器 `Settled` 事件）

  完成点不在任何客户端调用栈上（`RunAsync` 早已返回 `targeting`；取消由 `TargeterBridgeAdapter.DriveAsync` 的 `Task.Delay` 竞速在后台推进）。故在执行器侧新增一个"**动作在交出去之后自行完结**"的观测点：

  ```csharp
  // MatchExecutor
  private int _handedOff;                       // 0/1，Interlocked/Volatile 读写
  public event Action<ActionOutcome>? Settled;

  // RunAsync 进入时强制清零（避免上一轮 targeting 的残留标记误触发）
  public async Task<ActionOutcome> RunAsync(...)
  {
      ...
      Volatile.Write(ref _handedOff, 0);
      var task = Guard(action, match, ct);
      _active = ObserveSettle(task);            // 包装一层，完结时判定是否"无人等待"
      ...
  }

  private async Task<ActionOutcome> ObserveSettle(Task<ActionOutcome> task)
  {
      try
      {
          var outcome = await task.ConfigureAwait(false);
          if (Interlocked.Exchange(ref _handedOff, 0) == 1)   // 立刻复位
          {
              try { Settled?.Invoke(outcome); }
              catch { /* 订阅者异常不得污染动作结果 */ }
          }
          return outcome;
      }
      catch (Exception ex)
      {
          Interlocked.Exchange(ref _handedOff, 0);
          try { Settled?.Invoke(ActionOutcome.Failed(McpCodes.EngineRejected, ex.Message)); }
          catch { }
          throw;
      }
  }

  // WaitAsync 把 targeting 交回调用方时置位
  if (Bridge.HasPending) { Volatile.Write(ref _handedOff, 1); return ActionOutcome.Targeting(...); }
  // 有调用方开始等完结时清位（否则会与"调用方自己那次推送"重复）
  //   · RunAsync 入口已清零
  //   · SubmitAsync 在 await WaitAsync(active) 之前 Volatile.Write(ref _handedOff, 0)
  ```

  > 为什么"清位"必须发生在**调用方开始等待**时而不是"拿到结果"时：`ObserveSettle` 是 `_active` 的外层包装，它在把结果交给 `WaitAsync` **之前**就执行 `Exchange`——若只在拿到结果时清位，提交路径必然多推一帧。

  `ServerHost` 订阅侧**只做入队**：

  ```csharp
  _service.Executor.Settled += _ => EnqueuePush();
  private void EnqueuePush()
  {
      try
      {
          _ = _queue.EnqueueAsync(WorkPriority.Command,
                  async token => { await PushViewsAsync(token).ConfigureAwait(false); return true; },
                  _shutdown.Token);
      }
      catch (WorkQueueFullException) { /* 静默丢弃：下一次动作的推送兜底 */ }
      catch (ObjectDisposedException) { /* 关闭中 */ }
  }
  ```

  **`Settled` 的五条实现约束（硬性）**：
  1. `_handedOff` 一律经 `Interlocked` / `Volatile` 读写（continuation 在 thread pool 上）；continuation 内吞掉所有异常——`Settled` 订阅者异常不得污染动作结果。
  2. 订阅者只允许 `_queue.EnqueueAsync(..., _shutdown.Token)` 的 fire-and-forget；**禁止在事件内同步投影或发送**（此刻不在消费者上下文，`Executor.ReadOnly` 会抛）。
  3. 队列满或已关闭时静默丢弃：不抛、不重试（view 幂等，下一次动作的推送兜底）。
  4. 事件触发后立即复位 `_handedOff`；`RunAsync` 每次进入时强制清零（见上代码）。
  5. 顺序天然成立：`notify/target.expired` 在 `DriveAsync` 超时分支同步发出（早于 `selector.Submit(CancelEvent)`），`notify/view` 在动作完结后才入队 → **expired 先、view 后**，且 view 反映**取消后**状态。

  **共同约束**：`PushViewsAsync` 本身不加锁、不 await IO（`session.Send` 只是有界 channel 写入）；推送工作项一律 `WorkPriority.Command`（与产生变更的动作同优先级，保证顺序），不得用 `Interactive` 抢占（会让推送插到 pending 的 `target.*` 之前）。
- Acceptance:
  - [ ] AI 完成一整回合期间，客户端收到 `notify/view` 数 ≥ AI 实际 `Performed` 步数
  - [ ] AI 待机（`Performed == false`）期间不产生 `notify/view`
  - [ ] 判负后收到 `phase:"over"` 的 `notify/view`（而不是只能靠轮询 `observe.view` 发现）
  - [ ] **选靶超时（`TargetSelectionTimeout=1s`）→ 客户端先收 `notify/target.expired`、后收 `notify/view`，且该 view 的 `actions.playableUids` / `phase` 反映取消后的状态（不是取消前）**
  - [ ] 三处推送的 work item 均以 `WorkPriority.Command` 入队（可用 `SerialWorkQueue` 顺序断言）
  - [ ] `Settled` 订阅者抛异常时动作结果不受影响（注入抛异常的订阅者，断言动作仍返回正常结局）
  - [ ] 经 `target.submit` 正常完成的多步动作**只推一次**（`Settled` 与调用方那次推送不重复：`SubmitAsync` 入口清位）
  - [ ] 队列满时丢弃推送不抛异常（容量 1 + 阻塞消费者构造队满，断言无异常且后续动作仍推）
- Rationale: 需求约束 6（投影依赖只读执行面，只能在消费者上下文内算）；报告 P1-1。AI 空转不推是为了保护有界推送队列——否则"AI 卡住"会表现为"客户端收不到真实 view"。选第③处用 `Settled` 而非宿主定时器：桥内超时语义必须保留（单机/agent 形态无宿主，那是唯一保险），双取消路径的幂等复杂度不值得，且消费者内等待会阻塞单写者。
- Risk: `_handedOff` 是启发式——超时与 `target.submit` 极端并发时可能多推一帧（view 幂等，无害）；`Settled` 跨线程触发，若订阅者违规做同步投影会抛 `InvalidOperationException`（约束 2 已禁止，但属"靠纪律"的约束，代码上只能靠测试兜底）。
- Terminology: 投影、段

### S6: 断线宽限时序（F5）
- Status: Done
- Target: `ServerHost.CheckDisconnectGrace`
- Approach: 断线记录只在**认输成功后**移除；不轮到时保留并等下一次检查（节流/在途去重按残余风险 R4）。判负成功后按 F4 推送。
- Acceptance:
  - [ ] 断线发生在对手回合 → 对手结束回合后，宽限到期即判负（`phase == "over"`）
  - [ ] 断线方自己回合断线 → 仍在首次宽限到期时判负（不回归）
- Rationale: 需求 F5；报告 P1-2（最常见的掉线时序）。
- Risk: 记录保留 + 每 tick 重试意味着「轮到断线方」之前的每个心跳周期都会入队一次判定工作项（轻量但非零）；判负时机仍受心跳检查间隔（`HeartbeatTimeout/2`，上限 5s）粒度影响，1s 级用例需把 `HeartbeatTimeout` 调小。
- Terminology: —

### S7: 房间态推送与 `ready` 语义（F7）
- Status: Done
- Target: `ServerHost`（广播点）、`RoomSeatSnapshot`（删 `Ready`）、`Room.Snapshot`
- Approach: `room.claim` / `release` / `kick` / `add_ai` 成功、以及 lobby 断线释放后，向**所有会话**广播 `notify/room`，载荷＝`room.info` 的 details（序列化单一来源按残余风险 R5：抽 `RoomSnapshotJson.ToNode`）。删除 `seats[].ready`。
- Acceptance:
  - [ ] 客人 claim 后，房主 1s 内收到 `notify/room` 且 `seats[1].controllerKind == "remote"`
  - [ ] `room.info` 与 `notify/room` 载荷同形（同一序列化函数）
  - [ ] 载荷不含 `ready`
- Rationale: 需求 F7；报告 P1-4/P1-5。
- Risk: `notify/room` 广播给所有会话（含未绑定会话）——房间内容属产品上公开的信息（需求非目标已声明 `room.info` 不做访问控制），不构成泄漏；删除 `ready` 是**协议破坏性变更**，已有客户端读该字段会拿到 `undefined`（需求已明确要删）。
- Terminology: 房间态

### S8: 客户端心跳（F6）
- Status: Done
- Target: `src/Orc.Client/OrcClient.cs`、`ClientOptions.cs`
- Approach: 空闲计时器（任何入站帧重置）；静默 ≥ `HeartbeatInterval` 发 `ping`；发出后 `HeartbeatTimeout` 内无入站帧 → 关闭连接、`FailAllPending`（`IOException`）、`IsConnected=false`。默认值改 `HeartbeatInterval = 30s` / `HeartbeatTimeout = 15s`。`ping` 的形态按残余风险 R6（走 `CallRawAsync("ping")`，响应即"入站帧"证据）。
- Acceptance:
  - [ ] 服务端心跳超时 2s、客户端不发 ping（`HeartbeatInterval` 置大）→ `CallAsync` 抛 `IOException`（非 `TimeoutException`）
  - [ ] 正常对局空闲 40s 不掉线
  - [ ] `HeartbeatInterval`/`HeartbeatTimeout` 有行为覆盖（替换现「只断言默认值」的假覆盖）
- Rationale: 需求 F6；报告 P1-3。
- Risk: 空闲计时器与读循环存在时间窗竞争（刚收到帧、计时器已判定超时）→ 可能误判断线一次；`HeartbeatInterval` 默认改 30s 后，被 NAT 静默丢弃的半开连接最长需 `30+15=45s` 才被发现（与文档口径一致，但比旧实现的探测节奏慢）。
- Terminology: —

### S9: 客户端读循环健壮性（F9/F10/F11/F12）
- Status: Done
- Target: `OrcClient.cs`
- Approach: ①`NotificationReceived` 逐订阅者 try/catch（一个 handler 抛异常不影响读循环与其余 handler）；②响应 `id` 容忍数字/未知（不抛、忽略）；③`IsConnected` 纳入 `_closedReason`；④`CallRawAsync` 写失败时移除 pending 条目并完成 TCS。
- Acceptance:
  - [ ] handler 抛异常后连接仍可用、其余 handler 仍被调用
  - [ ] 收到 `{"id":1,...}` 响应不导致读循环退出
  - [ ] 服务端关闭后 `IsConnected == false`
  - [ ] 写失败后 `_pending` 不残留（可经反射或行为断言）
- Rationale: 需求 F8–F19 表（F9–F12）。
- Risk: 逐订阅者隔离会**吞掉** handler 异常——UI 回调里的错误不再表现为掉线，可能变成静默失效（本批不做诊断事件）；数字 `id` 容忍后，第三方客户端用数字 id 时响应会被静默忽略（仍是「假死」，只是不再杀读循环）。
- Terminology: —

### S10: 串行队列 drain（F8）
- Status: Done
- Target: `src/Orc.Server/Execution/SerialWorkQueue.cs`
- Approach: `DisposeAsync` 取消消费者后 drain 剩余项，逐项以 `ObjectDisposedException` 完成其 TCS；`WorkItem` 增抽象 `Fail(Exception)` 供非泛型处理。
- Acceptance:
  - [ ] 关闭时排队项抛 `ObjectDisposedException`（而非挂起）
  - [ ] `PendingCount` 归零
- Rationale: 需求 F8；与「断开后请求立即失败」同一原则。
- Risk: drain 在 `DisposeAsync` 内完成，若队列中存在长期运行的工作项，关闭会被拖慢（正在执行项由 `_shutdown` 传递取消）；`ObjectDisposedException` 与既有 `OperationCanceledException` 语义并存，调用方需同时处理两种。
- Terminology: 串行执行队列

### S11: 服务端健壮性收敛（F14/F15/F16/F17）
- Status: Done
- Target: `ServerHost.HandleClientAsync` / `Room.Snapshot` / `DeckRulesValidator` / `McpMatchLauncher` 注释
- Approach: ①捕获 `SocketException`（连接重置不产生未观察异常）；②`Snapshot()` 锁内只拷座位数组，`_addresses()` 移到锁外；③`DeckRulesValidator` 透传 `RequiredDeckSize`；④修正 `McpMatchLauncher` 注释为「由 `ServerOptions` 传入」。
- Acceptance:
  - [ ] 连接重置不产生未观察任务异常
  - [ ] 快照与座位状态一致且不持锁枚举网卡（可经 `_addresses` 注入延迟探针断言）
  - [ ] `RequiredDeckSize = 30` 时 31 张卡组被拒（现状：按 39 校验）
- Rationale: 需求 F8–F19 表。
- Risk: `Snapshot()` 移出锁后，返回值与「锁内瞬时状态」之间可能出现极短窗口的座位变化（快照本就是瞬时视图，可接受）；`DeckRulesValidator` 透传 `RequiredDeckSize` 会改变端到端夹具的通过条件（`EnforceDeckRules=true` 且卡组非 39 张的用例会转红，需同步核对）。
- Terminology: —

### S12: NDJSON 增量限长（F13）
- Status: Done
- Target: `src/Orc.Protocol/NdjsonFraming.cs`（`NdjsonReader`）
- Approach: 不再用 `StreamReader.ReadLineAsync`（整行先缓冲）；改为自有缓冲读取、逐块扫描换行，累计超上限立即抛 `InvalidDataException`。限长单位按残余风险 R7（按字节计、BOM 剥离、跨块多字节交 `Decoder`）。
- Acceptance:
  - [ ] 发送 2 MiB 无换行数据 → 服务端断开，且内存不随报文增长（上限 1 MiB）
  - [ ] 正常帧（含 UTF-8 多字节、BOM、空行）行为不变
- Rationale: 需求 F13；报告 P2-6。
- Risk: 自写 reader 替换 `StreamReader` 后，BOM / 多字节字符跨块边界 / 空行语义都需重新验证（本批最容易引入回归的一处）；限长单位改为字节后，中文密集报文的有效上限会比「字符上限」更早触发。
- Terminology: —

### S13: 选靶异常传播（F18）＋ 空卡组（F19）
- Status: Done
- Target: `src/Orc.Mcp/Match/TargeterBridgeAdapter.cs`、`MatchExecutor.cs`
- Approach: `DriveAsync` 异常需让动作以失败收束——挂载方式按残余风险 R8：桥暴露 `Faulted` 信号，`MatchExecutor.WaitAsync` 的 `WhenAny` 纳入该信号 → 返回 `engine-rejected` 并清 `_active`（`IsBusy` 随之为假）；`RunAsync` 入口处若发现 `IsBusy && Bridge.Fault != null` 亦先清空再回失败，避免"上一轮异常把队列永久卡住"。F19 现状已满足（`Room.Claim` 先判空卡组），仅补测试。
- Acceptance:
  - [ ] 选靶期间注入异常 → 动作返回 `engine-rejected`，后续动作不再 `busy`
  - [ ] 空卡组 → `deck-invalid`（补测试）
- Rationale: 需求 F18/F19；报告 P2-11。
- Risk: 桥的 `_fault` 与执行器 `_active` 是两套生命期，异常路径必须保证 `_active` 一定完结（否则 `IsBusy` 仍可能恒真）；「用户取消」（`Cancelled`）与「引擎异常」（`engine-rejected`）在此路径上需显式区分，避免误报。
- Terminology: 选靶

### S14: 测试补齐与去伪（F20）
- Status: Done
- Target: `tests/Orc.Server.Tests/`（新增 `SecurityTests` / `PushDeliveryTests` / `ClientHeartbeatTests`）、既有 5 处弱断言
- Approach: 每个修复项**先落测试**（红）再改实现（绿）；同时修订因 F1/F2 而失效的既有用例（`EndToEndTests` / `AiAndDisconnectTests` 的 `observe.view {seat}` 调用需按会话座位改写，`AiAndDisconnectTests` 的轮询改用 `notify/view` 等待）。

  **`SecurityTests` 用例清单（编号即执行顺序，第 4 例为一等公民）**

  | # | 用例 | 断言要点 | 对应 |
  |---|---|---|---|
  | 1 | `CrossSeatView_IsRejected_AndNoRebind` | 座位 0 `observe.view {seat:1}` → `unauthorized`；随后推送 `notify/view.seat == 0`（原报告 REBIND 复现路径） | F1 / P0-1 |
  | 2 | `Concede_ForOtherSeat_IsRejected` | 座位 0 `match.concede {owner:1}` → `unauthorized`，且 `observe.view` 的 `phase != "over"` | F2 / P0-2 |
  | 3 | `ClaimLocal_FromNonLoopback_IsRejected` | 以假远端地址（非回环）claim `kind:"local"` → `unauthorized` | F3 / P0-3 |
  | 4 | **`StaleBinding_AfterRelease_CannotAct`（悬空凭证，一等公民）** | 四步全链断言，见下 | F1+F2 / 本轮裁决 |
  | 5 | `UnboundSession_ActionTools_AreUnauthorized` | 未绑定会话 `match.end_turn` / `mulligan.done` → `unauthorized` | F2 |
  | 6 | `RoomStart_WithoutHost_IsUnauthorized` | 无 local 座位时 `room.start` / `room.release` / `room.kick` → `unauthorized` | F3 |
  | 7 | `TargetSubmit_ForOtherSeatPending_IsRejected` | 座位 0 提交座位 1 的 pending → `unauthorized`，且座位 1 随后仍可提交成功 | F2 |
  | 8 | `TargetSubmit_UnboundSession_IsRejected` | 未绑定会话提交任意 pending（含 `seat == null`）→ `unauthorized` | F2 |
  | 9 | `ResumeFromSecondConnection_StealsBinding` | `A` 持座位 0 且未断，`A2` 用同令牌 `room.resume` → `A` 的 `match.end_turn` 变 `unauthorized`，`A2` 可动 | 不变量 |
  | 10 | `SeatScope_DefaultsToDeny_ForUnregisteredTool` | 临时登记假工具 → 调用 `unauthorized` | S1 默认拒绝 |

  **第 4 例的可执行断言形态（`StaleBinding_AfterRelease_CannotAct`）**

  ```csharp
  // Arrange：开局前的 lobby 阶段
  var a = await ConnectAsync(host.Port);                 // A：先做房主/或客人，占座位 0
  var tokenA = (await a.CallAsync("room.claim", Claim(0, "local"))).Details!["seatToken"]!.GetValue<string>();
  Assert.True((await a.CallAsync("room.release", new JsonObject { ["seat"] = 0, ["seatToken"] = tokenA })).Ok);

  // Act：B 接手同一座位，再开局
  var b = await ConnectAsync(host.Port);
  Assert.True((await b.CallAsync("room.claim", Claim(0, "local"))).Ok);   // 座位已空 → 成功
  await b.CallAsync("room.add_ai", new JsonObject { ["seat"] = 1 });      // 补满座位
  Assert.True((await b.CallAsync("room.start", StartTokenFromClaim)).Ok);

  // Assert：A 的绑定已被拆除
  var concede = await a.CallAsync("match.concede", new JsonObject { ["owner"] = 0 });
  Assert.False(concede.Ok);
  Assert.Equal(ResultCodes.Unauthorized, concede.Code);                   // 不是 invalid-arguments / engine-rejected
  Assert.Equal("play", (await b.CallAsync("observe.view")).Details!["view"]!["phase"]!.GetValue<string>());
  // 且 A 收不到座位 0 的推送：统计 1s 内 a.NotificationReceived 的 notify/view.seat 集合为空
  ```

  另需 `SecurityTests` 覆盖 `room.release` / `room.kick` / `room.add_ai` / lobby 断线四处解绑各一条（可与上表 4/6 合并计数，但调用点必须逐条命中）。

  **`PushDeliveryTests` 用例清单**

  | # | 用例 | 断言要点 |
  |---|---|---|
  | 1 | `AiTurn_PushesViewPerPerformedStep` | AI 完成一整回合期间 `notify/view` 计数 ≥ `Performed` 步数；用推送等待代替轮询 |
  | 2 | `AiIdle_DoesNotPush` | AI 非其回合的 1s 内无新增 `notify/view` |
  | 3 | `DisconnectConcede_PushesGameOver` | 判负后收到 `phase == "over"` 的 `notify/view` |
  | 4 | `DisconnectDuringOpponentTurn_StillLosesAfterGrace` | 断线发生在对手回合 → 对手结束回合后宽限到期即判负（F5） |
  | 5 | `TargetingTimeout_PushesExpiredThenView` | `notify/target.expired` 先到、`notify/view` 后到（同连接顺序） |

  弱断言修订清单：`EndToEndTests:77-79`（改为断言 `selfHand` 不相交 + `selfPlayerName` 不同）、`ServerProcessTests:56`（改为断言退出码/进程已启动的前置证据）、`ResilienceTests:34-38/62-66`（断言 `IOException` 且 `InnerException` 为关闭原因）、`SkeletonSmokeTests:34-40`（默认值断言改 30s/15s，行为覆盖移至 `ClientHeartbeatTests`）。

- Acceptance:
  - [ ] 全量 ≥ 2148 且 0 失败
  - [ ] 新增用例在修复前逐条失败（红→绿可回溯）；`SecurityTests` 10 例、`PushDeliveryTests` 5 例、`ClientHeartbeatTests` ≥ 3 例
  - [ ] `SecurityTests` 第 4 例在修复前必红（当前 `BindSeat` 宽绑定 + 无解绑，`A` 的 `concede` 会成功）
- Rationale: 需求 F20；报告 §3.2 的盲区逐条对应到用例，避免"自己看自己"的路径继续全绿。
- Risk: 新增用例大量依赖时序（AI 150/300ms 节奏、宽限 1s、心跳检查间隔），CI 负载高时易 flaky（报告 §3.4 已指出同类既有风险）；`SecurityTests` 第 3 例需构造非回环来源，纯 loopback 环境下得靠注入假远端地址或退回 `Room` 层单测。
- Terminology: —

### S15: 文档同步（F21）
- Status: Done
- Target: `docs/多人支持-服务端客户端分离/客户端接入指南.md`、`docs/多人支持-服务端客户端分离/requirements.md`
- Approach: 按需求 F21 清单改：§4.3 心跳（30s/15s 双向口径）、§5.2 `observe.view` 座位约束与 `unauthorized` 语义、§6.2 删 `ready`、§8.5/§12 `match.ended`、§11 路径、§14.1 推送方法；新增 `notify/room` 载荷与触发时机、"一个座位最多一个活动会话"的绑定不变量与解绑时机。`requirements.md` 修正「心跳已实现」等表述（归父 agent 处理）。
- Acceptance:
  - [ ] 文档中每条契约都能在代码或测试中定位
- Rationale: 需求 F21。
- Risk: 文档改动跨 8 处小节，且 `requirements.md` 归父 agent 维护，两边可能再次漂移；§11 路径修正需核对迁移包实际位置（报告 §四已指出本仓无 `proto/`）。
- Terminology: —

## 决策记录（grill 结论）

- **Q1 门禁分类机制** → 集中白名单四类（Bind / OwnerScoped / SeatRequired / Agnostic）＋ `observe.view` 与 `target.*` 专门分支；未分类即 `unauthorized`（默认拒绝）；顺序固定为「分类 → 座位校验 → 入队」；两道守卫测试（正向：宣告即须登记；反向：表内无死条目）＋ 一条默认拒绝行为守卫。`play.can_play` / `command.availability` 确认为 Agnostic（按 ref 纯读，ref 受 F1 隔离）。
- **Q2 `session.Seat` 生命周期** → 双向收敛。不变量「一座最多一会话、一会话最多一座」；`UnbindSeat` 五个调用点（release / kick / add_ai / lobby DropSeat / Bind 成功时同座位解绑）；"释放后换人"作为 `SecurityTests` 第 4 例（一等公民）。
- **Q5 选靶超时后的推送挂载** → 执行器 `Settled` 事件（只报"交出去之后自行完结"的那次）＋ 订阅者只入队；桥内超时语义保留（单机/agent 形态依赖它）。五条硬性实现约束见 S5。

## 残余风险（非阻塞；执行时按下列默认取舍，父 agent 可否决）

| # | 未决点 | 默认取舍（执行时按此实现） | 若否决的影响面 |
|---|---|---|---|
| R1 | `owner` 参数缺省 | **拒绝**（`unauthorized`），不替客户端补座位号 | 只改 S3 一处判定；若改为注入，S3 的 Acceptance 需增"缺省即按会话座位执行"用例 |
| R2 | `target.claim` 是否在服务端调用面收紧 | **保持可用**（本批不动协议面工具集合），仅在 `客户端接入指南` 标注"已退役、请改用 `target.submit`/`target.cancel`" | 若要求禁用：S2 增一行门禁（`unauthorized`），并改 §5.2 契约 |
| R3 | 回环判定口径 | **严格 `IPAddress.IsLoopback`**（`127.0.0.0/8`、`::1`）；本机网卡地址**不**视为回环 | 若放宽为"含本机网卡地址"，房主可用 LAN 地址连入，但 `local` 的来源约束退化为"同机"（可被同机其它进程冒用） |
| R4 | 宽限重试的节流 | `ConcurrentDictionary<int, byte> _graceInFlight` 去重：同一座位在途期间不重复入队；tick 间隔＝心跳检查间隔（`HeartbeatTimeout/2`，上限 5s） | 若需更快判负（<1s），须另设专用计时器 |
| R5 | `notify/room` 载荷来源 | 抽 `Orc.Mcp.Room.RoomSnapshotJson.ToNode(RoomSnapshot)`（与 `RoomTools` 现有序列化选项一致），`room.info` 与 `notify/room` 共用 | 若不复用，需在 S14 增加"两份载荷逐字段比对"的测试 |
| R6 | 客户端 `ping` 形态 | 走 `CallRawAsync("ping")`（独立 pending 条目，复用 `_nextId`），响应即"入站帧"证据；心跳失败统一 `IOException`（与 F11/F6 口径一致） | 若改为不占 pending 的裸写，需保证响应被读循环按未知 id 丢弃（依赖 F10） |
| R7 | NDJSON 限长单位 | 按**字节**计（`MaxLineLength = 1 MiB` 字节），BOM 剥离，跨块多字节由 `Decoder` 处理 | 若按字符计，1 MiB 中文报文可达 3 MiB 内存，DoS 防护弱化 |
| R8 | 选靶异常传播路径（F18） | 桥暴露 `Faulted` 信号（异常对象）；`MatchExecutor.WaitAsync` 的 `WhenAny` 纳入该信号 → 返回 `engine-rejected` 并清 `_active`（`IsBusy` 随之为假） | 若改为"注入 `CancelEvent` 让引擎自然收束"，需处理 `NextAsync` 已抛异常时无法提交的情形 |
| R9 | `ServerOptions.HeartbeatTimeout` 默认值 | **保持 45s**（需求未要求改服务端默认；只有客户端默认改 30s/15s） | 若改为 30s，与客户端 ping 间隔相同，边界抖动会误判断线 |
| R10 | 既有用例的连带改动 | 凡是"一个客户端看两个座位"的既有用例（`EndToEndTests` 若干处、`AiAndDisconnectTests` 的轮询）按会话座位改写；不改断言强度 | 若允许保留旧调用，需为 `observe.view` 开一条"单机诊断例外"——与 F1 冲突，不建议 |

> 收尾判断：以上 10 项均**不改变** S1–S15 的结构与验收口径，只影响局部实现选择，故不阻塞交执行子 agent。


## 执行记录（2026-10-21，执行子 agent）

> 结果：S1–S15 全部落地；`dotnet build OrcEngine.slnx -t:Rebuild` 0 错误（本批改动文件 0 警告；存量警告在 `Orc.Game` 等既有工程）；`dotnet test OrcEngine.slnx` **2262 通过 / 0 失败**（基线 2148；本批新增 53 例集中在 `Orc.Server.Tests`，其余增量属并行 agent）。

### 每步改动文件

| 步骤 | 文件 | 改动要点 |
|---|---|---|
| S1 | `src/Orc.Server/Transport/SeatScopeRegistry.cs`（新增） | `SeatScope` 四类枚举、`ToolScopes` 白名单、`SpecialCasedTools`、`IsClassified/ScopeOf/ScopeName/RegisteredNames`；`SeatScopePolicy.IsTrustedLocal` |
| S1 | `src/Orc.Server/Transport/ServerHost.cs` | `Gate`（分类→座位→入队）、`GateBind`、`ApplySeatEffects`、`BindSeat/UnbindSeat/UnbindSeatOf`、`IsToolClassified/SeatScopeOf/RegisteredToolNames`、`ClientSession.RemoteAddress`、lobby 断线解绑 |
| S2 | `ServerHost.cs` | `GateSpecial`：`observe.view` 座位改写/拒绝；`target.submit/cancel` 六行裁决；`target.claim` 放行 |
| S3 | `ServerHost.cs` | `OwnerScoped` 读 `arguments.owner`（缺失/非整数/不等 → `unauthorized`）；`SeatRequired` 只要求已绑定 |
| S4 | `src/Orc.Server/Room/Room.cs` | `StartAsync`/`Release`/`Kick` 在 `HostSlot()==null` 时 `unauthorized`；`CanControl` 改为显式传 host |
| S5 | `ServerHost.cs`、`src/Orc.Mcp/Match/MatchExecutor.cs` | AI 步内推送、判负成功后才推、`Settled` 事件 + `_handedOff`（`Interlocked/Volatile`）、`EnqueuePush` |
| S6 | `ServerHost.cs` | 断线记录只在认输成功后移除；`_graceInFlight` 去重；不轮到时保留重试 |
| S7 | `src/Orc.Mcp/Room/RoomSnapshotJson.cs`（新增）、`RoomTools.cs`、`RoomContracts.cs`、`Room.cs`、`ServerHost.cs` | `notify/room` 广播（claim/release/kick/add_ai/断线释放）；`room.info` 与推送共用序列化；删 `RoomSeatSnapshot.Ready` |
| S8 | `src/Orc.Client/OrcClient.cs`、`ClientOptions.cs` | 心跳循环（空闲 → `ping` → `HeartbeatTimeout` 判死）、`Close()`、默认 30s/15s |
| S9 | `OrcClient.cs` | 订阅者逐个 try/catch、非对象帧/数字 id 容忍、`IsConnected` 纳入 `_closedReason`、写失败/取消清理 pending |
| S10 | `src/Orc.Server/Execution/SerialWorkQueue.cs` | `DisposeAsync` drain、`WorkItem.Fail` |
| S11 | `ServerHost.cs`、`Room.cs`、`DeckRulesValidator.cs`、`ServerBootstrap.cs`、`McpMatchLauncher.cs` | 捕获 `SocketException`；`Snapshot()` 锁外枚举网卡；`RequiredDeckSize` 透传；注释如实 |
| S12 | `src/Orc.Protocol/NdjsonFraming.cs` | 自写增量 reader（按字节限长、BOM/CRLF/空行/多字节语义不变） |
| S13 | `MatchExecutor.cs`、`McpService.cs`、`TargeterBridgeAdapter.cs`（未改，仅复用 `Fault`） | `WaitAsync`/`RunAsync` 纳入桥故障、`TryFailFaultedAction`；`McpService.InvokeAsync` 的 busy 门禁先收束故障动作 |
| S14 | `tests/Orc.Server.Tests/*`（新增 `SecurityTests/PushDeliveryTests/RoomPushTests/ClientHeartbeatTests/ClientRobustnessTests/NdjsonFramingTests/TargetingFaultTests/TestSupport`）、`EndToEndTests/RoomTests/ResilienceTests/SkeletonSmokeTests/ServerProcessTests/TransportTests`；`tests/Orc.Mcp.Tests/FakeRoomControl.cs` | 新增 53 例；修 4 处弱断言 + 1 处假覆盖；`RoomSeatSnapshot` 夹具同步 |
| S15 | `docs/多人支持-服务端客户端分离/客户端接入指南.md` | §4.3 心跳、§5.2/5.2.1 座位作用域与绑定不变量、§6.2 删 `ready`、§6.6 `notify/room`、§8.1/8.5/8.6、§9、§11 路径、§12 `match.ended`、§14.1 推送方法 |

### 新增/修改的测试与最终计数

- `Orc.Server.Tests`：61 → **114**（+53）。分文件：`SecurityTests` 27、`PushDeliveryTests` 6、`RoomPushTests` 4、`ClientHeartbeatTests` 4、`ClientRobustnessTests` 4、`NdjsonFramingTests` 3、`TargetingFaultTests` 2、`SerialWorkQueueTests` +1、`RoomTests` +2。
- 全量：**2262 通过 / 0 失败**（`Orc.Script 17 / Orc.Tests 370 / Orc.Mcp 80 / Orc.Ai 13 / Orc.Packages 355 / Orc.Server 114 / Orc.Game 1313`）。
  - 说明：本工作区同时有另一个 agent 在改 `Orc.Packages`/`Orc.Mcp`（共享同一工作树），`Orc.Packages` 298→355、`Orc.Mcp` 76→80 属**对方**新增；我只新增/修改了 `Orc.Server.Tests` 与文档。
- 红→绿证据：S2/S3 的 10 条用例在"关闭专门分支 + 关闭 owner 比对"后逐条失败（实测 20 例中 10 失败），恢复实现后全绿；S1 的新增探针在修复前不存在（编译失败＝红）；S8/S9/S10/S12/S13 的用例在实现落地前分别因"无 ping → 连接被心跳判死"、"数字 id 抛 `InvalidOperationException` 杀读循环"、"Dispose 后排队项挂起"、"`StreamReader` 先缓冲整行"、"busy 永真"而失败。

### 与文档不一致或需要主 agent 裁决的点

1. **`SeatScope` 可见性**：文档写 `internal enum SeatScope`，但又要求 `public static SeatScopeOf`。`Orc.Server` 没有 `InternalsVisibleTo`，public 方法不能返回 internal 类型 → 枚举保持 `internal`，`ServerHost.SeatScopeOf` 返回**作用域名 `string?`**（`"bind"/"owner-scoped"/"seat-required"/"agnostic"/"special"`）。守卫测试只用 `IsToolClassified` 与 `RegisteredToolNames`。
2. **S14 第 3 例（非回环 claim local）走了真实连接**：本机存在非回环 IPv4（`10.130.35.183`），用例 `ClaimLocal_FromNonLoopback_IsRejected` 通过该地址真连；同时保留 `SeatScopePolicy.TrustsOnlyLoopback` 单测与 `ClaimLocal_FromLoopback_IsAccepted` 集成用例。若 CI 无 LAN 地址，用例自动退化为策略单测（同文档兜底方案），**需要主 agent 决定 CI 是否接受该降级分支**。
3. **S1 的"被拒绝请求不入队"断言**：`ServerHost` 未暴露队列快照，改为断言"拒绝后引擎状态/`IsBusy` 不变"。若需硬断言 `PendingCount` 不变，建议加一个 `ServerHost.PendingWorkCount` 诊断属性（本批未加）。
4. **S5 的"三处推送均以 `WorkPriority.Command` 入队"无独立用例**：三处都走同一个 `EnqueueAsync(WorkPriority.Command, …)`（AI 步/判负在动作工作项内，`Settled` 走 `EnqueuePush`），没有可观测的插队面；顺序由 `TargetingTimeout_PushesExpiredThenView` 间接覆盖。是否加内部探针请裁决。
5. **S2 `pending.Seat == null` 的"先后应答"完整链路不可构造**：引擎对**所有真实选择器**都注入 `OwnerSeat`，示例卡包也造不出"无归属"的真实动作。改为两段覆盖：①门禁层（未绑定 `unauthorized` / 已绑定放行，`TargetSubmit_NullSeatPending_UnboundRejected_BoundAllowed`）；②真实动作完成后后到者 `invalid-arguments`（`TargetSubmit_AfterCompletion_IsInvalidArguments`）。
6. **S13 增补了一处文档未写的落点**：`McpService.InvokeAsync` 的"挂起期门禁"在进入执行器**之前**就把请求拦成 `busy`，所以仅在 `MatchExecutor.WaitAsync/RunAsync` 处理故障不足以让 F18 转绿——必须先 `TryFailFaultedAction` 收束故障动作。该增补是 F18 可验收的必要条件。
7. **`EndToEndTests:77-79` 的修订方式与文档建议不同**：文档建议断言 `selfPlayerName` 不同，但该字段是**相对标签**（两侧都是"我方"）。改为更强的交叉核对：两侧 `selfHand` 的 uid 集合不相交，且一方 `selfHand` 张数 == 另一方 `enemyHandCount`。
8. **UnbindSeat 调用点③（`room.add_ai`）与④（lobby 断线）只能防御性命中**：在① ② ⑤ 正确的前提下，不存在"座位为空但仍被绑定"的可构造状态，因此两条路径的效果无法从协议面观测。用例保证的是可观测不变量（release/kick 后原会话立即 `unauthorized`、断线释放后座位可被他人 claim、`notify/room` 广播），而非"删除该行会红"。若要求逐行可红，需要在 `ServerHost` 上加测试钩子（不建议为测试开洞）。
9. **S7 删除 `Ready` 是破坏性变更**：连带改了 `Orc.Mcp.Tests/FakeRoomControl`、`TransportTests` 的夹具与 `RoomTests` 的断言（原 `Assert.True(Seats[0].Ready)` → `Connected`）。
10. **S15 未改 `requirements.md`**：按执行要求（由主 agent 维护）。其中"心跳已实现"等表述与 `客户端接入指南` 的基线数字（2148）仍需父 agent 同步。
11. **`Room.Release`/`Kick` 在无房主时改为 `unauthorized`**：这会牺牲"两个远程座位、无房主"房间的自助释放能力（文档 S4 Risk 已预见）。`RoomTests` 未覆盖该新分支的"有房主不受影响"以外情形，新增的反向用例在 `SecurityTests.RoomStartReleaseKick_WithoutHost_AreUnauthorized`。
12. **文档 S12 的"内存不随报文增长"**：已用 `CountingStream` 断言"读走 ≤ 1 MiB + 64 KiB 即失败"，而非测量进程内存（更稳定）。上限单位已改为**字节**，`Ndjson.MaxLineLength` 注释同步。

## 复核轮执行记录（2026-10-21，第二遍执行子 agent）

> 目的：独立复核上一轮"全部 Done"的结论，并按用户执行要求重跑「重建 + 全量测试」。
> 结论：S1–S15 的落地属实（分类表、门禁顺序、解绑五点、推送三点、心跳、drain、增量限长、选靶异常传播均在代码里可定位，新增用例非空断言）；复核中发现**一处真实缺陷**（F12 的并发漏点），已按「先红后绿」修复。

### 本轮唯一的代码改动

| 文件 | 改动 | 为什么 |
|---|---|---|
| `src/Orc.Client/OrcClient.cs` | `CallRawAsync` 的"连接已关闭"检查移入 `_pendingGate` 临界区（与 `_pending.Add` 同一把锁） | 原实现先查 `_closedReason` 再取锁登记，存在 `检查通过 → 读循环判死并 `FailAllPending` 清空 → 本请求登记` 的窗口，留下**永远不会被完成**的 pending 条目（`_closedReason` 已置位后不再有人清理它）。这正是 F12「pending 不泄漏」的漏点，只在全量并行负载下显现：单跑该用例 5/5 绿，全量跑偶发红（实测 `_pending` 残留 1 条 `["1"]`） |
| `tests/Orc.Server.Tests/ClientRobustnessTests.cs` | `FailedWrites_DoNotLeakPendingEntries`：① 5 次调用改为逐个断言**确实抛出异常**（原先丢弃结果）；② 残留断言改为「有限时间内归零」（读循环的清理是异步的） | 原断言取瞬时快照，断言强度依赖调度时序；新断言的强度更高（既要求失败，也要求最终不残留），且不受 CI 负载影响 |

> 红→绿证据：全量运行 `dotnet test OrcEngine.slnx` 时该用例红（`Collection was not empty: [["1"] = TaskCompletionSource]`）→ 修改 `OrcClient` 后单项目连跑 3 次全绿（114/114）→ 全量再跑绿（2263/2263）。

### 最终计数（本轮实测）

- `dotnet build OrcEngine.slnx -t:Rebuild` → **0 错误**，4 警告（`Orc.Game`/`Orc.Game.Tests` 的既有可空性警告，非本批文件）。
- `dotnet test OrcEngine.slnx` → **2263 通过 / 0 失败 / 0 跳过**。
  - `Orc.Tests 370 / Orc.Script 17 / Orc.Mcp 80 / Orc.Ai 13 / Orc.Packages 356 / Orc.Server 114 / Orc.Game 1313`。
  - 与上一轮 2262 的差值 = `Orc.Packages` 355→356（并行 agent 新增，非本批）。
- `Orc.Server.Tests` 114 例的构成复核：`SecurityTests 27 / PushDeliveryTests 6 / RoomPushTests 4 / ClientHeartbeatTests 4 / ClientRobustnessTests 4 / NdjsonFramingTests 3 / TargetingFaultTests 2 / SerialWorkQueueTests 6 / RoomTests 15` 等，与上一轮记录一致。

### 环境注意（非代码问题，复现时先看这里）

1. **共享工作树并发冲突**：本工作区同时有另一个 agent 在改 `Orc.Packages`/`Orc.Mcp` 并跑 `dotnet build OrcEngine.sln --no-incremental`。并发构建会抢占 `Orc.Server.Tests.runtimeconfig.json`、`Orc.Game.pdb`、`Microsoft.CodeAnalysis.CSharp.dll`，并让**内容文件（`Cards/KeywordPrefabs/*.prefab.json`）复制不完整**——表现是 `Orc.Game.Tests` 大批量报「词条效果库绑定核验失败（prefab 缺失）」。这类失败与本批改动无关，重建即可恢复（已实测：重建前 583 红，重建后 1313 全绿）。
2. 因此复跑验收时请确保无其他 `dotnet` 进程在跑（`Get-Process dotnet`），否则「文件被占用 / 内容缺失」的噪声会掩盖真实结论。

### 本轮新增的、需要主 agent 裁决的点

13. **F12 的语义边界**：复核发现「写失败」并非唯一残留路径——连接被对端关闭后，读循环与 `CallRawAsync` 之间存在登记竞态。已用「检查与登记同锁」消除，并把用例断言改为「失败 + 最终归零」。若主 agent 认为应当额外为 `CallRawAsync` 加「登记后二次校验 + 主动清理」的兜底，请明确（当前实现依赖 `FailAllPending` 与临界区互斥，已能覆盖）。
14. **上一轮第 1–12 条结论本轮未改**，其中第 2 条（非回环用例的真实连接 + `SeatScopePolicy` 单测 + loopback 集成用例三件套）已逐条核对确认存在，且 `NonLoopbackIPv4()` 返回 `null` 时会自动退化为策略单测——符合用户给定的兜底口径；第 3 条（`PendingCount` 硬断言）与第 4 条（推送优先级独立用例）仍建议由主 agent 决定是否加测试钩子。
