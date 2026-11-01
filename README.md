# GPose ADOF

**Dalamud custom repository URL:**

```text
https://raw.githubusercontent.com/LevHowly/ADOF_Dalamud_Controller/master/pluginmaster.json
```

In Dalamud, open **Settings > Experimental > Custom Plugin Repositories**,
add this URL, save, then install **ADOF Dalamud Controller** from the plugin
installer. No manual download or developer plugin configuration is needed.
ReShade with add-on support (or GShade; see compatibility below) and qUINT must already be installed; the plugin's
setup screen installs its bundled GPose ADOF shader and bridge.

A Dalamud controller and ReShade add-on for **GPose ADOF**, a photographic
variant of qUINT ADOF. The controller now targets
[`GPose_ADOF.fx`](docs/GPose_ADOF.fx), technique `GPoseADOF`, displayed as
**GPose ADOF** in ReShade. Stock `qUINT_dof.fx` is no longer the controlled effect.

The qUINT bokeh renderer and its attribution are retained. The old artistic
near/far curve calibration and experimental hard-focus interval are replaced
with a thin-lens circle-of-confusion response.

## Controls

Open the window with `/adofcontrol`. It also opens on entering GPose (including
loading the plugin while already in GPose), and closes on exit.

## First-time setup

The plugin download embeds GPose ADOF and its native IPC add-on; no separate
shader/add-on download is needed. On startup it uses Dalamud's game data path
to locate the game folder and checks `ReShade.ini` / `GShade.ini` and the matching
injector DLL's version metadata.

- **No ReShade/GShade:** setup shows an error. Install a Direct3D 11 injector
  with ReShade add-on support yourself; the plugin does not install the injector.
- **Missing shader prerequisites:** install qUINT through ReShade's installer.
  `qUINT_common.fxh` and `ReShade.fxh` must be in the configured effect search
  paths. The plugin does not redistribute or download those dependencies.
- **Missing/outdated bundled files:** setup offers **Install GPose ADOF** instead
  of the focus controls. It reads `[ADDON] AddonPath` (default: game folder) and
  `[GENERAL] EffectSearchPaths`, including comma-separated and recursive `**`
  paths, and installs the shader beside the existing qUINT include.
- **After installation:** completely close and reopen the game. Reloading
  Dalamud alone cannot load the native add-on. Setup stays in restart-required
  mode for that plugin session.
- **Files present, no live IPC:** setup asks for a restart. If that persists,
  verify ReShade supports add-ons and enable **ADOF ReShade Bridge** in its
  Add-ons tab.
- **Connected IPC failing/not ready:** setup shows the error (including an
  incompatible running bridge, missing/failed shader or a response timeout).
  Disable Performance Mode and check ReShade's shader compilation errors.

Installation is opt-in, verifies embedded file hashes and preserves existing
copies under `install-backups/` in the Dalamud plugin configuration directory.
It does not edit ReShade configuration, presets, other shaders or the injector.
Filesystem/permission errors are displayed and logged in Dalamud. Use **Recheck
setup** after correcting a setup error. Setup status and recheck controls are
hidden once setup is ready. Setup's `ping` is non-mutating and checks the loaded shader
and required bridge uniforms before unlocking controls.
Entering GPose automatically rechecks the files and live IPC every time, so
setup errors detected during game startup can recover without a manual recheck.
The setup screen shows automatic-check progress, not a recheck button, while
checking. If the startup check is still running, the GPose-entry check is queued.
IPC readiness is tried up to three times with a short delay before reporting an
error; manual recheck remains available after a failed check.

### GShade compatibility (v1.0.2)

Onboarding recognizes GShade DLL metadata and `GShade.ini`, honors its
`AddonPath` (normally `.\gshade-addons`) and `EffectSearchPaths` (normally
`.\gshade-shaders\Shaders`), and uses the same bundled bridge and shader.
Leftover ReShade configuration alone does not override GShade detection.
If both matching injector installations are detected, setup reports ambiguity
instead of silently choosing a destination.

