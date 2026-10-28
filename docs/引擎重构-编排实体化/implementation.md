# Implementation — 编排实体化

**状态：实现 grill 已收敛（第 17 轮收口，STATUS: DONE）。** 17 个阻塞议题（W-01…W-20，含并入项）全部裁决并落表；
8 条非阻塞项转为「假设 + 替代方案」（A-1…A-8），供准出一次性审。本文可直接交给执行 agent：按 S1→S14 的依赖顺序推进，
每步有可检验验收，批次门与 R8 五批对齐，风险清单在文末。

> **Status 语义**：`Done`＝该步骤的**实现决策已收敛并写入本文档**（不代表代码已实现）；`In Progress`＝决策已部分落表；`Todo`＝待首批执行时回填。
> 本文档不替代 `requirements.md`：凡本文与需求冲突，以需求为准。

> **一句话结论**：编排从"效果主触发器 + 名字字符串挂载"改为**实体（四类）+ 预制体（类）+ 成员＝有序边集合**；
> 三条通道定型为**挂载（设计端，声明序）／包装（元效果端，before/after/replace）／框架边（注销兜底 + 一次性）**；
> `hooks`/band/接缝/显示名寻址/主动被动二分/`Bus` 全部退役，资产由迁移器批量转换，每批全绿。

> **Status 语义**：`Done`＝该步骤的**实现决策已收敛并写入本文档**（不代表代码已实现）；`In Progress`＝正在盘问；`Todo`＝待裁决。
> 本文档不替代 `requirements.md`：凡本文与需求冲突，以需求为准并以 QUESTION 提出回退。

## Scope

- Target：`src/Orc`（内核编排层：实体／预制体／成员 handler／挂载／信号／视图投影／初始化触发器／审查面）、
  `src/Orc.Game`（引擎实体与卡牌实体的承载、组件化效果、40 处 `new Trigger<` 迁移）、
  `src/Orc.Foundation.Actions`（15 处动作触发器）、`src/Orc.Protocol`（选择器消息）、
  `samples/`（151 份 JSON 的 schema 迁移）、`tests/`（六测试项目分批全绿）。
- Excludes：需求 §1 Excludes 全部条目——判定器（27 条内置）与资产服务的判定逻辑语义、卡牌数值／平衡／KARDS 规则、
  `components` 数据组件（`factionCost`/`battleStats`/`tagData`/`keywords`）形态与加载链、UI 与美术投影、卡牌持久化格式。
- 交付形态：沿用 R8「全量替换 + 分批落地」，每批 `dotnet test OrcEngine.sln` 全绿且能指出"这一批改变了哪些行为"；
  不留 Obsolete 适配层，新 schema 不做旧格式兼容读取（转换由迁移器负责）。

## 现状基线（代码核验，仅列影响实现路径的事实）

| 事实 | 证据 |
| --- | --- |
| 触发器固定绑定一个视图类型（泛型参数）：一个触发器只能有一个数据形状 | `src/Orc/Core/Trigger.cs:29`（`Trigger<TView>`）、`Trigger.cs:523`（`ContextViewBinder.Create<TView>`） |
| 视图是 **C# 类**：须 `[ContextView]`、public、非 sealed、无参构造、访问器 virtual | `src/Orc/Core/ViewMeta.cs:71-131` |
| 视图同时是 **csx 的编译基座**：csx 入口签名 `Func<TView, Context, CancellationToken, Task>`，按 `Type` 精确绑定 | `src/Orc/Core/Scripting.cs:16-21`（`ScriptRequest.ViewType`）、`Scripting.cs:85-105`（`ScriptHandlerAdapter.TryBind`） |
| 写的合法性与"可写"标记由**生成代理的属性闸门**实施（非运行期字典校验） | `ViewMeta.cs:119-131`、`src/Orc/Core/ViewProxyFactory.cs`；`src/Orc/Cards/Effect.cs:212-219` 注释（写 `[Read]` 属性抛 `PermissionDeniedException`） |
| csx 沙箱只引用**白名单内已加载程序集**，默认白名单不含 `Microsoft.CSharp`（`dynamic` 的运行时绑定器所在程序集） | `src/Orc.Script/CSharpScriptEvaluator.cs:142-171`、`src/Orc.Script/CSharpScriptSecurityOptions.cs:12-22` |
| csx 失败面是**结构化返回**（`compile`/`signature`/`sandbox`/`timeout`/`runtime`），不是装配期异常 | `src/Orc.Script/CSharpScriptEvaluator.cs:54-125`、`src/Orc/Core/Scripting.cs:27-64` |
| 现有失败口径是**三层**：装配/配置层 fail-fast、运行时装载层＝隔离（回滚为未生效、不阻断）、独立构造＝跳过 | `src/Orc.Game/Cards/EffectSystem.cs:20-21`、`:169-176` |
| 预制体目录加载是**单文件隔离**（失败进报告、其余照常） | `src/Orc/Cards/PrefabManager.cs:73-106` |
| csx 编译是**懒**的：发生在 handler 解析时（装载/注册时点），不在 `RegisterPrefab` 时点 | `src/Orc/Cards/PrefabManager.cs:108-125`、`PrefabJson.Deserialize` 只做结构校验 |
| 资产共引用 6 个视图类型（迁移面的数据形状清单） | `samples/` 的 `viewType`：`Orc.Cards.CardEventView`、`Orc.Game.Triggers.CardTriggerView`、`Orc.Game.Damage.DamageApplyView`、`Orc.Game.Commanding.UnitAttackTriggerView`、`Orc.Game.Commanding.ActionQuotaView`、`Orc.Game.Cards.TargetingView` |
| csx handler 的签名直接写视图类型全名（迁移面最大） | `samples/card-pack-diy/effects/diy.adjacent-cover.mitigation.prefab.json:17`（`Func<Orc.Game.Damage.DamageApplyView, …>`） |
| 被动语义＝`Kind=Passive` + 非空 `hooks` 才可 `Bus.Mount`；主动不可挂载 | `src/Orc/Core/Trigger.cs:139-143`、`src/Orc/Core/Bus.cs:35-51` |
| `hooks` 是**开放字符串集合**，不做注册/白名单；首见即登记引擎词汇表 | `src/Orc/Core/Bus.cs:7`（文档口径）、`Bus.cs:62-72`、`Bus.cs:64`（`_engine.Hooks.Register(hook)`） |
| 同一更新上的执行序＝挂载优先级升序 → 注册序升序（与声明序无关） | `src/Orc/Core/Bus.cs:258-263`（`SnapshotFor` 排序） |
| 引擎级流程处理器（`CardLoadoutProcessor`）也靠 hooks 激活 | `src/Orc/Cards/CardLifecycle.cs:217-237`（`card.placed`／`effect.removed`／`card.destroyed`） |
| 每个效果的 disposing 兜底**隐式**强制含 `card.destroyed`，并**隐式**拒绝 `effect.removed` | `src/Orc/Cards/Effect.cs:614-640`（`NormalizeDisposalHooks`）、`src/Orc/Cards/Prefabs.cs:491-541`（`NormalizeDisposingTrigger`） |
| 该隐式挂载今天在**订阅读面**可观测（测试直接断言） | `tests/Orc.Tests/EffectLifecycleTests.cs:30`、`tests/Orc.Game.Tests/G7HandOperationsTests.cs:345`（`Bus.GetSubscribers(CardDestroyed)` 含 `{effect}/dispose`） |
| `injects[].target` 是**显示名**，经对局级注册表回退解析 | `src/Orc.Game/Match/Match.cs:673-684`（`MatchNamedTriggers.Register("伤害处理触发器", …)`）、`src/Orc.Game/Cards/EffectSystem.cs:496-501` |
| 效果装载四步固定：挂 disposing → 挂主触发器 → `IsMounted` → `OnMount`；作者经 `OnMount/Inject` 手写注入 | `src/Orc/Cards/Effect.cs:332-351`、`Effect.cs:166-191` |
| 保留槽是**触发器内部**的排序段：`RegisterDisposal` 专写、普通 `Register` 不可写入、恒在全部普通事件之后，但槽内仍按 `priority` 升序＋注册序 | `src/Orc/Core/Trigger.cs:247-267`（含 `Trigger.cs:250` 槽内优先级）、根 `CONTEXT.md:77`（"只接受效果销毁 handler"） |
| 保留槽当前的唯一用途就是"效果销毁 handler"（预制体 disposing 事件、`ActiveEffect` 的一次性销毁） | `src/Orc/Cards/DynamicEffect.cs:190`／`:225`、`src/Orc/Cards/Effect.cs:708-720` |
| 卸载是**按 owner 扫表**匹配（`ReferenceEquals(Owner)`），不是按边句柄撤销 | `src/Orc/Core/Bus.cs:88-124` |
| 注入**只能**在 `OnMount` 装载语境中调用（装配＝业务代码） | `Effect.cs:176-180`（`_loading` 守卫） |
| 主动／被动二分贯穿四层 | `TriggerKind`／`PassiveEffect` vs `ActiveEffect<TView>`（`Effect.cs:672`/`:688`）／预制体 `kind`／`Effect.cs:394`、`:486` |
| 审查面按「触发器种类」聚合节点，事件/下游取首个实例代表；hook 订阅读面另表 | `src/Orc/Core/Orchestration.cs:217-244`、`Orchestration.cs:113-123`（`AuditChain.Hooks`） |
| 现有内置"更新字符串"共 39 个常量：内核 4 个（`card.placed`／`card.destroyed`／`card.data`／`effect.removed`），游戏层 35 个，且多为**多段点分名**（`turn.start.before`／`card.stat.changed`／`unit.position.changed`／`unit.damage.dealt`／`keyword.attack.finished`） | `src/Orc/Core/Updates.cs:15-28`、`src/Orc.Game/GameUpdates.cs`（35 个 `public const string`） |
| 自定义更新名被测试与沙盒普遍使用（`custom.placed`／`u`／沙盒运行期建触发器） | `tests/Orc.Tests/AuditChainFoundationTests.cs:207`、`tests/Orc.Tests/BusEmitTests.cs`、`src/Orc.Game/Sandbox/SandboxPrimitives.cs:959` |
| 卡牌实体的成员（打出／部署／部署词条／加入／单位化／老兵／揭示／指向）是在 **C# 构造器**里新建、并以**显示名**登记为"具名触发器"供效果注入按名解析 | `src/Orc.Game/Cards/UnitCard.cs:60-82`（`RegisterNamedTrigger("部署触发器", …)`） |
| 预制体三层（Event/Trigger/Effect）＋快照 schemaVersion=3 | `src/Orc/Cards/Prefabs.cs:20/176/304`、`src/Orc/Cards/PrefabJson.cs:550`（`EffectSnapshot.SchemaVersion`） |

## Implementation Steps

