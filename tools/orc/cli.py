"""编排入口：参数 → 能力探测（D13.2）→ 启动判定链（D3.1）→ Front。

``play`` 的三条互斥出口只由两条可观测事实决定：实例是否还在 ``manager.list`` 且 running、
令牌是否还能回座（D3）。不落盘任何"上次怎么退出的"推断。
"""

import argparse
import json
import os
import sys
import time
import uuid

from . import commands, manager, render, timeouts, transport
from .session import Session


PLAY = "play"
WATCH = "watch"


# ---------------------------------------------------------------- 参数


def build_parser():
    parser = argparse.ArgumentParser(
        prog="orc_play",
        description="OrC 人类 CLI 客户端（play 对局 / watch 观战）",
    )
    sub = parser.add_subparsers(dest="mode")

    def add_common(target):
        target.add_argument("--manager", help="管理器地址（host:port，默认 127.0.0.1:51999）")
        target.add_argument("--pack", help="卡包目录（默认 samples/card-pack-ai）")
        target.add_argument("--deck", help="卡组 JSON（字符串数组；相对路径按当前工作目录解析）")
        target.add_argument("--plain", action="store_true", help="行式输出（无第三方依赖）")
        target.add_argument("--tui", action="store_true", help="强制全屏界面（缺少 prompt_toolkit 时报错）")
        target.add_argument("--session-file", help="会话记录路径（默认 .orc-manager/cli/session.json）")
        target.add_argument("--launch-manager", action="store_true", help="管理器未运行时由本会话拉起（常驻）")
        target.add_argument("--verbose", action="store_true", help="日志行附 (seq=N) 追溯")
        target.add_argument("--stream", action="store_true", help="plain 下实时打印新日志")

    add_play = sub.add_parser(PLAY, help="与 AI 打一局（人类坐 seat 0）")
    add_common(add_play)
    add_play.add_argument("--keep-instance", action="store_true", help="退出时保留实例（下次 play 可回座）")
    add_play.add_argument("--new", action="store_true", help="忽略会话记录，新开实例（不 kill 旧实例）")

    add_watch = sub.add_parser(WATCH, help="全知观战（只读）")
    add_common(add_watch)
    add_watch.add_argument("--instance", help="要观战的实例标识（缺省用会话记录里的实例）")
    return parser


def parse(argv=None):
    parser = build_parser()
    args = parser.parse_args(argv)
    if not args.mode:
        parser.print_help()
        return None
    if getattr(args, "tui", False) and getattr(args, "plain", False):
        parser.error("--tui 与 --plain 不能同时使用")
    return args


# ---------------------------------------------------------------- 能力探测（D13.2）


def select_front(opts, stdin, stdout, say):
    if opts.tui:
        try:
            from . import front_tui  # 强制 TUI：导入失败即报错（不静默降级）
        except ImportError as exc:
            raise manager.CliError(
                "--tui 需要 prompt_toolkit（%s）" % exc,
                "请运行 tools\\orc_play.bat 自动建 venv，或改用 --plain。")
        return front_tui.TuiFront(stdin, stdout)
    if not (getattr(stdin, "isatty", lambda: False)() and getattr(stdout, "isatty", lambda: False)()):
        return _plain_front(stdin, stdout)
    if opts.plain:
        return _plain_front(stdin, stdout)
    try:
        import prompt_toolkit  # noqa: F401
    except ImportError:
        say("未检测到 prompt_toolkit，已使用 --plain 界面；如需全屏界面请运行 tools\\orc_play.bat")
        return _plain_front(stdin, stdout)
    from . import front_tui
    return front_tui.TuiFront(stdin, stdout)


def _plain_front(stdin, stdout):
    from . import front_plain
    return front_plain.PlainFront(stdin, stdout)


# ---------------------------------------------------------------- 运行时（Game）


