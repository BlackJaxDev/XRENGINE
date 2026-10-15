# Vulkan Draw Readability Validation

Date: 2026-10-14

Architecture: [Vulkan command recording](../../../architecture/rendering/vulkan-command-recording.md#inline-mesh-draw-flow)
Validation: [Vulkan core validation](../../testing/rendering/vulkan-core-validation.md)

## Scope

Refactor the direct `VkMeshRenderer.RecordDraw` path for readability. Preserve
locking, command order, resource use, diagnostics, and failure results. The
prepared worker encoder and indirect recording paths are outside this change.

## Changes And Source Review

The main method now calls named preparation, deformation-upload, indexed-draw,
and non-indexed-draw methods. Detailed trace output has separate helpers.
`DrawRecordingState` is a local value type passed by reference. It replaces the
captured local state without adding a heap object or persistent renderer state.

The review confirmed these rules:

- The existing recording lock encloses all operations.
- Indexed attempts remain in triangle, line, and point order. Success does not
  skip later indexed attempts.
- The non-indexed fallback runs only when no indexed attempt succeeds.
- Uniform notification occurs at most once for the logical draw.
- Successful recording returns empty failure output, even when another
  primitive attempt failed.
- Vulkan bindings, draw calls, shader coverage, and profiling scope order remain
  unchanged.

No tests were added or changed.

## Build

The first isolated build could not read the Windows SDK registry in the sandbox.
The same session build passed with the required access: zero warnings and zero
errors. The session name is `vulkan-draw-cleanup`.

## Runtime Setup

Use an isolated copy of the Unit Testing World settings. Select Vulkan with
`RequireRequested`, the Default render pipeline, desktop mode, and the Sponza
deferred model at the origin. Disable light probes and temporal antialiasing for
this bounded visual check. Do not change the shared source settings.

The session uses these existing environment variables:

- `XRE_UNIT_TEST_WORLD_SETTINGS_PATH`: isolated fixture file.
- `XRE_ADVANCED_RENDER_PIPELINE_MODE=Disabled`.
- `XRE_FORCE_MESH_SUBMISSION_STRATEGY=CpuDirect`.
- `XRE_VULKAN_COMMAND_CHAINS=0`.
- `XRE_VULKAN_PRIMARY_COMMAND_BUFFER_REUSE=0`.
- `XRE_VULKAN_VALIDATION=1`.
- `XRE_VULKAN_SYNC_VALIDATION=1`.
- `XRE_VK_TRACE_DRAW=1`.

`CpuDirect` selects CPU draw submission; Vulkan still executes the rendering on
the GPU. Disabling command chains and primary reuse makes fresh inline draws
exercise the edited methods. Trace messages distinguish indexed and
non-indexed commands.

## Runtime Results

The isolated session passed the bounded desktop check. Both PNG captures were
opened and inspected. They show textured Sponza geometry, lighting, and a
different view after the camera moved. The capture tool reported
`VulkanRenderer` and a completed GPU readback for both images.

| View | Position | Look-at target | Capture time |
|---|---|---|---|
| First | `(8, 3, 0)` | `(0, 3, 0)` | 13:57:17, America/Los_Angeles |
| Second | `(-8, 4, 1)` | `(3, 3, 0)` | 13:57:40, America/Los_Angeles |

The first capture request timed out during cold resource preparation. The
engine remained active with no terminal fault. Later captures succeeded after
the required programs and resources became ready. Camera movement also caused
resource-admission retries for newly visible meshes.

At the final snapshot, render frame 989 was `Completed`. The counters reported
99 completed frames, 102 completed command recordings, zero failed frames, and
zero rejected command recordings. There were 35 deferred and 855 rejected frame
attempts during resource admission. These counts are not performance results.
No command chains were scheduled or recorded, and no primary command buffers
were reused.

Standard and synchronization validation were both active. Their cumulative
counters reported zero errors and 13 warnings. The reported warnings concern
the Vulkan loader and vertex attributes that a shader does not consume.

The mesh log contains 48,305 indexed draw traces and 2,737 non-indexed draw
traces. Indexed examples include `sponza_14` in `DeferredGBufferFBO` and
`UIBatchTextQuadMesh` in the UI pass. Non-indexed examples include
`Shared.FullscreenTriangle` in BRDF integration and GTAO passes. These messages
come from the edited direct draw helpers.

The Vulkan, rendering, general, and mesh logs contain no `VUID-`,
`Validation Error`, or `[ERROR]`/`[ERR]` entries. One UI mesh was not ready during
startup while its program was pending. No skipped draws were logged after
warmup. Early BRDF, shadow-publication, and frame-history warnings remain in the
startup log. Slow frame-data preparation messages also remain; this check does
not measure or change that behavior. No shutdown validation errors were found,
but the log does not establish a full resource-teardown audit.

The session manager stopped only `vulkan-draw-cleanup`. Shared Unit Testing World
settings were not changed.

## Limits

This bounded desktop check does not establish performance, hardware XR parity,
or coverage of every failure branch. The user has not yet reported a result.
