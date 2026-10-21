# Requirements — 卡牌资产元数据管理与后端编辑器

为卡牌/效果/触发器/handler 建立「GUID ＋ 元数据」双层管理（资产身份层 ＋ 文件载体层），使卡包成为元数据列表容器、加载由元数据驱动（资产加载器 → 分层列表 → 卡牌加载器触发器 ＋ 合法性检测套件），并以后端编辑器承担版本/依赖/保存、前端编辑器承担内容编辑与提交。

> **交接状态**：需求 R1–R18 全部 Confirmed；实现 S1–S25 全部 Done；代码/测试审查缺陷 F1–F12 已全部修复（2026-10-21）。
> **当前基线**：全量测试 **2263 通过 / 0 失败 / 0 跳过**；构建 0 错误（增量 0 警告 / 全量 4 条既有可空性警告）。
> **文档索引**：决策全集＋矛盾记录＝`requirements-grill-plan.md`；实现 S1–S12＝`implementation.md`；**审查缺陷修复 S13–S25＝`实现方案-审查缺陷修复.md`**；修复需求＝`修复需求-审查缺陷.md`；审查结论＝`代码审查与测试审查报告.md`；**前端交接（自包含）＝`前端交接文档.md`**；术语＝仓库根 `CONTEXT.md`。
> **新会话起手**：读本文件 → 读 `前端交接文档.md`（当前 API/契约真源）→ 需要历史决策时再读 grill-plan 与 `implementation.md`。

## Scope
- Includes: 资产身份（GUID/元数据/版本/依赖/冲突）、元数据 sidecar 与卡包 manifest、加载协议（注册/加载/分层列表/增量重载）、卡牌合法性三档与报告、双 lock 与两段加载（正式/测试）、ID 中间层、后端编辑器（会话/提交/保存/转正/历史版本）、沙盒内置编辑器、MCP 工具化、存量资产迁移。
- Excludes: 自动补全机制与"对局内随机卡牌生成"（D45，仅保留报告字段描述作为输入契约）、前端图形界面实现（本次只定义契约与后端能力）、卡图二进制资源（仅路径引用）、网络/多人同步、具体玩法与数值平衡。

## Constraints
- 沿用 P1（改变才传播）／P2（同步顺序语义）／读宽写窄／失败隔离。
- **包版本不参与任何解析**（不参与依赖、不进 lock、不进资产 GUID），仅作内容指纹。
- 加载阶段只做注册与依赖解析；**csx 编译留首用懒编译**。
- 单编辑会话；单活跃对局；弱沙箱（csx 同进程，非安全边界）。
- 报告不落盘，仅 API/工具返回。

## Requirement Items

### R1: 资产身份双层
- Status: Confirmed
- Scenario/Trigger: 声明或加载任意游戏资产（卡牌/效果/触发器/handler）时。
- Behavior: 四类资产各有元数据条目，携带 GUID（版本级身份）、资产种类、局部 ID、版本、依赖与冲突声明。GUID 由「**包稳定身份（`manifest.guid`）＋ 资产种类 ＋ 局部 ID ＋ 资产版本**」派生，**不含包版本**。允许同一资产多版本同时注册，但一次加载只加载一个版本。
- Acceptance:
  - [ ] 四类资产均可声明 GUID 与元数据
  - [ ] GUID 只由包 ID/种类/局部 ID/资产版本派生
  - [ ] 同一资产多版本可并存注册；单次加载只取一个版本
- Terminology: [资产](../../CONTEXT.md)、[资产版本](../../CONTEXT.md)、[资产名](../../CONTEXT.md)

### R2: 卡牌元数据自包含
- Status: Confirmed
- Scenario/Trigger: 加载一张卡牌时。
- Behavior: 卡牌的版本、依赖、冲突、加载方式（`loader`）全部记录在其元数据 sidecar 内。sidecar 与源文件**成对存在**（宿主语义）：源文件缺失 ⇒ 该元数据不成立（孤儿 meta 进报告、不注册）；源文件在而 sidecar 缺 ⇒ 该卡按"无元数据"走三档判定。sidecar 为权威，源文件 `id` 必须一致，不一致判非法。
- Acceptance:
  - [ ] 卡牌元数据含版本/依赖/冲突/loader
  - [ ] 源文件缺失时孤儿 meta 不参与注册且可诊断
  - [ ] sidecar 与源文件 `id` 不一致 ⇒ 非法
