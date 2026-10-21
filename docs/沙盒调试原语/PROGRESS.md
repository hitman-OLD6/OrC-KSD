# 执行进度 — 沙盒调试原语

- 更新时间: 2026-10-21T14:18:00+08:00
- 工具调用计数: 30
- 当前步骤: S9（核验收尾）
- 总体状态: 已完成（1 项验收受既有引擎缺口阻塞、1 项构建设施受外部进程阻塞）

## 已完成

### 本轮（核验轮）：准出补裁逐条复核 ＋ 全量回归

| 补裁项 | 落点 | 复核结论 |
|---|---|---|
| S8 指令链「先推进回合」 | `tests/Orc.Mcp.Tests/SandboxPrimitiveChainTests.cs:126,177` | `EnsureTurnOfAsync` 在 `play.command` 前以 `match.end_turn` 推进到卡 owner；三条链均通过 |
| D10 判定器改写 | `src/Orc.Game/Sandbox/SandboxPrimitives.cs:475-502`（`GainPointsAsync`/`LosePointsAsync`）＋ `SandboxPrimitiveTools.cs:243-247` | `after != target` ⇒ `warning="points-adjusted"` ＋ `requested`/`actual`；用例 `SetPoints_WithGainJudicator_AppliesWrappedAmount` 通过 |
| D11 视图共用兜底 | `SandboxPrimitives.cs:327-551`（handler 首行 `Fail(result, reason)`） | 六 handler 无业务抛出；`Unitize_MissingSlot_ReturnsReason` 通过 |
| 内核纯加性 | `src/Orc/Core/Trigger.cs`（`+_sealed` / `+IsSealed` / `+Seal()` / `+ThrowIfSealed()` ＋ 4 处守卫） | `Register`×2 / `RegisterDisposal` / `RegisterWrapperCore` 首行守卫；默认不封闭；`Trigger_DefaultNotSealed_ExistingRegistrationStillWorks` 通过 |
| 禁止直写 `Slot.Place` | `grep -rn "\.Place" src/Orc.Game/Sandbox/` | 仅命中 `PlaceToHand/DeckTop/SupportLine` 与失败原因常量；单位上前线走 `JoinUnitAsync` |
| 旧工具零变更 | `src/Orc.Mcp/Tools/ShapeTools.cs:24,43,60,79,98` | 仅 description 末尾追加「（逐步由原语替代）」；参数/行为/返回结构未动；`ExistingSandboxTools_ContractUnchanged`、`LegacySetPoints_ContractUnchanged` 通过 |

### S1–S9（前轮落地，本轮复跑确认）

| 步 | 内容 | 用例数 / 结果 |
|---|---|---|
| S1 | 内核封闭态 ＋ 六保留触发器（注册唯一 handler → 立即 `Seal`） | 6 / 全通过 |
| S2 | `MatchOptions.SandboxAuthorized` ＋ `Match.Sandbox` ＋ 两处装配点 | 3 / 全通过 |
| S3 | 六入口（授权→参数→硬门槛→Invoke→读载体→审计）＋ 结果载体/失败常量 | 4 / 全通过 |
| S4 | 六 handler（唯一写点；`remove` 三分支；`set_points` 升/降/同值） | 23 / 全通过 |
| S5 | 审计留痕（`RootStream.WriteLog`，不进总线、不产段） | 1 / 全通过 |
| S6 | `diag.audit_chain` 增 `reservedTriggers` 只读节点 | 3 / 全通过 |
| S7 | `SandboxPrimitiveTools`（6 工具）＋ 目录登记 ＋ 旧五件套标注 | 8 / 全通过 |
| S8 | 指令链 / 反制链（部分）/ 新卡链 ＋ 等价性 | 5 / 全通过 |
| S9 | 零影响（段数/摘要/无痕迹/不入编排） | 3 / 全通过 |

引擎级沙盒用例（`tests/Orc.Game.Tests/Sandbox`）**39 例全通过**；MCP 层沙盒＋诊断用例 **21 例全通过**。

## 全量回归（`--no-build`，exit 0）

