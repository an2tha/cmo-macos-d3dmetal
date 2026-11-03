# cmo-on-macos native interface (development)

`Command Native.app` is the app entry point for cmo-on-macos and a universal Swift/AppKit launcher. It loads an x86_64 Objective-C AppKit/WebKit library **inside the game's Wine process**. `WKWebView`, buttons, and other native views become actual children of the matching Wine window's content view. This is not a separate browser window or a screenshot streaming renderer.

The simulation, proprietary game models, custom WPF views, and DirectX map still run in Wine. This is an incremental native interface implementation, not a standalone port of the simulation or a complete replacement for every Windows control.

For game-folder selection, prerequisites, first launch, updates and removal, use
[the installation guide](INSTALL.md). This document covers native implementation
and validation. Choosing the app does not require choosing experimental native
controls; its **Original Windows interface** mode uses the same app launcher.

## Build and run

The current Windows CI artifact and published `v0.1` release do not contain this
app. Build it locally on macOS until a macOS package is published.

On macOS with Xcode command-line tools and the .NET SDK:

```sh
python3 scripts/build_native.py
open 'dist/Command Native.app'
```

Use **Choose…** to select the folder containing `Command.exe` and `Command.exe.config`, enter the exact **CrossOver bottle** name, select an **Interface**, then click **Launch Command**. Custom choices are not currently saved between app launches. The app creates a separate per-session directory in `~/Library/Application Support/CommandNative/Runtime`, links the installed game assets, and copies the new adapters and the complete original game configuration there. The installed game binaries are not overwritten. Normal game data and saves still use the game's existing locations. Session logs are retained in `~/Library/Application Support/CommandNative/Logs` and the normal CommandMetal log locations. The temporary runtime is removed after the launched process exits.

The app is locally ad hoc signed, not notarized for public distribution. The embedded library is x86_64 because the current game and Wine process are x86_64, including under Rosetta on Apple Silicon. The launcher itself contains both arm64 and x86_64 code.

To build alongside an active session, use a separate bundle and app identity:

```sh
python3 scripts/build_native.py --dotnet /path/to/dotnet \
  --output 'dist/Command Native Refined.app' --bundle-id local.command.native.refined
```

Quit the current game normally before launching a new game from that candidate. Building it does not update views in an already running process.

## Environment switch

| Variable | Behavior |
| --- | --- |
| `CMO_NATIVE_UI=0` | Original Windows GUI and WebView2, including the existing Settings8 guard. Default for the existing shell launcher. |
| `CMO_NATIVE_UI=browser` | Embedded WebKit rendering with the existing Windows controls. |
| `CMO_NATIVE_UI=all` | Embedded WebKit and native peers for supported standard controls. Default selection in the new app. |
| `CMO_NATIVE_HOST` | Absolute macOS path to `libCommandNativeHost.dylib`. The app supplies this automatically. |
| `CMO_WINE_PREFIX` | Absolute macOS bottle directory, used to resolve drive-letter resource mappings. The shell launcher supplies it from the bottle name. |
| `CMO_NATIVE_TRACE=1` | Log browser operations, requested URLs, HTML lengths, and navigation results for development. Page HTML and JavaScript bodies are not logged. |
| `CMO_WINE_BIN=/absolute/path/to/wine` | Use a local CrossOver launch wrapper for development or recovery. Defaults to the installed CrossOver wrapper. The native bridge still verifies the actual Wine libraries. |

With a separately installed candidate adapter, set the mode and library before running `Play-D3DMetal.command`. Changes take effect on the next game launch. Native startup failures are explicit; the launcher does not silently mix in an Edge fallback after native mode was requested. Choosing `0` on the next launch restores the original path.

## Native coverage