### S1: 基线核验与实现约束落表
- Status: Done
- Target: 本文档「现状基线」表；`src/Orc`、`src/Orc.Game`、`src/Orc.Foundation.Actions` 只读核验
- Approach: 逐条核对需求 §0 与代码实际行为；把需求未覆盖、但实现路径必须回答的机制缺口登记为盘问队列（见文末「待裁决」区）。
- Acceptance:
  - [x] 需求 §0 的代码事实与仓库一致（补入视图元数据／csx 编译基座／写闸门、hooks 开放集合与排序语义、disposing 隐式规则及其订阅读面依赖、保留槽语义与唯一用途、卸载按 owner 扫表、csx 沙箱白名单、三层失败口径、单文件隔离、csx 懒编译、39 个信号常量含多段名、`UnitCard` 显示名登记，共 14 条）
  - [x] 每个实现步骤的裁决来源可追溯到某一轮 QUESTION 与父 agent 的 ANSWER（见文末「待裁决（盘问队列）」）
  - [x] 8 条非阻塞项转为「假设 + 替代方案」（A-1…A-8，准出时一次性审）
- Rationale: 需求 §0 是"效果装载"视角的基线；实现路径还依赖视图元数据校验、hooks 开放语义与排序、disposing 隐式规则、引擎级处理器四条事实，缺它们会误判迁移成本与行为变更面。

### S2: 内核实体模型（四类实体 + 预制体 + 成员 handler + 可见性）
- Status: In Progress
- Target: `src/Orc/Core`（新增实体／预制体／成员／可见性模型；不改判定器与资产服务）
- Approach（已裁决部分 — 成员＝有序边集合，R1i）：
  1. **成员执行＝有序边集合**：**自有边**（成员所属实体自己声明的，**固定最前**）→ **外来挂载边**（按声明序，R1g）→ **框架注销边**（R1f）。**不存在独立于边集合的 "core" 概念**——核心逻辑就是自有边。这条让"成员"与"边集合"成为同一个东西，包装才有着力点。
  2. **`band` / 分段概念退役**：成员内部不再有 `Rewrite`/`Veto`/`Apply` 这类次序枚举（现 `ActionTriggerBands`／`ActionHandleBands`／`DefaultBands` 随批次退役）。
  3. 包装见 S2a。
- Acceptance:
  - [ ] 模型里不存在"主动／被动"分类字段（R1）
  - [ ] 预制体可声明继承（`extends`），子预制体整段覆写父成员（R3a）
  - [ ] 可见性为位标志、默认全关、子只能收紧（R5）
  - [ ] 同一 handler 资产被 100 个预制体引用，全局只注册 1 份（R4）
  - [ ] 成员表可在装载期静态枚举（供 S4 的静态解析与 S10 的审查导出）
  - [ ] 成员执行序可由"自有边 → 外来边声明序 → 框架边"完整推出（R1i）
  - [ ] 模型里不存在 `band` 概念（R1i）
- Rationale: 成员与边集合合一，是把"核心逻辑"从特权位置降为"第一条自有边"；只有这样，"包装整个成员"才有唯一着力点，且 band 与相位不会变成两套并行的次序语言。
- 待裁决: 见文末队列。

### S2a: 包装通道（相位 + 接缝退役）
- Status: In Progress
- Target: `src/Orc/Core`（`WrapperPhase`／包装器注册面）、`src/Orc.Game`（减伤/免疫/反制的落点）、判定器包装面
- Approach（已裁决部分，R1i）：
  1. **包装的目标是成员**（包裹该成员的整体执行），**不是某一条边**；因此**不需要**"成员＋来源"的二级地址——寻址仍只用 `作用域#成员名`（S4）。
  2. **相位**：`before`（改写输入，**LIFO**）／`after`（尾部副作用，**FIFO**）／`replace`（替代整个边集合，**最后声明者生效**）。三者先后**一律按声明序**推出，不引入优先级数字。
  3. **接缝（seam）退役**：现 `Context.SeamAsync` 具名接缝与 `EventPrefab.Seams`／`WrapperPrefab.SeamName` 一并退役（`src/Orc/Cards/Prefabs.cs:369-377` 的"只有前置包装器可挂接缝"校验随之消失）。需要"在中间插入"＝拆成员（R3a）。
  4. **判定器包装器同属这一套**：相位语义相同，作用对象是判定器而非成员（现 `JudicatorWrapperPrefab`，`src/Orc/Cards/Prefabs.cs:298`）。
  5. **中止规则（必须写进文档与代码注释，最易各写一半的地方）**：成员执行中任一单元（自有边／外来边／`before` 包装器）调用 `ctx.Stop()` 时——
     - **本成员剩余单元全部不执行**（后续外来边、`after` 包装器、框架注销边）；
     - **已执行的 `before` 包装器不回滚**，其写入保留在数据面上；
     - `replace` 若已接管，则不回退到原边集合；
     - 停止**不外溢**到调用方的其它成员（作用域＝当前成员，与现 `Context.Stop` 的"当前事件链"范围对齐）。
  6. **迁移规则（与 S11 同批）**：`injects + band: Rewrite` → `wrap: engine#<成员>, phase: before`（改写视图字段）；`Veto` → `before` + `ctx.Stop()`；`Apply` 段的外来注册项 → 外来挂载边。
- Acceptance:
  - [ ] 包装声明只能指向成员（`作用域#成员名`），不出现"成员＋来源"的二级地址（R1i）
  - [ ] `before` LIFO／`after` FIFO／`replace` 最后声明者生效，次序可仅由声明序推出（R1i）
  - [ ] 模型里不存在接缝（seam）概念（R1i）
  - [ ] 判定器包装器与成员包装器共用同一相位语义与同一注册面（R1i）
  - [ ] 免疫（`before` + `ctx.Stop()`）命中后，同成员的 `after` 包装器与后续边不执行，且该规则在实现注释与作者文档中均可见（R1i）
  - [ ] `ctx.Stop()` 的作用域限于当前成员，不外溢到调用方其它成员
- Rationale: band 与包装器在语义上重叠（都是"改变执行次序/内容"）；若两套并存，作者要理解两套次序语言，与"抽象更强"的目标相反。接缝退役是因为"成员＝边集合"之后，中间插入已无稳定锚点，拆成员是唯一不会退化成隐式优先级的做法。
- 待裁决: 见文末队列。

### S3: 信号（原 hooks → 引擎成员触发器）与总线退役
- Status: In Progress
- Target: `src/Orc/Core`（`Bus`、`HookRegistry`、`Updates` 调用面）、`src/Orc/Cards`（`Effect` disposing 面、`CardLifecycle`）、`src/Orc.Game`（`Match` 引擎实体装配、`GameUpdates` 发射点）
- Approach（已裁决部分）：
  1. **`hooks` 字段从预制体中删除**：触发器不再声明"订阅哪些更新字符串"。原 hook 名映射为引擎实体的成员触发器（`card.load` → `engine.card-load`）。
  2. **`Emit(updateType, payload)` 语义改为"调用该成员触发器"**：`updateType` 不再作为开放字符串路由键，而是解析到 `engine.<signal>` 成员；payload 进入该成员调用的数据面。
  3. **"订阅"＝挂载**：卡牌／组件／引擎成员要响应某时机，就在自己的装配声明里挂到 `engine.<signal>` 上；模型回到 R1b 的两条通道（挂载／包装）。
  4. **执行序的唯一依据（R1g）**：同一宿主成员上的挂载项按**声明序**（宿主预制体里组件的声明顺序 + 组件内成员／挂载的声明顺序，**静态**，与装配批次无关）；运行时注册的信号上的边按**追加序**（建边先后）。**"装载序"不再作为排序依据**。`Bus.SnapshotFor` 的 `priority → 注册序` 排序（`Bus.cs:258-263`）随总线一并退役，**不引入任何优先级数字**。
  4b. **边表必须支持有序插入**：`placed` 批的边若其组件声明序更靠前，要插到已存在的 `loaded` 边之前——只支持追加的实现不满足 R1g。重建实例（复装）产生的边按新实例的声明序**回填原位**，不追加到末尾。
  4c. **无意义挂载＝装载期拒绝**：`active-when: placed` 的组件挂到加载期信号（如 `engine#card.load`）——信号触发时它还没装配、永不生效，属配置错误，装载期拒绝。
  4d. **注册与挂载是两个动作、同一门面**（R1h）：注册＝创建新的可寻址触发器（信号）；挂载＝往已有触发器上挂一条边。二者不合并（作用于不同对象）。
  4e. **允许往已声明的信号上运行期挂边**；其顺序来源＝`appended`（追加末尾）。`order-source` 是**边**的属性，不是信号或实体的属性——同一信号上可以既有 `declared` 边又有 `appended` 边。
  4f. **运行期边必须绑 owner**，随 owner 销毁撤销；**不存在无主永久边**。沙盒那条运行期边以"沙盒会话"为 owner（`src/Orc.Game/Sandbox/SandboxPrimitives.cs:959`）。
  5. **运行时注册**是唯一允许动态进入全局可寻址集合的入口：注册后该触发器可被寻址、可被后续挂载声明引用；引用**未注册**触发器＝装载期明确错误并指出名字，删除 `MatchNamedTriggers` 式的按名回退（`Match.cs:673-684`、`EffectSystem.cs:496-501`）。
  6. **静态可解析约束**：除运行时注册外，`engine` / `host` / `self` 三作用域都必须在预制体装载期静态解析（不接受运行期查表）；因此装载顺序固定为「先建全部静态成员表 → 再解析挂载声明」，运行时注册发生在该表建立**之后**、且只能**追加**入口。
  7. **分域**：内核基础预制体声明内核 4 个信号，游戏层 `extends` 后追加 35 个；两者同处 `engine` 根下，寻址解析**跨层合并**成员表（来源层对解析无差别，但审查面要标出来源层）。
  8. **自定义更新名不再允许"任意字符串"**：`custom.placed`／`u` 这类名字一律走**运行时注册**进入 `engine` 命名空间，注册后才可被挂载声明引用；沙盒负责"先注册信号、再装载资产"的顺序；`SandboxPrimitives` 的运行期建触发器改走注册 API（`src/Orc.Game/Sandbox/SandboxPrimitives.cs:959`）。
  9. **审查面**：`engine#<signal>` 成员的"来源边"（谁挂载在它上面、来自哪个实体／组件、**位次**、**顺序来源 `order-source: declared | appended`**、成员来自内核还是游戏层）是一等导出项（R7 要求能回答"谁订阅了某个信号"）。`order-source` 缺失则 R1g 的第 3／5 条验收无法验。
- Acceptance:
  - [ ] 模型里不存在 `hooks` 字段（R1e）
  - [ ] 所有内建时机（`card.load`／`card.destroyed`／`card.placed`／`effect.removed` 等）都是引擎实体的成员触发器（R1e）
  - [ ] 运行时可注册新触发器，注册后立即可被挂载声明引用（R1e）
  - [ ] 引用未注册触发器＝装载期错误并指出作用域与成员名（R1e）
  - [ ] 同一时机上多个订阅者的执行序可仅凭声明序／追加序推出，无隐藏优先级（R1e／R1g／R1）
  - [ ] 边表支持有序插入：`placed` 批的边可插到已存在 `loaded` 边之前（R1g）
  - [ ] 重建实例的边按声明序回填原位，不追加到末尾（R1g）
  - [ ] `active-when: placed` 的组件挂到加载期信号＝装载期拒绝（R1g）
  - [ ] 注册与挂载是两个独立动作（同一门面、不同对象），代码中不存在"注册即挂载"的合并入口（R1h）
  - [ ] 同一信号上可同时存在 `declared` 与 `appended` 边，且 `order-source` 按边标注（R1h）
  - [ ] 运行期挂的边全部绑 owner；owner 销毁后该边消失；不存在无主边（R1h）
  - [ ] 自定义更新名只能经运行时注册进入 `engine` 命名空间，代码中不存在"任意字符串直接 Emit"的路径（R1e）
  - [ ] 审查面能反查"某信号上有哪些来源边"，并标出该信号成员来自内核还是游戏层（R7）
  - [ ] `Bus`／`HookRegistry` 的订阅读面被 S10 的审查面等价替代，不留两套订阅读面
