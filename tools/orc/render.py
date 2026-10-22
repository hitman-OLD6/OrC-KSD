"""渲染：``Session 快照 → list[str]``（纯函数）＋ 战斗日志叙述层（D8/D9）＋ 中文宽度。

Front 只附加样式（TUI）或原样打印（plain），两者拿到的是同一批行（D2.7）。
"""

import unicodedata

from . import timeouts


# ---------------------------------------------------------------- 宽度


def dw(text):
    """显示宽度：CJK 宽字符记 2（不用 ``len()``，否则 TUI 与 plain 会错得不一样）。"""
    total = 0
    for char in text or "":
        total += 2 if unicodedata.east_asian_width(char) in ("W", "F") else 1
    return total


def pad(text, width):
    text = text or ""
    return text + " " * max(0, width - dw(text))


def pad_left(text, width):
    text = text or ""
    return " " * max(0, width - dw(text)) + text


def cut(text, width):
    """按显示宽度截断（不切开宽字符，超出加省略号）。"""
    text = text or ""
    if dw(text) <= width:
        return text
    out = []
    used = 0
    for char in text:
        step = 2 if unicodedata.east_asian_width(char) in ("W", "F") else 1
        if used + step > width - 1:
            break
        out.append(char)
        used += step
    return "".join(out) + "…"


def rule(title, width):
    head = "-- %s " % title
    return head + "-" * max(0, width - dw(head))


# ---------------------------------------------------------------- 叙述层（D8/D9）

TEMPLATE_NAMES = (
    "card.played", "unit.deployed", "unit.damage.dealt", "unit.acted",
    "unit.position.changed", "card.died", "card.dying", "card.leaving",
    "card.drawn", "point.gained", "point.lost", "point.changed",
    "turn.start", "turn.end", "card.stat.changed", "card.damaged",
)


def _entity_name(node, fallback=None):
    if isinstance(node, dict):
        if node.get("$hidden"):
            return fallback
        name = node.get("name")
        if name:
            return name
    return fallback


def _entity_ref(node):
    if isinstance(node, dict) and not node.get("$hidden"):
        return node.get("$ref")
    return None


def _has_hidden(node, depth=0):
    if depth > 4:
        return False
    if isinstance(node, dict):
        if node.get("$hidden") is True:
            return True
        return any(_has_hidden(value, depth + 1) for value in node.values())
    if isinstance(node, list):
        return any(_has_hidden(value, depth + 1) for value in node[:20])
    return False


def _update_name(entry):
    keywords = entry.get("keywords") or []
    return entry.get("message") or (keywords[0] if keywords else "?")


def _summary(data):
    if not isinstance(data, dict) or not data:
        return ""
    parts = []
    for key, value in data.items():
        if isinstance(value, dict):
            if value.get("$hidden"):
                text = "$hidden"
            elif value.get("name"):
                text = value["name"]
            elif value.get("$ref"):
                text = value["$ref"]
            elif value.get("$type"):
                text = str(value["$type"]).rsplit(".", 1)[-1]
            else:
                text = "对象"
        elif isinstance(value, list):
            text = "%d 项" % len(value)
        else:
            text = str(value)
        parts.append("%s=%s" % (key, text))
    return ", ".join(parts)[:120]


def _side_prefix(ref, ctx):
    if not ref or ctx.get("mode") != "play":
        return ""
    if ref in ctx.get("self_uids", ()):
        return "你："
    if ref in ctx.get("enemy_uids", ()):
        return "对手："
    return ""


class _Draft(object):
    __slots__ = ("text", "idxs")

    def __init__(self, text, idxs):
        self.text = text
        self.idxs = list(idxs)

    def add(self, idx):
        self.idxs.append(idx)


_SETUP_NOISE_PREFIXES = ("已挂载触发器", "已卸载触发器", "已装载", "已装配", "已注入", "已注册")
_SETUP_NOISE_MARKERS = ("已装载（注入完成）", "已装配（预制体", "已装载（预制体", "放置初始化完成")

# 装配期事件：对玩家不是叙事，默认不显示，`--verbose` 可见（D8.1 例外）。
_SETUP_UPDATE_NAMES = frozenset({
    "card.load",
    "card.created",
    "card.initialized",
    "card.placed",
    "deck.shuffled",
    "effect.loaded",
    "trigger.attached",
})

_DESENSITIZED = ("对手：一次不可见的变化", "（隐藏）一次不可见的变化")


