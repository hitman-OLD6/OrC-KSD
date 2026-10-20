# Implementation — 卡牌资产元数据管理与后端编辑器

> **交接状态**：需求已定稿（R1–R17 全部 Confirmed）；实现 grill **已完成**（I1–I10 全部裁定）；**S1–S10b 全部 Done**（2038 测试通过 / 0 失败 / 构建 0 警告 0 错误）。遗留边界见文末「已知边界与遗留」。
> **执行顺序**：S2 → S3 → S4 → **S10a（前置）** → S5 → S6 → S7 → S8 → S9 → S10b。
> **新会话起手**：读「诊断分级」「资产域约束」「代码基线记录」三节 → 按执行顺序逐步实现，每步交付前对照 Acceptance 核对。
> 决策全集见 `requirements-grill-plan.md`（D1–D61）；未决实现问题见 `implementation-grill-plan.md`（I1–I10）。

## Scope

- Target：`src/Orc`（版本/元数据基础类型）、`src/Orc.Packages`（元数据注册、lock、资产加载器、包扫描）、`src/Orc.Game`（卡牌加载器触发器、三档套件、中间层、对局接线）、`src/Orc.Mcp`（`editor.*` 工具与 `AssetEditor`）、`samples/*`（迁移）、`tests/*`。
- Excludes：自动补全与随机卡生成、前端 UI、卡图二进制、网络多人。

## Open Decisions（grill 中）

| 编号 | 议题 | 状态 |
| --- | --- | --- |
| I1 | `AssetVersion` N 段化的类型形态与等价归一 | **已定**（原地放宽＋规范形右补 0） |
| I2 | 版本级注册表与 `CardLibrary` 的关系 | **已定**（新增 AssetRegistry，CardLibrary 不感知 GUID） |
| I3 | 现有 `CardPackLoader`/`CardDataLoader` 去留 | **已定**（保解析原语、删 `Load`、抽 `PackageScanner`） |
| I4 | 包级 `dependsOn` 退役与包版本类型（hash 串） | **已定**（SelectHighest 退役、约束保留、插件走 LoadAfter[]） |
| I5 | 卡牌加载触发器的挂载点与命名 | **已定**（直调＋两层注册面，专用视图） |
| I6 | 中间层与卡组/效果引用的改造范围 | **已定**（`pack/{id}/{局部ID}` 不含 kind；歧义与多版本分离） |
| I7 | 测试资产的实例化隔离 | **已定**（实例级域标记＋读面，不设对局开关） |
| I8 | 编辑器写路径与 `SandboxPathGuard` | **已定**（`IEditableRootPolicy` 纯判定＋父目录授权） |
| I9 | 迁移工具形态 | **已定**（库函数＋`tools/Orc.Tools.Migrate`，默认 dry-run） |
| I10 | 报告 schema 定型 | **已定**（独立 `CardLoadReport`，键名冻结） |

**实现 grill 已收口**：I1–I10 全部裁定并落进 S2–S10b。无遗留「it depends」。

## 诊断分级（R5 门禁实现依据）

包级与资产级的判定边界如下；**两级不得互相借用**（消除「Warnings≠0 即拒包」的旧语义）。

| 级别 | 判定项 | 后果 |
| --- | --- | --- |
| 包级拒绝（结构性） | manifest 不可解析／缺必填字段／`schemaVersion` 不支持 | 整包不注册，进宿主失败明细 |
| 包级拒绝（结构性） | sidecar 不可解析／缺必填字段 | 整包不注册 |
| 包级拒绝（结构性） | 同一 GUID 在本包内重复声明 | 整包不注册 |
| 包级拒绝（结构性） | lock 文件缺失／不可解析 | 整包不参与加载 |
| 包级拒绝（结构性） | 两份 lock 间同资产不同版本 | 报错并指名双方 |
| 资产级报告（不阻断包） | sidecar 与源文件 `id` 不一致 | 该资产非法（R2），不生成 |
| 资产级报告（不阻断包） | 源文件缺失（孤儿 meta） | 不注册，进报告 |
| 资产级报告（不阻断包） | 字段缺失 | 按三档判定（D40 表） |
| 资产级报告（不阻断包） | 卡图／描述缺失 | 警报，可玩 |
| 资产级报告（不阻断包） | 版本号非法 | 该资产非法，不生成 |

- **待删除点**：`src/Orc.Packages/CardPackLoader.cs:59-64` 的 `Errors==0 && Warnings==0` 双零门整体移除；`CardPackLoader.cs:48-52` 的 `cards/` 目录存在性强校验移除。
- **唯一保留的目录约定**：`versions/<version>/`（R3 授权的自动扫描）。

## 资产域（AssetDomain）约束

- 域标记**随实例走、不可在加载后丢弃**：卡实例（或等价载体）携带 `AssetDomain { Formal, Test }`；`CardLibrary` 条目同样带域（与 `Replace(..., domain)` 同源）。仅靠 `ContainsTestAssets` 聚合计数不足以判定「这张卡是不是测试版」。
- 域**不得参与任何玩法逻辑判定**：效果、触发器、数值计算一律不得读取 `IsTestAsset`。域的合法消费方只有三类——构筑/存档、UI 展示、诊断报告。若某处玩法逻辑需要区分域，视为需求缺口，回退讨论，不得就地加判断。
- 未来实现存档/回放时域标记必须一并持久化，**禁止只存 appId**（否则读档后测试卡被当作正式卡）。本条现在只作文档约束，不实现存档层。
- **不设对局级开关**（不引入 `AllowTestAssets`）：正式/测试由「是否合并测试 lock」自然决定；测试 lock 不存在即无测试资产，开关不增加安全性，反而制造「谁有权定义正式对局」的双真源。编辑器预览同样无需开关。

## 代码基线记录（防臆造）

- **仓库无卡组持久化层**：全仓无 `deck.json` / `SaveDeck` 之类实现；卡组＝外部传入的 id 列表（`MatchLaunchOptions.cs:7-9`），"构筑"仅指玩家主国/盟国配置（`Player.cs:239-249`）。后续步骤不得臆造卡组存档落点。
- `TriggerRegistry` 注册与挂载完全解耦（`TriggerRegistry.cs:6-7`）：未注册触发器照常运行，注册纯记账。

## Implementation Steps

### S1: 术语与需求基线
- Status: Done
- Target：`CONTEXT.md`、`docs/资产元数据与后端编辑器/*`
- Approach：新术语落表；`资产名` 改分型；包版本旧语义退役；需求 R1–R16 定稿。
- Acceptance:
  - [x] CONTEXT 含全部新词条与 `_Avoid_`
  - [x] requirements.md 无 Draft
- Rationale：术语先行，避免实现期命名漂移。
- Terminology: [资产](../../CONTEXT.md)、[资产版本](../../CONTEXT.md)

