# Requirements Grill Plan — 判定器玩家分域

> 本轮 grill 聚焦：名单判定器的覆盖范围（哪些 targeter 走它）、玩家判定域的解析入口、回合准入口径统一。

## 基线 v2（2026-10-18 复核；moding 已全面替换为 wrapper）

### 包装器机制（取代 moding）

- 三类相位（`src/Orc/Core/Wrapper.cs:7-17`）：
  - `Before`：核心前 **LIFO**，`Action<object[]?>`，**可就地改写参数**，不可短路；
  - `After`：核心后 **FIFO**，`Func<object[]?, object[]?, object[]?>`，逐层改写结果；
  - `Replace`：与核心同签名，**最后一个未注销者生效**，只替换核心段。
- 管道顺序（`src/Orc/Core/UnifiedServices.cs:156-173`）：`Before(LIFO) → Replace 或默认核心 → After(FIFO 逐层改写)`。
- 注册面：`RegisterBeforeWrapper` / `RegisterAfterWrapper` / `RegisterReplaceWrapper` / `UnregisterWrapper`；插件声明面 `DeclareXxxWrapper(WrapperTarget.Judicator, name, ...)`；句柄 `JudicatorWrapperRegistration`。
- 残留检查：`RegisterModing` / `DeclareModing` / `ModingTarget` 在 `src` 中 **0 匹配**；仅注释与 docs 仍用旧词。
- 既有用法：`BuiltInPlugin.DeclareKeywordRule` 已改用 `DeclareReplaceWrapper`（`BuiltInPlugin.cs:110`）。
- 测试：`tests/Orc.Tests/JudicatorWrapperTests.cs`、`WrapperPipelineTests.cs`、`TriggerWrapperTests.cs`。

### 未变化的基线

- 判定器注册表仍是**对局级单例**：`Match._judicators`（`Match.cs:47/528`），`Register(string, Judicator)` 无玩家参数。
- `ValidationJudicator` 签名仍为 `(refs, subject)`，**无 player**。
- `Player` 类仍无规则/判定器字段。
- `targeting.candidate.eligibility` 生产仍无消费方（仅测试与 `LegacyTargetingShims`）。
- 判定器名 22 条不变。

## 已对齐决策

- **D1（Q1=c）**：每人一套判定器域，同源实例化，默认逻辑逐字一致；差异只能经显式注入。
- **D2（Q1a）**：差异来源＝卡/单位效果带来的独特规则。
- **D3′（Q1b + C5①）**：判定器**不枚举可行动作**（打牌/移动/攻击由管理器枚举）；但**可以产出目标候选名单**。
- **D4（Q1c）**：判定域归属＝操作发起者；对方卡牌可改写己方判定器。
- **D6（Q2a）**：注入可指向对手域或自己的域；跨域需在场来源卡锚定。
- **D7（Q2b）**：幅度双向；跨域放宽需显式声明且来源在场。
- **D8（Q2c）**：来源离场/死亡/被沉默 → 注入自动撤销。
- **D9（Q3）**：targeter 的可选名单由判定器 handler 产出；单位声明可选性变化时**包装该判定器**。
- **D10（Q11=a）**：**改造现有** `targeting.candidate.eligibility`：签名改为 `(发起者视角, 选择描述) → 名单`；名字保留。理由：该判定器生产无消费方，改造面最小；避免"名单判定器 + 单项判定器"双真源。
- **D11（Q11a）**：名单判定器输入**复用 `EffectSelector`**（`sel/side/zone/keyword/count`）+ 发起者视角。
- **D12（Q11b）**：提交侧**保留单项复核**（防名单外引用伪造）。
- **D13（Q11c）**：单位声明的包装相位＝**`After`**（拿到名单后剔除受影响目标，FIFO 天然叠加）。
- **D14（Q4=b）**：名单判定器覆盖**交互式索敌 + 无头选靶**两条路径；不含交战目标、守护/拦截。
- **D15（Q4a）**：候选阶段即剔除（前端不可见）；提交侧再按 D12 复核。
- **D16（Q4b）**：`effect.target.resolve` **内部调用**名单判定器（单真源，不复制过滤逻辑）；不合并为同一条判定器。
- **D17（Q4c）**：`不可被指向`不影响攻击目标与守护/拦截；若需要"不可被攻击"另立独立限制类型。
- **D18（Q9=a）**：插件注册面**不需要**玩家维度——插件只注册一次全局条目，玩家域由宿主实例化/分桶。
- **D19（Q9a）**：独立构造路径（无 `Player`）统一退化到**默认域**（无玩家键，行为等同现状）。
- **D20（Q9b）**：玩家域键＝`Player.Index`（0/1），域生命周期挂在对局上。
- **D21（Q5=b）**：限制作用域＝**方向**（`opponent`/`friendly`/`any`）× **来源卡类型**（`command`/`unit-effect`/`any`）；"无法被敌方指令指向"＝`opponent + command`。
- **D22（Q5a）**：方向词汇复用 `KeywordProgram.Subjects`；来源类型新增 `command`/`unit-effect`/`any`。
- **D23（Q5b）**：agent / 启发式 AI 与真人同权（同一限制、同一判定域）。
- **C1 已确认**：`CONTEXT.md` 的"判定器"定义已改为"实例无状态 + 注册表按玩家分域 + 域内全局生效"。
- **D24（Q6=a）**：回合准入**统一下沉**为判定器；只下沉"回合归属"一条，不下沉整条准入链。
- **D25（Q6a）**：三处"owner == current"语义一致，下沉不产生行为差异。
- **D26（Q6b）**：新增判定器名 `action.turn.owner`（不并入 `action.input.allowed`）。
- **D27（Q6c）**：需要"卡效果改写回合准入"的用例（作为下沉的价值验收）。
- **D28（Q7，由代码确认）**：真人 / agent / 启发式 AI 已共用同一入口——`CanAcceptInput` 注入 `CommandManager`（`Match.cs:641`）与 `PlayManager`（`Match.cs:719`）；无前端状态时 `InteractionStateReader == null`，判定器按原生规则放行。
- **D29（Q12=c）**：可选性声明的能力覆盖**静态卡数据 + 运行动态赋予/剥夺 + 卡面文本/DSL 可表达**。
- **D30（Q13）**：玩家判定域**不可被其他玩家读取**；跨域写入仅由卡效果显式发起。
- **D31（Q14）**：多条 `After` 按注册序执行，不引入显式优先级；各限制必须是合取且与顺序无关。
- **D32（Q8）**：允许 `targeting.candidate.eligibility` 签名变更与 `JudicatorRegistry` 加玩家维度；保留无 player 重载走默认域。

