# Implementation Grill Plan — 引擎共享变量

> 聚焦：API 签名的可用性（值类型/泛型约束）、异常口径、类型保护严格度、托管位置与测试落点。

## Resolved Questions

### I1: `TryGet<T>` 是否限 `where T : class` {Completed}
- 已定：**不限**。否则 `TryGet<int>` 无法编译，计数器用途失效；`EngineReferences.TryResolve<T>` 的 `class` 约束不照搬。

### I2: 异常口径 {Completed}
- 已定：缺失 → `KeyNotFoundException`（对齐 `Card.GetData<T>`）；类型不符 → `InvalidCastException`（带实际类型名）；空键 → `ArgumentException`；null 值 → `ArgumentNullException`。

### I3: 类型保护严格度 {Completed}
- 已定：**写入按运行时类型相等**（`existing.GetType() != value.GetType()` 即拒绝）；**读取按 `is T`**（允许基类/接口/子类读取）。不对称是刻意的。

### I4: `Set` 是否返回旧值 / 提供 `TrySet` {Completed}
- 已定：不提供。`Set` 返回 void；"仅当缺失才写"用 `Contains` + `GetOrCreate` 表达。

### I5: `GetOrCreate<T>` 的约束 {Completed}
- 已定：`where T : class, new()`。值类型没有共享实例语义（取到的是副本），不开放。

### I6: 变更范围 {Completed}
- 已定：仅内核新类型 + `LogicEngine` 一行属性 + 测试 + `CONTEXT.md`；`EngineReferences`、csx 白名单、快照、事件流、MCP、游戏层零改动。

### I7: 测试落点 {Completed}
- 已定：`tests/Orc.Tests/EngineVariablesTests.cs`（被测类型属 `Orc` 内核；`EngineReferencesTests` 放在 `Orc.Mcp.Tests` 是因 MCP 是首个消费者）。