### S2: 资产身份与元数据模型
- Status: Done
- Target：`src/Orc/Core/AssetNaming.cs`（`AssetVersion` 原地放宽）、新增 `AssetIdentity`/`AssetMetadata`、`src/Orc.Packages/AssetMetadataJson.cs`（sidecar 读写）、`PackageManifest`（v2）
- Excludes：**包版本类型不在本步改造**——`PackageManifest.Version` 的换型（`AssetVersion` → `PackageVersion`＝内容指纹串＋`-tmp`）归 S4/I4；S2 不得顺手改 manifest 的 version 字段类型或 `AssetVersionConstraint`。
- Approach：（I1 已定：原地放宽单一类型）
  - `AssetVersion` 内部改存 1..8 段 `int[]`（构造时拷贝、对外不可变）；保留 `AssetVersion(int,int,int)` 构造与 `Major/Minor/Patch` 读属性（≡ `SegmentAt(0..2)`，缺失段返回 0）；新增 `FromSegments(ReadOnlySpan<int>)`、只读 `Segments` 视图、`SegmentCount`。
  - 相等/哈希/比较一律按**规范形＝右补 0 至 8 段**：`1.2` ≡ `1.2.0`，`Equals` 与 `GetHashCode` 必须一致（registry 以版本作字典键）。
  - `ToString()` 保留书写段数（不剥尾 0）——R12 要求新建资产展示 `1.0.0`；**硬约束：任何跨文件/跨结构的版本比对禁止字符串比较**（sidecar/lock 写出原样写段数，读入后靠规范形归一）。
  - `AssetKind{Card,Effect,Trigger,Handler}`；GUID 派生＝f(包 ID, kind, 局部 ID, 规范形版本)。
  - sidecar DTO `{schemaVersion, packageId, source, assets[]}`；读宽写窄；文件名 `<源名>.meta.json`。
  - manifest v2 `{schemaVersion, id, guid, kind, name?, version, metadata[]}`；`dependsOn` 退役。
    - **字段分工（D50 遗漏补全）**：`id` 人类可读，用于应用层前缀 `pack/{id}/` 与目录名；`guid` 稳定身份，用于资产名 `@{包GUID8}` 与 GUID 派生输入。二者都必填——D31 允许同 ID 包并存，仅凭 `id` 无法唯一标识资产名。
    - 现有 `PackageManifest` 已有 `Id`/`Guid` 双字段，无需新增存储；v2 只是把它们从「可选共存」升为「均必填」。
- Acceptance:
  - [x] N 段版本比较/递增符合 D26（含补 0、增段、降级拒绝）
  - [x] `1.2` 与 `1.2.0` 比较相等**且** `GetHashCode` 相等
  - [x] `AssetVersion(1,2,3)` 的 `Major/Minor/Patch` 与 `FromSegments([1,2,3,4,5])` 前三段一致，第 4 段起经 `Segments` 可见
  - [x] `ToString()` 保留书写段数（`1.0.0` 不被写成 `1`）
  - [x] GUID 不含包版本，升资产版本才换 GUID
  - [x] sidecar 读写往返语义保真、文本规范
  - [x] manifest v2 解析/写出与列表校验（S2 内未换型；现行 `PackageVersion` 由 S4 换入）
  - [x] manifest v2 的 `id` 与 `guid` 均必填；缺 `guid` ⇒ 结构性拒绝（包级）
- Rationale：身份是一切下游（注册/锁/中间层/编辑器）的输入，必须先定；单一类型避免两套相等语义在 GUID 派生与字典键上分叉。
- Terminology: [资产](../../CONTEXT.md)、[资产名](../../CONTEXT.md)、[资产版本](../../CONTEXT.md)

### S3: 资产注册表与中间层
- Status: Done
- Target：新增 `AssetRegistry`（键＝GUID）、`AssetIdBridge`（应用层 ID→GUID）、改造 `RuntimeCardCatalog`（底层字典转接）、`CardLibrary`（新增 `Replace`）
- Approach：（I2 已定：注册/定版/实例化三职责分离）
  - `AssetRegistry`（进程级）：**只做注册面**——GUID 键存「元数据 ＋ 已生成 DTO」，二级索引 `(包ID, kind, 局部ID) → 有序版本列表`；**不做版本选择**（对外只给 `VersionsOf(...)` 候选）。
  - `AssetIdBridge`：**不持有 DTO**，只持有 `(kind, 应用层 ID) → GUID` 映射；正式域/测试域各一张表；定版时向 `AssetRegistry.VersionsOf(...)` 取候选，再按 lock 与遮蔽规则定版。
  - **应用层 ID 形态（I6 已定）**：固定 `pack/{包id}/{局部ID}`，**不含 kind**；kind 是并列字段，跨类型引用的一等键＝`(kind, appId)` 二元组（卡与效果可同局部 ID；把 kind 压进字符串会破坏 UI 冻结的前缀契约）。
  - **引用书写三档**：①同包引用（数据体内 `effects.prefabs`、`BecomesVeteran:/VeteranOf:`）写裸局部 ID，注册期由「当前包」上下文补 `pack/{包id}/`；②跨包引用必须写全名；③无包上下文入口（卡组、MCP 工具、编辑器）两种形态皆可，裸 ID 可用前提是它只对应**一个应用层 ID**。
  - **歧义判定 vs 多版本（不得混用）**：候选去重键是**应用层 ID 字符串**（非 GUID）；去重后 ≥2 个不同应用层 ID ⇒ 歧义（报错并列候选）；去重后只剩 1 个应用层 ID 但对应多版本 ⇒ **不是歧义**，交 lock／中间层定版（同 ID 两包产生相同应用层 ID 时由 lock 裁决，版本不同即 lock 冲突）。
  - `MatchContentLoader.ResolveCardId` 是临时实现：**在 S7 接线时删除**，解析逻辑迁入 `AssetIdBridge`；仓库内不得留第二份解析实现。
  - `RuntimeCardCatalog`：快照语义原样保留（跨对局冻结、冻结时机不变）；底层字典改由 `AssetRegistry` 承载（写入口转调、读面签名保持兼容），**不新增第三套字典**。
  - `CardLibrary`：**不感知 GUID**，键始终是应用层 ID；新增 `Replace(应用层ID, definition, domain)` 专供测试段遮蔽——只允许替换已存在的键，并记录 `domain ∈ {Formal, Test}`（供构筑/存档区分）；`Register` 的重复即抛语义不变。
  - 查询面支持 lambda 与结构化过滤（R9）。
  - **新旧链路接替（I3 已定）**：`CardDataLoader`（单文件 `*.card.json` → DTO，逐文件隔离）**原样保留**为解析原语，由注册面调用；`CardPackLoader.Load` **删除**、不设双轨；其「发现包／枚举包目录」职责抽为独立 `PackageScanner` 供 `EngineHost` 使用，与注册逻辑解耦。
  - 应用层 ID 前缀 `pack/<包ID>/<局部ID>`（UI 冻结契约）在**注册期生成 DTO 时**拼定，位置从旧装载器上移到注册面。