- Rationale: `hooks` 若保留为独立字段，模型实际是三通道，与 R1b 的两通道二分及 R7 的边图直接矛盾（第 1 轮 QUESTION 已陈明）；用户裁定"原 hook 全部改为引擎的成员触发器"，并补运行时注册作为原"必须走引用查询"路径的替代。执行序按**声明序／追加序**（R1g）为 R1「无隐藏优先级」的必然结果。
- 待裁决: 其余机制见文末「待裁决」表。

### S4: 挂载与成员寻址（装载期解析与校验）
- Status: In Progress
- Target: `src/Orc/Core`（挂载声明解析、可见性校验、fail-fast 报告）
- Approach（已裁决部分 — 寻址语法）：
  1. **语法＝`作用域#成员名`**：第一个 `#` 之前是作用域标识（`engine` / `host` / `self`），之后**整体**是成员名；**成员名允许含 `.`**。
  2. **解析按第一个 `#` 切分，不按固定段数、不按 `.` 再切**。成员名零转换：`hooks: ["unit.position.changed"]` → `engine#unit.position.changed`（无损、可逆）。
  3. **跨层合并成员表**：`engine` 根下同时有内核基础预制体声明的成员与游戏层 `extends` 追加的成员（`engine#card.destroyed` 属内核、`engine#card.damaged` 属游戏层），解析对来源层无差别。
  4. 旧示例 `engine.damage-apply#on-before` **作废**（R1d 已修正）；实现与文档一律不得按"`.` 分层 + 固定段数"解析。
  5. **`host#` 的静态解析依据＝组件预制体的 `requires-host`**：解析器先在"该组件允许的全部宿主类型"的类型预制体成员表里取交集校验；成员不在某个允许类型的表里＝装载期拒绝。因此"同一组件既挂单位卡又挂 HQ"只在引用共有成员时成立（R3h）。
  6. **`self#`** 解析到组件实体自身预制体的成员表；自挂载不受可见性约束（R5 补充），但仍受"目标成员存在"约束。
- Acceptance:
  - [ ] 挂载声明只含 `作用域#成员名`，不含显示名（R1d）
  - [ ] 成员名含 `.` 时可正确解析；实现在第一个 `#` 处切分（R1d）
  - [ ] 目标成员不存在／未声明 `可被挂载` ＝装载期报错并指出作用域与成员名（R1d／R5）
  - [ ] 子视图字段集 ⊄ 宿主视图字段集 ∪ 子实体自有成员 ＝拒绝挂载并报告缺失字段名（R1c）
  - [ ] 挂载声明中出现 `replace` 语义 ＝装载期拒绝（R1b）
  - [ ] `engine`／`host`／`self` 三根在装载期完成静态解析，运行期只剩运行时注册这一条动态入口（R1e）
  - [ ] 跨层成员在同一 `engine` 根下合并解析（内核 4 + 游戏层 35）（R1e）
  - [ ] `host#<member>` 的存在性按组件 `requires-host` 允许的全部宿主类型成员表校验（R3h）
  - [ ] `self#<member>` 解析到组件自身成员表，自挂载不受可见性约束（R5 补充）
  - [ ] 包装声明与挂载声明共用同一套 `作用域#成员名` 寻址，不出现二级地址（R1i）
- Rationale: 寻址必须无损可逆，否则迁移器无法机械转换、审查面也无法把边反查回声明；`作用域#成员名` 让"隔离符"只剩一个，成员名内部可自由使用 `.`。
- 待裁决: W-10（`host`／`self` 的解析目标与卡牌成员表的声明载体）。

### S5: 上下文视图投影（ctxViewer + 封闭类型集）
- Status: In Progress
- Target: `src/Orc/Core`（视图声明→投影；替代 `ViewMeta`／`ViewProxyFactory` 的"C# 视图类"路径）、`src/Orc.Script`（csx 求值器契约）、`src/Orc/Cards`（handler 委托形态）
- Approach（已裁决部分）：
  1. **R1a 的作用域**：「不新增任何 C# 类型」＝**不为每个数据形状新增类**；允许且仅允许**一个通用「承载视图」类型**承接全部形状。该类型是唯一的视图运行期代表。
  2. **csx 入口签名统一**为 `Func<dynamic, Context, CancellationToken, Task> HandleAsync = …`。内核侧 handler 的委托形态相应变为 `Func<object, Context, CancellationToken, Task>`（`dynamic` 在委托类型参数位置擦除为 `object`）。
  3. **`ScriptRequest.ViewType` 退化为"承载视图"这一个类型**（不再是"资产声明的形状"）：缓存键随之退化为「承载视图类型 + 入口名 + 超时 + 源码哈希」（现为 `ViewType|EntryName|TimeoutMs|源码哈希`，`Orc.Script/CSharpScriptEvaluator.cs:42`），不再按形状分桶。
  4. **签名校验的新口径（不得按名称/类型逐字相等）**：校验"形状 + 首参可接收性"——委托须为四参（首参、`Context`、`CancellationToken`）且返回 `Task`；首参类型须能**接收承载视图实例**（`IsAssignableFrom(承载视图)`），`dynamic` 擦除后即 `object`。**不得**要求 csx 头部类型与资产声明逐字相等：`Func<T,…>` 对 `T` 逆变，`Func<旧视图,…>` 在类型层面可赋给 `Func<object,…>`，但运行期以承载视图调用会类型不匹配——所以判据是"首参能否接收承载视图"，而不是"类型名是否相同"。
  5. **`Microsoft.CSharp` 进入 `Orc.Script` 的 `AllowedAssemblies`**（`Orc.Script/CSharpScriptSecurityOptions.cs:12-22`）。判据（记录，避免以后被当作安全回退）：它只提供 DLR 绑定器，不扩大类型可见面——白名单内的 `Orc`／`Orc.Game` 类型本来就能直接访问公开成员；关键字过滤是文本级过滤，`dynamic` 不绕过它；因此实际增量只是"编译期成员检查"，而这项检查已按 R1a 换成**静态语法检查**，不是新增风险面。
  6. **承载视图的名字空间＝数据面（仅视图声明字段）**，**不含**组件私有字段（W-02 原表述"投影到数据面或组件私有字段"已按 R1c + R3e 收紧作废）。
     6b. 私有字段唯一通道＝`owner.<Name>`；csx 里 `owner` 也是动态访问。写 `view.ammo = 3`（`ammo` 是私有字段）＝`binding` 失败，作者必须改写 `owner.ammo`。
     6c. **同名禁止**：视图字段名与同一实体的私有字段名不得相同＝装载期拒绝（技术上可区分，但两条通道指向同名状态会让读者误判，代价大于收益）。
     6d. **静态检查同时覆盖两条通道**：`view.<Name>` 比对视图声明；`owner.<Name>` 比对「组件私有字段声明 ∪ 实体公开属性」（如 `Host`、`Name`）。**两边未声明都判 `binding`**。
     6e. 因此 R1c 的"写回宿主数据面、对后续步骤可见"**不受影响**——写回只发生在 `view.<Name>` 这条通道上；私有字段的读写不产生数据面写回。
  7. **转换语义不变**：字段值是真实实体对象，`view.Host is Orc.Cards.Card` 一类判断照旧成立（现 `CardEventView` 字段为 `object?`，`src/Orc/Cards/CardLifecycle.cs:10-68`）。
  8. **双闸门**：①**装载期静态语法检查**（**用 Roslyn 语法树，不做正则/字符串匹配**；`Orc.Script` 本就要解析 csx，解析成本不增加）——把 `view.<Name>` 的读/写与视图声明比对，未声明字段＝报错、写只读字段＝拒绝；②**运行期写入闸门**在承载视图的动态 setter（兜底防线）。
  8b. **"算写"的判定口径**（唯一契约，执行方按此实现，不得扩大也不得缩小）：
      - 算写：直接赋值、`+=`／`-=`／`++`／`--`、作为 `ref`／`out` 实参。
      - 不算：注释、字符串字面量（语法树天然排除）。
      - 别名：只追踪**直接**别名（`var v = view; v.X = …`）；**不追踪**经方法或局部函数传递的 `view`。
  8c. **未识别形态＝放行 + 运行期闸门 + 审查标注**（判据：误判代价＝整包拒绝（W-17），漏判代价＝运行期闸门生效；宁可放行留痕，不可误杀）。审查面必须能列出"本包内存在 N 处未经静态验证的 `view` 传递"，让作者知道哪里的保护是概率性的（见 S10）。
  8d. **错误分类新增 `binding`**（静态检查失败），与 `compile`／`sandbox` 分开——三者指向完全不同的修复动作。`ErrorCategory` 的取值集合随之扩为 `binding`／`compile`／`signature`／`sandbox`／`timeout`／`runtime`（`src/Orc/Core/Scripting.cs:43-64`）。
  8e. **这套语法子集约束要写进卡牌开发文档（作者可见）**——否则作者不知道 `view` 不能交给 helper 去写。文档位置在 S15 一并交代。
  9. **csx 在预制体装载期全量预编译，不保留懒编译**；编译失败＝拒绝装载／拒绝注册（fail-fast），**不降级**为"隔离记录 + 成员缺失"。判据：R6 的「漏挂不可能发生」要求装载完成时"该能跑的都能跑"，懒编译把失败推到首次执行，与之直接矛盾。落地含义：现 `ResolveHandler` 的懒解析（`src/Orc/Cards/PrefabManager.cs:108-125`）不再是首个编译点，`RegisterPrefab`／`LoadDirectory` 装载期就要编译全部 csx；缓存键含源码哈希（`Orc.Script/CSharpScriptEvaluator.cs:23`／`:42`），进程内复用不受影响。这一条**推翻**现三层失败口径中的"运行时装载层＝隔离"（`src/Orc.Game/Cards/EffectSystem.cs:20-21`）。
  9b. **运行时注册**同理：编译失败＝注册失败并报 `ErrorCategory`，注册失败后不得留下半个可寻址成员（R1e）。
  10. **接受的代价**（必须写进文档与错误消息）：字段名拼错的防线从"编译期"降到"装载期静态语法检查"；且"经方法/局部函数传递的 `view`"不在检查范围内，那一类保护是概率性的。
  11. **迁移器**：只需机械改写 csx 头部 `Func<…View,` → `Func<dynamic,`，正文不动（samples 内 24 份 csx）。