def _is_setup_noise(entry):
    """装配期的基础设施条目（D8.1 例外）：默认不显示，`--verbose` 可见。"""
    kind = entry.get("kind")
    if kind == "log":
        if entry.get("level") == "error":
            return False
        message = (entry.get("message") or "").strip()
        if message.startswith(_SETUP_NOISE_PREFIXES):
            return True
        return any(marker in message for marker in _SETUP_NOISE_MARKERS)
    if kind == "update":
        return _update_name(entry) in _SETUP_UPDATE_NAMES
    return False


def narrate_segment(segment, ctx):
    """一个事件流段 → 若干可读日志行（D8.1 过滤、D9.1 模板、D8.2/D9.3 折叠）。"""
    entries = segment.get("entries") or []
    if not entries:
        return []
    seq = segment.get("sequence")
    verbose = bool(ctx.get("verbose"))
    mode = ctx.get("mode") or "play"

    kept = []
    for index, entry in enumerate(entries):
        if not isinstance(entry, dict):
            continue
        if entry.get("kind") == "attach":
            continue
        if entry.get("level") == "debug":
            continue
        if not verbose and _is_setup_noise(entry):
            continue
        kept.append((index, entry))
    if not kept:
        return []

    updates = [(i, e) for i, e in kept if e.get("kind") == "update"]
    names = [_update_name(e) for _, e in updates]
    has_deploy = any(name in ("unit.deployed", "card.played") for name in names)
    has_point_delta = any(name in ("point.gained", "point.lost") for name in names)

    damage_refs = set()
    died_refs = set()
    acted_refs = set()
    played_refs = set()
    deployed_refs = set()
    for _, entry in updates:
        name = _update_name(entry)
        data = entry.get("data") or {}
        if name == "unit.damage.dealt":
            ref = _entity_ref(data.get("Card"))
            if ref:
                damage_refs.add(ref)
        elif name in ("card.died", "card.dying"):
            ref = _entity_ref(data.get("Card"))
            if ref:
                died_refs.add(ref)
        elif name == "unit.acted":
            ref = _entity_ref(data.get("Unit"))
            if ref:
                acted_refs.add(ref)
        elif name == "card.played":
            ref = _entity_ref(data.get("Card"))
            if ref:
                played_refs.add(ref)
        elif name == "unit.deployed":
            ref = _entity_ref(data.get("Unit"))
            if ref:
                deployed_refs.add(ref)

    consumed = set()          # 被合并掉的条目下标
    point_gain = 0
    point_loss = 0
    point_draft = None
    stat_changed = []
    drafts = []

    def emit(text, idxs):
        if not text:
            return
        drafts.append(_Draft(text, idxs))

    for index, entry in kept:
        if index in consumed:
            continue
        kind = entry.get("kind")
        if kind == "log":
            message = entry.get("message") or ""
            if entry.get("level") == "error":
                keywords = entry.get("keywords") or []
                extra = ""
                for keyword in keywords:
                    if str(keyword).startswith("exception:"):
                        extra = "（%s）" % str(keyword)[len("exception:"):]
                        break
                emit("⚠ %s%s" % (message, extra), [index])
            else:
                emit("· %s" % message, [index])
            continue
        if kind != "update":
            emit("[%s] %s" % (kind, _summary(entry.get("data"))), [index])
            continue

        name = _update_name(entry)
        data = entry.get("data") or {}
        if _has_hidden(data):
            emit("对手：一次不可见的变化" if mode == "play" else "（隐藏）一次不可见的变化", [index])
            continue

        if name == "card.played":
            card_ref = _entity_ref(data.get("Card"))
            card_name = _entity_name(data.get("Card"), "某张卡")
            merged_deploy = None
            if card_ref and card_ref in deployed_refs:
                for other_index, other in updates:
                    if other_index in consumed:
                        continue
                    if _update_name(other) == "unit.deployed" and \
                            _entity_ref((other.get("data") or {}).get("Unit")) == card_ref:
                        merged_deploy = other_index
                        break
            prefix = _side_prefix(card_ref, ctx)
            if merged_deploy is not None:
                consumed.add(merged_deploy)
                emit("%s打出 %s 并部署到%s" % (prefix, card_name, _line_of(data)), [index, merged_deploy])
            else:
                emit("%s打出 %s" % (prefix, card_name), [index])
            continue

        if name == "unit.deployed":
            unit_ref = _entity_ref(data.get("Unit"))
            unit_name = _entity_name(data.get("Unit"), "某单位")
            emit("%s部署 %s 到%s" % (_side_prefix(unit_ref, ctx), unit_name, _line_of(data)), [index])
            continue

        if name == "unit.damage.dealt":
            actor = data.get("Unit")
            actor_ref = _entity_ref(actor)
            actor_name = _entity_name(actor)
            target = data.get("Card")
            target_ref = _entity_ref(target)
            target_name = _entity_name(target, "某个目标")
            amount = data.get("Amount")
            amount_text = "%s 点伤害" % amount if amount is not None else "伤害"
            prefix = _side_prefix(actor_ref, ctx)
            if actor_ref and actor_ref in acted_refs:
                text = "%s%s 攻击 %s，造成 %s" % (prefix, actor_name or "某单位", target_name, amount_text)
            elif actor_name:
                text = "%s%s 对 %s 造成 %s" % (prefix, actor_name, target_name, amount_text)
            else:
                text = "%s对 %s 造成 %s" % (prefix, target_name, amount_text)
            if target_ref and target_ref in died_refs:
                text += "（目标被摧毁）"
            emit(text, [index])
            continue

        if name == "unit.acted":
            continue

        if name == "unit.position.changed":
            if has_deploy:
                continue
            unit = data.get("Unit")
            emit("%s%s 移动到新位置" % (_side_prefix(_entity_ref(unit), ctx),
                                        _entity_name(unit, "某单位")), [index])
            continue

        if name in ("card.died", "card.dying"):
            card_ref = _entity_ref(data.get("Card"))
            if card_ref and card_ref in damage_refs:
                continue
            emit("%s%s 被摧毁" % (_side_prefix(card_ref, ctx), _entity_name(data.get("Card"), "某张卡")), [index])
            continue

        if name == "card.leaving":
            card = data.get("Card")
            emit("%s%s 离场" % (_side_prefix(_entity_ref(card), ctx), _entity_name(card, "某张卡")), [index])
            continue

        if name == "card.drawn":
            player = data.get("Player")
            card = data.get("Card")
            if card is None or (isinstance(card, dict) and card.get("$hidden")):
                emit("对手抽了 1 张牌" if mode == "play" else "抽了 1 张牌", [index])
            else:
                emit("%s抽了 1 张牌" % (_side_prefix(_entity_ref(player), ctx) or ""), [index])
            continue

        if name in ("point.gained", "point.lost"):
            amount = data.get("Amount")
            amount = int(amount) if isinstance(amount, (int, float)) else 0
            if name == "point.gained":
                point_gain += amount
            else:
                point_loss += amount
            if point_draft is None:
                point_draft = _Draft("", [index])
                drafts.append(point_draft)
            else:
                point_draft.idxs.append(index)
            continue

        if name == "point.changed":
            if has_point_delta:
                continue
            new_value = data.get("NewPoints")
            emit("指挥点 变为 %s" % new_value, [index])
            continue

        if name == "turn.start":
            number = data.get("TurnNumber")
            emit("", [index])
            emit("—— 第 %s 回合开始 ——" % (number if number is not None else "?"), [index])
            emit("", [index])
            continue

        if name == "turn.end":
            emit("", [index])
            emit("—— 回合结束 ——", [index])
            emit("", [index])
            continue

        if name == "card.damaged":
            card_ref = _entity_ref(data.get("Card"))
            if card_ref and card_ref in damage_refs:
                continue
            emit("%s%s 受到 %s 点伤害" % (_side_prefix(card_ref, ctx),
                                          _entity_name(data.get("Card"), "某张卡"),
                                          data.get("Amount")), [index])
            continue

        if name == "card.stat.changed":
            stat_changed.append((index, entry))
            continue

        emit("[%s] %s" % (name, _summary(data)), [index])

    if point_draft is not None:
        point_draft.text = _point_text(point_gain, point_loss)

    if not has_strong_event(kept) and stat_changed:
        idxs = [index for index, _ in stat_changed]
        card_name = None
        for _, entry in stat_changed:
            card_name = _entity_name((entry.get("data") or {}).get("Card"))
            if card_name:
                break
        drafts.append(_Draft("%s 的属性发生变化" % (card_name or "某张卡"), idxs))

    lines = []
    counts = []          # 与 lines 平行：连续相同行的计数（>1 时行尾显示 ×N）
    for draft in drafts:
        text = draft.text
        if verbose:
            suffix = " (seq=%s.%s)" % (seq, ",".join(str(index) for index in draft.idxs))
            text = text + suffix if text else suffix.strip()
        # 同段内连续完全相同的行合并（只在本段内折叠，D8.2 的同一条纪律）；
        # 脱敏行只保留一条且不带计数（计数本身会泄漏对手动作量）。
        if text in _DESENSITIZED and text in lines:
            continue
        if text and lines and lines[-1] == text and counts[-1] >= 1:
            counts[-1] += 1
            continue
        lines.append(text)
        counts.append(1)
    for index, count in enumerate(counts):
        if count > 1 and lines[index] not in _DESENSITIZED:
            lines[index] = "%s ×%d" % (lines[index], count)
    while lines and lines[0] == "":
        lines.pop(0)
    while lines and lines[-1] == "":
        lines.pop()
    return lines


