#!/usr/bin/env python3
"""沙盒局内「生成新卡 + 新效果」验证对局。

一条脚本内同时挂两条连接（文档 §5.4／§10.3 的标准姿势）：

  * 编排连接（原始 TCP，回环 + **未绑定座位**）→ card.write / card.load / sandbox.*
  * 座位连接（Orc.Mcp.Bridge，MCP stdio）      → mulligan.done / play.* / command.* / match.end_turn

**为什么必须一条脚本跑完**：桥断开后服务端按 `DisconnectGrace`（默认 60 秒）判负，
分步手工调用会在宽限期后把对局打没。所以连接要在整个对局期间保持。

剧本：
  1. 换牌 → 进入 play
  2. 常规打 1 个我方回合（出牌 / 攻击 / 结束回合），让对局处于"进行中"
  3. **局内**写一张全新卡（含部署效果）→ card.load 注册进当前对局 → sandbox.instantiate 造到手上
     → play.unit 走完整动作链打出（校验费用/手牌/槽位）
  4. 断言新效果真的结算（读 events.jsonl：damage 事件 + 敌方单位掉血）
  5. 继续推进到终局，打印验证清单

用法：python tools/sandbox_newcard_duel.py
前置：python sandbox-server-ctl.py start --foreground（实例在 51888 上跑着）
"""

from __future__ import annotations

import json
import socket
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
BRIDGE_DLL = ROOT / "src" / "Orc.Mcp.Bridge" / "bin" / "Debug" / "net8.0" / "Orc.Mcp.Bridge.dll"
RUN = ROOT / "outputs" / "agent-match" / "mcp-run"
TOKEN = RUN / "agent.token"
EVENTS = RUN / "events.jsonl"
PORT = 51888
SEAT = 1

LOG: list[str] = []
CHECKS: list[tuple[str, bool, str]] = []

# 局内新卡：部署时对敌方一个单位造成 2 点伤害（csx 效果，写法对齐 samples/card-pack-demo）
NEW_CARD_ID = "sandbox.shocktrooper"
NEW_CARD_PATH = f"pack/cards/{NEW_CARD_ID}.card.json"
NEW_CARD = {
    "schemaVersion": 1,
    "id": NEW_CARD_ID,
    "name": "突击工兵",
    "components": [
        {"component": "typeCategory", "type": ["infantry"]},
        {"component": "factionCost", "faction": "Germany", "kredits": 1},
        {"component": "battleStats", "operationCost": 1, "attack": 2, "defense": 3},
        {"component": "tagData", "rarity": "Standard"},
        {
            "component": "effects",
            "inline": [
                {
                    "schemaVersion": 2,
                    "root": {
                        "id": "sandbox.shocktrooper.effect",
                        "version": 1,
                        "mainTrigger": {
                            "id": "t.main",
                            "stableKey": "局内新卡·突击工兵（部署对敌方单位造成 2 点伤害）",
                            "kind": "passive",
                            "viewType": "Orc.Cards.CardEventView",
                            "hooks": ["unit.deployed"],
                            "events": [
                                {
                                    "id": "onUnitDeployed",
                                    "entry": "HandleAsync",
                                    "version": 1,
                                    "csx": (
                                        "// 局内新卡·突击工兵：unit.deployed → Host==载荷单位 → 对敌方一个单位造成 2 点伤害。\n"
                                        "Func<Orc.Cards.CardEventView, Context, CancellationToken, Task> HandleAsync = async (view, ctx, ct) =>\n"
                                        "{\n"
                                        "    if (view.Unit is not Orc.Cards.Card deployed || !object.ReferenceEquals(deployed, view.Host))\n"
                                        "    {\n"
                                        "        return;\n"
                                        "    }\n"
                                        "\n"
                                        "    var runtime = Orc.Game.Effects.EffectRuntime.ResolveFor(deployed);\n"
                                        "    if (runtime is null)\n"
                                        "    {\n"
                                        "        return;\n"
                                        "    }\n"
                                        "\n"
                                        "    var targets = await runtime.SelectAsync(deployed, new Orc.Game.Effects.EffectSelector(\"one\", \"enemy\", null, null, null, null, null));\n"
                                        "    foreach (var target in targets)\n"
                                        "    {\n"
                                        "        await runtime.DamageAsync(target, 2, deployed, ct);\n"
                                        "    }\n"
                                        "};\n"
                                    ),
                                }
                            ],
                            "version": 1,
                        },
                        "otherTriggers": [],
                        "wrappers": [],
                        "injects": [],
                    },
                }
            ],
        },
    ],
}


