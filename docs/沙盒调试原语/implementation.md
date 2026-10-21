# Implementation — 沙盒调试原语（引擎保留触发器）

状态：实现 grill **已收敛**（S1–S9 全部到可执行粒度；D1–D9 全部已裁决；无待裁项）。
目标：在 `Orc.Game` 落地一组「构造期封闭 handler 集、仅沙盒授权实例可触发」的保留触发器原语；MCP 服务层薄封装；以自动化用例固化「指令 / 反制 / 新卡生成与加载」三条链。

## Scope

- Target：
  - `src/Orc/Core/Trigger.cs` —— 唯一内核改动：一次性封闭态（加性、默认不封闭）。
  - `src/Orc.Game/Sandbox/`（新）—— 保留触发器族、原语入口、结果载体、失败原因枚举。
  - `src/Orc.Game/Match/Match.cs`、`MatchOptions.cs` —— 授权事实、`Sandbox` 公开面、装配与 `Initialize` 接线。
  - `src/Orc.Mcp/Tools/` —— `sandbox.*` 写类工具薄封装 ＋ `DebugToolCatalog` 登记 ＋ `diag.audit_chain` 增节。
  - `tests/Orc.Game.Tests`、`tests/Orc.Mcp.Tests` —— 引擎级与工具级用例。
- Excludes：
  - 回放 / 存档 / 时间旅行 / 撤销。
  - `play` 模式工具面变更、引擎规则语义变更、`card.load` 的"只追加不覆盖"。
  - 对效果 / 插件 / 宿主开放这些触发器。
  - `set_turn`（已否决）、`set_stats`（第二批）。
  - `instantiate` 的 `deck_bottom` / `discard` 去向（已裁决删除）。

## 关键规则（不选）

1. **封闭性由内核承载**：`Trigger<TView>` 增一次性封闭态（`Seal()`），封闭后 `Register`（两重载）／`RegisterDisposal`／`RegisterBeforeWrapper`／`RegisterAfterWrapper`／`RegisterReplaceWrapper` 一律抛 `InvalidOperationException`，文本含 `DisplayName`；默认不封闭。
2. **内核不带语义**：`Orc.Core` 只提供"不可再注册"能力，不出现"沙盒/调试"字样。
3. **封闭时点＝构造返回前**：保留触发器在自身构造里"注册唯一内置 handler → 封闭"；缺 handler ⇒ 构造抛、实例不产出。
4. **双防线**：①不可达（实例仅 internal 持有，无 public 属性、不进 `MatchNamedTriggers`、不进预制体 `injects`）；②封闭态（`Orc.Game.Tests` 经 `InternalsVisibleTo` 直取实例断言抛）。
5. **诊断只读**：对局级导出列 `reservedTriggers`（名 / 封闭态 / handler 名）；静态 `GameHooksJson` 不动；`diag.report.engine.triggers` 计数不含保留触发器。
6. **生产零影响**：未授权或未调用原语时，事件流段数、摘要、既有 2310 项测试逐字节不变。
7. **落地底座＝引擎唯一写点**（判据不是"发不发信号"，而是"该写点是否效果侧唯一写点"）：
   - 可用：`MatchCardService.PlaceToHandAsync / PlaceToDeckTopAsync / PlaceToSupportLineAsync / CreateAsync`、`PlayManager.JoinUnitAsync`、`IJoinActionServices.UnitizeOnJoinAsync`、`IMoveActionServices.LandMoveAsync`、`MatchActionServices.ApplyDeathAsync`、`Engine.DestroyCard`、`HandLimitBurn.DestroyAndEmitDiscardedAsync`、`PlayerManager.DrawCard`、`ResourceManager.ChangePointsAsync / GainPointsAsync / LosePointsAsync`。
   - 禁用：`PlayUnitAsync` / `BeginUnitPrePlayAsync` / `DeployAsync` / `PlayOrderAsync` / `UseCounterAsync` 等**动作入口**（校验、扣费、相位、选靶那层）。
   - 不得直写 `Slot.Place` ＋ 手挂 `UnitStateData`（那会绕过 `card.placed` / `unit.joined`）。
