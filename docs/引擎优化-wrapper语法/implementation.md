# Implementation — wrapper（管道式包装器）语法

> 实现完成：S1–S8 全部 Done；全量测试 1609 通过、0 失败（基线 1598 + 新增 11）。
> 核心决策见 `implementation-grill-plan.md`（I1–I9 已对齐）。

## Scope

- Target：内核 `Trigger`（事件 handler 管道 + 动作缝）、`ServiceEntry`/`ServiceRegistry`（判定器/资产服务管道）、预制体 schema、csx 插件面、审计面、测试。
- Excludes：数值修饰链、band/priority 排序、效果 DSL、csx 对 Trigger 的注册。

## Implementation Steps

### S0: 术语落表与需求定稿
- Status: Done
- Target：`CONTEXT.md`、`docs/引擎优化-wrapper语法/requirements.md`
- Approach：新增包装器/管道式/动作缝/触发器（纯编排节点）词条；moding 词条退役；需求全部标 Confirmed。
- Acceptance:
  - [x] CONTEXT 含四个新词条与 `_Avoid_` 指引
  - [x] 需求文档无 Draft 项
- Rationale：术语先落表，避免实现期命名漂移。
- Terminology: [包装器（wrapper）](../../CONTEXT.md)

### S1: Trigger 侧数据模型与注册 API
- Status: Done
- Target：`src/OrC/Core/Trigger.cs`、`src/OrC/Core/Wrapper.cs`
- Approach：`EventEntry` 增设 `_before`/`_after`/`_replace` 与每条目 `Dictionary<string, SeamEntry>`（Ordinal）；新增 `RegisterBeforeWrapper`（含 seam 重载）/`RegisterAfterWrapper`/`RegisterReplaceWrapper`；`UnregisterWrapper`；`Register` 增加可选 `seams` 声明；新增 `TriggerWrapperRegistration`（Phase/Seam）。
- Acceptance:
  - [x] 三类 wrapper 可注册/注销（句柄可寻址）
  - [x] 动作缝按 `(handler 句柄, 缝名)` 锚定
  - [x] 签名不符/未声明缝 fail-fast
- Rationale：先定数据模型，执行管道与校验都依赖它。
- Terminology: [包装器（wrapper）](../../CONTEXT.md)、[动作缝（action seam）](../../CONTEXT.md)

### S2: Trigger 执行管道与快照
- Status: Done
- Target：`src/OrC/Core/Trigger.cs`（`RunEventsAsync`）、`src/OrC/Core/Context.cs`、`ExecutionFrame`
- Approach：进入 handler 前快照三组（before 反序、after 正序、replace 取最后一个）；按管道顺序 await；`ExecutionFrame.SeamExecutor` 由 `RunEventsAsync` 注入，`Context.SeamAsync(name, ct)` 转发并按次快照该缝列表；进入/退出时设置/清空；前置/replace 失败跳过该 handler 剩余段，后置逐 wrapper 隔离。
- Acceptance:
  - [x] 执行序 `w2→w1→core→w1→w2`
  - [x] replace 只替换核心段
  - [x] 异常口径符合 R6
  - [x] 非 handler 上下文调用 `SeamAsync` fail-fast
- Terminology: [管道式（pipeline）](../../CONTEXT.md)、[动作缝（action seam）](../../CONTEXT.md)

### S3: 判定器/资产服务管道
- Status: Done
- Target：`src/OrC/Core/UnifiedServices.cs`、`JudicatorRegistry.cs`、`AssetService.cs`
- Approach：`ServiceEntry` 增设 `_before`/`_after`/`_replace`；`Invoke` 同步组装（before 反序 → replace 或 `Service.Invoke` → after 正序逐层改写 result）；三个独立注册方法 + `UnregisterWrapper`；句柄 `ServiceWrapperRegistration` 及其具体化。
- Acceptance:
  - [x] 统一签名三类 wrapper 生效
  - [x] replace 最后者胜、注销回退
