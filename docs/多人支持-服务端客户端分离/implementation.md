# Implementation — 多人支持（服务端 / 客户端分离）

> 状态：**实现完成**（S1–S8e 全部 Done；S4b 交付候选集；S9 主要用例 Done）。
> 最终回归：`dotnet test OrcEngine.slnx` → 2148 通过 / 0 失败。
>
> 交付文档：
> - [`requirements.md`](./requirements.md)：需求与验收（按真实实现状态勾选）
> - [`implementation.md`](./implementation.md)：实现步骤、取舍与已知限制（本文）
> - [`客户端接入指南.md`](./客户端接入指南.md)：**前端交接文档**（自包含：改动清单 / 状态机 / 全部 API / 字段表 / 时序 / 迁移 / 排查）
> - [`requirements-grill-plan.md`](./requirements-grill-plan.md)：需求取证与问答记录
> - [`implementation-grill-plan.md`](./implementation-grill-plan.md)：实现问答记录

## Scope

- Target：在不改动引擎规则语义的前提下，把 `Orc.Mcp` 之上补出「房间 / 座位 / 控制器」，并新增一个传输宿主进程，使 Godot 客户端、外部客户端、agent 客户端只经 MCP 外形的 JSON 协议与逻辑引擎交互。
- Excludes：
  - 观战 / 回放 / 账号 / 匹配 / 排行榜 / 生涯数据（R10）。
  - 多房间/多对局（一实例一局）。
  - 卡组资产（后续演进）。
  - Godot 侧的具体 UI 改造（仅约定协议面与客户端侧必需改动）。

## 步骤顺序与里程碑

依赖关系（→ 表示"先于"）：

```
S1 工程骨架（Orc.Server / Orc.Client / Orc.Protocol / Orc.Server.Tests）
 ├→ S2 协议信封 ──→ S2b 串行管道
 ├→ S5 段脱敏（纯内核，独立可测）
 └→ S3 房间状态机 ──→ S7 卡组规则与建局 ──→ S4 投影/actions ──→ S6 选靶路由
                                                        └→ S8 客户端拉起
S9 e2e（消费 S1–S8）
```

执行顺序：**S1 → S2 → S2b → S5 → S3 → S7 → S4 → S6 → S8 → S9**。
（S5 早做：它是纯内核改动，先暴露 `$entity` → `$ref` 的测试改动面，避免后期与 S4 混在一起。）

里程碑与验证：

| 里程碑 | 包含步骤 | 可验证的结果 |
|---|---|---|
| M1 服务端闭环 | S1+S2+S2b+S3+S7 | 测试客户端连入、填 seat、开局，跑完一局（无 Godot） |
| M2 客户端可见 | +S4+S5+S6 | Godot 可渲染 view+actions、可选靶、无隐藏信息泄漏 |
| M3 单机体验 | +S8 | 客户端内"开启房间"一键可用，退出无残留 |
| M4 双人 | +S9 | 双客户端 e2e、断线重连、旧测试全绿 |

## Implementation Steps

### S1: 工程落点（房间层 / 传输层 / 客户端库）
- Status: **Done**
- Target: `src/Orc.Server/`（房间状态机 ＋ 传输 ＋ 会话 ＋ 进程宿主）、`src/Orc.Client/`（协议客户端 ＋ 推送订阅 ＋ DTO）、**`src/Orc.Protocol/`（新增，见下）**、`tests/Orc.Server.Tests/`；`src/Orc.Mcp/` 只加窄接口与工具注册。
- 计划调整：协议消息类型服务端与客户端都要用，放 `Orc.Server` 会让 `Orc.Client` 反向引用整个引擎。故新增零引擎依赖的 `Orc.Protocol`：
  `Orc.UI.Contracts ← Orc.Protocol ← { Orc.Client, Orc.Server }`，`Orc.Server → Orc.Mcp`。
- Approach:
  - 房间/座位/控制器/会话状态全部在 `Orc.Server`；传输（NDJSON/TCP）同属该工程。
  - `Orc.Mcp` 新增窄接口 `IRoomControl`（纯 DTO 入参/出参，**不得出现引擎活对象**），`McpService` 构造可选注入；`room.*` 工具仍在 `ToolRegistration` 注册并转调该接口（保证单一工具表与单一模式门禁）。
  - 依赖方向单向：`Orc.Server → Orc.Mcp`。
  - 开局由 `Orc.Server` 的房间状态机调用 `McpService.CreateMatchAsync`（public 既有入口）。
