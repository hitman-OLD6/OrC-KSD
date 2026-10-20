# 示例卡包（card-pack-demo）

> 『端到端创作卡牌工具链』交付演示：一份可直接复制、可预检、可装载、可对局的最小**分发数据**样本——
> 演示并验收「json → 游戏内卡牌」全链：预检（分发预检器）→ 装载 → 注册 → 对局初始化 → 打出 → 效果行为。
>
> 本包为**纯数据分发**：全部效果均以 csx 形态承载（内联或效果库），**无需编写任何 C# 代码**即可运行。
> 配套演示测试见 `tests/Orc.Game.Tests/CardPackDemoTests.cs`（「跑起来是什么样」的完整样例）。

---

## 一、包结构总览

```
card-pack-demo/
├── README.md                            本文档
├── manifest.json                        包清单 v2（id=demo / metadata[] 列各 sidecar；S10a 迁移补齐）
├── lock.formal.json / lock.test.json    两 lock（正式／测试；本包资产 GUID，S10a 迁移生成）
├── cards/                               卡目录 A（主卡目录）
│   ├── demo.rifleman.card.json              步枪兵——基础单位卡（无效果）
│   ├── demo.grenadier.card.json             掷弹兵——内联 csx 效果（对象态）
│   └── demo.quartermaster.card.json         军需官——效果库引用（共享效果 ①）
├── cards-support/                       卡目录 B（支援卡目录）
│   ├── demo.vanguard.card.json              先锋——效果库引用（共享效果 ②）
│   └── demo.scout.card.json                 侦察兵——内联 csx 效果（字符串态）＋定向取卡
└── effects/                             效果库目录（1 个）
    └── demo.rally.prefab.json               集结号——被 cards/ 与 cards-support/ 跨目录共享
```

> 每个资产源文件同目录另有 `<源文件名>.meta.json` 资产元数据 sidecar（S10a 迁移生成，初始版本 `1.0.0`；
> 一个源文件一份、文件名可由源文件名唯一推出）。本包 `manifest.json` 的 `id=demo` 由目录名去 `card-pack-`
> 前缀派生，`guid` 由 `id` 确定性派生（重跑迁移不漂移）。

| 文件 | 内容 | 演示点 |
|------|------|--------|
| `cards/demo.rifleman.card.json` | 基础单位卡（六组件应用面完备、无效果） | 最小可打出单位 |
| `cards/demo.grenadier.card.json` | 内联效果（对象态）：部署时对敌方一个单位造成 1 点伤害 | 内联 csx 效果（对象态） |
| `cards/demo.quartermaster.card.json` | 引用 `effects/demo.rally.prefab.json` | 效果库引用＋跨目录共享 |
| `cards-support/demo.vanguard.card.json` | 引用同一库效果 `demo.rally` | 一效果两卡共享（另一张） |
| `cards-support/demo.scout.card.json` | 内联效果（字符串态）：单位加入时从卡组定向抽取此牌 | 定向取卡＋字符串态兼容读取 |
| `effects/demo.rally.prefab.json` | 库效果：部署时从卡组抽 1 张牌 | 效果库分发（`*.prefab.json`） |

效果行为一览：

| 效果 id | 形态 | 触发时机 | 行为（对局内可观察） |
|---------|------|----------|----------------------|
| `demo.grenade`（掷弹兵内联） | 内联·对象态 | `unit.deployed`（部署时） | 对敌方一个单位造成 1 点伤害 |
| `demo.rally`（集结号·效果库） | 库文件 `*.prefab.json` | `unit.deployed`（部署时） | 从卡组抽 1 张牌 |
| `demo.enlist`（侦察兵内联） | 内联·字符串态 | `unit.joined`（单位加入时） | 从卡组定向抽取此牌进入手牌 |

---

## 二、使用步骤（五段管道）

> 管道口径：**预检 → 装载 → 注册 → 初始化 → 使用**。
> 注意：**注册必须先于卡加载**——卡 `effects` 声明的效果预制体须在注册面就绪（引用就绪前提）。

### 第 1 步：预检（dry-run 校验）

在注册/装载进对局**之前**，用分发预检器对分发数据做纯只读静态校验：

```csharp
var report = CardDistributionValidator.Validate(
    cardDirectories: new[] { "<包根>/cards", "<包根>/cards-support" },
    prefabLibraryDirectories: new[] { "<包根>/effects" });
```

预检报告示例输出（关键字段，描述级）：

```
输入面：卡目录 2 个（cards/、cards-support/）；效果库目录 1 个（effects/）
通过面：成功卡定义 5；成功快照 3（库 1 ＋ 内联 2）
问题面：Errors 0；Warnings 0
判定：  HasNoErrors = true（可放行）
```

成功判据（本包）：**Errors = 0 且 Warnings = 0**（含跨目录共享引用面）；统计面＝输入 2 卡目录 ＋ 1 库目录、成功卡 5、成功快照 3。主要读取面：`report.HasNoErrors` / `report.ErrorCount` / `report.WarningCount` / `report.SuccessfulCardCount` / `report.SuccessfulSnapshotCount`。

