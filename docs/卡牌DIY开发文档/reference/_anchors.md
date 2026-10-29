# 代码基线 · 事实源锚点表

> **内部资产**：供正文写作取证用，不进入 `README.md` 的全量索引。
> 定位只用「符号名 + 文件」；代码演进后以 `grep -rn "<符号名>" src` 复核，定位失配即按「符号存在性检查」处理。
> 层归属：**内核**＝`src/Orc`；**游戏层**＝`src/Orc.Game`。

> ## 2026-10-29 重建
>
> v4 编排实体化后 `Trigger<TView>`／`Effect`／`PassiveEffect`／`Bus`／`PrefabManager`／`EffectSystem`／
> `ActionTriggerBands`／`ActionHandleBands`／`AttackFlowBands`／`DamageFlowBands` 均已从代码库删除。
> 本表**只登记现行符号**；旧符号已移除（需要时看 [09 章](../09-新编排模型与术语迁移.md) 的映射表）。

## 0. 取证口径说明

- 一个事实点只登记**一个**权威符号；同一机制出现两个符号名时，先登记为术语冲突（交 `CONTEXT.md` 裁决），不得并列入表。
- 「载荷键」以 `GameUpdates.Payload*` 常量为准，不写裸字符串。
- `Source` 语义：信号载荷键 `Source`＝**事件引发者**（可空、恒写）；视图属性 `TriggerCard`＝**当前 handler 的触发者**（帧值、链上最后者胜）。

## 1. 信号（39 条＝内核 4 ＋ 游戏层 35）

| 机制面 | 事实点 | 符号名 | 定位 |
| --- | --- | --- | --- |
| 信号·内核 | 内核 4 个信号名（声明序） | `KernelSignals.Names` | `src/Orc/Core/Model/KernelSignals.cs` |
| 信号·内核 | 内核 4 个信号成员 | `KernelSignals.Members` | 同上 |
| 信号·内核 | 内核基础引擎预制体 id（`"engine"`） | `KernelSignals.PrefabId` | 同上 |
| 信号·内核 | 内核载荷键 `Card` / `Effect` | `KernelSignals.PayloadCard` / `PayloadEffect` | 同上 |
| 信号·内核 | 内核更新常量（4） | `Updates.CardPlaced` / `CardDestroyed` / `CardData` / `EffectRemoved` | `src/Orc/Core/Updates.cs` |
| 信号·游戏 | 游戏层 35 个信号名（声明序） | `GameSignals.Names` | `src/Orc.Game/Model/GameSignals.cs` |
| 信号·游戏 | 游戏层 35 个信号成员（含载荷字段） | `GameSignals.Members` | 同上 |
| 信号·游戏 | 游戏层信号常量 | `GameUpdates.*` | `src/Orc.Game/GameUpdates.cs` |
| 信号·载荷键 | 引发者键（恒写、可空） | `GameUpdates.PayloadSource` | 同上 |
| 信号·载荷键 | 原因键（`card.leaving` / `counter.disarmed`） | `GameSignals.PayloadReason` | `src/Orc.Game/Model/GameSignals.cs` |
| 信号·时机 | 时机枚举（`Any`/`Load`/`Placed`） | `SignalTiming` | `src/Orc/Core/Model/Primitives.cs` |
| 信号·构造 | 信号成员的构造面 | `SignalMember.Create` / `SignalMember.Payload` | `src/Orc/Core/Model/Signals.cs` |
| 信号·可见性 | 信号默认可见性（`Hostable|Callable`） | `SignalMember.DefaultVisibility` | 同上 |
| 信号·失败 | 未注册／非信号时发射 | `SignalNotRegisteredException` | 同上 |

## 2. 成员、边、挂载、包装

