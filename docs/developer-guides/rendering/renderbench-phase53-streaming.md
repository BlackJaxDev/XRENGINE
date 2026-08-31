# Headless Phase 5.3 texture streaming

`XREngine.RenderBench --scenario phase53-streaming` exercises the normal
imported-texture manager and renderer-scoped Vulkan upload service without
creating a window, surface, swapchain, editor session, or XR session. It is a
correctness scenario, not a performance recipe.

## Run

Build RenderBench using the normal project build or the repository's isolated
`--artifacts-path` convention. Then run its DLL from the repository root:

```powershell
dotnet <artifacts>/bin/XREngine.RenderBench/debug/XREngine.RenderBench.dll `
  --scenario phase53-streaming --scenario-depth both --scenario-repeats 2 `
  --scenario-frames 240 --width 640 --height 360 --output-dir <run>/reports/streaming
```

Use an output directory under `Build/_AgentValidation/<task-run>/`. The parent
launches fresh, windowless children and retains each result/stdout/stderr.
For a single diagnostic child, add `--scenario-lane production` and choose one
depth convention. The streaming default is 240 boundary attempts per transition;
this bounds each upload/cancellation stage, not the total accepted frame count.
Every accepted production frame is retained in `scenario-result.json`.

For native validation, set `XRE_VULKAN_VALIDATION=1` and
`XRE_VULKAN_SYNC_VALIDATION=1` in the launching process. Require the corresponding
enabled flags and active debug messenger in `nativeValidation`; zero errors
with disabled layers is not validation evidence.

## What the scenario observes

- Two background RGBA8 mip chains (4096 and 2048 square) and one foreground
  chain (256 square) enter through manager-owned generation/publication authority.
  The retained, deterministic memory source isolates uploads; it is not a new
  disk-decode or cache-parse benchmark.
- A real world/viewport performs ordinary collect, swap, record, and submit.
  Render-thread scheduled work runs at the normal pre-collection boundary.
- Each ticket must become ready only after its last chunk completes and the
  native descriptor generation is published. The three chains total 34 mips and
  112,197,628 bytes, larger than the bounded imported staging capacity.
- After production completion, cold copies read every mip in bands no larger
  than 1 MiB. Every SHA-256 must match the retained CPU source. Copies require
  an authentic completed production receipt, exclusive diagnostic admission,
  and the exact native image-generation lifetime lease. They never feed
  rendering or count as zero-readback/performance evidence.
- Cancellation controls cover an admitted job canceled before native submission
  and a real submitted chunk whose completion has not yet been observed.
  Neither may publish a descriptor generation. Subsequent ordinary boundaries
  must settle the service and allow dependent retirement rotations.
  Submitted-but-unobserved is not a claim that the GPU was still executing at
  the cancellation instant.
- Transfer batches contain at most four chunks / 16 MiB; a chunk is at most
  4 MiB. Four staging slots are reserved for foreground work and eight for
  background work. Full capacity yields rather than allocating an overflow
  staging buffer. Publication and completed-staging retirement are separately
  bounded at each render-frame boundary.
- Diagnostics separate worker preparation, native allocation, staging copy,
  transfer recording, CPU fence wait, GPU elapsed time and final publication,
  alongside queue age, bytes, items and budget deferrals. Four worker-prepared
  timestamp pairs remain batch-owned through fence completion and command
  cleanup. Result reads never request a GPU wait; unavailable samples are
  counted explicitly.

Pipeline/material backing preparation or required-upload completion budgets can
produce a typed Pending result before explicit target acquisition. The cold
harness yields outside production admission and retries a fresh plan for at most
five seconds; no unsubmitted plan becomes a frame receipt and no required compute
operation is silently dropped. The material scenario separately exercises a
4096² texture bound to the real required manifest before upload completion.

## Acceptance boundaries

Successful chunk counts alone do not establish coalescing, foreground capacity
isolation, retirement safety, or correct pixels. Inspect actual batch/chunk
counts, native identity/content checks, cancellation evidence, service drain,
and native validation separately.

The final `reports/phase53-streaming-final` matrix under the Phase 5.2 bounded
rendering validation root passes normal/reversed depth twice, with 209 completed
receipts per child. Each verifies all 34 mip hashes, 55 completed chunks in
52 submissions, three final publications and 52 GPU timing samples with zero
unavailable. Cancellation publishes nothing and drains through seven ordinary
boundaries. Standard/synchronization validation report zero errors; two loader
warnings per child are recorded separately. The earlier content-only run with
55 single-child submissions is not the coalescing acceptance result.

See [the Phase 5.3 closeout](../../work/progress/rendering/vulkan-phase53-headless-completion.md)
for the complementary material and pipeline evidence. No desktop, Advanced
shaded-output, cross-vendor, or OpenXR promotion is implied.
