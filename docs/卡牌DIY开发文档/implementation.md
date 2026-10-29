# Implementation — 卡牌 DIY 开发文档

> **⚠ 历史存档（2026-10-29）**：本文件记录 S1–S27 的实施步骤与分步实测记录，属过程留档。
> v4 编排实体化后大量符号已删除（`Effect`／`PassiveEffect`／`CardLoadout`／`Trigger<TView>`／`PrefabManager`／`EffectSystem`／
> `ActionTriggerBands`／`OrderFlowBands`），**不要照本文的符号调试**。现状以 [README](./README.md)、[02 章](./02-触发器与handler.md) 与 `src/` 为准。


实现 grill 已收敛并进入执行：R9（`Source` / `TriggerCard` 两面）已落地（S1–S7 Done），文档库与八个示例已产出（S8–S20 Done，含偏差记录）。需求新增 **R10 战斗机制** 与 **R11 伤害触发-处理分离**，故追加 **Part C（S21–S27）** 作为本轮执行对象，并同步回改 Part B 的 05/07/08 章、参考层与示例 8。

## Scope

- Target：`src/Orc/**`、`src/Orc.Game/**`、`docs/卡牌DIY开发文档/**`、`samples/card-pack-diy/**`、`tests/Orc.Tests/**`、`tests/Orc.Game.Tests/**`。
- Excludes：`*.sln` / `*.slnx` / 工程文件（需求 Constraints 冻结）；UI/宿主投影与 MCP 协议（需求 Excludes）。
- 已核实环境（2026-10-19）：`dotnet build tests/Orc.Game.Tests/Orc.Game.Tests.csproj` 成功（**2 个警告 0 个错误**）；`net8.0`；SDK `10.0.401`。
- **全量回归基线**：当前 **1650 通过**（R9 执行批次结束时点）。R10/R11 迁移后通过数必须 ≥1650（新增用例另计），且不得删除既有用例。
- **构建门禁口径**：全量 `dotnet build OrcEngine.sln` **允许既有警告**——`src/Orc.Plugins.BuiltIn/BuiltInPlugin.cs:143,149` 有 2 个既有 `CS8604`，故 `-warnaserror` **不可用于全量构建**。严格零警告门禁只适用于内核单工程 `dotnet build src/Orc/Orc.csproj -warnaserror`（Orc 自身零警告）；全局门禁不适用。
- 已核实基线：`Trigger<TView>._events` 为 `EventEntry`（`src/Orc/Core/Trigger.cs:834-950`）；`ExecutionFrame`（`Trigger.cs:1203-1250`）；`Trigger.Owner` 仅服务 `Bus.UnmountOwner`（`src/Orc/Core/Bus.cs:88`）；`GameUpdates` 30 个 `Emit*`（`GameUpdates.cs:305-800`）+ 回合五连（`Managers/TurnManager.cs:90-116`）；`Effect` 的 disposing 触发器与动态触发器均 `owner: this`（`Effect.cs:78-98`、`DynamicEffect.cs:276-291`）；视图 getter 对非 `Ref` 属性走 `Unbox_Any`（`src/Orc/Core/ViewProxyFactory.cs:213`），`Ref<T>` 型属性额外做失效校验（`:196-211`），Optional 属性遇 `null` 回退 `default`（`:181-190`）。

## 全局约定（R9 落地后生效，Part A 所有步骤共同遵守）

1. **类型**：新增的 `Source` 与 `TriggerCard` 视图属性一律 `Card?`；**既有流程视图里已是非空 `Card` 的 `Source` 保持不动**——`AttackFlowView.Source`（`src/Orc/Cards/BaseFlows.cs:44`）、`DamageFlowView.Source`（`BaseFlows.cs:58`）、`OrderFlowView.Source`（`src/Orc/Cards/OrderFlow.cs:34`）。理由：流程内必有来源，改可空会破坏既有契约。
2. **归一化唯一入口**：X1 的 `Ref<Entity>` 只在写入边界经 `SourceNormalizer.ToCard(object?)` 转换；禁止在各调用点手写 `as Card`。失效引用 / 非 `Card` 目标 → `null` + 隔离留痕（`CardsLog`，不抛）。
3. **错形态 fail-fast**：归一化之后若数据面仍出现与属性声明不符的形态（漏改仍写 `Ref` 等），按 `Unbox_Any` 既有语义**直接抛**（`InvalidCastException`），不做静默兜底。内核入口对 `TriggerCard` 载荷同样显式校验（非 `Card` 且非 `null` → 抛），错误信息须指向 `SourceNormalizer`。
4. **文档口径**（写进 02 / 05 章）：`view.Source` ＝事件引发者（只读、可空）；`view.TriggerCard` ＝当前 handler 的触发者（链上最后者胜）；handler 内再发信号时传 `source: view.TriggerCard`。

## Implementation Steps

### Part A — R9 触发器来源透传

### S1: 内核来源数据模型与审计面
- Status: Done
- 改动文件：`src/Orc/Core/Trigger.cs`（`Trigger<TView>`、`AddEvent`、`EventEntry`、`TriggerEventInfo`、`ExecutionFrame`）、`src/Orc/Core/Context.cs`、新增 `src/Orc/Core/TriggerPayloadKeys.cs`、新增 `src/Orc/Cards/SourceNormalizer.cs`。
- 关键签名：
  ```csharp
  // Trigger<TView>：registrant 追加末位，既有位置实参不受影响
  public Trigger(..., string? stableKey = null, object? registrant = null);
  public object? Registrant { get; }

  public TriggerRegistration Register(string name, Func<TView, Context, CancellationToken, Task> handler,
      int priority = 0, IEnumerable<string>? downstream = null, IEnumerable<string>? seams = null,
      object? registrant = null);
  public TriggerRegistration Register(string name, Func<TView, Context, CancellationToken, Task> handler,
      Enum band, int priority = 0, IEnumerable<string>? downstream = null, IEnumerable<string>? seams = null,
      object? registrant = null);
  public TriggerRegistration RegisterDisposal(string name, Func<TView, Context, CancellationToken, Task> handler,
      int priority = 0, IEnumerable<string>? downstream = null, IEnumerable<string>? seams = null,
      object? registrant = null);

  public sealed class TriggerEventInfo { public object? Registrant { get; } public Card? SourceCard { get; } }
  internal sealed class ExecutionFrame { internal Card? TriggerCard { get; set; } }
  public sealed class Context { public Card? TriggerCard => _frame?.TriggerCard; }
  public static class TriggerPayloadKeys { public const string TriggerCard = "TriggerCard"; }

  // 归一化唯一入口：契约 Card
  public static class SourceNormalizer
  {
      public static Card? ToCard(object? value, LogicEngine? engine = null, string? context = null);
  }
  ```
- Approach：`EventEntry.Registrant` 取值链＝`Register(registrant:)` → `Trigger.Registrant` → `_owner is Card or Effect` → `null`。`SourceCard` 读取期派生（`Card → c`；`Effect → e.HostOrNull`；其余 `null`），宿主销毁后自动降级、不抛。`SourceNormalizer` 内部读 `Ref<Entity>.TargetForObservation`（同程序集 internal 面，避免 `Value` 抛 `StaleReferenceException`），非 `Card`/失效 → `null` 且 `CardsLog.Write(engine, "source", …, LogLevel.Warning)`。
- 失败语义：`registrant` 非 `Card`/`Effect` 按 `null` 处理（不抛）；`SourceCard` 永不抛；`ToCard` 对失效/错型降级 `null` 但不静默（留痕）；`TriggerReflection.Create` 的 8 位位置实参需补第 9 个（`owner`），漏补＝`TargetParameterCountException`（所有动态效果装载即崩）。
- 回归风险：`TriggerEventInfo` 构造签名变化（唯一调用点 `EventEntry.ToInfo()`）；`TriggerReflection.Create` 反射实参数量；`SourceNormalizer` 新增公开类型（无破坏）。
- 验收命令：`dotnet build src/Orc/Orc.csproj -warnaserror`（仅内核单工程可用零警告门禁）；`dotnet test tests/Orc.Tests/Orc.Tests.csproj --filter "FullyQualifiedName~AuditChain|FullyQualifiedName~TriggerMetadata"`
- Acceptance:
  - [ ] `Trigger.Events` 每项可读 `Registrant` / `SourceCard`；非卡非效果＝`null`
  - [ ] `Owner` 的挂载/卸载行为零变化（既有 `UnmountOwner` 用例不改断言即通过）
  - [ ] 宿主销毁后 `SourceCard = null` 且不抛
  - [ ] `SourceNormalizer.ToCard` 对 `Card` / `Ref<Card>` / 失效 `Ref` / 其它对象 / `null` 五种输入各有断言，后两者留痕
- Rationale：注册者放在注册项上才能表达「共享流程触发器里不同效果各自注入」；`Owner` 与 `Registrant` 分离可把改动限制在归因面。
- Terminology: [触发者卡牌](../../CONTEXT.md)
- 实测：`dotnet build src/Orc/Orc.csproj -warnaserror` → 0 警告 0 错误；`dotnet test tests/Orc.Tests/Orc.Tests.csproj --filter "FullyQualifiedName~AuditChain|FullyQualifiedName~TriggerMetadata"` → 27 通过 / 0 失败
- 偏差：`SourceNormalizer.ToCard` 的五输入断言与审计用例在 S7 才新增（本步验收过滤区间内无对应用例，已由既有 AuditChain 27 条兜底）；`null` 输入不写留痕（合法空来源，避免玩家主动操作路径每次告警），失效引用与错型仍按文档留痕。

### S2: 逐事件解析与视图注入
- Status: Done
- 改动文件：`src/Orc/Core/Trigger.cs`（`InvokeAsync:406-491`、`RunEventsAsync:535-598`）。
- 关键签名：
  ```csharp
  // 入口初值（只认 TriggerCard 键；非 Card 且非 null ⇒ 抛）
  var entry = data is not null && data.TryGetValue(TriggerPayloadKeys.TriggerCard, out var raw) ? raw : null;
  if (entry is not null && entry is not Card)
      throw new InvalidCastException($"载荷 '{TriggerPayloadKeys.TriggerCard}' 必须为 Card（收到 {entry.GetType().Name}）；请在写入边界经 SourceNormalizer.ToCard 归一化。");
  frame.TriggerCard = (entry as Card) ?? parent?.TriggerCard;

  // 每个事件执行前（before 包装器之前）
  frame.TriggerCard = item.SourceCard ?? frame.TriggerCard;
  ctx.Data[TriggerPayloadKeys.TriggerCard] = frame.TriggerCard;   // 视图代理按名直读
  ```
- Approach：解析点落在「进入事件前」，故 before / replace / core / after 看到同一值；包装器与动作缝随所属 `EventEntry`，不单设注册者；事件间不还原（规则 4）。`Source` 键不读不写、不进帧。
- 失败语义：入口载荷形态非法 → `InvalidCastException`（fail-fast，不静默）；`TriggerCard` 允许 `null`；事件内 handler 异常仍按既有隔离记录（写入发生在 try 之外，不会半途跳过）；`ctx.Data` 同名键被覆盖属框架注入面（与 `Effect.WrapWithHostCore` 注入 `Host` 同手法，`Effect.cs:206-220`）。
- 回归风险：①「数据面被框架写入」可能打破断言 `ctx.Data` 键集合的既有用例（先跑 `ContextViewTests`/`EventStreamTests` 基线）；②`Replace` 包装器场景下 `TriggerCard` 变化可能影响依赖旧值的用例；③入口抛错会让「上游仍写 `Ref` 而漏归一化」的路径立刻暴露（属预期）。
- 验收命令：`dotnet test tests/Orc.Tests/Orc.Tests.csproj --filter "FullyQualifiedName~TriggerSource|FullyQualifiedName~ContextView"`
- Acceptance:
  - [ ] 卡/效果注册 handler 进入时 `TriggerCard` 被改写为其 source 卡
  - [ ] 非卡注册 handler 不改写，原样继承
  - [ ] 同帧多事件「后者覆盖前者」；嵌套 `InvokeAsync` 无初值时继承父帧
  - [ ] 顶层玩家操作 `TriggerCard = null`，连续多轮不残留
  - [ ] 入口载荷为 `Ref<Entity>` 时抛 `InvalidCastException`（错形态必抛）
