# Physics Chain Critical-Path Mapping (2026-10-12)

Status: Reference. This is the durable record of a read-only code mapping. It is not a plan and not a measurement.

[Investigation](skinned-gpu-chain-benchmark-2026-10-06.md) · [Code items](../../todo/physics/physics-chain-thousands-scale-optimization-todo.md) · [Design](../../design/physics/physics-chain-steady-state-cpu-design.md)

## How to read this record

On 2026-10-12, four read-only agents traced the steady-state per-chain CPU work at commit `beb7d7896`, one agent for each critical-path area. Each agent read the code, counted the work per chain, and proposed changes. The record keeps their results with minimal editing.

- Line numbers refer to `beb7d7896`, before the 2026-10-12 changes. Verify each location before you use it.
- Each "Estimated saving" is the agent's estimate at 2,000 chains, not a measurement. Two estimates were wrong by a large factor: the sampled trace attributed about 3.5 ms to `PollSimulationReceipts`, and stage timers measured 0.07–0.11 ms. Measure first; see the [dispatcher stages](skinned-gpu-chain-benchmark-2026-10-06.md#render-thread-dispatcher-stages).
- The [todo](../../todo/physics/physics-chain-thousands-scale-optimization-todo.md) tracks which proposals are done and which remain. The committed spatial query cache, the publisher unchanged-row gate, and part of the dispatcher proposals were implemented on 2026-10-12; the investigation records their results.

## Areas

