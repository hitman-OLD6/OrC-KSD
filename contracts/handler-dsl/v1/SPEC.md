# handler DSL 规范 v1

> 本文件是 **handler DSL** 的权威规范（R21 的「语义规范 SPEC」）。
> 术语见 `.grill/context/CONTEXT-效果契约.md`：**handler DSL** 与既有的
> `Orc.Game.EffectParsing.Dsl`（**卡面文本效果 DSL**）是两个东西，禁止混称；代码标识符统一 `HandlerDsl`。
>
> 本包 `contracts/handler-dsl/v1/` 位于引擎仓库顶层、与 `src/` 平级，**不属于任何 csproj 的编译范围**。
> 前后端各持**同版本副本**；程序集（`Orc.HandlerDsl`）是派生产物，正确性由 `vectors/` 守护（D2）。

```text
contracts/handler-dsl/v1/
├── SPEC.md               本文件：语义规范（含「生成格式（冻结）」）
├── schema.json           DSL 文档的 JSON Schema（按 §3 字段封闭表生成）
├── catalog.schema.json   节点目录**元模型**的 JSON Schema（§12）
├── vectors/              golden vectors（§15）
│   ├── README.md
│   ├── check/            校验类：期望「一个错误码 ＋ 一个节点路径」
│   └── gen/              生成类：期望 csx 文本（*.expected.csx）
└── EXCLUSIONS.md         排除清单（方法名 ＋ 理由 ＋ 替代路径）
```

---

## §0 描述对象与边界

1. 一份 DSL 文档描述**一段 handler 逻辑 ＋ 它需要的输入**。
2. 文档**不**携带：资产 id、成员名、挂载地址（`engine#`／`host#`／`self#`）、触发时机、相位、
   `mounts`／`wraps` 声明。归属由预制体／moding 草案的成员声明通过引用持有（R13）。
3. 文档只声明「我要读哪些视图字段名」；字段类型由后端按挂载点目标成员的视图声明**推导**（R13）。
4. 文档是**生成输入**，不是回存数据：未知节点名／未知字段一律拒绝，**不做无损保留**（R21；
   与资产 sidecar 的「保留未知字段」策略不冲突，两者对象不同）。
5. 文档**只活在草案与编辑器**：摊平产物里存的是生成后的 csx 文本，不含任何 DSL 节点结构（R28）。

---

## §1 版本轴（两条，必须切开）

| 轴 | 类型 | 语义 | 不匹配时 |
| --- | --- | --- | --- |
| `schemaVersion` | 整数（本版 `1`） | **「已存 DSL 能否照原样装载」的保证** | 装载期拒绝，错误含双方版本（`schema-version`） |
| `catalogRevision` | 整数（由目录给出） | 目录**实例**（具体节点与引脚）的内容版本 | **不拒绝**，只作诊断（见 §12.7） |

`schemaVersion` 覆盖：DSL 语法（节点／字段结构）＋ 校验规则（§8–§11、§14 的可判条款）＋
引脚映射规则（§12.1–§12.3）＋ 目录**元模型**（§12）＋ **生成格式**（§13）。

**目录变更分两类**（D12）：

| 变更 | 性质 | 义务 |
| --- | --- | --- |
| 新增节点、新增**可选**引脚、放宽取值集（含放宽 `read.of` 封闭集） | 增量 | 只递增 `catalogRevision`，旧 DSL 照常装载 |
| 删除节点、删除引脚、改名、引脚由可选变必填、收窄取值集 | **破坏** | **同时**升 `schemaVersion` |

**`schemaVersion` 的另一半义务**：新增错误码（对已冻结的 v1）、改动 §13 任一格式项，
同样必须升 `schemaVersion`。`schemaVersion` 的语义是「旧文档还能不能照原样装载」，**不是**「规范文本变了没有」。

---

## §2 文档形态：树

```json
{
  "schemaVersion": 1,
  "catalogRevision": 42,
  "actions": [ /* 节点数组，顺序即执行顺序 */ ]
}
```

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `schemaVersion` | 是 | 整数；必须等于本包版本 |
| `catalogRevision` | 否 | 整数；只作诊断（§12.7），**永不**作为拒绝理由 |
| `actions` | 是 | 节点数组（可空，空＝空 handler） |

1. **数组顺序即执行顺序**；执行顺序完全由「数组顺序 ＋ 嵌套」决定（R18）。
2. **不存在** exec 引脚、`next` 字段、节点间跳转边、环。
3. **节点无标识**：DSL 不提供 id／`ref`／任何跨位置引用语法。错误定位恒用**节点路径**（§14.2）。
4. 树里不存在「同一节点」：每个位置就是一个独立实例。
5. **嵌套深度上限 64**（与既有预制体 chain 深度口径一致）。根 `actions` 里的节点深度为 1，
   其子节点深度 +1；`depth > 64` ＝拒绝（`graph-shape`）。
6. 「不是树」的判定只剩三种：**不是对象**、**子节点不是数组**、**深度超限**。
7. 出度 > 1 的画布节点如何折叠成树是**前端职责**：纯表达式（子树内无 `await`、无副作用）可复制成独立副本；
   含 `await` 或副作用的节点必须唯一消费者，否则折叠拒绝并提示改用 `let` 显式共享。
   **后端不实现「图 → 树」规范化**：输入不是树＝拒绝并指明位置。