- Acceptance:
  - [x] 同资产多版本并存注册；`AssetRegistry` 无任何定版逻辑
  - [x] `AssetIdBridge` 不持有 DTO（全仓只有注册面持有一份 DTO）
  - [x] 应用层 ID 在单域内只解析到一个版本；测试遮蔽不污染正式域
  - [x] `RuntimeCardCatalog` 快照仍在「对局初始化时刻」冻结，时机不变、无第三套字典
  - [x] `CardLibrary.Replace` 只替换已存在键（缺失键＝拒绝），并记录 domain
  - [x] `CardPackLoader.Load` 已删除；包扫描经 `PackageScanner`；无 sidecar 的卡走「无元数据」三档而非整包失败
  - [x] 门禁按「诊断分级」两表执行（不存 `Warnings≠0 即拒包` 路径）
  - [x] bridge 以 `(kind, appId)` 二元组为键；应用层 ID 不含 kind
  - [x] 歧义（≥2 个不同应用层 ID）与多版本（同一应用层 ID 多版本）判定分离，各有用例
  - [x] 全仓仅一份引用解析实现（S7 删除 `MatchContentLoader.ResolveCardId` 后无第二份）
    - 收口（S7 已完成）：`Orc.Mcp/Match/MatchContentLoader.cs` 整文件删除；卡组解析改经 `AssetIdBridge.Resolve(AssetKind.Card, …)`。
      静态守卫＝`tests/Orc.Mcp.Tests/CardIdResolutionSingleSourceTests`。
- Rationale：把「注册」「定版」「实例化」彻底分成三个单一职责（D10），让卡组不受升版影响（R14），并把对局层改动压到最小（不改键、不识 GUID）。
- Terminology: [中间层](../../CONTEXT.md)、[注册](../../CONTEXT.md)、[应用层 ID](../../CONTEXT.md)

### S4: lock、包版本去解析化与插件装载序
- Status: Done
- Target：新增 `AssetLock`（正式/测试两域）、`LockMerger`、`PackageVersion`；改造 `PackageManifest.Version`、`PackageResolver`、`EngineHost` 的版本面与插件装载序
- Approach：
  - lock：读/写两文件；合并＝按域分别合并；同一资产不同版本⇒结构化错误；未入 lock 的版本不参与；lock 不含包版本。
  - **包版本去解析化**：`PackageVersion`＝`hash(包 ID ＋ 包内元数据)` 串 ＋ `-tmp` 标记，替换 `PackageManifest.Version`；
    `PackageResolver.SelectHighest`（`PackageResolver.cs:185-204`，按 `Manifest.Version.CompareTo` 择最高版）**退役**——包版本不再参与任何择版；
    `EngineHost.Find(id, AssetVersion)`（`:60/122`）、`TryUnloadCardPack(id, AssetVersion)`（`:372`）与 `AssetCatalogSnapshot.VersionLocks`（`:127`）改为按包版本串（内容指纹）寻址，`VersionLocks` 改为 `id@指纹` 的审计记录。
  - `AssetVersionConstraint` **保留并复用**：D47 规定资产级 `dependsOn` 的引用形态＝「kind ＋ 应用层 ID ＋ 版本约束」，约束解析/匹配逻辑沿用；退役的只有它在**包级 dependsOn** 上的用法。
  - **manifest `dependsOn` 读宽写窄**：读时**忽略并产出一条迁移告警**（不报错，旧 manifest 仍可解析）；写时不再产出该字段。
  - **插件装载序**：由 `BuiltInPluginPackage` 的注册清单为每个内置包声明 `LoadAfter[]`（纯包 ID、代码内、**不进 manifest**）；`EngineHost` 启动期断言三项——①显式序覆盖全部已发现插件；②无环；③无未知包 ID；任一不满足即启动失败并指名。
- Acceptance:
  - [x] 两文件格式与键序稳定
  - [x] 跨域绝不互相合并
  - [x] 冲突报告指名资产与两版本
  - [x] 包版本为内容指纹串，同一内容同指纹、不同内容不同指纹，且不含时间戳
  - [x] `SelectHighest` 已退役；`AssetVersionConstraint` 仍被资产级依赖复用（非退役）
  - [x] 含 `dependsOn` 的旧 manifest 可正常解析（仅迁移告警），新写出的 manifest 不含该字段
  - [x] 插件装载序由 `LoadAfter[]` 决定，三项启动断言均有用例覆盖
- Rationale：lock 是「正式版」的唯一表达（D6）。D31 的核心意图（废弃包级 dependsOn、同 ID 包可并存、资产冲突才拒）完整保留；「插件装载序由资产拓扑决定」是推导而**非意图**，且插件无 lock/sidecar，「资产拓扑」在其形态上无定义——故以 `LoadAfter[]` 显式装配声明落地，并保留可逆路径（若将来要求插件统一走资产拓扑，则为插件造 sidecar ＋ 冷加载拓扑）。
- Terminology: [正式 lock](../../CONTEXT.md)、[测试 lock](../../CONTEXT.md)

### S5: 资产加载器与加载列表
- Status: Done
- Target：新增 `AssetLoader`（分层/缓存/增量重载/门禁）、`LoadList`
- Approach：lock 集合 → 注册表取版本 → Kahn 分层（禁环）→ 层内 ID 序 → 缓存；新列表比层，首个变更层起重装；门禁判定器 `reloadGate`；失败全有或全无。
  - **计算与装载分离**：`LoadListPlanner.Plan(registry, locks)` 是纯函数（依赖检测／Kahn 分层／层内序一次算完，运行期无依赖查找），产出 `AssetLoadPlan{Formal, Test, Issues}`；
    `AssetLoader` 只承担缓存 ＋ 层比对（`LoadList.FirstChangedLayer`）＋ 门禁 ＋ 落地与回滚。
  - **两域两条列表**：正式／测试分别合并 lock、分别分层、分别算差量（`AssetReloadDelta`），装载序仍是「先正式后测试」（R15）。
  - **装载动作缝**：`IAssetLoadSink.Apply/Revert`——S5 只定契约与回滚语义，S6 的卡牌加载器实现它；失败＝抛异常，加载器撤销新段并恢复旧段。
  - **内含资产**：`AssetRegistration.IsEmbedded`（`anchor != null`）不入拓扑节点，挂在宿主条目 `LoadListItem.Embedded` 上；依赖内含资产者连到其宿主（宿主＝同包同源文件中 anchor 为 null 的注册）。
  - **域规则**：正式资产依赖测试资产 ⇒ `FormalDependsOnTest` 剔除；测试资产依赖未入正式 lock 的正式资产 ⇒ `TestDependsOnUnlockedFormal` 剔除（不整段失败）；依赖缺失连带剔除依赖者（`DependencyInvalid`）。
  - **门禁**：`ReloadGateJudicator`（判定器名 `reloadGate`）返回 `ReloadVerdict{Allowed, Reason}`；仅重载过门禁（首次装载与「无变更」不过）；门禁未注册＝无门禁，门禁抛错⇒结构化拒绝（加载器不抛错）。
