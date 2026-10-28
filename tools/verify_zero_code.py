"""零代码验证：完全按文档里的步骤，只用 MCP 工具走完两条路。

  A. 打 AI        ：spawn(play) -> connect(玩家座) -> match.start -> observe.view -> 走一步
  B. 沙盒做卡     ：spawn(sandbox) -> connect(调试面) -> card.write -> 校验 -> 销毁

不直接碰实例端口，也不碰 bridge：所有操作都经 `orc-manager` 这一条 MCP 连接。
这证明了「宿主不重启也能拉起服务器并用起来」。
"""

from __future__ import annotations

import json
import os
import subprocess
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DLL = os.path.join(REPO, "artifacts", "slot", "bin", "Orc.Manager", "debug", "Orc.Manager.dll")
PACK = os.path.join(REPO, "samples", "card-pack-ai")
PORT = 51999

with open(os.path.join(PACK, "manifest.json"), "r", encoding="utf-8") as handle:
    manifest = json.load(handle)
PACKAGE = {"id": manifest["id"], "version": manifest["version"], "guid": manifest["guid"]}
DECK = ["ai.sample.infantry"] * 39


class Mcp:
    def __init__(self):
        self.proc = subprocess.Popen(
            ["dotnet", DLL, "mcp", "--connect", f"127.0.0.1:{PORT}"],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8", bufsize=1,
        )
        self.next_id = 0
        self.pending_notifications: list[dict] = []

    def _read(self):
        while True:
            line = self.proc.stdout.readline()
            if not line:
                raise RuntimeError("前端关闭了 stdout")
            frame = json.loads(line)
            if "id" not in frame:
                self.pending_notifications.append(frame)
                continue
            return frame

    def call(self, method, params=None):
        self.next_id += 1
        frame = {"jsonrpc": "2.0", "id": str(self.next_id), "method": method}
        if params is not None:
            frame["params"] = params
        self.proc.stdin.write(json.dumps(frame, ensure_ascii=False) + "\n")
        self.proc.stdin.flush()
        return self._read()

    def tool(self, name, arguments):
        reply = self.call("tools/call", {"name": name, "arguments": arguments})
        result = reply["result"]
        body = json.loads(result["content"][0]["text"])
        return result["isError"], body

    def close(self):
        self.proc.stdin.close()
        self.proc.wait(timeout=10)


failures = []


def check(label, ok, detail=""):
    print(f"[{'PASS' if ok else 'FAIL'}] {label}" + (f"  ({detail})" if detail else ""))
    if not ok:
        failures.append(label)


