# Changelog

## 0.1.0, standalone source package

- Package the surface, context-state, and Direct2D batching adapters as an independent x64 .NET Framework 4.8 build.
- Replace the build-time DirectN dependency with explicit Windows SDK identities and native factory calls.
- Add a manual pixel/readback and state-restoration probe without game dependencies.
- Move diagnostics into per-session local application data, with GC counters, exception tracing, and draw profiling opt-in.
- Request MSync off in the configurable D3DMetal launcher following an initial successful prototype session.
- Add an installer that preserves the game's configuration and backs up previous CommandMetal files.
- Add locked dependency restore, ZIP packaging, host-side tests, and GitHub Actions artifacts.

The standalone rewrite has not yet completed in-game regression testing. Sidebar clipping and intermittent-freeze investigation remain open. Historical local experiments and proprietary inputs are excluded from this repository.