- Acceptance:
  - [x] 分层满足「第 N 层只依赖 ≤N-1 层」
  - [x] 环依赖报错含环路径
  - [x] 增量重载从首个变更层起、失败回滚
  - 验收证据：`tests/Orc.Packages.Tests/LoadListPlannerTests.cs`（14 例：分层只依赖低层／层内局部 ID 序／环路径闭环含 `→`／自依赖成环／内含资产不建节点且随宿主同层／依赖内含资产连到宿主／孤儿内含走报告／正式依赖测试判非法／测试依赖未入正式 lock 只判该资产非法／版本约束不符只报告／依赖缺失连带／未注册不参与／lock 冲突结构性失败／跨域注册不混入）
    与 `tests/Orc.Packages.Tests/AssetLoaderTests.cs`（11 例：首装全量／首变更层增量／第 0 层变更整表重载／无变更不重装／装载失败回滚为旧列表且可重试／门禁拒绝结构化／门禁请求携带首变更层与条目／未注册门禁放行／移除条目摘除／两域差量独立／结构性非法不装载）。
    实现：`src/Orc.Packages/LoadList.cs`（`LoadList`／`LoadListItem`／`LoadListLayer`／`AssetLoadPlan`）、`LoadListPlanner.cs`（依赖检测＋Kahn＋环路径＋域规则＋问题码）、`AssetLoader.cs`（缓存／增量／回滚／门禁调用／`IAssetLoadSink`）、`ReloadGate.cs`（`ReloadGateJudicator`／`ReloadVerdict`）。
- Rationale：拓扑与装载顺序一次算完（D10），避免运行期依赖查找。
- Terminology: [加载列表](../../CONTEXT.md)、[加载](../../CONTEXT.md)

### S6: 卡牌加载器与三档套件
- Status: Done
- Target：新增 `CardLoaderRegistry`（进程级）、`CardLoaderTrigger : Trigger<CardLoadBatchView>`、`CardLoadBatchView`、`CardValiditySuite`、报告模型
- Approach：（I5 已定：直调＋LowLevel 登记；两层注册面不混用）
  - **进程级（信任边界）**：插件声明加载器 → 登记进 `CardLoaderRegistry`（挂 `AssetRegistry` 的 loader 子面）；manifest/sidecar 的 `loader` 字段只能引用此处已注册的名字，「卡包自带加载器 ⇒ 拒绝」在本层硬校验。
  - **对局级（审计面）**：对局初始化时把本局实际使用的加载器实例登记进 `TriggerRegistry(layer=LowLevel)`——仅记账、不参与挂载（`TriggerRegistry.cs:6-7` 明示注册与挂载解耦）。
  - **触发通道**：不挂总线，唯一入口 `LoadBatchAsync(items, ct)` 返回「报告＋引用列表」；批量＝一次调用，单卡＝一元清单。因 `TriggerRegistryEntry` 构造为 `internal`，加载器必须是 `Trigger<TView>` 子类，故定义专用视图 `CardLoadBatchView`（承载整批清单与报告收集器），**不复用现有事件视图**——复用会让「加载是命令不是事件」在类型上失守。
  - 套件三档（D40 判定表）；缺失字段填类型默认值；非法不生成；牌堆非法位留空；报告按资产去重＋引用计数。
  - **报告 schema（I10 冻结，禁止改名）**：

    ```
    CardLoadReport {
      schemaVersion: 1,
      batchId: string,
      items: [{ assetGuid, assetName, appId, kind, domain: "formal"|"test",
                tier: "valid"|"warning"|"invalid",
                fields: [{ path, expectedType, defaultValue, reason }],
                refCount: int }],
      slots: [{ playerId, index, appId, assetGuid?, tier, fieldPaths: [] }],
      summary: { total, valid, warning, invalid, totalRefs }
    }
    ```

    - `reason` 为**枚举**（禁止自由文本）：`missing | wrongType | outOfRange | sourceMissing | sidecarMissing | idMismatch | versionInvalid | loaderUnregistered | conflict | duplicateGuid`。
    - `slots` 必备：每个牌堆卡位一条（`assetGuid` 解析失败时可为 null）——否则无法诊断「第几个卡位为什么空了」。
    - `batchId` 仅作「同一次加载的多条日志」关联用，**不承载时间戳语义**（取会话内单调序，保证测试断言稳定）。
    - 报告**不落盘**（R5），只经返回值与工具返回。
  - 既有 `CardDistributionValidationReport` 保持二分结构不动，与新报告**不合并、不互转**（静态预检 vs 运行期装配，语义不同）。
- Acceptance:
  - [x] 三档判定逐项符合 D40
  - [x] 报告字段与键名严格符合冻结 schema（camelCase、`reason` 仅取枚举值）
  - [x] 每个牌堆卡位在 `slots` 有且仅有一条（含留空位）
  - [x] `batchId` 不含时间戳、可稳定断言
  - [x] 报告不落盘；`CardDistributionValidationReport` 未被改动
  - [x] 非法卡不生成、开局继续
  - [x] `loader` 引用未注册加载器 ⇒ 结构性拒绝；卡包自带加载器 ⇒ 拒绝
  - [x] 本局使用的加载器可在 `TriggerRegistry(layer=LowLevel)` 查到（仅记账，不挂总线）
  - [x] `CardLoadBatchView` 为专用视图类型，未被复用于任何事件语义
  - [x] 加载段把 `AssetDomain` 写入卡实例与 `CardLibrary` 条目，实例可查 `IsTestAsset`
  - [x] 全仓无任何玩法逻辑读取域标记（静态检索为证）
  - 验收证据：`tests/Orc.Game.Tests/CardValiditySuiteTests.cs`（23 例：完整卡合法／D40 非法七项逐条〔id、factionCost、typeCategory、单位 battleStats、指令缺 effects、反制空 effects、effects 引用不存在、版本号非法〕／D40 警报五项逐条〔name、卡图缺失、卡图文件不存在、描述、tagData、未知词条带下标〕／id 与 sidecar 不一致／源文件缺失／sidecar 缺失／卡级聚合／多警报仍可玩／无原文回退 DTO／插件追加项不改骨架／档位与原因 token 冻结）
    与 `tests/Orc.Game.Tests/AssetDomainTests.cs`（4 例：实例随身域＋实例化读域／Replace 改域／Remove 幂等摘除／**全仓静态检索**域标记只出现在授权消费方）
    与 `tests/Orc.Packages.Tests/CardLoaderTests.cs`（20 例：按资产去重＋引用计数／slots 完整性含留空位／非法不生成且开局继续／警报卡按默认值生成／空批次／报告 JSON 键序与取值词表／字段条目四键与 `reason` 只取枚举／`batchId` 单调且无时间戳／未注册 loader 结构性拒绝／已注册 loader 放行／卡包自带 loader 在注册面拒绝／同名重复声明拒绝／LowLevel 记账／无总线可直调／`Apply` 写域进条目与实例＋幂等＋`Revert` 摘除／报告 domain token 随域／样例包端到端全无非法／报告不落盘的静态证据／专用视图未被复用／旧 `CardDistributionValidationReport` 形状冻结）
    实现：`src/Orc.Game/Cards/Loading/`（`CardValiditySuite`／`CardValidityInput`／`CardValidityContext`／`ICardValidityCheck`／`CardFieldIssue`／`CardIssueReason`／`CardLoadTier` 及各 token）、`src/Orc.Packages/Loading/`（`CardLoaderTrigger`／`CardLoadBatchView`＋`CardLoadBatchOutcome`／`CardLoaderRegistry`＋`CardLoaderDescriptor`＋`CardLoaderRefusedException`／`CardLoadReport`＋`CardLoadReportJson`／`CardLoadItem`＋`CardReference`＋`CardLoadResult`）、`src/Orc.Packages/AssetRegistry.cs`（`CardLoaders` 子面、`AssetRegistration.PackageGuid`／`AssetName`）、`src/Orc.Game/Cards/CardDefinition.cs`（`CreateForLoading` 补全构造）、`CardBase`／`CardLibrary`（域随身与 `Remove`）
    - 分层说明：加载器需 `AssetRegistration`（`Orc.Packages`）⇒ 触发器与报告落在 `Orc.Packages/Loading`（依赖方向 `Orc.Packages → Orc.Game` 不允许反向）；三档骨架留在 `Orc.Game`（引擎内置、与资产身份解耦）。
    - 未调和冲突（D40 与既有代码行为）：①`name` 缺失＝警报 vs `CardDefinition` 主构造「名称非空白」fail-fast ⇒ 以 `CardDefinition.CreateForLoading` 单开加载期入口（既有路径行为不变）；②`tagData` 缺失＝警报 vs `CardDefinition` 必填 `tagData` ⇒ 套件合成默认组件（`standard`）。

