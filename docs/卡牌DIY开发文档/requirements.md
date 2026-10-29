# Requirements — 卡牌 DIY 开发文档

> **⚠ 历史存档（2026-10-29）**：本文件记录 2026-10 期间的需求 grill 过程（R1–R11、S1–S27），
> 其中提到的 `Effect`／`PassiveEffect`／`CardLoadout`／`Trigger<TView>`／`injects`／`OrderFlowBands` 等符号
> **已在 v4 编排实体化后删除**。现状以 [README](./README.md) 与 `src/` 为准；本文件只用于追溯「为什么这样设计」。


一句话：为「想在本引擎里做卡」的创作者写一份**可照做**的开发文档，覆盖效果原理、扩展语法/机制 API、方法论、8 个典型效果示例与词条/预制效果清单。

> 状态：**需求 grill 进行中**（Draft 条目待用户裁决）。术语见 [CONTEXT.md](./CONTEXT.md)。

## Scope

- Includes：
  - 文档库本体（阅读指南 + 9 章正文 + 参考层 + 模板层）。
  - 文档正文：引擎卡牌效果原理（装载/触发/结算/销毁的完整因果链）。
  - 文档正文：引擎提供的特殊语法与机制（触发器、handler、判定器、targeter/选择器、wrapper 管道与动作缝）及其 API 接口面。
  - 文档正文：基于引擎机制的卡牌开发方法论（选型判据、落地路径、常见坑、自检清单）。
  - 文档正文：8 个指定效果示例（逐条给出可落地的实现与其机制解释）。
  - 文档正文：词条（Keyword）与预制效果（KeywordPrefab / 模板效果）清单与用法。
  - **示例证据**：数据侧示例落成 `samples/card-pack-diy/` 真实卡包；代码侧示例落成 `tests/Orc.Game.Tests/` 中的可编译、可执行用例。
- Excludes：
  - 引擎内核的完整架构说明（→ `README.md`、`.ams/context/orc-engine/`、`docs/game-flow/`）。
  - UI/宿主接入与投影（→ `docs/ui-开发者交接文档.md`、`docs/mcp-ui-integration.md`）。
  - MCP 工具清单与协议细节（→ `src/Orc.Mcp`、`samples/Orc.Mcp.Stdio/README.md`）。
  - 对局业务规则全量复述（→ `docs/game-flow/01`–`09`）。
  - 引擎源码改造建议（本文档只描述**现状能力**与**在现状下的写法**）。

## Constraints

- 术语以仓库根 `CONTEXT.md` 与 `.ams/context/*/CONTEXT.md` 为唯一称谓来源；本文档不另造同义词，新术语只登记在 `docs/卡牌DIY开发文档/CONTEXT.md`。
- 一切 API 名称、签名、路径、字段名必须与 `src/` 现状一致（以代码为真源，文档不得描述未实现能力）。
- 不虚构 API；能力缺口必须显式标注（缺口层级：内核缺 / 游戏层未接线 / 规则缺失 / 部分可覆盖）。
- 中文正文；代码/JSON/csx 保持原文（ASCII 引号）。
- 单篇可读、可跳读：带目录与「按需求找写法」索引。
- **仓库改动边界（R9 授权后修订）**：允许修改 `src/Orc/**`（内核）与 `src/Orc.Game/**`（游戏层）以落地 R9；文档库与示例仍新增于 `docs/卡牌DIY开发文档/**`、`samples/card-pack-diy/**`、`tests/Orc.Game.Tests/**`。不允许改 `*.sln` / `*.slnx` / 工程文件。
- **可验证**：`dotnet build` 与全量 `dotnet test` 必须通过；新增能力须有专项测试；若环境不可用，如实报告并降级。

## Requirement Items

### R1: 读者与主线
- Status: Confirmed
- Scenario/Trigger: 一位没有读过引擎源码的卡牌作者打开本文档。
- Behavior: 文档以**卡牌 DIY 作者**为第一读者，以「数据侧（`*.card.json` + `*.prefab.json` + csx）」为主干路径，C# 效果实现作为进阶路径，并在每个机制处标明两条路径的可用性。
- Acceptance:
  - [x] 开篇 1 页内说明读者、前置知识、两条路径的分工与选择判据
  - [x] 正文示例默认给出数据侧写法；仅 C# 可表达处显式标注并给 C# 写法
  - [x] 不要求读者阅读 `src/` 即可完成一个可加载的卡