| 机制面 | 事实点 | 符号名 | 定位 |
| --- | --- | --- | --- |
| 成员 | 成员声明（名字＋视图＋可见性＋自有边＋校验＋段序＋缝） | `MemberDefinition` | `src/Orc/Core/Model/Members.cs` |
| 成员 | 自有边（成员自带的执行体） | `OwnedEdge` | 同上 |
| 成员 | 可见性位标志（`Hostable`/`Mountable`/`Callable`） | `MemberVisibility` | `src/Orc/Core/Model/Primitives.cs` |
| 成员 | 私有字段声明（`owner.<Name>`） | `PrivateFieldDefinition` | `src/Orc/Core/Model/Members.cs` |
| 成员 | 合法性检验契约 | `IMemberValidation` / `DelegateMemberValidation` | 同上 |
| 成员 | 段名 → 执行序下标 | `MemberDefinition.BandIndexOf` | 同上 |
| 成员 | 是否声明某缝 | `MemberDefinition.DeclaresSeam` | 同上 |
| 成员·多态 | 整段覆写（可见性只能收紧） | `MemberDefinition.Override` | 同上 |
| 实体 | 四类实体共用运行期形态 | `OrchestrationEntity` | `src/Orc/Core/Model/OrchestrationEntity.cs` |
| 实体 | 一次成员执行（one-shot → 校验 → before → 边集合 → after） | `ExecuteMemberAsync` | 同上 |
| 实体 | 边集合按段序执行 | `RunEdgesAsync` | 同上 |
| 实体 | 排序键摊平（段下标／种类／同段内序） | `CollectUnits` | 同上 |
| 实体 | 运行期注册成员 | `RegisterMember` | 同上 |
| 实体 | 运行期给既有成员接入自有边 | `AttachMemberEdge` | 同上 |
| 实体 | 成员执行结果（Executed/Stopped/ValidationRejected/Reason） | `MemberExecutionResult` | 同上 |
| 实体 | `ctx.Stop()` 的作用域（只停本成员） | `MemberExecutionScope` | 同上 |
| 实体 | 私有字段读写（owner 通道） | `GetPrivateField` / `SetPrivateField` | 同上 |
| 边 | 边表（按成员归属） | `MemberEdgeTable` | `src/Orc/Core/Model/Mounting.cs` |
| 边 | 一条外来边（目标＋声明实体＋位次＋段名） | `MountEdge` | 同上 |
| 边 | 撤销句柄 | `MountHandle` | 同上 |
| 边 | 挂载声明（target + member） | `MountDeclaration` | 同上 |
| 边·排序 | 静态声明序（组件 → 成员/挂载 → 边） | `DeclarationOrder` | `src/Orc/Core/Model/Primitives.cs` |
| 挂载 | 装载期规划与七条校验 | `MountPlanner.Plan` | `src/Orc/Core/Model/OrchestrationRuntime.cs` |
| 挂载 | 规划结果＋报告 | `MountPlan` / `PrefabLoadReport` | 同上 |
| 包装 | 包装声明（目标＋相位＋handler＋缝） | `WrapDeclaration` | `src/Orc/Core/Model/Mounting.cs` |
| 包装 | 包装目标（成员／判定器／缝） | `WrapTarget` | 同上 |
| 包装 | 包装注册面 | `WrapRegistry` | 同上 |
| 包装 | 包装条目／句柄 | `WrapEntry` / `WrapHandle` | 同上 |
| 包装 | 相位枚举（`Before`/`After`/`Replace`） | `WrapperPhase` | `src/Orc/Core/Model/Primitives.cs` |
| 装配 | 组件实例装配态（`IsAssembled`/`IsDisposed`） | `ComponentAttachment` | `src/Orc/Core/Model/ComponentAssembly.cs` |
| 装配 | 装配/卸载（逐句柄撤销） | `OrchestrationRuntime.Bind` / `Unmount` | `src/Orc/Core/Model/OrchestrationRuntime.cs` |
| 装配 | 注册组件声明的包装 | `OrchestrationRuntime.RegisterWraps` | 同上 |
| 注销 | 兜底信号规则（恒含 `card.destroyed`，拒 `effect.removed`） | `ComponentDisposalPolicy` | `src/Orc/Core/Model/DisposalPolicy.cs` |
| 注销 | 保留成员名前缀（`framework.`） | `ComponentDisposalPolicy.ReservedPrefix` | 同上 |
| 预制体 | 预制体声明（OOP 的类，`Extends` 全继承） | `PrefabDefinition` | `src/Orc/Core/Model/Prefabs.cs` |
| 预制体 | 展开后的类型（成员表/私有字段/挂载/包装） | `PrefabType` | 同上 |
| 预制体 | 继承链展开器（环检测、深度 64、覆写合并） | `PrefabCatalog.Build` / `Resolve` | 同上 |
| 地址 | 成员地址（`engine`/`host`/`self`，按第一个 `#` 切） | `MemberAddress` | `src/Orc/Core/Model/MemberAddress.cs` |
| 执行帧 | 当前 handler 的触发者 | `Context.TriggerCard` | `src/Orc/Core/Context.cs` |
| 执行帧 | 调用具名缝（LIFO 管道） | `Context.SeamAsync` | 同上 |
| 段序 | 三套段序方案的唯一真源 | `BandSchemes`（`ActionTrigger`/`ActionHandle`/`OrderFlow`） | `src/Orc.Game/Model/BandSchemes.cs` |
| 段序 | 21 端逐条的段序方案与自有边段名 | `EngineProcesses.ActionEndPositions` | `src/Orc.Game/Model/EngineProcesses.cs` |
| 段序 | 按成员名查段名／方案 | `EngineProcesses.BandOf` / `BandsOf` | 同上 |
| 引擎 | 订阅信号成员（建端点＋边） | `LogicEngine.SubscribeMember` | `src/Orc/Core/LogicEngine.cs` |
| 引擎 | 给任意引擎成员挂外来边 | `LogicEngine.AttachMemberEdge` | 同上 |
| 引擎 | 宿主回调订阅（**无执行帧**） | `LogicEngine.Subscribe` | 同上 |
| 引擎 | 对局级共享变量 | `LogicEngine.Variables`（`EngineVariables`） | 同上 |
| 审查 | 审查图（实体/成员/边/包装） | `AuditGraph` | `src/Orc/Core/Model/AuditGraph.cs` |
| handler | handler 委托（三参） | `KernelHandler` | `src/Orc/Core/Model/Handlers.cs` |
| handler | handler 资产／注册表 | `HandlerAsset` / `HandlerRegistry` | 同上 |
| handler | 签名校验（须三参、返回 `Task`） | `HandlerSignatures.Describe` | 同上 |
| 强类型视图 | 把强类型处理器接成自有边 | `MemberEdges.For<TView>` | `src/Orc/Core/Model/MemberEdges.cs` |

