# Requirements Grill Plan — 多人支持（服务端 / 客户端分离）

> 本轮 grill 聚焦：把「逻辑引擎作为服务端、Godot 前端作为客户端、两者仅经字符串/远程协议交流」这件事的需求边界钉死。
> 当前状态：需求 grill 第 1 轮；Q1 已发出；Q2–Q9 为登记待问。
> 前端参考基线：`D:\Godot-orcKsd-main`（用户声明"可能过时，仅供参考"，见 C1）。

## 已确认的代码基线（供 grill 取证，不再提问）

### 引擎侧（`d:\OrC-KSD`）

- **解决方案实含 21 个工程**（`OrcEngine.slnx`）：`Orc`、`Orc.Game`、`Orc.Foundation.Actions`、`Orc.Script`、`Orc.Packages`、`Orc.Plugins.BuiltIn`、`Orc.UI.Contracts`、`Orc.UI`、`Orc.Mcp`、`Orc.Ai` ＋ 6 测试 ＋ 4 示例。README 工程表（17 项）已过时，漏 `Orc.Foundation.Actions`／`Orc.Ai`／`Orc.Ai.Tests`／`Orc.Ai.Sample`。
- `src/OrC-KSD` 是**空目录**（改名残留），与 `src/Orc` 无关。
- **已有协议无关服务层** `src/Orc.Mcp`：`McpService` 门面；业务入口 `InvokeAsync(string toolName, JsonElement args)`（`McpService.cs:131`，注释自称"协议适配层的唯一入口"）；工具 47 个（authoring）/ 24 个（player），schema 为手写 JSON Schema 字符串；结果统一外壳 `McpToolResult(bool Ok, string Code, string Message, JsonNode? Details, JsonNode? EngineResult)`（`Contracts/McpToolResult.cs:22`）。
- **传输层抽象不存在**：无 `IToolTransport`/`IRpcChannel`/帧/信封。仅 `samples/Orc.Mcp.Stdio/Program.cs`（306 行手写 JSON-RPC over stdin/stdout NDJSON），**无任何测试覆盖**。
- **选靶已是纯字符串协议**：`PendingSelection(SessionId, SelectorName, Mode, Min, Max, Candidates, Identifiers)` ＋ `PendingCandidate(Index, Kind, ReferenceKey, DisplayName, IsAlive)`（`Mcp/Match/TargeterBridgeAdapter.cs:14-38`）；`target.submit` 用**序号**回传；`McpService.PendingJson` 已给出线上 JSON 形态。引擎内部仍是活对象（`ITargeterBridge.BeginTargeting(ITargeterSession)`、`ISelectorInstance.Submit(SelectorEvent)`、`SelectorPresentation.Candidates : IReadOnlyList<Ref<Entity>>`、`Parameter : object?`）——**只能在服务端终结**。
- **稳定引用键已存在**：`EngineReferences`（`src/Orc/Core/EngineReferences.cs`，N 格式 GUID；实体用 `Entity.Id`，`Player` 由表分配）＋ `McpService.KeyOf`。
- **事件流段可导出但语义有损**：`EventSegmentJson` / `EventStreamJson` / `SnapshotJson` 可整段出 JSON；但 `LogEntry.Data`（`src/Orc/Core/LogEntry.cs:63`）装的是**活引擎对象**（`GameUpdates.cs:317-330` 直接放 `Card`/`Player`），`JsonValueWriter` 降级为 `{"$entity":"名字"}`（`JsonValueWriter.cs:103-114`）——**不可回解**、且与 `EngineReferences` 的 GUID 不对齐。
- **投影是纯值对象、天然可 JSON 化**：`UiMatchView`/`UiCardView`/`UiCardDefinition`（`src/Orc.UI.Contracts/UiViewModel.cs`）零引擎引用；但无 `JsonSerializerContext`/枚举转换器，且 `UiMatchView` **从未上线**（`Tools/ObserveTools.cs:208-225` 只手写了 14 个标量子集）。
- **单对局硬约束**：`MatchExecutor.Attach` 已有活跃对局即抛 `InvalidOperationException("已存在活跃对局（同一进程只允许一个活跃对局——targeter 桥接无法区分多局归属）")`（`Match/MatchExecutor.cs:89-101`）；`MatchExecutor._gate = SemaphoreSlim(1,1)`；busy 语义 `IsBusy`。
- **宿主集成面泄漏活对局**：`IMcpHost.ActiveMatch`/`TryGetActiveMatch(out Match?)`、`MatchLifecycleEventArgs(..., Match?)`（`Contracts/HostingContracts.cs:40-43`）；`IMcpHost.TrySubmitTargeting` 内部 `Executor.SubmitAsync(...).GetAwaiter().GetResult()`（`McpService.Hosting.cs:68-71`）——**同步阻塞在异步上**。
- **模式/座位是进程级单值**：`McpServiceOptions.Mode`（Authoring/Player）、`AgentSeat`（`:70`）；认领者只有两个硬编码常量 `ClaimantService`/`ClaimantHost`；无 clientId / 会话表 / 断线回收。
- **AI 在进程内**：`Orc.Ai/AiPilot.cs` 直接读活 `match.State`/`match.Players[Seat]`，经 `McpService.AiGateway` 拿对局——决策面无法外移（除非新增"仅投影"网关）。
- **沙盒写根在服务端**：`SandboxEnvironment`/`SandboxPathGuard`，`card.write`/`file.list`/`asset.promote` 全在服务端磁盘。

