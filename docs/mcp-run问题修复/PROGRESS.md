# 执行进度 — MCP 无头对战问题修复

- 更新时间: 2026-10-21T02:10:00+08:00
- 工具调用计数: ~150
- 当前步骤: 已完成
- 总体状态: 已完成

## 已完成
- S1 反制门禁：`CounterCard.IsOwnersTurn`（回合门禁，上下文不可达＝放行）＋归属过滤（`(view.Source as CardBase)?.Owner` 与反制归属相等即跳过）。
- S2 自动解除：`GameUpdates.CounterDisarmed` + `EmitCounterDisarmed`；`GameHooks` 34→35；`GameHooksJson` 条目；`CounterCard.HandleTurnStartAsync`；DIY 信号表。
- S3 选靶归属/类别：`SelectorParameter.Action`、`SelectorPresentation.Action`、`TargeterFlow` 写值、`SelectorActions` 常量；7 处构造点补 `Owner`+`Action`；`PendingSelection.Action` + `PendingJson["action"]`。
- S4 同源：`ComputeActions` 的 `moves`/`attackPreviews` 读 `GetCommandAvailability` 候选；`toZone` 扩 `support`；删除 `AttackTargets`。
- S5 state 不落后：动作类调用返回前 `RefreshViewAsync`；`SeatStateTracker.IsActionTool` 公开。
- S6 board：`isCounterArmed` + `handCount`/`armedCounterCount`。
- S7 归因：`LogCall(extra)`；`ServerHost` 调用前抓 `AnswerTarget`，写 `answers/action/picks/pickedRefs` 与发起侧 `pendingSessionId`。
- S8 口径：`IsInfrastructureError` 白名单 → `errorCount`；其余 `!ok` → `failedCalls`（summary 输出）。
- S9 语义：`PlayFailureReason.CounterUnavailable` + `CounterCard.IsDestroyed` + `UseCounterAsync` 入口守卫；`CompactCards(indexField)` → 手牌 `handIndex`、战线 `slot`；开发指南 §4、`docs/game-flow/04-打出与部署.md` 同步。
- S10 用例：`CounterEffectAssemblyTests` 两处（EndTurn 前置、销毁后改用结果断言）、`MatchOutcomeTests` 激活时序、`GameHooksTests` 计数（34→35 / 29→30 / 导出 31→32）；契约基线用 `ORC_WRITE_BASELINE=1` 重生成，diff 仅 `gameUpdates` 新增 `CounterDisarmed` + commit/generatedAt。
- 新增用例：`Orc.Game.Tests/CounterArmingWindowTests`（7，含主代理补的「己方回合 + 归属未知裸卡 → 回合门禁拦住」反例）、`Orc.Mcp.Tests/FixRegressionTests`（5，覆盖 R3/R4/R5/R8）、`Orc.Server.Tests/HeadlessAttributionTests`（3，覆盖 R6/R7）。
- 已验证：Game 1373 全绿；Mcp 112 全绿（107+5）；Server 154 全绿（151+3）。

## 进行中
- 无。

## 未开始
- 无。

## 最终验证（2026-10-21T18:26:00+08:00）
- `dotnet build OrcEngine.sln`：成功，0 警告 0 错误。
- `dotnet test tests/Orc.Game.Tests`：1373 通过 / 0 失败（主代理复跑，含新增反例）。
- `dotnet test tests/Orc.Mcp.Tests`：112 通过 / 0 失败（主代理复跑）。
- `dotnet test tests/Orc.Server.Tests`：154 通过 / 0 失败（主代理复跑）。
- 契约基线 diff：仅 `commit`/`generatedAt` 元数据 + `gameUpdates` 新增 `CounterDisarmed`。

## 阻塞 / 风险
- 工作区存在大量未提交改动（非本次范围），契约基线 diff 以其为基准解读。
- `counter.disarmed` 的原因键写成 `Reason`（PascalCase，与 `card.leaving` 一致），值 `"turn-start"` 与文档一致。
- `GameHooksTests` 另有两处计数断言（helpers 29→30、导出 signals 31→32）随新信号同步修正——文档只点名 34→35，属同一守卫。
- `match.end_turn` 返回的 `details` 本身不带 `turn` 字段（引擎结果只有状态文本），故 R4 用例以「权威视图 turn」与「推送帧旧 turn」对比断言——同一个不变量（state 不得描述动作前的局面），只是可观测通道不同。
- 旧批次文档 `docs/多人支持-服务端客户端分离/客户端接入指南.md` 仍写「attackPreviews 不判断护卫/拦截」——现已由引擎候选承载（属该文档的过期描述，未在本次范围同步）。

## 下一步
- 全量三套测试复跑，输出结论。

## S11 端到端验证（2026-10-21T18:36:00+08:00）

- 新增 `scripts/verify-counter-window.py`（经 MCP 桥打完整局，主动制造「同回合武装反制 + 打指令」）。
- 真机对局（seed 1006108646，19 回合，`outputs/verify-counter-fix/`）：
  - `counter.disarmed` 17、`counter.triggered` 23、`card.played` 17、`card.discarded` 3；
  - 己方指令不消耗己方反制（2 次现场验证）；己方回合开始自动解除、点数未回升；对手回合 `pendingSessionId` 恒 null；
  - `target.submit` 带 `answers`/`action=move`/`picks`/`pickedRefs`/段区间；`summary` 的 `errorCount=0`、`failedCalls=0`。
- 真机暴露并已修复的两个缺陷：
  1. `McpService.AiGateway.SubmitAsync` 归属判定方向反了（`!=` → `==`），R2 填上 Seat 后把 AI 自己的选靶拒掉 → 对局死锁；
  2. 待决期间 `room.resume` 被忙门禁拒 → 桥断线后无法重连（应答待决又必须先连上）→ 死锁；已放行。
- 回归：`AiGateway_AnswersRequestsOwnedByAiSeat`、`RoomResume_Is_Not_Blocked_While_Targeting`（Mcp.Tests 112→114）。
- 最终：build 0 警告 0 错误；Game 1373 / Mcp 114 / Server 154 全绿。
