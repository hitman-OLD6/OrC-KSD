"""CommandEngine 的确定性断言（S5/S6/S13）：调用序列、非法序号、门禁、别名。"""

import unittest

from orc import commands
from orc.session import Session
from orc.tests import fake


def build(session, script=None, context=None):
    dispatcher = fake.FakeDispatcher(script)
    engine = commands.CommandEngine(session, dispatcher, context=context or {})
    return engine, dispatcher


def play_session(hand=None, self_line=None, playable=None, phase="play", known=True):
    session = Session(mode="play", seat=0)
    view = fake.make_view(phase=phase, hand=hand, self_line=self_line,
                          active="self" if phase == "play" else "self")
    actions = fake.make_actions(playable=playable, mulligan_open=(phase == "mulligan"), known=known)
    session.apply_frame("notify/view", fake.seat_frame(view, actions))
    return session


class PlayTwoStepTest(unittest.TestCase):
    def setUp(self):
        self.unit = fake.make_card("u1", "步兵第 3 团", "unit", cost=1)
        self.session = play_session(hand=[self.unit], playable=["u1"])
        self.pending = fake.make_pending("s-1")
        self.engine, self.dispatcher = build(self.session, {
            "play.unit_pre": [fake.pending_result(self.pending)],
        })

    def test_pre_then_submit(self):
        result = self.engine.execute("play 1")
        self.assertEqual(self.dispatcher.names(), ["play.unit_pre"])
        self.assertEqual(self.dispatcher.calls[0][1], {"ref": "u1"})
        self.assertEqual(self.session.pending["sessionId"], "s-1")
        self.assertFalse(result.failed)

        self.engine.execute("1")
        self.assertEqual(self.dispatcher.names(), ["play.unit_pre"])
        self.engine.execute("ok")
        self.assertEqual(self.dispatcher.names(), ["play.unit_pre", "target.submit"])
        self.assertEqual(self.dispatcher.args_of("target.submit"),
                         {"sessionId": "s-1", "picks": [1]})
        self.assertIsNone(self.session.pending)

    def test_inline_parameter_does_both_steps(self):
        self.engine.execute("play 1 2")
        self.assertEqual(self.dispatcher.names(), ["play.unit_pre", "target.submit"])
        self.assertEqual(self.dispatcher.args_of("target.submit")["picks"], [2])

    def test_invalid_inline_index_cancels_without_submit(self):
        result = self.engine.execute("play 1 9")
        self.assertEqual(self.dispatcher.names(), ["play.unit_pre", "target.cancel"])
        self.assertTrue(result.failed)
        self.assertTrue(any("超出范围" in line for line in result.lines))

    def test_gate_blocks_other_commands_while_pending(self):
        self.engine.execute("play 1")
        before = len(self.dispatcher.calls)
        result = self.engine.execute("end")
        self.assertEqual(len(self.dispatcher.calls), before)
        self.assertTrue(any("请先完成选择" in line for line in result.lines))

    def test_cancel_clears_pending(self):
        self.engine.execute("play 1")
        self.engine.execute("x")
        self.assertIsNone(self.session.pending)
        self.assertIn("target.cancel", self.dispatcher.names())


class DispatchByCardTypeTest(unittest.TestCase):
    def test_order_goes_to_command_pre(self):
        order = fake.make_card("o1", "命令卡", "order", cost=1)
        session = play_session(hand=[order], playable=["o1"])
        engine, dispatcher = build(session, {"play.command_pre": [fake.ok_result()]})
        engine.execute("play 1")
        self.assertEqual(dispatcher.names(), ["play.command_pre"])

    def test_counter_has_no_selection_step(self):
        counter = fake.make_card("c1", "反制卡", "counter", cost=1)
        session = play_session(hand=[counter], playable=["c1"])
        engine, dispatcher = build(session, {"play.counter": [fake.ok_result()]})
        engine.execute("play 1")
        self.assertEqual(dispatcher.names(), ["play.counter"])

    def test_counter_ignores_extra_parameter_with_warning(self):
        counter = fake.make_card("c1", "反制卡", "counter", cost=1)
        session = play_session(hand=[counter], playable=["c1"])
        engine, dispatcher = build(session, {"play.counter": [fake.ok_result()]})
        result = engine.execute("play 1 3")
        self.assertEqual(dispatcher.names(), ["play.counter"])
        self.assertTrue(any("忽略" in line for line in result.lines))

    def test_unplayable_card_blocked_locally(self):
        unit = fake.make_card("u1", "贵单位", "unit", cost=9, blocked=["费用不足"])
        session = play_session(hand=[unit], playable=[])
        engine, dispatcher = build(session)
        result = engine.execute("play 1")
        self.assertEqual(dispatcher.calls, [])
        self.assertTrue(any("费用不足" in line for line in result.lines))

    def test_unknown_playability_does_not_block(self):
        unit = fake.make_card("u1", "单位", "unit", cost=1)
        session = play_session(hand=[unit], playable=[], known=False)
        engine, dispatcher = build(session, {"play.unit_pre": [fake.ok_result()]})
        engine.execute("play 1")
        self.assertEqual(dispatcher.names(), ["play.unit_pre"])

    def test_hand_index_out_of_range(self):
        session = play_session(hand=[fake.make_card("u1", "单位")], playable=["u1"])
        engine, dispatcher = build(session)
        result = engine.execute("play 5")
        self.assertEqual(dispatcher.calls, [])
        self.assertTrue(any("1..1" in line for line in result.lines))


