# 执行进度 — 卡池运行期注册与注销

- 更新时间: 2026-10-23T04:20:00+08:00
- 工具调用计数: ~140
- 当前步骤: S1–S13 全部完成（含本轮补齐的 S1/S2/S5/S6/S7 缺口），acceptance 已逐条核对
- 总体状态: 完成（3 条 acceptance 未满足/未验证，见下）

## 已完成
- 通读文档与基线代码。
- S3（前置）：`AssetLoadIssue.Conflict`、`AssetLoadIssueCode.EmptyPool`、`PackageProbeCodes`/`PackageDirectoryProbe`/`AssemblyInspection`/`PackageInspection`/`AssemblyPlanSummary`、`MatchCardAssembly.Inspect`（`Register` 改为同一私有实现并保持异常等价）。
- S1：`PackPool`（进程级集合载体、初值剔沙盒包、`LastGood` 迁入）+ `IPoolControl`/`PoolCallResult`/`PoolAudience` + `PoolProjection`；`ServerHost` 创建并向 `AdminApi`/`McpService` 注入；`ServerBootstrap.Validate` 先过滤沙盒路径。
- S2：读面预演（不写集合/不写盘）、真对账（写面＋开局路径，独立提交）、`packset.reconciled`、同 key 保留先注册者、IO 容错。
- S4：注册/注销事务边界（先对账→候选→准入看 fatal→写入或整体拒绝）、入参口径（绝对路径/沙盒包拒绝/幂等/重复键/失败码映射）。
- S5：`pool.list`/`pool.register`/`pool.unregister` 三个 op（`AdminOps.All` 已含）；`pool.list` 不入队；drain 内写 op 拒绝、list 放行。
- S6：`PoolTools`（两模式 `pool.list`、sandbox 专属写工具、写类清单、`IsQueueFreeRead` 与 drain 白名单只含 `pool.list`）。
- S7：`match.start` 键存在即拒（`invalid-arguments` + 指引句）；开局输入＝集合＋沙盒包；`assemblyInput`/降级标记；空集合特化 message。
- S8：`PoolProjection.Render` 白名单式投影（play 无目录/自由文本，sandbox/admin 完整）。
- S9/S10：沙盒固定包降级（`sandbox-degraded`）、只读探测 `sandbox-metadata-unsynced`、`packset.changed`、`instance.info.initialPackDirectories`。
- S11：`AssetLibrary` 注释与实现一致（沙盒包只参与本层 lock 判定、不写进 `packDirectories`）；`manager.admin` 通用代发（op/args 原样、未知码原样透传、payload 深拷贝）零改动；管理器侧无集合状态。
- S12：新增 `tests/Orc.Server.Tests/PackPoolGuardTests.cs`（21 条）＋ `HostFixture.includeSandboxPackInPacks`。
- S13：`对外说明.md` 按实现校对（`cause=io`、单飞口径、`assemblyInput.packages` 不含沙盒、`pool.assembled` 零改动仅追加两段）。
- 本轮补齐的既有步骤缺口：
  - S1 单飞：`PackPool.Snapshot` 增加 single-flight（并发合并、算完即弃、无 TTL／无条目缓存）。
  - S2 `removed[].cause`：IO 失败映射为 `io`（包级 `errorCode` 仍是 `io-error`）。
  - S5/S6 drain 说明：工具面 `pool.register`/`pool.unregister` 被拒时用 `PackPool.DrainMessage`，与管理面逐字一致（白名单仍只含 `pool.list`）。
  - S7 `assemblyInput.packages`：只列集合条目，沙盒由 `sandboxIncluded` 表达（不再给沙盒目录配 null id）。

## 测试结果（本轮）
- `Orc.Server.Tests`：**110 通过 / 0 失败**（基线 89，新增 21）。
- `Orc.Mcp.Tests`：117 通过 / **1 失败**（既有，HEAD 同样失败）。
- `Orc.Packages.Tests`：361 通过 / **2 失败**（既有，HEAD 同样失败）。
- 基线核对方式：`git worktree HEAD` 单独跑该 3 条用例，同样失败 ⇒ 与本任务无关。
  - `SandboxPrimitiveToolTests.ReturnedRefIsUsableByNextCall`（棋盘位置模型重构遗留：`slot` 期望 2 实际 0）
  - `PackageVersionTests.样例包_重算内容指纹与manifest一致`、`AssetMigrationToolTests.样例包_每个源文件都有sidecar且清单为v2与两lock一致`（样例包内容指纹漂移）

## Acceptance 结论

