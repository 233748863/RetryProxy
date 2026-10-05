"""验收断言自身的确定性回归，保证字段丢失不会被静默放过。"""
import contextlib
import io
import json
import sqlite3
import tempfile
import unittest
from pathlib import Path

from config_storage_probe import read_config, subset, verify_identity, verify_import


class ConfigStorageProbeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="RetryProxyConfigProbe-")
        self.root = Path(self.temp.name)
        self.db = self.root / "config.db"
        self.source = self.root / "source.json"
        self.config = dict(proxy=dict(selected_route_id="r", routes=[dict(id="r")], providers=[]),
                           commonConfig=dict(currentThemeType=0), otherConfig={}, preparations=[])
        self.write(self.config)

    def tearDown(self):
        self.temp.cleanup()

    def write(self, config):
        with contextlib.closing(sqlite3.connect(self.db)) as db, db:
            db.execute("CREATE TABLE IF NOT EXISTS config (key TEXT PRIMARY KEY, value TEXT)")
            for key, node in [("proxy", "proxy"), ("common", "commonConfig"),
                              ("other", "otherConfig"), ("preparations", "preparations")]:
                db.execute("INSERT OR REPLACE INTO config VALUES (?, ?)", (key, json.dumps(config[node])))
        self.source.write_text(json.dumps(config), encoding="utf-8")

    def test_read_maps_all_nodes(self):
        self.assertEqual(read_config(self.db), self.config)

    def test_missing_database_is_not_created(self):
        missing = self.root / "missing.db"
        with self.assertRaises(sqlite3.OperationalError):
            read_config(missing)
        self.assertFalse(missing.exists())

    def test_missing_node_is_rejected(self):
        with contextlib.closing(sqlite3.connect(self.db)) as db, db:
            db.execute("DELETE FROM config WHERE key = 'preparations'")
        with self.assertRaises(AssertionError):
            read_config(self.db)

    def test_subset_allows_new_defaults_and_omitted_null(self):
        subset(dict(id="r", optional=None), dict(id="r", new_default=False))

    def test_changed_secret_fails_without_disclosure(self):
        with self.assertRaises(AssertionError) as failure:
            subset(dict(api_key="fixture-secret-a"), dict(api_key="fixture-secret-b"))
        self.assertNotIn("fixture-secret", str(failure.exception))

    def test_import_requires_identical_archived_source_and_backup(self):
        archived = self.root / "config.json.migrated.bak"
        archived.write_bytes(self.source.read_bytes())
        backup = self.root / "backup"
        backup.mkdir()
        with self.assertRaises(AssertionError):
            verify_import(self.db, self.source)
        (backup / "config_fixture.json.bak").write_bytes(self.source.read_bytes())
        with contextlib.redirect_stdout(io.StringIO()):
            verify_import(self.db, self.source)
        archived.write_text("{}", encoding="utf-8")
        with self.assertRaises(AssertionError):
            verify_import(self.db, self.source)

    def test_live_import_allows_autostart_but_not_port_changes(self):
        route = self.config["proxy"]["routes"][0]
        route.update(desired_running=False, listen_port=30123)
        self.write(self.config)
        original = self.source.read_bytes()
        route["desired_running"] = True
        self.write(self.config)
        self.source.write_bytes(original)
        (self.root / "config.json.migrated.bak").write_bytes(original)
        (self.root / "backup").mkdir()
        (self.root / "backup/config_fixture.json.bak").write_bytes(original)
        with contextlib.redirect_stdout(io.StringIO()):
            verify_import(self.db, self.source)
        route["listen_port"] = 30124
        self.write(self.config)
        self.source.write_bytes(original)
        with self.assertRaises(AssertionError):
            verify_import(self.db, self.source)

    def test_identity_rejects_missing_route(self):
        original = self.root / "original.json"
        original.write_bytes(self.source.read_bytes())
        self.config["proxy"]["routes"] = []
        self.write(self.config)
        with self.assertRaises(AssertionError):
            verify_identity(self.db, original)


if __name__ == "__main__":
    unittest.main()