- Terminology: [DIY 作者](./CONTEXT.md)、[数据侧](./CONTEXT.md)、[代码侧](./CONTEXT.md)

### R2: 文档库形态（自包含 + 可扩展）
- Status: Confirmed
- Scenario/Trigger: 作者需要检索某个机制或某个 API；或后续维护者要新增一章、一个示例、一张表。
- Behavior: 建成一个**文档库**而非单篇长文：
  - **唯一入口 `README.md`**（阅读指南）：读者画像、前置知识、三条阅读路径（新手上路 / 按任务查 / 直接抄示例）、全量目录、章节状态表。
  - **主题分章**：每章是一个独立文件，章首固定「本章解决什么 / 前置 / 读完你能做什么 / 对应代码路径 / 本章自检」元信息块；正文不依赖其他章的隐含上下文（需要的概念在本章复述或链接）。
  - **示例独立成篇**：`examples/` 下每个示例一文件，自带卡面文本、完整数据/代码、时序、坑位，可单独阅读与复制。
  - **参考层 `reference/`**：纯查表（信号表、API 速查、词条表），不含叙述。
  - **可扩展约定 `_template/`**：章节模板 + 示例模板 + 登记规则（新增 = 复制模板 → 按编号命名 → 在 README 索引登记），不改动既有章节。
  - 目录骨架（固定）：
    ```
    docs/卡牌DIY开发文档/
    ├── README.md              阅读指南与总索引（唯一入口）
    ├── 00-总览与心智模型.md
    ├── 01-效果原理.md
    ├── 02-触发器与handler.md
    ├── 03-判定器与wrapper.md
    ├── 04-目标选择targeter.md
    ├── 05-动作面与运行时API.md
    ├── 06-卡牌开发方法论.md
    ├── 07-词条与预制效果.md
    ├── 08-能力边界.md
    ├── examples/              README + 8 个示例（一例一文件）
    ├── reference/             信号表 / API 速查 / 词条表
    └── _template/             章节模板 / 示例模板
    ```
- Acceptance:
  - [x] `README.md` 含三条阅读路径与全量文件索引，读者只读它即可定位到任一条写法
  - [x] 每章章首有固定元信息块（5 项齐全）
  - [x] 任取一章单独阅读，无需回看前一章即可理解本章结论
  - [x] `_template/` 含章节模板、示例模板与「新增文件登记步骤」
  - [x] `reference/` 三张表齐全，且与正文交叉链接
- Terminology: [文档库](./CONTEXT.md)、[阅读指南](./CONTEXT.md)、[章节模板](./CONTEXT.md)

### R3: 卡牌效果原理
- Status: Confirmed
- Scenario/Trigger: 作者要理解「我写的东西什么时候被调用、为什么没生效」。
- Behavior: 讲清五段因果：①效果如何随卡装载（装载链、通道即声明）②事件如何到达 handler（总线、hooks、band 排序、触发器快照）③handler 如何改状态（EffectRuntime 受控门面 / 领域服务）④数值如何传播（修饰链、光环、集中触发）⑤效果如何销毁（disposing 触发器、teardown、effect.removed）。
- Acceptance:
  - [x] 给出「装载 → 触发 → 结算 → 传播 → 销毁」的端到端时序图
  - [x] 明确 `card.destroyed`（引擎销毁）与 `card.died`（游戏层死亡）的区别
  - [x] 明确「装载 ≠ 触发」「注册 ≠ 生效」两组易混语义
  - [x] 给出「效果不生效」的排查树（至少 6 个分支）
- Terminology: [装载链](./CONTEXT.md)、[disposing 触发器](../../CONTEXT.md)、[集中触发](../../CONTEXT.md)

### R4: 特殊语法与机制 API
- Status: Confirmed
- Scenario/Trigger: 作者要写一个需要监听、判定、选靶或被改写的效果。
- Behavior: 逐机制给出「是什么 / 何时用 / 完整 API 面 / 最小可跑片段 / 边界」：触发器（band、hooks、kind、保留槽）、handler（csx 签名、装配来源、异常语义）、判定器（强类型/统一签名、注册表、玩家域、wrapper）、targeter（Selector、参数、流程、三态结果）、wrapper（before/after/replace、严格签名校验、动作缝）、修饰器与光环、词条程序。
- Acceptance:
  - [x] 每个机制有 API 表（名称、签名、层归属、失败语义）
  - [x] wrapper 三类给出执行序 `w2→w1→core→w1→w2` 与签名校验规则
  - [x] targeter 给出「后端给候选、前端只采集手势」的契约与三态结果
  - [x] 判定器给出「玩家域」与「全局层→玩家层」两层管道语义
