# Implementation Grill Plan — 卡牌资产元数据管理与后端编辑器

> 本轮 grill 聚焦：把 R1–R17 落成可执行的步骤切分（S1–S10），逐条对齐实现路径。
> **交接状态**：实现 grill 第 0 轮（未开始）；S1 已完成；**下一轮从 I1 开始**，I1 确认后进 S2。
> **新会话起手**：需求 `requirements.md` → 本文件「已确认的代码基线」→ 按 S2…S10 逐条对齐（每条 1–2 个分支问题）。

## 已确认的代码基线（实现取证用）

- `AssetVersion` 为三段 `record struct`（`src/Orc/Core/AssetNaming.cs:51-96`）；`AssetName` 类型名仅 json/csx/asm（`:105-211`）。
- `PackageManifest` 含 `kind/id/guid/version/dependsOn[]/assets[]`，资产版本继承包版本（`src/Orc.Packages/PackageManifest.cs:47-147`）。
- `CardLibrary.Register(id, definition)` 以局部 id 为唯一键、重复即抛（`src/Orc.Game/Cards/CardLibrary.cs:65-74`）。
- 包级装载必须在 `Match.Initialize` 之前（`src/Orc.Packages/CardPackLoader.cs:250`）；`LowLevel` 触发器在 `Initialize` 内登记（`src/Orc.Game/Match/Match.cs:509-512`）。
- 包级双零门：Errors=0 **且** Warnings=0（`CardPackLoader.cs:57-64`）。
- 无 lock 文件；`AssetCatalogSnapshot.VersionLocks` 仅内存（`EngineHost.cs:127`）。
- 卡数据体无版本字段（`CardDataJson.cs:17-20`）；预制体每个 trigger/event 有 `version` 字段（`Prefabs.cs`）。
- 服务层 47 工具（player 24／authoring 47），沙盒专属登记期不登记（`McpService.cs:87-90`）。

## 未决问题

### I1: `AssetVersion` 放宽到 N 段的兼容面 【Pending】
- 现有三段 `record struct` 被 60+ 处使用；放宽后是否保留 `(major,minor,patch)` 构造与 `Major/Minor/Patch` 属性？如何表达"段数不同但补 0 相等"（`1.2` vs `1.2.0`）？
- 建议：新增 `AssetVersionN`（段数组 + 比较器），`AssetVersion` 保留并改为其特例/适配器，逐步迁移。

### I2: 版本级注册表与 `CardLibrary` 的关系 【Pending】
- `CardLibrary` 键＝局部 ID 且重复即抛；多版本并存注册需要版本级键。
- 选项：a) 新增 `AssetRegistry`，`CardLibrary` 只持有"已解析的当前版本定义"（中间层下游）；b) `CardLibrary` 内部改为版本级字典 + 解析面。

### I3: 现有 `CardPackLoader`/`CardDataLoader` 的去留 【Pending】
- 新链路（元数据→lock→分层→卡牌加载器）与现有 `CardPackLoader.Load`/`CardDataLoader.LoadDirectory` 双零门并存还是替换？硬切还是适配层？

### I4: 包级 `dependsOn` 退役的迁移面 【Pending】
- 3 个内置插件包 manifest 与测试中的 `dependsOn` 需要处理；插件加载顺序改为资产级拓扑后，内置包（判定器/资产服务）能否被资产级依赖表达？

### I5: 卡牌加载触发器的挂载点与命名 【Pending】
- `LowLevel` 触发器名、触发时机（`LoadDecksAsync` 之前/替代之）、与 4 个现有流程触发器的关系；整批清单如何传入（`Context` 载荷）。

### I6: 中间层与卡组/效果引用的改造范围 【Pending】
- 卡组文件、卡牌 `effects.prefabs`、`BecomesVeteran:<id>` 等引用是否统一走中间层？还是仅卡组走？

### I7: 测试资产的实例化隔离 【Pending】
- 测试资产实例化后如何标记（避免进入正式构筑/存档）；是否禁止测试资产出现在正式对局。

### I8: 编辑器写路径与 `SandboxPathGuard` 【Pending】
- `<包目录>.tmp/` 位于正式包根旁，可能落在沙盒根之外——需要扩展守卫还是要求编辑器只对沙盒根内包生效？

### I9: 迁移工具形态 【Pending】
- CLI／MCP 工具／编辑器 API 三选一或并存；是否在 `EngineHost.Start` 前强制跑一次（防"未迁移即加载失败"）。

### I10: 报告 schema 定型 【Pending】
- 字段描述（路径/类型/默认值/缺失原因）作为自动补全契约，需要冻结键名；报告是否进 `diag.*` 工具面。
