# Implementation Grill Plan — wrapper（管道式包装器）语法

> 需求已定稿（见 `requirements.md`）。本轮进入实现 grill：按模块顺序对齐改造路径。
> 当前状态：第 1 轮，I1（注册 API 形状）已发出。

## 代码基线（实现取证）

- `src/Orc/Core/Trigger.cs`：`EventEntry`（handler + band/priority + `_modings`）、`RunEventsAsync`（`:516`，解析点 `:531`）、`SnapshotSorted`（`:703`）、`TriggerEventInfo.ModingCount`（`:915`）。
- `src/Orc/Core/UnifiedServices.cs`：`ServiceEntry`（`LastModing`/`Invoke`）、`ServiceRegistry`（`RegisterModing`/`UnregisterModing`）、`IStrongTypedService<TDelegate>`。
- `src/Orc/Core/Judicator.cs`：`Unpack<T>`、`Judicator<TDelegate>.Adapt`。
- `src/Orc/Core/JudicatorRegistry.cs` / `AssetService.cs`：两个服务级具体化。
- `src/Orc/Cards/PrefabJson.cs`（`:273/301-305`）、`src/Orc/Cards/DynamicEffect.cs`（`:148-162`、反射桥 `:359-375`）。
- `src/Orc/Core/PluginScripting.cs`（`ModingTarget`/`DeclareModing`）、`src/Orc.Packages/PluginRegistrationContext.cs`（`ApplyModings`）、`src/Orc.Plugins.BuiltIn/BuiltInPlugin.cs`（`DeclareKeywordRule`）。
- 审计：`src/Orc/Output/AuditChainText.cs:61`、`AuditChainJson.cs:97`、`src/Orc.Game/GameHooks.cs:18`。
- 测试：`tests/Orc.Tests/TriggerModingTests.cs`、`JudicatorModingTests.cs`、`PrefabTests.cs`、`DynamicEffectTests.cs`；`tests/Orc.Game.Tests/*`（12 个文件）；`tests/Orc.Packages.Tests/*`、`tests/Orc.Script.Tests/*`。

## 已对齐实现决策

- **I1=a（Trigger 侧注册 API）**：
  - 三个独立方法 `RegisterBeforeWrapper` / `RegisterAfterWrapper` / `RegisterReplaceWrapper`；动作缝用 `RegisterBeforeWrapper(handle, seam, wrapper)` 重载。
  - `Register(..., seams: IEnumerable<string>? seams = null)` 声明动作缝。
  - `UnregisterWrapper(TriggerWrapperRegistration)` 统一注销。
  - `EventEntry` 存储＝`_before` / `_after` / `_replace` 三组列表 + 每条目独立 `Dictionary<string, SeamEntry>`（`StringComparer.Ordinal`）；`SeamEntry` 只有 `_before`；replace 取 `_replace[^1]`。

- **I2=a（服务级注册 API 与管道）**：
  - 三个独立方法（统一签名）：`RegisterBeforeWrapper(THandle, Action<object[]?>)`、`RegisterAfterWrapper(THandle, Func<object[]?, object[]?, object[]?>)`、`RegisterReplaceWrapper(THandle, Func<object[]?, object[]?>)`；`UnregisterWrapper(ServiceWrapperRegistration)`。
  - `ServiceEntry.Invoke` 同步组装：before（反序）→ replace 或 `Service.Invoke` → after（正序，逐层改写 result）。
  - 句柄命名：`JudicatorWrapperRegistration` / `AssetServiceWrapperRegistration`，基类 `ServiceWrapperRegistration`。

- **I3=a（强类型工厂）**：独立静态类 `Wrapper`；`Before<T1..T4>`（含 0 参）、`After<T1..T4,TResult>`（含 0 参 + 结果），约定最后一个泛型参数为结果类型；返回 `BeforeWrapper`/`AfterWrapper` 结构（含统一签名委托 + `ParameterTypes` + `ReturnType`）；replace 走 `RegisterReplaceWrapper<TDelegate>`（内部 `Adapt`）。

- **I4=a（签名校验）**：
  - 新增 `IServiceSignature`（`ParameterTypes` / `ReturnType`），`Judicator<TDelegate>` 一处实现并静态缓存 `typeof(TDelegate).GetMethod("Invoke")`；`ServiceEntry` 注册时缓存；非强类型判定器回退 `[object[]] → object[]`。
  - 映射：before＝参数相等 + 返回 `void`；after＝参数＝目标参数 + 目标返回、返回＝目标返回；replace＝参数与返回都与目标相等。
  - Trigger 目标签名固定 `Func<TView, Context, CancellationToken, Task>`；`Delegate` 重载用 `GetType().GetMethod("Invoke")`；失败抛 `ArgumentException`。

