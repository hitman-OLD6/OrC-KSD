# 目标选择（会话化 pending）

## 交互模型

引擎发起目标选择 → 外部应答。对 MCP 而言映射为**两次工具调用**：

1. 触发动作（如 `sandbox.spawn`、`match.end_turn` 或任何需要选位的入口）→ 工具**立即返回**，不阻塞：
   ```json
   {
     "ok": true, "code": "targeting",
     "details": { "pending": {
        "sessionId": "9f0c...", "selector": "fieldUnit", "mode": "Click",
        "min": 1, "max": 1,
        "candidates": [ { "index": 1, "kind": "unit", "ref": "a1b2...", "name": "示例单位", "alive": true } ],
        "identifiers": []
     } }
   }
   ```
2. 用 `target.submit { sessionId, picks: [序号...] }` 提交，或 `target.cancel { sessionId }` 取消。

提交后返回**动作结局**；若该动作还需第二次选择，则再次返回 `code: "targeting"`（多步选择）。

## 规则

- 候选**由引擎给定**：`index` 是会话内序号（1 起），只能用序号提交；`ref` 仅供观察/对账。
- **标识集选择器**（`selector=option`：`candidates` 空、`identifiers` 非空，如 `choice` 的选项）同样用 `picks` 提交，
  按 `identifiers` 的 **1 起序号**解释；审查导出的 `choiceSelectors.picked` 与此同源（两处都是 1 起，不必换算）。
  这类请求的 `pending` 还会带 `prompt`（呈现给裁决者的提示文案）——引用类请求不带该键。
- 非法选取（如越界、数量不符）返回 `invalid-selection`，**对局不中断**，可重试（引擎侧重试上限 16）。
- 取消（含超时）按成功结果处理：`ok: true` + `code: "cancelled"`（引擎三态里的 Cancelled）。
- 等待应答有超时（默认 10 分钟，服务层可配）；超时按取消处理，工具返回会带 `timedOut` 标记。
- **挂起期间**只允许 `target.*` 与只读工具；其他输入工具一律返回 `pending-targeting`。

## 各选择器语义

| 选择器 | 场合 | 交互模式 |
|---|---|---|
| `fieldUnit` | 选场上单位 | Click |
| `handOrigin` / `unitHandDrag` | 手牌发起的拖拽 | Drag |
| `afterPlacement` | 放置后选择 | Click |
| `slot` | 选槽位 | Click |
| `targetPoint` | 选目标点 | Click |
| `hand` | 选手牌（可多选） | Click |
| `mulligan` | 换牌 | Click |
| `option` / `cardPicker` / `cardPickerReference` | 选项 / 选牌 | Click |

> 权威清单以运行时为准：`diag.hooks` 的判定器清单里含 `targeting.*` 条目；选择器名由引擎的 `SelectorNames` 常量集给定。