class Game(object):
    """Front 与协议之间的唯一胶水：一条输入 → 一行到多行输出。"""

    def __init__(self, session, dispatcher, engine, runtime, opts, stdout):
        self.session = session
        self.dispatcher = dispatcher
        self.engine = engine
        self.runtime = runtime
        self.opts = opts
        self.stdout = stdout
        self.exit_code = None
        self._log_cursor = 0
        self._banner = []

    # -- 输出 --
    def write(self, lines):
        if not lines:
            return
        for line in lines:
            self.stdout.write(line + "\n")
        self.stdout.flush()

    def banner_lines(self):
        return list(self._banner)

    def board_lines(self, width=96, log_lines=8):
        snap = self.session.snapshot()
        snap.picks = list(self.engine.picks)
        return render.render(snap, width=width, log_lines=log_lines)

    def new_log_lines(self):
        entries = self.session.logs.since(self._log_cursor)
        if not entries:
            return []
        self._log_cursor = entries[-1][0] + 1
        return [text for _, _, text in entries]

    # -- 一条输入 --
    def handle(self, line):
        before_version = self.session.view_version
        before_actions = self.engine.action_count
        try:
            result = self.engine.execute(line)
        except commands.Disconnect as exc:
            return self._on_disconnect(str(exc))
        if self.engine.action_count != before_actions and result.exit_code is None:
            # 动作已发出：等这一拍的视图刷新再渲染（最多 1s），让块内棋盘与结果同时落地
            self._wait_view_change(before_version, 1.0)
        if result.exit_code is not None:
            self.exit_code = result.exit_code
        return result

    def _wait_view_change(self, version, timeout):
        deadline = time.time() + timeout
        while time.time() < deadline:
            if self.session.view_version != version:
                return True
            time.sleep(0.02)
        return False

    def _on_disconnect(self, message):
        """断线：带 seatToken 重连一次（R11）；失败则退出。"""
        self.session.set_connected(False)
        note = ["连接断开：%s" % message]
        if self.session.mode == WATCH:
            note.append("观战连接已断开（对局不受影响）。")
            self.exit_code = 2
            return commands.RunResult(note, exit_code=2, failed=True)
        token = self.session.seat_token
        if not token:
            note.append("没有可用的座位令牌，无法自动回座；请重新运行 play。")
            self.exit_code = 2
            return commands.RunResult(note, exit_code=2, failed=True)
        try:
            dispatcher = self.runtime.reconnect()
        except Exception as exc:  # noqa: BLE001
            note.append("自动重连失败：%s" % exc)
            note.append("请重新运行 play（会凭令牌回座）。")
            self.exit_code = 2
            return commands.RunResult(note, exit_code=2, failed=True)
        self.dispatcher = dispatcher
        self.engine.dispatcher = dispatcher
        self.session.set_connected(True)
        note.append("已重连并恢复座位。")
        return commands.RunResult(note)

    # -- 退出 --
    def shutdown(self, reason="quit"):
        return self.runtime.shutdown(self, reason)


