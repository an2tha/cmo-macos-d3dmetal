# Building cmo-on-macos and continuous integration

For everyday setup, follow [Install and play](INSTALL.md). The primary macOS
entry point is `Command Native.app`. `CommandMetal` and `CommandBrowser` remain
the names of its Windows compatibility components.

## Build the macOS app

On macOS, install Xcode command-line tools (`xcode-select --install`), Python
3.10 or newer and the .NET 10 SDK. From the repository root:

```sh
python3 scripts/build_native.py
```

This first builds the managed adapter package, then compiles the native bridge
and both launcher architectures. The result is `dist/Command Native.app`, with
its adapters and native library embedded. CrossOver and game files are not needed
to build it. They are needed to play. The app is ad hoc signed locally and is not
notarized. Open it only when ready for a new saved-game test session:

```sh
open 'dist/Command Native.app'
```

The app prepares its own runtime. Do not run `scripts/install.py` as an additional
app installation step. Keep the whole bundle together when copying it to
Applications. [Native interface scope and tests](NATIVE-UI.md).

Build options:

```sh
python3 scripts/build_native.py --dotnet /absolute/path/to/dotnet
python3 scripts/build_native.py --skip-managed
python3 scripts/build_native.py \
  --output 'dist/Command Native Candidate.app' \
  --bundle-id local.command.native.candidate
```

`--skip-managed` reuses `dist/CommandMetal`; use it only after building the intended
managed revision. `--output` replaces an existing bundle at that location, so
choose a separate output for work alongside a running app. Building does not
replace views in a running game. Quit the current game before launching another.

## Build the standalone Windows adapter package

### Prerequisites

Install a .NET 10 SDK and Python 3.10 or newer. The resulting executables target **.NET Framework 4.8, Windows x64**. The SDK version is a build requirement, not an additional runtime to install inside CrossOver.

The build uses Microsoft's `Microsoft.NETFramework.ReferenceAssemblies` 1.0.3 to provide framework reference assemblies outside Windows. The browser bootstrap also uses `Lib.Harmony` 2.3.3, embedded as a resource so installation needs no additional managed DLL. Lock files pin resolved dependencies and content hashes. The managed build needs no game DLL, proprietary configuration file, Apple library or CrossOver installation. The native app build additionally uses the macOS frameworks supplied by the Xcode tools.

The managed executable projects explicitly target the `win-x64` runtime so locked restore uses the same dependency graph on every host. Runtime identifiers are not appended to output directories; packaging continues to read `bin/Release/net48`.

### Commands

```sh
python3 -m unittest discover -s tests -v
python3 scripts/build.py
```

For an SDK outside PATH:

```sh
python3 scripts/build.py --dotnet /absolute/path/to/dotnet
```

The build tool restores with `--locked-mode`, builds `CommandMetal.sln` in Release, and creates:

```text
dist/
  CommandMetal/
    CommandMetal.exe
    CommandMetal.exe.config
    CommandBrowser.exe
    CommandBrowser.exe.config
    GraphicsProbe.exe
    GraphicsProbe.exe.config
    Play-D3DMetal.command
    scripts/install.py
    README.md
    CHANGELOG.md
    CONTRIBUTING.md
    LICENSE
    docs/
    third-party/Harmony-LICENSE.txt
    SHA256SUMS
  CommandMetal-win-x64.zip
```

The package configurations are minimal framework declarations for build/version checks. The installer replaces both installed launcher configurations with exact copies of the user's `Command.exe.config`. Do not overwrite a game's configuration with a minimal build-time file. The process starts through `CommandBrowser.exe`, so its full configuration is required even though it loads `CommandMetal.exe` afterward.

To intentionally update dependencies, edit the relevant `PackageReference` in `Directory.Build.props` or the browser project, run `dotnet restore CommandMetal.sln --force-evaluate`, review the project lock files, and rerun tests/build. Ordinary builds must not regenerate the lock files silently.

## GitHub Actions

The workflow still appears as **Build CommandMetal**, and its artifact remains
`CommandMetal-win-x64`. Those names refer to the adapter package, not a different
repository. The current workflow does **not** build, sign, notarize or upload
`Command Native.app`. The published `v0.1` release also contains only the adapter
ZIP. Build the app locally until a macOS package is published.


