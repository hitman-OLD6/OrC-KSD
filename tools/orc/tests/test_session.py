"""Session 状态仓库（S4）：分帧入库、按局分段日志、轮换清理。"""

import unittest

from orc.session import Session
from orc.tests import fake


class FrameTest(unittest.TestCase):
    def test_seat_frame_updates_fields(self):
        session = Session(mode="play")
        session.apply_frame("notify/view", fake.seat_frame(
            fake.make_view(turn=4, phase="play", kredits=5), fake.make_actions(playable=["u1"])))
        snap = session.snapshot()
        self.assertEqual(snap.turn, 4)
        self.assertEqual(snap.phase, "play")
        self.assertEqual(snap.actions["playableUids"], ["u1"])
        self.assertEqual(snap.view["selfKredits"], 5)

    def test_omniscient_frame_fills_both_seats(self):
        session = Session(mode="watch")
        session.apply_frame("notify/view", fake.omniscient_frame())
        self.assertEqual(sorted(session.views.keys()), [0, 1])
        self.assertIsNotNone(session.view)

    def test_pending_expired_clears(self):
        session = Session(mode="play")
        session.set_pending(fake.make_pending("s"))
        session.apply_frame("notify/target.expired", {"sessionId": "s"})
        session.set_pending(None)   # 读线程的 on_pending(None) 也是这条路径
        self.assertIsNone(session.snapshot().pending)

    def test_unknown_cleared_when_view_changes(self):
        session = Session(mode="play")
        session.apply_frame("notify/view", fake.seat_frame(fake.make_view(turn=1)))
        session.mark_unknown(True)
        session.apply_frame("notify/view", fake.seat_frame(fake.make_view(turn=2)))
        self.assertFalse(session.unknown)

    def test_end_view_marks_ended(self):
        session = Session(mode="play")
        view = fake.make_view()
        view["outcome"] = "Win"
        view["resultTitle"] = "胜利"
        view["resultReason"] = "hq-zero"
        view["finalTurn"] = 9
        session.apply_frame("notify/view", fake.seat_frame(view))
        self.assertTrue(session.ended)
        self.assertEqual(session.final_view["outcome"], "Win")


class LogBookTest(unittest.TestCase):
    def test_per_room_segments(self):
        session = Session(mode="play")
        session.room_id = "a"
        session.add_log(["a1", "a2"])
        session.room_id = "b"
        session.add_log(["b1"])
        self.assertEqual(session.logs.current("b"), ["b1"])
        self.assertEqual(session.logs.previous("b"), ["a1", "a2"])
        entries = session.logs.interleaved(["a", "b"], limit=10)
        self.assertEqual([text for _, _, text in entries], ["a1", "a2", "b1"])

    def test_capacity(self):
        session = Session(mode="play")
        session.room_id = "a"
        session.add_log([str(index) for index in range(250)])
        self.assertEqual(len(session.logs.current("a", limit=0)), 200)

    def test_since_cursor(self):
        session = Session(mode="play")
        session.room_id = "a"
        session.add_log(["one"])
        cursor = session.logs.last_index()
        session.add_log(["two", "three"])
        entries = session.logs.since(cursor)
        self.assertEqual([text for _, _, text in entries], ["two", "three"])


class RotateTest(unittest.TestCase):
    def test_rotation_clears_screen_state(self):
        session = Session(mode="play")
        session.apply_frame("notify/view", fake.seat_frame(fake.make_view()))
        session.set_pending(fake.make_pending("s"))
        session.mark_unknown(True)
        session.apply_frame("notify/room.rotated", {"roomId": "new-room", "seatCount": 2, "epoch": 2})
        snap = session.snapshot()
        self.assertTrue(snap.ending)
        self.assertIsNone(snap.pending)
        self.assertFalse(snap.unknown)
        self.assertEqual(snap.room_id, "new-room")

    def test_final_view_survives_rotation(self):
        session = Session(mode="play")
        view = fake.make_view()
        view["outcome"] = "Lose"
        view["resultTitle"] = "失败"
        session.apply_frame("notify/view", fake.seat_frame(view))
        session.apply_frame("notify/room.rotated", {"roomId": "r2", "seatCount": 2, "epoch": 2})
        snap = session.snapshot()
        self.assertIsNotNone(snap.final_view)
        self.assertEqual(snap.final_view["outcome"], "Lose")

    def test_rotated_reset(self):
        session = Session(mode="play")
        session.apply_frame("notify/room.rotated", {"roomId": "r2", "seatCount": 2, "epoch": 2})
        session.apply_rotated_reset("r3")
        snap = session.snapshot()
        self.assertFalse(snap.ending)
        self.assertIsNone(snap.rotated)
        self.assertEqual(snap.room_id, "r3")


if __name__ == "__main__":
    unittest.main()