class Runtime(object):
    """一次 CLI 会话的可变依赖（实例归属、记录、重连）。"""

    def __init__(self, opts, manager_client, record_path, record, pack, deck, packages,
                 pack_dirs, session, stdout, say):
        self.opts = opts
        self.mc = manager_client
        self.record_path = record_path
        self.record = record or {}
        self.pack = pack
        self.deck = deck
        self.packages = packages
        self.pack_dirs = pack_dirs
        self.session = session
        self.stdout = stdout
        self.say = say
        self.instance_id = None
        self.port = None
        self.dispatcher = None
        self.closed = False
        self.manager_action = None
        self.watch = opts.mode == WATCH

    # -- 记录 --
    def write_record(self, **updates):
        if self.watch or not self.instance_id:
            return
        record = dict(self.record)
        record.update({
            "cliSessionId": record.get("cliSessionId") or uuid.uuid4().hex,
            "instanceId": self.instance_id,
            "label": record.get("label") or ("cli:" + self.instance_id[4:]),
            "manager": self.mc.endpoint,
            "port": self.port,
            "seat": 0,
            "packDirectories": list(self.pack_dirs),
            "deck": list(self.deck),
            "packages": list(self.packages),
        })
        record.update(updates)
        self.record = manager.save_record(self.record_path, record)

    def touch_record(self):
        if self.watch:
            return
        manager.touch_record(self.record_path, self.record)

    # -- 重连（R11）--
    def reconnect(self):
        if self.dispatcher is not None:
            try:
                self.dispatcher.close()
            except Exception:  # noqa: BLE001
                pass
        dispatcher = _connect(self.port, self.session, self.opts.verbose)
        dispatcher.request("initialize", _initialize_params(), timeouts.INITIALIZE)
        params = {
            "roles": ["controller", "viewer"],
            "seat": 0,
            "displayName": "人类玩家",
            "deck": self.deck,
            "packages": self.packages,
            "token": self.session.seat_token,
        }
        attached = dispatcher.request("session.attach", params, timeouts.READ_CALL)
        if (attached.get("state") not in ("admitted", "viewer")) or attached.get("code") not in (None, "ok"):
            raise manager.CliError("回座失败：%s / %s" % (
                attached.get("code"), attached.get("message") or ""))
        self.dispatcher = dispatcher
        return dispatcher

    # -- 退出（D3.8 唯一出口）--
    def shutdown(self, game=None, reason="quit"):
        if self.closed:
            return game.exit_code if game and game.exit_code is not None else 0
        self.closed = True
        dispatcher = self.dispatcher
        if dispatcher is not None:
            try:
                dispatcher.close()
            except Exception:  # noqa: BLE001
                pass
        if self.watch:
            self.say("观战结束（实例未受影响）。")
            self.mc.close()
            return 0
        code = game.exit_code if game and game.exit_code is not None else 0
        if not self.instance_id:
            return code
        if self.opts.keep_instance:
            self.write_record()
            self.say("实例保留：%s（端口 %s）——下次 play 会凭令牌回座。" % (
                self.instance_id, self.port))
            return code
        result = self.mc.kill(self.instance_id)
        still = None
        try:
            still = self.mc.find(self.instance_id)
        except manager.CliError:
            still = None
        if still is None:
            manager.delete_record(self.record_path)
            if result.get("ok"):
                self.say("实例 %s 已销毁。" % self.instance_id)
            else:
                self.say("实例 %s 已不在清单中（kill 返回 %s），记录已清理。" % (
                    self.instance_id, result.get("code")))
        else:
            self.write_record()
            self.say("实例 %s 仍在运行（kill 失败：%s）。记录已保留：%s" % (
                self.instance_id, result.get("message") or result.get("code"), self.record_path))
        self.mc.close()
        return code


# ---------------------------------------------------------------- 连接与握手


def _initialize_params():
    return {
        "clientName": "orc-cli",
        "protocolVersion": "1.0",   # 线协议版本：必须是 "1.0"（探测已确认）
        "label": "人类玩家",
    }


def _connect(port, session, verbose=False):
    dispatcher = transport.SocketDispatcher("127.0.0.1", port, label="server", verbose=verbose)
    dispatcher.on_frame = session.apply_frame
    dispatcher.on_pending = lambda pending: session.set_pending(pending)
    dispatcher.on_disconnect = lambda reason: session.set_connected(False)
    dispatcher.connect()
    return dispatcher


def _attach_params(deck, packages, token=None, roles=None):
    params = {
        "roles": roles or ["controller", "viewer"],
        "seat": 0,
        "displayName": "人类玩家",
        "deck": deck,
        "packages": packages,
        "deckConfig": None,   # D5.4：tier=none 下不声明
    }
    if token:
        params["token"] = token
    return params


def _wait_view(session, timeout=None, watch=False):
    timeout = timeout or timeouts.VIEW_WAIT
    deadline = time.time() + timeout
    while time.time() < deadline:
        if watch:
            if len(session.views) >= 1:
                return True
        elif session.view is not None:
            return True
        time.sleep(0.1)
    return False


# ---------------------------------------------------------------- 启动判定链


class _SeatTaken(Exception):
    pass


class _ResumeFailed(Exception):
    pass