- Rationale：R9 的「唯一边界」＝进入 handler，解析必须在事件循环内；写回数据面让 csx 与 C# 共用 `view.TriggerCard`，无需第二套读取 API。
- Terminology: [触发者卡牌](../../CONTEXT.md)、[触发器](../../CONTEXT.md)
- 实测：`dotnet test tests/Orc.Tests/Orc.Tests.csproj --filter "FullyQualifiedName~TriggerSource|FullyQualifiedName~ContextView"` → 13 通过 / 0 失败（本步时 `TriggerSource` 用例尚未新增，仅命中 ContextView）；S7 落地后复跑 → 22 通过 / 0 失败
- 偏差：入口载荷校验置于 `InvokeAsync` 的 try 之外（若置于 try 内会被既有「契约兜底」吞成 ContractFailure，无法满足「错形态必抛」）；`ctx.Data` 每事件写入同名键属框架注入面（键值可空，Optional 视图读取回退 default）。

### S3: 注册点接线（谁算「卡/效果注册者」）
- Status: Done
- 改动文件：`src/Orc/Cards/Effect.cs`（`Inject:182`、`InjectByFramework:244`）、`src/Orc/Cards/DynamicEffect.cs`（`TriggerReflection.Create/Register/RegisterDefaultBand/RegisterBandCore/RegisterCore`）、`src/Orc.Game/Cards/CounterCard.cs`（`AddEffectHandler:76-80`、激活注册循环 `:123-126`）、`src/Orc.Game/Cards/OrderCard.cs`（`AddPrePlayHandler:73`、`AddActiveHandler:86`）。
- 关键签名：
  ```csharp
  target.Register(name, handler, band, priority, registrant: this);
  TriggerReflection.RegisterDefaultBand(target, viewType, name, handler, priority, registrant: this);
  internal static TriggerRegistration Register(..., object? registrant = null);
  internal static TriggerRegistration RegisterDefaultBand(..., int priority, object? registrant = null);
  private static TriggerRegistration RegisterBandCore<TView>(..., int priority, object? registrant);
  public void AddEffectHandler(string name, Delegate handler, object? registrant = null);
  // OrderCard 的两个公开装配入口（现状签名）；各加尾随 registrant 并透传到 Register
  public TriggerRegistration AddPrePlayHandler(string name,
      Func<CardTriggerView, Context, CancellationToken, Task> handler, int priority = 0,
      object? registrant = null);
  public TriggerRegistration AddActiveHandler(string name,
      Func<CardTriggerView, Context, CancellationToken, Task> handler, int priority = 0,
      object? registrant = null);
  ```
- Approach：只为**有明确效果/卡归属**的注册点显式传 `registrant`；`UnitCard`/`OrderCard`/`CounterCard` 的**卡自身**内置链 handler **必须传 `registrant: this`**（注册者＝本卡，链内信号方可归因到本卡）；`BaseFlows`/`OrderFlow`/`CardLifecycle`/`CommandManager` 的**引擎级内置流程** handler 不传（`null`＝透传）。`AddPrePlayHandler`/`AddActiveHandler` 改为 `PrePlayTrigger.Register(name, handler, priority, registrant: registrant)` 与 `PlayTrigger.Register(name, handler, priority, registrant: registrant)`；调用方（授予指令效果者）传 `registrant: effect`。动态触发器构造期 `owner: effect` 已满足回退；`TriggerReflection.Create` 补第 9 个 ctor 实参 `owner`。`CounterCard` 的 `AddEffectHandler` 同理由授予方传 `registrant: effect`。
- 失败语义：漏传＝该注册项退化为「透传者」（静默、不报错），故以「`grep "\.Register("` 清单逐条判定」为验收，不依赖编译期；`AddEffectHandler` 新增参数默认为 `null`，旧调用零破坏。
- 回归风险：①`TriggerReflection.Create` 参数数量错＝动态效果全崩（`DynamicEffectTests`/`PrefabEffectLoadingTests` 兜底）；②`Register` 重载新增末位可选参数，与现有 `seams:` 具名调用兼容（需确认无 `params` 歧义）。
- 验收命令：`dotnet test tests/Orc.Tests/Orc.Tests.csproj --filter "FullyQualifiedName~DynamicEffect|FullyQualifiedName~EffectLoading"`；`dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --filter "FullyQualifiedName~Counter|FullyQualifiedName~PlayChain"`
- Acceptance:
  - [ ] 全仓库 `Register(` 调用点逐条判定并留注释（卡/效果 / 流程 / 无主）
  - [ ] 效果注入 handler 读到 `TriggerCard` ＝效果宿主卡
  - [ ] 引擎流程 handler 不引入新值（继承）
- Rationale：注册者只有注册点知道；集中到注册 API 显式传递，避免第二套隐式「当前效果」上下文。
- Terminology: [装载链](./CONTEXT.md)
- 实测：`dotnet test tests/Orc.Tests/Orc.Tests.csproj --filter "FullyQualifiedName~DynamicEffect|FullyQualifiedName~EffectLoading"` → 20 通过 / 0 失败；`dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --filter "FullyQualifiedName~Counter|FullyQualifiedName~PlayChain"` → 61 通过 / 0 失败（修正后复跑；初版为 60）
- 判定清单（全仓库触发器 `Register(` 调用点）：卡/效果归属＝`Effect.Inject`、`Effect.InjectByFramework`、`CounterCard.AddEffectHandler`＋激活注册循环、`OrderCard.AddPrePlayHandler`/`AddActiveHandler`、动态触发器构造期 `registrant=effect`、`UnitCard`（5 处内置链）、`OrderCard`（打出宣告/打出收尾）、`CounterCard`（使用反制）、`KeywordEffects`（词条效果）；引擎级内置流程（不传、继承）＝`BaseFlows`（3 处）、`OrderFlow`、`CommandManager`（4 处）、`RetriggerSystem`、`CardLifecycle`。
- 偏差：①`CounterCard.AddEffectHandler` 保留 `Func<CardTriggerView, Context, CancellationToken, Task>` 形参（文档记作 `Delegate`；保留强类型可避免运行期强转，新增 `registrant` 尾参语义不变）；②`KeywordEffects.cs:344` 初版未传 `registrant`，已由下方「修正」补齐。
- 修正：R9 复核发现语义断点——卡自身内置链 handler 未传 `registrant`（`Trigger<CardTriggerView>` 构造期也无 owner），`ResolveRegistrant` 的 owner 回退取不到卡，导致 `card.played`/`unit.deployed`/`unit.joined`/`counter.triggered` 等信号的 `Source` 恒为 `null`。已补：①`UnitCard` 5 处（打出链/部署链/加入链/单位化/升级链）、`OrderCard` 2 处（打出宣告/打出收尾）、`CounterCard` 1 处（使用反制）全部改传 `registrant: this`；②`KeywordEffects` 词条效果注册追加 `registrant: this`（效果归属）；③发信号显式传来源——`EmitCardPlayed`/`EmitCardPlaced`/`EmitUnitDeployed`/`EmitUnitJoined` 传 `view.TriggerCard ?? 本卡`、`EmitCounterTriggered` 传 `view.TriggerCard ?? card`、`EmitCardDied` 传 `source: killer`；④`tests/Orc.Game.Tests/TriggerSourceSignalTests.cs` 新增 4 条归因用例（`unit.deployed`＝被部署单位、`card.played`＝被打出卡、`counter.triggered`＝反制卡、`card.died`＝Killer）。视图面说明：上述链视图均为 `CardTriggerView`，S5 已提供 `TriggerCard` 属性，故取帧值即可；`?? 本卡` 仅为防御性回退（链上无触发者时仍能归因到自身）。实测：S3 过滤 20＋61 通过；全量 `dotnet test OrcEngine.sln` → 1635 通过 / 0 失败。

### S4: 游戏层信号接入（30 个助手 + 回合五连）
- Status: Done
- 改动文件：`src/Orc.Game/GameUpdates.cs`、`src/Orc.Game/Managers/TurnManager.cs`（`EmitTurnAsync`）、`src/Orc.Game/GameHooks.cs`（新增 `PayloadSource`）、`src/Orc.Game/Output/GameHooksJson.cs`。
- 关键签名：
  ```csharp
  // 尾随 source 置于 ct 之后：既有 (engine, card, ct) 位置调用零破坏
  public static Task EmitCardDiscarded(LogicEngine engine, Card card, Player player,
      CancellationToken ct = default, Card? source = null);
  private Task EmitTurnAsync(string updateType, Player player, int turnNumber,
      CancellationToken ct = default, Card? source = null);

  public const string PayloadSource = "Source";
  ```
- Approach：每个 `Emit*` 在载荷尾部追加 `[PayloadSource] = source`（**恒写，缺省 `null`**，键集合稳定、Optional 面可判）；回合五连与玩家主动操作恒 `null`；`GameHooksJson.SignalMetadata` 31 条 `SignalEntry` 的载荷键集合追加 `PayloadSource`。
- 失败语义：`source` 允许 `null`（无引发者），不参与既有 `ArgumentNullException` 校验；其余参数校验顺序不变。
- 回归风险：①载荷多一键会打破「载荷键集合精确断言」用例（`GameHooksTests`/`GameUpdatesTests`），需逐条改为含 `Source`；②`GameHooksJson` 对外契约变更需同步参考层信号表。
- 验收命令：`dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --filter "FullyQualifiedName~GameHooks|FullyQualifiedName~GameUpdates|FullyQualifiedName~TurnCycle"`
- Acceptance:
  - [ ] 31 条信号均可在载荷携带 `Source`（30 个 `Emit*` + 五连）
  - [ ] 不传 `source` 时载荷 `Source = null`，既有行为不变
  - [ ] `GameHooksJson` 每条信号载荷键含 `Source`
- Rationale：尾随可选参数是唯一不破坏既有调用点的加法；恒写 `null` 让「无引发者」与「键缺失」不混淆。
- Terminology: [触发点（信号）](./CONTEXT.md)
- 实测：`dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --filter "FullyQualifiedName~GameHooks|FullyQualifiedName~GameUpdates|FullyQualifiedName~TurnCycle"` → 24 通过 / 0 失败
- 偏差：为通过既有「载荷键集合精确计数」用例，同步把 `GameHooksTests` 的 `17 → 18`（该用例断言 `GameUpdates` 的 `Payload*` 常量数与 `GameHooks.PayloadKeys` 对齐；属断言形态迁移、意图不变）。

### S5: 视图面与 X1 收敛
- Status: Done
- 改动文件：`src/Orc/Cards/CardLifecycle.cs`（`CardEventView`）、`src/Orc.Game/Triggers/CardTriggerView.cs`、`src/Orc.Game/Cards/RetriggerSystem.cs`、`src/Orc.Game/Commanding/CommandViews.cs`、`src/Orc.Game/Commanding/CommandManager.cs`、`tests/Orc.Game.Tests/TriggerCardTests.cs`。
- 关键签名：
  ```csharp
  // 新增两面（一律 Card?）；流程视图既有的非空 Card Source 保持不动
  [Optional][Read] public virtual Card? Source { get; set; }
  [Optional][Read] public virtual Card? TriggerCard { get; set; }

  // X1 对外签名不变（玩家主动＝空、效果引发＝宿主卡）
  public Task<EventStream> BeginCommandAsync(UnitCard unit, CancellationToken ct = default,
      Ref<Entity>? triggerCard = null);

  // 写入边界：唯一归一化点
  data[CommandDataKeys.TriggerCard] = SourceNormalizer.ToCard(triggerCard, _engine, "command.triggerCard");
  ```
- Approach：`CommandDataKeys.TriggerCard` 字面值＝`TriggerPayloadKeys.TriggerCard`，值统一为 `Card`；`CommandManager` 原逐层手工透传（`DispatchSelectedAsync→DispatchAttackAsync/DispatchMoveAsync`）保留但标注「兼容入口，帧继承已覆盖」。4 个命令视图的 `TriggerCard` 属性 `Ref<Entity>?` → `Card?`；`TriggerCardTests` 6 条断言改直读，**不改变 X1 对外语义**。命令链写入统一走 `SourceNormalizer`，不允许散落 `as Card`。
- 失败语义：`Ref` 失效 / 指向非 `Card` → `ToCard` 返回 `null` + 留痕（不抛）；若数据面出现与属性声明不符的形态（漏改仍写 `Ref`）→ 视图 getter `Unbox_Any` 抛 `InvalidCastException`（fail-fast，不静默兜底）。
- 回归风险：①`TriggerCard` 属性改型是公开面破坏性变更（`src/Orc.UI`、`src/Orc.Mcp` 若读该属性需同步修复）；②「显式入口写入」与「帧继承」需保证不互相覆盖（入口写入先于帧解析）；③新增两个视图属性会改 `ViewMeta` 缓存键（按类型缓存，天然隔离）。
- 验收命令：`dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --filter "FullyQualifiedName~TriggerCard"`；`dotnet build OrcEngine.sln`（允许既有 2 个 CS8604）
- Acceptance:
  - [ ] `view.Source` 与 `ctx.Get("Source")` 同值；`view.TriggerCard` 为帧值
  - [ ] 监听者 handler 内 `view.TriggerCard` ＝监听者自身宿主（≠ `Source`），有专项用例
  - [ ] 既有流程视图 `AttackFlowView/DamageFlowView/OrderFlowView.Source` 仍为非空 `Card`
  - [ ] X1 六条用例迁移后全绿，两向语义断言保留
  - [ ] 数据面错形态 → 视图读取抛 `InvalidCastException`
