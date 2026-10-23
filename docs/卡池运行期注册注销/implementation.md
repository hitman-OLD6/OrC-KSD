# Implementation — 卡池运行期注册与注销

实现方案（grill 已定稿）：按 requirements.md R1–R7 与现有代码结构（`Orc.Server/Admin`、`Orc.Mcp/Tools`、`Orc.Packages/Loading`）拆成 S1–S13；`Approach` 里的做法均已由父 agent 裁决，无遗留待裁决项。实施时按 S1 → S13 顺序推进（S3 的 `Inspect` 是其余步骤的前置）。

## Scope
- Target:
  - `Orc.Server`：管理面 op 表（`AdminOps` / `AdminApi`）、进程级已注册包集合的载体、开局装配链（`AdminApi.StartCoreAsync` / `TryAssemble` / `ResolvePackDirectories`）。
  - `Orc.Mcp`：工具表（`ToolRegistration` 新增 `pool.*` 组）、`McpService.AddTool` 的 `sandboxOnly`/`readOnly`/`requiredRole` 门控、跨层契约 `IPoolControl`（对齐既有 `IRoomControl` 的"活对象隔离点"模式）。
  - `Orc.Packages`：两处**最小新增**（`AssetLoadIssue.Conflict` ＋ `MatchCardAssembly.Inspect`），既有签名不变。
  - `Orc.Manager`：只做验证（`manager.admin` 是通用代发器，见 `ManagerServer.cs:225`），预期零功能改动。
- Excludes:
  - 磁盘上卡包文件的创建/修改/删除（编辑器职责）、管理器资产库语义（`R2 只增不减` 不变）。
  - 局内热加载（本需求明确不支持）；seat 声明并集语义重设计。
  - 协议层新增方法（`pool.*` 走既有 `tools/*` 与父子私有通道）。

## 现状基线（读码得到的事实，供后续裁决引用）
- 装配唯一实现＝`MatchCardAssembly.Register(dirs)`（`Orc.Packages/Loading/MatchCardAssembly.cs:108`）：**既解析又判定**，结构性非法**抛异常**（`InvalidDataException`，message 为拼接文本），成功时返回装配对象；`LockMerger.Merge` → `LoadListPlanner.Plan` 在内部完成。
- 冲突明细在当前判定链路上被**拍平为字符串**：`LoadListPlanner` 把 `LockConflict`（`LockMerger.cs:15`，含 `Kind/AppId/FirstPackageId/FirstVersion/SecondPackageId/SecondVersion`）压成 `AssetLoadIssue(Code, Domain, AppId, Message, Path)`，结构化字段只剩 `Message`（`LoadListPlanner.cs:122-126`）。
- `match.start` 单局覆盖参数的现状：`AdminApi.ResolvePackDirectories`（`AdminApi.cs:389`）读 `args["packDirectories"]`，为空则回落 `_host.Options.PackDirectories`，随后**无条件追加沙盒包**。
- 装配降级现状（三段，`AdminApi.TryAssemble:319`）：①按入参装配 → ②失败则按 `_lastGoodPackDirectories` 重试 → ③仍失败则剔除沙盒包重试。另在装配前无条件调用 `SyncSandboxMetadata`（`AdminApi.cs:429`，**会写盘**：补 sidecar/lock）。
- 生命周期：`ServerHost` 与串行队列＝进程级，`McpService` 与 `Room`＝房间级、轮换重建（`ServerHost.cs:45-56`、`CreateService:188`）。工具调用在串行队列内执行（`HandleToolsCallAsync:944`），例外是 `IsQueueFreeRead` 白名单（`:997`）。
- 工具门控：`sandboxOnly` 在非 Authoring 下**不登记**（`McpService.cs:148`）；写类调试工具还需 `AllowDebugWrites`（`:154`）；角色由 `InferRole` 静态推断（`:181`），`pool.*` 若无显式 `requiredRole` 会因 `sandboxOnly` 落到 `ToolRole.Debug`，而 `ToolRole.Debug` 要求 `SessionState.DebugFace`（`ServerHost.cs:2014`）；"回环来源"附加判据只对 `DebugToolCatalog.WriteToolNames` 内的名字生效（`ServerHost.cs:1058`）。
- 启动期：`ServerBootstrap.Validate:98` 只校验 `PackDirectories` "可索引"（目录存在 ＋ manifest 可解析），不装卡池 —— 与 R1 末条一致。

## Implementation Steps

### S1: `PackPool`（进程级集合载体）与 `IPoolControl` 契约
- Status: Todo
- Target:
  - 新建 `Orc.Server` 侧进程级组件 `PackPool`：核心状态（已注册包集合）＋ 对账 ＋ 试算 ＋ 注册/注销 ＋ 自适应入口。
  - 新建 `Orc.Mcp` 侧 `PoolContracts.cs` 的 `IPoolControl`（与 `IRoomControl` 并列），由 `PackPool` 实现。
  - `ServerHost` 构造时创建并持有 `PackPool`，注入 `AdminApi` 与每个 `McpService`（与 `IRoomControl` 同一条注入路径，`ServerHost.cs:188-203`）。