def _attach(mc, dispatcher, instance_id, deck, packages, token=None, seat=0):
    params = _attach_params(deck, packages, token=token)
    if seat != 0:
        params["seat"] = seat
    attached = dispatcher.request("session.attach", params, timeouts.READ_CALL)
    code = attached.get("code")
    state = attached.get("state")
    if code in ("invalid-token", "stale-seat"):
        raise _ResumeFailed(code)
    if code == "seat-taken":
        raise _SeatTaken()
    if code not in (None, "ok"):
        raise manager.CliError("座位申请被拒绝：%s / %s" % (
            code, attached.get("message") or ""),
            "可尝试 --new 新开实例，或用 watch --instance <id> 旁观。")
    if state == "admitted":
        return attached
    if state != "pending":
        raise manager.CliError("座位申请未进入 pending（state=%s）" % state)

    connection_id = attached.get("connectionId")
    admitted = mc.admin(instance_id, "conn.admit", {"connectionId": connection_id, "seat": 0})
    if not admitted.get("ok"):
        raise manager.CliError("座位批准失败：%s / %s" % (
            admitted.get("code"), admitted.get("message") or ""))
    payload = admitted.get("payload") or {}
    attached["seatToken"] = payload.get("seatToken")
    return attached


def _start_match(mc, instance_id, pack_dirs):
    attempt = 0
    while True:
        result = mc.admin_wait(instance_id, "match.start", {"packDirectories": pack_dirs})
        if result.get("ok"):
            return result
        if result.get("code") == "room-rotating" and attempt < len(timeouts.ROOM_ROTATING_RETRY_MS):
            time.sleep(timeouts.ROOM_ROTATING_RETRY_MS[attempt] / 1000.0)
            attempt += 1
            continue
        return result


def _spawn(mc, pack_dirs, say):
    instance_id = manager.new_instance_id()
    label = "cli:" + instance_id[4:]
    result = mc.spawn(instance_id, label, pack_dirs)
    if not result.get("ok"):
        raise manager.CliError(
            "实例创建失败：%s / %s" % (result.get("code"), result.get("message") or ""),
            "管理器已在 %s 常驻，可修复后重试（不会 kill 管理器）。" % mc.endpoint,
        )
    payload = result.get("payload") or {}
    port = payload.get("port")
    if not port:
        raise manager.CliError("实例创建成功但未返回端口：%s" % json.dumps(payload, ensure_ascii=False))
    say("实例已创建：%s（端口 %s）" % (instance_id, port))
    return instance_id, port, payload.get("roomId")


