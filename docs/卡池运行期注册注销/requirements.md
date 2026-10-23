# Requirements — 卡池运行期注册与注销

把「本实例加载哪些卡包」从 spawn 时一次性给定的启动参数，变成开局前可增删的运行期状态；管理员不重启进程、不改启动参数，即可调整下一局的卡池。

## Scope
- Includes: 实例级包集合；注册/注销动作及其入参；开局装配的卡包来源；play 与 sandbox 两种模式下的工具可见性；集合变更与活跃对局的关系；变更结果的可观测面（列表、失败原因）。
- Excludes: 管理器资产库语义变更（`R2 只增不减` 不变）；磁盘上卡包文件的创建/修改/删除（编辑器职责）；局内热加载卡包（明确不支持，另见现状）；seat 声明并集语义的重新设计（仅在冲突时裁决并记录）。

## Constraints
- 卡池仍按房间构建，开局（`match.start`）是唯一装配点（沿用 `R9 卡池重装`）。
- 集合变更失败不得破坏当前可用卡池：沿用「最后一次成功集合」回退一次。
- 沙盒包仍是实例级固定包，强制加入每局并集，**不受**注册/注销影响。
- 管理权来源＝进程所有权；新增工具不得给线上连接（controller/viewer）额外的卡池管理权限。
- 进程启动仍不预装卡池，只校验可索引（沿用 `R3`）。
- **合法状态不变量**：**集合非空时**，它在**每次对账之后**必然可装配。变更＝一次性完成「更新 ＋ 合法性检查」，检查不通过就整体拒绝；不存在「先改后验」「半套生效」「待下次开局才发现」的窗口。合法性判定与开局装配使用**同一套**判定实现，不复制规则。空集合是**合法状态**（spawn 初值本就可能为空），但开局必然失败并明确报告，不是「非法状态」。
- **对账（reconcile）**：外部磁盘变化可能使条目失效（目录消失、`manifest.id` 被替换）。对账＝按当前磁盘状态判定每条目有效性、剔除失效条目；**对账只会发生在写面（注册/注销）与开局路径**。读面只做「对账预演」——纯计算、不写集合、不写磁盘，并给出「若现在开局会怎样」的结论（与开局实际结果一致）。**对账独立提交**：它一旦移除条目就生效，不随随后的变更被拒或开局失败而回滚。

## Requirement Items

### R1: 包集合的归属与生命周期
- Status: Confirmed
- Scenario/Trigger: 服务器实例存活期内，管理员要在开局前调整本实例可用的卡包。
- Behavior: 实例持有**进程级、可变**的「已注册包集合」；初值＝spawn 参数 `packDirectories`。集合只决定**本实例**下一局的卡池装配输入：不影响其他实例，不改写管理器资产库，不写入任何持久化配置；进程重启后回到 spawn 初值。
- Acceptance:
  - [ ] spawn 完成后，集合内容等于该实例的 `packDirectories` 显式项（沙盒固定包单独计入，不由本集合表达）
  - [ ] 无注册/注销发生期间，多次开局使用同一集合
  - [ ] 集合变更不落盘；重启实例后集合恢复为 spawn 初值
  - [ ] 同机其他实例、以及管理器资产库的注册结果不受任何影响
  - [ ] 进程启动仍不预装卡池（不因集合存在而提前装配）
- Terminology: [服务器实例](../CONTEXT.md)、[卡池重装](../CONTEXT.md)

### R2: 注册与注销的标识与冲突判定
- Status: Confirmed
- Scenario/Trigger: 管理员在开局前决定「本实例还要多加载一个包」或「不再加载某个包」。
- Behavior:
  - **集合键＝（`manifest.id`, `manifest.version`）**，即包 id ＋ 包版本；同一 `manifest.id` 的多个版本**允许并存**，不预设包级互斥规则。
  - **注册**入参＝绝对目录路径。实例读该目录的 `manifest.json`（校验 kind=cardpack、可解析）后，以读到的 `(id, version)` 登记；集合条目同时记住目录路径（装配时要读文件）。
  - **集合条目以目录为实体**，`(id, version)` 是**每次读盘刷新**的快照：注册登记目录后，每次注册表更新、`pool.list`、开局试算都会重读该目录的 `manifest.json` 并刷新快照。磁盘内容变化（编辑器 `promote` 后版本改变）⇒ 同一目录的版本号自动跟随，无需重新注册。
  - **注销**入参＝`(packageId, version)`，与**当前快照**精确匹配；用过期版本号注销 ⇒ 失败并回报当前版本，不隐式按 id 全删。
  - 若重读发现目录的 `manifest.id` 已变化（目录被替换成另一个包）或目录已消失 ⇒ 该条目为**失效条目**：读面只报告（`ok=false` ＋ 原因 ＋「将在对账时移除」），**不改写集合**；真正的移除发生在下一次写面或开局路径的对账中，并产生可观测记录。不存在的条目不存在"被静默跳过"的可能。
  - **每次注册表更新（注册或注销）立即重算「合并后的 lock」**（各包 `lock.formal.json` ＋ `lock.test.json` 按域合并 → 装载计划），判定口径完全复用 `LockMerger`：同一资产 GUID 重复＝去重、同一 `(kind, appId)` 不同 GUID＝冲突。
  - **试算无冲突** ⇒ 变更生效；**有冲突或计划非法** ⇒ **拒绝该次变更**，集合保持变更前的状态（不落半套），响应携带冲突明细（资产、双方包、双方版本）。
  - 集合变更不落盘；进程重启后回到 spawn 初值。
