# Requirements Grill Plan — wrapper（洋葱包装器）语法

> 本轮 grill 聚焦：把现有 moding（纯替换）升级为「前/后触发 wrapper + replace wrapper + 动作缝」的管道结构。
> 当前状态：需求 grill 第 3 轮；Q1/Q2/Q3 已确认（D1–D4），Q4 已发出，其余为登记待问。

## 已确认的代码基线（供 grill 取证，不再提问）

- 触发器 handler 签名唯一：`Func<TView, Context, CancellationToken, Task>`，**无返回值变体**（`src/Orc/Core/Trigger.cs:184/195/211`）。
- 触发器 moding：条目内 `_modings` 列表，解析点 `item.LastModing ?? item.Handler`（`Trigger.cs:531-532`、`:789-790`）；语义＝栈（最后者胜、注销回退）、**纯替换、无 next**；注册项撤销时 `ClearModings()`（`:245`）。
- 服务级 moding：`ServiceEntry` 内同构栈（`src/Orc/Core/UnifiedServices.cs:59/79-83`）；双形态＝统一签名 `Func<object[]?, object[]?>` ＋ 强类型 `IStrongTypedService<TDelegate>.Adapt`（`:211/231/263`）。
- 判定器：abstract `Invoke(object[]? args)`（`src/Orc/Core/Judicator.cs:19`）；18 个强类型子类的 `Adapt` 全部为 `args => new object[]{ handler(Unpack<T>(args,i)...) }` 形态。
- 现有对外暴露面：
  - csx/插件：`ModingTarget { Judicator, AssetService }`、`DeclareModing`（`src/Orc/Core/PluginScripting.cs:25-32/73`）；`ApplyModings` 无 Trigger 分支（`src/Orc.Packages/PluginRegistrationContext.cs:107-123`）。
  - 效果预制体：`modings[] = { target, replacement }`（`src/Orc/Cards/PrefabJson.cs:273/301-305`）→ `DynamicEffect.cs:148-162` → 反射桥 `TriggerReflection.RegisterModing`。
  - 审计读面：`TriggerEventInfo.ModingCount`、`AuditChainText.cs:61`、`AuditChainJson.cs:97`。
  - `BuiltInPlugin.DeclareKeywordRule`（`src/Orc.Plugins.BuiltIn/BuiltInPlugin.cs:104-115`）用"直接调用基线实例"绕过自引用。
- 现有具名插入点：band（`Register(..., Enum band, ...)`）、保留槽（`RegisterDisposal`）、模板槽位 `mainTrigger.events[<eventId>]`。
- `seam` / `hookPoint` / 机制级"动作缝"：**不存在**；只有 `src/Orc.Game/Managers/ResourceManager.cs:166` 一处自然语言注释提到"动作缝"，非机制。
- 术语表：`CONTEXT.md:88/91/147/295` 使用 **moding**（判定器/资产服务/插件/调试 API 条目）。

## 已对齐决策

- **D1（Q1=d）**：执行顺序＝前触发 LIFO、后触发 FIFO。
  即 `w2 → w1 → core → w1 → w2`（w1 先注册、w2 后注册）。
  补充口径：**执行顺序对注册者不可见**——不提供 priority/顺序参数，注册者只声明"前/后"与挂载目标；链序由引擎确定。

- **D2（Q2=a）**：形态＝**管道式（pipeline）**，不采用"洋葱式"称谓。
  理由：洋葱式要求 wrapper 显式持有并调用 next、在核心前后各执行一段；本需求不需要此严格性。
  执行流＝全部前触发 wrapper 依次执行完 → 核心 handler → 全部后触发 wrapper 依次执行 → 输出。wrapper 不接触 next。

- **D3（Q3）**：前触发 wrapper 的签名与能力：
  - 签名＝`(TView, Context, CancellationToken) -> Task`（异步、无业务返回值；"void"在异步语境下按 `Task` 落地）。
  - **允许就地改写参数**：Trigger 侧改写 `Context` 内数据（`TView` 为密封只读视图，不可改）；Judicator 侧就地改写 `object[]` 元素。
  - **不可替换入参引用本身**（不引入 `ref`／不返回新 args），**不可短路**（不跳过后续 wrapper 与核心 handler）。
  - 顺序对注册者不可见，但改写按管道顺序叠加、结果可依赖顺序；注册者不获得顺序信息。