def _point_text(gain, loss):
    if gain and loss:
        return "指挥点 +%d-%d" % (gain, loss)
    if gain:
        return "指挥点 +%d" % gain
    return "指挥点 -%d" % loss


def _line_of(data):
    position = data.get("Position")
    name = _entity_name(position)
    return name or "战场"


STRONG_NAMES = set(TEMPLATE_NAMES) - {"card.stat.changed", "unit.acted"}


def has_strong_event(kept):
    for _, entry in kept:
        if entry.get("kind") == "log":
            return True
        if entry.get("kind") != "update":
            continue
        if _update_name(entry) in STRONG_NAMES:
            return True
    return False


# ---------------------------------------------------------------- 终局横幅（D12.1）

REASON_TEXT = {
    "hq-zero": "因总部被摧毁",
    "hqzero": "因总部被摧毁",
    "concede": "因认输",
    "aborted": "对局被管理员终止",
}


def result_line(view):
    if not isinstance(view, dict):
        return ""
    parts = []
    title = view.get("resultTitle")
    outcome = view.get("outcome")
    if title:
        parts.append(title)
    elif outcome == "Win":
        parts.append("胜利")
    elif outcome == "Lose":
        parts.append("失败")
    else:
        parts.append("对局终止")

    reason = view.get("resultReason")
    if reason:
        text = REASON_TEXT.get(str(reason).lower(), str(reason))
        sides = view.get("concedingSide")
        if str(reason).lower() == "concede" and sides in ("Self", "Enemy"):
            text = "因你认输" if sides == "Self" else "因对方认输"
        parts.append(text)

    final_turn = view.get("finalTurn")
    if final_turn:
        parts.append("第 %s 回合" % final_turn)
    return "—— %s ——" % " · ".join(parts)


