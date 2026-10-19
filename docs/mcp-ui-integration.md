# UI 接入开发手册 —— 在真实 UI 里用人机同局（Orc.Mcp 宿主集成）

> **自包含声明**：本文档可独立使用——按第 2 节的 7 步流程从零完成接入，不需要读引擎源码或其它文档。
> 文中所有 API 均给出**完整签名**（第 10 节为全量参考），所有代码片段**可直接复制**。
> 可运行的对照实现：[`samples/Orc.Mcp.HostSample`](../samples/Orc.Mcp.HostSample/Program.cs)（`dotnet run --project samples/Orc.Mcp.HostSample`）。

---

## 目录

| 节 | 内容 |
|---|---|
| 0 | 你要构建什么（架构与责任划分，5 分钟） |
| 1 | 前置条件（SDK、工程引用、目录） |
| 2 | **开发流程总览（7 步 + 每步验收点）** |
| 3 | Step 1：创建服务层会话 |
| 4 | Step 2：对局生命周期与投影 |
| 5 | Step 3：选靶接入（人机同局核心） |
| 6 | Step 4：事件流段消费 |
| 7 | Step 5：驱动对局（强类型动作面） |
| 8 | Step 6：player 模式（座位与信息过滤） |
| 9 | Step 7：端到端验证与自检清单（DoD） |
| 10 | API 参考（完整签名，自包含） |
| 11 | 故障排查（症状 → 原因 → 处置） |
| 12 | 反模式清单（不要做的事） |
| 13 | 与其它文档的关系 |

---

## 0. 你要构建什么

**形态**：UI 宿主与服务层在**同一进程**共享**同一个对局**——
agent 当"卡牌编辑者"（全知 + 调试能力），人也在同一个 UI 里正常操作；二者共用一套门禁与一次对局事实。

```
              ┌──────────────── 同一进程 ────────────────┐
   agent ──MCP(stdio)──►  Orc.Mcp 服务层（唯一驱动真源）  │
                          │  唯一 ITargeterBridge          │
                          │  唯一 MatchExecutor 门禁        │
                          │  唯一段取用点 + SegmentCache    │
                          │  唯一活跃 Match（所有权）        │
                          └───────┬──────────────┬─────────┘
                                  │              │
                    IMcpHost（集成面）      IMcpActions（强类型动作面）
                                  │              │
                          ┌───────▼──────────────▼─────────┐
                          │     你的 UI 宿主（Godot 等）     │
                          │  · 投影 provider（自建）每帧 Project
                          │  · 待决选靶请求：渲染→认领→提交   │
                          │  · 段消费（可选）：取段→喂回服务层 │
                          │  · 玩家输入 → 走强类型动作面      │
                          └────────────────────────────────┘
```

**责任划分**（谁拥有什么）：

| 事项 | 归属 | UI 侧做法 |
|---|---|---|
| 对局的创建/销毁 | 服务层 | 只监听 `MatchChanged`，不持有销毁权 |
| 目标选择桥（`ITargeterBridge`） | 服务层（唯一） | 订阅待决请求参与应答，**不要自己实现桥** |
| 串行化门禁（对局调用不并发） | 服务层 | 所有动作走 `IMcpActions`，**不要直调引擎** |
| 事件流段队列（取走即移除） | 二者之一（显式声明） | 默认服务层；若要零延迟表现则声明 `Host` 并喂回 |
| 投影 provider | **UI 自建** | `CreateProvider(artLookup, matchId, definitionLookup)` |
| 美术/文本解析 | UI | 通过 `artLookup`/`definitionLookup` 回调 |

**4 条硬规矩**（违反任一条都会破坏引擎的不并发前提）：

1. 不直调 `Match`/`PlayManager`/`CommandManager` —— 一律走 `IMcpActions`；
2. 不自己实现 `ITargeterBridge` —— 走 `SubscribeTargetingRequests` + `TrySubmitTargeting`；
3. 不直接 `engine.TakeSegments()` —— 除非已 `SetSegmentConsumer(SegmentConsumer.Host)` 并把段 `IngestSegments` 喂回；
4. 不销毁对局 —— 只经服务层（`sandbox.dispose` 或宿主生命周期结束）。

---

## 1. 前置条件