8. **失败经结果载体回传，不靠异常**：入口前置查硬门槛（不满足＝不 Invoke、零副作用、返回带 `reason` 的失败）→ handler 复用原子 → 结局写回 `SandboxPrimitiveResult`（`Ok` / `Reason` / 载荷）→ 入口读取返回。既有 `Trigger.InvokeAsync` 异常口径（`Orc/Core/Trigger.cs:423` 业务异常隔离后仍 `Normal`）不改。
9. **满手＝爆牌（成功语义）**：`instantiate→hand` 与 `draw` 满手均走 `CardPlaceStatus.Burned` / `card.burned`，不报错、不算硬门槛。
10. **参数形态**：槽位用 `line: "support"|"frontline"` ＋ `slot: 0..4` 两字段（两条线各自 0–4，不扁平化）。
11. **`remove` 无 `mode` 参数**：分支由实体当前所在区域自动决定（手牌 → 弃置链；在场单位 → 死亡链；其余 → 一步式销毁）。
12. **旧工具与新原语并存，不合并、不改写**：语义差异由名字与参数体现，不用"升级"掩盖。既有 `sandbox.spawn/kill/damage/set_points/emit` 行为与返回结构一字不动，仅在 description 里标注"逐步由原语替代"。
13. **点数不受槽数限制**：`points > pointSlots` 允许，但工具返回附 `warning: "points-exceed-slots"`（不拒绝、不截断、不静默）；`points == 当前值` ⇒ 成功但零信号，返回 `delta: 0`（不为"无变化"硬发 `point.changed`）。

## Implementation Steps

### S1: 内核封闭态 + 保留触发器族

- Status: Todo
- 落点文件：
  - `src/Orc/Core/Trigger.cs`（改）：新增 `private bool _sealed` / `public bool IsSealed` / `public void Seal()`；在 `Register`×2、`RegisterDisposal`、`RegisterBeforeWrapper`、`RegisterAfterWrapper`、`RegisterReplaceWrapper` 入口首行加 `ThrowIfSealed()`。
  - `src/Orc.Game/Sandbox/SandboxPrimitives.cs`（新）：6 个 `Trigger<SandboxPrimitiveView>` 字段（`_instantiate` / `_unitize` / `_move` / `_remove` / `_setPoints` / `_draw`），构造内逐个 `Register(名, handler)` 后立即 `Seal()`；任一注册失败直接抛（实例不产出）。
  - `src/Orc.Game/Sandbox/SandboxViews.cs`（新）：`[ContextView] SandboxPrimitiveView` —— `[Optional][Read]` 的 `DefinitionId` / `Owner` / `Line` / `Slot` / `Card` / `Amount` / `Result`（`SandboxPrimitiveResult`）。
- 改动类型：内核加性（1 个字段 + 1 个属性 + 1 个方法 + 6 处守卫）；新文件 2 个。
- 验收用例名：
  - `Orc.Game.Tests/Sandbox/SandboxTriggerClosureTests.Register_AfterSeal_Throws_WithTriggerName`
  - `…WrapperRegistration_AfterSeal_Throws`
  - `…PublicSurface_DoesNotExposeReservedTriggers`（反射扫 `Match` 公开成员 + `MatchNamedTriggers` 查询 + 预制体 `injects` 解析）
  - `…Construction_MissingHandler_FailsFast`
  - `…Trigger_DefaultNotSealed_ExistingRegistrationStillWorks`（回归）
- Rationale: R1 两条 Decision；`Register` 非 virtual（`Orc/Core/Trigger.cs:203,215`），子类拦不住 ⇒ 必须改内核。

### S2: 沙盒授权门禁与装配接线