- Acceptance:
  - [x] 解决方案可构建（0 警告 0 错误），四个新工程在 `OrcEngine.slnx` 注册。
  - [x] 依赖方向单向：`Orc.Server → { Orc.Mcp, Orc.Protocol }`、`Orc.Client → Orc.Protocol`、`Orc.Protocol → Orc.UI.Contracts`，无环。
  - [ ] 房间状态可被 `room.*` 工具读写，且受现有模式门禁与白名单管辖（S3）。
  - [ ] 传输层可替换承载（TCP/WebSocket/测试内存通道）而不改业务代码（S2/S8）。
  - [ ] `IRoomControl` 的签名中不出现 `Match`/`Player`/`Card`/`Ref<T>` 等引擎类型（S3）。
  - [ ] 房间状态机可脱离引擎单测（fake `IRoomControl` 实现）（S3）。
- Rationale: 用户选择 b——房间逻辑与引擎解耦，可独立单测、独立演进；代价是多一层窄接口，且该接口是新的"活对象泄漏点"，必须用纯 DTO 约束死。
- Terminology: [房间](../CONTEXT.md)、[房间态](../CONTEXT.md)、[座位](../CONTEXT.md)、[控制器](../CONTEXT.md)、[会话](../CONTEXT.md)

### S2: 协议信封与会话层
- Status: **Done**（信封与帧已完成；会话层在 S8 接传输）
- Target: `src/Orc.Server/Protocol/`
- Approach:
  - 严格 JSON-RPC 2.0：请求 `{jsonrpc,id,method,params}`，响应 `{id,result}` / `{id,error:{code,message,data}}`。
  - **业务失败放 `result`**：`{ok:false, code, message, details}`；`error` 只用于 -32700/-32601/-32602 这类协议错误。
  - `isError` 重新定义为"工具执行失败"（业务失败即 true），不再沿用"仅 internal"的旧规则。
  - `initialize` 返回 `{protocolVersion, schemaVersion, capabilities}`；`schemaVersion` 复用 `ContractSchemaVersion`；不匹配即拒绝。
  - 服务端推送＝无 `id` 的 notification。
- Acceptance:
  - [ ] 单条 NDJSON 帧可解析出 method/params/id；未知方法返回 -32601。
  - [ ] 业务失败不占用 `error` 通道，且 `details` 可承载结构化载荷。
  - [ ] 客户端仅凭 `ok`/`code` 即可判定结果，无需两处判断。
- Rationale: stdio 示例已是此形状；协议错误与业务失败分离，客户端判定单点化。
- Terminology: 协议信封（待登记）

### S2b: 串行执行管道（唯一写者）
- Status: **Done**（队列完成；接入 `McpService` 与 AI 在 S3）
- Target: `src/Orc.Server/Execution/`（有界 Channel ＋ 单消费者）
- Approach:
  - 所有入口（工具调用、`target.*`、AI 动作、房间操作、定时器）投递到同一条有界队列，单消费者串行 `await` 执行；网络线程只入队，结果经 TCS 回传。
  - 优先级：`target.*` / `session.*` > 对局动作；队列满返回 `busy` 业务错误，不阻塞、不堆积。
  - **AI 托管客户端同样入队**，禁止直接调用 `IMcpActions`。
  - 投影在消费者上下文内计算，结果投给独立写队列异步发送（慢客户端不拖引擎）。
- Acceptance:
  - [ ] 任意并发压力下，同一时刻只有一个 WorkItem 在访问引擎。
  - [ ] `target.submit` 能在动作挂起后立刻被处理，不被普通动作阻塞。
  - [ ] AI 动作与远程动作共享同一队列。
- Rationale: `SemaphoreSlim` 只能互斥、不能表达优先级；集中到一条队列后"谁在访问引擎"可单点排查。
- Terminology: [对局执行器](../CONTEXT.md)

