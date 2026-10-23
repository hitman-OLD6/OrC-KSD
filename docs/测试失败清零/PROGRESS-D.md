# 进度 — D 片（S7/S8）

- 更新时间: 2026-10-23T19:45+08:00
- 工具调用计数: ~45
- 当前步骤: S7/S8 已完成，S11 顺带项已做
- 总体状态: 已完成（含 1 项待裁决）

## 已完成

### 基线实测（与任务书描述不同）
- `dotnet test tests/Orc.Packages.Tests --artifacts-path D:/OrC-KSD/artifacts/test-d` → **失败 0 / 通过 363**（`d-pkg-base.trx`、`d-pkg-full.trx`、`d-pkg-after.trx`）。两条「样例包」用例**开工时已是绿**。
- `dotnet test tests/Orc.Mcp.Tests` → 1 红（`ReturnedRefIsUsableByNextCall`），修后 **失败 0 / 通过 118**（`d-mcp-full.trx`）。

### S7 — 样例包自洽（结论：磁盘数据本就收敛，缺的是「生成工具」与产品口径不一致）
1. `tools/Orc.Tools.Migrate --package samples/card-pack-across-the-pacific`（dry-run）：**Create 0｜Skip 19｜Overwrite 0｜障碍 0**。
   ⇒ 现状 manifest/lock/sidecar 与产品口径（`AssetMigrationTool.Plan`）完全收敛，**不需要**再动 JSON。
2. 判断"数据是谁写的"：`manifest.json`／`lock.formal.json`／`lock.test.json` 无尾换行（产品写窄输出）＝迁移工具写；`*.meta.json` 有尾换行＝`_tools/build.py` 写。
3. 查出**生成工具失真**（本轮真正修的东西）：`_tools/build.py`
   - 旧指纹 `md5(json.dumps(metadata 路径列表))` = `80037686…`，与 `PackageVersion.ComputeContent`（SHA-256(包 id ＋ 各 sidecar 规范化文本 ＋ 段长前缀)）**不是同一算法**；
   - 旧代码把 16 条资产同时写进 `lock.test.json`，而口径要求测试域为空（`AssetMigrationTool.BuildLocks` 注释已写明 DomainMismatch 原因）。
   ⇒ 任何人重跑 build.py 都会把这两条用例重新打红。（注：`**/_tools/` 在 `.gitignore:29`，该修复**不入库**，仅本地工具可用；仓库内数据生成真源是 `tools/Orc.Tools.Migrate`。）
4. 修好 build.py 后重建，**指纹复现为 `ce8b9e35138198adca54102d2c0d43de`（与迁移工具写出的 manifest 逐字节一致）**；重建后再 dry-run：仍 **Skip 19｜Overwrite 0｜障碍 0**；`git diff --stat` 与重建前一致（44/101/18 行），**未产生任何新增漂移**。
5. 开局校验（独立端口 52235，未碰 51999）：服务端 `--mode sandbox --sandbox artifacts/test-d/smoke-d/sandbox`（`pack/` 为重建后包的副本），AI 双座，管理通道发 `match.start`：
   - 发布 **`pool.assembled`**：`definitions=10`、`fallback=false`、`degradation{usedLastGood:false,sandboxDropped:false,reconciled:[]}`、`assembly.sandboxIncluded=true`；
   - 回执 `ok`，`state=InMatch`、`packs=["pack/orc.across-the-pacific/"]`。
   ⇒ `pool.assembled` 与开局校验均通过。

### S8 — `SandboxPrimitiveToolTests.ReturnedRefIsUsableByNextCall`
- 拆前失败形态：`sandbox.move {ref, line:"frontline", slot:2}` 返回 `Ok` 但 `slot=0`；`Assert.Equal(2, 0)`。
- **判定：ref 本身可用**（`unitize`、`move` 两步调用都成功）——失败点是用例按**旧固定槽位模型**假设"空的前线存在 2 号槽"；新模型里 `slot`＝该线当前快照的插入空隙序号，空前线只有 0 号一个空隙。
- 修法（测试侧）：先铺 2 个单位到前线（并入断言其 `slot` 为 0/1），使 2 号成为合法插入点，再 move 到 2 并保留原断言 `slot==2`。**未改期望值、未加 Skip、未删断言**。

### S11 顺带（position-stale 对外串）
- `tools/orc/commands.py` `_ENGINE_REJECT_TEXT` 增 `"PositionStale": "位置已变化，请重选"`（`tools/orc` Python 单测 104 条仍全绿）。
- `docs/game-flow/04-打出与部署.md` 的 `PlayFailureReason` 清单补 `PositionStale`。

## 进行中

- （无）

## 待裁决

1. **产品缺陷（不在本片可写范围）**：`src/Orc.Game/Sandbox/SandboxPrimitives.cs:739` `TryResolveSlot` 用
   `gapList[Math.Min(Math.Max(index,0), gapList.Count-1)]` **把越界索引钳制到末位**，导致 `sandbox.move/unitize/instantiate`
   传 `slot=99` 时静默落到最后一个插入点并回 `Ok`（本用例拆前正是这个钳制把 `slot:2` 变成了 `slot:0`）。
   同方法自己的注释写的是"越界＝`slot-out-of-range`"，与实现不符；且违反 Constraints 8「不得用越界兜底换取绿色」。
   建议改为越界即 `SandboxFailure.SlotOutOfRange`。**未改**：该文件不在本片授权路径内（且属昨日移动链在途文件）。
   本片改写后的用例在该修复后仍然为绿（2 号在线上有 2 个单位时是合法插入点）。
2. `samples/card-pack-across-the-pacific/_tools/build.py` 的修复**无法入库**（`**/_tools/` 被 `.gitignore` 忽略）。
   若希望仓库内自带可复现的生成器，需另行决定是否把 `_tools/` 纳入版本控制或改成调用 `tools/Orc.Tools.Migrate`。

## 改动文件清单

| 文件 | 说明 |
| --- | --- |
| `samples/card-pack-across-the-pacific/_tools/build.py` | 指纹改为复现 `PackageVersion.ComputeContent`；`lock.test.json` 写空；lock 条目按 `(kind, appId, version)` 排序；manifest/lock 写窄不带尾换行（**gitignore，不入库**） |
| `samples/card-pack-across-the-pacific/manifest.json`、`lock.formal.json`、`lock.test.json` | 由修好的 build.py 重建；结果与重建前**逐字节一致**（未提交改动保持原样，未做任何 checkout/restore） |
| `tests/Orc.Mcp.Tests/SandboxPrimitiveToolTests.cs` | `ReturnedRefIsUsableByNextCall`：补 2 个单位铺出 2 号插入点，保留原断言 |
| `tools/orc/commands.py` | 失败原因映射表加 `PositionStale` |
| `docs/game-flow/04-打出与部署.md` | `PlayFailureReason` 清单补 `PositionStale` |
| `docs/测试失败清零/PROGRESS-D.md` | 本文件 |

## 下一步

- 父 agent 统一提交（④ 单元：`samples/**`＋`tests/Orc.Mcp.Tests/**`＋`tools/orc/commands.py`；注意 `_tools/` 不在版本控制内）。
- 若采纳待裁决 1，另开一个产品缺陷 commit（`src/Orc.Game/Sandbox/SandboxPrimitives.cs`），并重跑 `Orc.Mcp.Tests`＋`Orc.Game.Tests`。
