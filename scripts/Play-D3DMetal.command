#!/bin/zsh
set -eu

# Override these for a different bottle, installation, or CrossOver application.
bottle="${CMO_BOTTLE:-Steam}"
crossover_app="${CROSSOVER_APP:-/Applications/CrossOver.app}"
game_dir="${CMO_GAME_DIR:-$HOME/Library/Application Support/CrossOver/Bottles/$bottle/drive_c/Games/Command - Modern Operations}"
wine_bin="${CMO_WINE_BIN:-$crossover_app/Contents/SharedSupport/CrossOver/bin/wine}"
batching="${CMO_D2D_BATCHING:-1}"
panel_layout="${CMO_PANEL_LAYOUT:-1}"
focus_compat="${CMO_FOCUS_COMPAT:-0}"
serialize_barriers="${CMO_SERIALIZE_BARRIERS:-0}"
native_ui="${CMO_NATIVE_UI:-0}"
case "$native_ui" in
    0|browser|all) ;;
    *) print -u2 'CMO_NATIVE_UI must be 0, browser, or all.'; exit 1 ;;
esac
if [[ "$native_ui" != 0 ]]; then
    native_host="${CMO_NATIVE_HOST:-${0:A:h}/libCommandNativeHost.dylib}"
    if [[ ! -f "$native_host" ]]; then
        print -u2 'The native UI library is missing. Build the macOS native package first.'
        exit 1
    fi
    export CMO_NATIVE_HOST="${native_host:A}"
    export CMO_WINE_PREFIX="${CMO_WINE_PREFIX:-$HOME/Library/Application Support/CrossOver/Bottles/$bottle}"
fi
export CMO_NATIVE_UI="$native_ui"
export CX_ROOT="${CX_ROOT:-$crossover_app/Contents/SharedSupport/CrossOver}"
if [[ "$batching" != 0 && "$batching" != 1 ]]; then
    print -u2 'CMO_D2D_BATCHING must be 0 or 1.'
    exit 1
fi
for setting in "$panel_layout" "$focus_compat" "$serialize_barriers"; do
    if [[ "$setting" != 0 && "$setting" != 1 ]]; then
        print -u2 'CMO_PANEL_LAYOUT, CMO_FOCUS_COMPAT and CMO_SERIALIZE_BARRIERS must be 0 or 1.'
        exit 1
    fi
done

if [[ ! -x "$wine_bin" || ! -f "$game_dir/CommandMetal.exe" || ! -f "$game_dir/CommandBrowser.exe" || ! -f "$game_dir/CommandBrowser.exe.config" ]]; then
    print -u2 'CrossOver or the installed CommandMetal files could not be found. See README.md for path overrides.'
    exit 1
fi
game_dir="${game_dir:A}"
if /bin/ps -ww -axo comm= | /usr/bin/grep -E '(^|[/\\])(Command|CommandMetal|CommandBrowser|CommandBrowserBarrier|CommandUiAuto[0-9]+)\.exe$' >/dev/null; then
    print -u2 'Command is already running. Save and close it before starting another session.'
    exit 1
fi

# CrossOver can host Command inside its native Menu Helper process.
while read -r task_pid task_command; do
    if [[ "$task_command" == */Contents/MacOS/"Menu Helper" ]]; then
        if /usr/sbin/lsof -n -a -p "$task_pid" -Fn 2>/dev/null | /usr/bin/grep -Fx -- "n$game_dir/Command.exe" >/dev/null; then
            print -u2 'Command is already running inside CrossOver. Save and close it before starting another session.'
            exit 1
        fi
    fi
done < <(/bin/ps -ww -axo pid=,comm=)

# Z: is Wine's Unix filesystem mapping. This avoids assuming a C: game path.
windows_game="Z:${game_dir//\//\\}"
# Keep each session's native diagnostics after the next launch. Forward output
# to the terminal or app as well, retaining Wine's original exit status.
native_log_dir="${CMO_NATIVE_LOG_DIR:-$HOME/Library/Logs/CommandMetal}"
/bin/mkdir -p "$native_log_dir"
native_log=$(/usr/bin/mktemp "$native_log_dir/wine-$(/bin/date -u +%Y%m%dT%H%M%SZ).XXXXXX")
exec > >(/usr/bin/tee -a "$native_log") 2>&1
print -r -- "Native session log: $native_log"
print 'Starting Command with D3DMetal and MSync off.'
print "If Wine reports a synchronization mismatch, save and quit all applications in bottle $bottle, then retry."
exec "$wine_bin" --bottle "$bottle" --wait --debugmsg=-all,err+all \
    --env "CX_GRAPHICS_BACKEND=d3dmetal D3DM_MTL4=0 D3DM_MAX_FPS=60 WINEMSYNC=0 WINEESYNC=0 CMO_D2D_BATCHING=$batching CMO_PANEL_LAYOUT=$panel_layout CMO_FOCUS_COMPAT=$focus_compat CMO_SERIALIZE_BARRIERS=$serialize_barriers" \
    --workdir "$windows_game" "$windows_game\\CommandBrowser.exe"
