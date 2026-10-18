# Requirements — wrapper（管道式包装器）语法

> 状态：**需求已定稿**（D1–D20 全部确认）；实现 grill 进行中，见 `implementation-grill-plan.md`／`implementation.md`。

一句话：把引擎现有的 moding（单 delegate 纯替换）升级为「前触发 wrapper / 后触发 wrapper / replace wrapper + handler 具名动作缝」的管道式包装结构，使改写从"整段替换"变为"可叠加、可改写、可插入"的分段处理。

## Scope

- Includes：
  - handler（触发器事件）与判定器上的前置/后置/替换 wrapper 注册与执行管道。
  - handler 具名动作缝及其前置 wrapper 列表。
  - wrapper 挂载时的 delegate 签名校验。
  - moding 的迁移/退役路径（含预制体、csx 插件、审计读面）。
- Excludes：
  - 数值修饰链（Modifier）与光环机制（那是值变换，不是控制流包装）。
  - band / priority 事件排序机制（wrapper 不参与事件排序）。
  - 效果解析器/DSL 的语法扩展（若需要另行提出）。
  - csx 对 Trigger 的 wrapper 注册（触发器是纯编排节点）。

## Constraints

- 现有触发器 handler 为异步 `(TView, Context, CancellationToken) -> Task`；任何链式设计必须可 await。
- 判定器无状态、统一签名为 `object[]? Invoke(object[]? args)`；强类型面经 `IStrongTypedService<TDelegate>.Adapt` 适配。
- 既有"注册即配置、装配期一次成型、未注册引用 fail-fast、句柄即权限、句柄无效幂等宽容"的机制口径需明确继承或推翻。
- 快照语义（一次调用＝一次解析/快照）需明确。
- 术语以 `CONTEXT.md` 为唯一称谓来源；新术语须先仲裁后落表。

## Requirement Items

### R1: 三类 wrapper 与签名
- Status: Confirmed
- Scenario/Trigger: 外部逻辑希望介入某个 handler/判定器的执行——观察、改写入参、处理输出，或整体替换。
- Behavior: 提供三类包装器：
  - **前触发 wrapper**：签名 `(TView, Context, CancellationToken) -> Task`；在核心 handler 之前执行；允许就地改写参数（Trigger 改写 `Context`，Judicator 就地改写 `object[]` 元素）；不可替换入参引用、不可短路。
  - **后触发 wrapper**：在核心 handler 之后执行，参与最终输出。判定器入参＝`(object[]? args, object[]? result) -> object[]?`；触发器侧退化为尾部副作用，签名与 handler 同参。
  - **replace wrapper**：签名与目标 handler 完全一致；承接原 moding 的纯替换语义，最后一个未注销者生效；**只替换核心段**，前/后 wrapper 仍执行。
  - 挂载时必须校验其 delegate 签名与目标一致。
- Acceptance:
  - [x] 前触发签名 `(TView, Context, CancellationToken) -> Task`、可就地改写、不可短路
  - [x] 判定器后触发签名 `(object[]? args, object[]? result) -> object[]?`
  - [x] Trigger 后触发＝尾部副作用（与 handler 同参）
  - [x] replace 只替换核心段、最后一个未注销者生效
  - [x] 强类型 wrapper 经通用泛型工厂构造（判定器文件不动）；replace 复用现有 `Adapt`

### R2: 管道执行链
- Status: Confirmed（顺序与形态）
- Scenario/Trigger: 同一 handler/判定器上挂载了多个前/后 wrapper。
- Behavior: 执行流＝全部前触发 wrapper → 核心 handler（或 replace wrapper）→ 全部后触发 wrapper → 输出；wrapper 不接触 next。
  - 前触发 LIFO（后注册者先执行）、后触发 FIFO（先注册者先执行）。
  - 例（w1 先注册、w2 后注册）：`w2 → w1 → core → w1 → w2`。
  - 不提供显式 priority；执行顺序对注册者不可见（注册者只声明"前/后/replace"与挂载目标），但改写按管道顺序叠加、结果可依赖顺序。
- Acceptance:
  - [x] 前触发按 LIFO、后触发按 FIFO 执行（`w2→w1→core→w1→w2`）
  - [x] 不提供 priority/顺序参数
  - [x] wrapper 不接收 next、不可在核心 handler 返回后再插入逻辑
  - [x] 动作缝上的前触发 wrapper 顺序规则与 R2 一致
  - [x] handler 级快照：执行期增删不影响本次、下一次解析点生效

