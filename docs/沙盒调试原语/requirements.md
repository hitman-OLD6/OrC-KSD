# Requirements — 沙盒调试原语（引擎保留触发器）

让沙盒模式能**直接设定对局状态**，而不必用「出牌 → 切回合 → 补费用 → 再出牌」去把局面"打"出来：引擎内置一组**保留触发器**，装配完成后 handler 集封闭、仅沙盒模式可触发，用于实例化卡、单位化、改行动方等场景构造。

> 上游：`docs/无头沙盒-测试环境/requirements.md`（无头对局链路）。
> 触发本次需求的三个待验证面：**指令使用**、**反制使用**、**新卡的生成与加载**——现有 `sandbox.*` 只能沿着正常动作链拼局面，验证这三条链要跨越回合/费用/槽位/相位四重门禁，且每一步都在事件流里留下"伪动作"。

## Scope

- Includes：
  - 引擎层（`Orc.Game`）新增的**保留触发器族**及其公开触发面。
  - 装配后封闭 handler 集（禁注册）与沙盒授权门禁。
  - 每个原语的静默语义（状态写、派生状态处理、审计痕迹）。
  - MCP 服务层对原语的薄封装（`sandbox.*` 写类工具）。
  - 用原语跑通并回归验证：指令使用、反制使用、新卡的生成与加载。
- Excludes：
  - 回放 / 存档 / 时间旅行 / 撤销（原语不提供 undo）。
  - `play` 模式的任何工具面变更。
  - 卡定义的动态覆盖（`card.load` 仍"只追加不覆盖"，改卡换 id）。
  - 对效果 / 插件 / 宿主开放这些触发器（不可注册 handler 是需求，不是实现细节）。
  - 引擎规则的语义变更（原语不改判定器、不改效果语义、不改正常动作链）。

## Constraints

- **生产零影响**：未授权（非沙盒）时，保留触发器不可触发且不出现在公开枚举面；既有装配路径与性能不变，既有 2310 项测试不回归。
- **单写者**：所有原语调用仍经 `SerialWorkQueue` / `MatchExecutor` 单消费者，不开第二条写路径。
- **状态自洽**：静默设定后引擎不变量必须成立（组件齐备、槽位占用与位置字段一致、修饰基线已建立），否则后续正常动作会以异常形式失败。
- **复用效果侧原子，禁止动作入口**：原语必须复用效果侧已有的原子（`MatchCardService.CreateAndPlaceTo*`、`PlayManager.JoinUnitAsync`、`IJoinActionServices.UnitizeOnJoinAsync` 等），**禁止**走带校验的动作入口（`PlayUnitAsync` / `BeginUnitPrePlayAsync` / `DeployAsync` / `PlayOrderAsync` / `UseCounterAsync`）。判据是**"该原子是否为引擎唯一写点"**，而非"`MatchCardService` 有没有现成组合"——组合缺失时可直接调唯一写点，但**不得自建第二条写路径**。理由：效果侧原子与"一张卡的效果"同源，天然满足 R4；自建路径会与效果侧漂移。
- **失败经结果载体回传**：`Trigger.InvokeAsync` 会隔离 handler 的业务异常（`Outcome` 仍为 `Normal`），因此原语**不得**以抛异常表达业务失败。硬门槛检查放在入口（进链之前）；链内失败经结果载体（`ActionTargetResult` 的 `MarkTargetInvalid` / `MarkSkipped`）写回，由入口读出。失败原因（如 `TargetSlotOccupied`、`UnitAlreadyUnitized`）必须保留到对外返回，不得塌成笼统的 `engine-rejected`。
- **内核改动加性**：承载封闭态的 `Orc.Core` 改动必须是纯加性（默认不封闭）；不得改变既有触发器的注册/调用语义、异常口径与性能。
- **平台**：Windows 优先，无新第三方依赖。

## Requirement Items

### R1: 保留触发器的形态与封闭性