### 第 2 步：装载（读入，纯数据产出）

逐目录装载卡数据体（`*.card.json`）：

```csharp
var loadA = CardDataLoader.LoadDirectory("<包根>/cards");          // → Definitions / Prefabs / EffectDeclarations
var loadB = CardDataLoader.LoadDirectory("<包根>/cards-support");  // 逐目录装载（见"共享边界"）
```

装载为**纯数据产出**——不注册进引擎；产出「卡定义集 ＋ 内联效果预制体 ＋ 效果声明」三样数据。

### 第 3 步：注册（效果声明 ＋ 效果库/预制体注册）

```csharp
// ① 卡 → 效果声明（引用 ＋ 内联并入；逐卡声明）
var registry = new CardEffectRegistry();
foreach (var (cardId, prefabIds) in loadA.EffectDeclarations.Concat(loadB.EffectDeclarations))
{
    registry.DeclarePrefab(cardId, prefabIds);
}

// ② 组对局（注册表随装配输入传入；定义集＝装载产出）
var match = new Match(deckA, deckB, definitions, /* … */ effectRegistry: registry);

// ③ 内联效果预制体注册（每张内联快照）
foreach (var snapshot in loadA.Prefabs.Concat(loadB.Prefabs))
{
    match.Engine.Prefabs.RegisterPrefab(snapshot);
}

// ④ 效果库装载（扫描目录 `*.prefab.json` 并注册——**装载与注册合并语义**，如实说明、不强制拆分）
var libraryLoad = match.Engine.Prefabs.LoadDirectory("<包根>/effects"); // → Loaded / Failures
```

「注册」一段涵盖的三件事：**效果声明注册（`DeclarePrefab`）＋ 内联预制体注册（`RegisterPrefab`）＋ 效果库注册（`LoadDirectory`——内部逐文件注册）**。

### 第 4 步：初始化（对局装配）

```csharp
await match.Initialize();
```

初始化装配（卡库/判定器/效果运行时/命令管理器等）并装载双方卡组；**效果随卡装载**（内联与库引用在此解析——故注册须先行）。

### 第 5 步：使用（对局内打出／触发）

```csharp
var result = await match.PlayManager.PlayUnitAsync(unit, slot);  // 真实打出（部署链）
```

效果行为随后由对局信号驱动（如 `unit.deployed` / `unit.joined`），csx 经受控面 `EffectRuntime.ResolveFor(...)` 执行（`DrawAsync` / `SelectAsync` / `DamageAsync` / `FetchFromDeckAsync` 等）。

---

## 三、共享边界说明（重要）

- **同一卡目录内重复声明同一效果 id 会触发读面软隔离（警告）**——同一目录的装载批次中，同一预制体 id 只保留首次出现，其余条目被隔离并计入 Warnings。
- 因此**共享效果请跨卡目录布置**：本包的 `demo.rally` 由 `cards/`（军需官）与 `cards-support/`（先锋）**各一张**引用，预检维持双零。
- **跨来源冲突**（库 × 内联同 id、内联 × 内联跨卡目录同 id）＝ **Error**（预检器报错）——请保证全包预制体 id 全局唯一。
- 内联效果支持两种写法：**对象态**（推荐）与**字符串态**（JSON 文本；兼容读取形态、**推荐对象态**）——本包两张内联卡各示范一种。

## 四、命名与文本规范

- **命名**：统一 `demo.` 前缀（卡 id 与效果 id 一致族）；文件名遵循分发契约——卡 `<id>.card.json`、效果 `<root.id>.prefab.json`；前缀可按需替换（硬性底线：不得与官方语料/既有 id 冲突）。
- **文本**：UTF-8 无 BOM、LF 换行、2 空格缩进、尾随换行（与工具落盘产物同风格）。
- **版本**：卡数据体 `schemaVersion = 1`；效果快照 `schemaVersion = 2`。

## 五、基于本包创作自己的卡包

1. 复制 `card-pack-demo/` 为你的包目录。
2. 全局替换 `demo.` 前缀为你的前缀（避免与官方/既有 id 冲突）。
3. 增改卡文件与效果文件——注意保持**跨目录共享边界**（每个卡目录内的效果 id 引用不重复）。
4. 跑预检（`CardDistributionValidator.Validate`）——确认 Errors / Warnings 双零。
5. 按五段管道接入你的宿主程序（可对照演示测试逐步照做）。

## 六、如何运行演示测试

```bash
dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --filter "FullyQualifiedName~CardPackDemoTests"
```

成功的样子：该测试类全部通过——预检双零、五卡装载、注册与初始化、打出与效果行为断言（含定向取卡「卡组 -1 / 手牌 +1」）。

## 七、延伸阅读

- 写方向落盘（把内存数据写出为分发文件）：`CardDataWriter` / `PrefabWriter`；
- 效果编译驱动（卡面文本/DSL → 编译 → 打包 `*.prefab.json`）：`EffectCompilationDriver`。
