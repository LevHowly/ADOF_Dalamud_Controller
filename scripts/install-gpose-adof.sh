#!/usr/bin/env bash
set -euo pipefail

root_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
game_dir="${GAME_DIR:-$HOME/.xlcore/ffxiv/game}"
shader_dir="${SHADER_DIR:-$game_dir/reshade-shaders/Shaders}"
source_file="$root_dir/docs/GPose_ADOF.fx"
destination="$shader_dir/GPose_ADOF.fx"
backup_dir="$root_dir/backups/shaders"

if [[ ! -f "$source_file" || ! -f "$shader_dir/qUINT_common.fxh" ||
      ! -f "$shader_dir/ReShade.fxh" ]]; then
    printf 'GPose ADOF source or shared shader includes are missing in %s.\n' "$shader_dir" >&2
    printf '%s\n' "Set SHADER_DIR to an existing ReShade shader search directory containing qUINT_common.fxh and ReShade.fxh." >&2
    exit 1
fi

GAME_DIR="$game_dir" bash "$root_dir/scripts/install-reshade-addon.sh"

if [[ -e "$destination" ]]; then
    mkdir -p -- "$backup_dir"
    backup="$backup_dir/GPose_ADOF.fx.backup.$(date +%Y%m%d-%H%M%S).$$"
    cp -p -- "$destination" "$backup"
    printf 'Backed up existing shader to %s\n' "$backup"
fi
install -m 0644 -- "$source_file" "$destination"
printf 'Installed GPose ADOF: %s\n' "$destination"
printf '%s\n' \
    "Ensure this directory is in ReShade's Effect Search Paths and Performance Mode is off." \
    "Restart the game to load the rebuilt bridge. Enable GPose ADOF from the Dalamud controller." \
    "Existing qUINT presets are not changed; the controller disables legacy ADOF when it activates GPose ADOF."
