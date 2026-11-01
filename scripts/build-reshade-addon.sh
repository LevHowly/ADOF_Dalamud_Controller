#!/usr/bin/env bash
set -euo pipefail

root_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
sdk_dir="${RESHADE_SDK_DIR:-$root_dir/.deps/reshade}"
xwin_dir="${XWIN_DIR:-$root_dir/.xwin}"
build_dir="${BUILD_DIR:-$root_dir/build/reshade-addon}"
sdk_commit="7bf9de8b33bcc76c3177007e65d73c72dd0f34c0"

if [[ ! -f "$sdk_dir/include/reshade.hpp" ]]; then
    mkdir -p "$(dirname "$sdk_dir")"
    git clone --depth 1 --filter=blob:none --sparse https://github.com/crosire/reshade.git "$sdk_dir"
    git -C "$sdk_dir" fetch --depth 1 origin "$sdk_commit"
    git -C "$sdk_dir" checkout --detach FETCH_HEAD
    git -C "$sdk_dir" sparse-checkout set include
fi

if ! command -v clang-cl >/dev/null 2>&1 || ! command -v lld-link >/dev/null 2>&1; then
    printf '%s\n' "clang-cl and lld-link are required to build a ReShade-compatible MSVC-ABI add-on." >&2
    exit 1
fi

if [[ ! -f "$xwin_dir/crt/include/vcruntime.h" ||
      ! -d "$xwin_dir/sdk/include/um" ||
      ! -d "$xwin_dir/sdk/include/ucrt" ||
      ! -d "$xwin_dir/sdk/include/shared" ]]; then
    printf 'Windows SDK/CRT headers not found in %s\n' "$xwin_dir" >&2
    printf '%s\n' \
        "Install xwin with 'cargo install xwin --locked', then run:" \
        "xwin --accept-license --arch x86_64 --cache-dir .xwin-cache splat --output \"$xwin_dir\"" >&2
    exit 1
fi

mkdir -p "$build_dir"
source_file="$root_dir/reshade-bridge/adof_reshade_bridge.cpp"
output_file="$build_dir/adof_reshade_bridge.addon64"

clang-cl --target=x86_64-pc-windows-msvc -fuse-ld=lld-link \
    /nologo /LD /O2 /EHsc /std:c++20 /MT /W4 \
    /DNOMINMAX /DWIN32_LEAN_AND_MEAN /DNDEBUG \
    /imsvc "$xwin_dir/crt/include" \
    /imsvc "$xwin_dir/sdk/include/ucrt" \
    /imsvc "$xwin_dir/sdk/include/um" \
    /imsvc "$xwin_dir/sdk/include/shared" \
    /imsvc "$sdk_dir/include" \
    "$source_file" "/Fe:$output_file" "/Fo:$build_dir/adof_reshade_bridge.obj" \
    /link \
    "/LIBPATH:$xwin_dir/crt/lib/x86_64" \
    "/LIBPATH:$xwin_dir/sdk/lib/um/x86_64" \
    "/LIBPATH:$xwin_dir/sdk/lib/ucrt/x86_64"

rm -f "$build_dir/adof_reshade_bridge.lib" "$build_dir/adof_reshade_bridge.exp" \
    "$build_dir/adof_reshade_bridge.obj" "$build_dir/adof_reshade_bridge.addon64.lib"

printf 'Built ReShade add-on: %s\n' "$output_file"