def log(message: str) -> None:
    print(message, flush=True)
    LOG.append(message)


def check(name: str, ok: bool, detail: str = "") -> None:
    CHECKS.append((name, ok, detail))
    log(f"  [{'PASS' if ok else 'FAIL'}] {name}{(' — ' + detail) if detail else ''}")


# --------------------------------------------------------------------------- 编排连接

class Wire:
    """未绑座位的编排连接：沙盒写类工具只对它可见。"""

    def __init__(self, port: int = PORT) -> None:
        self.sock = socket.create_connection(("127.0.0.1", port), timeout=10)
        self.sock.settimeout(30)
        self.buffer = b""
        self.seq = 0

    def _read_line(self) -> dict:
        while b"\n" not in self.buffer:
            chunk = self.sock.recv(1 << 20)
            if not chunk:
                raise RuntimeError("编排连接被关闭")
            self.buffer += chunk
        line, self.buffer = self.buffer.split(b"\n", 1)
        return json.loads(line.decode("utf-8"))

    def rpc(self, method: str, params: dict | None = None) -> dict:
        self.seq += 1
        rid = f"orch-{self.seq}"
        message: dict = {"jsonrpc": "2.0", "id": rid, "method": method}
        if params is not None:
            message["params"] = params
        self.sock.sendall((json.dumps(message) + "\n").encode("utf-8"))
        while True:
            frame = self._read_line()
            if frame.get("id") == rid:
                return frame

    def call(self, tool: str, args: dict | None = None) -> dict:
        response = self.rpc("tools/call", {"name": tool, "arguments": args or {}})
        if response.get("error"):
            return {"ok": False, "code": "rpc-error", "message": response["error"].get("message")}
        return response.get("result") or {}

    def close(self) -> None:
        try:
            self.sock.close()
        except OSError:
            pass


# --------------------------------------------------------------------------- 座位连接（MCP 桥）

class Mcp:
    def __init__(self, port: int = PORT, token: Path = TOKEN) -> None:
        self.proc = subprocess.Popen(
            ["dotnet", str(BRIDGE_DLL), "--connect", f"127.0.0.1:{port}", "--token-file", str(token)],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8", errors="replace", bufsize=1,
        )
        self.seq = 0
        self.closed = False

    def _rpc(self, method: str, params: dict | None = None) -> dict | None:
        self.seq += 1
        message: dict = {"jsonrpc": "2.0", "id": self.seq, "method": method}
        if params is not None:
            message["params"] = params
        try:
            assert self.proc.stdin is not None and self.proc.stdout is not None
            self.proc.stdin.write(json.dumps(message, ensure_ascii=False) + "\n")
            self.proc.stdin.flush()
        except OSError:
            self.closed = True
            return None
        while True:
            line = self.proc.stdout.readline()
            if not line:
                self.closed = True
                return None
            text = line.strip()
            if not text:
                continue
            frame = json.loads(text)
            if frame.get("id") == self.seq:
                return frame

    def call(self, tool: str, args: dict | None = None) -> dict:
        response = self._rpc("tools/call", {"name": tool, "arguments": args or {}})
        if response is None:
            return {}
        content = (response.get("result") or {}).get("content") or []
        if not content:
            return {}
        return json.loads(content[0]["text"])

    def do(self, tool: str, args: dict | None = None) -> dict:
        return (self.call(tool, args) or {}).get("result") or {}

    def board(self) -> dict:
        return (self.call("seat.board") or {}).get("result", {}).get("details") or {}

    def view(self) -> dict:
        return ((self.call("observe.view") or {}).get("result") or {}).get("details", {}).get("view") or {}