class PanelNumberingTest(unittest.TestCase):
    def setUp(self):
        # selfLine 顺序＝投影顺序（支援线在前、前线在后），编号必须按这个顺序
        self.front = fake.make_card("f1", "前线甲", zone="frontline", slot=0)
        self.support = fake.make_card("s1", "支援乙", zone="support", slot=0)
        self.session = play_session(
            hand=[fake.make_card("h1", "手牌卡")],
            self_line=[self.front, self.support],
            playable=["h1"],
        )

    def test_attack_uses_panel_order(self):
        engine, dispatcher = build(self.session, {"command.attack": [fake.ok_result()]})
        engine.execute("attack 1")
        self.assertEqual(dispatcher.args_of("command.attack"), {"ref": "s1"})
        engine.execute("attack 2")
        self.assertEqual(self.session.units()[1]["uid"], "f1")

    def test_out_of_range_echoes_count(self):
        engine, dispatcher = build(self.session)
        result = engine.execute("attack 9")
        self.assertEqual(dispatcher.calls, [])
        self.assertTrue(any("可用 1..2" in line for line in result.lines))

    def test_leave_calls_command_leave(self):
        engine, dispatcher = build(self.session, {"command.leave": [fake.ok_result()]})
        engine.execute("leave 1")
        self.assertEqual(dispatcher.names(), ["command.leave"])


class MulliganGateTest(unittest.TestCase):
    def setUp(self):
        self.session = play_session(hand=[fake.make_card("h1", "手牌")], phase="mulligan")
        self.engine, self.dispatcher = build(self.session, {"mulligan.done": [fake.ok_result()]})

    def test_play_rejected(self):
        result = self.engine.execute("play 1")
        self.assertEqual(self.dispatcher.calls, [])
        self.assertTrue(any("keep" in line for line in result.lines))

    def test_end_not_redirected(self):
        result = self.engine.execute("结束回合")
        self.assertEqual(self.dispatcher.calls, [])
        self.assertTrue(any("换牌阶段不能结束回合" in line for line in result.lines))

    def test_keep_calls_mulligan_done(self):
        self.engine.execute("keep")
        self.assertEqual(self.dispatcher.names(), ["mulligan.done"])
        self.assertEqual(self.dispatcher.args_of("mulligan.done"), {"owner": 0})

    def test_quit_has_no_confirmation_in_mulligan(self):
        result = self.engine.execute("quit")
        self.assertEqual(result.exit_code, 0)
        self.assertIsNone(self.engine.confirm)

    def test_x_without_pending_does_not_call(self):
        result = self.engine.execute("x")
        self.assertEqual(self.dispatcher.calls, [])
        self.assertTrue(any("keep" in line for line in result.lines))


class ConfirmFlowTest(unittest.TestCase):
    def setUp(self):
        self.session = play_session(hand=[fake.make_card("h1", "手牌")])
        self.engine, self.dispatcher = build(self.session, {"match.concede": [fake.ok_result()]})

    def test_quit_needs_second_word(self):
        result = self.engine.execute("quit")
        self.assertIsNone(result.exit_code)
        self.assertEqual(self.engine.confirm, "quit")
        cancelled = self.engine.execute("end")
        self.assertIsNone(cancelled.exit_code)
        self.assertEqual(self.engine.confirm, None)
        self.engine.execute("quit")
        confirmed = self.engine.execute("退出")
        self.assertEqual(confirmed.exit_code, 0)

    def test_concede_needs_confirmation(self):
        self.engine.execute("concede")
        self.assertEqual(self.dispatcher.calls, [])
        self.engine.execute("concede")
        self.assertEqual(self.dispatcher.names(), ["match.concede"])
        self.assertEqual(self.dispatcher.args_of("match.concede"), {"owner": 0})

    def test_keep_instance_text(self):
        engine, _ = build(self.session, {}, context={"keep_instance": True})
        result = engine.execute("quit")
        self.assertTrue(any("实例将保留" in line for line in result.lines))


