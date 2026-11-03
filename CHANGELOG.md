# cmo-on-macos changelog

## Unreleased

- Use cmo-on-macos as the project name and make `Command Native.app` the main setup route. Add a complete installation guide covering bottle/game-folder selection, interface modes, logs, updates and removal. Preserve actual component filenames and document that current release/CI ZIPs do not include the app.
- Add the universal Swift/AppKit launcher and embedded AppKit/WebKit interface, with a per-session runtime that preserves the installed game executables. Native controls remain a development option, with browser-only and original Windows modes selectable in the app. See [coverage and validation](docs/NATIVE-UI.md).

- Publish five-run synthetic drawing benchmarks, raw measurements and a reproducible command. Add warmup and completed-work timing to GraphicsProbe; share the batching callback delegate types to fix the probe's .NET delegate round-trip mismatch.
- Preserve native Wine output in a separate log for each launch, including launches through an app, and detect experimental bootstrap processes before starting a second game.
- Add experimental native serialization for the reviewed CrossOver process-wide barrier, with an exact module hash check and a four-thread manual stress test. The original barrier remains intact. Extended in-game validation is pending.
- Add opt-in selected-unit sidebar layout for the reviewed game build.
- Synchronize map bounds through the game's normal renderer resize routine, preserving map click coordinates.
- Wait for an idle renderer and next-frame camera updates; restore bounds and viewport together on failure.
- Defer rollback during a resize, reject replaced map targets, and retry transient restoration failures with backoff.
- Add an opt-in guard for the captured MainForm message-handler activation wait, preserving other activation callers.
- Extend Windows framework self-tests with viewport failures, camera lag, busy rendering, reentrant rollback and focus caller boundaries.

The controlled panel/renderer test passed, then a later whole-window freeze was captured in `Form.Activate` / `SetForegroundWindow`. The guard matched that path during a subsequent combined automatic test. The user confirmed correct panel/input behavior through resizing, sidebar visibility changes, app switching, unit/database dialogs and keyboard focus, without another freeze during the check. A subsequent long-session Rosetta assertion was captured with two threads in the original native barrier. The isolated serialized barrier passed 40,000 calls and a combined graphics/GC probe, while longer in-game stability remains unproven. The public script enables the sidebar fix; focus and native barrier guards remain opt-in. The playable prototype and standalone renderer rewrite retain separate validation boundaries.

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
