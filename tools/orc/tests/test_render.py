"""Renderer 与叙述层的确定性断言（S7/S8/S13）：模板折叠、降级、中文对齐。"""

import unittest

from orc import render
from orc.session import Session
from orc.tests import fake


def entry(name, data=None, kind="update", level="info", message=None, keywords=None):
    return {
        "kind": kind,
        "timestamp": "2026-01-01T00:00:00+00:00",
        "level": level,
        "source": "bus",
        "message": message if message is not None else name,
        "keywords": keywords if keywords is not None else [name],
        "data": data or {},
    }


def ref(uid, name):
    return {"$ref": uid, "name": name, "$alive": True}


def segment(entries, seq=7):
    return {"sequence": seq, "timestamp": "2026-01-01T00:00:00+00:00",
            "entries": entries, "children": []}


def ctx(self_uids=(), enemy_uids=(), mode="play", verbose=False):
    return {"mode": mode, "verbose": verbose,
            "self_uids": set(self_uids), "enemy_uids": set(enemy_uids)}


class NarrationTest(unittest.TestCase):
    def test_attack_damage_and_death_fold_into_one_line(self):
        seg = segment([
            entry("unit.acted", {"Unit": ref("a", "步兵")}),
            entry("unit.damage.dealt", {"Unit": ref("a", "步兵"), "Card": ref("b", "敌装甲"), "Amount": 3}),
            entry("card.damaged", {"Card": ref("b", "敌装甲"), "Amount": 3}),
            entry("card.died", {"Card": ref("b", "敌装甲")}),
        ])
        lines = render.narrate_segment(seg, ctx(self_uids=["a"], enemy_uids=["b"]))
        self.assertEqual(len([line for line in lines if line.strip()]), 1)
        text = lines[0]
        self.assertIn("你：步兵 攻击 敌装甲", text)
        self.assertIn("3 点伤害", text)
        self.assertIn("目标被摧毁", text)
        self.assertNotIn("受到", text)

    def test_effect_damage_has_no_attack_word(self):
        seg = segment([
            entry("unit.damage.dealt", {"Unit": None, "Card": ref("b", "敌装甲"), "Amount": 3}),
        ])
        lines = render.narrate_segment(seg, ctx(self_uids=["a"], enemy_uids=["b"]))
        self.assertEqual(len(lines), 1)
        self.assertIn("对 敌装甲 造成 3 点伤害", lines[0])
        self.assertNotIn("攻击", lines[0])

    def test_deploy_suppresses_position_changes(self):
        seg = segment([
            entry("card.played", {"Card": ref("a", "步兵"), "Player": {"$type": "P"}}),
            entry("unit.deployed", {"Unit": ref("a", "步兵")}),
            entry("unit.position.changed", {"Unit": ref("a", "步兵")}),
        ])
        lines = render.narrate_segment(seg, ctx(self_uids=["a"]))
        self.assertEqual(len(lines), 1)
        self.assertIn("打出 步兵 并部署到战场", lines[0])
        self.assertNotIn("移动", "\n".join(lines))

    def test_two_sides_exchange_keeps_two_lines(self):
        seg = segment([
            entry("unit.acted", {"Unit": ref("a", "甲")}),
            entry("unit.damage.dealt", {"Unit": ref("a", "甲"), "Card": ref("b", "乙"), "Amount": 2}),
            entry("unit.acted", {"Unit": ref("b", "乙")}),
            entry("unit.damage.dealt", {"Unit": ref("b", "乙"), "Card": ref("a", "甲"), "Amount": 1}),
        ])
        lines = render.narrate_segment(seg, ctx(self_uids=["a"], enemy_uids=["b"]))
        self.assertEqual(len(lines), 2)
        self.assertTrue(lines[0].startswith("你："))
        self.assertTrue(lines[1].startswith("对手："))

    def test_hidden_payload_uses_fixed_sentence(self):
        seg = segment([
            entry("card.drawn", {"Player": {"$type": "P"}, "Card": {"$hidden": True}}),
        ])
        lines = render.narrate_segment(seg, ctx())
        self.assertEqual(lines, ["对手：一次不可见的变化"])

    def test_attach_and_debug_dropped_without_placeholder(self):
        seg = segment([
            entry("x", kind="attach", level="debug", message="子执行流挂载"),
            entry("y", kind="update", level="debug", message="unit.foo"),
        ])
        self.assertEqual(render.narrate_segment(seg, ctx()), [])

    def test_unknown_event_degrades_with_raw_name(self):
        seg = segment([entry("unit.foo", {"Unit": ref("a", "甲")})])
        lines = render.narrate_segment(seg, ctx())
        self.assertTrue(lines[0].startswith("[unit.foo]"))

    def test_turn_start_line(self):
        seg = segment([entry("turn.start", {"TurnNumber": 3})])
        lines = render.narrate_segment(seg, ctx())
        self.assertIn("—— 第 3 回合开始 ——", lines)

    def test_point_delta_merged(self):
        seg = segment([
            entry("point.gained", {"Amount": 2}),
            entry("point.changed", {"NewPoints": 5}),
            entry("point.lost", {"Amount": 1}),
        ])
        lines = [line for line in render.narrate_segment(seg, ctx()) if line.strip()]
        self.assertEqual(len(lines), 1)
        self.assertIn("指挥点 +2-1", lines[0])

    def test_stat_changed_alone(self):
        seg = segment([entry("card.stat.changed", {"Card": ref("a", "甲")})])
        lines = [line for line in render.narrate_segment(seg, ctx()) if line.strip()]
        self.assertEqual(lines, ["甲 的属性发生变化"])

    def test_verbose_appends_sequence(self):
        seg = segment([entry("card.leaving", {"Card": ref("a", "甲")})], seq=12)
        lines = render.narrate_segment(seg, ctx(verbose=True))
        self.assertIn("(seq=12.0)", lines[0])

    def test_log_error_marked(self):
        seg = segment([entry("e", kind="log", level="error", message="出错了",
                             keywords=["exception:InvalidOperationException"])])
        lines = render.narrate_segment(seg, ctx())
        self.assertIn("⚠ 出错了", lines[0])
        self.assertIn("InvalidOperationException", lines[0])