def _boot_play(opts, runtime, say):
    """D3.1 的三条互斥出口（回座 / 同实例重开 / 新开）。"""
    session = runtime.session
    record = runtime.record
    instance = None
    if record.get("instanceId"):
        try:
            instance = runtime.mc.find(record["instanceId"])
        except manager.CliError:
            instance = None

    if instance and instance.get("running"):
        port = instance.get("port") or record.get("port")
        dispatcher = _connect(port, session, opts.verbose)
        dispatcher.request("initialize", _initialize_params(), timeouts.INITIALIZE)
        reused = False
        try:
            attached = _attach(runtime.mc, dispatcher, record["instanceId"],
                               record.get("deck") or runtime.deck,
                               record.get("packages") or runtime.packages,
                               token=record.get("seatToken"))
            room_id = instance.get("roomId")
            if record.get("roomId") and room_id and room_id != record.get("roomId"):
                raise _ResumeFailed("room-changed")
            token = attached.get("seatToken") or record.get("seatToken")
            session.set_admitted(True, token)
            runtime.dispatcher = dispatcher
            runtime.instance_id = record["instanceId"]
            runtime.port = port
            runtime.write_record(roomId=room_id, seatToken=token)
            say("已凭座位令牌回到实例 %s（端口 %s）。" % (record["instanceId"], port))
            return dispatcher, "resume"
        except _ResumeFailed as exc:
            reused = True
            say("座位令牌已失效（%s），尝试在同一实例内重开…" % exc)
        except _SeatTaken:
            reused = True
            say("该实例的座位已被占用，改为新开实例。")
        except manager.CliError:
            dispatcher.close()
            raise

        if reused:
            # (c) 同实例重开（D3.5②）：不 kill、不 spawn
            try:
                attached = _attach(runtime.mc, dispatcher, record["instanceId"],
                                   runtime.deck, runtime.packages, token=None)
                started = _start_match(runtime.mc, record["instanceId"], runtime.pack_dirs)
                if not started.get("ok"):
                    raise manager.CliError(
                        "同实例重开失败：%s / %s" % (started.get("code"), started.get("message") or ""),
                        "对局可能仍属于别的会话（D3.5③）；可用 --new 新开，或 watch --instance %s 旁观。"
                        % record["instanceId"])
                room_id = (started.get("payload") or {}).get("roomId") or instance.get("roomId")
                token = attached.get("seatToken")
                session.set_admitted(True, token)
                runtime.dispatcher = dispatcher
                runtime.instance_id = record["instanceId"]
                runtime.port = port
                runtime.write_record(roomId=room_id, seatToken=token)
                say("已在实例 %s 内重开（房间 %s）。" % (record["instanceId"], room_id))
                return dispatcher, "restart"
            except _SeatTaken:
                say("该实例的座位已被占用，改为新开实例。")
                dispatcher.close()
            except manager.CliError:
                dispatcher.close()
                raise

    # 3) 新开
    instance_id, port, _room = _spawn(runtime.mc, runtime.pack_dirs, say)
    runtime.instance_id = instance_id
    runtime.port = port
    runtime.write_record(roomId=_room, seatToken=None)
    dispatcher = _connect(port, session, opts.verbose)
    try:
        dispatcher.request("initialize", _initialize_params(), timeouts.INITIALIZE)
        attached = _attach(runtime.mc, dispatcher, instance_id, runtime.deck, runtime.packages)
        started = _start_match(runtime.mc, instance_id, runtime.pack_dirs)
        if not started.get("ok"):
            raise manager.CliError(
                "开局失败：%s / %s" % (started.get("code"), started.get("message") or ""),
                "实例 %s（端口 %s）仍在运行：可用 watch --instance %s 查看，或 quit 时销毁。"
                % (instance_id, port, instance_id))
        room_id = (started.get("payload") or {}).get("roomId")
        token = attached.get("seatToken")
        session.set_admitted(True, token)
        runtime.dispatcher = dispatcher
        runtime.write_record(roomId=room_id, seatToken=token)
        say("对局已开始：房间 %s（实例 %s，端口 %s）。" % (room_id, instance_id, port))
        return dispatcher, "new"
    except manager.CliError:
        try:
            dispatcher.close()
        except Exception:  # noqa: BLE001
            pass
        raise


def _boot_watch(opts, runtime, say):
    record = runtime.record
    target = opts.instance or record.get("instanceId")
    if not target:
        raise manager.CliError(
            "没有可观战的实例：请用 --instance <id> 指定，或先跑一局 play。",
            "查看实例清单：manager.list（或直接运行 play）。")
    instance = runtime.mc.find(target)
    if instance is None:
        raise manager.CliError("实例 '%s' 不在管理器清单中（%s）" % (target, runtime.mc.endpoint))
    port = instance.get("port")
    runtime.instance_id = target
    runtime.port = port
    dispatcher = _connect(port, runtime.session, opts.verbose)
    dispatcher.request("initialize", _initialize_params(), timeouts.INITIALIZE)
    attached = dispatcher.request("session.attach", {"roles": ["viewer"]}, timeouts.READ_CALL)
    if attached.get("code") not in (None, "ok") or attached.get("state") not in ("pending", "omniscient"):
        raise manager.CliError("观战申请被拒绝：%s / %s" % (
            attached.get("code"), attached.get("message") or ""))
    connection_id = attached.get("connectionId")
    admitted = runtime.mc.admin(target, "conn.admit", {"connectionId": connection_id})
    if not admitted.get("ok"):
        raise manager.CliError("观战批准失败：%s / %s" % (
            admitted.get("code"), admitted.get("message") or ""))
    runtime.dispatcher = dispatcher
    say("已接入实例 %s（端口 %s），全知观战。" % (target, port))
    return dispatcher