- Terminology: [资产元数据](../../CONTEXT.md)、[卡牌数据体](../../CONTEXT.md)

### R3: 卡包＝元数据列表容器
- Status: Confirmed
- Scenario/Trigger: 加载一个卡包时。
- Behavior: manifest＝`{ schemaVersion, id, guid, kind, name?, version, metadata: [sidecar 路径…] }`，只列**当前正式版**；`versions/*` 由目录约定自动扫描注册。manifest 不承载资产内容，只承载列表与包身份。**注册**＝元数据解析 ＋ 包内全部资产 DTO 生成（不实例化、不进对局）；**加载**＝合并 lock ＋ 拓扑分层装载。加载顺序＝先注册、再按元数据声明的协议加载。
  - 字段分工（D50 的遗漏补全，非新需求）：`id`＝人类可读包标识（应用层前缀 `pack/{id}/` 与目录名用）；`guid`＝包稳定身份（资产名 `@{包GUID8}` 与 GUID 派生的输入）。二者缺一不可——同 ID 包可并存（D31），故资产名不能只用 `id` 区分。
- Acceptance:
  - [ ] manifest 仅含身份与元数据列表（不含资产内容）
  - [ ] 加载严格按「元数据 → 协议加载」两段
  - [ ] `versions/*` 中的历史版本可被发现与注册
- Terminology: [卡包](../../CONTEXT.md)

### R4: 内含资产的元数据最小面
- Status: Confirmed
- Scenario/Trigger: 声明效果预制体内含的触发器/handler 时。
- Behavior: 内含资产在宿主文件的 sidecar 中各占一条；字段为 `guid / kind / id / version(继承宿主) / anchor(指向 stableKey 或事件 id) / dependsOn`；`loader` 与 `conflictsWith` 对内含资产留空。内含资产**不参与拓扑分层**（随宿主同层装载）。
- Acceptance:
  - [ ] 内含资产 GUID 独立、版本继承宿主
  - [ ] anchor 可解析到 `stableKey` 或事件 `id`
  - [ ] 内含资产不作为独立拓扑节点
- Terminology: [内含资产](../../CONTEXT.md)、[触发器](../../CONTEXT.md)

### R5: 卡牌加载器
- Status: Confirmed
- Scenario/Trigger: 对局初始化统一加载双方牌堆，或局内新增卡（token/升级/老兵）时。
- Behavior: 卡牌加载器以**底层触发器**（`LowLevel`）形式存在，由**插件声明并注册**，卡包只能引用已注册加载器。持有**引擎内置合法性检测套件**（插件可追加检查项，不可改三档骨架）。**整批一次触发**（单项清单退化为单卡一次），产出**问题报告 ＋ 卡牌引用列表**。合法返回卡牌本身；缺失字段尽力生成（写类型默认值 0/null/空串）并按三档标记；非法不生成。卡级聚合＝任一非法项即整卡非法。实例化**不做任何依赖解析**，只做纯装配并挂载效果主触发器。
- Acceptance:
  - [ ] 加载器以 `LowLevel` 触发器注册并按整批触发
  - [ ] 三档判定按 D40 判定表执行
  - [ ] 卡图/描述缺失＝警报且可玩；效果缺指令/反制、单位缺数据＝非法
  - [ ] 产出报告 ＋ 卡牌引用列表（按资产去重＋引用计数）
  - [ ] 报告携带**稳定字段描述**（字段路径／期望类型／默认值／缺失原因），作为后续自动补全的输入契约
  - [ ] 报告不落盘，仅经 API/工具返回
  - [ ] 牌堆中非法资产：卡位留空、开局继续
  - [ ] 包级门禁只拒绝**结构性非法**（清单/元数据/lock），卡级警报与非法卡不阻断包
  - [ ] 实例化无依赖解析；效果主触发器在实例化时挂载
  - [ ] 卡包自带加载器 ⇒ 拒绝
- Terminology: [卡牌加载器](../../CONTEXT.md)、[合法性检测套件](../../CONTEXT.md)

