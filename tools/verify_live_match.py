"""端到端：一个脚本同时扮演**玩家**（连实例端口）与**管理员**（连管理器端口）。

验证的三件事：
  1. 玩家连接能走 `session.attach` 进入 pending（不挂起、立即返回）；
  2. 管理员经管理器 `conn.admit` 批准后，该连接收到 `notify/admission` 并拿到座位令牌；
  3. 批准后同一个连接能读到自己的视角（`observe.view` 返回 view 帧）。

用法：
    python tools/verify_live_match.py --port 56097 --manager 51999
"""

from __future__ import annotations

import argparse
import json
import os
import socket
import sys

SAMPLE_PACK = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "samples", "card-pack-ai")
CARD_ID = "ai.sample.infantry"


class Conn:
    """NDJSON JSON-RPC 连接（服务器与管理器共用同一帧格式）。"""

    def __init__(self, port: int, timeout: float = 15.0):
        self.sock = socket.create_connection(("127.0.0.1", port), timeout=timeout)
        self.file = self.sock.makefile("rwb")
        self.next_id = 0

    def send(self, method: str, params=None, notify: bool = False):
        frame = {"jsonrpc": "2.0", "method": method}
        if not notify:
            self.next_id += 1
            # 服务器的 RpcRequest.Id 是 string（不是 JSON-RPC 里常见的数字/字符串二选一），必须传字符串。
            frame["id"] = str(self.next_id)
        if params is not None:
            frame["params"] = params
        self.file.write((json.dumps(frame, ensure_ascii=False) + "\n").encode("utf-8"))
        self.file.flush()
        return self.next_id

    def recv(self):
        line = self.file.readline()
        if not line:
            raise RuntimeError("连接已关闭")
        return json.loads(line.decode("utf-8"))

    def call(self, method: str, params=None):
        request_id = str(self.send(method, params))
        while True:
            reply = self.recv()
            # 推送（无 id）会**穿插**在响应之前（例如 attach 会先推 notify/room.seats），必须跳过。
            if "id" not in reply:
                continue
            if str(reply.get("id")) != request_id:
                continue
            if "error" in reply:
                raise RuntimeError(f"JSON-RPC 错误：{reply['error']}")
            if "result" not in reply:
                raise RuntimeError(f"响应缺少 result：{reply}")
            return reply["result"]

    def close(self):
        try:
            self.file.close()
            self.sock.close()
        except OSError:
            pass


def load_package():
    with open(os.path.join(SAMPLE_PACK, "manifest.json"), "r", encoding="utf-8") as handle:
        manifest = json.load(handle)
    return {"id": manifest["id"], "version": manifest["version"], "guid": manifest["guid"]}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, required=True, help="实例业务端口")
    parser.add_argument("--manager", type=int, default=51999, help="管理器端口")
    parser.add_argument("--instance", default="agent-live", help="管理器里的实例标识")
    parser.add_argument("--seat", type=int, default=1)
    args = parser.parse_args()

    package = load_package()

    player = Conn(args.port)
    try:
        init = player.call("initialize", {
            "clientName": "live-match",
            # 服务器用的是自家线协议版本（ProtocolInfo），不是 MCP 的日期版本号。
            "protocolVersion": "1.0",
            "label": "live-match",
        })
        print(f"[ok] initialize -> connectionId={init.get('connectionId')}")

        attach = player.call("session.attach", {
            "roles": ["controller", "viewer"],
            "seat": args.seat,
            "displayName": f"seat{args.seat}",
            "deck": [CARD_ID] * 39,
            "packages": [package],
        })
        print(f"[ok] attach -> state={attach.get('state')} seat={attach.get('seat')}")
        if attach.get("state") != "pending":
            print(f"[FAIL] 期望 pending，实际 {attach.get('state')}")
            return 1

        # 管理员通道：批准这条连接（与 MCP 的 manager.admin 走同一个管理面）
        manager = Conn(args.manager)
        try:
            admitted = manager.call("manager.admin", {
                "instanceId": args.instance,
                "op": "conn.admit",
                "args": {"connectionId": init.get("connectionId")},
            })
        finally:
            manager.close()

        if not admitted.get("ok"):
            print(f"[FAIL] conn.admit -> {admitted.get('code')}: {admitted.get('message')}")
            return 1
        print("[ok] conn.admit -> ok")

        # 等 approval 通知（中间可能有 notify/room.seats）
        token = None
        for _ in range(20):
            frame = player.recv()
            method = frame.get("method")
            if method == "notify/admission":
                params = frame.get("params") or {}
                token = params.get("seatToken")
                print(f"[ok] notify/admission -> decision={params.get('decision')} roles={params.get('roles')} "
                      f"seat={params.get('seat')} token={'(present)' if token else '(none)'}")
                break
            print(f"[..] 忽略推送 {method}")

        if not token:
            print("[FAIL] 未收到带 seatToken 的 admission 通知")
            return 1

        # 管理员通道：开局（两个 seat 此刻都有 controller：seat0=AI 配置即批准，seat1=刚批准）
        manager = Conn(args.manager)
        try:
            started = manager.call("manager.admin", {"instanceId": args.instance, "op": "match.start", "args": {}})
        finally:
            manager.close()

        if not started.get("ok"):
            print(f"[FAIL] match.start -> {started.get('code')}: {started.get('message')}")
            return 1
        payload = started.get("payload") or {}
        print(f"[ok] match.start -> definitions={payload.get('definitions')} fallback={payload.get('fallback')}")

        view = player.call("tools/call", {"name": "observe.view", "arguments": {}})
        body = view.get("content", [{}])[0].get("text") if isinstance(view.get("content"), list) else None
        parsed = json.loads(body) if body else view
        if isinstance(parsed, dict) and parsed.get("ok"):
            # 服务器 tools/call 的载荷字段是 `details`（`payload` 是管理器侧的叫法，两者不同）。
            payload = parsed.get("details") or parsed.get("payload") or {}
            view_obj = payload.get("view") or {}
            hand = view_obj.get("selfHand") or []
            print(f"[ok] observe.view -> perspective={payload.get('perspective')} "
                  f"phase={view_obj.get('phase')} turn={view_obj.get('turn')} hand={len(hand)}")
        else:
            print(f"[FAIL] observe.view -> {parsed}")
            return 1

        print()
        print("RESULT: 玩家接入链路通（attach -> pending -> admit -> admission -> observe.view）")
        return 0
    finally:
        player.close()


if __name__ == "__main__":
    sys.exit(main())