- **D4（Q3 追加）**：新增第三类 **replace wrapper**，承接原 moding 的纯替换语义。
  - 签名与目标 handler 完全一致（Trigger：`(TView, Context, CancellationToken) -> Task`；Judicator：`(object[]?) -> object[]?`）。
  - 解析＝**最后一个未注销者生效**（沿用 moding 栈语义、注销回退上一项）。
  - 作用范围待 Q4 确认（只替换核心段／替换整个条目管道）。

- **D5（Q4=a）**：replace wrapper **只替换核心段**——前触发 wrapper → replace → 后触发 wrapper；前/后 wrapper 仍执行，三类 wrapper 正交。
  无前/后 wrapper 时，旧 moding 行为逐字不变。

- **D6（Q5=a）**：后触发 wrapper 的入参（Judicator）＝`(object[]? args, object[]? result) -> object[]?`——同时拿到原参数与核心结果，返回新结果。

- **D7（Q5=允许）**：Trigger 侧允许后触发 wrapper；handler 无业务返回值，故退化为**尾部副作用**，签名 `(TView, Context, CancellationToken) -> Task`。

- **D8（Q6=b）**：动作缝**属于 handler 条目**（不是触发器级扁平字典，用户已澄清口误）。
  - 显式声明：注册 handler 时声明 `seams: ["x"]`。
  - 挂载目标＝`(handler 句柄, 缝名)`；同名缝在不同 handler 上互不干扰。
  - 动作缝上的 wrapper 只能是前触发；签名与所在 handler 一致。

- **D9（Q6 子问题）**：动作缝调用入口挂在 `Context` 上（`ctx.SeamAsync("x")`）；不改现有 `(TView, Context, CancellationToken)` handler 签名。

- **D10（Q8=a）**：wrapper 签名不符＝**挂载期 fail-fast**，抛 `ArgumentException`（消息含期望签名/实际签名/目标），拒绝挂载、管道不变。

- **D11（Q7）**：未声明缝的**调用**（`ctx.SeamAsync("x")` 而 handler 未声明）与**挂载**（wrapper → 未声明缝）均 **fail-fast**。

- **D12（Q8-3）**：校验维度＝参数类型逐一**严格相等**（含 `TView` 具体类型）、返回类型相等；不允许协变/基类兼容。

- **D13（Q8-4）**：校验双轨——强类型 API 走编译期泛型约束；统一签名/脚本/预制体面走运行时反射兜底。

- **D14（Q9=a）**：**硬切**——删除 moding 全部命名并同步迁移：
  `RegisterModing`→`RegisterReplaceWrapper`、`UnregisterModing`→`UnregisterReplaceWrapper`、预制体 `modings[]`→`wrappers[]`、csx `DeclareModing`→`DeclareWrapper`、审计 `modingCount`→`wrapperCount`。
  依据：39 个 JSON 的 `modings` 全为空数组（无非空旧数据）；C# 生产调用 4 处、测试 60+ 处（机械改名）。

- **D15（Q9 子问题=a）**：预制体采用**单数组 + `phase`**：
  `wrappers: [{ phase: "before"|"after"|"replace", target: "<eventId | seam:名>", handler: {...} }]`。

- **D16（Q10=c）**：强类型 wrapper 走**通用泛型工厂**（如 `Wrapper.Before<T1>(Action<T1>)`、`Wrapper.After<T1,TResult>(Func<T1,TResult,TResult>)`），工厂内部负责 `object[]` 拆包/组包；判定器文件不动，调用方保留编译期类型约束。
  replace 的强类型面继续复用现有 `IStrongTypedService<TDelegate>.Adapt`。

- **D17（Q10 子问题）**：csx `DeclareWrapper` **不覆盖 Trigger**——`WrapperTarget` 仍只有 `Judicator`/`AssetService`。
  理由（用户）：触发器本质上是纯编排节点；Trigger 的 wrapper 经 C# 或预制体 `wrappers[]` 声明。

- **D18（Q11=a）**：**handler 级快照**——进入某 handler 前固定其前/后/replace 三组列表；动作缝在每次 `SeamAsync` 调用时固定该缝列表。执行期增删不影响本次、下一次解析点生效。理由：稳定性。

- **D19（Q12=a 及其它同意）**：
  - Trigger 前触发 wrapper 异常＝跳过该 handler 剩余部分（核心 + 后触发），记录并继续下一事件。
  - Trigger 后触发 wrapper 异常＝记录并继续，该 wrapper 不生效、输出保留核心结果。
  - Trigger replace wrapper 异常＝同 handler 异常（记录并继续）。
  - Judicator wrapper 异常＝原样传播；`CancellationToken` 透传、`OperationCanceledException` 穿透上抛。