- Rationale：单面 `TriggerCard` 会被监听者自身注册覆盖，「敌方弃牌」读不到敌方；两面分工是 R6e 成立的前提。X1 收敛为入口初值后机制只剩一条。
- Terminology: [触发者卡牌](../../CONTEXT.md)、[触发点](./CONTEXT.md)
- 实测：`dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --filter "FullyQualifiedName~TriggerCard"` → 8 通过 / 0 失败；`dotnet build OrcEngine.sln` → 0 错误（增量构建 0 警告；全量构建的 2 个既有 CS8604 见全局口径）
- 偏差：①`RetriggerSystem.cs` 无视图面/X1 触点，本步未改动（其内部导航载荷 `Target`/`Chain` 不属 R9 两面）；②`EffectSystemTests.cs` 的 X1 探针随公开面改型同步迁移（`List<Ref<Entity>?>` → `List<Card?>`、`Host.Ref` → `Host`，断言意图不变）。

### S6: 动作面透传（EffectRuntime / 弃置入口）
- Status: Done
- 改动文件：`src/Orc.Game/Managers/PlayerManager.cs`（`DiscardCardAsync:266`）、`src/Orc.Game/HandLimitBurn.cs`（`DestroyAndEmitDiscardedAsync:33-48`）、`src/Orc.Game/Effects/EffectRuntime*.cs`、`src/Orc.Game/Cards/ProgramKeywordComponent.cs`、`src/Orc.Game/EffectParsing/Templates/ops/*.csx.tpl`（统一传 `self`）。
- 关键签名：
  ```csharp
  public async Task DiscardCardAsync(Player player, CardBase card,
      CancellationToken ct = default, Card? source = null);

  internal static Task DestroyAndEmitDiscardedAsync(LogicEngine engine, Player player, Card card,
      CancellationToken ct = default, Card? source = null);

  // EffectRuntime：无状态门面（ResolveFor(card)），无宿主字段；所有动作新增尾随可选 source
  public Task DrawAsync(Card viewer, int count, CancellationToken ct = default, Card? source = null);
  public Task<DeckFetchResult> FetchFromDeckAsync(Card target, CancellationToken ct = default, Card? source = null);
  public Task<bool> BuffAsync(Card target, int attack, int defense, EffectDuration duration = EffectDuration.Permanent,
      CancellationToken ct = default, Card? source = null);
  // Grant/Revoke/SetKeywordValue/Move/Upgrade/Reveal/Silence/Pin/AddToHand/ShuffleIn/Gain*/Lose* 同形追加尾随 source
  // Damage/Kill 既有 Card? source 形参（位于 ct 前）沿用、位置不变——语义已满足，避免破坏既有调用
  ```
- Approach：`EffectRuntime` 全部动作方法统一在 `ct` 之后追加 `Card? source = null`；**不做任何猜测性回退**（不拿 `target`/`viewer` 当 source）。`source` 只做两件事：写入下游信号的 `Source` 载荷、作为下游动作（如伤害来源/击杀者/弃牌施动方）的显式入参。默认 csx 模板（`src/Orc.Game/EffectParsing/Templates/ops/*.csx.tpl`）统一以 `self` 作为 source 传入（`DamageAsync(target, amount, self, ct)`；`DrawAsync(self, count, ct, self)` 等）；词条程序 33 个操作在对应发射点同样显式传宿主卡；`DiscardCardAsync` 透传到 `card.discarded`。文档 05 章必须写明「不传 `source` ⇒ 下游信号 `Source = null`」。
- 失败语义：`source` 永不为动作增加前置条件（`null` 合法＝下游 `Source = null`，不猜测、不回退）；动作自身校验失败顺序与异常类型不变（`ArgumentNullException` / `InvalidOperationException` / `DeckFetchResult.Rejected`）。
- 回归风险：①`DiscardCardAsync` 尾随参数不破坏既有位置调用（`G7HandOperationsTests.cs:315-391`）；②`EffectRuntime` 20+ 方法逐一手改易漏（以「方法 → 下游发射点」清单核对）；③词条 33 操作建议按资源/抽牌/伤害/移动分批提交。
- 验收命令：`dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --filter "FullyQualifiedName~EffectRuntime|FullyQualifiedName~Discard|FullyQualifiedName~KeywordProgram"`
- Acceptance:
  - [ ] `DiscardCardAsync(p, c, source: enemyCard)` 后 `card.discarded.Source = enemyCard`
  - [ ] `DiscardCardAsync(p, c)` 后 `Source = null`
  - [ ] `EffectRuntime` 每个动作至少一条用例断言「显式传 source ⇒ 下游信号携带该 source；不传 ⇒ `null`」
- Rationale：施动方只有动作面知道；可选透传是唯一不改变动作语义的加法。
- Terminology: [动作面](./CONTEXT.md)
- 实测：`dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --filter "FullyQualifiedName~EffectRuntime|FullyQualifiedName~Discard|FullyQualifiedName~KeywordProgram"` → 73 通过 / 0 失败
- 偏差：①`EffectRuntime` 的 19 个动作已按文档追加尾随 `source`，但本期**只有** `DamageAsync`/`KillAsync`（既有形参）与弃置路径有下游消费方；其余动作的 `source` 为下游接线预留（写入 `card.drawn`/`slot.*`/`unit.*` 等的服务不在 S6 改动文件清单内，未改动）。②`CostModAsync`/`DeployCostModAsync`/`DeclareAuraAsync`/动态挂载面不在文档列举集合内，未加 `source`；对应模板（`costMod`/`aura`）保持原样，其余 14 个 ops 模板统一传 `self`。③模板文本变化使 `KeywordEffectChannelTests` 的渲染断言追加 `, self`（断言形态迁移，意图不变）。

### S7: R9 测试与回归
- Status: Done
- 改动文件：新增 `tests/Orc.Tests/TriggerSourceTests.cs`、新增 `tests/Orc.Tests/SourceNormalizerTests.cs`、新增 `tests/Orc.Game.Tests/TriggerSourceSignalTests.cs`、迁移 `tests/Orc.Game.Tests/TriggerCardTests.cs`。
- 关键签名（用例名即验收）：
  ```text
  CardRegistered_Handler_Overrides_TriggerCard
  NonCardRegistered_Handler_Inherits_TriggerCard
  LastCardRegistered_Handler_Wins_On_Shared_Trigger
  TopLevel_Player_Action_Leaves_TriggerCard_Null_And_Does_Not_Leak
  Explicit_Payload_TriggerCard_Seeds_Initial_Value
  Audit_Exposes_Registrant_And_SourceCard
  SourceCard_Degrades_To_Null_After_Host_Destroyed
  Payload_With_Ref_Under_TriggerCard_Throws            // 错形态必抛（入口）
  View_With_Mismatched_Data_Throws                     // 错形态必抛（视图 getter）
  Discarded_Carries_Source_On_Payload_And_View
  Discarded_By_Enemy_Triggers_Discarded_By_Self_Does_Not   // R6e 双向
  TurnSignals_Always_Carry_Null_Source
  ```
- Approach：内核 7 条 + 归一化 5 输入 + 错形态 2 条放 `tests/Orc.Tests`；游戏层 4 组放 `tests/Orc.Game.Tests`（复用 `TestHost`/`CommandTestKit`）。回归前先 `dotnet build OrcEngine.sln`（允许既有 2 个 CS8604）。
- 失败语义：断言只比世界状态/视图值，不比日志文本；随机用例固定种子（`RandomService`）。
- 回归风险：1600+ 用例中，凡断言「载荷键集合」「`ctx.Data` 键集合」「视图属性类型」者均可能受影响；迁移原则＝**只改断言形态，不改断言意图，不删用例**。
- 验收命令：
  - `dotnet test tests/Orc.Tests/Orc.Tests.csproj --filter "FullyQualifiedName~TriggerSource|FullyQualifiedName~SourceNormalizer"`
  - `dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --filter "FullyQualifiedName~TriggerSourceSignal|FullyQualifiedName~TriggerCard"`
  - `dotnet test OrcEngine.sln`
- Acceptance:
  - [ ] 上述 12 类用例全绿（含两条错形态必抛）
  - [ ] 全量 `dotnet test` 通过（既有 1600+ 无回归）
  - [ ] 新增用例不依赖时间与未固定种子的随机
- Rationale：R9 验收均为行为断言；拆到「规则三条 / 顶层空 / 审计 / 归一化 / 错形态」粒度，失败可直接定位是解析点、注册点还是载荷层。
- Terminology: [触发者卡牌](../../CONTEXT.md)
- 实测：`dotnet test tests/Orc.Tests/Orc.Tests.csproj --filter "FullyQualifiedName~TriggerSource|FullyQualifiedName~SourceNormalizer"` → 14 通过 / 0 失败；`dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --filter "FullyQualifiedName~TriggerSourceSignal|FullyQualifiedName~TriggerCard"` → 15 通过 / 0 失败；`dotnet test OrcEngine.sln` → 1635 通过 / 0 失败（Orc.Tests 344、Orc.Game.Tests 1142、Orc.Script 17、Orc.Packages 72、Orc.Mcp 47、Orc.Ai 13）；S3 修正后复跑一致
- 偏差：文档 12 类用例名逐一落地（内核 9 条含 2 条错形态＋归一化 5 输入；游戏层 3 条＋监听面并入弃置用例）；S2 的验收过滤在本步前无 `TriggerSource` 用例（当时仅 13 条 ContextView），S7 落地后复跑为 22 通过。

### Part B — 文档库与示例

> Part B 的「关键签名」列指该步骤的**关键产物与结构**（文件、小节、表头、命令），因为文档步骤没有 C# 签名。

### S8: 代码基线与事实源锚点表
- Status: Done
- 改动文件：新增 `docs/卡牌DIY开发文档/reference/_anchors.md`（内部资产，不进 README 索引）。
- 关键签名/结构：表头固定四列 `| 机制面 | 事实点 | 符号名 | 定位 |`；覆盖 31 条信号、触发器/band、handler 与装载链、判定器 21 名、targeter、wrapper 三类、"EffectRuntime 公开面、`KeywordIds`、`KeywordProgram`（11/17/33/14）、`*.card.json` 六组件、`*.prefab.json` v2。
- Approach：R9 落地后**重取一次**基线（`Source`/`TriggerCard` 两面、`Registrant`/`SourceCard` 审计字段、`SourceNormalizer`）。定位只用「符号名 + 文件」，行号仅作提示。
- 失败语义：查不到符号＝该事实点不得写进正文（宁缺勿造）；同一机制出现两个符号名＝登记为术语冲突，交 CONTEXT 裁决。
- 回归风险：R9 后续微调（如签名再变）会让锚点表过期——收口时以 S20 的「符号存在性检查」兜底。
- 验收命令：`grep -c "^| " docs/卡牌DIY开发文档/reference/_anchors.md`；抽查 10 条逐条 `grep -n "<符号名>" src`
- Acceptance:
  - [ ] 每个条目可由符号名在 `src/` 定位
  - [ ] 信号表与 `GameUpdates` 常量逐条对齐（含 `Source`）
  - [ ] 无「规划中/未实现」条目
- Rationale：文档不得描述未实现能力；R9 改了载荷与元数据，正文前必须重取基线。
- Terminology: [数据侧](./CONTEXT.md)、[代码侧](./CONTEXT.md)
- 实测：`grep -c "^| " docs/卡牌DIY开发文档/reference/_anchors.md` → 220（其中**事实行 200**、表头/分隔行 20）；抽查 10 条 `grep -n "<符号名>" src` → 10/10 命中；另以脚本逐行校验 200 条 `file:line`：文件存在、行号在范围内、该行内容含对应符号 → **0 处失配**。
- 偏差：文档口径与代码现状有三处不符，已按代码登记并在表内标注「口径校正」——①信号：文档记 31 条，`GameUpdates`/`GameHooks.Signals` 实为 **34**（多 `card.dying`/`card.leaving`/`keyword.attack.finished`；31 只是 `GameHooksJson.SignalMetadata` 的导出条数）；②判定器：文档记 21 名，`JudicatorNames` 实为 **23**（22 内置 ＋ 1 示范）；③预制体：文档记 v2，`EffectSnapshot.SchemaVersion` 实为 **3**（读入接受 2/3、写出恒 3）。