- **.NET SDK 8+**（能构建 `net8.0`）；UI 侧框架不限（Godot 4 的 C# 即 .NET）。
- **引擎仓库路径**：假设为 `<OrcEngineRoot>`（即包含 `src/Orc.Mcp` 的目录）。
- 工程引用：

```xml
<ItemGroup>
  <ProjectReference Include="<OrcEngineRoot>/src/Orc.Mcp/Orc.Mcp.csproj" />
</ItemGroup>
```

`Orc.Mcp` 的依赖（`Orc` / `Orc.Game` / `Orc.Packages` / `Orc.Plugins.BuiltIn` / `Orc.UI` / `Orc.Script`）通常已在 UI 宿主引用链中；
服务层本身**无第三方依赖、无 Godot/控制台依赖**（控制台 IO 只在示例里）。

- **目录准备**：指定一个沙盒根（唯一可写根），例如 `<用户数据>/orc-sandbox`。目录不存在会自动创建（含 `pack/{manifest.json,cards/,effects/}` 脚手架）。
- **两处内置插件清单**（`orc.builtin.core` / `orc.builtin.ui`）由工程引用自动复制到输出目录的 `package/plugins/`；若你的部署方式不复制，可用 `McpServiceOptions.PluginsRoot` 指定其所在目录。

---

## 2. 开发流程总览

| Step | 做什么 | 关键 API | 验收点（做完即可自查） |
|---|---|---|---|
| **1** | 创建服务层会话 | `new McpService(new McpServiceOptions{...})` | `service.Tools.Count` 为 47（authoring）；`service.info` 可读到限制清单 |
| **2** | 对局生命周期 → 投影 | `MatchChanged`、`TryGetActiveMatch`、`CreateProvider`、`Project` | Created 后 `provider != null` 且每帧能拿到 `UiMatchView`；Disposed 后置空 |
| **3** | 选靶接入 | `SubscribeTargetingRequests`、`TryClaimTargeting`、`TrySubmitTargeting` | 触发一次需选位的动作时，UI 能收到请求并提交成功 |
| **4** | 段消费 | `SetSegmentConsumer`、`PullSegments`、`IngestSegments` | 选择任一模式后，表现侧能拿到段且不出现"事件断流" |
| **5** | 驱动对局 | `IMcpActions`（16 个方法） | 玩家操作经服务层生效；忙碌期得到 `pending-targeting`/`busy` 而非并发 |
| **6** | （可选）player 模式 | `AgentSeat`、模式差异 | 调试工具不存在；观察面按座位过滤 |
| **7** | 端到端验证 | 参考宿主对照 + 自检清单 | 第 9 节清单全绿 |

---

## 3. Step 1：创建服务层会话

### 3.1 选项全表（`McpServiceOptions`）

| 属性 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `Mode` | `McpServiceMode` | `Authoring` | `Authoring`＝卡牌编辑者（沙盒，含调试工具）；`Player`＝agent 玩家（工具面与玩家等同） |
| `SandboxRoot` | `string`（**required**） | — | **唯一可写根**；全部写类工具路径必须在其下 |
| `PacksRoot` | `string?` | `null` | 正式资产根（只读；`asset.promote` 的允许写入范围） |
| `PluginsRoot` | `string?` | `null` | 内置插件清单所在目录（默认输出目录下 `package/plugins`） |
| `PromoteEnabled` | `bool` | `false` | 产物提升开关（沙盒 → 正式卡包） |
| `CsxEnabled` | `bool` | `false` | csx 编辑能力（引擎沙箱为弱沙箱、非安全边界） |
| `TargetSelectionTimeout` | `TimeSpan` | 10 分钟 | 待决选靶等待上限；超时按**取消**处理 |
| `SegmentCacheCapacity` | `int` | `200` | 服务层保留的最近 N 段 |
| `ContractSchemaVersion` | `int` | `1` | 服务层契约版本（独立于引擎内部格式版本） |
| `MatchId` | `string` | `"sandbox"` | 投影用的对局标识（UI 侧语义） |
| `SandboxPackId` | `string` | `"sandbox"` | 脚手架 manifest 的 `id` |
| `PlaceholderCardId` | `string` | `"sandbox.placeholder"` | 脚手架占位卡 id（满足引擎"卡组名单非空"构造契约） |
| `Seed` | `int?` | `null` | 随机种子（复现用） |
| `AgentSeat` | `int?` | `null` | **player 模式**下 agent 的座位（0/1）；未配置时 player 观察面会明确拒绝 |