### R6: 元数据落盘与资产名
- Status: Confirmed
- Scenario/Trigger: 生成或读取资产元数据时。
- Behavior: 每个资产源文件（`*.card.json` / `*.prefab.json` / `*.csx` / 其他 json）配一份同目录 sidecar，命名 `<源文件名>.meta.json`，一对一可逆映射，内含该文件声明的全部资产条目。资产名按**资产种类**分型：`{种类}：{局部ID}@{包GUID8}#{版本}`（种类∈card/effect/trigger/handler/service）。元数据沿用读宽写窄。
- Acceptance:
  - [ ] 每个资产源文件有且仅有一份 sidecar
  - [ ] sidecar 文件名可由源文件名唯一推出，反之亦然
  - [ ] 资产名种类 token 为 card/effect/trigger/handler/service
  - [ ] 元数据读宽（注释/尾逗号/大小写）写窄（固定键序/2 空格/LF）
- Terminology: [资产名](../../CONTEXT.md)

### R7: 双 lock 与分层加载
- Status: Confirmed
- Scenario/Trigger: 打包与加载 match 时。
- Behavior: 每包两份 lock（本包正式版资产 GUID／依赖资产 GUID），**只能手动更新**；lock 表达"卡包的正式版"，资产改动与正式版解耦。加载＝合并参与方 lock → 若同一资产出现不同版本则**报错** → 否则在已注册资产上做**拓扑分层**（第 0 层无依赖），产出二维层状加载列表 → 按层装载。依赖检测、拓扑排序、加载顺序在一次计算中完成；禁止环形依赖（报错并输出环路径）。层内按局部 ID 字典序（仅供确定性展示与比对，加载不承诺层内语义顺序）。加载器缓存列表；新列表从**首个变更层**起增量重载其后全部资产，只刷新注册面、不破坏对局稳定性。
- Acceptance:
  - [ ] 两个 lock 随包产出且只能手动更新
  - [ ] 未入 lock 的版本不参与加载
  - [ ] lock 间同资产不同版本 ⇒ 报错并指名
  - [ ] 无冲突时产出二维层状列表并按依赖顺序装载
  - [ ] 环依赖 ⇒ 报错并输出环路径
  - [ ] 增量重载从首个变更层起，已实例化卡不受影响
  - [ ] 重载门禁＝判定器 `reloadGate`（结构化返回，不抛错）
  - [ ] 插件包同样产出两 lock（asm 插件见 R18；csx 插件同卡包）
- Terminology: [正式 lock](../../CONTEXT.md)、[测试 lock](../../CONTEXT.md)、[加载列表](../../CONTEXT.md)

### R8: 编辑器 DIY 前置
- Status: Confirmed
- Scenario/Trigger: 进入编辑器时。
- Behavior: 必须先生成临时卡包（同包目录旁 `<包目录>.tmp/`）或复制卡包，并生成临时版本；进入时选择编辑已有卡包或新建卡包，新建时必须给定期望依赖的卡包 ID。单编辑会话。
- Acceptance:
  - [ ] 未创建临时包则无法进入编辑
  - [ ] 新建卡包必须指定依赖包 ID
  - [ ] 同时只允许一个编辑会话
- Terminology: [临时卡包](../../CONTEXT.md)

### R9: 编辑器资产 API
- Status: Confirmed
- Scenario/Trigger: 前端或 agent 枚举/编辑资产时。
- Behavior: 提供列出已注册资产的 API：进程内收 `Func<AssetInfo,bool>` lambda，MCP 工具收结构化过滤（kind/packageId/idPrefix/tag/版本范围）。打开卡包后可编辑已有资产或新建；版本可手写（段数 1..8，纯数字，按"左对齐、缺段补 0、逐段比较"严格大于旧版；默认尾段 +1）。能力集：`pack.list`（可编辑包）、`session.begin/end`、`asset.list/get/commit/create/delete`、`version.set`、`dep.set`、`lock.preview`、`promote`、`reload`。提交双通道：`commit(path)`（前端直写）与 `commit(path, content)`（工具）。
- Acceptance:
  - [ ] 列举 API 支持过滤谓词（进程内 lambda／工具结构化）
  - [ ] 手写版本号不满足严格递增 ⇒ 拒绝
  - [ ] 默认自动递增＝尾段 +1
  - [ ] 提交双通道均可用
- Terminology: [后端编辑器](../../CONTEXT.md)