- [Render thread: GPU physics-chain dispatcher](#render-thread-gpu-physics-chain-dispatcher)
- [Render thread: Advanced and Vulkan preparation](#render-thread-advanced-and-vulkan-preparation)
- [Collect thread: serial swap](#collect-thread-serial-swap)
- [Collect thread: collection, and update-thread coupling](#collect-thread-collection-and-update-thread-coupling)

## Render thread: GPU physics-chain dispatcher

Scope: Render thread: GPU physics-chain dispatcher (XRWindow.GlobalPreRender -> GPUPhysicsChainDispatcher.ProcessDispatches/ProcessDispatchesCore), including input pages, palette and bounds publication, spatial-bound commit and notifications, simulation receipts, output pages, and the Rendering-side compute source (RuntimePhysicsChainRenderingBridge.Source) that the dispatcher calls for each chain.

### Call chain

All steps run on the render thread. Markers: [R] = one pass over every registered request (2,000). [A] = every active request, which is all 2,000 in the moving-root benchmark. [B] = every renderer palette binding (2,000, one skinned renderer per chain).

1. XRWindow render callback, profile scope "XRWindow.GlobalPreRender" (XREngine.Runtime.Rendering/Rendering/API/XRWindow.cs:2829-2832)
-> RuntimeWorldRenderer.GlobalPreRender (Rendering/RuntimeWorldRenderer.cs:206-211)
-> VisualScene3D.GlobalPreRender (Rendering/VisualScene3D.cs:310-316)
-> IRuntimeRenderDebugDrawingServices.ProcessGpuPhysicsChainDispatches (XREngine.Runtime.Host/RenderingHost/Engine.RuntimeRenderingHostServices.cs:602-603)
-> GPUPhysicsChainDispatcher.ProcessDispatches (Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs:1008-1012). It holds _registrationTransitionSync for the whole pass.
-> ProcessDispatchesCore (:1014-1100).

2. Start of ProcessDispatchesCore:
- PollSimulationReceipts (SimulationReceipts.cs:76-120): [A] entries of each completed receipt.
- ConfirmResidentSpatialState (SpatialBounds.cs:177-213): under _outputPageSync, every published page whose fence has signaled x [B] SpatialCheckpoints. This is 2-3 pages each frame.
- Under _registeredComponentsSync, [R] GPUPhysicsChainRequest.TryAcceptInput (:1028-1052, :3649-3688). Each call takes the InputGate lock, swaps the packets, and runs ApplyAcceptedInput (:3716-3770), which copies about 40 fields.
- [A] Component.AcceptGpuDrivenBoneBindings (:1057-1063). This goes to RuntimePhysicsChainRenderingBridge.Source (RuntimePhysicsChainRenderingBridge.cs:287-303), which calls RefreshGpuDrivenBoneBindings internally.
- Under _registeredComponentsSync, [R] RefreshGpuDrivenBoneBindings again (:1066-1070; bridge :345-378).
- ResolveComputeBackend, program-link gates.

3. ProcessDispatchGroups (:2458-2559):
- TryBeginSimulationReceipt.
- PrepareFrameArenaCapacity (:2754-2785): [A] EnsureRequestArenaSlices, then PreprovisionInputPages.
- Group split: [A].
- TryAcquireInputPage (InputPages.cs:52-83). It resets _dynamicHeaderResourceGeneration and _activeWorkResourceGeneration to -1 (:74-75).
- backend.BeginBatch, then ProcessDispatchGroup (:2661).

4. BuildResidentArenaBuffers (:1456-1516):
- [A] EnsureRequestArenaSlices a second time, plus the per-tree GPUPerTreeParams build.
- BuildActiveWorkRecords (:1518-1555): [A] metadata, and ClassifyKernelBucket with 7 IReadOnlyList indexer calls per chain.
- EnsureSpecializedKernelResources -> BuildDepthTopologyIfNeeded (Kernels.cs:101-145): [A] 5-field hash.
- [A] UploadDirtyRequestRanges (:1876-1933): 7 Matrix4x4-to-affine conversions, rollback copies, ObserveResidentSpatialInputs.
- UploadInputPageColliders (InputPages.cs:370-383): [A] one mapped-memory lease each.
- UploadInputPageTransforms (:422-424): one dense lease.
- UploadDynamicTreeHeaders (:1935-1976) and UploadActiveWorkInputs (:1611-1678): each runs [A] layout check, full write, and [A] layout rebuild.

5. Dispatch and simulation commit:
- [A] ObserveResidentSpatialSolveAttempt.
- DispatchActiveWorkGeneration: 3 fixed dispatches.
- DispatchSpecializedPhysics: 2 or fewer indirect dispatches.
- QueueAsyncReadback (:2871-2876): [A] RequiresGpuReadback.
- CommitBatch.
- [A] CompleteResidentSpatialSolveAttempt, MarkSimulationInputConsumed (InputGate lock), RecordSimulationReceipt (:2507-2512).
- PublishSelectiveReadbacksAfterSimulation -> TrackReadbackWorlds (:3040-3048): [A] HashSet.Add.

6. PublishRegisteredOutputs (:2561-2643):
- Under lock, [R] HasGpuDrivenRenderers plus a Dictionary.Add into _outputPublicationRequestByComponent.
- CollectBatchedGpuDrivenBonePaletteBindings: [R] 2 interface calls (:2276-2287).
- [B] IsCertifiedCompleteBoundsBinding.
- TryBeginOutputPage (OutputPages.cs:361-409) clears the page tables.
- PublishBatchedGpuDrivenBoneMatrices (:2169-2274): [R] collects the bindings again (:2183), [B] signature hash, 1 dispatch.
- CopyPreviousOutputPalette (OutputPages.cs:587-724): 2 x [B], with about 8 hash operations each.
- PublishGpuDrivenBounds (Bounds.cs:87-120):
  - CaptureGpuBoundsSceneRoutes copies the full GPUScene route table (:409).
  - PublishGpuDrivenBoundsWithRoutes (:122-362): [R] dictionary rebuild, then [B] about 25 hash operations, 2-3 locks, envelope and material lookups; allocator EndLayout [B]; slot-metadata zero and fill.
  - PublishGpuBoundsToScene (:483-566): copies the route table a second time (:488), then [B].
- CommitBatch.
- CommitOutputPage (OutputPages.cs:523-585), under _outputPageSync: 2 x [B] dictionary copies, then NotifyCommittedSpatialOutputChanged [B] (SpatialBounds.cs:351-362). That raises XRMeshRenderer.CommittedOutputChanged -> RenderableMesh.RendererCommittedOutputChanged -> VisualScene3D.NotifyCoveredShadowCasterOutputChanged.
- FinalizeGpuBoundsSceneOwnership (Bounds.cs:579-602): [B] GPUScene.SetRendererOwnsGpuCopiedAabb (GPUScene._lock), then MarkGpuCommandBoundsPublished.
- PublishBatchedGpuDrivenBonePaletteSources (:2380-2408): [B] 2 lookups plus XRMeshRenderer.SetGpuDrivenSkinPaletteSource (XRMeshRenderer.cs:4128-4171, _dirtyBoneSyncRoot lock).
- CompleteActiveRequestUpdates (:2645-2652): [R] InputGate lock.

7. finally: SealInputPages, SealSimulationReceipt.

8. Later in the same callback: GlobalPostRender -> ProcessCompletions (:1302-1364). In the steady state this has no per-chain loop.

Total: about 35-40 row passes per frame, about 60-80 hash operations, and 10-11 lock acquisitions per chain. Dispatch and draw counts are already fixed.

### Corrections to the 2026-10-08 inventory

- Inventory (Render: GPU dispatcher): 'one CommittedWorldBoundsChanged event for each renderer'. At HEAD the event is XRMeshRenderer.CommittedOutputChanged(renderer, epoch, published, boundsChanged) (XRMeshRenderer.CommittedBounds.cs:14-25). RenderableMesh sets the reconcile dirty flag only when boundsChanged is true. The event still fires for every renderer on every commit, inside _outputPageSync, because it advances the covered-shadow output revision (RenderableMesh.CommittedBounds.cs:62-72).
- Inventory/design: 'The committed-bounds generation is the global ProducerEpoch'. This is fixed at HEAD. ResolveCommittedSpatialProxy (SpatialBounds.cs:329-344) keeps the prior proxy and its per-renderer version. NotifyCommittedSpatialOutputChanged compares the versions against the prior page.
- Inventory: 'about 25 hash operations ... for each renderer'. At HEAD the output path does about 45-60 hash operations for each renderer per frame. Causes: the proxy change added prior-page RendererStates lookups (TryBuildRendererSpatialBounds:293 and ResolveCommittedSpatialProxy:333, each copying about 250 bytes); there are 5 ConcurrentDictionary GetRenderWorld lookups for each binding; there are 2 full GPUScene route-table copies for each frame (Bounds.cs:409, :488); and CommitOutputPage does 2 full dictionary copies (OutputPages.cs:575-580).
- Inventory: 'about 8 lock acquisitions' for each chain. On Vulkan, the per-request collider write (InputPages.cs:370-383) also opens one mapped-memory lease for each request. A lease takes 2 global VulkanBlockAllocator._lock acquisitions and about 4 allocation-dictionary lookups (VulkanBufferResourceService.cs:340-384, VulkanBlockAllocator.cs:318-366). The total is about 10-11 locks for each chain.
- Inventory: 'Version checks skip copies but still visit each row 2-4 times'. At HEAD the header and active-work version checks never skip anything. TryAcquireInputPage resets _dynamicHeaderResourceGeneration and _activeWorkResourceGeneration to -1 on every acquire (InputPages.cs:74-75), so every frame does a full upload of the headers, instance metadata and tree work. The layout-current loops and the per-request uploaded-version stamps do no useful work. In total, rows are visited about 35-40 times per frame across all passes, not 2-4 times.
- Todo (Physics steady state, first item): 'calls TryAcceptInput and PhysicsChainComponent.AcceptGpuDrivenBoneBindings for every active request'. At HEAD, TryAcceptInput runs for every registered request, because it decides which requests are active (GPUPhysicsChainDispatcher.cs:1032-1049). AcceptGpuDrivenBoneBindings and RefreshGpuDrivenBoneBindings are implemented by RuntimePhysicsChainRenderingBridge.Source in XREngine.Runtime.Rendering, not by PhysicsChainComponent in Core. Source.AcceptGpuDrivenBoneBindings calls RefreshGpuDrivenBoneBindings itself (bridge :302), so active requests refresh twice per frame. The return value of the inner call is lost.
- Inventory: 'Several passes run twice' is confirmed at HEAD, now with specific locations: CollectBatchedGpuDrivenBonePaletteBindings (:2578 and :2183), EnsureRequestArenaSlices (:2762 and :1462), RefreshGpuDrivenBoneBindings (bridge :302 and dispatcher :1069), GPUScene.CaptureRendererCommandIndices (Bounds.cs:409 and :488), and the prior-page RendererStates lookups (3-4 times for each renderer).
- Design and todo say that the collider cost is that each page 'receives all colliders'. At HEAD the main cost is not the bytes. It is the 2,000 separate mapped writes for each frame (one lease per request). The transform catalog already uses one dense write.

### Allocations found on these paths

- The steady-state dispatcher passes have no heap allocation that I could confirm. They use struct enumerators, CollectionsMarshal spans, static lambdas (TryWriteInputPage at InputPages.cs:354-363), ArrayPool rentals (only when readback is requested), and reused collections. Recheck with Report-NewAllocations after the changes.
- RuntimePhysicsChainRenderingBridge.cs:328-332: RebuildPaletteBindings passes a captured closure `model => {...}` (captures this) to IterateComponents, and GetAllRenderersWhere returns an iterator. This runs on the render thread under _registrationTransitionSync, on each binding rebuild (invalidation, bone-generation change, retry). It is structural, not per frame, but a retry loop can repeat it.
- RuntimePhysicsChainRenderingBridge.cs:405-425: TryAddPaletteBinding allocates `new List<GPUDrivenBoneMappingData>`, `new List<uint>`, `indices.ToArray()`, `mappings.ToArray()` and a new PaletteState record for each renderer on each rebuild. This is structural only.
- GPUPhysicsChainDispatcher.cs:1212, 1234, 1254, 1273: TryDispatchDirect, TryDispatchIndirect, TryCopyBuffer and TryCompletePass build the interpolated key string before the ShouldLogEvery throttle. While a backend keeps rejecting work, every rejected call allocates a string each frame, even when the log is suppressed.
- GPUPhysicsChainDispatcher.cs:1039-1041: RecordDispatchFailure with an interpolated string allocates once for each rejected input sequence. A chain that submits a non-affine input every tick allocates once per tick.
- GPUPhysicsChainDispatcher.Bounds.cs:564-576: ReportCompatibilityRouteRejection runs every frame in the steady state, because every covered command is a compatibility rejection. The message string allocates every 5 seconds (throttled). The ConcurrentDictionary lookup in ShouldLogEvery runs every frame.
- GPUPhysicsChainDispatcher.OutputPages.cs:412-427 and :76-92: the stall snapshot and the page-state array allocate only on a stall or a diagnostic read. This is documented.
- To check: PushBufferUpdate -> XRDataBuffer.CommitDirtyBytes runs each frame on non-mapped DynamicDraw buffers: the bounds work items (96 KB) at Bounds.cs:316-317, the slot metadata at :309-311, and the copy items at :531-532. I did not confirm whether the Vulkan upload path allocates staging or op records for each commit. The retained-bounds-rows and compatibility-copy hotspots remove 2 of these 3 uploads in the steady state.

### Hotspots

#### Retained bounds rows for each renderer: rebuild the slot, envelope, material and work item only on change; compute only spatial state each frame

- Thread: render. Estimated saving: 1.2 ms. Risk: medium. Effort: medium. Skips unchanged items today: no.
- Related todo item: Replace the per-request loops in GPUPhysicsChainDispatcher.ProcessDispatchesCore with page-versioned request rows (design rule 5: retained plans). Validation overlaps 'Add material bounds contract tests' and 'Add committed bound version and proxy tests'.
- Locations:
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.Bounds.cs:122-362 (PublishGpuDrivenBoundsWithRoutes)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.Bounds.cs:131,159,260 (allocator BeginLayout/Acquire/EndLayout each frame)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.Bounds.cs:176-196 (blendshape capture, envelope, palette-input bound dictionaries)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.Bounds.cs:197,441-468 (ResolveDrawnMaterialBoundsContract)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.Bounds.cs:293-317 (slot metadata zeroed and refilled; work items uploaded each frame)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/PhysicsChainPaletteAtlasAllocator.cs:20-56`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/PhysicsChainMeshEnvelope.cs:57-96`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/PhysicsChainMaterialBoundsEvaluator.cs (Evaluate: ConditionalWeakTable lookup and lock)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.SpatialBounds.cs:276-344`

**Work per item.** Each binding, each frame:
- allocator Acquire (HashSet.Add + TryGetValue on a combined key) and EndLayout (key enumeration + Contains)
- _committedBoundsSources.TryGetValue, _pendingBoundsSources.Add, _gpuBoundsSlotByRenderer.Add
- CaptureBlendshapeResources (15-field struct)
- PhysicsChainMeshEnvelope.TryGetPaletteInputBounds: ConditionalWeakTable lookup, lock(cache), 2 Matrix4x4? compares, stamp construction, Matches
- _pendingPaletteInputBoundsByRenderer.Add, _lastPublishedPaletteInputBoundsByRenderer.TryGetValue
- route TryGetCommandMaterialSnapshots (dictionary + list), then IsCurrent and Evaluate for each material (ConditionalWeakTable lookup + lock)
- work-item construction, slot-metadata fill through dictionary enumeration
- 2 prior-page RendererStates lookups that copy about 250 bytes each
That is about 15-18 hash operations and 2-3 locks.

**Calls per frame.** 2,000 rows. Plus 1 full work-item upload (96 KB through CommitDirtyBytes) and 1 slot-metadata upload (32 KB) per frame.

**Estimate reasoning.** The removable work per row costs about 650 ns:
- allocator, about 90 ns
- source and slot dictionaries, about 95 ns
- envelope, about 130 ns
- palette-input dictionaries, about 65 ns
- material snapshot and Evaluate, about 165 ns
- work item and metadata, about 25 ns
- blendshape capture, about 20 ns
The cheap per-row witnesses that remain cost about 50 ns, so the net saving is about 600 ns x 2,000 = about 1.2 ms. The spatial and proxy computation and the page RendererStates/SpatialCheckpoints writes stay each frame.

**Proposal.** 1. Add a private struct RetainedBoundsRow (new file GPUPhysicsChainDispatcher.RetainedBoundsRows.cs) with these fields: Request, Renderer, Scene, PaletteBase/Count, BoundsSlot, SlotGeneration, Source (PhysicsChainGpuBoundsSource), LocalBounds (unioned with the last published bound), EnvelopeStamp, MaterialContract, retained material snapshots, BoneBufferGeneration, and HasBlendshapes.
2. Keep List<RetainedBoundsRow> _retainedBoundsRows and a layout key. The key holds the palette binding signature, the route publication content key, and _committedBoundsSources identity.
3. Run the current full path (allocator layout, envelope, material, sources, work items) only when the key changes, a row witness fails, or the row has blendshapes.
4. Row witnesses: renderer.BoneBufferGeneration; mesh reference, GeometryRevision, UtilizedBones reference and BindRootMatrix through EnvelopeStamp.Matches; each retained PhysicsChainDrawMaterialSnapshot.IsCurrent.
5. Each frame, only: TryBuildRendererSpatialBounds, ResolveCommittedSpatialProxy, outputPage.RendererStates.Add, SpatialCheckpoints, and the dense slot-metadata write. Write the metadata from the row array. The epoch word changes per page, so it stays a dense rewrite.
6. Skip _gpuBoundsWorkItemBuffer.WriteDataRaw/CommitDirtyBytes when the rows did not change. The work-item buffer is not paged, and its content is identical.

**Correctness risks.** - A material parameter change must still change the padding in the same frame. IsCurrent on the retained snapshots covers BindingValueVersion; a route change must invalidate the key.
- A change of mesh geometry, bind root or bone layout must still reject or refresh. EnvelopeStamp.Matches covers this.
- Blendshape meshes must keep per-page morph-weight retention, so they keep the full path.
- A slot history reset (RequiresHistoryReset) and a bone-generation change must still advance SlotGeneration.
- No new per-frame allocation: the row list is reused.

**Validation.** Unit tests with a fake backend:
- An unchanged frame makes 0 allocator Acquire calls, 0 envelope calls and 0 work-item uploads.
- A material BindingValueVersion change recomputes the contract in the same frame.
- A GeometryRevision or BoneBufferGeneration change rebuilds the row and advances SlotGeneration.
- A blendshape mesh takes the full path.
Live check: GetGpuBoundsDiagnosticsSnapshot is unchanged, and no MaterialContractRejectionCount drift. A RenderDoc trigger capture shows identical bounds-atlas contents. Compare the GlobalPreRender residual in both mesh modes.

#### Carry the request and resolved GPUScene in each palette binding; remove the per-frame lookup tables and registry lookups

- Thread: render. Estimated saving: 0.7 ms. Risk: low. Effort: small. Skips unchanged items today: no.
- Related todo item: Replace the per-request loops in GPUPhysicsChainDispatcher.ProcessDispatchesCore with page-versioned request rows.
- Locations:
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs:525-534 (GpuDrivenRendererPaletteBinding)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs:2563-2577 (_outputPublicationRequestByComponent rebuilt each frame)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs:2276-2287 (CollectBatchedGpuDrivenBonePaletteBindings)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs:2385-2392 (redundant lock and 2 lookups)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.Bounds.cs:134-153 (_gpuBoundsRequestByComponent rebuilt each frame)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.Bounds.cs:251,394,426,496,591 (5 GetRenderWorld calls per binding)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.OutputPages.cs:631`
  - `XREngine.Runtime.Rendering/Rendering/RuntimeRenderWorldRegistry.cs:28-50 (ConcurrentDictionary lookup)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/RuntimePhysicsChainRenderingBridge.cs:268-282`

**Work per item.** Each binding, each frame: 1 Dictionary.Add plus 3 TryGetValue on _outputPublicationRequestByComponent and _gpuBoundsRequestByComponent; 1 indexer set on _gpuBoundsRequestByComponent; 1 ConcurrentDictionary.TryGetValue on _registeredComponents; 5 GetRenderWorld calls. Each GetRenderWorld call is an interface World read, a ConcurrentDictionary lookup in RuntimeRenderWorldRegistry, and the VisualScene and GPUCommands properties. That is about 11 hash operations and about 10 interface or property calls on scattered objects.

**Calls per frame.** About 22,000 hash operations per frame at 2,000 bindings (1 pass each in PublishRegisteredOutputs, CopyPreviousOutputPalette, PublishGpuDrivenBoundsWithRoutes, CaptureGpuBoundsSceneRoutes, PublishGpuBoundsToScene, FinalizeGpuBoundsSceneOwnership and PublishBatchedGpuDrivenBonePaletteSources).

**Estimate reasoning.** There are 11 hash operations per binding at about 30 ns each with cache misses, plus about 10 indirections at about 3 ns each. That is about 360 ns per binding, or about 0.72 ms for 2,000 bindings. The world-to-scene mapping changes only on world attach or detach, and the request of a binding cannot change during the pass.

**Proposal.** 1. Add GPUPhysicsChainRequest Request and GPUScene? Scene to GpuDrivenRendererPaletteBinding.
2. In CollectBatchedGpuDrivenBonePaletteBindings, record the list count before AppendBatchedGpuDrivenBonePaletteBindings. Then fill Request and Scene into the appended range, or pass the request to the interface method.
3. Resolve Scene once for each request. Cache it on GPUPhysicsChainRequest as CachedWorld, CachedRenderWorldRevision and CachedScene. Re-resolve only when Component.World changes by reference, or when a new static RuntimeRenderWorldRegistry.Revision changes. Attach and Detach increment that revision with Interlocked.
4. Delete _outputPublicationRequestByComponent and _gpuBoundsRequestByComponent. Use binding.Request at OutputPages.cs:631, Bounds.cs:153 and the dispatcher at :2390. Bindings come only from _outputPublicationRequests, which already filters ParticleOffset >= 0 and Particles.Count > 0.
5. Replace the 5 binding.Component.World.GetRenderWorld() sites with binding.Scene.
6. In PublishBatchedGpuDrivenBonePaletteSources, remove the nested lock (_registrationTransitionSync) and the _registeredComponents/ReferenceEquals check. ProcessDispatches already holds _registrationTransitionSync, and Register and Unregister need that lock, so registration cannot change in the pass. Add Debug.Assert(Monitor.IsEntered(_registrationTransitionSync)).

**Correctness risks.** - A stale scene after a render world is detached and attached again for the same context. The registry revision guards this case.
- A world change on the component. The reference compare guards this case.
- Registration change during the pass. This is impossible while the lock is held; the assert documents it.
- No new allocation: both fields are references inside an existing record struct.

**Validation.** Unit tests in XREngine.UnitTests/Rendering:
- A binding carries its request.
- The scene cache is invalidated after RuntimeRenderWorldRegistry.Detach/Attach and after the component World changes.
- The existing PhysicsChainGpuBoundsRouteTests pass.
Live check: alternate A/B 2,000-chain windows in -MeshSharing Shared and Unique. Compare the XRWindow.GlobalPreRender residual and the render-callback phase time. OutputPageDiagnostics and GetCommittedSpatialBoundsStatus must stay Ready with no MaterialRouteChanged or BoundsSourceChanged.

#### Copy the previous palette in bulk when the palette layout and row identities are unchanged

- Thread: render. Estimated saving: 0.6 ms. Risk: medium. Effort: medium. Skips unchanged items today: no.
- Related todo item: Add producer-token history tests for AdvancedGpuDeformationResources.PhysicsHistory and AdvancedPreparationExtractor. Also related: page-versioned request rows.
- Locations:
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.OutputPages.cs:587-724 (CopyPreviousOutputPalette, 2 loops over the bindings)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.OutputPages.cs:936-939 (PaletteSlices and PaletteHistoryResetRenderers are tables for each page)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs:2310-2378 (RebuildBatchedGpuDrivenBonePaletteBuffers: the layout changes only here)`

**Work per item.** Each binding, each frame:
- destination PaletteSlices.TryGetValue + Add
- IsUsableOutputPage(source)
- source.PaletteSlices.TryGetValue
- _outputPublicationRequestByComponent.TryGetValue
- source.RendererStates.TryGetValue (250-byte copy)
- GetSpatialSourceIdentity compare
- 4 mesh property reads, including a Matrix4x4? BindRootMatrix compare
- _copiedPreviousPaletteSlices TryGetValue + Add
- in the second loop, ContainsKey + _seededPreviousPaletteSlices.Add
That is about 8-9 hash operations.

**Calls per frame.** 2 passes over 2,000 bindings, and 1-2 coalesced GPU copies.

**Estimate reasoning.** A binding costs about 350 ns: 8 hash operations at about 30 ns, a 250-byte struct copy, and the mesh property reads. That is about 0.7 ms for 2,000 bindings. A bulk path needs 1 page-level check and 1 copy, but some witnesses stay until the retained rows exist. Net saving is about 0.6 ms. The GPU copy size does not change, because the ranges already coalesce into one contiguous copy.

**Proposal.** 1. Add int PaletteLayoutRevision to PhysicsChainOutputPage, set from a dispatcher _paletteLayoutRevision. Increment the dispatcher revision in RebuildBatchedGpuDrivenBonePaletteBuffers.
2. Add ulong RowIdentityRevision to the page. It comes from a dispatcher counter. The counter increments when a request's ExecutionGeneration, StaticDataVersion or ParticleStateVersion changes (in ApplyAcceptedInput), when a binding's BindingGeneration or RendererBoneBufferGeneration changes (on binding collection), and when a retained bounds row's envelope witness fails.
3. Replace PhysicsChainOutputPage.PaletteSlices with a reference to an immutable PaletteLayoutTable (built once per layout).
4. In CopyPreviousOutputPalette, use the bulk path when all of these are true: the source page is usable, source.PaletteLayoutRevision == current, source.RowIdentityRevision == current, and the source page has no PaletteHistoryResetRenderers. The bulk path issues one TryCopyPreviousPaletteRange over [0, _gpuDrivenPaletteSliceAllocator.HighWater), assigns the layout table, and keeps PaletteHistoryResetRenderers empty. In all other cases, run the existing per-binding path.

**Correctness risks.** Previous-frame history must never come from unrelated content: the todo contract says that incompatible history uses current vertices. The identity revision must cover every input of today's 'matched' predicate, including mesh geometry, bind root and bone layout. Copying free allocator gaps is harmless. A failed or abandoned page must not supply history; the existing IsUsableOutputPage check stays.

**Validation.** Unit tests with a fake backend:
- An unchanged layout gives one copy and no per-binding dictionary work.
- Each of these events alone makes the affected renderer take the reset path: a request reset (ParticleStateVersion), a mesh GeometryRevision change, a bone-generation change, and a layout rebuild.
- A failed source page gives no history.
Live check: motion vectors and temporal AA show no history pops in trigger captures; compare the GlobalPreRender residual.

#### Dense double-buffered request rows written by SubmitData (no per-request TryAcceptInput swap or object reads in the header build)

- Thread: render (and the update thread for writes). Estimated saving: 0.6 ms. Risk: medium. Effort: large. Skips unchanged items today: no.
- Related todo item: Replace the per-request loops in GPUPhysicsChainDispatcher.ProcessDispatchesCore with page-versioned request rows. Also: 'Write all chain roots in one bulk pass' and the 'root-only physics ceiling' prototype.
- Locations:
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs:790-822,883-934 (SubmitData, CompleteSubmittedInput on the update thread)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs:1028-1052,3649-3688,3716-3770 (TryAcceptInput, ApplyAcceptedInput)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs:1456-1487 (headers built from IReadOnlyList and object fields)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs:1876-1933 (UploadDirtyRequestRanges)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs:2507-2512 (post-commit loop with a second InputGate lock)`

**Work per item.** Each request, each frame:
- InputGate lock, packet swap, CopySchedulingFrom, and ApplyAcceptedInput (about 40 field copies + 2 change-detection compares)
- header build through IReadOnlyList interface calls and object fields
- upload plan, 7 rollback list adds, ObserveResidentSpatialInputs and 5 uploaded-version stamps
- a post-commit loop with a second InputGate lock and a receipt-entry add

**Calls per frame.** About 4 passes over 2,000 requests.

**Estimate reasoning.** - TryAcceptInput costs about 110 ns, which is 0.22 ms.
- Header build overhead costs about 50 ns, which is 0.1 ms.
- Upload overhead costs about 80 ns, which is 0.16 ms.
- The post-commit loop costs about 50 ns, which is 0.1 ms.
Together this is about 0.6 ms. The dense transform conversion (7 x 48 B) remains.

**Proposal.** 1. Add PhysicsChainRequestRows, a dispatcher-owned SoA of scalar header fields (DeltaTime, ObjectScale, Weight, Force, Gravity, ObjectMove, LoopCount, TimeVar, the scheduling fields, versions and Sequence). Index it by a stable row index assigned at Register.
2. Keep two banks. SubmitData writes the pending bank row under InputGate and sets a dense dirty flag. ProcessDispatches swaps the banks with one Interlocked operation and walks the dense dirty flags.
3. Array payloads (particles, static data, trees, colliders, bones) keep their versioned packets and are copied only when their versions change.
4. BuildResidentArenaBuffers reads the rows densely. MarkSimulationInputConsumed and AcknowledgeAcceptedInput become row writes.

**Correctness risks.** - Torn reads across fields: a full-row write under InputGate plus the bank swap at the phase boundary prevents them.
- Sequence, retry and acknowledgement semantics: the existing tests for input acceptance and retry must pass.
- Slot reuse after Unregister: a row generation guards it.

**Validation.** Unit tests:
- A rejected affine input is still reported once per sequence.
- RetryAcceptedInput resubmits.
- Scheduling-only updates keep the submitted seed.
- Unregister followed by Register does not reuse stale rows.
Live check: the root-only ceiling flag, then the GlobalPreRender residual in both mesh modes.

#### Write colliders with one dense mapped write per input bank, skipped when the bank already holds the current version

- Thread: render. Estimated saving: 0.5 ms. Risk: low. Effort: small. Skips unchanged items today: no.
- Related todo item: Write colliders and the transform catalog into a mapped input page only when that page's copy is stale (UploadInputPageColliders/UploadInputPageTransforms).
- Locations:
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.InputPages.cs:370-383 (UploadInputPageColliders)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.InputPages.cs:345-368 (TryWriteInputPage)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs:1505 (call site)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs:1876-1933 (UploadDirtyRequestRanges; plan.UploadColliderData is computed but not used)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/PhysicsChainInputPage.cs:7-23`
  - `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkDataBuffer.cs:1016-1027`
  - `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Buffers/VulkanBufferResourceService.cs:340-384`
  - `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Memory/VulkanBlockAllocator.cs:318-366`

**Work per item.** Each request, each frame, makes one separate mapped write. On Vulkan, each write does:
- the GenericRenderObject owner-wrapper scan
- VulkanBufferResourceService.ValidateLeaseOwnership twice (one allocation-dictionary lookup each)
- TryMap and Unmap: 2 more allocation lookups and 2 acquisitions of the global VulkanBlockAllocator._lock with map refcount changes
- 2 Interlocked counters in TryAcquire
- RecordArenaUpload: 3 more Interlocked adds
The copy itself is 128 bytes.

**Calls per frame.** 2,000 mapped-memory leases per frame, which means about 4,000 global allocator-lock acquisitions and about 8,000 dictionary lookups. The transform catalog already uses one lease.

**Estimate reasoning.** One lease costs about 200-300 ns: about 4 dictionary lookups at 25 ns, 2 locks at 20 ns, 5 Interlocked operations at 8 ns, and the call overhead. For 2,000 requests that is 0.4-0.6 ms. One 4,096 x 64 B = 256 KB memcpy costs about 30-50 us. When the bank already holds the current collider version, the copy cost is 0. The global allocator lock is also shared with other threads that map memory, so contention relief can add to the saving.

**Proposal.** 1. Add List<GPUColliderData> _colliderInputCatalog, sized to _colliderArenaHighWater by an EnsureColliderInputCatalogCapacity() method next to EnsureTransformInputCatalogCapacity. Add ulong _colliderCatalogVersion.
2. In UploadDirtyRequestRanges, when plan.UploadColliderData is set and request.Colliders.Count > 0, copy request.AcceptedInput.Colliders into the catalog slice at request.ColliderOffset and increment _colliderCatalogVersion. Also increment it in ApplyPendingArenaLayoutReset and on collider arena growth.
3. Add ulong ColliderCatalogVersion to PhysicsChainInputPage. Reset it to 0 when TryEnsureMappedInputBuffer replaces page.Colliders, and in RetireInputPages and quarantine.
4. Replace UploadInputPageColliders(requests) with: if page.ColliderCatalogVersion != _colliderCatalogVersion, call TryWriteInputPage(_collidersBuffer, CollectionsMarshal.AsSpan(_colliderInputCatalog), 0) and set page.ColliderCatalogVersion = _colliderCatalogVersion.
5. On RollbackTransformCatalogGroup, increment _colliderCatalogVersion. InvalidateUploadedInputs already forces the requests to copy their colliders again.

**Correctness risks.** - A bank whose persistent mapping was recreated must not skip the write. Resetting the version when the buffer is replaced covers this.
- A rolled-back batch can leave new collider data in a bank. The solver did not consume it, and the version increment forces a rewrite.
- Stale data in the slice of a freed request is not referenced; this is the same as today.
- If benchmark colliders follow the moving root, the version changes every tick, and every bank gets one dense 256 KB write per frame. This is still correct and much cheaper.
- No allocation after the catalog grows.

**Validation.** Unit test (todo done condition): change one collider, then rotate through all 8 banks with a fake backend. Each bank gets current data once, and an unchanged population causes no further collider writes (count TryWriteInputPage calls through _inputPageMappedWriteCount).
Live check: InputPageDiagnostics.MappedWriteCount per frame drops from about 2,000+ to 5 or fewer. Compare the GlobalPreRender residual in A/B windows. There must be no InputPageFenceFailed or PlanUnsubmitted change.

#### Run the compatibility bounds-rejection copy and the ownership finalize only when routes or ownership change

- Thread: render (takes GPUScene._lock, which other threads share). Estimated saving: 0.5 ms. Risk: medium. Effort: medium. Skips unchanged items today: no.
- Related todo item: Keep fully covered renderers in a persistent GPU-culled draw set.
- Locations:
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.Bounds.cs:483-566 (PublishGpuBoundsToScene; every copy item has SourceSlot = uint.MaxValue)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.Bounds.cs:579-602 (FinalizeGpuBoundsSceneOwnership)`
  - `Build/CommonAssets/Shaders/Compute/PhysicsChain/PhysicsChainBoundsToScene.comp (SourceSlot 0xffffffff writes constant zero rejection markers)`
  - `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.BoundsHelpers.cs:486-491 (MarkGpuCommandBoundsPublished sets _bvhRefitPending every frame)`
  - `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.BoundsHelpers.cs:539-559 (SetRendererOwnsGpuCopiedAabb takes _lock and does 2 HashSet.Add)`
  - `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.BoundsHelpers.cs:680-685,730-773 (route publication; PublicationGeneration changes at every swap)`

**Work per item.** Each binding, each frame:
- _gpuBoundsSlotByRenderer.ContainsKey
- routes.TryGetCommandIndices (dictionary lookup + list fill)
- copy-item add
- _gpuBoundsCopiedRenderers.Add
- in finalize: HashSet.Contains, GetRenderWorld, and SetRendererOwnsGpuCopiedAabb (GPUScene._lock + 2 HashSet.Add)
Then 1 dispatch writes the same zero markers into the same command slots, and MarkGpuCommandBoundsPublished starts a BVH refit in another phase.

**Calls per frame.** 2,000 rows, 2,000 GPUScene lock acquisitions, 1 compute dispatch, a 16 KB upload, and 1 BVH refit trigger per frame.

**Estimate reasoning.** After the binding-scene hotspot removes the GetRenderWorld calls, about 240 ns per binding remains: 3 hash operations, 1 lock, 2 HashSet.Add and the list work. That is about 0.48 ms for 2,000 bindings, plus the GPU dispatch and upload. A per-frame BVH refit in the swap or collect phase is also avoided, but that saving is not counted here.

**Proposal.** 1. In GPUScene, add long RendererRouteContentRevision. Increment it under _lock when the command membership (_commandIndicesPerMeshCommand), slot assignment or compaction, the renderer captured in _commandIndexLookup, or a DrawMetadata.MaterialID changes. PublishRendererCommandIndices copies it into the published snapshot.
2. In GPUScene, add long GpuCopiedAabbOwnershipRevision. Increment it in PruneGpuAabbOwnersWithoutPublishedCommands when it removes an owner, in SetRendererOwnsGpuAabb/SetRendererOwnsGpuCopiedAabb(false), and when EnsureCommandAabbCapacity or EnsureGpuCullBoundsCapacity replaces or resizes CommandAabbBuffer or CullBoundsBuffer.
3. In the dispatcher, keep for each scene the last published tuple: route content revision, ownership revision, buffer identities, and the binding layout revision.
4. When the tuple is unchanged, skip PublishGpuBoundsToScene, FinalizeGpuBoundsSceneOwnership and MarkGpuCommandBoundsPublished.

**Correctness risks.** The CPU must not overwrite an owned slot after ownership is lost. The ownership revision covers this. Buffer reallocation without a content copy must force a rewrite; buffer identity and capacity are in the key. The compatibility culling must never see a stale CPU bound for a covered renderer. The markers are constant, so the data stays correct as long as ownership and slots are unchanged.

**Validation.** Unit tests:
- An unchanged route and ownership state gives 0 copy dispatches and 0 SetRendererOwnsGpuCopiedAabb calls.
- Each of these events alone gives one rewrite: a command added or removed, slot compaction, a material change, an ownership prune, and a buffer resize.
Live check: GpuBoundsDiagnostics.SceneCopyDispatchCount stops growing in the steady state. The compatibility culling still rejects covered commands in a debug capture.

#### Remove passes that run twice and checks that are always defeated

- Thread: render. Estimated saving: 0.45 ms. Risk: low. Effort: small. Skips unchanged items today: no.
- Related todo item: Replace the per-request loops in GPUPhysicsChainDispatcher.ProcessDispatchesCore with page-versioned request rows (the investigation recorded 'several passes run twice').
- Locations:
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs:2183 (second CollectBatchedGpuDrivenBonePaletteBindings; first at :2578)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs:1462 (second EnsureRequestArenaSlices; first at :2762)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/RuntimePhysicsChainRenderingBridge.cs:302 (inner RefreshGpuDrivenBoneBindings; the dispatcher loop at GPUPhysicsChainDispatcher.cs:1066-1070 calls it again)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.Bounds.cs:409 and :488 (full GPUScene route-table copy twice per frame)`
  - `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GpuSceneRendererCommandIndexSnapshot.cs:32-40 (CopyTo)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.InputPages.cs:74-75 (every acquire forces a full upload)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs:1613-1614,1665-1676,1680-1694 (active-work layout loops)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs:1940-1941,1967-1992 (header layout loops)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs:2586-2588 (IsCertifiedCompleteBoundsBinding loop without break)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.SpatialBounds.cs:177-213 (signaled pages walked again every frame)`

**Work per item.** - Bindings are collected again: 2 interface calls, 1 Volatile renderer read, and a 72-byte struct add per request.
- EnsureRequestArenaSlices runs again: 5 interface Count calls per request.
- RefreshGpuDrivenBoneBindings runs twice per active request. The inner call also discards a true return value.
- The route-table copy runs twice: 2,000 Dictionary.Add and 2,000 tuple adds under GPUScene._lock each time.
- 4 layout or stamp loops (IsDynamicHeaderLayoutCurrent, IsActiveWorkLayoutCurrent, and the 2 rebuild loops) run although TryAcquireInputPage always forces a full upload.
- IsCertifiedCompleteBoundsBinding scans all bindings to compute one bool.
- ConfirmResidentSpatialState walks 1-2 already-confirmed pages x 2,000 checkpoints.

**Calls per frame.** About 10 extra passes over 2,000 rows each frame.

**Estimate reasoning.** Estimated savings per item:
- Second binding collection: 2,000 x 60-90 ns = 0.12-0.18 ms.
- Second arena check: 0.05 ms.
- Duplicate refresh: 0.04 ms.
- Second route copy: 2,000 x about 35 ns = about 0.07 ms.
- Layout loops: 4 x 2,000 x 5-10 ns = 0.04-0.08 ms.
- Certified loop: 0.03 ms.
- Checkpoint re-walks: 0.03-0.06 ms.
The total is about 0.4-0.5 ms.

**Proposal.** (a) PublishBatchedGpuDrivenBoneMatrices: delete the CollectBatchedGpuDrivenBonePaletteBindings call. The only caller, PublishRegisteredOutputs, already collected the bindings from the same _outputPublicationRequests list. One snapshot also makes hasCompleteRenderer match the published list.
(b) BuildResidentArenaBuffers: replace EnsureRequestArenaSlices with a Debug.Assert. PrepareFrameArenaCapacity ran it this frame for a superset of the requests, and accepted inputs cannot change during the pass.
(c) RuntimePhysicsChainRenderingBridge.Source.AcceptGpuDrivenBoneBindings: remove the inner `_ = RefreshGpuDrivenBoneBindings()`. The dispatcher loop then reports a rebuild into _paletteOutputRefreshPending; today the inner call swallows it.
(d) PublishGpuBoundsToScene: reuse the snapshot from CaptureGpuBoundsSceneRoutes. Read only scene.PublicationGeneration under the lock, through a new GPUScene.GetRendererCommandPublicationGeneration(), and capture again only if it differs. The swap cannot run inside the render callback, because EngineTimer serializes it.
(e) Remove _dynamicHeaderLayout, _activeWorkLayout, IsDynamicHeaderLayoutCurrent, IsActiveWorkLayoutCurrent and the per-request Uploaded{DynamicHeader,SchedulingMetadata,TreeWork*,Header*} stamps. Always write the dense ranges, because bank rotation requires it and headers change every frame. Do per-bank versions later with the shared page-version type.
(f) Break the IsCertifiedCompleteBoundsBinding loop at the first true value.
(g) Add bool SpatialCheckpointsConfirmed to PhysicsChainOutputPage. Set it after the first walk of a signaled page and clear it in TryBeginOutputPage.

**Correctness risks.** - (d) depends on the swap/render serialization. The generation check keeps it safe if that ordering changes.
- (c) changes when a rebuild is reported: it becomes earlier and more complete.
- (e) must keep the full-write behavior. Partial writes are wrong with 8 rotating banks.
- (g) must still detect KnownProducerFailure on a later fence failure. Keep the fence-status check before the confirmed flag.

**Validation.** - Unit test with a fake backend: one ProcessDispatches calls AppendBatchedGpuDrivenBonePaletteBindings once for each request.
- A palette-binding invalidation sets _paletteOutputRefreshPending in the same frame.
- Checkpoint confirmation runs once per page.
- Live check: compare the GlobalPreRender residual in A/B windows in both mesh modes. The bounds diagnostics (PublishedCommandCount, LiveSlotCount) do not change.

#### Small input-side steps: dirty-driven bone-binding refresh, cached kernel bucket and topology revision, change-driven readback-world tracking

- Thread: render. Estimated saving: 0.35 ms. Risk: low. Effort: small. Skips unchanged items today: no.
- Related todo item: Replace the per-request loops in GPUPhysicsChainDispatcher.ProcessDispatchesCore with page-versioned request rows.
- Locations:
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs:1057-1070 (Accept and Refresh loops)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/RuntimePhysicsChainRenderingBridge.cs:287-303,345-378,440-441`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs:1541-1553,1557-1577 (ClassifyKernelBucket every frame)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.Kernels.cs:101-118 (5-field topology hash every frame)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs:3040-3048 (TrackReadbackWorlds HashSet.Add for each request)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs:2871-2876 (RequiresGpuReadback interface call for each request)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs:2645-2652 (InputGate lock for each registered request)`

**Work per item.** Per request, each frame:
- 2 interface calls on the scattered Source for the bone bindings, which change only on signature, state-version, mode or invalidation changes
- 7 IReadOnlyList<GPUParticleStaticData> indexer calls that return 64-byte structs, to classify a bucket that depends only on StaticDataVersion
- 5 HashCode.Add calls
- 1 interface call to ReadbackCoordinator plus 1 HashSet.Add
- 1 interface call plus a chain dereference for RequiresGpuReadback
- 1 InputGate lock in CompleteActiveRequestUpdates, also for requests with no new input

**Calls per frame.** About 7 passes over 2,000 requests, plus 14,000 indexer calls.

**Estimate reasoning.** - Bone-binding calls: 2,000 x about 40 ns = 0.08 ms.
- Bucket classification: 14,000 x about 8 ns = 0.11 ms.
- Topology hash: 2,000 x about 25 ns = 0.05 ms.
- Readback-world tracking: 0.06 ms.
- Readback-requirement scan: 0.04 ms.
The total is about 0.35 ms.

**Proposal.** (a) Add AppliedBoneStructureSignature, AppliedParticleStateVersion and AppliedEffectiveGpuDrivenSkinning to GPUPhysicsChainRequest. Call Component.AcceptGpuDrivenBoneBindings only when one of them changes.
(b) Replace the RefreshGpuDrivenBoneBindings sweep over all registered requests with a refresh set. Source sets an Interlocked RefreshRequested flag and enqueues itself into a dispatcher ConcurrentQueue<GPUPhysicsChainRequest> from InvalidateGpuDrivenRenderers, RendererCoverageChanged, and the bone-generation mismatch in AppendBatchedGpuDrivenBonePaletteBindings. Requests with a retry pending stay in a small retry list that is polled each frame.
(c) Cache KernelBucket and KernelBucketStaticVersion on each request.
(d) Replace the BuildDepthTopologyIfNeeded hash with _depthTopologyRevision. Increment it on arena allocation change, StaticDataVersion or tree-count change in ApplyAcceptedInput, and registration change. Fold an active-set identity (count and an ID running hash) into the TryAcceptInput loop that already runs.
(e) Register the readback coordinator with the dispatcher from Source.UpdateReadbackCoordinator when the world changes, and remove TrackReadbackWorlds.
(f) Keep a count of requests that need readback. Update it when GpuSyncToBones changes, and skip the QueueAsyncReadback scan when the count is 0.
(g) Restrict CompleteActiveRequestUpdates to requests that accepted input this frame or have a retry pending. Acknowledging other requests is idempotent.

**Correctness risks.** - A missed invalidation event would leave a stale binding. Keep the bone-generation check in AppendBatchedGpuDrivenBonePaletteBindings as the backstop that enqueues a refresh, and keep the retry polling.
- The topology revision must also cover changes to active-set membership, because depth ranges are built over the dispatch-group order.
- No allocation: ConcurrentQueue segments are reused; size the queue at registration.

**Validation.** Unit tests (todo done condition: input, binding and registration changes still reach the dispatcher with the current latency):
- InvalidateGpuDrivenRenderers rebuilds the bindings in the next ProcessDispatches.
- A renderer bone-generation change rebuilds them.
- A StaticDataVersion change reclassifies the bucket and rebuilds the depth ranges.
- A chain that leaves the active set rebuilds the topology.
Live check: the GlobalPreRender residual.

#### CommitOutputPage: swap the source tables, notify only changed bounds outside _outputPageSync, and advance the covered-shadow revision once per scene

- Thread: render (holds _outputPageSync; the collect-thread TryGetCommittedSpatialBounds and the render-thread TryCaptureRendererOutputState wait on it). Estimated saving: 0.35 ms. Risk: medium. Effort: small. Skips unchanged items today: no.
- Related todo item: Replace the per-request loops in GPUPhysicsChainDispatcher.ProcessDispatchesCore with page-versioned request rows (output publication). Also related: the covered-shadow revision part of 'Give the primary directional shadow tile an explicit native GPU consumer...'.
- Locations:
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.OutputPages.cs:523-585 (dictionary copies at :575-580; notification at :581-582 inside lock(_outputPageSync))`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.SpatialBounds.cs:351-362 (NotifyCommittedSpatialOutputChanged)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.SpatialBounds.cs:329-344 (ResolveCommittedSpatialProxy already knows whether it kept the prior version)`
  - `XREngine.Runtime.Rendering/Rendering/XRMeshRenderer.CommittedBounds.cs:24-25`
  - `XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.CommittedBounds.cs:62-79`
  - `XREngine.Runtime.Rendering/Rendering/VisualScene3D.CanonicalCollection.cs:28-37`

**Work per item.** Each renderer, each commit:
- 2 Dictionary.Add calls (_pendingBoundsSources -> _committedBoundsSources and _pendingPaletteInputBoundsByRenderer -> _lastPublishedPaletteInputBoundsByRenderer)
- enumeration of the about-250-byte RendererStates entries
- a prior-page TryGetValue with a 250-byte copy
- a delegate invoke, then the RenderableMesh handler: CurrentLODRenderer and mask Volatile reads, World?.VisualScene, and an Interlocked.Exchange on one shared scene field
All of this runs while _outputPageSync is held.

**Calls per frame.** 4,000 dictionary adds and 2,000 event invocations per frame. The lock is held for about 0.3-0.45 ms.

**Estimate reasoning.** - The dictionary copies cost 4,000 x about 35 ns = 0.14 ms.
- The notifications cost 2,000 x about 100 ns = 0.2 ms. In the steady state none of them reports a changed bound; the committed-bound investigation counted 0 changed-bound notifications in 1.44 million keeps.
- Collect-thread waits on the shorter lock hold are an additional, unmeasured gain.

**Proposal.** 1. Make _committedBoundsSources/_pendingBoundsSources and _lastPublishedPaletteInputBoundsByRenderer/_pendingPaletteInputBoundsByRenderer non-readonly. In CommitOutputPage, swap the references and do not Clear() and copy. The next build already clears the pending tables (Bounds.cs:135-136), and AbandonOutputPage clears them too.
2. In PublishGpuDrivenBoundsWithRoutes, append a renderer to a reused List<XRMeshRenderer> _changedBoundsRenderers when ResolveCommittedSpatialProxy fits a new proxy or the spatial bound is invalid.
3. In CommitOutputPage, when the binding layout is unchanged and published is true, copy only _changedBoundsRenderers (plus the scene list) into a reused notify list under the lock. After the lock is released, invoke NotifyCommittedOutputChanged for those renderers only.
4. For each scene in _gpuBoundsScenes, call VisualScene3D.NotifyCoveredShadowCasterOutputChanged(epoch, published) once, guarded by a new VisualScene3D._coveredShadowCasterCount. RenderableMesh.SetCoveredShadowCasterLayerMask transitions and UnregisterCommittedBoundsRenderers maintain that count.
5. Withdrawals (published=false, InvalidateFailedPublishedPages, RetireOutputPages) and layout changes keep the full per-renderer notification.

**Correctness risks.** Shadow cascades must never miss a covered-caster output. One scene-level bump for each commit when the caster count is above 0 can only over-invalidate; it cannot under-invalidate. Withdrawn and failed pages must still notify every renderer. Invoking the handlers outside the lock changes their thread context only by ordering; readers already poll the published page under the lock.

**Validation.** Unit tests (the todo 'committed bound version and proxy tests' item):
- An unchanged bound raises no CommittedOutputChanged, but VisualScene3D's covered shadow output revision still advances once for each commit.
- A bound that leaves its proxy notifies only that renderer.
- A withdrawn page notifies all renderers.
Live check: the shadow cascade still updates with moving chains in a trigger capture. Compare the GlobalPreRender residual.

#### Publish palette sources through one shared page view instead of calling SetGpuDrivenSkinPaletteSource for each renderer each frame

- Thread: render. Estimated saving: 0.35 ms. Risk: high. Effort: large. Skips unchanged items today: no.
- Related todo item: Make renderers bind output slices from PhysicsChainOutput directly.
- Locations:
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs:2380-2408 (PublishBatchedGpuDrivenBonePaletteSources)`
  - `XREngine.Runtime.Rendering/Rendering/XRMeshRenderer.cs:4128-4171 (SetGpuDrivenSkinPaletteSource: _dirtyBoneSyncRoot lock; structuralChange is true every frame because the 4-page ring changes the buffers)`
  - `XREngine.Runtime.Rendering/Rendering/XRMeshRenderer.cs:2353-2371 (coverage capture on each call)`
  - `XREngine.Runtime.Rendering/Rendering/XRMeshRenderer.DeformationSources.cs:16-78 (consumers read ActiveSkinPaletteBuffer and SkinnedOutputVersion)`

**Work per item.** Each renderer, each frame:
- lock(_dirtyBoneSyncRoot)
- generation compare and about 12 field compares or writes
- SetField
- CaptureGpuDrivenBoneCoverageLocked (Settings reads, Mesh.UtilizedBones)
- Volatile write
- MarkSkinnedOutputDirty (Interlocked)
- PaletteHistoryResetRenderers.Contains

**Calls per frame.** 2,000 renderer lock acquisitions per frame.

**Estimate reasoning.** A renderer costs about 170 ns: the lock is about 20 ns, the compares and writes about 30 ns, the coverage capture about 20 ns, Interlocked about 10 ns, HashSet.Contains about 30 ns, and cache misses on 2,000 renderers about 50 ns. That is about 0.34 ms. The 2 lookups in this loop are counted in the binding-scene hotspot.

**Proposal.** 1. Add PhysicsChainPalettePageView, a sealed class owned by the dispatcher with Current, Previous, ProducerEpoch, a reset set and a ContentRevision.
2. XRMeshRenderer stores the view once with a renamed SetGpuDrivenSkinPaletteView(owner, view, base, count, generation, complete) call. That call runs only on structural change.
3. ActiveSkinPaletteBuffer and ActivePreviousSkinPaletteBuffer resolve through the view. HasValidGpuDrivenPaletteHistory reads view.IsHistoryValid(this). SkinnedOutputVersion folds in view.ContentRevision, so consumers still see a new version for each output.
4. The dispatcher updates the view once per commit.

**Correctness risks.** - Every palette consumer must resolve through the view: SkinningPrepassDispatcher dirty logic, AdvancedGpuDeformationResources history, the AdvancedPreparationExtractor, and both the OpenGL and Vulkan paths.
- Per-renderer history validity must stay exact.
- An in-flight frame must keep its own page's buffers. Output-page leases already retain them.
- No hidden fallback.

**Validation.** Unit tests:
- The renderer reports the new page buffers after each commit, with no per-frame setter call.
- SkinnedOutputVersion advances for each output.
- A history-reset renderer reports invalid previous history.
Live check: trigger captures in both backends show correct skinning and motion vectors; compare the GlobalPreRender residual.

#### Resident spatial confirmation that survives multi-frame GPU latency (a guard for the 100 Hz steady state)

- Thread: render. Estimated saving: 0 ms. Risk: medium. Effort: medium. Skips unchanged items today: no.
- Related todo item: Add committed spatial-snapshot containment tests for GPUPhysicsChainDispatcher.SpatialBounds and PhysicsChainMeshEnvelope (held or failed production). Prerequisite for the 100 Hz validation.
- Locations:
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.SpatialBounds.cs:215-245 (ObserveResidentSpatialInputs increments ResidentSpatialRevision on every transform upload)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.SpatialBounds.cs:247-266 (ObserveResidentSpatialSolveAttempt increments it again and unions RootReachBounds)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.SimulationReceipts.cs:103-116 (applies the tight state only if the revision is unchanged)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.SpatialBounds.cs:195-208 (same rule for page checkpoints)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/PhysicsChainCommittedSpatialProxy.cs:33-36`

**Work per item.** Each moving chain increments ResidentSpatialRevision twice per frame. A completed receipt or page checkpoint can tighten ResidentParticleBounds only if its fence signals before the next ProcessDispatches. Otherwise the bound stays a running union of every root-reach bound.

**Calls per frame.** [A] each frame. The tightening succeeds today only because the GPU (about 8.8 ms) finishes within one 45 ms frame.

**Estimate reasoning.** This change saves no time at today's frame rate. It prevents a regression at the target rate. At 100 Hz with 1 or more frames of fence latency, the exact bound of every moving chain grows without limit. PhysicsChainCommittedSpatialProxy.CanKeep then fails again and again, and the per-renderer version changes. That sends changed-bound notifications, and the RenderableMesh reconcile slow path and CPU tree moves come back. The committed-proxy change removed those costs, which were about 12-16% of collect and swap. Culling precision also falls.

**Proposal.** 1. Keep a ring on each GPUPhysicsChainRequest with capacity 8, the InputPageCount. Each entry is (ulong Revision, AABB RootReach, float BasisStretch) for a solve submitted since the last confirmed checkpoint. Also keep ulong ResidentSeedRevision, set on each particle upload.
2. When a receipt or checkpoint for revision R completes, and no seed was written after R, set ResidentParticleBounds = Tight(R) ∪ RootReach(every ring entry with revision > R), and set ResidentBasisStretch the same way.
3. Drop the entries with revision R or lower. Apply the same rule in PollSimulationReceipts and ConfirmResidentSpatialState.
4. Store the ring inline as a fixed-size struct, so it does not allocate.

**Correctness risks.** The bound must contain every GPU state that is possible for in-flight solves. The union with the reach of all newer submitted solves keeps this. A reset or seed after R must invalidate the tight state. A failed fence must keep the union; RetryAcceptedInput already does this.

**Validation.** Unit test with a fake fence that signals 2-3 frames late while the root moves:
- The exact bound stays within the tight bound united with the in-flight reach.
- The bound does not grow over 1,000 frames.
- The committed version stays stable.
Live check: at the higher frame rate, the committed-bound keep counter (a temporary probe) shows no proxy refits.

### Notes

1. Scale. The XRWindow.GlobalPreRender residual is about 10.5 ms at 2,000 chains, which is about 5.3 us per chain. My per-pass estimates add up to about 7-10 ms. The input side (accept, arena, headers, per-request uploads, colliders) is about 2.4-3.4 ms. The output side (bindings, previous palette, bounds rows, scene copy, commit/notify, finalize, palette sources) is about 4.5-6.5 ms. The largest single item is PublishGpuDrivenBoundsWithRoutes, at about 2-3 ms.

2. Combined gain. All hotspots above together remove about 5.5 ms of render-thread CPU work. That leaves about 2-2.5 us per chain in this area, which is still about the full 2.5 us budget for the whole critical path. To reach the design target, each frame must do only dense per-row work. Two todo items do that: 'renderers bind output slices directly', and per-page dense spatial rows written by the GPU or by one dense pass.

3. Critical-path caveat. Check this before you claim frame-rate gains. In the physics-frozen timeline, EngineTimer.DispatchRender stayed at 30.19 ms (30.24 ms with physics running). The GlobalPreRender residual dropped to 0.35 ms, but the Advanced.VisibilityPreparation residual rose from 6.18 to 19.47 ms. I found no worker joins or waits in Rendering/Preparation, so the absorbed time is probably lock contention or a fence or frame-resource wait elsewhere. If so, savings in this area reduce render-thread CPU time but do not reduce the render callback until that wait is found. Measure the render callback and VisibilityPreparation after the first 3 hotspots (bindings, colliders, deduplication).

4. Locks shared with other threads:
- ProcessDispatches holds _registrationTransitionSync for the whole pass (about 10 ms). A Register or Unregister call on another thread blocks for up to one render pass. This is structural only.
- CommitOutputPage holds _outputPageSync for about 0.3-0.45 ms. The collect thread takes the same lock about 16,000 times per frame (TryGetCommittedSpatialBounds takes it twice, and runs about 4 times per mesh). Render-thread readers also take it: AdvancedPreparationExtractor.TryCaptureRendererOutputState, and Vulkan record validation through TryValidateOutputPage.
- FinalizeGpuBoundsSceneOwnership takes GPUScene._lock 2,000 times per frame.
- The collider leases take the global VulkanBlockAllocator._lock 4,000 times per frame.

5. Still open at HEAD:
- The todo item 'Count every output-page producer failure' still applies. SpatialBounds.cs:188-191 and SimulationReceipts.cs:93-98 set KnownProducerFailure without ++_outputPageFailureCount or RecordFenceFailureObservation. InvalidateFailedPublishedPages counts only when KnownProducerFailure was not already set.
- The todo item 'per-request loops' is confirmed. No request, binding or page row is skipped when it is unchanged, except the particle-state and static-template uploads, which the version check skips.

6. Out of scope, noted for the swap/collect owners:
- GPUScene.PublishRendererCommandIndices rebuilds the whole route table at every swap (GPUScene.BoundsHelpers.cs:730-758).
- MarkGpuCommandBoundsPublished triggers a BVH refit every frame (:486-491).
- RuntimePhysicsChainRenderingBridge._sources is a plain Dictionary. The update thread reads it in Execute; check its threading.

7. Dispatch and draw count is already fixed: 3 active-work dispatches, 2 or fewer indirect solver dispatches, 1 palette dispatch, 1 bounds dispatch, 1 compatibility copy dispatch, plus barriers. No per-chain dispatch remains in the steady state.

8. Rule check. All proposals avoid hidden CPU fallback and keep strict rejection paths. None adds per-frame allocations. Each one can be implemented and unit-tested independently, except:
- The retained-bounds-rows witnesses help the bulk previous-palette copy.
- The binding-scene change makes the compatibility-copy gating smaller.

## Render thread: Advanced and Vulkan preparation

Scope: Render thread: Advanced visibility preparation (AdvancedPreparationExtractor, AdvancedSharedPreparationService, AdvancedGpuDeformationResources, visibility input variant and indexed instance groups, VulkanAdvancedVisibilityInputStorage) and Vulkan primary recording preparation and recording of the canonical visibility stable-bin stream (VulkanPreparedStableBinStream, VulkanVisibilityGeometryRecordClosure, Primary.Operations raster lane, directional shadow lane)

### Call chain

RENDER THREAD. EngineTimer.DispatchRenderFrame (XREngine.Runtime.Host/Core/Time/EngineTimer.FrameDispatch.cs:111-137, RenderFrame?.Invoke) -> XRWindow render callback (XREngine.Runtime.Rendering/Rendering/API/XRWindow.cs:2702) -> GlobalPreRender (XRWindow.cs:2829-2832; GPUPhysicsChainDispatcher.ProcessDispatches, which belongs to the physics area) -> RenderViewportsCallback/RenderWindowViewports (XRWindow.cs:2845, 2874) -> pipeline commands -> VPRC_AdvancedRenderStage.TryBuildStageRequest (VPRC_AdvancedRenderStage.cs:205; Acquire at :241-247) -> AdvancedSharedPreparationService.Acquire (AdvancedSharedPreparationService.cs:103; lock _sync :110; the first consumer of each FrameId runs Build :166 and then GpuDeformation.TryExecute :174; later stages in the same frame hit the cache) -> AdvancedPreparationExtractor.Build (AdvancedPreparationExtractor.cs:212). Build makes these passes: TryCaptureGpuBoundsPublication (AdvancedPreparationExtractor.GpuBounds.cs:15, per draw); TryPrepareCanonicalDeformationMeshes (:656-688, per draw, calls TryPrepareMesh); the ExtractCommand loop (:322-331 -> :690-880, per draw) -> TryAddDeformation (:904-1084) -> {TryPrepareMesh (AdvancedGpuDeformationResources.MeshPreparation.cs:79), GPUPhysicsChainDispatcher.TryCaptureRendererOutputState (GPUPhysicsChainDispatcher.OutputPages.cs:186, second call), TryGetOrAddPose (AdvancedGpuDeformationResources.cs:496 -> XRMeshRenderer.TryPrepareDeformationInputs XRMeshRenderer.DeformationSources.cs:16 -> AddExternalPaletteCopy ExternalPalettes.cs:48), AdvancedDeformedVertexArena.TryAcquireSlice (:110), GetOrCreateAnimationSchedule (:1213 -> AdvancedAnimationScheduler.Schedule), AdvancedDeformationJobStream.TryAdd (:60)}; FinalizeJobs (JobStream:106); ApplyDeformationAdmissionVerdicts (:1092, per draw); AdvancedDeformationDispatchPlanner.Build (:43, 3 passes per job); GpuDeformation.Publish (:654); AdvancedIndirectRangePlanner.Build (:71, 2 passes per payload); AddVisibilityPlansCore -> ClassifyCandidatesForViews (:555, per candidate per view) + AdvancedVisibilityPlanner.BuildPlan (:68, per candidate per view). Then TryExecute (AdvancedGpuDeformationResources.cs:725) -> TryCopyExternalPalettes (ExternalPalettes.cs:74; one renderer.TryEnqueueGpuBufferCopy per palette run, which leads to VulkanCommandRuntime.TryEnqueueBufferCopy and a pooled BufferCopyOp). Stage enqueue: VulkanFrameLoop.TryEnqueueAdvancedVisibilityStage (VulkanFrameLoop.FeatureOperations.cs:67; new AdvancedVisibilityOp :198) -> VulkanFrameOperationQueue.TryAcquireAdvancedVisibilityInput (:297; directional shadow requests never share a lease :306-308) -> VulkanAdvancedVisibilityInputStorage.TryCaptureAtAuthoring (:149) -> AdvancedSharedPreparationService.TryCopyVisibilityInputs (:216; per-draw slice loop :297-312) -> AdvancedVisibilityInputVariant.TryBuild (:17) -> AdvancedIndirectRangePlanner.Build + AdvancedIndexedInstanceGroupPlanner.Build (:48-176). This capture runs once for the main family and once more for each deferred directional cascade group (VPRC_AdvancedRenderStage.ExecuteDirectionalShadowRaster :480-512). VULKAN RECORDING (render thread, inside XRWindow.Renderer.RenderWindow XRWindow.cs:2949): primary recording preparation (VulkanRenderer.CommandBufferRecording.Primary.Preparation.cs:659-1216; target validation :830 -> TryValidateAdvancedVisibilityTarget :1963) -> TrySealAdvancedVisibilityBins (:1820-1961) -> VulkanPreparedStableBinStream: TryBuildVisibilityGeometryStream (:250; per payload :350-644, with per-payload closure.TryValidate :544 and TryAppend :618), Freeze/SortRecordsForFreeze (:760/:782), TryResolveManifests (:1030, per record), TrySealSubmissionPlans (:1366 per record, then :1114 per header); TryRetainTemplatesForFramePlan (:849, per record); TryPrepareVisibilityRasterPipelines (:1625, per-record member checks :1753-1821); TryBuildVisibilityRasterPayloads (:1523, per record); VulkanAdvancedVisibilityResourceRuntime.TryPrepare (:182, dense set-1 uploads :413-448). Recording: Primary.Operations.cs raster lane (:870-1217) runs once for early raster and once for late raster. For each view (:930) and each header (:938), it validates every record (:1054-1076) through VulkanVisibilityGeometryRecordClosure.TryValidate (VulkanVisibilityAtlasRecordClosure.cs:35), then VulkanVisibilityPreparedVertexSource.TryValidate (:39), then VulkanResourceRuntime.TryValidateNativeBufferRange (VulkanResourceRuntime.NativeBufferRange.cs:86: wrapper lookup and lifetime-tracker lock). After that it issues one CmdDrawIndexedIndirectCount per bin (VulkanCommandRuntime.StableBinSubmissionRecording.cs:13). The strict directional shadow GPU lane records per cascade and per header only (Primary.DirectionalShadowGpu.cs:145-228).

### Corrections to the 2026-10-08 inventory

- 'Render: Advanced preparation: ExtractCommand does about 150-250 operations, 2 locks, and 8 hash operations for each draw.' At HEAD on Vulkan, a covered skinned draw takes at least 4 monitor locks: GPUPhysicsChainDispatcher._outputPageSync twice (TryCaptureRendererOutputState at AdvancedPreparationExtractor.cs:733 and :952), the ContractCache lock in PhysicsChainMaterialBoundsEvaluator.Evaluate:64, and XRMeshRenderer._resourcePublicationGate in TryGetOrAddPose:510. It also makes about 15-20 hash/probe operations: 2 renderer-keyed RendererStates lookups; 2-3 ConditionalWeakTable lookups (material contract, uber vertex source); and _meshSlices, _meshAliasWitnesses (ConditionalWeakTable), and InputWitnesses twice each, because TryPrepareMesh runs in both the TryPrepareCanonicalDeformationMeshes pre-pass and TryAddDeformation. Add the _poseEntries tuple lookup and insert, the schedule and feedback probes, 2 arena owner probes, the job dedupe probe, and the visibility-planner draw-slot probe.
- The inventory describes Advanced preparation as one ExtractCommand loop. At HEAD, Build makes three per-draw passes (TryCaptureGpuBoundsPublication, TryPrepareCanonicalDeformationMeshes, ExtractCommand) plus ApplyDeformationAdmissionVerdicts, a 2-pass range plan, per-view classification, and BuildPlan. Every authoring capture (VulkanAdvancedVisibilityInputStorage.TryCaptureAtAuthoring) then copies all columns and rebuilds the range planner and the indexed-instance-group planner. This happens once for the main family and once more for each deferred directional shadow cascade group, because shadow requests never share a lease (VulkanFrameOperationQueue.cs:306-308).
- 'Render: Vulkan recording: one CmdDrawIndexedIndirectCount for each bin; record validation for each view, bin, and phase.' This is still true but no longer the main per-chain cost. The primary recording preparation rebuilds the canonical visibility stable-bin stream every frame (TryBuildVisibilityGeometryStream, Freeze/sort, TryResolveManifests, TrySealSubmissionPlans, TryRetainTemplatesForFramePlan, TryPrepareVisibilityRasterPipelines, TryBuildVisibilityRasterPayloads). Each pass visits every record, and the records are about 2 KB structs copied by value in most passes. Each per-record recording validation also does a native lifetime check (wrapper lookup plus a lifetime-tracker lock) on a vertex source that is identical for every record in the bin.
- On Vulkan, UsesPackedAggregateInputs is false (only WebGPU implements IAdvancedAggregateDeformationBackendCapability). SetJobControls and the canonical instance-bound lookups in ExtractCommand therefore do not run on the benchmark path. Covered palettes go through AddExternalPaletteCopy -> TryEnqueueGpuBufferCopy (one ordered BufferCopyOp per non-contiguous palette run), a per-chain path that the inventory does not list.

### Allocations found on these paths

- VulkanRenderer.CommandBufferRecording.Primary.Preparation.cs:1978-2005 (TryValidateAdvancedVisibilityTarget): `EFrameBufferAttachment[] expected = [...]` and `string[] names = [...]` collection expressions allocate two arrays per call. It is called for the visibility raster, late raster, and MSAA resolve stages each frame, about 4-6 small arrays per frame. Fix: static readonly expected arrays, and compare names through a switch rather than a string[].
- VulkanFrameLoop.FeatureOperations.cs:198: `new AdvancedVisibilityOp(...)` (a sealed record class with a large request struct) per Advanced stage enqueue per frame (about 8-10, plus copy-constructor clones when sealed authoring copies are created). Fixed count, but per-frame heap allocation; candidates for pooling like BufferCopyOp.Rent.
- VulkanPreparedStableBinStream.cs:1837-1846 -> VulkanResidentDrawTemplateNativeState non-inline ctor (`vertexBuffers.ToArray()`, `vertexBindings.ToArray()`): runs per non-canonical (resident-template) header in each frame, because headers are rebuilt each frame and reusePreparedRaster is never true for a fresh header. Not hit by canonical chain bins, but it is a per-frame allocation when the visibility family contains resident-template bins.
- AdvancedSharedPreparationService/Extractor/GpuDeformation steady state: no per-frame allocations found. Arrays grow only on a new high-water mark (EnsureCapacity, EnsureDynamicPoseCapacity, AddExternalPaletteCopy Array.Resize). _poseEntries is cleared and refilled without growth after warm-up. The AdvancedIndexedInstanceGroupPlanner._contentCache inserts only new content. PhysicsChainMaterialBoundsEvaluator allocates ContractCache only on the first sight of a material. BufferCopyOp is pooled (BufferCopyOp.Rent).
- GPUPhysicsChainDispatcher.RecordOutputPageBusyAttempt (OutputPages.cs:412-427) and GetOutputPageStatesSnapshot (:76-92) allocate only on the stall/diagnostics path (not steady state).

### Hotspots

#### Reuse the sealed canonical visibility stream across frames and slim its records

- Thread: render (Vulkan primary recording preparation). Estimated saving: 2.5 ms. Risk: medium. Effort: large. Skips unchanged items today: no.
- Related todo item: Validate sealed visibility records only when they change; Separate stable Advanced preparation templates from frame bindings
- Locations:
  - `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Preparation.cs:1024-1215 and :1820-1961`
  - `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/StableBins/VulkanPreparedStableBinStream.cs:250-652, 829-841 (ThawForReuse each frame), 1030-1856`
  - `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/StableBins/VulkanPreparedStableBinRecord.cs`

**Work per item.** Every frame, the family stream is thawed and rebuilt from 2,000 payloads: about 2 KB records, sort, manifest views, seal, template retention, raster member validation, and raster payloads. Almost all per-record content is identical frame to frame. Only the deformation overlay temporal flags and the frame-slot vertex source vary, and the vertex source is the same for every record in a bin.

**Calls per frame.** 1 family x 2,000 records x about 7 passes

**Estimate reasoning.** After the ref-readonly and hoisting fixes, the remaining stream build is still about 1.5-2 µs per record (about 3-4 ms). Reuse leaves only a structural signature pass (about 10 ns per row) and the overlay rebuild (dense), saving about 2.5-3 ms.

**Proposal.** (a) Move the per-bin-invariant data out of VulkanPreparedStableBinRecord: the full VisibilityNativeState (except the per-record ElementCount, which is already in VisibilityDirectDraw), the shared VertexSource, IndexSlice, and SceneNativeGeneration in the closure (keep Geometry, the VertexBase/IndexBase/counts, and the geometry ranges). Records drop to about 150 B. (b) Have FramePlan's family stream keep a seal signature: the structural payload signature, producers, ranges, scene NativeGeneration, the per-bin vertex/index source identity (native range generation), pass index, view mask, FrameOpContext compatibility, and target closure plus program link generations. When it matches, skip ThawForReuse/TryBuildVisibilityGeometryStream/TryResolveManifests/TrySealSubmissionPlans and rebuild only the deformation overlay and VisibilityGeometrySources. Re-run the per-header native validation.

**Correctness risks.** Frame plans rotate per frame slot, so the vertex source (deformation output frame-slot buffer) differs between slots. Keep it per-bin and re-validated, not in the record key, or the reuse never hits. Template leases (TryRetainTemplatesForFramePlan) must still be taken per frame plan acceptance. Any producer, range, or membership change must force a reseal. CopyFrom paths (FramePlan.cs:265) must copy the slim layout.

**Validation.** New stream tests: reuse on equal signatures; reseal on payload, producer, or range change, on scene generation change, and on program relink; per-header source validation still rejects a superseded buffer. Live: S13a family step probes near zero on steady frames; RenderDoc capture equality of draws and arguments; both mesh modes at 2,000.

#### Retained extractor row templates keyed by publisher row signatures

- Thread: render (Advanced.VisibilityPreparation). Estimated saving: 2 ms. Risk: medium. Effort: large. Skips unchanged items today: no.
- Related todo item: Separate stable Advanced preparation templates from frame bindings
- Locations:
  - `XREngine.Runtime.Rendering/Rendering/Preparation/Advanced/AdvancedPreparationExtractor.cs:690-1084 (ExtractCommand/TryAddDeformation; commandChanged is constant false at :717)`
  - `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/Advanced/AdvancedSceneSubmissionPublicationSnapshot.cs:44-53 (row SourceVersion=ContentSignature, MeshVersion=StructuralSignature)`
  - `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/Advanced/AdvancedGpuScenePublisher.cs:374-386, 1460-1526`

**Work per item.** After the small fixes, each row still recomputes per frame the canonical geometry lookup, geometry offsets, meshlet residency, topology generation, mesh slice, feature flags, the material verdict, and the constant payload and candidate fields. Its total is about 150-250 operations (inventory) or about 1.2-2.5 µs measured-equivalent.

**Calls per frame.** 2,000

**Estimate reasoning.** This takes a row from about 1.2-1.8 µs (after the small fixes) to about 0.2-0.3 µs: a witness compare, a fresh output-state read, an arena acquire, a pose copy, a job add, and payload finalize. At 2,000 that saves about 2 ms.

**Proposal.** Add AdvancedPreparedRowTemplate[] _rowTemplates (indexed by command index) holding the witness (Draw, Geometry, Material, Deformation handles; submission Flags/Pass/StateClass/InstanceCount; Source, Renderer, and Mesh references; row SourceVersion and MeshVersion; GpuBoundsSource slot, slot generation, and bone-buffer generation; MaterialSnapshot versions; mesh skinning and blendshape state references; StaticInputsRevision and _meshPreparationPolicy; publication DatabaseEpoch, TopologyGeneration, and Geometry.Generations) and the cached immutable outputs (offsets, meshlet range, gpuMeshSlice, topologyGeneration, features without VelocityInvalid, candidate flags and bind bounds, the payload constant part). When the witness matches, skip TryPrepareMesh, the geometry lookup, and feature derivation. Keep these per frame: the output-state read, history compatibility, TryAcquireSlice, the pose copy record, TryAdd, temporal reason, and VelocityInvalid. A witness mismatch takes today's exact path and rewrites the template. Set commandChanged from the witness mismatch.

**Correctness risks.** The witness must cover every input of the cached outputs. Risky inputs are the mesh skinning-state and blendshape-buffer revisions (not in StructuralSignature) and AllowSkinning/AllowBlendshapes. Pose, bounds, root motion, history, and output-page leases must stay out of the template; a held page must never make a row reusable without the per-row output-state check. Compaction or reordering changes the command index, so key on the Draw handle and verify it.

**Validation.** New tests: template reuse on equal frames; invalidation on mesh geometry revision, skinning-buffer rebuild, material layout change, bounds slot reuse, publication compaction, and policy switch. Producer-token history tests (todo) for frame gaps and page replacement. Live: extraction ticks per row; both mesh modes; the frozen-scene ceiling check.

#### Access frozen stable-bin records by reference and remove redundant per-record key re-checks

- Thread: render (Vulkan primary recording preparation and recording). Estimated saving: 1.5 ms. Risk: low. Effort: small. Skips unchanged items today: no.
- Related todo item: Validate sealed visibility records only when they change; Separate stable Advanced preparation templates from frame bindings (VulkanPreparedStableBinStream)
- Locations:
  - `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/StableBins/VulkanPreparedStableBinStream.cs:1044-1056 (TryResolveManifests by-value record copies and key copy)`
  - `VulkanPreparedStableBinStream.cs:1380-1447 (TrySealSubmissionPlans template overload, record copy at :1382)`
  - `VulkanPreparedStableBinStream.cs:868-897 (TryRetainTemplatesForFramePlan, copy at :870)`
  - `VulkanPreparedStableBinStream.cs:1547-1596 and 1604-1612 (TryBuildVisibilityRasterPayloads and ResolveVisibilityNativeState, which copies the whole record again and returns a ~600 B native state by value)`
  - `VulkanPreparedStableBinStream.cs:1753-1798 (TryPrepareVisibilityRasterPipelines: record copy, payload copy, ResolveVisibilityNativeState copy, record.Key != header.Key at :1776, and new VulkanRenderBinNativeCompatibility(...).Equals at :1793)`
  - `VulkanRenderer.CommandBufferRecording.Primary.Operations.cs:1050-1061 (per-record closure copy, done twice per frame)`

**Work per item.** VulkanPreparedStableBinRecord is a readonly record struct of about 2 KB (VulkanRenderBinKey with NativeCompatibility, ContextCompatibility, and VisibilityBinding holding two VulkanFrameDataSlice and a prepared vertex source; VisibilityNativeState with three primitives and a full PendingMeshDraw holding two Matrix4x4 values; VisibilityGeometryClosure with a full AdvancedGeometryRecord and two slices), with about 20 GC references. Each record is copied by value to a stack local about 7 times per frame (manifests, seal, retain, raster pipelines x2, raster payloads x2), and the ~660 B closure is copied twice more in recording. There are also two large key equality compares per record (the run grouping in TryResolveManifests, then the redundant record.Key != header.Key) and one NativeCompatibility construction and compare. That is about 15-17 KB of struct traffic per record per frame, about 34 MB per frame at 2,000 records, over a 4 MB working set that does not fit in L2.

**Calls per frame.** 1 stream per visibility family per frame x 2,000 records x about 7 by-value passes, plus 2 recording phases

**Estimate reasoning.** About 30 MB of avoidable stack copying per frame at a realistic 10-20 GB/s with L3-resident data costs about 1.5-3 ms. Some copies may survive (JIT spills), so this uses the conservative half. The removed redundant key compare (about 80 ns x 2,000) adds about 0.15 ms.

**Proposal.** Mechanical change with no semantic change. (1) Replace `VulkanPreparedStableBinRecord record = _records[i]` with `ref readonly VulkanPreparedStableBinRecord record = ref _records[i]` in TryResolveManifests, TrySealSubmissionPlans (record writes stay as `_records[i] = record with {...}` only where it mutates), TryRetainTemplatesForFramePlan, TryPrepareVisibilityRasterPipelines, and TryBuildVisibilityRasterPayloads. (2) Change ResolveVisibilityNativeState to return `ref readonly VulkanResidentDrawTemplateNativeState`: return the retained template's field by ref, or `ref _records[i].VisibilityNativeState`. (3) In TryResolveManifests, compare `ref readonly` keys (`_records[end].Key.Equals(in key)`), adding an `in` overload of the key equality if needed. (4) Delete the `record.Key != header.Key` re-check in TryPrepareVisibilityRasterPipelines, because headers are built from exact key runs in TryResolveManifests and the stream is frozen. (5) In Primary.Operations.cs:1060, use `ref readonly var recordClosure = ref records[recordIndex].VisibilityGeometryClosure`, and do the same for records[0] at :1050. Change ReadOnlySpan element reads to `ref readonly` locals.

**Correctness risks.** None if no write happens between the reference and its use. The stream is frozen (_frozen) before every one of these passes, and record mutation (TrySealSubmissionPlans :1435) writes a whole new value, which keeps readonly semantics. The removed key re-check is implied by header construction. Keep a debug assertion under VulkanFeatureProfile DevParity/Diagnostics.

**Validation.** Build plus existing rendering tests (XREngine.UnitTests Rendering filter). Live: Measure-PhysicsChainScale.ps1 -ChainCount 2000 in -MeshSharing Shared and Unique; compare the S13a advanced-family step probes (BinGeometryStream, BinManifests, BinSubmissionPlans, RasterPipelines, RasterHeaderValidation, RasterRealization) and the render-callback phaseTimings before and after; capture one frame to confirm the draw count and image are unchanged.

#### Validate the shared visibility vertex source once per bin instead of once per record and phase

- Thread: render (Vulkan recording, early and late raster ops). Estimated saving: 1 ms. Risk: low. Effort: small. Skips unchanged items today: no.
- Related todo item: Validate sealed visibility records only when they change
- Locations:
  - `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Operations.cs:1054-1076`
  - `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/StableBins/VulkanVisibilityAtlasRecordClosure.cs:35-78 (TryValidate)`
  - `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/StableBins/VulkanVisibilityPreparedVertexSource.cs:39-60`
  - `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Authority/VulkanResourceRuntime.NativeBufferRange.cs:86-107`
  - `VulkanPreparedStableBinStream.cs:544-552 (seal-time per-payload closure.TryValidate)`

**Work per item.** Per record, per view, per raster phase: copy the ~660 B closure, PreparedVertexSource.Matches and IndexSlice.Matches against records[0] (about 25 field compares), then IsValid (about 15 compares and RangeFits), the scene generation and slice match, and the range checks. For skinned records this is followed by TryValidateNativeBufferRange: WrapperLookup.GetOrCreate (EnsureOwnerFirstConstructionCompleted, IsApiWrapperPublicationReady, BackendObjects.Get lookup), about 4 property reads, a VulkanFrameLockScope Monitor on the lifetime tracker, and TryResolvePublishedResourceSlotNoLock. Every record in a canonical visibility bin uses the same deformation-output native range (TryPrepareVisibilityRasterPipelines already proved each member matches header.Key.VisibilityBinding.VertexSource), so the result is the same 2,000 times. The seal pass repeats the same native check once per payload.

**Calls per frame.** Recording: 2 phases x 1 view x 2,000 records = 4,000; seal: 2,000

**Estimate reasoning.** About 150-230 ns per recording validation (closure copy, lock, wrapper lookup) x 4,000 = 0.6-0.9 ms, plus about 100-150 ns of native validation x 2,000 at seal = 0.2-0.3 ms. The remaining value checks become O(headers).

**Proposal.** (1) Split VulkanVisibilityGeometryRecordClosure.TryValidate into TryValidateValues(in sceneState), with no resource runtime, and the existing native check. In TryBuildVisibilityGeometryStream, call PreparedVertexSource.TryValidate once for currentVertices (just captured by TryCaptureDeformationSources) and not per payload; per payload, call only TryValidateValues. (2) Store on the stream the seal-time scene native generation, index slice, and static vertex slice. Set a new `_rasterMembersValidatedFor` stamp (the target closure plus the program link generation) when TryPrepareVisibilityRasterPipelines has validated every member against header.Key.VisibilityBinding. (3) In Primary.Operations.cs, replace the per-record loop with a per-header check: assert bins.IsFrozen, assert the stamp matches payload.TargetClosure, check binding.SceneNativeGeneration == sealedSceneState.NativeGeneration, sealedSceneState.Indices.Matches(binding.IndexSlice), and the static slice match for non-native sources, and run binding.VertexSource.TryValidate(ResourceRuntime) once per header. On failure, throw the same VulkanPlanPreconditionException. Keep the full per-record loop only under VulkanFeatureProfile DevParity/Diagnostics.

**Correctness risks.** Records are immutable after Freeze(), and the per-record value facts were validated at seal against the same sealed scene state. Only the native lifetime of the shared buffer can change between seal and record, and the per-header TryValidate still checks it before every phase. A changed or replaced record means a new seal next frame, which still runs TryValidateValues per record. Risk: a bin that mixes record sources. TryPrepareVisibilityRasterPipelines already rejects that (it checks geometry.PreparedVertexSource.Matches(binding.VertexSource) and Template.IsValid == canonicalAtlas), and the stamp proves that it ran.

**Validation.** Unit test: a stream whose record source differs from its header binding fails TryPrepareVisibilityRasterPipelines, and a superseded native range (bumped lifetime generation) fails the per-header recording check. Live: EVulkanCpuStage.PrimaryAdvancedRasterValidationOperation CPU-stage time should drop from about O(4,000) to O(headers); 2,000-chain harness windows in both mesh modes; inject a deformation-output buffer replacement and confirm the frame retries rather than draws.

#### Verify and collapse per-chain external palette copies

- Thread: render (AdvancedSharedPreparationService.Acquire -> TryExecute). Estimated saving: 0.8 ms. Risk: medium. Effort: medium. Skips unchanged items today: no.
- Related todo item: Route remaining chain compute-skinning paths through aggregate deformation
- Locations:
  - `XREngine.Runtime.Rendering/Rendering/Preparation/Advanced/AdvancedGpuDeformationResources.cs:566-598 (Vulkan is not IAdvancedAggregateDeformationBackendCapability, so covered rows use AddExternalPaletteCopy)`
  - `XREngine.Runtime.Rendering/Rendering/Preparation/Advanced/AdvancedGpuDeformationResources.ExternalPalettes.cs:48-105`
  - `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.OrderedComputeApi.cs:64-124, 321-346`

**Work per item.** Per non-contiguous palette run: renderer.TryEnqueueGpuBufferCopy, then 2 wrapper lookups, EnsureStorageAllocatedForGpuUse, TryEnsureReadyForRendering, CaptureFrameOpContextOrLastActive, BufferCopyOp.Rent, and a frame-op queue enqueue; later the op is lowered and records one vkCmdCopyBuffer. AddExternalPaletteCopy merges only when the physics palette slice for row i+1 directly follows row i, both in the page palette and in draw order. Nothing guarantees that the PhysicsChainPaletteAtlasAllocator order matches the canonical submission order.

**Calls per frame.** 1 to 2,000 copy runs (unmeasured)

**Estimate reasoning.** Conditional: 0 ms if the runs already merge, and up to about 1.5 ms (2,000 x 0.5-0.8 µs plus the frame-op stream cost) if they do not. 0.8 ms is the expected value.

**Proposal.** Step 1 (an hour): expose _externalPaletteCopyCount in AdvancedDeformationDispatchTelemetry and read it in a 2,000-chain window. Step 2, if it is above a few: sort the external copies by source base before enqueuing, which allows merging (the destination order is free, because the job's BonePaletteOffset is the destination). Or better, have aggregate deformation bind the retained page palette directly: give each covered job BonePaletteOffset = physicsState.PaletteBase and bind lease.CurrentPaletteBuffer as a second palette source selected by a job flag. This removes the per-chain copies entirely.

**Correctness risks.** The merge must keep source and destination contiguity exact. Direct binding needs a shader ABI change (a second palette binding plus a job flag) and must keep the page retained through dispatch; the publication already carries the page token in _pendingPhysicsSource.

**Validation.** Telemetry: copy-run count before and after. AdvancedGpuDeformationResourceTests for merged-copy ranges. Live: containment checks (current and previous positions inside routed bounds) and a native capture showing one or a few vkCmdCopyBuffer calls.

#### Remove duplicate per-row lookups in AdvancedPreparationExtractor.ExtractCommand

- Thread: render (Advanced.VisibilityPreparation). Estimated saving: 0.7 ms. Risk: low. Effort: small. Skips unchanged items today: no.
- Related todo item: Separate stable Advanced preparation templates from frame bindings
- Locations:
  - `XREngine.Runtime.Rendering/Rendering/Preparation/Advanced/AdvancedPreparationExtractor.cs:732-738 and :950-953 (two TryCaptureRendererOutputState calls per covered draw)`
  - `AdvancedPreparationExtractor.cs:656-688 and :937-941 (TryPrepareMesh in the pre-pass and again in TryAddDeformation)`
  - `AdvancedPreparationExtractor.cs:712 and :1551-1568 (CaptureSnapshot copies about 200 B; only ModelHistoryReason and ForceCpuRendering are used)`
  - `AdvancedPreparationExtractor.cs:1086-1090, :1007, :1271-1276 (Settings and FrameTiming reads per row)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.OutputPages.cs:186-209`
  - `XREngine.Runtime.Rendering/Rendering/Preparation/Advanced/AdvancedGpuDeformationResources.MeshPreparation.cs:79-97`

**Work per item.** TryCaptureRendererOutputState: lock(_outputPageSync); page lookup; IsUsableOutputPage (fence SubmissionStatus, ProducerRenderer.AcceptsBackendWork, IsDeviceLost); a ReferenceEqualityComparer Dictionary<XRMeshRenderer,...> lookup; record-struct equality on the bounds source; BoneBufferGeneration; Envelope.Matches (a 64 B matrix compare and 4 property reads); a copy of the ~250 B output state. This runs twice per covered row. TryPrepareMesh (ready path): _meshSlices Dictionary lookup, _meshAliasWitnesses ConditionalWeakTable lookup, witness.Matches (about 15 loads incl. skinning-state and blendshape readers), and an InputWitnesses lookup. This also runs twice per row. CaptureSnapshot: 9 field loads from the scattered RenderCommandMesh3D, including 2 matrices.

**Calls per frame.** 2,000 rows x (2 output-state captures + 2 mesh preparations + 1 snapshot) per frame

**Estimate reasoning.** One fewer output-state capture saves about 100-200 ns x 2,000 = 0.2-0.4 ms, before any contention with the collect thread, which takes the same monitor in TryGetCommittedSpatialBounds (SpatialBounds.cs:45). Reusing the pre-pass mesh slice saves about 120-200 ns x 2,000 = 0.25-0.4 ms. Snapshot trimming and hoisting save about 0.05-0.1 ms.

**Proposal.** (1) In ExtractCommand, capture `PhysicsChainGpuRendererOutputState state` and its morph-weight span once, and pass them by `in`/out into TryAddDeformation. Remove the second TryCaptureRendererOutputState and use the captured state for Covers() and TryGetOrAddPose. (2) In TryPrepareCanonicalDeformationMeshes, store the per-command result (status, AdvancedGpuDeformationMeshSlice, topologyGeneration) in new arrays `_preparedMeshSlices` and `_preparedMeshStatus` of length MaximumDraws, together with a new AdvancedGpuDeformationResources.StaticInputsRevision counter. Increment that counter in TryCommitPendingMesh, in the _meshSlices.Remove paths, in TrySelectStaticGeneration switches, and in TryEnsureStaticInputsWritable. TryAddDeformation reuses the stored slice when the revision is unchanged; otherwise it calls TryPrepareMesh. Also add a one-entry `last mesh` memo in the pre-pass: in shared-mesh mode all 2,000 rows share one XRMesh. (3) Add RenderCommandMesh3D.CaptureAdvancedTemporalFlags() that returns (ModelHistoryReason, ForceCpuRendering) instead of the full snapshot. (4) Read RuntimeEngine.Rendering.Settings.AllowSkinning/AllowBlendshapes, UsesPackedAggregateInputs, and FrameTiming.RenderDeltaSeconds once per Build into fields.

**Correctness risks.** The output-state capture must still happen after TryCaptureGpuBoundsPublication retains the page, which it does, and its result is used inside the same row. Mesh-slice reuse is valid only within one open frame and an unchanged StaticInputsRevision. A commit or generation switch during the pre-pass increments the revision and forces the existing call. A stale slice would point at moved source rows, so every mutation path must increment the revision; add a unit test for each. Settings reads inside one Build are already logically frozen (the package strategy is frozen per frame).

**Validation.** AdvancedGpuDeformationResourceTests: a revision bump after commit, removal, and generation switch forces re-preparation. AdvancedPreparationIntegrationContractTests: covered-row payloads, jobs, and slices are bit-identical before and after. Live: AdvancedSharedPreparationTelemetry extraction ticks (get_advanced_profile_diagnostics) and harness windows in both mesh modes.

#### Dense, lock-free renderer output states on the retained physics output page

- Thread: render (Advanced.VisibilityPreparation); contention with collect. Estimated saving: 0.6 ms. Risk: medium. Effort: medium. Skips unchanged items today: no.
- Related todo item: Separate stable Advanced preparation templates from frame bindings; Resolve committed bounds once for each renderer in each frame
- Locations:
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.OutputPages.cs:186-209 and :913-943 (RendererStates Dictionary per page)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/PhysicsChainGpuOutputPageLease.cs`
  - `XREngine.Runtime.Rendering/Rendering/Preparation/Advanced/AdvancedPreparationExtractor.GpuBounds.cs:15-32`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.SpatialBounds.cs:45 (collect thread takes the same monitor)`

**Work per item.** Per covered row: lock(_outputPageSync), page token validation, IsUsableOutputPage, and a hash lookup keyed by the XRMeshRenderer reference. The monitor is shared with the collect thread's TryGetCommittedSpatialBounds (about 4 calls per covered mesh per frame per the todo), so the 2,000-4,000 render-thread acquisitions per frame contend while collect overlaps render.

**Calls per frame.** 2,000 (after the duplicate-lookups change) or 4,000 today

**Estimate reasoning.** Uncontended, about 60-100 ns per call x 2,000 = 0.15-0.2 ms. Contended Monitor acquisitions cost microseconds each. The investigation left preparation lock waits as an open check (6 to 19 ms in the frozen-physics mode), so this uses a mid estimate of 0.6 ms and could be more.

**Proposal.** At page commit (CommitOutputPage), build `PhysicsChainGpuRendererOutputState[] StatesBySlot` (indexed by PhysicsChainGpuBoundsSource.BoundsSlot, plus SlotGeneration) next to RendererStates, and expose it as ReadOnlyMemory on PhysicsChainGpuOutputPageLease, valid while the lease is retained. Add GPUPhysicsChainDispatcher.TryReadRendererOutputState(in PhysicsChainGpuOutputPageLease lease, in PhysicsChainGpuBoundsSource source, XRMesh mesh, out state, out morphWeights), with no lock: an array index, a SlotGeneration and BoundsSource equality check, BoneBufferGeneration, and Envelope.Matches. Validate the lease once per Build under the lock (TryCaptureGpuBoundsPublication already does this).

**Correctness risks.** A retained page's states are mutated only on the render thread: TryBeginOutputPage requires RetainCount==0, AbandonOutputPage touches only the writing page, and RetireOutputPageNativeResources runs only in NotifyOutputProducerKnownIdle on the render thread. So a render-thread lock-free read is safe while the lease is held. The dense array must be immutable after commit (allocate per page and reuse only when RetainCount==0). Failure invalidation still flows through IsUsableOutputPage at lease acquisition; a failure that arrives after acquisition is already handled by the existing producer-failure path. A held page alone must not authorize reuse, and it does not: per-row envelope and generation checks remain.

**Validation.** Unit test (dispatcher): StatesBySlot equals RendererStates for every committed renderer; a slot with a new SlotGeneration or bone-buffer generation is rejected; a page reused after release cannot be read through an old lease token. Live: the preparation lock-wait check in physics-validation.md (get_advanced_profile_diagnostics) in full and frozen-physics windows; extraction ticks.

#### Hoist per-family invariants out of TryBuildVisibilityGeometryStream

- Thread: render (Vulkan primary recording preparation). Estimated saving: 0.5 ms. Risk: low. Effort: small. Skips unchanged items today: no.
- Related todo item: Separate stable Advanced preparation templates from frame bindings
- Locations:
  - `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/StableBins/VulkanPreparedStableBinStream.cs:554-643`
  - `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/StableBins/VulkanRenderBinKey.cs:119-176 (CreateVisibilityGeometry)`
  - `VulkanRenderBinCompatibility.cs (VulkanRenderBinNativeCompatibility ctor and VulkanRenderBinContextCompatibility.Create)`

**Work per item.** Per payload: MixVisibilityKey over 5 shared-source values; `default(PendingMeshDraw) with {...}` (about 450 B zeroed and written; only CullMode and Instances vary); VulkanPreparedMeshPrimitive; a VulkanResidentDrawTemplateNativeState ctor (about 600 B); VulkanRenderBinKey.CreateVisibilityGeometry (Mix over 19 values, of which only geometry, producer, and raster fields vary per payload; VulkanRenderBinContextCompatibility.Create(in context), the same for all payloads; a NativeCompatibility built from the native state); manifest.ResetVisibilityGeometry; TryAppend `record with {...}`, which writes about 2 KB with about 20 GC write barriers into the heap array. Also Draws.TryGetDenseIndex and Geometry.TryGet, which copy the ~200 B AdvancedGeometryRecord.

**Calls per frame.** 2,000 (once per payload per frame)

**Estimate reasoning.** Context compatibility, the shared-source key hash, and the PendingMeshDraw/native template make up about 200-300 ns of the estimated 600-1,000 ns per payload in this loop. Hoisting them saves 0.4-0.6 ms.

**Proposal.** Before the payload loop, build once: the vertex-source signature for currentVertices and for the static source; VulkanRenderBinContextCompatibility.Create(in context); and up to 4 PendingMeshDraw templates keyed by (CullMode, Instances==1). Add a VulkanRenderBinKey.CreateVisibilityGeometry overload that takes the precomputed context compatibility and a precomputed partial Mix hash of the shared fields (scene generation, vertex source, index slice, IndexBuffer handle, binding signature), so only the per-payload fields are mixed. Build NativeCompatibility once per (template, IndexCount==ignored), because ignoreDrawCounts zeroes the count. No layout or record change.

**Correctness risks.** The keys must stay bit-identical. Mix is order-dependent, so the hoisted prefix must be mixed in the same order. Simplest: compute the full key the old way under DevParity and assert equality. The context and sources are constant for the whole call (one context, one sceneState, one deformation publication).

**Validation.** Debug assertion that old and new keys are equal for every payload in DevParity runs; S13aAdvancedFamilyStep.BinGeometryPayloads step time; 2,000-chain harness windows in both modes.

#### Memoize the covered material bounds verdict per Build

- Thread: render (Advanced.VisibilityPreparation). Estimated saving: 0.4 ms. Risk: low. Effort: small. Skips unchanged items today: no.
- Related todo item: Separate stable Advanced preparation templates from frame bindings; Add material bounds contract tests
- Locations:
  - `XREngine.Runtime.Rendering/Rendering/Preparation/Advanced/AdvancedPreparationExtractor.cs:735-745`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/PhysicsChainMaterialBoundsCertificate.cs:18-21`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/PhysicsChainMaterialBoundsEvaluator.cs:58-98 and :295-310`

**Work per item.** Per covered row: PhysicsChainDrawMaterialSnapshot.IsCurrent runs twice (5 XRMaterial version reads each). Evaluate does a ConditionalWeakTable GetValue, lock(cache), 4 version reads, and MatchesShaders, which loops over material.Shaders and does a ShaderHelper.HasTrustedUberVertexSource ConditionalWeakTable lookup per vertex shader. The benchmark chains share one XRMaterial, so 2,000 identical evaluations happen per frame.

**Calls per frame.** 2,000

**Estimate reasoning.** About 150-300 ns per row (2-3 ConditionalWeakTable lookups and 1 monitor) x 2,000 = 0.3-0.6 ms in shared-material scenes.

**Proposal.** Add a small per-Build memo in AdvancedPreparationExtractor (2-4 entries, reset in Build): key = (XRMaterial reference, BindingLayoutVersion, BindingValueVersion, BindingResourceVersion, ShaderStateRevision, UberStateRevision, certifiedPadding bits, producerSupported); value = the Covers() result. On a hit, skip PhysicsChainMaterialBoundsCertificate.Covers. Read the 5 versions from deformationSource.MaterialSnapshot (captured at publication) and compare them with the live material once per memo entry (the IsCurrent semantics).

**Correctness risks.** The verdict is a pure function of the material state that the evaluator itself caches by the same versions, plus the shader witness. MatchesShaders covers shader-list and source-revision changes that do not bump material versions. Include a material ShaderStateRevision and, per memo entry, run one full Evaluate per Build, so a shader change is still seen on the next frame. A mid-Build shader swap is already a race in the current code.

**Validation.** Unit test: two rows that share a material evaluate once per Build; a value-version bump between Builds changes the verdict; a rotation effect still rejects the route (BoundsSlot = uint.MaxValue). Live: the XRE_PHYSICS_CHAIN_TEST_VERTEX_EFFECTS=1 procedure in physics-validation.md still rejects rotation/scale.

#### Directional shadow cascade leases copy the family lease instead of re-running capture and variant planning

- Thread: render (Advanced stage enqueue). Estimated saving: 0.4 ms. Risk: low. Effort: small. Skips unchanged items today: no.
- Related todo item: Separate stable Advanced preparation templates from frame bindings; Add directional shadow slice and strict-admission tests
- Locations:
  - `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/FrameOps/VulkanFrameOperationQueue.cs:297-350 (shadow requests skip sharing at :306-308)`
  - `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/FrameOps/VulkanAdvancedVisibilityInputStorage.cs:149-258 (TryCaptureAtAuthoring) and :264-373 (CaptureOrValidate copy logic)`
  - `XREngine.Runtime.Rendering/Rendering/Preparation/Advanced/AdvancedVisibilityInputVariant.cs:17-52`
  - `XREngine.Runtime.Rendering/Rendering/Preparation/Advanced/AdvancedIndexedInstanceGroupPlanner.cs:48-176`
  - `XREngine.Runtime.Rendering/Rendering/Preparation/Advanced/AdvancedIndirectRangePlanner.cs:71-220`
  - `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Advanced/VPRC_AdvancedRenderStage.cs:480-512`

**Work per item.** Per capture, per payload: TryCopyVisibilityInputs under the preparation lock (5 span copies and the per-draw TryGetDrawDeformationSlice loop); the range planner (ResolveProducer, key build, FNV hash, probe, 2 more passes); and the group planner (two 96 B `payload with` rewrites, a Dictionary lookup on the 5-field record-struct content key, probe, group update). The cascade capture produces columns identical to the main family capture of the same publication. MatchesFamily already ignores the visibility targets for shadow stages.

**Calls per frame.** (1 + deferred cascade groups, usually 1) captures x 2,000 payloads

**Estimate reasoning.** One full capture is about 150-250 ns per payload x 2,000 = 0.3-0.5 ms. A sibling dense copy costs about 0.05-0.1 ms, so each removed cascade capture saves about 0.3-0.4 ms.

**Proposal.** Add VulkanAdvancedVisibilityInputStorage.TryCaptureFromSibling(in request, VulkanAdvancedVisibilityInputStorage sibling, out reason). It copies payloads, candidates, bounds routes, producers, ranges, groups, payload indices, and deformation slices from an active sibling, as CaptureOrValidate does. It copies the lane through _directionalShadowLane.TryCopyFrom(request.DirectionalShadowLane), retains the bounds page through GPUPhysicsChainDispatcher.TryRetainOutputPage(sibling.BoundsPage.Token), and copies Publication, Indirect, and DeformationPublication. In TryAcquireAdvancedVisibilityInput, for a shadow request, first look for an active lease whose Input.MatchesRequest(request) is true (non-shadow), and use a free lease with TryCaptureFromSibling. Fall back to TryCapture when no sibling exists.

**Correctness risks.** The sibling must be the same exact publication: MatchesRequest checks Publication.Equals and VisibilityContentGeneration. The sibling must stay retained during the copy: hold SyncRoot and call TryRetain first, then Release. The bounds page must be retained separately per lease, as Reset releases it. Ownership of the lane copy is unchanged.

**Validation.** Unit test: a sibling-captured lease is IsValid, MatchesPublication, and holds an independent bounds-page retain (the release count balances). Live: the AdvancedSharedPreparationTelemetry copy count drops by the number of cascade groups; the strict shadow windows still show 8,000 shadow draws with no rejection.

#### Covered-source fast path in TryGetOrAddPoseCore

- Thread: render (Advanced.VisibilityPreparation). Estimated saving: 0.4 ms. Risk: medium. Effort: small. Skips unchanged items today: no.
- Related todo item: Separate stable Advanced preparation templates from frame bindings; Make the bones of fully covered GPU chains dormant on the CPU
- Locations:
  - `XREngine.Runtime.Rendering/Rendering/Preparation/Advanced/AdvancedGpuDeformationResources.cs:496-628`
  - `XREngine.Runtime.Rendering/Rendering/XRMeshRenderer.DeformationSources.cs:16-78`
  - `XREngine.Runtime.Rendering/Rendering/XRMeshRenderer.cs:81-130 (publication lease Monitor)`

**Work per item.** Per covered row: Monitor.Enter/Exit on the renderer _resourcePublicationGate; a _poseEntries tuple-key lookup (always a miss: cleared each frame at :456) and insert; TryPrepareDeformationInputs (lease validation with Monitor.IsEntered, OwnsDeformationMesh, mesh.EnsureComputeSkinningBuffers, gpuOwnedPose checks, a CaptureBlendshapeResources copy of about 15 fields, the ActiveSkinPalette properties), whose palette result is discarded because the retained physics palette replaces it; envelope.Matches twice (64 B matrix compare each); about 8 XRDataBuffer property checks; AddExternalPaletteCopy.

**Calls per frame.** 2,000

**Estimate reasoning.** The full call is about 300-500 ns. A covered path that only validates the retained palette and envelope and records the copy is about 100-150 ns. Saving about 200-300 ns x 2,000 gives 0.4-0.6 ms.

**Proposal.** When retainedPalette != null, take a dedicated TryGetOrAddCoveredPose path. Skip TryPrepareDeformationInputs unless mesh.HasBlendshapes and AllowBlendshapes; when blendshapes are needed, call it with skinning:false. Use PoseVersion = the page ProducerEpoch (the key is only for intra-frame dedupe). Envelope.Matches is checked by the caller's single output-state capture (the duplicate-lookups hotspot), so check it once here. Keep the renderer lease only around the blendshape capture.

**Correctness risks.** The covered path must not be used with partial coverage (mixed palettes with UsesLocalBoneOwnership); boundsSource.HasValue already implies complete coverage, since TryCaptureGpuBoundsSource requires HasCompleteGpuDrivenBoneCoverage. Teardown safety: without the lease, read only immutable mesh and renderer identity, or keep the lease if the reviewer prefers. Blendshape-covered chains keep the morph capture.

**Validation.** AdvancedGpuDeformationResourceTests: covered pose slices (palette base/count, active blendshape range) are equal on the old and new paths; a blendshape-covered renderer still captures morphs. Live: native frames show the same palette copies and containment results (the previous-deformation-history check in physics-validation.md).

#### Retain the visibility input variant (ranges, producers, instance groups) across frames

- Thread: render (Advanced stage enqueue). Estimated saving: 0.3 ms. Risk: low. Effort: medium. Skips unchanged items today: no.
- Related todo item: Separate stable Advanced preparation templates from frame bindings; Add indexed instance-group tests
- Locations:
  - `XREngine.Runtime.Rendering/Rendering/Preparation/Advanced/AdvancedVisibilityInputVariant.cs:17-52`
  - `XREngine.Runtime.Rendering/Rendering/Preparation/Advanced/AdvancedIndexedInstanceGroupPlanner.cs:48-176`
  - `XREngine.Runtime.Rendering/Rendering/Preparation/Advanced/AdvancedIndirectRangePlanner.cs:71-220`
  - `XREngine.Runtime.Rendering/Rendering/Preparation/Advanced/AdvancedPreparationExtractor.cs:372-378 (extractor range plan superseded on Vulkan by the variant at VulkanAdvancedVisibilityInputStorage.cs:228)`

**Work per item.** Per capture, per payload: 3 range-planner passes, 2 group-planner passes with two 96 B payload rewrites, a record-struct Dictionary lookup, and a probe. The extractor runs a range plan too, which the Vulkan variant then discards.

**Calls per frame.** (1 + cascade groups) x 2,000, plus 2,000 in the extractor

**Estimate reasoning.** Range and group planning is about 100-150 ns per payload per capture. One structural signature pass of about 10 ns per payload replaces it, about 0.2-0.3 ms per capture; dropping the redundant extractor range plan adds about 0.05-0.1 ms.

**Proposal.** In AdvancedVisibilityInputVariant, keep the last result: producers, ranges, payload indices, groups, and the per-payload group index. Key it on (submission strategy, publication.DatabaseEpoch, Geometry.Generations, MaterialPayloads.Generations, vertex/index buffer handles, payload count) plus a 64-bit structural signature folded over the per-payload fields that decide producers and grouping (Geometry, Material, PrimitiveSection, IndexCount, VertexCount, InstanceCount, RasterStateClass, Coverage, CullMode, PrimitiveTopology, Skinned, MeshletsResident, ForceCpuDiagnostic). On a key match, copy the retained columns and re-stamp InstanceGroupIndexPlusOne only. Also skip the extractor's _indirectPlanner.Build when the consumer builds its own variant, or let the variant reuse it when the strategies match.

**Correctness risks.** A signature collision would reuse wrong groups. Add an exact per-payload structural column compare (dense, about 5 ns per row) instead of trusting only the hash, or confirm in DevParity. Changed membership, compaction, and source replacement change the handles or the generations.

**Validation.** Indexed instance-group tests (todo): partial changes to one payload's geometry, material, or count invalidate reuse; equal frames reuse. Live: copy ticks in AdvancedSharedPreparationTelemetry; the meshSharingEvidence group count is unchanged in both modes.

#### Skip animation scheduling for GPU-covered rows

- Thread: render (Advanced.VisibilityPreparation). Estimated saving: 0.2 ms. Risk: low. Effort: small. Skips unchanged items today: no.
- Related todo item: Separate stable Advanced preparation templates from frame bindings
- Locations:
  - `XREngine.Runtime.Rendering/Rendering/Preparation/Advanced/AdvancedPreparationExtractor.cs:989-1002 and :1213-1304`
  - `XREngine.Runtime.Rendering/Rendering/Preparation/Advanced/AdvancedAnimationScheduler.cs:34-132`

**Work per item.** Per row: a _boneTierScratch write, an open-address schedule lookup probe, TryGetVisibilityFeedback, an interface call to RuntimeRenderingHostServices.FrameTiming.RenderDeltaSeconds, and Scheduler.Schedule (FindOrAddSlot probe, cadence, StablePhase hash, tier select, 3 array writes). For covered sources, the palette comes from the physics output page. UpdateRenderPose is never used, and BoneTier is always 0 with a single tier, so the only effect is the folded PoseGeneration constant.

**Calls per frame.** 2,000

**Estimate reasoning.** About 80-120 ns per row x 2,000.

**Proposal.** In TryAddDeformation, when boundsSource.HasValue (complete GPU coverage), skip GetOrCreateAnimationSchedule and use a constant decision (BoneTier 0). Keep the scheduler for CPU/animated palettes. Hoist RenderDeltaSeconds to one read per Build in BeginAnimationScheduling.

**Correctness risks.** AnimationSchedules telemetry no longer lists covered rows. Document this, or add a covered-row count. The visibility feedback ring is unaffected. PoseGeneration stays deterministic because the tier is 0 either way.

**Validation.** AdvancedPreparationIntegrationContractTests: covered-row job keys are unchanged. Animated (non-covered) skinned rows still schedule. Live harness extraction ticks.

### Notes

I did not edit or build anything; all findings come from source reading at HEAD beb7d7896. Per-item ns figures are estimates from operation counts and the investigation's microbenchmark (dense ~2 ns per row, object pattern 21-83 ns), not measurements.

Budget picture: the two measured residuals in this area (Advanced.VisibilityPreparation ~6.2 ms, command recording ~6.7 ms) fit the counts. ExtractCommand plus its pre-passes costs about 1.5-2.5 µs per draw, and each authoring capture about 0.15-0.25 µs per draw (x2 with the shadow lane). The stable-bin rebuild and validation cost about 2-3 µs per record. Shared and unique mesh produce one extractor row and one stable-bin record per payload either way; indexed instance groups collapse only GPU arguments. This explains why the two modes measure the same.

Expected gains: the small, low-risk items (ref-readonly records, per-bin source validation, hoisted stream invariants, duplicate-lookup removal, material memo, scheduler skip, sibling shadow capture, covered pose path) should remove about 5 ms of the ~13 ms at 2,000 chains. Reaching this area's share of the ~2.5 µs per chain budget (about 0.5 µs per chain) needs the two large retained-template items: extractor row templates, and stream reuse with slim records.

Must stay fresh every frame: the physics output-page lease and per-renderer output state (palette base, envelope, PreviousPaletteValid/token history), bounds-route producer epoch, deformation arena slices and history flags, temporal reasons and VelocityInvalid, pose copy records, the deformation overlay, the frame-slot deformation output native range, set-1 counter and argument clears, per-view plans, and native lifetime checks (once per shared buffer, not per record).

Open checks:
(1) The preparation lock-wait counters (AdvancedSharedPreparationService _acquireLockWaitTicks/_copyLockWaitTicks) cover only _sync. GPUPhysicsChainDispatcher._outputPageSync has no wait instrumentation, yet the render thread takes it 4,000x per frame in extraction while the collect thread takes it in TryGetCommittedSpatialBounds. This is a plausible cause of the frozen-physics 6 to 19 ms jump and should be instrumented first.
(2) The external palette copy-run count is unmeasured. Read it before choosing between merging and binding the page palette directly.
(3) AdvancedPreparationOptions.Default caps MaximumDeformationJobs and arena OwnerCapacity at 4,096. Above about 4,096 covered chains, rows overflow into counted fallback, so the 2,000-chain target fits but larger scales will not.
(4) GlobalPreRender (GPUPhysicsChainDispatcher.ProcessDispatches, ~10.5 ms residual) is on the same render thread but belongs to the physics area and was not analyzed here.

Every proposal keeps strict-path rejection with counted reasons, adds no CPU fallback, and adds no per-frame heap allocation. The listed allocation fixes are independent hygiene changes.

## Collect thread: serial swap

Scope: Collect thread, serial swap section (EngineTimer DispatchSwapBuffers -> RuntimeWorldRenderer.GlobalSwapBuffersCore -> TransformHierarchyStore publication, Lights swap, VisualScene3D/GPUScene swap, AdvancedGpuScenePublisher.Publish, CpuBvhRenderTree.Swap, RenderCommandCollection/RenderCommandMesh3D swap) at HEAD beb7d7896

### Call chain

Collect thread: EngineTimer.CollectVisibleThread (XREngine.Runtime.Host/Core/Time/EngineTimer.cs:383) -> RunCollectVisibleIteration (:401; waits _renderDone :435, so render is idle during the swap; update and fixed-update threads keep running) -> TryPublishCollectVisibleGeneration (EngineTimer.FrameDispatch.cs:27) -> ProcessCollectVisibleSwapJobs (:34) -> DispatchSwapBuffers (EngineTimer.cs:907) -> WorldSwapBuffers (:919) BEFORE Timer.SwapBuffers (:920, viewport/window swaps).
WorldSwapBuffers -> RuntimeWorldHost.SwapWorldBuffers (XREngine.Runtime.Host/WorldHost/RuntimeWorldHost.cs:266) -> RuntimeWorldRenderer.GlobalSwapBuffers (XREngine.Runtime.Rendering/Rendering/RuntimeWorldRenderer.cs:156) -> GlobalSwapBuffersCore (:172):
 1. ApplyRenderMatrixChanges (:271) -> TransformHierarchyStore.PublishRenderMatrices (XREngine.Runtime.Core/Scene/Transforms/TransformHierarchyStore.cs:529; lock _passGate, which the update thread holds for all of Process :419-482) -> row loop over all ~24k rows (:546-555) -> TransformPublicationRecords.Publish (Rendering/Commands/GPUScene/TransformPublicationRecords.cs:20) -> DispatchNotifications (:496; Interlocked.Exchange per row :503) -> TransformBase.DispatchMatrixNotifications (TransformBase.cs:762) -> OnRenderMatrixChanged (:1522) -> XRComponent no-op handler (XRComponent.cs:339, subscribed for every component at :335) + RenderableMesh.RootBone_WorldMatrixChanged (RenderableMesh.Transforms.cs:212) -> UsesCommittedWorldBounds (RenderableMesh.CommittedBounds.cs:25) -> GPUPhysicsChainDispatcher.TryGetCommittedSpatialBounds (GPUPhysicsChainDispatcher.SpatialBounds.cs:38-165). Same chain runs in collect at GlobalPreCollectVisible (RuntimeWorldRenderer.cs:135).
 2. RenderableMesh.ProcessPendingRenderMatrixUpdates (RenderableMesh.Transforms.cs:481) - queue empty for covered meshes (all enqueue paths return on UsesCommittedWorldBounds).
 3. Lights.SwapBuffers (Lights3DCollection.Buffers.cs:12) -> DirectionalLightComponent.SwapBuffers -> shadow RenderCommandCollection.SwapBuffers (RenderCommandCollection.cs:2562; dirty-delta queue :2603-2626, ~2000 covered commands from the primary shadow view clean-skipped).
 4. AdvancedGlobalResourceCapture.Capture (Rendering/FrameView/AdvancedGlobalResourceCapture.cs:41) -> GPUScene.SetAdvancedGlobalResources.
 5. VisualScene3D.GlobalSwapBuffers (VisualScene3D.cs:325) -> PublishCoveredCommandSources (VisualScene3D.CanonicalCollection.cs:85; 2000 x _rc._dirty) -> VisualScene.GlobalSwapBuffers (VisualScene.cs:61) -> CpuBvhRenderTree.Swap (XREngine.Data/Trees/BVH/CpuBvhRenderTree.cs:126 -> PublishPending :800, clean early exit :809-817) -> GPUScene.SwapCommandBuffers (GPUScene.CommandBuffers.cs:48, under GPUScene._lock): dirty-range stream copies (:84-88), PublishRendererCommandIndices only if dirty (:111-115 -> GPUScene.BoundsHelpers.cs:730), PublishMeshSubmissionIfRequested (:116), PublishAdvancedResidentSceneIfRequested (:118 -> GPUScene.ResidentPublication.cs:185) -> AdvancedGpuScenePublisher.Publish (AdvancedGpuScenePublisher.cs:138) -> PublishCore (:167): EnsureBoundaryCapacity (:201) -> ScenePlan scope (:220) TryBuildAndPreflightWholeScenePlan (AdvancedGpuScenePublisher.MaterialTransitions.cs:130; per-command loop :148-308; TryPrepareSourceGroups SourceGroups.cs:76; registration loop :313; AdvancedGpuMaterialPublisher.TryPreflightTransition :150 per planned material) -> TryEnsurePlannedGeometryBoundaryCapacity (GeometryTransitions.cs:340, per command) -> TryStagePlannedGeometry (:144, per command) -> TryPreflightGlobalResources (GlobalResources.cs:58) -> ReuseCheck TryReuseUnchangedPublication (PublicationCoalescing.cs:11; HasPlannedPublicationMutation :115) -> else commit: ApplyTransitions per-command loop (AdvancedGpuScenePublisher.cs:314-406), TombstoneMissingRegistrations (:963), CaptureAndCommit (:416-473), IdentityDelivery (:477 -> SourceGroups.cs:251 -> RenderCommandMesh3D.PublishCanonicalDrawIdentities RenderCommandMesh3D.cs:844), CaptureCommittedMaterialPlans (MaterialTransitions.cs:445), PromoteSourceGroups, PromoteOrderedRegistrations.
 6. VisualScene3D.ProcessQueuedRaycasts, SwapBuffersHook.
Then Timer.SwapBuffers -> viewport pipelines -> main-view RenderCommandCollection.SwapBuffers (covered main-view commands are collected on the GPU and are not queued).

### Corrections to the 2026-10-08 inventory

- CpuBvhRenderTree.Swap '~1.63 ms, two calls' is pre-proxy. At HEAD covered meshes enqueue no Move (committed bound version and proxy), so both calls (VisualScene3D.cs:274 in collect, VisualScene.cs:64 in swap) take the clean early exit in PublishPending (CpuBvhRenderTree.cs:809-817): no swap commands, no topology or bounds revision change. It is no longer a per-chain cost.
- Serial swap row ('the whole-scene plan visits every command ... about 150 operations and 2 locks each'): partly stale. Covered untextured identity-world draws now reuse committed static draw plans (MaterialTransitions.cs:228-243: no CreateGeometry, no signatures, no material resolution), the ordered registration identity cache replaces most registration hash lookups, and source groups are retained. Still per command: 2 locks (GPUScene._lock re-entry in TryGetSourceCommand, dispatcher _outputPageSync in TryCaptureGpuBoundsSource), IsRegistrationCurrent (9 table lookups), and, because the benchmark creates one XRMaterial per chain, TryReuseUnboundMaterialPlan plus per-material TryPreflightTransition. Measured cost is still ~3.5 us per command.
- 'The reuse check runs after the full plan (confirmed)' is still true at HEAD (PublishCore order :201-271). New: the reuse check probably fails every steady-state frame in this benchmark, because the directional shadow record carries LastRenderedFrameLo/Hi (AdvancedGlobalResourceCapture.cs:219), the covered-caster output revision is part of the cascade content hash (Lights3DCollection.Shadows.cs:1784-1786), and any shadow byte change sets _plannedShadowPayloadUpdateCount (GlobalResources.cs:207-217), which HasPlannedPublicationMutation treats as a mutation (PublicationCoalescing.cs:121). So the full commit path (ApplyTransitions, CaptureAndCommit, IdentityDelivery, CaptureCommittedMaterialPlans) likely runs per frame in addition to ScenePlan. Confirm with S13aPublicationTelemetry PublicationReuse vs PublicationResourceMutation.
- 'TryGetCommittedWorldBounds runs about 9-12 times per chain per frame': within the swap section the remaining caller at HEAD is RenderableMesh.RootBone_WorldMatrixChanged -> UsesCommittedWorldBounds, once per moved covered root per render-matrix publication (also once more in collect). The query still costs ~6 locks, 2 ConditionalWeakTable lookups, and 4-6 Dictionary lookups (GPUPhysicsChainDispatcher.SpatialBounds.cs:38-165, PhysicsChainMaterialBoundsEvaluator.cs:63-64).
- 'PublishRenderMatrices and DispatchNotifications visit every transform row during collection and again at the swap' is still true, and incomplete: PublishRenderMatrices takes _passGate, which the update thread holds for its whole Process pass including callbacks (TransformHierarchyStore.cs:421-481), so the swap can block on the update thread; the _processing early return at :533 is unreachable from another thread. PublishRenderMatrices also holds the store sequence odd for its whole loop (:543-557), so update-thread matrix reads spin during each publication.
- Skip unchanged render commands at the swap: largely already true at HEAD. RenderCommandCollection.SwapBuffers walks only its dirty-delta queue and clean-skips unchanged commands (RenderCommandCollection.cs:2603-2626); covered main-view commands are collected on the GPU and are not queued; PublishCoveredCommandSources swaps only _rc._dirty commands; RenderableMesh.ProcessPendingRenderMatrixUpdates is empty for covered meshes. The remaining per-command swap work is the publisher (hotspot 1) and the render-matrix notification path (hotspots 2-3), not RenderCommandMesh3D.SwapBuffers.
- GlobalSwapBuffers 'residual 4.59 ms' has no profiler scope named RuntimeWorldRenderer.GlobalSwapBuffers at HEAD (GlobalSwapBuffersCore, RuntimeWorldRenderer.cs:172-204, has none). Its matrix/mesh/scene split is available only through RuntimeWorldTickTelemetry s_swapMatrixTicks/s_swapMeshTicks/s_swapSceneTicks (:197-203). The residual most likely contains PublishRenderMatrices (including any _passGate wait), DispatchNotifications with the ~2,000 committed-bound queries, and AdvancedGlobalResourceCapture.

### Allocations found on these paths

- AdvancedGlobalResourceCapture.Capture (XREngine.Runtime.Rendering/Rendering/FrameView/AdvancedGlobalResourceCapture.cs:51-52,76,99,129,133,135), called once per swap from RuntimeWorldRenderer.cs:186: new object?[] lightSources, new AdvancedLightRecord[], List<AdvancedShadowCaptureRow> (+growth), shadowRows.ToArray(), List<AdvancedProbeCaptureRow>, new AdvancedProbeRecord[n] (allocates even for n = 0), plus foreach over ThreadSafe EventLists (DynamicDirectionalLights/PointLights/SpotLights/LightProbes), which allocate snapshots per the comment at Lights3DCollection.Buffers.cs:23. About 8-12 objects per frame, independent of chain count. The capture is retained by GPUScene for the frame package, so pooling needs a small ring.
- RuntimeEngine.Rendering.Stats.RenderMatrix.RecordRenderMatrixChange (XREngine.Runtime.Rendering/Runtime/Statistics/RuntimeEngine.Rendering.Stats.RenderMatrix.cs:134): listeners.GetInvocationList() allocates one array per render-matrix change when EnableRenderMatrixListenerTracking is on (~2,000+ per publication at 2,000 chains). Debug flag only.
- TransformBase.RefreshMatrixSubscribers (XREngine.Runtime.Core/Scene/Transforms/TransformBase.cs:73-77): up to five GetInvocationList arrays per subscribe/unsubscribe; structural only (TrackBones on coverage change), not steady state.
- RenderCommandMesh3D.PublishCanonicalDrawIdentities (RenderCommandMesh3D.cs:853-855): new AdvancedGpuSceneDrawHandleSet -> handles.ToArray() only when the handle slice changes (structural). Identity snapshots are record structs; no PropertyChanged subscribers on commands were found, so no event-args allocation per identity delivery.
- AdvancedGpuScenePublisher commit path (AdvancedGpuScenePublisher.cs:390-394): AdvancedBrowserGeometryBasisSource.Capture / AdvancedBrowserUberAttributeSource.Capture per command on every committed publication for textured or uber-surface materials (not hit by this benchmark; check for allocation before scaling textured scenes).
- RenderableMesh.ProcessPendingRenderMatrixUpdates (RenderableMesh.Transforms.cs:487): ConcurrentQueue.Count on a multi-segment queue can freeze segments and force new segment allocation on later enqueues; the queue is empty for covered meshes, so this is low priority.
- GPUScene.RebuildDebugMeshletCollectionFromUpdatingCommands (GPUScene.CommandBuffers.cs:915): LINQ OrderBy over _commandIndexLookup; debug meshlet path only, when dirty.
- No LINQ, captured closures, boxing, or new arrays were found in the steady-state loops of TransformHierarchyStore.PublishRenderMatrices/DispatchNotifications, TransformPublicationRecords.Publish, RenderCommandCollection.SwapBuffers, CpuBvhRenderTree.Swap clean path, PublishCoveredCommandSources, or the publisher plan and reuse loops (all use preallocated, stamped arrays).

### Hotspots

#### Advanced scene publication plans every command before the unchanged check, and in this benchmark it probably commits a full publication every frame

- Thread: swap (collect thread). Estimated saving: 6.5 ms. Risk: medium. Effort: medium. Skips unchanged items today: no.
- Related todo item: Change-driven renderer path: Publish only changed rows from AdvancedGpuScenePublisher; Serial swap: Build the Advanced scene publication during collection
- Locations:
  - `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/Advanced/AdvancedGpuScenePublisher.cs:167-271 (PublishCore order: BoundaryCapacity, ScenePlan :220-225, GeometryCapacity, Stage, GlobalPreflight, ReuseCheck :265)`
  - `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/Advanced/AdvancedGpuScenePublisher.MaterialTransitions.cs:130-354 (per-command loop :148-308)`
  - `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/Advanced/AdvancedGpuScenePublisher.MaterialTransitions.cs:371-430,1008-1104,1577-1587 (TryReuseCommittedUntexturedDrawMaterial, TryReuseUnboundMaterialPlan, IsRegistrationCurrent)`
  - `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/Advanced/AdvancedGpuScenePublisher.cs:1174-1219,1486-1496 (CaptureSourceState, TryCaptureGpuBoundsSource)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.OutputPages.cs:127-139,488-518 (lock + 4-page failure scan per command)`
  - `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.Materials.cs:361-377 (TryGetSourceCommand: GPUScene._lock re-entry + Dictionary per command)`
  - `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/Advanced/AdvancedGpuScenePublisher.PublicationCoalescing.cs:115-131 (any shadow payload update defeats reuse)`
  - `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/Advanced/AdvancedGpuScenePublisher.GlobalResources.cs:193-217,243 (shadow record byte compare -> _plannedShadowPayloadUpdateCount)`
  - `XREngine.Runtime.Rendering/Rendering/FrameView/AdvancedGlobalResourceCapture.cs:192-220 (LastRenderedFrameLo/Hi in the directional shadow record)`
  - `XREngine.Runtime.Rendering/Rendering/Lights3DCollection.Shadows.cs:1784-1786 (covered caster output revision enters the cascade content hash, so the cascade re-renders on each physics output)`
  - `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/Advanced/AdvancedGpuScenePublisher.cs:314-406,445-490 (commit path per command: UpdateRegistration, AppendLegacyMapping, submission row, deformation row with 7 Dictionary<string> ContainsKey on renderer.Buffers, CaptureCommittedMaterialPlans)`
  - `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/Advanced/AdvancedGpuScenePublisher.SourceGroups.cs:251-292 -> RenderCommandMesh3D.cs:844-879 (identity delivery per command)`

**Work per item.** Plan, per command (2,000): DrawMetadata row read; TryGetSourceCommand (System.Threading.Lock re-entry + Dictionary<uint>); open-addressing insert; CaptureSourceState (~200-byte snapshot copy, CullBoundsBuffer read, renderer.TryGetMesh); 5 material version reads; TryCaptureGpuBoundsSource (monitor _outputPageSync + InvalidateFailedPublishedPages 4-page fence-status scan + Dictionary + coverage reads); CreateRenderState; ordered-registration check; HasValidatedCanonicalGeometry; TryReuseCommittedUntexturedDrawMaterial (~25 compares + IsRegistrationCurrent = 9 dense table lookups + planned-material probe); the benchmark makes one XRMaterial per chain (UnitTestingWorld.Math.cs:1234 CreateLitColorMaterial), so each row misses the probe and runs TryReuseUnboundMaterialPlan (~6 database lookups, variant hash, HeaderMatches, 2 slot inserts, ~300-byte request write); ResolveTemporalEvent. Then CanReuseSourceGroups (per command), registration pass, TryPreflightTransition per planned material (layout translate, emissive texcoord check, variant hash), geometry capacity and stage passes (cheap continue). About 2 locks, 8-12 hash/probe lookups per command; measured about 3.5 us per command (ScenePlan 6.96 ms / 2,000). Reuse fails when any shadow record byte changes; LastRenderedFrame changes on every cascade re-render, so the commit path also runs per command (~0.5 us each, estimated) plus identity delivery (2 SetField + 2 Interlocked per command).

**Calls per frame.** 1 publication per swap; 3-6 full passes over 2,000 commands (plan, source-group check, registration pass, geometry passes, reuse or commit loop, committed-plan capture, identity delivery)

**Estimate reasoning.** ScenePlan measured 6.96 ms at 2,000 chains. The proposed gate replaces it with one lock-free witness pass (~2,000 x 30-60 ns = 0.06-0.12 ms). If the steady state commits each frame (likely, see shadow LastRenderedFrame), the global-only commit also removes the per-command ApplyTransitions, deformation-row, committed-plan, and promotion loops (~0.5-1 ms estimated) but keeps identity delivery (~0.2-0.4 ms). Net 6-8 ms; 6.5 is the conservative value. If globals mutate every frame and only the first part (no-mutation skip) is built, the saving in this benchmark is near zero, so both parts are one item.

**Proposal.** Add an unchanged-row gate before the ScenePlan scope in AdvancedGpuScenePublisher.PublishCore, and a global-only commit. (1) Witnesses: GPUScene gets a long AdvancedSourceInputVersion, incremented in SwapCommandBuffers when commandSnapshotDirty || stageStreamsDirty || _rendererRoutesDirty || TotalCommandCount changed. RenderCommandMesh3D gets a long _renderSnapshotVersion incremented in SwapBuffersCore (:755) and ApplyLateRenderThreadWorldMatrix (:923); expose RenderCommand._mutationVersion (RenderCommand.cs:126) as internal MutationVersion (already bumped by renderer, submesh, material, render-options, and mesh observers, RenderCommandMesh3D.cs:223-555). GPUPhysicsChainDispatcher gets ulong CommittedBoundsSourceVersion, incremented under _outputPageSync wherever _committedBoundsSources is added, replaced, or cleared and when _publishedOutputPageIndex becomes -1, plus a method PollCommittedBoundsSourceVersion() that takes the lock once and runs InvalidateFailedPublishedPages once (keeps the failure side effect at the swap boundary). (2) Extend AdvancedResidentRegistration (next to CommittedMaterialPlan, which already retains Source, Renderer, Mesh, Material, Command, RenderState, and material versions) with RenderSnapshotVersion, MutationVersion, RendererBoneBufferGeneration, MeshGeometryRevision; write them in CaptureCommittedMaterialPlans. No new retention: registrations already own these references. (3) New TryPassUnchangedRowGate(scene, frameId): O(1) checks (current publication valid, not rejected or faulted, no pending identity recipients, ordered identities and retained source groups and committed plan cache valid, membership generation unchanged, AdvancedSourceInputVersion and CommittedBoundsSourceVersion unchanged, AllowSkinning and CalculateSkinningInComputeShader unchanged, frameId == _lastPlannedFrameId + 1, no boundary growth this call), then one dense loop over _orderedRegistrationIdentities that compares, per supported row, the command RenderSnapshotVersion and MutationVersion, renderer.TryGetMesh references, the 5 material versions and zero texture count, mesh GeometryRevision and VertexCount, renderer.BoneBufferGeneration and HasCompleteGpuDrivenBoneCoverage. No locks, no dictionaries. (4) On pass: keep _commandDrawHandles and _legacyMappings (move the Array.Clear calls at :171-173 and :214 after the gate), zero the material and resource plan counts, run TryPreflightGlobalResources. No global mutation: return as a reuse without identity re-delivery (publication reference unchanged) and record S13a reuse. Global mutation: open the transaction, ApplyPreflightedGlobalResources, skip the per-command UpdateRegistration/AppendLegacyMapping/deformation-row loop and TombstoneMissingRegistrations, copy Submission rows from the leased previous publication snapshot into TryCaptureSubmissionPublication, commit, deliver identities (required by the current identity contract), skip CaptureCommittedMaterialPlans and Promote*. (5) Replace per-row LastSeenFrameId stamping on gated frames with a publisher field _rowsSeenThroughFrameId; ResolveTemporalEvent uses max(registration.LastSeenFrameId, _rowsSeenThroughFrameId) while the membership generation is unchanged. Any failed check takes the existing full path unchanged.

**Correctness risks.** Staleness if a plan input changes without a witness change. Inputs read today and their witnesses: DrawMetadata, command lookup, bounds rows -> AdvancedSourceInputVersion; command render snapshot -> RenderSnapshotVersion; live renderer mesh/material and render options -> MutationVersion plus direct TryGetMesh reference compare; material binding values/resources/layout/shader/uber revisions -> direct per-row compare (these do not mark the command dirty); mesh geometry -> GeometryRevision compare; GPU bounds route and output-page failure -> CommittedBoundsSourceVersion after PollCommittedBoundsSourceVersion; coverage -> per-row coverage plus render settings; temporal FrameGap -> frame contiguity check; capacity -> no-growth check. GPU bound values and output-page leases are not part of the publication; preparation still validates page leases per frame, so no held page can authorize reuse. A rejected route (cleared committed sources) changes the dispatcher version, so the full plan runs and rejects as today (no hidden fallback). The global-only commit must not retain scratch closures: it copies from the ring-retained previous snapshot under a lease. Per-frame heap allocation: none added (all fields and spans preallocated).

**Validation.** Unit tests in XREngine.UnitTests/Rendering (next to AdvancedGpuSceneIdentityCacheTests and PhysicsChainGpuBoundsRouteTests, using AdvancedGpuSceneIdentityCacheFixture): an unchanged second publication passes the gate (new counter LastUnchangedRowGateHitCount) and keeps handles and publication; each of these forces the full plan and the expected result: material BindingValueVersion change, RenderOptions.CullMode change, mesh MarkGeometryChanged, command WorldMatrix change, renderer Mesh swap, coverage loss, committed bounds source cleared (publication rejected), frame gap (FrameGap temporal reason), command add/remove, boundary growth; a light/shadow record change takes the global-only commit and leaves command rows and legacy mappings bit-identical. Runtime: first read S13aPublicationTelemetry counters (PublicationReuse vs PublicationResourceMutation) at 2,000 chains to confirm the shadow-driven commit; after the change, GpuIndirect.AdvancedPublication.ScenePlan should fall from ~7 ms to <0.2 ms and swap phase (phaseTimings) should fall by ~6 ms in shared and unique mesh modes.

#### Covered meshes run the full committed-bound query in every root render-matrix notification

- Thread: swap (collect thread); identical cost in collect at GlobalPreCollectVisible. Estimated saving: 1.1 ms. Risk: low. Effort: small. Skips unchanged items today: no.
- Related todo item: Change-driven renderer path: Resolve committed bounds once for each renderer in each frame; Open test code: Add covered-mode transition tests for RenderableMesh and XRMeshRenderer
- Locations:
  - `XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.Transforms.cs:212-229 (RootBone_WorldMatrixChanged)`
  - `XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.Transforms.cs:263-304 (Component_WorldMatrixChanged, Component_WorldMatrixPreviewChanged)`
  - `XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.Transforms.cs:306-357,393-399 (ApplyImmediateRenderMatrixUpdate, QueueCurrentRenderMatrixUpdate, ApplyPendingRenderMatrixUpdates)`
  - `XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.CommittedBounds.cs:25-35 (UsesCommittedWorldBounds)`
  - `XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.CommittedBounds.cs:82-139 (ReconcileCommittedWorldBounds)`
  - `XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.cs:320-340 (root bone and component subscriptions)`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.SpatialBounds.cs:38-165`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/PhysicsChainMaterialBoundsEvaluator.cs:58-98`
  - `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.BoundsHelpers.cs:696-705`
  - `XREngine.Runtime.Core/Scene/Components/XRComponent.cs:329-342 (every component subscribes a no-op RenderMatrixChanged handler)`
  - `XREngine.Runtime.Core/Scene/Transforms/TransformBase.cs:762-769,1522-1531`

**Work per item.** Per moving covered chain root, per render-matrix publication: multicast delegate invoke (XRComponent no-op for PhysicsChainComponent + RenderableMesh handler), RenderMatrix seqlock read, RecordRenderMatrixChange and RecordEvent (2 Interlocked), then UsesCommittedWorldBounds -> TryGetCommittedSpatialBounds: 6 lock acquisitions (2x dispatcher _outputPageSync, _cpuSpatialMaterialSync, 2x GPUScene._lock, evaluator cache lock), 2 ConditionalWeakTable lookups, 4-6 Dictionary lookups, envelope match, material snapshot currency loops twice. Estimated 0.6-1.0 us per root with scattered objects. The answer is discarded: covered meshes return.

**Calls per frame.** ~2,000 per publication that contains root moves; one publication at collect and one at swap, each with root moves in ~2/3 of cycles (update ~30 ms vs cycle ~45 ms)

**Estimate reasoning.** 2,000 roots x ~0.8 us x ~0.7 probability of root moves in the swap publication = ~1.1 ms on the swap critical path; about the same again in collect. The replacement costs 2 volatile reads per root (~30 us total).

**Proposal.** In RenderableMesh add internal bool IsReconciledCovered => Volatile.Read(ref _usesCommittedWorldBounds) != 0 && ReferenceEquals(CurrentLODRenderer, _committedBoundsRenderer). Use it instead of UsesCommittedWorldBounds in RootBone_WorldMatrixChanged, Component_WorldMatrixChanged, Component_WorldMatrixPreviewChanged, ApplyImmediateRenderMatrixUpdate, QueueCurrentRenderMatrixUpdate, and ApplyPendingRenderMatrixUpdates. In ReconcileCommittedWorldBounds, in the branch where wasCovered and the mesh is no longer covered, call QueueCurrentRenderMatrixUpdate() after RefreshSkinnedCullingBoundsForSceneCulling so root and component matrices skipped while covered are reseeded. Keep UsesCommittedWorldBounds for the reconcile and canonical-collection decisions. Same item, optional: in XRComponent.OnTransformChanged/OnTransformChanging subscribe RenderMatrixChanged only when the runtime type overrides OnTransformRenderWorldMatrixChanged (per-type bool cached once by reflection on DeclaringType), which removes the no-op delegate from every moving chain root and lets bones and roots with no real listener skip OnRenderMatrixChanged entirely.

**Correctness risks.** While the reconciled flag is set but the committed bound became invalid after the last reconcile, root motion is not queued. Coverage loss already sets _committedBoundsStateDirty (RendererGpuBoneCoverageChanged, RendererCommittedOutputChanged with boundsChanged on a withdrawn page), and ReconcileCommittedWorldBounds runs before CPU tree traversal in every collect; the added QueueCurrentRenderMatrixUpdate reseeds from current transforms, so the first CPU-collected frame after loss sees current matrices. ApplyPendingRenderMatrixUpdates today publishes at swap if the query fails; with the flag it waits for the next reconcile, which precedes the next CPU use. The XRComponent change must keep the one-time call at OnTransformChanged (:336). No allocation added.

**Validation.** Unit test: a covered RenderableMesh receives root RenderMatrixChanged and makes zero TryGetCommittedSpatialBounds calls (counter on the dispatcher or a test hook); coverage loss followed by reconcile queues one render-matrix update and restores CPU culling bounds from the current root matrix; XRComponent subclass without override has no RenderMatrixChanged subscription, subclass with override still receives it. Runtime: enable RuntimeWorldTickTelemetry and compare s_swapMatrixTicks (RuntimeWorldRenderer.cs:199) before/after; expect ~1 ms lower per swap at 2,000 chains in both mesh modes.

#### Swap-time PublishRenderMatrices can block behind the update thread's whole Process pass

- Thread: swap (collect thread) waiting on update thread. Estimated saving: 0.4 ms. Risk: medium. Effort: small. Skips unchanged items today: no.
- Related todo item: Shared versioning contract: Give TransformHierarchyStore page-local sequences and a range read
- Locations:
  - `XREngine.Runtime.Core/Scene/Transforms/TransformHierarchyStore.cs:419-482 (Process holds _passGate across reorder, propagation, worker pool, and DispatchNotifications callbacks)`
  - `XREngine.Runtime.Core/Scene/Transforms/TransformHierarchyStore.cs:529-534 (lock(_passGate); the _processing early return cannot be reached from another thread)`
  - `XREngine.Runtime.Rendering/Rendering/RuntimeWorldRenderer.cs:176,271-280`
  - `XREngine.Runtime.Host/WorldHost/RuntimeWorldHost.cs:237,263-264 (Process runs in PostUpdateFrame)`

**Work per item.** No per-item work; a monitor wait whose length equals the remaining update-thread Process time (14,000 RecalculateMatrices with _gate locks plus WorldMatrixChanged callbacks at 2,000 chains).

**Calls per frame.** 1 wait opportunity at swap, 1 at collect

**Estimate reasoning.** Not measured. If Process takes 2-6 ms of a ~30 ms update iteration, the swap starts inside Process ~10-20% of the time and waits on average half of it: expected 0.2-0.6 ms, with p95 impact up to the full Process time. This affects interval p95 more than the mean.

**Proposal.** Measure first: compare RuntimeWorldRenderer s_swapMatrixTicks (includes the wait) with TransformHierarchyCounters.PublicationMs (excludes it). If the difference is material, add PublishRenderMatrices(bool skipIfProcessing) that uses Monitor.TryEnter(_passGate); RuntimeWorldRenderer.GlobalSwapBuffersCore passes true, GlobalPreCollectVisible keeps the blocking call. A skipped swap publication leaves _publication flags set, so the next collect publishes them, which matches the evident intent of the existing _processing check.

**Correctness risks.** In a skipped frame the CPU render matrices lag one update tick (never a torn hierarchy, because the alternative of publishing mid-Process is not used). Covered chains are unaffected (GPU pose). Ordinary moving meshes can show one frame of extra latency in rare frames; validate visually. No allocation.

**Validation.** Runtime first: enable XRE_WORLD_TICK_TELEMETRY at startup, read s_swapMatrixTicks/s_swapCalls and TransformHierarchyCounters at 2,000 chains in both mesh modes. After the change: swap-phase p95 drops; unit test that PublishRenderMatrices(skipIfProcessing: true) returns 0 while another thread holds the pass and that the rows publish on the next call.

#### Bones of fully covered chains still get render publication at every swap

- Thread: swap (collect thread); same at collect. Estimated saving: 0.25 ms. Risk: medium. Effort: medium. Skips unchanged items today: no.
- Related todo item: Physics steady state: Make the bones of fully covered GPU chains dormant on the CPU
- Locations:
  - `XREngine.Runtime.Core/Scene/Transforms/TransformHierarchyStore.cs:546-555,563`
  - `XREngine.Runtime.Core/Scene/Transforms/TransformHierarchyStore.cs:206,271-273,317-319,336-338 (_publication set on every world write)`
  - `XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.CommittedBounds.cs:99-115 (TrackBones subscribe:false when covered)`

**Work per item.** Per bone (12,000 non-root bones): 64-byte compare + 64-byte copy + slot append, sink 2 x 64-byte writes, mask handling. The GPU path does not read these values; CPU readers are editor overlays, picking, readback, and uncovered fallback.

**Calls per frame.** 12,000 rows x 1 swap publication (+ collect)

**Estimate reasoning.** ~12,000 x ~25 ns (copy, compare, sink) = 0.3 ms per publication x ~0.7 = ~0.2-0.25 ms at swap.

**Proposal.** Add a per-row bool _renderDormant to TransformHierarchyStore with internal SetRenderDormant(TransformHandle, bool). XRMeshRenderer (or RenderableMesh in the TrackBones(subscribe:false) branch) sets it for every bone of a renderer whose coverage is complete and that has no other render listener; it clears it on coverage loss or when a render listener is added. PublishRenderMatrices skips dormant rows and leaves their _publication flag set, so clearing dormancy publishes them on the next pass. TransformBase.RenderMatrix for a dormant row returns the world matrix, so CPU readers never see an old value. Long term this is replaced by the full dormant-bones item (no world recalculation).

**Correctness risks.** Shared bones (one transform used by covered and uncovered renderers) must not be dormant: use a reference count, not a bool. Readers that require the swap-coherent render matrix get the live world matrix instead; acceptable for overlays, must be checked for selective readback. Reseed on coverage loss is automatic because _publication stays set. No allocation.

**Validation.** Unit test: a fully covered chain publishes no bone render matrices; coverage loss publishes them before the next CPU consumer; RenderMatrix of a dormant bone equals its world matrix. Runtime: published-row count in TransformHierarchyCounters drops by ~12,000 per call at 2,000 chains.

#### Render-matrix publication scans and notifies every row, including 12,000 bones that have no render listener

- Thread: swap (collect thread); same work in collect. Estimated saving: 0.2 ms. Risk: low. Effort: small. Skips unchanged items today: no.
- Related todo item: Shared versioning contract: Give TransformHierarchyStore page-local sequences and a range read; Physics steady state: Make the bones of fully covered GPU chains dormant on the CPU
- Locations:
  - `XREngine.Runtime.Core/Scene/Transforms/TransformHierarchyStore.cs:529-575 (PublishRenderMatrices: loop over _count in _order, BeginWrite held for the whole loop)`
  - `XREngine.Runtime.Core/Scene/Transforms/TransformHierarchyStore.cs:496-526 (DispatchNotifications: Interlocked.Exchange on every row)`
  - `XREngine.Runtime.Core/Scene/Transforms/TransformHierarchyStore.cs:371 (SetSubscribers counts all five events together)`
  - `XREngine.Runtime.Core/Scene/Transforms/TransformBase.cs:71-79 (RefreshMatrixSubscribers)`
  - `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/TransformPublicationRecords.cs:20-37`

**Work per item.** Per row (~24,000 rows; ~12 transforms per chain): _order load + _publication test. Per published row (~14,000: root + 6 bones per chain): 64-byte compare, 64-byte copy, mask write, slot append; sink: 2 x 64-byte record writes + generation under one lock. DispatchNotifications: 1 Interlocked.Exchange per row (~24,000 full fences), ~14,000 list appends, ~14,000 DispatchMatrixNotifications calls, of which ~12,000 bones have no render listener. While the loop runs the store sequence is odd, so update-thread Read() calls spin.

**Calls per frame.** 1 at swap + 1 at collect (each ~0.7 probability of carrying 14,000 rows); Process on the update thread runs a third full DispatchNotifications scan

**Estimate reasoning.** Interlocked scan ~24,000 x 8 ns = 0.19 ms plus ~12,000 no-listener dispatches x ~8 ns = 0.1 ms per publication; x ~0.7 at swap = ~0.2 ms (plus ~0.2 ms in collect and less update-thread spinning).

**Proposal.** In TransformHierarchyStore add bool[] _renderListened (resized in EnsureCapacity) set by a new internal SetRenderListener(TransformHandle, bool) called from TransformBase.RefreshMatrixSubscribers (value: _renderMatrixChanged is not null) and from Attach. In PublishRenderMatrices set mask 16 only when _renderListened[i], and append i to a preallocated int[] _publicationNotifySlots in the same in-order loop. After publication call a new DispatchPublicationNotifications() that visits only those slots (Interlocked.Exchange per listed slot, same exception-retry rule as DispatchNotifications). Process keeps the full scan because its deferred masks come from parallel ranges. Optionally keep a dense list of rows whose _publication was set (append on false->true under _gate) so the 24,000-row scan becomes O(written rows); this keeps hierarchy order only if the list is consumed through _position sorting, so leave it for the page-version item.

**Correctness risks.** Order: PublishRenderMatrices runs under _passGate after Process cleared all masks, so the only nonzero masks after its loop are the ones the loop set, in _order order; listing them preserves callback order. A listener added between publication and dispatch would miss one render notification; today it would also miss it if the matrix did not change. Rows that gain a listener later receive the next change. No allocation (arrays sized with capacity).

**Validation.** Unit tests next to XREngine.UnitTests/Scene/TransformPropagationWorkerTests.cs: publication of a moved subtree notifies only rows with RenderMatrixChanged listeners, in hierarchy order; a listener added later is notified on the next change; a throwing listener re-queues the remaining listed rows. Runtime: TransformHierarchyCounters.PublicationMs (TransformHierarchyStore.cs:572) per call drops at 2,000 chains.

#### Per-command dispatcher lock in TryCaptureGpuBoundsSource on full-plan frames

- Thread: swap (collect thread). Estimated saving: 0.15 ms. Risk: low. Effort: small. Skips unchanged items today: no.
- Related todo item: Change-driven renderer path: Publish only changed rows from AdvancedGpuScenePublisher
- Locations:
  - `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/Advanced/AdvancedGpuScenePublisher.cs:1486-1496`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.OutputPages.cs:127-139,488-518`
  - `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/Advanced/AdvancedGpuScenePublisher.MaterialTransitions.cs:200-204`

**Work per item.** Per covered command: monitor enter/exit on _outputPageSync, InvalidateFailedPublishedPages (4 pages x fence SubmissionStatus read + usability check), Dictionary<XRMeshRenderer> lookup, 3 coverage/settings reads.

**Calls per frame.** 2,000 per full plan

**Estimate reasoning.** ~2,000 x 60-100 ns. Subsumed by hotspot 1 on steady frames; this keeps changed frames cheaper.

**Proposal.** Add GPUPhysicsChainDispatcher.BeginCommittedBoundsCapture(out ulong version) that takes _outputPageSync once, runs InvalidateFailedPublishedPages once, and exposes a read-only view of _committedBoundsSources valid until EndCommittedBoundsCapture (or copies the needed entries into a publisher-owned preallocated Dictionary only when the version changed). TryBuildAndPreflightWholeScenePlan calls it once and does lock-free lookups per command.

**Correctness risks.** The render thread is idle during the swap, so one lock hold for the plan duration does not add contention; the capture must stay inside the swap boundary so a later page commit cannot be mixed in. No allocation when the version is unchanged.

**Validation.** Existing PhysicsChainGpuBoundsRouteTests plus a test that a failed published page cleared during capture rejects the publication with the existing route reason.

#### Renderer route republication bumps one global generation that invalidates every renderer's material-route cache

- Thread: swap (collect thread); the cache refill cost lands in later bound queries on collect and render threads. Estimated saving: 0.1 ms. Risk: low. Effort: medium. Skips unchanged items today: yes.
- Related todo item: Change-driven renderer path: Resolve committed bounds once for each renderer in each frame
- Locations:
  - `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.CommandBuffers.cs:111-115`
  - `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.BoundsHelpers.cs:730-773`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.SpatialBounds.cs:61-79`

**Work per item.** When any command snapshot or draw-metadata range is dirty: per command Dictionary lookup + metadata read + material Dictionary lookup + Append; then per GPU-AABB renderer a ContainsRenderer lookup; the generation increment makes each renderer's next TryGetCommittedSpatialBounds re-capture its material snapshots under GPUScene._lock.

**Calls per frame.** 0 on clean frames; 2,000 commands on any frame with one dirty command

**Estimate reasoning.** Clean frames skip it today (commandSnapshotDirty/drawMetadataDirty gate). Only frames with unrelated command changes pay ~0.3-0.5 ms plus cache refills; expected average is small. Confirm with P3Diagnostics CommandSwapExecuted vs CommandSwapCleanSkipped.

**Proposal.** Track dirty command indices for routes (from the draw-metadata dirty range and the content-version writers) and update only those renderers' entries in GpuSceneRendererCommandIndexSnapshot, with a per-renderer route generation instead of one PublicationGeneration; TryGetRendererCommandMaterialGeneration returns the renderer generation.

**Correctness risks.** A missed dirty index leaves a stale drawn material in the CPU spatial bound check; keep the full rebuild on structural changes (add/remove/reorder) and use the partial path only for draw-metadata-only changes. No allocation if the per-renderer state is pooled.

**Validation.** Read P3Diagnostics counters at 2,000 chains first; implement only if executed swaps are frequent. Unit test: a material override change on one command changes only that renderer's route generation.

### Notes

Answers to the key questions.
(a) Yes. The whole-scene plan can be skipped before planning with an O(1) gate plus one lock-free witness pass (hotspot 1). Required fresh witnesses: GPUScene command/draw-metadata/bounds/route state (one version set in SwapCommandBuffers), per-command render snapshot and mutation versions, live renderer mesh/material references, material binding and shader/uber versions (these do not mark commands dirty), mesh geometry revision, dispatcher committed-bounds-source version with one InvalidateFailedPublishedPages poll per swap (output-page failure witness), renderer bone generation and coverage, render settings, frame contiguity for temporal reasons, and no boundary growth (capacity). GPU bound values and output-page leases are not in the publication; preparation keeps validating them per frame. Global resources must still be preflighted each swap, and the benchmark very likely mutates them every frame (shadow LastRenderedFrame), so the item must include a global-only commit or it saves nothing here. An alternative is to move per-frame shadow fields (LastRenderedFrame) out of the scene publication into the frame package, which would let the existing reuse path fire; that changes a native consumer contract and needs owner review.
(b) Building the publication during collection is not recommended before (a). The plan reads swap-published state: _allLoadedDrawMetadataBuffer is copied in SwapCommandBuffers, command render snapshots are swapped by PublishCoveredCommandSources, Lights.SwapBuffers, and the viewport swaps, and the render thread updates _committedBoundsSources while collection runs. A collect-time build would need the same witness set as (a) to validate at commit, so (a) is the prerequisite and already removes the steady-state cost; a collect-time build only helps frames with real row changes, which the 'publish only changed rows' item handles better.
(c) Mostly done at HEAD (see stale corrections). The remaining per-command swap work is in the publisher and in render-matrix notifications.
(d) For the 14,000 covered-chain bones (root + 6 per chain) the swap does: one _publication test per row over ~24,000 rows, 64-byte compare and copy, two 64-byte sink record writes, one Interlocked.Exchange per row in DispatchNotifications, ~14,000 DispatchMatrixNotifications calls, and ~2,000 root listener invocations that run the full committed-bound query. Hotspots 2, 3, and 6 remove most of this without the full dormant-bones change.
Ranking by ms / (risk x effort), with low/small = 1 and medium = 2: hotspot 1 (6.5/4 = 1.6), hotspot 2 (1.1/1), hotspot 3 (0.2/1), hotspot 4 (0.4/2, measure first), hotspot 5 (0.15/1), hotspot 6 (0.25/4), hotspot 7 (0.1/2). Hotspots 1 and 2 are independent and each fits in about a day (hotspot 1 may need two). Together they target ~7.5 ms of the ~13-15 ms swap; the remaining swap after them is mostly the commit/identity delivery on global-only frames, the transform publication, and viewport swaps.
Measurements to take before coding (all exist): S13aPublicationTelemetry reuse vs mutation counters (confirms the shadow-driven commit), P3Diagnostics CommandSwapExecuted vs CommandSwapCleanSkipped, RuntimeWorldTickTelemetry s_swap*Ticks (needs XRE_WORLD_TICK_TELEMETRY at startup), TransformHierarchyCounters PublicationMs/PropagationMs, and the AdvancedPublication sub-scopes (ScenePlan, ReuseCheck, ApplyTransitions, CaptureAndCommit, IdentityDelivery).
Outside this area but on the same path: RefreshCoveredCommandSources in collect (VisualScene3D.CanonicalCollection.cs:39-83) calls ReconcileCommittedWorldBounds, UsesCommittedWorldBounds, and CanUseCanonicalGpuCollection (which calls UsesCommittedWorldBounds again and two publisher lookups) for every covered mesh each frame; that is the collect-thread owner of the 'resolve committed bounds once' item. PublishMeshSubmissionIfRequested (GPUScene.MeshSubmissionPublication.cs:226) is a per-command loop if any pipeline requests mesh submission publication; for the Vulkan Advanced GpuIndirect path RequiresGpuMeshSubmissionPublication appears false, but confirm at runtime.
All file paths are relative to the repository root. No files were changed.

## Collect thread: collection, and update-thread coupling

Scope: Collect thread: pre-collect publication, main-view and primary directional shadow collection, swap-time matrix publication. Update thread: coupling to the frame critical path, and per-chain late-tick work.

### Call chain

WAIT EDGES (EngineTimer). The update thread (EngineTimer.cs:366-378 UpdateThread -> DispatchUpdate 934 -> FrameDispatch.cs:49 DispatchVariableUpdate) has no wait on collect or render. The collect thread waits only on _renderDone (EngineTimer.cs:435). The render thread waits only on the visibility publication (EngineTimer.cs:558). Vulkan releases the collect thread after submission and the next-frame-slot wait, before present (VulkanRenderer.FrameLoop.Submission.cs:448-453). So cycle = max(collect, render callback) + serial swap, and the update thread has no direct edge into it.

COLLECT THREAD (XRE-CollectVisible): EngineTimer.CollectVisibleThread (EngineTimer.cs:383) -> RunCollectVisibleIteration (401) -> FrameDispatch.cs:11 TryCollectVisibleGeneration -> DispatchCollectVisible (EngineTimer.cs:847).
 (1) PreCollectVisible -> RuntimeWorldRenderer.GlobalPreCollectVisible (RuntimeWorldRenderer.cs:122):
   a. ApplyRenderMatrixChanges (271) -> TransformHierarchyStore.PublishRenderMatrices (TransformHierarchyStore.cs:529). It takes _passGate (also held by update-thread Process), then _gate. It keeps one odd seqlock sequence across a scan of all ~24k rows (543-557), copies ~14k changed rows, and runs sink Publish (TransformPublicationRecords.cs:20). DispatchNotifications (564 -> 496) runs Interlocked.Exchange on every row and calls TransformBase.DispatchMatrixNotifications (TransformBase.cs:762) for ~14k rows. For each moving chain root: OnRenderMatrixChanged (1522) -> RenderMatrix seqlock read -> XRComponent.OnTransformRenderWorldMatrixChanged (empty) and RenderableMesh.RootBone_WorldMatrixChanged (RenderableMesh.Transforms.cs:212) -> UsesCommittedWorldBounds (RenderableMesh.CommittedBounds.cs:25) -> XRMeshRenderer.TryGetCommittedWorldBounds (XRMeshRenderer.CommittedBounds.cs:17) -> GPUPhysicsChainDispatcher.TryGetCommittedSpatialBounds (GPUPhysicsChainDispatcher.SpatialBounds.cs:38).
   b. RenderableMesh.ProcessPendingRenderMatrixUpdates (RenderableMesh.Transforms.cs:481). This is empty for covered meshes in the steady state.
   c. VisualScene3D.GlobalCollectVisible (VisualScene3D.cs:265): ProcessPendingRenderableOperations (512); RefreshGpuBoundsEligibility (622, Clear and re-Add of a list and a set over _skinnedMeshes); RefreshCoveredCommandSources (VisualScene3D.CanonicalCollection.cs:39: ReconcileCommittedWorldBounds :47, UsesCommittedWorldBounds :48, RefreshCoveredCommandSource RenderableMesh.CanonicalCollection.cs:19, CanUseCanonicalGpuCollection :67 -> RenderableMesh.CanonicalCollection.cs:54-93, which calls UsesCommittedWorldBounds again at :59); then ActiveCpuRenderTree.Swap (CpuBvhRenderTree.cs:126, which takes the clean early-out at 809-817).
 (2) CollectVisible:
   a. RuntimeWorldRenderer.GlobalCollectVisible (150) -> Lights3DCollection.CollectVisibleItems (Lights3DCollection.Shadows.cs:23) -> DirectionalLightComponent.CollectVisibleItems (DirectionalLightComponent.CascadeShadows.cs:3913) -> primary viewport XRViewport.CollectVisible -> VisualScene3D.CollectRenderedItems (VisualScene3D.MaskedVisibility.Entry.cs:9). allowGpuCollection is false, because CanCollectCoveredSourcesOnGpu (VisualScene3D.CanonicalCollection.cs:91) rejects shadow passes. -> CollectRenderedItemsGpu (VisualScene3D.cs:678). For each of ~2,000 renderables: AllowRender (RenderInfo3D.cs:218 -> Intersects 229 -> WorldCullingVolume 178 -> bound query, even with a null volume); RenderInfo.CollectCommands (RenderInfo.cs:343) -> RenderableMesh.BeforeAdd (RenderableMesh.cs:378: lifetime Monitor; 3 seqlock reads for the camera distance at 390-391; ReconcileCommittedWorldBounds 430; UsesCommittedWorldBounds 431; SyncMaterialPassCommands; highlight) -> RenderCommandMesh3D.CollectedForRender (RenderCommandMesh3D.cs:696 -> CullingVolume 1022 -> bound query) -> RenderCommandCollection.AddCPU (RenderCommandCollection.cs:945 -> CaptureSortDistance 714 -> bound query; collection Lock; pass dictionary; GetSortOrderKey with 2 dictionary lookups; membership HashSet add plus append, sorted later).
   b. Cascades take only PrepareGpuShadowFramePackage (DirectionalLightComponent.CascadeShadows.cs:3992-4012).
   c. Main view: XRViewport.CollectVisibleAutomatic -> the same entry with allowGpuCollection=true -> IsCollectedByCanonicalGpu (VisualScene3D.CanonicalCollection.cs:99), which runs the full CanUseCanonicalGpuCollection a second time, including one more bound query.
 (3) PostCollectVisible: ImportedTextureStreamingManager.OnPostCollectVisible.
 (4) Wait _renderDone -> FrameDispatch.cs:27 TryPublishCollectVisibleGeneration -> DispatchSwapBuffers (EngineTimer.cs:907) -> RuntimeWorldHost.SwapWorldBuffers (RuntimeWorldHost.cs:266) -> RuntimeWorldRenderer.GlobalSwapBuffersCore (RuntimeWorldRenderer.cs:172): ApplyRenderMatrixChanges again (176). This is a second PublishRenderMatrices; it again waits on _passGate and again runs root handlers with bound queries. Then ProcessPendingRenderMatrixUpdates, and VisualScene3D.GlobalSwapBuffers (325: PublishCoveredCommandSources, base VisualScene.GlobalSwapBuffers which runs a second tree Swap at VisualScene.cs:64, then GPUCommands.SwapCommandBuffers).

BOUND QUERY LEAF. GPUPhysicsChainDispatcher.SpatialBounds.cs:38-166, per call:
- lock _outputPageSync two times (45, 101);
- lock _cpuSpatialMaterialSync (57);
- ConditionalWeakTable route lookup (63);
- GPUScene._lock two times through TryGetRendererCommandMaterialGeneration (66, 151; GPUScene.BoundsHelpers.cs:696-705);
- PhysicsChainMaterialBoundsEvaluator.Evaluate (ConditionalWeakTable lookup plus lock, PhysicsChainMaterialBoundsEvaluator.cs:58-98);
- RendererStates.TryGetValue two times (large struct copies), _committedBoundsSources lookup, IsUsableOutputPage two times, envelope match, material IsCurrent two times.
The render thread holds _outputPageSync in CommitOutputPage while it copies two 2,000-entry dictionaries and raises 2,000 CommittedOutputChanged events (GPUPhysicsChainDispatcher.OutputPages.cs:523-585), and in ConfirmResidentSpatialState (GPUPhysicsChainDispatcher.SpatialBounds.cs:177-213).

UPDATE THREAD (XRE-Update):
- UpdateFrame -> RuntimeWorld.Update (RuntimeWorld.Transforms.cs:15).
- Normal group: MathIntersectionsWorldControllerComponent.UpdateBenchmarkRootMotion (MathIntersectionsWorldControllerComponent.cs:932, 2,000 Transform.Translation sets) and PhysicsChainWorld.UpdateTick (PhysicsChainWorld.cs:463).
- Late group: PhysicsChainWorld.LateTick (498 -> LateTickExclusive 539): EvaluateQualityBudget (PhysicsChainWorld.QualityBudget.cs:197); PrepareGpuRestInputPhase (PhysicsChainWorld.GpuInputs.cs:69); CaptureGpuRestInputsForWorldPhase (123); the per-component loop (573), BeginWorldLateTick (PhysicsChainComponent.cs:261) -> ExecuteGpuLateUpdate (PhysicsChainComponent.GPU.cs:110) -> PrepareGpuDispatchDataCore (226; the fast path at 234-245 is off when colliders exist) -> RuntimePhysicsChainRenderingBridge.Execute (112) -> GPUPhysicsChainDispatcher.SubmitData (GPUPhysicsChainDispatcher.cs:790, per-request InputGate); PublishActivityDiagnostics (PhysicsChainWorld.ActivityDiagnostics.cs:48).
- PostUpdateFrame -> RuntimeWorldHost.ProcessDirtyTransforms (RuntimeWorldHost.cs:263) -> TransformHierarchyStore.Process (TransformHierarchyStore.cs:419). It holds _passGate for the whole call. 4 TransformPropagationWorkers (TransformPropagationWorkers.cs:25; the unit-test world default is RecalcChildMatricesType=Asynchronous, UnitTestingWorld.Toggles.cs:476) run ProcessRange -> TransformBase.RecalculateMatrices (TransformBase.cs:1100). This takes _gate 2-6 times per transform and increments the shared _sequence. DispatchNotifications also runs inside _passGate.

FIXED THREAD: PhysicsChainWorld.FixedTick (PhysicsChainWorld.cs:428). It shares _tickGate with LateTick. No render or collect path waits on _tickGate.

### Corrections to the 2026-10-08 inventory

- The claim 'Every covered mesh moves in the octree and CPU BVH, and the BVH refits' is no longer true. The per-renderer version and proxy keep tree placement for an unchanged bound, and CpuBvhRenderTree.PublishPending takes the clean early-out (CpuBvhRenderTree.cs:809-817). The timeline value 'CpuBvhRenderTree.Swap 1.63 ms (two calls per cycle)' was measured before that change and needs a new measurement. There are still two Swap calls per cycle: VisualScene3D.cs:274 and VisualScene.cs:64.
- The claim 'The bounds generation is the global ProducerEpoch' is fixed (GPUPhysicsChainDispatcher.SpatialBounds.cs:329-344, NotifyCommittedSpatialOutputChanged :351-362).
- The claim 'ReconcileCommittedWorldBounds still runs about four times per mesh per frame' (investigation, committed bound section) is wrong at HEAD. In GPU dispatch mode it runs two times per covered mesh per cycle: VisualScene3D.CanonicalCollection.cs:47 and RenderableMesh.cs:430 in the primary shadow BeforeAdd. RefreshSkinnedCpuCullingBounds and RefreshCommittedCpuTreeMembers run only in CPU-direct mode or on a mode switch. Total committed-bound queries are still about 10-11 per covered mesh per cycle: UsesCommittedWorldBounds 4-5 times, RenderCommandMesh3D.CullingVolume 2 times, WorldCullingVolume 1 time, Reconcile 2 times, and the root-bone handler 1-2 times.
- The claim 'about 5 locks and 6 hash lookups for each call' needs a refinement. Each TryGetCommittedSpatialBounds call takes 6 lock acquisitions: _outputPageSync 2 times, _cpuSpatialMaterialSync, GPUScene._lock 2 times through TryGetRendererCommandMaterialGeneration, and the material contract cache lock. It also does 2 ConditionalWeakTable lookups and about 5 dictionary lookups, of which 2 copy the large PhysicsChainGpuRendererOutputState struct.
- The phrase 'BeforeAdd and AddCPU, with a lock and a sorted insert' needs a refinement. AddCPU appends to SnapshotSortedRenderCommandCollection and adds to a membership HashSet. The sort is deferred to EnsureSorted on first enumeration (RenderCommandCollection.cs:126-191).
- The serial-swap todo item lists RenderableMesh.ProcessPendingRenderMatrixUpdates as swap work. At HEAD, covered meshes never enqueue to it, because the handlers and QueueCurrentRenderMatrixUpdate return early when the mesh is covered, so the queue is empty in the steady state. The per-mesh cost moved into the RenderMatrixChanged handlers: RootBone_WorldMatrixChanged at RenderableMesh.Transforms.cs:216 still runs a full bound query for each moving root on each publication.
- The claim 'PublishRenderMatrices and DispatchNotifications visit every transform row during collection and again at the swap' is still true, with one addition. DispatchNotifications runs a third time per update tick inside Process, and Process and both publications all hold _passGate. This couples the serial swap to update-thread propagation, which the inventory does not mention.
- The claim 'TransformHierarchyStore ... each read uses a full memory barrier' is still true, with additions. Read retries with SpinWait.SpinOnce() at the default threshold, which calls Thread.Sleep(1) from the 20th retry (TransformHierarchyStore.cs:168,187). PublishRenderMatrices keeps the sequence odd for its full row scan (543-557).
- Not in the inventory: Process runs on 4 dedicated propagation workers in the unit-test world (RecalcChildMatricesType=Asynchronous, UnitTestingWorld.Toggles.cs:476; TransformPropagationWorkers.cs:25), and all of them contend on one _gate and one _sequence cache line.
- Still true at HEAD: about 13 IsActiveInHierarchy walks per chain per late tick; the fast pack path is off when colliders exist (PhysicsChainComponent.GPU.cs:234-245); RefreshGpuBoundsEligibility rebuilds its list and set each frame (VisualScene3D.cs:622-638); CanUseCanonicalGpuCollection runs two times per mesh (VisualScene3D.CanonicalCollection.cs:67-68 and :99-101); and CanCollectCoveredSourcesOnGpu excludes shadow passes (:91-97).

### Allocations found on these paths

- I found no confirmed steady-state per-frame heap allocation on the traced paths: GlobalPreCollectVisible, GlobalCollectVisible, main and primary-shadow CollectRenderedItemsGpu, BeforeAdd, AddCPU, PublishRenderMatrices, DispatchNotifications, Process, the propagation workers, or the PhysicsChainWorld late tick. Lists are pre-sized, enumerators are struct types, and the Sort comparisons are static lambdas.
- First use only: GPUPhysicsChainDispatcher.SpatialBounds.cs:63-64 creates one CpuSpatialMaterialRouteCache per renderer (ConditionalWeakTable.GetValue with a static factory). PhysicsChainMaterialBoundsEvaluator.cs:63 creates one ContractCache per material.
- Conditional: XRBase.SetField (XRBase.cs:279-301) allocates XRPropertyChangingEventArgs<T> and XRPropertyChangedEventArgs<T> on each set when an accepting handler exists. The benchmark root motion sets Transform.Translation 2,000 times per tick (MathIntersectionsWorldControllerComponent.cs:946-948). The SceneNode handlers are filtered to Parent and World (SceneNode.Transform.cs:19-25), so this does not allocate unless another accepting subscriber, such as an editor inspector, is attached.
- Diagnostics only: the RenderCommandCollection.AddCPU missing-pass path builds strings with LINQ OrderBy (RenderCommandCollection.cs:958-963), capped at 30 messages.
- Not on these paths: VisualScene3D.GetEnumerator (VisualScene3D.cs:502-510) uses ToArray and an iterator.
- Not verified: whether GPUPhysicsChainDispatcher.SubmitData (GPUPhysicsChainDispatcher.cs:807-812) reuses the input arrays when the transform signature changes on every tick. The root moves each tick, so _gpuTransformSignature increments.
- The unattributed 1.3-1.4 MB per completed frame is not explained by any loop traced here. Look in the render-thread paths.

### Hotspots

#### Explicit GPU consumer for covered casters in the primary directional shadow tile

- Thread: collect (plus swap and render follow-on). Estimated saving: 2.2 ms. Risk: high. Effort: large. Skips unchanged items today: no.
- Related todo item: Change-driven renderer path: Give the primary directional shadow tile an explicit native GPU consumer and command ownership contract
- Locations:
  - `XREngine.Runtime.Rendering/Rendering/VisualScene3D.CanonicalCollection.cs:91-97`
  - `XREngine.Runtime.Rendering/Rendering/VisualScene3D.cs:678-731`
  - `XREngine.Runtime.Rendering/Scene/Components/Lights/Types/DirectionalLightComponent.CascadeShadows.cs:3913-3928`
  - `XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.cs:378-478`
  - `XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommandCollection.cs:945-1013`

**Work per item.** For each covered mesh: AllowRender, then BeforeAdd (lifetime Monitor, material pass sync, highlight), CollectedForRender, and AddCPU (collection Lock, pass dictionary, GetSortOrderKey with 2 dictionary lookups, membership HashSet add, Entry.Capture with state-bucket resolution). This is about 2.5 us today with 5 bound queries and about 1-1.2 us after the items above. The shadow command collection then swaps, sorts, and prepares 2,000 CPU commands.

**Calls per frame.** 2,000 per cycle

**Estimate reasoning.** Collect only, after the bound-query items: ~1.1 us x 2,000. The swap and render-thread savings for the shadow command collection are not estimated here.

**Proposal.** Implement the todo item: route covered casters of the primary tile through the GPU caster culling that the cascades already use, with an explicit consumer and a command-ownership contract that keeps CastShadow, layer, mirror, and non-mesh policy. Rejected strict work must stay dirty, with no empty draw and no CPU fallback.

**Correctness risks.** As the todo item states: the general shadow pipeline still consumes CPU-owned commands, and a previous simple bypass was unsafe. Caster policy and strict-admission retry must stay exact.

**Validation.** The directional shadow slice and strict-admission tests listed in the todo. Shadow captures from several camera positions. Collect and swap phase times in both mesh modes.

#### Use the reconcile result instead of querying the committed bound again

- Thread: collect. Estimated saving: 1.8 ms. Risk: low. Effort: small. Skips unchanged items today: no.
- Related todo item: Change-driven renderer path: Resolve committed bounds once for each renderer in each frame
- Locations:
  - `XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.CommittedBounds.cs:82-139`
  - `XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.CommittedBounds.cs:25-35`
  - `XREngine.Runtime.Rendering/Rendering/VisualScene3D.CanonicalCollection.cs:47-48`
  - `XREngine.Runtime.Rendering/Rendering/VisualScene3D.CanonicalCollection.cs:67-68`
  - `XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.CanonicalCollection.cs:59`
  - `XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.cs:430-431`

**Work per item.** For each covered mesh in each cycle: ReconcileCommittedWorldBounds computes activeCovered with one bound query. Right after it, UsesCommittedWorldBounds and the UsesCommittedWorldBounds term inside CanUseCanonicalGpuCollection each query again. In the primary shadow pass, BeforeAdd also calls Reconcile and then UsesCommittedWorldBounds. Each extra query costs 6 lock acquisitions, 2 ConditionalWeakTable lookups, about 5 dictionary lookups with ~150-byte struct copies, and one material contract evaluation (about 0.3 us uncontended).

**Calls per frame.** 6,000 redundant bound queries per cycle (3 per covered mesh x 2,000)

**Estimate reasoning.** Calibration: the CollectRenderedItemsGpu residual is 6.17 ms for two calls. Those calls contain about 6 queries per mesh plus about 0.8 us per mesh of other work, so one query is about 0.3-0.35 us. 6,000 x 0.3 us = 1.8 ms of collect phase. Collect (23 ms) is below render (29 ms) today, so this becomes frame time only after the render callback drops. It is still required to bring collect to about 6 ms for 100 Hz.

**Proposal.** Change RenderableMesh.ReconcileCommittedWorldBounds() to return bool activeCovered (the value it computes at :87-90). After the call, _usesCommittedWorldBounds equals activeCovered and _committedBoundsRenderer equals CurrentLODRenderer. In VisualScene3D.RefreshCoveredCommandSources, use 'bool covered = mesh.ReconcileCommittedWorldBounds();'. Add an internal overload CanUseCanonicalGpuCollection(GPUScene scene, bool committedBoundsKnownValid) that skips the UsesCommittedWorldBounds term when the caller passes true. Keep the old overload for IsCollectedByCanonicalGpu. In RenderableMesh.BeforeAdd, replace 'ReconcileCommittedWorldBounds(); if (!UsesCommittedWorldBounds)' with 'if (!ReconcileCommittedWorldBounds())'.

**Correctness risks.** The value comes from the same thread at the same instant. The only difference from today is a sub-microsecond window in which the render thread can commit or withdraw a page between the two queries. Today that window can make the second query disagree with the tree decision that Reconcile just made, so the change removes an inconsistency. Coverage loss still takes the existing slow path in Reconcile. IsSkinned is part of activeCovered; _skinnedMeshes holds only skinned meshes, and BeforeAdd uses the value only inside 'if (skinned)'.

**Validation.** Unit test in XREngine.UnitTests/Rendering/RenderableMeshBoundsTests.cs. Use a counting fake ICommittedWorldBoundsProvider and assert that one RefreshCoveredCommandSources pass makes one query per covered mesh (today 3) and that coverage loss still restores CPU bounds. Live check: harness phaseTimings collect, and RuntimeWorldRenderer.CollectionTelemetry collect scene ticks with XRE_WORLD_TICK_TELEMETRY=1, in shared and unique mesh modes.

#### Resolve each covered renderer's committed bound once per collect cycle (revision-keyed cache)

- Thread: collect (and swap). Estimated saving: 1.4 ms. Risk: medium. Effort: medium. Skips unchanged items today: no.
- Related todo item: Change-driven renderer path: Resolve committed bounds once for each renderer in each frame
- Locations:
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.SpatialBounds.cs:38-166`
  - `XREngine.Runtime.Rendering/Rendering/XRMeshRenderer.CommittedBounds.cs:17-18`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.OutputPages.cs:508-517`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.OutputPages.cs:523-585`
  - `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.BoundsHelpers.cs:696-705`
  - `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/PhysicsChainMaterialBoundsEvaluator.cs:58-98`

**Work per item.** Each call takes 6 locks: _outputPageSync two times, _cpuSpatialMaterialSync, GPUScene._lock two times, and the material cache lock. It also does 2 ConditionalWeakTable lookups and about 5 dictionary lookups with large struct copies. It contends on _outputPageSync with the render thread, which runs at the same time as collect and holds that lock in CommitOutputPage (2,000 events and 2 dictionary copies) and in ConfirmResidentSpatialState.

**Calls per frame.** ~10-11 per covered mesh per cycle at HEAD (~21,000); ~4 per mesh after the five single-site items above

**Estimate reasoning.** This is the incremental value after the five single-site items: about 4 remaining queries per mesh become ~1.3 full resolutions plus ~2.7 cache hits of ~50-80 ns, so 2,000 x ~0.7 us = 1.4 ms. Standalone on HEAD it saves about 2,000 x (10.5 - 1.3) x 0.3 us minus the hit cost, which is about 4.7 ms. It also reduces _outputPageSync contention with the render thread.

**Proposal.** Add ulong _committedSpatialStateRevision to GPUPhysicsChainDispatcher. Increment it under _outputPageSync wherever _publishedOutputPageIndex, _committedBoundsSources, or the published page's RendererStates change: CommitOutputPage, InvalidateFailedPublishedPages, and the clear and unregister paths. Expose GPUScene _publishedRendererCommandIndices.PublicationGeneration as a volatile field. Add a per-renderer cache on XRMeshRenderer (internal fields: Revision, SceneGeneration, BoneBufferGeneration, Mesh, PublishedPage, Bounds, Version, Status, and the reused material-snapshot list) guarded by one per-renderer Lock. Hit condition: the revision, scene generation, BoneBufferGeneration, Mesh reference, and HasCompleteGpuDrivenBoneCoverage are equal, IsUsableOutputPage(cached page) is true, and AreCpuSpatialMaterialSnapshotsCurrent is true. On a miss, run the existing slow path and store the result.

**Correctness risks.** A fence failure or device loss does not increment the revision, so every hit checks IsUsableOutputPage again. A material edit without a scene publication is caught by the snapshot-currency check on each hit. A new page increments the revision, so a later frame always sees the new output. The cache lives in renderer fields with a reused list, so it adds no per-frame allocation. The InvalidateFailedPublishedPages side effect still runs on misses and at the page boundaries. The design rule against a global per-row generation does not apply: the revision only invalidates the cache, and the per-renderer proxy version still names the bound.

**Validation.** Unit tests: a new output page in a later frame is seen (todo done condition); a fence failure on the cached published page rejects a hit; a material value change rejects a hit; a bone generation change rejects a hit; the proxy version is unchanged on hits. Live: collect phase and CollectRenderedItemsGpu scope time, in shared and unique mesh modes.

#### Remove Thread.Sleep(1) from TransformHierarchyStore reads and shorten the publication odd window

- Thread: render, collect, update (readers); collect (publication writer); update (Process writers). Estimated saving: 1 ms. Risk: low. Effort: small. Skips unchanged items today: no.
- Related todo item: Shared versioning contract: Give TransformHierarchyStore page-local sequences and a range read
- Locations:
  - `XREngine.Runtime.Core/Scene/Transforms/TransformHierarchyStore.cs:150-195`
  - `XREngine.Runtime.Core/Scene/Transforms/TransformHierarchyStore.cs:540-557`
  - `XREngine.Runtime.Core/Scene/Transforms/TransformHierarchyStore.cs:591-592`
  - `XREngine.Runtime.Core/Scene/Components/Physics/PhysicsChainRigidGpuRestInputCache.cs:116`
  - `XREngine.Runtime.Core/Scene/Components/Physics/PhysicsChainGpuRestInputRange.cs:133`

**Work per item.** Each Read does a volatile sequence read, an alive check, a 64-byte copy, Thread.MemoryBarrier, and a second sequence read. On a retry it calls SpinWait.SpinOnce() with the default threshold, and from the 20th retry each call runs Thread.Sleep(1). That is at least 1 ms and can be up to the system timer period, because the engine never sets the timer resolution. PublishRenderMatrices keeps the one store-wide sequence odd across its full ~24,000-row scan. So any concurrent reader sleeps: render-thread RenderMatrix reads during the pre-collect publication, and update late-tick root.Parent.WorldMatrix reads. Update-thread Process also increments the same sequence about 16 times per chain per tick.

**Calls per frame.** 2 long odd windows per cycle (pre-collect and swap); thousands of reads per cycle across 3 threads

**Estimate reasoning.** Uncertain. One earlier window recorded 1,041,463 contended render-space reads and 803.9 ms of retry time, about 1.8 ms per cycle across threads at about 22 Hz. Each Sleep(1) on the render thread adds at least 1 ms directly to the interval, and the sleeps fall in the p95 tail. I assume about half of the retry time is removed on average.

**Proposal.** (a) In TransformHierarchyStore.Read, call spin.SpinOnce(sleep1Threshold: -1) at both retry sites, so the reader yields and never calls Sleep(1). (b) In PublishRenderMatrices, call BeginWrite/EndWrite for each 64-row chunk of _order inside the existing _gate hold, not once around the full loop, so a reader waits at most one chunk. (c) Optional second step: add a separate _renderSequence for space 2. Increment it in PublishRenderMatrices, in Write(space 2), and in Attach/Detach/EnsureCapacity. Then update-thread local and world writes stop invalidating render-matrix reads.

**Correctness risks.** (a) Correctness does not change. A reader burns CPU, bounded by the shorter windows, instead of sleeping. (b) Each Read already returns one row. A reader can see row A new and row B old during a publication, the same as today across successive reads; every single matrix stays consistent. A future multi-row range read needs page sequences. (c) Every writer of _render, _owners, or _generations must increment _renderSequence, or a reader can see a torn alive check.

**Validation.** Harness: GetReadContentionTelemetrySnapshot for each space. MaxSpinCount must stay below 20 (proof of no Sleep(1)), and RetryTicks per window must drop. Unit tests: a concurrent writer during Read returns an untorn matrix; a stale handle still throws; Read during EnsureCapacity growth succeeds; a chunked publication still publishes every flagged row.

#### Skip matrix-notification work for plain transforms with no subscribers

- Thread: update (Process) and swap/collect (PublishRenderMatrices). Estimated saving: 0.9 ms. Risk: low. Effort: small. Skips unchanged items today: no.
- Related todo item: Physics steady state: Make the bones of fully covered GPU chains dormant on the CPU (partial); Serial swap: Skip unchanged render commands at the swap
- Locations:
  - `XREngine.Runtime.Core/Scene/Transforms/TransformHierarchyStore.cs:373-378`
  - `XREngine.Runtime.Core/Scene/Transforms/TransformHierarchyStore.cs:496-526`
  - `XREngine.Runtime.Core/Scene/Transforms/TransformHierarchyStore.cs:546-555`
  - `XREngine.Runtime.Core/Scene/Transforms/TransformBase.cs:762-776`
  - `XREngine.Runtime.Core/Scene/Transforms/TransformBase.cs:1100-1145`
  - `XREngine.Runtime.Core/Scene/Transforms/TransformBase.cs:1482-1531`

**Work per item.** In Process, each bone takes one more _gate lock in DeferNotification. Later it gets an Interlocked.Exchange and DispatchMatrixNotifications, which reads LocalMatrix/WorldMatrix through the seqlock. Plain Transform overrides no hook, and GPU-driven bones have 0 subscribers. In publication, all ~14,000 changed rows get mask 16 and are added to _changed and dispatched; ~12,000 of them do nothing. DispatchNotifications also runs Interlocked.Exchange on all ~24,000 rows, three times per cycle.

**Calls per frame.** ~14,000 per update tick; ~14,000 per publication with changes (~1.3 per cycle); 3 full-row Interlocked scans per cycle

**Estimate reasoning.** Each publication: ~12,000 x ~30 ns of dispatch with owner cache misses, plus a 24,000-row Interlocked scan, is about 0.5 ms. About 0.35 ms of that is in the serial swap and the rest is in pre-collect. Process sheds about 1-2 ms of _passGate hold time on the update thread, which also shortens the swap wait in the _passGate item.

**Proposal.** Add bool[] _notifyOwner, set at Attach as owner.GetType() != typeof(Transform), because only subclasses can override the hooks. Notify only when Volatile.Read(_subscribers[i]) != 0 || _notifyOwner[i]. Use this test in DeferNotification (return true without the lock when nothing is to be done), in PublishRenderMatrices before '|= 16', and in TransformBase.NotifyMatrixChange for the immediate path. Replace the full _count scan in DispatchNotifications with a notification bitset (ulong[] _notifyDirty, the same pattern as _dirty), scanned word by word.

**Correctness risks.** A handler that subscribes between a change and its dispatch misses that one change. The same window exists today for a subscription that arrives after dispatch. _subscribers is written under _gate in SetSubscribers; read it with Volatile in workers. Telemetry (RecordEvent, RecordRenderMatrixChange) already counts only when handlers exist, so counts do not change. Subclasses such as RigidBodyTransform and UIBoundableTransform keep full notifications.

**Validation.** Unit tests: a plain Transform with 0 subscribers gets no dispatch; a handler added later gets the next change; a subclass that overrides OnWorldMatrixChanged still gets every change; publication still writes _render and the sink for every flagged row. Live: TransformHierarchyStore.Counters PropagationMs and PublicationMs, and phaseTimings swap.

#### Skip the bound query in RenderMatrixChanged handlers of covered meshes

- Thread: swap (collect thread) and collect (pre-collect). Estimated saving: 0.8 ms. Risk: low. Effort: small. Skips unchanged items today: no.
- Related todo item: Change-driven renderer path: Resolve committed bounds once for each renderer in each frame; Physics steady state: Make the bones of fully covered GPU chains dormant on the CPU
- Locations:
  - `XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.Transforms.cs:212-229`
  - `XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.Transforms.cs:263-309`
  - `XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.Transforms.cs:338-343`
  - `XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.Transforms.cs:393-399`
  - `XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.CommittedBounds.cs:117-139`
  - `XREngine.Runtime.Core/Scene/Transforms/TransformHierarchyStore.cs:563-564`
  - `XREngine.Runtime.Rendering/Rendering/RuntimeWorldRenderer.cs:176`

**Work per item.** For each moving chain root (the root is the mesh RootBone): OnRenderMatrixChanged reads RenderMatrix through the seqlock. Then RootBone_WorldMatrixChanged calls UsesCommittedWorldBounds: CurrentLODRenderer, coverage, and one full TryGetCommittedSpatialBounds (~0.3 us), only to return early. The PhysicsChainComponent on the root also gets an empty virtual call. All of this runs inside _passGate, so update-thread Process is blocked for the duration.

**Calls per frame.** 2,000 per publication with root changes; ~1-1.5 such publications per cycle (pre-collect, plus the swap when another update tick finished during collect)

**Estimate reasoning.** 2,000 x 0.3 us x ~1.3 publications per cycle = 0.8 ms. About 0.35 ms of it is in the serial swap, which is on the critical path today. It also shortens the _passGate hold, which reduces update-thread stalls.

**Proposal.** Add 'private bool IsCoveredByReconciledBounds => Volatile.Read(ref _usesCommittedWorldBounds) != 0 && ReferenceEquals(CurrentLODRenderer, Volatile.Read(ref _committedBoundsRenderer));'. Use it in place of UsesCommittedWorldBounds in RootBone_WorldMatrixChanged, Component_WorldMatrixChanged, Component_WorldMatrixPreviewChanged, ApplyImmediateRenderMatrixUpdate, QueueCurrentRenderMatrixUpdate, and ApplyPendingRenderMatrixUpdates. Write _committedBoundsRenderer with Volatile. In the covered-to-uncovered branch of ReconcileCommittedWorldBounds (:130-138), call QueueCurrentRenderMatrixUpdate() after clearing the flag, so skipped root and component motion is seeded again from current matrices. Alternative: unsubscribe the handlers while covered and subscribe again on loss, like TrackBones does.

**Correctness risks.** After a page withdrawal and before the next reconcile (at most one cycle; _committedBoundsStateDirty forces that reconcile), the handlers skip updates. The new reseed in the loss branch restores them, together with the existing RefreshSkinnedCullingIntersectionOverride and RefreshSkinnedCullingBoundsForSceneCulling, before the CPU tree or CPU draw reads them. Today the handler instead sees the withdrawal at once.

**Validation.** Unit test: for a covered mesh, a root render-matrix change makes no bound-provider call. After coverage loss, the pending root matrix is seeded before the next ApplyPendingRenderMatrixUpdates or BeforeAdd. Live: RuntimeWorldRenderer.CollectionTelemetry swap and collect matrix ticks, and phaseTimings swap.

#### Return early from RenderInfo3D.Intersects when the culling volume is null

- Thread: collect. Estimated saving: 0.6 ms. Risk: low. Effort: small. Skips unchanged items today: no.
- Related todo item: Change-driven renderer path: Resolve committed bounds once for each renderer in each frame
- Locations:
  - `XREngine.Runtime.Rendering/Rendering/Info/RenderInfo3D.cs:229-240`
  - `XREngine.Runtime.Rendering/Rendering/Info/RenderInfo3D.cs:178-181`
  - `XREngine.Runtime.Rendering/Rendering/VisualScene3D.cs:707-709`

**Work per item.** The primary shadow collection passes a null volume for members of _gpuBoundsEligibleSet. Intersects still evaluates WorldCullingVolume (one full bound query and ToBox), then classifies against null, which always returns Contains. The result is always true.

**Calls per frame.** 2,000 per cycle (primary shadow view)

**Estimate reasoning.** 2,000 x ~0.3 us of collect phase.

**Proposal.** In RenderInfo3D.Intersects, after the CullingIntersectionOverride branch, add 'if (cullingVolume is null) return true;'. ClassifyWorldCullingVolume(null, ...) returns Contains, so the result does not change for containsOnly true or false.

**Correctness risks.** There is no semantic change. The only side effect lost is the InvalidateFailedPublishedPages() call inside TryGetCommittedSpatialBounds. Reconcile in the same cycle and the page maintenance at the dispatch and acquire boundaries still run it.

**Validation.** Unit test: Intersects(null, false) and Intersects(null, true) return true and do not call a counting committed-bounds provider. Live: CollectRenderedItemsGpu scope time.

#### Reuse the collected sort bound in AddCPU

- Thread: collect. Estimated saving: 0.6 ms. Risk: low. Effort: small. Skips unchanged items today: no.
- Related todo item: Change-driven renderer path: Give the primary directional shadow tile an explicit native GPU consumer (interim reduction)
- Locations:
  - `XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommandMesh3D.cs:696-708`
  - `XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommandMesh3D.cs:714-728`
  - `XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommandMesh3D.cs:1022-1033`
  - `XREngine.Runtime.Rendering/Rendering/Info/RenderInfo.cs:361-362`
  - `XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommandCollection.cs:950-952`

**Work per item.** RenderInfo.CollectCommands calls CollectedForRender, which reads CullingVolume (one bound query) for UpdateRenderDistance. AddCPU then calls CaptureSortDistance, which reads CullingVolume again for the same command and camera.

**Calls per frame.** 2,000 per cycle (primary shadow view)

**Estimate reasoning.** 2,000 x ~0.3 us of collect phase.

**Proposal.** In RenderCommandMesh3D.CollectedForRender, store _collectedSortBounds, _collectedSortDistance, _collectedSortCamera, and _collectedSortStamp (RuntimeRenderingHostServices.FrameTiming.CollectFrameId). CaptureSortDistance(camera, out sortBounds, out fallbackPosition) returns the stored values when the camera reference and stamp match. Otherwise it computes them as it does today.

**Correctness risks.** Both calls run back to back on the collect thread for one command and one camera. The camera key prevents reuse across the shadow and main views in one cycle. The stamp prevents reuse across cycles.

**Validation.** Unit test: CaptureSortDistance returns the same distance, bounds, and fallback with and without a preceding CollectedForRender, and a different camera recomputes. Live: collect phase.

#### Skip the camera-distance reads in BeforeAdd for shadow passes

- Thread: collect. Estimated saving: 0.5 ms. Risk: low. Effort: small. Skips unchanged items today: no.
- Related todo item: Shared versioning contract: Give TransformHierarchyStore page-local sequences and a range read (fewer contended reads)
- Locations:
  - `XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.cs:388-397`
  - `XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.cs:456-457`
  - `XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.cs:467`
  - `XREngine.Runtime.Rendering/Rendering/Camera/XRCamera.cs:1105-1110`
  - `XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.TextureStreaming.cs:21-22`

**Work per item.** There are 3 seqlock reads with full barriers: RootBone.RenderTranslation, camera RenderForward, and camera RenderTranslation. Under update-thread writes they retry and can reach Thread.Sleep(1). In a shadow pass the distance is used only by the ModelRenderDiagnostics log: UpdateLOD is skipped, and ShouldRecordImportedTextureStreamingUsage returns false.

**Calls per frame.** 6,000 store reads per cycle (3 x 2,000 covered meshes)

**Estimate reasoning.** Uncontended cost is about 6,000 x 25 ns = 0.15 ms. These 2,000 root reads per cycle are a likely source of the earlier ~1M contended render-space reads per window (440 cycles x 2,000 is about 0.9M; this is an inference). I assume a share of the ~1.8 ms of retry time per cycle.

**Proposal.** In RenderableMesh.BeforeAdd, compute distance only when !passes.IsShadowPass || ModelRenderDiagnostics.HasActiveTrace. Otherwise pass 0.

**Correctness risks.** None. Shadow passes do not use the distance outside diagnostics.

**Validation.** Harness read-contention snapshot: render-space contended reads per window drop. A capture confirms that shadows are unchanged. Live: CollectRenderedItemsGpu scope time.

#### Do not block the serial swap on update-thread transform propagation

- Thread: swap (collect thread), blocked by update. Estimated saving: 0.5 ms. Risk: medium. Effort: small. Skips unchanged items today: no.
- Related todo item: Serial swap: Skip unchanged render commands at the swap
- Locations:
  - `XREngine.Runtime.Core/Scene/Transforms/TransformHierarchyStore.cs:419-482`
  - `XREngine.Runtime.Core/Scene/Transforms/TransformHierarchyStore.cs:529-575`
  - `XREngine.Runtime.Rendering/Rendering/RuntimeWorldRenderer.cs:172-178`
  - `XREngine.Runtime.Rendering/Rendering/RuntimeWorldRenderer.cs:271-280`
  - `XREngine.Runtime.Host/WorldHost/RuntimeWorldHost.cs:237`
  - `XREngine.Runtime.Host/WorldHost/RuntimeWorldHost.cs:263-264`

**Work per item.** This is a whole-call wait, not per item. The swap's ApplyRenderMatrixChanges calls PublishRenderMatrices, which waits on _passGate for the whole update-thread Process: 14,000 transforms with 2-6 contended _gate acquisitions each across 4 workers, plus DispatchNotifications callbacks.

**Calls per frame.** 1 swap publication per cycle (the pre-collect publication also waits, off the critical path today)

**Estimate reasoning.** With a Process duration D of about 3-5 ms (update iteration ~30 ms, late tick ~24 ms) and an update period T of about 30 ms, the mean wait is D squared over 2T, about 0.15-0.4 ms. About 10-15% of swaps wait up to D, which is directly in the interval p95, so the p95 effect is larger than the mean.

**Proposal.** Add PublishRenderMatrices(bool waitForPropagation). When it is false, use Monitor.TryEnter(_passGate). If Process holds the gate, return 0, keep the _publication flags, and increment a skipped-publication counter. Call it with false from RuntimeWorldRenderer.GlobalSwapBuffersCore. Keep true in GlobalPreCollectVisible, so collection always sees a complete tick.

**Correctness risks.** When the swap skips, the frame uses the render matrices published at pre-collect, which is the same state that collection used, so the CPU render state stays coherent. Non-covered objects get at most one more update tick of latency in those frames. The pending rows are published at the next pre-collect. Monitor.TryEnter is re-entrant, so same-thread re-entry is unchanged. Covered GPU chains take their pose from the GPU and are not affected.

**Validation.** Unit test: block Process inside a worker callback; PublishRenderMatrices(false) returns 0 at once and keeps the flags; the next PublishRenderMatrices(true) publishes them. Live: add a _passGate wait-time counter; phaseTimings swap mean and interval p95.

#### Remove the second full CanUseCanonicalGpuCollection per mesh in the main view

- Thread: collect. Estimated saving: 0.5 ms. Risk: medium. Effort: small. Skips unchanged items today: no.
- Related todo item: Change-driven renderer path: Keep fully covered renderers in a persistent GPU-culled draw set
- Locations:
  - `XREngine.Runtime.Rendering/Rendering/VisualScene3D.CanonicalCollection.cs:99-101`
  - `XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.CanonicalCollection.cs:54-93`
  - `XREngine.Runtime.Rendering/Rendering/VisualScene3D.cs:699-706`

**Work per item.** One HashSet Contains, ~25 condition checks (settings, material type, render options, uniform handlers), loops over textures, passes, and render commands, one bound query, and two publisher dictionary lookups (FindRegistration and FindPlannedCommand).

**Calls per frame.** 2,000 per cycle (main view)

**Estimate reasoning.** The non-bound part is about 0.25 us x 2,000. The bound part is counted in the cache item.

**Proposal.** In RefreshCoveredCommandSources, record per mesh the collect frame id, the renderer, the material, and the material's BindingLayoutVersion, ShaderStateRevision, and UberStateRevision when the mesh is canonical. In IsCollectedByCanonicalGpu, check set membership and a cheap volatile-input check: same stamp, !_rc._dirty, !HasPendingRenderMatrixUpdate, IsCoveredByReconciledBounds, same CurrentLODRenderer, and same material versions. The GPUScene canonical registration changes only at the swap on the same thread, so it cannot change between pre-collect and main collection.

**Correctness risks.** Another thread can change inputs between pre-collect and main collection: textures, render options, highlight bits, ForceCpuRendering, Instances, or callbacks. Confirm that each input either marks _rc dirty or changes a captured version; add any that do not. A missed input skips CPU collection for one frame, and the canonical publication then draws or rejects the mesh with the newer state.

**Validation.** Unit tests: changing a material texture, highlight bits, or ForceCpuRendering between pre-collect and main collection routes the mesh to CPU collection in the same frame. Live: collect phase.

#### Merge the two per-frame skinned-mesh passes in GlobalCollectVisible

- Thread: collect. Estimated saving: 0.3 ms. Risk: low. Effort: small. Skips unchanged items today: no.
- Related todo item: Change-driven renderer path: Keep fully covered renderers in a persistent GPU-culled draw set
- Locations:
  - `XREngine.Runtime.Rendering/Rendering/VisualScene3D.cs:265-284`
  - `XREngine.Runtime.Rendering/Rendering/VisualScene3D.cs:622-638`
  - `XREngine.Runtime.Rendering/Rendering/VisualScene3D.CanonicalCollection.cs:39-83`
  - `XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.CanonicalCollection.cs:19-44`

**Work per item.** The code enumerates the HashSet<RenderableMesh> two times. It runs Clear and re-Add on _gpuBoundsEligibleRenderables, _gpuBoundsEligibleSet, _coveredCommandSources, and _canonicalGpuCollectionSet (2 HashSet adds per mesh). It does a no-op _committedCpuTreeMembers.Add hash, and RefreshCoveredCommandSource does ~10 no-op SetField writes and 3 PassSet lookups. SetCoveredShadowCasterLayerMask does one Interlocked.Exchange.

**Calls per frame.** 2 x 2,000 per cycle

**Estimate reasoning.** About 0.1-0.15 us per mesh of duplicated iteration and set rebuild, x 2,000.

**Proposal.** Compute eligibility (HasCompleteGpuDrivenBoneCoverage) and coverage in one loop over _skinnedMeshes. Keep the four collections persistent and apply membership changes only on a transition (add when covered or canonical, remove on loss), so steady-state frames do no set writes. A second step skips RefreshCoveredCommandSource when a per-mesh covered-source revision is unchanged; every input mutation path must increment that revision.

**Correctness risks.** Merging the loops and diffing membership keeps the same per-frame decision, so it does not affect correctness. The revision skip has the missed-mutation risk that the design notes for event-driven lists; land it only with tests for each input.

**Validation.** Unit tests from the todo item: entry, removal, and coverage loss keep the four collections correct. Live: GlobalPreCollectVisible scope time.

#### Batch transform propagation per range (one _gate hold and one sequence bump per range)

- Thread: update (Process); couples to swap through _passGate and to readers through _sequence. Estimated saving: 0.3 ms. Risk: medium. Effort: medium. Skips unchanged items today: no.
- Related todo item: Shared versioning contract: Give TransformHierarchyStore page-local sequences and a range read
- Locations:
  - `XREngine.Runtime.Core/Scene/Transforms/TransformHierarchyStore.cs:249-279`
  - `XREngine.Runtime.Core/Scene/Transforms/TransformHierarchyStore.cs:301-325`
  - `XREngine.Runtime.Core/Scene/Transforms/TransformHierarchyStore.cs:419-494`
  - `XREngine.Runtime.Core/Scene/Transforms/TransformBase.cs:1100-1145`
  - `XREngine.Runtime.Core/Scene/Transforms/TransformBase.cs:1195-1235`
  - `XREngine.Runtime.Execution.Threads/TransformPropagationWorkers.cs:25-49`
  - `XREngine.Editor/Unit Tests/Default/UnitTestingWorld.Toggles.cs:476`

**Work per item.** Each chain (root and 6 bones) takes about 18 _gate acquisitions and about 16 Interlocked increments of the shared _sequence. 4 worker threads contend on one Monitor and one cache line.

**Calls per frame.** 2,000 ranges / 14,000 transforms per update tick

**Estimate reasoning.** On the critical path, the effect is indirect: a shorter _passGate window and fewer reader retries. On the update thread, my inference is 2-3 ms less Process time per tick (about 36,000 contended Monitor handoffs at about 100 ns each).

**Proposal.** First, measure Process with RecalcChildMatricesType Sequential and Asynchronous at 2,000 chains (TransformHierarchyStore.Counters PropagationMs); with this lock layout one thread can be faster than the pool. Then add TransformHierarchyStore.ProcessOrdinaryRange for ranges in which every owner is exactly Transform, not ForceManualRecalc, and has no _worldOverride. Compute local matrices outside the lock (CreateLocalMatrix), then write local and world matrices and set notification bits for the range under one _gate hold and one BeginWrite/EndWrite pair. Keep the per-transform path for all other types and for ERenderMatrixUpdateMode.ForceSynchronous.

**Correctness risks.** _order keeps parents before children inside a range. Cross-thread Write calls stay excluded by _gate. Synchronous render-matrix mode must take the existing path. The notification masks must match the per-transform path.

**Validation.** Unit tests: range propagation equals per-transform propagation for random hierarchies, and a concurrent Write during Process is safe. Live: PropagationMs, read-contention telemetry, and interval p95.

#### Late-tick dependency checks and IsActiveInHierarchy walks (update thread only)

- Thread: update. Estimated saving: 0.2 ms. Risk: medium. Effort: medium. Skips unchanged items today: no.
- Related todo item: Physics steady state: Replace per-tick rest dependency checks with versioned dependency sets; Replace per-tick IsActiveInHierarchy walks with a world-owned active flag
- Locations:
  - `XREngine.Runtime.Core/Scene/Components/Physics/PhysicsChainWorld.GpuInputs.cs:69-163`
  - `XREngine.Runtime.Core/Scene/Components/Physics/PhysicsChainWorld.GpuInputs.cs:187-257`
  - `XREngine.Runtime.Core/Scene/Components/Physics/PhysicsChainComponent.GpuInputs.cs:25-74`
  - `XREngine.Runtime.Core/Scene/Components/Physics/PhysicsChainComponent.GpuInputs.cs:132-232`
  - `XREngine.Runtime.Core/Scene/Components/Physics/PhysicsChainWorld.cs:539-725`
  - `XREngine.Runtime.Core/Scene/Components/Physics/PhysicsChainWorld.QualityBudget.cs:197-261`
  - `XREngine.Runtime.Core/Scene/Components/Physics/PhysicsChainWorld.ActivityDiagnostics.cs:61-85`
  - `XREngine.Runtime.Core/Scene/SceneNode.cs:352-361`
  - `XREngine.Runtime.Core/Scene/Components/Physics/PhysicsChainComponent.GPU.cs:234-245`

**Work per item.** Each chain in each late tick: about 13 IsActiveInHierarchy recursions of about 5 levels each through the virtual Parent getter (1 quality, 5 prepare, 3 capture, 1 component loop, about 2 collider append, 1 activity). The opaque check walks about 47 parent steps with GetType. Owner marks do about 30 lookups in a 14,000-entry ReferenceEquality dictionary, and the collider dependency check about 16. There are RuntimeSlot struct copies. The full particle pack runs because colliders disable the fast path, with 2 AppendGpuCollider calls and collider hashing. root.Parent.WorldMatrix is a store read that can hit the publication odd window. Sampled cost is 5.8 us per chain for rest gathering alone.

**Calls per frame.** 2,000 per late tick (~33 Hz at 2,000 chains)

**Estimate reasoning.** The update thread has no wait edge into collect, swap, or render, so the critical-path gain is only indirect (CPU and turbo budget, fewer store reads). On the update thread it saves 8-11 ms of the 23.8 ms late tick, which raises the animation and input rate.

**Proposal.** Smallest safe step: add a world-level ulong HierarchyDependencyEpoch. Increment it in TransformHierarchyStore.HierarchyChanged, Attach, and Detach, in the SceneNode.IsActiveSelf and XRComponent.IsActive setters, and on PhysicsChainComponent collider-list or particle-version changes. Cache per PhysicsChainGpuRestInputRange the opaque flag, the forced-compatibility membership, and the collider dependency, together with the epoch, and skip the walks when the epoch is equal. Compute one per-slot active flag in the first late-tick loop and reuse it in all later stages of that tick. Replace the epoch with per-page versions when the shared page-version type exists.

**Correctness risks.** The epoch is conservative: any change anywhere recomputes all rows, so it is correct but falls back to today's cost under constant hierarchy churn. Design rule 2 asks for local versions; record this as an interim step. The per-tick active flag assumes no activation change inside the late tick; confirm that PhysicsChainWorld.BeginNodeActivationMutation defers such changes.

**Validation.** The unit tests listed in those todo items (child transform, child order, reparent, collider membership, collider transform, ownership, and activation of the chain, an ancestor, and a collider). Live: late-tick stage telemetry with XRE_WORLD_TICK_TELEMETRY and the update phase.

### Notes

IS UPDATE ON THE CRITICAL PATH? Not by a wait edge. The update thread loops on its own. Collect waits only on _renderDone, and render waits only on the publication. So the update rate sets the animation and input rate, not the completed Hz. The update thread couples to the frame only indirectly:
(1) TransformHierarchyStore._passGate. Both PublishRenderMatrices calls (pre-collect and the serial swap) wait for the whole update-thread Process (an estimated 3-5 ms at 2,000 chains). That is a mean of about 0.15-0.4 ms on the swap, with tail hits of up to the full Process time.
(2) The single _sequence seqlock. Update-thread writes make collect-thread and render-thread reads retry, and SpinWait escalates to Thread.Sleep(1). The collect thread's own publication keeps the sequence odd for its full scan.
(3) _outputPageSync and the other dispatcher locks. These are shared between the render-thread physics dispatch and the about 21,000 bound queries per cycle on the collect thread. The physics-frozen ablation (collect -3.4 ms, swap -3.6 ms) fits this channel better than a direct update effect. This is an inference, not a measurement.
(4) Shared CPU and turbo budget on the laptop: the update thread, 4 propagation workers, and the fixed thread run beside collect and render.
At a 100 Hz render rate, the update iteration of about 30 ms would advance root motion and physics inputs at about 33 Hz. If the target needs an input on every frame, the update-thread items become required, not optional.

CRITICAL-PATH CONTEXT. Cycle = max(collect, render callback) + swap. Collect (about 23 ms) is below render (about 29 ms), so collect savings become frame time only after the render items land. Swap savings count at once: in this area, the root handler bound query, notification filtering, and not waiting on _passGate in the swap. For 100 Hz, collect must reach about 6 ms. The items here remove an estimated 10-11 ms of collect if all of them land, including the GPU primary shadow consumer. About 8 ms of collect lies in scopes I did not attribute: Lights3DCollection, XRViewport.CollectVisible overhead, and _outputPageSync waits.

ESTIMATE METHOD. One bound query costs about 0.3 us uncontended, derived from the 6.17 ms CollectRenderedItemsGpu residual for about 6 queries per mesh. Estimates for overlapping items are incremental, in rank order: the revision-keyed cache saves about 1.4 ms after the five single-site items, or about 4.7 ms if done alone. The _passGate item and the seqlock item mainly reduce the interval p95, which mean-based ms estimates undercount.

MEASUREMENT HOOKS THAT ALREADY EXIST:
- RuntimeWorldRenderer.CollectionTelemetry (matrix, mesh, and scene ticks for pre-collect and swap; requires XRE_WORLD_TICK_TELEMETRY at startup).
- TransformHierarchyStore.Counters (PropagationMs, PublicationMs).
- TransformHierarchyStore.GetReadContentionTelemetrySnapshot (MaxSpinCount of 20 or more shows that Sleep(1) ran). The harness already collects this.
- Harness phaseTimings.
Recommended first live check: record MaxSpinCount for each space and PropagationMs in a 2,000-chain window, with RecalcChildMatricesType Sequential and with Asynchronous.

The first nine items are each about a day or less and independent, except that the per-mesh canonical recheck item reuses IsCoveredByReconciledBounds from the root-handler item. I edited, built, and ran nothing. All counts come from reading the code at HEAD beb7d7896.
