# 03 判定器与 wrapper

> **本章解决什么**：讲清「判定怎么被改写」：判定器的两种签名形态、按名注册、全局层→玩家层两层管道，
> 以及两条改写通道——**判定器包装**（`judge-wraps[]`）与**成员包装**（`wraps[]`）＋ 具名缝。
> 读完你能安全地改写一条规则而不破坏其它卡。
> **前置**：00（心智模型）、02（成员、信号与 handler）。
> **读完你能做什么**：①注册一个新判定器或包装既有判定器；②按场景选择 before/after/replace/具名缝；
> ③判断改写失败（签名不符、目标错、域错）时的表现。
> **对应代码路径（2026-10-29 核对）**：`Orc.Core.Judicator`／`Judicator<TDelegate>`、`Orc.Core.JudicatorRegistry`、
> `Orc.Core.UnifiedServices`（`ServiceEntry.Invoke` 的管道）、`Orc.Core.WrapperFactory`、`Orc.Game.Judicators.*`、
> `Orc.Core.Model.Mounting`（`WrapDeclaration`／`WrapTarget`／`WrapRegistry`）、`Orc.Core.Model.AssetSchema`（`wraps`／`judge-wraps`）。
> **本章自检**：能说出判定器侧与成员侧执行顺序的**不同**；能写出一个 `wraps[]` 声明并解释为什么包装器必须先声明成成员；
> 能说出三种相位的差异与签名校验后果。

## 状态

- 章节状态：**完成**
- 最近一次证据运行：2026-10-29（`tests/Orc.Tests/Model/WrapChannelTests.cs`、`SeamTests.cs`、
  `tests/Orc.Tests/JudicatorWrapperTests.cs`、`tests/Orc.Tests/JudgeWrapExecutionTests.cs` 全绿）

---

## 1. 判定器是什么

判定器是**无状态规则对象**：输入载荷、输出结果，不持有对局状态。两层形态：

| 形态 | 基类 | 入口 | 用途 |
| --- | --- | --- | --- |
| 统一签名 | `Judicator` | `override object[]? Invoke(object[]? args)` | 最底层；调用方按约定组包/拆包 |
| 强类型适配 | `Judicator<TDelegate>` | 继承统一入口；额外实现 `Adapt(TDelegate)` | 给包装器提供**严格签名校验**的类型信息 |

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

> 跨程序集继承时按 C# 规则写 `protected override`（不能写 `protected internal`，否则 CS0507）。
> `Unpack<T>(args, index)` 是基类提供的拆包辅助（缺参/类型不符＝`ArgumentException`，fail-fast）。

## 2. 注册表与调用

```csharp
var registry = new JudicatorRegistry();                  // 对局级；通常由 Match 装配持有
var handle = registry.Register(JudicatorNames.CostCheck, new MyCostJudicator());
var again = registry.Resolve(JudicatorNames.CostCheck);  // 与 handle 同一可寻址锚

var result = registry.Invoke(JudicatorNames.CostCheck, new object[] { cardRef, owner }, playerIndex: owner?.Index);
```

| API | 语义 | 失败 |
| --- | --- | --- |
| `Register(name, judicator)` | 注册即配置；重复注册拒绝 | `ArgumentException`（名空白）/`ArgumentNullException`/`InvalidOperationException`（重名） |
| `Resolve(name)` | 按名取句柄（与注册所得同一锚） | `KeyNotFoundException`（未注册＝配置错误） |
| `Invoke(name, args, playerIndex?)` | 一次调用＝一次快照；执行管道 | 未注册同上；包装器/核心异常不捕获、不包装 |
| `RegisterBeforeWrapper/AfterWrapper/ReplaceWrapper` | 挂载改写逻辑（见 §4） | 目标句柄不属本表＝`ArgumentException` |

**判定器名是受控常量集合**：`Orc.Game.Judicators.JudicatorNames`，现 **28 个**。分组：