- Status: Todo
- 落点文件：
  - `src/Orc.Game/Match/MatchOptions.cs`（改）：新增 `public bool SandboxAuthorized { get; init; }`（缺省 `false`）。
  - `src/Orc.Game/Match/Match.cs`（改）：ctor 读入授权事实存 `_sandboxAuthorized`；`Initialize` 内管理器群建齐后构造 `_sandbox`；新增 `public SandboxPrimitives Sandbox => RequireReady(_sandbox);`。
  - `src/Orc.Mcp/McpService.MatchLaunch.cs:129`（改）：`options: new MatchOptions { …, SandboxAuthorized = Options.Mode == McpServiceMode.Authoring }`。
  - `src/Orc.Mcp/Tools/SandboxTools.cs:97`（改）：`matchOptions` 加 `SandboxAuthorized = service.Options.Mode == McpServiceMode.Authoring`。
  - `src/Orc.Server`（不改）：`ServerBootstrap.cs:95` 已把 `--mode sandbox` 映射为 `McpServiceMode.Authoring`，经 `McpMatchLauncher.cs:101` → `CreateMatchAsync` 自动生效。
- 改动类型：加性属性 + 2 处装配点。
- 验收用例名（`tests/Orc.Game.Tests/Sandbox/SandboxPrimitiveTests.cs`）：
  - `Unauthorized_AllPrimitives_Rejected_StateUnchanged`（快照逐字段比对）
  - `Authorized_PlayPathDoesNotAuthorize`（`McpServiceMode.Player` 建局 ⇒ 未授权）
  - `ModeTests.PlayMode_ToolsList_ContainsNoPrimitiveTools`（`tests/Orc.Server.Tests` 既有文件内追加）
- Rationale: R2「授权由装配方在构造期决定、缺省未授权」；`Match` 形参已很长，授权属"装配选项"而非"构造输入"，故落 `MatchOptions`。

### S3: 原语入口面、结果载体与失败语义

- Status: Todo
- 落点文件：
  - `src/Orc.Game/Sandbox/SandboxPrimitiveResult.cs`（新）：`sealed class`，字段 `Ok` / `Reason`（`SandboxFailure` 常量）/ `Ref` / `Line` / `Slot` / `Before` / `After` / `Burned`。
  - `src/Orc.Game/Sandbox/SandboxFailure.cs`（新）：失败原因常量类（kebab-case 字符串，见「失败原因枚举」）。
  - `src/Orc.Game/Sandbox/SandboxPrimitives.cs`（改）：每个原语一个 `public async Task<SandboxPrimitiveResult> XAsync(...)`：①授权检查 → ②前置硬门槛（不满足＝直接返回、不 Invoke）→ ③`trigger.InvokeAsync(engine, data)`（`data` 带 `Result` 实例）→ ④读 `Result` 返回；⑤`RootStream.WriteLog("sandbox.<primitive>", …)` 审计留痕（只进日志、不 BeginAction、不产段）。
- 改动类型：新增入口 6 个；无既有签名变更。
- 验收用例名（`tests/Orc.Game.Tests/Sandbox/SandboxPrimitiveTests.cs`）：
  - `HardGateTouched_ReturnsReason_AndSnapshotUnchanged`
  - `ValidationSkipped_Succeeds_WhenPointsTurnAndHandWouldFail`
  - `NoAuditSegment_WhenPrimitiveProducesNoSignal`（`sandbox.points` 同值 ⇒ `TakeSegments()` 为空、日志有痕）
  - `SandboxPrimitiveSignalEquivalenceTests.Unitize_MatchesEffectCardSequence`（R4 "逐条一致" 的承载用例，与 S8 共用）
- Rationale: 关键规则 8；`ActionTargetResult` 的 `ActionFailureKind` 只有粗粒度（`Precondition/Blocked/TargetInvalid/Cancelled/HandFull/DeathPrevented/ValidationRejected`，`Actions/ActionContract.cs:68-90`），细粒度原因只能由原语自带载体承载。

### S4: 逐原语实现

