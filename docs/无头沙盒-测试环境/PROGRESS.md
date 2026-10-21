# 执行进度 — 无头沙盒测试环境

- 更新时间: 2026-10-21T12:10:00Z
- 工具调用计数: ~78
- 当前步骤: 全部 S1–S8 完成并通过全量回归（含复核轮）
- 总体状态: 已完成

## 已完成
- 构建：`dotnet build OrcEngine.slnx --no-incremental` → 成功（0 错误）。
- 全量测试：`dotnet test OrcEngine.slnx` → **2304 通过 / 0 失败**（基线 2263 ＋ 新增 41）。
- S1 启动入口与选项：`--headless` 与 `--mode` 正交、无头专属参数解析阶段早退 `1`、就绪行增量契约、`agent.token` 与座位号解耦。
- S2 座位编排与自动开局：`Room.PresetSeat`/`Room.StartBySystemAsync`、绑定事实判据＋执行期二次确认、无头断线不释放座位、`--wait-agent` 超时 `3`。
- S2b 卡组三档：`DeckRuleTier` 分项放宽（lenient 保留 `blank-id`/`unknown-card`）、`AiDeckGenerator` 尽力生成、摘要记卡组张数与指纹。
- S3 双模式与读写边界：`DebugToolCatalog` 单一清单、写类默认不注册、`OrchestratorOnly`（回环＋未绑定）、`tools/list` 按会话过滤、`observe.view` 按模式分流。
- S4 日志：`DrainSegments` 唯一取段、work item 尾部 drain、全知/脱敏双流按 `sequence` 对齐、`calls.jsonl` 含门禁拒绝与 `segmentsFrom/To`。
- S5 stdio 桥：`src/Orc.Mcp.Bridge`（零第三方依赖）、`seat.state`/`seat.segments`、段缓冲 200 ＋ `dropped`、退出码 0/2。
- S6 生命周期：终局自退（1 进程 1 局）、退出码 0/1/2/3、`summary.json`、`room.MarkEnded`、`--keep-alive` 拒绝再来一局。
- S7 一键脚本：`scripts/headless.sh` ＋ `headless_sandbox.py`（参数面齐全、桥片段可粘贴、`taskkill /T /F` ＋ `--parent-pid` 双保险、端口释放探测）。
- S8 e2e：验收映射表 31＋2 条用例全部存在且通过；复核轮补 S2b 4 条（三档/生成器/claim 口径）。

## 复核轮（2026-10-21T12:10Z）
逐步骤重跑相关用例，并补齐 S2b 缺失的验收守卫（4 条，`tests/Orc.Server.Tests/DeckRuleTierTests.cs`）：

| 步骤 | 命令／断言 | 结果 |
|---|---|---|
| 构建 | `dotnet build OrcEngine.slnx` | 0 警告 / 0 错误 |
| S1 | `ProcessHeadlessTests` 4 条（就绪行、非无头不变、无头专属参数退 1、显式 sandbox 优先） | 4/4 |
| S2 | `HeadlessTests` 13 条（自动开局/断线保座/重连/ai+ai/超时） | 13/13 |
| S2b | `DeckRuleTierTests` 4 条 ＋ `DeckRulesTests` 12 条 | 4/4、12/12 |
| S3 | `ModeTests` 7 条 | 7/7 |
| S4 | `HeadlessTests`（段连续、`$hidden`、`calls.jsonl` 门禁行） | 13/13 |
| S5 | `BridgeTests` 6 条 ＋ `BridgeProcessTests` 1 条 | 6/6、1/1 |
| S6 | `ProcessHeadlessTests` 10 条（退出码 0/1/3、keep-alive、目录结构、强杀可读） | 10/10 |
| S7 | 脚本冒烟：单局 ai+ai、批量 `--games 2`、agent 片段、`--games>1` 非 ai+ai 退 1 | 通过 |
| S8 | 全量 `dotnet test OrcEngine.slnx` | **2304 通过 / 0 失败** |

