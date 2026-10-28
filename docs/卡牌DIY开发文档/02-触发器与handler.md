# 02 触发器与 handler

> **本章解决什么**：把「什么时候被调用」讲透——触发器声明（kind/band/hooks/保留槽）、执行排序、注册者与来源三规则、csx handler 签名、异常隔离。读完你能自己判断「这个 handler 会不会跑、跑的时候 `TriggerCard` 是谁」。
> **前置**：00（两面概念）、01（装载链与执行帧）。
> **读完你能做什么**：①按需求选对 kind/hooks/band；②给每个注册项找对注册者（卡/效果/透传）；③写出可编译的 csx handler 并正确读 `view.Source` 与 `view.TriggerCard`。
> **对应代码路径（2026-10-28 核对）**：`Orc.Core.Model.OrchestrationEntity`（`RunEdgesAsync`/`RegisterMember`/`AttachMemberEdge`）、`Orc.Core.Model.MemberEdges`（`For<TView>`）、`Orc.Core.Model.Members`（`MemberDefinition`/`OwnedEdge`/`OwnEdgePosition`/`MemberVisibility`）、`Orc.Core.LogicEngine`（`SubscribeMember`/`AttachMemberEdge`）、`Orc.Core.Context`、`Orc.Cards.CardEventView`、`Orc.Game.Triggers.CardTriggerView`。
> **本章自检**：能说出旧触发器概念在新模型里各对应什么；能判断一个 handler 会挂在哪个成员的哪条边上、执行时 `TriggerCard` 是谁。

> ## ⚠ 本章描述的对象已删除：`Trigger<TView>`（整章作术语对照读）
>
> 本章讲的是 `Orc.Core.Trigger<TView>`——**该类型已从代码库删除**（git 状态 `D`），连同 `Bus`、`TriggerEventInfo`、
> `PrefabManager.ResolveHandler`、`band`/`seam`/保留槽一起退役。本章**不再描述可执行机制**，只保留作**旧→新对照**。
>
> | 本章的旧概念 | 新模型对应物 | 在哪 |
> | --- | --- | --- |
> | `Trigger<TView>`（一个固定视图类型的注册表 ＋ 挂载单元）| **成员**（`MemberDefinition`）：名字 ＋ 视图声明 ＋ 可见性 ＋ 位次 | `RunEdgesAsync` |
> | `hooks: ["unit.deployed"]`（字符串订阅）| `mounts[].target = "engine#unit.deployed"`（成员地址）| `OrchestrationRuntime.Bind` |
> | `Register(name, handler, band, priority)` | **自有边** `OwnedEdge` ＋ 成员位次 `OwnEdgePosition` | `MemberEdges.For<TView>` |
> | `band` 方案 / `EventEntry.SortKey = band×1000＋priority` | 位次 `first`（流程型）/ `last`（管道型）＋ 包装相位 `before`/`after`/`replace` | `OrchestrationEntity` 类注释 R1i |
> | `seams` 动作缝 | **无**——需要中间插入＝拆成员 | — |
> | `RegisterDisposal` 保留槽 | **框架注销边**（自动挂、恒最后、作者不能写）| `ComponentDisposalPolicy` |
> | `kind: passive/active` | **无此分类**——改由 `MemberVisibility` 位标志（`hostable`/`mountable`/`callable`）| — |
> | 注册者三级回退 → `SourceCard` | 边归属（`owner`）＋ `view.TriggerCard`／`view.Source` | — |
>
> **仍然有效、可以照用**：§4 的 `Source` / `TriggerCard` 三规则与读法（名字和语义未变）、§3 的排序直觉
> （"谁先跑"→ 现由位次＋相位决定）、§5 csx handler 的签名与 `view` 字段读法。
>
> 旧→新完整术语映射见 [09-新编排模型与术语迁移](./09-新编排模型与术语迁移.md)。
## 状态

- 章节状态：**草稿**
- 最近一次证据运行：—（原理章；行为由 `tests/Orc.Tests/TriggerSourceTests.cs` 与 `TriggerExecutionTests.cs` 覆盖）

---

## 1. 触发器是什么

一个 `Trigger<TView>` 是**一个固定视图类型的注册表 + 一个挂载单元**：