- Acceptance:
  - [ ] 全仓只有一个承载视图类型，不存在"每形状一个 C# 类"（R1a 作用域澄清）
  - [ ] csx 入口签名统一为 `Func<dynamic, Context, CancellationToken, Task>`（R1a）
  - [ ] 签名校验按"形状 + 首参可接收性"，不按类型逐字相等；旧式 `Func<CardEventView,…>` 一类头部按可接收性判定而非名字（R1a）
  - [ ] `ScriptRequest.ViewType` 恒为承载视图类型，缓存键不再含形状维度（R1a）
  - [ ] 视图字段类型超出封闭内建集＝预制体装载期拒绝（R1a）
  - [ ] 必填字段：**可静态判定的部分**（字段名、类型属封闭集、`requires-host` 覆盖）在装载期 fail-fast；**数据面值缺失**在首次执行时 fail-fast（数据面是运行期产物，装载期不存在该值）（R1a + 父 agent 第 17 轮判定）
  - [ ] 嵌套结构不可表达（显式拒绝，非静默降级）（R1a）
  - [ ] 静态语法检查（Roslyn 语法树）能拒绝"未声明字段"与"写只读字段"，报告指出字段名与精确位置（R1a／R1c）
  - [ ] "算写"的判定恰为五类（直接赋值／`+=`／`-=`／`++`／`--`／`ref`·`out` 实参），不扩大不缩小（R1a）
  - [ ] 注释与字符串字面量不参与比对；直接别名被追踪，经方法/局部函数传递的 `view` 被放行（R1a）
  - [ ] 放行的"未经静态验证的 `view` 传递"能被审查面计数并列出（R1a／R7）
  - [ ] 静态检查失败的错误分类为 `binding`，与 `compile`／`sandbox` 可区分（R1a）
  - [ ] 视图的名字空间只含视图声明字段；`view.<私有字段>`＝`binding` 失败（R1c／R3e）
  - [ ] 私有字段只能经 `owner.<Name>` 访问；`owner.<未声明>`＝`binding` 失败（R3e）
  - [ ] 视图字段名与同实体私有字段名相同＝装载期拒绝（R3e）
  - [ ] 写回只发生在 `view.<Name>` 通道；`owner.<Name>` 的写入不产生数据面写回（R1c）
  - [ ] 运行期 setter 闸门对绕过静态检查的写入仍失败（R1c）
  - [ ] csx 在预制体装载期完成编译，代码中不存在"首次执行才编译"的路径（R1a）
  - [ ] csx 编译失败＝拒绝装载／拒绝注册，代码中不存在"编译失败→隔离记录→继续装载"的路径（R1a）
  - [ ] 运行时注册失败后不残留可寻址成员，错误码沿用 `ErrorCategory` 词汇（R1e）
  - [ ] 视图内建 `Owner` 由框架回填、不来自数据面（R4）
  - [ ] `view.Host is Orc.Cards.Card` 一类转换在新承载视图下行为与今天一致
- Rationale: 单一承载视图使"声明视图不新增 C# 类"与"csx 仍可写 `view.X`"同时成立；`dynamic` 的绑定器程序集不扩大类型可见面，故放行它只换掉了"编译期成员检查"这一层（R1a 已把该层换成静态语法检查）。签名校验改为"首参可接收性"而非类型逐字相等，是为了让 151 份资产的头部改写成为纯机械操作而不是逐份重编译对齐。静态检查必须建立在语法树上（`Orc.Script` 已持有 Roslyn 解析结果），因为"误判＝整包拒绝"（W-17）之下任何字面级匹配的假阳都会误杀可用卡包；而"漏判"已被限定为只影响概率性保护，并有运行期闸门与审查标注兜住。
- 待裁决: W-14（承载视图是否可落到组件私有字段）。

### S6: 数据面与写回契约
- Status: In Progress
- Target: `src/Orc/Core/Context.cs`（数据面）、写回路径
- Approach（已裁决部分）：
  1. **写回只发生在 `view.<Name>` 通道**，落到宿主数据面、对同链后续步骤可见（R1c）。`owner.<Name>` 的写入是实体状态写入，不产生数据面写回。
  2. **私有字段不进数据面**；公共变量不落组件私有字段（R3e）。视图字段名与同实体私有字段名相同＝装载期拒绝（S5 第 6c 条）。
  3. 经 `owner`／`owner.host` 的读取不受视图包含校验约束（R1c 定稿澄清）——它们是强类型引用上的读取，不是数据面投影。
- Acceptance:
  - [ ] 写回落宿主数据面、对同链后续步骤可见（R1c）
  - [ ] 写未声明为可写的字段＝失败（R1c／R3e）
  - [ ] 私有字段不进数据面；公共变量不落组件私有字段（R3e）
  - [ ] 经 `owner`／`owner.host` 的强类型读取不受视图包含校验约束（R1c 定稿澄清）
- Rationale: 待裁决（其余部分）。
- 待裁决: 数据面键的命名空间与生命周期（跨成员写回、嵌套执行的隔离）需在 S3 的成员模型定形后回填。

### S7: 引擎实体（原全局流程触发器迁入）
- Status: In Progress
- Target: `src/Orc`（内核基础预制体）、`src/Orc.Game`（`CommandManager` 7 处 + `CardLifecycle` 2 处）、`src/Orc.Foundation.Actions`（15 处）
- Approach（已裁决部分 — 成员名与 `injects` 映射，R1j）：
  1. **`injects` 的 4 个在用显示名 → 成员地址**：
     | 显示名 | 成员地址 | 现有 band | 迁移后形态 |
     | --- | --- | --- | --- |
     | `伤害处理触发器` | `engine#damage-apply` | `Rewrite`（4 处） | `wrap` + `phase: before`（改写视图字段） |
     | `单位攻击触发器` | `engine#unit-attack` | `Commit`（1 处） | `wrap` + `phase: after`（该流程结算之后执行） |
     | `行动额度触发器` | `engine#action-budget` | 无 `band`（1 处） | 外来挂载边（成员内按声明序） |
     | `指向触发器` | `host#targeting` | 无 `band`（1 处） | 外来挂载边（成员内按声明序） |
     `host#targeting` 用 `host#` 是因为卡与 HQ 各有该成员，按当前宿主实例解析天然消歧——**不需要**为此收窄 `requires-host`。
  2. **映射表与实现侧常量同源**：迁移器按"显示名 ↔ 成员名"常量表转换，**不硬编码**；加一条守护测试（常量集合 == 预制体成员集合）守漂移。
  3. **未被资产引用的具名触发器全部保留**：卡级 8 个（`预打出`／`打出`／`部署`／`部署词条`／`加入`／`单位化`／`老兵`／`揭示`）、引擎级 6 个（`指挥`／`单位移动`／`造成攻击伤害`／`对战伤害`／`效果伤害`／`战斗伤害`）——它们是 C# 侧直接调用的流程入口，保留为引擎／类型预制体成员并给出成员名。
- Acceptance:
  - [ ] 对局内不存在第二个引擎实体；跨实体挂载一律以 `engine#` 为根寻址（R6b）
  - [ ] 引擎初始化后其成员集合可由预制体完整枚举（R6b）
  - [ ] 新效果不写主触发器壳、不写 hooks 字符串即可生效（R6）
  - [ ] 初始化期不执行业务逻辑（R6）
  - [ ] 4 个在用显示名的映射与 R1j 表逐条一致；`host#targeting` 在卡与 HQ 上都可解析
  - [ ] 30 个保留成员（卡级 8 + 引擎级 6 + 内核 4 + 游戏层 35 中的既有流程项）在预制体中可枚举，且与 C# 常量同源（守护测试通过）
- Rationale: 映射以常量表承载而非迁移器硬编码，是因为成员名同时被"预制体声明""审查导出""C# 调用点"三处引用，任何一处独立命名都会漂移；守护测试把漂移变成红灯。
- 待裁决: 见文末队列。

### S8: 卡牌实体 + 组件实体（效果降为组件成员集）
- Status: In Progress
- Target: `src/Orc/Cards`（`Effect`／`DynamicEffect`／`CardLifecycle`／`Card`）、`src/Orc.Game`（`UnitCard` 7 处、`Hq`、`EffectSystem`）
- Approach（已裁决部分 — 类型预制体 / 宿主需求 / 可见性范围）：
  1. **卡牌成员表由类型预制体声明**：`card.base`（薄基类，只含 `Host`／`Name` 这类基础）→ `card.unit` / `card.order` / `card.counter` 各自声明成员。卡牌实例化时按**既有 `typeCategory` 数据组件**自动绑定类型预制体；**卡数据文件不新增 `extends`／`members`**。
  2. **不引入"移除成员"**：类型之间靠"各自声明 + 共享薄基类"区分，而不是"继承后删成员"（R3a 只定义覆写）。
  3. **`requires-host`**：组件预制体声明允许的宿主类型集合（`unit`/`order`/`counter`/`hq`/`any`）；其引用的 `host#<member>` 必须存在于**其允许的全部宿主类型**的成员表中；要引用某类型独有成员（如 `host#deploy`）＝必须把 `requires-host` 收窄到该类型（R3h）。
  4. **`self#<member>` 允许**：可见性**只约束跨实体挂载**；实体的初始化触发器装配自己的成员不受可见性约束（R5 补充）。
  5. **影响（必须写进实现记录）**：`UnitCard` 构造器里"新建 7 个触发器 + `RegisterNamedTrigger(显示名)`"（`src/Orc.Game/Cards/UnitCard.cs:60-82`）整套消失，改为读类型预制体的成员声明；卡牌实例化路径（现按 `CardCategory` 产出 `UnitCard`／`OrderCard`／`CounterCard`，`src/Orc.Game/Cards/CardCategory.cs:3-22`）与"绑定类型预制体"合流为同一步。
  6. **`typeCategory` 的绑定口径（须与既有解析规则一致，不得按"列表长度"判）**：`type` 列表是**类别词与单位类型词混排**——类别词 `order`→command、`countermeasure`→counter（至多一个、须独占列表），单位类型词 `infantry`/`tank`/`artillery`/`fighter`/`bomber` 可多个且不可重复；**无类别词 ⇒ 类别＝unit**（`src/Orc.Game/Cards/Data/Components/BuiltInCardComponents.cs:134-207`）。因此"多值"不等于"不唯一"：`["infantry","tank"]` 是合法的**单位卡**，只映射到 `card.unit`；真正的不唯一＝出现多个类别词（现解析器已抛 `FormatException`，"类别至多一个"）或出现未定义词。
  7. **实测分布（samples 全量，见 S8 验收）**：33 份卡数据全部为单元素 `type` 列表，0 缺失、0 多值；类别分布 unit 24（infantry 18／tank 5／artillery 1）、command 8、counter 1。
  8. **唯一性只按"类别词唯一"判定，不按列表长度**：`["infantry","tank"]` 合法（→ `card.unit`）；多个类别词／未定义词／**缺 `typeCategory`** ＝装载期拒绝。**不设默认类型预制体兜底**（实测 0 缺失，不为假想情况加兜底）。
  9. **C# 类退化为"绑定入口"，不是第二真源**：`new UnitCard(...)`／`OrderCard`／`CounterCard`／`Hq` 的**构造函数签名不变**，内部只负责"找到并绑定类型预制体"；成员表一律由类型预制体决定，卡牌类里不得再出现 `new Trigger<`。因此 20 处直接构造（`src/Orc.Game/Cards/CardLibrary.cs` 3 处 + 17 个测试文件）零改动。
  10. **显式映射表（4 条，唯一真源；新增类型必须同步这两处）**：
      | # | C# 绑定入口（唯一改动点 A） | 类型预制体（唯一改动点 B） | 绑定时机 |
      | --- | --- | --- | --- |
      | 1 | `UnitCard` 构造器 | `card.unit`（extends `card.base`） | 构造期 |
      | 2 | `OrderCard` 构造器 | `card.order`（extends `card.base`） | 构造期 |
      | 3 | `CounterCard` 构造器 | `card.counter`（extends `card.base`） | 构造期 |
      | 4 | `Hq` 构造器 | `card.hq`（extends `card.base`） | 构造期 |
      **新增一种卡牌类型＝改两处：新增一张类型预制体 + 在对应 C# 构造器里加一行绑定**（并经 `typeCategory` 词表加一个类别词/单位类型词，见第 8 条）。数据驱动路径（`CardLibrary.Instantiate`）与直接构造路径共用同一张表，两条路径的成员集合必须一致。