### R3: 动作缝
- Status: Confirmed
- Scenario/Trigger: 核心 handler 内部需要在某个业务时点插入一段可被外部包装的逻辑。
- Behavior: 动作缝**属于 handler 条目**，注册 handler 时显式声明（`seams: ["x"]`）；每个 handler 维护自己的 `动作缝名 → 前触发 wrapper 列表`；同名缝在不同 handler 上互不干扰。
  - handler 内经 `Context` 的动作缝方法调用（`ctx.SeamAsync("x")`），按该 handler 的列表顺序执行；不改 handler 签名。
  - 前触发 wrapper 既可挂在 handler 上，也可挂在 `(handler 句柄, 缝名)` 上；动作缝只接受前触发 wrapper，签名与所在 handler 一致。
- Acceptance:
  - [x] 同名缝在两个 handler 上互不串扰
  - [x] 动作缝 wrapper 按前触发顺序执行
  - [x] 调用入口在 `Context` 上、不改变 handler 签名
  - [x] 未声明缝的调用与挂载均 fail-fast

### R4: 挂载校验
- Status: Confirmed
- Scenario/Trigger: 挂载 wrapper 时其 delegate 与目标签名不符，或目标缝未声明。
- Behavior: **挂载期 fail-fast**（抛 `ArgumentException`，消息含期望/实际签名与目标），拒绝挂载且管道不变。
  - 校验维度：参数类型逐一严格相等（含 `TView` 具体类型）、返回类型相等；不允许协变/基类兼容。
  - 校验双轨：强类型 API 编译期泛型约束；统一签名/脚本/预制体面运行时反射兜底。
- Acceptance:
  - [x] 签名不符时抛异常且不挂载
  - [x] 挂载到未声明的缝时抛异常
  - [x] 协变/基类签名不被接受

### R5: replace wrapper（moding 承接）与迁移
- Status: Confirmed
- Scenario/Trigger: 现有 moding 调用面（预制体 `modings[]`、csx `DeclareModing`、`RegisterModing` 测试与生产代码）在改造后如何继续工作。
- Behavior: 新增 replace wrapper 承接纯替换语义；解析＝最后一个未注销者生效、注销回退上一项；只替换核心段，前/后 wrapper 仍执行。
  - **硬切迁移**：`RegisterModing`→`RegisterReplaceWrapper`、`UnregisterModing`→`UnregisterReplaceWrapper`、`modings[]`→`wrappers[]`、`DeclareModing`→`DeclareWrapper`、`modingCount`→`wrapperCount`；不保留 Obsolete 别名。
  - 预制体 schema＝单数组 + `phase`：`wrappers: [{ phase: "before"|"after"|"replace", target: "<eventId | seam:名>", handler: {...} }]`。
  - csx `DeclareWrapper` 只覆盖 `Judicator`/`AssetService`（不覆盖 Trigger——触发器是纯编排节点）。
- Acceptance:
  - [x] replace 生效时原核心 handler 不执行，前/后 wrapper 仍按管道执行
  - [x] 多个 replace 并存＝最后注册者生效，注销回退上一项
  - [x] 旧命名全部移除，仓库内无残留 `moding` API/字段（CONTEXT 术语同步退役）
  - [x] 预制体 `wrappers[]` 以 `phase` 区分三类
  - [x] csx `WrapperTarget` 不含 Trigger

### R6: 执行期语义（快照／异常／取消）
- Status: Confirmed
- Scenario/Trigger: 管道执行期间发生 wrapper 增删、wrapper 抛异常、动作取消。
- Behavior:
  - **快照**：handler 级——进入某 handler 前固定其前/后/replace 列表；动作缝在每次 `SeamAsync` 调用时固定该缝列表。
  - **异常**：前触发异常＝跳过该 handler 剩余部分（核心 + 后触发），记录并继续下一事件；后触发异常＝记录并继续、该 wrapper 不生效、输出保留核心结果；replace 异常＝同 handler 异常；判定器异常＝原样传播。
  - **取消**：`CancellationToken` 透传每一层；`OperationCanceledException` 穿透上抛。
  - **嵌套/重入**：每次进入 handler/判定器都重新构建管道（独立快照）；重入合法，递归终止由调用方负责。
- Acceptance:
  - [x] 执行期注册/注销不影响本次已开始的 handler／缝调用
  - [x] 前触发异常不执行核心与后触发，但后续事件继续
  - [x] 后触发异常不改变已产出的核心结果
  - [x] 判定器异常不被吞掉或包装
  - [x] 重入时按进入点重新构建管道，不复用外层快照

## Terminology

- [包装器（wrapper）](../CONTEXT.md)
- [管道式（pipeline）](../CONTEXT.md)
- [动作缝（action seam）](../CONTEXT.md)
- [触发器（Trigger）](../CONTEXT.md)
- [判定器](../CONTEXT.md)
