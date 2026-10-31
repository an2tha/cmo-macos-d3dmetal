# Compatibility and known issues

## Evidence boundaries

The working prototype was tested with Command v1.10 build 1900.20, CrossOver 26.3, Apple D3DMetal 4.0b2, macOS 27.2, and an Apple M4 Max. Its map rendered and was playable, with remaining lag. Those observations describe the prototype, not a completed validation of the standalone rewrite in this repository.

The original state adapter passed isolated pixel/readback and state-restoration probes, including repeated drawing, explicit Flush, and allocation stress. The cleaned `GraphicsProbe.exe` must be rerun on the intended backend after any interop change. Compilation and CI cannot establish D3DMetal behavior.

## Current issues

| Issue | Status | Acceptance condition |
| --- | --- | --- |
| Black or unresponsive map on the affected D3DMetal backend | Addressed by the original surface/context-state prototype; rewrite needs regression testing | Scenario loads, map renders, pan/zoom and unit dialogs remain usable |
| Whole-window freezes or unexpected exits | Captured activation wait guarded in a controlled test; other freezes and longer stability remain under investigation | Repeated representative sessions without the reported freeze, with comparable diagnostics |
| Slow map operations, especially zoomed out | Improved by drawing batches in the prototype, still present | Smoother pan/zoom without incorrect graphics or state restoration |
| Right-side Group/Unit Status panel clipped beneath the map | Paired resize and automatic integration passed the user's panel/input/transition checks | Full panel, including the no-selection state, survives selection, resizing, scrolling, and map movement |
| Database Viewer list populates but entry details stay blank | Resolved in the tested prototype with the Settings8 compatibility bootstrap | Details render after selecting an entry, with the same map renderer retained |

The selected sidebar's identity and WPF body were confirmed separately from the hover host. Width-only reservation exposed the panel but broke map click alignment. Calling the game's normal renderer resize after the bounds change corrected renderer and camera dimensions, and the user confirmed the full panel and normal map controls. The automatic implementation is available through `CMO_PANEL_LAYOUT=1`. The user confirmed automatic startup, complete panel/input behavior, resizing, hide/show, empty/reselected-unit state and scrolling in the combined test.

## Captured window-activation freeze

After the successful controlled panel test, the user reported another whole-window freeze. The process remained alive. Passive CLR inspection captured `MainForm.WndProc -> Form.Activate -> SetForegroundWindow` on the UI thread, while a native sample showed a windowing mutex wait and idle graphics workers. The simulation thread remained active. This is distinct evidence from the earlier GC-wait sample; a shared cause has not been established.

The opt-in focus guard matched the observed activation caller during the next normal startup. After an interrupted test was closed normally, a fresh combined automatic test passed user-confirmed app switching, unit/database dialogs, keyboard focus, viewport resizing and sidebar transitions without another freeze during the check. `CMO_FOCUS_COMPAT=1` remains experimental because longer stability and other environments are unvalidated. The controlled test retained the original playable graphics renderer.

## Database Viewer finding

An isolated probe using the installed SDK and Runtime 109 reproduced the missing Settings8 interface and escaping `NotImplementedException`. Local HTML navigation and DOM layout succeeded, and the user confirmed the probe's heading and text were visible. With the targeted guard, the settings call completed and HTML remained functional. The user then confirmed the real database details rendered when the bootstrap invoked the unchanged installed renderer. Original game, renderer, configuration, and Microsoft SDK files were verified unchanged.

This guard covers the validated SDK binary and the missing optional API. It does not replace WebView2 or establish compatibility with every SDK/runtime combination. The standalone renderer rewrite still has its separate regression-testing boundary described above.

## Synchronization finding

The MSync-off experiment kept the playable prototype's executable and configuration unchanged. A fresh Wine server was observed with `WINEMSYNC=0`, `WINEESYNC=0`, and `CX_GRAPHICS_BACKEND=d3dmetal`. The user subsequently reported normal play with no crashes yet. This makes synchronization a useful working hypothesis; it does not identify a proven root cause or guarantee that all hangs are fixed.

An earlier frozen session had its main thread waiting for CLR garbage collection. That identifies a waiting location, not the thread or condition preventing collection from completing. Graphics workers were idle in that sample. A concurrent-GC configuration experiment was inconclusive and is not included.

## Deliberate limits

- State emulation covers bindings Wine Direct2D was observed to change. It does not preserve all UAV, predication, stream-output, or D3D11.1 constant-buffer subrange state.
- Automatic resource-alias unbinding across shader stages can invalidate assumptions for unusual aliases.
- The batch path assumes raw Direct3D work happens outside the Direct2D drawing batch, or after an explicit Flush.
- The existing emulation handles a null incoming state differently from the complete native contract. Microsoft's [SwapDeviceContextState documentation](https://learn.microsoft.com/en-us/windows/win32/api/d3d11_1/nf-d3d11_1-id3d11devicecontext1-swapdevicecontextstate) specifies that a null state has no effect. This behavior is retained from the prototype and needs a dedicated test before changing it.
- Shared COM vtables and method slots depend on the backend's implementation. A CrossOver/D3DMetal update requires regression testing.
- `GraphicsProbe.exe` tests a small rendering path, not the full simulation, WPF interface, or multi-hour game stability.
- Intel Macs, other game builds, other CrossOver versions, DXVK, and native Windows are not validated targets for this launcher.