class WatchGateTest(unittest.TestCase):
    def setUp(self):
        self.session = Session(mode="watch", seat=0)
        frame = fake.omniscient_frame()
        self.session.apply_frame("notify/view", frame)
        self.engine, self.dispatcher = build(self.session)

    def test_all_actions_rejected_with_zero_calls(self):
        for line in ("play 1", "attack 1", "move 1", "leave 1", "end", "concede",
                     "keep", "mulligan", "restart", "ok", "x"):
            result = self.engine.execute(line)
            self.assertTrue(result.failed, line)
            self.assertTrue(any("观战模式只读" in item for item in result.lines), line)
        self.assertEqual(self.dispatcher.calls, [])

    def test_quit_without_confirmation(self):
        result = self.engine.execute("quit")
        self.assertEqual(result.exit_code, 0)

    def test_hand_and_units_are_local(self):
        self.engine.execute("hand")
        self.engine.execute("units")
        self.engine.execute("log")
        self.assertEqual(self.dispatcher.calls, [])


class AliasesAndHelpTest(unittest.TestCase):
    def setUp(self):
        self.session = play_session(hand=[fake.make_card("h1", "手牌")])
        self.engine, self.dispatcher = build(self.session)

    def test_all_sixteen_commands_present(self):
        english = [row[0] for row in commands.COMMANDS]
        self.assertEqual(len(english), 16)
        for forbidden in ("order", "counter", "join", "begin", "command"):
            self.assertNotIn(forbidden, english)

    def test_chinese_aliases_share_path(self):
        self.assertEqual(self.engine.canonical("出牌"), "play")
        self.assertEqual(self.engine.canonical("PLAY"), "play")
        self.assertEqual(self.engine.canonical("攻击"), "attack")
        self.assertEqual(self.engine.canonical("结束回合"), "end")
        self.assertEqual(self.engine.canonical("?"), "help")

    def test_help_lists_chinese_and_english(self):
        result = self.engine.execute("help")
        text = "\n".join(result.lines)
        for verb in ("play", "出牌", "attack", "攻击", "end", "结束回合", "quit", "退出"):
            self.assertIn(verb, text)
        self.assertNotIn("opponent", text)
        self.assertNotIn("--token", text)

    def test_help_single_command(self):
        result = self.engine.execute("help 出牌")
        text = "\n".join(result.lines)
        self.assertIn("play", text)
        self.assertNotIn("attack", text)


class HandListingTest(unittest.TestCase):
    def test_unplayable_listed_after_playable_with_reason(self):
        hand = [fake.make_card("u1", "可打出"), fake.make_card("u2", "打不出", blocked=["指挥点不足"])]
        session = play_session(hand=hand, playable=["u1"])
        engine, dispatcher = build(session)
        result = engine.execute("hand")
        text = "\n".join(result.lines)
        self.assertIn("指挥点不足", text)
        self.assertEqual(dispatcher.calls, [])

    def test_units_listing_has_marks(self):
        unit = fake.make_card("u1", "步兵", zone="frontline", slot=0)
        session = play_session(self_line=[unit])
        session.actions = fake.make_actions(attack_previews=[{"attackerUid": "u1", "defenderUid": "e1"}])
        engine, dispatcher = build(session)
        result = engine.execute("units")
        self.assertTrue(any("⚔" in line for line in result.lines))
        self.assertEqual(dispatcher.calls, [])


class TimeoutTest(unittest.TestCase):
    def test_action_timeout_marks_unknown_without_retry(self):
        session = play_session(hand=[fake.make_card("h1", "手牌")])
        dispatcher = fake.FakeDispatcher()

        def boom(name, arguments, timeout, kind):
            dispatcher.calls.append((name, dict(arguments or {})))
            from orc.transport import CallTimeout
            raise CallTimeout(name, 1.0)

        dispatcher._invoke = boom
        engine = commands.CommandEngine(session, dispatcher)
        result = engine.execute("end")
        self.assertTrue(result.failed)
        self.assertTrue(session.unknown)
        self.assertEqual(len(dispatcher.calls), 1)


