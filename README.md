# CommandMetal

An experimental D3DMetal compatibility launcher for **Command: Modern Operations** on macOS with CrossOver.

CommandMetal supplies the DXGI surface and D3D11 context-state behavior used by Wine's Direct2D renderer. It runs the original `Command.exe` in the same process, with process-local graphics adapters installed first. The game executable and its libraries are not patched on disk.

The included `CommandBrowser.exe` bootstrap also handles an optional WebView2 API missing from Runtime 109, allowing the Database Viewer's details pane to initialize. It then starts `CommandMetal.exe` in the same process. The launch script enables selected-unit sidebar and map viewport synchronization by default. A separate experimental option can contain a captured window-activation wait.

**Status:** the original prototype renders a playable map, with remaining lag. The sidebar resize, map input and database details passed in-game checks. The optional focus and native barrier guards address captured failure paths, but a later whole-window freeze remains unresolved. The standalone renderer passes the isolated drawing benchmarks below; full in-game regression testing of that rewrite is still required.

## Benchmarks

Measured on **2 November 2026** with an Apple M4 Max (16 CPU cores, 40 GPU cores, 48 GB RAM), AC power, macOS 27.2 (26B5101f), CrossOver 26.3 and D3DMetal. MSync and ESync were off. These are **synthetic drawing-loop measurements, not game FPS**.

| Standalone adapter configuration | Median iterations/second | Range across 5 runs |
| --- | ---: | ---: |
| D3DMetal, batching off (`CMO_D2D_BATCHING=0`) | 434 | 407 to 436 |
| D3DMetal, batching on (`CMO_D2D_BATCHING=1`) | 1,005 | 989 to 1,018 |

Batching delivered **2.32 times the throughput in this workload**. Both rows use the compatibility adapters; the first row is not an unmodified-game or Wine renderer baseline. All ten runs passed pixel-readback and graphics-state checks. [Raw measurements and environment](docs/benchmarks/2026-11-02-m4-max.json).

Each run uses a 600×400 target, 100 warmup iterations and 2,000 timed iterations with 64 overlapping rectangles each. The loop includes clearing, brush/buffer setup, drawing and topology checks. Timing includes final blocking GPU readback and pixel validation; startup and the final display presentation are excluded. Settings alternate across five runs each. Diagnostic tracing and forced GC are disabled. Background applications were not controlled.

There is no presentation inside the timed loop, so iterations/second is not displayed FPS and is not capped by the launcher's requested 60 FPS limit. Map movement, text, terrain, simulation work, Wine-versus-D3DMetal speed and long-session stability are not measured here. See [the reproducible command](docs/BUILDING.md#reproduce-the-drawing-benchmark).

## Requirements

- A licensed installation of Command: Modern Operations and its required .NET Framework 4.8 dependencies in CrossOver.
- An Apple Silicon Mac and CrossOver with D3DMetal available.
- Python 3.10 or newer for the installer. A .NET 10 SDK is needed only when building from source.

The prototype was exercised with Command v1.10, build 1900.20, CrossOver 26.3, D3DMetal 4.0b2, macOS 27.2, and an Apple M4 Max. Other versions and hardware are untested. See [compatibility and limitations](docs/COMPATIBILITY.md).

## Download or build

Open **Actions > Build CommandMetal**, select a successful `main` run, and download the `CommandMetal-win-x64` artifact. Extract the artifact, then extract the included `CommandMetal-win-x64.zip`.

To build from source on Windows, macOS, or Linux:

```sh
python3 scripts/build.py
```

On Windows, use `python` instead of `python3` if needed. The package is written to `dist/CommandMetal-win-x64.zip`. Builds need neither CrossOver nor a game installation. [Build details and CI](docs/BUILDING.md).

## Install

1. Save and close Command.
2. Extract a package, or build the source checkout.
3. Run the installer with your actual game directory:

```sh
python3 scripts/install.py --game-dir "$HOME/Library/Application Support/CrossOver/Bottles/Steam/drive_c/Games/Command - Modern Operations"
```

The installer writes `CommandMetal.exe`, `CommandBrowser.exe`, and their `.exe.config` files beside `Command.exe`. Each configuration is an exact copy of the existing `Command.exe.config`, preserving the game's binding redirects and runtime settings. Previous launcher files are backed up under `CommandMetal-backups` in the game directory. It refuses installation while a Command process is running.

## Play