| 组 | 名字 |
| --- | --- |
| 准入／验证（7） | `action.input.allowed`、`action.turn.owner`、`validation.cost.check`、`validation.counter.use`、`validation.move.recheck`、`validation.attack.recheck`、`inhibit.leg.eligibility` |
| 判定器示范（1） | `deck.top.tag` |
| 选靶（1） | `targeting.candidate.eligibility` |
| 交战（6） | `combat.target.legal`、`combat.range`、`combat.guard.eligibility`、`combat.interception`、`combat.counter.eligibility`、`combat.ambush.condition` |
| 移动／攻击（3） | `move.leg.eligibility`、`attack.leg.eligibility`、`move.frontline-enemy` |
| 部署（1） | `deploy.position.candidates` |
| 效果（1） | `effect.target.resolve` |
| 资源（5） | `resource.slot.increment`、`resource.slot.gain`、`resource.slot.lose`、`resource.point.gain`、`resource.point.lose` |
| **通用谓词（3）** | `unit.adjacent`、`unit.type`、`unit.side` |

`unit.*` 三个通用谓词是给作者用的：csx 里 `runtime.Judge("unit.adjacent", a, b)` 直接问「这两个单位相邻吗」，
不必自己算坐标。完整签名见 [参考层 · api速查](./reference/api速查.md) §2。

> **数据侧能消费判定结果，但不能注册判定器**。注册判定器＝代码侧。

## 3. 一次 `Invoke` 的精确顺序

`ServiceEntry.Invoke(args, domain)` 的执行序（按代码）：

| 步 | 执行内容 | 顺序 |
| --- | --- | --- |
| 1 | 旧域桶 `before`：全局域（默认域）→ 玩家域 | **LIFO**（历史语义，随旧插件桶保留） |
| 2 | 新模型面 `before`（`judge-wraps` 的 `before`） | **FIFO**（声明序正序） |
| 3 | `replace`：新模型面最后声明者 → 旧域玩家层 → 旧域全局层 → 核心 | **最后一个生效** |
| 4 | 旧域桶 `after`：玩家域 → 全局域 | **FIFO** |
| 5 | 新模型面 `after`（`judge-wraps` 的 `after`） | **FIFO** |

- `playerIndex = null`（默认域）＝只有全局层；`playerIndex = 玩家索引`＝全局层＋该玩家层。
- **玩家域**用于「只改我这张卡／这个玩家看到的规则」；全局层用于「插件／装配期对所有人生效」。
- 快照语义：一次调用开始时取快照，执行期间的增删不影响本次。

> **两条面并存是过渡态**：旧插件桶（`Register*Wrapper`）与新模型面（`judge-wraps`）都能改同一个判定器。
> 同一判定器上跨两面的**相对次序不构成契约**——写卡只用 `judge-wraps`，写插件才用 `Register*Wrapper`。

## 4. 两条改写通道

| 通道 | 改什么 | 数据侧声明 | 代码侧入口 |
| --- | --- | --- | --- |
| **判定器包装** | 一条**判定规则**的输入／结果／核心 | `judge-wraps[]` | `registry.Register*Wrapper(handle, …)` |
| **成员包装** | 一个**成员**的外围或具名缝 | `wraps[]` | `runtime.Wraps.Register(new WrapDeclaration(…), …)` |

两者共用相位语义与同一注册面（`WrapRegistry`），区别只在目标：判定器目标是 `judicator:<名>`，成员目标是 `作用域#成员名`。

### 4.1 三种相位

| 相位 | 签名（判定器侧） | 能做什么 | 顺序 |
| --- | --- | --- | --- |
| `before` | `Action<object[]?>` | 就地改写 `args` 元素（**不能短路**） | FIFO（新模型面） |
| `after` | `Func<object[]?, object[]?, object[]?>` | 接收「原参数 ＋ 核心结果」，返回新结果 | FIFO |
| `replace` | `Func<object[]?, object[]?>` | **替换整个核心**，不叠加 | 最后声明者生效 |

成员侧签名是统一的 `Func<dynamic, Context, CancellationToken, Task>`（与 handler 同形），
`before` 改写视图里的可写字段、`after` 做尾部副作用、`replace` 接管整个边集合。

### 4.2 严格签名校验（判定器侧）

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
- `RegisterReplaceWrapper<TDelegate>(handle, wrapper)` 要求目标实现 `IStrongTypedService<TDelegate>`（即 `Judicator<TDelegate>`）；
  否则 `InvalidOperationException`（适配错误）。

> 校验失败发生在**挂载期**（不是调用期）：签名不符立刻 `ArgumentException`，不会留下「挂了但永远不生效」的包装器。