- Status: Todo
- 落点文件：`src/Orc.Game/Sandbox/SandboxPrimitives.cs`（6 个 handler，私有方法 `HandleInstantiateAsync` 等）。
- 改动类型：新增。
- 验收用例名（每原语独立，见「对照表」的期望信号与原因；均落在 `tests/Orc.Game.Tests/Sandbox/SandboxPrimitiveTests.cs`）：
  - `Instantiate_ToHand_EmitsHandAdd` / `Instantiate_ToHandAtLimit_Burns` / `Instantiate_ToDeckTop_NoSignal` / `Instantiate_ToSupportSlot_ExactSlot` / `Instantiate_ToFrontlineSlot_ExactSlot` / `Instantiate_UnknownDefinition_Rejected` / `Instantiate_NonUnitToLine_Rejected`
  - `Unitize_FromHandCard_EmitsPlacedAndJoined` / `Unitize_ToFrontlineSlot_Allowed`（并含 `JoinUnitAsync` 前线无障碍的断言：位置字段＝前线槽、`IsDestroyed=false`）
  - `Unitize_OpponentSlot_Rejected`（`slot-not-owned`）/ `Unitize_MissingSlot_ReturnsReason`（视图共用下的参数兜底）
  - `Move_SupportToFrontline_Allowed`（跨线）/ `Move_OccupiedTarget_Rejected`
  - `Remove_HandCard_Discards` / `Remove_OnFieldUnit_DeathChain` / `Remove_OtherEntity_Destroyed`
  - `Draw_N_Cards_Sequence` / `Draw_AtHandLimit_Burns`
  - `SetPoints_SetHigher_EmitsGainedThenChanged` / `SetPoints_SetLower_EmitsLostThenChanged` / `SetPoints_SameValue_NoSignal_ReturnsDeltaZero` / `SetPoints_ExceedsSlots_SucceedsWithWarning` / `SetPoints_WithGainJudicator_AppliesWrappedAmount`（风险 3）
- Rationale: 关键规则 7/9/10/11。

### S5: 审计留痕

- Status: Todo
- 落点文件：`src/Orc.Game/Sandbox/SandboxPrimitives.cs`（`WriteAudit` 私有方法）。
- 改动类型：新增。
- 行为：每次原语调用恰一条 `EventStream.WriteLog(source: "sandbox.<primitive>", message: 入参摘要, level: Info, keywords: ["sandbox-primitive", <名>])`；写当前执行者流（无则 `RootStream`）；**不** `BeginAction`；不进总线（不用 `Bus.Emit`）。
- 验收用例名：`SandboxPrimitiveTests.AuditTrace_PerCall_WrittenToStream_NoBusUpdate`
- Rationale: R4 D3；与 `Bus.WriteTrace`（`Orc/Core/Bus.cs:240`）同口径。注：原子内部的 action scope 照常产生段（那是效果侧信号聚合，不是留痕产生的段）。

### S6: 诊断面暴露

- Status: Todo
- 落点文件：
  - `src/Orc.Game/Sandbox/SandboxPrimitives.cs`（改）：`public string ExportReservedTriggersJson()` → `[{ name, sealed, authorized, handlers: [名…] }]`。
  - `src/Orc.Game/Match/Match.cs`（改）：`public string ExportReservedTriggersJson()` 转发（未初始化返回 `[]`）。
  - `src/Orc.Mcp/Tools/DiagTools.cs:127-137`（改）：`diag.audit_chain` 结果顶层加 `reservedTriggers` 节点（解析引擎 JSON 后挂上，不重写导出器）。
- 改动类型：加性（既有字段一个不动）。
- 验收用例名（`tests/Orc.Mcp.Tests/DiagTests.cs`）：`AuditChain_ContainsReservedTriggers` / `AuditChain_Unauthorized_MarkedUnavailable` / `Report_TriggerCountExcludesReserved`
- Rationale: R1「可观测不可写」＋ R2 二选一取"列出并标注 `unavailable`"。

### S7: MCP 服务层薄封装

- Status: Todo
- 落点文件：
  - `src/Orc.Mcp/Tools/SandboxPrimitiveTools.cs`（新）：6 个 `service.AddTool(…, sandboxOnly: true)`（写类 ⇒ 需 `AllowDebugWrites`，`McpService.cs:102-112` 已实现门禁）。
  - `src/Orc.Mcp/Tools/DebugToolCatalog.cs:14-43`（改）：`WriteToolNames` 加 6 个工具名。