### S3: 房间状态机与工具面
- Status: **Done**
- Target: `src/Orc.Mcp/Room/RoomContracts.cs`（`IRoomControl` + 纯 DTO）、`src/Orc.Mcp/Tools/RoomTools.cs`、`src/Orc.Server/Room/Room.cs`、`RoomControl.cs`
- Approach:
  - `IRoomControl` 为服务层唯一房间入口，入参/出参纯 DTO（无 `Match`/`Player`/`Card`/`Ref<T>`）；未注入＝`room.*` 整组不登记。
  - `room.*` 七条：`info`/`claim`/`release`/`add_ai`/`start`/`kick`/`resume`；`start` 为 `Task`（避免 sync-over-async）。
  - `Room` 状态机：Lobby →（座位填满 + 房主令牌）→ Starting → InMatch → Ended；开局经 `IMatchLauncher`（S7 接 `CreateMatchAsync`），卡组经 `IDeckValidator`（S7 接 `DeckRules`），二者均为接口，故房间可脱引擎单测。
  - 房主＝`kind: local` 的座位；无 local 座位时开局不校验令牌（单机/测试便利）。
  - 房间操作用实例锁（短临界区）；与引擎动作的串行化在 S8 的分发层统一入队。
  - `McpCodes` 增补房间码（`busy`/`seat-taken`/`unauthorized`/`room-not-ready`/`room-started`/`deck-invalid`），与 `ResultCodes` 同名同值（有一致性测试）。
- Acceptance:
  - [x] `room.*` 覆盖：查询快照、声明 seat、释放 seat、生成 AI、开局、踢人、重连。
  - [x] 工具面受现有模式门禁与白名单管辖（`ToolWhitelistTests` 已扩到 31 条并通过）。
  - [x] `IRoomControl` 签名不含引擎类型。
  - [x] 房间状态机可脱引擎单测（fake validator/launcher，12 个用例）。
- Rationale: 房间先于对局存在，不能复用 `MatchExecutor`；把"开局装配"与"卡组校验"抽成接口，既满足可测性，也让 S7 的替换不波及状态机。
- Terminology: [房间](../CONTEXT.md)、[房间态](../CONTEXT.md)、[座位](../CONTEXT.md)、[控制器](../CONTEXT.md)、[座位令牌](../CONTEXT.md)

### S4a: 投影与动作可用性（第一层）
- Status: **Done**
- Target: `src/Orc.Mcp/Execution/ConsumerContext.cs`、`Match/MatchExecutor.cs`（`ReadOnly`）、`McpService.Projection.cs`、`Tools/ObserveTools.cs`
- Approach:
  - `ConsumerContext` 放在 `Orc.Mcp`（`AsyncLocal<bool>`），由 `Orc.Server` 的 `SerialWorkQueue` 在消费者内开启——避免 `Orc.Mcp → Orc.Server` 的反向依赖。
  - `MatchExecutor.ReadOnly(read)`：不取 `_gate`、不判 `Busy`，不在消费者上下文内直接抛。
  - `McpService.ProjectView(seat)`：整枚 `UiMatchView`（`artLookup`/`definitionLookup` 传 null）＋ `actions` 同拍产出；载荷含 `view`/`actions` 与兼容旧 `observe.view` 的扁平标量。
  - `observe.view` 改为转调 `ProjectView`（不再自己投影）。
  - `actions` 第一层：`playableUids`（逐张 `PlayManager.CanPlay`）、`handPlayabilityKnown`、`canEndTurn`、`attacksEnabled`、`mulliganOpen`。
- Acceptance:
  - [x] 客户端单条消息即可获得 `view` + `actions`。
  - [x] 非消费者上下文误用只读面被拒（返回 `engine-rejected`，有测试）。
  - [x] 整枚投影含手牌/双方战线数组（有测试）。
- 契约口径（客户端必须按此解析）：
  - `view.phase` 是**小写字面量** `mulligan` / `play` / `over`（不是枚举名）；
  - `view.outcome` 经字符串枚举序列化，取 `Win` / `Lose` / `None`；
  - `actions.playableUids` 与 `view` 中卡片 `uid` 同为实体 GUID（N 格式）。
- Rationale: 读路径与写路径共用门禁会在 `IsBusy` 时整体失效；前端桥接层本来就是直读引擎算 actions，服务端沿用同一语义。
- Terminology: [对局执行器](../CONTEXT.md)、[投影](../CONTEXT.md)

### S4b: 动作可用性的候选集
- Status: **Done（候选集版本）**
- Target: `src/Orc.Mcp/McpService.Projection.cs`
- Approach:
  - `deploymentGaps`：`PlayManager.GetDeploymentGaps(unit)` → `[{ uid, gaps: [gapRef] }]`（引擎既有 API，只是接线）。
  - `moves`：`GetCommandAvailability(unit).Move.CanUse` 为真 → 前线空槽 `[{ uid, toZone: "front", slotIndex }]`（引擎注释：移动候选＝前线空槽，无后撤/横移）。
  - `attackPreviews`：`Attack.CanUse` 为真 → 敌方前线单位；敌前线为空时指向敌方 HQ。
    `damageToDefender` / `damageToAttacker` ＝**有效攻击力**（`Modifiers.GetEffectiveValue(Attack)`，含修饰器链），HQ 不计反击；
    `defenderDies` / `attackerDies` 恒 `false`。
