# handler DSL 前端接入指南（v1）

> 面向：负责「效果编辑器」画布的前端开发者。
> 权威规范是 [`SPEC.md`](./SPEC.md)；本文只讲**你要做什么、怎么接、怎么自测**，不重复规范条文。
> 遇到本文与 SPEC 冲突，**以 SPEC 为准**——那说明本文该修了，请提 issue。

---

## 目录

1. [你要交付的三件事](#1-你要交付的三件事)
2. [职责边界：哪些不是你的事](#2-职责边界哪些不是你的事)
3. [五分钟跑通](#3-五分钟跑通)
4. [接入时序](#4-接入时序)
5. [三个后端接口](#5-三个后端接口)
6. [两套类型名并存](#6-两套类型名并存)
7. [画布模型](#7-画布模型)
8. [折叠成树（唯一的算法难点）](#8-折叠成树唯一的算法难点)
9. [本地校验：13 个错误码](#9-本地校验13-个错误码)
10. [错误展示](#10-错误展示)
11. [提交与保存](#11-提交与保存)
12. [用契约包自测](#12-用契约包自测)
13. [十一条禁令](#13-十一条禁令)
14. [版本演进时你要做什么](#14-版本演进时你要做什么)

---

## 1. 你要交付的三件事

| # | 交付 | 说明 |
| --- | --- | --- |
| 1 | **画布** | 把一份 DSL 树画成可编辑的节点图：节点、引脚、连线、表达式 |
| 2 | **折叠** | 把自由连线的图折叠成**树**（数组顺序即执行顺序），见 §8 |
| 3 | **保存** | 产出 `.hdsl.json` 文本 → `AssetEditor.Commit` 提交 |

加上一件隐形的：**本地校验**。你必须在保存前跑完 13 条校验（§9），把错误显示在节点上。
后端在装载期会再校验一次（那是最终裁定），但你不能依赖它——作者改一次图就要等一次后端，编辑体验会碎掉。

---

## 2. 职责边界：哪些不是你的事

这一节比上一节更重要。**越界的实现会在半年后变成两份互不一致的真源。**

| 能力 | 前端 | 后端 |
| --- | --- | --- |
| 节点清单、引脚、是否可复制 | 查询后展示 | 反射扫描生成 |
| 引脚类型、必填性、枚举取值集 | 查询后展示 | 目录声明 |
| 多输出拆解、枚举落型 | **不展开**（已展开好给你） | 目录生成时展开 |
| 13 条校验 | **实现**（本地即时反馈） | 装载期再判（最终裁定） |
| 图 → 树 | **实现** | **不实现**；收到非树就拒 |
| **DSL → csx** | **绝对禁止** | 唯一实现 |
| 摊平、版本、lock | 不参与 | 实现 |
| 预制体展开（继承／覆写） | **不实现** | `DescribePrefab` 给你 |

> **为什么「DSL → csx」是硬禁令**：同一份 DSL 必须永远生成同一段文本（文本的哈希就是 handler 的身份）。
> 一旦前端也生成一遍，两边差一个空格就会产生两个 handler id、两份资产，而行为完全一样——这种 bug 极难发现。

---

## 3. 五分钟跑通

### 3.1 最小 DSL

```json
{
  "schemaVersion": 1,
  "actions": [
    { "op": "ask", "kind": "unit", "side": "enemy", "prompt": "选一个敌方单位", "bind": "targets" },
    { "op": "iterate", "over": { "var": "targets" }, "as": "t",
      "actions": [
        { "op": "damage", "target": { "var": "t" }, "amount": { "lit": 3 } }
      ] }
  ]
}
```

读法：**选一个敌方单位 → 对每个选中的单位打 3 点伤害**。

- `actions` 是数组，**顺序即执行顺序**（没有 `next`、没有连线）。
- `ask` 返回 `list<entity>`（0 或 1 个元素），所以要用 `iterate` 遍历，不能直接当单个实体用。
- `{ "var": "t" }` 是绑定引用；`t` 由 `iterate` 的 `as` 引入。
- `{ "lit": 3 }` 是字面量。

### 3.2 它长在资产里

DSL 不是资产，是一个独立文件，被草案的成员引用：

```
<pack>/
  cards/…
  effects-v4/…
  handler-dsl/
    fire-support-on-played.hdsl.json     ← 你的产物
  modings/
    my-fire-support.moding.json           ← 草案引用它
```

草案里的引用（**只是片段**，完整结构见 §3.4）：

```json
{
  "members": [
    {
      "name": "onPlayed",
      "handler": {
        "dsl": "handler-dsl/fire-support-on-played.hdsl.json",
        "dslHash": "sha256:…"
      }
    }
  ]
}
```

- `dsl` 是**相对包根**的路径（`/` 分隔）。
- `dslHash` 可选：它是引用那一刻的内容哈希，用于「DSL 改过了，请重新摊平」的诊断。

### 3.3 保存

```csharp
editor.Commit("handler-dsl/fire-support-on-played.hdsl.json", dslJson);
```

**DSL 文件没有版本、不进 lock、不 bump 任何资产版本**——这是有意的：它的可见性由草案承担，自己不是资产。
所以这条提交路径会**立刻成功**，不会有「版本冲突」。

### 3.4 草案（moding）怎么写

作者编辑的**从来不是父资产**，而是一份**草案**——一个只记「我改了什么」的增量文件。

`modings/my-fire-support.moding.json`：

```json
{
  "schemaVersion": 4,
  "asset": {
    "kind": "moding",
    "id": "my-fire-support.draft",
    "product": { "id": "my-fire-support" },
    "extends": "diy.fire-support.effect",
    "requires-host": "any",
    "active-when": "loaded",
    "one-shot": false,
    "private-fields": [],
    "members": [
      {
        "name": "onPlayed",
        "visibility": ["callable", "mountable"],
        "view": {
          "fields": [
            { "name": "Card", "type": "entity", "writable": false, "required": false }
          ]
        },
        "handler": {
          "id": "my-fire-support.draft#onPlayed",
          "entry": "HandleAsync",
          "dsl": "handler-dsl/on-played.hdsl.json",
          "dslHash": "sha256:…"
        }      }
    ],
    "mounts": [
      { "target": "engine#card.played", "member": "onPlayed" }
    ],
    "wraps": []
  }
}
```

#### 三个 id 别搞混

| 字段 | 是什么 | 例 |
| --- | --- | --- |
| `asset.id` | 草案**自己**的资产 id | `my-fire-support.draft` |
| `asset.product.id` | 摊平后**产物**的资产 id（**可省略**；缺省＝把 `.draft` 去掉） | `my-fire-support` |
| `asset.extends` | **父效果** id（**必填**，没有它就不是草案，而是组件资产） | `diy.fire-support.effect` |

#### 成员只写增量（最容易错的一条）

`DescribePrefab` 给你的是**展开后的全表**（父的 ＋ 你的），但 `.moding.json` 里**只写**两样：

- **重载的成员**：`DescribePrefab` 里 `Overridden = true` 的那些（整段替换父的同名成员）；
- **新增的成员**：`Added = true` 的那些（父里没有的名字）。

把展开全表整个写进去 = **全量覆写**。后果是：父资产以后修了 bug，对这份草案**完全无效**——
父子链名存实亡，而 UI 上看起来一切正常。这是草案机制里唯一「写错也不报错」的地方。

#### handler 三选一（互斥）

`dsl`（＋可选 `dslHash`）／`script`（内联 csx）／`assembly-key`（程序集键）。三个都给＝装载期拒绝。

`dslHash` 的形态是 `sha256:` ＋ **64 位小写十六进制**（即对 `.hdsl.json` **文件原文**做的 SHA-256）。
比对时大小写不敏感、允许省略 `sha256:` 前缀；但**建议按规范写全**——它是「DSL 改过了，请重新摊平」
的诊断依据，写错格式会让提示永远不触发。

#### 保存顺序

**先提交 DSL 文件，再提交草案。** 反过来会出现「草案引用的 DSL 还不存在」——
草案提交会走资产校验（失败时内容回滚），而 DSL 提交是纯写入，所以这个顺序最省事。

#### 草案不是最终资产

发布时后端把草案**摊平**成 `*.component.json`（产物）：产物里只有生成好的 csx 文本，**没有任何 DSL 结构**。
你不用管摊平，也不用在产物里保留 DSL。

> 摊平的三类来源（照抄父 csx ／ 由 DSL 生成 ／ 父本身也是生成物）由后端决定，前端不参与。

---

## 4. 接入时序

```
启动
 │
 ├─ CatalogRevision()            ← 轻查询，只回一个整数
 │   └─ 与缓存不同？→ GetCatalog() 拉全量并缓存
 │
 ├─ ListPackages() / ListAssets()  ← 选包、选父效果
 │
 ├─ DescribePrefab(packRoot, prefabId, draftPath?)   ← 打开编辑器
 │   └─ 无草案时 draft=null、所有成员 inherited=true
 │
 ├─ 画布编辑（本地校验，实时出错误）
 │
 ├─ 折叠成树 → 序列化为 DSL 文本
 │
 └─ Commit(relativePath, content)
```

**唯一必须记住的时序约束**：目录缓存要跟着 `catalogRevision` 走。
revision 变了就重拉，不要用「启动时拉一次、一直用」。

---

## 5. 三个后端接口

> **程序集位置**：`HandlerCatalogFacade` 在 `Orc.Game`，`AssetEditor` 在 `Orc.Packages`。
> 这两个前端桥接层（`Kards.Ui.OrcBridge`）**已经引用**了，不需要新增引用。
> 你会用到的 DTO 都在 `Orc.Game` / `Orc.Packages` 里，**不必**引用 `Orc.HandlerDsl`（那是后端实现）。

### 5.1 节点目录（引擎能力）

```csharp
// Orc.Game.Effects.HandlerCatalogFacade
int CatalogRevision();          // 轻查询：只回版本号
HandlerCatalogDto GetCatalog(); // 全量：节点 + 引脚 + 可读项
```

```csharp
record HandlerCatalogDto(
    int CatalogRevision,
    IReadOnlyList<string> ScannedFaces,          // 节点来自哪些门面（调试用）
    IReadOnlyDictionary<string, string> Readables, // read.path 的封闭取值 → 类型名
    IReadOnlyList<HandlerCatalogNodeDto> Nodes);

record HandlerCatalogNodeDto(
    string Name,                    // 就是 DSL 里的 op 取值
    bool HasAwait,                  // 折叠判定用（§8）
    bool HasSideEffect,             // 折叠判定用（§8）
    string? FaceType,               // 来源类型；合成节点（stop/interrupt）为空
    IReadOnlyList<HandlerCatalogPinDto> Inputs,
    IReadOnlyList<HandlerCatalogPinDto> Outputs);

record HandlerCatalogPinDto(
    string Name,
    string Type,                    // entity / number / string / boolean / list<entity> …
    bool Required,
    IReadOnlyList<string> Subtypes, // 实体引脚的运行期接受集：unit / hq / card
    IReadOnlyList<string> EnumValues, // 非空＝下拉框选项
    string? EnumType);
```

**这个接口回答「引擎能表达什么」**，与包无关。所以它不接 `packRoot`。

### 5.2 预制体视图（资产上下文）

```csharp
// Orc.Packages.Editing.AssetEditor
PrefabDescription DescribePrefab(string packRoot, string prefabAssetId, string? draftPath = null);
```

返回的是**已经展开好的**成员表，你不需要自己算继承：

```csharp
record PrefabDescription(
    string PackRoot,
    string PrefabAssetId,
    PrefabSummary? Prefab,                        // 父未注册＝null（同时给 Diagnostics）
    PrefabDraftSummary? Draft,                    // 无草案＝null
    IReadOnlyList<PrefabMemberDescription> Members,
    IReadOnlyList<PrefabMountableTarget> MountableTargets,
    IReadOnlyList<PrefabWrappableMember> WrappableMembers,
    IReadOnlyList<PrefabDescriptionDiagnostic> Diagnostics);
```

`PrefabMemberDescription` 里你会用到的：

| 字段 | 用途 |
| --- | --- |
| `Name` / `Address` | 成员名与地址（`self#onPlayed`） |
| `Inherited` / `Overridden` / `Added` | **继承／重载／新增三态**，直接用于徽标，不要自己推 |
| `Overridable` | 能否重载（资产级开关关闭或成员封死时为 false，此时把入口灰掉） |
| `IsSignal` / `SignalTiming` | 是否信号、时机（`any`／`load`／`placed`） |
| `Sealed` | 成员级封死 |
| `SeamNames` | handler 内部的具名缝（包装可挂点） |
| `ViewFields` | 视图字段：名／类型／**可写**／必填 |
| `HandlerSource` / `HandlerId` | 当前 handler 来源（`script`／`dsl`／`assembly-key`） |
| `MountedTo` / `WrappedAt` | 已有挂载与包装 |

**`ViewFields[].Writable` 是 `write` 节点可写字段的唯一依据。** 不要从别处推断。

`MountableTargets` 里每条都带 `Timing`——**必须校验**：
把 `load` 时机的信号挂给 `active-when: placed` 的组件，装载期会被拒。这个错误在编辑器里就该拦住。

### 5.3 提交

```csharp
// Orc.Packages.Editing.AssetEditor（或 AssetEditorSession）
EditorCommitResult Commit(string relativePath, string content);
```

- `relativePath` 相对**包根**。
- 接受的后缀：`*.card.json` / `*.prefab.json` / `*.deck.json` / `*.moding.json` / `*.hdsl.json`。
- `.hdsl.json` 走**快速通道**：写入即完成，不解析、不校验、不碰 lock。

---

## 6. 两套类型名并存

这不是笔误，是两层各自的既有约定。**跨层混用会写错 DSL。**

| 场景 | 写法 | 来源 |
| --- | --- | --- |
| **节点引脚**类型（目录） | `list<entity>` | `HandlerCatalogPinDto.Type` |
| **视图字段**类型（资产／`DescribePrefab`） | `entity[]` | `PrefabViewFieldDescription.Type` |
| **DSL 文档里**（`script.inputs[].type`） | `list<entity>` | SPEC §6.1 |

等价关系（SPEC §6.6）：`entity[]` ↔ `list<entity>`，`number[]` ↔ `list<number>`，其余同理。

**实践建议**：内部统一成一种规范形态（推荐 `list<T>`），只在渲染视图字段那一侧做一次转换。
不要让两种字符串在代码里自由流动——`"entity[]" != "list<entity>"` 会让相等判断悄悄失效。

---

## 7. 画布模型

DSL 是树，但作者看到的是图。两者之间有一个明确的映射，理解它能让你的数据模型简单很多。

### 7.1 三层结构

```
执行层（节点）      值层（表达式）           绑定层
─────────────      ─────────────────        ──────────
actions[] 数组  ←→  引脚上的表达式树    ←→  let / bind / as
顺序即执行          纯数据、可嵌套           不可变、词法作用域
```

- **执行层**：一个 `actions[]` 就是一列竖排的节点，上下顺序就是执行顺序。没有「连线」这个概念——
  你不能在画布上从一个节点拉一条线到另一个节点表示「然后」，那是数组顺序。
- **值层**：引脚上的值是**表达式树**（字面量／绑定引用／视图字段／读取／算子嵌套）。
  画布上通常表现为「值连线」或「内联小卡片」，但**它不参与执行顺序**。
- **绑定层**：`let` / `bind` / `iterate.as` 产生的名字，供值层引用。

### 7.2 节点形状清单

| op | 视觉建议 | 子区域 |
| --- | --- | --- |
| `sequence` | **可以不画**（等价于「这一层就是顺序」），或画成无边框容器 | `actions` |
| `branch` | 菱形／双出口 | `test`（值）＋ `then` / `else`（两个执行区） |
| `loop` | 带次数徽标 | `times`（值）＋ `actions` |
| `while` | 带条件与上限徽标 | `test` / `limit`（值）＋ `actions` |
| `iterate` | 带集合与元素名徽标 | `over`（值）＋ `as`（名字）＋ `actions` |
| `choice` | **交互节点**（特殊色） | `prompt` ＋ `options[].actions` 多分支 |
| `let` | 小卡片 | `value`（值）＋ `name`（名字） |
| `write` | 小卡片 | `field`（从可写字段选）＋ `value`（值） |
| `ask` | **交互节点**（特殊色） | 若干属性 ＋ 可选出引脚 |
| `script` | 代码块 | `body`（文本域）。**`inputs`／`outputs` 只是声明，v1 不接线**（见 §7.4） |
| 动作节点 | 普通圆角矩形 | 目录声明的引脚 ＋ 可选 `bind` |
| `stop` / `interrupt` | 终止符号 | 无引脚 |

**交互节点（`ask` / `choice`）要单独着色**：它们受 `ask-in-loop` 约束（不能出现在任何循环体内），
着色是作者唯一能在连线时立刻感知的方式。

### 7.3 引脚连线规则

1. **类型必须精确匹配**（SPEC §7.6）：`number` 不能喂 `string`，`list<entity>` 不能喂 `entity`。
   没有隐式转换——不要好心帮作者加一个「自动取第一个」。
2. **实体子类型只是提示**：`Subtypes: ["unit","hq"]` 表示运行期接受这两种。
   静态能确定不匹配时（如上游是 `ask{kind:"hq"}`、引脚只收 `unit`）才拒。
3. **必填引脚必须有值**，可选引脚可以空着（空着就用后端默认值）。
4. **枚举引脚**（`EnumValues` 非空）用下拉框，不要自由输入——写错值＝装载期拒绝。

### 7.4 `script` 节点的特殊处理

`script{ inputs, outputs, body }` 里，**`inputs`／`outputs` 在 v1 只是声明，没有「引脚值」通道**（SPEC §3）：

- 正文里**不能**引用未绑定的名字（写了 `x` 而没在别处绑定＝编译失败）；
- `outputs` **不会**成为图的绑定——你不能从 script 节点拉一条值连线出去；
- 它能用的只有 `view`（视图字段）、`orcSelf`（宿主卡）、`ctx`、`ct`，以及**前面节点已建立**的绑定。

所以画布上：可以显示这两个引脚列表（供作者参考「这段脚本预期吃什么」），但**不要把它们做成可连线的端口**。
需要与图交换值时，让作者改用 `let` / `bind` / 动作节点。

> 这一条是 v1 的**已知限制**，不是遗漏。真要接线，得给 DSL 加 `arguments` 字段，那是下一个契约增量。

---

## 8. 折叠成树（唯一的算法难点）

画布允许一个节点被多个下游使用（自由连线），但 DSL 是树。**这个折叠必须在前端完成。**

### 8.1 三种情况

| 情况 | 判定 | 处理 |
| --- | --- | --- |
| 出度 = 1 | 普通 | 直接放进目标位置 |
| 出度 > 1，且是**纯表达式** | 子树内无 `await`、无副作用 | **复制**成多个独立副本 |
| 出度 > 1，且含 `await` 或副作用 | 其余 | **拒绝折叠**，提示改用 `let` |

### 8.2 判定依据

目录给的是**单节点**标记，**不是**子树结果（SPEC §12.3）：
目录是节点模板，同一个 `damage` 挂在两处可以带完全不同的子树，所以「某个节点的子树」在目录这一层不存在。

你必须**在具体树上自己聚合**：

```text
awaitOf(x)      = 自身标记(x) ∨ 任一子节点 awaitOf ∨ 任一值表达式含 count／read
sideEffectOf(x) = 自身标记(x) ∨ 任一子节点 sideEffectOf
```

- 动作节点：`x.HasAwait` / `x.HasSideEffect` 直接读目录；
- 内建 op 没有目录条目，自身标记是**固定**的：

| op | await | sideEffect |
| --- | --- | --- |
| `ask` / `choice` / `script` | ✔ | ✔ |
| `write` / `stop` / `interrupt` | ✘ | ✔ |
| `let` / `sequence` / `branch` / `loop` / `while` / `iterate` | ✘ | ✘ |

> `script` 一律按「有 await 且有副作用」处理——正文是任意 csx，静态判定不了，保守是对的。

纯表达式 = `!awaitOf && !sideEffectOf`。

典型：
- `{ "view": "Defense" }`、`{ "lit": 1 }`、`{ "op": "add", … }` → 纯，可复制
- `{ "op": "count", "args": [...] }` → 含 `await`（查询），不可复制
- 任何动作节点、`write`、`ask`、`choice`、`script` → 有副作用，不可复制
- **容器节点要往子树看**：一个 `branch` 自身无标记，但它的 `then` 里有 `damage`，那整个 `branch` 就不可复制

### 8.3 为什么必须拒绝，而不是也复制

复制一次 `count` 就是**在两个不同时刻各查一次**；复制一次 `damage` 就是**打两次**。
作者在画布上看到的是「一个节点，两个下游」，直觉是「共享一个结果」，实际却是执行两次。
这是图形化编辑器最经典的坑，必须在折叠时挡住。

正确的表达是显式共享中间值：

```json
{ "op": "let", "name": "n", "value": { "op": "count", "args": [ {"view":"Units"} ] } },
{ "op": "branch", "test": { "op": "gt", "args": [ {"var":"n"}, {"lit":0} ] }, "then": [...] },
{ "op": "damage", "target": {...}, "amount": { "var": "n" } }
```

### 8.4 复制要给作者看得见

如果静默复制，作者会以为画布上一个节点对应 DSL 里一个节点，读图时数错节点数。
保存前给一个可见提示，例如：**「此处将生成两份独立副本」**，并在折叠预览里显示复制数。

---

## 9. 本地校验：13 个错误码

完整定义见 SPEC §14。这里给**判定条件**，你可以直接照着写。

| 码 | 判定条件 | 定位到 |
| --- | --- | --- |
| `graph-shape` | ①不是对象 ②子节点不是数组 ③深度 > 64 ④`"else": []` ⑤`then` 缺失或为空 | 该节点路径 |
| `node-unknown` | `op` 不在「10 个内建 op ∪ 目录节点名」内 | 该节点 |
| `pin-unknown` | 节点出现目录未声明的引脚名；或 `read.path` 不在 `Readables` 内 | 该引脚 |
| `pin-missing` | 必填引脚无值 | 该引脚 |
| `pin-type` | 表达式推导类型 ≠ 引脚期望类型；或实体子类型静态确定且无交集 | 该引脚 |
| `bind-scope` | 名字未定义／已定义（单赋值）／遮蔽外层／与视图字段同名；`bind` 形态非法（单输出写成对象、多输出键集不等） | 该 `bind`／该名字 |
| `write-forbidden` | `write.field` 不在视图声明内，或 `Writable=false`；视图未知（null）时一律拒 | 该 `field` |
| `loop-unbounded` | `limit`／`times` 缺失，或类型不是 `number` | 该字段 |
| `ask-in-loop` | `ask`／`choice` 出现在任意循环体内（`loop`／`while`／`iterate`） | 该交互节点 |
| `schema-version` | `schemaVersion` ≠ 1 | 根 `$` |
| `expr-op-unknown` | 算子名不在 §5 封闭集内 | 该算子节点 |
| `expr-arity` | 算子入参个数不符 | 该算子节点 |
| `expr-type` | 算子入参类型不符（含「聚合输入不是集合」） | 该算子节点 |

**前端能实现全部 13 条**——所需数据你都有：
`GetCatalog()` 给引脚与算子，`DescribePrefab()` 给视图字段与可写性。

### 9.1 节点路径

错误定位一律用**节点路径**，不是节点 id（DSL 里没有 id）：

```text
$
actions[0]
actions[0].else
actions[0].bind.status      ← 字段级
actions[1].args[0]          ← 表达式内部
```

规则见 SPEC §14.2。路径里 `$` 是根。

---

## 10. 错误展示

作者看到的应该是**「哪个节点的哪个引脚，为什么不对」**，不是错误码。

| 码 | 建议文案（可本地化） |
| --- | --- |
| `pin-missing` | 「`{引脚}` 还没有连接」 |
| `pin-type` | 「`{引脚}` 需要 `{期望}`，当前是 `{实际}`」 |
| `write-forbidden` | 「`{字段}` 不可写」 |
| `ask-in-loop` | 「选择类节点不能放在循环里」 |
| `loop-unbounded` | 「循环必须指定次数上限」 |
| `bind-scope` | 「名字 `{名}` 已存在／不可用」 |
| 其余 | 直接展示后端给的 `Message` |

**不要在 UI 里隐藏错误码**——放在 tooltip 或详情里。作者报 bug 时你第一句话就会问「码是多少」。

---

## 11. 提交与保存

### 11.1 路径

```
<pack>/handler-dsl/<任意名>.hdsl.json
```

- 后缀必须是 `.hdsl.json`，否则会被拒绝；
- 不在 `handler-dsl/` 下也能提交（只是约定），但**不要**放进 `effects-v4/`——那里被资产扫描器扫，会把它当资产解析失败。

### 11.2 内容

- UTF-8，无 BOM；
- 行尾用 `\n`（不是必须，但建议——与 `vectors/gen` 的期望文本一致，便于比对）；
- **不要格式化后再比对**：这段文本会被哈希，多一个空格就是一个新 handler id。

### 11.3 提交失败

`Commit` 只对 `.hdsl.json` 做写入，**不做内容校验**（校验是你的职责，见 §9）。
失败只可能是 IO 或路径问题。

对 `.moding.json` 则不同：它会走资产校验，可能返回 `Violations`（此时**内容会被回滚**，磁盘不变）。
所以：**先提交 DSL，再提交草案**——避免草案通过而 DSL 缺失。

---

## 12. 用契约包自测

`contracts/handler-dsl/v1/vectors/` 是**你的测试数据**，也是前后端一致性的唯一基准。

### 12.1 两类

| 类别 | 文件 | 内容 | 你要做什么 |
| --- | --- | --- | --- |
| `check` | `vectors/check/*.check.json` | DSL ＋ 期望的**一个错误码 ＋ 一个节点路径** | **跑一遍，结论必须一致** |
| `gen` | `vectors/gen/*.gen.json` | DSL ＋ 期望的 csx 文本 | **不用管**（后端专有） |

### 12.2 `check` 向量格式

```json
{
  "kind": "check",
  "name": "…",
  "refs": ["R14", "SPEC §9.1"],
  "document": { "schemaVersion": 1, "actions": [ … ] },
  "catalog": { … 目录快照（含 catalogRevision / nodes / readables） … },
  "view": { "fields": { "Target": { "type": "entity" }, "Defense": { "type": "number", "writable": true } } },
  "expect": { "code": "pin-missing", "nodePath": "actions[0].amount" }
}
```

注意 `catalog` 是**内嵌快照**：用文件里的，不要用 `GetCatalog()` 的结果——
否则后端加个节点，向量就会随机失败。

### 12.3 一致性要求

SPEC §15.1：**同一份 `check` 向量，前端跑一遍、后端跑一遍，产出的 (码, 路径) 必须相同。**

这是测试期保证，不是运行期协商——不要设计「把校验结果发给后端复核」的通道。

---

## 13. 十一条禁令

1. **不要生成 csx。** 一次都不要尝试，包括「只为了预览」。
2. **不要给节点加 id。** DSL 没有 id，定位恒用路径。画布内部的 id 不要写进文件。
3. **绑定名不要以 `__` 开头。** 那是生成器保留前缀（`__runtime`／`__bound_*`／`__res_*` 等），
   撞上会在生成的 csx 里重复声明。校验器会以 `bind-scope` 拒绝（SPEC §8.7）。
3. **不要用 `"else": []`。** 没有 else 分支就**省略这个字段**。
4. **不要把单输出的 `bind` 写成对象。** 单输出＝字符串，多输出＝对象，没有等价写法。
5. **不要用 `.hdsl.json` 之外的扩展名。**
6. **不要把 DSL 放进 `effects-v4/`**（会被资产扫描器误吃）。
7. **不要在循环里放 `ask` / `choice`。**
8. **不要自己展开多输出或枚举**——目录已经展开好了。
9. **不要把 `catalogRevision` 不一致当成错误。** 它只用于诊断提示。
10. **不要假设「节点清单永远不变」**。用 revision 判断何时重拉。

---

## 14. 版本演进时你要做什么

### 14.1 后端新增了一个动作方法

你会看到 `CatalogRevision()` 变了。

**你要做的**：重拉 `GetCatalog()`，新节点自动出现。
**你不用做的**：改代码、发版、等后端通知。

这就是 `catalogRevision` 与 `schemaVersion` 分开的意义（SPEC §1）。

### 14.2 后端删了节点／改了引脚名

这是**破坏性变更**，`schemaVersion` 会跟着变。

**你会看到**：`GetCatalog()` 返回的目录里那个节点没了，或引脚名变了。
**你要做的**：把旧文档里引用它的地方标成错误（`node-unknown` / `pin-unknown`），让作者修。

后端不会帮你自动迁移——**不做猜测性回填**是刻意的设计。

### 14.3 `schemaVersion` 不匹配

加载旧文档时，如果文档的 `schemaVersion` 与本包不同：**直接拒绝**，并在 UI 上说明「这份图是用旧版本画的」。

不要尝试「尽力解析」——那会让作者以为图还能用，实际装载必失败。

---

## 附：契约包文件清单

```
contracts/handler-dsl/v1/
├── FRONTEND-GUIDE.md        ← 本文
├── SPEC.md                  ← 规范正文（权威）
├── schema.json              ← DSL 文档的 JSON Schema（可直接用于编辑器校验）
├── catalog.schema.json      ← 目录元模型（你收到的 GetCatalog 结果应符合它）
├── catalog.snapshot.json    ← 真实目录快照（26 个节点，可直接当 mock 数据）
├── vectors/
│   ├── check/*.check.json   ← 你的测试数据（19 条，每码至少一条）
│   └── gen/*.gen.json       ← 后端专有（8 条），你不用管
└── EXCLUSIONS.md            ← 哪些引擎能力不能图形化（解释「为什么没有那个节点」）
```

**建议的开发顺序**：
1. 用 `catalog.snapshot.json` 做 mock，先把画布画出来；
2. 接 `GetCatalog()`，替换 mock；
3. 用 `vectors/check/` 驱动本地校验器；
4. 接 `DescribePrefab()` 做「选父效果 → 重载成员」流程；
5. 接 `Commit()` 保存。