- 工具名与参数（对齐既有命名族）：
  | 工具 | 参数 |
  |---|---|
  | `sandbox.instantiate` | `definitionId`, `owner`, `zone`(`hand|deck_top|support|frontline`), `slot`(0..4；`zone` 为 `support`/`frontline` 时必填，线由 `zone` 决定) |
  | `sandbox.unitize` | `ref`, `line`, `slot` |
  | `sandbox.move` | `ref`, `line`, `slot` |
  | `sandbox.remove` | `ref` |
  | `sandbox.points` | `owner`, `points`（**绝对值**；可低于当前值） |
  | `sandbox.draw` | `owner`, `count`（缺省 1） |
- 与既有工具的显式差异（**不合并**）：
  | 既有（保留原样） | 新原语 | 差异 |
  |---|---|---|
  | `sandbox.set_points { owner, delta }`（增量；`delta ≤ 0` 失败） | `sandbox.points { owner, points }`（绝对值；可减） | 参数与信号口径不同（旧＝仅 `point.changed`；新＝`point.gained`/`point.lost` → `point.changed`） |
  | `sandbox.spawn { definitionId, owner, zone }`（仅 hand / support 首空槽） | `sandbox.instantiate`（四去向、精确槽位） | 新面覆盖旧面；旧面行为不动 |
- 同时改：`ShapeTools.cs` 五个既有工具的 description 末尾追加"（逐步由原语替代）"；**不改**参数、行为、返回结构（`ToolContractTests.cs:111` 只断言 description 非空，无文案锁定）。
- 返回：`details = { ok, ref, line, slot, before, after, reason? }`；成功时必含新建实体 `ref`（供下一步调用）。`sandbox.points` 例外：`details = { ok, owner, before, after, delta, pointSlots, warning? }`（无 `ref`）。`sandbox.draw`：`details = { ok, owner, drawn: [{ref…}], burned: [ref…] }`。
- 错误映射：参数非法 → `invalid-arguments`；引擎拒绝 → `engine-rejected` ＋ `details.reason`；未授权 → `engine-rejected` ＋ `reason="sandbox-not-authorized"`；未建局 → `not-ready`（既有）。不得出现 `internal`。
- 验收用例名：`SandboxPrimitiveToolTests.ToolsHidden_WithoutAllowDebugWrites` / `…ReturnedRefIsUsableByNextCall` / `…InvalidArguments_Code` / `…EngineRejected_CodeWithReason` / `…Points_ExceedsSlots_Warning` / `…Points_SameValue_NoSignal` / `…LegacySetPoints_ContractUnchanged` / `ToolContractTests`（既有契约测试补名单）
- Rationale: R5；`sandboxOnly` ＋ `AllowDebugWrites` 门禁已存在，无需新门禁。

### S8: 三条链的自动化用例

- Status: Todo
- 落点文件：`tests/Orc.Mcp.Tests/SandboxPrimitiveChainTests.cs`（新，基于既有 `SandboxFixture.cs`，`AllowDebugWrites` 真值）。
- 改动类型：新增测试文件（不改夹具语义）。
- 用例：
  1. **指令链** `OrderChain_PrimitiveBuiltBoard_PlayCommand_Succeeds`：`card.write` → `card.load` 注册定义 → `sandbox.instantiate`(指令卡, zone=hand) → `sandbox.points`(补足到费用值) → **若 `observe.view` 的 `currentPlayerIndex` ≠ 该卡 owner，先 `match.end_turn` 推进到其回合**（`PlayManager.CheckAdmission` 要求己方回合 ＋ 卡在手牌）→ `play.command` → 断言 `card.played`、扣费、离手、主动效果生效（用一条已知效果卡，断言其信号/状态）。
  2. **反制链** `CounterChain_PrimitiveBuiltCounter_StopsOrder`：造含 effect handler 的反制卡 → 入手 → `match.end_turn` 推进到持有方 → `play.counter` → `match.end_turn` 回对手 → 对手 `play.command` → 断言 Counter 段 `Stop`、`card.discarded`（一次性消耗）、指令照常扣费离手、效果未触发。
  3. **新卡链** `NewCardChain_WriteLoadInstantiatePlay`：`card.write` → `card.load` → `sandbox.instantiate` → 打出 → 断言效果；negative：非法定义 `card.write`/`card.load` 被拒。