- Rationale：把加载做成引擎内一等公民（R5），使加载可被观察与编辑；`LowLevel` 在本仓只要求登记分类，故直调即正解而非妥协。
- Terminology: [卡牌加载器](../../CONTEXT.md)、[合法性检测套件](../../CONTEXT.md)

### S7: 对局接线
- Status: Done
- Target：`src/Orc.Game/Match/Match.cs`、`Cards/CardLibrary.cs`、`Cards/CardBase.cs`、卡组装载
- Approach：初始化改为「资产加载器产出列表 → 卡牌加载器整批 → 实例化按需」；`CardLibrary` 键经中间层解析；局内新增卡走单卡调用；实例化不做依赖解析。
  - **时序硬约束**：整批加载阶段只做解析、校验、DTO 生成，**不分配任何实例 ID**；实例化仍在 `LoadDecksAsync` 内原位进行，`Match.cs:737` 的 ID 水位线快照时机与顺序不变。新链路替换的是「卡从哪来、是否合法」，不是「何时实例化」。
  - **分层解法**：`Orc.Game` 不得引用 `Orc.Packages` ⇒ 对局层只声明端口 `IMatchCardLoader`（`Cards/Loading/MatchCardLoader.cs`：`MatchCardSlot`／`CardSlotOutcome`／`CardLoadOutcome`），实现＝`Orc.Packages/Loading/MatchCardAssembly`（注册面 → 两域 lock → `AssetLoader` 分层列表 → `CardLoaderTrigger` → 卡库）。`Attach(library, triggers)` 由 `Match.Initialize` 在卡库创建后调用。
  - **两条链路，一条时序**：提供端口 ⇒ 整批装载双方牌堆（`MatchCardLoaderWiring：一次调用`）并按卡位结局**原位剪除留空卡位**、条目规范为应用层 ID；未提供 ⇒ 代码内定义集直注册（独立装配/测试路径，语义与接线前一致）。
  - **`Apply` 装的是套件补全后的定义**（`Evaluate` 按 GUID 记忆判定、`Apply` 与批次共用同一实例）：警报卡不再以缺字段的注册面原始 DTO 进卡库；非法卡不落条目。S5 的 sink 契约未改。
  - **删除 `MatchContentLoader`**：`Orc.Mcp/McpService.MatchLaunch.cs` 改用 `MatchCardAssembly`；卡组解析经 `AssetIdBridge.Resolve(AssetKind.Card, …)`（解析唯一实现）。
  - **局内新增卡**：`MatchCardService.CreateAsync` 先走一元清单 `LoadBatchAsync`（同一入口），端口把本批产出的卡写回中间层；`RegisterCard` 的运行期直注册卡（无资产身份）按记账集合识别、不参与资产解析。
- Acceptance:
  - [x] 双方牌堆统一整批加载
    - 证据：`tests/Orc.Game.Tests/MatchCardLoaderWiringTests.Both_Decks_Are_Loaded_In_A_Single_Batch`（12 卡位、一次调用、玩家 0 全在前）；`tests/Orc.Packages.Tests/MatchCardAssemblyTests.Both_Decks_Load_In_A_Single_Batch_And_Watermark_Stays_After_Deck_Loading`（真实链路：`LastReport.Slots`＝6＝双方合并）
  - [x] 局内 token/升级走加载路径并更新中间层
    - 证据：`MatchCardLoaderWiringTests.In_Match_New_Card_Uses_Single_Card_Batch`（一元清单、归属玩家标识）；`MatchCardAssemblyTests.In_Match_Card_Uses_Single_Card_Batch_And_Syncs_Bridge`（单卡批次后 `Bridge.TryGet` ＝ 注册面 GUID、卡落库并可实例化、ID＞水位线）；`In_Match_Card_Resolution_Goes_Through_Bridge_Only`（清空中间层 ⇒ 明确失败，不回退注册面扫描）
  - [x] 实例化路径无依赖查找
    - 证据：`MatchCardLoaderWiringTests.Instantiation_Never_Consults_The_Loader`（关闭装载面后 `CardLibrary.Instantiate` 照常）；`MatchCardAssemblyTests.Instantiation_Does_Not_Consult_Bridge_Or_Registry`（`Bridge.Clear()` 后实例化照常）；`MatchCardAssemblyTests.Both_Decks…` 末尾（Initialize 后清空中间层仍可实例化）
  - [x] ID 水位线快照仍在校牌加载之后原位执行（提前即视为回归）
    - 证据：`MatchCardLoaderWiringTests.Batch_Allocates_No_Instance_Id_And_Watermark_Is_Snapshotted_After_Deck_Loading`（批次窗口内 `AssemblyPlayerManager` 为 null ⇒ ID 提供器不可达；Initialize 后水位线＝2×牌堆＝16，构筑内全为「非构筑外」，随后新建卡为「构筑外」）
  - [x] S3 遗留项收口：全仓仅一份引用解析实现
    - 证据：`tests/Orc.Mcp.Tests/CardIdResolutionSingleSourceTests`（`MatchContentLoader`／`MatchContentLoadResult` 类型不存在；源码零 `ResolveCardId` 命中；`AssetIdBridge.Resolve` 恰两条重载；对局层程序集不含注册面类型）
- Rationale：这是需求与现有装配顺序冲突最集中的一处（C4/C19），单独成步便于回归；水位线语义（构筑外判定）挂在实例化顺序上，故必须显式钉住。
- Terminology: [卡牌加载器](../../CONTEXT.md)