- Approach:
  - `PackPool` 一次创建、跨房间轮换存活（集合是进程级状态，R1）；`AdminApi` 现有的 `_lastGoodPackDirectories`（`AdminApi.cs:35`）**迁进 `PackPool`**（它就是 R9 的「最后一次成功输入」），`AdminApi` 不再自存包列表。
  - `IPoolControl` 的方法返回 `Orc.Packages` 的判定模型（`AssemblyInspection` 等），**不在 `Orc.Mcp` 再定义一份 DTO**（`Orc.Mcp` 已引用 `Orc.Packages`）。
  - **禁止窄入口**（"工具把 op+args 转给管理面"）：会在消费者上下文内重入 `AdminApi.InvokeAsync` → 自死锁，且两层各自构造同一份 JSON 必然漂移。管理面与工具面必须调**同一个** `IPoolControl` 实现。
  - 响应投影**只有一处**（`PoolProjection.Render(model, audience)`，S8），管理面与工具两条路径共用，保证逐字段一致。
  - 队列边界：`PackPool` 的核心方法（对账、试算、注册、注销）**同步 ＋ 同一把 `lock`**，核心**绝不**调 `InQueueAsync`。
    - **写操作**（register/unregister）走**自适应入口**：`SerialWorkQueue.IsConsumerContext` 为真 ⇒ 直调核心，为假 ⇒ `InQueueAsync` 包裹后调核心。
    - **`pool.list` 不走队列**：在**同一把 `lock` 内**取快照并预演，读到的一定是某个完整状态（变更前或变更后），不会读到中间态。不为它单独引入读写锁或快照缓存（撤销前一轮"三个 op 都走自适应入口"的说法）。
    - **`pool.list` 加单飞（single-flight）**：同一时刻多个调用合并为一次计算、共享同一份结果，**算完即弃**；不加 TTL、不加条目级缓存、不按连接限流。单飞只作用于 `pool.list`；写操作天然串行（自适应入口 ＋ 队列/`lock`），不需要单飞。
  - 硬约束：工具回调**不得**直接调 `AdminApi.InvokeAsync`（自死锁）；任何新增 `pool.*` 入口只能经 `IPoolControl`。
  - **初值口径（裁决）**：读 spawn 列表 → **剔除与 `<sandboxRoot>/pack` 等价（路径归一化后比较）的项**（R7：沙盒包不属于集合，初值里出现只会诱导调用方去注销它）→ 其余项即集合初值，**保留 spawn 列表原顺序**＝初值的注册序（S4 的顺序口径）。
  - **启动期校验顺序（裁决）**：先过滤沙盒包路径，再对其余项做可索引校验，任一不可索引即**拒绝启动**且错误信息指名目录与原因（`ServerBootstrap.Validate:98-114` 现有行为保留）。依据：spawn 参数是管理员显式配置（配错就 fail-fast），运行期磁盘漂移不是显式输入（用对账自愈），两套口径不混。
- Acceptance:
  - [ ] 房间轮换后集合与「最后一次成功输入」均不变（同一 `PackPool` 实例）
  - [ ] 管理面 op 与工具面对同一操作返回逐字段一致的载荷（含 play 模式脱敏后）
  - [ ] 写操作在工具路径不产生嵌套入队（`IsConsumerContext` 为真时不调 `InQueueAsync`）
  - [ ] `pool.list` 不占队列、与变更互斥于同一把 `lock`，读到完整快照
  - [ ] 并发多个 `pool.list` 调用合并为一次计算、共享结果，且结果不跨时刻保留（无 TTL／无条目缓存）
  - [ ] `AdminApi` 内不再存在包列表类字段
  - [ ] spawn 列表里混入 `<sandboxRoot>/pack` 时，集合初值不含它，且启动校验不因它失败
  - [ ] spawn 里显式给出的某目录不可索引 ⇒ 进程拒绝启动，错误含目录与原因
- Rationale: 集合是进程级状态（R1），`AdminApi` 只是其中一个入口；藏在 `AdminApi` 内部会让工具经管理面间接取状态、耦合方向反了，也无法单独测对账/试算。

### S2: 条目快照刷新、对账预演与真对账
- Status: Todo
- Target: 集合类型内部的"重读 manifest → 判定条目有效性"通路（读面与写面/开局共用同一份有效性判定）
- Approach:
  - **两档语义（裁决）**：
    - **读面＝对账预演**：纯计算，判定每条目有效性、给出"若现在开局会怎样"；**不写集合、不写磁盘**。R4 的「只读、无副作用」措辞不变。
    - **真对账**：只发生在两处——写面（`register`/`unregister`）与开局路径（装配前）。**对账独立提交**：不随变更被拒或开局失败回滚。
  - 执行顺序固定：**先真对账（提交）→ 再用对账后的有效条目跑 `Inspect`**。失效条目**一律不参与试算，也不得挡住新注册**（用已证明不可装载的 lock 当守门员会让结论随操作顺序翻转）。有效性判定与 `Inspect` 同属一份实现。
  - 失效条目在预演结果里标注 `willRemove=true`（S3 字段集），**不显示成"已移除"**（集合确实没变）。
  - 顶层 `ok` 反映"预演之后"：失效条目本身不算 fatal——排除失效条目后其余仍可装配则 `ok=true` ＋ 一条汇总 `warnings`（"N 条失效条目将在下次写面或开局时移除"）；排除后不可装配（如唯一包失效导致零卡）才 `ok=false`。
  - **对账 IO 容错**：读目录本身抛 IO 异常 ⇒ 按「该条目失效」处理，在 `removed[]` 里给 `cause=io`，**不让对账整体失败**（一个坏盘符不能冻结整个注册表）。
  - **同 key 不同路径（磁盘漂移造成）**：保留**先注册者**，移除**后来者**，发 `packset.reconciled`（`cause=duplicate-key`）。**不交给 `Inspect`**：Inspect 的冲突判定按资产版本，同 key 的两份会报成一堆资产级冲突，把"重复副本"误诊成"版本冲突"。**前提假设**：「同 `(id, version)` ⇒ 内容等价」——依据 `manifest.version` 是**内容指纹**；若将来包版本改成非内容指纹，这条规则必须重新裁决。
  - 与 S4「注册时同 key 不同路径 ⇒ 失败」不矛盾：注册是**人的显式动作**（可以要求人先决策），对账是**无人值守的维护**（只能按确定规则收敛）。
  - **事件 `packset.reconciled`**：仅在真对账**实际移除条目**时发（写面与开局路径都发），载荷 `removed[]`＝`{ packageId, version, cause }`；未移除任何条目则不发（避免噪音）。读面预演**不发事件**。
  - **事件不带路径**：通道事件经管理器**原样转发**，可能落到非管理连接上，因此事件载荷一律不含绝对目录（目录只在管理面 op 的响应里给）。