class AlignmentTest(unittest.TestCase):
    def test_hand_rows_same_width(self):
        hand = [
            fake.make_card("u1", "步兵第 3 团", "unit", cost=1),
            fake.make_card("o1", "命令卡", "order", cost=2, blocked=["指挥点不足"]),
        ]
        session = Session(mode="play")
        session.apply_frame("notify/view", fake.seat_frame(fake.make_view(hand=hand),
                                                           fake.make_actions(playable=["u1"])))
        rows = render.hand_rows(session.snapshot())
        # 列对齐：类型列之前的显示宽度必须一致（尾部备注允许不等长）
        offsets = set()
        for row, type_text in zip(rows, ("指令", "单位")):
            index = row.find(type_text)
            offsets.add(render.dw(row[:index + len(type_text)]))
        self.assertEqual(len(offsets), 1, offsets)

    def test_help_table_aligned(self):
        rows = [("play", "出牌", "play <手牌#>", "打出第 N 张手牌", "对局动作"),
                ("end", "结束回合", "end", "结束当前回合", "对局动作")]
        lines = render.help_table(rows)
        data = [line for line in lines if line.startswith(("play", "end"))]
        offsets = set()
        for line in data:
            index = line.find("打出" if "打出" in line else "结束当前")
            offsets.add(render.dw(line[:index]))
        self.assertEqual(len(offsets), 1, offsets)

    def test_wide_char_width(self):
        self.assertEqual(render.dw("abc"), 3)
        self.assertEqual(render.dw("出牌"), 4)
        self.assertEqual(render.pad("出牌", 6), "出牌  ")


class LayoutTest(unittest.TestCase):
    def test_result_line(self):
        view = {"outcome": "Lose", "resultReason": "Concede", "concedingSide": "Self",
                "finalTurn": 12, "resultTitle": "失败"}
        self.assertEqual(render.result_line(view), "—— 失败 · 因你认输 · 第 12 回合 ——")

    def test_result_line_without_outcome_does_not_say_draw(self):
        view = {"outcome": "None", "resultReason": "Aborted", "finalTurn": 3}
        line = render.result_line(view)
        self.assertIn("对局终止", line)
        self.assertNotIn("平局", line)

    def test_watch_title_is_neutral(self):
        session = Session(mode="watch")
        session.apply_frame("notify/view", fake.omniscient_frame(
            fake.make_view(active="enemy",
                           self_line=[fake.make_card("a1", "A 步兵", zone="frontline", slot=0)]),
            fake.make_view(active="self",
                           self_line=[fake.make_card("b1", "B 装甲", zone="frontline", slot=1)],
                           hand=[fake.make_card("b2", "B 手牌")]),
        ))
        lines = render.render(session.snapshot())
        head = "\n".join(lines)
        self.assertIn("轮到 B", lines[0])
        self.assertNotIn("我方", head)
        self.assertNotIn("敌方", head)
        self.assertEqual(head.count("前线（共享）"), 1)
        self.assertIn("B1. B 手牌", head)

    def test_watch_single_side_marked(self):
        session = Session(mode="watch")
        session.apply_frame("notify/view", {
            "perspective": {"mode": "omniscient"},
            "views": [{"seat": 0, "view": fake.make_view(), "actions": fake.make_actions()}],
        })
        lines = render.render(session.snapshot())
        self.assertIn("单侧数据", lines[0])

    def test_play_notice_mulligan(self):
        session = Session(mode="play")
        session.apply_frame("notify/view", fake.seat_frame(
            fake.make_view(phase="mulligan", hand=[fake.make_card("h", "手牌")]),
            fake.make_actions(mulligan_open=True)))
        lines = render.render(session.snapshot())
        self.assertTrue(any("keep=直接开始" in line for line in lines))

    def test_unknown_notice(self):
        session = Session(mode="play")
        session.mark_unknown(True)
        lines = render.render(session.snapshot())
        self.assertTrue(any("结果未知" in line for line in lines))

    def test_gap_label_uses_neighbours(self):
        session = Session(mode="play")
        session.apply_frame("notify/view", fake.seat_frame(fake.make_view(
            self_line=[fake.make_card("s1", "甲", zone="support", slot=0),
                       fake.make_card("s2", "乙", zone="support", slot=1)])))
        pending = fake.make_pending()
        rows = render.candidate_rows(pending, session.snapshot())
        self.assertIn("插入位置 1：最左", rows[0])
        self.assertIn("在 甲 与 乙 之间", rows[1])
        self.assertNotIn("Formation gap", "\n".join(rows))


if __name__ == "__main__":
    unittest.main()
