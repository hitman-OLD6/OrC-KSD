"""Session：单写状态仓库（D2.3）＋ 按局分段的战斗日志（D12.2）。

写者只有读线程与动作 worker；UI 只取 ``snapshot()`` 的拷贝（RLock 只护拷贝）。
"""

import copy
import threading
from collections import deque

from . import render, timeouts


OUTCOMES = ("Win", "Lose")


def is_ended_view(view):
    if not isinstance(view, dict):
        return False
    outcome = view.get("outcome")
    if outcome in OUTCOMES:
        return True
    return view.get("phase") == "over"


class LogBook(object):
    """当前局 ＋ 上一局各留一份 deque（更早丢弃）；另有一条全局时间线供 ``log --all``。"""

    def __init__(self):
        self.items = {}
        self.order = []
        self.stream = deque(maxlen=timeouts.LOG_MAXLEN * 2)
        self._next = 0

    def add(self, room_id, lines):
        if not lines:
            return
        room_id = room_id or "-"
        if room_id not in self.items:
            self.items[room_id] = deque(maxlen=timeouts.LOG_MAXLEN)
            self.order.append(room_id)
        for line in lines:
            self.items[room_id].append(line)
            self.stream.append((self._next, room_id, line))
            self._next += 1

    def current(self, room_id, limit=20):
        items = list(self.items.get(room_id or "-", ()))
        return items[-limit:] if limit else items

    def previous(self, room_id, limit=20):
        room_id = room_id or "-"
        if room_id not in self.order:
            return []
        idx = self.order.index(room_id)
        if idx <= 0:
            return []
        items = list(self.items.get(self.order[idx - 1], ()))
        return items[-limit:] if limit else items

    def interleaved(self, room_ids, limit=20):
        rooms = set(room_ids)
        items = [entry for entry in self.stream if entry[1] in rooms]
        return items[-limit:] if limit else items

    def since(self, index, limit=None):
        items = [entry for entry in self.stream if entry[0] >= index]
        return items[-limit:] if limit else items

    def last_index(self):
        return self._next

    def clear(self):
        self.items = {}
        self.order = []
        self.stream.clear()
        self._next = 0


class Snapshot(object):
    """渲染只读快照（不含锁，不含可变引用语义）。"""

    def __init__(self):
        self.mode = "play"
        self.seat = 0
        self.room_id = None
        self.phase = None
        self.turn = 0
        self.view = None
        self.views = {}
        self.actions = None
        self.pending = None
        self.logs = []
        self.notice = ""
        self.ended = False
        self.ending = False
        self.rotated = None
        self.final_view = None
        self.final_seat = 0
        self.unknown = False
        self.admitted = False
        self.connected = True
        self.seat_token = None
        self.self_name = "我方"
        self.enemy_name = "对手"
        self.seats = {}
        self.last_seq = 0
        self.picks = []


