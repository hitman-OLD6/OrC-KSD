# 03 判定器与 wrapper

> **本章解决什么**：讲清「判定怎么被改写」：判定器的强类型/统一签名两面、按名注册、全局层→玩家层两层管道，以及 wrapper 的 before/after/replace 与动作缝。读完你能安全地改写一条规则而不破坏其它卡。
> **前置**：00（五段模型）、02（触发器、注册者与来源）。
> **读完你能做什么**：①注册一个新判定器或包装既有判定器；②按场景选择 before/after/replace/动作缝；③判断改写失败（签名不符、目标错、域错）时的表现。
> **对应代码路径（2026-10-28 核对）**：`Orc.Core.Judicator` / `Judicator<TDelegate>`、`Orc.Core.JudicatorRegistry`、`Orc.Core.ServiceRegistry`（`RegisterBeforeWrapper`/`RegisterAfterWrapper`/`RegisterReplaceWrapper`）、`Orc.Core.WrapperFactory`、`Orc.Game.Judicators.*`、`Orc.Core.Model.Mounting`（`WrapDeclaration`/`WrapRegistry`）。
> **本章自检**：能画出 `w2→w1→core→w1→w2`（判定器侧）；能说出两层管道的四段顺序；能说出三种 wrapper 的差异与签名校验后果。

> **⚠ 部分失效（2026-10-28 核对）**：**判定器侧全部有效**（`Judicator`、`JudicatorRegistry`、`ServiceRegistry`、
> `Wrapper` 及其三段 wrapper 都还在）。失效的是**触发器侧**——`Trigger<TView>` 已删除，因此：
>
> | 节 | 状态 |
> | --- | --- |
> | §1–§3 判定器定义／注册／两层管道 | ✅ 有效 |
> | §4 wrapper 三类 | ✅ 判定器侧的 `ServiceRegistry.Register*Wrapper` 有效 |
> | §4.2 触发器包装器（`trigger.RegisterBeforeWrapper`）| ❌ 失效：`Trigger` 已删 |
> | §5 `w2→w1→core→w1→w2` | ⚠️ 判定器侧仍成立；**成员侧的 `before` 已改为 FIFO**（旧为 LIFO），见 00 章 §3② |
> | §6 动作缝（seams） | ❌ 失效：缝概念整体退役，需要中间插入＝拆成员 |
> | §7 `wrappers[]` 预制体声明 | ⚠️ 旧 schema 字段已不可表达；v4 改用 `wraps[]` ＋ `phase` |
>
> 成员/信号侧的改写现由 **`wraps[]`**（`WrapDeclaration` ＋ `WrapRegistry`）承担，
> 判定器侧包装仍走 `judge-wraps`（`WrapTarget.ForJudicator`）。术语映射见 [09 章](./09-新编排模型与术语迁移.md)。
## 状态

- 章节状态：**草稿**
- 最近一次证据运行：—（原理章；行为由 `tests/Orc.Tests/JudicatorWrapperTests.cs` / `JudicatorRegistryTests.cs` / `JudicatorBindingTests.cs` 覆盖）

---

## 1. 判定器是什么

判定器是**无状态规则对象**：输入载荷、输出结果，不持有对局状态。两层形态：

| 形态 | 基类 | 入口 | 用途 |
| --- | --- | --- | --- |
| 统一签名 | `Judicator` | `override object[]? Invoke(object[]? args)` | 最底层；调用方按约定组包/拆包 |
| 强类型适配 | `Judicator<TDelegate>` | 继承统一入口；额外实现 `Adapt(TDelegate)` | 给 wrapper 提供**严格签名校验**的类型信息 |

```csharp
// 统一签名：最简形态
public sealed class AlwaysValidJudicator : Judicator
{
    public override object[]? Invoke(object[]? args) => new object[] { true };
}
```

强类型形态（`Judicator<TDelegate>`）要求实现：

```csharp
protected internal abstract Func<object[]?, object[]?> Adapt(TDelegate handler);
```

> 跨程序集继承时按 C# 规则写 `protected override`（不能写 `protected internal`，否则 CS0507）。`Unpack<T>(args, index)` 是基类提供的拆包辅助（缺参/类型不符＝`ArgumentException`，fail-fast）。

## 2. 注册表与调用

```csharp
var registry = new JudicatorRegistry();               // 对局级；通常由 Match 装配持有
var handle = registry.Register(JudicatorNames.CostCheck, new MyCostJudicator());
var again = registry.Resolve(JudicatorNames.CostCheck);   // 与 handle 同一可寻址锚

var result = registry.Invoke(JudicatorNames.CostCheck, new object[] { cardRef, owner }, playerIndex: owner?.Index);
```