### S9: 文档库骨架与模板层
- Status: Done
- 改动文件：`README.md`、`00`–`08` 九个章节文件、`examples/README.md`、`reference/README.md`、`_template/章节模板.md`、`_template/示例模板.md`、`_template/登记规则.md`。
- 关键签名/结构：章首元信息块五件套（本章解决什么 / 前置 / 读完你能做什么 / 对应代码路径 / 本章自检）；README 三条阅读路径（新手上路 / 按任务查 / 直接抄示例）+ 全量索引表 + 章节状态表（`未开始 / 草稿 / 完成 / 证据已跑`）。
- Approach：先落空壳与模板，模板本身可读（占位段落不写 TODO 而是写「该节回答的问题」）；登记规则三步＝复制模板 → 按编号命名 → README 索引登记。
- 失败语义：模板缺任一元信息项＝该章不合格；README 索引漏登记＝S20 直接失败。
- 回归风险：后续增章若不复制模板会破坏同构；登记规则须写「不改既有章」。
- 验收命令：`ls docs/卡牌DIY开发文档/{00..08}-*.md | wc -l`（应为 9）；`grep -c "本章解决什么" docs/卡牌DIY开发文档/0*.md`（每篇 ≥1）
- Acceptance:
  - [ ] 目录骨架与 R2 固定结构逐项一致
  - [ ] 九章空壳五件套齐全且非空
  - [ ] README 三条路径各给「起始文件 + 3–5 步跳转序列」
- Rationale：骨架先行保证同构，避免收尾返工。
- Terminology: [文档库](./CONTEXT.md)、[阅读指南](./CONTEXT.md)、[章节模板](./CONTEXT.md)
- 实测：`ls docs/卡牌DIY开发文档/{00..08}-*.md | wc -l` → 9；`grep -c "本章解决什么" docs/卡牌DIY开发文档/0*.md` → 9 篇各 1 次（五件套齐全）；`README.md` 三条路径各含起始文件 + 3–5 步跳转。
- 偏差：①模板第三件按执行口径命名为 `_template/新增文件登记步骤.md`（文档记 `登记规则.md`，内容等价）；②未建 `examples/README.md`（本批明确不建示例，README 以「未开始」占位且不建链接）与 `reference/README.md`（README 直接索引三张表）；③九章为空壳（仅五件套＋「本章将回答的问题」＋状态位），正文留待 S11+。

### S10: 参考层三表
- Status: Done
- 改动文件：`reference/信号表.md`、`reference/API速查.md`、`reference/词条表.md`。
- 关键签名/结构：信号表列＝`信号 | 发射时机 | 载荷键 | 引发者来源 | 典型用法`；API 速查列＝`方法签名 | 层归属 | 失败语义 | 数据侧可达`；词条表列＝`标识 | 类型 | 参值 | 读取口 | 打标状态`。
- Approach：纯查表、无叙述；正文只链接不复制；`Source` 在信号表单列「引发者来源」解释（`Source` 可空、`TriggerCard` 为帧值）。
- 失败语义：条目数与 S8 取证不符＝表格作废重取；签名与 `src/` 不一致＝改表不改码。
- 回归风险：R9 后续若再动签名，三表需同步（S20 抽查）。
- 验收命令：`dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --filter "FullyQualifiedName~GameHooks"`；人工核对三表条目数
- Acceptance:
  - [ ] 三表条目数与 S8 一致（信号 31、`EffectRuntime` 全覆盖、`KeywordIds.All` 全覆盖）
  - [ ] 每条注明「数据侧可达 / 仅代码侧可达」
- Rationale：参考层集中维护，正文改写不牵动表格。
- Terminology: [词条](../../CONTEXT.md)、[动作面](./CONTEXT.md)
- 实测：`dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --filter "FullyQualifiedName~GameHooks"` → 15 通过 / 0 失败；三表条目数人工核对＝信号 **38**（34 `GameUpdates` ＋ 4 `Updates`）、动作面 **27**（`EffectRuntime` 26 个公开方法 ＋ `CastAsync`）、判定器 **23**、选择器 **14**（11 模板 ＋ 3 类型）、词条 **19 标识 ＋ 32 操作 ＋ 4 张表**（11/17/32/14）。
- 偏差：①第三表按执行口径改为 `reference/缺口表.md`（文档记 `词条表.md`）；词条清单/操作并未丢失，并入 `reference/api速查.md` §4（保留 R7「`KeywordIds.All` 全覆盖、四表逐条」）。②文件名按执行口径为小写 `api速查.md`（文档记 `API速查.md`）。③`KeywordProgram.Operations` 实为 **32** 条（文档/需求记 33），已按代码登记并标注。

- **R10/R11 追加（待回改）**：API 速查增 `BattleAsync(Card first, Card second, Card? source = null, ct)` 与三端注册口（`DamagePipeline.CombatDamage/EffectDamage/BattleDamage/Apply`）；词条表为「重甲/免疫/伏击」增「挂载层」列（`combat.damage` / `damage.apply` / `unit.attack`）；信号表脚注「三端与处理端是具名触发器，不是总线信号，不进信号表正文」。

### S11: 00 总览与心智模型
- Status: Done
- 改动文件：`00-总览与心智模型.md`。
- 关键签名/结构：六节＝读者与前置 / 两条路径与判据 / 五段心智模型 / 最小可加载卡骨架 / 「装载≠触发、注册≠生效」/ 来源两面示意。
- Approach：一页内完成 R1 的读者说明；来源示意图用 ASCII，标出「发射时定 `Source` → 帧解析 `TriggerCard` → 再发信号时 `source: view.TriggerCard`」三步。
- 失败语义：读者按本章仍无法产出可加载卡＝本章不合格（用 S17 的卡包做对照验收）。
- 回归风险：与 02/05 章两面口径漂移——统一使用「全局约定 4」措辞。
- 验收命令：`dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --filter "FullyQualifiedName~CardDistributionValidator"`（骨架字段可校验）
- Acceptance:
  - [ ] 不读 `src/` 即可照本章产出一张可加载的卡
  - [ ] 来源示意图与 S5 语义一致
- Rationale：R9 改变了「谁引发」的可观测面，必须进入心智模型。
- Terminology: [DIY 作者](./CONTEXT.md)、[装载链](./CONTEXT.md)、[触发点（信号）](./CONTEXT.md)
- 实测：`docs/卡牌DIY开发文档/00-总览与心智模型.md` 五件套齐全（本章解决什么/前置/读完你能做什么/对应代码路径/本章自检）；§4 卡数据落临时目录后经 `CardDistributionValidator.Validate` → `SuccessfulCardCount=1 / SuccessfulInlineSnapshotCount=1 / errors=0 / warnings=0`；来源两面 ASCII 图与 S5 语义一致（`Source`＝载荷、`TriggerCard`＝帧值）。
- 偏差：文档要求「不读 `src/` 即可照本章产出可加载卡」——本章给出六组件表＋可直接落盘的 `*.card.json`（内联效果），但**装载/进对局**仍需宿主调用 `CardDataLoader`/`Match`，本章只覆盖到「可加载」的骨架，不覆盖宿主接入（属 Excludes）。

### S12: 01 效果原理
- Status: Done
- 改动文件：`01-效果原理.md`。
- 关键签名/结构：五节（装载链 / 事件到 handler / handler 改状态 / 数值传播 / 销毁）+ 端到端时序图 + 排查树（≥6 分支，含「来源为空」分支）。
- Approach：每节给「谁调用谁 + 代码路径 + 断点观察点」；`card.destroyed` 与 `card.died` 单列对照表（发射点 / 载荷 / 触发者 / 是否发 `unit.position.changed`）。
- 失败语义：排查树分支不可独立判定＝重写；时序图节点名与代码符号不一致＝重画。
- 回归风险：R9 在装载链前后各插入一步（注册者/来源），时序图节点需含 `Registrant`。
- 验收命令：人工走查 + `grep -n "card.destroyed\|card.died" src/Orc/GameUpdates.cs`（对照表与代码一致）
- Acceptance:
  - [ ] 时序图覆盖五段，节点名与符号一致
  - [ ] `card.destroyed` 与 `card.died` 对照表齐全
  - [ ] 排查树 ≥6 分支且可独立判定
- Rationale：R3 是全书地基，排查树被方法论章复用。
- Terminology: [装载链](./CONTEXT.md)、[disposing 触发器](../../CONTEXT.md)、[集中触发](../../CONTEXT.md)
- 实测：`mermaid sequenceDiagram` 覆盖五段（装载/触发/结算/传播/销毁），节点名取代码符号（`CardLoadout.MountEffect`/`Effect.ExecuteMount`/`Trigger.InvokeAsync`/`EffectRuntime`/`Effect.ExecuteDispose`）；`card.destroyed` vs `card.died` 七列对照表；排查树 **8 分支**（文档要求 ≥6），每分支带独立判定方法与修法，含「来源为空」分支；附一个可编译的代码侧片段（`PassiveEffect` + `Inject` 到 `AttackFlowBands.Resolve`）。
- 偏差：文档要求「`grep -n "card.destroyed\|card.died" src/Orc/GameUpdates.cs` 对照代码」——`card.destroyed` 常量实际在内核 `src/Orc/Core/Updates.cs`（不在 `GameUpdates.cs`），对照表按真实发射点（`Updates.CardDestroyed` / `GameUpdates.CardDied`）书写。

### S13: 02–05 机制章
- Status: Done
- 改动文件：`02-触发器与handler.md`、`03-判定器与wrapper.md`、`04-目标选择targeter.md`、`05-动作面与运行时API.md`。
- 关键签名/结构：每机制四段（是什么 / 何时用 / 完整 API 表 / 最小片段 + 边界）；02 章增设「注册者与来源」小节，写死四面：`Register(registrant:)`、审计面 `Registrant`/`SourceCard`、`Source` 与 `TriggerCard` 分工、再发信号传 `source: view.TriggerCard`；05 章动作面表增「是否透传 source」列。
- Approach：API 表四列（名称 / 签名 / 层归属 / 失败语义）；wrapper 给 `w2→w1→core→w1→w2` 与严格签名校验；targeter 给「后端给候选、前端只采集手势」与三态；判定器给「全局层 → 玩家层」两层管道。
- 失败语义：数据侧不可达处未标注＝该章不合格；最小片段不可运行＝删除或改标「仅示意」。
- 回归风险：R9 的 `Register` 末位参数会改变既有示例签名——所有片段须用 `src/` 现状签名。
- 验收命令：`dotnet build OrcEngine.sln`（允许既有 2 个 CS8604）；片段逐条对照 `reference/_anchors.md`
- Acceptance:
  - [ ] 每机制有 API 表（名称/签名/层归属/失败语义）
  - [ ] 02 章四面齐全且与全局约定 4 一致
  - [ ] 每机制最小片段取自 S18 示例或可独立运行
- Rationale：R4 验收点按章对应；四段式保证可单独阅读。
- Terminology: [触发器](../../CONTEXT.md)、[判定器](../../CONTEXT.md)、[包装器](../../CONTEXT.md)、[动作缝](../../CONTEXT.md)、[选择器](../../.ams/context/targeter/CONTEXT.md)
- 实测：四章五件套齐全；02 含 kind/band/hooks/保留槽、`SortKey = band×1000 + priority`（同分按注册序、保留槽恒最后）、注册者三规则各有可运行例子、csx `entry` 与签名、异常隔离表、wrapper 位置（指向 03）；03 含判定器两层管道五步次序、wrapper 三类、严格签名校验、`w2→w1→core→w1→w2` 代码与预期日志、`wrappers[]` 预制体声明；04 含 11 个选择器模板、`ReferenceSetParameter` 全参、`Step/Retry`、三态、候选后端契约、handler 内完整选靶片段；05 动作表 **26 行**与 `reference/api速查.md` §1 同源（含 `source` 尾参/无尾参标记）、修饰 vs 光环、`EngineVariables`、失败语义五类。
- 偏差：①S13 要求「每机制最小片段取自 S18 示例或可独立运行」——S18 未建设（Part B 后续），本章片段为**新写并按 `_anchors.md` 逐符号核对**的等价片段，未经示例证据复用；②文档「02 章增设注册者与来源小节写死四面」已落（§4：`Register(registrant:)`／审计面／两面分工／再发信号）；③`03` 的强类型 replace 示例使用自定义 `Judicator<TDelegate>`（内置费用判定器是 `ValidationJudicator`，不实现强类型适配面），已在文中说明。