| 项目 | 通过 | 失败 |
|---|---|---|
| Orc.Tests | 370 | 0 |
| Orc.Script.Tests | 17 | 0 |
| Orc.Packages.Tests | 356 | 0 |
| Orc.Ai.Tests | 13 | 0 |
| Orc.Mcp.Tests | 107 | 0 |
| Orc.Server.Tests | 151 | 0 |
| Orc.Game.Tests | 1352 | 0 |
| **合计** | **2366** | **0** |

基线 2310 ⇒ 新增 56 例（`Orc.Game.Tests/Sandbox` 39 ＋ `Orc.Server.Tests` 1 ＋ `Orc.Mcp.Tests` 16），零回归。

## 进行中

无。

## 未开始

无（S1–S9 全部落地）。

## 阻塞与风险

1. **`dotnet build OrcEngine.slnx` / `dotnet test OrcEngine.slnx`（带构建）非 0 错误**——外部进程
   `Orc.Server.dll --headless --mode play --seat0 ai --seat1 agent --port 51888 --wait-agent 7200`（PID 29880，13:41 启动）
   锁定 `src/Orc.Server/bin/Debug/net8.0/*.dll`，仅 MSB3021/MSB3027 拷贝错误（代码 0 错误）。
   - 本轮应对：各项目单独构建（0 错误），`Orc.Server.Tests` 用 `-p:BuildProjectReferences=false` 构建
     （读取 `Orc.Server/bin` 中已是最新的 `Orc.Server.dll`，不写该目录），再以 `dotnet test OrcEngine.slnx --no-build` 跑全量 ⇒ 2366/0/exit 0。
   - 该进程在等待 agent（`--wait-agent 7200`，最早 15:41 自退）；**未擅自终止**用户对局。
   - 待进程退出后，`dotnet build OrcEngine.slnx` 与 `dotnet test OrcEngine.slnx` 可直接复跑验证。
2. **R6 反制链「Counter 段 Stop ＋ 一次性弃置」不可达（既有引擎缺口，非本批引入）**：
   `CounterCard.AddEffectHandler` 在 `src` 内零调用者（`grep -rn "AddEffectHandler" src` 仅命中定义与文档注释），
   数据侧 `effects` 组件不会被挂成 Counter 段 handler。用例
   `CounterChain_PrimitiveBuiltCounter_ActivatesAndOrderResolves` 锁定「激活成功 ＋ 指令照常扣费离手」，
   `Stop` / `card.discarded` 断言待缺口补齐后补（补齐属引擎语义变更，超出本批 Excludes）。
3. **`src/Orc.Mcp/Tools/` 全目录被 `.gitignore:30 **/tools/` 忽略**（既有仓库配置）：工具层改动不会出现在 `git status`，
   交付时需按文件清单人工核对/加白名单，否则易漏提交。

## 下一步

1. PID 29880 退出后复跑带构建的全量（预期同为 2366/0）。
2. 如需闭合 R6 反制链，另立需求补 `effects → CounterCard.AddEffectHandler` 装配，并补 `Stop` / `card.discarded` 断言。
3. 处理 `.gitignore` 对 `src/Orc.Mcp/Tools/` 的忽略，确保工具层文件可入库。

## 与 implementation.md 的偏差（沿用前轮，理由已记录）

1. `SandboxPrimitiveView` 增 `Zone` 字段——`hand` / `deck_top` 无法由 `line` 表达。
2. `unitize` 若卡在该玩家手牌，先 `Hand.Remove`（无信号）再 `JoinUnitAsync`——否则「既在手牌又在场」破坏实例唯一归属不变量。
3. `AuditChain_Unauthorized_MarkedUnavailable` 落在引擎级（`Export_MarksUnauthorized_AsUnavailable`）：MCP 层无法构造未授权对局（`sandbox.create` 恒授权），导出器与 `diag.audit_chain` 同源。
4. 反制链用例名由 `CounterChain_PrimitiveBuiltCounter_StopsOrder` 改为 `..._ActivatesAndOrderResolves`（原因见阻塞 2）。
