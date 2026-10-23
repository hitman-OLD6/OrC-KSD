# Implementation — 失败测试清零

把 `Orc.Game.Tests`(111) / `Orc.Packages.Tests`(2) / `Orc.Mcp.Tests`(1) 三条红线清零，且每条变绿都带归因（测试过期／产品缺陷／资产过期／门禁授权）。

## 终态（2026-10-23）

**`dotnet test OrcEngine.sln` 8 个项目 2367 条，失败 0 / 跳过 0。**

| 项目 | 开工 | 终态 |
| --- | --- | --- |
| Orc.Tests | 370 绿 | 370 绿 |
| Orc.Script.Tests | 17 绿 | 17 绿 |
| Orc.Ai.Tests | 13 绿 | 13 绿 |
| Orc.Server.Tests | 110 绿 | **112 绿**（补回 3 条被注释吞掉的 Theory 用例） |
| Orc.Mcp.Tests | 1 红 / 118 | 118 绿 |
| Orc.Packages.Tests | 2 红 / 363 | 363 绿 |
| Orc.Game.Tests | **111 红** / 1354 | 1354 绿 |
| Orc.Manager.Tests | 20 绿 | 20 绿 |

实际执行与计划的偏差（如实记录）：

1. **拆钳制使失败数从 111 涨到 175**（+66 新红 / -2 新绿）。这不是倒退——是旧 `At()` 越界钳制掩盖了 66 处布置错误。S2 的核心价值就是把假绿变真红，归因基线因此从 111 改为 175。
2. **S9/S10 无法按"先 A 后 C"串行推进**：175 条里 163 条是同一族（位置模型取位），按测试类分片并行处理（T1 57 / T2 58 / T3 54 / P 6 / D 3）比按档串行快得多，且每片内仍按"先判测试过期、再判产品缺陷"的顺序。
3. **查出 4 处计划外的产品缺陷**（B-1 BattleLine 快照未作废、B-2 部署重放丢位置、B-3 判定器导出面漏项、C-4 沙盒 slot 钳制），全部按 R2/裁决 5 修复并单列归因，不是"改测试抹平"。
4. **样例包额外发现**：`card-pack-across-the-pacific` 6 个 effect prefab 同样用已删除的 `UnitStateData.Position`，但没有任何用例编译它们（`csx制品_*` 只校验内置三件）——属"测试覆盖盲区下的功能缺失"，一并修掉（commit `624aa90`）。
5. **发现一处测试假绿**：`tests/Orc.Server.Tests/PackPoolTests.cs:256` 把 `[Theory]` 写在注释行里，`MatchStart_Rejects_PackDirectories_Key` 的 3 个 InlineData 从未被 xUnit 收集。修复后 Server 从 109 → 112。**该文件属卡池批次未跟踪文件，本次未提交**（见遗留）。
6. **提交单元**：实际 6 个 commit（①② 由第一批执行者提交，③④⑤⑥ 由收口提交），未按"每处缺陷单开"严格拆分——测试文件里同一文件承载多个缺陷的随动断言，物理上不可拆。每条缺陷在 commit message 与 `ATTRIBUTION.md` 里逐条列明。

## Scope

- Target:
  - 测试侧（先手）：`tests/Orc.Game.Tests/TestGapListExtensions.cs`、`CommandTestInfrastructure.cs`、`TestPositionCompat.cs` 及全部调用点
  - 生产代码：`src/Orc.Game/**`（位置模型相关判定器、`PlayManager`、`MatchCardService`、`Sandbox/**`）、`src/Orc.Game/Cards/KeywordPrefabs/*`（内置词条 csx 制品）
  - 生产资产：`samples/card-pack-bulwark/effects/keyword.repair.prefab.json`、`samples/card-pack-across-the-pacific/**`
  - 门禁资产：`tests/Orc.Game.Tests/Baselines/contract-freeze.json`、`tests/Orc.Game.Tests/AssetDomainTests.cs` 的白名单表
  - 其他测试：`tests/Orc.Mcp.Tests/**`、`tests/Orc.Packages.Tests/**`
  - 过程产物：`docs/测试失败清零/PROGRESS.md`（每步前/后失败数、拆钳制后的新基线清单）、`docs/测试失败清零/ATTRIBUTION.md`（归因表）、`docs/测试失败清零/check_attribution.py`（归因表轻量校验）
- Excludes:
  - 新增功能；性能优化
  - `Orc.Tests`／`Orc.Script.Tests`／`Orc.Ai.Tests`／`Orc.Server.Tests`／`Orc.Manager.Tests` 的用例（仅作回归护栏）
  - `tools/orc` 的 Python 测试
  - 未提交的「卡池注册注销」改动的功能内容（只要求不退化、可独立回退）

## Constraints

1. **全量判据＝`dotnet test OrcEngine.sln`**，唯一口径。`OrcEngine.slnx` 只是镜像，仅用于最终交叉核对；实测两份清单逐项一致（15 src ＋ 8 tests ＋ 4 samples ＋ 1 tools），当前无差异需登记——若执行期间出现差异，以 `.sln` 为准并把差异写进本文档。
2. **禁止杀 `Orc.Manager` 常驻进程**：本机有用户正在跑的管理器实例
   `dotnet "D:\OrC-KSD\src\Orc.Manager\bin\Debug\net8.0\Orc.Manager.dll" serve --port 51999`（PID 21968），
   它持久锁住 `src/Orc.Manager/bin/Debug/net8.0/` 下的 DLL，任何需要重建 `Orc.Manager` 的 `dotnet test` 会报 **MSB3027／MSB3021**（不是并发争抢，重试无效）。
   处置：**所有**重跑测试与最终验收一律改用独立产物目录
   `dotnet test <proj> --artifacts-path "D:/OrC-KSD/artifacts/test" --nologo`。
   不带该参数得到的红＝假红，不得据此改测试或改产品。