### S8: 后端编辑器 `AssetEditor`
- Status: Done
- Target：新增 `src/Orc.Packages/Editing/*`（会话/临时包/提交/版本/依赖/转正/历史）、`src/Orc.Packages/Fs/IEditableRootPolicy`、`PathNormalizer`（由 `SandboxPathGuard` 抽取）
- 分层约束：**`Orc.Packages` 不得引用 `Orc.Mcp`**。做法＝把 `SandboxPathGuard` 的路径归一逻辑（realpath 归一、拒绝 UNC/`..`、符号链接解析、大小写归一）抽为可复用工具 `PathNormalizer`，`SandboxPathGuard` 本身改为 `IEditableRootPolicy` 的一个实现，规则只有一份。
- Approach：（I8 已定：策略下沉注入）
  - `IEditableRootPolicy.Approve(absolutePath)` 为**纯判定**，授权根在构造时固定，**会话期不得扩权**（这正是排除"多根白名单"方案的理由）。
  - 判定规则：先 realpath 归一 → 再要求**目标路径的直接父目录**命中授权根之一（仅前缀匹配会被 `<root>/../../etc` 与符号链接绕过）。
  - `AssetEditor` 构造函数**强制注入策略**，不提供无策略重载或默认实现；缺策略即编译期不允许构造。沙盒模式由宿主注入单根实现，非沙盒由前端配置根列表。
  - 临时包：复制包到 `<包目录>.tmp/`（同级兄弟，且其父目录须经 `Approve`）；**`<包目录>.tmp` 已存在时直接失败并指名**，不得清理或复用他人残留。
  - 会话单例；提交双通道；测试 lock 自动重算；转正去测试段并递增、写两 lock、当前版本移入 `versions/<v>/`；放弃删目录。
- Acceptance:
  - [x] 会话互斥、临时包位置正确
  - [x] 版本递增校验与默认 +1
  - [x] 转正产出新版本＋两 lock＋历史目录
  - [x] 放弃无残留
  - [x] `AssetEditor` 无无策略构造路径（编译期即禁止）
  - [x] 越界路径（UNC／`..`／符号链接逃逸／父目录未授权）一律拒绝
  - [x] 既有 `<包>.tmp` ⇒ 创建失败并指名，不清理不复用
  - [x] 路径归一规则全仓仅一份实现（`PathNormalizer`），`Orc.Packages` 无对 `Orc.Mcp` 的引用
  - 验收证据：`tests/Orc.Packages.Tests/AssetEditorTests.cs`（31 例：会话互斥且放弃后可重启／临时包为同级兄弟／既有 `.tmp` 失败并指名且残留未被清理／临时包父目录未授权拒绝／`pack.list` 不列 `.tmp`／提交默认追加测试段／`version.set` 拒绝降级·相等·补 0 后相等·格式非法／默认递增尾段 +1／`version.set` 后首次提交不再 +1／版本变更换新 GUID／提交内容通道写盘／文件指针通道读前端直写／UNC·`..`·绝对越界拒绝／符号链接逃逸拒绝／转正产出新版本＋两 lock＋`versions/1.0.0/` 历史＋去 `tmp`／未改动资产不归档（避免 GUID 重复）／转正后包仍可被 `PackageRegistrar` 注册（新旧两版并存）／放弃后正式包字节不变／新建资产从 `1.0.0` 起并进入测试 lock／新建包必须给依赖包 ID／新建包转正生成首个正式 lock／删除资产移出正式 lock 且历史仍在 `versions/`／资产列举 lambda·结构化过滤／lock 预览显示转正版本／依赖写入 test lock 语义·自依赖拒绝／无会话报错／构造必须带策略（反射）／`Orc.Packages` 不引用 `Orc.Mcp`（反射）／归一规则单一来源（源码检索）／无 loader 的 reload 结构化返回／非法 sidecar source 打开失败无残留）
    与 `tests/Orc.Packages.Tests/EditorVersionRulesTests.cs`（9 例／14 用例：测试版本追加段／8 段无法追加／转正去段并递增（`1.2.3.5`→`1.2.4`）／未改动与新资产保持原版本／默认尾段 +1／手写降级·相等拒绝／补 0 后相等拒绝／`1.2`→`1.2.1`·`1.2`→`1.3` 合法／格式非法拒绝）
    与 `tests/Orc.Packages.Tests/PathNormalizerTests.cs`（9 例：UNC／`..`／相对路径拒绝／大小写归一／`Combine` 拒绝越界与绝对路径／策略按直接父目录判定（根本身不受理）／未授权父目录带原因／授权根构造期固定／符号链接逃逸归一后判外）
    与 `tests/Orc.Mcp.Tests/SandboxPathGuardTests.cs`（+2 例：守卫即单根 `IEditableRootPolicy`／符号链接逃逸拒绝）
    实现：`src/Orc.Packages/Fs/PathNormalizer.cs`（`NormalizedPath`／归一与 realpath）、`src/Orc.Packages/Fs/IEditableRootPolicy.cs`（接口＋`EditableRootPolicy`）、`src/Orc.Packages/Editing/`（`AssetEditor`／`AssetEditorSession`／`EditorVersionRules`／`EditorModels`／`AssetEditorException`）、`src/Orc.Mcp/Sandbox/SandboxPathGuard.cs`（改为委托策略的守卫）
    - 能力集（R9）：`pack.list`＝`AssetEditor.ListPackages`；`session.begin/end`＝`BeginSession`／`EndSession`；`asset.list/get/commit/create/delete`＝`ListAssets`（lambda 与 `EditorAssetQuery`）／`GetAsset`／`Commit(path)`＋`Commit(path, content)`／`CreateAsset`／`DeleteAsset`；`version.set`＝`SetVersion`；`dep.set`＝`SetDependencies`；`lock.preview`＝`PreviewLocks`；`promote`＝`Promote`；`reload`＝`Reload`。
    - 未调和（详见交付报告）：①包级依赖 R8 与 D31 退役冲突——新建包的「期望依赖包 ID」只作会话输入（校验＋告警），**不写 manifest**；②`versions/<v>/` 的 sidecar `source` 须带 `versions/<v>/` 前缀（注册面按包根校验 `source` 与文件名一致），否则整包结构性拒绝；③新建资产无正式基线，故以 `1.0.0` 进入测试域（不追加测试段），转正保持 `1.0.0`。
- Rationale：编辑器表达力集中在后端（R12），前端与 agent 只做提交；「授权单位＝父目录 ＋ 纯判定不扩权」是唯一不需要运行时扩权动作又能覆盖 `.tmp` 同级派生的规则。
- Terminology: [后端编辑器](../../CONTEXT.md)、[临时卡包](../../CONTEXT.md)

### S9: MCP 工具与门控
- Status: Done
- Target：`src/Orc.Mcp/Tools/EditorTools.cs`、`src/Orc.Mcp/Tools/DiagTools.cs`（新增 `diag.card_load`）、`ToolRegistration.cs`、`ToolWhitelistTests`
- Approach：`editor.*` 全组 `sandboxOnly`；结构化过滤；`editor.reload` 调资产加载器；保持「非沙盒不登记」双门控。
  - `diag.card_load` 命名对齐现有风格（`diag.report` / `diag.audit_chain` / `diag.entry_points` → 点后 snake_case），档位 `sandboxOnly + readOnly`；返回两部分：**本批报告**（`CardLoadReport`）与**本局累积视图**（按 GUID 合并、累加 `refCount`）。
  - **工具名映射（R9 能力 → 工具，一一对应、无一对多）**：`pack.list`→`editor.pack_list`、`session.begin`→`editor.session_begin`、`session.end`→`editor.session_end`、`asset.list/get/commit/create/delete`→`editor.asset_*`、`version.set`→`editor.version_set`、`dep.set`→`editor.dep_set`、`lock.preview`→`editor.lock_preview`、`promote`→`editor.promote`、`reload`→`editor.reload`（共 13 个）。命名沿用仓库既有「单点前缀 ＋ 点后 snake_case」，与 `diag.*` 同风格。
  - **持有者分层**：编辑器与策略由宿主注入（`McpService.Editor`，策略＝`SandboxPathGuard` 单根）；累积视图模型与合并逻辑只存在于 `Orc.Packages.Loading`（`CardLoadLedger`），由 `MatchCardAssembly`（对局级装配）记录；工具面只读，**不复制任何报告类型**。
  - `editor.asset_list` 的结构化过滤直接构造 `EditorAssetQuery` 并交 `AssetEditor.ListAssets(query)`（与进程内 lambda 走同一后端组件）。
