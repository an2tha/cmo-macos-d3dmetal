# Diagnostics and troubleshooting

## Synchronization mismatch at launch

A Wine server already running with MSync enabled cannot be reused for this MSync-off launch. Save and close every application in that bottle, use CrossOver's **Quit All Applications**, verify MSync is off, and launch again. The script deliberately does not terminate the server automatically.

The bottle toggle can still display a saved default that differs from the environment requested for one launch. A changed flag alone is not proof of the running server's mode. Use a fresh bottle session for a controlled comparison.

## Missing files or startup errors

Install the launcher beside the real `Command.exe` using `scripts/install.py`. The full game configuration is required because assembly redirects and WPF runtime settings belong to the installed game. Rerun the installer after an update changes `Command.exe.config`.

The original prototype encountered a WPF ResourceDictionary exception with an invalid-window-handle inner failure during a dirty bottle session. A clean restart cleared that occurrence. That is one observation, not a universal fix for ResourceDictionary errors.

## Logs

CommandMetal writes startup and launcher failures to `%LOCALAPPDATA%\CommandMetal\logs` inside the bottle. Session filenames contain a UTC timestamp and Windows process ID. Set `CMO_LOG_DIR` to another Windows-visible directory to override the location. File-write failures are contained so diagnostics do not replace the error being reported.

Additional diagnostics are opt-in:

| Variable | Effect |
| --- | --- |
| `CMO_DIAGNOSTICS=1` | One-second managed heap, GC, and state/batch counters |
| `CMO_TRACE_EXCEPTIONS=1` | First-chance logging for selected exception classes; can be expensive |
| `CMO_D2D_PROFILE=1` | Selected Direct2D call counts and timing; changes measurement overhead |
| `CMO_D2D_BATCHING=0` | Disable drawing batching to compare the ordinary state-emulation path |
| `CMO_LOG_DIR` | Directory for CommandMetal logs |

With a CLI launch, native Wine errors also appear in the terminal. For a controlled capture, redirect stdout/stderr to a file. CrossOver's `--cx-log` option can add verbose trace channels; the launcher avoids it so ordinary testing does not accidentally include heavy exception-unwind tracing.

## Reporting a freeze

Record the game/CrossOver/macOS/backend versions, synchronization settings, scenario name, approximate play duration, last action, and whether the whole window or only the map stopped responding. Include small relevant log excerpts after reviewing them for local paths and scenario details. Do not attach game executables, game DLLs, saved scenarios, or full memory dumps to public issues.

Main-thread GC waits, a zero wrapper exit status, or a missing managed exception do not by themselves prove a cause. Keep observations separate from hypotheses. Avoid attaching a debugger or running intrusive UI inspection during a session with unsaved progress.