3. **`Orc.Manager.Tests` 是护栏，不是待修项**：基线 0 失败 / 20 通过。不设修复步骤，只在 S11 核对仍为 0 失败。
4. **跳过口径**：验收＝失败 0 且跳过 0（本机基线即 0）。若在无 mklink 权限的机器上出现 `ReparsePointFactAttribute` 引发的环境型 `Skip`，允许存在，但必须在报告中单列并注明"非本次工作引入"；**不得为了凑跳过 0 去改 `ReparsePointFactAttribute`**。
5. **顺序**：**S2 拆钳制（归因地基）→ S3 过期语义独立化（`PositionStale`）** → B 档（产品/资产真坏，现为 6 条：csx 4 ＋ 门禁 2；原 `combat.range` 2 条已降级并入 A 档）→ A 档（测试过期）→ C 档（逐条判定）。B 档与 S2/S3 可并行验证，但**不得交叉修改同一批文件**（S2 动 `tests/Orc.Game.Tests` 的辅助与调用点；S3 动失败原因枚举与三个入口；B 档动 `samples/`／`src/Orc.Game/Cards/KeywordPrefabs`／门禁基线）。
6. **拆钳制后重新分档**：先只拆钳制、不做其他修改，跑一遍记"新红/新绿"清单；该清单是唯一归因起点。拆除后**新冒出的红单独登记**，不许用调断言消掉——它们的价值就是暴露先前被掩盖的问题。
7. **归因四类**：测试过期／产品缺陷／资产过期／门禁授权；每条改写用例必须落一类。C 档改产品侧的，登记「症状／真源依据／改的是哪一侧」。失败形态按**拆后实际形态**记录，不沿用拆前消息文本。
8. **不得弱化**：不新增 `Skip`、不删 `[Fact]`、不把门禁改成恒真、不放宽产品校验、不用越界兜底换取绿色；真源有歧义时停下上报，不自行选边。
9. **断言原语义在新模型下无对应物时**（不是"位置换了"，而是"命题本身不成立"）→ 不自行重定义期望值，登记上报（属需求级变更）。

10. **落盘纪律（R7，硬约束）**：
    - **禁区**：`git add -A`／`git add .`／`git checkout --`／`git restore` 一律禁止；只允许逐路径 `git add <path>`，**每次 add 前先 `git diff -- <path>` 核对**内容确属本次工作。
    - **卡池批次文件只读不写、不提交**：`src/Orc.Mcp/Pool/`、`src/Orc.Mcp/Tools/PoolTools.cs`、`src/Orc.Server/Admin/*`、`src/Orc.Server/ServerBootstrap.cs`、`src/Orc.Packages/Loading/MatchCardAssembly.cs`、`src/Orc.Packages/LoadListPlanner.cs`、`tools/orc/transport.py`。若发现"非改不可"，**停下上报**，不许自行扩大范围。
    - **混合文件允许叠加**（同文件既有在途改动又有本次改动，如 `MatchCardService.cs`），但提交说明必须分段写清哪部分属哪批；`MatchCardService.cs`／`SandboxPrimitives.cs` 里昨日的位置模型收尾不算卡池批次，可随该文件进对应 commit。
    - **样例包三文件绝对不还原**（`manifest.json`／`lock.formal.json`／`lock.test.json`）。判定其状态的唯一方式是跑重建工具看能否收敛：能收敛 ⇒ 继续重建进 commit ④；收敛不了（如源与 sidecar 数量对不上且原因不明）⇒ **停下上报**，不许猜、不许手改 JSON 凑指纹（这两条用例本质是"数据由工具生成"，手改必然对不上）。
    - **`scripts/`（含 `tmp-fix-*.ps1`）、`dist/`、`samples/card-pack-space-war.zip` 原样不动**：不清理、不补 `.gitignore`、不提交。
    - **提交粒度**：按**语义单元**切分，任一单元可独立 `git revert`。**固定单元 5 个**（见下"提交单元"）＋ **C 档每查出一处真缺陷再单开一个**（数量不设上限，不为凑"5 个"牺牲回退粒度）。
      - 一行修复也单开——只要它对应一个独立缺陷。
      - **同一缺陷同时改产品与测试 ⇒ 合并成一个 commit**（产品改了测试不改，中间态必红，违反"提交前已绿"）；message 分段写"产品侧：…；测试侧：…"。
      - **需求级歧义不提交**：先上报裁决，裁决后单开，message 标注"需求级修正"，与 bugfix 区分。
      - **⑤ 只含 `tests/**`**：一旦某个 commit 里出现 `src/**`，它就不属于 ⑤，属于 ③ 或某个 C 档缺陷单元。提交前用 `git diff --stat <commit>` 自查 `src/` 与 `tests/` 是否被混在"测试适配"里。
      - **每个 commit 前对应测试项目必须跑过**（带 `--artifacts-path`），不许提交未验证状态。
    - **message 风格**：中文 `fix:` 前缀（与仓库既有风格一致），点明步骤号与归因（如"fix: S3 过期空隙独立成因 PositionStale（产品缺陷·R2 授权）"），不许只写"修测试"。