- Acceptance:
  - [ ] 注册只给目录路径即可，响应回报该包解析出的 `id` 与 `version`
  - [ ] 同一 `(id, version)` 重复注册＝明确反馈（已存在），不产生第二条条目
  - [ ] 同一 id 不同版本可同时存在于集合中
  - [ ] 注销必须同时给出 id 与 version；只给 id 不足以移除
  - [ ] 磁盘内容变化后（版本指纹改变），`pool.list` 显示新版本、下一局直接装载新版本，无需重新注册
  - [ ] 用过期版本号注销 ⇒ 失败并回报当前版本
  - [ ] 目录消失或被换成另一个 `manifest.id` ⇒ 读面报该条目失效（`ok=false` ＋ 原因），集合内容不变
  - [ ] 下一次写面或开局对账时该条目被移除，并产生可观测记录（事件/告警）
  - [ ] 每次注册/注销都返回合并 lock 的试算结论（成功条目数／冲突明细）
  - [ ] 变更被拒时，集合等于**对账后**的状态：除被对账移除的失效条目（有独立事件）外，与操作前一致
  - [ ] 允许注销最后一个条目，集合可为空；空集合下变更本身成功，`pool.list` 的 `ok` 由「集合 ＋ 沙盒固定包」共同决定：沙盒包存在时仍 `ok=true` 并带 `registry-empty` 告警；两者皆空时 `ok=false` 并给出「无包可装载」的结构化说明
  - [ ] 空集合时开局失败，错误指明「集合为空」而非笼统的卡池装配失败
  - [ ] 目录不可读／缺 `manifest.json`／kind 非 cardpack ⇒ 注册被拒且不改变集合
- Terminology: [卡包](../CONTEXT.md)、[资产版本](../CONTEXT.md)、[正式 lock](../CONTEXT.md)、[测试 lock](../CONTEXT.md)

### R3: 变更的生效时机与活跃对局
- Status: Confirmed
- Scenario/Trigger: 实例存在 `InMatch` 房间时，管理员注册或注销卡包。
- Behavior: 变更**立即作用于集合**（含试算校验），但**只影响下一次开局**。当前对局持有的是开局时装配好的卡库，与集合无引用关系：即使注销的正是本局在用的包，本局照常打完，只是下一局不再加载它。不设「待生效队列」，不出现「已登记但未生效」的中间态。
- Acceptance:
  - [ ] `InMatch` 时注册/注销不被拒绝，返回试算结论
  - [ ] 变更后当前对局的卡定义、卡组、进行中的动作完全不受影响
  - [ ] 终局轮换出的新 lobby 开局时，按变更后的集合装配
  - [ ] 不存在「待生效」字段；`pool.list` 只有一份集合状态
- Terminology: [房间轮换](../CONTEXT.md)、[卡池重装](../CONTEXT.md)

### R4: 入口形态与鉴权
- Status: Confirmed
- Scenario/Trigger: 管理员（或沙盒里的 agent）要查看/变更当前包集合。
- Behavior:
  - **写操作（register/unregister）的权威入口＝管理面 op**（`pool.register` / `pool.unregister`），与 `conn.admit` / `match.start` 同级，只由进程主人（含经 `manager.admin` 代发的管理器）调用。
  - **只读（`pool.list`）**：管理面 op ＋ MCP 工具**两种模式都注册**，角色不设限（只读、无副作用）。
  - **写操作的 MCP 工具**：**仅 sandbox 模式**注册（沿用 `sandboxOnly` + debug 角色 + 回环未绑座位 的既有门控）；play 模式的工具表中**不存在**。
  - **路径脱敏**：play 模式下的 `pool.list`（工具与 op 均然）**不返回绝对目录**，只给 `id` / `version` / 试算结论；管理面与 sandbox 模式返回完整条目（含目录）。