---

## §3 节点字段封闭表（v1 权威）

`op` 取值域 ＝ **10 个内建 op** ∪ **目录里的动作节点名**。内建 op 名与目录节点名冲突＝**扫描期断言失败**（S4）。
内建节点**字段集封闭**：出现表外字段＝`pin-unknown`。

```jsonc
// ── 内建控制流（6）────────────────────────────────────────────────────────
{ "op": "sequence", "actions": [ … ] }
{ "op": "branch",   "test": <boolExpr>, "then": [ … ], "else": [ … ]? }
{ "op": "loop",     "times": <numExpr>, "actions": [ … ] }
{ "op": "while",    "test": <boolExpr>, "limit": <numExpr>, "actions": [ … ] }
{ "op": "iterate",  "over": <listExpr>, "as": "<绑定名>", "actions": [ … ] }
{ "op": "choice",   "prompt": "<文案>", "options": [ { "label": "<文案>", "actions": [ … ] } ] }

// ── 内建核心（4）──────────────────────────────────────────────────────────
{ "op": "let",    "name": "<绑定名>", "value": <expr> }
{ "op": "write",  "field": "<视图字段名>", "value": <expr> }
{ "op": "ask",    "kind": "unit"|"hq"|"card", "side"?, "zone"?, "filter"?, "prompt": "<文案>",
                  "bind"?: "x", "fallback": "empty"|"first" }
{ "op": "script", "inputs":  [ { "name": "<名>", "type": "<引脚类型>" } ],
                  "outputs": [ { "name": "<名>", "type": "<引脚类型>" } ],
                  "body": "<csx 片段>" }

// ── 动作节点（目录生成）──────────────────────────────────────────────────
{ "op": "<目录节点名>", "<引脚名>": <expr>, …, "bind"?: "x" | { "<输出名>": "<绑定名>" } }
```

字段与必填性（`?` ＝可省略；其余必填）：

| 节点 | 字段集 | 校验要点 |
| --- | --- | --- |
| `sequence` | `op`, `actions` | `actions` 必填；可为空数组 |
| `branch` | `op`, `test`, `then`, `else?` | `test` 必须推导为 `boolean`；`then` 必填且**非空** |
| `loop` | `op`, `times`, `actions` | `times` 必须推导为 `number`，缺／类型不符＝`loop-unbounded` |
| `while` | `op`, `test`, `limit`, `actions` | `test`→`boolean`；`limit`→`number`，缺／类型不符＝`loop-unbounded` |
| `iterate` | `op`, `over`, `as`, `actions` | `over` 必须是 `list<T>`，否则 `pin-type`；`as` 为合法绑定名 |
| `choice` | `op`, `prompt`, `options` | `prompt` 为字符串字面量；`options` 非空，每项含 `label`＋`actions` |
| `let` | `op`, `name`, `value` | `name` 为合法绑定名（§9） |
| `write` | `op`, `field`, `value` | `field` 必须是**字符串字面量**；可写性见 §11.3 |
| `ask` | `op`, `kind`, `prompt`, `side?`, `zone?`, `filter?`, `bind?`, `fallback?` | `kind`∈{`unit`,`hq`,`card`}；`fallback` 缺省 `empty` |
| `script` | `op`, `inputs`, `outputs`, `body` | 引脚 `type` 取 §7 封闭集；`body` 为字符串；**v1 不与图交换值**（见下） |
| 动作节点 | `op`, 目录声明的输入引脚名, `bind?` | 表外字段＝`pin-unknown`；必填引脚缺失＝`pin-missing` |

**`script` 在 v1 是**纯逃生舱**：正文内联进生成的 csx，但 `inputs`／`outputs` **只是声明**
（供前端画引脚、供未来版本接线），**没有「引脚值」通道**——正文里不能引用未绑定的名字，
只能用 `view`／`orcSelf`／`ctx`／`ct` 与**前面节点已建立**的绑定。需要与图交换值时，用 `let`／`bind`／
动作节点，不要把值塞进脚本再取出（v1 没有取回机制，`outputs` 不会成为图的绑定）。

**子数组命名规则（唯一）**：

- 通用容器（`sequence`／`loop`／`while`／`iterate`）的序列体／循环体 → **`actions`**；
- 分支两臂 → **`then`／`else`**（不叫 `actions`，否则同节点两字段无法区分）；
- 抉择选项 → **`options[].actions`**。

**`else` 缺省形态＝省略字段**：`"else": []` 与「没有 else」语义相同却产生两种文本，破坏文本确定性（§13），
故 `"else": []` ＝**拒绝**（`graph-shape`），不是警告。

**字段名一处刻意不统一**：`let{name}` 与 `iterate{as}` 保留不同字段名（「它叫什么」vs「作为什么」），
但**作用域规则完全等同**（§9）。

**`choice` 与 `branch` 的判据不能混**：`branch` 是静态判定（`test` 求值、纯数据流）；
`choice` 是玩家交互（弹选项、选完执行对应臂）。`choice` 与 `ask` 同属**交互节点**，同受 `ask-in-loop` 约束；
所有 `options[].actions` 处在**同一作用域层级**（互斥、不叠加）。

