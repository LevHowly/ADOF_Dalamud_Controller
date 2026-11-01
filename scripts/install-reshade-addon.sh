#!/usr/bin/env bash
set -euo pipefail

root_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="${ADDON_FILE:-$root_dir/build/reshade-addon/adof_reshade_bridge.addon64}"
game_dir="${GAME_DIR:-$HOME/.xlcore/ffxiv/game}"
destination="$game_dir/adof_reshade_bridge.addon64"
backup_dir="$root_dir/backups/addons"

if [[ ! -f "$source_file" ]]; then
    printf 'Add-on not found: %s\nBuild it first with scripts/build-reshade-addon.sh.\n' "$source_file" >&2
    exit 1
fi

if [[ ! -d "$game_dir" || ! -f "$game_dir/ffxiv_dx11.exe" || ! -f "$game_dir/ReShade.ini" ]]; then
    printf 'Game directory does not look like the ReShade-enabled FFXIV folder: %s\nSet GAME_DIR to the correct folder.\n' "$game_dir" >&2
    exit 1
fi

if [[ -e "$destination" ]]; then
    mkdir -p -- "$backup_dir"
    backup="$backup_dir/adof_reshade_bridge.addon64.backup.$(date +%Y%m%d-%H%M%S).$$"
    cp -p -- "$destination" "$backup"
    printf 'Backed up existing add-on to %s\n' "$backup"
fi

install -m 0644 -- "$source_file" "$destination"
printf 'Installed ReShade add-on: %s\nRestart the game for ReShade to load it.\n' "$destination"