### R10: 临时卡包生命周期
- Status: Confirmed
- Scenario/Trigger: DIY 编辑结束保存或放弃时。
- Behavior: 编辑期全部提交落在临时包与**测试 lock**（自动重算）。测试资产版本＝正式版后追加测试段（`1.2.3` → `1.2.3.1`、`.2`…）。结束时：
  - **转正**：移除测试段并递增正式版尾段（`1.2.3.5` → `1.2.4`），更新两 lock，去掉 `tmp`；当前版本源文件＋sidecar 移入 `versions/<version>/` 保留历史。
  - **放弃**：删除整个临时目录（含测试 lock）。
  - 编辑器内可修改依赖（`dependsOn`），测试 lock 随之重算。
- Acceptance:
  - [ ] 保存产出新版本＋新 lock，且旧版本内容保留在 `versions/<version>/`
  - [ ] 放弃后临时包与改动不残留
  - [ ] 测试版本号规则正确（追加段／转正去段并递增）
  - [ ] 依赖可改且反映到测试 lock
- Terminology: [临时卡包](../../CONTEXT.md)、[测试资产](../../CONTEXT.md)

### R11: 沙盒内置编辑器
- Status: Confirmed
- Scenario/Trigger: 沙盒模式下。
- Behavior: 进程内组件 `AssetEditor` ＋ MCP 工具组 `editor.*`，仅在 Authoring 档登记；player 档工具清单中不存在。
- Acceptance:
  - [ ] 沙盒模式可访问编辑器能力
  - [ ] 非沙盒模式工具清单不含 `editor.*`
- Terminology: [沙盒模式](../../CONTEXT.md)

### R12: 前后端编辑器分工
- Status: Confirmed
- Scenario/Trigger: 编辑器实现分工时。
- Behavior: 后端管资产版本/依赖/冲突/保存/转正；前端管内容编辑与提交（资产纯文本、卡图为路径）；编辑器全部表达力由后端承担。前端只能写临时包，正式包只读；同机可直写文件后 `commit(path)`，跨机只能 `commit(path, content)`。新建资产版本从 `1.0.0` 起；新建包时资产同样从 `1.0.0` 起、包版本为 `hash-tmp`、首个正式 lock 在转正时生成；资产 ID 手写（后端校验）；支持删除资产（当前正式版移出 lock，历史版本仍留在 `versions/`）。
- Acceptance:
  - [ ] 前端不承担版本/依赖/校验逻辑
  - [ ] 前端无法写正式包
  - [ ] 新建包/新建资产的初始版本为 `1.0.0`，首个正式 lock 在转正时生成
  - [ ] 删除资产：当前版移出 lock，历史版本仍在 `versions/`
- Terminology: [前端编辑器](../../CONTEXT.md)

### R13: agent 经工具使用编辑器
- Status: Confirmed
- Scenario/Trigger: agent 需要执行编辑器操作时。
- Behavior: 编辑器能力以 `editor.*` 工具暴露，agent 可完成与前端等价的完整流程（建会话→编辑→提交→预览重载→转正/放弃）。
- Acceptance:
  - [ ] 编辑器核心操作均有对应工具
  - [ ] 工具与前端走同一后端组件
- Terminology: [调试 API](../../CONTEXT.md)

### R14: ID 中间层
- Status: Confirmed
- Scenario/Trigger: 应用层（卡组、卡面、效果引用）指向具体资产版本时。
- Behavior: 应用层使用**应用层 ID**（与版本无关）；资产层使用 **GUID**；**中间层**负责应用层 ID → 资产层 ID 的转接，并承载遮蔽表。遮蔽仅在测试段生效，正式段永不引用测试定义。
- Acceptance:
  - [ ] 卡组/引用在资产升版后无需修改
  - [ ] 同一应用层 ID 在中间层只解析到一个资产版本
  - [ ] 测试段遮蔽不影响正式段解析
- Terminology: [中间层](../../CONTEXT.md)

### R15: 两段加载与测试资产
- Status: Confirmed
- Scenario/Trigger: 资产加载器装载时。
- Behavior: 产出正式加载列表与测试加载列表两段，先装正式、再装测试。正式 lock 只与正式 lock 合并，测试 lock 只与测试 lock 合并。测试资产可与正式资产**同应用层 ID**（遮蔽），依赖单向（测试→正式／测试→测试），正式资产不得依赖测试资产。正式段失败不尝试测试段；测试段失败只回滚测试段；测试资产依赖的正式资产不在正式 lock ⇒ 该测试资产判非法（进报告、不整段失败）。
- Acceptance:
  - [ ] 正式/测试 lock 分域合并
  - [ ] 测试资产可遮蔽同 ID 正式资产并生效
  - [ ] 正式资产依赖测试资产 ⇒ 拒绝
  - [ ] 失败隔离符合上述三条