---

## §4 引脚取值子语言（五种变体，封闭）

**统一同一套变体**，不在「引脚值」与「条件」之间再造第二套：

```json
{ "lit": 2 }                                                  // 字面量（number / string / boolean）
{ "var": "drawn" }                                            // 绑定引用（§9 作用域规则）
{ "view": "Defense" }                                          // 当前执行帧的视图字段
{ "read": { "of": "self", "path": "attack" } }                 // 卡属性／玩家资源读取
{ "op": "sub", "args": [ { "view": "Defense" }, { "lit": 1 } ] } // 算子应用（可嵌套）
```

1. 表达式对象**恰有一个**上述键；出现第六种形态、或多于一个变体键＝`pin-unknown`。
2. `lit` 的 JSON 类型决定类型：number → `number`、string → `string`、boolean → `boolean`；其它 JSON 类型（null／数组／对象）＝`pin-unknown`。
3. `var` 的绑定名必须在作用域内可见（§9），否则 `bind-scope`。
4. `view` 的字段名必须在挂载点目标成员的视图声明里，否则 `pin-unknown`。
5. `read.of` 取值集（v1 封闭）：`self`／`viewer`；`read.path` 取值集由目录声明的**可读项**给出，未知＝`pin-unknown`。
6. `op` 见 §5；`args` 必须是非空数组。

---

## §5 算子表（v1 封闭）

| 算子 | 元数 | 入参类型 | 结果 | 说明 |
| --- | --- | --- | --- | --- |
| `add` | 2 | `number`,`number` | `number` | |
| `sub` | 2 | `number`,`number` | `number` | |
| `mul` | 2 | `number`,`number` | `number` | |
| `eq` | 2 | 同类型 T ∈ {`entity`,`number`,`string`,`boolean`} | `boolean` | 两侧类型必须**完全相同** |
| `ne` | 2 | 同上 | `boolean` | |
| `lt` | 2 | `number`,`number` | `boolean` | 有序比较只对 `number` |
| `le` | 2 | `number`,`number` | `boolean` | |
| `gt` | 2 | `number`,`number` | `boolean` | |
| `ge` | 2 | `number`,`number` | `boolean` | |
| `and` | 2 | `boolean`,`boolean` | `boolean` | 不短路以外的语义差异；生成保持书写顺序 |
| `or` | 2 | `boolean`,`boolean` | `boolean` | 同上 |
| `not` | 1 | `boolean` | `boolean` | |
| `count` | 1 | `list<T>`（T 任意） | `number` | 空集 → `0` |
| `sum` | 1 | `list<number>` | `number` | 空集 → `0` |
| `max` | 1 | `list<number>` | `number` | 空集 → `0` |
| `min` | 1 | `list<number>` | `number` | 空集 → `0` |
| `empty` | 1 | `list<T>` | `boolean` | 判空；`ask` 返回 0..n，靠它判空 |

1. **v1 不提供除法**（也不提供取模／幂）：`div`／`mod`／`pow` 等一律 `expr-op-unknown`。
2. 算子名不在上表内＝`expr-op-unknown`；`args` 个数不符＝`expr-arity`；
   入参类型不符、或聚合入参不是集合＝`expr-type`。
3. 集合的聚合只接受 `list<T>`：入参是标量＝`expr-type`（消息说明「聚合输入必须是集合」）。
4. `sum`／`max`／`min` 只接受 `list<number>`；`list<entity>` 的数值聚合没有定义＝`expr-type`
   （实体集合的大小用 `count`，取值用 `iterate` ＋ 元素引脚）。

---

## §6 引脚类型封闭集

1. 引脚类型**只取**：`entity`／`number`／`string`／`boolean`／`list<T>`，其中 T ∈ {`entity`,`number`,`string`,`boolean`}。
   文本形态固定为上述五种的写法：`entity`、`number`、`string`、`boolean`、`list<entity>`、`list<number>`、
   `list<string>`、`list<boolean>`。
2. **不提供**结构体、字典、可空类型、任意 CLR 类型、`list<list<…>>`。出现上述五类之外的引脚类型名
   （目录侧或 DSL 侧）＝拒绝。
3. 枚举用 `string` ＋ 目录声明该引脚的**允许取值集**（`enum`）表达。
4. `entity` **不设静态子类型**：节点目录可为实体引脚声明「运行期接受的子类型集」（`unit`／`hq`／`card`），
   仅用于连线提示与 §8.3 的静态判定；图校验期只对**基类型**做硬拒绝。
5. `Player`／`Position`／`Deck` **不作为一等引脚类型**出现。
6. `ViewFieldType`（`Orc.Core.Model.ViewSpec`）的八个名字与本节同构：`entity[]` ↔ `list<entity>`（其余同理）。
   视图字段类型 → 引脚类型时按此等价关系归一；对外文本仍用 §6.1 的写法。

---

## §7 表达式类型推导

1. 每个表达式节点有**确定类型**；类型**只由该节点 kind ＋ 入参类型决定、与上下文无关**——
   不做双向推断、不做隐式转换。**同一表达式搬到另一个引脚，推导结果不变**。