def watch_result_line(views):
    if not views:
        return ""
    first = views.get(0) or list(views.values())[0]
    parts = []
    outcome = first.get("outcome")
    if outcome == "Win":
        parts.append("胜利：A（seat 0）")
    elif outcome == "Lose":
        parts.append("胜利：B（seat 1）")
    else:
        parts.append("对局终止")
    reason = first.get("resultReason")
    if reason:
        parts.append(REASON_TEXT.get(str(reason).lower(), str(reason)))
    final_turn = first.get("finalTurn")
    if final_turn:
        parts.append("第 %s 回合" % final_turn)
    return "—— %s ——" % " · ".join(parts)


# ---------------------------------------------------------------- 卡面小工具


def _is_hq(card):
    return bool(card.get("isHq")) or (card.get("definition") or {}).get("cardType") == "hq"


def _name(card):
    return (card.get("definition") or {}).get("name") or card.get("cardId") or "?"


def _stats(card):
    card_type = (card.get("definition") or {}).get("cardType")
    attack = card.get("effectiveAttack")
    defense = card.get("effectiveDefense")
    health = card.get("health")
    if card_type in ("order", "counter"):
        return "-"
    if defense is None:
        defense = health
    if attack is None and defense is None:
        return ""
    return "%s/%s" % (attack if attack is not None else "-", defense if defense is not None else "-")


def _cost(card):
    cost = card.get("effectiveCost")
    return "%s费" % cost if cost is not None else "-"


def _hq_line(view, key):
    hq = view.get(key) or {}
    health = hq.get("health")
    if health is None:
        health = hq.get("effectiveDefense")
    definition = hq.get("definition") or {}
    max_health = definition.get("baseDefense")
    if health is None:
        return "HQ ?"
    if max_health:
        return "HQ %s/%s" % (health, max_health)
    return "HQ %s" % health