- 断言口径：信号序列用 `engine.Subscribe`（`LogicEngine`，见 `CommandManager.cs:287` 用法）收集 update 类型序列后逐条比对；需求 R4「原语 vs 等效效果卡」另有一条 `SandboxPrimitiveSignalEquivalenceTests`（`tests/Orc.Game.Tests`）。
- 验收用例名：上述 3 条 + `SandboxPrimitiveSignalEquivalenceTests.Unitize_MatchesEffectCardSequence`
- Rationale: `card.write`/`card.load`/`play.command`/`play.counter`/`match.end_turn` 均为 MCP 工具（`AssetTools.cs:21`、`CardTools.cs:18`、`PlayerActionTools.cs:114,125`、`MatchTools.cs:13`）⇒ 三条链只能在 MCP 层跑。

### S9: 零回归与零影响验证

- Status: Todo
- 落点文件：`tests/Orc.Game.Tests/Sandbox/SandboxZeroImpactTests.cs`（新）＋ 全量回归。
- 验收用例名（`tests/Orc.Game.Tests/Sandbox/SandboxZeroImpactTests.cs`）：
  - `ZeroImpact_AuthorizedNoCalls_SameSegmentsAndSummary`（授权但零调用的对局 vs 未授权对局：段数、摘要一致）
  - `ExistingSandboxTools_ContractUnchanged`（`sandbox.spawn/kill/damage/set_points/emit` 返回结构）
- 全量回归（无对应用例名）：`dotnet test OrcEngine.slnx` 全绿（基线 2310 ＋ 新增）。

## 原语 → 引擎写点 → 信号序列 → 失败原因

> 共同前置（表内不重复）：所有原语均可返回 `sandbox-not-authorized`（未授权实例）与 `match-not-ready`（`Sandbox` 未就绪）。
> 命名：原语 `set_points` 的工具名＝`sandbox.points`（对齐"`sandbox.<动词>`"规则）；其余原语与工具同名。

| 原语 / 去向 | 引擎写点（唯一写点） | 信号序列（顺序） | 失败原因（`details.reason`） |
|---|---|---|---|
| `instantiate` → hand | `MatchCardService.CreateAsync` → `PlaceToHandAsync`（`AddToHandAction`） | 正常：`card.hand.add`；满手：`card.destroyed` → `card.burned`（**成功**，`burned=true`） | `definition-unknown` / `placement-rejected` |
| `instantiate` → deck_top | … → `PlaceToDeckTopAsync` | 零信号（静默） | `definition-unknown` |
| `instantiate` → support[slot] | … → `PlaceToSupportLineAsync` → `JoinUnitAsync` → `UnitizeOnJoinAsync` | `card.placed` → `unit.joined` | `slot-not-owned` / `target-slot-occupied` / `slot-out-of-range` / `not-a-unit-card` / `game-ended` / `phase-blocked` |
| `instantiate` → frontline[slot] | … → `JoinUnitAsync(前线槽)`（不经 `ContainsSlot` 组合校验） | `card.placed` → `unit.joined` | 同上 |
| `unitize` | `JoinUnitAsync` → `UnitizeOnJoinAsync` | `card.placed` → `unit.joined` | `entity-not-found` / `not-a-unit-card` / `unit-already-unitized` / `slot-not-owned` / `target-slot-occupied` / `slot-out-of-range` / `game-ended` / `phase-blocked` |
| `move` | `LandMoveAsync` | `unit.position.changed` | `entity-not-found` / `not-on-battlefield` / `entity-destroyed` / `target-slot-occupied` / `same-slot` |
| `remove`（在手牌） | `player.Hand.Remove` → `HandLimitBurn.DestroyAndEmitDiscardedAsync` | `card.destroyed` → `card.discarded` | `entity-not-found` / `entity-destroyed` |
| `remove`（在场单位） | `MatchActionServices.ApplyDeathAsync` | `card.dying` →（亡计结算）→ `card.died` | `entity-not-found` / `entity-destroyed` |
| `remove`（其余） | `Engine.DestroyCard` | `card.destroyed` | `entity-not-found` / `entity-destroyed` |
| `draw` ×N | `PlayerManager.DrawCard`（循环 N 次，每张独立结局） | 每张：`card.drawn` → `card.hand.add`；满手：`card.drawn` → `card.destroyed` → `card.burned` | `deck-empty`（首张即返，已抽出的不回滚——按"每张独立"语义） |
| `set_points`（设绝对值，升高） | `ResourceManager.GainPointsAsync(Δ)`（Δ＝目标−当前） | `point.gained`（Δ）→ `point.changed`（before→after） | `points-out-of-range`（int 溢出，防御性） |
| `set_points`（设绝对值，降低） | `ResourceManager.LosePointsAsync(Δ)` | `point.lost`（Δ）→ `point.changed`（before→after） | 同上 |
| `set_points`（目标＝当前） | 不调原子（零变更） | **零信号** | 成功，`delta: 0`；`points > pointSlots` 时仍附 `warning` |