2. `number` 与 `string` 之间**没有任何自动转换**；`number` 表达式接 `string` 引脚＝`pin-type`。
3. 推导是**自底向上**的：先算子表达式，再按 §5 表算本节点。
4. `pin-type` **不分裂**：判定仍在引脚层——先把整棵表达式推导出一个类型，再与引脚期望类型比对；
   表达式内部的错用走 `expr-*`（§14.1）。
5. **实体子类型的静态确定下移到表达式层**：
   - `view` 字段的实体类型来自视图声明（`entity` → 子类型未知）；
   - `iterate` 的 `as` 绑定类型＝`over` 的元素类型；
   - `ask` 的结果恒为 `list<entity>`，其静态子类型＝`ask.kind`（`unit`／`hq`／`card`）。
   上游可**静态确定**为 `unit` 而目标引脚只收 `hq`＝拒绝；**不可静态确定时放行**，
   运行期沿用动作面原有的 `false`／空集语义。
6. 类型比对规则（`IsAssignableTo`）：
   - 基类型必须**完全相同**（`list<T>` 比到元素）；不同＝`pin-type`；
   - `entity` 两侧都有子类型集且**交集为空**＝`pin-type`；任一侧未知＝放行。

---

## §8 绑定与作用域

1. **单赋值**：绑定不可重新赋值；要新值就新名字。DSL 里**不存在** `set`／重新赋值原语；出现＝`node-unknown`。
2. **词法作用域**：绑定只在**声明它的那个 `actions[]` 数组内、从声明点之后的兄弟节点及其子树**可见；
   离开该数组即失效。
3. **禁止遮蔽**：同一可见链上同名一律拒绝（嵌套作用域遮蔽也算重名）＝`bind-scope`。
4. 循环体里的绑定在**每次迭代重新求值**，不视为重复赋值；循环体内声明、循环体引用＝正常；
   循环外引用循环内绑定＝`bind-scope`。
5. 分支内声明、分支外引用＝`bind-scope`（错误含绑定名与声明位置）。
6. **`let` 是跨多处引用同一中间值的唯一正路**：`let{name, value}` 求值一次、后续按名引用。
   它不是「变量声明的糖」，是共享中间结果的机制。
7. 绑定名规则：`[A-Za-z_][A-Za-z0-9_]*`，且不得与视图字段名同名（否则 `bind-scope`）；
   非字面量／非法字符＝`bind-scope`。
   **另：不得以双下划线 `__` 开头**——那是生成器的保留前缀（`__runtime`／`__bound_*`／`__res_*`／
   `__i*`／`__choice*`，见 §13 F13）。作者用同名绑定会在生成的 csx 里造成重复声明，
   而那是编译错误、报出来读者对不上图；故在图校验期即以 `bind-scope` 拒绝。
8. **绑定来源**只有三处：`let{name}`、动作节点的 `bind`、`iterate{as}`。

### §8.9 `bind` 的双形态（不许可等价写法）

| 动作节点的输出数 | `bind` 形态 | 例 |
| --- | --- | --- |
| 单输出 | **字符串**（绑定名） | `"bind": "drawn"` |
| 多输出 | **对象**（键集必须**恰好等于**目录声明的输出名集合） | `"bind": { "status": "st", "card": "c" }` |

1. **不写 `bind` ＝忽略返回值**，生成码不产生未使用变量。
2. 单输出写成对象（`{"ok":"x"}`）＝`bind-scope`（同一语义两种文本，破坏确定性）。
3. 多输出对象缺键／多键／键不在输出名集内＝`bind-scope`。

---

## §9 控制流语义

### §9.1 三种循环（封闭）

```jsonc
{ "op": "loop",    "times": <numExpr>, "actions": [ … ] }             // 固定次数
{ "op": "iterate", "over": <listExpr>, "as": "x", "actions": [ … ] }  // 遍历
{ "op": "while",   "test": <boolExpr>, "limit": <numExpr>, "actions": [ … ] } // 条件循环
```

1. `times`／`limit` **必填**，可以是**任意 `number` 表达式**（含 `{var:…}`／`{count:…}` 这类运行期值），
   **不要求编译期常量**。缺／类型不是 `number`＝`loop-unbounded`（不落默认值兜底）。
2. **有界保证＝上限表达式只求值一次**：循环开始时快照成一个数，循环体内不重估。
3. `while.test` **每轮重估**；`limit` 只在循环开始时求值一次。
4. 上限为**负数**＝循环体零次执行，**不报错**。
5. 生成的 csx 恒为 `for` 循环，**不出现裸 `while(true)`**（即使上限是运行期值）。
6. **不做常量折叠**：任意表达式都合法，没有「可算 vs 不可算」的边界要划。
7. 聚合不通过重赋值表达：需要聚合时用聚合算子（`count`／`sum`／`max`／`min`）或引擎变量，或转脚本节点。

### §9.2 早退只有两种

| 节点 | 语义 |
| --- | --- |
| `stop` | ＝`ctx.Stop()`，停本成员剩余边 |
| `interrupt` | ＝`ctx.Interrupt`，链级中断 |

