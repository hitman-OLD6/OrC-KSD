# 02 成员、信号与 handler

> **本章解决什么**：把「什么时候被调用、被调用时执行什么、按什么顺序执行」讲透——信号是什么、成员是什么、
> `mounts` 怎么把成员接到信号上、`band` 段序怎么排、具名缝从哪插入、csx handler 怎么写、异常怎么走。
> **前置**：00（心智模型）、01（效果原理）。
> **读完你能做什么**：①判断一个 handler 挂在哪个成员的哪条边上；②写出能通过装载期校验的 `mounts`／`wraps` 声明；
> ③算清同一成员上多条边的执行序；④写出可编译的 csx handler 并正确区分 `Source` 与 `TriggerCard`。
> **对应代码路径**：`Orc.Core.Model` 下的 `Members`（`MemberDefinition`／`OwnedEdge`／`MemberVisibility`）、
> `Signals`（`SignalMember`）、`Mounting`（`MountDeclaration`／`MountEdge`／`WrapDeclaration`）、
> `OrchestrationEntity`（`RunEdgesAsync`／`CollectUnits`）、`OrchestrationRuntime`（`MountPlanner`）、
> `KernelSignals`／`GameSignals`（信号清单）、`Orc.Core.LogicEngine`（`SubscribeMember`／`AttachMemberEdge`）。
> **本章自检**：能说出「信号」与「成员」的关系；能算出「`band=Commit` 的自有边 + `band=Initiate` 的外来边」谁先跑；
> 能解释 `self#` 为什么不受可见性约束；能说出 `Source` 与 `TriggerCard` 各自由谁写。

> **文件名说明**：本章原名「触发器与 handler」。v4 编排实体化后 `Trigger<TView>` 类型已从代码库删除，
> 「触发器」这一概念由**成员**（`MemberDefinition`）取代。文件名保留是为了不打断既有链接；
> 正文只描述现行模型。旧写法如何读懂，见 [09 章](./09-新编排模型与术语迁移.md) 的映射表。

## 状态

- 章节状态：**完成**
- 最近一次证据运行：2026-10-29（`tests/Orc.Tests/Model/MemberBandOrderTests.cs`、`SeamTests.cs`、
  `tests/Orc.Game.Tests/EngineEntityTests.cs` 全绿）

---

## 1. 三句话建立坐标系

1. **一切可执行的逻辑都挂在「成员」上**。成员是「一个视图声明 ＋ 一组自有边」的命名执行单元。
2. **信号是最普通的一种成员**。`unit.deployed` 之所以能「触发」，是因为引擎预制体里有一个叫 `unit.deployed` 的成员，
   它的 `SignalTiming` 非 null，于是别人可以挂到它上面。
3. **「挂上去」＝ 加一条边**。边有三种来源：成员自带的自有边、别人挂上来的外来边、框架加的注销边。

| 词 | 是什么 | 在哪声明 | 地址 |
| --- | --- | --- | --- |
| **成员** | 视图 ＋ 可见性 ＋ 自有边 ＋ 合法性检验 ＋ 段序 ＋ 具名缝 | 预制体 `members[]`（资产层即组件 JSON） | `作用域#成员名` |
| **信号** | `SignalTiming` 非 null 的成员（时机入口） | 内核 `engine` / 游戏层 `engine.game` | `engine#<信号名>` |
| **自有边** | 成员自己的执行体（＝旧模型的「主逻辑」） | `members[].handler`（资产）／`OwnedEdge`（C#） | — |
| **外来边** | 别人挂到该成员上的边（＝旧模型的「订阅」） | `mounts[]`（资产）／`SubscribeMember`（C#） | 指向目标成员 |
| **框架边** | 注销用的边（作者不能写） | 装配时由框架合成 | `engine#card.destroyed` |

> 新模型**没有**「主动／被动」分类字段，**也没有**优先级数字。一个成员会不会被执行，只看有没有边指向它；
> 执行序只看段序与声明序。旧资产的 `kind`／`priority`／`stableKey` 在 v4 schema 里是**装载期拒绝字段**。