- Approach（已裁决部分 — 组件实例粒度与生命周期 R3i）：
  11. **同一 prefab 在同一宿主上至多一份实例**：卡数据重复引用同一组件 prefab id ＝装载期拒绝。需要两份就做两个 prefab；**本期不引入组件参数**（"带参数的组件实例"是另一个设计）。
  12. **构造与装配分离**（这是收敛现状两套时点的唯一办法）。时序固定为：
      1. 宿主基础构造（卡牌／HQ 实例就位）；
      2. **构造**组件实体：填宿主引用（`owner.host` 可用）、初始化私有字段；
      3. 判定 `active-when`；
      4. **装配**（仅当命中时点）：调用组件实体的**初始化触发器** → 按声明建立挂载边 → 建立 `framework:disposal` 边（S8 第 1 条）；
      5. 卸载：逐句柄撤销边 → 销毁组件实体（宿主离场／回手即到此）。
      因此 R6 的"实例化后调初始化触发器"收紧为：**初始化触发器在装配时点被调用**，不是构造后立即。
  13. **`active-when` ∈ {`loaded`, `placed`}，缺省 `placed`**；缺省值必须**在预制体 schema 层显式落成 `placed`**（预制体文本/dto 里能看到该值），**不得靠 C# 默认参数隐式决定**——否则"导出预制体"与"装载预制体"看到的是两种模型。`loaded` 用于"在手中即生效"的效果。
  14. **复装＝重建实例**：宿主离场/回手＝组件销毁；再次入场＝**新建组件实体**，私有字段复位。**不提供**"复用实例 + 重跑初始化"的路径（否则 R3e 的私有变量会带着上一轮的值）。
  15. **失败作用域上浮到包级**：组件装配失败＝宿主失败＝卡包失败（与 W-17 一致），不产生"少了某个组件的卡"。与"否决半可用成员"同一条原则。
  16. **术语消歧（R3j）**：卡数据 `components`＝**数据组件**（数据，不是实体、不挂载、不进成员表）；本次重构的"组件"＝**组件实体**。`typeCategory` 是数据组件，但它是组件实体的**绑定键**——两者在装载链上互相依赖，不是同一层；代码命名与文档不得混用。
  17. **初始化触发器的签名＝只有"定义视图"**（R1h 的可判定边界，靠 API 物理强制、不靠文档约束）：可读 = 宿主类型、卡数据声明的词条、预制体声明等**装配期不可变数据**；不可读 = 血量／场上单位／随机数／回合等**对局状态**，且不得产生副作用或发事件。因此"若宿主**当前拥有**某词条则挂 Y 边"必须走运行期 handler；"卡数据**声明了**某词条"可在初始化期装配。签名里不出现运行时视图类型，越界即编译不过。
- Approach（已裁决部分 — 组件注销兜底 R1f）：
  1. **隐式**：框架在组件实例化装配时自动把该组件的注销成员挂到 `engine.card-destroyed`，作者不声明、也不能声明；声明即重复（vacuously 无第二个入口）。
  2. **位次**：框架注销边 ≡ 该组件的**最后一个挂载项**。`engine#card.destroyed` 上先按组件**声明序**分段，每段内部顺序为「作者声明的销毁响应… → 框架注销边」。不引入任何优先级数字（沿用 R1／R1g「无隐藏优先级」）。
  3. **单一真源**：该规则只保留**一处**（预制体层，现 `EffectPrefab.NormalizeDisposingTrigger`，`src/Orc/Cards/Prefabs.cs:491-541`）；框架合成边从这一处出发。现 `Effect.NormalizeDisposalHooks`（`src/Orc/Cards/Effect.cs:614-640`）与之重复、是漂移源，随旧类型在 S13 一次退役，不得先改成"另一份新实现"。
  4. **校验保留**：`effect.removed` 不得作为兜底信号，预制体装载期拒绝（防自触发递归），报告须指出组件与信号名。
  5. **审查可见**：该边以来源 `framework:disposal` 出席（与 `explicit`／`injected` 并列），见 S10；不得表达为"无来源"。
  6. **撤销按句柄、不按来源扫表**：装配期为每条边（含框架注销边）分配独立句柄，组件卸载时逐句柄精确撤销自己创建的那些边。**禁止**在 `engine.card-destroyed` 上做 `owner` 扫描清理——现 `Bus.UnmountOwner`（`src/Orc/Core/Bus.cs:88-124`）正是那种扫法，多组件并发卸载时无法区分"谁该撤哪条"，随总线一并退役。
  7. **与"保留槽"的语义差异（不是改名）**——三条必须写进实现注释，避免执行方误认为同一件事：
     - **载体不同**：保留槽是**触发器内部**的排序段（`Trigger.cs:247-267`）；框架注销边是**信号挂载表里的一条边**。前者作用域＝一个触发器的事件清单，后者作用域＝一个信号上的全部边。
     - **排序依据不同**：保留槽内部仍按 `priority` 升序＋注册序（`Trigger.cs:250`）；框架注销边的位次由"组件声明序 + 组件内部位于作者边之后"唯一确定，**无任何优先级数字**（R1f／R1g）。
     - **层级塌缩**：今天是两层（信号 → 订阅的触发器 → 其内部事件），保留槽只在第二层"恒最后"；新模型塌缩为一层（信号 → 边），因此"恒最后"必须重新表述为"该组件的最后一条边"，而不是"该触发器的最后一个事件"。
  8. **判定"这条边是否适用于本载荷"不是一条边**：现 disposing 触发器的守卫事件（`Effect.cs:84`、`:548-557`）承担的是 R1 里的「合法性检验」，属触发器的嵌入逻辑，不得平移成一条排在队首的边；否则信号上的边序将同时由"检验"与"编排"两种东西占据。
- Approach（已裁决部分 — 一次性组件 `one-shot`，R3k）：
  18. **触发点**：该组件的**任一成员首次执行完成后**注销。`one-shot` 作用于**整个组件实例**，不绑定某个成员（与现"主触发器触发一次"的语义差异必须写进注释）。
  19. **注销者＝框架**：框架在成员执行外围合成"检查 + 注销"，作者不写注销调用；审查标注来源 `framework:one-shot`（与 `framework:disposal` 并列）。
  20. **计数状态＝实体内置状态**，不占作者私有字段（作者的 R3e 私有字段与框架的一次性位互不干扰）。
  21. **重入安全**：执行**前**检查、执行**后**立即置位——同一执行链内连续两次命中，第二次不执行。
  22. **复装＝复位**：重建实例即重新可用（与 R3i 一致：一次性随实例生命周期，不跨实例持久）。
  23. **无触发点即拒绝**：`one-shot` 组件若没有任何外部可触发的边（未挂任何信号、且不可被调用）＝装载期拒绝——它永不执行也就永不注销。这条替代现 `ValidateForUse` 的"无主触发器即拒绝"（`src/Orc/Cards/Effect.cs:484-491`）。
- Acceptance:
  - [ ] 一张卡可携带多个组件，互不干扰（R3c）
  - [ ] 卡牌预制体不含"组件挂在哪个触发点"的信息（R3c）
  - [ ] 组件注销不依赖宿主遍历其成员表（R3c）
  - [ ] 组件初始化时可安全读取宿主身份／基础数据；卡牌与 HQ 装配路径同一条（R3d）
  - [ ] 非可组合实体声明组件＝装载期拒绝（R3d）
  - [ ] 单位卡有 `deploy` 成员、指令卡没有，且指令卡装载期不报错（R3g）
  - [ ] 卡数据文件不需要写 `extends`／`members` 段（R3g）
  - [ ] `typeCategory` 无法唯一确定类型预制体＝装载期拒绝（R3g）
  - [ ] `["infantry","tank"]` 这类多单位类型**不**被判为"不唯一"，映射到 `card.unit`（R3g 第 6 条口径）
  - [ ] 缺 `typeCategory` ＝装载期拒绝，且不存在默认类型预制体回退路径（R3g）
  - [ ] 卡牌类与 `Hq` 源码中不存在 `new Trigger<`（C# 类不决定成员表）（第 9 条）
  - [ ] 映射表 4 条与代码一致；两条实例化路径（数据驱动 / 直接构造）产出的成员集合相同
  - [ ] 组件引用 `host#deploy` 却声明 `requires-host: any`＝装载期拒绝（R3h）
  - [ ] 同一组件同时挂单位卡与 HQ、只引用两者共有成员时装载通过（R3h）
  - [ ] `self#` 自挂载不受可见性约束；跨实体挂载仍受 R5 约束（R5 补充）
  - [ ] 组件的挂载边在装配时按声明序建立，注销时成组撤销（不再有 `Bus.UnmountOwner` 的 owner 扫表，`Bus.cs:88-124`）
  - [ ] 未显式声明的组件同样会在宿主销毁时注销（R1f）
  - [ ] 同信号上作者边先于本组件的框架注销边执行，且次序可仅由声明序推出（R1f／R1g）
  - [ ] 任何组件卸载后，其在 `engine.card-destroyed` 上的边全部撤销（R1f）
  - [ ] 组件卸载走逐句柄撤销，代码中不存在"按来源扫描信号边表"的清理路径（R1f + 第 3 轮约束 2）
  - [ ] 组件预制体把 `effect.removed` 声明为兜底信号＝装载期拒绝（R1f）
  - [ ] 兜底规则在代码中只有一处实现（预制体层），`Effect.NormalizeDisposalHooks` 不再有等价逻辑的第二份
  - [ ] 卡数据重复引用同一组件 prefab id ＝装载期拒绝（R3i）
  - [ ] `active-when` 缺省为 `placed`，且该缺省在预制体文本/dto 中可读到（不靠 C# 默认参数）（R3i）
  - [ ] `loaded` 的组件在宿主加载后即装配、`placed` 的组件在入场后才装配；两者不会同时装配同一实例（R3i）
  - [ ] 复装＝新建组件实体、私有字段复位；代码中不存在"复用实例 + 重跑初始化"的路径（R3i）
  - [ ] 组件装配失败＝卡包失败，不存在"卡进对局但缺某组件"的中间态（R3i）
  - [ ] 数据组件与组件实体在类名/字段名/文档中不混用（R3j）
  - [ ] `one-shot` 组件在任一成员首次执行完成后注销；第二次命中不执行（含同一执行链内连续命中）（R3k）
  - [ ] 一次性位是实体内置状态，不占用作者私有字段；重建实例后复位（R3k）
  - [ ] 审查面把该注销标注为 `framework:one-shot`（与 `framework:disposal` 并列）（R3k）
  - [ ] `one-shot` 组件无任何外部可触发边＝装载期拒绝（R3k）
  - [ ] 初始化触发器的签名只含定义视图；代码无法从初始化路径读血量/场上状态/随机数/回合（R1h）
  - [ ] 初始化期不产生副作用、不发事件（R6／R1h）