| 组成 | 说明 | 符号 |
| --- | --- | --- |
| 名称 | 展示/留痕用（非唯一键） | `Trigger<TView>.Name` |
| 稳定键 | 定义级身份（审查链/引用用） | 构造参数 `stableKey` |
| kind | 主动/被动 | `TriggerKind.Active` / `Passive` |
| band 方案 | 默认 `DefaultBands` 或自定义枚举 | 构造参数 `bandType` |
| hooks | 被动触发器的总线订阅名（可多个） | 构造参数 `hooks` |
| 事件表 | 注册项（handler + band + priority + seams + registrant） | `Register` / `RegisterDisposal` |
| 所有者 | 框架卸载/宿主注入用（**不是**归因） | 构造参数 `owner` |
| 注册者 | 归因默认值（卡/效果） | 构造参数 `registrant` |

```csharp
var trigger = new Trigger<MyView>(
    name: "我的触发器",
    kind: TriggerKind.Passive,
    hooks: new[] { GameUpdates.UnitDeployed });
```

> **一个触发器只有一个视图类型**。视图不是「随便的类」：需要 `[ContextView]` 标记，属性用 `[Read]` / `[Mutate]` / `[Optional]` 声明；框架会生成代理类型，按属性名直读数据面。

## 2. kind 与三种注册形态

| 形态 | kind | 怎么被调用 | 典型用途 |
| --- | --- | --- | --- |
| **被动** | `Passive` ＋ 非空 `hooks` | `Bus.Mount` 后由 `LogicEngine.Emit` 驱动 | 卡/效果监听信号 |
| **主动** | `Active`（默认） | 直接 `InvokeAsync(engine, data)` | 流程编排、效果主触发器、指令施放链 |
| **保留槽** | 同所属触发器 | 普通事件全部执行完后**恒最后**执行 | 一次性销毁、收尾（`RegisterDisposal`） |

约束（构造/注册期 fail-fast）：

- `Active` 声明非空 `hooks` ＝拒绝（主动触发器不可挂载）。
- `Passive` 无 `hooks` ＝可构造，但 `Bus.Mount` 会拒绝（`InvalidOperationException`）——只能被直接 `Invoke`（纯注入型载体）。
- 保留槽与 band 数值无关，**位置不可被普通事件覆写**；`UpdatePriorities` 只影响挂载优先级，不改变槽位。

## 3. band 与排序键

执行序由 `EventEntry.SortKey` 决定：

```
SortKey = bandValue × 1000 + priority     // priority ∈ [0, 1000)
同分 → 注册序（Seq）升序
保留槽（IsDisposalSlot）→ 恒排在所有普通事件之后（先按此分组）
```

| 方案 | 注册写法 | 说明 |
| --- | --- | --- |
| 默认（未声明 `bandType`） | `Register(name, handler, priority: 10)` | 只有一个区段 0；传 `DefaultBands.Default` 也可 |
| 自定义（构造传 `bandType`） | `Register(name, handler, AttackFlowBands.Resolve, priority: 100)` | **每个注册必须显式带该枚举成员**，省略＝拒绝 |

现成 band 方案（游戏层流程）：

| 枚举 | 成员 | 语义 |
| --- | --- | --- |
| `AttackFlowBands` | `Counter(1)` / `Resolve(2)` / `Finalize(3)` | 反制检查 / 攻击结算 / 收尾 |
| `DamageFlowBands` | `PreApply(1)` / `Apply(2)` | 结算前记录 / 伤害生效 |
| `OrderFlowBands` | `Counter(1)` / `Resolve(2)` / `Finalize(3)` | 反制检查 / 施放结算（`card.played` priority 0 → 主动效果 priority 100）/ 收尾（扣费/离手） |
| `ActionTriggerBands` | `Initiate(1)` / `Announce(2)` / `CounterGuard(3)` / `Counter(4)` / `Adjust(5)` / `Commit(6)` | 底层动作触发端：发起前 / 宣告 / 禁反制 / 反制 / 落地前调整 / 转发处理端 |
| `ActionHandleBands` | `Rewrite(1)` / `Veto(2)` / `Apply(3)` | 底层动作处理端：改写 / 否决 / 落地 |

```csharp
// 同 band 内：priority 小的先执行；同为 100 时按注册序
trigger.Register("先手", HandlerA, AttackFlowBands.Resolve, priority: 10);
trigger.Register("后手", HandlerB, AttackFlowBands.Resolve, priority: 20);
```