def _side_header(view, name):
    return "%s  %s  指挥点 %s/%s  手牌 %s  牌库 %s" % (
        name,
        _hq_line(view, "selfHq"), 
        view.get("selfKredits", 0),
        view.get("selfMaxKredits", 0),
        view.get("selfHandCount", 0),
        view.get("selfDeckCount", 0),
    )


def _enemy_header(view, name="对手"):
    return "%s  %s  指挥点 %s/%s  手牌 %s  牌库 %s" % (
        name,
        _hq_line(view, "enemyHq"),
        view.get("enemyKredits") if view.get("enemyKredits") is not None else "?",
        view.get("enemyMaxKredits") if view.get("enemyMaxKredits") is not None else "?",
        view.get("enemyHandCount") if view.get("enemyHandCount") is not None else "?",
        view.get("enemyDeckCount") if view.get("enemyDeckCount") is not None else "?",
    )


def _line_text(cards, numbered=False, marks=None, start=1):
    if not cards:
        return "（空）"
    parts = []
    for offset, card in enumerate(cards):
        if numbered:
            index = start + offset
            mark = (marks or {}).get(card.get("uid"), "")
            text = "%d. %s %s%s" % (index, _name(card), _stats(card), mark)
        else:
            text = "%s %s" % (_name(card), _stats(card))
        parts.append(text.strip())
    return " │ ".join(parts)


def _split_line(view, key):
    cards = view.get(key) or []
    support = [card for card in cards if card.get("zone") == "support" and not _is_hq(card)]
    front = [card for card in cards if card.get("zone") == "frontline" and not _is_hq(card)]
    return support, front


def _marks(snap):
    """可行动标记（D10.3）：⚔ 可攻击、→ 可移动、两者都有 ⚔→。"""
    actions = snap.actions or {}
    movable = set()
    attackable = set()
    for move in actions.get("moves") or []:
        if move.get("uid"):
            movable.add(move["uid"])
    for preview in actions.get("attackPreviews") or []:
        if preview.get("attackerUid"):
            attackable.add(preview["attackerUid"])
    marks = {}
    for uid in movable | attackable:
        if uid in attackable and uid in movable:
            marks[uid] = " ⚔→"
        elif uid in attackable:
            marks[uid] = " ⚔"
        else:
            marks[uid] = " →"
    return marks


def _blocked_reason(card):
    reasons = card.get("blockedReasons") or []
    if reasons:
        return "（%s）" % "、".join(str(reason) for reason in reasons)
    return ""


# ---------------------------------------------------------------- 手牌 / 单位清单


def hand_rows(snap, seat=None, numbered=True, prefix=""):
    """``hand`` 展示用**手牌全集**（D7.4）：**编号＝手牌位置**，不可打出的照常列出并标注原因。

    编号必须与位置一一对应，否则玩家敲的序号会对不上牌；可打出性只用行尾标注表达，不重排。
    """
    cards = _hand_cards(snap, seat)
    actions = _actions_for(snap, seat)
    playable = set(actions.get("playableUids") or [])
    known = actions.get("handPlayabilityKnown", False)
    rows = []
    for pos, card in enumerate(cards, start=1):
        ok = (not known) or (card.get("uid") in playable)
        marker = "%s%d." % (prefix, pos)
        note = ""
        if not ok:
            note = _blocked_reason(card) or "（当前不可打出）"
        rows.append("%s %s  %s  %s  %s%s" % (
            pad(marker, 5 if not prefix else 6),
            pad(_name(card), 20),
            pad_left(_cost(card), 4),
            pad_left(_stats(card), 7),
            _type_text(card),
            note,
        ))
    return rows


def _type_text(card):
    card_type = (card.get("definition") or {}).get("cardType") or "?"
    return {"unit": "单位", "order": "指令", "counter": "反制", "hq": "总部"}.get(card_type, card_type)


def _hand_cards(snap, seat=None):
    if snap.mode == "watch" or seat is not None:
        target = snap.seat if seat is None else int(seat)
        return list(((snap.views or {}).get(target) or {}).get("selfHand") or [])
    return list((snap.view or {}).get("selfHand") or [])


def _actions_for(snap, seat=None):
    if snap.mode == "watch":
        return {}
    return snap.actions or {}