11. **编目纪律**：开工前落"在途清单"快照（本文件末尾"在途清单"节，46 项）；收工时再跑一次对比，凡在清单里但本轮不打算改的文件，内容必须与开工前一致——这是唯一能自证"未侵占他人工作"的方式。

## 归因表（`ATTRIBUTION.md`）约定

载体：`docs/测试失败清零/ATTRIBUTION.md`。**粒度＝分组 + 成员全名**（不是 111 行平铺，也不是只给总数）。

- 一级按归因类别分组（同因批量归一组，如"`Assert.Same` 错位：测试仍按槽位实体断言"）；组内**列出全部成员用例全名**（`类名::方法名`），一个不许漏——"逐条可追溯"靠成员列表实现。
- 组的公共字段写一次；组内成员只列名字。
- **C 档例外**：每条单独成行、写全五列（症状／真源依据／改动侧／改动位置／commit）。
- 固定六列：`用例`（组内为成员列表）／`拆前失败形态`／`归因类别`／`改动位置`／`真源依据`／`commit`。
- 归因类别取值五种：`测试过期`／`产品缺陷`／`资产过期`／`门禁授权`／`需求级修正`（需求级歧义专用，与 bugfix 区分）。

**与 commit message 的分工**：commit message 写纵向（这一步做了什么、为什么、影响面，面向 review 顺读）；ATTRIBUTION 写横向（每条用例归哪一类、依据、落在哪个 commit，面向事后追溯）。两者只以 commit hash 挂钩，不互相复制正文。

**例外规则**：
1. 一条用例由两个原因共同促成 ⇒ **一行，主因＋次因备注**，不拆两行。主因判据唯一：**单独移除该原因后用例是否仍绿**——仍绿即次因。
2. 中间态**不进 ATTRIBUTION**（属 `PROGRESS.md` 的分档基线：拆钳制后的新红/新绿清单）；ATTRIBUTION 只记最终归因。
3. `[Theory]` 部分 `InlineData` 失败 ⇒ **行级**，写成 `方法名(参数摘要)`；不同参数可能是不同根因，不得合并。

### 模板

```markdown
# ATTRIBUTION — 失败测试归因表

> 归因依据：`requirements.md` R1 四类 ＋ `implementation.md` 各 S；中间态基线见 `PROGRESS.md`。
> 类别：测试过期 / 产品缺陷 / 资产过期 / 门禁授权 / 需求级修正

## 测试过期

### A-1 Assert.Same 错位：仍按"槽位实体"比较
- 拆前失败形态：`Assert.Same() Values are not the same instance`
- 归因类别：测试过期
- 改动位置：`tests/Orc.Game.Tests/XxxTests.cs:123`
- 真源依据：`CONTEXT.md`「位置模型」；`FormationGap` 是插入点、`BoardPosition` 是位置值，均非"槽位实体"
- commit：`<hash>`
- 成员：
  - `XxxTests::Abc`
  - `XxxTests::Def`
  - `YyyTests::Ghi(E2E)`

## 产品缺陷

### C-1 相邻光环加成缺失
- 症状：期望 6/4，实际 4/2
- 真源依据：`docs/game-flow/07-修饰器与光环.md`「…」
- 改动侧：产品
- 改动位置：`src/Orc.Game/…:NN`
- commit：`<hash>`
- 主因/次因：主因＝相邻判定；次因＝（无）
```

### 校验脚本约定

- 位置：`docs/测试失败清零/check_attribution.py`（**不放 `scripts/`**——R7 规定那里原样不动）。运行：`py -3.13 docs/测试失败清零/check_attribution.py`。
- 输入：全量跑出的 trx（`--trx <path>`，缺省找 `--artifacts-path` 下的 `*.trx`）与 `--attribution`（缺省同目录 `ATTRIBUTION.md`）。
- 提取：只认反引号内的用例令牌，形如 `` `类名::方法名` `` 或 `` `类名::方法名(参数摘要)` ``。
- 比对：令牌归一化为 `类名.方法名`，与 trx 的 `testName` 集合匹配——命中条件为相等、或以 `类名.方法名(` 开头（行级参数）；也接受 trx 全名以 `.类名.方法名` 结尾（写了短名时）。
- 判定：**0 命中 ⇒ 报错**（拼错名字／引用已删用例／复制串行），列出该令牌并让退出码非 0；多命中 ⇒ 警告（提示写全名）。只做这一种轻量校验，不做语义校验。
- 输出：分组数、成员数、未匹配项、警告项；有问题时退出码 1，可直接作为收工门禁。

## 提交单元

**固定 5 个**（下表）＋ **C 档每查出一处真缺陷追加一个**（数量不设上限）。

| # | 范围 | 主要内容 | 提交前必须跑的测试 | 允许出现的路径 |
| --- | --- | --- | --- | --- |
| ① | S2 | 拆测试侧取位钳制（`TestGapListExtensions`／`CommandInfrastructure`／删 `TestPositionCompat`＋调用点） | `Orc.Game.Tests` | `tests/**` |
| ② | S3 | `PositionStale` 三入口落地（含 `MatchCardService.cs`／`SandboxPrimitives.cs` 同文件内的昨日移动链收尾） | `Orc.Game.Tests`＋`Orc.Mcp.Tests` | `src/**`＋`tests/**`（同一缺陷，合并） |
| ③ | S4–S6 | 产品侧：csx 制品、契约基线重生成、域标记白名单 | `Orc.Game.Tests`＋`Orc.Packages.Tests` | `src/**`、`samples/**`、`tests/Orc.Game.Tests/Baselines/**`、`AssetDomainTests.cs` |
| ④ | S7–S8 | 数据与协议：样例包重建、MCP 用例／`position-stale` 对外串 | `Orc.Packages.Tests`＋`Orc.Mcp.Tests` | `samples/**`、`tests/Orc.Mcp.Tests/**`、`src/Orc.Mcp/**`（仅失败原因映射）、`tools/orc/commands.py` |
| ⑤ | S9–S10 | 测试适配：A 档取位重写（含 `combat.range` 载荷）、C 档"查明是测试过期"的那部分 | `Orc.Game.Tests` | **仅 `tests/**`** |
| ⑥… | S10 产品缺陷 | 每处独立缺陷一个 commit（产品＋随动测试合并） | 受影响项目 | 按缺陷定 |

