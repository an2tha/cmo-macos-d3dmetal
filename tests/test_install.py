import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock


SPEC = importlib.util.spec_from_file_location("installer", Path(__file__).resolve().parents[1] / "scripts/install.py")
installer = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(installer)


class InstallationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.game = self.root / "Game with spaces"
        self.game.mkdir()
        self.original = b"MZoriginal-game"
        self.configuration = b'\xef\xbb\xbf<?xml version="1.0" encoding="utf-8"?>\r\n<configuration><runtime><assemblyBinding xmlns="urn:schemas-microsoft-com:asm.v1" /></runtime></configuration>\r\n'
        (self.game / "Command.exe").write_bytes(self.original)
        (self.game / "Command.exe.config").write_bytes(self.configuration)
        (self.game / "DirectN.dll").write_bytes(b"vendor-library")
        self.source = self.root / "CommandMetal.exe"
        self.source.write_bytes(b"MZnew-adapter")
        self.browser = self.root / "CommandBrowser.exe"
        self.browser.write_bytes(b"MZbrowser-bootstrap")
        self.processes = mock.patch.object(installer, "running_games", return_value=[])
        self.processes.start()
        self.addCleanup(self.processes.stop)

    def assert_vendor_unchanged(self):
        self.assertEqual((self.game / "Command.exe").read_bytes(), self.original)
        self.assertEqual((self.game / "Command.exe.config").read_bytes(), self.configuration)
        self.assertEqual((self.game / "DirectN.dll").read_bytes(), b"vendor-library")

    def test_fresh_install_preserves_configuration_bytes_and_records_absent_files(self):
        backup = installer.install(self.source, self.game)
        self.assertEqual((self.game / "CommandMetal.exe").read_bytes(), self.source.read_bytes())
        self.assertEqual((self.game / "CommandMetal.exe.config").read_bytes(), self.configuration)
        self.assertEqual((self.game / "CommandBrowser.exe").read_bytes(), self.browser.read_bytes())
        self.assertEqual((self.game / "CommandBrowser.exe.config").read_bytes(), self.configuration)
        manifest = json.loads((backup / "manifest.json").read_text())
        self.assertTrue(all(not item["existed"] for item in manifest["files"].values()))
        self.assert_vendor_unchanged()

    def test_existing_launcher_and_configuration_are_backed_up_exactly(self):
        (self.game / "CommandMetal.exe").write_bytes(b"old-adapter")
        (self.game / "CommandMetal.exe.config").write_bytes(b"old-config")
        backup = installer.install(self.source, self.game)
        self.assertEqual((backup / "CommandMetal.exe").read_bytes(), b"old-adapter")
        self.assertEqual((backup / "CommandMetal.exe.config").read_bytes(), b"old-config")
        self.assertTrue(json.loads((backup / "manifest.json").read_text())["files"]["CommandMetal.exe"]["existed"])
        self.assert_vendor_unchanged()

    def test_active_game_refuses_install_before_creating_backup(self):
        with mock.patch.object(installer, "running_games", return_value=["CommandMetal.exe"]):
            with self.assertRaisesRegex(RuntimeError, "Save and close"):
                installer.install(self.source, self.game)
        self.assertFalse((self.game / "CommandMetal.exe").exists())
        self.assertFalse((self.game / "CommandMetal-backups").exists())
        self.assert_vendor_unchanged()

    def test_game_started_during_preflight_prevents_replacement(self):
        with mock.patch.object(installer, "running_games", side_effect=[[], ["Command.exe"]]):
            with self.assertRaisesRegex(RuntimeError, "started during"):
                installer.install(self.source, self.game)
        self.assertFalse((self.game / "CommandMetal.exe").exists())
        self.assert_vendor_unchanged()

    def test_invalid_game_config_is_refused(self):
        (self.game / "Command.exe.config").write_bytes(b"<broken")
        with self.assertRaises(installer.ET.ParseError):
            installer.install(self.source, self.game)
        self.assertFalse((self.game / "CommandMetal.exe").exists())

    def test_second_file_failure_restores_old_pair(self):
        self.check_rollback(existing=True)

    def test_second_file_failure_removes_new_executable_on_fresh_install(self):
        self.check_rollback(existing=False)

    def test_last_file_failure_restores_existing_pair_and_removes_browser(self):
        self.check_rollback(existing=True, fail_at=4)

    def test_incomplete_package_is_refused_before_installation(self):
        self.browser.unlink()
        with self.assertRaisesRegex(ValueError, "complete build package"):
            installer.install(self.source, self.game)
        self.assertFalse((self.game / "CommandMetal.exe").exists())
        self.assert_vendor_unchanged()

    def check_rollback(self, existing, fail_at=2):
        if existing:
            (self.game / "CommandMetal.exe").write_bytes(b"old-adapter")
            (self.game / "CommandMetal.exe.config").write_bytes(b"old-config")
        original_replace = installer.replace_file
        calls = 0

        def fail_second(destination, contents):
            nonlocal calls
            calls += 1
            if calls == fail_at:
                raise OSError("Simulated config replacement failure")
            original_replace(destination, contents)

        with mock.patch.object(installer, "replace_file", side_effect=fail_second):
            with self.assertRaises(OSError):
                installer.install(self.source, self.game)
        if existing:
            self.assertEqual((self.game / "CommandMetal.exe").read_bytes(), b"old-adapter")
            self.assertEqual((self.game / "CommandMetal.exe.config").read_bytes(), b"old-config")
        else:
            self.assertFalse((self.game / "CommandMetal.exe").exists())
            self.assertFalse((self.game / "CommandMetal.exe.config").exists())
        self.assertFalse((self.game / "CommandBrowser.exe").exists())
        self.assertFalse((self.game / "CommandBrowser.exe.config").exists())
        self.assert_vendor_unchanged()

    def test_backup_metadata_failure_does_not_commit(self):
        original_write = Path.write_text

        def fail_metadata(path, *args, **kwargs):
            if path.name == "README.txt":
                raise OSError("Simulated backup metadata failure")
            return original_write(path, *args, **kwargs)

        with mock.patch.object(Path, "write_text", new=fail_metadata):
            with self.assertRaises(OSError):
                installer.install(self.source, self.game)
        self.assertFalse((self.game / "CommandMetal.exe").exists())
        self.assert_vendor_unchanged()

    def test_process_detection_handles_windows_paths_and_spaces(self):
        listing = "C:\\Games\\A game\\CommandMetal.exe\n/usr/bin/python3\nC:\\Games\\A game\\Command.exe\n"
        self.processes.stop()
        with mock.patch.object(installer.os, "name", "posix"), \
             mock.patch.object(installer.subprocess, "check_output", return_value=listing):
            self.assertEqual(installer.running_games(), ["CommandMetal.exe", "Command.exe"])
        self.processes.start()

    def test_process_detection_handles_windows_tasklist_csv(self):
        self.processes.stop()
        with mock.patch.object(installer.os, "name", "nt"), \
             mock.patch.object(installer.subprocess, "check_output", return_value='"CommandMetal.exe","42","Console"\n"CommandBrowser.exe","44","Console"\n"other.exe","43","Console"\n'):
            self.assertEqual(installer.running_games(), ["CommandMetal.exe", "CommandBrowser.exe"])
        self.processes.start()


if __name__ == "__main__":
    unittest.main()