---

## 2. 成员：一个可被挂载的执行单元

### 2.1 声明字段

资产层（`*.component.json` 的 `asset.members[]`）：

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `name` | 是 | 成员名。**允许含 `.`**（如 `card.destroyed`），寻址时零转换保留 |
| `visibility` | 否 | 可见性数组，取值 `hostable`／`mountable`／`callable`；缺省＝空（私有） |
| `view.fields[]` | 否 | 视图字段：`{ name, type, writable?, required? }`；`writable` 缺省 false，`required` 缺省 true |
| `handler` | 否 | `{ id, entry?, script? , assembly-key? }`；`script` 与 `assembly-key` 二选一。**省略 handler ＝纯编排成员**（只有视图、没有自有边） |
| `band` | 否 | 自有边落在段序方案的哪一段（段名字符串）；必须出现在 `bands` 里 |
| `bands` | 否 | 段序方案（有序段名数组）。省略＝单段方案（所有边同段、按声明序） |
| `seams` | 否 | 具名缝声明（缝名数组）；省略＝不声明任何缝 |

视图字段类型是**封闭集**：`entity`／`number`／`string`／`boolean`／`string-array` 一类内建类型，
不是任意 CLR 类型。声明在成员上的字段名，就是 csx 里 `view.<Name>` 能读到的名字。

### 2.2 可见性三位（默认全关）

| 位 | 含义 | 缺了会怎样 |
| --- | --- | --- |
| `hostable` | **可作宿主**：别的成员能挂到我身上 | 他人挂载被装载期拒绝（`visibility` 分类） |
| `mountable` | **可被挂载**：我能被挂到别处 | 本成员挂出去被装载期拒绝 |
| `callable` | **可被调用**：能被 `InvokeAsync` 跨实体调用 | 跨实体调用抛 `PermissionDeniedException` |

**`self#` 自挂载不受可见性约束**——同一个组件内部成员之间的编排是作者自由，不需要给自己开位。

跨实体挂载（`engine#` ／ `host#`）**两侧都要有相应位**：目标要 `hostable`，来源要 `mountable`。

### 2.3 成员从哪来

| 来源 | 层标记 | 例子 |
| --- | --- | --- |
| 内核 `engine` 预制体 | `Kernel` | `card.placed`、`card.destroyed`、`card.data`、`effect.removed` |
| 游戏层 `engine.game`（`extends: engine`） | `Game` | 35 个游戏信号 ＋ 25 个流程成员（`damage-apply`、`unit-attack`…） |
| 卡牌类型预制体 | `Game` | `base` ← `unit`／`order`／`counter`／`hq`；卡上的 `targeting`、`deploy`、`play`… |
| 组件资产 | `Asset` | 卡包里 `*.component.json` 声明的成员 |
| 运行期注册 | `Runtime` | `OrchestrationEntity.RegisterMember`（引擎内部用） |

继承展开在**装载期**完成（`PrefabCatalog.Build`）：成员表「父先子追加、同名覆写」，所以子预制体不需要重抄父成员。
成员名在**同一实体**内唯一；跨实体同名不算冲突（地址里的作用域负责消歧）。

---

## 3. 信号：成员的一种

### 3.1 信号的判定条件

信号不是另一套机制，只是「`SignalTiming` 非 null 的成员」：

- 可见性固定为 `Hostable | Callable`（可被挂载＝可订阅；可被调用＝可发射）；
- 视图＝载荷声明，字段**全部可选**（信号允许部分缺省，如 `card.died` 的 `Killer` 可为 null）；
- 地址＝`engine#<信号名>`，名字**零转换**（`hooks: ["unit.position.changed"]` → `engine#unit.position.changed`）。

### 3.2 信号清单（39 条＝内核 4 ＋ 游戏层 35）

**内核（`engine`）**

| 信号 | 时机 | 载荷 |
| --- | --- | --- |
| `card.placed` | 入场期 | `Card` |
| `card.destroyed` | 任意 | `Card` |
| `card.data` | 任意 | `Card` |
| `effect.removed` | 任意 | `Card`、`Effect` |