`stop`／`interrupt` 是**目录节点**（由动作面扫描得到），不是内建 op；但它们**不接受 `bind`**。
**不提供** `break`／`continue`／`return`、递归、用户自定义函数；出现即未知 op（`node-unknown`）。

---

## §10 目标语义与交互节点

### §10.1 显式 `iterate`，动作只吃单个实体

1. 集合是**一等值** `list<entity>`；`iterate{over, as, actions}` 负责遍历。
2. 动作节点的实体引脚**只接受单个 `entity`**；接 `list<entity>` ＝`pin-type`，提示改用 `iterate`。
3. **不存在**「动作节点自带查询字段、后端隐式 `foreach`」的形态；双目标动作（如 `battleUnits`）
   两个引脚都只吃单 `entity`，图上必须显式两层 `iterate`（不产生隐式笛卡尔积）。
4. 生成的 csx 里 `foreach` **只由 `iterate` 节点产生**；动作节点自身不产生循环。
5. `iterate.over` 为空集＝循环体不执行、不报错。

### §10.2 `ask` 与纯查询分离

1. `ask` 发起玩家交互，返回 `list<entity>`（0..n），**恒为该类型，不提供「保证非空」变体**；作者自行判空。
2. 三类目标获取彻底分开：**纯查询**（`all`／`random`／`first`… 无交互）、**`ask`**（交互）、**`iterate`**（遍历）。
   表达式里不藏交互副作用。
3. `ask` 在 `iterate` 循环体内＝`ask-in-loop`（`choice` 同受此限）。
4. **`fallback` 只管「无交互通道」这一种情况**，取值 `empty`（缺省）或 `first`。**不许静默取第一个**：
   `first` 必须由作者显式选择，并在诊断面留痕（可查「本次走的是降级」）。
5. **有通道、玩家主动取消选靶 → 返回空集，不走 `fallback`**。取消是玩家的意图表达，不是环境缺失；
   无通道才是环境缺失，作者才有权预先指定替代策略。**这两条是两句话，不得合成「拿不到目标时」。**
6. 纯查询节点在无前端环境下的行为与有前端一致（不依赖交互通道）。

---

## §11 视图字段

1. DSL 只声明「我要读哪些字段名」；字段**类型**由挂载点目标成员的视图声明推导（§0.3）。
2. 引用了目标成员**未声明**的视图字段＝装载期拒绝并指名该字段（`pin-unknown`）。
3. `write{field, value}` 写**当前执行帧**：目标字段必须在视图声明里且 `writable: true`，
   否则 `write-forbidden`（错误含成员地址与字段名）。字段名必须是**编译期字面量**，不接受运行期按名写。
4. `write` 与 `changeAttribute` 严格区分：前者改**执行帧**（当次生效，不改卡持久属性、不发 `card.stat.changed`），
   后者改**卡属性**并走修饰链。二者生成的码语义互不干扰。
5. `write` 生成的 csx 是给 `view.<Field>` 赋值，**不经过 `EffectRuntime`**。

---

## §12 节点目录元模型

目录＝**可调用原语的清单**＋每个原语的引脚定义。目录本身是**纯数据**（JSON 可序列化，无运行期对象引用）。

### §12.1 默认映射规则（方法签名 → 节点引脚）

| 签名要素 | 映射 |
| --- | --- |
| `CancellationToken` 参数 | **恒不出引脚** |
| `Task<T>` 返回 | 展开为输出引脚（`T` 按 §6 映射）；`Task`（无泛型）＝无输出 |
| 参数类型 | 按 §6 封闭集映射；不在封闭集内＝**目录生成失败**（标注拆解 or 进 `EXCLUSIONS.md`） |
| 参数默认值 | → 引脚**可省略**（非必填） |
| 结果对象 | **必须显式标注**多输出拆解；未标注＝目录生成失败（不静默退化成一个 `string` 引脚） |
| 参数名首参为 `host`／`viewer`／`self` | **隐式宿主**：不出引脚，生成码固定用 `orcSelf`（白名单封闭，不再加） |
| `source` 参数 | 可选输入引脚；编辑器默认折叠，缺省接宿主；展开连线时接所连实体 |

### §12.2 节点名与重载合并

1. **默认派生**：去 `Async` 后缀、首字母小写（`DamageAsync` → `damage`）。
2. 冲突或改名时**必须有显式标注** `[HandlerDslNode("death")]`；**显式名优先于派生**。
3. **同名（去 `Async` 后）方法合并为一个节点**，参数取**并集**；只在部分重载里出现的参数→该引脚**可选**。
4. 同名参数在不同重载里**类型不同**＝目录生成失败，必须用标注区分成两个节点（即「冲突」）。
5. **引脚名同样受保护**：参数改名必须保留标注里的旧引脚名，否则就是**删除引脚**（破坏性，须升 `schemaVersion`）。

### §12.3 折叠标记（供前端做 §2.7 的出度折叠）

目录为每个节点带两个**单节点**标记：

- `hasAwait` ＝ 这个动作本身会挂起（方法返回 `Task`，生成码需要 `await`）；
- `hasSideEffect` ＝ 这个动作本身改变状态。

