#!/usr/bin/env python3
"""Render and exercise native toolbar peers without starting Wine or the game."""
import argparse
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--game-dir', required=True, type=Path, help='Installed game folder containing Symbols/Menu')
    parser.add_argument('--output', type=Path, default=ROOT/'.tools/native-toolbar')
    args = parser.parse_args()
    if sys.platform != 'darwin':
        parser.error('The toolbar fixture requires macOS and Xcode command-line tools.')
    assets = args.game_dir.resolve()/'Symbols/Menu'
    if not assets.is_dir():
        parser.error('The game folder must contain Symbols/Menu.')
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    executable = output/'preview'
    subprocess.run(['xcrun', 'clang', '-fobjc-arc', '-fblocks', '-Wall', '-Wextra',
        '-Wno-unused-parameter', '-Wno-incompatible-pointer-types',
        '-framework', 'AppKit', '-framework', 'WebKit', '-framework', 'UniformTypeIdentifiers',
        str(ROOT/'tests/native_toolbar.m'), '-o', str(executable)], check=True)
    result = subprocess.run([str(executable), str(output), str(assets)], text=True, capture_output=True)
    (output/'result.txt').write_text(result.stdout + result.stderr, encoding='utf-8')
    if result.returncode:
        print(result.stdout + result.stderr, file=sys.stderr)
        result.check_returncode()
    print('Toolbar checks passed. Previews and results: ' + str(output))


if __name__ == '__main__':
    main()