| API | 语义 | 失败 |
| --- | --- | --- |
| `Register(name, judicator)` | 注册即配置；重复注册拒绝 | `ArgumentException`（名空白）/`ArgumentNullException`/`InvalidOperationException`（重名） |
| `Resolve(name)` | 按名取句柄（与注册所得同一锚） | `KeyNotFoundException`（未注册＝配置错误） |
| `Invoke(name, args, playerIndex?)` | 一次调用＝一次快照；执行管道 | 未注册同上；包装器/核心异常不捕获、不包装 |
| `RegisterBeforeWrapper/AfterWrapper/ReplaceWrapper` | 挂载改写逻辑（见 §4） | 目标句柄不属本表＝`ArgumentException` |

判定器名是**受控常量集合**（`JudicatorNames`，23 个）；清单与用途见 [参考层 · api速查](./reference/api速查.md) §2。**数据侧不能注册/改写判定器**（仅代码侧）。

## 3. 两层管道：全局层 → 玩家层

一次 `Invoke(name, args, playerIndex)` 的精确顺序（`ServiceEntry.Invoke`）：

| 步 | 执行内容 | 说明 |
| --- | --- | --- |
| 1 | `before` **全局层**（默认域）**LIFO** | 后注册先跑 |
| 2 | `before` **玩家层**（`playerIndex` 域）**LIFO** | 仅该玩家域生效 |
| 3 | **replace**：玩家层最后一个 ?? 全局层最后一个 ?? core | 玩家层优先；都没有＝核心逻辑 |
| 4 | `after` **玩家层** **FIFO** | 先注册先跑；可逐层改写结果 |
| 5 | `after` **全局层** **FIFO** | 最后跑（全局作为基线改写） |

- `playerIndex = null`（默认域）＝只有全局层；`playerIndex = 玩家索引`＝全局层 + 该玩家层。
- **玩家域**用于「只改我这张卡/这个玩家看到的规则」；全局层用于「插件/装配期对所有人生效」。
- 快照语义：一次调用开始时取快照，执行期间的增删不影响本次。

## 4. wrapper 三类

| 类型 | 统一签名 | 顺序 | 能力 | 触发器侧对应 |
| --- | --- | --- | --- | --- |
| `before` | `Action<object[]?>` | LIFO | 就地改写 `args` 元素（不能短路） | `RegisterBeforeWrapper`（同 handler 签名，可挂具名缝） |
| `after` | `Func<object[]?, object[]?, object[]?>` | FIFO | 接收「原参数＋核心结果」，返回新结果 | `RegisterAfterWrapper` |
| `replace` | `Func<object[]?, object[]?>` | 最后一个生效 | **只替换核心段**，不叠加 | `RegisterReplaceWrapper` |

### 4.1 严格签名校验

两类注册入口：

```csharp
// ① 统一签名：不做强类型校验（你自己保证 args 形状）
registry.RegisterBeforeWrapper(handle, args => { /* args![0] ... */ });

// ② 强类型工厂：校验参数类型/返回类型与目标严格一致；不符＝挂载期 ArgumentException
registry.RegisterBeforeWrapper(handle, Wrapper.Before<Ref<Entity>, int>((cardRef, cost) =>
{
    // 只改「费用」这个参数
}));

registry.RegisterAfterWrapper(handle, Wrapper.After<Ref<Entity>, int, bool>((cardRef, cost, ok) => ok));
```

- `Wrapper.Before<T1..T4>` → `BeforeWrapper`（带 `ParameterTypes`，与目标签名逐项比较）。
- `Wrapper.After<T1..T4,TResult>` → `AfterWrapper`（参数＝目标参数＋结果；返回须等于目标返回）。
- `RegisterReplaceWrapper<TDelegate>(handle, wrapper)` 要求目标实现 `IStrongTypedService<TDelegate>`（即 `Judicator<TDelegate>`）；否则 `InvalidOperationException`（适配错误）。

> 校验失败发生在**挂载期**（不是调用期）：签名不符立刻 `ArgumentException`，不会留下「挂了但永远不生效」的包装器。

### 4.2 触发器包装器（同一套三段，不同签名）

> ❌ **本节失效（2026-10-28）**：`Trigger<TView>.RegisterBeforeWrapper` 已随 `Trigger` 删除。成员侧的改写改用 `wraps[]` 声明（`phase`），
> 且 `before` 的顺序由旧 LIFO 改为 **FIFO**。下面代码仅作历史存档。

