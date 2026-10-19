# Unity Avatar Package Validation

Guide: [Unity Conversion](../../../developer-guides/assets/unity-conversion-integrations.md)
Investigation: [Unity Avatar Package Import](../../investigations/assets/unity-avatar-package-import.md)

## Current Release Acceptance — 2026-10-19

Both prefab import routes passed from fresh Release editor processes with
Vulkan, `AdvancedRenderPipeline`, and `GpuIndirectZeroReadback` at 1920x1080.
Package and disk caches were retained. These checks cover first process use,
resource publication, and first light activation. They do not cover empty-cache
import. The accepted build completed with zero warnings and errors.

Exact source assets, missing references, scene counts, capture identifiers,
and package-specific results are retained in local accessory notes.

| Check | NativeOnly | AssimpOnly |
|---|---|---|
| Settled avatar | Published; texture streaming settled | Published; texture streaming settled |
| Recovered image bindings | Source dimensions and SHA-256 checked | Matched the same source images |
| First point-light activation | Timer active; six resident face snapshots | Same |
| Point mode | Sequential GPU faces; `SharedIndirectInstancingUnsupported` | Same |
| Authored shadow coverage | Changed point and spot pixels; exact restoration | Same |
| Opaque shadow removal | Changed point and spot pixels; exact restoration | Same |
| Standard and synchronization validation | Zero errors | Zero errors |

Loader and unused-attribute warnings remain. Front and back views and shadow
atlas controls were captured and viewed. RenderDoc was disabled. Missing-source
markers remain, so full source appearance acceptance is open.

### Release Performance

Both 60-second captures passed benchmark metadata checks on an RTX 3090. The
first five seconds were excluded. Every remaining render attempt completed.
Both used the same Release binary, GPU indirect submission, and bindless material
table. Presentation was uncapped. Shader and texture caches were warm.
Validation, RenderDoc, and editor UI were off.

| Measurement | NativeOnly | AssimpOnly |
|---|---:|---:|
| Accepted samples | 2,627 | 2,862 |
| Mean presentation interval | 20.93 ms | 19.21 ms |
| Mean cadence | 47.78 Hz | 52.06 Hz |
| Median interval | 21.05 ms | 19.24 ms |
| p95 interval | 23.74 ms | 21.82 ms |
| p99 interval | 25.23 ms | 28.67 ms |
| Maximum interval | 40.32 ms | 42.46 ms |
| Intervals above 11.11 ms | 100% | 100% |
| Mean active CPU render dispatch | 11.93 ms | 10.26 ms |

Cadence uses accepted `QueuePresent` completion timestamps. It does not measure
display scanout or headset delivery. Output and resolved internal extents were
1920x1080, although settings metadata retained a render scale of 0.67.

The workload used a static T-pose and a fixed front camera. One directional
light was active with shadows off. Local lights and probe capture were off.
Bloom and auto exposure were off. Tone mapping was Linear, exposure was 1, and
contrast was 0. Neither route meets 90 Hz in this desktop workload. These results
do not establish stereo or continuous probe-capture performance.

CPU timings overlap. They must not be added as independent frame costs. GPU
timing history was unavailable. Recent changes address repeated shader-file
checks, SH uniform ownership, and texture locking. Their Release build passed
with zero warnings and errors. The user stopped Computer Use when live checks
began; the editor was stopped. The measurements above predate those changes.

## Setup

Use a named isolated editor and an external JSONC profile. Select desktop
Vulkan with `RequireRequested`, `AdvancedRenderPipeline`, and
`GpuIndirectZeroReadback`. Require Advanced admission. Import the Unity prefab
entry through the composed source snapshot; direct FBX import is only a
diagnostic control. Repeat with `NativeOnly` and `AssimpOnly`, with other
settings unchanged.

Use strict import to check source closure. Use an explicit partial-import
profile only for incomplete-source preview. Missing-asset markers are expected
in partial preview and must not count as visual parity.

For neutral inspection, disable bloom and auto exposure. Use Linear tone
mapping, exposure 1, and contrast 0. These settings are not a matched Unity
lighting reference. Inspect final material bindings after geometry import.

## Validation Matrix

| Area | Acceptance check | Current result |
|---|---|---|
| Dependency resolution | Declared sources resolve and compose | Passed for available sources |
| Strict source closure | Required visual assets exist | Open; missing materials and textures remain |
| Partial model handling | Supported geometry survives unsupported effects | Passed for the exercised sources |
| Scene visibility | Main scene visible; separate effect scenes hidden | Passed |
| Shader conversion | Supported sources retain native Uber identity | Passed for checked inputs; broader support remains open |
| FBX coordinates | Apply unit conversion only to supported source charts | Implemented; broader paired acceptance remains pending |
| Advanced Vulkan | Both routes advance without CPU fallback or terminal fault | Passed at the accepted Release state |
| Visual output | View front, oblique, and back captures | Checked; missing sources prevent full appearance acceptance |
| Live edits | Change and restore scalar and texture bindings | Earlier controls passed; new CPU-reuse changes need revalidation |
| Camera and deformation | Move and hold camera without readiness failure | Earlier controls passed; new build needs revalidation |
| Masks and multilayer shading | Change and restore supported controls | Earlier controls passed for exercised features |
| Shadows | Verify coverage, caster removal, and exact restoration | Warm Release controls passed |
| Validation layers | No standard or synchronization errors | Passed on both accepted Release routes |
| CPU-reuse changes | Verify lighting, material edits, texture replacement, and source invalidation | Pending |
| Release performance | At least 60 seconds under matched conditions | Latest rates are 47.78 and 52.06 Hz; new build not measured |
| Stereo and other backends | Check each supported output mode | Not covered by these results |

## Measurement Procedure

Use one Release binary and the same 1920x1080 workload for both routes. Disable
validation, RenderDoc, editor UI, and code-profiler frame logging. Warm shader
and texture caches. Wait for complete frames and settled scene preparation.
Capture at least 60 seconds per route. Report mean, median, p95, p99, maximum,
fraction above 11.11 ms, and accepted presentation cadence. Retain observer
metadata. Keep the editor visible and exclude unrelated GPU workloads.

## Open Findings

Missing source references prevent complete appearance acceptance. No additional
source project is available. The 90 Hz target remains open. Validate the current
CPU fixes, then measure both routes again. Obtain valid GPU timing before
assigning remaining cost to GPU execution. Continuous probe capture and stereo
remain separate checks.
