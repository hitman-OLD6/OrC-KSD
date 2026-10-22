"""单测替身：FakeDispatcher（注入任意响应序列，不起 socket、不起 TTY，D2.6）。"""

from orc.transport import ACTION, IDispatcher, ToolResult


def ok_result(code="ok", message="ok", details=None, engine=None):
    return {"ok": True, "code": code, "message": message,
            "details": details or {}, "engineResult": engine or {}}


def fail_result(code, message="失败", details=None):
    return {"ok": False, "code": code, "message": message, "details": details or {}}


def pending_result(pending, code="targeting"):
    return {"ok": True, "code": code, "message": "待决",
            "details": {"pending": pending}, "engineResult": {}}


class FakeDispatcher(IDispatcher):
    def __init__(self, script=None, requests=None):
        IDispatcher.__init__(self)
        self.connected = True
        self.calls = []
        self.requests = []
        self.script = {}
        for name, payloads in (script or {}).items():
            self.script[name] = list(payloads)
        self.request_script = {}
        for method, payloads in (requests or {}).items():
            self.request_script[method] = list(payloads)
        self._pending = None

    # -- 脚本 --
    def expect(self, name, payload):
        self.script.setdefault(name, []).append(payload)
        return self

    def _invoke(self, name, arguments, timeout, kind):
        self.calls.append((name, dict(arguments or {})))
        payloads = self.script.get(name)
        if payloads:
            payload = payloads.pop(0)
        else:
            payload = ok_result()
        if callable(payload):
            payload = payload(dict(arguments or {}))
        return ToolResult(payload)

    def request(self, method, params=None, timeout=None, retries=0):
        self.requests.append((method, dict(params or {})))
        payloads = self.request_script.get(method)
        if payloads:
            return payloads.pop(0)
        return {}

    # -- 待决 --
    def peek_pending(self):
        return self._pending

    def wait_pending(self, session_id=None, timeout=None):
        return self._pending

    def clear_pending(self, session_id=None):
        self._pending = None

    def names(self):
        return [name for name, _ in self.calls]

    def args_of(self, name):
        for call_name, arguments in self.calls:
            if call_name == name:
                return arguments
        return None


def make_card(uid, name, card_type="unit", attack=3, defense=3, cost=1, zone="hand",
              slot=-1, owner="self", is_hq=False, playable=None, blocked=None):
    definition = {
        "kind": "CardDefinition",
        "cardId": "pack/x/%s" % uid,
        "name": name,
        "cardType": card_type,
        "unitType": "infantry",
        "cost": cost,
        "baseAttack": attack,
        "baseDefense": defense,
    }
    card = {
        "kind": "CardInstance",
        "uid": uid,
        "definition": definition,
        "cardId": definition["cardId"],
        "effectiveCost": cost,
        "effectiveAttack": attack,
        "effectiveDefense": defense,
        "health": defense,
        "zone": zone,
        "slotIndex": slot,
        "ownerSide": owner,
        "isHq": is_hq,
        "canPlayCard": False,
        "canAttack": False,
        "canMoveAndAttack": False,
        "blockedReasons": list(blocked or []),
        "keywords": [],
    }
    return card


def make_actions(playable=None, moves=None, attack_previews=None, mulligan_open=False,
                 known=True):
    return {
        "playableUids": list(playable or []),
        "handPlayabilityKnown": known,
        "moves": list(moves or []),
        "attackPreviews": list(attack_previews or []),
        "orderTargets": [],
        "deploymentGaps": [],
        "canEndTurn": True,
        "attacksEnabled": True,
        "mulliganOpen": mulligan_open,
    }


def make_view(phase="play", turn=1, hand=None, self_line=None, enemy_line=None,
              kredits=3, max_kredits=3, active="self", hand_count=None, deck_count=30):
    self_hand = list(hand or [])
    return {
        "matchId": "room-1",
        "turn": turn,
        "selfPlayerName": "我方",
        "enemyPlayerName": "对手",
        "phase": phase,
        "outcome": "None",
        "concedingSide": "None",
        "activePlayerSide": active,
        "selfKredits": kredits,
        "selfMaxKredits": max_kredits,
        "enemyKredits": kredits,
        "enemyMaxKredits": max_kredits,
        "selfHandCount": len(self_hand) if hand_count is None else hand_count,
        "selfDeckCount": deck_count,
        "enemyHandCount": 4,
        "enemyDeckCount": 30,
        "selfCounterCount": 0,
        "selfHand": self_hand,
        "selfLine": list(self_line or []),
        "frontLineSlotCount": 5,
        "supportLineSlotCount": 4,
        "selfHq": make_card("hq-self", "我方总部", "hq", attack=None, defense=20,
                            zone="support", slot=2, is_hq=True),
        "enemyLine": list(enemy_line or []),
        "enemyHq": make_card("hq-enemy", "对手总部", "hq", attack=None, defense=20,
                             zone="support", slot=2, owner="enemy", is_hq=True),
        "pendingChoices": [],
    }


def seat_frame(view=None, actions=None, seat=0):
    return {
        "kind": "view.updated",
        "perspective": {"mode": "seat", "seat": seat},
        "view": view or make_view(),
        "actions": actions or make_actions(),
    }


def omniscient_frame(view_a=None, view_b=None, actions=None):
    return {
        "kind": "view.updated",
        "perspective": {"mode": "omniscient"},
        "views": [
            {"seat": 0, "view": view_a or make_view(), "actions": actions or make_actions()},
            {"seat": 1, "view": view_b or make_view(active="enemy"), "actions": actions or make_actions()},
        ],
    }


def make_pending(session_id="p1", selector="slot", action="deploy", minimum=1, maximum=1,
                 candidates=None, identifiers=None):
    return {
        "sessionId": session_id,
        "selector": selector,
        "mode": "Click",
        "min": minimum,
        "max": maximum,
        "seat": 0,
        "action": action,
        "candidates": list(candidates or [
            {"index": 1, "kind": "formation-gap", "ref": "g1", "name": "Formation gap 0", "alive": True},
            {"index": 2, "kind": "formation-gap", "ref": "g2", "name": "Formation gap 1", "alive": True},
        ]),
        "identifiers": list(identifiers or []),
    }
