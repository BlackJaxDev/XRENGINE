# XRDataBuffer RHI Write Model Validation

Scope: Validate `XRDataBuffer` write scopes, dirty ranges, persistent rings, device-local uploads, readback tickets, compute-to-render barriers, and GPU submission strategy integration.

Architecture: [XRDataBuffer RHI Write Model](../../../architecture/rendering/xrdatabuffer-rhi-write-model.md). Code todos: [CPU Direct Fast Path](../../todo/rendering/optimization/cpu-direct-fast-path-todo.md), [Compact Zero-Readback Rendering](../../todo/rendering/optimization/compact-zero-readback-rendering-todo.md). Parent validation: [GPU-Driven Submission Validation](gpu-driven-submission-validation.md).

## Setup

Tasks: `Build-Editor`, `Test-VulkanPhase3-Regression`, and `Measurement-GameLoopRenderPipeline-Release-All`. Run focused buffer tests with `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "XRDataBuffer|XRBuffer|Readback|DeviceAddress|Descriptor" --no-restore` when source changes touch this area.

Launch profiles: `Editor (Unit Testing World)` and `Editor (Unit Testing World, Validation Layers)`.

Configure Unit Testing World `Rendering.RenderBackend` for OpenGL or Vulkan as required. Enable upload-stage logging or equivalent route diagnostics when a check needs slot, offset, alignment, or route evidence. Use `XRE_FORCE_MESH_SUBMISSION_STRATEGY` to compare `CpuDirect`, `GpuIndirectInstrumented`, and `GpuIndirectZeroReadback`.

## Checks

### Targeted Source Validation