**它们不描述子树**。目录是**节点模板**，同一个 `damage` 挂在甲位置和乙位置时可以带完全不同的子树，
「某个节点的子树」在目录这一层没有意义。所以递归聚合**由前端在具体树上做**：

```text
awaitOf(x)      = 自身标记(x) ∨ 任一子节点 awaitOf ∨ 任一值表达式含 count／read
sideEffectOf(x) = 自身标记(x) ∨ 任一子节点 sideEffectOf
```

内建 op 的自身标记（不在目录里，固定）：

| 自身标记 | op |
| --- | --- |
| await ✔ / sideEffect ✔ | `ask`、`choice`、`script` |
| await ✘ / sideEffect ✔ | `write`、`stop`、`interrupt` |
| await ✘ / sideEffect ✘ | `let`、`sequence`、`branch`、`loop`、`while`、`iterate` |

`script` 保守视为两者皆有：正文是任意 csx，无法静态判定。
合成节点 `stop`／`interrupt` 的标记同样由目录给出（`hasAwait=false`／`hasSideEffect=true`），按常规读即可。

### §12.4 标注语言（**只定义有哪些标注**）

命名空间固定 `Orc.Game.Effects.HandlerDsl`，标注**只标在 `Orc.Game` 的动作面方法／参数／类型上**。
本包只定义「标注这门语言」；**「谁标了什么」只存在程序集里**（本包不含按方法名索引的实例表）。

| 标注 | 目标 | 字段 | 语义 |
| --- | --- | --- | --- |
| `HandlerDslActionFace` | 类型 | — | 登记为动作面：其**全部 public 实例方法**进入扫描 |
| `HandlerDslNode` | 方法 | `name` | 显式节点名（冲突／改名时必填，优先于派生） |
| `HandlerDslPin` | 参数 | `name`，`type?`，`required?` | 显式引脚名／类型覆盖／必填性覆盖 |
| `HandlerDslResult` | 方法 | `outputs: {name,type,kind}[]` | 结果对象的多输出拆解（`kind` ∈ 标量／枚举前缀） |
| `HandlerDslEnumValues` | 参数／输出 | `values: string[]` | `string` 引脚的允许取值集 |
| `HandlerDslSubtype` | 参数／输出 | `types: string[]`（∈ `unit`／`hq`／`card`） | 实体引脚的运行期接受子类型集 |

标注字段的取值类型**按 §6 封闭集**，不为标注另造一套 DSL。

### §12.5 扫描输入集

扫描输入集＝**带 `[HandlerDslActionFace]` 的类型**的全部 public 实例方法。
**不采用鸭子类型判据**（`Task` 返回 ＋ 首参 `Card` 会把内部步骤方法、测试辅助、事件处理器一并扫进来）。
新增门面只加特性：扫描器不动、本包不动、前端不动。

### §12.6 `catalogRevision`

目录实例携带 `catalogRevision`；可单查询 revision（轻查询），前端据此决定要不要重拉全量目录。
**目录只能经运行期查询获得**；仓库内落盘的目录文件仅作测试夹具（vectors 快照），不被运行期读取。

### §12.7 DSL 里的 `catalogRevision`（诊断，不是门）

1. **不写＝合法**：按当前目录解析；节点缺失＝`node-unknown` 拒绝（正确行为，不静默）。
2. 写了且与当前不一致＝**仍按当前目录解析**；若解析因此失败，错误信息额外带
   「本文档针对 catalogRevision=X，当前 Y」。
3. **绝不因 revision 不一致而预先拒绝**——那等于把内容版本偷变成硬门。

### §12.8 可读项（`read.path`）

可读项由目录声明（`readables`），v1 初版：

| path | 类型 | 含义 |
| --- | --- | --- |
| `attack` | `number` | 攻击力（实时值，含修饰链） |
| `defense` | `number` | 防御力（实时值，含修饰链） |
| `opCost` | `number` | 行动费 |
| `deployCost` | `number` | 部署费 |
| `hqHealth` | `number` | HQ 血量 |
| `kredits` | `number` | 玩家当前点数 |
| `maxKredits` | `number` | 玩家点数上限 |
| `name` | `string` | 实体名 |
| `zone` | `string` | 所在区域标识 |

未知 `path` ＝`pin-unknown`（复用码，不新增）。

### §12.9 合成节点（`stop`／`interrupt`）

`stop`／`interrupt` 是**内建 op**（§9.2），但**没有对应的动作面方法可扫描**——它们由 `ctx` 提供。
因此目录在构造时**注入**这两个节点：

| 项 | 约定 |
| --- | --- |
| 来源 | **不属于任何** `[HandlerDslActionFace]` 门面；目录快照里 `source.faceType` 记为空、`source` 整体省略 |
| 引脚 | 0 个输入、0 个输出（**不接受 `bind`**） |
| 折叠标记 | `hasAwait: false`、`hasSideEffect: true` |
| 保留名 | 任何门面方法派生出同名节点＝**扫描期失败**（不允许用动作面方法顶替上下文原语） |
| 快照校验 | 扫描器与快照校验器**不得**因「找不到来源方法」而报错——它们是合法的合成节点，不是孤儿 |

