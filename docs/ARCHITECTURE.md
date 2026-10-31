# Adapter architecture

## Startup

`CommandMetal.exe` is a Windows x64 .NET Framework 4.8 executable. It sets its working directory to its installation directory, optionally starts diagnostics, creates a hidden native seed window and a BGRA Direct3D 11 swap chain, and installs the adapters before invoking the original managed `Command.exe` entry point. A native `STATIC` window avoids initializing WinForms before the game's own protected startup code.

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
