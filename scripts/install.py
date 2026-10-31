#!/usr/bin/env python3
"""Install only CommandMetal's two files, preserving the game's configuration."""
from __future__ import annotations

import argparse
import csv
from datetime import datetime, timezone
import io
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import uuid
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


def running_games() -> list[str]:
    if os.name == "nt":
        listing = subprocess.check_output(["tasklist", "/fo", "csv", "/nh"], text=True)
        names = [row[0] for row in csv.reader(io.StringIO(listing)) if row]
    else:
        listing = subprocess.check_output(["ps", "-axo", "comm="], text=True)
        names = [line.strip().replace("\\", "/").rsplit("/", 1)[-1] for line in listing.splitlines()]
    return [name for name in names if name.lower() in {"command.exe", "commandmetal.exe"}]


def replace_file(destination: Path, data: bytes) -> None:
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=destination.parent, prefix=".commandmetal-", delete=False) as stream:
            temporary = Path(stream.name)
            stream.write(data)
        os.replace(temporary, destination)
    finally:
        if temporary is not None and temporary.exists():
            temporary.unlink()


def install(source: Path, game: Path) -> Path:
    source, game = source.resolve(strict=True), game.resolve(strict=True)
    executable = game / "Command.exe"
    config = game / "Command.exe.config"
    if source == executable or source == game / "CommandMetal.exe":
        raise ValueError("Choose the newly built executable outside the game folder.")
    if not executable.is_file() or not config.is_file():
        raise ValueError("Choose the game folder containing Command.exe and Command.exe.config.")
    configuration = config.read_bytes()
    # Validate without serializing, preserving all vendor settings and byte encoding.
    if ET.fromstring(configuration).tag != "configuration":
        raise ValueError("Command.exe.config has an unexpected root element.")
    binary = source.read_bytes()
    if binary[:2] != b"MZ":
        raise ValueError("The source is not a Windows executable.")
    if running_games():
        raise RuntimeError("Save and close Command before installing. No process was stopped.")

    backup = game / "CommandMetal-backups" / (datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + uuid.uuid4().hex[:8])
    backup.mkdir(parents=True)
    files = {"CommandMetal.exe": binary, "CommandMetal.exe.config": configuration}
    previous = {}
    for name in files:
        destination = game / name
        previous[name] = destination.read_bytes() if destination.exists() else None
        if previous[name] is not None:
            shutil.copy2(destination, backup / name)

    (backup / "manifest.json").write_text(json.dumps({
        "files": {name: {
            "existed": contents is not None,
            "sha256": hashlib.sha256(contents).hexdigest() if contents is not None else None,
        } for name, contents in previous.items()},
        "restore": "With Command closed: restore backed-up files that existed; remove the newly installed files that did not exist.",
    }, indent=2) + "\n", encoding="utf-8")
    (backup / "README.txt").write_text(
        "Previous launcher files, when present, were copied here before installation.\n"
        "Save and close Command before restoring them. See manifest.json for files originally absent.\n"
        "Command.exe and its config were not modified.\n",
        encoding="utf-8",
    )

    # Recheck after reading/copying inputs, before replacing either launcher file.
    if running_games():
        raise RuntimeError("Command started during preflight. Close it and retry; installed files were not replaced.")
    changed = []
    try:
        for name, contents in files.items():
            replace_file(game / name, contents)
            changed.append(name)
    except BaseException:
        for name in reversed(changed):
            if previous[name] is None:
                (game / name).unlink(missing_ok=True)
            else:
                replace_file(game / name, previous[name])
        raise
    return backup


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-dir", type=Path, required=True)
    parser.add_argument("--source", type=Path, help="Built CommandMetal.exe; defaults to the package or dist output")
    args = parser.parse_args()
    source = args.source or (ROOT / "CommandMetal.exe" if (ROOT / "CommandMetal.exe").exists() else ROOT / "dist/CommandMetal/CommandMetal.exe")
    try:
        backup = install(source, args.game_dir)
    except (OSError, ValueError, RuntimeError, ET.ParseError) as error:
        parser.exit(1, str(error) + "\n")
    print("Installed CommandMetal.exe and a copy of the game's configuration.")
    print("Previous launcher backup: " + str(backup))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