The Harmony patch redirects the exact reviewed WebView2 SDK API used by this game: shared asynchronous initialization, core/settings access, HTML and URL navigation, virtual-host folder mappings, script evaluation, zoom, navigation cancellation, navigation completion, and web messages. The game methods and protected IL are not rewritten. Detached browsers wait for their parent window. Every WebKit operation runs on the macOS main thread. Events return to the owning WinForms UI thread.

Supported standard GUI peers include buttons and toolbar icons, checkboxes, radio buttons, labels, plain/multiline/password text fields, numeric text entry, fixed-choice combo boxes, sliders, progress bars, top-aligned single-row tab strips, single-selection lists, basic trees, and toolstrip/menu items. Native actions update the original controls and invoke their existing handlers. Windows painting is suppressed only for controls whose native attachment was acknowledged, and restored if synchronization fails. A 100 ms coalesced snapshot synchronizes text, state, visibility, clipping, size, and parent geometry. Unchanged snapshots are not sent.

Toolbars use borderless AppKit controls inside the original item rectangles. Top-level menus retain native pull-down tracking without the large popup arrow. Snapshots include the item's font, foreground, strip background, display style, intended image dimensions, tooltip, and checked state. Font points are converted using the WinForms control DPI and the native window scale. Small font-metric differences can reduce a toolbar caption by at most ten percent to preserve its full title. Overflow items retain the original overflow path instead of overlaying stale rectangles on the main toolbar. Ordinary dialog buttons and choice controls keep their native bezels.

Version 0.2.4 samples each toolbar renderer's background-only paint into a three-pixel square once per strip snapshot. Native backgrounds and Aqua/Dark Aqua appearance therefore follow painted colours even when `BackColor` and a custom `ProfessionalColorTable` still report white. Full strip coordinates preserve gradient placement; icons, captions, hover and selection are excluded. The Wine fixture verifies both menu and tool strips through dark, light, and dark transitions with deliberately mismatched metadata. Trace mode records palette changes without dumping control contents.

The game trace identified `DarkUI.Renderers.DarkToolStripRenderer` and `DarkUI.Renderers.DarkMenuRenderer` painting RGB 60/63/65 while the affected controls declared white, matching the regression case.

Version 0.2.5 preserves the encoded pixel aspect ratio of toolbar artwork. Image-only banners use the requested image height and available item width instead of stretching into the strip's shared square icon box. Images next to captions fit proportionally within their requested box. The AppKit fixture uses the installed 114 by 32 command banners with square size metadata, checks proportional shrinking in narrow items and restoration after resizing, and renders dark and light previews. The image-sizing change does not alter command hit areas or event routing.

Version 0.2.6 presents the reviewed MainForm time row with the selected compression value (such as `1×` or `5×`), the existing time-mode value, and a `Play` or `Pause` action with a native symbol. Static `Time Compression:` and `Time Mode:` prefixes are removed from the native presentation. Compression follows the current scenario's public state, with checked menu items as a fallback; playback follows the original button's start/pause image state. Commands still invoke the original controls. The row reclaims the label space without changing the game's layout or simulation state, and retains the existing layout if overflow or custom hosts prevent a complete mirror.

Wide command banners in 0.2.6 fit the available button height with uniform scaling. The shortcut strip's vertical item margins become usable image space while its horizontal slots remain unchanged. The fixtures cover real banner artwork with the shared 16 by 16 icon-box metadata, constrained widths, and restored widths. The Wine fixture also verifies selected-rate changes, external state updates, play/pause transitions, disabled playback, and dark/light transitions. Compression disappeared in the real 0.2.6 toolbar, although its trace recorded rate and playback changes. The original fixture did not cover a hidden source button or later activation of the compact layout.

Version 0.2.7 replaces the visible compression caption slot with a native selector backed by the original compression menu. It no longer depends on the source button being visible. Native peers are recreated when their control kind changes, and a newly attached toolbar background is placed below overlapping controls. The Wine fixture verifies the selector's native hit testing with a hidden source, original menu event routing, fallback to a label for a custom host, return to an interactive selector, and playback remaining above the new background. These checks and the standalone toolbar checks pass. The real-game trace the session browser log records `ToolStripLabel1` as the replacement selector, `1× / Play`, and 35 attached MainForm peers. The loaded library was verified as the 0.2.7 bundle.

