"""一次性验证：像宿主那样通过 stdio MCP 前端驱动管理器。

不依赖 WorkBuddy：这里扮演一个标准 MCP 客户端（initialize → initialized → tools/list → tools/call），
断言每一帧都是合法 JSON-RPC，并把关键字段打印出来。
"""

import json
import subprocess
import sys

DLL = r"D:\OrC-KSD\src\Orc.Manager\bin\Debug\net8.0\Orc.Manager.dll"
PORT = 51999

proc = subprocess.Popen(
    ["dotnet", DLL, "mcp", "--connect", f"127.0.0.1:{PORT}"],
    stdin=subprocess.PIPE,
    stdout=subprocess.PIPE,
    stderr=subprocess.PIPE,
    text=True,
    encoding="utf-8",
    bufsize=1,
)

_next = 0


def send(method, params=None):
    global _next
    _next += 1
    frame = {"jsonrpc": "2.0", "id": _next, "method": method}
    if params is not None:
        frame["params"] = params
    proc.stdin.write(json.dumps(frame, ensure_ascii=False) + "\n")
    proc.stdin.flush()
    return _next


def notify(method, params=None):
    frame = {"jsonrpc": "2.0", "method": method}
    if params is not None:
        frame["params"] = params
    proc.stdin.write(json.dumps(frame, ensure_ascii=False) + "\n")
    proc.stdin.flush()


def read():
    line = proc.stdout.readline()
    if not line:
        raise RuntimeError("前端意外关闭了 stdout")
    return json.loads(line)


def call(name, arguments):
    send("tools/call", {"name": name, "arguments": arguments})
    reply = read()
    result = reply["result"]
    payload = json.loads(result["content"][0]["text"]) if not result["isError"] else None
    return result["isError"], payload, result


failures = []


def check(label, condition, detail=""):
    mark = "PASS" if condition else "FAIL"
    print(f"[{mark}] {label}" + (f"  ({detail})" if detail else ""))
    if not condition:
        failures.append(label)


# 1) initialize
send("initialize", {
    "protocolVersion": "2024-11-05",
    "capabilities": {},
    "clientInfo": {"name": "workbuddy-sim", "version": "0"},
})
reply = read()
info = reply["result"]["serverInfo"]
check("initialize", info["name"] == "orc-manager", f"serverInfo={info}")

# 2) initialized (notification, no response expected)
notify("notifications/initialized")

# 3) tools/list
send("tools/list")
reply = read()
tools = [t["name"] for t in reply["result"]["tools"]]
check("tools/list count == 6", len(tools) == 6, f"{tools}")
check("has manager.spawn", "manager.spawn" in tools)

# 4) manager.ping
is_error, payload, _ = call("manager.ping", {})
check("manager.ping", not is_error and payload["ok"] is True, str(payload))

# 5) manager.list (empty)
is_error, payload, _ = call("manager.list", {})
check("manager.list empty", not is_error and payload["payload"]["count"] == 0, str(payload["payload"]))

# 6) manager.spawn
is_error, payload, _ = call("manager.spawn", {
    "instanceId": "mcp-smoke",
    "packDirectories": [r"D:\OrC-KSD\samples\card-pack-ai"],
    "seats": [{"kind": "ai"}, {"kind": "agent"}],
    "mode": "sandbox",
    "allowDebugWrites": True,
    "deckRules": "none",
    "label": "mcp-smoke",
})
check("manager.spawn", not is_error and payload["ok"] is True, str(payload.get("payload")))
port = payload["payload"]["port"] if payload and payload.get("payload") else None
check("spawn returns port", isinstance(port, int) and port > 0, f"port={port}")

# 7) manager.list (1)
is_error, payload, _ = call("manager.list", {})
check("manager.list count == 1", payload["payload"]["count"] == 1, str(payload["payload"]))

# 8) manager.admin instance.info
is_error, payload, _ = call("manager.admin", {
    "instanceId": "mcp-smoke",
    "op": "instance.info",
    "args": {},
})
state = payload["payload"]["roomState"] if payload and payload.get("payload") else None
check("admin instance.info", not is_error and state == "Lobby", f"roomState={state}")

# 9) manager.kill
is_error, payload, _ = call("manager.kill", {"instanceId": "mcp-smoke"})
check("manager.kill", not is_error and payload["ok"] is True, str(payload.get("payload")))

# 10) manager.list (0)
is_error, payload, _ = call("manager.list", {})
check("manager.list empty after kill", payload["payload"]["count"] == 0, str(payload["payload"]))

proc.stdin.close()
proc.wait(timeout=10)

stderr = proc.stderr.read()
if stderr.strip():
    print("--- front stderr ---")
    print(stderr.strip()[:500])

print()
if failures:
    print(f"RESULT: {len(failures)} FAILED -> {failures}")
    sys.exit(1)

print("RESULT: all checks passed")