## 3. 判定器（28 名）

| 机制面 | 事实点 | 符号名 | 定位 |
| --- | --- | --- | --- |
| 判定器 | 名称常量表（唯一字面值来源） | `JudicatorNames` | `src/Orc.Game/Judicators/JudicatorNames.cs` |
| 判定器·基类 | 统一签名 | `Judicator` | `src/Orc/Core/Judicator.cs` |
| 判定器·基类 | 强类型适配 | `Judicator<TDelegate>` | 同上 |
| 判定器 | 注册表（按名解析／包装器改写） | `JudicatorRegistry` | `src/Orc/Core/JudicatorRegistry.cs` |
| 判定器 | 管道入口（两层域） | `ServiceEntry.Invoke` | `src/Orc/Core/UnifiedServices.cs` |
| 判定器 | 包装器注册（旧插件面） | `RegisterBeforeWrapper` / `AfterWrapper` / `ReplaceWrapper` | 同上 |
| 判定器 | 强类型包装器工厂 | `Wrapper.Before` / `Wrapper.After` | `src/Orc/Core/WrapperFactory.cs` |
| 判定器·执行 | csx 侧问一条规则 | `EffectRuntime.Judge` | `src/Orc.Game/Effects/EffectRuntime.cs` |
| 判定器包装 | `judge-wraps` 的落点（共用 `WrapRegistry`） | `WrapTarget.ForJudicator` | `src/Orc/Core/Model/Mounting.cs` |

