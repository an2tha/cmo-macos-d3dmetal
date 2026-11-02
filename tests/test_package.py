import hashlib
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest import mock
import zipfile


SPEC = importlib.util.spec_from_file_location("builder", Path(__file__).resolve().parents[1] / "scripts/build.py")
builder = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(builder)


class PackagingTests(unittest.TestCase):
    def test_only_explicit_runtime_inputs_are_packaged_with_hashes_and_unix_launcher_permissions(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for project in ("CommandMetal", "CommandBrowser", "GraphicsProbe"):
                folder = root / "src" / project / "bin/Release/net48"
                folder.mkdir(parents=True)
                (folder / (project + ".exe")).write_bytes(b"MZtest")
                (folder / (project + ".exe.config")).write_text("<configuration />")
                (folder / "DirectN.dll").write_bytes(b"must-not-ship")
                (folder / "Command.exe").write_bytes(b"must-not-ship")
            (root / "docs").mkdir()
            (root / "docs/BUILDING.md").write_text("Build docs\n")
            (root / "third-party").mkdir()
            (root / "third-party/Harmony-LICENSE.txt").write_text("License fixture\n")
            (root / "scripts").mkdir()
            (root / "scripts/install.py").write_text("# Installer fixture\n")
            (root / "scripts/Play-D3DMetal.command").write_bytes(b"#!/bin/zsh\nexit 0\n")
            for name in ("README.md", "LICENSE", "CHANGELOG.md", "CONTRIBUTING.md"):
                (root / name).write_text("Fixture\n")
            (root / "private.log").write_text("must-not-ship")
            with mock.patch.object(builder, "ROOT", root):
                archive = builder.package()
            with zipfile.ZipFile(archive) as zipped:
                names = zipped.namelist()
                self.assertIn("CommandMetal.exe", names)
                self.assertIn("CommandBrowser.exe", names)
                self.assertIn("third-party/Harmony-LICENSE.txt", names)
                self.assertIn("scripts/install.py", names)
                self.assertIn("CONTRIBUTING.md", names)
                self.assertNotIn("DirectN.dll", names)
                self.assertNotIn("Command.exe", names)
                self.assertNotIn("private.log", names)
                info = zipped.getinfo("Play-D3DMetal.command")
                self.assertEqual(info.create_system, 3)
                self.assertEqual((info.external_attr >> 16) & 0o777, 0o755)
                self.assertNotIn(b"\r", zipped.read(info))
                for line in zipped.read("SHA256SUMS").decode().splitlines():
                    expected, name = line.split("  ", 1)
                    self.assertEqual(hashlib.sha256(zipped.read(name)).hexdigest(), expected)


if __name__ == "__main__":
    unittest.main()
