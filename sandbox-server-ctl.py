#!/usr/bin/env python3
"""Orc 服务器管理器的**薄包装**（起 / 停 / 查 / 重启）。

定位
----
服务端生命周期已交给**管理器**（R1）：一实例＝一子进程，管理面是**父子私有通道**
（子进程 stdin/stdout），只有创建者（管理器进程）能调，不监听端口。
因此本脚本不再直接拼 `Orc.Server` 的参数，只做三件事：

1. 起一个**管理器进程**（新控制台窗口），由它 spawn 一个 sandbox 实例；
2. 记 pid / 端口，供 `status` / `stop` 使用；
3. 启动前的自检（构建产物、卡包、端口、mcp.json 端口一致性）。

管理操作（批准连接、开局、终止、重置沙盒包）在**那个新窗口**里手输 NDJSON——
这就是父子私有通道的命令格式（不是远程协议）：

    {"op":"connections.list"}
    {"op":"conn.admit","args":{"connectionId":"<id>"}}
    {"op":"match.start"}
    {"op":"room.abort"}

用法
----
    python sandbox-server-ctl.py start      # 新窗口起管理器 + sandbox 实例
    python sandbox-server-ctl.py status     # 端口/pid 探测
    python sandbox-server-ctl.py stop       # 按 pid 精确停（管理器会回收子进程树）
    python sandbox-server-ctl.py restart
    python sandbox-server-ctl.py check      # 只自检，不启动

退出码：0 成功 / 1 参数或启动失败 / 2 运行期探测失败。
"""

from __future__ import annotations

import argparse
import json
import os
import socket
import subprocess
import sys
import time
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent
MANAGER_DLL = REPO_ROOT / "artifacts" / "slot" / "bin" / "Orc.Manager" / "debug" / "Orc.Manager.dll"
SERVER_DLL = REPO_ROOT / "src" / "Orc.Server" / "bin" / "Debug" / "net8.0" / "Orc.Server.dll"
DEFAULT_RUN_DIR = REPO_ROOT / "outputs" / "agent-match" / "mcp-run"
DEFAULT_PACKS = REPO_ROOT / "samples" / "card-pack-demo"
DEFAULT_PORT = 51888
DEFAULT_INSTANCE = "sandbox"
MCP_CONFIG = Path.home() / ".workbuddy" / "mcp.json"

PID_FILE = DEFAULT_RUN_DIR / "manager.pid"

IS_WINDOWS = os.name == "nt"


def log(message: str) -> None:
    print(message, flush=True)


def port_open(port: int, host: str = "127.0.0.1", timeout: float = 0.4) -> bool:
    try:
        with socket.create_connection((host, port), timeout=timeout):
            return True
    except OSError:
        return False


def read_pid() -> int | None:
    try:
        return int(PID_FILE.read_text(encoding="utf-8").strip())
    except (OSError, ValueError):
        return None


def write_pid(pid: int) -> None:
    PID_FILE.parent.mkdir(parents=True, exist_ok=True)
    PID_FILE.write_text(str(pid), encoding="utf-8")


def pid_alive(pid: int) -> bool:
    if pid <= 0:
        return False
    if IS_WINDOWS:
        try:
            out = subprocess.check_output(
                ["tasklist", "/FI", f"PID eq {pid}", "/NH"], text=True, stderr=subprocess.DEVNULL)
        except subprocess.CalledProcessError:
            return False
        return str(pid) in out
    try:
        os.kill(pid, 0)
        return True
    except OSError:
        return False


def seats_json() -> str:
    # 座位形态由 spawn 配置决定：0＝AI（配置即已批准），1＝agent（等 session.attach + 管理员批准）。
    return json.dumps(
        [{"kind": "ai", "displayName": "电脑"}, {"kind": "agent"}], separators=(",", ":"))


def build_command(args: argparse.Namespace) -> list[str]:
    return [
        "dotnet", str(MANAGER_DLL), "spawn",
        "--id", args.instance,
        "--mode", "sandbox",
        "--allow-debug-writes",
        "--packs", str(Path(args.packs).resolve()),
        "--seats", seats_json(),
        "--port", str(args.port),
        "--deck-rules", "lenient",
        "--seed", str(args.seed),
        "--label", args.instance,
        "--serve-stdin",
    ]