## 4. targeter 与选择器

| 机制面 | 事实点 | 符号名 | 定位 |
| --- | --- | --- | --- |
| 选择器 | 选择器实例（槽位声明载体） | `Selector<TResult>` | `src/Orc.Game/Targeting/Selector.cs` |
| 选择器 | 11 个稳定模板 | `SelectorTemplates` | `src/Orc.Game/Targeting/Selectors.cs` |
| 选择器 | 候选参数（引用集＋数量＋域校验＋tag） | `ReferenceSetParameter` | `src/Orc.Game/Targeting/Selector.cs` |
| 选择器 | 无头选靶描述 | `EffectSelector` | `src/Orc.Game/Effects/EffectRuntime.cs` |
| targeter | 玩家侧管理器 | `TargeterManager` | `src/Orc.Game/Targeting/TargeterManager.cs` |
| targeter | 前端桥接契约 | `ITargeterBridge` | `src/Orc.Game/Targeting/ITargeterBridge.cs` |
| targeter | 流程契约（Step/Retry/取消） | `ITargeterFlow` | `src/Orc.Game/Targeting/ITargeterFlow.cs` |
| targeter | 三态结果 | `TargeterResult` | `src/Orc.Game/Targeting/TargeterResult.cs` |
| targeter | 单步结果 | `SelectorResult<TResult>` | `src/Orc.Game/Targeting/SelectorResult.cs` |
| targeter | 手牌选靶 | `HandSelection.PickAsync` | `src/Orc.Game/Targeting/HandSelection.cs` |

## 5. 动作面 `EffectRuntime`

| 机制面 | 事实点 | 符号名 | 定位 |
| --- | --- | --- | --- |
| 动作面 | 卡 → 运行时（未装配＝null） | `EffectRuntime.ResolveFor` | `src/Orc.Game/Effects/EffectRuntime.cs` |
| 动作面·消灭 | 死亡链（source 作 Killer；形参在 ct 前） | `KillAsync` | 同上 |
| 动作面·伤害 | 单位/HQ 伤害（走 `effect-damage`） | `DamageAsync` | 同上 |
| 动作面·战斗 | 快照＋同时结算（走 `battle-damage`） | `BattleAsync` | 同上 |
| 动作面·修饰 | 攻防增减（duration → 到期相位） | `BuffAsync` | 同上 |
| 动作面·词条 | 授予（无参／带参值） | `GrantAsync`（两个重载） | 同上 |
| 动作面·词条 | 撤销／参值改写 | `RevokeAsync` / `SetKeywordValueAsync` | 同上 |
| 动作面·抽牌 | 抽牌（含满手爆牌裁决） | `DrawAsync` | 同上 |
| 动作面·卡组 | 定向取卡（三态） | `FetchFromDeckAsync` | 同上 |
| 动作面·修饰 | 行动费／部署费修饰（**无 source**） | `CostModAsync` / `DeployCostModAsync` | 同上 |
| 动作面·手牌 | 按卡名加入手牌／洗入卡组 | `AddToHandAsync` / `ShuffleInAsync` | 同上 |
| 动作面·状态 | 压制／抑制／升级／揭示 | `PinAsync` / `SilenceAsync` / `UpgradeAsync` / `RevealAsync` | 同上 |
| 动作面·移动 | 无头移动（frontline/support） | `MoveAsync` | 同上 |
| 动作面·资源 | 槽加／减，点数加／减 | `GainPointSlotsAsync` / `LosePointSlotsAsync` / `GainPointsAsync` / `LosePointsAsync` | 同上 |
| 动作面·选靶 | 无头／交互式统一入口 | `SelectAsync` | 同上 |
| 动作面·光环 | 声明／重算（**无 source**） | `DeclareAuraAsync` / `RerunAurasAsync` | 同上 |
| 动作面·光环 | 已声明光环表 | `AurasOf` | 同上 |
| 动作面·查询 | 归属玩家单位 | `OwnerUnits` | 同上 |
| 动作面·判定 | 问一条判定规则 | `Judge` | 同上 |
| 动作面·条件 | 求值条件表达式串 | `EvaluateCondition`（静态） | 同上 |
| 动作面·场地 | 加入前线／支援阵线 | `JoinFrontlineAsync` / `JoinSupportLineAsync` | `src/Orc.Game/Effects/EffectRuntime.Board.cs` |
| 动作面·场地 | 手牌选靶 | `PickHandCardsAsync` | 同上 |
| 动作面·场地 | 加入战场的成员地址 | `JoinFrontlineTrigger` / `JoinSupportLineTrigger` | 同上 |
| 动作面·结果 | 手牌选靶结果 | `HandCardPick` | 同上 |
| 动作面·枚举 | 修饰期限 | `EffectDuration` | `src/Orc.Game/Effects/EffectRuntime.cs` |
| 动作面·选靶 | 目标阈值过滤 | `EffectThreshold` | 同上 |
| 动作面·光环 | 光环受益谓词 | `EffectAuraFilter` | 同上 |

