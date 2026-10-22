"""传输层与 Dispatcher 的确定性断言（S1/S13）。"""

import unittest

from orc import timeouts, transport
from orc.tests import fake


class PendingDedupTest(unittest.TestCase):
    def setUp(self):
        self.dispatcher = transport.SocketDispatcher("127.0.0.1", 1)
        self.seen = []
        self.dispatcher.on_pending = lambda pending: self.seen.append(pending)

    def test_same_session_id_kept_once(self):
        pending = fake.make_pending("s-1")
        self.dispatcher._ingest_pending(pending)
        self.dispatcher._ingest_pending(dict(pending))
        self.assertEqual(len(self.dispatcher._pending), 1)
        self.assertEqual(self.dispatcher.peek_pending()["sessionId"], "s-1")

    def test_expired_clears(self):
        self.dispatcher._ingest_pending(fake.make_pending("s-1"))
        self.dispatcher._expire_pending({"sessionId": "s-1"})
        self.assertIsNone(self.dispatcher.peek_pending())
        self.assertIsNone(self.seen[-1])

    def test_late_response_goes_to_orphans(self):
        self.dispatcher._resolve({"jsonrpc": "2.0", "id": "99", "result": {"ok": True}})
        self.assertIn("99", self.dispatcher._orphans)


class RetryPolicyTest(unittest.TestCase):
    def setUp(self):
        self._saved = timeouts.ROOM_ROTATING_RETRY_MS
        timeouts.ROOM_ROTATING_RETRY_MS = (1, 1, 1)
        timeouts.BUSY_RETRY = 0.0

    def tearDown(self):
        timeouts.ROOM_ROTATING_RETRY_MS = self._saved
        timeouts.BUSY_RETRY = 2.0

    def test_room_rotating_retried(self):
        dispatcher = fake.FakeDispatcher({"match.end_turn": [
            fake.fail_result("room-rotating"),
            fake.ok_result(),
        ]})
        result = dispatcher.call_tool("match.end_turn", {})
        self.assertTrue(result.ok)
        self.assertEqual(len(dispatcher.calls), 2)

    def test_busy_retried_once(self):
        dispatcher = fake.FakeDispatcher({"match.end_turn": [
            fake.fail_result("busy"),
            fake.ok_result(),
        ]})
        result = dispatcher.call_tool("match.end_turn", {})
        self.assertTrue(result.ok)
        self.assertEqual(len(dispatcher.calls), 2)

    def test_engine_rejected_not_retried(self):
        dispatcher = fake.FakeDispatcher({"play.unit_pre": [fake.fail_result("engine-rejected")]})
        result = dispatcher.call_tool("play.unit_pre", {"ref": "x"})
        self.assertFalse(result.ok)
        self.assertEqual(len(dispatcher.calls), 1)

    def test_unknown_code_not_retried(self):
        dispatcher = fake.FakeDispatcher({"x.y": [fake.fail_result("weird-code")]})
        dispatcher.call_tool("x.y", {})
        self.assertEqual(len(dispatcher.calls), 1)


class ToolResultShapeTest(unittest.TestCase):
    def test_targeting_carries_pending(self):
        result = transport.ToolResult(fake.pending_result(fake.make_pending("s")))
        self.assertTrue(result.ok)
        self.assertEqual(result.code, "targeting")
        self.assertEqual(result.pending["sessionId"], "s")
        self.assertTrue(result.is_targeting)

    def test_pending_targeting_is_not_error(self):
        payload = fake.fail_result("pending-targeting", details={"pending": fake.make_pending("s")})
        result = transport.ToolResult(payload)
        self.assertFalse(result.ok)
        self.assertEqual(result.pending["sessionId"], "s")


if __name__ == "__main__":
    unittest.main()
