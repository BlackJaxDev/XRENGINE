# Vulkan Uniform Readability Validation

Date: 2026-10-14

Architecture: [Mesh uniform source map](../../../architecture/rendering/vulkan-command-recording.md#mesh-uniform-source-map)
Validation: [Vulkan core validation](../../testing/rendering/vulkan-core-validation.md)

## Scope

Make `VkMeshRenderer.Uniforms.cs` easier to read without changing behavior or
adding runtime cost. Move complete methods into focused partial files. Keep
method signatures, executable statements, and field order. Correct indentation
and add comments about storage ownership, write leases, publication, source
priority, and reflected byte layout.

The change introduces no helper calls, wrappers, allocations, locks, or shader
changes. Existing cold-path allocations and legacy conversion paths remain.

## Validation Method

Save the original source and Release Vulkan assembly before editing. Compare
Roslyn token streams for each member, excluding comments and whitespace.
Compare Release IL with metadata operands resolved to symbols. Generated lambda
ordinals can change when methods move, so compare their owning method, lambda
slot, and body. Also compare local signatures, exception regions, the class
initializer, and compiled field order.

Use named isolated sessions `vulkan-uniform-before` and `vulkan-uniform-after`.
Both use Release builds and the same private Unit Testing World fixture:
Vulkan with `RequireRequested`, Default pipeline, desktop Sponza at the origin,
light probes off, and temporal antialiasing off. The shared settings file is
unchanged. Use `CpuDirect` submission, command chains off, and primary reuse off
to exercise fresh draw recording on the GPU.

For timing, keep validation and draw tracing off. Enable
`XRE_VULKAN_RECORDING_DIAG=1` in both sessions to collect CPU stage time and
allocation counters. These counters add diagnostic overhead. They are a
comparison under instrumentation, not a shipping frame-rate benchmark.

After warmup, collect three 30-second windows at camera position `(8, 3, 0)`,
looking at `(0, 3, 0)`. Run only one validation editor during each measurement.
Do not build during a measurement. Divide the change in cumulative
`primary_mesh_operation.process_elapsed_ms` by the change in invocation count
and completed frame count. This stage includes uniform writes, descriptor
work, and draw setup. The reported `frame_data_auto_uniform_upload` stage has
no active recording scope in this path, so its zero values cannot measure
uniform cost. Uniform binding counters describe the latest frame; they are
not cumulative totals.

## Findings

The first source comparison found a moved static field. Restore
`NormalizedEngineUniformNames` to the end of the main partial to preserve both
initializer order and compiled field order. No runtime change was accepted.

The final isolated editor build passed with zero warnings and zero errors.
Build evidence is in the task run under
`Build/_AgentValidation/20261014-122330-merge-and-startup/reports/uniform-cleanup/`.
The same run contains the disposable source/IL comparison tool and reports,
profiler snapshots, and viewport captures.

The final comparison passed:

- All 119 source member token streams match.
- All three initialized fields keep their order.
- All 103 uniform methods and nine generated lambda bodies have matching
  Release IL after metadata and generated-name normalization.
- The class initializer matches. All 153 fields keep their compiled order,
  types, and attributes.
- None of the other 29,640 ordinary methods in the Vulkan DLL changed.

The first baseline timing attempt ended when its editor exited normally.
The bootstrap log reports `Engine.Run returned normally` and `ProcessExit`;
it does not establish why the session closed. Discard that incomplete sample.
For the controlled repeat, use the `vulkan-uniform-after` session output for
both runs. While the session is stopped, install the saved baseline Vulkan DLL,
measure it, then restore the final Vulkan DLL and measure again. Session
retention removed stopped build outputs during the final import cleanup build.
That build failed with three missing-file copy errors. A session-manager build
restored the outputs and passed with zero warnings and zero errors. The final
source and IL comparison also passed again.

This rebuild means the timing pair does not establish byte-identical editor
dependencies. Treat the live timings as a bounded check of the exercised path,
not an isolated uniform microbenchmark. The exact uniform IL comparison is the
stronger evidence that the cleanup adds no managed operations. The saved final
DLL hash matches the DLL from the successful final build.

## Live Timing Results

| Window | Baseline frames | Baseline mesh ms/frame | Cleaned frames | Cleaned mesh ms/frame |
|---|---:|---:|---:|---:|
| 1 | 385 | 33.910 | 526 | 25.317 |
| 2 | 346 | 37.509 | 518 | 25.764 |
| 3 | 337 | 40.027 | 517 | 25.542 |

Each window recorded 323 mesh operations per completed frame. All eight
boundary samples reported 2,055 auto-uniform fast-path draws, two legacy draws,
2,055 plan hits, and two plan misses. Both runs had zero failed frames during
the measurement windows. The broader mesh stage reported 792,432 allocated
bytes at each baseline boundary. Candidate samples reported 792,424 bytes,
with one sample at 827,784 bytes. These are whole-stage samples, not uniform-only
allocation measurements.

No slowdown appeared in this bounded run. Do not attribute the lower times to
the cleanup: machine load, warmup, and the intervening editor rebuild limit the
comparison. The unchanged executable tokens and Release IL show that the
cleanup introduces no new allocation sites or managed work.

## Live Correctness Results

The final DLL rendered Sponza from `(8, 3, 0)` toward `(0, 3, 0)` and from
`(-8, 4, 1)` toward `(3, 3, 0)`. Both PNG captures were opened and inspected.
They show textured geometry, lighting, and the expected change in view. An
earlier immediate capture after camera movement showed incomplete output;
the later capture showed the full scene. Use the settled captures at 14:27:28
and 14:28:42, America/Los_Angeles, as the validation evidence.

The final candidate snapshot at render frame 1,968 reported 1,104 completed
frames and zero failed frames. Standard Vulkan validation and synchronization
validation were active. Their cumulative counters reported zero errors and
13 warnings. The retained warning samples concern the loader and unused vertex
attributes. Release session files do not contain the Vulkan message stream;
these results come from the saved MCP cumulative validation report.

Both `GetFirstAutoUniformWriteFailure` results were null: the candidate and
the saved baseline reported no rejected uniform write. The optional
`XRE_VULKAN_AUTO_UNIFORM_PARITY=1` check did report a mismatch. The baseline
reproduced it with the same final editor dependencies and validation flags:

| Result | Candidate | Saved baseline |
|---|---|---|
| First mismatched entry | `EngineTime`, byte offset 0 | `EngineTime`, byte offset 0 |
| Block | `XREngine_AutoUniforms_Vertex_Frame_Instance` | Same block |
| Block size and frequency | 16 bytes, Frame | 16 bytes, Frame |
| Latest-frame parity sites in sampled report | 161 | 161 |
| Latest-frame frequency mismatch sites | 2 | 2 |
| Vulkan validation errors | 0 | 0 |
| Failed frames | 0 | 0 |

The parity checker replaces mismatched bytes with the legacy bytes. This is
existing diagnostic behavior. The source split is ruled out as the cause by
both the baseline reproduction and the compiled-code comparison. No timing or
generation logic was changed. A separate investigation can compare the captured
`EngineTime` publication with the timer value used by the legacy writer.

Both owned sessions were stopped. The final DLL was restored after the baseline
control run. The shared editor settings were not changed.

## Existing Source Checks

After live validation, the user approved the test updates. Eleven existing
checks now read the partial files that own their asserted code. One check now
asserts the capacity assignment instead of an old comment. Other assertions
remain unchanged.

The isolated test project build passed with zero warnings and zero errors.
The final isolated `dotnet vstest` run executed exactly 11 checks: three passed,
eight failed, and none were skipped. The three passing checks cover color
uniform writes, renderer-arena storage, and explicit arena lifetime failures.

The eight failures assert text absent from the saved original uniform source or
from unchanged code:

| Check area | Existing stale assertion |
|---|---|
| Repeated skipped preparation | `OnRenderRequested` in `VkMeshRenderer.cs`. |
| Per-eye current matrices | A snapshot assignment in `VkMeshRenderer.cs`. |
| Stereo history | `PreviousRightEyeViewMatrix` in `VkMeshRenderer.cs`. |
| FPS overlay | An old reusable-command-buffer refresh call. |
| Frequency ownership | `Renderer.MappedFrameArena` instead of the current resource access. |
| Inverse model resolution | A `nameof` switch case where the resolver uses an enum case. |
| Frame-stable time | The old `EnsureMaterialUniformFrameTime` method. |
| Skinning parity | A direct `MeshRenderer.ActiveSkinPaletteBase` value assignment. |

Do not treat this test run as green. The exact command, first failures, baseline
classification, and TRX are in `reports/uniform-cleanup/` under the task run.
Updating these older contracts needs a separate review of their current code
ownership and intended behavior; this cleanup does not weaken them.

## Limits

The live check covers the desktop fixture. It does not cover all uniform types,
failure branches, or hardware XR. Equal IL establishes equal compiled managed
operations, not identical native instruction placement or wall-clock timings.
The user has not yet reported a result.