- **伤害预测的边界（重要）**：引擎的伤害可被**伤害管线上的 handler** 改写（重甲减伤、伏击改写、免疫归零，以及 csx 效果注册的改写），
  这些改写发生在真正 Apply 时。要"准确预演"必须跑一遍管线，而管线有副作用、引擎明确不承诺回滚。
  因此本实现的 `damageTo*` 是**基础伤害值，不是最终伤害**：客户端只能据此显示"大致伤害"，不得作为权威数值；
  最终以对局实际结算为准。
  - `orderTargets` 恒空，**改为交互式提供**（用户定稿：拖出即预打出）。
    **交互契约**：
    1. 客户端拖动指令卡 = 调 `play.command_pre {ref}`（不是本地预览）；
    2. 服务端走 `BeginOrderPrePlayAsync` → 预打出段 → 效果 handler 发起选靶 → 推 `notify/target.request`（候选＝真实目标，按 seat 定向）；
    3. 玩家拖到候选上 = `target.submit`；拖回/ESC = `target.cancel`；
    4. **取消零副作用**（引擎保证：预打出失败/取消＝打出不发生、不发更新、卡留手、无扣费/离手）。
    **为什么不做静态数组**：指令目标由效果 handler 运行期构造的 `EffectSelector` 决定，候选再过 `effect.target.resolve` 判定器（可被 wrapper 改写）；
    `CardDefinition` 与 `EffectSnapshot` 都没有目标声明字段。静态产出等于执行 handler。
    **示例数据限制**：`samples/card-pack-ai` 的指令是空效果（无选靶），无法演示本契约；需真实卡包（带选靶的指令）验证。
    **成本提示**：预打出会挂起动作（选靶期间执行器忙，其他动作被门禁挡住），直到提交或取消——这是"拖出即进入选择"的固有代价。
- Acceptance:
  - [x] `deploymentGaps` 接线并随可打出单位出现（e2e 断言非空）。
  - [x] `moves`/`attackPreviews` 结构与引擎候选一致（初期局面为空，无法在示例包下构造非空场景）。
  - [ ] `orderTargets`（明确降级到选靶请求，不在本阶段）。
- Rationale: 候选集足够让客户端高亮操作；伤害预测是独立的正确性面，等真有需求再做。
- 已知限制：`moves` 只覆盖“推进到前线空槽”；`attackPreviews` 不判断护卫/拦截等词条效果（`IsUnitGuarded` 未接入）——实际是否可攻击仍以引擎裁决为准，客户端应按“候选”而非“保证”呈现。

### S5: 段按座位过滤
- Status: **Done**
- Target: `src/Orc/Output/IEventVisibility.cs`、`JsonValueWriter.cs`、`EventStreamJson.cs`、`EventSegmentJson.cs`、`src/Orc.Game/Ui/MatchEventVisibility.cs`
- Approach:
  - 内核新增 `IEventVisibility`：`IsEntryHidden(payload, viewer)`（条目级）＋ `IsEntityVisible(entity, viewer)`（实体级兜底）。
  - `JsonValueWriter` / `EventStreamJson` / `EventSegmentJson` 增加**可选**可见性重载（旧签名保留，单机路径零改动）；实体统一输出 `{"$ref": GUID, "name": 名, "$alive": bool}`，不可见输出 `{"$hidden": true}`（不带名字、不带引用键）。
  - 计划调整：实现放 `Orc.Game/Ui/MatchEventVisibility.cs`，**不是** `Orc.UI`。查证后发现投影层的 `Visibility` 恒为 `Full`，脱敏靠"结构性省略"（不给对手手牌），没有可复用的判定逻辑。
  - 条目级判定是必需的：`card.drawn` 发射时卡已离开卡组、尚未进手牌，容器归属判不到，必须靠 payload 里的 `player`。
  - `Orc.Server` 注入在 S4 推送链路完成。