- Acceptance:
  - [ ] 磁盘版本变化后 `pool.list` 显示新版本，下一局直接装载新版本，无需重新注册
  - [ ] 目录换成另一个 `manifest.id` ⇒ 读面 `packages[]` 该条 `ok=false`/`willRemove=true`/带原因，集合不变；下一次写面或开局才移除并发 `packset.reconciled`
  - [ ] `pool.list` 在任意情形下都不改集合、不写盘
  - [ ] 排除失效条目后不可装配时，预演 `ok=false`
  - [ ] 目录读 IO 异常 ⇒ 该条目按失效移除（`cause=io`），其余条目与本次变更照常判定
  - [ ] 同 key 不同路径 ⇒ 保留先注册者、移除后来者，`cause=duplicate-key`；保留者的装载结果不受影响
  - [ ] 真对账无移除时不发 `packset.reconciled`
- Rationale: R2 甲（刷新即移除）与 R4（读面只读）的冲突按"读面预演 / 写面－开局真对账"落到 Constraints 的「对账」定义；对账独立提交保证"集合永远不含已知坏条目"。

### S3: 试算＝开局装配的同一套判定实现（`Inspect` 旁路）
- Status: Todo
- Target: `Orc.Packages` 三处**最小新增**（不改既有签名）：
  - `AssetLoadIssue` 增可空字段 `LockConflict? Conflict`（record 可选参数，缺省 `null`）；`LoadListPlanner` 生成 `LockConflict` 那条 issue 时把原始对象塞进去。
  - `AssetLoadIssueCode` 新增 `EmptyPool`（`IsFatal == false`）。
  - `MatchCardAssembly.Inspect(IReadOnlyList<string> directories) -> AssemblyInspection`：纯读、不 `Bind`、不落地、**不抛**（结构性非法与空输入都走结果）。
  - `Register` 改为内部调用同一个私有实现，失败时再抛与现在**等价且 message 一致**的 `InvalidDataException`；命中 `EmptyPool` 时抛原来那条 `ArgumentException("至少需要一个卡包目录。")`。判定实现只有一份。
- Approach:
  - **禁止** Server 层解析 message 字符串，**禁止**在 Server 复制冲突判定。
  - 判定结论载体（固定字段集，不多不少）：
    - `ok: bool`
    - `packages: [{ directory, packageId, version, guid, ok, errorCode?, error?, willRemove }]` —— **由集合条目逐行构造**（含失效条目）：有效行的 `ok` 与装载摘要来自 `Inspect`；失效行不入 `Inspect`，直接给 `ok=false`/`willRemove=true`/`errorCode`（`error` 自由文本仅管理面与 sandbox 出现）。`errorCode` 取值：`directory-missing` / `manifest-missing` / `manifest-invalid` / `kind-not-cardpack` / `io-error` / `id-changed`。
    - `issues: [{ code, domain, appId, message, path[], fatal, conflict?{kind, appId, firstPackageId, firstVersion, secondPackageId, secondVersion} }]`（`message`/`path[]` 仅管理面与 sandbox 出现，见 S8）
    - `warnings: [string]`
    - `plan: { definitionCount, formalLayers, testLayers, itemCount, cardCount }` —— 只给摘要，不给完整卡清单
  - 四样都要（成功/失败、结构化冲突、告警、装载摘要）；`pool.list` **复用同一个模型**（经 S8 的投影函数按受众裁剪），不得另造投影。
  - **成功判据与 `Register` 完全一致**：能产出有效 plan 且至少一张卡（沿用"未装载到任何卡定义"那条），由同一实现保证。
  - **空输入的契约**：`Inspect([])` 返回 `ok=false` ＋ `AssetLoadIssueCode.EmptyPool`（`fatal=false`），`plan` 给**全零摘要**（`definitionCount:0, formalLayers:0, testLayers:0, itemCount:0, cardCount:0`），**不抛**。`Register` 内部先 `Inspect`，命中 `EmptyPool` 时抛原 `ArgumentException`（既有测试与调用点不变）。新增枚举值后需检查所有 `switch` 是否有 `default` 分支，避免静默吞掉。
  - **`ok` 与 `fatal` 的语义分工**：`ok`＝"现在开局会不会成功"；`fatal`＝"是否阻断本次变更"。空集合是 `ok=false` + `fatal=false`（用户正是在注销，不该被拦）。
  - **试算输入目录列表必须来自集合条目（当前对账后的有效快照）**，不允许调用方临时拼目录；失效条目一律不参与（S2/S4 裁决）。
  - 试算内部顺序固定：先真对账（提交 ＋ 可能发 `packset.reconciled`）→ 再对有效条目 `Inspect`（S2）。
  - 三条降级的分工（裁决）：
    - **lastGood 回退：不进试算**。集合本身即"最后一次成功"；试算失败 ⇒ 拒绝变更、集合不变。
    - **沙盒包剔除：进试算，但不作为拒绝理由**。输入＝集合全部条目 ＋ 沙盒固定包；沙盒包非法时剔除它重算一次，其余 ok 即 `ok=true`、`warnings` 带 sandbox-degraded ＋ 原因；只有剔除后仍非法才 `ok=false`。
    - **`SyncSandboxMetadata`：绝不出现在试算路径**。试算必须纯读、零副作用（`pool.list` 对 viewer 开放）；补元数据只发生在开局装配路径。