> **数据侧限制**：预制体**事件**的 `band` 字段只接受 `"default"` 与 `"disposing"`（落保留槽），其它取值＝构造期 `ArgumentException`（不会静默等同默认区段）；要指向自定义 band 方案 → 代码侧 `Inject`（见 §7）。预制体 **`injects.band`** 另有一条通道：可按名解析目标触发器的 band 成员（G-03 已解决，P3；见 07 章）。

## 4. 注册者与来源（三规则）

### 4.1 规则与回退链

`EventEntry.Registrant` 的取值链（注册期解析一次）：

```
Register(registrant: X) → Trigger 构造参数 registrant → 触发器 owner（仅当它是 Card/Effect）→ null
```

非 `Card`/`Effect` 的注册者按 `null` 处理（不抛）。`SourceCard` 是**读取期派生**：

| 注册者 | `SourceCard` |
| --- | --- |
| `Card` | 该卡 |
| `Effect` | 效果宿主卡（`HostOrNull`；宿主销毁后自动降级 `null`，不抛） |
| 其它/null | `null` |

### 4.2 三规则 + 具体例子

例子共用一个探针视图（真实视图写法：`[ContextView]` + `[Optional][Read]`）：

```csharp
[ContextView]
public class ProbeView
{
    [Optional]
    [Read]
    public virtual Card? TriggerCard { get; set; }
}
```

**规则 1：卡/效果注册 → 覆盖**（注册者的卡优先于入口初值）

```csharp
var card = new Card(engine, "引发者");
var trigger = new Trigger<ProbeView>("探针");
Card? seen = null;

trigger.Register("卡注册", (view, ctx, ct) =>
{
    seen = view.TriggerCard;   // 帧值＝注册者的卡
    return Task.CompletedTask;
}, registrant: card);

await trigger.InvokeAsync(engine);   // 未传 TriggerCard
// seen == card（覆盖生效）
```

**规则 2：非卡注册 → 透传**（不改写，原样继承）

```csharp
Card? seen = null;
trigger.Register("引擎流程", (view, ctx, ct) =>
{
    seen = view.TriggerCard;
    return Task.CompletedTask;
}); // registrant 缺省＝null（且 owner 不是 Card/Effect）

await trigger.InvokeAsync(engine, new Dictionary<string, object?>
{
    [TriggerPayloadKeys.TriggerCard] = card,   // 入口初值
});
// seen == card（继承，不是覆盖）
```

**规则 3：最后者胜**（同一条链上多个卡/效果注册项，后者覆盖前者）

```csharp
var a = new Card(engine, "甲");
var b = new Card(engine, "乙");
var seen = new List<Card?>();

trigger.Register("甲", (view, _, _) => { seen.Add(view.TriggerCard); return Task.CompletedTask; }, registrant: a);
trigger.Register("乙", (view, _, _) => { seen.Add(view.TriggerCard); return Task.CompletedTask; }, registrant: b);

await trigger.InvokeAsync(engine);
// seen == [a, b]：甲先看到自己，乙再覆盖为自己的宿主
```

### 4.3 入口初值与 `Source` 的边界

- 入口只认 `TriggerPayloadKeys.TriggerCard`（字面值 `"TriggerCard"`）：值是 `Card` → 作初值；`null`/缺键 → 继承父帧；**其它类型 → 立即抛 `InvalidCastException`**（错形态不静默）。
- `Source` **不进帧、不作初值**：它只是信号载荷里的一个键，视图按名读。

#### 两种 `Source`，别混用

| | 出现在 | 含义 | 谁写 |
| --- | --- | --- | --- |
| **R9 信号视图的 `Source`** | `CardEventView.Source`、`CardTriggerView.Source`（`hook` 信号） | **事件引发者**（「这张牌为什么死了/为什么被弃」） | 发射方（`Emit*(..., source:)`） |
| **流程视图的 `Source`** | `DamageEventView.Source`、`DamageApplyView.Source`、`AttackFlowView.Source` | **本次流程的施动方**（「这一下是谁打的」） | 流程入口（`DamageAsync`/`BattleAsync`/攻击流程） |

- 信号视图的 `Source` 回答**归因**问题：`card.discarded` 的 `Source`＝谁导致弃牌（可空，见 [信号表](./reference/信号表.md)）。
- 流程视图的 `Source` 回答**结算**问题：它不经过总线、不进帧，只活在这次伤害/攻击的视图里；改它不会改任何信号的 `Source`。
- 同一个 handler 里两个都可能出现（例如监听 `card.damaged` 的触发器视图有 `Source`，而它内部发起的 `DamageEventView` 也有 `Source`）——先问「我要归因还是要施动方」。