- Acceptance:
  - [x] 不可见实体输出 `{"$hidden":true}`，不含名字与引用键。
  - [x] 条目级判定：对手抽牌条目整体隐藏，自己的不隐藏。
  - [x] 实体级兜底：对手手牌不可见、自己手牌与 HQ 可见；两张**同名**卡（同一 CardDefinition）按实例区分，不误判。
  - [x] 现有 `$entity` 断言已同步改为 `$ref`/`name`/`$alive`，全量测试绿（2088 通过）。
- Rationale: 导出后无法按名字反查实体（同名卡无法区分），必须先输出 GUID 引用键；接口必须在内核（内核不能引用 `Orc.UI`）。
- Terminology: [段](../CONTEXT.md)、[引擎托管引用表](../CONTEXT.md)
- Rationale: 导出后无法按名字反查实体；接口必须在内核（内核不能引用 `Orc.UI`）；实现放 `Orc.UI` 才能与投影共用一份规则。
- Terminology: [段](../CONTEXT.md)、[引擎托管引用表](../CONTEXT.md)

### S6: 选靶归属与路由
- Status: **Done**（路由归属已完成；claim 保留为兼容壳）
- Target: `src/Orc.Game/Targeting/Selector.cs`、`SelectorPresentation.cs`、`TargeterSession.cs`、`src/Orc.Mcp/Match/TargeterBridgeAdapter.cs`、`McpService.AiGateway.cs`
- Approach:
  - `SelectorParameter.Owner`（`Player?`，init）＋ `SelectorPresentation.OwnerSeat`（`int?`，`internal set`）。
  - **单点注入**：`TargeterFlow.Step` 里 `instance.Presentation.OwnerSeat = parameter.Owner?.Index`——不侵入 4 个 `CreateInstance` 重载。
  - `PendingSelection` 增 `Seat`（`int?`，末位默认 null）；`PendingJson` 增 `seat`。
  - 桥取值优先级：`OwnerSeat` → `Presentation.Parameter as Player`（零改动兼容既有路径）。
  - 已补 owner 的构造点：`EffectRuntime.BeginTargetSelectionAsync`（`OwnerOf(viewer)`）、`PlayManager` 部署选位（`card.Owner`）。
  - `IAiMatchGateway.SubmitAsync` 校验：待决请求声明了归属且与 AI 座位不一致 → 直接失败。
  - `claim` 相关 API 保留但**语义退役**（不再决定路由）；测试仍覆盖其向后兼容行为。
- Acceptance:
  - [x] `PendingSelection.Seat` 正确反映归属座位（`HostIntegrationTests` 断言 0 号座位打出的卡 → seat 0）。
  - [x] 全量测试绿（2121 通过）。
  - [ ] 跨玩家选择（A 打出、B 选择）的路由用例（需效果级夹具，S9 补）。
  - [x] `target.expired` 推送（`TargeterBridgeAdapter.PendingExpired` → `notify/target.expired`，按 seat 定向；有 e2e）。
- Rationale: 引擎全链路无 owner；用 `SelectorParameter` 承载、在 `TargeterFlow.Step` 单点注入是最小侵入；`claim` 直接删除会破坏 AI 与宿主既有路径，改为"保留 API、退出路由"。
- Terminology: [控制器](../CONTEXT.md)、[座位](../CONTEXT.md)

### S7: 建局链路与卡组规则
- Status: **Done**
- Target: `src/Orc.Game/Decks/DeckRules.cs`、`src/Orc.Mcp/Match/MatchLaunchOptions.cs`、`McpService.MatchLaunch.cs`、`src/Orc.Server/Room/McpMatchLauncher.cs`、`DeckRulesValidator.cs`
- Approach:
  - `DeckRules.Validate(deck, pool, requiredSize)` 为**唯一实现**：张数、卡池存在性、同名上限按 `Rarity` 分档（standard 4 / limited 3 / special 2 / elite 1）；`ValidateFaction` 复用 `FactionRules`（主国必填/五大主国、盟国≠主国≠中立）。
  - `MatchSeatConfig` 扩为 `(Seat, Deck, Name, DeckConfiguration)`；`CreateMatchAsync` 把构筑配置传给 `Match` 的 `deckConfigForPlayerA/B`（此前一直收 null）。
  - `CreateMatchAsync` 在卡组装载后执行 `DeckRules` 校验（卡 id 已是解析后的卡池键）；失败返回 `deck-invalid`，消息含具体卡 id 与违规类别。
  - 新增 `MatchLaunchOptions.EnforceDeckRules`（默认 true）：**协议面（`McpMatchLauncher`）硬编码 true**，只有诊断/测试可关。
  - `McpMatchLauncher`：房间座位 → `MatchLaunchOptions`；AI 空卡组经注入的 `aiDeckFactory` 生成（S8 启动链路提供）。
  - `DeckRulesValidator`：房间态校验适配（卡池由启动链路注入）。