- Terminology: [测试 lock](../../CONTEXT.md)、[测试资产](../../CONTEXT.md)

### R16: 元数据冲突声明
- Status: Confirmed
- Scenario/Trigger: 加载集合内同时出现互相冲突的资产时。
- Behavior: 资产元数据可声明 `conflictsWith[]`（kind＋应用层 ID＋版本约束）；判定作用于**本次加载集合**；**双向硬冲突**⇒加载失败并指名双方与版本。不引入互斥组。
- Acceptance:
  - [ ] 冲突声明随元数据落盘
  - [ ] 本次加载集合内出现冲突双方 ⇒ 失败并指名
  - [ ] 未加载的资产不参与冲突判定
- Terminology: [资产](../../CONTEXT.md)、[正式 lock](../../CONTEXT.md)

### R17: 版本规则
- Status: Confirmed
- Scenario/Trigger: 生成、手写或校验版本时。
- Behavior:
  - **资产版本**＝纯数字 1..8 段（`.` 分隔、非负、每段 ≤ int32）。
  - **规范形**＝右补 0 至 8 段；比较、相等、哈希、GUID 派生一律以规范形为准（故 `1.2` ≡ `1.2.0`）。展示串（`ToString`）保留书写时的段数，但**禁止以字符串比对版本**。
  - 比较＝**左对齐、缺段补 0、逐段比较**，第一处不等决定大小。
  - 递增＝**严格大于**旧版（允许增段；禁止降级与补 0 后相等）；默认自动递增＝**尾段 +1**。
  - 非法版本号（格式不符／非递增）⇒ 编辑器拒绝提交；加载期该资产判**非法**（不生成、进报告）。
  - **包版本**＝`hash(包 ID ＋ 包内元数据)`，**不含时间戳**；不参与依赖、不进 lock、不进资产 GUID（仅内容指纹）。临时包加 `-tmp`，转正时去掉。
- Acceptance:
  - [ ] `1.2`→`1.2.1`、`1.2`→`1.3` 合法；`1.2.3`→`1.2`、`1.2`→`1.2.0` 拒绝
  - [ ] `1.2` 与 `1.2.0` 判为同一版本（比较相等、哈希相同、GUID 相同）
  - [ ] 默认递增＝尾段 +1
  - [ ] 包版本不含时间戳，且不影响任何身份/解析
  - [ ] 临时包版本带 `tmp`，转正去掉
- Terminology: [资产版本](../../CONTEXT.md)、[资产名](../../CONTEXT.md)

### R18: 插件服务资产
- Status: Confirmed
- Scenario/Trigger: 装载插件包（冷加载）时。
- Behavior:
  - 插件提供的引擎服务（判定器／资产服务／词条／卡组件／行动契约／投影提供者／观察者）作为 **`service` kind** 资产纳入元数据体系，子类由 `serviceKind` 字符串表达；**新增服务种类只加常量，不改 `AssetKind` 枚举**。
  - **代码声明是权威**（`IPluginAssetSource.Describe()`）；asm 插件（无源文件）用**包级 `assets.meta.json`**（`source`＝程序集名）作为落盘镜像；csx 插件用一对一 sidecar。
  - 插件资产**不进卡组装载链**（不实例化、不参与卡组拓扑）；按 lock 校验后才算"正式版"。
  - **asm 插件不保留历史版本**：官方更新 ⇒ 资产版本递增、GUID 变更 ⇒ 旧 lock 校验失败（`stale-lock`/`unlisted`）并**提示重锁**，不静默降级。
  - 范围：本次＝框架 ＋ `orc.builtin.core` 试点（49 条：24 判定器 ＋ 19 词条 ＋ 6 卡组件）。
- Acceptance:
  - [ ] `service` kind 与 `serviceKind` 可往返（sidecar／lock／资产名）
  - [ ] core 插件声明 49 条并在冷加载期注册进注册面
  - [ ] `assets.meta.json` 与两 lock 可由工具生成，且校验通过
  - [ ] 插件升级未重锁 ⇒ 校验失败并提示重锁
  - [ ] 插件资产不进入卡组装载列表
- Terminology: [插件](../../CONTEXT.md)、[资产](../../CONTEXT.md)、[正式 lock](../../CONTEXT.md)
