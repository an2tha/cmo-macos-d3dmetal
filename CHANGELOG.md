# Changelog

## Unreleased

- Add opt-in selected-unit sidebar layout for the reviewed game build.
- Synchronize map bounds through the game's normal renderer resize routine, preserving map click coordinates.
- Wait for an idle renderer and next-frame camera updates; restore bounds and viewport together on failure.
- Defer rollback during a resize, reject replaced map targets, and retry transient restoration failures with backoff.
- Add an opt-in guard for the captured MainForm message-handler activation wait, preserving other activation callers.
- Extend Windows framework self-tests with viewport failures, camera lag, busy rendering, reentrant rollback and focus caller boundaries.

The controlled panel/renderer test passed, then a later whole-window freeze was captured in `Form.Activate` / `SetForegroundWindow`. The guard matched that path during a subsequent combined automatic test. The user confirmed correct panel/input behavior through resizing, sidebar visibility changes, app switching, unit/database dialogs and keyboard focus, without another freeze during the check. Longer stability remains unproven; both public UI flags default to off. The playable prototype and standalone renderer rewrite retain separate validation boundaries.

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
