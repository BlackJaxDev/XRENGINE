# Advanced Render Pipeline GPU Visibility Preparation And Deformation Slice

Date: 2026-07-29
Status: Implementation complete; live visibility/depth capture validation moves
with document 04

## Outcome

Document 03 now provides one shared, pipeline-neutral preparation path for
desktop and OpenXR-eye consumers:

- completion-gated visibility feedback and deterministic animation cadence,
  grace, bone-LOD, and telemetry contracts;
- fixed-capacity, collision-safe, whole-job deformation admission;
- frame-slot current/previous deformed-vertex arenas with stable draw offsets,
  fence-driven growth, and explicit velocity invalidation;
- live aggregate deformation buffers and a bounded compute dispatch per
  layout/precision family for OpenGL and Vulkan;
- sparse blendshape then skinning evaluation, including spill influences and
  precomposed-palette handling;
- explicit consumer barriers for visibility, depth, velocity, reconstruction,
  shadows, probes, and captures;
- persistent per-view early/late visibility planning with normal and reversed
  depth conventions, conservative history invalidation, GPU-only counts, and
  same-frame deferred-candidate recovery;
- shared static/skinned meshlet and traditional-indirect payload/range
  contracts without material-instance command fragmentation; and
- one shared preparation publication acquired by the desktop advanced pipeline
  and RVC eye pipeline while each keeps independent output-local resources and
  histories.

The final hot-path audit removed a per-frame
`AdvancedDeformationExecutor` allocation by making the executor owned and
reused by the live GPU deformation resource.

## Validation

- Focused NUnit suite: 47 passed, 0 failed. It covers animation scheduling,
  deterministic deformation, arena history/growth, whole-job admission,
  bounded dispatch, explicit Vulkan failure rather than silent fallback,
  visibility/indirect planning, mixed static/skinned meshlets, shared
  desktop/RVC acquisition, GPU buffer rotation, command reuse, zero readback,
  and warmed zero-allocation planning.
- Vulkan engine shader compiler: all five preparation compute shaders compiled
  successfully.
- Standalone glslang validation: all five preparation shaders compiled to both
  Vulkan and OpenGL SPIR-V (10 binaries total).
- Isolated runtime-rendering build: 0 errors. Existing Magick.NET advisory
  warnings remain unrelated.
- Benchmark matrix: 1, 8, 32, and 128 instances across still, moving,
  offscreen, and shadowed scenarios; every compatible case remained one
  aggregate dispatch with zero warmed managed allocations.
- `rdc doctor`: passed, including the Vulkan layer and replay support.

The focused suite initially exposed a Vulkan-only shader rewrite collision:
the loose uniform `ViewMask` had the same name as a visibility-record field.
Renaming the uniform to `ActiveViewMask` fixed the engine-rewritten GLSL; the
full 47-test suite then passed.

## Validation Boundary

A useful RenderDoc validation of early/late visibility cannot happen until
document 04 declares the named visibility/depth resources and schedules the
early raster, current depth-pyramid build, and late raster. Capturing the
currently selected reference renderer would not validate these new resources
or their barriers. The OpenGL/Vulkan GPU-capture checkbox therefore remains
open in document 03 and is explicitly the closeout gate in document 04.

Disposable evidence is under:

`Build/_AgentValidation/renderer-root-trace/03-gpu-preparation-20260729/`

## Next

Implement document 04 in this order:

1. choose and version the desktop visibility payload format;
2. declare visibility/depth/history/counter/indirect resources;
3. wire early compute, early raster, depth-pyramid generation, late compute,
   and late raster;
4. connect every geometry producer to one payload contract; and
5. inspect named outputs and barriers in OpenGL and Vulkan RenderDoc captures.
