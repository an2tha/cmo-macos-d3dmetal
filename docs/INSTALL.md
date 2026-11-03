# Install and play with cmo-on-macos

Use **Command Native.app** for the complete launcher experience. It prepares the graphics and browser components for each launch, offers native or Windows interface modes, and keeps session output in one window. The current app bundle is named Command Native; cmo-on-macos is the project name.

## 1. Prepare Command in CrossOver

You need an Apple Silicon Mac, CrossOver with D3DMetal available, and your own licensed copy of Command: Modern Operations.

1. Install CrossOver, then install Command through your normal game installer or Steam inside a bottle.
2. Install the game's required dependencies in the same bottle, including **.NET Framework 4.8**. The .NET 10 SDK used to build this project is a separate Mac tool and does not replace that Windows runtime.
3. Keep the game installation intact. You will select its folder in the launcher; do not copy individual game DLLs into the app bundle.

The reviewed setup uses Command v1.10 build 1900.20 and CrossOver 26.3. The native bridge checks exact native-module hashes, so another CrossOver build may need a compatibility update. [Tested versions and limits](COMPATIBILITY.md).

## 2. Get Command Native.app

**Current distribution:** the published `v0.1` release contains the older manual adapter ZIP. The Windows Actions workflow also produces that ZIP. Neither includes the app. Build the macOS app from source using the steps below until an app package is published on [Releases](https://github.com/an2tha/cmo-on-macos/releases).

Install these build tools on your Mac:

- Xcode command-line tools: run `xcode-select --install` in Terminal and finish the installer if they are not installed already.
- [Python 3.10 or newer](https://www.python.org/downloads/macos/).
- [.NET 10 SDK for macOS](https://dotnet.microsoft.com/en-us/download/dotnet/10.0). Choose Arm64 on Apple Silicon. Install the SDK, not just the runtime.

Open Terminal and run:

```sh
git clone https://github.com/an2tha/cmo-on-macos.git
cd cmo-on-macos
python3 scripts/build_native.py
```

Wait for the build to finish. Its final output is the path to `dist/Command Native.app`. Open it with:

```sh
open 'dist/Command Native.app'
```

You can copy the complete app bundle from `dist` to Applications using Finder. Keep the bundle intact. No separate Python installer or copying `.exe` files into the game folder is needed for this route. Python, Xcode and the SDK are build requirements, not requirements for using the completed app.

If `dotnet` is not on PATH, use `python3 scripts/build_native.py --dotnet /absolute/path/to/dotnet`. More options are in [BUILDING.md](BUILDING.md).

The current app is locally ad hoc signed, not Apple-notarized. If macOS blocks an app you built or obtained from a source you trust, follow [Apple's instructions for opening an unnotarized app](https://support.apple.com/en-nz/102445). Do not disable system-wide security checks.

## 3. Find the game folder and bottle name

In CrossOver, select the bottle that contains Command and note its exact name. Use **Bottle Actions > Open C: Drive** to open its files in Finder. See [CodeWeavers' illustrated instructions](https://support.codeweavers.com/en_US/opening-a-bottles-c-drive).

Find the folder containing **both `Command.exe` and `Command.exe.config`**. Typical locations inside the bottle's C: drive are:

| Installation | Example folder inside C: |
| --- | --- |
| Standalone installation | `Games/Command - Modern Operations` |
| Steam installation | `Program Files (x86)/Steam/steamapps/common/Command - Modern Operations` |

These are examples. A custom Steam library or installer destination can be elsewhere. Select the folder containing the actual executable, not `steamapps`, the whole C: drive or a shortcut.

For a bottle named `Steam` in CrossOver's default location, the standalone example has this Mac path:

```text
~/Library/Application Support/CrossOver/Bottles/Steam/drive_c/Games/Command - Modern Operations
```

Finder's **Go > Go to Folder…** accepts that path. Replace `Steam` and the installation subfolders as needed. If you moved CrossOver's bottle storage, use the location opened by CrossOver instead.

## 4. Configure and launch

1. Save and close the current game.
2. In CrossOver, select the game's bottle, choose **D3DMetal** and turn **MSync off**. When changing synchronization settings, save and close every application in that bottle and use **Quit All Applications** before proceeding. The launcher will not do this for you.
3. Open **Command Native.app**.
4. Click **Choose…** beside **Game folder** and select the folder from step 3.
5. Set **CrossOver bottle** to its exact name. The default `Steam` is not a requirement.
6. Select an **Interface**:

| Choice in the app | What to expect |
| --- | --- |
| Native controls and browser (development) | Default selection. Native supported controls and WebKit views; custom surfaces still use the game's implementation. |
| Native browser only (development) | Native web content with the existing Windows controls. Useful for isolating control issues. |
| Original Windows interface | Windows controls and WebView2 with the targeted browser compatibility guard. Graphics and sidebar fixes still load. |

Click **Launch Command**. Progress and errors appear in the lower part of the app window. Leave the launcher open until the game exits. Interface changes take effect on the next launch.

The bundled script requests D3DMetal, drawing batching, sidebar synchronization, MSync off, ESync off and a 60 FPS cap. The focus and native barrier guards remain opt-in in this repository. These settings do not establish a stable 60 FPS result or resolve every freeze.

## 5. Check the first session

Load a scenario, pan and zoom the map, select a unit, scroll its status panel and open a database entry. Check that clicks land under the pointer and descriptions appear. Save and exit normally.

For later sessions, open this app again and verify the game folder, bottle and interface before launching. The current app uses its defaults or environment overrides when opened; it does not save those choices as preferences. Adding it to the Dock makes it easy to find.

The original Command shortcut in CrossOver starts the game without these adapters. Use the app when you want the cmo-on-macos setup. If an interface problem appears, save and close the game, relaunch the app with **Original Windows interface**, and consult [troubleshooting](TROUBLESHOOTING.md).

## Files, saves and logs

The app creates a per-session runtime in `~/Library/Application Support/CommandNative/Runtime`. It links the installed game assets and copies its own adapters plus the complete original game configuration. The installed executables are not overwritten. Runtime links are removed when the launched process exits; the underlying game files remain.

**Saves and game data are shared with your installed game.** This is not an isolated save copy. Back up important saves using your usual game backup process.

Launcher output is retained in `~/Library/Application Support/CommandNative/Logs`. Native Wine output is retained in `~/Library/Logs/CommandMetal`. Managed diagnostics are inside the bottle at `%LOCALAPPDATA%\CommandMetal\logs`. [Finding and sharing logs](TROUBLESHOOTING.md#logs).

## Update or remove

Save and close Command and the launcher before replacing the app. Build the newer revision or obtain its published app package, then replace the whole app bundle. The next launch prepares a fresh runtime using that app's adapters and the installed game's current configuration. A game or CrossOver update can require a new reviewed compatibility profile.

To remove the app, close the game and delete `Command Native.app`. The installed game remains available through CrossOver. You can separately remove its launcher logs and temporary runtimes under `~/Library/Application Support/CommandNative` after all sessions have closed. Do not delete the CrossOver bottle or game saves to uninstall this launcher.

## Advanced: manual adapter installation

This is the alternative for users who want the shell launcher rather than the app. It is not an extra app setup step.

Download `CommandMetal-win-x64.zip` from an appropriate [release](https://github.com/an2tha/cmo-on-macos/releases), or download the `CommandMetal-win-x64` artifact from a successful **Build CommandMetal** [Actions run](https://github.com/an2tha/cmo-on-macos/actions/workflows/build.yml). Actions downloads may have an outer artifact ZIP; extract that and then the contained package. Use that revision's documentation because older releases may lack current fixes.

After saving and closing Command, open Terminal in the extracted package and run:

```sh
python3 scripts/install.py --game-dir "$HOME/Library/Application Support/CrossOver/Bottles/Steam/drive_c/Games/Command - Modern Operations"
zsh Play-D3DMetal.command
```

Replace the example game path with yours. For another bottle or game location, launch with:

```sh
CMO_BOTTLE='My Bottle' \
CMO_GAME_DIR='/absolute/path/to/Command - Modern Operations' \
CROSSOVER_APP='/Applications/CrossOver.app' \
zsh Play-D3DMetal.command
```

The installer writes `CommandMetal.exe`, `CommandBrowser.exe` and their configurations beside the game. Configurations are exact copies of `Command.exe.config`. Previous adapter files are backed up under `CommandMetal-backups`. It refuses installation while a recognized Command process is running. A source checkout uses `zsh scripts/Play-D3DMetal.command` after building and installing.

The shell route defaults to the Windows interface. Starting `CommandMetal.exe` directly bypasses browser and sidebar setup; start through the supplied script instead. Native UI mode also needs the native library and reviewed profile, which the app packages automatically.

To remove a manual installation, save and close Command, then remove only the two adapter executables and their `.exe.config` files, or restore the previous files using a backup's `manifest.json`. Files recorded as originally absent should be removed. Do not remove `Command.exe`, `Command.exe.config`, `DirectN.dll` or the game's browser SDK. Start the original game through CrossOver afterward.