- Terminology: [判定器](../../CONTEXT.md)

### S4: 强类型包装器工厂
- Status: Done
- Target：`src/OrC/Core/WrapperFactory.cs`、`src/OrC/Core/Judicator.cs`
- Approach：`Wrapper.Before<T1..T4>`（含 0 参）、`Wrapper.After<T1..T4,TResult>`（含 0 参 + 结果）返回 `BeforeWrapper`/`AfterWrapper`（统一签名委托 + `ParameterTypes` + `ReturnType`）；`Judicator<TDelegate>` 实现 `IServiceSignature` 并静态缓存签名；replace 继续用 `Adapt`。
- Acceptance:
  - [x] 1~4 参 before/after 可用
  - [x] 工厂产物携带签名描述、可被严格校验
- Terminology: [包装器（wrapper）](../../CONTEXT.md)

### S5: 预制体 schema 与反射桥
- Status: Done
- Target：`PrefabJson.cs`、`DynamicEffect.cs`、`Prefabs.cs`、39+ 个 JSON
- Approach：`modings[]`→`wrappers[]`，DTO `{ phase, target, seam?, handler }`；`phase` 字符串→枚举、非法值 fail-fast；`DynamicEffect` 反射桥按 phase 分派；`EventPrefab` 增加 `seams` 声明并贯通 JSON 读写；模板/样例全量迁移。
- Acceptance:
  - [x] 旧字段无残留；模板/样例全部迁移
  - [x] `seam` 目标可解析并校验（未声明＝fail-fast）
- Terminology: [包装器（wrapper）](../../CONTEXT.md)、[动作缝（action seam）](../../CONTEXT.md)

### S6: csx/插件面迁移
- Status: Done
- Target：`PluginScripting.cs`、`PluginRegistrationContext.cs`、`ProcessRegistrationContext.cs`、`BuiltInPlugin.cs`
- Approach：`ModingTarget`→`WrapperTarget`（不含 Trigger）；三个独立声明方法 `DeclareBeforeWrapper`/`DeclareAfterWrapper`/`DeclareReplaceWrapper`；沿用"声明即登记 + `ApplyWrappers` 统一应用"；`BuiltInPlugin` 改用 replace wrapper 并保留自持基线。
- Acceptance:
  - [x] csx 三类可声明（判定器/资产服务）
  - [x] 进程级脚本拒绝消息同步
- Terminology: [触发器（Trigger）](../../CONTEXT.md)

### S7: 审计面
- Status: Done
- Target：`AuditChainText.cs`、`AuditChainJson.cs`、`GameHooks.cs`、`TriggerEventInfo`
- Approach：`ModingCount`→`WrapperCount` + 三类细分 + 缝计数；文本 ` wrappers=n (b/a/r) seams=n(s:n)`；JSON 六个字段；`GameHooks.JudicatorModing`→`JudicatorWrapper`。
- Acceptance:
  - [x] 文本/JSON 输出新字段
  - [x] 无 `moding` 残留
- Terminology: [包装器（wrapper）](../../CONTEXT.md)

### S8: 测试迁移与全量验证
- Status: Done
- Target：`tests/Orc.Tests/*`、`tests/Orc.Game.Tests/*`、`tests/Orc.Packages.Tests/*`、`tests/Orc.Script.Tests/*`
- Approach：`TriggerModingTests`→`TriggerWrapperTests`、`JudicatorModingTests`→`JudicatorWrapperTests`，原用例等价改写（跨句柄从宽容改为 fail-fast）；新增 `WrapperPipelineTests` 覆盖三类管道、动作缝、异常、强类型工厂严格校验；全量 `dotnet test`。
- Acceptance:
  - [x] 旧语义全部有等价覆盖
  - [x] 新能力（before/after/seam/校验/快照/异常）有专项用例
  - [x] 全量测试通过（1609 通过、0 失败）
- Terminology: [包装器（wrapper）](../../CONTEXT.md)