class Session(object):
    def __init__(self, mode="play", seat=0, verbose=False):
        self.lock = threading.RLock()
        self.mode = mode
        self.seat = seat
        self.verbose = verbose
        self.room_id = None
        self.epoch = None
        self.phase = None
        self.turn = 0
        self.view = None
        self.views = {}
        self.actions = None
        self.pending = None
        self.outcome = None
        self.final_view = None
        self.final_seat = seat
        self.rotated = None
        self.ended = False
        self.ending = False
        self.unknown = False
        self.admitted = False
        self.connected = True
        self.seat_token = None
        self.seats = {}
        self.notice = ""
        self.logs = LogBook()
        self.last_seq = 0
        self.view_version = 0
        self.last_error = ""
        self._fingerprint = None

    # ------------------------------------------------------------ 写入口

    def set_notice(self, text):
        with self.lock:
            self.notice = text or ""

    def set_pending(self, pending):
        with self.lock:
            self.pending = pending if pending else None

    def clear_pending(self):
        with self.lock:
            self.pending = None

    def add_log(self, lines, room_id=None):
        with self.lock:
            self.logs.add(room_id or self.room_id, lines)

    def set_admitted(self, admitted, seat_token=None):
        with self.lock:
            self.admitted = admitted
            if seat_token:
                self.seat_token = seat_token

    def set_connected(self, connected):
        with self.lock:
            self.connected = connected

    def mark_unknown(self, unknown):
        with self.lock:
            self.unknown = bool(unknown)

    # ------------------------------------------------------------ 帧 → 状态

    def apply_frame(self, method, params):
        if method == "notify/view":
            self.apply_view(params)
        elif method == "notify/segment":
            self.apply_segments(params)
        elif method == "notify/admission":
            self.apply_admission(params)
        elif method == "notify/room.rotated":
            self.apply_rotated(params)
        elif method == "notify/room.seats":
            self.apply_seats(params)

    def apply_view(self, params):
        perspective = params.get("perspective") or {}
        mode = perspective.get("mode")
        with self.lock:
            if mode == "omniscient" or params.get("views"):
                views = params.get("views") or []
                for item in views:
                    seat = item.get("seat")
                    if seat is None:
                        continue
                    self.views[int(seat)] = item.get("view")
                    if self.view is None and int(seat) == self.seat:
                        self.view = item.get("view")
                        self.actions = item.get("actions")
                if self.mode == "watch" and self.views:
                    # 中性视图以 seat 0 的帧为主，另一侧由 views 补齐
                    self.view = self.views.get(self.seat) or list(self.views.values())[0]
                    self.actions = None
            else:
                view = params.get("view")
                seat = perspective.get("seat")
                if view is not None:
                    self.view = view
                    self.actions = params.get("actions")
                    if seat is not None:
                        self.views[int(seat)] = view
            if self.room_id is None:
                self.room_id = (params.get("roomId") or (self.view or {}).get("matchId") or None)
            view = self.view or {}
            fingerprint = (view.get("turn"), view.get("phase"), view.get("selfKredits"),
                           len(view.get("selfHand") or []),
                           tuple(card.get("uid") for card in (view.get("selfHand") or [])))
            if self.unknown and fingerprint != self._fingerprint:
                # D11.5 ③：视野真的变了 ⇒ 动作已生效，清 unknown
                self.unknown = False
                self.notice = ""
            self._fingerprint = fingerprint
            self.phase = view.get("phase") or self.phase
            self.turn = view.get("turn") or self.turn
            self.view_version += 1
            if is_ended_view(view):
                self.outcome = view.get("outcome")
                self.final_view = view
                self.final_seat = self.seat
                self.ending = True
                self.ended = True

    def apply_segments(self, params):
        segments = params.get("segments") or []
        if not segments:
            return
        ctx = self._narrate_context()
        lines = []
        max_seq = self.last_seq
        for segment in segments:
            seq = segment.get("sequence")
            if isinstance(seq, int):
                max_seq = max(max_seq, seq)
            lines.extend(render.narrate_segment(segment, ctx))
        with self.lock:
            self.last_seq = max_seq
        self.add_log(lines)

    def _narrate_context(self):
        self_uids = set()
        enemy_uids = set()
        view = self.view or {}
        for card in view.get("selfLine") or []:
            if card.get("uid"):
                self_uids.add(card["uid"])
        for card in view.get("selfHand") or []:
            if card.get("uid"):
                self_uids.add(card["uid"])
        for card in view.get("enemyLine") or []:
            if card.get("uid"):
                enemy_uids.add(card["uid"])
        for key in ("selfHq", "enemyHq"):
            card = view.get(key)
            if isinstance(card, dict) and card.get("uid"):
                (self_uids if key == "selfHq" else enemy_uids).add(card["uid"])
        return {
            "mode": self.mode,
            "verbose": self.verbose,
            "self_uids": self_uids,
            "enemy_uids": enemy_uids,
        }

    def apply_admission(self, params):
        decision = params.get("decision")
        token = params.get("seatToken")
        with self.lock:
            if decision == "admitted":
                self.admitted = True
                if token:
                    self.seat_token = token
            elif decision in ("rejected", "revoked"):
                self.admitted = False
            self.state_notice = params.get("reason") or ""

    def apply_rotated(self, payload):
        with self.lock:
            self.rotated = payload
            self.ending = True
            self.ended = True
            self.pending = None
            self.unknown = False
            # 清一屏状态：战场留在 final_view 里（D12.5 保留最终战场）
            if self.mode == "play":
                self.view = None
                self.actions = None
            else:
                self.views = {}
                self.view = None
            self.phase = "lobby"
            self.room_id = payload.get("roomId") or self.room_id

    def apply_seats(self, payload):
        with self.lock:
            self.seats = payload or {}

    def apply_rotated_reset(self, room_id=None):
        """重开成功后清掉终局菜单状态（D12.5/D12.8：日志按新 roomId 另起一份）。"""
        with self.lock:
            self.rotated = None
            self.ended = False
            self.ending = False
            self.outcome = None
            self.final_view = None
            self.phase = None
            self.turn = 0
            self.view = None
            self.actions = None
            self.pending = None
            self.unknown = False
            self.notice = ""
            self._fingerprint = None
            if room_id:
                self.room_id = room_id

    # ------------------------------------------------------------ 读入口

    def snapshot(self):
        with self.lock:
            snap = Snapshot()
            snap.mode = self.mode
            snap.seat = self.seat
            snap.room_id = self.room_id
            snap.phase = self.phase
            snap.turn = self.turn
            snap.view = self.view
            snap.views = dict(self.views)
            snap.actions = self.actions
            snap.pending = self.pending
            snap.notice = self.notice
            snap.ended = self.ended
            snap.ending = self.ending
            snap.rotated = self.rotated
            snap.final_view = self.final_view
            snap.final_seat = self.final_seat
            snap.unknown = self.unknown
            snap.admitted = self.admitted
            snap.connected = self.connected
            snap.seat_token = self.seat_token
            snap.seats = dict(self.seats)
            snap.last_seq = self.last_seq
            snap.logs = list(self.logs.current(self.room_id, limit=0))
            view = self.view or {}
            snap.self_name = view.get("selfPlayerName") or "我方"
            snap.enemy_name = view.get("enemyPlayerName") or "对手"
            snap.picks = []
            return snap

    def hand(self, seat=None):
        with self.lock:
            if self.mode == "watch" or seat is not None:
                target = self.seat if seat is None else int(seat)
                view = self.views.get(target)
                return list((view or {}).get("selfHand") or [])
            return list((self.view or {}).get("selfHand") or [])

    def units(self, seat=None):
        """我方面板顺序（D10.1）：支援线在前、前线在后，各自 slotIndex 升序。HQ 不参与编号。"""
        with self.lock:
            if self.mode == "watch" or seat is not None:
                target = self.seat if seat is None else int(seat)
                view = self.views.get(target)
            else:
                view = self.view
            line = list((view or {}).get("selfLine") or [])
        own = [card for card in line if card.get("ownerSide") == "self" and not card.get("isHq")]
        own.sort(key=lambda card: (0 if card.get("zone") == "support" else 1,
                                   card.get("slotIndex") if card.get("slotIndex") is not None else 0))
        return own

    def log_copy(self, limit=20):
        with self.lock:
            return self.logs.current(self.room_id, limit=limit)


def clone(value):
    try:
        return copy.deepcopy(value)
    except Exception:  # noqa: BLE001 - 快照失败时退回浅拷贝，不阻断渲染
        return value