- **I5=a（动作缝接线）**：
  - `ExecutionFrame.SeamExecutor`（`Func<string, CancellationToken, Task>?`）由 `RunEventsAsync` 进入每 handler 前注入、`finally` 清空；`Context.SeamAsync(name, ct)` 转发，帧为空时 `InvalidOperationException`。
  - `SeamAsync(string, CancellationToken = default)`；`seams` 只加两个普通 `Register` 重载；每次 `SeamAsync` 调用时反序快照；缝 wrapper 异常冒泡到 `RunEventsAsync` 按 D19 处理。

- **I6=a（预制体 schema）**：
  - DTO 改为 `{ phase, target, seam?, handler }`；`target` 只定位事件槽位，`seam` 可选（未声明即加载期 fail-fast）。
  - 字段名 `handler`；`phase` 字符串→枚举、非法值 fail-fast（写出固定小写）。
  - `DynamicEffect` 反射桥按 phase 分派 `BeforeCore`/`AfterCore`/`ReplaceCore`，`TView` 仍由 `mainViewType` 决定。

- **I7=a（csx/插件面）**：
  - 三个独立声明方法 `DeclareBeforeWrapper` / `DeclareAfterWrapper` / `DeclareReplaceWrapper`；`WrapperTarget` 仍只有 Judicator/AssetService。
  - 沿用"声明即登记 + `ApplyWrappers` 统一应用"；`BuiltInPlugin.DeclareKeywordRule` 保留自持基线实例；进程级拒绝消息同步改名。

- **I8=a（审计面）**：
  - `TriggerEventInfo` 保留 `WrapperCount` 并细分 `BeforeWrapperCount`/`AfterWrapperCount`/`ReplaceWrapperCount`、`SeamCount`/`SeamWrapperCount`。
  - 文本 ` wrappers=3 (b:2/a:1/r:0) seams=1(s:2)`；JSON 同名六个字段。
  - `GameHooks.JudicatorModing`→`GameHooks.JudicatorWrapper`（保留值 2）。

## 未决问题

### I9: 测试迁移与验证策略 【In Progress — 本轮】
- a) 旧测试文件改名并逐条等价改写（`TriggerModingTests`→`TriggerWrapperTests`、`JudicatorModingTests`→`JudicatorWrapperTests`），另增 wrapper 专项文件覆盖新能力。
- b) 删除旧测试、重写。
- c) 旧测试保留原名 + 新增（命名混乱）。
- 子问题：先跑一次基线全量测试并记录既有失败；每个 S 完成后 `dotnet build`，S8 跑 `dotnet test`。

### I3: 强类型工厂的签名与重载 【Pending】
- `Wrapper.Before<T1>(Action<T1>)` 等 1~4 参重载；after 的 `(T1..Tn, TResult) -> TResult`。
- 工厂产物如何携带"签名描述"供校验。

### I4: 签名校验的实现 【Pending】
- 统一签名：委托类型逐一比较 vs `MethodInfo` 反射。
- 强类型工厂：泛型参数序列 vs 运行时类型。
- Trigger 的 `TView` 校验点。

### I5: 动作缝的落地 【Pending】
- `Register(..., seams: [...])` 参数扩展（重载爆炸？）。
- `Context` 如何拿到当前 `EventEntry`（执行帧注入）。
- `SeamAsync` 的异步签名与快照点。

### I6: 预制体 schema 与反射桥 【Pending】
- `PrefabJson` DTO 改造；`phase` 反序列化（字符串/枚举）。
- `target` 语法（`eventId` / `seam:名`）解析与校验。
- `DynamicEffect` 反射桥按 phase 分派。

### I7: csx/插件迁移 【Pending】
- `WrapperTarget`/`DeclareWrapper` 签名（phase 参数）。
- `ApplyWrappers` 的替换/资产管理。
- `BuiltInPlugin.DeclareKeywordRule` 改用 replace wrapper 后的自引用问题。

### I8: 审计面 【Pending】
- `WrapperCount` 是否细分 before/after/replace；文本/JSON 输出字段。

### I9: 测试迁移与验收 【Pending】
- 旧 moding 测试的等价改写；新增三类 wrapper/动作缝/校验/快照/异常用例。
- 全量构建与测试命令。

## 风险与注意

- `Trigger.Register` 已有 4 个重载，新增 `seams` 参数容易造成重载歧义；可能改为可选参数（源码兼容、二进制不兼容）。
- `Context` 是公共类型，`SeamAsync` 需内部持有执行帧；注意 `Context` 在触发之外被构造时的退化行为（fail-fast 或空转）。
- `ServiceEntry.Invoke` 是同步的；判定器 wrapper 不得引入异步。
- `PrefabJson`/`DynamicEffect` 的反射桥依赖方法签名字符串，改造需同步 `EffectTemplateSlots` 的寻址语法。