### 4.4 审计面

```csharp
foreach (var info in trigger.Events)
{
    Console.WriteLine(
        $"{info.Name}: registrant={info.Registrant?.GetType().Name ?? "null"}, " +
        $"sourceCard={info.SourceCard?.Name ?? "null"}, priority={info.Priority}, band={info.BandValue}");
}
```

`trigger.Events` 是**执行序快照**（含 band/priority/包装器计数/保留槽标记），用于排查「为什么它先跑」。

## 5. csx handler：签名与 `entry`

### 5.1 签名（唯一合法形态）

```csharp
Func<TView, Context, CancellationToken, Task>
```

- `TView` 必须与触发器的 `viewType` 一致（不一致＝`signature` 失败）。
- 委托必须是脚本里的**顶层变量**，变量名＝事件 `entry`（默认 `"HandleAsync"`）。
- csx 需要 `using` 的命名空间要写全限定名（脚本上下文不保证 using）。

### 5.2 事件 JSON 形态

```json
{
  "id": "onUnitDeployed",
  "entry": "HandleAsync",
  "csx": "Func<Orc.Cards.CardEventView, Context, CancellationToken, Task> HandleAsync = async (view, ctx, ct) =>\n{\n    if (view.Unit is not Orc.Cards.Card unit)\n    {\n        return;\n    }\n\n    if (!object.ReferenceEquals(unit, view.Host))\n    {\n        return;\n    }\n\n    var runtime = Orc.Game.Effects.EffectRuntime.ResolveFor(unit);\n    if (runtime is null)\n    {\n        return;\n    }\n\n    await runtime.DrawAsync(unit, 1, ct, unit);\n};\n",
  "version": 1
}
```

字段说明：

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `id` | 是 | 事件 id（同一触发器内定位/包装器 `target` 用） |
| `entry` | 否 | 默认 `"HandleAsync"`；必须是脚本顶层变量名 |
| `csx` | 二选一 | 内联脚本文本 |
| `assemblyKey` | 二选一 | 已注册的程序集处理器键（`PrefabManager.RegisterHandler`） |
| `downstream` | 否 | 静态下游声明（审查链用，不改运行行为） |
| `seams` | 否 | 本事件声明的动作缝名（配合 before 包装器） |
| `band` | 否 | `"default"`（普通区段）/ `"disposing"`（保留槽）；其它值＝构造期 `ArgumentException` |
| `version` | 否 | 事件版本（缺省 1） |

解析失败分类（`HandlerResolution`）：`handler-missing`（assemblyKey 未注册）／`signature`（签名不符）／`evaluator-missing`（未装 `IScriptEvaluator`）／`entry-missing`（脚本无该变量或不是委托）／`compile`。

### 5.3 通用视图的可读键（csx 常用）

| 视图 | 键 | 类型 | 备注 |
| --- | --- | --- | --- |
| `CardEventView` | `Card` | `object?` | 需 `is Orc.Cards.Card` 转型 |
| | `Unit` | `object?` | `unit.*` 信号的卡 |
| | `Host` | `object?` | 效果宿主（注入型 handler 可读） |
| | `Player` | `object?` | 只有玩家的信号 |
| | `Killer` | `object?` | `card.died` 击杀者 |
| | `Source` | `Card?` | **事件引发者**（可空） |
| | `TriggerCard` | `Card?` | **当前触发者**（帧值） |
| `CardTriggerView` | `Card` / `Player` / `Position` / `CaptureBox` / `Argument` / `SelectorSlots` / `Source` / `Target` / `TriggerCard` | 混合 | 打出/部署/加入链、反制适配视图（`Target`＝被反制指令的施放目标） |

## 6. 异常隔离语义

| 阶段 | 异常 | 结果 |
| --- | --- | --- |
| 入口载荷（`TriggerCard` 错形态） | `InvalidCastException` | **直接上抛**（不进契约兜底） |
| 验证（`Validate`） | 判定器内部异常 | 契约兜底 → `ExecutionOutcome.ContractFailure` |
| 视图绑定/结构错误 | 任意 | 契约兜底 → `ContractFailure`（记录后安全结束） |
| 单个 handler | `OperationCanceledException` | **上抛**（取消不隔离） |
| 单个 handler | 其它异常 | 记录 `exception:<类型>`（Error 级）→ **`continue`**：本事件的 after 包装器不执行，后续事件照常 |
| 动作面返回 false/0 | 无异常 | 链照常，`Outcome` 不变（调用方自己判返回值） |