**游戏层（`engine.game`，全部 `Any`，除注明）**

| 组 | 信号 | 载荷 |
| --- | --- | --- |
| 回合 | `turn.start.before`／`turn.start`／`turn.start.after`／`turn.end.before`／`turn.end` | `TurnNumber`、`Player`、`Source` |
| 卡 | `card.played`／`card.drawn`／`card.hand.add`／`card.discarded`／`card.burned` | `Card`、`Player`、`Source` |
| 卡·数值 | `card.stat.changed` | `Card`、`ChangedFields`、`Source` |
| 卡·加载 | `card.load`（**`Load` 时机**） | `Card`、`Player`、`Source` |
| 卡·死亡 | `card.died`／`card.dying` | `Card`、`Killer`、`Source` |
| 卡·离场 | `card.leaving` | `Card`、`Reason`、`Source` |
| 词条 | `keyword.attack.finished` | `Card`、`Source` |
| 单位 | `unit.joined`／`unit.deployed` | `Unit`、`Position`、`Source` |
| 单位 | `unit.position.changed` | `Unit`、`OldPosition`、`NewPosition`、`Source` |
| 单位 | `unit.upgraded`／`unit.revealed`／`unit.acted`／`unit.combat.survived` | `Unit`、`Source` |
| 单位 | `unit.types.changed` | `Unit`、`AddedType`、`Source` |
| 单位 | `unit.damage.dealt` | `Unit`、`Card`、`Amount`、`Source` |
| 伤害 | `card.damaged` | `Card`、`Amount`、`Source` |
| 反制 | `counter.triggered` | `Card`、`Player`、`Source` |
| 反制 | `counter.disarmed` | `Card`、`Player`、`Reason`、`Source` |
| 卡组 | `deck.shuffled` | `Player`、`Deck`、`Source` |
| 资源 | `slot.gained`／`slot.lost` | `Player`、`Amount`、`Source` |
| 资源 | `slot.changed` | `Player`、`OldSlots`、`NewSlots`、`Source` |
| 资源 | `point.gained`／`point.lost` | `Player`、`Amount`、`Source` |
| 资源 | `point.changed` | `Player`、`OldPoints`、`NewPoints`、`Source` |

> 权威清单是 `Orc.Game.Model.GameSignals.Names`（35 条，守护测试锁漂移）＋ `KernelSignals.Names`（4 条）。
> 逐信号的发射时机与归因现状见 [参考层 · 信号表](./reference/信号表.md)。

### 3.3 时机与装配时点必须匹配

`SignalTiming` 只有三个值，作用只有一个：**判断「这个组件赶不赶得上」**。

| 时机 | 含义 | 谁能赶上 |
| --- | --- | --- |
| `Any` | 与装配批次无关（绝大多数信号） | 所有组件 |
| `Load` | 加载期信号（目前只有 `card.load`） | 只有 `active-when: loaded` 的组件 |
| `Placed` | 入场期信号（`card.placed`） | `active-when: placed` 的组件正常参与 |

**装载期拒绝**：`active-when: placed` 的组件挂到 `timing = Load` 的信号上＝`timing` 分类错误。
理由是它在那时还没装配，永远不会生效——这是配置错误，不是「到时再看」。
真要在加载期生效，就把 `active-when` 改成 `loaded`。

---

## 4. 挂载：把成员接到信号上

### 4.1 声明形态

```json
{
  "members": [
    {
      "name": "onDeployed",
      "visibility": ["callable", "mountable"],
      "view": { "fields": [{ "name": "Unit", "type": "entity" }] },
      "handler": { "id": "diy.grenadier#onDeployed", "entry": "HandleAsync", "script": "…" }
    }
  ],
  "mounts": [
    { "target": "engine#unit.deployed", "member": "onDeployed" }
  ]
}
```

读法：**把本组件的 `onDeployed` 成员，挂到引擎的 `unit.deployed` 成员上**。
执行方向是「`unit.deployed` 被发射 → 依次执行挂在上面的边 → 每条边调用其声明实体的 `onDeployed`」。