- **R10/R11 追加（待回改）**：02 章增「伤害三层」小节（三端→处理端、`DamageEventView`/`DamageApplyView`、改写只改 `Amount`、唯一扣数值在 `damage.apply`），并显式区分**两种 `Source`**（流程视图＝施动方；信号视图＝事件引发者）；05 章动作表增 `BattleAsync` 行、把 `DamageAsync` 的链路改为 `effect.damage → damage.apply`，并替换「两向 `DamageAsync`」的旧写法。

### S14: 06 卡牌开发方法论
- Status: Done
- 改动文件：`06-卡牌开发方法论.md`。
- 关键签名/结构：五步流程（文本归类 → 选承载 → 选触发点 → 选动作面 → 自检）；映射表四列（句式 / 承载 / 触发点 / 动作面）≥10 行；反例 ≥3；自检清单 ≥10 项（含「归因读 `Source` 还是 `TriggerCard`」）。
- Approach：每个反例给「错写法 → 症状 → 排查树分支编号」，与 01 章互链。
- 失败语义：反例无法在排查树定位＝补分支或删反例（不许悬空）。
- 回归风险：映射表若引用未实现机制＝违反 R8；每行须在 `_anchors.md` 有锚点。
- 验收命令：`grep -c "^| " docs/卡牌DIY开发文档/06-卡牌开发方法论.md`（映射表 ≥11 行含表头）；人工核对反例互链
- Acceptance:
  - [ ] 映射表 ≥10 类句式且四列齐全
  - [ ] 反例均能在 01 章排查树找到分支
  - [ ] 自检清单 ≥10 项可勾选
- Rationale：R5 要求把一句话卡面转成路径。
- Terminology: [词条程序](./CONTEXT.md)、[预制效果](./CONTEXT.md)
- 实测：映射表 **15 类句式**（4 列齐全：句式/承载/触发点/动作面＋路径），高于文档 ≥10；反例 **5 个**（错写法→症状→后果→正解→排查树分支），高于 ≥3；自检清单 **14 项**（含「归因读 `Source` 还是 `TriggerCard`」），高于 ≥10；§6 片段为完整 `*.prefab.json`（`card.played`→`DrawAsync`），JSON 语法与 `PrefabJson.Deserialize` 结构校验通过。
- 偏差：文档写「映射表 ≥10 行」而本次要求 ≥12；按 15 类交付。片段为方法论示例，未落成 S18 示例文件（S18 未建设）。

### S15: 07 词条与预制效果
- Status: Done
- 改动文件：`07-词条与预制效果.md`。
- 关键签名/结构：词条全清单表；`KeywordProgram` 四张表（规则点 11 / 事件 17 / 操作 33 / 目标 14）；预制效果三层结构图 + `injects`/`wrappers` 字段表 + 最小 `*.prefab.json` 骨架；「模板效果 / 效果 DSL / 效果预制体」三层对照。
- Approach：计数以常量数组为准（`KeywordIds.All`、`KeywordProgram` 常量）；补「词条信号效果走 `engine.Subscribe`，只能靠载荷 `Source` 归因，拿不到 `TriggerCard`」。
- 失败语义：计数不符＝整表重取；最小骨架不可反序列化＝换骨架。
- 回归风险：`*.prefab.json` 的 `schemaVersion` 变更会让骨架过期（以 `PrefabJson` 现状为准）。
- 验收命令：`dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --filter "FullyQualifiedName~Prefab|FullyQualifiedName~KeywordProgram"`
- Acceptance:
  - [ ] 11/17/33/14 与常量数组逐项一致
  - [ ] 最小骨架可被 `CardDistributionValidator` 或 `PrefabJson.Deserialize` 通过
  - [ ] 三层关系与适用者齐备
- Rationale：R7 验收是计数式断言。
- Terminology: [词条](../../CONTEXT.md)、[词条程序](./CONTEXT.md)、[预制效果](./CONTEXT.md)
- 实测：词条表 **19/19** 条全覆盖（标识/类型/参值/行为/读取口/打标；打标 7 条＝闪击/奋战/烟幕/伏击/重甲/守护/冲击，与 `BuiltInPlugin.cs:188-210` 的 `isBattleKeyword: true` 逐条一致）；词条程序四表 **11/17/32/14** 逐条列出；数值表达式白名单 6 类（字面量、`value/entries/attacks/linked.alive`、`target.*`、`event.*`、`state.*`、`data.*`）与 `KeywordProgram.Number` 逐项一致；预制体三层＋`injects`/`wrappers[]` 字段表＋最小 JSON；3 段 JSON 实测：`PrefabJson.Deserialize` OK（`diy.minimal`）、`KeywordProgram.Parse`（draw handler）OK、`KeywordProgram.Parse`（targeting.forbid rule）OK；「模板效果/DSL/预制体」三层对照与「词条程序 vs csx」同行为双写法（部署时抽 1 张）齐备。
- 偏差：文档记「操作 33 条」，`KeywordProgram.Operations` 实为 **32**（与 S10 口径一致）；词条程序需**代码侧** `ProgramKeywordComponent.Register` 注册标识，故 `targeting.forbid` 等自定义程序标为「混合」而非纯数据侧。

- **R10/R11 追加（待回改）**：07 章增「伤害三层与词条挂载点」表（伏击→`unit.attack`；重甲→`combat.damage`；免疫→`damage.apply`，HQ 侧经门户内改写段），并修词条表「行为」列（重甲＝只减对战、免疫＝全伤害归零）。

### S16: 08 能力边界
- Status: Done
- 改动文件：`08-能力边界.md`。
- 关键签名/结构：四级归档表（内核缺 / 游戏层未接线 / 规则缺失 / 部分可覆盖）；每条五列（现象 / 层级 / 现状替代 / 需改哪里 / 关联示例）。
- Approach：首批列「数据侧无法注入指定 band」「`OrderCard` 打出链未接反制窗口」「`EffectRuntime` 无战斗/撤退入口」「targeter 候选来源限制」；**移除**「弃置无归因」（标 R9 已解决 + 读法）；新增「`engine.Subscribe` 路径无 `TriggerCard`」。
- 失败语义：只写「不支持」而无替代＝该条不合格；层级标两个＝互斥性破坏。
- 回归风险：R9 解决的缺口若仍留在表里＝自相矛盾（S20 检查关键词「无归因」）。
- 验收命令：`grep -n "无归因\|TriggerCard" docs/卡牌DIY开发文档/08-能力边界.md`
- Acceptance:
  - [ ] 每条四级标注齐全且互斥
  - [ ] 每条给替代或明确「需改引擎」
  - [ ] 「弃置归因」标 R9 已解决
- Rationale：R8 要求不能只写不支持；边界章是示例可行性的合法性来源。
- Terminology: [能力边界](./CONTEXT.md)
- 实测：缺口 **11 条**，分级 **内核缺 2（G-06/G-07）｜游戏层未接线 6（G-01/G-02/G-03/G-04/G-08/G-10）｜规则缺失 1（G-09）｜部分可覆盖 2（G-05/G-11）**，每条五列（现象/层级/替代写法/需改哪里/编号）齐全；五条必列项全部在表内：①`OrderCard` 不进 `OrderFlow`（G-01）②`EffectRuntime` 无战斗/撤退（G-02，代之以 `AttackFlow.ExecuteAsync` 两向＋`MoveByEffectAsync`＋词条 `attack/retreat`）③`injects` 仅默认区段（G-03）④`Source` 未透传（G-04，**逐条列出 28 条恒 null 信号**＋6 条非空对照）⑤targeter 候选来源（G-05）；「弃置无归因」按 R9 移入 §4「已解决」，**不列入缺口表**。
- 偏差：文档 S16 要求「移除『弃置无归因』并标 R9 已解决」——已按「独立小节（非缺口）」处理，保留读法与证据用例名；G-04 的 28 条为当前代码快照，后续接线后需同步递减。
- 修正：S16 实测中「G-02 代之以 `AttackFlow.ExecuteAsync` 两向」的表述**不成立**——S19 实测发现 `AttackFlow.ExecuteAsync` 的默认「伤害生效」处理器扣的是内核 `HealthData`，游戏层单位没有该组件（事件被隔离、零伤害）。08 章 G-02 与 `reference/缺口表.md` 已改为「两向 `EffectRuntime.DamageAsync`（先快照攻击力）」，并显式标注 `AttackFlow` 不可替代。

- **R10/R11 追加（待回改）**：关闭 G-02（`BattleAsync` 上线，删除「两向 `DamageAsync`」替代写法，改写为「已解决」并保留与指挥对战的差异说明）；新增「R11 行为变更」条目（免疫现覆盖 `effect`/`battle` 伤害，重甲仍只减 `combat`），层级标「规则变更（已确认）」；G-04 需按 R9 现状复核是否仍成立。

### S17: 数据侧示例卡包
- Status: Done
- 改动文件：新增 `samples/card-pack-diy/README.md`、`cards/diy.*.card.json`、`effects/diy.*.prefab.json`。
- 关键签名/结构：卡文件命名 `diy.<slug>.card.json`；README 表列＝`卡名 | 卡面文本 | 机制 | 证据命令 | 证据等级`；与 `examples/0X-*.md` 双向链接。
- Approach：形态对齐 `samples/card-pack-demo`（`cards/` + `effects/` + README）；R9 相关卡（示例 5）待 S4/S5 落地后取真实键名；预检走 `CardDistributionValidator`（card 目录 + effects 目录）。
- 失败语义：预检 error ≠ 0 ＝卡包不合格；数据侧不可表达的示例不得硬塞（标「仅代码侧」）。
- 回归风险：引擎字段改名会让卡包预检失败（S20 复跑兜底）。
- 验收命令：`dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --filter "FullyQualifiedName~CardDistributionValidator"`
- Acceptance:
  - [ ] `CardDistributionValidator` 报 0 error
  - [ ] 每张数据侧示例卡在 `examples/` 有同名文件引用其实体路径
- Rationale：R6 要求数据侧示例落成真实卡包。
- Terminology: [数据侧](./CONTEXT.md)
- 实测：`dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --filter "FullyQualifiedName~DiyCardPackTests"` → `已通过! - 失败: 0，通过: 1`；预检面 `SuccessfulCardCount=4 / SuccessfulLibrarySnapshotCount=4 / SuccessfulInlineSnapshotCount=0 / UnresolvedReferenceCount=0 / ErrorCount=0 / WarningCount=0 / HasNoErrors=true`。卡包＝`diy.fire-support` / `diy.volley` / `diy.memorial` / `diy.field-rescue`（各 1 卡 + 1 预制体，`schemaVersion` 3，csx `entry: "HandleAsync"`）。
- 偏差：①预制体文件名采用 `diy.<slug>.effect.prefab.json`（与 `samples/card-pack-demo` 的 `<id>.prefab.json` 不同，`.effect` 段让人一眼看出是效果预制体；加载器不依赖文件名）；②预检入口用 xunit 用例 `DiyCardPackTests` 包一层，而不是新写命令行工具——避免新增工程文件（Constraints）。

### S18: 八个示例成篇
- Status: Done
- 改动文件：`examples/README.md`、`examples/01..08-*.md`。
- 关键签名/结构：每例六段（卡面文本 / 机制选型 / 完整数据或代码 / 触发时序 / 边界与坑 / 证据：命令 + 预期输出 + 等级）；示例 5 增「两面分工」小节。
- Approach：示例 5 必须写清 `view.Source` 判敌方、`view.TriggerCard` 判自身、再发信号传 `source:`；示例 8 写清「战斗用 `AttackFlow` 的伤害面逼近」及与真实战斗链的差异。
- 失败语义：证据命令跑不出预期输出＝该例不合格；「数据侧可用」但无卡包文件＝降级为「仅代码侧」。
- 回归风险：示例代码随 `src/` 演进失效（每例证据命令在 S20 复跑）。
- 验收命令：逐例执行文件内写的 `dotnet test --filter`；`ls examples/0*.md | wc -l`（应为 8）
- Acceptance:
  - [ ] 8 个示例与 R6a–R6h 逐项对应
  - [ ] 每例标注路径可用性与证据等级
  - [ ] 每例给可复跑命令与预期输出
