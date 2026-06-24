# XRDataBuffer RHI Write Model Validation

Status: active validation ledger
Last updated: 2026-06-16

This testing note replaces the remaining validation checklist from the completed
`XRDataBuffer` RHI write model implementation plan. The architecture contract is
now documented in
[XRDataBuffer RHI Write Model](../../architecture/rendering/xrdatabuffer-rhi-write-model.md),
and caller guidance lives in
[XRDataBuffer Write Model](../../developer-guides/rendering/xrdatabuffer-write-model.md).

## Purpose

Prove that the implemented buffer write model behaves correctly on real OpenGL
and Vulkan hardware after the public API, backend state model, dirty-range
tracking, upload allocation, persistent ring scaffolding, readback tickets,
descriptor/device-address integration, and representative caller migrations have
landed.

This is intentionally a testing document. New feature design belongs in the
architecture doc or a future implementation tracker; this file records evidence
for the remaining hardware, barrier, and strategy validation.

## System Under Test

The validation scope covers:

- `XRDataBuffer` and `XRDataBuffer<T>` scoped writer commits.
- Dirty range and revision tracking.
- Backend-neutral state snapshots and readiness flags.
- `XRBufferCpuUploadAllocator` and backend upload routing.
- `XRBufferPersistentRingAllocator` slot ownership.
- `XRBufferReadbackTicket` completion and readback gating.
- OpenGL `GLDataBuffer` route diagnostics.
- Vulkan `VkDataBuffer`, `VulkanStagingManager`, dynamic ring, and
  device-address diagnostics.
- Migrated UI, particle, skinning, physics, softbody, GPUScene, view-set, and
  Surfel GI buffer paths.

Out of scope:

- Rewriting caller APIs already migrated to scoped writers.
- Broad GPU-driven rendering strategy redesign.
- Replacing existing Vulkan dynamic UBO rings or staging pools unless a
  validation failure proves they violate the shared contract.

## Common Setup

Build before every runtime validation pass:

```powershell
dotnet build .\XREngine.Editor\XREngine.Editor.csproj
dotnet build .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore
```

Use the Unit Testing World for repeatable rendering runs:

```powershell
dotnet .\Build\Editor\Debug\AnyCPU\Debug\net10.0-windows7.0\XREngine.Editor.dll --unit-testing
```

For visually observable failures, prefer the normal editor iteration loop with
MCP enabled:

```powershell
dotnet .\Build\Editor\Debug\AnyCPU\Debug\net10.0-windows7.0\XREngine.Editor.dll --unit-testing --mcp --mcp-allow-all --mcp-port 5467
```

Record the exact renderer, GPU, driver, branch, commit, Unit Testing World
settings, diagnostics flags, and session root for every run.

## Logs And Counters

Inspect the latest session under:

`Build/Logs/<configuration>_<tfm>/<platform>/<session>/`

Useful logs:

- `log_opengl.log` or `log_opengl.txt`
- `log_vulkan.log` or `log_vulkan.txt`
- `log_rendering.log` or `log_rendering.txt`
- `log_general.log` or `log_general.txt`
- `profiler-fps-drops.log`
- `profiler-render-stalls.log`

Expected buffer evidence:

- Upload bytes by route.
- Compatibility `PushData` and `PushSubData` bytes.
- Staging allocation and reuse counts.
- Persistent ring allocation, exhaustion, slot index, and fence-wait counts.
- Host-visible write counts.
- Host-cached readback counts.
- Readback bytes.
- Device-address consumers and downgrade reasons.
- Descriptor fallback counts.
- Zero-readback violation counts.
- Buffer readiness, uploaded revision, current revision, dirty range count, and
  pending upload state in upload-stage logs.

## Targeted Source Validation