`mounts[]` 只有两个字段：`target`（目标成员地址）与 `member`（本组件里要挂出去的成员名）。
**没有 `phase`、没有 `priority`、没有 `band`**——想改写走 `wraps[]`（03 章），想要次序靠声明序。

### 4.2 装载期六条校验

`MountPlanner.Plan` 在装载期一次性算定，任一条不过就进 `PrefabLoadReport`（该边不建立）：

| # | 校验 | 失败分类 | 典型报错 |
| --- | --- | --- | --- |
| ① | 地址形如 `作用域#成员名` | `addressing` | 缺少 `#`／作用域不是 `engine`/`host`/`self` |
| ② | 目标成员存在 | `addressing` | 「被挂载的成员 'x' 在预制体 'y' 上不存在」 |
| ③ | 目标声明 `hostable`，来源声明 `mountable`（`self#` 豁免） | `visibility` | 「未声明『可作宿主』，跨实体挂载被拒绝」 |
| ④ | 来源成员的**必填**视图字段 ⊆ 目标视图字段 ∪ 自身成员 | `view` | 逐个列出缺失字段名 |
| ⑤ | 挂载声明不得携带相位 | `channel` | 「挂载管编排、包装管修改」 |
| ⑥ | `host#` 按 `requires-host` 允许的**全部**宿主类型逐个校验 | `addressing` | 某宿主类型上没有该成员 |
| ⑦ | `placed` 组件不得挂 `Load` 时机信号 | `timing` | 见 §3.3 |

第 ④ 条只校验**必填**字段：可选字段不在目标视图里＝投影为类型默认值（与旧路径「载荷缺键即 null」一致）。
若把可选字段也纳入校验，一个 handler 同时挂 `turn.start`／`unit.deployed`（载荷形状不同）就会被全量误杀。

### 4.3 三种作用域

| 作用域 | 解析成什么 | 典型用法 |
| --- | --- | --- |
| `engine#x` | **引擎实体**的成员 `x` | 订阅信号；挂到流程成员（`damage-apply`、`unit-attack`） |
| `host#x` | **宿主实体**（卡牌／HQ）的成员 `x` | 挂到卡自己的 `targeting`、`deploy`、`play` |
| `self#x` | **本实体**的成员 `x`（组件内部编排） | 组件内 A 成员挂 B 成员；两侧可见性都不校验 |

`host#` 只对**组件实体**成立：卡牌预制体自己没有「宿主」。组件引用了某宿主类型独有的成员（如 `host#deploy`），
就必须把 `requires-host` 收窄到该类型，否则装载期报「目标成员不存在」。
`host#` 在 `requires-host` 允许多个宿主类型时逐个校验，但最终只产生**一条**边（边落在实际宿主实例上）。

---

## 5. 执行顺序：段序 ＋ 同段内序

### 5.1 排序键

一次成员执行的主流程（`OrchestrationEntity.ExecuteMemberAsync`）：

```
① one-shot 检查（该组件已消费额度 → 本次不执行）
② 合法性检验（member.Validation，拒绝 → ValidationRejected）
③ before 包装器         —— 声明序正序（FIFO）
④ 边集合：
     replace 包装器存在 → 只执行最后声明的那个（接管整个边集合）
     否则             → 按「段序 + 同段内序」依次执行
⑤ after 包装器          —— 声明序正序（FIFO）；被 Stop 后跳过
⑥ one-shot 置位
```

第 ④ 步内部（`CollectUnits`）：

```
排序键 = (段下标, 同段内种类, 同段内序)
  段下标   = band 在该成员 bands 方案里的下标；band 为 null ＝ 0
  同段内   = 自有边（0）在前，外来边（1）在后
  同段内序 = 自有边：声明序 → 运行期接入序
             外来边：边表快照序（声明序 → 追加序）
```

即：**同段内自有边恒在前，外来边恒在后**。跨段则只看段名在方案里的下标。
框架注销边是「声明序边」的一部分，位次＝该组件所有已声明挂载之后。