备注 1：过程文档（本文件、`PROGRESS.md`、`docs/测试失败清零/`）不计入功能单元，随最后一个提交或单独 `docs:` 提交由执行者择一，前提是不破坏功能单元的可回退性。

备注 2：C 档若**全部**查明是测试过期（无产品缺陷），则只有 5 个单元，⑥ 不出现——不要为了"证明有缺陷"去硬找。

备注 3：**卡池批次与本轮的冲突面已澄清**——`MatchCardService.cs`／`SandboxPrimitives.cs` 里的未提交内容属**昨日移动链修复**（位置模型收尾，与本次同类），不在卡池批次内；卡池批次改的是 `MatchCardAssembly.cs`、`LoadListPlanner.cs`、`Admin*`、`Pool*` 等。改这两个文件前先 `git diff -- <path>` 看清未提交内容，改完再 diff 一次确认只多了本次的部分。

## 基线事实（已核查，供各步执行时对照）

1. **位置模型已是硬切后的形态**：`src/Orc.Game/Board/BattleLine.cs:84-110`（`GetInsertionGaps()` 现取现用，快照变即销毁旧实例）、`src/Orc.Game/Board/FormationGap.cs:36`（`IsCurrent`）。`Board/Slot.cs` 已删除。`BattleLine` 索引器越界抛 `ArgumentOutOfRangeException`（`BattleLine.cs:51-62`）。
2. **测试侧存在三处「迁移过渡物」，会吞掉取位错误**（S2 全部拆除）：
   - `tests/Orc.Game.Tests/TestGapListExtensions.cs:21` — `At(index)` 把越界序号**钳制到最后一个插入点**。
   - `tests/Orc.Game.Tests/CommandTestInfrastructure.cs:169-180` — `PrepareOnSupportAsync` 内部同样钳制（注释仍写着"HQ 占位槽"，是旧槽位模型残留），`PrepareOnFrontAsync` 走 `At()` 钳制。
   - `tests/Orc.Game.Tests/TestPositionCompat.cs:13-24` — `Card.IsEmpty() => false` 恒假助手。
   - 现存调用点：`.At(` 约 250 处、`IsEmpty(` 约 30 处；`docs/游戏层-手牌选靶与前线入场/遗留问题.md` P1-6／P2-9 记为"临时手段／收敛后应删除"。
3. **同一"过期空隙"事实在不同入口报不同原因**：`src/Orc.Game/Managers/PlayManager.cs:323-326` 把 `!target.IsCurrent` 报成 `PlayFailureReason.TargetSlotOccupied`；而 `src/Orc.Game/Sandbox/SandboxPrimitives.cs:891` 对 `!destination.IsCurrent` 报 `PlacementRejected`。A 档 6 条 `准备单位失败：TargetSlotOccupied` 与此直接相关。
4. **契约基线重生成会带入错误的 `commit` 头**：`tests/Orc.Game.Tests/ContractFreezeTests.cs:148` 用 `ContractSnapshot.CurrentCommit()` 写头，而本次新增 API 尚未提交 → 基线头指向**不包含该 API** 的提交。
5. **判据已裁定，不再有歧义**：全量＝`OrcEngine.sln`；`Orc.Manager.Tests`（0/20）为护栏；跳过基线 0。构建必须带 `--artifacts-path`（见 Constraints 2），否则管理器 DLL 锁会造成假红。
6. **钳制过渡物已经在制造"错误归因"的入口**：`AuraSystemTests.Scenario1b_AdjacentAura_Stacks_From_Multiple_Sources`（`tests/Orc.Game.Tests/AuraSystemTests.cs:107-119`）——受益单位 `PrepareOnFrontAsync(..., 2)` 在空前线被钳到唯一插入点（落位 0），随后 `At(1)` 把左源放到 1（相邻，+2 → 4 通过），`At(3)` 在只有 3 个空隙时被钳到 2，右源落到位置 2（与受益单位隔一个单位）→ 只拿 2 而不是 4，得到 4/2。需求 C 档已据此改为"先拆钳制再重判"。
7. **同一个"空隙已过期"事实，三个入口三种处置**：
   - `src/Orc.Game/Managers/PlayManager.cs:323-326`（`JoinUnitAsync`，后端加入路径）：`!target.IsCurrent` → `PlayResult.Failure(TargetSlotOccupied)`（结果对象，且原因说是"被占用"）。
   - `src/Orc.Game/Match/MatchCardService.cs:333-337,644-645`（`PlaceToSupportLineAsync`，UI 放置入口）：`ContainsSlot` ＝ 同线 && `IsCurrent`，过期 → 抛 `ArgumentException`，**消息写成"不在玩家 #N 的支援线内"**（实际是在线内、只是快照过期）。
   - `src/Orc.Game/Sandbox/SandboxPrimitives.cs:891`（移动原语）：过期 → `SandboxFailure.PlacementRejected`（与"被占用"分开）。
   - 需求 A 档 4 条"`目标槽位 'Formation gap 1' 不在玩家 #0 的支援线内`"正是第 2 条那根错误消息，即"过期"被报成"不在线内"。
