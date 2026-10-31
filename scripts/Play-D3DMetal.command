#!/bin/zsh
set -eu

# Override these for a different bottle, installation, or CrossOver application.
bottle="${CMO_BOTTLE:-Steam}"
crossover_app="${CROSSOVER_APP:-/Applications/CrossOver.app}"
game_dir="${CMO_GAME_DIR:-$HOME/Library/Application Support/CrossOver/Bottles/$bottle/drive_c/Games/Command - Modern Operations}"
wine_bin="$crossover_app/Contents/SharedSupport/CrossOver/bin/wine"
batching="${CMO_D2D_BATCHING:-1}"
if [[ "$batching" != 0 && "$batching" != 1 ]]; then
    print -u2 'CMO_D2D_BATCHING must be 0 or 1.'
    exit 1
fi

if [[ ! -x "$wine_bin" || ! -f "$game_dir/CommandMetal.exe" || ! -f "$game_dir/CommandBrowser.exe" || ! -f "$game_dir/CommandBrowser.exe.config" ]]; then
    print -u2 'CrossOver or the installed CommandMetal files could not be found. See README.md for path overrides.'
    exit 1
fi
if /bin/ps -ww -axo comm= | /usr/bin/grep -E '(^|[/\\])(Command|CommandMetal|CommandBrowser)\.exe$' >/dev/null; then
    print -u2 'Command is already running. Save and close it before starting another session.'
    exit 1
fi

# Z: is Wine's Unix filesystem mapping. This avoids assuming a C: game path.
game_dir="${game_dir:A}"
windows_game="Z:${game_dir//\//\\}"
print 'Starting Command with D3DMetal and MSync off.'
print 'If Wine reports a synchronization mismatch, save and quit all Steam-bottle applications in CrossOver, then retry.'
exec "$wine_bin" --bottle "$bottle" --wait --debugmsg=-all,err+all \
    --env "CX_GRAPHICS_BACKEND=d3dmetal D3DM_MTL4=0 D3DM_MAX_FPS=60 WINEMSYNC=0 WINEESYNC=0 CMO_D2D_BATCHING=$batching" \
    --workdir "$windows_game" "$windows_game\\CommandBrowser.exe"
