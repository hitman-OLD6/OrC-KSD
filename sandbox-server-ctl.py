#!/usr/bin/env python3
"""无头沙盒服务端的**进程管理 + 自检**脚本（起 / 停 / 查 / 重启），供 MCP 桥接入 + 局内写卡。

定位
----
`start-sandbox-server.bat` 是**给人双击的启动器**（前台窗口 = 服务在跑，关窗即停）。
本脚本管的是它管不了的部分：后台起、按 pid 精确停、查状态、重启，以及启动后的配置自检。
名字里的 `ctl` 就是 control——不要把它当成唯一启动入口。

设计要点
--------
1. **脱离当前 shell**：用 DETACHED_PROCESS 起服务端，脚本退出后实例继续存活
   （不传 `--parent-pid`，避免父进程兜底把实例带走）。
   ⚠ 已知限制：若调用方处在 Job Object 里（agent 沙盒、部分编辑器/CI），
   `CREATE_BREAKAWAY_FROM_JOB` 可能被拒，实例会随父任务一起回收——
   这种环境请改用前台常驻，或让用户双击 `start-sandbox-server.bat`。
2. **端口/令牌对齐宿主 MCP 配置**：默认 `--port 51888`、`--run-dir outputs/agent-match/mcp-run`，
   也就是 `~/.workbuddy/mcp.json` 里 `orc-seat1` 写死的那两个值。改端口前先改 mcp.json。
3. **写类调试工具的门槛**：`card.*` / `sandbox.*` / `effect.write` 等只对「回环 + 未绑定座位」的
   编排连接可见可用；座位桥会 `room.resume` 绑座，因此**写卡走 orc-orch（`--no-resume`），
   出牌走 orc-seat1**。两条桥都指向同一个端口，互不干扰（一个绑座、一个不绑）。

用法
----
    python sandbox-server-ctl.py start            # 起（默认子命令；加 --foreground 常驻前台）
    python sandbox-server-ctl.py status           # 看状态
    python sandbox-server-ctl.py stop             # 停（按 pid，校验 dotnet 映像，绝不盲杀）
    python sandbox-server-ctl.py restart          # 重启
    python sandbox-server-ctl.py start --packs samples/card-pack-demo --seed 20261021

退出码：0 成功 / 1 参数或启动失败 / 2 运行期探测失败。
"""

from __future__ import annotations

import argparse
import ctypes
import json
import os
import socket
import subprocess
import sys
import time
from ctypes import wintypes
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent
SERVER_DLL = REPO_ROOT / "src" / "Orc.Server" / "bin" / "Debug" / "net8.0" / "Orc.Server.dll"
DEFAULT_RUN_DIR = REPO_ROOT / "outputs" / "agent-match" / "mcp-run"
DEFAULT_PACKS = REPO_ROOT / "samples" / "card-pack-demo"
DEFAULT_PORT = 51888
MCP_CONFIG = Path.home() / ".workbuddy" / "mcp.json"

IS_WINDOWS = os.name == "nt"
READY_TIMEOUT = 120.0


# --------------------------------------------------------------------------- 输出

def log(message: str) -> None:
    print(f"[sandbox] {message}", flush=True)


def err(message: str) -> None:
    print(f"[sandbox] {message}", file=sys.stderr, flush=True)


# --------------------------------------------------------------------------- 探测

def port_in_use(port: int) -> bool:
    """回环上能否建立连接（能＝已有实例在听）。"""
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
        probe.settimeout(0.3)
        return probe.connect_ex(("127.0.0.1", port)) == 0


def pid_alive(pid: int) -> bool:
    """进程是否存活。

    **不要用 tasklist 解析文本**：中文 Windows 下 tasklist 输出是 GBK 字节，
    按 UTF-8 解码会直接抛 UnicodeDecodeError。这里走 Win32 API，与代码页无关。
    """
    if pid <= 0:
        return False
    if IS_WINDOWS:
        return _win_process_alive(pid)
    try:
        os.kill(pid, 0)
        return True
    except OSError:
        return False


PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
STILL_ACTIVE = 259


def _win_handle(pid: int):
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    handle = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    return kernel32, handle


def _win_process_alive(pid: int) -> bool:
    kernel32, handle = _win_handle(pid)
    if not handle:
        return False
    try:
        code = wintypes.DWORD()
        if not kernel32.GetExitCodeProcess(handle, ctypes.byref(code)):
            return False
        return code.value == STILL_ACTIVE
    finally:
        kernel32.CloseHandle(handle)


def process_image(pid: int) -> str | None:
    """进程可执行文件全路径（用于"杀之前确认确实是服务端"）。"""
    if not IS_WINDOWS or pid <= 0:
        return None
    kernel32, handle = _win_handle(pid)
    if not handle:
        return None
    try:
        buffer = ctypes.create_unicode_buffer(32768)
        size = wintypes.DWORD(len(buffer))
        if not kernel32.QueryFullProcessImageNameW(handle, 0, buffer, ctypes.byref(size)):
            return None
        return buffer.value or None
    finally:
        kernel32.CloseHandle(handle)


def read_pid(pid_file: Path) -> int:
    try:
        return int(pid_file.read_text(encoding="utf-8").strip())
    except (OSError, ValueError):
        return 0


def tail(path: Path, lines: int = 20) -> str:
    try:
        return "\n".join(path.read_text(encoding="utf-8", errors="replace").splitlines()[-lines:])
    except OSError:
        return "(无)"


# --------------------------------------------------------------------------- 启动

def build_command(args: argparse.Namespace, run_dir: Path) -> list[str]:
    command = [
        "dotnet", str(SERVER_DLL),
        "--headless",
        "--mode", args.mode,
        "--packs", args.packs,
        "--seat0", args.seat0,
        "--seat1", args.seat1,
        "--run-dir", str(run_dir),
        "--port", str(args.port),
        "--deck-rules", args.deck_rules,
        "--wait-agent", str(args.wait_agent),
    ]
    if args.seed is not None:
        command += ["--seed", str(args.seed)]
    if args.log_seat_views:
        command.append("--log-seat-views")
    if args.keep_alive:
        command.append("--keep-alive")
    if args.mode == "sandbox" and args.allow_debug_writes:
        command.append("--allow-debug-writes")
    return command


def spawn_detached(command: list[str], run_dir: Path) -> tuple[int, Path, Path]:
    """脱离当前进程启动服务端；返回 (pid, stdout 路径, stderr 路径)。

    Windows 上光靠 `DETACHED_PROCESS` 不够：若调用方（终端/编辑器/agent 沙箱）在 Job Object 里，
    子进程仍会被 job 一起回收。必须再带 `CREATE_BREAKAWAY_FROM_JOB` 才能真正独立；
    该标志要求 job 允许 breakaway，被拒（WinError 5）时退回不带 breakaway 的方式。
    """
    out_path = run_dir / "server.stdout.log"
    err_path = run_dir / "server.stderr.log"
    out_file = open(out_path, "w", encoding="utf-8", newline="\n")
    err_file = open(err_path, "w", encoding="utf-8", newline="\n")

    base_flags = 0
    if IS_WINDOWS:
        base_flags = (
            getattr(subprocess, "DETACHED_PROCESS", 0x00000008)
            | getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0x00000200)
        )
    breakaway = 0x01000000 if IS_WINDOWS else 0

    def _launch(flags: int) -> subprocess.Popen:
        return subprocess.Popen(
            command,
            cwd=str(REPO_ROOT),
            stdin=subprocess.DEVNULL,
            stdout=out_file,
            stderr=err_file,
            creationflags=flags,
            start_new_session=not IS_WINDOWS,
        )

    try:
        process = _launch(base_flags | breakaway)
    except OSError:
        # job 不允许 breakaway：退回普通 detached（宿主独占 Job 时会被连带回收）
        out_file = open(out_path, "w", encoding="utf-8", newline="\n")
        err_file = open(err_path, "w", encoding="utf-8", newline="\n")
        process = _launch(base_flags)
        log("⚠ CREATE_BREAKAWAY_FROM_JOB 被拒，退回普通 detached 启动。")

    pid = process.pid
    # 句柄交给子进程，父进程侧不留引用
    out_file.close()
    err_file.close()
    return pid, out_path, err_path


