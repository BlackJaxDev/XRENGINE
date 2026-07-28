# OpenGL GPU Pipeline Timestamp Readiness Investigation

## Status

Open instrumentation defect. No rendering source fix was attempted as part of
the self-iteration harness implementation.

## Problem

The OpenGL backend issues GPU timestamp queries during a stable Unit Testing
World workload, but no query becomes available to the render-pipeline GPU
profiler. Consequently, `dump_gpu_render_pipeline_profile` has no history to
write. A self-iteration campaign that requires both CPU and GPU diagnostic
dumps rejects the OpenGL baseline instead of silently diagnosing from CPU data
alone.

## Reproduction

Two isolated `OpenGL` plus `CpuDirect` captures used a minimal alternate Unit
Testing World JSONC to remove asset streaming and shader-warmup churn:

| Capture | Dense timestamps | Stable samples | Queries issued | Query readback bytes | GPU-ready samples | GPU dumps |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `opengl-minimal-success` | yes | 52 | 11,783 | 0 | 0 | 0 |
| `opengl-minimal-sparse-gpu` | no | 274 | 64,116 | 0 | 0 | 0 |

Both captures:

- reached the configured stability window;
- reported `OpenGL` and `CpuDirect` as the active workload;
- wrote the CPU frame hierarchy;
- returned `isError=true` from
  `dump_gpu_render_pipeline_profile(all_pipelines=true)`;
- recorded zero ready GPU samples and no GPU p50/p95/p99 history.

Ignored evidence:

- `Build/_AgentValidation/self-iteration/_smoke/opengl-minimal-success/`
- `Build/_AgentValidation/self-iteration/_smoke/opengl-minimal-sparse-gpu/`

## Attempted Isolations

1. Dense timestamp mode was enabled to eliminate sparse instrumentation as the
   cause. Queries were issued, but none resolved.
2. Dense timestamp mode was disabled to exercise the normal query cadence.
   The result was unchanged.
3. The workload was reduced to a minimal world. Stability passed, ruling out
   the original Sponza shader-compilation churn as the reason GPU history was
   unavailable.

## Current Assessment

The evidence places the defect after query issuance and before publication of a
ready `RenderPipelineGpuProfiler` snapshot. The next investigation should trace
the OpenGL query lifecycle through:

- `GLRenderQuery.SetTimestamp`;
- `GLRenderQuery.TryGetTimestamp` and `GL_QUERY_RESULT_AVAILABLE`;
- pending-query retirement in `RenderPipelineGpuProfiler`;
- OpenGL context/thread ownership while results are polled.

RenderDoc can confirm that the representative frame and passes execute, but it
must remain a separate diagnostic capture rather than a formal timing
repetition.

## Suggested Validation For A Fix

Repeat both dense and sparse captures. A fix is complete only when each reports
nonzero GPU-ready samples, a finite GPU frame timing, and at least one
per-pipeline timing dump without blocking query reads or introducing per-frame
allocations.