脚本冒烟细节：
- 单局 ai+ai `--seed 1`：退出码 `0`，`summary.json{winner:1,turns:16,endReason:HqZero,seed:1,packFingerprint:ea9d40d01b7a3efe,errorCount:0}`，`events.jsonl` 55 段、`sequence` 1..55 连续。
- 批量 `--games 2 --seed 10`：`runs/run-001` 与 `run-002` 独立目录，种子 10/11，全局 `summary.json{games:2,completed:2,failed:0,aborted:false}`。
- agent 模式：打印 `mcpServers.orc-seat1`（`dotnet` ＋ 绝对 dll 路径 ＋ `--connect`/`--token-file`）；中断后端口可重绑定、无 `Orc.Server` 残留进程（单局无全局摘要，与 S7「全局摘要仅批量」一致）。
- `--games 2` ＋ agent 座位：参数错误退出 `1` 且**未创建**运行目录。

S2b 新增用例（补齐 implementation.md S2b Acceptance 空缺的 3 项）：
- `Lenient_RelaxesSizeAndCopyLimits_ButKeepsCardPoolAuthority`（张数/同名放宽，`blank-id`/`unknown-card` 保留）
- `Lenient_SkipsFactionPairing_ButStrictRejects`
- `AiDeckGenerator_BestEffort_ReturnsNonEmptyAndDeterministic`（小卡包非空、同种子同结果、strict 仍为空）
- `Claim_UsesLenientTier_SmallDeckAccepted_UnknownCardRejected`（房间态与建局期同档位口径）

## 上一轮修复的缺陷（2 处）
- **D1 终局不收尾（严重）**：终局发现点 `DetectMatchEnd → HandleMatchEnded → StopAiDrivers()` 会取消**当前 AI work item 自己的令牌**，
  随后 `MaybeFinalizeAsync` 用该已取消令牌 `File.WriteAllTextAsync` → 抛 `OperationCanceledException`，而 `_finalized` 已置位，
  于是 `summary.json` 永久缺失、`--exit-on-end` 失效、进程挂住、AI 驱动循环静默退出。
  修复：摘要写盘改用 `CancellationToken.None`；写失败记录原因并复位 `_finalized`；AI work item 用 `try/finally` 把
  drain/终局检测/收尾固定在尾部；AI 单步异常写 stderr（原先静默吞掉，正是该缺陷难定位的原因）。
  验证：demo 包 33s / ai 包 12–19s / bulwark 25s 均终局退出 `0` 并写出摘要。
- **D2 显式 `--sandbox` 被覆盖**：`Program.cs` 解析 `--sandbox` 时未置 `sandboxGiven = true`，无头下显式沙盒写根被
  `<run-dir>/sandbox` 覆盖（与 S1「显式给出以显式值为准」相违）。已修并补守卫用例。

## 上一轮新增用例（2 条）
- `HeadlessTests.AiAiReachesEndAndWritesSummary`（ai+ai 终局：摘要、退出码 0、停止请求、房间 ended）
- `ProcessHeadlessTests.ExplicitSandboxRootWins`（显式沙盒写根优先）

## 进行中
- （无）

## 未开始
- （无）

## 阻塞 / 风险
- 无硬阻塞。上一轮记录的「AI+AI 10 分钟不终局」经复测确认为 D1 的假死象（终局已发生但不收尾），已消除。
- 文档偏差（不影响交付，已在最终汇报列出）：引擎段 `sequence` 自 1 起（文档写「从 0」）；`MatchChanged(Ended)` 不触发
  （引擎终局不抛事件，改由 work item 尾部读 `match.State`）；`SecurityTests` 中「play 未绑定 `observe.view` 放行」的旧断言按 S3 收敛调整。
- 结构偏差（行为等价，未改决策）：Q7 要求「claim 与 start 共用**同一规则对象实例**」，实现为**同一档位/张数配置**传给
  两处 `DeckRules.Validate`（`DeckRulesValidator` 与 `McpMatchLauncher→MatchLaunchOptions`）。判定结果由不可变配置决定，
  已用 `DeckRuleTierTests.Claim_UsesLenientTier_*` 守住口径一致；未发现「声明通过、开局失败」的路径。
