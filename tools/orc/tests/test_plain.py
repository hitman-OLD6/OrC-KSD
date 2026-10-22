"""plain Front 端到端（S13）：喂 stdin / 抓 stdout，无需真实服务端。"""

import argparse
import io
import unittest

from orc import commands, front_plain
from orc.session import Session
from orc.tests import fake


class StubRuntime(object):
    def __init__(self, session):
        self.session = session
        self.records = 0
        self.shutdown_called = False

    def shutdown(self, game=None, reason="exit"):
        self.shutdown_called = True
        return game.exit_code if game and game.exit_code is not None else 0

    def touch_record(self):
        self.records += 1

    def reconnect(self):
        raise RuntimeError("测试环境不重连")


def build_game(session, dispatcher, context=None):
    from orc.cli import Game
    opts = argparse.Namespace(mode=session.mode, plain=True, stream=False, verbose=False,
                              keep_instance=False)
    engine = commands.CommandEngine(session, dispatcher, context=context or {})
    return Game(session, dispatcher, engine, StubRuntime(session), opts, io.StringIO())


def mulligan_session(hand=None):
    session = Session(mode="play")
    session.apply_frame("notify/view", fake.seat_frame(
        fake.make_view(phase="mulligan", hand=hand or [fake.make_card("h1", "步兵第 3 团")]),
        fake.make_actions(mulligan_open=True)))
    return session


class PlainEndToEndTest(unittest.TestCase):
    def test_keep_then_quit(self):
        session = mulligan_session()
        dispatcher = fake.FakeDispatcher({"mulligan.done": [fake.ok_result()]})
        game = build_game(session, dispatcher)
        out = io.StringIO()
        front = front_plain.PlainFront(io.StringIO("keep\nquit\n"), out)
        front.attach(game)
        code = front.run()
        text = out.getvalue()
        self.assertEqual(code, 0)
        self.assertEqual(dispatcher.names(), ["mulligan.done"])
        self.assertIn("换牌阶段", text)
        self.assertIn("已确认（keep）", text)
        self.assertIn("keep=直接开始", text)

    def test_eof_with_pending_cancels_and_exits_nonzero(self):
        session = mulligan_session()
        session.set_pending(fake.make_pending("s-9"))
        dispatcher = fake.FakeDispatcher({"target.cancel": [fake.ok_result("cancelled")]})
        game = build_game(session, dispatcher)
        out = io.StringIO()
        front = front_plain.PlainFront(io.StringIO(""), out)
        front.attach(game)
        code = front.run()
        self.assertEqual(code, 3)
        self.assertIn("target.cancel", dispatcher.names())
        self.assertIsNone(session.pending)

    def test_guide_prints_candidates(self):
        session = Session(mode="play")
        session.apply_frame("notify/view", fake.seat_frame(
            fake.make_view(hand=[fake.make_card("h1", "步兵第 3 团")]),
            fake.make_actions(playable=["h1"])))
        dispatcher = fake.FakeDispatcher()
        game = build_game(session, dispatcher)
        out = io.StringIO()
        front = front_plain.PlainFront(io.StringIO("play\n"), out)
        front.attach(game)
        front.run()
        text = out.getvalue()
        self.assertIn("步兵第 3 团", text)
        self.assertIn("用法：play", text)
        self.assertEqual(dispatcher.calls, [])

    def test_guide_candidates_match_hand_output(self):
        hand = [fake.make_card("h1", "步兵第 3 团"), fake.make_card("h2", "打不出的卡", blocked=["指挥点不足"])]
        session = Session(mode="play")
        session.apply_frame("notify/view", fake.seat_frame(
            fake.make_view(hand=hand), fake.make_actions(playable=["h1"])))
        dispatcher = fake.FakeDispatcher()
        guide_engine = commands.CommandEngine(session, dispatcher)
        guide = [line.strip() for line in guide_engine.execute("play").lines
                 if line.strip().startswith(("1.", "2."))]
        hand_engine = commands.CommandEngine(session, dispatcher)
        listing = [line.strip() for line in hand_engine.execute("hand").lines
                   if line.strip().startswith(("1.", "2."))]
        self.assertEqual(guide, listing)
        self.assertEqual(len(guide), 2)

    def test_quit_in_match_needs_confirmation(self):
        session = Session(mode="play")
        session.apply_frame("notify/view", fake.seat_frame(
            fake.make_view(hand=[fake.make_card("h1", "手牌")])))
        dispatcher = fake.FakeDispatcher()
        game = build_game(session, dispatcher)
        out = io.StringIO()
        front = front_plain.PlainFront(io.StringIO("quit\n"), out)
        front.attach(game)
        front.run()
        self.assertIn("再输入一次 quit", out.getvalue())
        self.assertFalse(game.engine.confirm is None)


class ExitCodeTest(unittest.TestCase):
    def test_last_failure_sets_exit_code(self):
        session = Session(mode="play")
        session.apply_frame("notify/view", fake.seat_frame(fake.make_view()))
        dispatcher = fake.FakeDispatcher()
        game = build_game(session, dispatcher)
        out = io.StringIO()
        front = front_plain.PlainFront(io.StringIO("nosuchcommand\n"), out)
        front.attach(game)
        code = front.run()
        self.assertEqual(code, 1)


if __name__ == "__main__":
    unittest.main()
