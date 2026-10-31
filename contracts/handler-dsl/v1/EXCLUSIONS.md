# 排除清单 v1

> 本清单属契约包（R27），**可审查、可测试**。
>
> **职责边界（D10）**：本文件只解释「为什么这个**已登记门面**里的方法不进目录」，
> **不承担「发现新门面」**——那由守护测试的 `*ActionFace` 漏标断言负责。两者职责不得混。
>
> **不是** `docs/卡牌DIY开发文档/reference/缺口表.md`：排除清单＝「图形化表达不了」，缺口表＝「引擎层面没有」。
> 两者可重叠，但判定标准不同，不得互相替代、不得合并（R27／D1 配套）。
>
> **代码侧唯一真源**＝`src/Orc.HandlerDsl/HandlerDslExclusions.cs`；本文件是人读镜像，
> 两侧条目集由 `ExclusionGuardTests.代码侧清单与EXCLUSIONS点md同步` 逐条比对（文档写了代码没排、代码排了文档没写，都判失败）。

## 条目格式（封闭，三项全填）

| 方法 | 理由 | 替代路径 |
| --- | --- | --- |

- **理由**必须说明「为什么默认映射规则 ＋ 标注都表达不了」，不接受「暂不支持」这类空缺说法。
  **能表达但本轮不做的，必须写明「可以拆、本轮未拆、补什么就能进目录」**——理由写错会让后来者以为
  这条路走不通，从而在别处重复踩坑。
- **替代路径**必须是可执行的：节点组合（写出具体 op 序列），或明确写「只能脚本节点」。
- 新增条目必须同时补齐三列——守护测试按三态判定（默认规则／标注／本清单）落地，清单不许悄悄变长。

## 分区（两种性质，不得混写）

| 分区 | 含义 | 处置 |
| --- | --- | --- |
| **A. 结构性排除** | 现有表达手段（默认规则 ＋ 6 个标注）**都表达不了** | 长期有效，除非契约扩容（须升 `schemaVersion`） |
| **B. 延后** | **技术上可拆**（补标注即可进目录），只是本轮不做 | 补标注后即移出本清单，不需要改契约 |

## 守护测试口径（S5 落地）

**一个测试、三态判定**：遍历集＝带 `[HandlerDslActionFace]` 的类型集合，对其中每个 public 方法断言
**三态必居其一**：①走默认规则能生成 ／ ②有标注 ／ ③在本清单里。三者都不满足＝测试失败，
错误信息给出方法名与建议（补标注 or 进清单）。

另加一条**漏标断言**：程序集内名字匹配 `*ActionFace` 的 public 类型必须带 `[HandlerDslActionFace]`，
否则测试失败（兜住「按约定新建门面却忘了登记」）。

---

## A. 结构性排除（现有手段表达不了）

| 方法 | 理由 | 替代路径 |
| --- | --- | --- |
| `ResolveFor` | 静态工厂，返回运行时实例（`EffectRuntime`）；不是状态变更动作，返回值也不在封闭集内 | 生成码内部使用（动作节点调用的接收者解析），作者无需在图上调用 |
| `Judge` | 变参 ＋ `object` 载荷，封闭集表达不了；判定器名字面是另一套符号系统（`JudicatorNames`） | 用 `read` ＋ 比较算子组合（`eq`／`lt`／`ge`…）表达同一条件，或改用脚本节点 |
| `EvaluateCondition` | 入参是条件表达式字符串（`count=s=enemy:gte:#2` 一类），等于在 DSL 里开第二套 DSL（R27 明令不许） | 用表达式树表达同一条件（`read` ＋ 比较算子 ＋ `and`／`or` 组合） |
| `AurasOf` | 返回 `AuraRegistry`（引擎对象），不在封闭集内；且它只是取注册表，本身不是动作 | 只能脚本节点；常规持续态走 `DeclareAuraAsync` 的动作节点 |
| `SelectAsync` | `EffectSelector` 是 record 参数（含阈值与阵营多字段），`[HandlerDslPin]` 只能覆盖**单个**参数的类型，无法把 record 展开成多引脚（R16 不提供结构体）；且它是**交互**语义 | 用 `ask` 节点（R23）；纯查询另由 `read`／聚合算子表达 |
| `DeclareAuraAsync` | `EffectAuraFilter` 是 record 参数（阵营／兵种／词条／区域／排除自身五字段），封闭集表达不了，也没有把 record 展开成多引脚的标注 | 只能脚本节点；或改用 `buff` 动作节点表达一次性修饰 |
| `AskAsync` | 内建 `ask` 节点的**运行时实现**（交互通道降级策略的落点），不是给作者连线的原语 | 用 `ask` 节点：本方法只由生成码调用 |
| `ChooseAsync` | 内建 `choice` 节点的**运行时实现**（选项呈现），不是给作者连线的原语 | 用 `choice` 节点：本方法只由生成码调用（**注意**：见下方「运行时能力现状」） |
| `ReadValue` | `read` 变体的**运行时读取入口**（`read.path` → 实时值），不是给作者连线的原语 | 用 `read` 变体：本方法只由生成码调用 |
| `OwnerUnits` | **查询**而非动作：首参 `host` 是「查谁的」这个数据参数，不是执行宿主；却因名字落在隐式宿主白名单（`host`／`viewer`／`self`）里被当成服务宿主，生成的 `host: orcSelf` 还附加 `Card`→`CardBase` 的向下转换失败 | 用 `read`（属性读取）或脚本节点；若要「遍历某人拥有的单位」，应由新增的只读 path／目录节点承担，而不是伪装成动作节点 |