`DslCatalog` 的实现在**构造时**注入这两个节点；从目录快照反序列化时**跳过**它们的 `nodes` 条目
（否则同一节点被注入两次）。生成码固定为 `ctx.Stop()`／`ctx.Interrupt()`，不经 `EffectRuntime`。

---

## §13 生成格式（冻结）

**v1 发布后本节不可变**。格式一旦可「优化」，每次优化都会让全部已发布 DSL 重新摊平产生全新 handler id
——行为没变、身份全变、diff 全红。**只优化语义、不优化排版**。改本节任一项＝**破坏性变更**（须升 `schemaVersion`）。

| # | 规则 |
| --- | --- |
| F1 | 缩进 **4 空格**，不用 tab；缩进层级随嵌套 +1 级 |
| F2 | 行尾 **LF**（`\n`），不产生 CRLF |
| F3 | 文件末尾**恰好一个换行**（不多不少，也不产生空行占位） |
| F4 | 数字一律 **`InvariantCulture`**：整数字面量**不带小数点**、**不带正号**；浮点用 `InvariantCulture` 的最短往返表示 |
| F5 | 字符串用**常规转义**（`\"`、`\\`、`\n`、`\t`、`\r`），**不输出 Unicode 转义**（`\uXXXX`） |
| F6 | 变量名由**结构位置编码**决定，**不用自增序号**：编码＝节点路径把 `[n]` 写成 `_n`、`.` 写成 `_`，前缀 `__`；例：`actions[2].then[0]` → `__actions_2_then_0`，循环上限临时变量 `__bound_<编码>`。作者声明的绑定名（`let`／`bind`／`as`）沿用原名 |
| F7 | 空块**不生成空行占位**；无 `bind` 不产生未使用变量 |
| F8 | **生成文本不含节点名**（注释与变量名皆无）；**同结构、仅节点名不同 ⇒ 文本逐字节相同** |
| F9 | 不生成任何含时间戳／随机值／哈希的注释 |
| F10 | 求值顺序唯一确定：节点按数组顺序；表达式内**从左到右**逐个 `await` 顺序执行、不并发、不为省 `await` 重排 |
| F11 | **只有两处**允许临时变量：①**循环上限**（`__bound_*`）——上限**只求值一次**，必须具名才能跨轮复用（字面量直接内联，不落变量）；②**多输出 `bind` 拆解**（`__res_*`）——结果对象要先**持有**再逐个拆解，无法内联。除此之外生成器不引入临时变量 |
| F12 | 文本比对**逐字节全等，不做任何规范化**（两份只在行尾或空白上不同的文本＝两个 handler id、两份资产） |
| F13 | 门面别名固定为 **`orcSelf`**（隐式宿主）与 **`__runtime`**（`EffectRuntime` 解析结果，整段只解析一次）；两者是**固定名**，不参与 F6 的位置编码 |
| F14 | 动作调用的实参**必须**含：①**隐式宿主**（`host`／`viewer`／`self` 形参，值＝`orcSelf`）；②**`CancellationToken`**（形参名取自目录的 `tokenParameter`，值＝`ct`）；③`source` 未连线时＝`orcSelf`。三者都**不出引脚**，但漏传会让生成物缺参或永久失去取消能力 |

**两类临时变量的命名同样遵循结构位置编码**（F6），**不得**用自增序号：`__bound__actions_2`、`__res__actions_0`
——自增序号会随「排版微调」漂移，而结构位置编码是内容寻址的一部分（D9）。

**为什么 F13／F14 也要冻结**：它们不改变「文本长什么样」的美学，而是**可执行性**。
`__runtime` 若每次动作各调一次 `ResolveFor`、或隐式宿主/`ct` 漏传，生成的文本看起来完全正常、
向量比对也全绿，但编译或运行必炸（历史上三者都真的漏过）。`vectors/gen/*.expected.csx` 必须能被真编译，
这条由「契约包期望文本编译」用例守护。

具体语句形态由生成器实现并以 `vectors/gen/` 锁住；本节冻结的是与「文本作为身份」相关的格式契约。

---

## §14 错误码封闭集与错误对象

### §14.1 封闭码集（v1，13 个）

| 码 | 判定 | 类别 |
| --- | --- | --- |
| `graph-shape` | 不是对象／子节点不是数组／深度超限／`else: []`／`then` 缺失或空 | `structure` |
| `node-unknown` | `op` 缺失、非字符串、或不在「10 内建 ∪ 目录节点名」 | `structure` |
| `pin-unknown` | 内建节点表外字段／动作节点未声明引脚／`view` 字段未声明／`read.path` 不在可读项／`read.of` 越界／表达式不是五种变体之一／引脚类型名越界 | `structure` |
| `pin-missing` | 必填字段／必填引脚缺失 | `structure` |
| `pin-type` | 引脚类型不符（含 `entity` 接 `number`、实体引脚接 `list<entity>`、静态可确定子类型不匹配） | `structure` |
| `bind-scope` | 绑定形态错误（单输出写对象）、键集不符、绑定名非法／重名／遮蔽／跨作用域、未定义 `var` | `structure` |
| `write-forbidden` | `write` 目标未声明或 `writable: false` | **`view`** |
| `loop-unbounded` | 循环缺上限、或上限类型不是 `number` | `structure` |
| `ask-in-loop` | `ask`／`choice` 出现在 `iterate` 循环体内 | `structure` |
| `schema-version` | `schemaVersion` 不匹配 | `structure` |
| `expr-op-unknown` | 算子名不在 §5 | `structure` |
| `expr-arity` | 算子入参个数不符 | `structure` |
| `expr-type` | 算子入参类型不符／聚合输入非集合 | `structure` |

