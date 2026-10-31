# 生成类（`kind: "gen"`）vector

本目录的每条 vector 由 **DSL 输入 ＋ 目录快照 ＋ 期望 csx 文本**三部分组成：

```text
gen/001-literal-shift.gen.json
gen/001-literal-shift.expected.csx
```

## 为什么期望文本外置成文件

行尾（LF）、缩进（4 空格）、文件末尾换行**正是被比对的对象**（SPEC §13 F1–F3）。
塞进 JSON 字符串会先在传输层被转义／规整，比对就失去了意义。

## 比对口径

- **逐字节全等**，不做任何规范化（不 trim、不统一行尾、不重排空白）。实现见
  `tests/Orc.HandlerDsl.Tests/GeneratorTests.gen向量_与期望文本逐字节一致`。
- 生成类**仅后端跑**：前端不得存在 DSL → csx 的代码路径（R12 第 1 条）。

## 已落地

| vector | 锁住的规则 |
| --- | --- |
| `001-literal-shift` | 动作调用形态（实例门面经 `EffectRuntime.ResolveFor(orcSelf)`）＋ 表达式**内联**（无临时变量）＋ 数字字面量 `InvariantCulture` |
| `002-branch-write` | 块结构（`if`／`else` 各一层缩进）＋ `write` 直写 `view.<Field>`（不经 `EffectRuntime`）＋ 空块不生成占位 |
| `003-loop-runtime-bound` | 运行期上限只落**一个** `__bound_*` 临时变量、恒为 `for`（无裸 `while`）＋ `foreach` 只由 `iterate` 产生 ＋ `stop` → `ctx.Stop()` |
| `004-let-while-branch-write` | `let` 的绑定形态 ＋ `while`（`limit` 只求值一次、`test` 每轮重估）＋ `sequence` 不额外产生缩进 ＋ 表达式内 `read` 的 `await` 顺序保持书写顺序 |
| `005-choice-ask-iterate` | `choice` 的 `switch` 骨架（选项 index 顺序）＋ `ask` 的调用形态与降级参数 ＋ `foreach` 由 `iterate` 产生 ＋ 字符串常规转义（引号） |
| `006-script-multibind-source` | `script` 正文原样内联 ＋ 多输出 `bind` 的 `__res_*` 拆解（键序＝作者的键序）＋ `source` 连线（不连线则接宿主） |

## 重写期望文本

只在人显式设 `ORC_WRITE_VECTORS=1` 时由 `GeneratorTests.gen向量期望文本_仅在显式开关下重写` 重写；
常规测试运行只读。格式改动会让这些 vector 失败——按 SPEC §13 那也是**破坏性变更**（须升 `schemaVersion`）。