- Rationale：R2 要求示例独立成篇；R6 要求逐例证据。
- Terminology: [数据侧](./CONTEXT.md)、[代码侧](./CONTEXT.md)
- 实测：`ls docs/卡牌DIY开发文档/examples/0*.md | wc -l` → `8`；`examples/README.md` 已建（8 行索引 + 证据等级定义 + 边界表）。八例六段齐全，均达 L2；01/02/03/05 另达 L1（进卡包）。
- 偏差：①示例 8 原计划「用 `AttackFlow` 的伤害面逼近」——实测 `AttackFlow.ExecuteAsync` 的默认伤害处理器扣内核 `HealthData`，游戏层单位无该组件（信号被隔离、零伤害），改为**两向 `DamageAsync`**（先快照攻击力）；同步修正 05 章 §8、08 章 G-02 与 `reference/缺口表.md`；②示例 3 按 D2 口径监听 `card.died` 而非 `card.destroyed`；③示例 7 给出两条路径（`TargetingDeclaration` / 词条程序 `targeting.forbid`），标注「仅代码侧可用 / 混合」。

- **R10/R11 追加（待回改）**：示例 8 的「强制战斗」改用 `runtime.BattleAsync(unit, enemy, source: host, ct)`，删除两向 `DamageAsync` 写法；「边界与坑」补三条差异（无射程/守护/烟幕/拦截/伏击/反击资格；不付行动代价；双方快照攻击力、先死者仍出伤）。

### S19: 代码侧示例与证据用例
- Status: Done
- 改动文件：`tests/Orc.Game.Tests/DiyExamples*.cs`（一例一类）、复用 `CommandTestKit`/`MockTargeterBridge`/`PlayChainTestKit`。
- 关键签名/结构：类名 `DiyExample01_DamageAndArmorTests` … `DiyExample08_AssaultAdvanceRetreatTests`；用例名中文；自定义 `Effect`/`Judicator`/wrapper 的最小实现放同类文件内。
- Approach：断言世界状态而非日志；示例 5 双向（敌方弃置触发 / 己方弃置不触发）；示例 8 三分支（推进 / 撤退 / 阵亡）。
- 失败语义：依赖日志文本或未固定随机＝用例不合格；改工程文件＝违反 Constraints。
- 回归风险：新增用例与既有夹具命名冲突；共享 `TestHost` 状态需每例独立构造。
- 验收命令：`dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --filter "FullyQualifiedName~DiyExample0"`
- Acceptance:
  - [ ] `--filter` 可按示例粒度单独复跑
  - [ ] 新增用例在既有工程内编译通过（不改工程文件）
  - [ ] 随机取样固定种子
- Rationale：一例一类便于文档逐例给过滤命令。
- Terminology: [代码侧](./CONTEXT.md)
- 实测：`dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --filter "FullyQualifiedName~Diy"` → `已通过! - 失败: 0，通过: 15，已跳过: 0，总计: 15`（8 例 14 条 + 卡包预检 1 条）；单例过滤 `~DiyExample0N` 均可单独复跑。
- 偏差：①新增 `DiyExampleKit.cs` 共享夹具（定位卡包 / 建局 / 准备指令 / 找空槽），未改任何工程文件；②示例 7 的 `ProgramKeywordComponent.Register` 必须早于 `Match.Initialize`（对局在初始化时冻结词条快照，事后注册 fail-fast），且 viewer 必须是 `OrderCard` 才能命中 `sourceKind: "command"`；③示例 8 的推进分支受 `MoveAsync` 移动资格门禁约束，用例用 `CommandTestKit.Activate(unit)` 置「本回合可移动」；④示例 4 的自定义 `AuraDeclaration.transform` 只有 `(受益卡, 当前值)`，需在 `OnMount` 捕获 `GameEnvironment` 再闭包计数；⑤「随机取样固定种子」一项未引入种子开关，改为**构造上消除随机性**（示例 2 只断言「总伤害恒为 3」；示例 8 每局只放 1 个敌方候选），断言不依赖具体命中对象。

- **R10/R11 追加（待回改）**：`DiyExample08_AssaultAdvanceRetreatTests` 的三分支改由 `BattleAsync` 驱动（推进/撤退/阵亡），断言意图不变；补 `Battle_Damage_Is_Zeroed_By_Immune` 与 `Armor_Does_Not_Reduce_Battle_Damage` 两条证据，使示例 8 的「免疫生效、重甲不生效」可复跑。

### S20: 全库校验与索引收口
- Status: Done
- 改动文件：`README.md`（索引表、章节状态表）、各章交叉链接、`_template/登记规则.md`。
- 关键签名/结构：四项检查＝链接完整性 / 元信息块五件套 / 参考层与正文互链 / 示例↔证据双向可达；README 状态表四态。
- Approach：链接检查用一次性 bash（相对链接逐条 `test -f`）；再跑 `dotnet build OrcEngine.sln`（允许既有 2 个 CS8604） 与全量 `dotnet test`，结果写进状态表。
- 失败语义：任一死链＝收口失败；示例证据不可复跑＝对应示例降级并改状态表。
- 回归风险：文档增长后索引漏登记（登记规则要求新增即登记）。
- 验收命令：
  ```bash
  dotnet build OrcEngine.sln && dotnet test OrcEngine.sln
  ```
- Acceptance:
  - [ ] 无死链
  - [ ] 九章 + 8 示例 + 3 表 + 2 模板全部登记
  - [ ] 全部示例证据命令可复跑并输出预期结果
- Rationale：R2/R6 的验收都依赖索引与可复跑。
- Terminology: [文档库](./CONTEXT.md)
- 实测：`dotnet build OrcEngine.sln` → `已成功生成。0 个错误，2 个警告`（均为既有 CS8604：`BuiltInPlugin.cs:143/149`）；`dotnet test OrcEngine.sln` → **1650 通过 / 0 失败**（Orc.Tests 344｜Orc.Script.Tests 17｜Orc.Mcp.Tests 47｜Orc.Ai.Tests 13｜Orc.Packages.Tests 72｜Orc.Game.Tests 1157）。链接自检：`docs/卡牌DIY开发文档/**.md` 相对链接 **194 条 / 断链 0**；`samples/card-pack-diy/README.md` 断链 0。索引收口：README §5.1 九章→「完成」、§5.4 八例+卡包+证据用例→「证据已跑」、§6 状态表逐行更新。
- 偏差：①`reference/缺口表.md` 原编号（8 条，G-01=injects…）与 08 章（11 条，G-01=`OrderFlow`）不一致，本次按 08 章口径**重排为 G-01…G-11**，「弃置无归因」移入「已解决」小节（不占缺口编号），`信号表.md` 的 G-06 引用同步改为 G-04；②文档未提供「CS8604 仅 2 个」之外的构建基线，按现状记录。

### Part C — R10 战斗机制与 R11 伤害触发-处理分离

> 现状锚点（2026-10-19 取证）：`AttackDamageTrigger`（`src/Orc.Game/Commanding/CommandManager.cs:216`，对局级、`CommandManager` 持有、默认互伤 handler 优先级 100）；`AttackDamageResolution`（`src/Orc.Game/Commanding/CommandTypes.cs:260`，伏击 `IsRewritten` ＋ 免疫归零/重甲减伤两个容器）；`KeywordDamageRewriteEffect` 三子类（`src/Orc.Game/Cards/KeywordEffects.cs:392` 伏击 / `:484` 重甲 / `:540` 免疫）；默认互伤 `HandleDefaultAttackDamageAsync`（`CommandManager.cs:1240`）与 `ResolveIncomingDamage`（`:1344`）；门户 `UnitCard.ApplyDefenseDamageAsync`（`src/Orc.Game/Cards/UnitCard.cs:582`）、`Hq.ApplyDamageAsync`（`src/Orc.Game/Players/Hq.cs:156`）；`EffectRuntime.DamageAsync`（`src/Orc.Game/Effects/EffectRuntime.cs:187`）现直调门户；`KeywordLoadContext`（`src/Orc.Game/Cards/KeywordSystem.cs:152-190`）。

### S21: 伤害事件模型（视图 / Kind / 结果位）
- Status: Done
- 改动文件：新增 `src/Orc.Game/Damage/DamageKinds.cs`、`src/Orc.Game/Damage/DamageViews.cs`。
- 关键签名：
  ```csharp
  public static class DamageKinds
  {
      public const string Combat = "combat";   // 真实指挥对战
      public const string Effect = "effect";   // 指令 / EffectRuntime.DamageAsync
      public const string Battle = "battle";   // EffectRuntime.BattleAsync
  }

  [ContextView]
  public class DamageEventView          // 三触发端共用
  {
      [Read]   public virtual Card Source { get; set; }   // 施动方（伤害来源）
      [Read]   public virtual Card Target { get; set; }
      [Mutate] public virtual int Amount { get; set; }    // 触发端唯一可改写项
      [Read]   public virtual string Kind { get; set; }   // combat / effect / battle
  }

  [ContextView]
  public class DamageApplyView          // 处理端
  {
      [Read]   public virtual Card Source { get; set; }
      [Read]   public virtual Card Target { get; set; }
      [Mutate] public virtual int Amount { get; set; }
      [Read]   public virtual string Kind { get; set; }
      [Mutate] public virtual bool IsZeroed { get; set; } // 结果位（免疫等置位）
  }
  ```
- Approach：两视图均为独立 `[ContextView]`（不复用 `AttackDamageTriggerView`——后者带 `Resolution`，属攻击流程专属）。`Source` 是**流程视图**的「施动方」，与 R9 信号视图的 `Source`（事件引发者）同名不同义；沿用 `AttackFlowView.Source` 先例（`src/Orc/Cards/BaseFlows.cs:44`），文档 02/05 章须显式区分两种 `Source`。
- 失败语义：`Source`/`Target` 为 `null` → 管道入口 `ArgumentNullException`；`Amount < 0` → `ArgumentOutOfRangeException`（不静默钳制）；`Kind` 非三值之一 → `ArgumentException`（防拼写漂移）。
- 回归风险：新增视图类型会进入 `ViewMeta` 缓存（按类型隔离，无污染）；`[Mutate]` 属性必须 `virtual`，否则 `ViewProxyFactory` 抛声明非法。
- 验收命令：`dotnet build src/Orc.Game/Orc.Game.csproj`；`dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --filter "FullyQualifiedName~DamagePipeline"`
- Acceptance:
  - [ ] 两个视图均可被 `ContextViewBinder` 创建，`Amount` 可被 handler 改写并回读
  - [ ] `Kind` 在三端载荷中只能取 `combat`/`effect`/`battle`
  - [ ] `IsZeroed` 为处理端独有结果位，触发端不可见
- Rationale：视图按层分离，才能把「只改 Amount」与「唯一扣数值」落成类型可校验的边界。
- Terminology: [伤害触发端](./CONTEXT.md)、[伤害处理端](./CONTEXT.md)

- 实测：`dotnet build src/Orc.Game/Orc.Game.csproj` → `已成功生成。0 个错误`；`dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --filter "FullyQualifiedName~DamagePipeline"` → `已通过! - 失败: 0，通过: 6`（含三端改写回读、门户白名单扫描）。`DamageEventView.Amount` 的 `[Mutate]` 改写被处理端读到（1 而非 3）。
- 偏差：①`Source` 改为 `[Optional][Read] Card?`——`EffectRuntime.DamageAsync(target, amount)`（无来源）是既有合法调用，若强制必填会破坏「无归属伤害」；②新增 `DamageSettlement` 结果接收器（两视图 `[Optional][Read]` 携带），否则管道无法在不暴露 `Context.Data` 的前提下判定「处理端是否已执行」（S22 的 `bool` 契约需要）；③`Target`/`Amount`/`Kind` 保持必填。

### S22: DamagePipeline（三端 + 处理端，对局级）
- Status: Done
- 改动文件：新增 `src/Orc.Game/Damage/DamagePipeline.cs`；`src/Orc.Game/Commanding/CommandManager.cs`（ctor 构造 + 默认 handler 注册 + 属性）；`src/Orc.Game/Cards/KeywordSystem.cs`（`KeywordLoadContext` 增 `DamagePipeline`）。
- 关键签名：
  ```csharp
  public enum DamageEventBands { Resolve = 1 }              // 触发端：改写在前、默认转发最后
  public enum DamageApplyBands { Rewrite = 1, Apply = 2 }   // 处理端：改写在前、扣数值最后

  public sealed class DamagePipeline
  {
      public Trigger<DamageEventView> CombatDamage { get; }   // combat.damage
      public Trigger<DamageEventView> EffectDamage { get; }   // effect.damage
      public Trigger<DamageEventView> BattleDamage { get; }   // battle.damage
      public Trigger<DamageApplyView> Apply { get; }          // damage.apply（唯一扣数值）

      public Task<bool> CombatAsync(Card source, Card target, int amount, CancellationToken ct = default);
      public Task<bool> EffectAsync(Card source, Card target, int amount, CancellationToken ct = default);
      public Task<bool> BattleAsync(Card source, Card target, int amount, CancellationToken ct = default);
  }

  // CommandManager
  public DamagePipeline DamagePipeline { get; }
  internal Task<int> ApplyDamageCoreAsync(Card source, Card target, int amount, string kind, CancellationToken ct);
  ```
