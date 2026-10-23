"""指令集（D14 唯一真源）、门禁（D2.5/D6.2/D12.7/D15.5）、引导与候选（D16）。

一个动作＝「前置调用 → 取待决（优先响应体里的 details.pending，D2.2）→ 按候选序号 target.submit」。
是否进入选择**一律以响应 ``code`` 为准**（D7.5），不预判。
"""

import re

from . import render, timeouts
from .transport import (ACTION, READ, CallTimeout, ConnectionLost, DispatcherError, RpcError,
                        ToolResult)


GROUP_ACTION = "对局动作"
GROUP_SELECT = "选择"
GROUP_READ = "只读"
GROUP_SYSTEM = "系统"

# (英文, 中文别名, 语法, 说明, 组) —— 共 16 条；不允许出现 order/counter/join/begin/command
COMMANDS = [
    ("play", "出牌", "play <手牌#> [候选#]",
     "打出第 N 张手牌：单位会让你选插入位置，指令可能直接结算也可能选目标，反制直接生效；不可打出的卡会先被拒绝并说明原因",
     GROUP_ACTION),
    ("attack", "攻击", "attack <单位#> [候选#]", "用我方面板第 N 号单位攻击（留空进入选择）", GROUP_ACTION),
    ("move", "移动", "move <单位#> [候选#]", "移动我方面板第 N 号单位（留空进入选择）", GROUP_ACTION),
    ("leave", "离开", "leave <单位#>", "让第 N 号单位离开战场", GROUP_ACTION),
    ("end", "结束回合", "end", "结束当前回合（对局进行中可用）", GROUP_ACTION),
    ("concede", "认输", "concede", "认输（需再输入一次确认）", GROUP_ACTION),
    ("keep", "保留", "keep", "换牌阶段：不换牌直接确认开始", GROUP_ACTION),
    ("mulligan", "换牌", "mulligan", "换牌阶段：选择要换掉的手牌", GROUP_ACTION),
    ("ok", "确定", "ok", "选择模式：提交已选序号", GROUP_SELECT),
    ("x", "取消", "x", "选择模式：取消本次选择", GROUP_SELECT),
    ("hand", "手牌", "hand [0|1]", "查看手牌（观战可指定座位）", GROUP_READ),
    ("units", "单位", "units [0|1]", "查看我方面板单位与可行动标记", GROUP_READ),
    ("log", "日志", "log [N] [--prev|--all]", "回看战斗日志（默认本局最近 20 条）", GROUP_READ),
    ("help", "帮助", "help [指令]", "列出全部指令 / 查询单条（? 亦可）", GROUP_SYSTEM),
    ("restart", "重开", "restart", "终局菜单：房间重建后开新一局", GROUP_SYSTEM),
    ("quit", "退出", "quit", "退出客户端（对局中需再确认一次）", GROUP_SYSTEM),
]

EXTRA_ALIASES = {
    "keep": ["不换"],
}

ALIAS_MAP = {}
for _english, _chinese, _syntax, _description, _group in COMMANDS:
    ALIAS_MAP[_english] = _english
    ALIAS_MAP[_chinese] = _english
    for _alias in EXTRA_ALIASES.get(_english, ()):
        ALIAS_MAP[_alias] = _english
ALIAS_MAP["?"] = "help"
ALIAS_MAP["？"] = "help"

SYNTAX = {english: syntax for english, _, syntax, _, _ in COMMANDS}
DESCRIPTION = {english: description for english, _, _, description, _ in COMMANDS}
CHINESE = {english: chinese for english, chinese, _, _, _ in COMMANDS}
GROUP = {english: group for english, _, _, _, group in COMMANDS}
ORDER = [english for english, _, _, _, _ in COMMANDS]