- Rationale: 隐式兜底避免"漏写注销只在运行期泄漏"（R6 否决"可覆写装配"的同一理据）；位次钉死为"组件最后一个挂载项"使节点内次序可仅由**声明序**推出，从而不与 R1／R1g 的"无隐藏优先级"冲突；来源标注 `framework:disposal` 是 R7「反查某宿主成员上挂了哪些来源」可答全的前提；按句柄撤销而非按 owner 扫表，是多组件在同一信号上并发卸载时唯一不串味的撤法。
- 需求侧事项（父 agent 处理，我不改 `requirements.md`）：R3f 已被标为**废止占位**，本文一律以 R1f 为准；R1f 与本文并集口径一致（含"只保留预制体层一处实现"这条实现提示）。
- 待裁决: 无（组件实例粒度与一次性均已有裁决：W-05／W-13）。

### S9: targeter 并入判定器体系（提交通道 + random 归引擎）
- Status: In Progress
- Target: `src/Orc/Core`（判定器注册／解析／moding 面）、`src/Orc.Game`（`TargeterManager`、`EffectRuntime.SelectAsync`／`BeginTargetSelectionAsync`、`EffectTargetResolveJudicator`）、`src/Orc.Ai`、`src/Orc.Mcp`
- Approach（假设 A-5／A-6）：把裁决语义与取样语义拆开——确定性（`all`/`self`）与引擎随机（`random`）留判定器、不产生 targeter；`choose` 走提交通道（`EffectRuntime.SelectAsync` 改为提交式）；`BeginTargetSelectionAsync` 死代码删除；`random` 的开关落在对局配置（默认禁用）。
- Acceptance:
  - [ ] 引擎内不存在"跳过提交通道直接得到裁定"的路径（R2）
  - [ ] 无前端接入时，需要裁定的流程显式挂起／失败，不静默降级（R2）
  - [ ] 死代码路径（`BeginTargetSelectionAsync`）被收敛为唯一入口，不保留第三条选择路径（R2a）
  - [ ] 生产对局执行到 `random` 选择器＝明确错误，不落"取首个"兜底（R2c）
  - [ ] 测试／沙盒／简单 AI 场景可显式开启随机（R2c）
  - [ ] `Orc.Ai` 实现为前端（拿候选、评分、提交），非引擎内决策器（R2）
- Rationale: 三条选择路径里有一条是死代码、一条绕过了"必须交给前端"（R2a 现状诊断）；只有把"取样"与"裁定"分开，才可能同时满足"引擎不代选"与"确定性选择不产生往返"。
- 待裁决: 无（`random` 配置承载按 A-5 实现，准出时审）。

### S10: 审查与诊断面（实体—成员—挂载边图）
- Status: In Progress
- Target: `src/Orc/Core/Orchestration.cs`、`src/Orc/Output/AuditChainJson.cs`／`AuditChainText.cs`
- Approach（假设 A-4）：沿用 `AuditChain` JSON 形态、`schemaVersion` 升 2、新增 `entities`／`members`／`edges`／`wraps` 四集合。硬要求：
  1. 必须能表达"谁订阅了某个信号"这条边（见 S3 第 9 条）。
  2. 边的**来源**是一等枚举：`explicit`（作者声明）／`injected`（元效果注入）／`framework:disposal`（R1f）／`framework:one-shot`（R3k）；**不得**把框架合成边表达成"无来源"。
  3. 每条边带 `order-source: declared | appended` 与位次（R1g／R1h），能据此推出执行序。
- Acceptance:
  - [ ] 可导出"某实体有哪些成员、各自挂在哪"（R7）
  - [ ] 可反查"某宿主成员上挂了哪些来源实体／组件"（R7）
  - [ ] 导出只读、可反复调用、无副作用（R7）
  - [ ] 能列出"本对局用到的随机选择器"（R2c／R7）
  - [ ] 能力不退化：现 `AuditChain.Hooks` 的订阅读面（`Orchestration.cs:113-123`）有等价替代项，且"未解析下游＝图缺口"的呈现保留
  - [ ] 审查图能列出全部 `framework:disposal` 边（R3f）
  - [ ] 边来源枚举闭合：任何边都能被归入四类之一（`explicit`／`injected`／`framework:disposal`／`framework:one-shot`），不存在 `unknown` / 空来源
  - [ ] 能列出"本包内存在 N 处未经静态验证的 `view` 传递"（S5 第 8c 条）
  - [ ] 能导出成员上的包装关系：谁包装了谁、相位、声明序位次（R1i／R7）
  - [ ] 能列出"哪些可选包被跳过及其失败项"（S14）
  - [ ] 每条边导出含 `order-source: declared | appended` 与位次，能据此推出实际执行序（R1g／R1h）
  - [ ] `order-source` 是**边级**字段：同一信号上混有 `declared` 与 `appended` 时逐边可辨（R1h）
  - [ ] 能读出每个视图字段落在哪一层（数据面 / 组件私有字段），以及某私有字段被哪些成员访问（R3e／S5 第 6 条）
- Rationale: R7 要求"不退化"且能回答"谁订阅了某个信号"；而这类边的丢失正是原 `inject-then-hook` 链条上最容易断的一环，所以来源与顺序来源必须是导出的一等字段，而不是注释式说明。
- 待裁决: 无（导出 schema 版本策略按 A-4 实现，准出时审）。

### S11: 资产 schema 迁移（新 schema + 迁移器）
- Status: Todo
- Target: `samples/**`（含卡内 `inline` 效果）、`src/Orc/Cards/PrefabJson.cs`、`src/Orc.Game/EffectParsing/**`（`EffectTemplate`／`EffectCompilationDriver`）
- Approach（已裁决部分）：`hooks: [X]` → 挂载声明 `engine#` + X 的**单条前缀替换**（机械、无损、可逆；例：`["card.load"]` → `engine#card.load`、`["unit.position.changed"]` → `engine#unit.position.changed`）。csx 头部机械改写 `Func<…View,` → `Func<dynamic,`（正文不动）。
  1b. **包装类声明的转换（R1i，第三类迁移）**：`wrappers[]`（目标＝本效果内事件 id）与 `judicatorWrappers[]` 一并转为新包装声明（目标＝成员/判定器 + 相位 + 声明序），`SeamName` 字段丢弃（接缝退役）；`injects[]` 里 `band: "Rewrite"` → `wrap: <目标成员>, phase: before`（改写视图字段）、`band: "Veto"` → `before` + `ctx.Stop()`、`band: "Apply"` → 外来挂载边。
  1c. **`injects[].target` 显示名 → 成员地址**按 S7 的常量表转换（`伤害处理触发器`→`engine#damage-apply` 等 4 条）；`host#targeting` 保留 `host#` 作用域。
  1d. **`band` 取值不全在 R1i 的三条规则内**：实测 7 处 `injects` ＝ 4 处 `Rewrite` + 1 处 `Commit` + 2 处无 `band`。转换规则：`Rewrite`→`wrap/before`；**`Commit`→`wrap/after`**；**无 `band`→外来挂载边**（旧默认段在成员内按声明序）。
  1e. **`oneShot` 字段原样保留**（语义改由 R3k 承载，无需资产侧改写）；`disposingTrigger` 段删除（R1f 的框架边取代之），其内现有事件转成该组件的**作者销毁响应边**（位于框架注销边之前）。
- Acceptance:
  - [ ] 迁移器可把 `samples/` 下全部效果／触发器 JSON（含卡内 `inline`）转为新 schema，产物可装载（R8）
  - [ ] 新 schema 不做旧格式兼容读取（R8）
  - [ ] 迁移后无残留 `viewType` 字符串、无 `injects[].target` 显示名、无 `hooks` 字段、无 `band` 字段、无 `seam` 字段
  - [ ] csx 头部改写后正文零差异（逐字节比对除首行签名外不变），且改写产物通过 S5 的装载期静态语法检查
  - [ ] `hooks`→`engine#<X>` 为纯前缀替换；**反向校验**：去掉 `engine#` 后得到的字符串必须能在旧资产中作为原 `hooks` 项找到（迁移器自带该自检，逐条通过）
  - [ ] `samples/` 里 4 处 `Rewrite` + 1 处 `Commit` + 2 处无 `band` 的注入迁移后行为等价（R1i／R1j，逐条对照现测试）
  - [ ] 迁移器不含硬编码映射表；显示名↔成员名常量与实现侧同源，守护测试通过（R1j）
- Rationale: 迁移必须"机械 + 可逆"：`hooks` 是纯前缀替换、csx 只改头部，因此可用反向自检证明无损；而 `band`（`Rewrite`/`Commit`/无 band）承载的是行为语义，只能按 R1i／R1j 的规则逐类转换并用现测试逐条对照，不能用文本规则蒙混。
- 待裁决: 无（`disposingTrigger` 的转换按 1e 处理：R1f 已使兜底隐式，故不补显式挂载声明）。

### S12: `Orc.Protocol` 选择器消息同批改造
- Status: In Progress
- Target: `src/Orc.Protocol`（选择器消息）、`src/Orc.Client`／`src/Orc.Server`／`src/Orc.Mcp` 的消费点
- Approach（假设 A-7）：加性扩展——保留现有消息字段，新增"待决请求 id／提交结果"；与 S9 同批落地。
- Acceptance:
  - [ ] 选择器消息形态与新提交通道一致，前端契约同步（R8）
  - [ ] 消息形态变更与 S9 同批落地，不出现新旧并存
- Rationale: 前端已按现有消息对接（`docs/前端适配-后端变更-20261020.md` 记录了字段级变更史），破坏性重命名会把联调成本转嫁给 UI；加性扩展让前端可渐进接入。
- 待裁决: 无。

### S13: 旧类型退役
- Status: In Progress
- Target: `src/Orc`（`Trigger<TView>`、`Effect`、`EffectPrefab`、`EventPrefab`、`TriggerPrefab`、`ViewMeta`／`ViewProxyFactory`、`Bus`／`HookRegistry` 的订阅面、`Wrapper` 的接缝面）
- Approach：一次性删除，不留 Obsolete 壳（R8）；删除前确认 S11 已完成、无资产引用旧 schema。
- Acceptance:
  - [ ] 代码库中不存在 `Trigger<TView>`／`Effect`／`EffectPrefab`／`EventPrefab`／`TriggerPrefab` 残留（R8）
  - [ ] 代码库中不存在 `band`／`seam`／`hooks` 概念（R1i／R1e）
  - [ ] 无 Obsolete 适配层（R8）
