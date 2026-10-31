# Changelog

## Unreleased

- Reserve map space behind the selected-unit sidebar for the reviewed game build, without changing the WPF panel content.
- Follow window resizing and sidebar visibility, with an environment opt-out and rollback when control identities change.
- Extend the Windows bootstrap self-test to exercise actual WinForms bounds hooks and restoration.

The controlled in-game test restored the complete panel and usable controls, scrolling, and map navigation. The automatic integration still needs a fresh in-game launch and transition checks.

## 0.1.1

- Fix blank database-entry details on the tested WebView2 Runtime 109 by guarding the unsupported optional Settings8 reputation-setting call.
- Add a separate `CommandBrowser.exe` bootstrap so the guard runs before the existing renderer launcher.
- Embed Harmony 2.3.3 and include its license, without distributing Microsoft or game binaries.
- Preserve browser defaults on the missing-interface path and forward supported interfaces and other failures.
- Install and back up both launcher/configuration pairs, and test rollback after a late replacement failure.
- Default the launch script to the browser bootstrap and retain MSync-off settings.

The isolated API/HTML tests passed, and the user confirmed database details render in the game with the unchanged playable renderer. The standalone renderer rewrite retains its separate regression-testing limitation.

## 0.1.0, standalone source package

- Package the surface, context-state, and Direct2D batching adapters as an independent x64 .NET Framework 4.8 build.
- Replace the build-time DirectN dependency with explicit Windows SDK identities and native factory calls.
- Add a manual pixel/readback and state-restoration probe without game dependencies.
- Move diagnostics into per-session local application data, with GC counters, exception tracing, and draw profiling opt-in.
- Request MSync off in the configurable D3DMetal launcher following an initial successful prototype session.
- Add an installer that preserves the game's configuration and backs up previous CommandMetal files.
- Add locked dependency restore, ZIP packaging, host-side tests, and GitHub Actions artifacts.

The standalone rewrite has not yet completed in-game regression testing. Sidebar clipping and intermittent-freeze investigation remain open. Historical local experiments and proprietary inputs are excluded from this repository.
