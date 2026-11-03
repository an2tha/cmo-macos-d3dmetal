# Contributing to cmo-on-macos

Keep changes focused on running Command on macOS: the app launcher, native interface, graphics compatibility, diagnostics and setup documentation. Make behavior reproducible with a small test when possible. Do not add vendor binaries, game configurations, saves, user paths, runtime captures, or experimental patches to the repository.

Before submitting:

1. Run the Python tests and locked Release build described in [BUILDING.md](docs/BUILDING.md).
2. For Swift/AppKit/WebKit changes, build the app on macOS and run the relevant native fixtures described in [NATIVE-UI.md](docs/NATIVE-UI.md). Report which modes and screens were actually checked.
3. For native graphics or state-lifetime changes, run the isolated graphics probe in a closed-game test session and report the exact backend/version.
4. For gameplay claims, test a saved scenario and distinguish startup success from sustained play.
5. State unresolved issues plainly. Do not describe a synchronization workaround as a proven root-cause fix.

A COM callback must not allow diagnostic formatting or file I/O exceptions to escape into native code. Keep delegates rooted, preserve HRESULTs, define ownership for every retained pointer, and keep native calls outside bookkeeping locks. Changes to vtable slots or GUIDs need an SDK reference and a targeted runtime check.

This source package is deliberately separate from a running game. Build output is not installed automatically. Users choose when to save and install a candidate.

Documentation should lead with `Command Native.app` and the steps a player takes.
Keep advanced shell installation separate. Use **cmo-on-macos** for the project,
while retaining real executable, app and workflow names in commands. Do not
rename `CommandMetal.exe` in prose as if a matching binary already exists.

Before documenting a download, check that the release contains that asset. The
current Windows CI ZIP is not a macOS app. Keep published packages, source-only
features and local performance candidates distinct. Native compute experiments
outside this repository are not shipped features until integrated and validated.