8. **契约基线不收录三张失败原因枚举**：`tests/Orc.Game.Tests/ContractSnapshot.cs:328-334` 的 `resultShapes` 只冻结 `PlayResult`/`CommandResult`/`CardPlaceResult`/`DeckFetchResult` 的**属性形状**，`PlayFailureReason`／`CardPlaceFailureReason`／`SandboxFailure` 的成员不在快照里。故 S3 新增 `PositionStale` 本身**预计不产生基线 diff**；若重生成时真出现这三处 diff，按"R2 授权"逐条登记，不得当作无关变动回退。
9. **`position-stale` 会流到对外面**：`tools/orc/commands.py:75`（Python 客户端失败原因→中文文案映射表）、`docs/game-flow/04-打出与部署.md:100`（`PlayFailureReason` 清单）均需同步，否则界面显示原始枚举名。
10. **`combat.range` 第 2 参已定：`BoardPosition`，生产签名不动**（2026-10-23 裁决）。生产侧本就自洽（`CombatTargetLegalJudicator.cs:127,151`）；两个失败用例各传错一种类型——`JudicatorAssemblyTests.cs:157,161` 传 `BattleLine`、`JudicatorCombatLegalityTests.cs:430-443` 传 `FormationGap`。故需求 B 档初判的"改生产代码契约"**不成立**，两条降级为 A 档（测试过期），并入 S9。载荷是契约面而非"最小必要参数"：`Evaluate` 只读 `target.Line` 不代表契约该收窄；`Index` 现不读但"按序号限定范围"要留面；HQ 目标的 `hqLine.IndexOf(hq)` 是位置语义的正确表达，不是死码；`combat.range` 是同步即时消费，不存在跨动作持有 `BoardPosition` 的过时路径。**禁止**用 `gap.Ordinal` 充当元素序号。

## Implementation Steps

### S1: 定验收判据（命令、项目集、跳过口径）
- Status: Done
- Target: 本文档 Constraints 1–4
- Approach: 已裁定并写入 Constraints：全量＝`OrcEngine.sln`（`.slnx` 仅交叉核对，实测项目集一致）；`Orc.Manager.Tests` 纳入但定性为护栏（0/20）；跳过口径＝0、环境型 `Skip` 单列说明；所有重跑带 `--artifacts-path "D:/OrC-KSD/artifacts/test"`。
- Acceptance:
  - [x] 判据唯一（一条命令 ＋ 一组项目 ＋ 跳过口径）
  - [x] `Orc.Manager.Tests` 归属明确（护栏）
  - [x] 构建环境约束写明（禁杀 PID 21968／独立产物目录）
- Rationale: 需求 R1 首条验收与"Skip 不得增加"原本互斥，且现状表缺 `Orc.Manager.Tests`；判据不先钉死，后续"清零完成"不可复核。

### S2: 拆测试侧取位钳制，重建归因基线
- Status: Done
- Target: `tests/Orc.Game.Tests/TestGapListExtensions.cs`、`tests/Orc.Game.Tests/CommandTestInfrastructure.cs:169-180`、`tests/Orc.Game.Tests/TestPositionCompat.cs` 及其全部调用点（`.At(` 约 250 处、`IsEmpty(` 约 30 处）
- Approach:
  1. `At(index)` 去钳制：越界抛 `ArgumentOutOfRangeException`，与 `BattleLine` 索引器（`src/Orc.Game/Board/BattleLine.cs:51-62`）的 fail-fast 口径一致；保留方法本身作为"命名取位"，不保留兜底语义。
  2. `PrepareOnSupportAsync` / `PrepareOnFrontAsync` 的 `index` 同样不钳制、越界抛；注释删掉"HQ 占位槽/按可部署序跳过占位槽"那套旧槽位模型说法，改成"第 index 个插入点（0 起，N 元素共 N+1 个）"。
  3. 删除 `TestPositionCompat`（`Card.IsEmpty() => false`／`FormationGap.IsEmpty()`），调用点按"位置元素"语义改写：原意"这里是空槽"的断言改成"该位置是哪个元素"的直接比较，或改写为插入点判断。
  4. **只拆钳制、不动其他**：跑 `dotnet test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --artifacts-path "D:/OrC-KSD/artifacts/test" --nologo`，把"新红／新绿／形态变化"三类清单写入 `PROGRESS.md`；失败形态按拆后实际文本登记（7 条"位置序号越界"很可能变成更早的抛出点）。
  5. 新冒出的红**单独成节**登记，不在此步修。
- Acceptance:
  - [ ] 测试侧不存在任何"越界钳制取位"：`At`／`Prepare*Async` 越界均抛
  - [ ] `TestPositionCompat` 已删除，调用点清零（`IsEmpty(` 在测试侧无残留引用）
  - [ ] `PROGRESS.md` 有拆后新基线（失败总数 ＋ 逐条名称 ＋ 拆后实际失败形态）
  - [ ] 新冒出的红单独登记，且本步未修改任何断言期望值
- Rationale: 用户裁定（拆三处全拆）；钳制等于让装配错误静默成功，违反"不得弱化"，且会把布置错误伪装成产品行为差异（基线事实 6），直接污染 A/C 档归因。