### 4.3 成员包装：数据侧怎么写

关键结构：**包装器必须先声明成一个成员（带 `handler`），再由 `wraps[]` 引用它的 handler id**。
那个成员自己不需要挂到任何信号——它是包装器的载体。

```jsonc
{
  "schemaVersion": 4,
  "asset": {
    "kind": "component",
    "id": "diy.adjacent-cover.mitigation",
    "requires-host": "any",
    "active-when": "loaded",
    "members": [
      {
        "name": "onDamageApply",
        "visibility": ["callable", "mountable"],
        "view": {
          "fields": [
            { "name": "Target", "type": "entity", "writable": false, "required": true },
            { "name": "Amount", "type": "number", "writable": true, "required": true },
            { "name": "IsZeroed", "type": "boolean", "writable": true, "required": true }
          ]
        },
        "handler": { "id": "diy.adjacent-cover.mitigation#onDamageApply", "entry": "HandleAsync", "script": "…" }
      }
    ],
    "mounts": [],
    "wraps": [
      {
        "target": "engine#damage-apply",
        "phase": "before",
        "handler": "diy.adjacent-cover.mitigation#onDamageApply"
      }
    ]
  }
}
```

读法：**把本组件的 `onDamageApply` handler 挂到 `engine#damage-apply` 的外围（before）**。
每次有人走伤害处理端，先执行这段（改写 `Amount`／`IsZeroed`），再走 `damage-apply` 的边集合。

`wraps[]` 字段：

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `target` | 与 `judge` 二选一 | 成员地址 `作用域#成员名` |
| `judge` | 与 `target` 二选一 | 判定器名（不能同时给 `seam`） |
| `phase` | 是 | `before`／`after`／`replace` |
| `handler` | 是 | **handler 资产 id**（必须在本资产的 `members[].handler.id` 里存在） |
| `seam` | 否 | 具名缝名；只在成员目标上、且只能配 `before` |

约束（装载期拒绝）：

- `target` 缺 `#` → `addressing` 失败；
- `handler` 指向未注册的 id → 装载期 `KeyNotFoundException`（**最常见的手误**：写成成员名而不是 handler id）；
- `seam` 与 `judge` 同时出现 → `structure` 失败（判定器没有 handler 内部插入点）；
- `seam` 配 `after`／`replace` → `structure` 失败（缝内只有前置）。

### 4.4 判定器包装：数据侧怎么写

```jsonc
"judge-wraps": [
  {
    "judge": "move.leg.eligibility",
    "phase": "before",
    "entry": "Handle",
    "script": "Func<object[]?, object[]?, object[]?> Handle = (args, _) => { /* 改写 args */ return args; };"
  }
]
```

- `script` 是**同步**签名 `Func<object[]?, object[]?, object[]?>`；`entry` 缺省 `"Handle"`。
- 不需要先声明成员——`judge-wraps` 自带脚本，装载期直接编译（handler id 由 `资产id|判定器名` 自动合成）。
- 与 `wraps[]` 的差别仅此一处：**成员包装要借成员的 handler，判定器包装自带脚本**。

## 5. 两侧的执行顺序不一样

这是最容易写错的一处：**判定器侧是「洋葱」，成员侧是「三明治」**。

### 5.1 成员侧（`wraps[]`）

```
before（FIFO：w1 → w2）
  └─ replace 存在 → 只执行最后一个 replace（接管整个边集合）
     否则       → 按段序执行边集合（自有边 → 外来边）
after（FIFO：w1 → w2）
```

设 `w1` 先声明、`w2` 后声明：

```
w1-before → w2-before → 边集合（或 replace）→ w1-after → w2-after
```

**before 是 FIFO，不是 LIFO**——这是与旧模型（`Trigger<TView>` 时代）最容易被记忆带偏的一处。
`SeamTests`／`WrapChannelTests` 都锁了这个顺序。

### 5.2 判定器侧（`judge-wraps` / `Register*Wrapper`）

判定器侧是**管道**：每个 `before` 就地改写同一个 `args`，核心读最后的值；`after` 逐层改写 `result`。
新模型面（`judge-wraps`）的 `before` 与 `after` 都是 FIFO；旧域桶的 `before` 仍是历史 LIFO（见 §3 表）。