def wait_ready(pid: int, out_path: Path, err_path: Path) -> dict | None:
    """在 stdout 日志里找就绪行（JSON 对象，含 port）。逐行扫描，容忍 dotnet 的其它输出。"""
    deadline = time.monotonic() + READY_TIMEOUT
    while time.monotonic() < deadline:
        if out_path.exists():
            text = out_path.read_text(encoding="utf-8", errors="replace")
            for line in text.splitlines():
                line = line.strip()
                if not line.startswith("{"):
                    continue
                try:
                    payload = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if isinstance(payload, dict) and "port" in payload:
                    return payload
        if not pid_alive(pid):
            err("服务端进程已退出，stderr 尾部：")
            err(tail(err_path))
            return None
        time.sleep(0.4)
    err(f"等待就绪行超时（{READY_TIMEOUT:.0f}s），stderr 尾部：")
    err(tail(err_path))
    return None


def mcp_config_check(run_dir: Path, port: int) -> None:
    """核对宿主 mcp.json 是否与本次实例对得上（对不上就明确告诉用户改哪一项）。"""
    token_path = run_dir / "agent.token"
    if not MCP_CONFIG.exists():
        log(f"未找到 MCP 配置 {MCP_CONFIG}（不影响服务端，只影响宿主接入）。")
        return
    try:
        config = json.loads(MCP_CONFIG.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        log(f"MCP 配置无法解析：{MCP_CONFIG}")
        return

    servers = config.get("mcpServers") or {}
    matched = None
    for name, entry in servers.items():
        blob = " ".join(str(item) for item in entry.get("args", []))
        if str(port) in blob:
            matched = (name, blob)
            break

    if matched is None:
        log(f"⚠ MCP 配置里没有指向端口 {port} 的桥，宿主侧需要改成：")
        log(f'  "args": ["--connect", "127.0.0.1:{port}", "--token-file", "{token_path}"]')
        return

    name, blob = matched
    if str(token_path).replace("\\", "/") not in blob.replace("\\", "/"):
        log(f"⚠ MCP 桥 {name} 的令牌路径与本次运行目录不一致，应为：{token_path}")
    else:
        log(f"MCP 桥配置核对通过：{name} → 127.0.0.1:{port}")


def cmd_start(args: argparse.Namespace) -> int:
    run_dir = Path(args.run_dir).resolve()
    pid_file = run_dir / "server.pid"

    if not SERVER_DLL.exists():
        err(f"未找到服务端构建产物：{SERVER_DLL}")
        err("先构建：dotnet build OrcEngine.slnx（或加 --build 让本脚本代建）")
        return 1

    old_pid = read_pid(pid_file)
    if port_in_use(args.port):
        if old_pid and pid_alive(old_pid):
            err(f"端口 {args.port} 已被本脚本上次拉起的实例占用（pid {old_pid}）。")
            err("如需重启：python sandbox-server-ctl.py restart")
        else:
            err(f"端口 {args.port} 已被其它进程占用。换端口（并同步改 mcp.json）或先停掉占用者。")
        return 1

    if old_pid and pid_alive(old_pid):
        err(f"旧实例 pid {old_pid} 仍在运行但端口未监听，先 stop 再 start。")
        return 1

    run_dir.mkdir(parents=True, exist_ok=True)
    for name in ("summary.json", "events.jsonl", "calls.jsonl",
                 "seat0.jsonl", "seat1.jsonl", "agent.token"):
        stale = run_dir / name
        if stale.exists():
            try:
                stale.unlink()
            except OSError:
                pass

    if args.build:
        log("构建 OrcEngine.slnx ...")
        completed = subprocess.run(
            ["dotnet", "build", "OrcEngine.slnx"], cwd=str(REPO_ROOT),
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
            encoding="utf-8", errors="replace",
        )
        if completed.returncode != 0:
            err("构建失败：")
            err((completed.stdout or "")[-3000:])
            return 1

    command = build_command(args, run_dir)

    # 前台模式：不 detach，直接在当前进程里跑。
    # 用途＝在 agent 会话里用「后台任务」挂住实例（会话能回收 detached 子进程，但回收不了
    # 尚在运行的后台任务）；用户双击 .bat 时走 detached 模式。
    if args.foreground:
        log("前台模式启动：" + " ".join(command))
        log("（Ctrl+C 停止；本进程退出＝实例退出）")
        process = subprocess.Popen(command, cwd=str(REPO_ROOT))
        pid_file.write_text(str(process.pid), encoding="utf-8")
        try:
            return process.wait()
        except KeyboardInterrupt:
            process.terminate()
            log("收到中断，实例已停止。")
            return 0
        finally:
            pid_file.unlink(missing_ok=True)

    log("启动：" + " ".join(f'"{part}"' if " " in part else part for part in command))

    pid, out_path, err_path = spawn_detached(command, run_dir)
    ready = wait_ready(pid, out_path, err_path)
    if ready is None:
        return 2

    pid_file.write_text(str(pid), encoding="utf-8")
    token_path = Path(ready.get("agentTokenPath") or (run_dir / "agent.token"))

    log("=" * 72)
    log(f"实例就绪：pid {pid}，端口 {ready.get('port')}，模式 {ready.get('mode', args.mode)}")
    log(f"运行目录：{ready.get('runDir', run_dir)}")
    log(f"事件流：{ready.get('eventsPath')}")
    log(f"工具流水：{ready.get('callsPath')}")
    if ready.get("agentSeat") is not None:
        log(f"agent 座位：{ready['agentSeat']}，令牌：{token_path}")
    log(f"沙盒根（写卡落点）：{run_dir / 'sandbox'}")
    log("-" * 72)
    log("两条连接的分工（写类调试工具只认「回环 + 未绑座位」）：")
    log("  · MCP 桥 orc-seat1（--token-file）：room.resume 绑座 → 出牌/攻击/结束回合（play.*、command.*、seat.board）")
    log("  · MCP 桥 orc-orch（--no-resume）：只连不绑座 → card.write / card.load / effect.write / sandbox.* / diag.*")
    log("-" * 72)
    mcp_config_check(run_dir, int(ready.get("port") or args.port))
    log("停止：python sandbox-server-ctl.py stop")
    log("=" * 72)
    return 0


def cmd_stop(_args: argparse.Namespace) -> int:
    run_dir = Path(_args.run_dir).resolve()
    pid_file = run_dir / "server.pid"
    pid = read_pid(pid_file)

    # 端口没在听＝实例已经没了（正常终局 / 重启过电脑 / 崩溃）。此时只清 pid 文件，
    # **绝不** 按 pid 杀进程：重启后 pid 会被别的进程复用，盲目 taskkill 会误杀。
    if not port_in_use(_args.port):
        if pid_file.exists():
            pid_file.unlink()
        log(f"端口 {_args.port} 未监听，没有在跑的实例（已清理 pid 文件，未终止任何进程）。")
        return 0

    if not pid or not pid_alive(pid):
        err(f"端口 {_args.port} 有监听，但 pid 文件记录（{pid or '空'}）不是存活进程。")
        err(f"用 netstat -ano | grep :{_args.port} 找到占用者后自行处理。")
        return 2

    image = process_image(pid)
    if image and not image.lower().endswith("dotnet.exe"):
        err(f"pid {pid} 不是 dotnet 进程（{image}），拒绝终止。")
        return 2

    if IS_WINDOWS:
        subprocess.run(["taskkill", "/F", "/T", "/PID", str(pid)],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    else:
        os.kill(pid, 15)

    for _ in range(30):
        if not pid_alive(pid) and not port_in_use(_args.port):
            break
        time.sleep(0.2)

    if pid_alive(pid):
        err(f"pid {pid} 未能回收。")
        return 2

    pid_file.unlink(missing_ok=True)
    log(f"已停止实例 pid {pid}。")
    return 0


def cmd_status(args: argparse.Namespace) -> int:
    run_dir = Path(args.run_dir).resolve()
    pid = read_pid(run_dir / "server.pid")
    alive = pid_alive(pid)
    listening = port_in_use(args.port)

    log(f"pid: {pid or '(无)'} → {'存活' if alive else '未运行'}")
    log(f"端口 {args.port}: {'监听中' if listening else '未监听'}")

    summary = run_dir / "summary.json"
    if summary.exists():
        try:
            payload = json.loads(summary.read_text(encoding="utf-8"))
            log("单局摘要：" + json.dumps(
                {k: payload.get(k) for k in ("winner", "turns", "endReason", "seed", "aborted")},
                ensure_ascii=False))
        except json.JSONDecodeError:
            log(f"摘要无法解析：{summary}")

    if alive and listening:
        log("结论：实例在跑，可以接 MCP 桥 / 编排连接。")
        return 0
    if alive and not listening:
        log("结论：pid 存活但端口未监听（实例异常或正在关闭），执行 restart。")
        return 2
    if listening and not alive:
        log("结论：端口被其它进程占用（不是本脚本的实例）。")
        return 2
    log("结论：没有在跑的实例。执行 start 拉起。")
    return 2


def cmd_restart(args: argparse.Namespace) -> int:
    cmd_stop(args)
    return cmd_start(args)


# --------------------------------------------------------------------------- 入口

def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(
        prog="sandbox-server-ctl.py",
        description="拉起无头沙盒服务端（sandbox + allow-debug-writes），对齐宿主 MCP 桥配置。",
    )
    parser.add_argument("command", nargs="?", default="start",
                        choices=["start", "stop", "status", "restart"])
    parser.add_argument("--port", type=int, default=DEFAULT_PORT,
                        help=f"监听端口（缺省 {DEFAULT_PORT}，须与 mcp.json 一致）")
    parser.add_argument("--run-dir", default=str(DEFAULT_RUN_DIR),
                        help="运行目录（令牌/事件流/摘要；缺省 outputs/agent-match/mcp-run）")
    parser.add_argument("--packs", default=str(DEFAULT_PACKS),
                        help="卡包目录（; 分隔；缺省 samples/card-pack-demo）")
    parser.add_argument("--mode", choices=["play", "sandbox"], default="sandbox")
    parser.add_argument("--seat0", choices=["ai", "agent"], default="ai")
    parser.add_argument("--seat1", choices=["ai", "agent"], default="agent")
    parser.add_argument("--seed", type=int, default=None)
    parser.add_argument("--deck-rules", choices=["strict", "lenient"], default="lenient")
    parser.add_argument("--wait-agent", type=int, default=1800)
    parser.add_argument("--log-seat-views", action="store_true")
    parser.add_argument("--keep-alive", action="store_true")
    parser.add_argument("--allow-debug-writes", action="store_true", default=True)
    parser.add_argument("--build", action="store_true", help="启动前先 dotnet build")
    parser.add_argument("--foreground", action="store_true",
                        help="前台运行（不 detach）：供 agent 用后台任务挂住实例")
    args = parser.parse_args(argv)

    if args.mode != "sandbox" and args.allow_debug_writes:
        args.allow_debug_writes = False

    handlers = {
        "start": cmd_start,
        "stop": cmd_stop,
        "status": cmd_status,
        "restart": cmd_restart,
    }
    return handlers[args.command](args)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