Custom WPF controls, owner-drawn surfaces, rich editors, images, link labels, complex grids, editable combo boxes, multi-selection lists, image/checkbox trees, custom tab layouts, and unsupported toolstrip hosts retain their existing implementation. This boundary is intentional: guessing how a proprietary control works would discard its semantics. Native menu snapshots do not yet reproduce all dynamically populated opening handlers, and cross-boundary keyboard focus, mnemonics, accessibility, DPI changes, modal dialogs, and all game screens need broader validation. Keep `all` a development option.

WebKit uses an ephemeral data store. Mapped HTML/CSS/JavaScript references to declared virtual hosts are rewritten to a custom resource scheme. Paths are normalized and symlinks resolved before enforcing the mapped folder boundary. This is not complete WebView2 origin/CORS emulation. Chromium-only APIs, host objects, downloads, printing, persistent profiles, and arbitrary WebView2 extensions are not implemented. Settings that have no equivalent in this host, including Edge reputation and browser accelerator options, do not turn on an Edge runtime. Native WebKit keeps its platform behavior. Remote pages cannot send game messages through the local document bridge.

Local navigation accepts absolute Windows drive paths and `file:` URLs, including Wine's `Z:` mapping. Each explicitly opened page receives a resource host scoped to its containing folder. Relative HTML, CSS, and images load through that host, while SDK navigation/message events and `Source` retain Windows file URLs. Percent escapes are decoded once. Local paths are not passed to WebKit as unknown drive-letter schemes. UNC paths and resources outside the declared folder remain unsupported unless separately mapped.

Virtual-host folder mappings resolve empty and relative paths against the executable's directory. The game's `RenderCustomHTML` maps `Root` to an empty path before submitting inline preview HTML. Passing that path directly to `Path.GetFullPath` in 0.2.2 threw an exception and stopped the preview. Version 0.2.3 preserves this accepted SDK input. The Wine fixture repeats the `dbimages.invalid`, empty `Root`, and `TopWritable` mapping sequence, then verifies inline HTML and separately mapped styles while the working directory differs from the executable directory. Trace mode records mapping arguments before normalization to make failures visible.

## Wine boundary and ownership

`NativeHost.cs` verifies the exact native `ntdll.so` and `winemac.so` hashes before using their reviewed layout. It locates Darwin `dlopen`/`dlsym` import stubs from the verified ntdll image, creates a small read/execute loader trampoline, and invokes it through Wine's exported `__wine_unix_call`. Wine performs the ABI and stack transition. The AppKit bridge does not modify Wine's service table. With `CMO_SERIALIZE_BARRIERS=1`, it uses the guard's recorded original barrier address for image identification, retaining the same hash and layout checks.

The library exports one versioned, bounded message entry point. Requests copy their bytes before returning; AppKit receives work through its main run loop. Native replies are polled without synchronous UI waits or reverse CLR callbacks from AppKit. The loaded library and bridge table remain rooted until process exit. Disposing a browser removes its views and message handler, cancels pending navigation decisions, and stops its geometry timer.

Window matching uses the verified driver and Objective-C runtime metadata for the `WineWindow` class's `hwnd` ivar, checking its type and bounds. It does not assume a hard-coded Objective-C object offset. The native view uses the owning form's client geometry and clips against ancestor client rectangles. Arbitrary Wine/CrossOver releases are not supported without a new reviewed profile.