# ---------------------------------------------------------------- 主流程


def main(argv=None, stdin=None, stdout=None, stderr=None, dispatcher_factory=None):
    stdin = stdin or sys.stdin
    stdout = stdout or sys.stdout
    stderr = stderr or sys.stderr

    def say(text):
        stdout.write(text + "\n")
        stdout.flush()

    opts = parse(argv)
    if opts is None:
        return 0
    repo = manager.find_repo_root()

    record_path = os.path.abspath(opts.session_file) if opts.session_file \
        else manager.default_session_file(repo)
    record = None if getattr(opts, "new", False) else manager.load_record(record_path)
    record = record or {}
    endpoint = opts.manager or record.get("manager") or manager.DEFAULT_ENDPOINT

    mc = manager.ManagerClient(endpoint, verbose=opts.verbose)
    try:
        manager.ensure_manager(mc, opts.launch_manager, repo, say=say)
    except manager.CliError as exc:
        say("✗ %s" % exc.message)
        if exc.hint:
            say(exc.hint)
        return 2

    # 卡包与卡组（D5）：默认包按**仓库根**解析（不依赖 cwd），显式 --pack 才按 cwd 解析。
    pack_dir = opts.pack or os.path.join(repo, manager.DEFAULT_PACK)
    try:
        pack = manager.load_pack(pack_dir)
    except manager.CliError as exc:
        say("✗ %s" % exc.message)
        return 2
    except (IOError, OSError, ValueError) as exc:
        say("✗ 读取卡包失败：%s" % exc)
        return 2
    if not pack.asset_ids and opts.mode == PLAY:
        say("✗ 该卡包没有可用的卡：%s（不创建实例）" % pack.directory)
        return 2
    pack_dirs = [pack.directory]
    packages = [pack.package_ref()]
    if opts.deck:
        try:
            deck = manager.load_deck_file(os.path.abspath(opts.deck))
        except (IOError, OSError, ValueError) as exc:
            say("✗ 读取 --deck 失败：%s" % exc)
            return 2
        say("自定义卡组 %d 张，服务端将按 deckRules 校验。" % len(deck))
    else:
        deck = pack.deck()
        if len(pack.asset_ids) > manager.DECK_SIZE:
            say("卡包卡种数超过 39，仅取前 39。")

    session = Session(mode=opts.mode, seat=0, verbose=opts.verbose)
    runtime = Runtime(opts, mc, record_path, record, pack, deck, packages, pack_dirs,
                      session, stdout, say)
    runtime.record = record

    try:
        if opts.mode == PLAY:
            dispatcher, _path = _boot_play(opts, runtime, say)
        else:
            dispatcher = _boot_watch(opts, runtime, say)
    except manager.CliError as exc:
        say("✗ %s" % exc.message)
        if exc.hint:
            say(exc.hint)
        if runtime.instance_id and opts.mode == PLAY and not opts.keep_instance:
            say("（本次已创建的实例 %s 会在退出时清理）" % runtime.instance_id)
        runtime.shutdown(None, "boot-failed")
        return 2
    except transport.DispatcherError as exc:
        say("✗ 连接失败：%s" % exc)
        runtime.shutdown(None, "boot-failed")
        return 2

    session.set_connected(True)
    got_view = _wait_view(session, watch=(opts.mode == WATCH))
    if not got_view:
        # 推送可能永远不来（对局已终局/房间重建中）：主动读一次视图对齐（读类调用）
        try:
            aligned = dispatcher.call_tool("observe.view", {}, kind=transport.READ)
            if aligned.ok and isinstance(aligned.details, dict):
                session.apply_view(aligned.details)
                got_view = session.view is not None or bool(session.views)
            if not got_view:
                say("当前没有可渲染的对局（%s / %s）。" % (aligned.code, aligned.message))
        except transport.DispatcherError as exc:
            say("读取视图失败：%s" % exc)
    if not got_view:
        say("（当前没有进行中的对局：可能已终局或房间在重建；可用 help 查看指令，quit 退出）")

    engine = commands.CommandEngine(session, dispatcher, context=_engine_context(opts, runtime, session))
    game = Game(session, dispatcher, engine, runtime, opts, stdout)
    game._banner = _banner(opts, runtime)

    front = None
    try:
        front = select_front(opts, stdin, stdout, say)
    except manager.CliError as exc:
        say("✗ %s" % exc.message)
        if exc.hint:
            say(exc.hint)
    if front is None:
        runtime.shutdown(None, "front-unavailable")
        return 2
    front.attach(game)
    try:
        code = front.run()
    except KeyboardInterrupt:
        code = 0
    finally:
        code = runtime.shutdown(game, "exit")
    return code


