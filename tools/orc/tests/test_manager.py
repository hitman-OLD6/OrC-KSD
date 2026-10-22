"""管理器面与卡包/卡组（S2/S11）：只测纯逻辑，不启进程。"""

import json
import os
import unittest

from orc import cli, manager


REPO = manager.find_repo_root()
AI_PACK = os.path.join(REPO, "samples", "card-pack-ai")
DEMO_PACK = os.path.join(REPO, "samples", "card-pack-demo")


class PackTest(unittest.TestCase):
    def test_ai_pack_ids_are_prefixed_and_sorted(self):
        pack = manager.load_pack(AI_PACK)
        self.assertEqual(pack.package_id, "ai.sample")
        self.assertEqual(len(pack.asset_ids), 5)
        self.assertEqual(pack.asset_ids, sorted(pack.asset_ids))
        self.assertEqual(pack.card_ids[0], "pack/ai.sample/ai.sample.armor")

    def test_deck_is_39_and_deterministic(self):
        pack = manager.load_pack(AI_PACK)
        deck = pack.deck()
        self.assertEqual(len(deck), 39)
        self.assertEqual(deck, manager.load_pack(AI_PACK).deck())
        self.assertEqual(deck[:5], pack.card_ids)
        self.assertEqual(deck[5], pack.card_ids[0])

    def test_recursive_scan_finds_cards_support(self):
        pack = manager.load_pack(DEMO_PACK)
        self.assertTrue(any("scout" in asset for asset in pack.asset_ids), pack.asset_ids)

    def test_empty_pack_reported(self):
        empty = os.path.join(REPO, "outputs", "cli-probe", "empty-pack")
        if not os.path.isdir(empty):
            os.makedirs(empty)
        with open(os.path.join(empty, "manifest.json"), "w", encoding="utf-8") as handle:
            json.dump({"schemaVersion": 2, "id": "empty.pack", "version": "1", "guid": "g"}, handle)
        pack = manager.load_pack(empty)
        self.assertEqual(pack.asset_ids, [])
        self.assertEqual(pack.deck(), [])

    def test_missing_manifest_raises(self):
        with self.assertRaises(manager.CliError):
            manager.load_pack(os.path.join(REPO, "samples", "no-such-pack"))

    def test_deck_file_validation(self):
        path = os.path.join(REPO, "outputs", "cli-probe", "deck.json")
        with open(path, "w", encoding="utf-8") as handle:
            json.dump(["a", "b"], handle)
        self.assertEqual(manager.load_deck_file(path), ["a", "b"])
        with open(path, "w", encoding="utf-8") as handle:
            json.dump({"not": "array"}, handle)
        with self.assertRaises(manager.CliError):
            manager.load_deck_file(path)


class RecordTest(unittest.TestCase):
    def setUp(self):
        self.path = os.path.join(REPO, "outputs", "cli-probe", "session-test.json")

    def tearDown(self):
        manager.delete_record(self.path)

    def test_roundtrip_and_delete(self):
        record = manager.save_record(self.path, {
            "cliSessionId": "abc",
            "instanceId": "cli-12345678",
            "label": "cli:12345678",
            "manager": "127.0.0.1:52001",
            "port": 50000,
            "roomId": "r1",
            "seat": 0,
            "seatToken": "t",
            "packDirectories": [AI_PACK],
            "deck": ["x"],
        })
        self.assertEqual(record["schemaVersion"], 1)
        loaded = manager.load_record(self.path)
        self.assertEqual(loaded["instanceId"], "cli-12345678")
        self.assertEqual(loaded["manager"], "127.0.0.1:52001")
        manager.delete_record(self.path)
        self.assertIsNone(manager.load_record(self.path))

    def test_garbage_record_ignored(self):
        with open(self.path, "w", encoding="utf-8") as handle:
            handle.write("{not json")
        self.assertIsNone(manager.load_record(self.path))


class ArgsTest(unittest.TestCase):
    def test_usage_has_no_opponent_options(self):
        text = cli.build_parser().format_help()
        for word in ("opponent", "--token", "connect", "对手连接"):
            self.assertNotIn(word, text)

    def test_play_options(self):
        opts = cli.parse(["play", "--manager", "127.0.0.1:52001", "--plain", "--keep-instance"])
        self.assertEqual(opts.mode, "play")
        self.assertEqual(opts.manager, "127.0.0.1:52001")
        self.assertTrue(opts.keep_instance)
        self.assertTrue(opts.plain)

    def test_watch_options(self):
        opts = cli.parse(["watch", "--instance", "cli-abc"])
        self.assertEqual(opts.mode, "watch")
        self.assertEqual(opts.instance, "cli-abc")

    def test_endpoint_parsing(self):
        client = manager.ManagerClient("127.0.0.1:52001")
        self.assertEqual(client.endpoint, "127.0.0.1:52001")
        with self.assertRaises(manager.CliError):
            manager.ManagerClient("127.0.0.1")


if __name__ == "__main__":
    unittest.main()