### 前端侧（`D:\Godot-orcKsd-main`，可能过时）

- **UI 层已零引擎引用**：`proto/ui/**` 0 处 `using Orc`；`Main.cs:715-721` 有运行时程序集守卫（凡引用 `Orc.*` 除 `Orc.UI.Contracts` 即抛，打印 `UI_BOUNDARY_VERIFY_OK`）。
- **契约层已是纯 DTO**：`proto/contracts`（`Kards.Ui.Contracts`，net8）——`UiCommand`（纯 string uid）、`UiPresentation`（13 种 step + `UiPresentationResolution(MatchId, Steps, After, Sequence, Before, Priority)`）、`UiBattleActions`、`UiTargetRequest/UiChoiceSlot/IUiTargetResponder`（纯字符串 + 令牌 `MatchId:selectorId:counter`）。
- **表现层已冻结**：`proto/core/PresentationPlayer` + `UiSnapshots.Freeze`（含对对手手牌/反制/换牌的脱敏，`UiSnapshots.cs:37-70`）。
- **投影按需、不是每帧全量**：`OrcMatchHost.ProcessSegment` 每段一次 `after` 投影 ＋ 静默相位兜底重读；UI 每帧只 `Pump()` + 用缓存 `Actions`。
- **引擎动作入口 100% 收拢在桥接层**：`proto/ui` 里零直调；唯一 UI 入口 `OrcMatchRunner.SubmitAsync(UiCommand)`。
- **但桥接层是引擎的直连层**：`proto/bridge/Kards.Ui.OrcBridge` 38 个文件 `using Orc.*`，`OrcMatchSession.cs` 一个文件引 13 个引擎命名空间；`OrcEngineHost` 是**进程级静态单例**（`static EngineHost? _host`），启动即 `WarmUp`。
- **构建期硬依赖引擎源码树**：`Kards.Ui.OrcBridge.csproj` 以 `ProjectReference` 引 `$(OrcEngineRoot)` 下 6 个引擎工程 ＋ **内嵌引擎模板资源** `$(OrcEngineRoot)/EffectParsing/Templates/ops/*.csx.tpl`；`UiMatchView` 定义在引擎检出目录的 `Orc.UI.Contracts`。
- **前端在进程内跑引擎能力**（不只是对局）：DIY 卡编译链（`DiyCardCompiler` + `DesktopNativeCodegen` + `NativeScriptHost` + ClearScript/V8）、卡池准入（`VerifiedCardPool.Compile` / `DeckConstructionService.Validate`）、卡包宿主（`OrcEngineHost.LoadPackCards`）、对手驱动（`SessionAiGateway` + `Orc.Ai`）。
- **网络为零**：全仓无 `TcpClient|TcpListener|WebSocket|HttpClient|MultiplayerPeer|ENet` 命中；Godot 侧无网络 autoload。

## 代码 ↔ 陈述矛盾（需仲裁，不算实现问题）

### C1: 前端参考副本是**结构性过时**，不只是"可能过时"
- 该副本 README 自述："当前源码在 `H:/Working Folder/OrC-KSD.Godot`，实际构建和运行副本在 `H:/g/kards`"；`ENGINE_INTEGRATION_BASELINE.md` 绑定引擎 `750e11e` + Beta.2 patch（2026-10-17）。
- 本地引擎 `d:\OrC-KSD` 此后已演进（含 wrapper 改造在途，见 `docs/引擎优化-wrapper语法/`），基线不再一致。
- 影响：本次若"基于该副本改造"，等于同时承接一次引擎基线追平；若只作只读参考，则改造目标仓与验收环境须另行指定。