- Acceptance:
  - [ ] play 实例的 `tools/list` 不含 `pool.register` / `pool.unregister`
  - [ ] play 实例的 `pool.list` 响应中不存在任何绝对路径字段
  - [ ] sandbox 实例可用工具调用注册/注销（受 debug 角色与「回环 ＋ 未绑座位」门控）
  - [ ] `manager.admin` 能代发三个 op，效果与实例内管理面一致
  - [ ] viewer 角色调用 `pool.list` 不被角色拒绝（纯只读面）
- Terminology: [管理面](../CONTEXT.md)、[调试面](../CONTEXT.md)、[进程主人](../CONTEXT.md)

### R5: 开局卡池的来源（唯一真源）
- Status: Confirmed
- Scenario/Trigger: 管理员执行 `match.start` 时，实例如何决定本局加载哪些包。
- Behavior:
  - 本局卡池输入＝**当前集合的全部条目 ＋ 沙盒固定包**，没有第二条路径。
  - `match.start` 的 `packDirectories` **单局覆盖参数移除**；调用方仍传该字段 ⇒ 明确报错（提示改用 `pool.register`），**不开局**，不静默忽略。
  - spawn 参数 `packDirectories` 仍保留，但仅作为集合**初值**（R1），不再被 `match.start` 直接读取。
  - 装配失败时的回退沿用 `R9`：用最后一次成功的输入剔除沙盒包重试一次，仍失败才拒绝开局。
- Acceptance:
  - [ ] `match.start` 不再接受 `packDirectories`；携带该字段时返回明确错误且房间停留 `Lobby`
  - [ ] 本局实际装载的包集合＝`pool.list` 中集合条目（＋合法沙盒包）
  - [ ] 在**无并发磁盘变化、且开局未触发任何降级**（lastGood 回退／沙盒包剔除）的前提下，`pool.list` 的试算结论与开局实际装载结果一致（同一实现、同一输入）
  - [ ] 开局触发了降级时，响应必须显式报告降级类型与实际输入（不得静默）
- Terminology: [卡池](../CONTEXT.md)、[卡池重装](../CONTEXT.md)

### R6: 管理器的角色边界
- Status: Confirmed
- Scenario/Trigger: 管理员经管理器操作某实例的卡包集合。
- Behavior:
  - 管理器**不新增** `manager.pool_*` 专用工具；一律经既有 `manager.admin` 代发 `pool.*` op，管理器不解析、不缓存集合内容。
  - `manager.spawn` 的 `packDirectories` 语义不变：实例集合初值。
  - 管理器仍不持有任何运行中卡池状态（沿用 `R2 资产职责`）。
- Acceptance:
  - [ ] `manager.admin` 能代发 `pool.list` / `pool.register` / `pool.unregister`
  - [ ] 管理器侧不存在集合状态；实例重启后管理器不"恢复"上次注册
  - [ ] `manager.spawn` 传入的 `packDirectories` 等于实例集合初值
- Terminology: [管理器](../CONTEXT.md)、[资产库](../CONTEXT.md)

### R7: 沙盒模式的兼容语义
- Status: Confirmed
- Scenario/Trigger: `mode=sandbox` 的实例使用注册/注销能力。
- Behavior:
  - 沙盒实例与 play 实例持有**同一套**集合与同一套 op；初值同样来自 spawn 的 `packDirectories`（沙盒实例通常为空）。
  - **沙盒固定包**（`<sandboxRoot>/pack`）**不进集合**、不受注册/注销影响，每局强制加入；要清空它只能走 `sandbox.reset`（重建脚手架），该操作不改集合。
  - 工具面：沙盒模式额外注册 `pool.register` / `pool.unregister`（`sandboxOnly` ＋ debug 角色）；`pool.list` 在沙盒模式返回完整条目（含目录）。
  - **试算与开局装配的输入都包含沙盒固定包**；沙盒包自身非法时按 `R9` 的「剔除沙盒包重试」降级并告警，**不作为拒绝集合变更的理由**（沙盒包坏掉不该让管理员无法登记其他包）。
- Acceptance:
  - [ ] sandbox 实例可经工具完成注册/注销，行为与 play 实例的管理面一致
  - [ ] 注销任何包都不会移除沙盒固定包；`sandbox.reset` 不影响集合
  - [ ] 沙盒包损坏时注册仍可成功（响应带沙盒包告警），开局按剔除降级
  - [ ] 沙盒模式 `pool.list` 返回绝对目录；play 模式不返回（R4）
- Terminology: [沙盒包](../CONTEXT.md)、[沙盒模式](../CONTEXT.md)
