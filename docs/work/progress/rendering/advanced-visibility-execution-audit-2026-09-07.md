# Advanced visibility execution audit — 2026-09-07

ARP-A07 source audit of the full mono Vulkan family at `8b104bf7a` plus working
changes. This is an executable-path inventory, not runtime visibility accuracy
or occlusion acceptance. ARP-V55 and the other visibility validation rows remain
open. Layered and stereo requests still fail the one-view admission check.

All Vulkan paths below are under
`XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/`.

| Phase | Actual producer and consumed data | Synchronization and consumer |
|---|---|---|
| Admission | `Frame/Loop/Authority/VulkanFrameLoop.FeatureOperations.cs`, `TryEnqueueAdvancedVisibilityStage`, validates exact reservation, one view, pipeline readiness, render-graph context and frozen canonical publication before acquiring the input lease and enqueuing `AdvancedVisibilityOp`. | `Primary.Preparation` groups the exact family and requires the full phase counts. Missing stages are rejected before native recording. |
| Early visibility | `Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Operations.cs`, `RecordAdvancedVisibilityPreparationPayload`, binds the sealed early compute pipeline/descriptors, pushes view/payload/range offsets and issues `CmdDispatch(ceil(payloadCapacity/256),1,1)`. | The `Advanced.Visibility.EarlyToIndirect` shader-storage barrier precedes the second dispatch through the sealed build-indirect pipeline. It fills GPU-owned argument/count storage. |
| Early raster | The same file's shared visibility raster recorder consumes immutable stable bins, their sealed submission plan, and the exact per-view `IndirectArguments`, `MeshArguments` and `RangeCounts` slices. | `EmitAdvancedVisibilityRasterReadBarrier` runs outside rendering and publishes compute/transfer writes to draw-indirect and graphics shader reads. `Commands/Recording/VulkanCommandRuntime.StableBinSubmissionRecording.cs`, `TryRecordStableBinSubmission`, issues the core/KHR indexed-indirect-count call (20-byte stride) or EXT mesh-task-indirect-count call (12-byte stride). This is the actual Advanced consumer, not merely the generic indirect-draw API. |
| Current-view pyramid | `RecordAdvancedVisibilityLateComputePayload` transitions current depth to compute-read and the pyramid to compute-write, binds the sealed pyramid descriptors/pipeline, and dispatches from the current depth extent. | It transitions the written pyramid to shader-read before the retest dispatch. The descriptor runtime binds the exact source depth/pyramid images at bindings42/43. |
| Deferred retest | The same method binds the sealed late-visibility pipeline, pushes the same immutable view/payload addressing and dispatches over the payload capacity. `Build/CommonAssets/Shaders/Advanced/Preparation/LateVisibility.comp` is the shader-side deferred-candidate producer. | A shader-storage barrier publishes the retest writes. The pyramid is returned to the graph's required General layout at the LateRaster boundary. |
| Late raster | `RecordAdvancedVisibilityLateRasterPayload` requires the LateRaster phase and enters the shared visibility raster recorder against late state. CPU-direct producers are excluded from this GPU-produced candidate stream. | The shared raster-read barrier and `TryRecordStableBinSubmission` consume the late argument/count slices with actual indirect-count draws. CPU code does not read a GPU count to choose work. |

`Resources/Advanced/VulkanAdvancedVisibilityPipelineRuntime.cs` creates the
early/build-indirect and pyramid/late compute pipelines. The corresponding
`VulkanAdvancedVisibilityResourceRuntime` owns frame-slot slices, descriptors,
family seals and late image bindings. Preparation validates frozen program link
generations and target closures before recording; recording rechecks their
identity instead of selecting another strategy on failure.

`AdvancedRenderPipeline.VisibilityBuffer.cs` supplies visibility/depth and
indirect resource declarations in the renderer-neutral pipeline. Those
declarations alone are not the evidence of execution: the dispatch, barrier and
indirect-count calls above complete the producer/consumer trace. Every required
phase has an executable mono path; this audit found no declaration-only stage
that needs an additional implementation row before ARP-V55.

Remaining acceptance includes early/late visibility correctness, current-view
depth convention, newly revealed candidates, independent output histories,
capacity pressure and captures proving which draws each phase emitted. The new
minimal offscreen family and current output-bank lifetime edits await their own
build/runtime validation; this audit does not certify those changes.