- Rationale: 兼容壳会让"两套装配路径"长期并存，而 R6 否决可覆写初始化触发器的同一理据就是"漏挂/卸载漏撤不能成为合法状态"。
- 待裁决: 无。

### S14: 卡包装载门与分批验收门
- Status: In Progress
- Target: 卡包装配链（`src/Orc.Packages/CardPackAssembler.cs`、`SnapshotPackageActivator.cs`、`PrefabManager`）、`src/Orc.Game/Match/Match.cs`（装配段）、审查导出、六测试项目
- Approach（已裁决部分 — 卡包原子性 / 必需 vs 可选 / 报告契约）：
  1. **原子性**：一个卡包内任一资产装载失败＝**整包拒绝**。`PrefabLoadReport` 的"部分成功"语义取消，改为「全绿，或整包失败并列出全部失败项」（不是首个错误即停，要收集齐）。
     现状对照：`PrefabManager.LoadDirectory` 是单文件隔离并返回 `(loaded, failures)`（`src/Orc/Cards/PrefabManager.cs:73-106`）；`CardPackAssembler.Attach` 在 `report.Failures.Count > 0` 时抛 `InvalidDataException`（`src/Orc.Packages/CardPackAssembler.cs:35-43`）——后者已接近"整包拒绝"，但前者仍会**部分注册**成功项，必须先改成"先全量校验再统一注册"，否则失败包会留下半装载状态。
  2. **必需包 / 可选包**：卡包声明自己是必需还是可选。必需包失败＝对局不开局（初始化上抛）；可选包失败＝跳过该包、在报告中标明、对局照常开始。
     现状对照：对局装配段把包激活的 `Failures` 逐条写错误日志后**继续**（`src/Orc.Game/Match/Match.cs:641-646`，`MatchPackageActivationResult` 由 `SnapshotPackageActivator.cs:34-37` 产生）——即今天所有包都等价于"可选"。必需包需要在装配段新增"失败即中止开局"的路径，并把该结果作为对局初始化的一等失败（不是日志）。
  3. **"可选包被跳过"必须在审查面/诊断可见**：跳过的包要出现在审查导出与对局初始化报告里（包 id、失败项、被跳过这一事实）。理由：否则作者会以为包加载了，实际什么都没生效——这与 R7 的"能力不退化"是同一条要求。
  4. **csx 预编译接入点**：原子性要求"先全量校验再注册"，而 csx 全量预编译（S5 第 9 条）就是校验的一部分，必须发生在注册前。
  5. **订阅读面断言族的改法（随 R8 批次 3 落地）**：
     - 现有一族断言以总线订阅读面证明注销兜底已就位，形态为 `Bus.GetSubscribers(Updates.CardDestroyed)` 含 `"{effect.Name}/dispose"`。总线退役后一律改写为「审查面上存在一条 `framework:disposal` 边，其宿主成员＝`engine.card-destroyed`、来源实体＝该组件」——断言对象从"总线订阅者名字列表"换成"审查导出的边集合"，不再依赖显示名。
     - 该族至少含：`tests/Orc.Tests/EffectLifecycleTests.cs:30`（含位）／`:166`／`:192`／`:296`（回滚不滞留）／`:335`，`tests/Orc.Tests/Scenario1Tests.cs:75`、`Scenario2Tests.cs:34`／`:78`，`tests/Orc.Tests/CardsTestFixtures.cs:43`／`:55`（挂载/卸载观测点），`tests/Orc.Game.Tests/G7HandOperationsTests.cs:345`。
     - 改写规则同批适用于一切"以 `Bus.GetSubscribers`／`EnumerateHooks` 作为编排证据"的断言（`tests/Orc.Tests/AuditChainExportTests.cs`、`AuditChainFoundationTests.cs`、`BusMountTests.cs`、`BusEmitTests.cs`）——它们要么随 `Bus` 一并退役，要么迁到审查面/信号调用的等价断言上；不允许"保留 `Bus` 只为了让旧断言编译通过"。
- Acceptance:
  - [ ] 卡包装载原子：任一资产失败＝整包无任何注册残留，报告列出全部失败项（R1a）
  - [ ] 必需包失败＝对局不开始（初始化上抛），且失败对局不产生半初始化状态
  - [ ] 可选包失败＝跳过该包、对局照常开始
  - [ ] 被跳过的可选包在审查导出与初始化报告中可见（包 id + 失败项 + skipped 标记）
  - [ ] 每批结束 `dotnet test OrcEngine.sln` 全绿（R8）
  - [ ] 每批能指出"这一批改变了哪些行为"（R8）
  - [ ] 批次划分与 R8 五批一致，不出现"半新半旧第三条路径"
  - [ ] 订阅读面断言族全部改写为审查面边断言，且断言不再依赖任何显示名
- Rationale: R8 要求每批全绿，因此"断言所依赖的观测面先于被替换机制退役"是不可能的——必须在同一批内把观测面从订阅读面迁到审查面，否则只能靠保留 `Bus` 维持编译，等于留下第三条路径。卡包原子性 + 必需/可选二分把"失败的爆炸半径"从"一张卡悄悄缺效果"提到"包级"，从而与 W-17 的 fail-fast 裁决自洽。

### S15: 作者文档更新（卡牌开发文档）
- Status: In Progress
- Target: `docs/卡牌DIY开发文档/**`（`01-效果原理`、`02-触发器与handler`、`04-目标选择targeter`、`05-动作面与运行时API`、`08-能力边界`、`reference/信号表` 等）
- Approach（已裁决部分）：
  1. **必须写入作者可见的语法子集约束**：`view` 不得交给 helper／局部函数去写——此类写法不在装载期静态检查范围内，只有运行期闸门兜底（S5 第 8e 条）。
  2. **术语与结构同步**：`hooks`→信号（`engine#card.load` 等）、`injects[].target` 显示名→`作用域#成员名`、`host`/`self` 作用域、类型预制体与 `requires-host`、触发器/handler 合流、组件自托管、注销兜底。
  3. **失败面口径**：csx 失败分类（`binding`／`compile`／`signature`／`sandbox`／`timeout`／`runtime`）、卡包原子性与必需/可选包。
- Acceptance:
  - [ ] 文档中不再出现 `hooks`／`injects[].target` 显示名／`viewType` 的写法指导
  - [ ] "`view` 不能交给 helper 写"这条约束在作者文档中明确可见
  - [ ] 失败分类与卡包必需/可选口径在文档中可查
- Rationale: 语法子集约束与"放行但留痕"的处置只有在作者知道的前提下才成立；否则作者会把概率性保护当作确定性保护。
- 待裁决: 无（章节清单在 S11／S14 定形后按 R1e／R1i／R1j／R3j 的术语表机械替换）。

## 依赖顺序与批次映射（R8 五批 → 本文步骤）

| 步骤 | 依赖 | 所属批次 |
| --- | --- | --- |
| S1 基线 | — | 0（已做） |
| S2 实体模型 | S1 | 1 |
| S2a 包装通道 | S2 | 1 |
| S4 挂载/寻址 | S2 | 1 |
| S5 视图投影 | S2、S4 | 1 |
| S6 数据面 | S5 | 1 |
| S3 信号/总线退役 | S2 | 2 |
| S7 引擎实体 | S3、S4 | 2 |
| S8 卡牌+组件实体 | S2a、S4、S5、S6 | 3 |
| S10 审查面 | S3、S7、S8 | 3 |
| S11 资产迁移 | S7（常量表）、S8（schema） | 4 |
| S9 targeter 合并 | S1 的判定器面 | 2–5 并行 |
| S12 Protocol | S9 | 5 |
| S13 旧类型退役 | S11 完成 | 5 |
| S15 作者文档 | S11、S14 定形 | 5 |
| S14 卡包装载门 + 验收门 | 全部 | 贯穿每批 |

| R8 批次 | 本文步骤 | 批内门 |
| --- | --- | --- |
| 1 内核实体模型 | S1 → S2 → S2a → S4 → S5 → S6 | 模型/寻址/视图/数据面可装载；`binding` 类别可用 |
| 2 引擎实体 | S3 → S7 | 信号全集就位、`Bus` 退役、跨层合并解析通过 |
| 3 卡牌 + 组件实体 | S8 → S10 | 类型预制体绑定、`active-when`、注销兜底/一次性、审查边图替换订阅读面 |
| 4 资产迁移 | S11 | 迁移器全量转换 + 反向自检；卡包原子/必需可选 |
| 5 旧类型移除 | S13 → S12 → S15 → S14 收口 | 无 `Trigger<TView>`/`Effect`/`Prefab` 残留；文档与协议同步 |

## 假设与替代方案（A-1…A-8 — 准出时一次性审）

| 编号 | 假设（本文按此实现） | 依据 | 若假设不成立时的替代方案 |
| --- | --- | --- | --- |
| A-1 | **"必填"分两层**：可静态判定的部分（字段名在视图声明中、类型属封闭集、`requires-host` 覆盖）在**装载期** fail-fast；**数据面值缺失**在**首次执行**时 fail-fast。R1a 原句"必填字段缺失＝装载期 fail-fast"按此收口（父 agent 第 17 轮判定，**不再是冲突**） | 数据面是运行期产物，装载期不存在值；现实现即执行期抛 `KeyNotFoundException`（`src/Orc/Core/ContextViewBinder.cs:35-44`） | 若要求装载期就判定值缺失，则需"数据面 schema 先行声明 + 装载期静态类型检查"，等于引入一门数据面类型系统（R1a 明确否掉的 A 方案） |
| A-2 | **挂载/包装混用的装载期判定只看声明形态**：`mount` 声明不得带 `phase`，`wrap` 声明不得带"新增步骤"字段；csx 内部是否越界无法静态判定 → 审查面标注 + 评审，不做装载期拒绝 | 动态调用不可静态分析 | 若要求运行期也拒绝，需在 csx 里禁止所有跨实体调用（等于砍掉 `runtime.Judge` 等现有能力） |
| A-3 | **卡数据 `effects` 声明 → 一条＝一个组件实体实例**，列表序＝声明序；`inline` 与 `declared` 混排按其在列表中的位置定序 | R1g 需要声明序，且这是唯一现成序来源 | 若改为"按 prefab id 排序"，则声明序失去作者可控性，R1g 的"可从模型推出"变为"可从 id 推出" |
| A-4 | **审查导出沿用 `AuditChain` JSON 形态**（`src/Orc/Output/AuditChainJson.cs`），`schemaVersion` 升 2 并保留字段；新增 `entities`／`members`／`edges`／`wraps` 四个集合 | R7 不退化 + 已有前端消费面 | 若改为全新格式，S12 与前端需同批重写消费侧，批次 3 的红面扩大 |
| A-5 | **`random` 开关落在对局配置**（`Match` 构造选项），默认禁用；禁用时执行到随机选择器＝抛明确错误；测试/沙盒显式开启 | R2c 的"实现口径（待用户确认）" | 若改放资产域，会出现"同一卡包不能同时服务人类对局与 AI 测试"的矛盾（R2c 已指出） |
| A-6 | **targeter 合并＝拆"取样语义"与"裁定语义"**：确定性（`all`/`self`/`random`）留判定器，`choose` 走提交通道；`EffectRuntime.SelectAsync` 改提交式，`BeginTargetSelectionAsync` 死代码删除 | R2／R2a 现状诊断 | 若保留 `SelectAsync` 直读语义，则 R2「引擎永不代选」无法成立 |
| A-7 | **Protocol 选择器消息为加性扩展**：保留现有字段，新增"待决请求 id／提交结果" | R8 要求前端同批但未指定形态 | 若需破坏性重命名，S12 与前端联调改为双轨发版 |
| A-8 | **类别词/单位类型词表仍由 `TypeCategoryDefinition` 持有**；类型预制体 id 由"类别"唯一映射（`card.unit`/`card.order`/`card.counter`/`card.hq`），不引入新词表 | R3g + 现有解析器 | 若引入新词表，则 33 份卡数据需迁移（与 R3g"151 张卡零改动"冲突） |