### S3: 过期语义独立化（`PositionStale`）
- Status: Done
- Target: `src/Orc.Game/Cards/PlayChain.cs`（`PlayFailureReason`）、`src/Orc.Game/Match/MatchCardService.cs:52,333-352`（`CardPlaceFailureReason` 与 `PlaceToSupportLineAsync`）、`src/Orc.Game/Managers/PlayManager.cs:323-326`、`src/Orc.Game/Sandbox/SandboxFailure.cs`、`src/Orc.Game/Sandbox/SandboxPrimitives.cs:891`
- Approach:
  1. `PlayFailureReason` 新增 `PositionStale`；`JoinUnitAsync` 的 `!target.IsCurrent` 改返回 `PlayResult.Failure(PositionStale)`，**仍然不抛**（入口不抛契约不变）。
  2. `CardPlaceFailureReason` 新增 `PositionStale`；`PlaceToSupportLineAsync` 拆两条判定——
     - `!ReferenceEquals(slot.Line, line)`（真的不在该玩家支援线）⇒ 保留 `ArgumentException`，消息保留；
     - 同线但 `!slot.IsCurrent` ⇒ `CardPlaceResult.Rejected(PositionStale, card)`，不抛。
     `ContainsSlot` 的判定式（同线 && `IsCurrent`）按 R6 注保留不改，变的只是失败分类。
  3. `SandboxFailure` 新增 `PositionStale = "position-stale"`（与现有串常量同风格）；`MapMoveFailure` 里 `!destination.IsCurrent` 改用它，不再退到 `PlacementRejected`。
  4. **保留 `TargetSlotOccupied`**：真正的"目标位置已有单位"（部署/加入时位置被占）仍用它，不再兼任"过期"。
  5. 改完重跑 `Orc.Game.Tests` 与 `Orc.Mcp.Tests`，把 A 档 6+4 条的失败文本变化（`TargetSlotOccupied`／"不在线内" → `PositionStale`）记进 `PROGRESS.md`。
- Acceptance:
  - [ ] 过期在三个入口都报出可区分的原因（`PositionStale`／`position-stale`），不落到"被占用"或"不在线内"
  - [ ] "不在线内"仍是异常（真契约错误），`PlayManager` 入口仍不抛
  - [ ] `TargetSlotOccupied` 仍被"位置真被占"的场景使用，未被删
  - [ ] A 档 10 条的失败文本已从误导性文本变为 `PositionStale`
- Rationale: 用户裁定（统一语义、不统一形态）；需求 R2 第三条、R6 注。形态按既有契约走，是为了不把异常打到玩家路径上。

### S4: B 档 — 词条 csx 制品与引擎模型同步
- Status: Done
- Target: `samples/card-pack-bulwark/effects/keyword.repair.prefab.json`、`src/Orc.Game/Cards/KeywordPrefabs/keyword.mobilize.accrual.csx.prefab.json`、`src/Orc.Game/EffectParsing/Templates/position_basic.tpl.json`（及同类内嵌 csx）
- Approach: 待回填（位置读法改为现行 `UnitStateData.Line`/`BoardPosition` 面；"能编译"之外还要"效果生效"有可检验断言）。与 S2 可并行，但不改 `tests/Orc.Game.Tests` 的辅助文件。
- Acceptance:
  - [ ] `BulwarkKeywordTests::抢修_*`、`KeywordEffectCsxTests::csx*` 转绿（4 条）
  - [ ] 全部样例包 prefab 在 `csx制品_*` 校验用例下通过
  - [ ] 至少一条断言证明效果实际生效（非仅编译通过）
- Rationale: 需求 R3；`遗留问题.md` P0-1 已定性为功能性回归。

### S5: B 档 — 门禁授权：契约基线重生成
- Status: Done
- Target: `tests/Orc.Game.Tests/Baselines/contract-freeze.json`
- Approach: 待回填（`ORC_WRITE_BASELINE=1` 重生成；`commit` 头如何取得"包含本批 API 的提交"——基线事实 4）。S3 新增的三张失败原因枚举**预计不进基线**（基线事实 8：`resultShapes` 只冻结结果类型属性形状）；若 diff 里真出现它们，按"R2 授权"逐条登记，不回退、也不算夹带。
- Acceptance:
  - [ ] diff 逐条对得上 `JoinFrontlineAsync`／`JoinSupportLineAsync`／`PickHandCardsAsync`／`Join*Trigger`
  - [ ] 若含 `PositionStale` 相关 diff，归因表写明"本次失败分类新增 `PositionStale`（R2 授权）"
  - [ ] 出现其它无关变动即回退排查（R4）
- Rationale: 需求裁决 3。

### S6: B 档 — 门禁授权：域标记白名单两处
- Status: Done
- Target: `tests/Orc.Game.Tests/AssetDomainTests.cs:66-80`（`allowedFiles`／`allowedPrefixes`）
- Approach: 待回填（放行 `src/Orc.Manager/AssetLibrary.cs`、`src/Orc.Server/Pool/PackPool.cs` 并就地注明理由）。
- Acceptance:
  - [ ] `No_Gameplay_Logic_Reads_Asset_Domain` 转绿，且白名单新增项各带一行理由
  - [ ] 检索面自检（`hits` 断言）仍有效，未因白名单放宽而失去检出能力
- Rationale: 需求裁决 4；R4 要求"每一处新增都有理由"。