- Acceptance:
  - [ ] 同一次变更的试算结论与随后开局的实际装配结果一致（同一实现、同一输入；限定语见 S7）
  - [ ] 变更被拒时集合等于对账后状态（除被对账移除的条目外与操作前一致）
  - [ ] lock 冲突响应载荷携带资产、双方包、双方版本（来自 `AssetLoadIssue.Conflict`，非字符串解析）
  - [ ] 试算路径不写盘（沙盒 sidecar/lock 不被补写）、不产生任何持久化痕迹
  - [ ] `Register` 的既有失败 message 与行为不变（回归测试通过）
  - [ ] `Inspect([])` 返回 `ok=false` ＋ `EmptyPool`（`fatal=false`）＋ 全零 `plan`，且不抛
  - [ ] `Register([])` 仍抛 `ArgumentException("至少需要一个卡包目录。")`（既有调用点不变）
- Rationale: R2 的合法状态不变量要求"检查与装配同源"，否则出现"试算通过、开局失败"的窗口；`Inspect` 旁路让判定结果结构化且不改变既有装配路径语义。

### S4: 变更的事务边界（对账独立提交 → 试算候选 → 写入或整体拒绝）
- Status: Todo
- Target: `PackPool` 的注册/注销事务边界
- Approach（裁决）:
  - 顺序固定：**先真对账（提交）→ 再用对账后的有效条目试算候选变更 → 候选通过才写入条目**。
  - **"候选通过"的判据＝无 `fatal` issue**（不是 `ok=true`）：`ok` 只表示"现在开局会不会成功"，`fatal` 才表示"是否阻断本次变更"。空集合是 `ok=false` + `fatal=false`（`EmptyPool`），因此"注销到空"能通过；lock 冲突／环依赖／声明冲突／重复 lock 条目是 `fatal=true`，必须拒绝。
  - 对账与变更**不是同一笔事务**；变更被拒时集合＝**对账后**的状态，**不回滚对账**（失效条目按定义不可装载，塞回去只会让下一次开局再踩一次）。
  - 变更响应只报本次变更结果 ＋ 变更后的试算结论；变更被拒时额外带 `removedByReconcile` 摘要（数量 ＋ 条目键），让调用方明白"集合为什么和你上次看到的不一样"。`packset.reconciled` 是独立事件，不塞进同一份载荷。**成功**时另发 `packset.changed`（S10），失败不发。
  - 变更只影响下一局（R3），不设待生效字段。
  - **空集合合法（裁决）**：允许注销到最后一个包，不拒绝。依据：空集合在启动时就已存在（play 实例 spawn 不带包、sandbox 初值通常为空），若空算非法，实例一起来就非法。后果由开局承担（明确失败），不由变更动作承担。
  - **注册入参口径（裁决）**：必须 `Path.IsPathRooted`（相对路径 ⇒ `invalid-arguments`，message 明确「需绝对目录路径，不按进程 cwd 解析」）；归一化＝`Path.GetFullPath` ＋ `Path.TrimEndingDirectorySeparator` ＋ Windows 上 `OrdinalIgnoreCase` 比较（末尾分隔符、大小写、`..`、UNC 都在这步统一）。UNC 允许（管理员有权用网络盘），IO 失败按对账 `cause=io` 走失效。
  - **注册与 `<sandboxRoot>/pack` 等价的目录 ⇒ 拒绝（`invalid-arguments`），不静默幂等**：message 固定说明「沙盒固定包每局强制加入，不属于已注册包集合；它不受注册/注销影响，清空请用 `sandbox.reset`」。口径与 S1 初值过滤一致（都不入集合），但反馈不同——初值是配置输入（剔除以告警记录），显式注册是人的动作（必须报错），否则会出现"注册返回成功、`pool.list` 里没有、开局却装上了"的三方错位。
  - **条目顺序（裁决）＝注册序 append，不排序**：`pool.list` 按注册序展示，开局装配也按此序喂 `Register`（`PackPrefixes` 与注册顺序因此确定）；初值顺序＝spawn 列表顺序（S1）。不按 `(id, version)` 排序：会丢掉"谁先注册"（S2 的 `duplicate-key` 规则依赖它），且排序不产生正确性收益——唯一性由 key 保证、确定性由注册序保证。
  - **重复注册（裁决）**：判重以 `(id, version)` 为键，但落库前校验目录——同 key ＋ **同归一化路径** ⇒ **幂等成功**（`ok:true`、`existed:true`、`added:[]`，集合不变，且必须走一次试算/复用当前试算结论并原样返回，避免出现"`existed:true` 但 `ok:false`"）；同 key ＋ **不同路径** ⇒ 失败（`invalid-arguments`，`details.existingDirectory` 给出已在册目录，要求先 `unregister` 再注册——两个目录未来会各自漂移）。
  - **失败面映射（裁决，不新增码）**：
    - 目录不存在 ⇒ `invalid-arguments`
    - 目录不可读/无权限（`UnauthorizedAccessException`）⇒ `path-denied`
    - 其它 IO 失败 ⇒ `invalid-arguments` ＋ `details.cause = "io"`
    - 缺 `manifest.json` ⇒ `invalid-arguments`（「该目录不是卡包」）
    - `kind != cardpack` ⇒ `invalid-arguments`（与编辑器同款措辞）
    - 注销时 id 不存在 ⇒ `invalid-arguments`
    - 注销时 id 存在但版本不符 ⇒ `version-mismatch`，`details.currentVersion` 回报当前快照版本
    - 试算冲突 ⇒ `lock-conflict`（**不折成** `missing-assets`：前者"该换版本"、后者"该补包"，客户端要能分开处理）
    - 试算结构性非法（环依赖等，非冲突）⇒ `missing-assets`（与 `match.start` 同口径）