### 5.2 三套段序方案（`BandSchemes`）

| 方案 | 段序（下标小者先） | 用在哪 |
| --- | --- | --- |
| `ActionTrigger` | `Initiate` → `Announce` → `CounterGuard` → `Counter` → `Adjust` → `Commit` | 动作**触发端**（`join.deploy`、`combat-damage`、`unit-attack`…） |
| `ActionHandle` | `Rewrite` → `Veto` → `Apply` | 动作**处理端**（`join.handle`、`damage-apply`、`attack-damage`…） |
| `OrderFlow` | `Counter` → `Resolve` → `Finalize` | **指令流**（`order.cast` → `order.finalize`） |

每个动作端点的「自有边落哪一段」由 `EngineProcesses.ActionEndPositions` 固定（21 端，守护测试锁）：
触发端的自有边一律在 `Commit`（转发），处理端一律在 `Apply`（落地），指令施放在 `Resolve`。
这也解释了为什么「外来改写」天然早于「落地」：改写在更早的段，落地在末段。

> **段名只是字符串，语义在方案里**。`Commit` 在 `ActionTrigger` 里是末段，`Counter` 在 `OrderFlow` 里是首段——
> 同名不同序，因为方案的段序不同。写声明时先确认目标成员用的是哪套方案。

### 5.3 数据侧能控制到哪一步

| 想做的 | 资产层（JSON） | 代码侧（C#） |
| --- | --- | --- |
| 决定自有边落哪一段 | ✅ `members[].band` ＋ `members[].bands` | ✅ `MemberDefinition(band:, bands:)` |
| 决定外来边落哪一段 | ❌ **资产 schema 无此字段** | ✅ `SubscribeMember(..., band:)`／`AttachMemberEdge(..., band:)` |
| 决定同段内的先后 | ✅ 声明序（`mounts[]` 的数组顺序） | ✅ 声明序 → 运行期接入序 |
| 决定优先级数字 | ❌ 无此概念（`priority` 是旧字段，装载期拒绝） | ✅ 仅 C# 侧 `priority` 参数（同段内先小后大，留给引擎内部） |

这是数据侧的一个真实边界：**外来边在资产层只能「与自有边同段、排在自有边之后」**。
想让改写先于落地，正确做法是 `wraps[]`（包装在外围，天然先于边集合），而不是挂载。

---

## 6. 具名缝（seam）

缝是 **handler 内部的插入点**，用于「在逻辑中间插一段别人的改写」——这是包装和挂载都做不到的位置。

三处声明：

| 在哪 | 写什么 | 含义 |
| --- | --- | --- |
| 成员 | `members[].seams: ["beforeRedirect"]` | 本成员的 handler 里**允许**在 `beforeRedirect` 处被插入 |
| 包装 | `wraps[].seam: "beforeRedirect"` | 这条包装只在那个缝上执行；**相位只能是 `before`** |
| handler | `await ctx.SeamAsync("beforeRedirect", ct)` | 在这里执行该缝的包装器列表 |

执行语义（`SeamTests` 覆盖）：

- **LIFO**：同一缝上多个包装器，后声明的先执行（管道式：每个包装器包住「后续包装器 + 剩余逻辑」）。
- 缝**未声明**就调用 `ctx.SeamAsync` → `KeyNotFoundException`（fail-fast，不静默）。
- 缝**声明了但没人挂** → 空缝：调用不报错、无副作用。
- 成员**完全没声明缝** → 运行时不注入缝执行器，`ctx.SeamAsync` 抛 `InvalidOperationException`。
- 缝内只有 `before`：缝在 handler 内部，「成员落地之后」在这里没有位置。要 `after`／`replace` 请包在成员外围。

> 三层的分工固定：`wrap.phase` 管**成员外围**、`band` 管**成员内部边次序**、`seam` 管**handler 内部插入点**。
> 三者互不替代，选错层就会出现「改了但没生效」。

---

## 7. 来源两面：`Source` 与 `TriggerCard`