class CompletionTest(unittest.TestCase):
    def setUp(self):
        self.session = play_session(
            hand=[fake.make_card("h1", "步兵第 3 团"), fake.make_card("h2", "装甲")],
            self_line=[fake.make_card("u1", "甲", zone="support", slot=0)],
            playable=["h1"],
        )
        self.engine, self.dispatcher = build(self.session)

    def test_verb_prefix(self):
        values = [value for value, _ in self.engine.candidates_for("pl")]
        self.assertEqual(values, ["play "])

    def test_hand_candidates_contain_name_and_stats(self):
        candidates = self.engine.candidates_for("play ")
        self.assertEqual([value for value, _ in candidates], ["1", "2"])
        self.assertIn("步兵第 3 团", candidates[0][1])
        self.assertIn("3/3", candidates[0][1])

    def test_unit_candidates(self):
        candidates = self.engine.candidates_for("attack ")
        self.assertEqual([value for value, _ in candidates], ["1"])

    def test_second_argument_not_prefetched(self):
        self.assertEqual(self.engine.candidates_for("attack 1 "), [])

    def test_completion_does_not_execute(self):
        self.engine.candidates_for("play ")
        self.engine.candidates_for("attack ")
        self.assertEqual(self.dispatcher.calls, [])

    def test_log_flags(self):
        values = [value for value, _ in self.engine.candidates_for("log ")]
        self.assertEqual(values, ["--prev ", "--all "])


class RestartTest(unittest.TestCase):
    def test_restart_unavailable_before_rotation(self):
        session = play_session(hand=[fake.make_card("h1", "手牌")])
        engine, dispatcher = build(session, context={"restart": lambda: commands.ok(["重开"])})
        result = engine.execute("restart")
        self.assertTrue(result.failed)
        self.assertTrue(any("等待房间重建" in line for line in result.lines))
        self.assertEqual(dispatcher.calls, [])

    def test_restart_allowed_after_rotation(self):
        session = play_session(hand=[fake.make_card("h1", "手牌")])
        session.apply_frame("notify/room.rotated", {"roomId": "r2", "seatCount": 2, "epoch": 2})
        engine, dispatcher = build(session, context={"restart": lambda: commands.ok(["新一局"])})
        result = engine.execute("restart")
        self.assertFalse(result.failed)
        self.assertEqual(result.lines, ["新一局"])

    def test_ended_gate_only_allows_menu(self):
        session = play_session(hand=[fake.make_card("h1", "手牌")])
        view = fake.make_view()
        view["outcome"] = "Win"
        session.apply_frame("notify/view", fake.seat_frame(view))
        engine, dispatcher = build(session)
        for line in ("play 1", "end", "attack 1"):
            result = engine.execute(line)
            self.assertTrue(result.failed, line)
            self.assertTrue(any("对局已结束" in item for item in result.lines), line)
        self.assertEqual(dispatcher.calls, [])
        self.assertEqual(engine.execute("log").failed, False)


class GuideEscapeTest(unittest.TestCase):
    """引导里改敲完整指令，应退出引导并执行——不能被当成"非法序号"吞掉。"""

    def _engine(self):
        unit = fake.make_card("u1", "步兵第 3 团", "unit", cost=1)
        session = play_session(hand=[unit], self_line=[unit], playable=["u1"])
        return session, build(session, {"command.attack": [fake.ok_result()]})

    def test_bare_command_enters_guide_without_calling(self):
        session, (engine, dispatcher) = self._engine()
        result = engine.execute("attack")
        self.assertFalse(result.failed)
        self.assertEqual(engine.step, "attack")
        self.assertEqual(dispatcher.names(), [])
        self.assertTrue(any("我方单位" in line for line in result.lines))

    def test_full_command_in_guide_exits_and_runs(self):
        session, (engine, dispatcher) = self._engine()
        engine.execute("attack")
        result = engine.execute("attack 1")
        self.assertIsNone(engine.step)
        self.assertFalse(result.failed)
        self.assertEqual(dispatcher.names(), ["command.attack"])
        self.assertEqual(dispatcher.args_of("command.attack"), {"ref": "u1"})

    def test_read_only_command_in_guide_exits(self):
        session, (engine, dispatcher) = self._engine()
        engine.execute("attack")
        result = engine.execute("help")
        self.assertIsNone(engine.step)
        self.assertFalse(result.failed)
        self.assertEqual(dispatcher.calls, [])


if __name__ == "__main__":
    unittest.main()