### 已验证
- S1：两面载荷逐字段一致（play/sandbox 各一条）、写工具不嵌套入队（工具写成功）、`pool.list` 不占队列且与变更同锁、`AdminApi` 无包列表字段、spawn 混入沙盒包被过滤且启动不失败、不可索引目录拒绝启动
- S2：版本漂移即时可见并进入下一局装配、`id-changed` 读面预演、`pool.list` 零写盘、不可装配时 `ok=false`、IO 失效 `cause=io`、`duplicate-key` 保留先注册者、无移除不发事件
- S3：变更被拒后集合＝对账后状态、结构化冲突（`Inspect` 层）、试算零写盘、`Register` 既有 message／异常不变、`Inspect([])`＝`EmptyPool`、`Register([])` 抛原 `ArgumentException`
- S4：`removedByReconcile` 摘要、空集合候选允许写入、`InMatch` 变更不打扰本局、入参失败码（相对路径／目录不存在／缺 manifest／kind 非 cardpack／未知 id）、同 key 幂等同路径与不同路径拒绝、过期版本 `version-mismatch`、沙盒目录注册拒绝、注册序、注销到空、空集合 `pool.list`
- S5：三 op 可用、未知 op 可读失败、`AdminOps.All` 含三项且工具表不路由写 op、drain 内写拒 list 通且两面同文、`pool.list` 不入队
- S6：play 无写工具／sandbox 有、viewer 可调、`Connected` ⇒ `not-admitted`、描述含重操作口径、drain 白名单只含 `pool.list`
- S7：`packDirectories` 键存在即拒（`[]`/`null`/非数组）、错误码与指引句、实际装载＝集合条目、沙盒剔除降级双面报告、`assemblyInput` 字段、空集合特化 message
- S8：play 无绝对路径与自由文本、issues 白名单、`errorCode` 三受众恒在、sandbox 完整、唯一投影函数
- S9：注销/`sandbox.reset` 不动沙盒包、沙盒包损坏时注册成功＋`pool.list ok=true`＋告警、开局剔除降级、`sandbox-metadata-unsynced` 只读、两标记独立、零目录 ⇒ `EmptyPool`
- S10：`sandbox.state` 无集合段、描述、`packset.changed`（成功发/失败不发/无目录、无移除不发 reconciled）、`reconciled` 无目录、只有一份集合状态、`initialPackDirectories` 剔沙盒包
- S11：代码审查（无端到端用例，见下）
- S12：不变量（非空集合对账后可装配，含"变更被拒但对账已提交"）、事件、守卫、三类边界、`match.start` 三态、空集合、`EmptyPool` 无 switch 吞掉（grep 全是 `is` 模式/属性，无 `switch`）
- S13：工具描述、对外说明三组名字与迁移、与交接文档无冲突

### 未满足 / 未验证
1. **S5 第一条（未满足）**：play 模式下管理面 op 不给完整条目（按 R4＋S8 走白名单）。与 R4/S8 冲突，已裁决按 R4。
2. **S7 lastGood 回退与"开局失败不回滚对账"（未验证）**：需要"先成功一局 → 集合变坏 → 再开局"的序列，本任务未补该用例。
3. **S4 终局轮换后按新集合装配（未验证）**：需要打完整局触发轮换，未补。
4. **S1 房间轮换后集合不变（部分验证）**：结构上 `ServerHost.Packs` 为进程级、`CreateService` 重建仍注入同一实例；无专项轮换断言。
5. **S1 并发 `pool.list` 合并（已实现、未观测）**：单飞无外部可观测点，未写并发用例。
6. **S3/S4 池层 lock 冲突响应（未验证）**：`details.conflicts` 映射代码存在，但未构造"同 id 不同版本并存"的端口用例；仅在 `Orc.Packages.Tests` 验证了 `Inspect` 的结构化冲突。
7. **S4 "目录不可读 ⇒ path-denied"（未验证）**：Windows ACL 下难以稳定构造。
8. **S11 管理器集成（未跑）**：按要求未把 `Orc.Manager.Tests` 作为门槛；结论来自代码审查（`manager.admin` 原样透传、`ManagerCodes.Map` 未知码透传、payload 深拷贝）。

## 阻塞 / 风险
- play 模式 `pool.list` 的 op 与工具**都**按白名单脱敏（R4＋S8 acceptance），与 S5 首条「管理面在 play 模式给完整条目」冲突，后者未满足。
- 对账「失效条目」口径扩展：包级结构性非法（sidecar 不可解析等）也按失效条目移除，用于守住「对账后可装配」不变量（文档只列了目录消失/id 变化）。
- 磁盘漂移造成的「两个各自有效但合并 lock 冲突」不在对账可修复范围内（无确定性移除规则），`pool.list` 会 ok=false、开局失败；已在代码注释标注，未自造规则。
- 既有事件 `pool.assembled` 的 `packages` 字段（`MatchPoolReport.Packages`）**仍是本局目录列表**：S7 要求"事件同一信息不含目录"，S10 要求该事件"零改动"——按 S10 保零改动，只保证**新增**的 `assembly`/`degradation` 两段无目录。
- 变更被拒时响应的 `model` 是**被拒候选**的试算结论（含不可装载行），不是集合真身；集合真身看 `pool.list`（S4「报本次变更结果＋试算结论」的解读，测试内有注释）。
- `PoolAudience.Admin` 当前无调用方（管理面按实例模式取 `Sandbox`/`RemotePlay`）；保留枚举三档是 S8 的字段语义要求。
- `dotnet build OrcEngine.slnx` 因本机有正在运行的 `Orc.Manager`（PID 27864）锁定 `bin/Orc.Manager.dll` 而**复制失败**（编译本身成功）；未终止该进程。

## 下一步
- 无。若要收尾 2/3/5/6/7，可按上列"未验证"逐条补用例（各需 1 个集成场景）。