def unit_rows(snap, seat=None):
    units = _unit_cards(snap, seat)
    # watch 下由调用方（`units 0|1`）提供座位标题，这里不再重复座位前缀。
    label = "单位" if snap.mode == "watch" else "我方单位"
    empty = "（无在场单位）" if snap.mode == "watch" else "（我方无在场单位）"
    if not units:
        return [empty]
    marks = _marks(snap)
    rows = ["%s（%d）" % (label, len(units))]
    for index, card in enumerate(units, start=1):
        zone = "支援线" if card.get("zone") == "support" else "前线"
        slot = card.get("slotIndex")
        reason = "" if marks.get(card.get("uid")) else (_blocked_reason(card) or "")
        rows.append("%d. %s  %s第%s槽  %s%s" % (
            index, pad(_name(card), 20), zone,
            (slot + 1) if isinstance(slot, int) else "?",
            _stats(card), marks.get(card.get("uid"), "") + reason,
        ))
    return rows


def _unit_cards(snap, seat=None):
    if snap.mode == "watch" or seat is not None:
        target = snap.seat if seat is None else int(seat)
        view = (snap.views or {}).get(target) or {}
    else:
        view = snap.view or {}
    cards = [card for card in (view.get("selfLine") or [])
             if card.get("ownerSide") == "self" and not _is_hq(card)]
    cards.sort(key=lambda card: (0 if card.get("zone") == "support" else 1,
                                 card.get("slotIndex") if card.get("slotIndex") is not None else 0))
    return cards


# ---------------------------------------------------------------- 候选（D1.2/D1.6）


def candidate_rows(pending, snap):
    """待决候选 → ``序号. 可读标签``（formation-gap 用邻居名，slot 附槽号）。"""
    if not pending:
        return []
    candidates = pending.get("candidates") or []
    identifiers = pending.get("identifiers") or []
    occupants = _support_front_occupants(snap)
    rows = []
    for candidate in candidates:
        index = candidate.get("index")
        kind = candidate.get("kind")
        name = candidate.get("name")
        label = None
        if kind == "formation-gap":
            label = _gap_label(index, occupants)
        elif kind == "slot":
            slot = candidate.get("slotIndex")
            label = "前线第 %s 号槽" % (slot + 1 if isinstance(slot, int) else "?")
        elif kind == "unit":
            label = "%s %s" % (name or "某单位", "")
        elif kind == "hq":
            label = name or "总部"
        else:
            label = name
        if not label:
            label = "候选 %s（%s）" % (index, kind or "?")
        if candidate.get("alive") is False:
            label += "（已失效）"
        rows.append("%2d. %s" % (index, label.strip()))
    for position, identifier in enumerate(identifiers, start=len(candidates) + 1):
        rows.append("%2d. %s（标识）" % (position, identifier))
    return rows


def _support_front_occupants(snap):
    view = snap.view or {}
    if snap.mode == "watch" and snap.views:
        view = snap.views.get(snap.seat) or view
    line = view.get("selfLine") or []
    occupants = [card for card in line if not _is_hq(card)]
    occupants.sort(key=lambda card: (0 if card.get("zone") == "support" else 1,
                                     card.get("slotIndex") if card.get("slotIndex") is not None else 0))
    return occupants


def _gap_label(index, occupants):
    """D1.2：ordinal = index-1（0＝最左、N＝最右，其余夹在两者之间）。"""
    try:
        ordinal = int(index) - 1
    except (TypeError, ValueError):
        return "插入位置 ?"
    count = len(occupants)
    if ordinal <= 0:
        where = "最左"
    elif ordinal >= count:
        where = "最右" if count > 0 else "最右"
    else:
        left = _name(occupants[ordinal - 1])
        right = _name(occupants[ordinal])
        where = "在 %s 与 %s 之间" % (left, right)
    return "插入位置 %s：%s" % (index, where)


def need_text(minimum, maximum):
    """把 min/max 说成人话：避免「选 1..1」被读成候选范围（那是选取数量）。"""
    try:
        low, high = int(minimum), int(maximum)
    except (TypeError, ValueError):
        return "需选 1 个"
    if low == high:
        return "需选 %d 个" % low
    return "需选 %d..%d 个" % (low, high)


def selection_lines(snap, picks=None):
    pending = snap.pending or {}
    rows = candidate_rows(pending, snap)
    picks = picks or []
    minimum = pending.get("min", 0)
    maximum = pending.get("max", 0)
    caption = _selector_caption(pending, snap)
    head = "%s（%s，已选 %s 个）" % (caption, need_text(minimum, maximum), len(picks))
    lines = [rule(head, 96)]
    lines.extend(rows)
    return lines