Relevant upstream contracts: [Wine Unix library interface](https://github.com/wine-mirror/wine/blob/wine-10.0/include/wine/unixlib.h), [Wine Cocoa window class](https://github.com/wine-mirror/wine/blob/wine-10.0/dlls/winemac.drv/cocoa_window.h), [WKWebView](https://developer.apple.com/documentation/webkit/wkwebview), and [Harmony](https://harmony.pardeike.net/v2/).

## Validation

Run the existing Python tests and locked managed build. The native build also compiles both launcher architectures and the x86_64 library, then signs the local app.

The toolbar regression fixture runs independently of Wine and can be used while the game is open:

```sh
python3 scripts/test_native_toolbar.py --game-dir '/path/to/Command - Modern Operations'
```

It uses the installed game's toolbar artwork without bundling those assets, renders dark and light previews at 1000 and 1600 point window widths, and checks caption fit, image area, native menu and nested-menu command routing, disabled buttons, menu states, keyboard focus, and cleanup. Previews and results are saved under `.tools/native-toolbar`. This fixture checks AppKit presentation and event identities, not a live game session or the Wine bridge.

With no game session running, invoke the built `CommandBrowser.exe --native-ui-self-test` in the reviewed bottle, setting:

- `CMO_NATIVE_HOST` to the built library's macOS path.
- `CMO_WEBVIEW_SDK_DIR` to the installed game's Windows path.
- `CMO_WINE_PREFIX` to the bottle's macOS path.
- `CMO_NATIVE_SELF_TEST=1` to enable fixture-only native test commands.
- Optionally, `CMO_NATIVE_TEST_LOG` to a Windows output path and `CMO_NATIVE_TEST_SNAPSHOT` to a macOS PNG path.
- Optionally, `CMO_NATIVE_TEST_MODAL=1` to exercise a modal dialog, `CMO_SERIALIZE_BARRIERS=1` to test both native bridges together, and `CMO_NATIVE_TEST_PAGE` to a Windows path for a real installed HTML description.

The fixture loads the installed SDK, not the game assembly. It exercises the real Harmony patches, Wine ABI bridge, embedded WebKit, local CSS mapping, JavaScript results, message callbacks on the game UI thread, navigation cancellation, a native button invoking the original WinForms event, resize synchronization, and browser disposal. It also requests initialization before assigning a parent. Its 45-second UI watchdog is not a guarantee against a native process deadlock. Cached AppKit snapshots may omit WebKit's remote rendering layer after resize; use a live visual check for compositing.

Passing this fixture does not certify every game dialog or sustained gameplay. Record real-game startup separately and do not describe untested custom surfaces as native.

A 0.2.5 startup stalled with Rosetta's `expected saved LR to be in translated code` assertion despite the enabled barrier guard. The native GUI had not attached and no toolbar palette entries had been logged. The assertion preceded native sampling of the stalled process. This does not establish the assertion's cause; it remains a runtime stability limitation separate from the image-sizing regression tests. The failed startup was terminated and retried without changing barriers, garbage collection, or graphics.

The version 0.2.3 trace the session browser log records browser 2 attached to `Command.LoadScenario` and successful navigation to `Scenarios/LIVE/AIF_Desc.html`. The same session accepted the empty `Root` mapping and rendered the message log's inline HTML. This validates the observed preview flow, not every scenario or sustained gameplay.

A captured `LoadScenario` request used a raw `Z:\\...\\Scenarios\\Chains Of War\\GodofWarintro.html` path. Version 0.2.1 rejected its drive letter as a URL scheme. The updated modal Wine fixture renders that installed description through WebKit, with its relative `COW.css` stylesheet. It also checks raw `C:` paths, `Z:` file URLs via `Source`, literal percent/hash characters, relative images, and public navigation/message URL preservation. This is a rendering and bridge regression check, not a gameplay test.

On the reviewed installation, a startup-only game check reached `Command.MainForm`, attached 35 native control peers, and embedded a browser in `Command.MessageLogWindow_RawText`. No `msedgewebview2` process was present in that check. No scenario was loaded or played. The updated fixture also passed after enabling redundant Windows paint suppression.