- Acceptance:
  - [x] controller 的卡组/构筑配置完整落入 `Match`（不再收 null）。
  - [x] 非法卡组在建局期被拒，错误可定位到具体卡 id 与违规类型。
  - [x] 同名上限按稀有度生效（4/3/2/1，有单测）。
  - [x] 现有测试全绿（2115 通过）。
- 已知取舍：示例卡包（`samples/card-pack-ai`）只有 5 张卡，**不可能**组成 39 张合法构筑，因此 3 个诊断/AI 测试显式关闭 `EnforceDeckRules`——这是数据不足，不是放松生产校验。
- Rationale: 用户选择完整 KARDS 口径；稀有度已是四值字符串常量，可直接映射上限表；把规则放 `Orc.Game` 才能被建局与房间两处共用。
- Terminology: [卡牌标签](../CONTEXT.md)、[控制器](../CONTEXT.md)

### S8a: 传输链路（服务端宿主 + 共享客户端）
- Status: **Done**
- Target: `src/Orc.Server/Transport/ServerHost.cs`、`src/Orc.Client/OrcClient.cs`、`src/Orc.Protocol/ToolDescriptorPayload.cs`
- Approach:
  - `ServerHost`：`TcpListener`（端口可 0）＋ NDJSON 帧 ＋ 每连接读循环；所有 `tools/call` 入 `SerialWorkQueue`（`target.*`/`session.*` 为 Interactive 优先级）。
  - 方法集：`initialize`（版本协商，不匹配拒绝）、`ping`、`tools/list`（`ToolDescriptorPayload`）、`tools/call`。
  - 错误分层：解析/未知方法/参数错误走 JSON-RPC `error`；业务失败走 `result`（`ok/code/message/details`）。
  - `OrcClient`：连接、握手、请求-响应（id→TCS）、`notify/*` 订阅；不含业务规则。
- Acceptance:
  - [x] 真 socket（loopback）握手成功，`schemaVersion` 与 `capabilities` 正确。
  - [x] `tools/list` 含 `room.*`。
  - [x] `tools/call room.info` 返回房间快照。
  - [x] 未知方法返回 -32601；版本不匹配被拒；业务失败不占用 `error` 通道。
- Rationale: 先把"能连上"这条最大未验证风险消除；推送与进程治理留到 S8b。
- Terminology: [服务端](../CONTEXT.md)、[客户端](../CONTEXT.md)

### S8b: 启动链路与 AI 卡组
- Status: **Done**
- Target: `src/Orc.Server/ServerBootstrap.cs`、`Program.cs`、`Room/AiDeckGenerator.cs`、`Room/LocalAddresses.cs`、`ServerOptions.cs`
- Approach:
  - `ServerBootstrap.StartAsync`：装载卡池（`MatchCardAssembly.Register`）→ 组装 `Room`/`DeckRulesValidator`/`McpMatchLauncher` → 创建 `McpService`（Player 模式）→ 启动 `ServerHost`。
  - 构造环用 lambda 打破：`McpMatchLauncher` 接收 `Func<McpService>`（房间需要装配器、装配器需要服务、服务需要房间控制面）。
  - `Program`：`--packs/--sandbox/--port/--help`；stdout 只写一行 `{"ready":true,"port":N}`，日志走 stderr；Ctrl+C / ProcessExit 优雅退出。
  - `AiDeckGenerator`：随机主国 → 主国＋中立池 → 按稀有度上限填满；容量不足返回空（由装配器报 `deck-invalid`，不生成非法卡组）。
  - `LocalAddresses`：非回环 IPv4 ＋ 实际端口，供 `room.info`。
- Acceptance:
  - [x] bootstrap 起监听并服务 `room.info`（端口 > 0、两座位、`state: lobby`）。
  - [x] AI 卡组生成遵守同名上限；容量不足时返回空（有测试）。
  - [x] stdout 就绪行格式固定（客户端据此取端口）。
- Rationale: 先打通"能一键起来并服务房间"，把推送/进程回收留到 S8c——它们不改变这条主链路。
- Terminology: [房主](../CONTEXT.md)、[房间态](../CONTEXT.md)