GShade **5.2.5** is a candidate for compatibility: its
[official release notes](https://github.com/Mortalitas/GShade/releases/tag/v5.2.5)
incorporate ReShade 6.8.0, and static inspection of its x64 DLL confirms the
add-on loader, required registration/event exports and acceptance of API 20.
Detection/installation is regression-tested, but **GShade rendering and
autofocus readback have not been tested in-game**. Older versions may not
support the required API. After installation, restart and let the IPC readiness
check confirm loading; successful IPC alone does not certify rendering accuracy.
Missing shared includes, disabled add-ons and Performance Mode still need to
be corrected in GShade. No GShade binaries are bundled or modified.

## Focus controls

- **Activate depth of field:** enables GPose ADOF. Activation starts unchecked
  on plugin load. While active in GPose with setup ready, the plugin temporarily
  suppresses the game's native depth-of-field render flag to avoid stacked blur.
  The flag is restored immediately after each render; the GPose checkbox and
  saved game settings are not changed. Deactivation, GPose exit or plugin unload
  stops suppression. If the render hook is unavailable, the plugin logs and
  displays a warning to disable the game's depth of field manually.
- **Deactivate ADOF on exiting GPose:** remembered, enabled by default.
- **Manual focus:** checked by default. Enables the camera/depth controls and
  the **Autofocus** button. Unchecked enables continuous GPU autofocus and
  disables manual camera/depth editing. Returning to manual mode restores the
  last manual or captured focus distance.
- **Autofocus button:** with ADOF active in GPose, captures the shader's target
  once, holds that distance, and updates Dalamud's focus distance and near/far
  limits without changing aperture. Uses the same median 3-by-3 sampling and
  optional subject-matching area average as continuous autofocus. Adjust the
  target, radius and matching settings in ReShade. Disable **Use ReShade focus
  controls** for capture. Capture briefly synchronizes the GPU for a 4-byte
  Direct3D 11 readback; there is no continuous CPU depth readback. Manual
  camera/depth editing is disabled while waiting; **Cancel autofocus** restores
  the existing manual settings. Switching mode, changing settings or exiting
  GPose also cancels/discards superseded captures.
- **Aperture:** f/0.1 to f/32; an aperture derived from depth limits can extend
  beyond that slider range.
- **Focus Distance:** logarithmic, normally 0.1 to 50 m. The range expands to
  include a captured distance beyond 50 m (up to the calibrated 1000 m far plane).
- **Near / Far Focus:** photographic acceptable-blur limits, not a completely
  sharp band. Editing them recalculates aperture and focus distance.
- **Far focus at infinity:** supported explicitly, without serializing Infinity
  into the wire protocol or saved distance fields. Selecting it derives the
  hyperfocal camera setting for the chosen near limit.
- **Maximum blur radius:** 1 to 100 pixels. This caps the physical blur; it does
  not multiply the entire response or move the acceptable-focus boundaries.
- **Out-of-focus blur strength:** 1 to 32, default 8. Amplifies only blur outside
  the acceptable-focus band; 1 disables amplification. A global 1-pixel radius
  reduction then widens the rendered sharp region.
- **Enable depth visualizer:** shows GPose ADOF's green-to-red rendered-blur
  overlay, including blur strength and autofocus. Green means a rendered radius
  <=0.5 px; yellow is 5.25 px, orange is about 7.6 px, and red is >=10 px.
  These color thresholds do not scale with the maximum blur radius.
  Enabling it activates ADOF;
  disabling it returns to the normal blurred image without disabling ADOF.
  No separate shader is required. Deactivating ADOF also clears the checkbox.
- **Camera grid / White grid lines:** GPose-only rule-of-thirds overlay and
  center crosshair. The grid and depth visualizer are disabled on GPose exit.

Computed limits can extend beyond the editable distance range. Invalid edits
show an error and do not commit a partially updated configuration.

## Photographic response

Most calculations are in [`DepthOfFieldMath.cs`](DepthOfFieldMath.cs), not the
native add-on: near/far inversion, hyperfocal calculations, perspective-depth
conversion and blur coefficients.

Defaults:

| Constant | Value |
|---|---|
| Focal length `f` | 0.05 m (50 mm) |
| Acceptable sensor CoC diameter `c` | 0.00003 m (0.03 mm) |
| Corresponding acceptable image blur | 1-pixel diameter / 0.5-pixel radius |
| Projection near / far planes | 0.1 / 1000 m |
| ReShade linearization far plane | 1000 |

The one-pixel tolerance is a screen-space calibration, not a claim that the
game camera uses a measured real sensor. It remains constant with output
resolution. ReShade's depth reversal, orientation and UV corrections are
handled by `qUINT_common.fxh`; the projection assumptions above must still
match the game's depth source. Change the C# constants and rebuild the plugin
if those assumptions change. The bridge forwards the resulting mapping.

With aperture `N` and focus distance `s`, define:

```text
h = f² / (N c)              (hyperfocal distance minus focal length)
H = h + f
near = h s / (h + s - f)
far  = h s / (h - s + f)    (infinity if the denominator is <= 0)
```

For finite depth limits, the inverse is:

```text
s = 2 near far / (near + far)
h = near (s - f) / (s - near)
N = f² / (c h)
```

For an infinite far limit, the inverse selects `s = 2 near`. These equations
use `h`, rather than accidentally substituting `H` into the limit numerators.

The signed pixel radius for a scene distance `z` is:

```text
k = 0.5 h / (s - f)
radiusPixels(z) = k (1 - s / z)
```

Its magnitude is exactly 0.5 pixels at finite near/far limits, zero at the
focus plane, and increases outside the acceptable interval. The artistic
strength control then applies:

```text
boostedMagnitude = min(abs(radiusPixels), 0.5)
                 + max(abs(radiusPixels) - 0.5, 0) * blurStrength
renderedMagnitude = min(max(boostedMagnitude - 1, 0), maximumBlurRadius)
```

The physical near/far limits remain unchanged, but subtracting 1 pixel after
amplification makes the entire acceptable interval blur-free and widens the
rendered sharp region beyond it. At the default strength of 8, physical radii
up to 0.5625 pixels render with zero radius. At f/2.8 and 5 m, a 3-pixel physical
radius becomes 19.5 pixels. The rendered-blur visualizer includes this reduction;
the physical-tolerance view still shows the original optical limits.
Maximum blur radius is still a cap,
not a strength multiplier. Only the final radius is capped; scene distances
are not saturated at the hyperfocal plane.
Anamorphic bokeh can reduce horizontal radius while preserving vertical radius.

Dalamud sends `k` and `-k s` as manual-focus coefficients. The GPU evaluates
those coefficients against each pixel's depth. Continuous autofocus necessarily
samples on the GPU and derives its coefficients from that sampled distance;
the lens coefficient and projection mapping still come from Dalamud.

The existing bokeh shape, highlight, quality and chromatic settings remain in
ReShade. Gaussian/chromatic finishing is bypassed within the acceptable blur
tolerance, and the final pass preserves the original RGB at radii <= 0.25
pixels. The physical model still couples aperture, band width and out-of-focus
blur: unlike an artistic hard band, they cannot be independently arbitrary.

The existing `adof-blur-response.svg` and `.png` are historical illustrations
of the earlier hard-band experiment, **not graphs of GPose ADOF**.

## Reliable GPose deactivation

The client has one sender and one replaceable pending request. A slider drag
does not queue hundreds of obsolete states. Exiting GPose replaces pending
settings with a standalone disable command; at most the one in-flight exchange
must finish or time out first (2 seconds).

Failed requests are retried every 250 ms until acknowledged, superseded by a
new request, or the plugin is disposed. Errors remain visible in the window.
Explicitly reactivating supersedes an older disable request.

The add-on applies commands after the current effects frame, taking effect on
the next frame. A disable command does
not require camera settings or accessible uniforms. It disables GPose ADOF,
legacy `ADOF`, the earlier `ADOF_DepthOfFieldModified` technique, and the
visualizer. It also reapplies that disable when effects reload, until a valid
activation request arrives. It does not continually overwrite manual ReShade
checkbox changes on every frame. Activating GPose ADOF also
turns off legacy ADOF to avoid stacking both effects.

ReShade and the bridge must be running and eventually process an effects
frame to acknowledge a request. The controller cannot disable a shader in a
process it cannot reach.

Activation stages and failures are logged in `ReShade.log`. If activation
crashes, include that log with the crash report: a CLR fatal-error report with
an empty exception stack does not identify the failing activation stage.

## Testing directly in ReShade

Expand **Depth testing** in GPose ADOF and enable **Use ReShade focus controls**.
You can now adjust **Focus distance (meters)** and **Aperture (f-number)**
without relying on hidden controller coefficients. Alternatively, enable
**Test with near/far limits** to derive those camera values from the exposed
near/far acceptable-focus distances. Turn autofocus off when testing manual
focus; autofocus replaces the focus distance while retaining the selected
lens/aperture response.

**Depth calibration** also exposes the manual CoC coefficients, autofocus
lens coefficient, focal length, and inverse-depth mapping. Coefficient edits
apply without the ReShade camera override; override camera controls derive
their own coefficients instead. Dalamud updates still write their controlled
uniforms, including test focus/aperture and blur strength, so leave its sliders
alone while testing directly in ReShade. The override checkbox itself is not
overwritten by the controller; disable it when returning to normal Dalamud use.

## Build and install

The native add-on needs rebuilding for protocol v4; the old add-on is incompatible.
Build with the MSVC-ABI toolchain, **not MinGW**:

```sh
cargo install xwin --locked
xwin --accept-license --arch x86_64 --cache-dir .xwin-cache splat --output .xwin
bash scripts/build-reshade-addon.sh
dotnet build ADOF_Dalamud_Controller.csproj
```

The build uses `clang-cl` and `lld-link` and the pinned official ReShade headers.
Override `XWIN_DIR`, `RESHADE_SDK_DIR`, or `BUILD_DIR` if needed.
The native add-on must be built in `build/reshade-addon/` before building the
plugin; the plugin embeds that binary and `docs/GPose_ADOF.fx`. When overriding
`BUILD_DIR`, copy the completed add-on to that standard bundling location.

Install the rebuilt add-on **and** shader:

```sh
bash scripts/install-gpose-adof.sh
```

Defaults are `~/.xlcore/ffxiv/game` and its `reshade-shaders/Shaders` directory.
For a different layout:

```sh
GAME_DIR=/path/to/game SHADER_DIR=/path/to/shaders bash scripts/install-gpose-adof.sh
```

The installer verifies the game directory and existing `qUINT_common.fxh` /
`ReShade.fxh` includes, backs up existing destination files, and does not edit
presets. Backups are kept in the project's git-ignored `backups/addons/` and
`backups/shaders/` directories, not in the game or shader search folders.
`SHADER_DIR` must be in ReShade's Effect Search Paths. Restart the game,
disable ReShade Performance Mode, and check that **GPose ADOF** is loaded.
The new effect has its own preset section; old qUINT bokeh settings are not
automatically copied.

## IPC contract

Windows named pipe: `adof-reshade`. Each connection exchanges one UTF-8 JSON
line each way. Protocol v4 `set_state` carries activation, autofocus, blur
visualizer enablement and
precalculated scalar uniforms, not camera distances requiring native math.
See [`ReShadeIpcClient.cs`](ReShadeIpcClient.cs) for the exact request fields.

Standalone disable:

```json
{"protocolVersion":4,"type":"deactivate_adof"}
```

Success:

```json
{"ok":true}
```

The non-mutating `ping` command requires only `protocolVersion:4` and `type:"ping"`.
All native responses also include `protocolVersion:4`; setup checks that the
running bridge is compatible, and `ping` rejects unloaded shader/uniforms.

`capture_focus` carries the same camera fields as `set_state`, with activation
true, continuous autofocus false, and a unique GUID `captureId`. The bridge
arms the shader capture, waits for GPose ADOF to actually render, reads its
1x1 R32F focus texture, and sets matching manual-focus coefficients. Its response:

```json
{"ok":true,"focusDistanceMeters":5.0}
```

Retries use the same capture ID and return the cached completed result instead
of sampling a new distance after a lost connection. A rejected capture or an
invalid capture result stops sampling, restores the previous manual settings,
and displays a capture error; it does not repeatedly toggle autofocus.
The Direct3D 11 readback accepts R32F and R16F shader views with matching typed
or typeless storage, decoding half-float samples when ReShade exposes R16F.
Dalamud computes the manual camera/depth values
on its framework thread; stale responses cannot overwrite newer settings.

Failures return `ok:false` and an explicit `error`. A request waiting for a render frame
times out, but remains retryable; a connected client must complete its request
line within 2 seconds.

## Regression checks

```sh
dotnet build ADOF_Dalamud_Controller.csproj
dotnet run --project tests/GPoseTests.csproj
```

The dependency-free executable tests finite/infinite camera math, exact
acceptable boundary radii within float precision, depth conversion, blur-cap
independence, protocol shape, latest-state delivery, repeated exit failures,
reconnect, superseding an exit with activation, one-shot capture results,
invalid/rejected capture responses with manual restoration, stable transport
retry IDs, capture cancellation, and disposal.
It also tests onboarding installation/update into custom/recursive paths,
prerequisite errors, backup preservation, restart gating, IPC failure modes
and the exact resources embedded in the built plugin.
The native-blur render-hook fixture verifies suppression during rendering,
restoration after rendering/errors, preservation of unrelated flags, resource
errors, and bypass on deactivation or off-thread rendering. In-game validation
is still required for the game's native rendering path.
Its pipe server is a test double, not a substitute for an in-game rendering
check. Do not run it while another local test server uses `adof-reshade`.

The native state/readback fixture also runs directly on Linux, without Wine:

```sh
mkdir -p build/native-tests
clang++ -std=c++20 -Wall -Wextra -Werror -I tests/native/host -I tests/native \
  tests/native/main.cpp -o build/native-tests/bridge-tests-host
build/native-tests/bridge-tests-host
```

Its single-threaded Win32 shim and fake GPU verify render-before-read ordering,
readback synchronization/cleanup, matching manual coefficients, retry caching,
reload/exit cancellation and explicit readback failures. They do not replace
an in-game Direct3D 11 test.

## Publishing a plugin update

Build the native add-on, then run `bash scripts/package-release.sh`. It builds
the Release plugin, writes `build/release/latest.zip` with plugin files at the
archive root, and regenerates `pluginmaster.json` from the generated Dalamud
manifest. Both shader and add-on are embedded in the plugin DLL.

Commit the version/manifest changes before publishing. Tag the commit with the
printed version tag, push the commit and tag to GitHub, and attach `latest.zip`
to the matching GitHub release. The custom repository links all install/update
downloads to that versioned asset. Do not move an existing release tag; bump
the project version for subsequent updates.