## 代码 ↔ 陈述矛盾（需仲裁，不算实现问题）

### C1: 顺序语义三句话互相冲突 【待 Q1 仲裁】
- "无显式优先级，先到先得"（FIFO）
- "前触发……作为第一个被执行的逻辑"（新挂载者插到最前 = LIFO）
- "可被更新的 wrapper 覆盖优先级"（后注册者生效 = LIFO）
- 三者不能同时成立；且"前触发 LIFO + 后触发 FIFO"（标准洋葱）与"前 FIFO + 后 FIFO"（追加式洋葱）行为不同。

### C2: "前触发返回 void / 参数与触发器一致" 与现有异步契约不符 【已解决 — D3：统一为 `Task`】
- 现有 handler 为 `(TView, Context, CancellationToken) -> Task`，不是 `void`；异步链必须返回 `Task` 才能 await。
- 且 void 无返回值 ⇒ 无法短路、无法返回改写后的参数；"参数经过所有前触发 wrapper 链式调用处理之后输入核心 handler"只能靠改写 `Context`（可变载体），否则"处理"仅是副作用观察。

### C3: 后触发 wrapper 的输入签名未定义；Trigger 上没有"输出"可处理 【待 Q5/Q10 仲裁】
- "后触发返回值与 handler 返回值一致"——但未说参数。
- Trigger handler 返回 `Task`（无业务值），"后触发 wrapper 处理，最后输出"在 Trigger 上无输出可处理；只有 Judicator（`object[]?`）成立。

### C4: 动作缝的声明与命名空间未定 【待 Q6 仲裁】
- "每个 handler 可以有多个动作缝，通过字符串引用"——是注册时**声明**，还是 handler 内部调用即成立（隐式）？
- "触发器在内部维护字典<动作缝名, 前触发 wrapper 列表>"——字典是**触发器级**（同名缝跨 handler 共享）还是 **handler 条目级**（各 handler 独立命名空间）？
- 未声明/未知缝名被调用时的行为（静默无操作 / fail-fast）。

### C5: 纯替换在管道模型中的表达 【部分解决 — D4 新增 replace wrapper；迁移细节待 Q9】
- 若 wrapper 恒调用 next，则 `BuiltInPlugin` 的词条规则改写、预制体 `modings`、18 处强类型改写测试都无法直接映射。
- 需要决定：保留"Replace 型 wrapper"（不调用 next、直接产出结果）/ 保留 moding 兼容层 / 硬迁移。

### C6: 术语冲突 【待 Q14；"管道式"已定，拼写与旧词条目待仲裁】
- 用户写 `warpper`（标准拼写为 `wrapper`）；`CONTEXT.md` 既有权威词是 **moding**；`ResourceManager.cs:166` 已用"动作缝"指另一件事。

## Unresolved Questions

### Q13: 嵌套与递归 【In Progress — 本轮】
- wrapper 内再次调用同一 handler/判定器（重入）时的管道构建与快照。
- a) **每次进入都重新构建管道**（独立快照）；重入合法，递归终止由调用方负责（与现有 `ExecutionFrame` 嵌套语义一致）。
- b) 重入时复用外层快照（避免中途链变化，但语义复杂）。
- c) 禁止重入（检测到同一 handler 正在执行则 fail-fast／空转）。

### Q14: 术语落表【In Progress — 与 Q13 同轮收尾】
- 拼写：`warpper` → `wrapper`（确认）。
- 三类正式中文名：
  - a) 前置包装器 / 后置包装器 / 替换包装器；
  - b) 前触发包装器 / 后触发包装器 / 替换包装器；
  - c) 统一保留英文 before／after／replace wrapper。
- 新术语：**管道式（pipeline）**、**动作缝（action seam）**。
- `CONTEXT.md`：moding 词条退役并改写为"替换包装器（承接原 moding 语义）"；是否新增"触发器＝纯编排节点"定位。

### Q13: 嵌套与递归 【Pending】
- wrapper 内再次调用同一 handler/判定器（重入）时的管道构建与快照语义。

### Q14: 术语与拼写标准化 【Pending】
- `warpper` → `wrapper`；"前触发/后触发/replace wrapper"的正式中文名（前置包装器／后置包装器／替换包装器）。
- 动作缝（action seam）是否为正式术语；与 `ResourceManager` 既有自然语言用法如何区分？
- `CONTEXT.md` 的 moding 词条如何改写/退役。