- Ctrl-C 的 `130` 退出码在 Git Bash 下无法观测（`kill` 走 TerminateProcess，不触发 Python 信号处理器），按文档映射表
  以「无残留进程 ＋ 端口可重绑定」作脚本级验收，已在本轮冒烟确认。

## 下一步
- 交付最终汇报。

## 与启发式 AI 实打（2026-10-21T04:40Z，父 agent 复核轮）

三局战报（seat1＝agent 侧陪练脚本 `outputs/agent-match/play_match.py`，seat0＝服务端 AiPilot）：

| 局 | 卡组 | 回合 | 结果 | 退出码 | 段数 |
|---|---|---|---|---|---|
| 1 | 20 张（lenient 旧行为） | 59 | HqZero，AI 胜 | 0 | 185 连续 |
| 2 | 20 张（lenient 旧行为） | 27 | HqZero，AI 胜 | 0 | 145 连续 |
| 3 | 39 张（修复后） | 31 | HqZero，AI 胜 | 0 | 197 连续 |

修复的两个真实缺陷：

- **D3（严重）`OrcClient.CallAsync` 的 JsonNode 父节点冲突**：桥把 stdin 帧里的 `params.arguments`
  直接传给客户端，`new JsonObject { ["arguments"] = arguments }` 换父时抛
  `InvalidOperationException: The node already has a parent`，导致**所有经桥转发的工具调用**返回
  `not-connected`（桥原状态实际不可用；`seat.state` 因走桥本地路径而幸免）。
  修复：`arguments?.DeepClone()`；新增 `ClientRobustnessTests.Call_WithArgumentsAlreadyParented_DoesNotThrow`。
- **D4 `AiDeckGenerator` 在 lenient 下仍按 strict 同名上限填充**：小卡包（5 种 × 4）只生成 20 张，
  长局牌库抽空后引擎拒绝空集合 Draw，`end_turn` 反复返回 `not-ready`（真实原因被错误码掩盖）。
  修复：新增 `relaxCopyLimit`（无头 lenient 传 true，凑满 39 张），`DeckRuleTierTests` 扩展断言。
- 附带加固：`ServerHost.Serialize` 改为“序列化字符串再解析”，避免同类 JsonNode 父冲突。

全量回归：**2305 通过 / 0 失败**（此前 2304 ＋ 新增 1 条）。

## 实打暴露的问题修复（2026-10-21T13:10Z）

| # | 问题 | 修复 | 验证 |
|---|---|---|---|
| 1 | `pendingSessionId` 提交成功后不清（只在 `target.expired`/`match.started` 清） | `SeatStateTracker.ApplyCallResult`：动作类调用结束以返回载荷 `details.pending` 为准（有则更新、无则清空） | `SeatStateTrackerTests` ×3 |
| 2 | `seat.state` 落后推送帧一帧（turn/handCount 差 1） | `seat.state`/`seat.board` 返回前用 `observe.view` 权威刷新（`ApplyView`） | `ApplyView_RefreshesTurnAndHand` ＋ 端到端 |
| 3 | `attackPreviews.{defenderDies,attackerDies}` 恒为 `false` | 按有效攻击力 vs 有效血量计算（不含词条/效果改写） | 编译＋既有投影用例 |
| 4 | `deploymentGaps.gaps` 是 gap uid，agent 无法映射到 slot | 新增桥专属 `seat.board`：直接给 `emptySupportSlots`（已排除 HQ 占槽）与 `emptyFrontSlots` | `SeatBoard_ReturnsCompactSummaryWithEmptySlots` ＋ 端到端 |
| 5 | `CanMoveAndAttack` 实为“可移动”，误导“移动后可攻击” | 不改字段名（UI 契约）；`seat.board.moves` 给出可移动目标，文档写明“移动后不能再攻击” | 文档 §6 |

另：`seat.board` 把单次决策载荷从 3–5 万字符压到约 1–2 KB。

全量回归：**2310 通过 / 0 失败**（此前 2305 ＋ 新增 5 条）。