### S8c: 推送与会话座位
- Status: **Done**
- Target: `src/Orc.Server/Transport/ServerHost.cs`、`src/Orc.Mcp/Tools/ObserveTools.cs`
- Approach:
  - 会话表（`ConcurrentDictionary<sessionId, ClientSession>`）：每会话持有写队列（`Channel`）——慢客户端不阻塞引擎。
  - 座位绑定：`tools/call` 成功后从结果 `details.seat` 绑定；重连由 `room.resume` 重新绑定。
  - 推送（按会话座位分别生成，非广播）：`notify/view`（对局动作成功后）、`notify/match.started`/`notify/match.ended`（`MatchChanged`）、`notify/target.request`/`notify/target.expired`（按 `pending.Seat` 定向）。
  - **背压**：推送走**有界**队列（256，`DropOldest`）——慢客户端只丢最旧推送（`view` 幂等，下一帧即覆盖），服务端内存不随客户端卡顿增长。
  - **响应不走推送队列**：直接写 socket（与推送共用 writer 串行锁，不交错）。丢响应会让客户端永久挂起。
  - `observe.view` 增 `seat` 参数（缺省回落进程级 `AgentSeat`）——多客户端必须能各自取视角。
  - 心跳：`ping`/任意请求刷新 `LastSeen`；45s 超时关闭连接（TCP 半开不会产生 FIN）。
- Acceptance:
  - [x] 两个客户端各自 `observe.view` 拿到**不同座位**的投影（e2e 断言）。
  - [x] `notify/match.started` 在开局后送达。
  - [x] 推送按座位分发（`pending.Seat` 不匹配的会话不投递）。
  - [x] 段推送 `notify/segment`（S8d）。
  - [x] 背压：慢客户端不会让服务端内存线性增长（有界队列 + 丢弃最旧）。
  - [x] 客户端断线/心跳超时后请求**失败而非挂起**（`OrcClient._closedReason` 修复挂起竞态）。
- Rationale: 推送由传输层在"动作成功后"驱动，不依赖引擎事件；`view` 幂等，重复推送无害。
- Terminology: [投影](../CONTEXT.md)、[座位](../CONTEXT.md)

### S8d: 段推送
- Status: **Done**
- Target: `src/Orc.Server/Transport/ServerHost.cs`
- Approach:
  - 宿主声明为段取用者（`SetSegmentConsumer(Host)`）——服务层不再自行取段。
  - 动作成功后：`match.Engine.TakeSegments()` 取一次 → 喂回 `IngestSegments`（保持观察面缓存语义）→ 为每个已绑定座位的会话各导出一份过滤版（`EventSegmentJson.Serialize(segment, false, MatchEventVisibility, viewer)`）→ `notify/segment`。
  - **同一批段只取一次**：引擎段取用是单消费者语义，不能两个座位各取各的。
- Acceptance:
  - [x] 客户端收到 `notify/segment`，且段内对手抽牌显示为 `$hidden`（e2e 断言 `$hidden` 存在、`$entity` 不存在）。
  - [x] 段与视图在同一动作后先后推送。
- Rationale: 复用 S5 的 `IEventVisibility`，投影与段共用一份可见性判定，避免两套规则漂移。
- Terminology: [段](../CONTEXT.md)、[投影](../CONTEXT.md)

### S8e: 客户端进程拉起
- Status: **Done**
- Target: `src/Orc.Client/ServerProcess.cs`
- Approach:
  - `ServerProcess.StartAsync`：启动子进程（重定向 stdout/stderr）→ 解析 stdout 首行 `{"ready":true,"port":N}` → 后台泵 stderr（可选落盘 `<LogPath>`，并保留尾部用于错误提示）。
  - 超时（默认 15s）抛 `TimeoutException` 并附 stderr 尾部；进程提前退出抛 `InvalidOperationException` 并附 exit code ＋ stderr 尾部。
  - `DisposeAsync`：`Kill(entireProcessTree: true)` 回收整棵进程树（覆盖 `dotnet run` 这类孙进程）。