| | 在哪 | 含义 | 谁写 | 可空 |
| --- | --- | --- | --- | --- |
| `Source` | 信号**载荷**里的一个键；视图按名读 | **事件引发者**（这张牌为什么死／为什么被弃） | 发射方 `Emit*(..., source:)` | 是 |
| `TriggerCard` | **执行帧**值；`ctx.TriggerCard` 与视图属性同值 | **当前 handler 的触发者**（链上最后者胜） | 框架按边归属解析 | 是 |
| `owner` | 边的归属实体 | 撤销／审计用，**不是归因** | 挂载方声明 | — |

规则：

1. **`Source` 不进帧**。它只是载荷里的一个键，handler 内再发信号时要把 `view.TriggerCard` 或 `view.Source`
   显式传给下游动作（`DamageAsync(target, amount, source, ct)`），否则下游归因为 null。
2. **`TriggerCard` 沿链覆盖**：同一条执行链上多个卡／效果各自注册的边，后者看到的 `TriggerCard` 是自己的宿主
   （「最后者胜」）。它是**帧值**，不是载荷——别指望在 `Source` 里找到它。
3. **两者可以同时非空且不同**：`card.damaged` 的 `Source` 是「谁打的」，而当前 handler 的 `TriggerCard` 可能是
   「哪张卡注册了这条边」。先问自己要归因还是要施动方。

> 归因现状：真正会写非空 `Source` 的信号是有限的（`card.played`、`unit.joined`／`unit.deployed`、
> `counter.triggered`、`card.died`、`card.discarded`）。其余信号的 `Source` 恒为 null，
> 归因时优先读载荷里已有的对象键（如 `unit.damage.dealt.Unit`）。逐信号现状见 [信号表](./reference/信号表.md)。

---

## 8. csx handler：签名与 `entry`

### 8.1 签名（唯一合法形态）

```csharp
Func<dynamic, Context, CancellationToken, Task> HandleAsync = async (view, ctx, ct) => { … };
```

- **三参**：`view`（承载视图）、`ctx`（执行上下文）、`ct`（取消令牌）；返回 `Task`。
  资产层用 `dynamic`；C# 侧用 `MemberEdges.For<TView>` 拿强类型视图。
- `entry` 必须是脚本里的**顶层变量名**，缺省 `"HandleAsync"`。
- 脚本上下文**不保证 `using`**，命名空间写全限定名（`Orc.Game.Effects.EffectRuntime.ResolveFor(...)`）。
- 视图字段在 `members[].view.fields[]` 里声明后才有；未声明就写 `view.X` → `binding` 失败（装载期，不是运行期）。

### 8.2 handler 声明 JSON

```jsonc
{
  "id": "diy.fire-support#onPlayed",   // 资产 id：全局唯一，多份资产可引用同一 id
  "entry": "HandleAsync",               // 缺省 HandleAsync
  "script": "Func<dynamic, …> …",       // 与 assembly-key 二选一
  "assembly-key": "…"                   // 已注册的程序集处理器键
}
```

`script` 与 `assembly-key` **必须给一个**，都给或都不给＝`structure` 失败。

### 8.3 视图能读到什么

| 来源 | 怎么读 | 备注 |
| --- | --- | --- |
| 目标成员的载荷字段 | `view.Unit`、`view.Amount`… | 名字＝目标信号的载荷键；只有 `writable: true` 的可写 |
| 宿主对象 | `view.Host` | 框架回填，**不需要**在 `view.fields[]` 里声明 |
| 私有字段 | `owner.<Name>` | 只在实体上，不进数据面；`view.<私有字段>`＝`binding` 失败 |
| 触发者 | `view.TriggerCard`／`ctx.TriggerCard` | 帧值 |
| 引擎变量 | `ctx.Engine!.Variables` | 对局级 `key → object` |

> `view` 的字段名空间＝**数据面（仅视图声明字段）**。私有字段只能走 `owner.`，
> 两边同名＝装载期拒绝（同一条状态两个通道会让读者误判）。

### 8.4 失败分类

