# Contributing

Keep changes focused on the adapter and make game behavior reproducible with a small test when possible. Do not add vendor binaries, game configurations, saves, user paths, runtime captures, or experimental patches to the repository.

Before submitting:

1. Run the Python tests and locked Release build described in [BUILDING.md](docs/BUILDING.md).
2. For native graphics or state-lifetime changes, run the isolated graphics probe in a closed-game test session and report the exact backend/version.
3. For gameplay claims, test a saved scenario and distinguish startup success from sustained play.
4. State unresolved issues plainly. Do not describe a synchronization workaround as a proven root-cause fix.

A COM callback must not allow diagnostic formatting or file I/O exceptions to escape into native code. Keep delegates rooted, preserve HRESULTs, define ownership for every retained pointer, and keep native calls outside bookkeeping locks. Changes to vtable slots or GUIDs need an SDK reference and a targeted runtime check.

This source package is deliberately separate from a running game. Build output is not installed automatically. Users choose when to save and install a candidate.
