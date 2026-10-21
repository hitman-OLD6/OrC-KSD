#!/usr/bin/env python3
"""OrC 对局命令行：两条连接面，一套调用语法。

为什么是两条
------------
沙盒的写类调试工具（`card.write` / `card.load` / `sandbox.*` …）只对「回环 + **未绑定座位**」
的连接可见可用；而 MCP 桥连接时会 `room.resume` 绑座，所以桥只能出牌，不能写卡。
于是：

* `mcp`  —— 起 Orc.Mcp.Bridge，走 MCP 协议（tools/call）。绑座，能出牌/攻击/结束回合。
* `orch` —— 直连 TCP（NDJSON JSON-RPC），不 resume。未绑座 → 能写卡、摆局、全知观察。

用法
----
    python tools/orc_cli.py mcp call seat.board
    python tools/orc_cli.py mcp batch '[["mulligan.done",{"owner":1}],["seat.board",{}]]'
    python tools/orc_cli.py orch call service.info
    python tools/orc_cli.py orch batch '[["card.load",{"paths":["pack/cards/x.card.json"]}]]'
    python tools/orc_cli.py mcp tools            # 列工具名（当前模式下可见的那批）

参数约定：`call` 的第二个参数是 JSON 对象字符串（缺省 `{}`）；`batch` 是 `[[tool, args], …]`。
输出：每步一行 JSON（`{"tool": …, "result": …}`），`mcp` 模式额外附桥给的 `state`。
"""

from __future__ import annotations

import argparse
import json
import socket
import subprocess
import sys
import time
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
BRIDGE_DLL = REPO / "src" / "Orc.Mcp.Bridge" / "bin" / "Debug" / "net8.0" / "Orc.Mcp.Bridge.dll"
RUN_DIR = REPO / "outputs" / "agent-match" / "mcp-run"
TOKEN_FILE = RUN_DIR / "agent.token"

HOST = "127.0.0.1"
PORT = 51888


# --------------------------------------------------------------------------- 直连（编排面）

class Direct:
    """服务端线协议：NDJSON JSON-RPC 2.0。"""

    def __init__(self, host: str = HOST, port: int = PORT, timeout: float = 30.0) -> None:
        self.sock = socket.create_connection((host, port), timeout=timeout)
        self.reader = self.sock.makefile("rb")
        self.next_id = 0

    def _send(self, payload: dict) -> None:
        self.sock.sendall((json.dumps(payload, ensure_ascii=False) + "\n").encode("utf-8"))

    def rpc(self, method: str, params: dict | None = None) -> dict:
        self.next_id += 1
        request_id = str(self.next_id)
        message: dict = {"jsonrpc": "2.0", "id": request_id, "method": method}
        if params is not None:
            message["params"] = params
        self._send(message)

        while True:
            line = self.reader.readline()
            if not line:
                raise RuntimeError("连接已被服务端关闭")
            text = line.decode("utf-8", errors="replace").strip()
            if not text:
                continue
            frame = json.loads(text)
            if frame.get("id") == request_id:
                return frame
            # notify/* 推送（id 为 null）直接丢弃：本工具不做事件流消费

    def initialize(self) -> dict:
        return self.rpc("initialize", {"clientName": "orc-cli-orchestrator"})

    def call(self, tool: str, arguments: dict | None = None) -> dict:
        response = self.rpc("tools/call", {"name": tool, "arguments": arguments or {}})
        if "error" in response:
            return {"ok": False, "code": "protocol-error", "error": response["error"]}
        return response.get("result") or {}

    def list_tools(self) -> list[str]:
        response = self.rpc("tools/list")
        return [item.get("name") for item in (response.get("result") or [])]

    def close(self) -> None:
        try:
            self.reader.close()
            self.sock.close()
        except OSError:
            pass


# --------------------------------------------------------------------------- MCP 桥（座位面）

