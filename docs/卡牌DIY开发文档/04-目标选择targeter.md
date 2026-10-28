# 04 目标选择 targeter

> **本章解决什么**：讲清「选谁」：选择器怎么声明、候选从哪来、交互怎么跑、三态结果怎么处理。读完你能在 handler 内安全地发起一次选靶，并保证取消零副作用。
> **前置**：00（五段模型）、02（handler 与上下文）。
> **读完你能做什么**：①按场景选对选择器模板；②用 `ReferenceSetParameter` 交候选/数量/域校验；③正确处理 Ok/Cancelled/Failed 与重试。
> **对应代码路径**：`Orc.Game.Targeting.Selector` / `Selector<TResult>` / `ReferenceSetParameter` / `SelectorTemplates` / `ITargeterFlow` / `TargeterManager` / `SelectorResult<T>` / `TargeterResult`；`EffectRuntime.SelectAsync`（唯一选择入口）。
> **本章自检**：能说出「候选由后端给出」的具体含义；能区分 `SelectorStatus` 与 `TargeterStatus`；能解释取消为什么不产生副作用。

> **术语提醒（2026-10-28 核对）**：本章的 targeter 机制**仍然有效**（`TargeterManager`、`Selector`、`EffectRuntime.SelectAsync` 都在）。
> 唯一要留意的是**示例外层**：若把示例放进预制体，`hooks`／`injects`／`band`／`seam` 这些字段名已属**旧 schema**，
> v4 组件资产读到即拒绝装载。旧→新映射见 [09-新编排模型与术语迁移](./09-新编排模型与术语迁移.md)。
## 状态

- 章节状态：**草稿**
- 最近一次证据运行：—（原理章；行为由 `tests/Orc.Game.Tests/TargeterContractTests.cs` / `TargeterFlowTests.cs` 覆盖）

---

## 1. 选择器是什么

一次选靶 ＝ **选择器（怎么选）＋ 参数（选什么）＋ 流程（选几次/失败怎么办）**。

| 概念 | 符号 | 说明 |
| --- | --- | --- |
| 选择器基类 | `Selector` / `Selector<TResult>` | 定义交互方式（点击/拖拽）与产出类型 |
| 现成模板 | `SelectorTemplates` | 11 个稳定选择器（见 §1.1） |
| 参数基类 | `SelectorParameter` | 目前唯一实现＝`ReferenceSetParameter` |
| 流程宿主 | `ITargeterFlow` | `Step` / `Retry` / `RetryCount` |
| 玩家侧管理器 | `TargeterManager` | `ResolveFor(card)` 取管理器；`RunAsync(...)` 发起一次请求 |
| 单步结果 | `SelectorResult<TResult>` | Ok / Cancelled / Failed（+ 失败原因） |
| 请求结果 | `TargeterResult` | Ok / Cancelled / Failed（+ 失败原因/细节） |

### 1.1 选择器模板

| 模板 | 交互 | 典型用途 |
| --- | --- | --- |
| `SelectorTemplates.FieldUnit` | 拖拽 | 选场上单位 |
| `SelectorTemplates.HandOrigin` | 点击 | 手牌起始指向（单位/指令打出） |
| `SelectorTemplates.UnitHandDrag` | 拖拽 | 单位手牌拖到空槽 |
| `SelectorTemplates.AfterPlacement` | 点击 | 放置后指向 |
| `SelectorTemplates.Slot` | 点击 | 选槽位 |
| `SelectorTemplates.TargetPoint` | 点击 | 选目标点 |
| `SelectorTemplates.Hand` | 点击 | 选手牌 |
| `SelectorTemplates.Mulligan` | 点击 | 换牌 |
| `SelectorTemplates.Option` | 点击 | 选选项 |
| `SelectorTemplates.CardPickerList` | 点击 | 列表选卡 |
| `SelectorTemplates.CardPickerSet` | 点击 | 集合选卡 |

> 引用类模板（`FieldUnit`/`HandOrigin`/`Slot` 等）产出 `Ref<Entity>`；用 `.Value` 取目标（失效会抛 `StaleReferenceException`）。

## 2. `ReferenceSetParameter`：候选与约束