- Status: Confirmed
- Decision（用户原话落地）：**"无副作用、无动作缝隙"＝这组触发器在装配完成后禁止注册 handler**——handler 集由引擎内置、固定不变；没有第二方逻辑可插入，因此执行时不会被污染、也不存在可被他人观测/介入的中间时机。
- **Decision（形态与承载层，2026-10-21 补正）**：封闭性**由内核承载**——`Orc.Core` 的触发器增加一次性"封闭态"（默认不封闭，既有触发器的注册/调用语义与性能零变化，纯加性）。保留触发器**在构造完成时立即封闭**（注册齐内置 handler → 封闭，一气呵成）：不依赖对局生命周期，不存在"装配到一半仍可注册"的窗口。
- **Decision（双防线，消解验收互斥）**：
  - **主防线＝不可达**：保留触发器实例不经任何公开面暴露——无 public 属性、不进按名解析面（如 `MatchNamedTriggers`）、不进预制体 `injects` 解析。
  - **兜底＝封闭态**：即使经反射或程序集内部持有拿到实例，`Register` 亦一律抛异常。
  - 因此"调用即抛"（用 internal 权限测）与"无注册入口"（用公开面枚举测）是**两层独立断言**，不再互斥。
- Scenario/Trigger: 封闭态之后，任何代码（含程序集内部测试）试图向保留触发器注册 handler。
- Behavior: 引擎内置一组保留触发器；每个原语恰一个内置 handler；构造完成即封闭。封闭后注册一律抛 `InvalidOperationException`，异常文本含触发器名，handler 集不变。保留触发器不属于"效果可注入的扩展点"。
- Acceptance:
  - [ ] 封闭后（在 `Orc.Game.Tests`，有 internal 权限）对任一保留触发器调用注册 API → 抛 `InvalidOperationException`，异常文本含触发器名，handler 集不变。
  - [ ] 保留触发器在构造返回时即已封闭，不存在可注册窗口。
  - [ ] 公开面（public 属性 / 按名解析面 / 预制体 `injects`）中不存在通往保留触发器的引用路径；`diag.*` 导出**只读**列出触发器名、封闭态与内置 handler 名（可观测、不可写）。
  - [ ] 内置 handler 未注册齐（装配性错误）→ 构造期 fail-fast，实例不被产出（不进入"可触发但缺 handler"的中间状态）。
- Terminology: [触发器（Trigger）](../../CONTEXT.md), [handler](../../CONTEXT.md)

### R2: 沙盒授权门禁

- Status: Confirmed
- Decision（用户原话落地）：**非沙盒模式不能被触发**；这套触发器属于**引擎保留能力**，仅沙盒模式可用。
- Scenario/Trigger: 任一保留触发器被触发，而当前引擎实例未获沙盒授权。
- Behavior: 引擎实例持有"沙盒授权"这一显式事实（由装配方在构造期决定，缺省＝未授权）。未授权时触发一律被拒绝（明确失败、零状态变更）；`play` 模式下这些触发器既不出现于工具面，也无法经引擎公开面触发。
- Acceptance:
  - [ ] 未授权实例触发任一原语 → 明确拒绝，对局状态逐字段不变（快照比对）。
  - [ ] 授权只在沙盒装配路径开启：`Orc.Server --mode sandbox`、`McpService` Authoring 模式、测试夹具显式声明；`play` 路径不开启。
  - [ ] `play` 模式 `tools/list` 中不存在任何原语工具（既有语义不变）。
  - [ ] 未授权实例的诊断面（`diag.*`）不列出保留触发器，或列出但标注 `unavailable`——二者择一（实现期定，见 R4 说明）。
- Terminology: [沙盒模式](../../CONTEXT.md)

### R3: 原语清单

- Status: Confirmed
- Scenario/Trigger: 开发者 / agent 要在沙盒里摆出一个特定局面，用于验证某张卡或某条规则。
- Behavior: 第一批原语（草案，待裁决）：

  | 原语 | 语义（等效的效果侧操作） | 关键点 |
  |---|---|---|
  | `instantiate` | 按定义 id 造卡并放到指定去向（＝效果侧"生成并放置"的原子组合） | 去向＝`hand` / `deck_top` / `support[slot]` / `frontline[slot]`；**可指定精确槽位**（效果侧只能"相邻空槽"）；满手按爆牌裁决 |
  | `unitize` | 把单位卡单位化到指定槽（＝`JoinUnitAsync` 的加入链） | 支援线或前线、指定精确槽；发 `card.placed` ＋ `unit.joined`（见 R4） |
  | `move` | 把在场单位挪到另一空槽（＝效果侧"移动"） | 不扣行动费、不消耗行动次数 |
  | `remove` | 移除实体（＝效果侧"离场/销毁"） | 走引擎既有离场/销毁链；与 `sandbox.kill` 的关系待实现期收敛（见下方备注） |
  | `set_points` | 把指挥点数**设定为绝对值**（＝效果侧"加费/减费"的合成） | 不受 `PointSlots` 限制（引擎无此不变量）；超出槽数时返回 `warning`；事件按增量方向发 `point.gained` / `point.lost` 再发 `point.changed` |
  | `draw` | 抽 N 张到手牌（＝效果侧"抽牌"） | 满手按既有爆牌裁决（与效果侧同口径，属成功语义） |

  第二批（可选，不进本次主验收）：`set_stats`（直接设定攻/防/操作费；语义待实现期裁决，默认改修饰基线）。

