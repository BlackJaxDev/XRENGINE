# DDGI Baking and Infinite-Latency Workflow

This guide details the authoring, convergence, baking, serialization, and runtime loading workflows for **Baked and Infinite-Latency Dynamic Diffuse Global Illumination (DDGI)** in XRENGINE.

---

## Overview

DDGI is experimental and exposes three operational modes on `DDGIVolumeComponent`:

Rebake version 1 `.ddgi` assets captured before the 2026-09-21 distance-filter
correction. The loader rejects those obsolete visibility moments with a rebake
diagnostic; existing files are not rewritten. Version 2 captures use a narrow
directional distance filter. `ChebyshevPower` controls visibility contrast during
sampling. Re-capture in Dynamic mode before switching back to Baked mode.

| Mode | Runtime GPU Tracing | Atlas Updates | Visibility Occlusion | Target Platform / Use Case |
| :--- | :--- | :--- | :--- | :--- |
| **`Dynamic`** | Every frame (budgeted) | Every frame (interleaved cascades) | Fully active | High-end desktop, VR, dynamic time-of-day / moving geometry |
| **`SlowUpdate`** | Periodic or on-invalidation | Infrequent / infinite-latency | Fully active | Mid-range hardware, slow day/night cycles, on-demand lighting |
| **`Baked`** | Zero probe rays traced | No atlas updates | Uses captured visibility | Static scenes; platform support requires separate validation |

### Architectural Invariant: Zero-Fork Sampling

Baked DDGI reuses `DDGISampling.glsl` and `VPRC_DDGICompositePass`, including their screen compute dispatch, from dynamic DDGI.

When a volume is switched to `Baked` mode:
1. `VPRC_DDGIRaygenPass`, `VPRC_DDGITracePass`, `VPRC_DDGIHitShadePass`, `VPRC_DDGIRelocatePass`, `VPRC_DDGIUpdateIrradiancePass`, `VPRC_DDGIUpdateVisibilityPass`, and `VPRC_DDGIBorderCopyPass` **skip execution completely**.
2. Pre-baked octahedral irradiance and visibility atlases stored in the `.ddgi` asset are loaded into `DDGIIrradianceAtlas` and `DDGIVisibilityAtlas`.
3. The screen-sampling composite pass evaluates probe trilinear interpolation, spherical Chebyshev visibility tests, and multi-cascade boundary blending identically to dynamic mode.

Baked uploads wait for prior GPU reads and writes, including writes from an
interrupted dynamic update. An aborted update retains a completion receipt for
resource lifetime but never publishes history or increments the completed-update
counter. If that receipt is unavailable or fails, the context blocks further
updates and uploads until its pipeline cache is cleared; diagnostics report the
failure instead of allowing an unsafe overwrite.

---

## Step-by-Step Baking Workflow

### 1. Configure the DDGI Volume

1. Add a `DDGIVolumeComponent` to the scene.
2. Position the volume to encapsulate the static environment geometry.
3. Configure probe grid dimensions:
   - Example: `ProbeCounts = (16, 8, 16)` or `(32, 4, 32)`
   - Extents and probe spacing appropriate for the scene scale (e.g. 1m to 2m spacing).
4. For multi-cascade environments:
   - Set `CascadeCount` (e.g. 2 or 3).
   - Configure `CascadeSpacingMultiplier` (default 2.0).

### 2. Convergence Phase

1. Keep `UpdateMode = Dynamic`.
2. Disable `CameraScrolling` and `DisableCoarseVisibility`, then let every probe in every cascade update. Confirm the lighting visually; completing one update per probe does not guarantee convergence.
   - Probes will automatically relocate away from interior wall geometry via dual-grid clamping.
   - Irradiance and distance-moment visibility atlases will smoothly converge via temporal hysteresis ($0.97$).
   - Multiple bounces will stabilize as hit shading gathers indirect radiance from neighboring probes.

### 3. Capture and Export the Asset