```csharp
public ReferenceSetParameter(
    IEnumerable<Ref<Entity>> candidates,   // 候选快照（不可含 null）
    int min,                               // 至少选几个（≥0）
    int max,                               // 至多选几个（≥1，且 ≥ min）
    Func<Ref<Entity>, bool>? domainValidator = null,  // 动态成员性（false＝不合规；抛异常＝失败）
    object? tag = null);                   // 呈现参数（纯交付数据）
```

| 参数 | 作用 | 失败 |
| --- | --- | --- |
| `candidates` | **后端给出的候选快照**（不是前端收集） | 含 `null`＝`ArgumentException` |
| `min`/`max` | 数量约束 | 越界/`min>max`＝`ArgumentOutOfRangeException`/`ArgumentException` |
| `domainValidator` | 候选的**动态**合法性（如「仍在手牌」） | 返回 false＝`InvalidSelection` |
| `tag` | 前端呈现用（如起始卡） | 不参与判定 |

> 候选是**快照**：请求发出后，即使世界变化，候选集合不变；动态变化用 `domainValidator` 表达。

## 3. `ITargeterFlow`：Step / Retry

```csharp
Task<SelectorResult<TResult>> Step<TResult>(Selector<TResult> selector, SelectorParameter parameter);
Task<SelectorResult<TResult>> Retry<TResult>();
int RetryCount { get; }
```

- 一次 `Step` ＝ 产出选择器并**等待**玩家应答。
- `Retry` 重入**同一**选择器（同实例、同标识）；超过上限＝`RetryLimitExceeded`。
- 流程写成普通 `async` 分支（继续/中止），没有隐藏状态机。

## 4. 三态结果

| 层 | 状态 | 含义 | 典型处理 |
| --- | --- | --- | --- |
| 单步 `SelectorResult<T>` | `Ok` | 成功，`Value` 非空 | 取值继续 |
| | `Cancelled` | 玩家取消（一等状态） | 中止请求、零副作用 |
| | `Failed` | 失败，`Failure`＝`InvalidSelection`/`RetryLimitExceeded`/`Fault` | 重试或中止 |
| 请求 `TargeterResult` | `Ok`/`Cancelled`/`Failed` | 汇总 | 只有 `Ok` 才继续结算 |

失败原因：

- `InvalidSelection`：不在候选集/数量不符/已失效/未过域判定。
- `RetryLimitExceeded`：重试超限。
- `Fault`：系统异常（预留）。
- `TargeterResult.Failed(TargeterFailureReason, detail?)` 另有请求级失败原因（如 `TargetingFailed`），由调用方映射到业务结果。

> **取消为什么零副作用**：取消发生在 `Step` 内，尚未执行任何动作面调用；调用方在 `IsCancelled` 时直接返回，不扣费、不改状态、不发信号。

## 5. 契约：后端给候选、前端只采集手势

```
后端（你的 handler）                     前端（UI/宿主）
  收集候选（快照）                        展示候选
  ── Step(selector, parameter) ──►        采集点击/拖拽
  ◄── 提交语义事件（选中引用）──
  校验（候选集 + 域判定 + 数量）
  ◄── Ok / Cancelled / Failed
```

- 前端**不筛候选、不判合法性**；它只把玩家手势转成语义事件。
- 后端负责：候选来源、数量约束、域校验、非法选择的重试策略。
- 候选来源限制：目前交互式选靶只能消费既有模板 + 你的候选列表；**没有**「注册自定义候选提供器」的公开面（见 §7）。

## 6. 完整片段：handler 内发起选靶