- Approach：`DamagePipeline` 的 owner ＝**对局级 `CommandManager`**（在 `CommandManager` ctor 内 `new DamagePipeline(engine)`，与 `AttackDamageTrigger` 同层；`Match` 无需新增装配点，独立构造的 `CommandManager` 自带一份，5 处测试构造零改动）。默认 handler 由 `CommandManager` 在 ctor 内注册：三端各一条「转发处理端」（`DamageEventBands.Resolve`，priority 100，读改写后的 `view.Amount`），处理端一条「扣数值」（`DamageApplyBands.Apply`，priority 100，读 `IsZeroed`/`Amount` → `ApplyDamageCoreAsync`）。`KeywordLoadContext` 增 `DamagePipeline?`（装载期取用，与 `AttackDamageTrigger` 同路）。
- 失败语义：`DamagePipeline` 不可达（脱离对局的 `EffectRuntime`）→ 动作返回 `false`、零副作用；`ApplyDamageCoreAsync` 对已死亡单位 / 已终局 HQ → 返回 0、不抛（战斗同时结算需要）；非单位非 HQ → `false`。
- 回归风险：①`CommandManager` 新增 4 个 active 触发器会进入 `Orchestration` 采样与事件流（审查链快照数量变化的用例需同步）；②默认 handler 的 band/priority 若与改写 handler 冲突，会出现「先扣后改」——以 band 值（Resolve/Apply 均为最大）＋priority 100 双重保证；③`KeywordLoadContext` 构造签名变更（唯一调用点 `CommandManager.cs:226`）。
- 验收命令：`dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --filter "FullyQualifiedName~DamagePipeline"`
- Acceptance:
  - [ ] 三端各自可被 `inject`/wrapper 改写 `Amount`，且改写值被处理端读到
  - [ ] 处理端默认 handler 恒在全部改写 handler 之后执行
  - [ ] `DamagePipeline` 不可达时动作返回 `false` 且无任何信号
- Rationale：三层共用一个容器，才能保证「默认转发/默认扣数值」永远最后；放在 `CommandManager` 避免 `Match` 装配顺序与 `EffectRuntime` 循环依赖。
- Terminology: [伤害触发端](./CONTEXT.md)、[伤害处理端](./CONTEXT.md)

- 实测：`dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --filter "FullyQualifiedName~DamagePipeline"` → `已通过! - 失败: 0，通过: 6`；其中 `Pipeline_Combat_Effect_Battle_All_Reach_Apply` 断言三端改写值（1）全部被 `damage.apply` 读到，`Pipeline_Apply_Is_Only_Portal_Caller` 断言 `src/` 调用点唯一。
- 偏差：①处理端**实现**落在 `DamagePipeline.ApplyCoreAsync`（而非 `CommandManager`）——这样「唯一门户调用点」与「旁路为零」的 grep 白名单才成立；`CommandManager.ApplyDamageCoreAsync` 保留 S22 签名，仅作转接；②`DamagePipeline` 构造参数为「engine ＋ 伤害来源游标作用域函数」（`CommandManager.WithDamageSourceAsync`），避免把 Killer 归属逻辑复制到管道；③`KeywordLoadContext` 新增**可选**参数 `damagePipeline`（不破坏独立构造路径）。

### S23: 门户收口与「旁路为零」证明
- Status: Done
- 改动文件：`src/Orc.Game/Cards/UnitCard.cs`（`:582`）、`src/Orc.Game/Players/Hq.cs`（`:156`）、新增 `src/Orc.Game/Properties/AssemblyInfo.cs`、`src/Orc.Game/GameEntryPoints.cs`（条目描述改注）。
- 关键签名：
  ```csharp
  // UnitCard / Hq：public → internal（同程序集 + 测试友元可调）
  internal async Task ApplyDefenseDamageAsync(int amount, CancellationToken ct = default);
  internal async Task ApplyDamageAsync(int amount, CancellationToken ct = default);

  // 新增（不改编csproj）：让测试程序集继续编译 85 处既有门户调用
  [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Orc.Game.Tests")]
  ```
- Approach：可见性收口＝`internal`（编译期即挡住 UI/MCP/插件等外部程序集）；测试侧 85 处调用（`grep -rn "ApplyDefenseDamageAsync\|ApplyDamageAsync" tests`）由 `AssemblyInfo.cs` 的友元声明保留，**不改工程文件**。`Orc.Plugins.BuiltIn` 已是既有友元，需 grep 确认零调用（当前 0）。
- 失败语义：外部程序集调用 → 编译错误（CS0122）；友元程序集仍可旁路（测试专用，文档标注）；`internal` 不改变运行时行为与异常语义。
- 回归风险：①`Orc.UI`/`Orc.Mcp`/`Orc.Ai`/`Orc.Packages` 若引用了门户会编译失败（当前 grep 为 0，需在改动后复跑）；②`GameEntryPoints` 是文档元数据，条目仍列 `ApplyDefenseDamageAsync`，需标注「受控面：仅 `DamagePipeline` 可调」。
- 验收命令：
  ```bash
  grep -rn "\.ApplyDefenseDamageAsync(\|\.ApplyDamageAsync(" src --include=*.cs | grep -v "/obj/" | grep -v "/bin/"
  ```
  预期只剩：`src/Orc.Game/Damage/DamagePipeline.cs`（唯一调用点）+ `UnitCard.cs`/`Hq.cs`（定义与 XML 注释）+ `EffectRuntime` 的已删调用（应为 0）。
- Acceptance:
  - [ ] 两门户声明为 `internal`；非友元程序集调用＝编译错误
  - [ ] `src/` 内门户调用点仅 `DamagePipeline.cs` 一处（grep 白名单写入 `reference/_anchors.md`）
  - [ ] 全量构建通过（既有 2 个 CS8604 允许）
- Rationale：`internal` 是唯一能在编译期证明「外部无法旁路」的手段；友元声明放 `.cs` 而非 csproj，遵守「不改工程文件」约束。
- Terminology: [伤害处理端](./CONTEXT.md)

- 实测：`grep -rn "\.ApplyDefenseDamageAsync(\|\.ApplyDamageAsync(" src --include=*.cs`（排除 obj/bin）→ **3 行，全在 `src/Orc.Game/Damage/DamagePipeline.cs`**（159 / 164 / 180）。全量构建 `0 个错误` 证明 `Orc.UI` / `Orc.Mcp` / `Orc.Ai` / `samples` 无越权调用（编译期 CS0122 未出现）。白名单已写入 `reference/_anchors.md` §「门户调用白名单」。
- 偏差：降 `internal` ＋ 新增 `InternalsVisibleTo("Orc.Game.Tests")` 后，测试程序集里覆写 `protected internal` 成员（`Modifier.OnMount/OnUnmount/Apply`、`KeywordComponent.OnGrant/OnRevoke`）须同步写成 `protected internal`（否则 CS0507）：共 15 处，仅改访问修饰符、断言零改动。

### S24: 既有伤害路径迁移（攻击流程拆解）
- Status: Done
- 改动文件：`src/Orc.Game/Commanding/CommandManager.cs`（`HandleDefaultAttackDamageAsync:1240`、`HandleUnitAttackAsync` HQ 分支 `:1197-1210`、`DealDamageAsync:1460`、`DealDamageToHqAsync`、删除 `ResolveIncomingDamage:1344`）、`src/Orc.Game/Commanding/CommandTypes.cs`（`AttackDamageResolution` 精简）、`src/Orc.Game/Cards/KeywordEffects.cs`、`src/Orc.Game/Effects/EffectRuntime.cs`。
- 迁移映射（逐条）：
  | 现机制 | 现挂点 | 新挂点 |
  | --- | --- | --- |
  | 伏击改写 | `AttackDamageTrigger`（`AmbushRewriteEffect`） | **不变**——属攻击流程，不属伤害三层 |
  | 重甲减伤 | `AttackDamageTrigger` → `AttackDamageResolution` | `DamagePipeline.CombatDamage`（只减 `combat`） |
  | 单位免疫 | `AttackDamageTrigger` → `AttackDamageResolution` | `DamagePipeline.Apply`（`IsZeroed = true; Amount = 0`） |
  | HQ 免疫 | `HqDamageRewriter` | **保留**（`Hq.ApplyDamageAsync` 改写段＝处理端内部，语义等价，避免 HQ 装载期无 pipeline） |
  | 效果伤害 | `EffectRuntime.DamageAsync` 直调门户 | `DamagePipeline.EffectAsync` |
  | HQ 对战伤害 | `HandleUnitAttackAsync` 直调 `hq.ApplyDamageAsync` | `DamagePipeline.CombatAsync` |
  | 单位对战互伤 | `HandleDefaultAttackDamageAsync` 直调门户 | 两次 `DamagePipeline.CombatAsync`（攻击向/反击向） |
- 关键签名：
  ```csharp
  // 处理端唯一扣数值实现（原 DealDamageAsync / DealDamageToHqAsync 收敛于此）
  internal async Task<int> ApplyDamageCoreAsync(Card source, Card target, int amount, string kind, CancellationToken ct)
  {
      // source 为 UnitCard ⇒ WithDamageSourceAsync（Killer 归属）→ 门户 → 实际扣减 > 0 才发 unit.damage.dealt
  }

  // AttackDamageResolution：只留伏击
  public sealed class AttackDamageResolution { public bool IsRewritten { get; } public void MarkRewritten(); }
  ```
- Approach：`AttackDamageTrigger` 只保留「伏击判定 → 设定 `IsRewritten`」与「默认互伤」两条；默认互伤不再读 `Resolution` 的归零/减伤容器，改为两次 `CombatAsync`（反击资格仍由 `_combatCounterEligibility` 决定，不属于伤害三层）。死亡判定与 `unit.combat.survived` 留在攻击流程（不受伤害分层影响）。`unit.damage.dealt` 统一由 `ApplyDamageCoreAsync` 发射（从攻击路径移除重复发射）。
- 失败语义：`ApplyDamageCoreAsync` 对已死目标返回 0（战斗同时结算）；攻击路径的「已死＝跳过」防御保留；伏击命中时仍走 `ProcessDeathAsync`、不经任何伤害端（零伤害端调用）。
- 回归风险：①`AttackDamageResolution` 的四个公开方法删除＝破坏性（仓内调用：`ArmorReductionEffect`、`ImmuneZeroingEffect`、`ResolveIncomingDamage`）；②`unit.damage.dealt` 发射点上移可能改变同帧观察序（发射时机仍在每次扣减之后）；③HQ 对战伤害现在也走 `combat.damage`，重甲对 HQ 无效（重甲本就不作用于 HQ——语义不变）。
- 验收命令：`dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --filter "FullyQualifiedName~CommandCombat|FullyQualifiedName~KeywordEffect|FullyQualifiedName~RandomBattle"`
- Acceptance:
  - [ ] 三端调用关系：`unit.attack` → `AttackDamageTrigger`（伏击）→ `CombatAsync`×2 → `Apply` → 门户
  - [ ] 伏击命中＝零伤害端调用、攻击者死亡、被攻击者不受伤
  - [ ] `unit.damage.dealt` 每方向恰一次（不再双发）
  - [ ] `AttackDamageResolution` 只剩 `IsRewritten`
- Rationale：把「按来源区分的改写」从攻击记录里剥出来挂到端点，攻击流程只保留伏击与反击资格，符合 R11 的职责边界。
- Terminology: [伤害触发端](./CONTEXT.md)、[指挥对战](./CONTEXT.md)

- 实测：`dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --filter "FullyQualifiedName~CommandCombat|FullyQualifiedName~KeywordEffect|FullyQualifiedName~RandomBattle"` → 全通过；全量游戏层 `1168 通过 / 0 失败`。
- 偏差：①`AttackDamageResolution` 精简为只剩 `IsRewritten`（`MarkDamageZeroed`/`AddDamageReduction`/`GetDamageReduction`/`IsDamageZeroed` 删除）；②`EmitDamageDealtAsync` 删除——`unit.damage.dealt` 上移到处理端（每方向恰一次）；③HQ 简路改走 `DamagePipeline.CombatAsync`，HQ 免疫仍由 `HqDamageRewriter` 在门户改写段承接（装载期无管道依赖）；④伏击命中仍不经任何伤害端（零端调用）。