- Acceptance:
  - [x] 真进程冒烟：`dotnet run --no-build` 拉起服务端 → 读到端口 → 客户端连上 `room.info` → 释放后进程退出。
  - [x] 启动失败带 stderr 尾部（便于定位卡包目录缺失/端口占用）。
  - [x] 客户端崩溃兜底：`--parent-pid` ＋ `ParentWatchdog`（2s 轮询父 PID，父死子退）；`ServerProcess` 默认传自身 PID。
    - 计划调整：**未用 Windows Job Object**。P/Invoke 结构体在 x86/x64 布局不同且不可跨平台；父 PID 轮询语义等价（父死子亡）且可测（有真进程用例）。代价：2 秒检测延迟、PID 复用窗口（服务端生命周期短，可接受）。
  - [ ] 客户端宿主 UI（Godot 侧）接线——本仓只提供库。

### S10: 交付审计补齐（AI 托管 / 断线判负 / 指纹）
- Status: **Done**
- Target: `src/Orc.Server/Transport/ServerHost.cs`、`Room/Room.cs`、`ServerBootstrap.cs`、`Orc.Server.csproj`
- Approach:
  - **AI 托管**：`Orc.Server` 引用 `Orc.Ai`；`MatchChanged(Created)` 时为每个 `kind == ai` 的座位创建 `AiPilot`，驱动循环**每步经 `SerialWorkQueue` 入队**（与远程动作同门禁，不并发进引擎）；终局/对局销毁取消。
  - **断线状态**：会话断开 → `Room.MarkDisconnected(seat)`（房间态则 `DropSeat` 立即释放）；`room.resume` 成功 → `MarkConnected`。
  - **宽限判负**：心跳循环里检查断线座位，超过 `DisconnectGrace` 且轮到它（投影 `activePlayerSide == "self"`）时入队 `ConcedeAsync(seat)`；只尝试一次（先移除再判）。
  - **重连重推选靶**：`BindSeat` 成功后若 `Bridge.CurrentPending.Seat == seat`，重推 `notify/target.request`。
  - **卡包指纹**：`ComputePackFingerprint` 对包目录全部文件（按相对路径排序）做 SHA-256 取前 16 位，注入 `RoomSnapshot`。
- Acceptance:
  - [x] AI 无任何远程连接即可自行换牌并推进回合（`AiSeat_TakesItsTurn`）。
  - [x] 断线方轮到时宽限后判负、对局进入 `over`（`DisconnectedSeat_LosesAfterGrace`）。
  - [x] 房间态断线立即释放座位（`LobbyDisconnect_ReleasesSeatImmediately`）。
  - [x] 指纹稳定且为 16 位（`PackFingerprint_IsStable`）。
- 未做（有意）：
  - **WebSocket 承载**：Windows `HttpListener` 监听非 localhost 需 URL ACL/管理员权限，或引入 ASP.NET Core 框架引用；R5 定位为可选，本阶段不交付。
  - **示例卡包扩卡**：`samples/card-pack-ai` 仅 5 种卡，同名上限下最多 20 张，无法组成 39 张合法构筑；诊断/AI 夹具显式关闭规则，生产卡包必须合规。

### S9: 进程边界测试
- Status: **In Progress**
- Target: `tests/Orc.Server.Tests/`（`TransportTests` / `EndToEndTests`）
- Approach:
  - 分层 e2e：进程内宿主（`ServerBootstrap` ＋ loopback 随机端口）＋ `Orc.Client`；真进程冒烟留 S8d。
  - 已覆盖：握手/工具目录/工具调用/错误分层（4 例）、bootstrap＋`room.info`（2 例）、**双客户端 claim→start→match.started→双视角投影**（1 例）。
- Acceptance:
  - [x] 双客户端接入同一对局并各自取得投影。
  - [x] 完整生命周期：换牌 → 进行 → 结束回合 → 认输 → 终局（`phase: over`，双方 `outcome` 分别为 `Lose`/`Win`）。
  - [x] 出牌闭环：`play.unit_pre` → `notify/target.request`（按 seat 0 定向）→ `target.submit`。
  - [x] 断线重连：断开后新连接凭 `seatToken` 重新绑定并取回投影；错误令牌被拒。
  - [x] 跨玩家选靶路由：归属座位 1 的选靶只推给座位 1 的会话，座位 0 收不到（直接经 `Match.TargeterManager` 发起，绕开示例卡包无对应效果的限制）。
  - [x] 对手手牌在段中不可见（段推送 e2e 断言 `$hidden`）。
  - [x] 现有 `Orc.Mcp.Tests`（76）不回归。
- Rationale: 进程内宿主即可覆盖协议与业务（全走 socket）；真进程只补"启动/握手/回收"，避免 CI flaky。
- Terminology: [客户端](../CONTEXT.md)、[服务端](../CONTEXT.md)