```csharp
using System.Linq;
using Orc.Cards;
using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Targeting;

/// <summary>让玩家选一个敌方前线单位；取消/失败/无候选＝null（零副作用）。</summary>
private static async Task<Card?> PickEnemyFrontUnitAsync(Card host, CancellationToken ct)
{
    // ① 候选由后端现收集（快照）：战场上的敌方单位
    var environment = GameEnvironment.ResolveFor(host);
    var owner = (host as CardBase)?.Owner;
    var targeter = TargeterManager.ResolveFor(host);
    if (environment is null || owner is null || targeter is null)
    {
        return null; // 脱局/无 targeter：降级
    }

    var candidates = environment.Battlefield.FrontLine
        .Select(slot => slot.Occupant)
        .OfType<CardBase>()
        .Where(unit => !ReferenceEquals(unit.Owner, owner))
        .Select(unit => unit.Ref)
        .ToList();

    if (candidates.Count == 0)
    {
        return null; // 无候选：不发起交互
    }

    // ② 一次请求＝一次 Step；非法选择用 Retry 重入
    Ref<Entity>? picked = null;
    var result = await targeter.RunAsync(async flow =>
    {
        var step = await flow.Step(
            SelectorTemplates.FieldUnit,
            new ReferenceSetParameter(
                candidates,
                min: 1,
                max: 1,
                domainValidator: reference => reference.IsAlive,
                tag: host));

        while (step.IsFailed && step.Failure == SelectorFailureReason.InvalidSelection)
        {
            step = await flow.Retry<Ref<Entity>>();
        }

        if (step.IsCancelled)
        {
            return TargeterResult.Cancelled();
        }

        if (step.IsFailed)
        {
            return TargeterResult.FromSelectorFailure(step.Failure);
        }

        picked = step.Value;
        return TargeterResult.Ok();
    });

    // ③ 只有 Ok 才取目标；取消/失败一律 null
    return result.IsOk && picked is { IsAlive: true } reference
        ? reference.Value as Card
        : null;
}
```

**数据侧统一入口**（`EffectRuntime.SelectAsync`，**唯一选择入口**）：

```csharp
// ① 确定性（all / self）：候选集即答案，不发起前端交互
var all = await runtime.SelectAsync(host, new EffectSelector("all", "enemy", "frontline"));

// ② 外部裁定（one / any / choose）：必须经提交通道（前端）
var picked = await runtime.SelectAsync(host, new EffectSelector("one", "enemy", "frontline"));
// 内部经 TargetCandidateEligibility 判定器给候选 + TargeterManager 发起请求；
// 无前端桥接＝明确失败（引擎不代选，R2），不是"取首个"。
```

> **S9 之后的口径（R2／A-6）**：三分类由 `sel` 决定，**取样语义与裁定语义分开**：
>
> | `sel` | 归谁 | 是否产生 targeter | 无前端时 |
> | --- | --- | --- | --- |
> | `all`／`self` | 判定器（候选集即答案） | 否 | 正常工作 |
> | `random` | 引擎随机服务（种子可复现） | 否 | 生产对局**明确报错**（需 `MatchOptions.AllowRandomTargeting`，R2c） |
> | `one`／`any`／`choose` | **前端**（提交通道） | 是 | **明确失败**（引擎永不代选） |
>
> `BeginTargetSelectionAsync` 已删除（零调用的死代码）；交互式选靶一律走 `SelectAsync`。
> 选靶请求的"待决请求 id／提交结果"沿用既有协议形态（`sessionId` + `picks` 提交），
> 卡效果发起的请求标注 `action = effect`。

## 7. 边界

| 边界 | 说明 | 替代 |
| --- | --- | --- |
| 候选来源固定 | 交互式候选由调用方提供或候选判定器收敛；无自定义 provider 注册面 | `SelectAsync`（确定性）或代码侧自行 `RunAsync` 传候选 |
| 数据侧发起交互受限 | csx 可调 `SelectAsync(sel=one/any)`，但候选/域判定受限（候选由候选判定器统一收敛） | 需要复杂候选→代码侧自行 `RunAsync` |
| 生产禁用随机 | `sel=random` 在生产对局报错（R2c） | 改确定性选择；测试/沙盒显式开启 |
| 取消语义 | 一等状态；调用方必须自己保证「取消＝零副作用」 | 在任何动作面前先判 `IsCancelled` |
| 重试上限 | 由 targeter 会话控制；超限＝`RetryLimitExceeded` | 循环里保留终止条件 |

## 8. 本章自检

- [ ] 能说出「候选由后端给出」的三层含义（收集/约束/域校验）
- [ ] 能区分 `SelectorResult<T>` 与 `TargeterResult` 的职责
- [ ] 能写出 Step → 非法重试 → Ok/Cancelled/Failed 的完整分支
- [ ] 能解释取消为何不产生扣费/状态变更
- [ ] 能指出「候选来源不可自定义」时的两种替代（无头选靶 / 自行 RunAsync）