# 引擎拒绝码 → 人话（D11.4 的延伸：玩家看不懂枚举名）。只映射已确认的取值，
# 其余原样保留并提示查日志——不猜、不编造原因。
_ENGINE_REJECT_TEXT = {
    # 指挥/出牌失败原因（CommandFailureReason / PlayFailureReason，见 CommandTypes.cs）
    "NonOwnerTurn": "现在不是你的回合（等对手行动结束再指挥）",
    "OwnerInvalid": "这个单位没有归属，无法被指挥",
    "UnitDead": "该单位已阵亡",
    "NoActionAvailable": "该单位现在没有可用动作（可能本回合已行动过、指挥点不足，或没有合法目标）",
    "TargetingFailed": "选择目标失败（可能已没有可用候选）",
    "ExecutionRejected": "动作执行前被拒（时机或目标已变化）",
    "CommandFlowFault": "指挥流程内部错误（可在 log 里查看）",
    "InteractionBlocked": "该单位当前被禁止行动",
    # 通用
    "GameEnded": "对局已结束",
    "PhaseBlocked": "当前相位不允许这个动作",
    "TargetSlotOccupied": "目标位置已被占用",
    "PositionStale": "位置已变化，请重选",
    "PlayVerificationRejected": "打出校验未通过（目标或时机不合法）",
    "PlayChainFault": "打出链内部出错（可在 log 里查看）",
}


WATCH_ALLOWED = {"hand", "units", "log", "help", "quit"}
ENDED_ALLOWED = {"restart", "quit", "log", "help"}
MULLIGAN_ALLOWED = {"keep", "mulligan", "log", "help", "quit", "concede", "hand", "units"}
PENDING_ALLOWED = {"ok", "x", "log", "help"}
NUMBER_RE = re.compile(r"^\d+(\s*[,，、/]\s*\d+)*$")


class Abort(Exception):
    """在处理器内部中止本次指令（携带要显示的行）。"""

    def __init__(self, result):
        Exception.__init__(self, "abort")
        self.result = result


class Disconnect(Exception):
    """连接断开（由 Front 统一走重连）。"""

    def __init__(self, message):
        Exception.__init__(self, message)
        self.message = message


class RunResult(object):
    def __init__(self, lines=None, exit_code=None, refresh=True, failed=False):
        self.lines = list(lines or [])
        self.exit_code = exit_code
        self.refresh = refresh
        self.failed = failed

    def __repr__(self):
        return "<RunResult lines=%d exit=%s>" % (len(self.lines), self.exit_code)


def ok(lines=None, refresh=True):
    return RunResult(lines or [], refresh=refresh)


def fail(lines):
    return RunResult(lines, failed=True)