- Terminology: [触发器](../../CONTEXT.md)、[判定器](../../CONTEXT.md)、[包装器](../../CONTEXT.md)、[动作缝](../../CONTEXT.md)、[选择器](../../.ams/context/targeter/CONTEXT.md)

### R5: 卡牌开发方法论
- Status: Confirmed
- Scenario/Trigger: 作者拿到一句卡面文本，要决定「用哪条路径实现」。
- Behavior: 给出决策流程：①文本归类（静态持续 / 触发 / 施放 / 替换 / 反制）②选承载（词条程序 / 效果预制体 / 判定器 / wrapper / C# 效果）③选触发点（信号表）④选动作面（EffectRuntime 白名单）⑤自检（幂等、重入、作用域、离场清理、性能）。
- Acceptance:
  - [x] 一张「卡面文本 → 机制」映射决策表（至少 10 类句式）
  - [x] 「选错会怎样」的反例（≥3 个）
  - [x] 交付前自检清单（≥10 项，可勾选）
- Terminology: [词条程序](./CONTEXT.md)、[效果预制体](./CONTEXT.md)

### R6: 八个效果示例
- Status: Confirmed
- Scenario/Trigger: 作者要照抄一个「伤害 + 加防 / 群体随机伤害 / 全局计数 / 光环计数 / 弃牌检索 / 反制第二张指令 / 指令免疫 / 突袭-推进-撤退」的完整写法。
- Behavior: 8 个示例逐个给出：卡面文本 → 机制选型 → 完整代码/数据 → 触发时序 → 边界与坑。每个示例配一条**可执行证据**：
  - 数据侧 → 进入 `samples/card-pack-diy/`，`CardDistributionValidator` 预检 0 error；
  - 代码侧 → 进入 `tests/Orc.Game.Tests/`，`dotnet test --filter` 通过。
- Acceptance:
  - [x] R6a 单目标 3 伤害 + 友方 HQ +5 防御（handler）
  - [x] R6b 3 伤害随机分配至所有单位（handler）
  - [x] R6c 友方 HQ 回复 = 本局被摧毁单位数（全局监听 handler）
  - [x] R6d 每个敌方轰炸机使本单位 +1 攻击（持续计数）
  - [x] R6e 友方因敌方弃牌时从卡组抽取此牌
  - [x] R6f 敌方使用第二张指令时将其反制（反制）
  - [x] R6g 使一个单位无法被指令指向（指令效果）
  - [x] R6h 加入支援阵线 → 随机与敌方单位战斗 → 存活则移前线并对敌方 HQ 3 伤害，否则撤退
  - [x] 每个示例标注证据等级与「数据侧可用 / 仅代码侧可用」
  - [x] 全部示例证据可复跑（命令与预期输出写进示例文件）
- Terminology: —

### R6h: 示例 8 的卡面语义
- Status: Confirmed
- Scenario/Trigger: 卡面原文为「加入支援阵线 → 随机和一个敌方单位战斗 → 若其存活，使其移到前线，然后对敌方总部造成三点伤害，否则（即前线有敌方单位），使其撤退」。
- Behavior:
  - **建模前提**：本示例为**指令卡**——施放时把一个指定单位「加入」己方支援阵线（`JoinUnitAsync`，不走部署链、不扣部署费），随后该单位立即执行下述流程；触发点为施放后的显式流程，不是「单位自身入场」。
  - **流程（A 案）**：① 随机选一个敌方单位作为战斗对象 → ② 强制战斗（效果发起：不扣行动费、不清行动位）→ ③ 判定该单位存活：
    - 存活且**前线无**敌方存活单位 → 移到前线 + 对敌方总部造成 3 点伤害；
    - 存活但**前线有**敌方单位（推进条件不满足）→ 撤退（`retreat`：移回己方支援线空槽）；
    - 阵亡 → 不执行后续动作，仅留痕（不静默、不产生幽灵实例）。