- Acceptance:
  - [ ] 变更被拒 ⇒ 集合等于对账后状态：除被对账移除的失效条目（有独立事件）外，与操作前一致
  - [ ] 变更被拒的响应带 `removedByReconcile` 摘要（数量 ＋ 条目键）
  - [ ] 变更准入看 `fatal` 不看 `ok`：空集合候选（`ok=false`/`fatal=false`）允许写入；冲突候选（`fatal=true`）被拒
  - [ ] 失效条目的 lock 不参与候选试算，不能挡住新注册（与"先试算含失效条目"的结论相反的用例列入测试）
  - [ ] `InMatch` 期间注册/注销不被拒绝，且当前对局的卡定义、卡组、进行中动作不受影响
  - [ ] 终局轮换出的新 lobby 开局按变更后集合装配
  - [ ] 相对路径、目录不存在、不可读、缺 manifest、kind 非 cardpack 五种入参失败各返回上表指定的码，且集合不变
  - [ ] 同 key 同路径重复注册 ⇒ `ok:true`/`existed:true`/`added:[]`；同 key 不同路径 ⇒ 失败并给 `details.existingDirectory`
  - [ ] 过期版本注销 ⇒ `version-mismatch` ＋ `details.currentVersion`；id 不存在 ⇒ `invalid-arguments`
  - [ ] 注册与 `<sandboxRoot>/pack` 等价的目录 ⇒ `invalid-arguments`，message 含"每局强制加入/清空请用 `sandbox.reset`"；集合不变
  - [ ] 条目顺序＝注册序（初值＝spawn 顺序、新注册 append、`pool.list` 与装配输入同序）
  - [ ] 注销最后一个包 ⇒ 成功（集合变空，`ok=false`/`fatal=false`），不拒绝
  - [ ] 空集合时 `pool.list` 的 `ok=false` ＋ `EmptyPool` ＋ 全零 `plan`（结构照给，不因空而省略）
- Rationale: R2 新措辞「变更被拒时集合＝对账后的状态」+ Constraints「对账独立提交，不随变更被拒或开局失败回滚」。

### S5: 管理面 op（`pool.list` / `pool.register` / `pool.unregister`）
- Status: Todo
- Target: `Orc.Server/Admin/AdminCodes.cs`（`AdminOps` 常量 ＋ `All` 集合）、`AdminApi.InvokeAsync` 分支
- Approach: 三个 op 与 `conn.admit`/`match.start` 同级，最终落到同一个 `IPoolControl` 方法。
  - **写操作**（register/unregister）：drain 期间**拒绝**，与管理面/工具面同口径；**不用** `allowWhileDraining`（那是 `conn.revoke` 的专属先例，它只动会话不动栈）。错误码沿用 `room-rotating` 便于客户端统一处理，但 message 必须可执行且两面**逐字一致**：「房间正在轮换，卡包集合变更需等新 lobby 就绪后重试；`pool.list` 不受影响」。窗口时长不受 `pool.*` 影响（写操作已拒、list 不入队），**不改** `WaitForIdleAsync` 与轮换超时逻辑。
  - **`pool.list`**：**不入 `InQueueAsync`**，直调 `IPoolControl`（lock 内取快照），因此 drain 不影响它。
- Acceptance:
  - [ ] 三个 op 可由管理面调用，未知 op 仍回 `ok:false` 可读码
  - [ ] `AdminOps.All` 含三项，且业务端口 `tools/list` 不路由这些名字（守卫测试）
  - [ ] `pool.list` 在管理面（play 模式的 op 与 sandbox 模式）给完整条目；play 模式下**无论 op 还是工具**都走白名单、不含目录（R4：脱敏按实例模式，不按入口）
  - [ ] drain 内两个写 op 被拒、`pool.list` 成功；写 op 的 message 与工具面逐字一致
  - [ ] `pool.list` 不入队、不在 `InQueueAsync` 路径上
- Rationale: R4「权威入口＝管理面 op」，沿用既有 op 表与队列纪律。

### S6: MCP 工具面（两模式 `pool.list`、sandbox 专属写工具）
- Status: Todo
- Target: `Orc.Mcp/Tools`（新增 `pool.*` 注册组，登记进 `ToolRegistration.RegisterAll`）、`DebugToolCatalog.WriteToolNames`、`McpService` 构造注入 `IPoolControl`（与 `IRoomControl` 同一注入点）
- Approach: `pool.list` 两模式都登记、`readOnly: true`、角色不设限（`ToolRole.None`），并加入 `ServerHost.IsQueueFreeRead` 白名单（`ServerHost.cs:997`）——它是直取快照、不入队（撤销上轮"不入白名单"的说法）；`pool.register`/`pool.unregister` 用 `sandboxOnly: true` + 写类门控（非 Authoring 不登记、`AllowDebugWrites` 才登记、`ToolRole.Debug`、回环来源，需同时进入 `DebugToolCatalog.WriteToolNames` 才有回环判据，`ServerHost.cs:1058`）。`HandleToolsCallAsync` 的 drain 分支**只对 `pool.list` 开白名单**（放行到后续 `Gate`，角色仍按 R4），其余工具维持原样拒绝；两个写工具不加例外（S5 裁决）。工具 handler **直接调 `IPoolControl`**，**不得**绕 `AdminApi.InvokeAsync`。**`pool.register` 不受 `SandboxPathGuard` 约束**（有意设计，非漏检）：集合登记的是"要加载哪些包"，读的是调用方显式给出的目录，与"可写根"无关；资产库本来就在沙盒根外，套守卫会让正常用法变成 `path-denied`，还会与 R2 的"绝对路径"直接打架。安全边界靠"play 模式无此工具 ＋ sandbox 仅回环未绑座位的 debug 连接"，不靠路径守卫。
- Acceptance:
  - [ ] play 实例 `tools/list` 不含 `pool.register`/`pool.unregister`；`pool.list` 存在
  - [ ] sandbox 实例可用工具完成注册/注销，行为与管理面一致（受 debug 角色与"回环＋未绑座位"门控）
  - [ ] viewer 角色调用 `pool.list` 不被角色拒绝
  - [ ] `Connected`/`Pending` 调 `pool.list` ⇒ `not-admitted`（**不开** `room.info` 那种例外：R4 的"角色不设限"是角色维度，不是许可维度）
  - [ ] 工具描述写明"`pool.list` 是重操作（完整走一遍资产注册与分层计划）、不缓存、请勿高频轮询"
  - [ ] drain 内 `pool.list` 工具成功、两个写工具被拒（message 与管理面逐字一致）
  - [ ] drain 分支的白名单只含 `pool.list` 一项，其余工具行为不变（守卫测试）
