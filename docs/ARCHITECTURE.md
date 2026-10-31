# Adapter architecture

## Startup

The launch script starts `CommandBrowser.exe`, a Windows x64 .NET Framework 4.8 bootstrap. It registers a resolver for its embedded Harmony dependency, installs the targeted WebView2 capability guard and deferred sidebar layout hook, and invokes `CommandMetal.exe` in the same process. Its own `.exe.config` must contain the game's complete configuration because loading another assembly does not switch process configuration.

`CommandMetal.exe` sets its working directory to its installation directory, optionally starts diagnostics, creates a hidden native seed window and a BGRA Direct3D 11 swap chain, and installs the adapters before invoking the original managed `Command.exe` entry point. A native `STATIC` window avoids initializing WinForms before the game's own protected startup code.

The adapters call the Windows COM ABI directly. `NativeGraphics.cs` contains a small set of interface identifiers and a raw `D2D1CreateFactory` declaration. There is no compile-time or runtime dependency on the game's `DirectN.dll`. The game continues to load its own dependencies normally.

## Files

| File | Responsibility |
| --- | --- |
| `CommandMetal.cs` | Original game entry-point invocation and startup failure reporting |
| `MetalCompat.cs` | Hidden seed window, D3D11 device/swap chain, adapter installation |
| `SurfaceCompat.cs` | `IDXGISurface` wrapper for swap-chain back-buffer textures |
| `ContextStateCompat.cs` | Emulated context-state objects, binding snapshots, reference ownership, batching |
| `D2DBatchCompat.cs` | Direct2D `BeginDraw`, `EndDraw`, and `Flush` hooks |
| `NativeGraphics.cs` | Windows SDK interface identities and small native helpers |
| `Diagnostics.cs` | Session logs and opt-in exception/GC counters |
| `D2DPerf.cs` | Opt-in timing of selected Direct2D drawing calls |
| `GraphicsProbe.cs` | Manual pixel readback and state-restoration integration test |
| `BrowserBootstrap.cs` | Embedded dependency resolution and browser guard before renderer startup |
| `PanelLayout.cs` | Deferred selected-sidebar identification, map width reservation, and rollback |
| `WebViewGuard.cs` | Narrow capability check for an optional WebView2 Settings8 setter |

## WebView2 capability guard

The tested game ships WebView2 SDK 1.0.2151.40 while its Wine bottle has Runtime 109. The SDK's `IsReputationCheckingRequired` setter requests `ICoreWebView2Settings8`, which is unavailable in that runtime. The failed interface cast is wrapped in `NotImplementedException` and can interrupt database-pane initialization.

The bootstrap validates the SDK file SHA-256, the actual loaded module MVID, and the loaded file hash. It then installs a Harmony prefix on that single managed setter. The prefix reads the SDK wrapper's borrowed `_rawNative` COM object and calls `QueryInterface` for Settings8 (`9e6b0e8f-86ad-4e81-8147-a9b5edb68650`). Only `E_NOINTERFACE` skips the optional setter, retaining the runtime's browser defaults. Supported interfaces and other failures are forwarded to the original setter. Acquired native references are released; the borrowed RCW is never released through `ReleaseComObject`.

The validated Core.dll SHA-256 is `4045f33c0cc195950aa92908bab5f904f41f04f54cb2219c11ddddf0268e1a4d`, with MVID `293b524b-c2df-42fc-b7b5-f77c1008e0b7`. Unknown SDK builds are logged and left unchanged. No Microsoft DLL is rewritten. Harmony 2.3.3 is embedded in the bootstrap; its [MIT license](../third-party/Harmony-LICENSE.txt) is included in the package.