- Acceptance:
  - [x] 分支条件唯一且与卡面括号一致（括号＝推进条件，不是阵亡）
  - [x] 「撤退」按引擎 `retreat` 语义落在**己方支援线空槽**，不是回手牌
  - [x] 阵亡分支有显式留痕且不执行移动/伤害
  - [x] 示例证据用例覆盖三条分支（推进 / 撤退 / 阵亡）
- Terminology: [动作面](./CONTEXT.md)

### R6e-note: 示例 5 的归因依赖 R9
- Status: Confirmed
- Scenario/Trigger: 「友方因敌方弃牌时从卡组抽取此牌」的归因在现状不可判定：`card.discarded` 载荷只有 `{ Card, Player }`（`Player`＝归属方），`DiscardCardAsync(player, card)` 无 cause 参数，`Context` 与条件求值均读不到发起者。
- Behavior: 由 R9（来源透传）根治后，示例 5 保留原卡面「因敌方弃牌」：敌人弃牌的效果调用 `DiscardCardAsync(player, card, source)` 时携带施动卡，信号载荷与视图可见 `Source`，监听方判定「Source 归属敌方」即可。示例 5 的证据用例必须覆盖「敌方弃牌触发 / 己方弃牌不触发」两向。
- Acceptance:
  - [x] `card.discarded` 携带 `Source`（可为空）
  - [x] 示例 5 用 `view.Source` 判定敌方所致，且双向往返用例通过
  - [x] 08 章不再把「弃置无归因」列为现状缺口（改为已由 R9 覆盖）
- Terminology: [触发点](./CONTEXT.md)

### R9: 触发器来源透传（Source / TriggerCard 两面）
- Status: Confirmed
- Scenario/Trigger: 任何一次触发器执行都需要知道两件事：①**这条事件是谁引发的**（归因，如「敌方弃牌」「敌方指令」）；②**当前这段 handler 是由谁触发的**（执行归属，用于链上继续传递）。
- Behavior:
  - **两面分工（不可合并）**：
    - **`Source`（事件引发者）**＝信号载荷键，**发射时确定、只读**，不参与执行帧解析；值＝引发该动作的卡（效果引发＝效果宿主卡；玩家主动操作＝空）。监听者据此判定归因。
    - **`TriggerCard`（执行触发者）**＝执行帧值，**逐事件解析**；值＝当前 handler 的注册者的卡。handler 内再发信号时，把它作为新信号的 `Source` 继续往下传。
  - **`TriggerCard` 解析规则**：
    1. 进入某 handler 时，若其**注册者**是卡或效果（`Card` / 效果实例），`TriggerCard` := 注册者的卡（效果的 `Host`；卡自身）；**这是 source 的唯一边界**。
    2. 若注册者不是卡/效果（引擎内置流程、无主注册、装配期注册），`TriggerCard` **原样继承**当前值（透传，不改写）。
    3. 触发器入口的初值：显式载荷含 `TriggerCard` 键则取之；否则从父执行帧继承；顶层玩家主动操作为空。**`Source` 不进帧、不作为 `TriggerCard` 初值**。
    4. 每条链的最终 `TriggerCard` ＝ 链上**最后一个**卡/效果注册 handler 的 source（后覆盖前）。
  - **handler 元数据**：每个事件注册项记录「注册者（效果实例 / 卡 / 空）」与其派生的 source 卡；审计面可读（`Registrant` / `SourceCard`）。
  - **注册者供给**：`Register` 增可选 `registrant` 参数；`Effect.Inject` 自动以效果实例登记；缺省回退「触发器 owner（若为 Card/Effect）」，再无则 null（＝透传者）。
  - **注入面**：视图同时声明 `Source`（来自载荷、只读）与 `TriggerCard`（来自执行帧、逐事件更新）两个属性；csx 与 C# handler 均可读。
  - **游戏层**：`GameUpdates` 全部发射助手增可选 `source`，写入载荷 `Source`；各视图补两属性；`EffectRuntime` 各动作在发起新动作时传 `source`；`DiscardCardAsync` 增 `source` 参数。