def _engine_context(opts, runtime, session):
    def restart():
        return _restart(opts, runtime, session)

    return {
        "keep_instance": bool(getattr(opts, "keep_instance", False)),
        "restart": restart,
        "mode": opts.mode,
    }


def _restart(opts, runtime, session):
    """D12.3：无 token attach → conn.admit → match.start；永不 spawn。"""
    dispatcher = runtime.dispatcher
    say = runtime.say
    say("正在重开…")
    try:
        attached = dispatcher.request("session.attach",
                                      _attach_params(runtime.deck, runtime.packages), timeouts.READ_CALL)
        if attached.get("state") == "pending":
            admitted = runtime.mc.admin(runtime.instance_id, "conn.admit",
                                        {"connectionId": attached.get("connectionId"), "seat": 0})
            if not admitted.get("ok"):
                raise manager.CliError("重开时批准失败：%s" % (admitted.get("message") or admitted.get("code")))
            attached["seatToken"] = (admitted.get("payload") or {}).get("seatToken")
        elif attached.get("code") not in (None, "ok"):
            raise manager.CliError("重开时申请被拒绝：%s / %s" % (
                attached.get("code"), attached.get("message") or ""))
        started = _start_match(runtime.mc, runtime.instance_id, runtime.pack_dirs)
        if not started.get("ok"):
            code = started.get("code")
            message = started.get("message") or ""
            if code == "deck-invalid":
                regenerated = runtime.pack.deck()
                runtime.deck = regenerated
                runtime.write_record(deck=regenerated)
                retry = _start_match(runtime.mc, runtime.instance_id, runtime.pack_dirs)
                if retry.get("ok"):
                    say("卡组已按新卡池重新生成。")
                    started = retry
                else:
                    say("卡组已重新生成但仍失败：%s；请换 --pack。" % (retry.get("message") or ""))
                    return commands.fail([str(retry.get("message") or "")])
            elif code == "missing-assets":
                say("卡池缺资产（不是卡组问题）：%s" % message)
                say("请换 --pack 后重开。")
                return commands.fail([message])
            else:
                say("重开失败：%s / %s" % (code, message))
                return commands.fail([message])
        room_id = (started.get("payload") or {}).get("roomId")
        token = attached.get("seatToken")
        session.apply_rotated_reset(room_id)
        session.set_admitted(True, token)
        runtime.write_record(roomId=room_id, seatToken=token)
        return commands.ok(["新一局已开始：房间 %s。" % room_id])
    except manager.CliError as exc:
        return commands.fail([exc.message])
    except transport.DispatcherError as exc:
        return commands.fail(["重开失败：%s" % exc])


def _banner(opts, runtime):
    lines = []
    if opts.mode == WATCH:
        lines.append("OrC 观战 — 实例 %s（端口 %s）· 只读" % (runtime.instance_id, runtime.port))
        lines.append("可查看 hand / units / log，输入 quit 退出。")
    else:
        lines.append("OrC 对局 — 实例 %s（端口 %s）· 你坐 seat 0（先手）" % (
            runtime.instance_id, runtime.port))
        lines.append("输入 help 查看全部指令；keep 开始对局。")
    return lines


if __name__ == "__main__":
    raise SystemExit(main())