## 代码 ↔ 陈述矛盾（需仲裁）

### C1: CONTEXT 的"判定器"定义与"玩家域 + 动态注入"冲突 【Pending 确认】
- `CONTEXT.md:88`："不含对局可变状态；装配期加载……全局生效"。
- 建议措辞：判定器**实例**无状态；其**注册表**按玩家分域持有装配期配置与运行期 wrapper 栈（"玩家域内全局生效"）；对局状态只经调用载荷进入。

### C2: "精准改写敌方指令选目标的判定器"当前没有可改写的对象 【由 D10 解决】
- D10 改造后，生产索敌必须接到该判定器上；接线范围见 Q4。

### C3: 判定器结果被用于裁剪候选 【已消解（D3′）】
- 枚举/裁剪归调用方；判定器可产出目标名单，但不产出可行动作。

### C5: D9 与 D3 的冲突 【已消解（C5①）】
- D3 → D3′：限定于"可行动作"，目标名单可枚举。

## Unresolved Questions

### Q4: 名单判定器的覆盖范围 【已定：D14–D17】

### Q5: "敌方指令"的界定口径与作用域词汇 【已定：D21–D23】

### Q6: 回合准入是否统一下沉为判定器 【In Progress】
- a) 全部统一下沉：新增 `action.turn.eligibility` 类判定器；`PlayManager.CheckAdmission` 直写 if、`LegEligibilityJudicator`、`CounterUseJudicator` 三处同源。
- b) 只改 `PlayManager.CheckAdmission`（最小改动），其余保持。
- c) 不动。
- 子问 Q6a：反制卡现状强制"仅己方回合可用"（`CounterUseJudicator.cs:26` `EvaluateUse(TurnPlayerProvider)`）——这是期望行为还是 bug？（反制通常响应对方动作）
- 子问 Q6b：拒绝原因是否保留细分（`NonOwnerTurn` 等）？
- 子问 Q6c：是否允许卡效果修改回合准入（"本回合可额外打一张"）？这决定下沉的价值。

### Q7: 三类玩家共用与差异化 【已由 D28 确认】

### Q8: 兼容与破坏性变更许可 【已定：D32】

### Q12: "可选性变化"的声明能力覆盖范围 【已定：D29（c）】

### Q13: 玩家判定域的可见性 【已定：D30】

### Q14: 多条包装器的顺序确定性 【已定：D31】

---

## 状态

无未决问题。需求文档见 `requirements.md`（R1–R6 全部 Confirmed），待用户确认后退出需求 grill，进入 implementation grill。

### Q9: 插件注册面是否需要玩家维度 【已定：D18–D20（a）】
- 备注（留 implementation grill）：存储形态候选——①每玩家完整注册表；②单注册表 + 条目内 wrapper 栈按玩家分桶 + `Resolve(name, player)`；③单注册表 + 载荷带 player。独立构造路径走默认域。