In CrossOver, choose **D3DMetal** and turn **MSync off** for the game's bottle. For an existing session, save and close its applications, then use the bottle's **Quit All Applications** before launching with different synchronization settings.

From a source checkout:

```sh
zsh scripts/Play-D3DMetal.command
```

From an extracted package:

```sh
zsh Play-D3DMetal.command
```

The launcher selects D3DMetal, disables its forced Metal 4 path, caps the requested frame rate at 60, enables drawing batches, and requests `WINEMSYNC=0` and `WINEESYNC=0`. It never closes an existing game or kills the bottle. These flags are the current prototype's working settings, not a guarantee of 60 FPS or stability.

Override the paths for another bottle or installation:

```sh
CMO_BOTTLE='My Bottle' \
CMO_GAME_DIR='/absolute/path/to/Command - Modern Operations' \
CROSSOVER_APP='/Applications/CrossOver.app' \
zsh scripts/Play-D3DMetal.command
```

The normal CrossOver **Command** icon starts the original executable without these adapters. To use these fixes through CrossOver's Run Command dialog, select the installed `CommandBrowser.exe` and use the same graphics and synchronization settings. Starting `CommandMetal.exe` directly applies only the graphics adapters.

## Experimental sidebar and focus tests

These options are restricted to the reviewed Command build. The launch script enables the sidebar resize fix by default; the separate focus guard remains opt-in. The combined bootstrap passed a controlled in-game check of automatic startup, selection, panel scrolling, pan/zoom, context menus, resizing, sidebar hide/show, app switching, unit/database dialogs and keyboard focus. Longer sessions and other environments remain unvalidated.

After saving and closing the game, start a separate test session with:

```sh
CMO_PANEL_LAYOUT=1 CMO_FOCUS_COMPAT=1 zsh scripts/Play-D3DMetal.command
```

For an extracted package, use `Play-D3DMetal.command` without the `scripts/` prefix. `CMO_PANEL_LAYOUT=1` reserves sidebar space and synchronizes the renderer and picking camera. The launch script supplies this setting automatically on every run. `CMO_FOCUS_COMPAT=1` guards the exact message-handler activation path captured during one freeze. Unknown game hashes retain normal behavior. Set either option to `0` on the next launch to disable it. Starting `CommandBrowser.exe` directly still requires these environment variables; starting `CommandMetal.exe` directly bypasses the sidebar and browser fixes.

## Experimental long-session crash fix

A captured Rosetta assertion left two threads inside Wine's process-wide memory barrier. An optional native serialization guard addresses the concurrent Mach register-state query path, following the approach in [Microsoft's Rosetta fix](https://github.com/dotnet/runtime/pull/134384). It preserves the original barrier and the map/sidebar code. A four-thread stress test and a combined graphics/GC probe passed, and the user confirmed normal map/input/sidebar behavior. A later freeze with a Wine critical-section timeout remains unresolved; this is not a complete crash fix.

After saving and closing the game, use `CMO_SERIALIZE_BARRIERS=1 zsh scripts/Play-D3DMetal.command` to test it. This option requires the exact reviewed CrossOver native module and stops startup if the profile differs. It is off by default. See [diagnostics and limitations](docs/TROUBLESHOOTING.md#rosetta-thread-context-assertion-during-long-sessions).

## Remove or roll back

Save and close Command, then remove only `CommandMetal.exe`, `CommandBrowser.exe`, and their `.exe.config` files, or restore those files from a `CommandMetal-backups` subdirectory. Each backup's `manifest.json` records which files previously existed: restore those, and remove newly added files marked as originally absent. Launch the original `Command.exe` afterward. The installer does not replace `Command.exe`, `DirectN.dll`, the WebView2 SDK, or `Command.exe.config`.

## Development

- [How the adapters work](docs/ARCHITECTURE.md)
- [Building and GitHub Actions](docs/BUILDING.md)
- [Diagnostics and troubleshooting](docs/TROUBLESHOOTING.md)
- [Known issues and compatibility](docs/COMPATIBILITY.md)
- [Contributing and test expectations](CONTRIBUTING.md)

This repository includes adapter source, a manual drawing probe, build and install tools, and documentation. It excludes the game, game libraries, saved scenarios, local investigations, and experimental bitmap-cache patches. It is an independent project, unaffiliated with the game's publishers, CodeWeavers, or Apple. The adapter source is under the [MIT license](LICENSE); game and platform components retain their own licenses.