- Acceptance:
  - [x] 内核：`ExecutionFrame` 携带 `TriggerCard`，跨嵌套 `InvokeAsync` 继承；`Source` 不参与帧解析
  - [x] 内核：`EventEntry` 记录注册者，审计面可读 `Registrant` / `SourceCard`
  - [x] 规则：卡注册 handler 覆盖、非卡注册 handler 透传、最后者胜（三条各有专项测试）
  - [x] 顶层玩家操作 `TriggerCard` 为空（不得被残留值污染）
  - [x] `card.discarded` 等全部 `GameUpdates` 信号可携带 `Source`
  - [x] 监听者读 `Source` 可判定敌方所致；其 handler 内再发信号时 `Source` 取自身 `TriggerCard`
  - [x] 全量 `dotnet test` 通过（含既有 1600+ 用例）
- Terminology: [触发者卡牌](../../CONTEXT.md)、[触发点](./CONTEXT.md)

### R10: 战斗（Battle）机制
- Status: Confirmed
- Scenario/Trigger: 效果需要让两个单位「互相对拼」，但**不**走指挥对战的完整规则（射程/守护/烟幕/拦截/伏击/反击资格）。
- Behavior:
  - **入口**：`EffectRuntime.BattleAsync(Card first, Card second, Card? source = null, CancellationToken ct = default)`。
  - **语义**：先快照双方**攻击力有效值**，再各自经 `battle.damage` 触发端 → `damage.apply` 处理端结算；即使一方先被结算致死，另一方仍按快照受伤（同时结算）。
  - **无视**：射程、守护、烟幕、拦截、伏击改写、反击资格、压制；不扣行动费、不清行动位、不发 `unit.attack.*` / `unit.combat.survived` / `unit.acted`。
  - **前置**：双方均为在场存活单位；任一不满足 → 返回 `false`，零副作用。
  - **归属**：`first → second` 的伤害 `Source = first`、致死 `Killer = first`；反向同理。
  - **与「指挥对战」的区别**：指挥对战＝`unit.attack` 流程（合法性 + 反击 + 伏击）；战斗＝纯对拼，仅保留伤害层的通用改写。
- Acceptance:
  - [x] `BattleAsync` 在两条伤害路径上都携带 `Source`（各自为对方）
  - [x] 双方伤害按**快照攻击力**计算（先死者不影响后结算方的输出）
  - [x] 不触发任何攻击类信号与行动代价
  - [x] 任一方非在场存活单位 → `false` 且零副作用
  - [x] 免疫在战斗中生效、重甲不生效（重甲只挂 `combat.damage`）
- Terminology: [战斗](./CONTEXT.md)、[触发点](./CONTEXT.md)

### R11: 伤害的「触发-处理分离」引擎规范
- Status: Confirmed
- Scenario/Trigger: 任何来源要对卡牌造成伤害；或作者要写「受到双倍对战伤害」「非对战伤害 +1」「免疫」这类改写。
- Behavior:
  - **三触发端（语义化，可被具名效果改写）**：
    | 触发端 | 稳定键 | 来源 |
    | --- | --- | --- |
    | 卡牌对战伤害 | `combat.damage` | 真实指挥对战 |
    | 效果伤害 | `effect.damage` | 指令 / `EffectRuntime.DamageAsync` |
    | 战斗伤害 | `battle.damage` | `BattleAsync` |
  - **一处理端（唯一扣数值）**：`damage.apply`（对卡牌造成伤害）；全局改写（免疫归零）挂此处。
  - **职责边界**：触发端只改 `Amount` 或加条件，**不扣数值**；处理端是唯一调用数值门户的地方。
  - **门户收口**：`UnitCard.ApplyDefenseDamageAsync` / `Hq.ApplyDamageAsync` 降为受控面，其他牌/效果**不得旁路直调**；仅处理端可调。
  - **改写归属**：重甲 → `combat.damage`（只减对战）；免疫 → `damage.apply`（全伤害）；伏击/反击资格 → `unit.attack` 流程（不属于伤害三层）。
  - **视图**：`DamageEventView { Source, Target, Amount（可改写）, Kind（combat/effect/battle） }`；处理端视图额外带 `IsZeroed` 等结果位。
  - **规范地位**：本条是**引擎代码规范**——新增伤害来源必须选择/新增触发端，不得直接进处理端；新增"按来源区分"的改写必须挂对应触发端。