def _selector_caption(pending, snap):
    action = pending.get("action")
    selector = pending.get("selector")
    if selector == "mulligan" or action == "mulligan":
        return "换牌：选择要换掉的手牌"
    text = {
        "deploy": "选择部署位置",
        "attack": "选择攻击目标",
        "move": "选择移动目标",
    }.get(action)
    if text:
        source = pending.get("sourceName")
        if source:
            return "%s（来源：我方 %s）" % (text, source)
        return text
    return "选择（%s）" % (selector or action or "待决")


# ---------------------------------------------------------------- 主渲染


def render(snap, width=96, log_lines=8):
    if snap.mode == "watch":
        return render_watch(snap, width, log_lines)
    return render_play(snap, width, log_lines)


def render_play(snap, width=96, log_lines=8):
    lines = []
    title = "OrC 对局"
    if snap.room_id:
        title += " room %s" % snap.room_id
    title += " · 第 %s 回合" % (snap.turn or 0)
    phase = snap.phase
    if phase == "mulligan":
        title += " · 换牌阶段"
    elif snap.ended:
        title += " · 终局"
    else:
        title += _turn_suffix(snap)
    lines.append(rule(title, width))

    if snap.ended or snap.ending:
        view = snap.final_view or snap.view
        banner = result_line(view)
        if banner:
            lines.append(banner)
            lines.append("")

    view = snap.view
    if not isinstance(view, dict) or not view:
        lines.append("（等待战场数据…）")
    else:
        lines.append(rule("对手", width))
        lines.append("  " + _enemy_header(view, snap.enemy_name))
        enemy_support, enemy_front = _split_line(view, "enemyLine")
        lines.append("  支援线: " + _line_text(enemy_support))
        lines.append("  前线  : " + _line_text(enemy_front))
        lines.append(rule("我方", width))
        lines.append("  " + _side_header(view, snap.self_name))
        units = _unit_cards(snap)
        marks = _marks(snap)
        if units:
            for index, card in enumerate(units, start=1):
                zone = "支援线" if card.get("zone") == "support" else "前线"
                slot = card.get("slotIndex")
                reason = "" if marks.get(card.get("uid")) else (_blocked_reason(card) or "")
                lines.append("  %d. %s  %s%s  %s%s" % (
                    index, pad(_name(card), 20), zone,
                    "第%s槽" % (slot + 1) if isinstance(slot, int) else "",
                    _stats(card), marks.get(card.get("uid"), "") + reason,
                ))
        else:
            lines.append("  （没有在场单位）")
        lines.append(rule("手牌 (%s) · 编号＝手牌位置" % len(view.get("selfHand") or []), width))
        for row in hand_rows(snap):
            lines.append("  " + row)

    lines.extend(_log_block(snap, width, log_lines))

    if snap.pending:
        lines.append(rule("选择", width))
        for row in selection_lines(snap, _snap_picks(snap)):
            lines.append(row if row.startswith("-") else "  " + row)

    if snap.ending:
        lines.append("")
        lines.append("  对局已结束：restart 重开 / quit 退出")
    lines.append("")
    lines.append(_notice(snap))
    return lines


def _snap_picks(snap):
    return getattr(snap, "picks", None) or []


def _turn_suffix(snap):
    view = snap.view or {}
    if snap.phase != "play":
        return ""
    return " · 轮到对手" if view.get("activePlayerSide") == "enemy" else " · 轮到你"


def render_watch(snap, width=96, log_lines=8):
    views = snap.views or {}
    seats = sorted(views.keys())
    single = len(seats) < 2
    turn = snap.turn or 0
    active_seat = _active_seat(views)
    title = "对局 room %s · 第 %s 回合" % (snap.room_id or "?", turn)
    if active_seat is not None:
        title += " · 轮到 %s" % ("B" if active_seat == 1 else "A")
    if single:
        title += " （单侧数据）"
    lines = [rule(title, width)]

    if snap.ending or snap.ended:
        banner = watch_result_line(views)
        if banner:
            lines.append(banner)
            lines.append("")

    view_b = views.get(1)
    view_a = views.get(0)
    if view_b:
        lines.append(rule("B（seat 1）", width))
        lines.append("  " + _side_header(view_b, "B"))
        support, front = _split_line(view_b, "selfLine")
        lines.append("  支援线: " + _line_text(support))
        if not view_a:
            lines.append("  前线  : " + _line_text(front))
        lines.append("  手牌  : " + _watch_hand(view_b, "B"))
    if not single:
        lines.append(rule("前线（共享）", width))
        lines.append("  " + _shared_front(view_a, view_b))
    if view_a:
        lines.append(rule("A（seat 0）", width))
        lines.append("  " + _side_header(view_a, "A"))
        support, front = _split_line(view_a, "selfLine")
        lines.append("  支援线: " + _line_text(support))
        if not view_b:
            lines.append("  前线  : " + _line_text(front))
        lines.append("  手牌  : " + _watch_hand(view_a, "A"))

    lines.extend(_log_block(snap, width, log_lines))
    if snap.ending:
        lines.append("")
        lines.append("  对局已结束：quit 退出（观战只读）")
    lines.append("")
    lines.append(_notice(snap))
    return lines


