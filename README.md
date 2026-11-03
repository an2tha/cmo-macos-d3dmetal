# cmo-on-macos

**Run Command: Modern Operations on your Mac, with one launcher for graphics fixes, browser rendering and the native interface.**

cmo-on-macos brings the CrossOver setup, D3DMetal compatibility fixes, native macOS controls and troubleshooting into one project. Open **Command Native.app**, choose your installed game and bottle, then click **Launch Command**. The app prepares the compatibility components for that session without replacing the installed game executables.

You still need your own copy of Command and CrossOver. The launcher does not install either, and the simulation and map continue to run through CrossOver.

[Get started](docs/INSTALL.md) · [Troubleshooting](docs/TROUBLESHOOTING.md) · [Compatibility](docs/COMPATIBILITY.md) · [Report an issue](https://github.com/an2tha/cmo-on-macos/issues)

## What is included

| Component | What it does |
| --- | --- |
| **Command Native.app** | One place to choose your game folder, bottle and interface, launch the game and read session output |
| **D3DMetal compatibility** | Supplies the surface and graphics-state behavior needed for the map, with drawing batches to reduce overhead |
| **Sidebar and input fixes** | Keeps the unit status panel visible while coordinating map size and click coordinates |
| **Native browser rendering** | Uses embedded WebKit for supported database, scenario and other HTML views |
| **Native controls** | Adds AppKit peers for supported standard controls, with existing game handlers retained |
| **Windows interface fallback** | Selectable in the same app, including the targeted WebView2 compatibility guard |
| **Diagnostics** | Saved session logs, isolated drawing probes and documented checks for bug reports |

**Current status:** experimental and under active development. The playable prototype passed map, sidebar, input and database checks, with remaining lag and intermittent freezes. The native interface has passed isolated bridge/toolbar checks and a startup check; broad gameplay validation is still needed. Some custom controls remain Windows-rendered. [Known issues and tested versions](docs/COMPATIBILITY.md).

## Get the app

Download **Command-Native-0.2.7-macOS.zip** from [release v0.2.7](https://github.com/an2tha/cmo-on-macos/releases/tag/v0.2.7). It contains a universal Swift launcher with native Apple Silicon support. The embedded Wine bridge remains x86_64 because it runs inside the Windows game's translated process. Rosetta and the reviewed CrossOver runtime are still required. The app is ad-hoc signed, not Developer ID signed or notarized.

The launcher asks for your game folder and bottle and remembers them locally. No developer-specific paths, game assets, CrossOver binaries, or recovery wrappers are included. Build from source with:

Install the Xcode command-line tools, Python 3.10 or newer and the .NET 10 SDK, then run:

```sh
git clone https://github.com/an2tha/cmo-on-macos.git
cd cmo-on-macos
python3 scripts/build_native.py
open 'dist/Command Native.app'
```

The build needs no game files. Once built, the app does not need Python or the .NET SDK to launch the game. You can copy the whole `Command Native.app` bundle to Applications and add it to your Dock. The current app name is **Command Native**, while the project and repository are **cmo-on-macos**. Existing executable names such as `CommandMetal.exe` are component names and remain unchanged.

[Step-by-step setup, prerequisites and game-folder examples](docs/INSTALL.md). Developers can find build options and CI details in [BUILDING.md](docs/BUILDING.md).

## First launch

1. Install Command and its required .NET Framework 4.8 dependencies in a CrossOver bottle. This project starts from that installed game.
2. Save and close Command. Select **D3DMetal** in the bottle and turn **MSync off**. If changing synchronization settings, first save and close the bottle's other applications, then use **Quit All Applications**.
3. Open **Command Native.app**. Under **Game folder**, click **Choose…** and select the directory containing both `Command.exe` and `Command.exe.config`.
4. Enter the exact **CrossOver bottle** name. Use your own bottle's name.
5. Choose an **Interface**, then click **Launch Command**. The default native mode is a development option. **Original Windows interface** provides a comparison path with the graphics/sidebar fixes retained.
6. Load a scenario and check the map, unit selection, sidebar and Database Viewer. Use this app for subsequent launches so the compatibility components are loaded.

There is no separate `install.py` step for the app. It creates a temporary runtime, supplies the complete game configuration and retains logs after exit. Saves and game data still use the installed game's locations. [Updates, removal and the manual alternative](docs/INSTALL.md).

## Requirements and scope

- An Apple Silicon Mac, CrossOver with D3DMetal, and a licensed installation of Command: Modern Operations in a bottle.
- The game's required Windows dependencies, including .NET Framework 4.8, installed in that bottle.
- The native bridge currently requires the reviewed CrossOver native-module versions. An unknown profile stops native startup instead of applying guessed offsets.

The prototype was exercised with Command v1.10 build 1900.20, CrossOver 26.3, D3DMetal 4.0b2, macOS 27.2 and an Apple M4 Max. Other environments are unvalidated. A universal launcher binary does not mean Intel gameplay is supported. The launch script requests a 60 FPS cap; this is not a promise of 60 FPS in battles.

## Benchmarks

Measured on **2 November 2026** with an Apple M4 Max (16 CPU cores, 40 GPU cores, 48 GB RAM), AC power, macOS 27.2 (26B5101f), CrossOver 26.3 and D3DMetal. MSync and ESync were off. These are **synthetic drawing-loop measurements, not game FPS**.

| Standalone adapter configuration | Median iterations/second | Range across 5 runs |
| --- | ---: | ---: |
| D3DMetal, batching off (`CMO_D2D_BATCHING=0`) | 434 | 407 to 436 |
| D3DMetal, batching on (`CMO_D2D_BATCHING=1`) | 1,005 | 989 to 1,018 |

Batching delivered **2.32 times the throughput in this workload**. Both rows use the compatibility adapters; the first row is not an unmodified-game or Wine renderer baseline. All ten runs passed pixel-readback and graphics-state checks. [Raw measurements and environment](docs/benchmarks/2026-11-02-m4-max.json).

Each run uses a 600×400 target, 100 warmup iterations and 2,000 timed iterations with 64 overlapping rectangles each. The loop includes clearing, brush/buffer setup, drawing and topology checks. Timing includes final blocking GPU readback and pixel validation; startup and the final display presentation are excluded. Settings alternate across five runs each. Diagnostic tracing and forced GC are disabled. Background applications were not controlled.

There is no presentation inside the timed loop, so iterations/second is not displayed FPS and is not capped by the launcher's requested 60 FPS limit. Map movement, text, terrain, simulation work, Wine-versus-D3DMetal speed and long-session stability are not measured here. See [the reproducible command](docs/BUILDING.md#reproduce-the-drawing-benchmark).

## Documentation

- [Installation, everyday use and removal](docs/INSTALL.md)
- [Compatibility and known issues](docs/COMPATIBILITY.md)
- [Troubleshooting and session logs](docs/TROUBLESHOOTING.md)
- [Native interface coverage and validation](docs/NATIVE-UI.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Building, tests and GitHub Actions](docs/BUILDING.md)
- [Contributing](CONTRIBUTING.md) and [changelog](CHANGELOG.md)

## Project and license

cmo-on-macos is an independent community project, unaffiliated with the game's publishers, CodeWeavers or Apple. The source is under the [MIT license](LICENSE). Game and platform components retain their own licenses. The repository does not include Command, CrossOver, Apple libraries, game saves or proprietary game assets.


## Files and libraries in the functional build

| File or library | Role and changes |
| --- | --- |
| `Command Native.app/Contents/MacOS/CommandNative` | Universal Swift/AppKit launcher. Selects the installation and bottle, creates a separate session runtime, starts CrossOver and retains session logs. |
| `CommandBrowser.exe` | Managed bootstrap, built here. Loads embedded Harmony and installs browser, native-control and optional Wine barrier compatibility hooks before the graphics adapter starts the game. |
| `0Harmony.dll` (embedded) | Harmony runtime method interception. Redirects the reviewed WebView2 and WinForms entry points in memory; no patched Microsoft DLL is distributed. |
| `CommandMetal.exe` | Managed graphics adapter, built here. Supplies D3D11/DXGI surface and context-state compatibility, Direct2D batching, and coordinated sidebar/map resizing. |
| `libCommandNativeHost.dylib` | Objective-C x86_64 library loaded inside Wine. Embeds AppKit controls and WKWebView in Wine windows, maps local HTML resources and returns events to the original managed handlers. |
| `Command.exe` | Original installed game. Loaded from the selected installation; not redistributed or rewritten. Its simulation, custom controls and command handlers remain authoritative. |
| `Command.exe.config` | Original complete configuration copied to the two bootstrap executable configurations in the session directory. Minimal build configurations must not replace it. |
| `Microsoft.Web.WebView2.Core.dll`, `Microsoft.Web.WebView2.WinForms.dll` | Original installed SDK assemblies. Native mode redirects supported calls to WebKit. Windows-interface mode uses the targeted settings compatibility guard and the installed browser runtime. |
| `ntdll.dll`, `ntdll.so`, `winemac.so` | Installed Wine boundary and Cocoa window driver. The native bridge verifies reviewed module hashes before using their ABI/layout. Optional barrier serialization is an in-memory hook. These libraries are not bundled or replaced. |
| `d3d11.dll`, DXGI, `d2d1.dll`, D3DMetal | Installed graphics stack used by the adapter's surface/context and drawing fixes. Vendor implementations remain external dependencies. |
| AppKit, WebKit, Foundation | System macOS frameworks providing native UI, HTML rendering and launcher services. |
| Game HTML, CSS, images and scenario files | Read from the chosen game installation through session links and scoped resource mappings. They are not included in releases. |

Configuration is independent of an account name or game location. `CMO_GAME_DIR`, `CMO_BOTTLE`, `CROSSOVER_APP`, `CMO_WINE_PREFIX`, and `CMO_WINE_BIN` cover custom installations; `CX_ROOT` follows the selected CrossOver application unless explicitly overridden. Native UI remains selectable with `CMO_NATIVE_UI=0|browser|all`. Portability of paths does not imply compatibility with arbitrary Wine versions: the exact native-module guards remain enforced. See [native implementation and limitations](docs/NATIVE-UI.md).