### 3.2 会话类（可直接复制）

```csharp
using Orc.Mcp;
using Orc.Mcp.Hosting;
using Orc.Mcp.Match;

public sealed class OrcSession : IAsyncDisposable
{
    private readonly McpService _service;
    private Orc.Game.Ui.IUiProjectionProvider? _provider;
    private PendingSelection? _pendingForPlayer;

    public OrcSession(string sandboxRoot, string? formalPacksRoot = null)
    {
        _service = new McpService(new McpServiceOptions
        {
            Mode = McpServiceMode.Authoring,
            SandboxRoot = sandboxRoot,
            PacksRoot = formalPacksRoot,
            MatchId = "ui-session",
            AgentSeat = 0,               // player 模式才用得上
            Seed = null,                 // null＝随机
        });

        _service.MatchChanged += OnMatchChanged;
        _targetingSubscription = _service.SubscribeTargetingRequests(OnTargetingRequest);
    }

    public McpService Service => _service;

    private IDisposable? _targetingSubscription;

    private void OnMatchChanged(object? sender, MatchLifecycleEventArgs args)
    {
        if (args.Change == MatchLifecycleChange.Created && args.Match is { } match)
        {
            _provider = match.UiProjections.CreateProvider(
                artLookup: ArtPathOf, matchId: _service.Options.MatchId, definitionLookup: DefinitionOf);
        }
        else
        {
            _provider = null;   // 对局销毁：必须停止投影，不再读该对局
        }
    }

    private string? ArtPathOf(string cardId) => $"res://art/{cardId}.png";      // UI 侧语义
    private Orc.UI.Contracts.UiCardDefinition? DefinitionOf(string cardId) => null;

    private void OnTargetingRequest(PendingSelection pending)
    {
        // 此回调在桥的线程上：只做记录/渲染，**不要阻塞、不要同步提交**
        _pendingForPlayer = pending;
        _service.TryClaimTargeting(pending.SessionId, TargeterBridgeAdapter.ClaimantHost, out _);
    }

    public ValueTask DisposeAsync()
    {
        _targetingSubscription?.Dispose();
        _service.MatchChanged -= OnMatchChanged;
        return ValueTask.CompletedTask;
    }
}
```

### 3.3 验收点

```csharp
var service = new McpService(new McpServiceOptions { SandboxRoot = dir });
Console.WriteLine(service.Tools.Count);                 // authoring 模式应为 47
Console.WriteLine(service.Options.ContractSchemaVersion); // 1
```

---

## 4. Step 2：对局生命周期与投影

### 4.1 事件语义（重要）

`MatchChanged` 的 `Created` **在对局就绪后**才触发（`Initialize` 完成、UI 插件包激活相已跑、投影提供者工厂已注册）。
因此：**在 `Created` 里建 provider 是安全的；更早建立会拿到 `null`**（这是常见踩坑点）。

`Disposed` 触发后：`ActiveMatch` 变为 `null`，**必须停止投影**。

### 4.2 每帧投影（可直接复制）

```csharp
public void ProcessFrame(double delta)
{
    if (_provider is null || !_service.TryGetActiveMatch(out var match)) return;

    try
    {
        var view = _provider.Project(match, match.Players[0]);   // Orc.UI.Contracts.UiMatchView
        RenderMatch(view);   // 你的渲染层
    }
    catch (Exception ex)
    {
        Log.Warn(ex, "投影异常：保留上一帧（运行期隔离）");
    }
}
```

单卡读取（列表/详情的增量刷新）：

```csharp
var cardView = _provider.ReadCard(match, card, player, zone: "hand", slotIndex: -1); // UiCardView
```

### 4.3 验收点

- `sandbox.create`（或宿主侧建局流程）之后，UI 侧 `_provider != null`；
- 每帧能拿到 `UiMatchView`（回合/相位/手牌数/指挥点等）；
- 销毁对局后 `_provider == null` 且不再调用 `Project`。

---

## 5. Step 3：选靶接入（人机同局核心）

### 5.1 时序