def _active_seat(views):
    for seat, view in views.items():
        if (view or {}).get("phase") != "play":
            continue
        return seat if (view or {}).get("activePlayerSide") == "self" else 1 - seat
    return None


def _watch_hand(view, prefix):
    cards = view.get("selfHand") or []
    if not cards:
        return "（无）"
    parts = []
    for index, card in enumerate(cards, start=1):
        parts.append("%s%d. %s %s %s" % (prefix, index, _name(card), _cost(card), _stats(card)))
    return "  ".join(parts)


def _shared_front(view_a, view_b):
    entries = {}
    for view, prefix in ((view_a, "A"), (view_b, "B")):
        if not view:
            continue
        for card in view.get("selfLine") or []:
            if card.get("zone") != "frontline" or _is_hq(card):
                continue
            key = card.get("slotIndex")
            if key in entries:
                continue
            entries[key] = "%s%s" % (prefix, _name(card))
    if not entries:
        return "（空）"
    parts = []
    for slot in sorted(entries):
        parts.append("%s[%s]" % (entries[slot], slot + 1 if isinstance(slot, int) else "?"))
    return " │ ".join(parts)


def _log_block(snap, width, log_lines):
    if log_lines is None:
        # plain 前端自己打印「自上次以来的新日志」，块内不再重复整段历史（D8.5）。
        return []
    logs = (snap.logs or [])[-log_lines:] if log_lines else (snap.logs or [])
    lines = [rule("日志", width)]
    if not logs:
        lines.append("  （暂无）")
    else:
        for entry in logs:
            lines.append("  " + cut(entry, width - 2))
    return lines


def _notice(snap):
    if snap.unknown:
        return "⟳ 结果未知：等待服务端刷新 · 输入 retry 对齐 / quit 退出"
    if snap.notice:
        return snap.notice
    if snap.ending:
        return "[终局] restart 重开 / quit 退出"
    if snap.pending:
        pending = snap.pending
        return "[选择] 输入序号（可多选，逗号/空格分隔）· ok 提交 · x 取消（%s）" % need_text(
            pending.get("min", 0), pending.get("max", 0))
    if snap.mode == "watch":
        views = snap.views or {}
        seat = _active_seat(views)
        side = "A（seat 0）" if seat == 0 else ("B（seat 1）" if seat == 1 else "?")
        return "[观战] 第 %s 回合 · 轮到 %s · 输入 help 查看指令" % (snap.turn or 0, side)
    if snap.phase == "mulligan":
        count = len((snap.view or {}).get("selfHand") or [])
        return "[换牌阶段] keep=直接开始 · mulligan=选择换牌 · 手牌 %d 张" % count
    view = snap.view or {}
    if snap.phase == "play" and view.get("activePlayerSide") == "enemy":
        return "对手行动中…（输入行可用，动作会排队）"
    if not snap.connected:
        return "连接已断开 · 可尝试 retry 重连"
    return "[进行中] 轮到你 · help 查看指令"


# ---------------------------------------------------------------- help（D14.6）


def help_table(rows, width=96):
    """四列（英文/中文/语法/说明），中文列按显示宽度对齐。"""
    head = "%s %s %s %s" % (pad("英文", 10), pad("中文", 10), pad("语法", 26), "说明")
    lines = [rule("指令", width), head]
    current = None
    for english, chinese, syntax, description, group in rows:
        if group != current:
            current = group
            lines.append("")
            lines.append("[%s]" % group)
        lines.append("%s %s %s %s" % (
            pad(english, 10), pad(chinese, 10), pad(syntax, 26), description))
    return lines


# ---------------------------------------------------------------- 公开小工具（命令层复用，避免重复实现）

card_name = _name
card_cost = _cost
card_stats = _stats
card_type_text = _type_text
selector_caption = _selector_caption