`set_points` 的返回值：`{ owner, before, after, delta, pointSlots, warning? }`；`points > pointSlots` ⇒ `ok=true` ＋ `warning="points-exceed-slots"`（不拒绝、不截断）。

失败原因常量（`SandboxFailure`，wire 字符串；括号内为对应引擎枚举，无对应者为新增）：

`sandbox-not-authorized`、`match-not-ready`、`definition-unknown`（`KeyNotFoundException`）、`not-a-unit-card`（`ArgumentException`）、`slot-out-of-range`、`slot-not-owned`（槽位不在该玩家的指定线内——前置检查，`PlaceToSupportLineAsync` 的同义校验在原子层不存在）、`target-slot-occupied`（`PlayFailureReason.TargetSlotOccupied` / `CardPlaceFailureReason.TargetSlotOccupied`）、`unit-already-unitized`（`PlayFailureReason.UnitAlreadyUnitized` / `CardPlaceFailureReason.UnitAlreadyUnitized`）、`entity-not-found`、`entity-destroyed`、`not-on-battlefield`、`same-slot`、`deck-empty`、`game-ended`（`PlayFailureReason.GameEnded`）、`phase-blocked`（`PlayFailureReason.PhaseBlocked`）、`placement-rejected`（`CardPlaceFailureReason.PlacementRejected`）、`points-out-of-range`（int 溢出，防御性）。

映射规则：`SandboxPrimitiveResult.Ok=false` ⇒ MCP `error.code="engine-rejected"` ＋ `details.reason`；除 `sandbox-not-authorized`（同码不同 reason）外不细分 `error.code`。参数层错误（缺字段、类型错、`line/slot` 越界枚举）在工具层直接 `invalid-arguments`，不触引擎。

## 决策记录

- **D1（已裁 2026-10-21）封闭性**：内核一次性封闭态 ＋ 实例不暴露，双防线。→ 关键规则 1–4、S1。
- **D2（已裁，本轮落定）授权接线**：`MatchOptions.SandboxAuthorized`（缺省 `false`）；两个生产装配点取 `Options.Mode == Authoring`；Server 无需改（`ServerBootstrap.cs:95` 已映射）。→ S2。
- **D3（已裁）失败回传**：结果载体，不改内核异常口径。→ 关键规则 8、S3。
- **D4（已裁）落地底座**：复用效果侧唯一写点、禁止动作入口。→ 关键规则 7、对照表。
- **D5（已裁）去向收敛**：`frontline[slot]` 保留（`JoinUnitAsync` 是唯一写点，判据改为"唯一写点"）；`deck_bottom` / `discard` 删除。→ 对照表。
  - 收敛检查结论：`JoinUnitAsync` 对前线槽**无隐藏依赖**——`Slot` 只有 `Index`/`Occupant`、无线归属（`Board/Slot.cs:27-33`）；`HandleUnitizeAsync` 只查"空槽 + 未单位化"（`Cards/UnitCard.cs:302-321`）；守护维护三线同构（`CommandManager.cs:882-893`）。无缺口。