`.github/workflows/build.yml` runs on pushes, pull requests, and manual dispatch. It uses a Windows runner, installs .NET 10 and Python, runs host-side installer/package tests, builds the three executables, runs both launcher `--version` checks without the game installed, and verifies the bootstrap's embedded dependency by applying and removing a prefix on a self-test method. The same self-test uses owned WinForms controls and an injected viewport to exercise width enforcement, paired viewport updates, camera lag, busy rendering, resize failures, reentrant rollback and transient restoration retries. It also detours the real framework Form.Activate using hidden fixture forms to verify suppression of the observed caller while preserving legitimate, nested-dialog and other-form activation. It creates no visible game window. It uploads the ZIP as `CommandMetal-win-x64` with 30-day retention.

Official actions are pinned to commit SHAs. The workflow requests read-only repository permission, does not require secrets, and does not create releases or publish to a package registry. GitHub's [artifact documentation](https://docs.github.com/en/actions/tutorials/store-and-share-data) explains how to download an artifact from a workflow run.

CI verifies buildability and packaging. The Windows runner does not provide the user's CrossOver/D3DMetal environment, so a green build does not certify map rendering, performance, or freeze prevention. Local edits are not validated by Actions until they are pushed and the matching run succeeds.

## Manual Rosetta barrier validation

After saving and closing every game session, run the built browser bootstrap in the reviewed CrossOver bottle with `--barrier-self-test`. It installs the native compatibility path in that test process and issues 40,000 original barrier calls from four threads. It checks native call counts, maximum concurrent original calls, and lock failures. No game assembly or graphics device is loaded. The native-module profile must match; this test is not available on the Windows CI runner.

The worker join has a timeout, but a low-level runtime deadlock can also stop the managed timeout path. It is not an external process watchdog. Keep this probe separate from any session with unsaved progress.

## Manual graphics validation

Save and close the game first. Run the built `GraphicsProbe.exe` in the intended CrossOver bottle with the same D3DMetal and synchronization environment as the launcher. A basic run draws a red rectangle over blue, checks two GPU readback pixels, verifies primitive-topology restoration after EndDraw, and presents.

For repeated drawing, explicit Flush checks, and concurrent managed allocations, pass:

```text
--frames=2000 --draws=64 --flush --gc-stress
```

The probe returns a nonzero status on validation failure. Wine's underlying `E_NOTIMPL` Flush result is tolerated only while the topology restoration check still passes. The probe has a 45-second drawing-loop limit, not a guaranteed process timeout if a native call hangs. Never launch it into an active game session.

After probe success, use a separate saved-game session to check scenario load, map pan/zoom, selection, dialogs, save/load, and longer play. Record backend and synchronization settings with the result.

## Reproduce the drawing benchmark

Save and close all game sessions first. From an extracted package directory on macOS, run:

```sh
/Applications/CrossOver.app/Contents/SharedSupport/CrossOver/bin/wine \
  --bottle Steam --wait --debugmsg=-all,err+all \
  --env 'CX_GRAPHICS_BACKEND=d3dmetal D3DM_MTL4=0 D3DM_MAX_FPS=60 WINEMSYNC=0 WINEESYNC=0 CMO_D2D_BATCHING=1 CMO_DIAGNOSTICS=0 CMO_TRACE_EXCEPTIONS=0 CMO_D2D_PROFILE=0' \
  "Z:$PWD/GraphicsProbe.exe" \
  --benchmark --warmup=100 --frames=2000 --draws=64
```

For a source build, substitute `Z:$PWD/src/GraphicsProbe/bin/Release/net48/GraphicsProbe.exe`. Adjust CrossOver and bottle paths if needed. Change `CMO_D2D_BATCHING=1` to `0` for the ordinary state-emulation path. Run each setting five times in alternating order and report the median and full range from the `BENCHMARK` JSON line. Each process must finish successfully, print `PASS`, and retain correct pixels and topology. Do not combine this timing with `--gc-stress`, `--flush`, profiling or exception tracing.

Warmup and device startup are outside timing. A blocking staging-texture readback completes warmup before timing and measured GPU work before the timer stops. Each iteration includes temporary buffer allocation, a clear, brush creation/release, overlapping rectangle fills and topology checks. Timed readback includes its two pixel-result console writes. A single Present occurs after timing; no loop iteration presents. This is completed synthetic drawing throughput, not displayed FPS, isolated GPU time or a whole-game benchmark. The 45-second loop check is not an external process watchdog.