- Rationale: R4 + 既有 `sandboxOnly`/角色/回环三重门控（`McpService.cs:148-171`、`ServerHost.cs:1050-1063`）。

### S7: 开局装配改吃集合（移除 `match.start` 的单局覆盖）
- Status: Todo
- Target: `AdminApi.ResolvePackDirectories` / `StartCoreAsync` / `MatchLaunchOptions`
- Approach: 本局输入＝集合全部条目 ＋ 沙盒固定包（R5），无第二条路径。装配前先做一次真对账（S2）：移除**保留**，不因本局后续失败（卡组不合法／AI 无法生成／引擎拒绝，`AdminApi.cs:203-263` 的早退路径）而回滚，`packset.reconciled` 照发。「不留半装载态」由对局装配自身的回退负责（R9），不把两件事绑在一起。`match.start` 的拒绝判据（裁决）：**看键是否存在**，`args` 里出现 `packDirectories` 即报错——`[]`（"清空/用空池"）、`null`（意图不明）、非数组类型都算携带，一律不再回落 spawn 列表（现实现的 `dirs.Count == 0` 回落正是 R5 禁止的静默歧义）。错误码用既有 `invalid-arguments`（不新增码），message 固定包含指引句「卡池集合改由 `pool.register` / `pool.unregister` 管理；本参数已移除」，工具面与 op 面逐字一致。「最后一次成功输入」随 `PackPool` 迁走后（S1），回退逻辑调用 `PackPool.LastGood`，但**不进试算**（S3 裁决）；一旦真的走到回退分支，必须产出可观测告警/事件（"当前集合不可用、已用旧集合开局"），不允许静默通过。
- Acceptance:
  - [ ] `match.start` 带 `packDirectories` ⇒ 明确错误、房间停留 `Lobby`
  - [ ] 判据是"键存在"：`[]`、`null`、非数组类型三种都报错，且都不回落 spawn 列表
  - [ ] 错误码＝`invalid-arguments`，message 含 `pool.register`/`pool.unregister` 指引；工具面与 op 面逐字一致
  - [ ] 本局实际装载包集合＝`pool.list` 条目（＋合法沙盒包）——**仅在无并发磁盘变化、且未触发任何降级时成立**（R5 限定语）
  - [ ] 装配失败仍按"最后一次成功输入剔除沙盒包重试一次"降级
  - [ ] 走到 lastGood 回退或沙盒剔除降级时，响应与通道事件都带明确告警（非静默），并显式报告降级类型
  - [ ] 开局失败（卡组／AI／引擎拒绝）不回滚开局前的对账移除；该次移除的 `packset.reconciled` 已独立发出
  - [ ] 降级/失败响应在**管理面 op** 里带 `assemblyInput: { packages: [{id, version, directory}], sandboxIncluded, usedLastGood, sandboxDropped, reconciled }`；通道事件里同一信息**不含目录**
  - [ ] 不做"与最近一次试算的差异"诊断（试算可能没发生过，基线不存在）；定位靠"输入快照 ＋ 降级标记"与 `pool.list` 人工对照
  - [ ] 空集合开局：错误码仍 `missing-assets`，message 特化为「卡包集合为空（请先 `pool.register`）」，**不得**输出通用的"卡池装配失败"（否则管理员会去查 lock 而不是查集合）
- Rationale: R5 单一真源。

### S8: 响应投影（play 白名单 / sandbox / admin）
- Status: Todo
- Target: `PoolProjection.Render(model, audience)`（`audience ∈ { remote-play, sandbox, admin }`），工具面与 op 面共用
- Approach（裁决）:
  - **一个投影函数、两种受众**：`remote-play` 走**字段白名单**（只列允许出现的键），不是"删掉已知的路径字段"——以后新增任何自由文本字段默认不会泄露，漏的是"少给"而不是"多给"。
  - `remote-play` 的 `issues[]` 裁剪成最小可读集 `{ code, domain, appId, fatal }`（冲突类附 `conflict` 的 id/version，冲突本身不含路径）；`message`、`path[]`、`sourcePath`、`directory` 一律不出现。
  - `packages[].error`（自由文本）只在管理面与 sandbox 出现；play 模式由 `errorCode` 映射成**固定短语常量表**（如 `directory-missing → "目录不存在"`），天然不含路径。**不做文本脱敏**（盘符/UNC/正反斜杠/引号/大小写，正则替换永远漏）。
  - `sandbox` 与 `admin` 给完整模型，两者之间不再分档（R4 已定 sandbox 含目录）。工具面与 op 面共用这一个投影函数，禁止各自裁剪（R4 的"逐字段一致"靠它保证）。
- Acceptance:
  - [ ] play 实例 `pool.list`（工具与 op）响应中不存在任何绝对路径（含 `error`/`message`/`path[]` 等自由文本）
  - [ ] play 模式的 `issues[]` 只含 `{ code, domain, appId, fatal, conflict? }`
  - [ ] `errorCode` 在**三种受众**都存在（管理面/sandbox 额外带 `error` 自由文本），字段集不因入口不同而漂移
  - [ ] sandbox 实例 `pool.list` 返回绝对目录与完整 `error`/`message`
  - [ ] 投影函数只有一个（白名单式），新增字段默认不进 play 模式
