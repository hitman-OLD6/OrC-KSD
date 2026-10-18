# Requirements — 判定器玩家分域（对局合法性判定按玩家视角化）

> 状态：需求 grill 已完成（R1–R6 全部 Confirmed）；实现 S1–S5 已落地（全量测试 1612 项绿）。偏差与缺口见 `implementation.md` 文末。

一句话：让对局引擎的合法性判定器从"对局级单例、无玩家身份"变为"每个玩家各有一套可独立解析与改写的判定域"，从而支持按玩家/按来源的精细拦截（如"本单位无法被敌方指令指向"）与玩家绑定的操作准入（如"仅在友方回合内可打牌"）。

## Scope

- Includes：
  - 判定器注册表的**玩家分域**（每人一套域，同源实例化；插件注册面不变）
  - **目标候选名单判定器**（`targeting.candidate.eligibility` 改造：产出名单而非单项 bool）
  - 单位"可选性变化"的**声明与包装**（包装判定器、`After` 链叠加、来源离场自动撤销）
  - **回合归属准入**下沉为判定器（三处入口同源）
  - 真人 / agent / 启发式 AI 共用判定入口
- Excludes：
  - UI 投影与前端提示（不含"为什么不可选"的文案）
  - AI 策略（选什么动作；`HeuristicPolicy` 不在范围）
  - 数值修饰链（攻击/防御/费用有效值）
  - 判定器枚举**可行动作**集合（仍由管理器枚举）
  - 交战目标/守护/拦截的规则变更（"不可被指向"不作用于它们）
  - 隐蔽机制语义变更（既有剔除与 `includeCovert` 请求级放宽保持）
  - 联网重连时判断域状态的重建（另案）

## Constraints

- 判定器名（`JudicatorNames`）为冻结契约；新增 `action.turn.owner` 需同步 `GameHooks.JudicatorNameList` / `GameHooksJson` / `GameEntryPoints`。
- `targeting.candidate.eligibility` **签名变更**（D10）需申报迁移说明；`JudicatorRegistry` 增加玩家维度（`Resolve(name, player)`）允许破坏性变更，但**保留无 player 重载走默认域**（保证独立构造路径与测试夹具可用）。
- 判定器**实例**无状态；玩家域只持有 wrapper 栈，不持有对局状态（已同步 `CONTEXT.md`）。
- 玩家判定域**不可被其他玩家读取**：只解析自己的域；跨域写入仅由卡效果按显式声明发起。
- 同一条目多条 `After` 按注册序执行；**各限制必须为合取且与顺序无关**（顺序不改变最终结果）。
- 包激活/装配期：插件只面向全局条目注册，不感知玩家（`Player` 由宿主分桶）。

## Requirement Items

### R1: 判定归属形态（隔离同源）
- Status: Confirmed
- Scenario/Trigger: 任一玩家（真人 / agent / 启发式 AI）发起操作，引擎判定该操作是否合法。
- Behavior: 每个玩家持有自己的一套判定器域，由同一份装配定义实例化，默认逻辑逐字一致；差异只能经显式 wrapper 注入产生，不允许"某玩家天生一套不同实现"。
- Acceptance:
  - [ ] 同一局面、同一操作，在任一玩家域下判定基准结论一致（无任何包装时）
  - [ ] 玩家域可独立解析条目、独立挂载/撤销包装器，互不串改
  - [ ] 无 `Player` 的独立构造路径退化到默认域，行为与改造前一致
  - [ ] 真人 / agent / 启发式 AI 走同一判定入口
- Terminology: [判定器](../CONTEXT.md)

### R2: 目标候选名单判定器
- Status: Confirmed
- Scenario/Trigger: 发起者（单位/指令卡）需要获取可指向的目标名单。
- Behavior: `targeting.candidate.eligibility` 从"单候选 → bool"改造为"（发起者视角，选择描述）→ 候选名单"；名单由判定器 handler 产出，targeter 不自建候选。输入复用 `EffectSelector`（`sel/side/zone/keyword/count`）。覆盖**交互式索敌 + 无头选靶**两条路径；`effect.target.resolve` 内部调用该名单判定器（单真源）。候选阶段即剔除，提交侧保留单项复核。
- Acceptance:
  - [x] 名单由判定器 handler 产出（targeter 只消费）
  - [x] 交互式索敌与无头选靶对同一限制给出相同名单（交互入口 `EffectRuntime.BeginTargetSelectionAsync`）
  - [x] `effect.target.resolve` 不复制过滤逻辑（内部调用名单判定器）
  - [x] 提交名单外的引用被拒绝
  - [x] 攻击目标、守护/拦截不受本限制影响
  - [x] 隐蔽单位仍被默认剔除；`includeCovert` 请求级放宽保持可用（迁入 `EffectSelector`）
