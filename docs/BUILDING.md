# Build and continuous integration

## Prerequisites

Install a .NET 10 SDK and Python 3.10 or newer. The resulting executables target **.NET Framework 4.8, Windows x64**. The SDK version is a build requirement, not an additional runtime to install inside CrossOver.

The build uses Microsoft's `Microsoft.NETFramework.ReferenceAssemblies` 1.0.3 to provide framework reference assemblies outside Windows. The browser bootstrap also uses `Lib.Harmony` 2.3.3, embedded as a resource so installation needs no additional managed DLL. Lock files pin resolved dependencies and content hashes. No game DLL, proprietary configuration file, Apple library, or CrossOver installation is required for compilation.

Both projects explicitly target the `win-x64` runtime so locked restore uses the same dependency graph on every host. Runtime identifiers are not appended to output directories; packaging continues to read `bin/Release/net48`.

## Commands

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

`.github/workflows/build.yml` runs on pushes, pull requests, and manual dispatch. It uses a Windows runner, installs .NET 10 and Python, runs host-side installer/package tests, builds the three executables, runs both launcher `--version` checks without the game installed, and verifies the bootstrap's embedded dependency by applying and removing a prefix on a self-test method. It uploads the ZIP as `CommandMetal-win-x64` with 30-day retention.

Official actions are pinned to commit SHAs. The workflow requests read-only repository permission, does not require secrets, and does not create releases or publish to a package registry. GitHub's [artifact documentation](https://docs.github.com/en/actions/tutorials/store-and-share-data) explains how to download an artifact from a workflow run.

CI verifies buildability and packaging. The Windows runner does not provide the user's CrossOver/D3DMetal environment, so a green build does not certify map rendering, performance, or freeze prevention. The workflow itself can only be exercised after the repository is pushed to GitHub.

## Manual graphics validation

Save and close the game first. Run the built `GraphicsProbe.exe` in the intended CrossOver bottle with the same D3DMetal and synchronization environment as the launcher. A basic run draws a red rectangle over blue, checks two GPU readback pixels, verifies primitive-topology restoration after EndDraw, and presents.

For repeated drawing, explicit Flush checks, and concurrent managed allocations, pass:

```text
--frames=2000 --draws=64 --flush --gc-stress
```

The probe returns a nonzero status on validation failure. Wine's underlying `E_NOTIMPL` Flush result is tolerated only while the topology restoration check still passes. The probe has a 45-second drawing-loop limit, not a guaranteed process timeout if a native call hangs. Never launch it into an active game session.

After probe success, use a separate saved-game session to check scenario load, map pan/zoom, selection, dialogs, save/load, and longer play. Record backend and synchronization settings with the result.