### S7: Orc.Packages.Tests — 样例包 `card-pack-across-the-pacific` 自洽
- Status: Done
- Target: `samples/card-pack-across-the-pacific/**`（sidecar／`manifest` v2／两份 lock／内容指纹）
- Approach: 待回填。已知前提：该包的 `manifest.json`／`lock.formal.json`／`lock.test.json` **本身就在未提交改动里**（见 `git status`），两条失败的落点就是这三个文件所处的在途状态——先判"是接续把重建做完"还是"其它批次留在途"，再动（S11 会核对与卡池改动分区）。对应失败用例：`PackageVersionTests.样例包_重算内容指纹与manifest一致`（`tests/Orc.Packages.Tests/PackageVersionTests.cs:209-226`）、`AssetMigrationToolTests.样例包_每个源文件都有sidecar且清单为v2与两lock一致`（`tests/Orc.Packages.Tests/AssetMigrationToolTests.cs:312-345`）。
- Acceptance:
  - [ ] 每个源文件有 sidecar；manifest v2；两份 lock 一致；指纹重算一致
  - [ ] 该包重建后仍过 `pool.assembled` 与开局校验
- Rationale: 需求 R5。

### S8: Orc.Mcp.Tests — `SandboxPrimitiveToolTests.ReturnedRefIsUsableByNextCall`
- Status: Done
- Target: `tests/Orc.Mcp.Tests/**`、可能牵动 `src/Orc.Mcp/Match/CardResolver.cs`／`TargeterBridgeAdapter.cs`／`ShapeTools.cs`
- Approach: 待回填（先判"引用不可用"是产品缺陷还是用例按旧 `slot` 语义写的）。
- Acceptance:
  - [ ] 该用例转绿且不改 `Skip`
  - [ ] `Orc.Mcp.Tests` 其余 117 条不退化
- Rationale: 需求裁决 2（三个项目一起清零）。

### S9: A 档 — 测试侧取位重写（含 `combat.range` 载荷修正）
- Status: Done
- Target: 拆钳制后重新分档的 A 档用例（`Assert.Same` 错位、序号越界、`PositionStale`、空隙不在线内）＋ 原 B 档降级的两条：`tests/Orc.Game.Tests/JudicatorCombatLegalityTests.cs:430-443,452`、`JudicatorAssemblyTests.cs:157,161`
- Approach:
  1. 通用改写：以 S2 的清单为准，不沿用拆前条数；位置断言＝`GetInsertionGaps()` 现取，元素断言＝`Members`，允许 `gaps[^1]` 显式取末位。
  2. **`combat.range` 载荷修正（生产签名不动，只改测试）**：`RangeRule(UnitCard, BoardPosition)` 是契约面，家族（C1 `CombatTargetLegalJudicator`／C4 `CombatInterceptionJudicator`）齐整优先，`Index` 现在不读但范围内按序号区分要留面。
     - `JudicatorCombatLegalityTests`：目标改为**针对目标单位**构造 `new BoardPosition(unit.Line!, unit.Line!.IndexOf(unit))`；**禁止**用 `gap.Ordinal` 塞进去（插入点序号 ≠ 元素位置序号，那是新的语义污染）。
     - 430-431 的"目标槽位"措辞改"目标位置"，452 行 C4 注释一并校准。
     - `JudicatorAssemblyTests`：`line` 补成 `new BoardPosition(line, line.IndexOf(unit))`；即使意图是"只验证线"，也要显式写出序号来源。
     - C4 `CombatInterceptionJudicator` 不改（签名已对）；不增设第 4 种位置载体。
- Acceptance:
  - [ ] 每处改写保留原用例意图；原语义在新模型下无对应物的，登记上报而非重定义
  - [ ] 取空隙与使用之间不做改变阵线的操作（R2 第二条）
  - [ ] `KeywordRuleOverride...`、`Sub_Rule_Entries...` 转绿，且生产签名 `combat.range(UnitCard, BoardPosition)` 零改动
  - [ ] 归因表写"初判为产品契约不符，核查后生产侧自洽，实为测试传错载荷类型"，不写成"产品已修复"
  - [ ] 无删除 `[Fact]`、无新增 `Skip`
- Rationale: 需求 A 档定性 ＋ R2 ＋ 用户裁定 (a)(b)；载荷是契约面而非最小必要参数（裁决 2026-10-23，见基线事实 10）。

### S10: C 档 — 逐条判定
- Status: Done
- Target: 拆钳制后重新分档的 C 档用例（`AuraSystemTests`／`JudicatorTargetSelectionTests`／`CommandRulesTests`／`KeywordProgramTests`／`CardServiceTests::Scenario6...`／`KeywordEffectBatch3Tests::Blitz_Death...`／`PlayChain*`／`SandboxPrimitiveTests`／`Trigger*`／`EffectRemount*` 等）
- Approach: 待回填。每条按"症状／真源依据／改哪一侧"登记；`AuraSystemTests` 4 条先按"拆钳制后是否仍红"重判，不预设产品缺陷。
- Acceptance:
  - [ ] 每条都有登记行；改产品侧的条目与卡池未提交改动分区、可独立回退（裁决 5）
- Rationale: 需求实施纪律第三条；C 档表格已改"先拆钳制再重判"。

