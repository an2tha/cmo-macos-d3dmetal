# Diagnostics and troubleshooting

## Database list appears but entry details are blank

Start through `CommandBrowser.exe` or the supplied launch script. The renderer-only `CommandMetal.exe` does not install the browser guard. With the tested Runtime 109 and SDK 1.0.2151.40, the optional `IsReputationCheckingRequired` setter throws because Settings8 is unsupported. The bootstrap skips that setter only on `E_NOINTERFACE`, retaining browser defaults so pane initialization can continue.

Browser logs record guard installation, SDK identity mismatches, and skipped calls. Unknown SDK hashes are left unchanged. `CMO_WEBVIEW_COMPAT=0` disables the guard for a controlled comparison. Neither the SDK nor the WebView2 runtime is replaced by this fix.

## Unit status panel and map click offset

The controlled fix requires narrowing the native map and invoking the game's normal renderer resize. Changing only map bounds leaves the drawing and picking dimensions stale. The launch script enables `CMO_PANEL_LAYOUT=1` automatically to coordinate these steps and check the camera after the next frame. Starting `CommandMetal.exe` directly bypasses this fix. The automatic integration passed a controlled in-game panel, input and transition check.

If a test leaves clicks offset, save and close the game, then relaunch with `CMO_PANEL_LAYOUT=0`. Unknown game hashes and unsupported layouts are logged and left unchanged. Do not combine the package with older private sidebar probes.

## Window freezes during activation

One captured freeze blocked inside `SetForegroundWindow` from a MainForm message-handler activation call. `CMO_FOCUS_COMPAT=1` enables the narrow experimental guard for that exact caller. Other activation paths retain normal behavior. Those focus transitions passed the controlled test, while longer sessions remain unvalidated. A guard counter is evidence that the path was matched, not proof of long-term stability.

## Rosetta thread-context assertion during long sessions

An observed freeze ended with `expected saved LR to be in translated code` in `ThreadContextRegisterState.cpp`. A read-only capture of Wine's saved syscall frames found the main thread and a worker inside `NtFlushProcessWriteBuffers`. The matching CLR symbols placed the main thread in `ThreadSuspend::SuspendRuntime`. Removing first-chance stack-trace logging did not prevent recurrence.

Microsoft's [Rosetta barrier fix](https://github.com/dotnet/runtime/pull/134384) serializes concurrent Mach register-state queries. The optional `CMO_SERIALIZE_BARRIERS=1` compatibility path applies that serialization to the reviewed CrossOver 26.3 native Wine module. It retains the original barrier and uses a native pthread mutex. It does not disable GC, skip the barrier, query registers from managed callbacks, or change graphics and sidebar code.

After saving and closing the game, launch a test with:

```sh
CMO_SERIALIZE_BARRIERS=1 zsh scripts/Play-D3DMetal.command
```

This option is experimental and off by default. Its exact native-module hash and service-table checks must pass; an unsupported profile stops startup before the game opens. Set the variable to `0` to return to the ordinary barrier without changing the sidebar setting.

The unmodified four-thread test stalled with four saved calls in the original barrier. The serialized test completed 40,000 calls, and the combined D3DMetal/GC test retained correct pixels and graphics state. The user confirmed normal map, clicks and sidebar behavior in-game. A subsequent session still froze with a Wine critical-section timeout; it was restarted before its lock owner could be captured. That remaining failure is unresolved. The stress results address one identified failure path and do not establish that every freeze is fixed.

For a `RtlpWaitForCriticalSection` timeout, retain the full native log and leave the frozen process open for diagnosis. The reported owner and waiting thread IDs refer only to that process. The retry interval is not proof of a particular deadlock or its cause. Do not bypass the lock or disable the original memory barrier.

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
| `CMO_NATIVE_LOG_DIR` | macOS directory for the launch script's native Wine output, default `~/Library/Logs/CommandMetal` |
| `CMO_PANEL_LAYOUT=0` | Disable the sidebar and renderer/camera synchronization enabled by the launch script |
| `CMO_FOCUS_COMPAT=1` | Opt into the captured MainForm activation caller guard |
| `CMO_SERIALIZE_BARRIERS=1` | Opt into native serialization of the reviewed Wine process-wide barrier |
| `CMO_WEBVIEW_COMPAT=0` | Disable the optional Settings8 compatibility guard in CommandBrowser |

The launch script preserves native Wine output in a separate timestamped file under `~/Library/Logs/CommandMetal` on every run and forwards it to the terminal or app. Restarting does not overwrite the previous session's native log. CrossOver's `--cx-log` option can add verbose trace channels; the launcher avoids it so ordinary testing does not accidentally include heavy exception-unwind tracing.

## Reporting a freeze

Record the game/CrossOver/macOS/backend versions, synchronization settings, scenario name, approximate play duration, last action, and whether the whole window or only the map stopped responding. Include small relevant log excerpts after reviewing them for local paths and scenario details. Do not attach game executables, game DLLs, saved scenarios, or full memory dumps to public issues.

Main-thread GC waits, a zero wrapper exit status, or a missing managed exception do not by themselves prove a cause. Keep observations separate from hypotheses. Avoid attaching a debugger or running intrusive UI inspection during a session with unsaved progress.