- Terminology: [判定器](../CONTEXT.md)、[隐蔽](../CONTEXT.md)

### R3: 单位可选性声明
- Status: Confirmed
- Scenario/Trigger: 某单位带"本单位无法被敌方指令指向"类效果，敌方打出指向型指令。
- Behavior: 单位通过**包装名单判定器**声明可选性变化（不新建独立排除列表）；包装逻辑从名单中剔除受影响目标。作用域＝方向（`opponent`/`friendly`/`any`）× 来源卡类型（`command`/`unit-effect`/`any`）；"无法被敌方指令指向"＝`opponent + command`。多条限制经 `After` 链 FIFO 叠加（合取），来源离场/死亡/被沉默时自动撤销。
- Acceptance:
  - [x] 单个单位声明后，该目标从其敌方指令的名单中消失（`TargetingDeclaration` 测试）
  - [x] 两个不同来源同时声明时两条限制同时生效（`After` 合取；全局层 + 玩家层）
  - [x] 单位效果（非指令卡）发起的选靶不受"不可被敌方指令指向"约束（`Source` 维度）
  - [x] 来源离场后限制自动消失，名单恢复（`Unmount` + 求值在场校验）
  - [x] 声明能力覆盖：静态词条 + 运行期动态（DSL `targeting.forbid`/`targeting.allow`）
- Terminology: [判定器](../CONTEXT.md)、[包装器](../CONTEXT.md)

### R4: 玩家绑定的回合准入
- Status: Confirmed
- Scenario/Trigger: 非行动方玩家尝试打牌 / 移动 / 攻击 / 使用反制。
- Behavior: 新增判定器 `action.turn.owner`（`(player) → bool`）；`PlayManager.CheckAdmission`、`LegEligibilityJudicator`、`CounterUseJudicator` 三处同源调用，各自保留既有拒绝类别映射。只下沉"回合归属"一条，不下沉手牌/费用/相位准入链。
- Acceptance:
  - [x] 三处入口对"是否当前行动方"的结论一致（同一 `action.turn.owner`）
  - [x] 改造前后行为逐字一致（全量回归绿）
  - [x] 该判定器可在玩家域内被包装（两层管道）
  - [x] `CounterCard` 既有"仅己方回合"规则保持不变
- Terminology: [回合](../CONTEXT.md)

### R5: 三类玩家同源
- Status: Confirmed
- Scenario/Trigger: 真人 / agent / 启发式 AI 对同一局面做合法性判断。
- Behavior: 三者走同一判定入口（`CanAcceptInput` 已注入 `CommandManager`/`PlayManager`）；无前端状态时按原生规则放行；同一玩家域内可被同一限制包装。
- Acceptance:
  - [ ] agent/AI 发起操作时经过同一 `action.input.allowed` 与名单判定器
  - [ ] 同一局面下三者得到相同的合法性结论（无前端状态差异时）
- Terminology: [判定器](../CONTEXT.md)

### R6: 兼容与迁移
- Status: Confirmed
- Scenario/Trigger: 既有插件、测试夹具、独立构造路径在改造后仍可运行。
- Behavior: 判定器名保留（`targeting.candidate.eligibility` 仅改签名）；新增 `action.turn.owner` 纳入冻结名单；`Resolve(name)` 无 player 重载走默认域；`JudicatorSelectionRule` 随名单形态调整。
- Acceptance:
  - [ ] 既有 22 条判定器名不变（新增 1 条，共 23 条）
  - [ ] 无 player 的独立构造测试无需语义修改即可通过
  - [ ] `GameHooks` / `GameHooksJson` / `GameEntryPoints` 清单与代码一致
- Terminology: [判定器](../CONTEXT.md)