- Acceptance:
  - [x] Authoring 档注册 `editor.*`，Player 档清单不含
    - 证据：`tests/Orc.Mcp.Tests/EditorToolsTests.Authoring_Registers_Every_Editor_Tool_In_Own_Group`（13 个工具集＝R9 能力集，group 均为 `editor`）、`Player_Has_No_Editor_Tools_And_Calls_Are_Unknown`（清单不含 ＋ 调用＝`unknown-tool`）；`ToolWhitelistTests` 的差集护栏已纳入全部 `editor.*`
  - [x] 工具参数支持结构化过滤
    - 证据：`AssetList_Structured_Filters_Take_Effect`（kind／packageId／idPrefix／tags（AND）／minVersion+maxVersion 及组合，逐项命中数）
  - [x] 工具与进程内 API 等价
    - 证据：`AssetList_Tool_And_InProcess_Api_Are_Equivalent`（同一会话内工具投影 ＝ `ListAssets(EditorAssetQuery)` ＝ `ListAssets(lambda)`，逐字段一致；无过滤路径同样等价）；处理器只转调 `AssetEditor`，不复制版本/依赖/过滤判定
  - [x] `diag.card_load` 命名与既有 `diag.*` 风格一致，且 `sandboxOnly + readOnly`
    - 证据：`tests/Orc.Mcp.Tests/CardLoadDiagTests.Name_Follows_Diag_Convention_And_Is_Authoring_Only`（`diag.card_load`、group=`diag`、点后 snake_case、Player 档不存在）、`ReadOnly_Passes_Pending_Gate_While_Write_Tools_Are_Rejected`（挂起期只读工具可调用、非只读被 `pending-targeting` 拒绝）
  - [x] 累积视图按 GUID 合并、`refCount` 累加
    - 证据：`CardLoadDiagTests.Real_Match_Returns_Batch_Report_And_Cumulative_View`（真实对局：`batch.items`＝5、`batch.slots`＝78、累积视图 GUID/refCount 与批次一致、累积不含 `slots`）、`Cumulative_View_Merges_By_Guid_And_Accumulates_RefCount`（第二批后 `batchCount`＝2、逐 GUID `refCount` 翻倍、条目数不变）；`tests/Orc.Packages.Tests/CardLoadLedgerTests`（5 例：GUID 合并＋累加／首次出现序＋最近字段生效／`Latest`·`BatchCount`·`Clear`／JSON 键名与不写 `slots`／`MatchCardAssembly` 两批真实装积累加）
  - 验收证据：`tests/Orc.Mcp.Tests/EditorToolsTests.cs`（14 例：工具集与能力集一致／Player 无编辑器工具／`pack.list` 列出沙盒包／`session.begin` 默认沙盒包且复制到 `pack.tmp`／无会话＝not-ready／创建·双通道提交·列举·读取·版本·lock 预览·放弃全流程／转正写正式 lock／reload 结构化（`configured`＋状态词表）／越界包 `path-denied`／结构化过滤／工具与进程内 API 等价／版本非递增与格式非法拒绝／`dep.set` 落 sidecar 且裸 ID 归一／删除资产移出声明与正式 lock 预测）
    与 `tests/Orc.Mcp.Tests/CardLoadDiagTests.cs`（5 例：命名与档位／只读穿透挂起期门禁／真实对局双视图／累积合并且累加／沙盒局直注册路径报告为空表）
    与 `tests/Orc.Packages.Tests/CardLoadLedgerTests.cs`（5 例，见上）
    实现：`src/Orc.Mcp/Tools/EditorTools.cs`（13 个工具 ＋ 失败映射：`AssetEditorException.Code`→受控结果码并回带 `details.editorCode`）、`src/Orc.Mcp/McpService.Editor.cs`（编辑器／注册面／`CardAssembly` 持有）、`src/Orc.Mcp/Tools/DiagTools.cs`（`diag.card_load`）、`src/Orc.Packages/Loading/CardLoadLedger.cs`（台账＋累积视图＋JSON 写出，复用 `CardLoadReportJson` 的条目/汇总写法）、`src/Orc.Packages/Loading/MatchCardAssembly.cs`（每批 `Ledger.Record`）
  - 已知边界（详见交付报告）：①沙盒局（`sandbox.create`）走代码内定义集直注册路径，不经卡牌加载器 ⇒ `diag.card_load` 的 `batch` 为 null、累积为空表（带 `note` 说明），本批报告只对「元数据驱动」的真实对局（`CreateMatchAsync`）产生；②`editor.reload` 在沙盒流程中通常返回 `unchanged`/空计划——编辑器注册面（`EditorRegistry`）是宿主职责，沙盒包在转正前无 sidecar 与正式 lock，且沙盒局不消费该注册面；③沙盒包 manifest 的 `id`（默认 `sandbox`）与目录名 `pack` **不冲突**，应用层前缀为 `pack/sandbox/{局部ID}`。
- Rationale：agent 与前端同源（R13）。
- Terminology: [调试 API](../../CONTEXT.md)


### S10a: 存量迁移工具与样例包元数据生成（**前置步**）
- Status: Done
- Order：**S4 完成后、S5 开工前必须完成**（无 sidecar 即非法 ⇒ 新链路一旦启用，未迁移样例包全挂，后续验证无从谈起）
- Target：新增 `tools/Orc.Tools.Migrate`（控制台工程）、`Orc.Packages` 内 `AssetMigrationTool`、`samples/*` 的 sidecar／manifest v2／lock
- Approach：（I9 已定：库函数＋薄 CLI，宿主不写盘）
  - 迁移逻辑＝`AssetMigrationTool` 内的**纯函数**，返回结构化 `MigrationPlan`（可被 S10b 直接断言），CLI 只做参数解析与人读渲染。**不塞进 `samples/*`**——samples 职责是示例与宿主演示，工具另立工程以便 CI 直接调用（`dotnet run --project tools/Orc.Tools.Migrate -- ...`）。
  - `MigrationPlan` 建模为可断言数据结构：`{ 包绝对路径, 条目[] }`，条目＝`{ 源文件相对路径, sidecar 相对路径, 动作 ∈ {Create, Skip, Overwrite}, 声明资产[] (guid/kind/id/version) }`，另含 `manifest` 动作与 `lock` 动作、以及冲突/障碍明细。
  - 模式：**`--dry-run` 为默认**（仅输出计划与差异、不写盘）；`--apply` 才写盘。
  - 幂等：已存在 sidecar 的文件 `动作＝Skip` 并计入报告；仅 `--force` 允许 `Overwrite`；重复运行不得产生重复条目或版本漂移。
  - 内容：扫描 `*.card.json`／`*.prefab.json` → 生成同目录 sidecar（初始 `1.0.0`）与 manifest v2；为 `card-pack-demo`／`card-pack-diy` 补 manifest；产出初始两 lock（锁 `1.0.0` 全套）；`versions/` 不生成。
  - **宿主启动期不写盘**，但报错须可操作：指名「包 X 缺少 sidecar」时附带可直接复制执行的迁移命令（含该包绝对路径）。