- Rationale: R4 路径脱敏 + "同一投影两条路径"（S1）；白名单是"未来新增字段默认安全"的唯一可靠形态。

### S9: 沙盒固定包的角色与降级
- Status: Todo
- Target: `AdminApi.SandboxPackDirectory` / `SyncSandboxMetadata` / `TryAssemble` 的剔除分支
- Approach: 沙盒包不进集合、每局强制加入。试算输入包含它，但**剔除降级不作为拒绝集合变更的理由**（R7）：剔除后重算一次，其余 ok 即 `ok=true` 且 `warnings` 带 `sandbox-degraded` ＋原因（S3 裁决）；只有剔除后仍非法才 `ok=false`。`SyncSandboxMetadata` 只在开局装配路径执行，**不得进入试算**（试算零副作用）。
  - **两个标记不可合并**：
    - `sandbox-degraded`＝试算里沙盒包非法被剔除后仍可装配（语义："开局已经少了它"）。
    - `sandbox-metadata-unsynced`＝**只读探测**沙盒包缺 sidecar/lock（语义："开局会自动补齐，本次结论可能比实际悲观"）。探测只查文件是否存在/是否缺失，**不得**为了算得更准去调 `SyncSandboxMetadata` 或任何写盘路径；也**不预测**"补齐一定成功"（补齐可能因写权限/坏文件失败，届时走剔除降级，那是另一条报告路径）。
  - `pool.register` **不受** `SandboxPathGuard` 约束（安全边界靠"play 模式无此工具 ＋ sandbox 仅回环未绑座位的 debug 连接"，不靠路径守卫）；其独立最小校验只有三条：绝对路径、目录存在、`manifest.json` 可读可解析（S4）。
- Acceptance:
  - [ ] 注销任何包都不会移除沙盒固定包；`sandbox.reset` 不影响集合
  - [ ] 沙盒包损坏时注册仍成功（响应 `warnings` 带沙盒包告警），开局按剔除降级
  - [ ] 沙盒包损坏时 `pool.list` 的试算结论仍 `ok=true` 且带同一告警
  - [ ] 沙盒包缺 sidecar/lock 时 `pool.list` 带 `sandbox-metadata-unsynced`，且该次调用不产生任何写盘（探测只读）
  - [ ] `sandbox-degraded` 与 `sandbox-metadata-unsynced` 同时可能出现的用例各自独立断言，不互相顶替
  - [ ] `<sandboxRoot>/pack` 不存在且集合为空 ⇒ 输入为零目录 ⇒ `EmptyPool`（不是"至少需要一个卡包目录"的异常路径）
- Rationale: R7 + S3 的三条降级分工裁决。

### S10: 观测面与命名边界（含对外破坏性变更）
- Status: Todo
- Target: `McpService.Pool`（本局装配报告，`McpService.cs:216`）、`instance.info` 的包字段、通道事件
- Approach:
  - **`sandbox.state` 不加集合段**：它报"本局装配结果"，集合是"服务器配置状态"，生命周期与读者都不同；混进去会让一个调试工具同时承载两套语义（正是 `pool` 一词两义）。`sandbox.state.pool` 字段含义**不变**。可发现性靠工具描述解决——`pool.list` 的描述里写明"下一局开局会用到的卡包集合"。
  - **变更事件 `packset.changed`**：写操作**成功后**发一条，载荷＝`{ added: [{id, version}], removed: [{id, version}], ok, itemCount, cardCount }`；**不含绝对目录**。失败不发（失败信息在响应里）。
  - 现有 `MatchPoolReport` 表达"本局装配结果"，新集合表达"下一局的输入"，两者不混为一份状态（R3 明确 `pool.list` 只有一份集合状态）。`pool.list` 载荷**复用 S3 的判定结论模型**（唯一模型 ＋ S8 唯一投影），不另造第二套。
  - **`instance.info.packDirectories` → `initialPackDirectories`（对外可见的破坏性变更）**：语义＝spawn 初值（历史信息），并剔除沙盒包路径。迁移：不保留旧字段本体；若确有消费方读旧名，响应里给 `packDirectoriesRenamedTo: "initialPackDirectories"`。
  - **命名边界（三组名字，写进「对外变更与迁移」）**：
    - `pool.list` / `pool.register` / `pool.unregister`：**op**，操作的是「已注册包集合」（R4 已确认，不改名）。
    - `packset.changed` / `packset.reconciled`：**集合侧事件**（本轮新增，不破坏既有消费方）。
    - `pool.assembled` / `pool.assemble.failed` / `sandbox.state.pool`：**本局装配结果**，零改动（管理器原样转发，改名要动转发消费方与既有交接文档，收益为零）。
- Acceptance:
  - [ ] `sandbox.state` 的结构与 `pool` 字段含义不变；其中不出现集合段
  - [ ] `pool.list` 的工具描述写明"下一局开局会用到的卡包集合"
  - [ ] 写操作成功后发 `packset.changed`，载荷无绝对目录；失败不发
  - [ ] `packset.reconciled` 载荷 `removed[]` 无绝对目录（仅 `packageId`/`version`/`cause`）
  - [ ] 只有一份集合状态可读（`pool.list`）；`instance.info` 不再有 `packDirectories`
  - [ ] `instance.info.initialPackDirectories` 不含沙盒包路径
- Rationale: R3/R4 的可观测面要求 + 术语表「卡池」与「已注册包集合」并列（`CONTEXT.md:518-528`）：op 名沿 R4 钉死，歧义靠事件前缀与文档一句话消解。

