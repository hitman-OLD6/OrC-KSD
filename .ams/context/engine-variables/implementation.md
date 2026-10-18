# Implementation — 引擎共享变量（跨效果数据袋）

实现已完成并验证（Orc.Tests 317 通过，含新增 13 条）。

## Scope

- Target: `src/Orc/Core/EngineVariables.cs`（新增）、`src/Orc/Core/LogicEngine.cs`（加属性）、`tests/Orc.Tests/EngineVariablesTests.cs`（新增）、`CONTEXT.md`（术语）。
- Excludes: `EngineReferences`、csx 白名单、快照、事件流、MCP、UI、游戏层。

## Implementation Steps

### S1: EngineVariables 类型
- Status: Done
- Target: `src/Orc/Core/EngineVariables.cs`
- Approach: `Dictionary<string, object>`（Ordinal）；`Set` 校验空键/null 值，并以运行时类型相等阻止同 key 异类型覆盖；`TryGet<T>` 用 `is T` 判定（允许基类/接口读）；`Get<T>` 缺失抛 `KeyNotFoundException`、不符抛 `InvalidCastException`；`GetOrCreate<T> where T : class, new()`；`Remove`/`Clear`。
- Acceptance:
  - [x] 同 key 同类型覆盖、异类型拒绝。
  - [x] `TryGet` 缺失/不符返回 false，`Get` 抛明确异常。
  - [x] `GetOrCreate` 二次调用返回同一实例。
- Rationale: 与 `EngineReferences` 分开成表，避免污染 GUID 契约与 `Count` 诊断口径；`TryGet` 不加 `class` 约束以支持 `int` 计数器；写入严格相等、读取 `is` 兼容，防止同一 key 语义漂移同时不限制按基类消费。

### S2: 引擎托管位置
- Status: Done
- Target: `src/Orc/Core/LogicEngine.cs`（`References` 之后）
- Approach: `public EngineVariables Variables { get; } = new();`
- Acceptance:
  - [x] 两引擎实例变量表互不可见；`Clear` 互不影响。
- Rationale: 对局级＝引擎实例级（`Match.cs:160` 每局 `new LogicEngine()`），无需额外清空钩子。

### S3: 测试
- Status: Done
- Target: `tests/Orc.Tests/EngineVariablesTests.cs`
- Approach: 13 条 xUnit，覆盖 R2–R6（覆盖/拒绝/类型/缺失/清理/隔离/与 `References` 相互独立）。
- Acceptance:
  - [x] `dotnet test tests/Orc.Tests` 317 通过、0 失败。

### S4: 文档
- Status: Done
- Target: `CONTEXT.md`
- Approach: 「引擎托管引用表」之后新增「引擎共享变量」条目，并登记 `_Avoid_`（全局变量/全局字典）。
- Acceptance:
  - [x] 术语表含新条目，明确与引用表并列、不进快照、不回滚。