触发器不区分全局/玩家域，但三段语义一致：

```csharp
var reg = trigger.Register("核心", CoreHandler, priority: 100);

trigger.RegisterBeforeWrapper(reg, Before1);   // 先注册
trigger.RegisterBeforeWrapper(reg, Before2);   // 后注册 → 先执行（LIFO）
trigger.RegisterAfterWrapper(reg, After1);     // 先注册 → 先执行（FIFO）
trigger.RegisterAfterWrapper(reg, After2);
var replace = trigger.RegisterReplaceWrapper(reg, Replacement);  // 最后一个生效
```

签名统一为 `Func<TView, Context, CancellationToken, Task>`；`before` 只能改 `ctx` 数据/记录，**不能替换入参引用、不能短路**；`after` 在核心之后执行（核心抛异常时跳过）。

## 5. `w2→w1→core→w1→w2`

设 `w1` 先注册、`w2` 后注册。触发器/判定器的实际顺序：

```
before: w2 → w1          （LIFO）
replace ? replace : core  （最后一个 replace 生效，否则核心）
after:  w1 → w2          （FIFO）
```

完整时序：**`w2 → w1 → core → w1 → w2`**。

```csharp
var log = new List<string>();

var reg = trigger.Register("core", (view, ctx, ct) =>
{
    log.Add("core");
    return Task.CompletedTask;
});

trigger.RegisterBeforeWrapper(reg, (view, ctx, ct) => { log.Add("w1-before"); return Task.CompletedTask; });
trigger.RegisterBeforeWrapper(reg, (view, ctx, ct) => { log.Add("w2-before"); return Task.CompletedTask; });
trigger.RegisterAfterWrapper(reg, (view, ctx, ct) => { log.Add("w1-after"); return Task.CompletedTask; });
trigger.RegisterAfterWrapper(reg, (view, ctx, ct) => { log.Add("w2-after"); return Task.CompletedTask; });

await trigger.InvokeAsync(engine);
// log == ["w2-before", "w1-before", "core", "w1-after", "w2-after"]
```

判定器侧同理（把 `core` 换成 `Service.Invoke(args)`，`before/after` 换成 `object[]` 管道）；两层域时先全局后玩家：

```
before: 全局 LIFO → 玩家 LIFO
core:   replace(玩家 ?? 全局) ?? core
after:  玩家 FIFO → 全局 FIFO
```

## 6. 动作缝（seams）

> ❌ **本节整体失效（2026-10-28）**：`seams` 概念随 `Trigger` 一起退役，新模型里**没有动作缝**。
> 需要"在某个时点插入"＝**拆成员**（把那个时点做成一个独立的可挂载成员），或改用 `wraps` 的 `before`/`after` 相位。
> 下面内容仅作历史存档。

动作缝把「同一 handler 内的某个时点」开放给 before 包装器，而不改变主流程：

```csharp
// 1) 注册时声明缝名
var reg = trigger.Register("核心", CoreHandler, seams: new[] { "标记" });

// 2) 包装器挂到该缝（LIFO）
trigger.RegisterBeforeWrapper(reg, "标记", async (view, ctx, ct) =>
{
    ctx.Engine?.RootStream.WriteLog(
        "diy.seam", "缝已执行", Orc.Core.LogLevel.Info, new[] { "diy.seam" });
    await Task.CompletedTask;
});

// 3) handler 内调用缝
private static async Task CoreHandler(MyView view, Context ctx, CancellationToken ct)
{
    await ctx.SeamAsync("标记", ct);
    // ... 核心逻辑
}
```

失败语义：

| 情况 | 结果 |
| --- | --- |
| 不在 handler 执行期调用 | `InvalidOperationException` |
| 缝未在注册项声明 | `KeyNotFoundException` |
| 缝名为空白 | `ArgumentException` |

## 7. `wrappers[]` 预制体声明（数据侧）

> ⚠️ **本节 schema 字段已过时（2026-10-28）**：旧预制体的 `wrappers[]`（`phase` + `target` 事件 id + `seam`）仍可由旧装载器读入并迁移，
> 但 v4 组件资产把 `seam` 列为**旧字段**（读到即拒绝装载），改写改用 `wraps[]`：
> `{ "handler": "<成员地址>", "target": "engine#<成员>", "phase": "before|after|replace" }`；判定器侧用 `judge-wraps[]`。
> 下面示例保留为旧格式参考。

预制体可以在**主触发器**的事件上声明包装器：