```
agent 触发动作（需选位）
      │
      ▼
服务层 MatchExecutor 启动动作 ──► 引擎发起选靶 ──► 服务层唯一桥
      │                                              │
      │                          ┌───────────────────┴────────────────┐
      │                          ▼                                    ▼
      │                 PendingPublished 事件                动作包装 await 挂起
      │                          │                                    │
      ▼                          ▼                                    │
 UI 订阅回调：渲染候选 + 认领                                   （对局不推进，直到应答）
      │
      ▼（玩家点击发生在下一帧）
 TrySubmitTargeting(sessionId, picks, false, ClaimantHost, out reason)
      │
      ▼
 引擎继续执行 → 动作完成（或再次待决 → 再次进入本流程）
```

### 5.2 代码（可直接复制）

```csharp
// 订阅（构造时一次）
using var sub = service.SubscribeTargetingRequests(pending =>
{
    // 只做渲染/记录，禁止阻塞或同步提交
    _pendingForPlayer = pending;
    if (!service.TryClaimTargeting(pending.SessionId, TargeterBridgeAdapter.ClaimantHost, out var reason))
        Log.Info($"认领失败（可能已被 agent 应答）：{reason}");
});

// 帧循环里（玩家点击后）
public void OnPlayerPicked(List<int> candidateIndexes)
{
    if (_pendingForPlayer is not { } pending) return;
    _pendingForPlayer = null;

    if (!service.TrySubmitTargeting(
            pending.SessionId, candidateIndexes, cancel: false,
            TargeterBridgeAdapter.ClaimantHost, out var reason))
    {
        Toast(reason);   // 例：会话已取消（超时）/已被另一方认领
    }
}
```

> 代码中的 `RenderMatch` / `PlayPresentation` / `Toast` / `Log` 均为**宿主侧占位**，按你的 UI 框架实现。

`PendingSelection` 的字段（渲染所需全部信息）：

| 字段 | 类型 | 说明 |
|---|---|---|
| `SessionId` | `string` | 应答时回传 |
| `SelectorName` | `string` | 选择器类型名（决定视觉：`slot`/`fieldUnit`/`hand`/`mulligan`/`option`…） |
| `Mode` | `string` | `Click` / `Drag` |
| `Min` / `Max` | `int` | 选取数量约束 |
| `Candidates` | `IReadOnlyList<PendingCandidate>` | 引用类候选（**用序号提交**） |
| `Identifiers` | `IReadOnlyList<string>` | 标识类候选（选项/卡名等） |

`PendingCandidate`：`Index`（1 起，提交用）、`Kind`（`slot`/`formation-gap`/`unit`/`card`/`hq`/`entity`）、`ReferenceKey`（引擎引用键，观察/对账用）、`DisplayName`、`IsAlive`。

### 5.3 仲裁规则

| 规则 | 说明 |
|---|---|
| **先到先服务** | 同一请求只被一方应答；已被应答/超时取消的请求，另一方提交会失败（读 `reason`） |
| **双向接管** | `TryClaimTargeting(sessionId, claimant, out reason)` 可把人/agent 的请求接管；agent 侧对应 `target.claim` 工具 |
| **超时＝取消** | 默认 10 分钟；超时后对局**继续**（动作以 `cancelled` 结束），不会卡死 |
| **挂起期门禁** | 挂起期间服务层只放行 `target.*` 与只读工具；UI 动作会拿到 `pending-targeting` |

### 5.4 验收点

- 触发一次需选位的动作（如 `play.unit_pre`）后，UI **收到**请求且能**提交成功**；
- 故意让 agent 先应答 → UI 提交失败且 `reason` 说明"已被另一方认领"；
- 不提交、等到超时 → 动作以 `cancelled` 结束，对局仍可继续操作。

---

## 6. Step 4：事件流段消费

段队列是**取走即移除**的，因此同一时刻只能有一个实际取用者。

| 模式 | 谁取段 | UI 侧做法 | 适用 |
|---|---|---|---|
| `SegmentConsumer.Service`（默认） | 服务层 | `service.PullSegments(take: N)` 读缓存 | 无头/纯 MCP、或 UI 不需要零延迟表现 |
| `SegmentConsumer.Host` | **宿主** | 帧循环 `TakeSegments()` → `IngestSegments(...)` → 自行播放 | UI 要零延迟表现（推荐给真实 UI） |

```csharp
// 构造时声明一次
service.SetSegmentConsumer(SegmentConsumer.Host);

// 帧循环
public void ProcessSegments()
{
    if (!service.TryGetActiveMatch(out var match)) return;

    var segments = match.Engine.TakeSegments();
    if (segments.Count == 0) return;

    service.IngestSegments(segments);   // 喂回服务层缓存（agent 的 observe.segments 同源）
    PlayPresentation(segments);          // 你的表现层
}
```

