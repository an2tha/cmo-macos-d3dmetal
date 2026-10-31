#!/usr/bin/env python3
"""Restore locked dependencies, build the launchers and probe, and create a runtime package."""
from __future__ import annotations

import argparse
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def package() -> Path:
    output = ROOT / "dist/CommandMetal"
    if output.exists():
        shutil.rmtree(output)
    output.mkdir(parents=True)
    for project in ("CommandMetal", "CommandBrowser", "GraphicsProbe"):
        built = ROOT / "src" / project / "bin/Release/net48"
        for suffix in (".exe", ".exe.config"):
            shutil.copy2(built / (project + suffix), output / (project + suffix))
    for name in ("README.md", "LICENSE", "CHANGELOG.md", "CONTRIBUTING.md"):
        shutil.copy2(ROOT / name, output / name)
    shutil.copytree(ROOT / "docs", output / "docs")
    shutil.copytree(ROOT / "third-party", output / "third-party")
    (output / "scripts").mkdir()
    shutil.copy2(ROOT / "scripts/install.py", output / "scripts/install.py")
    shutil.copy2(ROOT / "scripts/Play-D3DMetal.command", output / "Play-D3DMetal.command")
    entries = sorted(file for file in output.rglob("*") if file.is_file())
    (output / "SHA256SUMS").write_text("".join(
        hashlib.sha256(file.read_bytes()).hexdigest() + "  " + file.relative_to(output).as_posix() + "\n"
        for file in entries
    ), encoding="utf-8")
    archive = ROOT / "dist/CommandMetal-win-x64.zip"
    with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as zipped:
        for file in sorted(output.rglob("*")):
            if file.is_file():
                entry = zipfile.ZipInfo(file.relative_to(output).as_posix())
                entry.create_system = 3
                executable = file.suffix == ".command"
                entry.external_attr = (0o100755 if executable else 0o100644) << 16
                entry.compress_type = zipfile.ZIP_DEFLATED
                zipped.writestr(entry, file.read_bytes())
    print("Package: " + str(archive))
    return archive


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default=os.environ.get("DOTNET", "dotnet"), help="Path to a .NET 10 SDK dotnet executable")
    args = parser.parse_args()
    environment = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1")
    for command in (
        [args.dotnet, "restore", "CommandMetal.sln", "--locked-mode"],
        [args.dotnet, "build", "CommandMetal.sln", "--configuration", "Release", "--no-restore"],
    ):
        subprocess.run(command, cwd=ROOT, env=environment, check=True)
    package()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