Run the focused buffer tests when touching this area:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "XRDataBuffer|XRBuffer|Readback|DeviceAddress|Descriptor" --no-restore
```

Required source-level coverage:

- [ ] Writer commit records dirty ranges and revisions.
- [ ] Writer dispose auto-commits when dispose behavior is `Commit`.
- [ ] Writer cancel leaves the buffer revision unchanged.
- [ ] Explicit `Commit()` makes later `Dispose()` a no-op.
- [ ] Explicit `Cancel()` makes later `Dispose()` a no-op.
- [ ] `RequireExplicitCommit` reports an error when disposed without
  `Commit()` or `Cancel()`.
- [ ] Dirty range merging collapses to full upload above the configured
  threshold.
- [ ] Write policy resolves to expected OpenGL and Vulkan routes.
- [ ] Readback tickets do not expose data before completion.
- [ ] Device-address queries report downgrade reasons when unsupported.
- [ ] Writer-driven growth keeps descriptor binding readiness valid.

## OpenGL Persistent Ring Validation

Goal: prove dynamic CPU-to-GPU writes can use fence-protected persistent ring
slots without same-buffer overwrite hazards.

Run setup:

- [ ] Configure Unit Testing World `Rendering.RenderBackend` for OpenGL.
- [ ] Enable upload-stage logging or equivalent buffer route diagnostics.
- [ ] Exercise UI batching, text, particle buffers, GPUScene dynamic buffers,
  and any available persistent-ring candidate path.

Pass criteria:

- [ ] Dynamic buffer writes report a persistent ring route when the backend
  supports it.
- [ ] Slot index, byte offset, byte count, alignment, and backing buffer identity
  are logged or inspectable.
- [ ] Slot reuse is guarded by GL sync or the shared fence abstraction.
- [ ] No frame binds a slot other than the slot committed for that frame.
- [ ] Ring exhaustion is either absent or logged with a clear fallback route.
- [ ] Coherent mapping is not treated as a replacement for slot ownership.
- [ ] Steady-state frames avoid compatibility `PushSubData` floods.
- [ ] No new render-thread stalls appear in the profiler logs beyond expected
  startup warmup.

## Vulkan Persistent Ring Validation

Goal: prove dynamic CPU-to-GPU writes can use fence- or timeline-protected ring
slots under Vulkan without stale descriptor offsets or stale device addresses.

Run setup:

- [ ] Configure Unit Testing World `Rendering.RenderBackend` for Vulkan.
- [ ] Enable Vulkan validation layers for correctness runs.
- [ ] Exercise dynamic render submission, view-set buffers, skinning/blendshape
  updates, and UI/particle dynamic buffers.

Pass criteria:

- [ ] Ring allocations honor uniform/storage alignment, non-coherent atom size,
  indirect-command alignment, and vertex/index alignment as applicable.
- [ ] Non-coherent paths flush the committed range before GPU use.
- [ ] Descriptor offsets, ranges, or device addresses match the committed slot.
- [ ] Slot reuse waits on the correct fence or timeline state.
- [ ] No stale one-frame or multi-frame data appears while moving the camera or
  changing visible content.
- [ ] Vulkan validation reports no buffer lifetime, descriptor, memory hazard,
  or synchronization errors attributable to the ring path.
- [ ] Ring exhaustion is logged with requested bytes, available bytes, frame
  slot, and fallback route.

## Vulkan Device-Local Static Upload Validation

Goal: prove `GpuOnly` and large static writes upload through staging into
device-local storage without requiring a caller-visible permanent CPU mirror.

Run setup:

- [ ] Configure Unit Testing World `Rendering.RenderBackend` for Vulkan.
- [ ] Load at least one imported model or scene with static mesh/attribute
  buffers.
- [ ] Exercise one GI setup or texture-buffer style upload path that uses the
  write model.

Pass criteria:

- [ ] Static writes resolve to a device-local/staging route where supported.
- [ ] `VulkanStagingManager` records allocation, copy, and reuse evidence.
- [ ] Uploaded revision advances after the copy is complete.
- [ ] `IsGenerated`, descriptor readiness, and GPU-use readiness remain distinct
  in diagnostics.
- [ ] Static clean frames do not keep re-uploading unchanged data.
- [ ] CPU mirrors are absent unless required by serialization, editor
  inspection, diagnostics, or explicit asset source ownership.
- [ ] Vulkan validation reports no transfer, ownership, layout, or access hazard
  tied to the upload.

## Vulkan Readback Ticket Validation

Goal: prove GPU-to-CPU reads are explicit, nonblocking by default, and correctly
invalidate host-visible readback memory.

Run setup:

- [ ] Configure Unit Testing World `Rendering.RenderBackend` for Vulkan.
- [ ] Exercise physics chain readback or another explicit `GpuToCpuReadback`
  buffer.
- [ ] Run one diagnostic readback path and one production zero-readback path.

Pass criteria:

- [ ] `XRBufferReadbackTicket` does not expose data before completion.
- [ ] Non-coherent readback memory is invalidated before exposing a span.
- [ ] Blocking wait occurs only through an explicit diagnostic path.
- [ ] Readback bytes and mapped readback buffers are counted.
- [ ] Production strategies reject accidental readback unless the buffer policy
  allows it.
- [ ] `GpuIndirectZeroReadback` and `GpuMeshletZeroReadback`, where available,
  report zero steady-state readback bytes.

## Compute-To-Render Barrier Validation

Goal: prove compute dispatch writes committed through the new model are visible
to later render reads with correct barriers and no stale buffer state.

Run setup:

- [ ] Configure OpenGL and Vulkan runs separately.
- [ ] Exercise softbody compute uploads, physics chain compute, particle
  compute, skinning prepass, or another compute writer followed by render use.
- [ ] Move the camera or modify simulation state so stale reads are visually
  obvious.

Pass criteria:

- [ ] Compute-written buffers transition to render-readable state before draw
  use.
- [ ] OpenGL memory barriers cover the buffer usage that follows the dispatch.
- [ ] Vulkan access masks, pipeline stages, queue ownership, and descriptor
  readiness match the dispatch-to-render dependency.
- [ ] No frame uses a stale uploaded revision after a compute write.
- [ ] Visual output updates on the expected frame and does not flicker between
  old and new buffer contents.
- [ ] Validation layers report no synchronization or descriptor hazards.

## GPU Submission Strategy Validation

Goal: prove the write model does not regress instrumented or zero-readback GPU
submission strategies.

Recommended harness:

```powershell
pwsh Tools/Measure-GameLoopRenderPipeline.ps1 -Strategies CpuDirect,GpuIndirectInstrumented,GpuIndirectZeroReadback
```

Pass criteria:

- [ ] `GpuIndirectInstrumented` may use explicit diagnostics and reports any
  readback bytes clearly.
- [ ] `GpuIndirectZeroReadback` reports zero steady-state readback bytes.
- [ ] `GpuIndirectZeroReadback` does not rely on compatibility `PushSubData`
  floods for normal dynamic writes.
- [ ] Draw counts, material-tier counts, culled command counts, and scatter
  tables update without same-buffer overwrite hazards.
- [ ] Zero-readback material draw path remains compatible with the current
  material binding policy.
- [ ] Fallback or downgrade logs include requested strategy, selected strategy,
  backend capability, and reason.

## Run Record Template

Copy this section for each validation run.

### YYYY-MM-DD Scenario Name

Session root:

`Build/Logs/...`

Build:

- Branch:
- Commit:
- Configuration:
- Renderer:
- GPU and driver:
- CPU and memory:
- Unit Testing World settings:
- Diagnostics flags:

Logs:

- [ ] API log
- [ ] `log_rendering`
- [ ] `log_general`
- [ ] `profiler-fps-drops.log`
- [ ] `profiler-render-stalls.log`
- [ ] MCP captures or RenderDoc captures, if used

Metrics:

| Metric | Value |
|---|---:|
| Upload bytes by route | |
| Compatibility push bytes | |
| Staging allocations | |
| Staging reuses | |
| Persistent ring allocations | |
| Persistent ring exhaustions | |
| Persistent ring fence waits | |
| Host-visible writes | |
| Host-cached readbacks | |
| Readback bytes | |
| Device-address downgrades | |
| Descriptor fallbacks | |
| Zero-readback violations | |
| Worst render stall | |
| Worst FPS drop | |

Result:

- [ ] Pass
- [ ] Fail
- [ ] Inconclusive

Notes:

- TBD

## Closeout Criteria

This validation line can close when:

- [ ] Targeted source tests pass or remaining failures are proven unrelated and
  tracked elsewhere.
- [ ] OpenGL persistent ring validation passes on hardware.
- [ ] Vulkan persistent ring validation passes on hardware.
- [ ] Vulkan device-local static upload through staging passes on hardware.
- [ ] Vulkan readback ticket validation passes on hardware.
- [ ] Compute dispatch writes followed by render reads pass on OpenGL and
  Vulkan.
- [ ] `GpuIndirectInstrumented` telemetry remains explainable.
- [ ] `GpuIndirectZeroReadback` reports zero steady-state readback bytes and no
  compatibility push flood.
- [ ] Remaining failures have linked logs, captures, and owner follow-ups.