> 声明为 `Host` 后，agent 侧 `observe.segments` 的 `takenNow` 会恒为 0——这不是错误，表示"段由宿主取走并喂入"。

### 验收点

- 动作发生后，UI 能取到段并播放；
- 同一段时间内不出现"UI 与服务层都在取"（表现为事件断流）。

---

## 7. Step 5：驱动对局（强类型动作面）

UI 的所有对局操作走 `IMcpActions`（与 agent 的 MCP 工具**共用同一门禁与 pending 状态机**）。

### 7.1 方法与引擎入口对照

| 方法 | 引擎入口 | 备注 |
|---|---|---|
| `EndTurnAsync(ct)` | `Match.EndTurn` | 受门禁/相位约束 |
| `ConcedeAsync(ownerIndex, ct)` | `Match.Concede` | |
| `MulliganBeginAsync(ownerIndex, ct)` | `Match.BeginMulliganAsync` | 可能返回 `targeting` |
| `MulliganDoneAsync(ownerIndex, ct)` | `Match.MulliganDone` | |
| `PlayUnitPreAsync(cardRef, ct)` | `PlayManager.BeginUnitPrePlayAsync` | **会发起部署选位** |
| `PlayUnitAsync(cardRef, ownerIndex, slotIndex, ct)` | `PlayManager.PlayUnitAsync(unit, Slot)` | |
| `JoinUnitAsync(cardRef, ownerIndex, slotIndex, ct)` | `PlayManager.JoinUnitAsync` | 不扣费、不走部署词条 |
| `PlayCommandPreAsync(cardRef, ct)` | `PlayManager.BeginOrderPrePlayAsync` | 协议名冻结（`command_pre` → 引擎 `BeginOrderPrePlayAsync`） |
| `PlayCommandAsync(cardRef, ct)` | `PlayManager.PlayOrderAsync` | 协议名冻结（`command` → 引擎 `PlayOrderAsync`）；仅内部委托改名 |
| `UseCounterAsync(cardRef, ct)` | `PlayManager.UseCounterAsync` | |
| `BeginCommandAsync(unitRef, ct)` | `CommandManager.BeginCommandAsync` | 移动/攻击统一入口 |
| `BeginMoveAsync(unitRef, ct)` | `CommandManager.BeginMoveAsync` | |
| `BeginAttackAsync(unitRef, ct)` | `CommandManager.BeginAttackAsync` | |
| `LeaveBattlefieldAsync(unitRef, ct)` | `CommandManager.LeaveBattlefieldAsync` | |
| `GetCommandAvailabilityAsync(unitRef, ct)` | `CommandManager.GetCommandAvailability` | **纯读**，UI 高亮用 |
| `CanPlayAsync(cardRef, ct)` | `PlayManager.CanPlay` | **纯读** |

`cardRef`/`unitRef` 都是**引擎引用键**（`N` 格式 GUID；来自投影 `UiCardView.Uid` 或 `observe.*` 的 `ref`）。

### 7.2 结果处理模板

```csharp
var outcome = await service.PlayUnitPreAsync(cardRef);

switch (outcome.Status)
{
    case ActionOutcome.StatusCompleted:
        // 动作完成；outcome.Payload 是引擎原生结果的 JSON 形态
        break;

    case ActionOutcome.StatusTargeting:
        // 等待选靶：UI 已在订阅回调里拿到请求；无需额外处理
        break;

    case ActionOutcome.StatusCancelled:
        // 合法结局（人/agent 取消、或超时按取消）：按"用户放弃"处理，不是错误
        break;

    case ActionOutcome.StatusBusy:            // 上一个动作未完成
    case ActionOutcome.StatusNotReady:        // 未建局
        ShowToast(outcome.FailureMessage);
        break;

    case ActionOutcome.StatusFailed:
        ShowToast($"{outcome.FailureCode}: {outcome.FailureMessage}");   // 引擎原因在 Payload
        break;
}

// 跨调用保留结果时：可读outcome.TimedOut 判断"本轮应答是否因超时被按取消处理"
```

`ActionOutcome` 字段：`Status`、`Payload`（`JsonNode?`）、`Pending`（`PendingSelection?`）、`FailureCode`、`FailureMessage`、`TimedOut`；常量：`StatusCompleted`/`StatusTargeting`/`StatusCancelled`/`StatusNotReady`/`StatusBusy`/`StatusFailed`。