> 关键差异：**handler 异常不会让整条链失败**，也不会回滚已发生的副作用；它只写一条错误日志。需要「出错就整体失败」的场景，要在 handler 内自己抛到链外（或检查返回值）。

## 7. 包装器在管道中的位置（详见 03）

单个事件的一次执行：

```
before 包装器（LIFO：后注册先跑）
  └─ replace 包装器（若有，替代 core；最后一个生效）
       或 core handler
after 包装器（FIFO：先注册先跑；异常时跳过）
```

- 触发器包装器签名与 handler 相同：`Func<TView, Context, CancellationToken, Task>`。
- `before` 只能**读** `ctx` 并做副作用（日志/外部计数）；**不能替换入参引用、不能短路**；`Context.Data` 是库内面，作者侧不可写。
- `after` 是尾部副作用（不改已产生的状态）。
- 具名**动作缝**：注册时 `seams: new[] { "标记" }`；handler 内 `await ctx.SeamAsync("标记", ct)`；`before` 包装器可挂在该缝上（`RegisterBeforeWrapper(target, seam, wrapper)`），按 LIFO 执行。

## 8. 最小片段：卡自身注册 handler + 审计

```csharp
using Orc.Cards;
using Orc.Core;
using Orc.Game;

// 位于 PassiveEffect.OnMount 内：Name / Host 来自 Effect 基类；_engine 为构造期注入
var trigger = new Trigger<CardEventView>(
    $"{Name}/弃牌监听",
    TriggerKind.Passive,
    hooks: new[] { GameUpdates.CardDiscarded });

// 卡自身注册 → 覆盖为「本卡」（监听者宿主）
trigger.Register("记录归因", (view, ctx, ct) =>
{
    var source = view.Source;          // 事件引发者（可空）
    var self = view.TriggerCard;       // 当前触发者＝本卡（Host）
    Console.WriteLine($"source={source?.Name ?? "null"}, self={self?.Name ?? "null"}");
    return Task.CompletedTask;
}, registrant: Host);

_engine.Bus.Mount(trigger);
```

数据侧等价写法（hooks + 通用视图，推荐）：

```json
{
  "id": "onDiscarded",
  "entry": "HandleAsync",
  "csx": "Func<Orc.Cards.CardEventView, Context, CancellationToken, Task> HandleAsync = async (view, ctx, ct) =>\n{\n    if (view.Source is not Orc.Cards.Card source || view.TriggerCard is not Orc.Cards.Card self)\n    {\n        return;\n    }\n\n    if (object.ReferenceEquals(source, self))\n    {\n        return; // 自己的弃置：不触发\n    }\n\n    var runtime = Orc.Game.Effects.EffectRuntime.ResolveFor(self);\n    if (runtime is null)\n    {\n        return;\n    }\n\n    await runtime.DrawAsync(self, 1, ct, self);\n};\n",
  "version": 1
}
```

## 9. 边界

| 边界 | 说明 | 替代 |
| --- | --- | --- |
| 数据侧不能选 band（事件级） | 预制体 `band` 仅识别 `"disposing"` | 代码侧 `Inject` 到目标 band |
| 数据侧不能给共享触发器注入 | `injects.band` 已可按名解析（P3）；目标触发器无专门 band 方案、或名不是成员＝隔离记录 | 同上（代码侧 `Effect.Inject`） |
| `Source` 不保证非空 | 取决于发射方是否传 `source` | 读已有载荷键（如 `Unit`）或 `TriggerCard`；见 [信号表](./reference/信号表.md) §7 |
| 一个触发器一个视图 | 不能在同一触发器里用两种视图 | 建两个触发器（同一 hooks） |

## 10. 本章自检

- [ ] 能说出 `Registrant` 的三级回退与 `SourceCard` 的派生规则
- [ ] 能举出「卡覆盖 / 非卡透传 / 最后者胜」三个例子并写出预期值
- [ ] 能算出两个事件（band=2/priority=10 与 band=3/priority=0）谁先执行
- [ ] 能写出一个合法 csx 事件对象（`entry` 与变量名一致）
- [ ] 能说出 handler 抛异常后：本条 after 跳过、后续事件继续、链不失败
