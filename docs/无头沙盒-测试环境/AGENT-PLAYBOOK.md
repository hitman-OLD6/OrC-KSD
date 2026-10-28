# AGENT PLAYBOOK — 用真实 MCP 工具与启发式 AI 打一局

> 给「新会话 / 重启后的 agent」的交接单。目标：**通过 MCP 工具（不是脚本）打赢 seat0 的启发式 AI，并记录潜在 bug。**

## 1. 当前实例（可能已过期，先核对）

| 项 | 值 |
|---|---|
| 服务端 PID | `29880` |
| 端口 | `51888` |
| 运行目录 | `D:/OrC-KSD/outputs/agent-match/mcp-run` |
| 座位令牌 | 同目录 `agent.token`（内容即令牌） |
| 模式 | `--headless --mode play --packs samples/card-pack-demo --seat0 ai --seat1 agent --exit-on-end` |
| 等待上限 | `--wait-agent 7200`（自 2026-10-21 13:41 起算，约 15:41 到期） |

核对命令（Git Bash）：

```bash
tasklist //FI "PID eq 29880" //FO CSV
netstat -ano | grep ":51888.*LISTEN"
```

实际启动命令（已核对，2026-10-21 15:09）：

```bash
dotnet artifacts/slot/bin/Orc.Server/debug/Orc.Server.dll \
  --headless --mode play --packs samples/card-pack-demo \
  --seat0 ai --seat1 agent --port 51888 \
  --run-dir ./outputs/agent-match/mcp-run \
  --wait-agent 7200 --exit-on-end
```

### 实例已退出时：重启

```bash
cd D:/OrC-KSD
dotnet artifacts/slot/bin/Orc.Server/debug/Orc.Server.dll \
  --headless --mode play --packs samples/card-pack-demo \
  --seat0 ai --seat1 agent --port 51888 \
  --run-dir D:/OrC-KSD/outputs/agent-match/mcp-run \
  --wait-agent 3600 --exit-on-end
```

> 端口必须与 `~/.workbuddy/mcp.json` 里的 `--connect` 一致；`--run-dir` 必须与 `--token-file` 的父目录一致。

## 2. MCP 配置

`C:/Users/baciliouce/.workbuddy/mcp.json`：

```json
{
  "mcpServers": {
    "orc-seat1": {
      "command": "dotnet",
      "args": [
        "D:/OrC-KSD/src/Orc.Mcp.Bridge/bin/Debug/net8.0/Orc.Mcp.Bridge.dll",
        "--connect", "127.0.0.1:51888",
        "--token-file", "D:/OrC-KSD/outputs/agent-match/mcp-run/agent.token"
      ]
    }
  }
}
```

宿主把工具名暴露为 `mcp__orc-seat1__<tool>`（形如 `seat_state` / `observe_view` / `play_unit`）。
**工具未出现＝会话启动快照未包含它**：新开会话或重启 WorkBuddy 后再试。

## 3. 工具面（33 个）

桥专属：`seat.state`（轮次/是否轮到你/待决/段统计）、`seat.segments`（回看事件段）。
服务端：`observe.view`（自己视角的整枚投影 + `actions`）、`mulligan.begin/done`、
`play.unit / play.unit_pre / play.join / play.command / play.command_pre / play.counter / play.can_play`、
`command.availability / command.begin / command.move / command.attack / command.leave`、
`match.end_turn / match.concede`、`target.submit / target.cancel / target.claim`、
`room.*`、`service.*`、`docs.*`。

每次 `tools/call` 的返回是 `{"result": <服务端结果>, "state": <桥精简状态>}`。

> `play` 模式下**没有** `sandbox.*` 调试原语（`instantiate` / `unitize` / `move` / `remove` / `points` / `draw`）——它们需要 `--mode sandbox --allow-debug-writes`，且只对回环未绑定连接可见。摆局面做卡请改用沙盒模式（见 `开发指南.md` §5.6）。

## 4. 打法（目标：赢）

循环：

1. `seat.state` → 看 `phase` / `yourTurn` / `pendingSessionId`。
2. `phase == "mulligan"` → `mulligan.done {owner:1}`。
3. `yourTurn == false` → 等（AI 会自己走；不必轮询太密）。
4. `yourTurn == true` → `observe.view` 取 `details.view` 与 `details.actions`：
   - **出牌**：手牌里 `cardType == "unit"` 的，选 `supportLineSlotCount` 内的空槽（`selfLine[].slotIndex` 之外的），
     `play.unit {ref, owner:1, slot}`；指令卡用 `play.command_pre {ref}` 再应答 pending。
   - **攻击**：`command.availability {ref}` → `attackAvailable` 为真才 `command.attack {ref}`；
     pending 出现时用 `target.submit {sessionId, picks}`，**优先选 `kind == "hq"` 的候选**。
   - **没动作**→ `match.end_turn`。
5. 终局：`view.phase == "over"`，读 `summary.json`（`winner`、`turns`、`endReason`）。

关键取舍（上一轮输的原因）：只用单位 + 攻击不够；优先把**费用打满**、优先攻击能一击击杀的单位，
不要反复试探明显打不出的牌（上一轮 `play.unit` 被拒 173 次，全是穷举尝试）。

## 5. 已知坑（遇到就记，别绕过）

- 牌库抽空后 `match.end_turn` 会返回 `not-ready`，message 为「卡组名单为空，无法抽取」——这是引擎拒绝空抽，
  不是协议 bug。无头 lenient 档已修为 39 张，正常不会撞上。
- `--run-dir` / `--sandbox` 传相对路径已在 `Program.cs` 绝对化（旧 dll 会抛 `PathViolationException`）。
- 固定端口被占用时，报错是裸 `SocketException: 每个套接字地址只能使用一次`，不含端口号（**待改进**）。
- 桥的 `tools/call` 曾因 `OrcClient.CallAsync` 的 JsonNode 父节点冲突全部失败（已修，含回归测试）。

## 6. 这一局要重点观察

1. `seat.state` 与 `observe.view` 的 `turn/activePlayerSide` 是否一致（桥快照 vs 服务端投影）。
2. `target.submit` 的候选序号是否始终有效（跨回合、跨动作）。
3. 一局结束后 `--exit-on-end` 是否真的退出、`summary.json` 是否完整、`events.jsonl` 是否连续。
4. 桥在长时间静置（AI 连续行动）后是否仍能拉到最新状态（段 `dropped` 计数）。