## B. 延后（可拆，本轮未拆；补标注即可进目录）

| 方法 | 理由 | 替代路径 |
| --- | --- | --- |
| `RevealAsync` | 返回 `RevealOutcome`——**可以**用 `[HandlerDslResult]` 拆成标量输出（如 `status:string:enum`），**本轮未拆**（不是结构上拆不了）；按 R25 第 3 条，结果对象未标注＝目录生成失败，故本轮先排除 | 补 `[HandlerDslResult]` 拆解后即可进目录（本轮未做）；在此之前只能脚本节点 |
| `JoinFrontlineAsync` | 返回 `ActionResult?`——**可以**用 `[HandlerDslResult]` 拆（如 `status:string` ＋ 目标数），**本轮未拆**；未标注时目录生成失败，故本轮先排除 | **优先补**：补 `[HandlerDslResult]` 拆解后即可进目录——这是「把单位塞进战场」的核心动作 |
| `JoinSupportLineAsync` | 同 `JoinFrontlineAsync`：返回 `ActionResult?`，**可以**拆，**本轮未拆** | **优先补**：补 `[HandlerDslResult]` 拆解后即可进目录——核心动作 |

其余动作面方法（`DamageAsync`／`KillAsync`／`BuffAsync`／`GrantAsync`／`DrawAsync`／`FetchFromDeckAsync`／
`PickHandCardsAsync`／`RerunAurasAsync`／`DeathAsync` …）靠
「默认映射规则 ＋ 结果对象拆解标注」进目录，**不排除**。

## 运行时能力现状（不改变节点可用性，只是不欺骗）

以下是**节点/表达式在目录里可用、但运行期能力尚不完整**的如实清单。它们不是排除条目，
**不参与**三态判定；写在这里是为了让前端与作者知道「什么时候会当场失败」。

| 能力 | 现状 | 失败形态 |
| --- | --- | --- |
| `choice` 节点 | 运行时入口 `ChooseAsync` **抛 `NotSupportedException`**：选项呈现需前端桥接（S10） | 运行期当场抛错（**不返回「无选择」哨兵**——那会让 `switch` 走 default／越界，等于「生效到错误分支上」） |
| `ask` 的 `prompt` | 生成码会传，但**运行期未使用**（提示文案的呈现面属前端桥接 S10） | 无（不影响选靶结果） |
| `ask` 的 `kind` | 生成码会传，但**运行期未使用**：候选集由选择器与判定器给出，`kind` 只参与**静态**子类型判定（SPEC §7.5） | 无（DSL 层校验仍生效） |
| `read` 的可读项 | `self`：attack／defense／opCost／deployCost／hqHealth／name／zone；`viewer`：kredits／maxKredits／hqHealth；其余 path **抛 `NotSupportedException`** | 运行期当场抛错（**不返回 0**——那时条件会被判成成立，比失败更糟） |

## 相对 D10 初版 5 条的增量（必须明示，不许悄悄变长）

D10 定的是初版 5 条（`ResolveFor`／`Judge`／`EvaluateCondition`／`AurasOf`／`SelectAsync`）。
落地时按 SPEC §12.1「参数类型不在封闭集内＝标注拆解 or 进 EXCLUSIONS.md」又追加若干条，分三类：

1. **record 参数无标注可拆**（`DeclareAuraAsync` 的 `EffectAuraFilter`）——R16 不提供结构体，
   `[HandlerDslPin]` 只能覆盖**单个**参数的类型，无法把一个 record 展开成多个引脚。→ 结构性
2. **内建 op 的运行时入口不该成为图上的节点**（`AskAsync`／`ChooseAsync`／`ReadValue`）——
   它们与 `ask`／`choice`／`read` 是同一能力的两面，列成节点就是「同一语义两个节点」；
   且 `AskAsync` 派生名直接撞内建 op `ask`（扫描期失败）。→ 结构性
3. **结果对象本轮未补拆解标注**（`RevealAsync`／`Join*Async`）——**可拆但本轮不做**。→ 延后（优先补 `Join*`）

## 新增动作落点（写进本清单旁边的操作约定，避免误读）

- **新动作优先加在 `KeywordEffectActionFace`**（非冻结门面）：引擎侧零仪式，不改本包、不升 `schemaVersion`，
  前端只需刷新目录。
- 若加在 `EffectRuntime`（冻结面）：先过引擎自己的 `ContractFreezeTests`（重生成 `contract-freeze.json`，
  只能走 `ORC_WRITE_BASELINE=1` ＋ `BaselineGeneratorTests`）。这是**引擎侧**的契约变更，与前端无关。
- 「前端零改动」的准确含义＝**前端代码与版本零改动、只需刷新目录**；它从不承诺「引擎侧零仪式」。