1. **集合外的码＝契约违规**（vector 失败）。
2. 每个码至少一条**拒绝 vector**。
3. 同一份非法 DSL，前端与后端报出的**码与节点路径一致**——这是**测试期保证**（同一份 `check` vector 两端各跑一遍），
   **不是运行期协商**：不存在「前端把校验结果提交后端复核」的通道。版本不同步导致的「前端能存、后端装不了」
   是版本问题、由 `schemaVersion` 硬门拒绝，**不要**与「分类不一致」混为一谈。
4. 生效位置：图错误码只出现在**草案装载期**与生成时（摊平／编辑器即时校验）；**产物装载期没有 DSL**，故无图错误码。

### §14.2 节点路径

- 根：`$`
- 子节点：`actions[i]`、`actions[i].then[j]`、`actions[i].else[j]`、`actions[i].actions[j]`、
  `actions[i].options[k].actions[j]`
- **字段级错误的 `NodePath` 指向该字段**：`<节点路径>.<字段名>`（如 `actions[0].else`、`actions[1].bind`）
- 表达式内部：在字段路径后接 `.args[i]`（如 `actions[0].amount.args[1]`）

节点路径是**唯一**的定位手段（无 id 概念）。

### §14.3 错误对象（内核最小形态）

```csharp
public sealed record HandlerDslError(string Code, string NodePath, string Message);
```

三字段、不膨胀。装载器把它搬进 `PrefabLoadError`（`Code`／`NodePath` 直搬，`Target`／`Category` 由装载器填）。
前端只拿 `PrefabLoadError`，不拿 `HandlerDslError`。

---

## §15 vectors

### §15.1 两类，分工固定

| `kind` | 内容 | 谁跑 |
| --- | --- | --- |
| `check` | DSL 输入 ＋ 期望**一个**错误码 ＋ 期望**一个**节点路径 | **前后端各跑** |
| `gen` | DSL 输入 ＋ 期望 csx 文本（外置 `vectors/gen/NNN.expected.csx`） | **仅后端** |

R26 的「各实现同一套规则」中的「规则」**限定为校验规则**：前端要的是「这个图能不能存」，不是「它长成什么 csx」。
因此 R12 第 1 条（前端不得有 DSL→csx 路径）与 R21（vector 是唯一基准）同时成立。

### §15.2 载体格式

```jsonc
// vectors/check/001-else-empty.check.json
{
  "kind": "check",
  "name": "空 else 数组被拒",
  "document": { "schemaVersion": 1, "actions": [ … ] },
  "catalog": { "catalogRevision": 1, "scannedFaces": [ … ], "nodes": [ … ], "readables": { … } },
  "view": { "fields": { "Defense": { "type": "number" } } },
  "expect": { "code": "graph-shape", "nodePath": "actions[0].else" }
}
```

```jsonc
// vectors/gen/001-literal-shift.gen.json
{
  "kind": "gen",
  "name": "字面量位移",
  "document": { … },
  "catalog": { … },
  "view": { … },
  "expect": { "csx": "001-literal-shift.expected.csx" }
}
```

1. 每条 vector **自带 `catalog` 快照**（含 `catalogRevision`）：vector 只在快照上解析与生成，
   **不依赖运行期扫描结果**——否则目录一增长，vector 就随机失败。
2. 每条 vector 自带 **`view` 快照**（`fields`：字段名 → 类型）：`{ "view": "Defense" }` 的类型来自视图声明，
   没有它就无法复现类型推导。`view` 的 `writable` 一经提供，`write-forbidden` 的向量即可复现。
3. 快照必须含**标注展开结果**（多输出拆解、枚举取值集）与**扫描的门面类型清单**（可回答「这个节点来自哪个门面」）。
4. `check` 的期望值必须是「一个码 ＋ 一个路径」，**不许**写「码属于集合 {a,b}」。
5. 期望文本外置为文件，**不要**在 JSON 里塞转义字符串——行尾／缩进正是被比对的对象。
6. 生成器行为改动导致既有 vector 失败＝必须**同时**升 `schemaVersion` 或修订 vector；不允许静默。

---

## §16 变更流程（摘要）

1. 改语义／语法／校验规则／引脚映射规则／目录元模型／生成格式 → 升 `schemaVersion`，补 vector。
2. 只加节点／加可选引脚／放宽取值集 → 只递增 `catalogRevision`。
3. 破坏性目录变更（删／改名／可选→必填／收窄取值集）→ **同时**升 `schemaVersion`。
4. 本包不含 per-method 标注实例；标注实例只存在程序集里。
5. 「前端零改动」的准确含义：**前端代码与版本零改动，只需刷新目录**；它**不承诺**引擎侧零仪式
   （新动作加在 `EffectRuntime` 仍需过引擎自己的契约冻结门）。