def main() -> int:
    mcp = Mcp()
    try:
        mcp.call("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                                "clientInfo": {"name": "zero-code", "version": "0"}})

        # ---------------------------------------------------------------- A. 打 AI
        print("\n=== A. 打 AI ===")
        err, spawned = mcp.tool("manager.spawn", {
            "instanceId": "ai-1",
            "packDirectories": [PACK],
            "seats": [{"kind": "ai", "displayName": "电脑"}, {"kind": "agent"}],
            "mode": "play",
            "deckRules": "none",
            "label": "zero-code-ai",
        })
        check("spawn(play) 返回端口", not err and spawned["payload"]["port"] > 0,
              f"port={spawned.get('payload', {}).get('port')}")

        err, conn = mcp.tool("manager.connect", {
            "instanceId": "ai-1", "seat": 1, "deck": DECK, "packages": [PACKAGE],
        })
        session = conn.get("payload", {}).get("sessionId") if not err else None
        tools = [t.get("name") for t in (conn.get("payload", {}).get("tools") or [])] if not err else []
        check("connect(seat=1) 得到 sessionId", bool(session), f"sessionId={session}")
        check("connect 返回该连接的可用工具", len(tools) > 5, f"{len(tools)} 个，如 {tools[:4]}")

        err, started = mcp.tool("manager.admin", {"instanceId": "ai-1", "op": "match.start", "args": {}})
        check("match.start 开局", not err and started["ok"], str(started.get("payload", {}).get("definitions")))

        err, view = mcp.tool("manager.action", {"sessionId": session, "tool": "observe.view"})
        # 经管理器转发时，实例的 `details` 被放进管理器的 `payload`（两层载荷字段名不同）。
        details = view.get("payload") or view.get("details") or {}
        hand = (details.get("view") or {}).get("selfHand") or []
        check("observe.view 看到自己手牌", not err and len(hand) > 0,
              f"hand={len(hand)} phase={(details.get('view') or {}).get('phase')}")

        # 走一步：结束回合（换牌期先确认）
        mcp.tool("manager.action", {"sessionId": session, "tool": "mulligan.done", "arguments": {"owner": 1}})
        err, turned = mcp.tool("manager.action", {"sessionId": session, "tool": "match.end_turn"})
        check("match.end_turn 被引擎接受", not err and turned.get("ok") is True, turned.get("code", ""))

        mcp.tool("manager.disconnect", {"sessionId": session})
        mcp.tool("manager.kill", {"instanceId": "ai-1"})

        # ---------------------------------------------------------------- B. 沙盒做卡
        print("\n=== B. 沙盒做卡 ===")
        err, spawned = mcp.tool("manager.spawn", {
            "instanceId": "sb-1",
            "packDirectories": [PACK],
            "seats": [{"kind": "ai"}, {"kind": "agent"}],
            "mode": "sandbox",
            "allowDebugWrites": True,
            "deckRules": "none",
            "label": "zero-code-sandbox",
        })
        check("spawn(sandbox)", not err, f"port={spawned.get('payload', {}).get('port')}")

        err, conn = mcp.tool("manager.connect", {"instanceId": "sb-1"})
        debug_session = conn.get("payload", {}).get("sessionId") if not err else None
        debug_tools = [t.get("name") for t in (conn.get("payload", {}).get("tools") or [])] if not err else []
        check("connect(调试面) 得到 sessionId", bool(debug_session), f"sessionId={debug_session}")
        check("调试面能看到 card.write", "card.write" in debug_tools, f"{debug_tools[:6]}")

        card = {
            "schemaVersion": 1,
            "id": "zero.code.probe",
            "name": "零代码探针卡",
            "components": [
                {"component": "typeCategory", "type": ["infantry"]},
                {"component": "factionCost", "faction": "neutral", "kredits": 1},
                {"component": "battleStats", "operationCost": 1, "attack": 1, "defense": 1},
                {"component": "tagData", "rarity": "standard", "tags": ["sandbox"]},
            ],
        }
        err, wrote = mcp.tool("manager.action", {
            "sessionId": debug_session,
            "tool": "card.write",
            "arguments": {
                "path": "pack/cards/zero.code.probe.card.json",
                "json": json.dumps(card, ensure_ascii=False),
                "overwrite": True,
            },
        })
        check("card.write 造卡", not err and wrote.get("ok") is True, wrote.get("code", ""))

        err, validated = mcp.tool("manager.action", {"sessionId": debug_session, "tool": "asset.validate"})
        check("asset.validate 通过", not err and validated.get("ok") is True, validated.get("code", ""))

        err, sessions = mcp.tool("manager.sessions", {})
        check("manager.sessions 可见代持连接", not err and sessions["payload"]["count"] >= 1,
              f"count={sessions.get('payload', {}).get('count')}")

        mcp.tool("manager.disconnect", {"sessionId": debug_session})
        mcp.tool("manager.kill", {"instanceId": "sb-1"})

        # 收尾：确认干净
        err, listed = mcp.tool("manager.list", {})
        check("实例已全部销毁", listed["payload"]["count"] == 0, f"count={listed['payload']['count']}")
    finally:
        mcp.close()

    print()
    if failures:
        print(f"RESULT: {len(failures)} FAILED -> {failures}")
        return 1
    print("RESULT: 零代码链路全通（A 打 AI ＋ B 沙盒做卡，全程只经 orc-manager 一条 MCP）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