def find_pending(node) -> dict | None:
    if isinstance(node, dict):
        pending = node.get("pending")
        if isinstance(pending, dict) and pending.get("sessionId"):
            return pending
        for value in node.values():
            found = find_pending(value)
            if found:
                return found
    elif isinstance(node, list):
        for value in node:
            found = find_pending(value)
            if found:
                return found
    return None


def submit(mcp: Mcp, pending: dict, prefer: tuple[str, ...] = ("unit", "hq")) -> dict:
    candidates = pending.get("candidates") or []
    picks: list[int] = []
    if candidates:
        pick = candidates[0].get("index")
        for kind in prefer:
            hit = next((c for c in candidates if c.get("kind") == kind), None)
            if hit:
                pick = hit.get("index")
                break
        picks = [pick]
    log(f"      选靶 {[(c.get('kind'), c.get('label') or c.get('name')) for c in candidates]} → picks={picks}")
    return mcp.do("target.submit", {"sessionId": pending["sessionId"], "picks": picks})


# --------------------------------------------------------------------------- 事件流

def event_tail(limit: int = 6) -> list[tuple]:
    if not EVENTS.exists():
        return []
    lines = EVENTS.read_text(encoding="utf-8", errors="replace").splitlines()[-limit:]
    rows: list[tuple] = []
    for line in lines:
        try:
            segment = json.loads(line)
        except json.JSONDecodeError:
            continue
        for entry in segment.get("entries", []):
            rows.append((segment.get("sequence"), entry.get("kind"), entry.get("source"), entry.get("message")))
    return rows


def event_count_of_damage(since_sequence: int) -> int:
    """统计某段之后的 damage 类事件（按 kind 包含 damage 计）。"""
    if not EVENTS.exists():
        return 0
    total = 0
    for line in EVENTS.read_text(encoding="utf-8", errors="replace").splitlines():
        try:
            segment = json.loads(line)
        except json.JSONDecodeError:
            continue
        if (segment.get("sequence") or 0) <= since_sequence:
            continue
        for entry in segment.get("entries", []):
            kind = str(entry.get("kind") or "")
            message = str(entry.get("message") or "")
            if "damage" in kind.lower() or "伤害" in message:
                total += 1
    return total


def last_sequence() -> int:
    if not EVENTS.exists():
        return 0
    lines = EVENTS.read_text(encoding="utf-8", errors="replace").splitlines()
    for line in reversed(lines):
        try:
            return json.loads(line).get("sequence") or 0
        except json.JSONDecodeError:
            continue
    return 0


# --------------------------------------------------------------------------- 对局动作

def wait_my_turn(mcp: Mcp, timeout: float = 90.0) -> dict:
    deadline = time.monotonic() + timeout
    board: dict = {}
    while time.monotonic() < deadline and not mcp.closed:
        board = mcp.board()
        if board.get("phase") == "over" or board.get("yourTurn"):
            return board
        time.sleep(0.4)
    return board


def play_turn(mcp: Mcp, board: dict, *, tag: str = "") -> None:
    """一个我方回合：先出单位，再打能打死的攻击，最后结束回合。"""
    if not board.get("yourTurn"):
        return
    turn = board.get("turn")

    for unit in board.get("hand", []):
        if unit.get("uid") not in (board.get("playable") or []):
            continue
        empty = board.get("emptySupportSlots") or []
        if not empty:
            break
        result = mcp.do("play.unit", {"ref": unit["uid"], "owner": SEAT, "slot": empty[0]})
        pending = find_pending(result)
        if pending:
            submit(mcp, pending, prefer=("slot",))
        if result.get("ok") or pending:
            log(f"  [T{turn}] 出单位「{unit.get('name')}」→ support {empty[0]} {tag}")
            board = mcp.board()
            break

    for preview in board.get("attackPreviews") or []:
        if preview.get("defenderDies") and not preview.get("attackerDies"):
            result = mcp.do("command.attack", {"ref": preview["attackerUid"]})
            pending = find_pending(result)
            if pending:
                submit(mcp, pending, prefer=("unit", "hq"))
            if result.get("ok") or pending:
                log(f"  [T{turn}] 攻击 defender={(preview.get('defenderUid') or '')[:8]} {tag}")
                break

    mcp.do("match.end_turn")
    log(f"  [T{turn}] 结束回合")