Architecture: [XRDataBuffer RHI Write Model](../../../architecture/rendering/xrdatabuffer-rhi-write-model.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Writer commit records dirty ranges and revisions. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Writer dispose auto-commits when dispose behavior is `Commit`. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Writer cancel leaves the buffer revision unchanged. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Explicit `Commit()` makes later `Dispose()` a no-op. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Explicit `Cancel()` makes later `Dispose()` a no-op. | Run the procedure for this feature. | The stated result is true. | Open | none |
| `RequireExplicitCommit` reports an error when disposed without `Commit()` or `Cancel()`. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Dirty range merging collapses to full upload above the configured threshold. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Write policy resolves to expected OpenGL and Vulkan routes. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Readback tickets do not expose data before completion. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Device-address queries report downgrade reasons when unsupported. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Writer-driven growth keeps descriptor binding readiness valid. | Run the procedure for this feature. | The stated result is true. | Open | none |
### OpenGL Persistent Ring Validation

Architecture: [XRDataBuffer RHI Write Model](../../../architecture/rendering/xrdatabuffer-rhi-write-model.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Configure Unit Testing World `Rendering.RenderBackend` for OpenGL. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Enable upload-stage logging or equivalent buffer route diagnostics. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Exercise UI batching, text, particle buffers, GPUScene dynamic buffers, and any available persistent-ring candidate path. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Dynamic buffer writes report a persistent ring route when the backend supports it. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Slot index, byte offset, byte count, alignment, and backing buffer identity are logged or inspectable. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Slot reuse is guarded by GL sync or the shared fence abstraction. | Run the procedure for this feature. | The stated result is true. | Open | none |
| No frame binds a slot other than the slot committed for that frame. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Ring exhaustion is either absent or logged with a clear fallback route. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Coherent mapping is not treated as a replacement for slot ownership. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Steady-state frames avoid compatibility `PushSubData` floods. | Run the procedure for this feature. | The stated result is true. | Open | none |
| No new render-thread stalls appear in the profiler logs beyond expected startup warmup. | Run the procedure for this feature. | The stated result is true. | Open | none |
### Vulkan Persistent Ring Validation

Architecture: [XRDataBuffer RHI Write Model](../../../architecture/rendering/xrdatabuffer-rhi-write-model.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Configure Unit Testing World `Rendering.RenderBackend` for Vulkan. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Enable Vulkan validation layers for correctness runs. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Exercise dynamic render submission, view-set buffers, skinning/blendshape updates, and UI/particle dynamic buffers. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Ring allocations honor uniform/storage alignment, non-coherent atom size, indirect-command alignment, and vertex/index alignment as applicable. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Non-coherent paths flush the committed range before GPU use. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Descriptor offsets, ranges, or device addresses match the committed slot. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Slot reuse waits on the correct fence or timeline state. | Run the procedure for this feature. | The stated result is true. | Open | none |
| No stale one-frame or multi-frame data appears while moving the camera or changing visible content. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Vulkan validation reports no buffer lifetime, descriptor, memory hazard, or synchronization errors attributable to the ring path. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Ring exhaustion is logged with requested bytes, available bytes, frame slot, and fallback route. | Run the procedure for this feature. | The stated result is true. | Open | none |
### Vulkan Device-Local Static Upload Validation

Architecture: [XRDataBuffer RHI Write Model](../../../architecture/rendering/xrdatabuffer-rhi-write-model.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Configure Unit Testing World `Rendering.RenderBackend` for Vulkan. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Load at least one imported model or scene with static mesh/attribute buffers. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Exercise one GI setup or texture-buffer style upload path that uses the write model. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Static writes resolve to a device-local/staging route where supported. | Run the procedure for this feature. | The stated result is true. | Open | none |
| `VulkanStagingManager` records allocation, copy, and reuse evidence. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Uploaded revision advances after the copy is complete. | Run the procedure for this feature. | The stated result is true. | Open | none |
| `IsGenerated`, descriptor readiness, and GPU-use readiness remain distinct in diagnostics. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Static clean frames do not keep re-uploading unchanged data. | Run the procedure for this feature. | The stated result is true. | Open | none |
| CPU mirrors are absent unless required by serialization, editor inspection, diagnostics, or explicit asset source ownership. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Vulkan validation reports no transfer, ownership, layout, or access hazard tied to the upload. | Run the procedure for this feature. | The stated result is true. | Open | none |
### Vulkan Readback Ticket Validation

Architecture: [XRDataBuffer RHI Write Model](../../../architecture/rendering/xrdatabuffer-rhi-write-model.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Configure Unit Testing World `Rendering.RenderBackend` for Vulkan. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Exercise physics chain readback or another explicit `GpuToCpuReadback` buffer. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Run one diagnostic readback path and one production zero-readback path. | Run the procedure for this feature. | The stated result is true. | Open | none |
| `XRBufferReadbackTicket` does not expose data before completion. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Non-coherent readback memory is invalidated before exposing a span. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Blocking wait occurs only through an explicit diagnostic path. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Readback bytes and mapped readback buffers are counted. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Production strategies reject accidental readback unless the buffer policy allows it. | Run the procedure for this feature. | The stated result is true. | Open | none |
| `GpuIndirectZeroReadback` and `GpuMeshletZeroReadback`, where available, report zero steady-state readback bytes. | Run the procedure for this feature. | The stated result is true. | Open | none |
### Compute-To-Render Barrier Validation

Architecture: [XRDataBuffer RHI Write Model](../../../architecture/rendering/xrdatabuffer-rhi-write-model.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Configure OpenGL and Vulkan runs separately. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Exercise softbody compute uploads, physics chain compute, particle compute, skinning prepass, or another compute writer followed by render use. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Move the camera or modify simulation state so stale reads are visually obvious. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Compute-written buffers transition to render-readable state before draw use. | Run the procedure for this feature. | The stated result is true. | Open | none |
| OpenGL memory barriers cover the buffer usage that follows the dispatch. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Vulkan access masks, pipeline stages, queue ownership, and descriptor readiness match the dispatch-to-render dependency. | Run the procedure for this feature. | The stated result is true. | Open | none |
| No frame uses a stale uploaded revision after a compute write. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Visual output updates on the expected frame and does not flicker between old and new buffer contents. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Validation layers report no synchronization or descriptor hazards. | Run the procedure for this feature. | The stated result is true. | Open | none |
### GPU Submission Strategy Validation

Architecture: [XRDataBuffer RHI Write Model](../../../architecture/rendering/xrdatabuffer-rhi-write-model.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| `GpuIndirectInstrumented` may use explicit diagnostics and reports any readback bytes clearly. | Run the procedure for this feature. | The stated result is true. | Open | none |
| `GpuIndirectZeroReadback` reports zero steady-state readback bytes. | Run the procedure for this feature. | The stated result is true. | Open | none |
| `GpuIndirectZeroReadback` does not rely on compatibility `PushSubData` floods for normal dynamic writes. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Draw counts, material-tier counts, culled command counts, and scatter tables update without same-buffer overwrite hazards. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Zero-readback material draw path remains compatible with the current material binding policy. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Fallback or downgrade logs include requested strategy, selected strategy, backend capability, and reason. | Run the procedure for this feature. | The stated result is true. | Open | none |
### YYYY-MM-DD Scenario Name

Architecture: [XRDataBuffer RHI Write Model](../../../architecture/rendering/xrdatabuffer-rhi-write-model.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| API log | Run the procedure for this feature. | The stated result is true. | Open | none |
| `log_rendering` | Run the procedure for this feature. | The stated result is true. | Open | none |
| `log_general` | Run the procedure for this feature. | The stated result is true. | Open | none |
| `profiler-fps-drops.log` | Run the procedure for this feature. | The stated result is true. | Open | none |
| `profiler-render-stalls.log` | Run the procedure for this feature. | The stated result is true. | Open | none |
| MCP captures or RenderDoc captures, if used | Run the procedure for this feature. | The stated result is true. | Open | none |
| Pass | Run the procedure for this feature. | The stated result is true. | Open | none |
| Fail | Run the procedure for this feature. | The stated result is true. | Open | none |
| Inconclusive | Run the procedure for this feature. | The stated result is true. | Open | none |
### Closeout Criteria

Architecture: [XRDataBuffer RHI Write Model](../../../architecture/rendering/xrdatabuffer-rhi-write-model.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Targeted source tests pass or remaining failures are proven unrelated and tracked elsewhere. | Run the procedure for this feature. | The stated result is true. | Open | none |
| OpenGL persistent ring validation passes on hardware. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Vulkan persistent ring validation passes on hardware. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Vulkan device-local static upload through staging passes on hardware. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Vulkan readback ticket validation passes on hardware. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Compute dispatch writes followed by render reads pass on OpenGL and Vulkan. | Run the procedure for this feature. | The stated result is true. | Open | none |
| `GpuIndirectInstrumented` telemetry remains explainable. | Run the procedure for this feature. | The stated result is true. | Open | none |
| `GpuIndirectZeroReadback` reports zero steady-state readback bytes and no compatibility push flood. | Run the procedure for this feature. | The stated result is true. | Open | none |
| Remaining failures have linked logs, captures, and owner follow-ups. | Run the procedure for this feature. | The stated result is true. | Open | none |

## Hardware Matrix
| Backend | Required result |
|---|---|
| OpenGL | Persistent ring and compute-to-render checks pass or report an explicit unsupported route. |
| Vulkan | Persistent ring, device-local upload, readback ticket, barrier, and device-address checks pass with validation layers clean. |

## Failures
| Check | Symptom | Investigation or code item |
|---|---|---|
| Ring overwrite | A frame uses a slot other than the committed slot. | Track in [CPU Direct Fast Path](../../todo/rendering/optimization/cpu-direct-fast-path-todo.md). |
| Production readback | `GpuIndirectZeroReadback` or `GpuMeshletZeroReadback` reports readback bytes. | Track in [Compact Zero-Readback Rendering](../../todo/rendering/optimization/compact-zero-readback-rendering-todo.md). |