## 6. 词条与词条程序

| 机制面 | 事实点 | 符号名 | 定位 |
| --- | --- | --- | --- |
| 词条 | 词条标识常量集（19 个） | `KeywordIds` | `src/Orc.Game/Cards/KeywordSystem.cs` |
| 词条 | 词条注册面 | `KeywordRegistry.Register` | 同上 |
| 词条 | 卡上读面（Has/GetValue/TryGetComponent） | `KeywordManager` | `src/Orc.Game/Cards/KeywordManager.cs` |
| 词条 | 静态规则门面 | `KeywordRules` | `src/Orc.Game/Cards/KeywordSystem.cs` |
| 词条程序 | 定义（skills/rules/events） | `KeywordProgram` | `src/Orc.Game/Cards/KeywordProgram.cs` |
| 词条程序 | 规则点 11 条 | `KeywordProgram.RuleNames` | 同上 |
| 词条程序 | 事件 17 条 | `KeywordProgram.EventNames` | 同上 |
| 词条程序 | 主体 6 条 | `KeywordProgram.Subjects` | 同上 |
| 词条程序 | 操作 32 条 | `KeywordProgram.Operations` | 同上 |
| 词条程序 | 目标 14 条 | `KeywordProgram.Targets` | 同上 |
| 词条程序 | 校验 | `KeywordProgram.Validate` | 同上 |
| 词条程序 | 解释执行 / 运行时 | `ProgramKeywordComponent.Execute` / `KeywordProgramRuntime` | `src/Orc.Game/Cards/ProgramKeywordComponent.cs` |

## 7. 卡数据与资产