def check(args: argparse.Namespace) -> int:
    ok = True

    log("[check] dotnet ............")
    if subprocess.call(["dotnet", "--version"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL) == 0:
        log("        OK")
    else:
        log("        MISSING - 安装 .NET 8 SDK/runtime")
        ok = False

    for label, path in (("manager dll", MANAGER_DLL), ("server dll", SERVER_DLL)):
        log(f"[check] {label} ..... " + ("OK  " + str(path) if path.exists() else "MISSING " + str(path)))
        ok = ok and path.exists()

    manifest = Path(args.packs) / "manifest.json"
    log("[check] card pack ......... " + ("OK  " + str(manifest) if manifest.exists() else "MISSING " + str(manifest)))
    ok = ok and manifest.exists()

    busy = port_open(args.port)
    log(f"[check] port {args.port} ...... " + ("IN USE（先 stop）" if busy else "FREE"))
    ok = ok and not busy

    if MCP_CONFIG.exists():
        blob = MCP_CONFIG.read_text(encoding="utf-8", errors="replace")
        aligned = f"127.0.0.1:{args.port}" in blob
        log("[check] mcp.json ......... " + ("port OK" if aligned else f"port MISMATCH（应为 127.0.0.1:{args.port}）"))
        ok = ok and aligned
    else:
        log(f"[check] mcp.json ......... MISSING {MCP_CONFIG}")

    log("[check] done." if ok else "[check] FAILED.")
    return 0 if ok else 1


def start(args: argparse.Namespace) -> int:
    if port_open(args.port):
        log(f"[start] FAILED：端口 {args.port} 已在监听（先 python sandbox-server-ctl.py stop）。")
        return 1

    if not MANAGER_DLL.exists() or not SERVER_DLL.exists():
        log("[start] FAILED：构建产物缺失。先 dotnet build OrcEngine.sln")
        return 1

    DEFAULT_RUN_DIR.mkdir(parents=True, exist_ok=True)
    command = build_command(args)

    # 新控制台窗口：管理命令就在那个窗口里手输（父子私有通道）。
    creationflags = getattr(subprocess, "CREATE_NEW_CONSOLE", 0) if IS_WINDOWS else 0

    log("[start] " + " ".join(command))
    process = subprocess.Popen(command, cwd=str(REPO_ROOT), creationflags=creationflags)
    write_pid(process.pid)

    deadline = time.time() + args.timeout
    while time.time() < deadline:
        if port_open(args.port):
            log(f"[start] 实例已就绪：port={args.port}（管理窗口 PID={process.pid}）")
            log("[start] 管理操作在**那个新窗口**里手输 NDJSON，例如：")
            log('        {"op":"connections.list"}')
            log('        {"op":"conn.admit","args":{"connectionId":"<id>"}}')
            log('        {"op":"match.start"}')
            return 0
        if process.poll() is not None:
            log(f"[start] FAILED：管理器退出（exit={process.returncode}）。")
            return 1
        time.sleep(0.5)

    log(f"[start] TIMEOUT：{args.timeout}s 内端口未就绪（PID={process.pid} 仍在运行）。")
    return 2


def status(args: argparse.Namespace) -> int:
    pid = read_pid()
    alive = pid is not None and pid_alive(pid)
    listening = port_open(args.port)

    log(f"pid        : {pid if pid is not None else '-'} (alive={alive})")
    log(f"port {args.port}  : {'LISTENING' if listening else 'closed'}")

    return 0 if listening else 2


def stop(args: argparse.Namespace) -> int:
    pid = read_pid()
    if pid is None or not pid_alive(pid):
        log("[stop] 没有在跑的管理器（pid 文件缺失或进程已退出）。")
        try:
            PID_FILE.unlink()
        except OSError:
            pass
        return 0

    # 只杀管理器；它的 Dispose 会 Kill(entireProcessTree:true) 回收服务器子进程。
    if IS_WINDOWS:
        subprocess.call(["taskkill", "/PID", str(pid), "/T", "/F"], stdout=subprocess.DEVNULL)
    else:
        try:
            os.kill(pid, 15)
        except OSError:
            pass

    try:
        PID_FILE.unlink()
    except OSError:
        pass

    log(f"[stop] 已停止管理器 PID={pid}（含子进程树）。")
    return 0


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Orc 管理器包装（sandbox 实例）")
    subparsers = parser.add_subparsers(dest="command")

    def add_common(target: argparse.ArgumentParser) -> None:
        target.add_argument("--port", type=int, default=DEFAULT_PORT)
        target.add_argument("--packs", default=str(DEFAULT_PACKS))
        target.add_argument("--instance", default=DEFAULT_INSTANCE)
        target.add_argument("--seed", type=int, default=20261021)
        target.add_argument("--timeout", type=float, default=60.0)

    add_common(parser)

    for name, help_text in (
        ("start", "新窗口起管理器 + sandbox 实例"),
        ("status", "端口/pid 探测"),
        ("stop", "按 pid 精确停"),
        ("restart", "stop 后 start"),
        ("check", "只自检，不启动"),
    ):
        add_common(subparsers.add_parser(name, help=help_text))

    args = parser.parse_args(argv)

    if args.command in (None, "start"):
        return start(args)
    if args.command == "status":
        return status(args)
    if args.command == "stop":
        return stop(args)
    if args.command == "restart":
        stop(args)
        time.sleep(1.0)
        return start(args)
    if args.command == "check":
        return check(args)

    parser.print_help()
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