```json
{
  "wrappers": [
    {
      "phase": "before",
      "target": "onUnitDeployed",
      "handler": {
        "id": "w1",
        "entry": "WrapAsync",
        "csx": "Func<Orc.Cards.CardEventView, Context, CancellationToken, Task> WrapAsync = async (view, ctx, ct) =>\n{\n    ctx.Engine?.RootStream.WriteLog(\"diy.wrapper\", \"before wrapper\", Orc.Core.LogLevel.Info, new[] { \"diy.wrapper\" });\n    await Task.CompletedTask;\n};\n",
        "version": 1
      }
    }
  ]
}
```

| 字段 | 取值 | 说明 |
| --- | --- | --- |
| `phase` | `before` / `after` / `replace` | 其它值＝装配失败（`wrapper-signature`/`Argument`） |
| `target` | 事件 id | 必须是**主触发器**里已注册的事件 id |
| `seam` | 缝名或省略 | 仅 `before` 用；拼错＝该缝不存在（运行期 `KeyNotFoundException`，装配期不校验缝存在） |
| `handler` | `EventPrefab` 形态 | 与普通事件同构（`id`/`entry`/`csx`/`assemblyKey`） |

> 数据侧只能包主触发器事件；包其它触发器或包判定器＝代码侧（`RegisterBeforeWrapper` / `RegisterAfterWrapper` / `RegisterReplaceWrapper`）。

## 8. 最小片段（代码侧）：替换费用检查

```csharp
using Orc.Core;
using Orc.Game.Judicators;

// 1) 定义一个强类型判定器（便于严格签名校验）
public delegate bool CostRule(Ref<Entity> cardRef, int quotedCost);

public sealed class HalfCostJudicator : Judicator<CostRule>
{
    public override object[]? Invoke(object[]? args)
    {
        var cardRef = Unpack<Ref<Entity>>(args, 0);
        var quoted = Unpack<int>(args, 1);
        return new object[] { cardRef.IsAlive && quoted >= 0 };
    }

    protected override Func<object[]?, object[]?> Adapt(CostRule handler)
        => args => new object[] { handler(Unpack<Ref<Entity>>(args, 0), Unpack<int>(args, 1)) };
}

// 2) 注册 + 包一层（全局层）
var handle = registry.Register("diy.cost.half", new HalfCostJudicator());
registry.RegisterBeforeWrapper(handle, Wrapper.Before<Ref<Entity>, int>((cardRef, cost) =>
{
    // 只改成本参数：减半（示例；实际以 args 改写为准）
}));

// 3) 只对某个玩家生效
registry.RegisterAfterWrapper(
    handle,
    Wrapper.After<Ref<Entity>, int, bool>((cardRef, cost, ok) => ok && cardRef.IsAlive),
    playerIndex: ownerIndex);
```

> 说明：上面的 `Wrapper.Before` 回调里改的是**已拆包的参数副本**，不会写回 `args`；要真正改写入参，用统一签名入口 `RegisterBeforeWrapper(handle, args => args![1] = newCost)`。两种入口的差别就是「类型安全」与「可改写」。

## 9. 边界

| 边界 | 说明 | 替代 |
| --- | --- | --- |
| 数据侧不能改判定器 | 只能消费既有判定结果 | 代码侧注册/包装 |
| `injects.band` 名与目标触发器不匹配 | 目标无专门 band 方案、或名不是该枚举成员＝隔离记录（P3 起 band 已可按名解析） | 填目标 band 方案的成员名，或代码侧 `Effect.Inject` |
| `replace` 只保留最后一个 | 不是叠加；注销后回退到前一个/核心 | 需要叠加用 `after` |
| 动作缝只在声明它的注册项上有效 | 缝名拼错＝运行期 `KeyNotFoundException` | 注册时写 `seams:`，装配期自查 |
| 强类型 replace 需目标实现 `IStrongTypedService<TDelegate>` | 普通 `Judicator` 不支持 | 用统一签名 replace，或继承 `Judicator<TDelegate>` |

## 10. 本章自检

- [ ] 能写出两层管道的四段顺序（含全局/玩家的先后）
- [ ] 能解释 `w2→w1→core→w1→w2` 中 before 为什么是 LIFO、after 为什么是 FIFO
- [ ] 能说出三种 wrapper 的能力差异（改写入参 / 改写结果 / 替换核心）
- [ ] 能说出签名校验失败的时机与异常类型
- [ ] 能写一个 `seams:` + `ctx.SeamAsync` + before 缝包装器的最小闭环
