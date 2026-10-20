# Implementation Grill Plan — 多人支持（服务端 / 客户端分离）

> 需求已冻结（见 `requirements.md` R1–R15）。本轮实现 grill 从"工程落点"开始，逐轮填充 `implementation.md`。
> 已确认的代码事实见 `requirements-grill-plan.md` 的"已确认的代码基线"一节，不再重复取证。

## Unresolved Questions

### I1: 房间层与传输层的工程落点 【Completed → 选项 b】
- 决策：房间层与传输同属新工程 `Orc.Server`；`Orc.Mcp` 新增窄接口 `IRoomControl`（纯 DTO），`room.*` 工具仍在 `ToolRegistration` 注册并转调。
- 依赖方向：`Orc.Server → Orc.Mcp`（单向）。

### I1a: `IRoomControl` 的边界 【Completed（随 I1）】
- 只允许 DTO：seat 号、controller kind、卡组 id 列表、构筑配置、就绪、错误码；禁止 `Match`/`Player`/`Card`/`Ref<T>`。
- `McpService` 构造改为可选注入（缺省＝无房间能力，工具不登记）。

### I2: 协议信封与错误模型 【Completed → 选项 a】
- 决策：严格 JSON-RPC 2.0；业务失败放 `result`（`ok/code/message/details`），`error` 只承载协议错误；`isError`＝工具执行失败；`initialize` 返回版本并校验。

### I3: 服务端唯一串行写者 【Completed → 选项 a】
- 决策：有界 Channel + 单消费者；`target.*`/`session.*` 优先；AI 同样入队；投影在消费者内算、发送在外部写队列；队列满回 `busy`。

### I4: 投影与 actions 的产出时机 【Completed → 选项 a】
- 事实一：`CanPlayAsync` / `GetCommandAvailabilityAsync` 都走 `Executor.RunAsync`（`McpService.Hosting.cs:376-405`），即每次取 `_gate`。
- 事实二：`RunAsync` 在 `IsBusy` 时直接返回 `Busy`（`MatchExecutor.cs:130-133`），而 `IsBusy = _active is { IsCompleted: false }` —— **选靶挂起期间 `_active` 未完成，所有 actions 查询都会得到 Busy**。
- 事实三：前端桥接层算 `UiBattleActions` 是直读活 `Match`，不走执行器，所以没有这个问题。
- 结论：actions 若复用 `IMcpActions`，会在"最需要高亮的选靶期"整体失效。
- 待定：是否新增只读执行面（不取门、不判 Busy，仅断言单写者）；一致性策略。

### I4: 投影与 actions 的产出时机 【Pending】
- 段结束时算 view + actions 再推；actions 异步未就绪时的"延后一拍"实现（R14 验收）；每 seat 各算一次的成本。

### I5: 段过滤的可见性判定 【Completed → 选项 a】
- 决策：内核 `IEntityVisibility` + 导出时判定；实体统一 `$ref`；不可见写 `{"$hidden":true}`；实现放 `Orc.UI` 与投影共用。

### I6: 选靶归属与路由 【Completed → 选项 a】
- 事实：整条链路没有 owner 信息——`SelectorParameter` 是空基类，`ReferenceSetParameter` 只有 candidates/min/max/domainValidator/tag（`Selector.cs:10-31`）；`TargeterFlow` 无 player（`TargeterSession.cs:95-123`）；`ISelectorInstance` 无 owner（`Selector.cs:190-211`）；`PendingSelection` 无 seat（`TargeterBridgeAdapter.cs:393-417`）。
- 反例：A 打出"对手弃一张牌"的效果时，选靶属于 B 但动作发起者是 A——桥侧用"当前动作发起者"推断会路由错。
- 改造成本量级：`ReferenceSetParameter`/`IdentifierSetParameter` 构造点约 10 处，分布 7 个文件。
- 待定：引擎注入 owner vs 桥侧 AsyncLocal 推断 vs 混合；`PendingSelection` 增 seat；claimant 逻辑拆除。

### I6: 选靶路由与超时 【Pending】
- controller 路由替代 `claim` 的具体改法；`target.expired` 推送；重连重推。

### I7: 建局链路与卡组规则 【Completed → 选项 a】
- 决策：完整 KARDS 口径——39 张 + id 在已装载池 + 同名上限按稀有度（standard 4 / limited 3 / special 2 / elite 1）+ 主国/盟国配对；`Orc.Game/Decks/DeckRules.cs` 为唯一实现；`MatchSeatConfig` 扩构筑配置；现有 demo/测试卡组同步修正。
- 已知代价：现有卡组数据会红，需一并改。

### I8: 客户端拉起与进程治理 【Completed → 选项 b】
- 决策：监听 0 端口 + stdout 就绪行；`room.info` 返回候选 IPv4+port；Job Object/父 PID watchdog；stderr 落盘并上报具体失败原因。

### I9: 客户端库与 e2e 测试形态 【Completed → 选项 a】
- 决策：新增 `Orc.Client`（协议/推送/重连/DTO，不含业务规则）；e2e 分层（进程内宿主为主 + 真进程冒烟）；`Orc.Mcp.Tests` 71 个保持绿，新 e2e 独立工程。

> **实现 grill 已收束**（2026-10-20）：I1–I9 全部 Completed，`implementation.md` 冻结为可执行计划。

### I8: 客户端拉起与进程治理 【Pending】
- 端口分配（监听 0 端口回报）、子进程回收、日志采集、失败提示。

### I9: 测试形态 【Pending】
- 现在零进程边界测试；需覆盖双客户端、断线重连、座位令牌、段脱敏。