- **已否决（2026-10-21 裁决）**：**不提供 `set_turn`**。理由：效果侧几乎没有只监听"回合数"的效果，收益不足；需要切换行动方时，由 agent 连续调用 `match.end_turn` 推进——回合钩子照跑，反而更接近真实对局。原 R4a（`set_turn` 例外语义）随之作废。
- **Decision（去向裁剪，2026-10-21 裁决）**：`instantiate` 只保留 **hand / deck_top / support[slot] / frontline[slot]** 四个去向。
  - `deck_bottom` **删除**：无效果侧原子（唯一先例在换牌链 `MulliganManager` 的 `InsertInstanceAt(Count)`），且"置底"对造局面无必需价值——要验证抽牌顺序，置顶已足够。
  - `discard` **删除**：引擎**不存在弃牌堆/墓地语义**（`PlayerManager` 明确 KARDS 官方无此语义）；"弃置"＝手牌移除 → 销毁 → `card.discarded`，属**动作**而非区域。需要弃置效果时用 `remove`。
  - `frontline[slot]` **保留**：`JoinUnitAsync` 是"加入链唯一写点"，结构上接受任意空槽且不校验线归属；`MatchCardService` 只是没把它包成"指定前线槽"的组合。原语直接复用该唯一写点，不自建第二条落位路径。
- 备注：`remove` 与 `unitize` 在 P2 语义下与既有 `sandbox.kill` / `play.join` 高度重合，其增量价值在于"统一到同一套原语面 + 可指定精确槽位 + 免校验"；是否保留 `remove` 待实现期按实际使用频次收敛（不影响本需求验收）。
- Acceptance:
  - [ ] 清单内的每个原语都有独立验收（见 R6 用例矩阵）。
  - [ ] 清单外的行为不实现（不提供"万能执行脚本"入口，也不提供 `set_turn`）。
  - [ ] 每个原语的"等效效果侧操作"在文档中写明，并有用例对齐二者的信号序列。
- Terminology: [卡定义](../../CONTEXT.md), [单位化](../../CONTEXT.md)

### R4: 执行语义（等效于卡牌效果）

- Status: Confirmed
- **Decision（2026-10-21 裁决，P2）**：原语的执行语义＝**等效于卡牌效果**——该发的信号全发、该触发的监听全触发、该跑的链照跑（装载、落位、加入、销毁、抽牌、点数变更），与"一张卡的效果做同样的事"完全等价，不多不少。
- **Decision（唯一差别）**：**只跳过校验，保留硬门槛**
  - **跳过（校验层）**：指挥点是否够、是否己方回合、卡是否在该玩家手牌、相位是否允许、是否需要选靶交互、行动次数/行动费用。
  - **保留（硬门槛＝引擎结构不变量）**：阵线容量（支援线/前线各 5 槽）、槽位被占、单位已单位化、实例唯一归属（不可同时在两个区域）、已销毁实例不可放置。
  - **手牌上限不属硬门槛**：满手按引擎既有**爆牌**裁决（`CardPlaceStatus.Burned`，效果侧同口径、属成功语义），不视为失败。
  - 硬门槛被触碰＝**明确失败且零状态变更**（不是"尽力而为"，也不是"静默跳过"）。
- **Decision（推论）**：不做"内部静默重算"的第二实现——派生状态一律经既有信号链自然重算（与效果侧同一口径，避免两套实现漂移）。
- Scenario/Trigger: 原语把一张单位单位化到槽位 3，而场上有"全体友军 +1 攻击"的光环与"每当单位加入，抽一张牌"的监听卡。
- Behavior: 单位化成功 → 发 `card.placed` 与 `unit.joined`；光环按 `card.placed` 重算、监听卡按 `unit.joined` 抽牌。二者都会发生——这正是"等效于卡牌效果"。
- Acceptance:
  - [ ] 同一局面下，"用原语造一个单位"与"用一张效果卡造同一个单位"产生的信号序列一致（逐条对齐）。
  - [ ] 硬门槛被触碰时返回明确失败，且对局快照逐字段不变。
  - [ ] 校验层不生效：点数不足、非己方回合、卡不在手牌时，原语仍可成功。