class Bridge:
    """MCP stdio 客户端：桥自己完成 connect → initialize → room.resume。"""

    def __init__(self, token_file: Path = TOKEN_FILE, startup_timeout: float = 30.0) -> None:
        if not token_file.exists():
            raise RuntimeError(f"令牌文件不存在：{token_file}（先起沙盒实例）")
        self.proc = subprocess.Popen(
            ["dotnet", str(BRIDGE_DLL), "--connect", f"{HOST}:{PORT}", "--token-file", str(token_file)],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8", errors="replace", bufsize=1,
        )
        self.next_id = 0
        self._startup_timeout = startup_timeout

    def _rpc(self, method: str, params: dict | None = None) -> dict:
        self.next_id += 1
        request_id = self.next_id
        message: dict = {"jsonrpc": "2.0", "id": request_id, "method": method}
        if params is not None:
            message["params"] = params
        assert self.proc.stdin is not None and self.proc.stdout is not None
        self.proc.stdin.write(json.dumps(message, ensure_ascii=False) + "\n")
        self.proc.stdin.flush()

        while True:
            line = self.proc.stdout.readline()
            if not line:
                raise RuntimeError("桥已退出：" + (self.proc.stderr.read() if self.proc.stderr else ""))
            text = line.strip()
            if not text:
                continue
            frame = json.loads(text)
            if frame.get("id") == request_id:
                return frame

    def initialize(self) -> dict:
        return self._rpc("initialize", {
            "protocolVersion": "2024-11-05",
            "capabilities": {},
            "clientInfo": {"name": "orc-cli", "version": "0"},
        })

    def call(self, tool: str, arguments: dict | None = None) -> dict:
        response = self._rpc("tools/call", {"name": tool, "arguments": arguments or {}})
        result = response.get("result") or {}
        content = result.get("content") or []
        if content and content[0].get("type") == "text":
            try:
                payload = json.loads(content[0]["text"])
            except json.JSONDecodeError:
                return {"ok": False, "code": "bad-frame", "message": content[0]["text"][:500]}
            if isinstance(payload, dict):
                payload.setdefault("_mcpIsError", bool(result.get("isError")))
            return payload
        return {"ok": False, "code": "empty", "message": "桥未返回内容"}

    def list_tools(self) -> list[str]:
        response = self._rpc("tools/list")
        return [item.get("name") for item in ((response.get("result") or {}).get("tools") or [])]

    def close(self) -> None:
        try:
            if self.proc.stdin:
                self.proc.stdin.close()
            self.proc.wait(timeout=5)
        except (OSError, subprocess.TimeoutExpired):
            self.proc.kill()


# --------------------------------------------------------------------------- 入口

def parse_steps(args: argparse.Namespace) -> list[tuple[str, dict]]:
    if args.action == "batch":
        # batch 没有工具名，JSON 会落在 tool 这个位置参数上
        raw = args.steps or args.tool or ""
        if not raw.strip():
            raise SystemExit("batch 需要 [[tool, args], …] 形式的 JSON")
        payload = json.loads(raw)
        return [(str(item[0]), item[1] if len(item) > 1 else {}) for item in payload]
    arguments = json.loads(args.steps) if args.steps else {}
    return [(args.tool, arguments)]


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(prog="orc_cli.py", description="OrC 对局双通道客户端")
    parser.add_argument("channel", choices=["mcp", "orch"])
    parser.add_argument("action", choices=["call", "batch", "tools"])
    parser.add_argument("tool", nargs="?", help="call 模式的工具名")
    parser.add_argument("steps", nargs="?", default="", help="call 的 JSON 参数 / batch 的 [[tool,args],…]")
    parser.add_argument("--quiet", action="store_true", help="只打印 result，不打印 tool 名外壳")
    args = parser.parse_args(argv)

    client = None
    try:
        if args.channel == "mcp":
            client = Bridge()
            client.initialize()
        else:
            client = Direct()
            client.initialize()

        if args.action == "tools":
            for name in client.list_tools():
                print(name)
            return 0

        if args.action == "call" and not args.tool:
            print("call 模式需要工具名", file=sys.stderr)
            return 1

        steps = parse_steps(args)
        failures = 0
        for tool, arguments in steps:
            try:
                result = client.call(tool, arguments)
            except RuntimeError as exc:
                print(json.dumps({"tool": tool, "fatal": str(exc)}, ensure_ascii=False))
                return 2
            if not result.get("ok", True):
                failures += 1
            line = result if args.quiet else {"tool": tool, "result": result}
            print(json.dumps(line, ensure_ascii=False))

        return 0 if failures == 0 else 3
    finally:
        if client is not None:
            client.close()


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(main(sys.argv[1:]))