Microsoft lists Settings8 and this property in the [Runtime 112 release notes](https://learn.microsoft.com/en-us/microsoft-edge/webview2/release-notes/sdk/1-0-1722-45). The workaround leaves existing reputation-checking behavior in place when the control API is unavailable.

## Selected-unit sidebar layout

The tested layout placed a 270-pixel selected-unit ElementHost over full-width native map windows. The WPF body measured and loaded normally. Reserving space in both `Panel_WW` and `WorldWindow` restored the complete panel in a controlled in-game test.

`PanelLayout` verifies the Command.exe SHA-256 (`53fc37bb0b634adbe46f4ddf3a87c99847d273cc6f1d980ba9796da6389d25fc`). A base `Form.OnShown` postfix waits for the real `Command.MainForm`, then discovers the exact selected host, WPF child, map, and map parent from reviewed fields on its UI thread. No forms or WPF controls are constructed before the game's protected startup. The hover host is excluded.

A `Control.SetBoundsCore` prefix caps widths only on those two exact map objects. Coalesced UI events update the reserved width on resize and sidebar movement/visibility changes. Hidden or offscreen sidebars restore the full map width. The discovery timer stops after attachment; there is no per-frame visual-tree scan. Changed identities or parent relationships disable enforcement, unsubscribe events, and restore widths on the original controls where safe. The inactive bounds prefix stays rooted until process exit.

`CMO_PANEL_LAYOUT=0` skips installation. Unknown game hashes are logged and left unchanged. The automatic integration still needs its fresh-launch and transition validation; the confirmed in-game result applies to the controlled width-reservation test.

## Surface adapter

The affected D3DMetal version does not expose the `IDXGISurface` interface expected by Wine Direct2D on a swap-chain texture. `SurfaceCompat` hooks `IDXGISwapChain::GetBuffer` (slot 9). For surface requests, it obtains an `ID3D11Texture2D` and returns a small, reference-counted COM wrapper implementing the surface interface. It forwards device, description, and private-data operations to that texture where applicable. CPU mapping of the back buffer is unsupported.

## Context-state adapter

Wine Direct2D uses `ID3D11Device1::CreateDeviceContextState` (slot 47) and `ID3D11DeviceContext1::SwapDeviceContextState` (slot 131). The affected backend lacks the behavior needed by the game. The adapter supplies reference-counted state objects and snapshots the binding slots Wine Direct2D changes.

State dictionaries are protected by a managed lock. Native graphics operations and native reference releases occur outside that bookkeeping lock. Static delegate roots keep reverse COM callbacks alive. Per-thread function-pointer caches avoid recreating delegates on every state operation.

This is a targeted Wine Direct2D compatibility layer. It does not implement the complete D3D11.1 state API. See [limitations](COMPATIBILITY.md).

## Drawing batches

Restoring all changed bindings around every small Direct2D operation is expensive. `D2DBatchCompat` hooks render-target slots 48 (`BeginDraw`), 49 (`EndDraw`), and 42 (`Flush`). Within a recognized drawing batch, the adapter keeps the drawing state physically bound and accounts for paired logical state changes. It restores the game state at the batch boundary. Nested, unexpected, or externally changed state falls back to the ordinary path.

The underlying `Flush` HRESULT is preserved. On the tested Wine version it can be `E_NOTIMPL`, even when the adapter correctly restores graphics state. This result must not be silently converted to success.

## Hook lifetime

Hooks modify COM vtable entries in this process. They do not change installed DirectX libraries. Delegates and original function pointers must remain rooted for the lifetime of any callback. The normal launcher does not attempt to unload active hooks while the game is running. Process exit ends their lifetime.

## SDK references

Interface GUIDs and method order are checked against Microsoft's Windows SDK headers: [d3d11.h](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/d3d11.h), [d3d11_1.h](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/d3d11_1.h), [dxgi.h](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/shared/dxgi.h), and [d2d1_1.h](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/d2d1_1.h). Microsoft also demonstrates requesting `ID2D1Factory1` through `D2D1CreateFactory` in its [Direct2D quickstart](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-quickstart-with-device-context).

The width-reservation experiment exposed the panel but introduced a map-only click offset. Undoing it restored click alignment, confirmed by the user. It is disabled by default while viewport/input consistency is investigated. `CMO_PANEL_LAYOUT=1` is an explicit experimental opt-in; do not use it for normal play yet.