### S11: 管理器侧（零功能改动验证）
- Status: Todo
- Target: `Orc.Manager`（`ManagerServer.cs:225` `manager.admin` 通用代发、`manager.spawn` 的 `packDirectories`）
- Approach: 管理器不解析、不缓存集合（R6）；`manager.admin` 已按 `op` 原样转发，`pool.*` 应自动可用 —— 需验证其在 `payload` 外形上不做裁剪。另两处收尾：`AssetLibrary.cs:49` 的「解析后的包目录（含实例级沙盒包）」注释要改掉，管理器**尽量不再把沙盒包下发进 `packDirectories`**；实例侧仍保留等价路径过滤（S1），两道都要有。
- Acceptance:
  - [ ] `manager.admin` 能代发三个 op，效果与实例内管理面一致
  - [ ] 管理器侧不存在集合状态；实例重启后不"恢复"上次注册
  - [ ] `manager.spawn` 传入的 `packDirectories` 等于实例集合初值（不含沙盒包）
  - [ ] `AssetLibrary` 不再把沙盒包算进 `packDirectories`（注释与实现一致）
- Rationale: R6。

### S12: 测试与不变量守卫
- Status: Todo
- Target: `tests/`（服务端集成与守卫）
- Approach: 覆盖 R2 的逐条 acceptance ＋ 合法状态不变量（每次真对账之后按集合开局必能装配）＋ `AdminOps.All` 与工具表的守卫测试（既有范式：`AdminOps.All` 注释即为此用途）＋ 对外变更面（`match.start` 键存在即拒、`initialPackDirectories` 改名）。
- Acceptance:
  - [ ] 不变量：**集合非空时**，每次真对账之后按集合（＋沙盒包）装配均可成功；含"变更被拒但对账已提交"的序列；空集合是合法状态、开局必然失败并明确报告
  - [ ] 对账事件：仅在实际移除条目时发出，`removed[]` 含 `id`/`version`/`cause`（`io` 场景有专门用例），不含绝对目录
  - [ ] 变更事件：写操作成功发 `packset.changed`，失败不发；载荷无绝对目录
  - [ ] play/sandbox 两模式的 `tools/list` 差异有守卫测试
  - [ ] 过期版本号注销、重复注册、目录被替换三类边界各有用例
  - [ ] `match.start` 带 `packDirectories`（`[]`/`null`/非数组）三态各有用例，房间停 `Lobby`
  - [ ] 空集合：注销到空成功、`pool.list` 给 `EmptyPool`、开局 message 特化；`<sandboxRoot>/pack` 存在时该用例不适用（非空输入）
  - [ ] `AssetLoadIssueCode.EmptyPool` 的新增不破坏既有 `switch`（无 `default` 的写法需逐一补齐）
- Rationale: R2 Constraints「合法状态不变量」需要可执行证据。

### S13: 对外说明与收尾
- Status: Todo
- Target: `pool.*` 的工具描述、`docs/卡池运行期注册注销/` 下的对外说明、交接文档
- Approach: 把三组名字的语义边界（S10）、`pool.list` 的重操作与用法建议（"变更后查一次、开局前查一次，不要定时轮询"）、`pool.register` 的路径口径与"不受可写根约束"都写进**对外可见的位置**，不只写代码注释。另：`docs/服务器管理器-长期生命周期/PROGRESS.md:64` 引用了 `sandbox.state.pool`，确认其语义未变即可，不需要改。
- Acceptance:
  - [ ] `pool.list` 的 MCP 工具描述含"重操作/不缓存/勿高频轮询"与"下一局开局会用到的卡包集合"
  - [ ] 对外说明含三组名字边界、`initialPackDirectories` 改名迁移、两个 `packset.*` 事件
  - [ ] 与既有交接文档无冲突表述（`sandbox.state.pool` 含义不变）

## 对外变更与迁移（破坏性）
1. **`match.start` 不再接受 `packDirectories`**：键出现即 `invalid-arguments`，message 含「卡池集合改由 `pool.register` / `pool.unregister` 管理；本参数已移除」。原"传空数组＝用实例默认池"的行为**取消**（此前是静默回落，现为硬错误）。
2. **`instance.info.packDirectories` → `initialPackDirectories`**：语义变为"spawn 初值（历史信息）"，且剔除沙盒包路径。旧字段本体不保留；读旧名的消费方会在响应里看到 `packDirectoriesRenamedTo: "initialPackDirectories"` 提示字段。
3. **新增能力**：管理面 `pool.list` / `pool.register` / `pool.unregister`；MCP 工具 `pool.list`（两模式、只读、角色不限）、`pool.register` / `pool.unregister`（仅 sandbox ＋ debug 角色 ＋ 回环）。
3.1 **新增字段**：`packages[].errorCode`（三种受众都有）、`packages[].willRemove`、`issues[].fatal`、判定结论的 `plan` 摘要。
3.2 **play 模式收窄**：`pool.list` 在 play 模式只给字段白名单（`issues[]` 收成 `{code, domain, appId, fatal, conflict?}`），`error`/`message`/`path[]`/`directory` 不出现；sandbox 与管理面给完整模型。客户端不应假设 play 与 sandbox 字段集相同。
4. **新增事件**（新增，不破坏既有消费方）：`packset.changed`（写操作成功）、`packset.reconciled`（实际移除失效条目）。两者载荷均**不含绝对目录**（通道事件经管理器原样转发，可能落到非管理连接）。
5. **命名边界**（避免下一个人再问一遍）：
   - `pool.list` / `pool.register` / `pool.unregister`＝**op**，操作「已注册包集合」；名沿 R4，不改。
   - `packset.changed` / `packset.reconciled`＝**集合侧事件**。
   - `pool.assembled` / `pool.assemble.failed` / `sandbox.state.pool`＝**本局装配结果**，零改动。
   - 若日后要把 op 统一成 `packset.*`，那是单独的破坏性迁移，不在本次范围。
6. **管理器**：`manager.admin` 无需改动即可代发 `pool.*`；`manager.spawn` 的 `packDirectories` 不再包含沙盒包路径。