- **Decision（审计留痕 D3）**：额外留一条 Log 级留痕（`EventStream.WriteLog`，只进日志不进总线、不产段），标明原语名与入参，用于复盘时区分"造的"与"打出来的"。理由：P2 下事件流里两类动作形态相同，不留痕则不可辨。
- **已否决（D4 → 移入第二批）**：`set_stats` 不进本次主验收，语义留待实现期裁决（默认改修饰基线）。
- Terminology: [更新（Update）](../../CONTEXT.md), [修饰器](../../CONTEXT.md), [单位化](../../CONTEXT.md)

### R4a: ~~`set_turn` 的例外语义~~（已作废）

- Status: Rejected（2026-10-21 用户裁决）
- 结论：**不提供 `set_turn`**。需要切换行动方时由调用方连续 `match.end_turn` 推进；回合钩子照跑，与真实对局一致。
- 保留本节仅为记录否决理由，不再具有任何验收含义。

### R5: MCP 服务层封装

- Status: Confirmed
- Scenario/Trigger: agent 经 MCP 在沙盒里造局面。
- Behavior: 原语以 `sandbox.*` 写类工具暴露，沿用既有门禁（`--mode sandbox` ＋ `--allow-debug-writes` ＋ 回环未绑定座位）；返回结构给"改变了什么"的可核对摘要（ref / 槽位 / 前后值）。
- **Decision（命名与旧工具关系，2026-10-21 裁决）**：原语工具统一命名 `sandbox.<动词>`（`instantiate` / `unitize` / `move` / `remove` / `draw` / `points`）。**既有 `sandbox.spawn` / `kill` / `damage` / `set_points` / `emit` 一律保留、行为与返回结构零变更**（R7），原语工具与之并存；旧工具在描述里标注"逐步由原语替代"，但不得改其语义。命名差异刻意体现语义差异：`sandbox.set_points` 是**增量**（`delta`，负值失败），`sandbox.points` 是**绝对值**（`points`，负方向走 `point.lost`）。
- Acceptance:
  - [ ] 每个原语一个工具；未开 `--allow-debug-writes` 时不在 `tools/list` 中。
  - [ ] 工具返回可用于下一步调用（给出新建实体的 ref）。
  - [ ] 参数错误 → `invalid-arguments`，引擎拒绝 → `engine-rejected`，不出现 `internal`。
- Terminology: [调试 API](../../CONTEXT.md)

### R6: 三条链的验证与回归

- Status: Confirmed
- Scenario/Trigger: 无头沙盒里验证"指令使用 / 反制使用 / 新卡生成与加载"。
- Behavior: 用原语（而非正常出牌链）摆出局面，跑通三条链，并把用例固化为自动化测试；同时以文档记录实测结论。
- Acceptance:
  - [ ] **指令链**：造指令卡 → 注册进当前对局 → 入手 → 补足费用 → `play.command` → 断言 `card.played`、扣费、离手、主动效果生效。
  - [ ] **反制链**：造反制卡（含 effect handler）→ 入手 → 若当前行动方非持有方，用 `match.end_turn` 推进到其回合 → `play.counter` 激活 → `match.end_turn` 推进到对手 → 对手 `play.command` → 断言 Counter 段 `Stop`、反制一次性消耗（弃置 `card.discarded`）、指令照常扣费离手、效果未触发。
  - [ ] **新卡链**：`card.write` → `card.load` → 原语实例化 → 打出 → 断言效果按预期触发（含一条 negative 用例：非法定义被拒）。
  - [ ] 三条链的用例在 `dotnet test OrcEngine.slnx` 中可重复通过。
- Terminology: [指令卡](../../CONTEXT.md), [反制卡](../../CONTEXT.md)

### R7: 既有链路零回归

- Status: Confirmed
- Scenario/Trigger: 全量测试。
- Behavior: 新增面全部为加性；`play` 模式、正常动作链、既有沙盒工具行为不变。
- Acceptance:
  - [ ] `dotnet test OrcEngine.slnx` 全绿（基线 2310 ＋ 新增）。
  - [ ] 既有 `sandbox.spawn` / `kill` / `damage` / `set_points` / `emit` 行为与返回结构不变。
  - [ ] 未授权实例与新授权实例在"不调用原语"时的行为逐字节一致（事件流段数、摘要）。
- Terminology: —