### S25: `BattleAsync`（R10）
- Status: Done
- 改动文件：`src/Orc.Game/Effects/EffectRuntime.cs`（新增方法）；`src/Orc.Game/Damage/DamagePipeline.cs`（复用 `BattleAsync` 端）。
- 关键签名：
  ```csharp
  public async Task<bool> BattleAsync(Card first, Card second, Card? source = null, CancellationToken ct = default)
  ```
- Approach：双方均须 `UnitCard`、`UnitStateData.Position is not null`、`!IsDestroyed`；先各自快照 `Modifiers.GetEffectiveValue(CardStatFields.Attack)`（`Math.Max(0, …)`），再依次 `DamagePipeline.BattleAsync(first, second, firstAtk)`、`DamagePipeline.BattleAsync(second, first, secondAtk)`——快照先于任何结算，故先死者不影响反向输出。不扣行动费、不清行动位、不发 `unit.attack.*`/`unit.combat.survived`/`unit.acted`；`source` 仅作「谁发起了这次战斗」的审计参数（写入日志），**不改变伤害归属**（`first→second` 恒 `Source = first`、`Killer = first`）。
- 失败语义：前置不满足 → `false` 且零副作用（不建流、不发信号、不扣血）；`source` 为 `null` 合法；`DamagePipeline` 不可达 → `false`；单侧目标已死（极端路径）→ 该侧返回 0、另一侧照常。
- 回归风险：新方法无既有调用；`DiyExample08` 由两向 `DamageAsync` 改用本方法后，其「攻击力快照」断言需改为「先算双方攻击再依次施加」的等价断言。
- 验收命令：`dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --filter "FullyQualifiedName~BattleAsync|FullyQualifiedName~DiyExample08"`
- Acceptance:
  - [ ] 两条伤害路径均携带 `Source`（各自为对方）
  - [ ] 双方伤害按快照攻击力计算（先死者不影响后结算方）
  - [ ] 免疫在战斗中生效、重甲不生效（重甲只挂 `combat.damage`）
  - [ ] 不触发任何攻击类信号与行动代价
  - [ ] 任一方非在场存活单位 → `false` 且零副作用
- Rationale：`BattleAsync` 是 R10 的唯一入口；快照＋顺序施加是在「无同时结算原语」下满足「同时结算」语义的最小实现。
- Terminology: [战斗](./CONTEXT.md)

- 实测：`dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --filter "FullyQualifiedName~BattleAsync|FullyQualifiedName~DiyExample08"` → `已通过! - 失败: 0，通过: 8`（`BattleAsyncTests` 5 条 + `DiyExample08` 3 条）。
- 偏差：①`source` 的审计落点＝`RootStream.WriteLog("战斗", ...)`（`LogLevel.Info`），不进入任何伤害归属；②快照在 `CommandManager.BattleByEffectAsync` 内做（先算双方攻击力，再各调一次 `DamagePipeline.BattleAsync`），保证「先死者不影响反向输出」；③同一单位自战＝`false`（S25 未写，按「双方」语义补充）。

### S26: R11 行为变更迁移与全量回归
- Status: Done
- 改动文件：`tests/Orc.Game.Tests/KeywordBatch2Tests.cs`、`tests/Orc.Game.Tests/KeywordEffectBatch2Tests.cs`、`tests/Orc.Game.Tests/CommandCombatTests.cs`、`tests/Orc.Game.Tests/DiyExample08_AssaultAdvanceRetreatTests.cs`、新增 `tests/Orc.Game.Tests/DamagePipelineTests.cs`、`tests/Orc.Game.Tests/BattleAsyncTests.cs`。
- 检索清单（迁移前必跑）：
  ```bash
  grep -rn "Immune" tests/Orc.Game.Tests --include=*.cs
  grep -rn "IsDamageZeroed\|AddDamageReduction\|MarkDamageZeroed\|GetDamageReduction" tests src --include=*.cs
  grep -rn "\.ApplyDefenseDamageAsync(\|\.ApplyDamageAsync(" tests/Orc.Game.Tests --include=*.cs | wc -l   # 当前 85
  ```
- 迁移规则（只改断言意图，不删用例）：
  | 场景 | 迁移前 | 迁移后 |
  | --- | --- | --- |
  | 效果伤害 × 免疫 | 「穿透，照常扣血」 | **归零**（R11 行为变更；用例名保留，断言反转并加注释 `R11-note`） |
  | 战斗伤害 × 免疫 | 不存在 | 归零（新用例） |
  | 重甲 × 效果伤害 | 不减免 | 不减免（不变，加断言固化） |
  | 重甲 × 战斗伤害 | 不存在 | 不减免（新用例） |
  | 重甲 × 对战伤害 | −X | −X（不变） |
  | 伏击断言 | 攻击者死亡/目标不受伤 | 不变 |
- 关键签名（用例名）：
  ```text
  Effect_Damage_Is_Zeroed_By_Immune            // R11-note 行为变更
  Battle_Damage_Is_Zeroed_By_Immune
  Armor_Does_Not_Reduce_Effect_Damage
  Armor_Does_Not_Reduce_Battle_Damage
  Pipeline_Combat_Effect_Battle_All_Reach_Apply
  Pipeline_Apply_Is_Only_Portal_Caller
  ```
- 失败语义：迁移后用例数与迁移前一致（新增另计）；任何「删除用例」＝不合格；基线 1650 通过，迁移后通过数 ≥1650 + 新增数。
- 回归风险：`KeywordBatch2Tests` 的 HQ 免疫用例依赖 `HqDamageRewriter` 保留（S24 已保留）；`DiyExample08` 改用 `BattleAsync` 后「撤退分支」判定不变。
- 验收命令：
  ```bash
  dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --filter "FullyQualifiedName~DamagePipeline|FullyQualifiedName~BattleAsync|FullyQualifiedName~Immune|FullyQualifiedName~Armor"
  dotnet test OrcEngine.sln
  ```
- Acceptance:
  - [ ] 「效果伤害 + 免疫」断言全部反转为归零，并标 `R11-note`
  - [ ] 全量通过数 ≥1650（基线）＋新增用例数
  - [ ] 迁移未删除任何既有用例
- Rationale：R11-note 是已确认的行为变更；把「穿透」断言显式反转并留注释，才能在回归时区分「变更」与「回归」。
- Terminology: [伤害处理端](./CONTEXT.md)、[战斗](./CONTEXT.md)

- 实测：`dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --filter "FullyQualifiedName~DamagePipeline|FullyQualifiedName~BattleAsync|FullyQualifiedName~Immune|FullyQualifiedName~Armor"` → 全通过；`dotnet test OrcEngine.sln` → **1661 通过 / 0 失败**（基线 1650 ＋ 新增 11）。
- 偏差：①仓库中原本**不存在**「效果伤害 × 免疫」的断言（免疫只在攻击流程内生效），因此 R11-note 的「迁移」落为**新增** `Effect_Damage_Is_Zeroed_By_Immune`，并在用例注释标 `R11-note`；②`KeywordEffectBatch2Tests` 两处「死后探针」原先读 `AttackDamageResolution` 的归零/减伤容器，容器删除后改为断言 `DamagePipeline.CombatDamage/Apply.Events` 中不再有对应注册项（用例保留、意图不变）；③新增 6 条（4 条行为 ＋ 2 条管道），另加 `BattleAsyncTests` 5 条，共 11 条。

### S27: 文档同步清单（Part B 回改的验收）
- Status: Done
- 改动文件：`05-动作面与运行时API.md`、`07-词条与预制效果.md`、`08-能力边界.md`、`reference/{信号表,API速查,词条表}.md`、`examples/08-*.md`、`examples/README.md`。
- 关键产物：
  - 05 章：动作表增 `BattleAsync` 行（签名/前置/失败语义/不发哪些信号）；`DamageAsync` 行补「经 `effect.damage` → `damage.apply`」；§8 的「两向 `DamageAsync`」改为 `BattleAsync`。
  - 07 章：新增「伤害三层」小节（三端 + 处理端 + 改写归属表：伏击→`unit.attack`、重甲→`combat.damage`、免疫→`damage.apply`）；词条表「打标/行为」列同步。
  - 08 章：G-02 关闭（`BattleAsync` 上线，替代与差异段落删除或改写为「已解决」）；新增「R11 行为变更」条目（免疫现覆盖效果/战斗伤害，重甲仍只减对战）。
  - 参考层：API 速查增 `BattleAsync` 与三个端点的注册口；信号表补「伤害三端不是总线信号，是具名触发器」注记；词条表增「挂载层」列。
  - 示例 8：完整代码改用 `BattleAsync`；「边界与坑」写明与指挥对战的差异（无射程/守护/烟幕/拦截/伏击/反击资格；不付行动代价）。
- 失败语义：文档出现「两向 `DamageAsync`」写法＝过期；08 章仍列 G-02＝未关闭。
- 回归风险：示例 8 的代码片段与 `DiyExample08` 测试需同步，否则读者照抄跑不通。
- 验收命令：
  ```bash
  grep -rn "两向 \`DamageAsync\`\|G-02" docs/卡牌DIY开发文档
  dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --filter "FullyQualifiedName~DiyExample08"
  ```
- Acceptance:
  - [ ] 05/07/08 三章与参考层三表全部按上表回改
  - [ ] 示例 8 与 `DiyExample08` 测试同源（同一实现）
  - [ ] 文档内不再出现「两向 DamageAsync」作为推荐写法
- Rationale：R10/R11 是引擎能力变更，文档若不回改就会教出「绕行写法当正解」的错。
- Terminology: [战斗](./CONTEXT.md)、[伤害触发端](./CONTEXT.md)

- 实测：`grep -rn "两向 `DamageAsync`\|G-02" docs/卡牌DIY开发文档` → 仅剩 G-02 的「已解决」记录与对照说明，无「推荐两向」写法；`dotnet test ... --filter "FullyQualifiedName~DiyExample08"` → `已通过! - 失败: 0，通过: 3`。文档链接自检：`docs/卡牌DIY开发文档/**.md` 相对链接 **212 条 / 断链 0**。
- 偏差：①`reference/api速查.md` 原无「伤害三层注册口」小节，新增 §1.1；②`07` 章词条表新增「挂载点（R11 起）」列（19 行逐条填）；③`08` 章 G-02 保留行号并标「已解决（R10）」，另增 §4.1「R11 行为变更」；④`信号表.md` 增「伤害三端不是总线信号」注记；⑤示例 8 的「词条程序写法」保留（仍是合法替代），但结论改为「只用于单纯打一下」。

## 收敛裁决（实现口径，已并入上述步骤）

- **D2 → 示例 3 的计数口径**：监听 `card.died`，按「双方单位死亡」累计（持久计数存于效果实例）；**不用** `card.destroyed`。依据：`card.died` 是游戏层死亡链末步、恰一次（`CommandManager.ProcessDeathAsync` ⑨），涵盖战斗死亡与效果击杀；`card.destroyed` 是引擎销毁（含非单位卡与手牌处置），与「被摧毁单位数」不同义。文档须在示例内写明「含双方、含效果击杀」。
- **D3 → 示例 2 的分配语义**：总值 3 点，**逐点独立随机指派**给一个存活单位（可重复命中）；无存活单位＝零伤害；总伤害恒为 3（有单位时）。依据：语料把该机制登记为 `damageSplit`（均分）/`conditionalRandomSplit`（随机分配，`docs/kards-diy-可参考语料报告.md:76-77`），需求句式为「随机分配」；示例内须并列写出「均分」替代读法及一行改法。
- **D4 → 示例 8 的战斗入口（已被 R10 取代）**：改用 `EffectRuntime.BattleAsync(unit, enemy, source: host, ct)`（S25）。原「两向 `AttackFlow.ExecuteAsync`」方案作废——执行批次已实测 `AttackFlow` 的默认「伤害生效」处理器扣的是内核 `HealthData`，游戏层单位无该组件（事件被隔离、零伤害），不具备可用性；`CommandManager.AttackByEffectAsync` 仍为 `internal`（`CommandManager.cs:1421`）且带射程/守护/伏击/反击资格判定，语义上不等于「战斗」。撤退仍用公开的 `MoveByEffectAsync`（`CommandManager.cs:1440`）。

## Open Decisions

无。D1–D7 均已闭环（D1/D5/D6 由需求与既有裁决定稿；D2/D3/D4 见上节收敛裁决）。