## 6. 具名缝（seam）

缝是 **handler 内部的插入点**，成员包装与判定器包装都到不了这个位置。

三处声明：

```jsonc
// 成员：允许在 "beforeRedirect" 处被插入
{ "name": "onTargeting", "seams": ["beforeRedirect"], "handler": { … } }

// 包装：只在那个缝上执行（相位只能是 before）
{ "target": "host#onTargeting", "phase": "before", "seam": "beforeRedirect", "handler": "…" }
```

```csharp
// handler 内：在这里执行该缝的包装器列表
await ctx.SeamAsync("beforeRedirect", ct);
// ... 核心逻辑
```

执行语义：

| 情况 | 结果 |
| --- | --- |
| 同一缝上多个包装器 | **LIFO**（后声明先执行，管道式） |
| 缝未声明就调用 `ctx.SeamAsync` | `KeyNotFoundException`（fail-fast） |
| 缝声明了但没人挂 | 空缝：不报错、无副作用 |
| 成员完全没声明缝 | 运行时不注入缝执行器，`ctx.SeamAsync` 抛 `InvalidOperationException` |
| 不在 handler 执行期调用 | `InvalidOperationException` |
| 缝名为空白 | `ArgumentException` |

> 为什么缝内只有 `before`：缝在 handler **内部**，「成员落地之后」在这里没有位置。
> 需要尾部副作用或整段替代，包在成员外围（`phase: after｜replace`）。

## 7. 最小片段（代码侧）：替换费用检查

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
var handle = registry.Register(JudicatorNames.CostCheck, new HalfCostJudicator());
registry.RegisterBeforeWrapper(handle, args => args![1] = (int)args[1]! / 2);

// 3) 只对某个玩家生效
registry.RegisterAfterWrapper(
    handle,
    Wrapper.After<Ref<Entity>, int, bool>((cardRef, cost, ok) => ok && cardRef.IsAlive),
    playerIndex: ownerIndex);
```

> 说明：`Wrapper.Before` 回调拿到的是**已拆包的参数副本**，改它不会写回 `args`；
> 要真正改写入参，用统一签名入口 `RegisterBeforeWrapper(handle, args => args![1] = newCost)`。
> 两种入口的差别就是「类型安全」与「可改写」。

## 8. 边界

| 边界 | 说明 | 替代 |
| --- | --- | --- |
| 数据侧不能**注册**判定器 | 只能包装既有判定器（`judge-wraps`） | 代码侧 `registry.Register` |
| 数据侧改不了外来边的**段名** | 资产 schema 的 `mounts[]` 无 `band` 字段 | 用 `wraps[]`（包装天然先于边集合），或转代码侧 |
| `replace` 只保留最后一个 | 不是叠加；注销后回退到前一个／核心 | 需要叠加用 `after` |
| 包装器必须先声明成成员 | `wraps[].handler` 是 handler 资产 id，不是成员名 | 声明一个不挂信号的成员承载它 |
| 具名缝只在声明它的成员上有效 | 挂到未声明的缝＝装载期拒绝；调用未声明的缝＝`KeyNotFoundException` | 在 `members[].seams` 声明 |
| 强类型 replace 需目标实现 `IStrongTypedService<TDelegate>` | 普通 `Judicator` 不支持 | 用统一签名 replace，或继承 `Judicator<TDelegate>` |
| 判定器侧 before 有 LIFO/FIFO 两面 | 旧域桶 LIFO、新模型面 FIFO，跨面次序无序 | 只用 `judge-wraps`，不混用 |

## 9. 本章自检

- [ ] 能说出判定器侧与成员侧执行顺序的**不同**（管道 vs 三明治；成员侧 before 是 FIFO）
- [ ] 能写出一个 `wraps[]` 声明，并解释为什么包装器要先声明成成员
- [ ] 能说出 `wraps[].handler` 填的是 handler id 而不是成员名
- [ ] 能说出三种相位的能力差异（改写入参 / 改写结果 / 替换核心）
- [ ] 能说出签名校验失败的时机与异常类型（挂载期 `ArgumentException`）
- [ ] 能写一个 `members[].seams` ＋ `wraps[].seam` ＋ `ctx.SeamAsync` 的最小闭环，并说出为什么只有 `before`