| 机制面 | 事实点 | 符号名 | 定位 |
| --- | --- | --- | --- |
| 卡数据 | schema 版本（＝1） | `CardDataJson.SchemaVersion` | `src/Orc.Game/Cards/Data/CardDataJson.cs` |
| 卡数据 | 反序列化（警告不抛） | `CardDataJson.Deserialize` | 同上 |
| 卡数据 | 目录加载（预检入口） | `CardDataLoader.LoadDirectory` | `src/Orc.Game/Cards/Data/CardDataLoader.cs` |
| 卡数据 | 数据体（id/name/components） | `CardDataBody` | `src/Orc.Game/Cards/Data/CardDataBody.cs` |
| 卡数据 | 分发预检（0 error 门禁） | `CardDistributionValidator.Validate` | `src/Orc.Game/Cards/Data/CardDistributionValidator.cs` |
| 卡数据·组件 | `typeCategory`／`factionCost`／`battleStats`／`tagData`／`keywords`／`effects` | `TypeCategoryDefinition` 等 6 个 | `src/Orc.Game/Cards/Data/Components/BuiltInCardComponents.cs` |
| 资产·schema | v4 版本（＝4）／文件后缀 | `AssetSchema.Version` / `FilePattern` | `src/Orc/Core/Model/AssetSchema.cs` |
| 资产·schema | 旧字段拒绝清单 | `AssetSchema.LegacyFields` | 同上 |
| 资产·schema | 解析／写出 | `AssetSchema.Read` / `Write` | 同上 |
| 资产·模型 | 组件资产 | `ComponentAsset` | `src/Orc/Core/Model/AssetModel.cs` |
| 资产·模型 | 成员/判定器包装声明 | `ComponentMemberAsset` / `ComponentJudgeWrapAsset` | 同上 |
| 资产·模型 | 资产 → 预制体（**不传 `extends`**） | `ComponentAsset.ToPrefabDefinition` | 同上 |
| 资产·装载 | 卡包原子装载门（包级报告/可选包跳过） | `AssetPackLoader` | `src/Orc/Core/Model/AssetPackLoader.cs` |
| 兼容 | 旧 `EffectSnapshot` → v4 组件（翻译执行） | `LegacySnapshotComponentBinder` | `src/Orc.Game/Cards/LegacySnapshotComponentBinder.cs` |
| 兼容 | 旧快照迁移器 | `AssetSchemaMigrator.Migrate` | `src/Orc.Game/Migration/AssetSchemaMigrator.cs` |
| 兼容 | 旧 `*.prefab.json` 读写 | `PrefabJson.Deserialize` / `Serialize` | `src/Orc/Cards/PrefabJson.cs` |
| 卡 | 薄容器（`AddData`/`GetData`） | `Card` | `src/Orc/Cards/Card.cs` |
| 卡 | 放置/销毁处理器 | `CardLoadoutProcessor` | `src/Orc/Cards/CardLifecycle.cs` |
| 游戏层 | `engine.game` 预制体（`extends: engine`） | `GameEnginePrefabs.Create` / `CreateCatalog` | `src/Orc.Game/Model/GameEnginePrefabs.cs` |
| 游戏层 | 卡牌类型预制体（`base` ← `unit`/`order`/`counter`/`hq`） | `CardProcesses.Definitions` | `src/Orc.Game/Model/CardProcesses.cs` |
| 游戏层 | 引擎流程成员名表（25 个） | `EngineProcesses.MemberNames` | `src/Orc.Game/Model/EngineProcesses.cs` |
| 游戏层 | 旧显示名 → 成员地址的官方映射 | `EngineProcesses.InjectTargets` / `Retained` | 同上 |
| 游戏层 | 生产端发射接线（成员执行） | `ModelMemberDispatch.InvokeEngineMemberAsync` | `src/Orc.Game/Model/ModelMemberDispatch.cs` |

## 8. 来源两面

| 机制面 | 事实点 | 符号名 | 定位 |
| --- | --- | --- | --- |
| 来源 | 归一化唯一入口（Card/Ref/失效/错型/null） | `SourceNormalizer.ToCard` | `src/Orc/Cards/SourceNormalizer.cs` |
| 来源 | 载荷键（与视图属性同名） | `TriggerPayloadKeys.TriggerCard` | `src/Orc/Core/TriggerPayloadKeys.cs` |
| 来源 | 执行期触发者读面 | `Context.TriggerCard` | `src/Orc/Core/Context.cs` |
| 来源 | 视图回填宿主（承载对象） | `OrchestrationEntity.HostForView` | `src/Orc/Core/Model/OrchestrationEntity.cs` |
| 来源 | 弃置动作尾随 source | `PlayerManager.DiscardCardAsync` | `src/Orc.Game/Managers/PlayerManager.cs` |