- **D6（已裁）**：`move` 跨线允许；`remove` 保留且按区域自动分流（无 `mode` 参数）。
- **D7（已裁，本轮落定）留痕不产段**：原语入口不 `BeginAction`；留痕写当前执行者流（无则 `RootStream`）。→ S5。
- **D8（已裁 2026-10-21）`set_points`**：删容量门槛（不拒绝不截断，附 `points-exceed-slots` warning）；信号按效果侧（`point.gained`/`point.lost` → `point.changed`）；工具新开 `sandbox.points`（绝对值），既有 `sandbox.set_points` 原样保留。→ 关键规则 12–13、对照表、S7 差异表。
- **D9（已裁，本轮落定）用例落点**：三条链在 `tests/Orc.Mcp.Tests`（工具名决定）；引擎级语义与封闭性在 `tests/Orc.Game.Tests`。→ S8。
- **D10（父 agent 准出补裁 2026-10-21）判定器改写**：`set_points` 走 `GainPointsAsync` / `LosePointsAsync`，**接受** `resource.point.gain` 等判定器对增量的改写（效果侧真实语义，R4 等效优先）；若实际 `after ≠ 请求值`，返回 `warning: "points-adjusted"` ＋ `requested` / `actual`，不静默。→ 风险 3。
- **D11（父 agent 准出补裁 2026-10-21）视图共用的兜底口径**：六原语共用 `SandboxPrimitiveView` 时，Handler 首行的参数校验失败一律写回 `SandboxPrimitiveResult.Reason = "invalid-arguments"`，**不得抛异常**（`Trigger.InvokeAsync` 会吞掉并记 `Normal`，抛等于静默成功）。→ 风险 2、S3。

## 风险与应对

- **风险 1：`JoinUnitAsync` 不校验槽位线归属**。原语若把对手槽位或错线槽位传进去，引擎会照单全收（`PlayManager.cs:371-379` 只查空槽/未单位化）。**应对**：前置检查承担 `slot-not-owned`（`SandboxPrimitives` 解析 `line` + `slot` 后比对 `Battlefield.GetSupportLine(player)` / `Battlefield.FrontLine` 的引用），并列入 S4 用例（`Unitize_OpponentSlot_Rejected`）。
- **风险 2：`SandboxPrimitiveView` 共用一个视图类型**。六个原语共用 ⇒ 绑定期不做必填校验，参数缺失只能由 Handler 兜住。**应对**：Handler 首行统一 `RequireXxx` 并回 `invalid`→`SandboxPrimitiveResult.Reason`；用例 `Unitize_MissingSlot_ReturnsReason` 覆盖。
- **风险 3：`GainPointsAsync` 受 `resource.point.gain` 判定器包裹**（`ResourceManager.cs:213`）。词条改写会让"设定绝对值"变成"设定 + 改写量"。**已裁（D10）**：接受改写——那正是效果侧真实语义（R4 等效优先）；但**不静默**：若实际 `after ≠ 请求的 points`，返回 `warning: "points-adjusted"` ＋ `requested` / `actual` 两字段。用例 `SetPoints_WithGainJudicator_AppliesWrappedAmount` 断言 warning 与 actual。
- **风险 4：`remove` 三分支的"其余"分支过宽**。`Engine.DestroyCard` 对已单位化单位的语义弱于死亡链（不发 `card.dying/died`、不结算亡计）。**应对**：分支判定顺序固定为"手牌 → 在场单位（`UnitStateData` 且未毁）→ 其余"，并以 `Remove_OnFieldUnit_DeathChain` 断言死亡链信号，防退化。