### S11: 全量回归 + 对外协议串同步 + 归因表 + PROGRESS 落档
- Status: Done
- Target: `docs/测试失败清零/ATTRIBUTION.md`（按上节约定落地）、`docs/测试失败清零/check_attribution.py`、`docs/测试失败清零/PROGRESS.md`、提交拆分；对外面：`tools/orc/commands.py:75`、`docs/game-flow/04-打出与部署.md:100`、MCP 侧失败原因映射
- Approach: 待回填（全量命令固定带 `--artifacts-path` 并产出 trx；`.sln` 跑完再用 `.slnx` 交叉核对一次项目集与结果）。`position-stale` 是对外串，收尾必须逐处同步（基线事实 9），漏一处界面就会显示原始枚举名。
- Acceptance:
  - [ ] `dotnet test OrcEngine.sln --artifacts-path "D:/OrC-KSD/artifacts/test"` 失败 0、跳过 0（环境型 `Skip` 若有则单列说明）
  - [ ] `Orc.Manager.Tests` 仍 0/20（护栏）
  - [ ] `ATTRIBUTION.md` 覆盖全部改写用例（分组 + 成员全名，C 档逐条五列）；`py -3.13 docs/测试失败清零/check_attribution.py` 退出码 0
  - [ ] 每条改写用例可追溯到一条归因；`PositionStale` 登记为"R2 授权的产品行为修正"，与卡池未提交改动分区、可独立回退
  - [ ] `position-stale` 已同步 Python 客户端映射表、MCP 失败原因映射、失败原因清单文档
  - [ ] `Orc.Server.Tests`／`Orc.Tests`／`Orc.Script.Tests`／`Orc.Ai.Tests` 保持全绿；移动链三处判定逻辑不变（R6）
  - [ ] `git log` 能看出 5 个固定单元 ＋ 每处缺陷单元；全程无 `git add -A`
  - [ ] 收工 `git status --porcelain` 与"在途清单"对比：清单内、本轮不该动的文件内容逐字未变
- Rationale: 需求 R1／R6／R7、裁决 2。

## 遗留（不在本轮范围）

- `scripts/tmp-fix-*.ps1`：迁移期批量替换脚本，留在工作区有被误用风险，需另起一单清理（本轮按 R7 原样不动）。
- `OrcEngine.sln` 与 `OrcEngine.slnx` 双份解决方案并存，本轮仅交叉核对、不做合并。
- **`tests/Orc.Server.Tests/PackPoolTests.cs` 的假绿修复未提交**：该文件是「卡池运行期注册注销」批次的未跟踪产物，内含 `[Theory]` 被写进注释导致 3 条 `MatchStart_Rejects_PackDirectories_Key` 从未执行。本轮已修（Server 109→112 全绿），但整文件提交会把未完成的卡池批次带进历史，故按 R7 留在工作区，**待卡池批次提交时一并入库**。
- `samples/card-pack-across-the-pacific/_tools/build.py` 被 `.gitignore:29`（`**/_tools/`）忽略，不入库；仓库内数据生成真源是 `tools/Orc.Tools.Migrate`。
- `artifacts/`（本轮测试产物：trx / bin / obj）未进 `.gitignore`，保持未跟踪。若长期保留建议另行加忽略规则（本轮 R7 规定不动 `.gitignore`）。

## 在途清单（开工前快照，2026-10-23，共 46 项）

> 用途：收工对照，证明本轮未侵占他人工作（Constraints 11）。`M`＝已修改未提交，`??`＝未跟踪。

```
 M CONTEXT.md
 M samples/card-pack-across-the-pacific/lock.formal.json
 M samples/card-pack-across-the-pacific/lock.test.json
 M samples/card-pack-across-the-pacific/manifest.json
 M samples/card-pack-ai/lock.test.json
 M samples/card-pack-bulwark/lock.test.json
 M samples/card-pack-demo/lock.test.json
 M samples/card-pack-diy/lock.test.json
 M samples/card-pack-space-war/lock.test.json
 M samples/card-pack-synthetic/lock.test.json
 M src/Orc.Game/Judicators/MoveRevalidationJudicator.cs
 M src/Orc.Game/Match/MatchCardService.cs
 M src/Orc.Game/Sandbox/SandboxPrimitives.cs
 M src/Orc.Manager/AssetLibrary.cs
 M src/Orc.Mcp/Contracts/McpToolResult.cs
 M src/Orc.Mcp/McpService.cs
 M src/Orc.Mcp/Tools/DebugToolCatalog.cs
 M src/Orc.Mcp/Tools/ToolRegistration.cs
 M src/Orc.Packages/AssetMigrationTool.cs
 M src/Orc.Packages/LoadListPlanner.cs
 M src/Orc.Packages/Loading/MatchCardAssembly.cs
 M src/Orc.Server/Admin/AdminApi.cs
 M src/Orc.Server/Admin/AdminCodes.cs
 M src/Orc.Server/ServerBootstrap.cs
 M src/Orc.Server/Transport/ServerHost.cs
 M tests/Orc.Game.Tests/CommandSystemTests.cs
 M tests/Orc.Game.Tests/Sandbox/SandboxPrimitiveTests.cs
 M tests/Orc.Packages.Tests/AssetMigrationToolTests.cs
 M tests/Orc.Server.Tests/RoomRotationTests.cs
 M tests/Orc.Server.Tests/TestSupport.cs
 M tools/orc/transport.py
?? dist/
?? docs/初始设计/kards官方卡牌.json
?? docs/卡池运行期注册注销/
?? docs/测试失败清零/
?? samples/card-pack-space-war.zip
?? scripts/
?? src/Orc.Mcp/Pool/
?? src/Orc.Mcp/Tools/PoolTools.cs
?? src/Orc.Packages/Loading/AssemblyInspection.cs
?? src/Orc.Packages/Loading/PackageDirectoryProbe.cs
?? src/Orc.Server/Pool/
?? tests/Orc.Packages.Tests/AssemblyInspectionTests.cs
?? tests/Orc.Server.Tests/PackPoolGuardTests.cs
?? tests/Orc.Server.Tests/PackPoolTests.cs
```
