#!/usr/bin/env python3
"""Build the optional native macOS app and embedded AppKit/WebKit library."""
import argparse
from pathlib import Path
import plistlib
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--skip-managed', action='store_true', help='Use an already built dist/CommandMetal package')
    parser.add_argument('--output', type=Path, default=ROOT/'dist/Command Native.app', help='Output app bundle; use a separate path while testing beside a running build')
    parser.add_argument('--bundle-id', default='local.command.native', help='Unique app identity for a separate development build')
    args = parser.parse_args()
    if sys.platform != 'darwin':
        parser.error('The native package requires macOS and Xcode command-line tools.')
    if not args.skip_managed:
        subprocess.run([sys.executable, str(ROOT/'scripts/build.py'), '--dotnet', args.dotnet], check=True)
    app = args.output.resolve()
    if app.suffix != '.app':
        parser.error('--output must name an .app bundle.')
    if app.exists():
        shutil.rmtree(app)
    resources = app/'Contents/Resources'
    macos = app/'Contents/MacOS'
    resources.mkdir(parents=True)
    macos.mkdir()
    subprocess.run(['xcrun', 'clang', '-arch', 'x86_64', '-mmacosx-version-min=12.0', '-fobjc-arc', '-fblocks', '-Wall', '-Wextra', '-Wno-unused-parameter', '-Wno-incompatible-pointer-types', '-dynamiclib', '-framework', 'AppKit', '-framework', 'WebKit', '-framework', 'UniformTypeIdentifiers', str(ROOT/'native/CommandNativeHost/NativeHost.m'), '-o', str(resources/'libCommandNativeHost.dylib')], check=True)
    outputs = []
    for arch in ('arm64', 'x86_64'):
        output = ROOT/f'dist/CommandNative-{arch}'
        subprocess.run(['xcrun', 'swiftc', '-swift-version', '5', '-target', f'{arch}-apple-macos12.0', '-O', '-framework', 'AppKit', str(ROOT/'native/CommandNativeApp/main.swift'), '-o', str(output)], check=True)
        outputs.append(output)
    subprocess.run(['xcrun', 'lipo', '-create', *map(str, outputs), '-output', str(macos/'CommandNative')], check=True)
    for output in outputs:
        output.unlink()
    shutil.copytree(ROOT/'dist/CommandMetal', resources/'adapter')
    (app/'Contents/Info.plist').write_bytes(plistlib.dumps({
        'CFBundleIdentifier': args.bundle_id, 'CFBundleName': app.stem,
        'CFBundleDisplayName': app.stem, 'CFBundleExecutable': 'CommandNative',
        'CFBundlePackageType': 'APPL', 'CFBundleShortVersionString': '0.2.7',
        'CFBundleVersion': '8', 'LSMinimumSystemVersion': '12.0',
        'NSHighResolutionCapable': True,
    }))
    subprocess.run(['codesign', '--force', '--sign', '-', str(resources/'libCommandNativeHost.dylib')], check=True)
    subprocess.run(['codesign', '--force', '--sign', '-', str(app)], check=True)
    print(app)


if __name__ == '__main__':
    main()