### 7.3 验收点

- 玩家操作能生效（回合推进、出牌、移动/攻击）；
- 在 agent 动作未完成时操作 → 得到 `pending-targeting`/`busy`，**不会**并发进入引擎；
- 选靶期间 UI 动作被门禁挡住。

---

## 8. Step 6：player 模式（座位与信息过滤）

| 维度 | 沙盒模式 `Authoring` | 玩家模式 `Player` |
|---|---|---|
| agent 座位 | **不占座位**（全知编辑者） | **占座位**（`AgentSeat`＝0/1） |
| 工具面 | 47 个（含调试工具） | 24 个（**无任何调试工具**；调用调试工具得 `unknown-tool`） |
| 观察面 | 全知（`observe.view` 含对手手牌明细） | 按座位过滤（对手手牌仅数量） |
| 与真人玩家 | 超出（这就是编辑者） | **操作能力完全等同**（白名单护栏测试保证） |

切到 player 模式：

```csharp
new McpServiceOptions { Mode = McpServiceMode.Player, SandboxRoot = dir, AgentSeat = 1 }
```

**当前交付状态（须知）**：player 模式的**工具面、座位与信息过滤已就位**；但"player 模式下对局从何而来"（宿主注入 or 服务层建局）尚未定案（遗留项 Q3-子4）。
若你的 UI 现在就要用 player 模式，请先与引擎侧确认建局路径；沙盒模式（本手册主线）不受影响。

未配置 `AgentSeat` 时调用 `observe.view` 会返回 `mode-denied` 并说明原因（不是静默失败）。

---

## 9. Step 7：端到端验证与自检清单

### 9.1 参考宿主对照（预期输出）

```bash
dotnet run --project samples/Orc.Mcp.HostSample
```

应依次看到（这是"已正确接线"的判据）：

```
工具数：47；模式：Authoring
[UI] 段取用者＝Host（服务层不再自行取段）
[tool] sandbox.create → ok=True code=ok 沙盒对局已创建并进入进行态。
[UI] 对局已创建 → 投影 provider 就绪（hasProvider=True）      ← 若 False，见第 11 节
[tool] card.write / asset.validate / card.load → ok=True
[agent] 预打出（将进入部署选位）…
[UI] 收到待决选择：selector=slot min=1 max=1 候选=2，sessionId=……
[agent] 预打出结局：status=targeting code=ok
[UI] 玩家点选 → 提交成功（picks=[1]）                          ← 若失败，读 reason
[帧 1] 投影：回合=1 相位=play 我方手牌=0 卡组=1 敌方手牌=0 指挥点=0/1
[UI] 取段 2 条 → 喂给服务层（缓存共 2 条；最近序列 2）
[UI] EndTurnAsync → status=completed
[UI] 对局已销毁 → 停止投影
```

### 9.2 自检清单（DoD）

- [ ] 工程已引用 `Orc.Mcp`；服务层可实例化，`Tools.Count` 符合模式预期（47 / 24）。
- [ ] `MatchChanged(Created)` 触发后能建出 provider 并每帧 `Project` 成功。
- [ ] `MatchChanged(Disposed)` 后停止投影，不再访问旧对局。
- [ ] 需选位的动作能让 UI 收到待决请求，且玩家点选能**提交成功**。
- [ ] agent 先应答时，UI 提交失败并显示可读 `reason`（先到先服务生效）。
- [ ] 段消费模式明确（`Service` 或 `Host`），无"双向都取"。
- [ ] 玩家所有操作经 `IMcpActions`；代码中不存在对 `PlayManager`/`CommandManager` 的直接调用。
- [ ] agent 忙时操作得到 `pending-targeting`/`busy`，不出现并发进入引擎。
- [ ] 投影异常被隔离（保留上一帧、不崩 UI、不炸对局）。
- [ ] 未启用 `PromoteEnabled`/`CsxEnabled`（除非确需）。

### 9.3 回归护栏（引擎侧已有，可参考）

```bash
dotnet test tests/Orc.Mcp.Tests      # HostIntegrationTests（仲裁链路）+ ToolWhitelistTests（不越权）
```

---

## 10. API 参考（自包含）

### 10.1 `McpService`（服务层门面）