- Acceptance:
  - [x] 全部样例包的每个资产源文件都有且仅有一份 sidecar
  - [x] 全部样例包 manifest 为 v2（metadata 列表非空、无 `dependsOn`）
  - [x] 全部样例包具备两 lock，且内容与 sidecar 声明一致
  - [x] 默认模式不写盘（dry-run 后目录字节不变）；`--apply` 才落盘
  - [x] 重复执行结果幂等（第二次全 `Skip`、无版本漂移）
  - [x] `--force` 才重写既有 sidecar
  - [x] 宿主未迁移报错含绝对路径与可复制命令
  - [x] `MigrationPlan` 为公开数据结构，S10b 可直接断言
    - 现状：`Orc.Packages/AssetMigrationTool.cs` 的 `MigrationPlan`（包绝对路径／`Entries`（源＋sidecar 相对路径／动作／声明资产）／`Manifest`＋`FormalLock`＋`TestLock` 动作／`Obstacles`）＋ `MigrationEntry.Content`（规范文本）；
      纯函数 `Plan(...)`／写盘 `Apply(...)` 分离，CLI 只渲染。样例包现状＝5 包／27 sidecar／10 lock（`samples/*/lock.formal.json`＋`lock.test.json`）；
      无 manifest 的 `card-pack-demo`／`card-pack-diy` 的 `id` 由目录名去 `card-pack-` 前缀派生、`guid` 由 id 确定性派生（`AssetMigrationTool.DerivePackageGuid`，不含时间戳）。
  - 验收证据：`tests/Orc.Packages.Tests/AssetMigrationToolTests.cs`（13 例：dry-run 不写盘／幂等／Skip-Overwrite／plan 结构／障碍报告／迁移提示／注册面可消费／样例包实况）；
    宿主报错的可复制命令由 `MigrationGuidance.ForPackage(...)` 产出，已接入 `PackageRegistrar` 的 sidecar 结构性错误与 `SidecarMissing` 诊断。
- Rationale：迁移是「新链路可启用」的硬前置，必须与收尾验证解耦单独排期；默认 dry-run ＋ 幂等是「会改用户资产目录的工具」的最低要求。

### S10b: 全量回归验证（收尾）
- Status: Done
- Target：`tests/*`、全量 `dotnet test`
- Approach：旧语义测试等价迁移或显式废弃；核对 S1–S9 各验收项；全量跑测。
- Acceptance:
  - [x] 迁移后全部样例包预检通过（结构性零错误，卡级仅报告）
  - [x] 旧语义测试等价迁移或显式废弃
  - [x] 全量测试通过
- 验收证据：
  - 样例包：`dotnet run --project tools/Orc.Tools.Migrate -- --dry-run --root samples` ⇒ 包 5｜Create 0｜Skip 42｜Overwrite 0｜**障碍 0**｜待写 0（幂等成立、结构零错误）。
  - 退役语义：删除的 13 个测试全部对应已退役语义（包级 `dependsOn`／`SelectHighest` 择版／包版本继承），且均有显式替代用例：`PackageResolverTests.包级dependsOn已退役_不再影响解析`、`PackagePrecheckTests.包级依赖已退役_不再参与预检`、`CardPackHotLoadTests.包级dependsOn已退役_缺失依赖不再阻断热加载`、`PackageManifestTests.资产名派生（包 GUID8 继承＋资产版本自持）`。断言净增 25（删 35／增 60）。
  - 全量：`dotnet build OrcEngine.slnx` 0 错误 0 警告；`dotnet test OrcEngine.slnx` **2038 通过 / 0 失败 / 0 跳过**。
- Rationale：收尾只做验证与测试等价化，不再引入迁移逻辑。
- Terminology: [资产元数据](../../CONTEXT.md)

## 已知边界与遗留（收尾记录）

以下为**实现已达成验收、但行为边界需要知会**的条目；不改代码，记录以免后续被当作 bug 重复排查。

| # | 边界 | 原因 | 处置 |
| --- | --- | --- | --- |
| B1 | 沙盒局（`sandbox.create`）的 `diag.card_load` 无本批报告，返回 `source:"none"` | 沙盒走「代码内定义集直注册」路径，不经卡牌加载器；补齐需给沙盒包造 sidecar＋lock | 用 `source` 字段区分两种对局；若要求统一，另立步骤给沙盒包生成元数据 |
| B2 | 沙盒脚手架 manifest 为 v1 形态（`version:"0.0.1"`、无 `schemaVersion`/`metadata[]`），首次 `asset.list` 为空 | 脚手架卡无 sidecar，而 sidecar 才是权威（R2） | 读宽接受；首次 create/commit/version/dep 会重写为 v2 并写 `metadata[]` |
| B3 | `editor.reload` 在沙盒流程中通常返回 `unchanged`／空计划 | 编辑器注册面 `EditorRegistry` 由宿主填充；沙盒包转正前无 sidecar／正式 lock | 工具如实转发结构化结果；不私自注册（会造成第二个真源） |
| B4 | `versions/<version>/` 归档 sidecar 的 `source` 必须写相对包根的全路径（含 `versions/<v>/` 前缀） | `PackageRegistrar` 校验 `source` 与包根相对路径一致 | 编辑器归档时同步改写 `source`；不视为缺陷 |
| B5 | 新建资产以 `1.0.0` 进入测试域，转正保持 `1.0.0`（不追加测试段） | 新资产无正式基线，`x.y.z.N` 的追加规则无适用前提 | 仅对**已有正式版**的首次改动才产生 `x.y.z.1` |
| B6 | `asset.list` 的 `tag` 过滤只覆盖卡（非卡资产 `Tags` 恒为空） | sidecar 无标签字段，标签只能从 `CardDefinition.Tags` 解析 | 已知限制；若需通用标签，应在 sidecar 增字段（需另立需求） |
| B7 | `CardLoadReport.items` 不承载「appId 未解析」的卡位 | 冻结 schema 的 `items[].assetGuid` 非空 | 该卡位仍占 `slots` 一条（`assetGuid:null`、`tier:invalid`、`fieldPaths:["appId"]`） |
| B8 | 样例卡普遍落在 `warning` 档 | 样例数据无卡图／描述，D40 判为警报 | 符合 D40；不是质量问题 |
| B9 | 全量重建（`--no-incremental`）可见 4 条既有 CS86xx 可空性警告 | 均为既有文件（`EffectSystem`／`KeywordServices`／`UnitTurnStatsTracker`／`BulwarkKeywordTests`），本次未触碰 | 常规增量构建为 0 警告；不在本次范围修 |