| 分类 | 含义 | 典型修法 |
| --- | --- | --- |
| `binding` | 静态语法检查失败：写了未声明的视图字段、写只读字段、`owner.<未声明>` | 补 `view.fields[]`／改走 `owner` |
| `compile` | csx 编译失败（语法、类型、缺 using） | 改脚本 |
| `signature` | 入口签名不符（须三参、返回 `Task`，首参能接收承载视图） | 改签名 |
| `sandbox` | 沙箱策略拒绝（白名单程序集／禁用片段） | 去掉被禁 API |
| `structure`／`addressing`／`visibility`／`channel`／`timing`／`view` | 装载期结构／寻址／可见性／通道混用／时机／视图字段问题 | 按报告里的地址与成员名改声明 |

**csx 在装载期全量预编译**：编译失败＝拒绝装载（不是「隔离记录后继续」）。所以「能装载」⇒「该能跑的都能跑」。

---

## 9. 异常与停止

| 情形 | 行为 |
| --- | --- |
| handler 抛异常 | **上抛**（模型层不捕获、不回滚已发生的副作用） |
| `ctx.Stop()` | 只停**本成员**剩余单元（后续外来边、`after`、框架边）；不外溢到调用方 |
| `ctx.Interrupt` | 链级中断：整条链不再推进 |
| 合法性检验拒绝 | `MemberExecutionResult.Rejected(reason)`：本次不执行，不抛 |
| one-shot 已消费 | `Executed=false, ValidationRejected=true`，原因注明「一次性额度已消费」 |
| 动作面返回 `false`／`0` | 无异常，链照常；调用方自己判返回值 |

`MemberExecutionResult` 是诊断读面：`Executed`（是否进入执行）、`Stopped`（是否被 `Stop`）、
`ValidationRejected`（是否被合法性检验拒绝）、`Reason`（拒绝原因）。

> 与旧模型的差别：旧模型「单个 handler 异常被隔离、记录后 continue」，新模型**不隔离**。
> 需要「出错也要继续」的场景，在 handler 内自己 `try/catch`。

---

## 10. 审计面

| 想查 | 读什么 |
| --- | --- |
| 某成员上有哪些边、按什么序 | `entity.FindEdges(memberName)?.Snapshot()`（快照序＝执行序依据） |
| 某成员的声明（视图／段名／缝） | `entity.FindMember(name)` |
| 装配是否全部成功 | `MountPlan.Report`（`PrefabLoadReport`：分类 ＋ 地址 ＋ 成员名） |
| 全图（实体／成员／边／包装） | `AuditGraph`：`Entities`／`Members`／`Edges`／`Wraps`，查询 `EdgesInto`／`EdgesOf`／`MembersOf`／`WrapsOn`／`EdgesBySource`／`OneShotComponents` |
| 包装挂在哪个目标 | `runtime.Wraps.Entries(target, phase)`／`BeforeExecutionOrder(target)` |

`MountEdge` 上带 `Source`（`explicit`／`injected`／`framework:disposal`／`framework:one-shot`）——
这是「这条边是谁加的」，与视图里的 `Source`（事件引发者）是两回事，注意别混。

---

## 11. 本章自检

- [ ] 能说出「信号」与「成员」的关系（信号＝`SignalTiming` 非 null 的成员）
- [ ] 能写出一个 `mounts` 声明，并说出它为什么需要 `mountable`／`hostable`
- [ ] 能解释 `self#` 为什么不受可见性约束
- [ ] 能算出「`band=Commit` 的自有边」与「`band=Initiate` 的外来边」谁先执行（答：外来边，段下标更小）
- [ ] 能说出数据侧为什么**改不了**外来边的段（资产 schema 无 `band` 字段）
- [ ] 能说出缝为什么只有 `before`，以及缝内多个包装器的执行方向（LIFO）
- [ ] 能区分 `Source`（载荷、事件引发者）与 `TriggerCard`（帧值、当前触发者）
- [ ] 能说出 csx handler 的签名（三参）与 `entry` 的约束
- [ ] 能说出 handler 抛异常后的行为（上抛，不隔离）