| 成员 | 签名 | 说明 |
|---|---|---|
| `Options` | `McpServiceOptions` | 运行选项（见 3.1） |
| `Tools` | `IReadOnlyList<McpToolDescriptor>` | 当前模式下可见的工具（顺序稳定） |
| `ActiveMatch` / `TryGetActiveMatch` | `Match?` / `bool TryGetActiveMatch(out Match? match)` | 活跃对局句柄（`IMcpHost` 亦有） |
| `InvokeAsync` | `Task<McpToolResult> InvokeAsync(string tool, JsonElement args, CancellationToken ct = default)` | 按 MCP 工具名调用（与 agent 同一实现） |
| `Sandbox` | `SandboxEnvironment` | 沙盒目录/脚手架/路径守卫 |
| `Segments` | `SegmentCache` | 段缓存（`PullSegments` 同源） |
| `ProcessStateNotes` | `IReadOnlyList<string>` | 本进程已发生的进程级变更留痕（不回滚但可见） |
| `Bridge` | `TargeterBridgeAdapter` | 唯一桥（含 `CurrentPending`、`ClaimantHost`/`ClaimantService` 常量） |
| `Executor` | `MatchExecutor` | 执行器（`Match`、`IsBusy`） |

### 10.2 `IMcpHost`（集成面，完整签名）

```csharp
Orc.Game.Match? ActiveMatch { get; }
bool TryGetActiveMatch(out Orc.Game.Match? match);
event EventHandler<MatchLifecycleEventArgs>? MatchChanged;
IDisposable SubscribeTargetingRequests(Action<PendingSelection> onRequest);
bool TrySubmitTargeting(string sessionId, IReadOnlyList<int> picks, bool cancel, string? claimant, out string? reason);
bool TryClaimTargeting(string sessionId, string claimant, out string? reason);
SegmentConsumer SegmentConsumer { get; }
void SetSegmentConsumer(SegmentConsumer consumer);
IReadOnlyList<Orc.Output.EventSegment> PullSegments(int? take = null);
void IngestSegments(IReadOnlyList<Orc.Output.EventSegment> segments);
```

### 10.3 `IMcpActions`（强类型动作面，完整签名）

见 7.1 的方法表——全部为 `Task<ActionOutcome> XxxAsync(..., CancellationToken ct = default)`。

### 10.4 关键类型与常量

```csharp
enum McpServiceMode { Authoring, Player }
enum SegmentConsumer { Service, Host }
enum MatchLifecycleChange { Created, Disposed }
sealed record MatchLifecycleEventArgs(MatchLifecycleChange Change, Orc.Game.Match? Match);

// 应答者标识（先到先服务判定的主体）
TargeterBridgeAdapter.ClaimantService   // "service"（agent / MCP 工具）
TargeterBridgeAdapter.ClaimantHost      // "host"（UI 宿主）

// 结果码（McpToolResult.Code；用于区分业务失败类型）
McpCodes.Ok / NotReady / ModeDenied / PathDenied / PendingTargeting / Targeting
        / Cancelled / InvalidSelection / RetryLimitExceeded / Timeout
        / ValidationFailed / EngineRejected / InvalidArguments / UnknownTool / NotSupported / Internal
```

### 10.5 工具清单（47 个，authoring；★＝player 模式不存在）

| 组 | 工具 |
|---|---|
| service | `service.info`、`service.tools` |
| sandbox ★ | `sandbox.create`、`sandbox.dispose`、`sandbox.state`、`sandbox.spawn`、`sandbox.kill`、`sandbox.damage`、`sandbox.set_points`、`sandbox.emit` |
| card ★ | `card.load`★、`card.list`★、`card.write`★、`effect.write`★、`asset.validate`★ |
| pack ★ | `pack.info`★、`file.list`★、`file.read`★、`asset.promote`★ |
| match | `match.end_turn`、`match.concede` |
| mulligan | `mulligan.begin`、`mulligan.done` |
| play | `play.unit_pre`、`play.unit`、`play.join`、`play.command_pre`、`play.command`、`play.counter`、`play.can_play` |
| command | `command.begin`、`command.move`、`command.attack`、`command.leave`、`command.availability` |
| target | `target.submit`、`target.cancel`、`target.claim` |
| observe | `observe.view`、`observe.snapshot`★、`observe.segments`★ |
| diag ★ | `diag.report`★、`diag.audit_chain`★、`diag.hooks`★、`diag.entry_points`★ |
| docs | `docs.list`、`docs.read` |