- Acceptance:
  - [x] 三类伤害各自经过对应触发端，且都能被 inject/wrapper 改写 `Amount`
  - [x] 处理端是唯一门户调用点（旁路为零，可用 grep 证明）
  - [x] 重甲只影响 `combat.damage`；免疫影响全部三类
  - [x] `Kind` 在三层载荷中一致可读
  - [x] 规范条文写入 `08 章`/`CONTEXT.md`，并有「新增伤害来源」检查清单
  - [x] 全量 `dotnet test` 通过（含行为变更迁移）
- Terminology: [伤害触发端](./CONTEXT.md)、[伤害处理端](./CONTEXT.md)

### R11-note: 行为变更（已确认）
- Status: Confirmed
- Scenario/Trigger: 免疫从对战流程迁到处理端后，效果伤害与战斗伤害也会被免疫归零（此前只有对战伤害吃免疫）。
- Behavior: 接受该变更；重甲不变（仍只减对战伤害）。既有断言「效果伤害穿透免疫」的测试须改为「效果伤害被免疫归零」，并在文档 08 章标注这是 **R11 引入的行为变更**。
- Acceptance:
  - [x] 全仓库检索「效果伤害 + 免疫」相关断言并迁移
  - [x] 变更记录写入 `implementation.md` 与 08 章
- Terminology: —

### R6f-note: 示例 6 的建模前提
- Status: Confirmed
- Scenario/Trigger: 「敌方使用第二张指令时将其反制」需在指令**结算前**中断；而游戏层原生 `OrderCard` 的打出链（预打出 → 打出宣告 → 主动 handler → 收尾）**不经过** `OrderFlow`，其触发器也未登记为具名触发器，数据侧 `injects` 无法接入。
- Behavior: 示例 6 以「可被反制的指令」建模——指令以 `engine.OrderFlow.ExecuteAsync(...)` 施放（`Counter → Resolve → Finalize` 三段），反制效果以 C# `PassiveEffect` 注入 `OrderFlowBands.Counter`，命中则 `ctx.Interrupt()` 使 `Resolve` 不执行。文档必须同时写明该缺口：**原生 `OrderCard` 不受反制窗口约束**，并给出替代写法与「需改哪里」。
- Acceptance:
  - [x] 示例可跑通（第二次施放被中断，伤害不发生）
  - [x] 缺口在正文与「能力边界」章双重标注（游戏层未接线）
  - [x] 给出「让普通指令卡可被反制」的最小改动点说明（只描述，不实施）
- Terminology: [能力边界](./CONTEXT.md)

### R7: 词条与预制效果
- Status: Confirmed
- Scenario/Trigger: 作者想复用既有词条行为，或把一段行为做成可复用制品。
- Behavior: 给出词条全清单（标识、类型、参值、行为、读取口）、词条程序（`KeywordProgram`）的规则点/事件/操作/目标/数值表达式全表，以及预制效果三层结构（事件/触发器/效果预制体）、`injects`、`wrappers`、模板效果与 op 模板。
- Acceptance:
  - [x] 词条表覆盖 `KeywordIds.All` 全部条目并标注打标状态
  - [x] 词条程序的 11 个规则点、17 个事件、**32 个操作**（以 `KeywordProgram.Operations` 为准）、14 个目标逐条列出
  - [x] 预制效果 JSON 给出可校验的最小骨架与字段表
  - [x] 说明「模板效果 / 效果 DSL / 效果预制体」三层关系与适用者
- Terminology: [词条](../../CONTEXT.md)、[词条程序](./CONTEXT.md)、[预制效果](./CONTEXT.md)

### R8: 能力边界
- Status: Confirmed
- Scenario/Trigger: 作者写的效果引擎当前不支持。
- Behavior: 独立章节列出已知缺口（含：数据侧无法注入指定 band、指令打出链未接反制窗口、EffectRuntime 无战斗/撤退入口、targeter 候选来源限制等），每条给「缺口层级 + 现状可行替代 + 需要改哪里」。
- Acceptance:
  - [x] 缺口逐条标注层级与替代方案
  - [x] 不把缺口写成「不支持」就结束——必须给绕行写法或明确「需改引擎」
- Terminology: [缺口层级](../../CONTEXT.md)

## Terminology

- [DIY 作者](./CONTEXT.md)
- [数据侧](./CONTEXT.md)
- [代码侧](./CONTEXT.md)
- [触发器](../../CONTEXT.md)
- [判定器](../../CONTEXT.md)
- [包装器](../../CONTEXT.md)
- [选择器](../../.ams/context/targeter/CONTEXT.md)
