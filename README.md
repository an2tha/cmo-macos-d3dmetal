# CommandMetal

An experimental D3DMetal compatibility launcher for **Command: Modern Operations** on macOS with CrossOver.

CommandMetal supplies the DXGI surface and D3D11 context-state behavior used by Wine's Direct2D renderer. It runs the original `Command.exe` in the same process, with process-local graphics adapters installed first. The game executable and its libraries are not patched on disk.

The included `CommandBrowser.exe` bootstrap also handles an optional WebView2 API missing from Runtime 109, allowing the Database Viewer's details pane to initialize. It then starts `CommandMetal.exe` in the same process. Use the supplied launch script to enable both fixes.

**Status:** the original prototype renders a usable map. Some lag and sidebar clipping remain. Turning MSync off has produced an initial user report of crash-free play, but intermittent freezes are still under investigation. This standalone rewrite builds without game files and needs its own in-game validation before it can replace the prototype with confidence.

## Requirements

- A licensed installation of Command: Modern Operations and its required .NET Framework 4.8 dependencies in CrossOver.
- An Apple Silicon Mac and CrossOver with D3DMetal available.
- Python 3.10 or newer for the installer. A .NET 10 SDK is needed only when building from source.

The prototype was exercised with Command v1.10, build 1900.20, CrossOver 26.3, D3DMetal 4.0b2, macOS 27.2, and an Apple M4 Max. Other versions and hardware are untested. See [compatibility and limitations](docs/COMPATIBILITY.md).

## Download or build

After this repository is pushed to GitHub, open **Actions > Build CommandMetal**, select a successful run, and download the `CommandMetal-win-x64` artifact. Extract the artifact, then extract the included `CommandMetal-win-x64.zip`.

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

The normal CrossOver **Command** icon starts the original executable without these adapters. To use both fixes through CrossOver's Run Command dialog, select the installed `CommandBrowser.exe` and use the same graphics and synchronization settings. Starting `CommandMetal.exe` directly applies only the graphics adapters.

## Remove or roll back

Save and close Command, then remove only `CommandMetal.exe`, `CommandBrowser.exe`, and their `.exe.config` files, or restore those files from a `CommandMetal-backups` subdirectory. Each backup's `manifest.json` records which files previously existed: restore those, and remove newly added files marked as originally absent. Launch the original `Command.exe` afterward. The installer does not replace `Command.exe`, `DirectN.dll`, the WebView2 SDK, or `Command.exe.config`.

## Development

- [How the adapters work](docs/ARCHITECTURE.md)
- [Building and GitHub Actions](docs/BUILDING.md)
- [Diagnostics and troubleshooting](docs/TROUBLESHOOTING.md)
- [Known issues and compatibility](docs/COMPATIBILITY.md)
- [Contributing and test expectations](CONTRIBUTING.md)

This repository includes adapter source, a manual drawing probe, build and install tools, and documentation. It excludes the game, game libraries, saved scenarios, local investigations, and experimental sidebar or bitmap-cache patches. It is an independent project, unaffiliated with the game's publishers, CodeWeavers, or Apple. The adapter source is under the [MIT license](LICENSE); game and platform components retain their own licenses.