---

## 11. 故障排查

| 症状 | 可能原因 | 处置 |
|---|---|---|
| `hasProvider=False` / 投影拿不到 | ①在 `MatchChanged` 之前就建 provider；②对局未装配 UI 包（服务层默认已装配 `orc.builtin.core` + `orc.builtin.ui`）；③插件清单未复制到输出目录 | 只在 `Created` 里建 provider；确认 `package/plugins/orc.builtin.ui/manifest.json` 存在（否则用 `PluginsRoot` 指定） |
| 提交选靶一直失败 | ①请求已被 agent 认领（先到先服务）；②已超时按取消；③`sessionId` 不是当前请求 | 读 `reason`；重新触发动作再取新会话 |
| 在订阅回调里提交"卡住"或失败 | 回调运行在桥的线程上，同步提交会与门禁互相等待 | 回调只渲染+认领，**提交放到帧循环**（本手册 Step 3 即此形态） |
| 事件流断断续续/表现丢帧 | UI 与服务层都在 `TakeSegments()` | 明确声明 `SegmentConsumer`；声明为 `Host` 时记得 `IngestSegments` 喂回 |
| 动作返回 `pending-targeting` / `busy` | 上一动作未完成或正在等选靶 | 先 `target.submit`/`target.cancel`（或让玩家点选）；别重试同一动作 |
| 动作返回 `not-ready` | 尚未建局或对局已终局 | 检查 `ActiveMatch`/`observe.view` 的状态 |
| `unknown-tool`（player 模式） | 调用了调试类工具（player 模式下不存在） | 用第 10.5 节表核对；player 模式不得触达调试能力 |
| 投影偶发异常 | 引擎侧投影隔离口径 | 保留上一帧、记录日志，不要中断帧循环 |
| 沙盒写入被拒（`path-denied`） | 写的路径越出 `SandboxRoot` | 只用沙盒根内相对路径 |

---

## 12. 反模式清单

| 不要做 | 为什么 | 应该做 |
|---|---|---|
| 直接 `new Match(...)` 或调 `PlayManager`/`CommandManager` | 绕过服务层门禁 → 与 agent 并发进入引擎（引擎不防并发） | 走 `IMcpActions` |
| 自己实现 `ITargeterBridge` 注入给 `Match` | `Match` 只允许一个桥；会与 agent 争抢选靶 | 订阅 `SubscribeTargetingRequests` |
| 自己在帧循环 `TakeSegments()` 但不喂回 | agent 的 `observe.segments` 永远看不到段 | `IngestSegments` 喂回（或改用 `Service` 模式） |
| 在 `MatchChanged` 之外缓存 `Match` 句柄 | 对局销毁后句柄失效 | 每次用 `TryGetActiveMatch` 取 |
| 在订阅回调里做重活/阻塞/同步提交 | 回调在桥的线程上，会拖慢或卡住选靶 | 回调只记录，帧循环处理 |
| 把 `Cancelled` 当错误弹窗 | 取消是合法结局（人/agent 主动放弃、或超时兜底） | 按"用户放弃"处理，不报错 |
| 试图绕过沙盒根写正式资产 | 防污染设计 | `asset.promote`（需 `PromoteEnabled`，且要求无活跃沙盒局） |
| 多开对局 | 服务层只允许一个活跃对局 | 多对局用多进程 |

---

## 13. 与其它文档的关系

| 文档 | 适用形态 | 与本手册的关系 |
|---|---|---|
| **本手册** | **服务层共享**（agent + UI 同进程同局） | 主文档：接入步骤 + 流程 + 验收 |
| [`docs/ui-开发者交接文档.md`](ui-开发者交接文档.md) | **引擎直连**（UI 自己建局、自己实现桥） | 引擎侧 API 的完整参考（段结构、信号清单、入口签名）；本手册在其上加了"服务层作为唯一驱动真源"的约束 |
| [`samples/Orc.Mcp.HostSample`](../samples/Orc.Mcp.HostSample/Program.cs) | 参考实现 | 本手册每步的可运行对照 |
| [`samples/Orc.Mcp.Stdio/README.md`](../samples/Orc.Mcp.Stdio/README.md) | 无头 MCP 形态 | agent 侧接入（客户端配置片段） |