### C2: 两套选靶契约并存，协议真源未定
- 引擎服务层：`PendingSelection`（selector/min/max/candidates/identifiers）＋ `target.submit` 用**序号**。
- 前端契约：`UiTargetRequest`（`UiChoiceSlot[]` + `MatchId:selectorId:counter` 令牌）＋ `IUiTargetResponder.Complete(requestId, selections)`。
- 二者语义重叠但不兼容。远程协议必须选其一为真源，或明确"服务端用 A、客户端用 B、中间做一对一映射"。

### C3: "把逻辑引擎作为服务端"与"前端在进程内跑引擎能力"的边界冲突
- 用户表述隐含"客户端不再跑引擎"。但前端目前**大量依赖引擎程序集**做非对局功能：DIY 效果实时编译、卡池准入审核、卡包装载、对手 AI。
- 若这些留在客户端，则客户端仍须携带引擎 DLL + V8 原生库 + 引擎源码树（构建期），"仅通过字符串交流"只在**对局**这一条线上成立。

## Unresolved Questions

### Q1: 「多人支持」的验收形态与边界 【In Progress — 本轮】
- a) **1v1 双真人**（KARDS 原生形态）：服务端承载一个对局，两个客户端各占一个座位；服务端按座位过滤隐藏信息；AI 保留为 PvE 填充座位。
- b) **只做"可分离"**：单客户端 + 服务端 AI 对手，验证协议闭环；双真人留到下一阶段。
- c) **多桌并发**：一个服务端进程承载 N 个对局、2N 个连接，含房间/匹配。
- d) 在 a 之上**含观战/回放**（第三方只读连接）。

### Q2: 客户端是否允许保留引擎程序集 【Pending】
- 决定 C3 的三块（DIY 编译、卡池准入、卡包宿主）是搬服务端、留客户端，还是双轨。

### Q3: 服务端粒度 【Pending】
- 单进程单对局（沿用 `MatchExecutor` 硬约束）/ 单进程多对局（需 matchId 分域）/ 每对局独立进程。

### Q4: 传输与编码 【Pending】
- NDJSON over TCP / WebSocket / 命名管道（同机优先）/ 保留 MCP 作为唯一协议（把客户端也做成 MCP 客户端）。

### Q5: 权威与一致性 【Pending】
- 投影每段全量 vs 增量；段由服务端推还是客户端拉；客户端是否允许乐观预测。

### Q6: 隐藏信息与座位 【Pending】
- 双真人时对手手牌的过滤点（服务端投影前过滤 vs 投影后脱敏），以及"谁是哪个座位"的会话绑定。

### Q7: 断线重连与对局恢复 【Pending】
- 待决选靶请求在断线时的归属（归还/取消/保留）；是否要求快照续接。

### Q8: 本阶段是否含账号/房间/匹配/生涯数据 【Pending】

### Q9: 引擎侧改动授权范围 【Pending】
- 是否允许改 `Orc.Mcp`/`Orc.Game` 的既有契约（如 `IMcpHost` 异步化、`MatchExecutor` 多局化），还是只允许"外挂一层"不改内核。

## 实现类问题（暂记，待实现 grill）

- I1：协议信封与契约版本协商（`ContractSchemaVersion` 现仅为 int 常量，无握手/校验）。
- I2：`LogEntry.Data` 的引用降级规则改造（`$entity` → `$ref` GUID）与配套测试。
- I3：`IMcpHost.TrySubmitTargeting` 同步阻塞改异步（网络 handler 会线程池饥饿/死锁）。
- I4：多客户端认领者表（`clientId → {seat, role, claimant}`）替掉两个硬编码常量。
- I5：`UiMatchView` 整枚上线 + 枚举序列化策略 + `artLookup`/`definitionLookup` 的客户端查表协议。
- I6：`MatchExecutor` 多对局注册表（`Attach` 现直接抛）。
- I7：前端 `IGameSession` 抽接口（`OrcMatchSession` 46.7KB + `OrcMatchHost` 17.8KB），把 `Pump()` 同步拉模型改成推/拉混合。
- I8：客户端工程与引擎源码树解耦（`Orc.UI.Contracts` 独立化、内嵌 `.tpl` 资源移出）。
- I9：AI 决策面外移方式（"仅投影"网关 vs 服务端内置）。