# --------------------------------------------------------------------------- 主流程

def main() -> int:
    if not TOKEN.exists():
        print(f"令牌不存在：{TOKEN}；先起沙盒实例。", file=sys.stderr)
        return 2

    wire = Wire()
    wire.rpc("initialize", {"clientName": "sandbox-orchestrator"})
    log("[编排] 已接入（回环 + 未绑定座位 → 写类调试工具可见）")

    mcp = Mcp()
    mcp._rpc("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                            "clientInfo": {"name": "workbuddy-agent", "version": "1"}})
    log("[MCP ] 桥已接入（已 room.resume → seat1）")

    # ---------- 1. 开局 ----------
    state: dict = {}
    for _ in range(150):
        state = (mcp.call("seat.state") or {}).get("state") or {}
        if state.get("phase"):
            break
        time.sleep(0.3)
    log(f"\n[1] 开局：phase={state.get('phase')} hand={state.get('handCount')}")

    if state.get("phase") == "mulligan":
        result = mcp.do("mulligan.done", {"owner": SEAT})
        pending = find_pending(result)
        if pending:
            mcp.do("target.submit", {"sessionId": pending["sessionId"], "picks": []})
        log("    换牌已确认（保留全部手牌）")

    board = wait_my_turn(mcp)
    log(f"[1] 进入 play：T{board.get('turn')} HQ 我{board.get('hq', {}).get('self')}/敌{board.get('hq', {}).get('enemy')}")

    # ---------- 2. 先打一个正常回合，让对局"进行中" ----------
    log("\n[2] 先打一个常规回合（对局进入进行中）")
    play_turn(mcp, board)
    board = wait_my_turn(mcp)
    log(f"    回到我方：T{board.get('turn')} 敌方场上 {len(board.get('enemyLine') or [])} 个单位")

    # 确保敌方场上有单位，新卡效果才有靶子
    if not board.get("enemyLine"):
        log("    敌方空场 → 用 sandbox.instantiate 给 AI 摆一个靶子")
        wire.call("sandbox.instantiate", {"definitionId": "demo.rifleman", "owner": 0,
                                          "zone": "support", "slot": 0})
        board = mcp.board()

    enemy_before = {u.get("uid"): u.get("def") or u.get("hp") for u in (board.get("enemyLine") or [])}
    log(f"    敌方血量基线：{[(u.get('name'), enemy_before.get(u.get('uid'))) for u in (board.get('enemyLine') or [])]}")

    # ---------- 3. 局内生成新卡 ----------
    log("\n[3] 局内新卡链：card.write → card.load → sandbox.instantiate → play.unit")
    marker = last_sequence()

    write = wire.call("card.write", {
        "path": NEW_CARD_PATH,
        "json": json.dumps(NEW_CARD, ensure_ascii=False),
        "overwrite": True,
    })
    check("card.write 落盘（写盘即校验）", bool(write.get("ok")),
          f"{write.get('code')} {write.get('message')}")

    load = wire.call("card.load", {"files": [NEW_CARD_PATH]})
    load_detail = json.dumps(load.get("details") or {}, ensure_ascii=False)[:240]
    check("card.load 注册进当前对局", bool(load.get("ok")), load_detail)

    listing = wire.call("card.list", {})
    registered = (listing.get("details") or {}).get("registeredInMatch") or []
    check("新卡出现在对局已注册定义里",
          any(NEW_CARD_ID in str(item) for item in registered),
          f"registered={len(registered)}")

    wire.call("sandbox.points", {"owner": SEAT, "points": 12})
    inst = wire.call("sandbox.instantiate", {"definitionId": NEW_CARD_ID, "owner": SEAT, "zone": "hand"})
    ref = (inst.get("details") or {}).get("ref")
    check("sandbox.instantiate 造卡到手", bool(inst.get("ok")) and bool(ref), f"ref={ref}")

    # 走完整动作链打出（这一步会校验费用/手牌/槽位，与沙盒原语不同）
    board = wait_my_turn(mcp)
    empty = board.get("emptySupportSlots") or []
    if not empty:
        log("    支援线满 → sandbox.points 后仍无可出槽，跳过实打验证")
        check("play.unit 实打新卡", False, "无空支援槽")
    else:
        before_kredits = (board.get("kredits") or {}).get("self")
        result = mcp.do("play.unit", {"ref": ref, "owner": SEAT, "slot": empty[0]})
        pending = find_pending(result)
        if pending:
            submit(mcp, pending, prefer=("slot",))
        after = mcp.board()
        after_kredits = (after.get("kredits") or {}).get("self")
        on_line = any(u.get("uid") == ref for u in (after.get("selfLine") or []))
        check("play.unit 实打新卡", bool(result.get("ok")) or bool(pending),
              f"{result.get('code') or ''} {result.get('message') or ''}".strip())
        check("新卡已在场（selfLine）", on_line, f"kredits {before_kredits} → {after_kredits}")

    # ---------- 4. 断言新效果结算 ----------
    time.sleep(1.2)
    board = mcp.board()
    enemy_after = {u.get("uid"): u.get("def") or u.get("hp") for u in (board.get("enemyLine") or [])}
    damaged = [uid for uid, hp in enemy_before.items()
               if uid in enemy_after and enemy_after[uid] is not None and hp is not None and enemy_after[uid] < hp]
    damage_events = event_count_of_damage(marker)

    log(f"\n[4] 效果断言：敌方血量 {[(k[:8], v) for k, v in enemy_after.items()]}，掉血单位 {[u[:8] for u in damaged]}")
    check("新效果触发（敌方单位掉血或死亡）",
          bool(damaged) or damage_events > 0,
          f"damage 事件 {damage_events} 条")

    log("    事件流尾部：")
    for seq, kind, source, message in event_tail(4):
        log(f"      #{seq} {kind} | {source} | {message}")

    # ---------- 5. 打到终局 ----------
    log("\n[5] 继续推进到终局")
    guard = 0
    while guard < 400 and not mcp.closed:
        guard += 1
        board = mcp.board()
        if not board:
            time.sleep(0.3)
            continue
        if board.get("phase") == "over":
            break
        if not board.get("yourTurn"):
            time.sleep(0.3)
            continue
        play_turn(mcp, board)

    final = mcp.board()
    log(f"    终局：phase={final.get('phase')} T{final.get('turn')} "
        f"HQ 我{final.get('hq', {}).get('self')}/敌{final.get('hq', {}).get('enemy')}")

    # ---------- 汇总 ----------
    log("\n=== 验证清单 ===")
    for name, ok, detail in CHECKS:
        log(f"  {'✓' if ok else '✗'} {name}{(' — ' + detail) if detail else ''}")
    passed = sum(1 for _, ok, _ in CHECKS if ok)
    log(f"  {passed}/{len(CHECKS)} 通过")

    summary = RUN / "summary.json"
    if summary.exists():
        payload = json.loads(summary.read_text(encoding="utf-8"))
        log(f"  单局摘要：{json.dumps({k: payload.get(k) for k in ('winner','turns','endReason','seed')}, ensure_ascii=False)}")

    (RUN / "newcard-duel.log").write_text("\n".join(LOG), encoding="utf-8")
    wire.close()
    mcp.proc.terminate()
    return 0 if passed == len(CHECKS) else 1


if __name__ == "__main__":
    sys.exit(main())