## 风险清单

| 风险 | 触发点 | 缓解 |
| --- | --- | --- |
| 迁移器产出"能解析但行为变了"的资产 | S11 的 4 类转换（`hooks`→挂载、`Rewrite`→before、`Commit`→after、无 band→外来边） | 每类各配一条"迁移前后行为等价"测试（S11 验收已列），并用现测试逐条对照 |
| 审查面在过渡期两套并存（`Bus` 订阅读面 + 边图） | 批次 3 | S14 明文禁止"为编译通过而保留 `Bus`" |
| `order-source` 与插入排序实现不一致 → 顺序无法复推 | S3／S4 | 守护测试：任意两条边用 (order-source, 位次) 排序结果 == 执行序 |
| `dynamic` 静态检查漏检被当成"安全" | S5 | 审查面列出"未经静态验证的 `view` 传递"计数，并在作者文档标注概率性保护 |
| 卡包原子性把"社区包语法错误"放大成"整局不开" | S14 | 必需/可选二分 + 跳过包在报告与审查面可见 |
| 初始化触发器越界读对局状态（靠约定而非 API） | S8 | 签名只暴露定义视图，物理不可达（S8 第 17 条） |

## 待裁决（盘问队列）—— 已全部收敛

| 编号 | 议题 | 指向 | 状态 |
| --- | --- | --- | --- |
| W-01 | 总线订阅（`hooks`）在新模型中的归属与寻址 | R1、R1b、R1d、R1e | **已裁决（第 1 轮）**：原 hook 全改为引擎成员触发器（成员名保留原样，寻址 `engine#card.load`）；`Emit`＝调用；订阅＝挂载；跨组件序＝**静态声明序**（R1g 最终口径，早轮"装载序"表述已废）；运行时注册为唯一动态入口；未注册引用＝明确失败。落表见 S3 |
| W-02 | 视图的消费面：预制的字段清单如何被 csx／C# handler 按名读取 | R1a、R1c | **已裁决（第 4 轮）**：作用域＝不为每个形状新增类，只允许一个通用承载视图；csx 统一 `Func<dynamic, Context, CancellationToken, Task>`；按名动态读写；双闸门（装载期静态语法检查 + 运行期 setter）；迁移器只改头部。落表见 S5／S11 |
| W-03 | 原 `injects[].target` 显示名 → 成员地址的映射表（含作用域归属 engine/host）、`Commit`/无 `band` 注入的转换规则 | R1d、R1i、R1j、R6b | **已裁决（第 16 轮）**：4 条映射（`engine#damage-apply`／`engine#unit-attack`／`engine#action-budget`／`host#targeting`）；映射表与实现侧常量同源 + 守护测试；未引用的 14 个具名触发器保留为成员；`Commit`→`wrap/after`；无 `band`→外来挂载边。落表见 S7／S11 |
| W-04 | `random` 开关的配置承载（R2c 标注"待用户确认"） | R2c | 转 ASSUMPTION A-5（非阻塞） |
| W-05 | 组件实例的粒度与生命周期边界（何时实例化／销毁；能否同 prefab 多实例／复装；与数据组件同名不同物的消歧） | R3c、R3d、R1f | **已裁决（第 12 轮）**：同一 prefab 至多一份实例（重复＝拒绝、不引入组件参数）；构造／装配分离，`active-when`∈{loaded,placed} 缺省 placed 且须在 schema 显式化；复装＝重建实例；装配失败上浮到包级；数据组件 vs 组件实体消歧。落表见 S8 |
| W-06 | 初始化触发器的运行期装配 API 边界（注册 vs 挂载、运行期边序与 owner、"业务逻辑"的可判定边界） | R6、R1e、R1g、R1h | **已裁决（第 14 轮）**：注册与挂载两动作一门面；允许往已声明信号运行期挂边（`appended`、边级 `order-source`）；运行期边必绑 owner、无无主边（沙盒会话可作 owner）；初始化期只能读"定义视图"、不得读对局状态或产生副作用（API 物理强制）。落表见 S3／S8／S10 |
| W-20 | 包装通道的承载体与寻址（目标是一条边还是成员；`before`/`after`/`replace` 与接缝在新模型中的表达；判定器包装器是否同属一套） | R1b、R1i、R7 | **已裁决（第 15 轮）**：band 退役；成员执行＝自有边→外来边（声明序）→框架边；包装目标＝成员（无二级地址）；相位 before(LIFO)/after(FIFO)/replace(最后声明者)；接缝退役；判定器包装器同属一套；三类迁移规则已定；`ctx.Stop()` 中止范围＝当前成员且不回滚已执行的 before。落表见 S2／S2a／S4／S10／S11 |
| W-07 | 审查导出 schema 的版本策略（`AuditChain.schemaVersion=1` 的去留） | R7 | 转 ASSUMPTION A-4（非阻塞） |
| W-08 | disposing 兜底：强制含 `card.destroyed`／拒绝 `effect.removed` 的隐式规则，改为显式声明还是保留隐式 | R1f、R3f、R6、R7、R8 | **已裁决（第 2–3 轮）**：保留隐式；框架注销边＝该组件所有已声明挂载之后；信号上的边按**静态声明序**排（R1g 最终口径，早轮"装载序"表述已废）；`effect.removed` 装载期拒绝保留；只保留预制体层一处实现；审查来源枚举含 `framework:disposal`；卸载按句柄精确撤销、禁止按 owner 扫表。落表见 S8／S10／S14 |
| W-09 | 信号命名的全集与命名规则（`card.load`→`engine.card-load` 对多段名如 `unit.position.changed` 的映射、封闭集合与自定义更新名的迁移） | R1e、R1d | **已裁决（第 9 轮）**：寻址改为 `作用域#成员名`，第一个 `#` 切分、成员名零转换且允许含 `.`；内核 4／游戏层 35 分域但同处 `engine` 根、跨层合并解析；自定义更新名只能经运行时注册进入命名空间。落表见 S3／S4／S11 |
| W-10 | `host` / `self` 作用域的解析目标，以及卡牌成员表的声明载体 | R1d、R5、R6b、R3d | **已裁决（第 10 轮）**：`host#`＝可组合实体实例成员、`self#`＝组件自身成员；成员表由类型预制体（`card.base`→`card.unit`/`card.order`/`card.counter`）声明并按 `typeCategory` 自动绑定；不引入移除成员；`host#` 校验依据＝组件 `requires-host`；`self#` 自挂载不受可见性约束。落表见 S4／S8 |
| W-11 | 执行序的唯一依据（声明序 vs 装配序）、运行时注册的边序、复装回填位次、无意义挂载 | R1e、R1f、R1g、R3i | **已裁决（第 13 轮）**：唯一依据＝静态声明序，装配批次不影响；边表须支持有序插入；运行时注册信号上的边按追加序（审查标 `order-source: appended`）；重建实例的边回填原位；`placed` 组件挂加载期信号＝装载期拒绝。落表见 S3／S8／S10 |
| W-12 | 视图消费面与"实际读写的字段"校验的边界（装载期能验到哪一步） | R1a、R1c | 并入 W-02 |
| W-13 | `OneShot`（一次触发/施放后自动失效）在新模型的承载：保留槽与 band 均已退役，"一次性"挂在哪 | R1f、R1i、R3i、R3k | **已裁决（第 17 轮）**：触发点＝任一成员首次执行完成后；注销由框架合成、审查标 `framework:one-shot`；计数＝实体内置状态（不占私有字段）；执行前检查、执行后置位（重入安全）；复装复位；无任何外部可触发边＝装载期拒绝。落表见 S8／S10／S11 |
| W-14 | 承载视图的按名读写是否也能落到组件私有字段（与 R1c"视图只管数据面"、R3e"私有变量只经 `owner.<field>`"的张力） | R1c、R3e、R1a | **已裁决（第 8 轮）**：名字空间＝数据面（仅视图声明字段）；私有字段唯一通道 `owner.<Name>`；视图字段与私有字段同名＝装载期拒绝；静态检查双通道（`view.*` 比视图声明、`owner.*` 比私有字段∪实体公开属性），两边未声明都判 `binding`；写回只在 `view` 通道。落表见 S5／S9 |
| W-15 | `dynamic` 依赖 `Microsoft.CSharp` 运行时绑定器，与 csx 沙箱程序集白名单的冲突 | R1a、R8 | **已裁决（第 5 轮）**：`Microsoft.CSharp` 进白名单（只提供 DLR 绑定器，不扩大类型可见面）；csx 形态保持 `view.<Name>`；`ViewType` 退化为唯一承载视图；签名校验改"形状 + 首参可接收性"；csx 编译失败＝拒绝装载／拒绝注册。落表见 S5 |
| W-16 | 装载期静态检查的判定口径与未识别形态的处置 | R1a、R1c | **已裁决（第 7 轮）**：用 Roslyn 语法树（不做字面匹配）；算写＝直接赋值／`+=`／`-=`／`++`／`--`／`ref`·`out` 实参；注释与字符串不参与；只追踪直接别名，经方法/局部函数传递者放行；放行者计入审查面"未经静态验证的 view 传递"；新增错误分类 `binding`；约束写进开发文档。落表见 S5／S10 |
| W-17 | csx 编译失败的**时点**与**爆炸半径**（拒绝该预制体／该卡／该卡包／整个对局；装载期全量编译 vs 懒编译） | R1a、R8 | **已裁决（第 6 轮）**：装载期全量预编译（不保留懒编译）；卡包内原子（整包拒绝，报告列全部失败项）；卡包分必需/可选（必需失败＝不开局，可选失败＝跳过并留痕）；运行时注册失败＝拒绝注册、错误码沿用 `ErrorCategory`。落表见 S5／S14 |
| W-19 | 类型预制体的绑定覆盖：直接 `new UnitCard(...)` 的路径（17 个测试文件 + `CardLibrary`）、HQ（无卡数据）、`typeCategory` 多值口径与"默认类型预制体"是否需要 | R3g、R3d、R8 | **已裁决（第 11 轮）**：唯一性只按"类别词唯一"判（`["infantry","tank"]` 合法）；缺 `typeCategory`／多类别词／未定义词＝拒绝且不设默认兜底；C# 类退化为绑定入口（构造签名不变、不得 `new Trigger<`）；`Hq` 绑 `card.hq`（extends `card.base`）；映射表 4 条落表。见 S8 |