1. Use the MCP `bake_ddgi_volume` action with an explicit `output_path` (for example, `Assets/BakedGI/SceneDDGI.ddgi`). It captures after the viewport finishes rendering, saves and reloads off the render thread, and verifies that probe and atlas payload bytes survive the round trip. The action does not switch the volume to baked mode.

   Engine integrations can instead capture on the render thread after all probes and cascades have updated, then save the returned asset off that thread. This explicit bake reads GPU data synchronously; normal DDGI updates do not. Version 2 assets require stationary cascades and visibility enabled for every cascade.
   ```csharp
   if (!DDGIBaking.TryCaptureFromPipeline(pipelineInstance,
       volume.SceneNode?.Name ?? "SceneDDGI", out DDGIBakedAsset? asset, out string failure))
       throw new InvalidOperationException(failure);
   // Save asset off the render thread after the capture completes.
   ```
2. The asset saves:
   - Magic header (`0x49474444` `"DDGI"`) and version `2`.
   - Grid dimensions, extents, spacing, origin, and tuning parameters (normal bias, view bias, Chebyshev power).
   - Actual GPU relocation offsets and active/inactive state flags (`DDGIProbeGPU[]`); nominal positions derive from grid parameters.
   - Per-cascade irradiance pixels stored as RGB float upload data for the `R11G11B10F` atlas.
   - Per-cascade visibility pixels stored as RG half-float upload data for the `RG16F` atlas.

### 4. Switch to Baked Mode

1. Set `volume.BakedAssetPath = "<repo-root>/Assets/BakedGI/SceneDDGI.ddgi";`.
2. Set `volume.UpdateMode = EDDGIUpdateMode.Baked;`.
3. In `Baked` mode, ray generation, BVH tracing and atlas updates stop. Screen sampling still dispatches GPU compute. Asset and resource identity are checked so replacing the asset or regenerating the viewport resources uploads the baked data again.
4. The baked asset owns the volume layout while this mode is active: origin, probe counts, half extents, cascade count, cascade spacing, stationary cascades, and full cascade visibility are restored before upload. Live presentation controls such as intensity, tint, and bias remain editable.
5. If a baked file cannot be read, the renderer reports the failure and leaves DDGI sampling disabled for that viewport. After repairing the file, clear and restore `BakedAssetPath`, or leave and re-enter `Baked` mode, to retry loading without restarting the editor.

---

## Infinite-Latency / Slow-Update Mode

For scenes with stationary lighting that occasionally changes (such as stepped time-of-day or light switches):

1. Set `volume.UpdateMode = EDDGIUpdateMode.SlowUpdate;`.
2. Configure `volume.SlowUpdateIntervalFrames`:
   - Example: `SlowUpdateIntervalFrames = 60` (traces one budgeted slice every 60 frames).
3. Whenever a light moves, changes intensity, or geometry is altered, call:
   ```csharp
   volume.RuntimeState.Invalidate();
   ```
   Invalidation triggers an update burst until every probe has been refreshed, then resumes the periodic cadence. This warm-up condition is not a convergence metric.

---

## Feature Limits & Constraints for Baked DDGI

| Feature | Dynamic DDGI | Baked DDGI |
| :--- | :--- | :--- |
| **Static Geometry Diffuse GI** | Full multi-bounce | Full multi-bounce |
| **Moving Dynamic Geometry GI** | Captured dynamically | Receives indirect light; does not emit bounce light |
| **Moving Dynamic Lights** | Reflected in real time | Not captured in probe field (use direct forward lighting) |
| **Chebyshev Visibility Occlusion** | Reduces occluded probe contributions | Uses captured moments; thin-wall leakage remains possible |
| **Normal & View Biasing** | Active | **Fully Active** |
| **Nested Cascade Blending** | Active | **Fully Active** |
| **GPU Probe Tracing Cost** | Scene and budget dependent; measure on target hardware | None; screen sampling still runs |
| **Atlas VRAM Footprint** | Grid and cascade dependent | Same atlas dimensions as captured dynamic volume |