class CommandEngine(object):
    def __init__(self, session, dispatcher, context=None):
        self.session = session
        self.dispatcher = dispatcher
        self.context = context or {}
        self.picks = []
        self.action_count = 0
        self.confirm = None
        self.step = None          # 引导：等待参数的命令
        self.last_failed = False
        self.last_phase = None

    # ------------------------------------------------------------ 入口

    def execute(self, raw):
        line = (raw or "").strip()
        if not line:
            return ok([])
        result = self._execute(line)
        self.last_failed = bool(result.failed)
        return result

    def _execute(self, line):
        parts = line.split()
        first = parts[0]

        # 二次确认（D14.4/D14.8）
        if self.confirm:
            return self._resolve_confirm(first)

        # 引导中（D16.6）：下一行当作该命令的参数
        if self.step is not None:
            return self._step_input(line, parts)

        verb = self.canonical(first)

        # 「结果未知」的对齐出口（D11.5 ④；不是常规动词，不列进 help）
        if first.lower() == "retry" and self.session.unknown:
            return self._cmd_retry(parts[1:])

        # 观战白名单（D15.5）
        if self.session.mode == "watch" and (verb not in WATCH_ALLOWED):
            return fail(["观战模式只读：可查看 hand / units / log，输入 quit 退出"])

        # 终局菜单（D12.7）
        if self._ended() and verb not in ENDED_ALLOWED:
            if verb == "end":
                return fail(["对局已结束；restart 重开 / quit 退出"])
            return fail(["对局已结束：restart 重开 / quit 退出"])

        # 选择模式（D2.5）
        if self.session.pending:
            return self._selection_line(verb, parts)

        # 换牌相位门禁（D6.2/D14.5）
        if self._in_mulligan() and verb not in MULLIGAN_ALLOWED:
            if verb == "end":
                return fail(["换牌阶段不能结束回合；请输入 keep 开始"])
            return fail(["换牌阶段尚不能出牌，请输入 keep 直接开始或 mulligan 选择换牌"])

        if verb is None:
            if first.isdigit():
                return fail(["当前没有待选择的请求：%s 是序号，只在选择模式内有效（出牌请用 play <手牌#>）"
                            % first])
            return fail(["未知指令：%s（输入 help 查看全部）" % first])

        handler = getattr(self, "_cmd_" + verb.replace(".", "_"), None)
        if handler is None:
            return fail(["未知指令：%s（输入 help 查看全部）" % first])
        try:
            return handler(parts[1:])
        except Abort as stop:
            return stop.result
        except Disconnect as stop:
            raise

    def canonical(self, word):
        if not word:
            return None
        return ALIAS_MAP.get(word.lower(), ALIAS_MAP.get(word))

    # ------------------------------------------------------------ 状态判定

    def _in_mulligan(self):
        if self.session.mode == "watch":
            return False
        snap = self.session.snapshot()
        if snap.phase == "mulligan":
            return True
        actions = snap.actions or {}
        return bool(actions.get("mulliganOpen"))

    def _ended(self):
        return bool(self.session.ended)

    def _hand(self, seat=None):
        return self.session.hand(seat)

    def _units(self, seat=None):
        return self.session.units(seat)

    # ------------------------------------------------------------ 二次确认

    def _ask_confirm(self, verb, text):
        self.confirm = verb
        return ok([text])

    def _resolve_confirm(self, word):
        expected = self.confirm
        self.confirm = None
        verb = self.canonical(word)
        if verb != expected:
            return ok(["已取消"])
        if expected == "quit":
            return RunResult(["再见。"], exit_code=0)
        if expected == "concede":
            result = self._tool("match.concede", {"owner": 0})
            if result.ok:
                self.session.set_notice("")
                return ok(["已认输。"])
            return self._explain(result)
        return ok(["已取消"])

    # ------------------------------------------------------------ 选择模式输入

    def _selection_line(self, verb, parts):
        token = parts[0]
        if NUMBER_RE.match(token):
            picks = self._parse_numbers(parts)
            return self._add_picks(picks)
        if verb == "ok":
            given = [int(item) for item in parts[1:] if item.isdigit()]
            if given:
                self.picks = given
            return self._submit_picks()
        if verb == "x":
            return self._cancel_pending()
        if verb in ("log", "help"):
            return getattr(self, "_cmd_" + verb)(parts[1:])
        return fail(["请先完成选择（输入序号 / ok 提交 / x 取消）"])

    def _parse_numbers(self, parts):
        numbers = []
        for token in parts:
            for piece in re.split(r"[,，、/\s]+", token):
                if not piece:
                    continue
                if piece.isdigit():
                    numbers.append(int(piece))
        return numbers

    def _add_picks(self, numbers):
        pending = self.session.pending or {}
        candidates = pending.get("candidates") or []
        maximum = pending.get("max")
        total = len(candidates) + len(pending.get("identifiers") or [])
        lines = []
        for number in numbers:
            if number < 1 or (total and number > total):
                lines.append("序号 %s 超出范围：可用序号 1..%s" % (number, total))
                continue
            if number not in self.picks:
                self.picks.append(number)
        if maximum and len(self.picks) > int(maximum):
            lines.append("最多选 %s 个（当前已选 %s 个）" % (maximum, len(self.picks)))
            self.picks = self.picks[:int(maximum)]
        lines.append("已选：%s（%s）· ok 提交 · x 取消" % (
            ",".join(str(item) for item in self.picks) or "无",
            render.need_text(pending.get("min", 0), pending.get("max", 0))))
        lines.extend(render.candidate_rows(pending, self.session.snapshot()))
        return ok(lines)

    def _submit_picks(self):
        pending = self.session.pending or {}
        minimum = int(pending.get("min") or 0)
        maximum = int(pending.get("max") or 0)
        if len(self.picks) < minimum:
            return fail(["还需再选 %d 个（当前 %d 个，要求 %s..%s）" % (
                minimum - len(self.picks), len(self.picks), minimum, maximum)])
        if maximum and len(self.picks) > maximum:
            return fail(["最多选 %d 个（当前 %d 个）" % (maximum, len(self.picks))])
        session_id = pending.get("sessionId")
        picks = list(self.picks)
        self.picks = []
        result = self._tool("target.submit", {"sessionId": session_id, "picks": picks})
        outcome = self._after_tool(result, "已提交选择。")
        if not (result.pending or result.code == "targeting"):
            self._clear_pending(session_id)
        return outcome

    def _cancel_pending(self):
        pending = self.session.pending or {}
        session_id = pending.get("sessionId")
        selector = pending.get("selector")
        result = self._tool("target.cancel", {"sessionId": session_id})
        self.picks = []
        self._clear_pending(session_id)
        if not result.ok and result.code not in ("cancelled", "already-ended"):
            return self._explain(result)
        if selector == "mulligan":
            self.session.set_notice("[换牌阶段] 已放弃本次换牌，仍需 keep 才开始")
            return ok(["[换牌阶段] 已放弃本次换牌，仍需 keep 才开始"])
        self.session.set_notice("已取消本次选择")
        return ok(["已取消本次选择。"])

    def _clear_pending(self, session_id=None):
        self.session.clear_pending()
        try:
            self.dispatcher.clear_pending(session_id)
        except Exception:  # noqa: BLE001 - Fake/真实实现都不应因清理失败反噬
            pass

    # ------------------------------------------------------------ 引导

    def _step_input(self, line, parts):
        step = self.step
        first = parts[0]
        if self.canonical(first) in ("x", "quit"):
            self.step = None
            return ok(["已取消引导。"])
        # 引导里改敲了完整指令（如 `attack 1`）：退出引导并按新指令执行。
        # 否则整行会被当成"非法序号"吞掉，玩家会以为命令没生效。
        if self.canonical(first) is not None:
            self.step = None
            return self.execute(line)
        if not parts or not all(piece.isdigit() or piece in (",", "，") for piece in parts):
            self.step = None
            return fail(["已退出引导（输入的不是序号）。"])
        self.step = None
        return self.execute("%s %s" % (step, " ".join(parts)))

    def _guide(self, verb, lines):
        self.step = verb
        lines = list(lines)
        lines.append("")
        lines.append("用法：%s　（直接输入序号，或输入 x 取消）" % SYNTAX.get(verb, verb))
        return ok(lines, refresh=False)

    def _guide_hand(self, verb):
        snap = self.session.snapshot()
        rows = render.hand_rows(snap)
        lines = ["手牌（%d）：编号＝手牌位置，不可打出的排在后面" % len(self._hand())]
        lines.extend("  " + row for row in rows)
        return self._guide(verb, lines)

    def _guide_units(self, verb):
        lines = list(render.unit_rows(self.session.snapshot()))
        return self._guide(verb, lines)

    # ------------------------------------------------------------ 工具调用

    def _tool(self, name, arguments=None, kind=ACTION):
        if kind == ACTION:
            self.action_count += 1
        try:
            return self.dispatcher.call_tool(name, arguments or {}, kind=kind)
        except CallTimeout as exc:
            # 已发出、未收到响应：标记「结果未知」，绝不重发（D11.2/D11.5/D11.6）
            self.session.mark_unknown(True)
            self.session.set_notice("⟳ 结果未知：等待服务端刷新")
            raise Abort(RunResult([
                "⟳ %s——动作已发出但未收到响应，不会重发。" % str(exc),
                "   等待服务端刷新（输入 retry 可主动对齐，quit 退出）。",
            ], failed=True))
        except ConnectionLost as exc:
            self.session.set_connected(False)
            raise Disconnect(str(exc))
        except RpcError as exc:
            raise Abort(fail(["服务端协议错误：%s" % str(exc)]))
        except DispatcherError as exc:
            raise Abort(fail(["通信失败：%s" % str(exc)]))

    def _absorb(self, result):
        """响应里的待决立即入库（F14：响应与推送会竞争，优先用响应体）。"""
        if result is not None and result.pending:
            self.session.set_pending(result.pending)
            self.picks = []
        return result

    def _after_tool(self, result, success_text=""):
        """统一处理一次动作调用的结局（D7.5：以 code 为准）。"""
        self._absorb(result)
        if result.ok and result.code in ("ok", "cancelled"):
            if result.pending:
                return self._enter_selection(result.pending)
            message = result.message if result.code != "ok" else success_text
            self.session.set_notice("")
            return ok([message] if message else [])
        if result.pending or result.code == "targeting":
            return self._enter_selection(result.pending)
        return self._explain(result)

    def _enter_selection(self, pending=None):
        pending = pending or self.session.pending
        if not pending:
            return fail(["服务端未给出选择请求（已对齐视图，请重试）"])
        snap = self.session.snapshot()
        pending = dict(pending)
        if pending.get("action") == "attack" and self.context.get("last_unit_name"):
            pending["sourceName"] = self.context.get("last_unit_name")
        self.session.set_pending(pending)
        self.picks = []
        lines = []
        caption = render.selector_caption(pending, snap)
        lines.append("%s（%s，共 %d 个候选）" % (
            caption,
            render.need_text(pending.get("min", 0), pending.get("max", 0)),
            len(pending.get("candidates") or [])))
        lines.extend(render.candidate_rows(pending, snap))
        lines.append("输入序号（可多选，逗号/空格分隔）· ok 提交 · x 取消")
        self.session.set_notice("请选择：输入序号 · ok 提交 · x 取消")
        return ok(lines)

    def _explain(self, result):
        code = result.code
        message = result.message or ""
        if code == "already-ended":
            self.session.set_notice("对局已结束")
            return ok(["对局已结束（already-ended，按成功处理）。"])
        if code == "pending-targeting":
            # 唯二不能当错误处理的码（D11.4）
            if result.pending:
                return self._enter_selection(result.pending)
            return ok(["请先完成当前选择（pending-targeting）。"])
        if code == "not-admitted":
            return fail(["当前连接未获座位批准：请重新申请座位（见 D3 判定链）。"])
        if code == "seat-taken":
            return fail(["座位已被占用：该实例的对局可能已由别的会话开始（可 --new 新开，或用 watch 旁观）。"])
        if code == "stale-room":
            return fail(["房间已轮换：界面将刷新到新房间。"])
        if code in ("stale-seat", "invalid-token"):
            return fail(["座位令牌已失效：请重新申请座位（play 会自动重试申请）。"])
        if code == "deck-invalid":
            return fail(["卡组非法：%s" % message, "　可用 --deck 指定卡组，或换 --pack。"])
        if code == "missing-assets":
            return fail(["卡池缺资产：%s" % message, "　请换 --pack（不会自动重生成卡组）。"])
        if code == "lock-conflict":
            return fail(["资产版本冲突：%s" % message])
        if code == "instance-boot-failed":
            return fail(["实例启动失败：%s" % message])
        if code == "unauthorized":
            return fail(["无权限：%s（该连接的角色不允许此操作）" % message])
        if code == "not-ready":
            return fail(["对局尚未就绪：%s" % message])
        if code == "busy":
            return fail(["服务繁忙（已重试一次）：%s" % message])
        if code == "engine-rejected":
            text = message[len("引擎拒绝："):] if message.startswith("引擎拒绝：") else message
            friendly = _ENGINE_REJECT_TEXT.get((text or "").strip())
            if friendly:
                return fail([friendly])
            return fail(["引擎拒绝：%s" % text, "　（可在 log 里查看引擎事件）"])
        if code == "cancelled":
            return ok(["动作被取消（引擎三态 Cancelled）——按成功处理。"])
        return fail(["%s：%s" % (code or "unknown", message or "（无说明）")])

    # ------------------------------------------------------------ 出牌 / 指挥

    def _int(self, token):
        try:
            return int(token)
        except (TypeError, ValueError):
            return None

    def _precheck(self, card):
        """D7.3：只在可信时用可打出性做本地预检。"""
        actions = self.session.actions or {}
        if not actions.get("handPlayabilityKnown"):
            return None
        playable = set(actions.get("playableUids") or [])
        if card.get("uid") in playable:
            return None
        reasons = card.get("blockedReasons") or []
        if reasons:
            return "当前不可打出（%s）" % "、".join(str(reason) for reason in reasons)
        return "当前不可打出（费用或相位限制）"

    def _cmd_play(self, args):
        if not args:
            return self._guide_hand("play")
        hand = self._hand()
        number = self._int(args[0])
        if number is None or number < 1 or number > len(hand):
            return fail(["手牌序号超出范围：可用序号 1..%d" % len(hand)])
        card = hand[number - 1]
        preselect = None
        if len(args) >= 2:
            preselect = self._int(args[1])
            if preselect is None or preselect < 1:
                return fail(["候选序号必须是从 1 开始的整数（收到 '%s'）" % args[1]])
        reason = self._precheck(card)
        if reason:
            return fail(["第 %d 张「%s」%s" % (number, render.card_name(card), reason)])

        card_type = (card.get("definition") or {}).get("cardType")
        name = render.card_name(card)
        if card_type == "counter":
            result = self._tool("play.counter", {"ref": card.get("uid")})
            if preselect is not None:
                lines = ["警告：反制卡没有需要选择的步骤，候选参数已忽略。"]
                outcome = self._after_tool(result, "反制已使用。")
                return RunResult(lines + outcome.lines, failed=outcome.failed)
            return self._after_tool(result, "反制已使用。")
        if card_type == "unit":
            result = self._tool("play.unit_pre", {"ref": card.get("uid")})
            self.context["last_unit_name"] = name
            return self._pre_with_preselect(result, preselect, "已打出「%s」。" % name)
        if card_type == "order":
            result = self._tool("play.command_pre", {"ref": card.get("uid")})
            return self._pre_with_preselect(result, preselect, "已打出指令「%s」。" % name)
        return fail(["未知卡类型 '%s'：无法决定调用路径（不会发送请求）" % card_type])

    def _pre_with_preselect(self, result, preselect, success_text):
        """前置调用后的统一收束：内联候选＝「响应带 targeting ⇒ 立即 submit」（D2.2）。"""
        outcome = self._after_tool(result, success_text)
        if outcome.failed:
            return outcome
        if preselect is None:
            return outcome
        pending = self.session.pending
        if not pending:
            return RunResult(list(outcome.lines) + ["警告：该动作没有需要选择的步骤，候选参数已忽略。"],
                             failed=outcome.failed)
        total = len(pending.get("candidates") or [])
        if preselect > total:
            lines = list(outcome.lines) + [
                "候选 %d 超出范围：可用序号 1..%d；本次动作已取消。" % (preselect, total)]
            self._cancel_pending()
            return RunResult(lines, failed=True)
        self.picks = [preselect]
        submitted = self._submit_picks()
        return RunResult(list(outcome.lines) + submitted.lines, failed=submitted.failed)

    def _resolve_panel_unit(self, token):
        units = self._units()
        number = self._int(token)
        if number is None or number < 1 or number > len(units):
            raise Abort(fail(["战场已刷新：我方单位共 %d 个（可用 1..%d），请重新选择" % (
                len(units), len(units))]))
        return units[number - 1]

    def _command(self, verb, args):
        if not args:
            return self._guide_units(verb)
        unit = self._resolve_panel_unit(args[0])
        preselect = None
        if len(args) >= 2:
            preselect = self._int(args[1])
            if preselect is None or preselect < 1:
                return fail(["候选序号必须是从 1 开始的整数（收到 '%s'）" % args[1]])
        name = render.card_name(unit)
        self.context["last_unit_name"] = name
        tool = {"attack": "command.attack", "move": "command.move", "leave": "command.leave"}[verb]
        label = {"attack": "攻击", "move": "移动", "leave": "离开"}[verb]
        echo = "%s：%s 号 %s" % (label, args[0], name)
        ignore_extra = verb == "leave"
        result = self._tool(tool, {"ref": unit.get("uid")})
        outcome = self._after_tool(result, "%s 已执行。" % echo)
        if outcome.failed:
            return RunResult([echo] + outcome.lines, failed=True)
        lines = [echo]
        if preselect is not None and ignore_extra:
            lines.append("警告：离开没有候选参数，已忽略。")
        elif preselect is not None:
            pending = self.session.pending
            if not pending:
                lines.append("警告：该动作没有需要选择的步骤，候选参数已忽略。")
            elif preselect > len(pending.get("candidates") or []):
                lines.append("候选 %d 超出范围：可用序号 1..%d；本次动作已取消。" % (
                    preselect, len(pending.get("candidates") or [])))
                self._cancel_pending()
                return RunResult(lines, failed=True)
            else:
                self.picks = [preselect]
                outcome = self._submit_picks()
                return RunResult(lines + outcome.lines, failed=outcome.failed)
        return RunResult(lines + outcome.lines, failed=outcome.failed)

    def _cmd_attack(self, args):
        return self._command("attack", args)

    def _cmd_move(self, args):
        return self._command("move", args)

    def _cmd_leave(self, args):
        return self._command("leave", args)

    # ------------------------------------------------------------ 回合 / 换牌

    def _cmd_end(self, args):
        if args:
            return fail(["end 不接受参数（收到 '%s'）" % " ".join(args)])
        result = self._tool("match.end_turn", {})
        if result.ok and result.code in ("ok", "cancelled", "already-ended"):
            self.session.set_notice("")   # 提示条由 activePlayerSide 派生，避免「等待对手…」滞留
            return ok(["回合已结束。"])
        return self._explain(result)

    def _cmd_keep(self, args):
        result = self._tool("mulligan.done", {"owner": 0})
        if result.ok:
            self.session.set_notice("")
            return ok(["已确认（keep）：不需要换牌。"])
        return self._explain(result)

    def _cmd_mulligan(self, args):
        result = self._tool("mulligan.begin", {"owner": 0})
        return self._after_tool(result, "换牌已开始。")

    def _cmd_ok(self, args):
        return fail(["当前没有待选择的请求（ok 只在选择模式内有效）。"])

    def _cmd_x(self, args):
        # D6.7：不调 target.cancel，只给提示，服务端状态不变
        return fail(["当前没有待选择的请求；换牌阶段请用 keep 开始"])

    def _cmd_concede(self, args):
        if not self._in_match():
            result = self._tool("match.concede", {"owner": 0})
            if result.ok:
                return ok(["已认输。"])
            return self._explain(result)
        return self._ask_confirm("concede", "再输入一次 concede 确认认输（本局将结束）")

    # ------------------------------------------------------------ 只读

    def _cmd_hand(self, args):
        seat = None
        if args:
            seat = self._int(args[0])
            if seat is None or seat not in (0, 1):
                return fail(["hand 的座位参数只能是 0 或 1"])
        snap = self.session.snapshot()
        lines = []
        if self.session.mode != "watch" and seat not in (None, 0):
            return fail(["对局模式下只能查看自己的手牌（hand 或 hand 0）；seat 1 的手牌不可见"])
        if self.session.mode == "watch":
            seats = [seat] if seat is not None else [0, 1]
            for value in seats:
                view = (snap.views or {}).get(value) or {}
                cards = view.get("selfHand") or []
                lines.append("%s（seat %d）手牌（%d）：" % ("A" if value == 0 else "B", value, len(cards)))
                if not cards:
                    lines.append("  （无）")
                for index, card in enumerate(cards, start=1):
                    lines.append("  %s%d. %s  %s  %s  %s" % (
                        "A" if value == 0 else "B", index,
                        render.pad(render.card_name(card), 20), render.pad_left(render.card_cost(card), 4),
                        render.pad_left(render.card_stats(card), 7), render.card_type_text(card)))
            return ok(lines, refresh=False)
        cards = self._hand()
        lines.append("手牌（%d）：" % len(cards))
        lines.extend("  " + row for row in render.hand_rows(snap, seat))
        return ok(lines, refresh=False)

    def _cmd_units(self, args):
        seat = None
        if args:
            seat = self._int(args[0])
            if seat is None or seat not in (0, 1):
                return fail(["units 的座位参数只能是 0 或 1"])
        if self.session.mode == "watch":
            seats = [seat] if seat is not None else [0, 1]
            lines = []
            for value in seats:
                rows = render.unit_rows(self.session.snapshot(), seat=value)
                lines.append("%s（seat %d）：" % ("A" if value == 0 else "B", value))
                lines.extend("  " + row for row in rows)
            return ok(lines, refresh=False)
        return ok(render.unit_rows(self.session.snapshot(), seat=seat), refresh=False)

    def _cmd_log(self, args):
        count = 20
        flag = None
        for token in args:
            if token in ("--prev", "--all"):
                flag = token
            elif token.isdigit():
                count = int(token)
        room = self.session.room_id
        if flag == "--prev":
            lines = list(self.session.logs.previous(room, limit=count))
            head = "上一局日志（最近 %d 条）：" % len(lines)
        elif flag == "--all":
            entries = self.session.logs.interleaved(self.session.logs.order[-2:], limit=count)
            prev_room = self.session.logs.order[-2] if len(self.session.logs.order) > 1 else None
            lines = []
            for _, room_id, text in entries:
                lines.append(("[上一局] " + text) if room_id == prev_room and prev_room != room else text)
            head = "全部日志（最近 %d 条）：" % len(lines)
        else:
            lines = self.session.log_copy(limit=count)
            head = "战斗日志（最近 %d 条）：" % len(lines)
        if not lines:
            return ok([head, "  （暂无）"], refresh=False)
        return ok([head] + ["  " + line for line in lines], refresh=False)

    def _cmd_help(self, args):
        if args:
            query = args[0]
            verb = self.canonical(query)
            if verb is None:
                return fail(["没有这条指令：%s（输入 help 查看全部）" % query])
            return ok(render.help_table([self._help_row(verb)]), refresh=False)
        rows = [self._help_row(verb) for verb in ORDER]
        return ok(render.help_table(rows), refresh=False)

    def _help_row(self, verb):
        chinese = CHINESE.get(verb, "")
        extras = EXTRA_ALIASES.get(verb, [])
        if extras:
            chinese = "/".join([chinese] + extras)
        if verb == "help":
            chinese = "帮助/?"
        return (verb, chinese, SYNTAX.get(verb, ""), DESCRIPTION.get(verb, ""), GROUP.get(verb, ""))

    # ------------------------------------------------------------ 系统

    def _in_match(self):
        snap = self.session.snapshot()
        return snap.phase in ("play", "mulligan") and not snap.ended

    def _cmd_quit(self, args):
        if self.session.mode == "watch":
            return RunResult(["再见。"], exit_code=0)
        snap = self.session.snapshot()
        # 只有"进行中的对局"才要二次确认（D14.4：换牌相位与终局菜单不确认）
        if snap.ended or snap.phase != "play":
            return RunResult(["再见。"], exit_code=0)
        keep = self.context.get("keep_instance")
        if keep:
            text = "再输入一次 quit 确认退出（实例将保留，可用 play 回座）"
        else:
            text = "再输入一次 quit 确认退出（当前对局将结束，实例将被销毁）"
        return self._ask_confirm("quit", text + "\n（若想结束回合请输入 end / 结束回合）")

    def _cmd_restart(self, args):
        if self.session.rotated is None and not self.session.ended:
            return fail(["等待房间重建…（收到 notify/room.rotated 后才能重开）"])
        hook = self.context.get("restart")
        if hook is None:
            return fail(["当前会话无法重开（缺少管理器上下文）"])
        return hook()

    def _cmd_retry(self, args):
        """主动 observe.view 对齐一次（读类调用，10s＋重试 1 次；不改动作状态）。"""
        result = self._tool("observe.view", {}, kind=READ)
        if not result.ok:
            return self._explain(result)
        frame = result.details if isinstance(result.details, dict) else {}
        if frame.get("kind") == "view.updated":
            self.session.apply_view(frame)
        self.session.mark_unknown(False)
        self.session.set_notice("")
        return ok(["已对齐视图。"])

    # ------------------------------------------------------------ 候选（D16.5）

    def candidates_for(self, text):
        """返回 [(补全文本, 显示文本)]；TUI 补全、plain 引导、hand/units 清单共用同一来源。"""
        stripped = text.rstrip()
        words = stripped.split()
        typing_word = bool(stripped) and not text.endswith(" ")
        if not words or typing_word:
            prefix = words[-1] if words else ""
            verbs = [(verb, CHINESE.get(verb, "")) for verb in ORDER]
            out = []
            for verb, chinese in verbs:
                if verb.startswith(prefix.lower()) or chinese.startswith(prefix):
                    out.append(("%s " % verb, "%s / %s  %s" % (verb, chinese, SYNTAX.get(verb, ""))))
            return out[:12]
        verb = self.canonical(words[0])
        if len(words) >= 2:
            if verb == "log":
                return [("--prev ", "--prev 上一局"), ("--all ", "--all 全部")]
            return []
        snap = self.session.snapshot()
        if verb == "play":
            out = []
            for index, card in enumerate(self._hand(), start=1):
                out.append((str(index), "%d. %s  %s %s" % (
                    index, render.card_name(card), render.card_cost(card), render.card_stats(card))))
            return out[:12]
        if verb in ("attack", "move", "leave"):
            out = []
            for index, card in enumerate(self._units(), start=1):
                out.append((str(index), "%d. %s  %s" % (index, render.card_name(card), render.card_stats(card))))
            return out[:12]
        if verb in ("hand", "units") and self.session.mode == "watch":
            return [("0 ", "0 座位 A"), ("1 ", "1 座位 B")]
        if verb == "log" and len(words) == 1:
            return [("--prev ", "--prev 上一局"), ("--all ", "--all 全部")]
        return []

    def completions(self, text):
        results = []
        for value, display in self.candidates_for(text):
            results.append((value, display))
        return results
