# golden vectors（v1）

一条 vector ＝ DSL 输入 ＋ 目录快照 ＋ 期望结果。vector 是**唯一基准**（R21）：两侧实现都以它为准；
生成器行为改动导致既有 vector 失败＝必须**同时**升 `schemaVersion` 或修订 vector，不允许静默（SPEC §15）。

## 两类（`kind`，封闭）

| `kind` | 目录 | 内容 | 谁跑 |
| --- | --- | --- | --- |
| `check` | `check/` | DSL ＋ 目录快照 ＋ 期望**一个**错误码 ＋ 期望**一个**节点路径 | **前后端各跑** |
| `gen` | `gen/` | DSL ＋ 目录快照 ＋ 期望 csx 文本（外置 `gen/NNN-<slug>.expected.csx`） | **仅后端** |

- `check` 的期望值**不许**写成「码属于集合 {a,b}」——那会把两侧分歧藏在「都有可能」里。
- `gen` 的前端不存在（R12 第 1 条：前端不得有 DSL→csx 路径）；它锁的是**生成器**的确定性与格式冻结（SPEC §13）。
- 「同一份非法 DSL，前后端报出的码与节点路径一致」是**测试期保证**：同一份 `check` vector 两端各跑一遍。
  不是运行期协商——不存在「前端提交校验结果给后端复核」的通道。

## 每条 vector 自带 `catalog` 快照

vector 只在**自带快照**上解析与生成，**不依赖运行期扫描结果**；否则目录一增长（`catalogRevision` 变），
vector 就会随机失败。快照＝`catalog.schema.json` 的实例形态，至少含：

- `catalogRevision`；
- `scannedFaces`（扫描了哪些门面类型——否则无法解释「为什么这个节点存在」）；
- `readables`（`read.path` 的封闭取值集）；
- `nodes[]`（含标注展开结果：多输出拆解、枚举取值集、实体子类型集）。

新建动作面方法**不会**让既有 vector 失败（快照是冻结的）。

## 命名

```text
check/NNN-<slug>.check.json     # 例：check/001-else-empty.check.json
gen/NNN-<slug>.gen.json         # 例：gen/001-literal-shift.gen.json
gen/NNN-<slug>.expected.csx     # 期望文本外置：行尾/缩进正是被比对的对象，不能先在 JSON 里被转义吃掉
```

## 比对

- `check`：实际产出的 `(Code, NodePath)` 必须与 `expect` **逐字相等**。
- `gen`：实际文本与 `.expected.csx` **逐字节全等**，**不做任何规范化**（行尾／缩进／尾换行都算）。
  两份只在行尾或空白上不同的文本＝两个 handler id、两份资产（SPEC §13 F12）。

## 覆盖面（v1 收口：S9 已交付）

- 13 个错误码**每个**至少一条 `check` 拒绝 vector（`check/019-pin-unknown-read-path` 为最后补齐的一码）：

  | 码 | 代表 vector |
  | --- | --- |
  | `graph-shape` | `001-else-empty`、`018-graph-shape-then-missing` |
  | `node-unknown` | `006-node-unknown` |
  | `pin-unknown` | `007-pin-unknown`、`019-pin-unknown-read-path` |
  | `pin-missing` | `008-pin-missing` |
  | `pin-type` | `002-entity-pin-fed-number`、`014-entity-pin-fed-list` |
  | `bind-scope` | `005-bind-dual-form`、`015-bind-view-name-clash`、`016-var-out-of-scope` |
  | `write-forbidden` | `009-write-forbidden` |
  | `loop-unbounded` | `004-loop-missing-times`、`017-loop-times-not-number` |
  | `ask-in-loop` | `010-ask-in-loop` |
  | `schema-version` | `011-schema-version` |
  | `expr-op-unknown` | `003-expr-op-unknown` |
  | `expr-arity` | `012-expr-arity` |
  | `expr-type` | `013-expr-type` |

- 生成类覆盖：**6 个控制流**（`sequence`／`branch`／`loop`／`while`／`iterate`／`choice`）、
  **4 个核心**（`let`／`write`／`ask`／`script`）、典型动作节点（`damage`／`draw`／`fetch`／`kill`／`stop`），
  以及 §13 的格式项（缩进／行尾／尾换行／数字格式／字符串转义／变量命名／空块／多 `await` 顺序／无多余临时变量）。
- 期望文本的重写只走 `ORC_WRITE_VECTORS=1`（`GeneratorTests.gen向量期望文本_仅在显式开关下重写`）：
  常规测试运行**只读不写**，否则生成器一改就变成假绿。

