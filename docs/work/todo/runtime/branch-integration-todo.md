# Branch Integration TODO

Last Updated: 2026-10-09
Status: Paused at the user's request.
Architecture: [Runtime organization](../../../architecture/runtime/project-organization.md), [Mesh submission contracts](../../../architecture/rendering/mesh-submission-strategies.md)
Validation: [Branch integration validation](../../testing/runtime/branch-integration-validation.md)
Resume state: [Branch integration handoff](../../progress/runtime/branch-integration-handoff.md)

## Current State

`master` has the branch naming rule and this handoff. Local `branch-integration` at `0011f22c5` contains the physics and prefab merge, but no WebGPU merge. Its Editor build passed; desktop runtime validation is incomplete. The browser candidate is `browser-library-boundary` at `fb827fb5a`, which includes WebGPU at `ff0eabf95`. A merge preview found 28 conflicting files. The user accepts unfinished browser work only if desktop behavior is preserved.

## Open Code Items

- [ ] Combine `PhysicsChainCpuWorkScheduler` with the browser `IPhysicsChainCpuWorkerGroup` and `DesktopPhysicsChainCpuWorkerGroup` provider boundary. Preserve fixed worker counter slots, captured exceptions, and completion signaling in `finally`. Done when the merged scheduler retains desktop telemetry and exception handling without placing native workers in the browser closure.
- [ ] Combine `PhysicsChainWorld`, its readback extensions, and the browser disposal partial. Preserve nonblocking cross-world readback admission, `SourceBusy`, deferred transfers, deferred structural changes, and runtime graph cleanup. Retain both outer-tick and executing-worker ownership checks. Done when disposal cannot wait on its own worker or attach a deferred transfer to a disposed destination. Request focused concurrency review of the final diff.
- [ ] Unify `RenderFrameViewDescriptor.CullingLayerMask` and browser `CameraCullingMask`. Retain `CameraOrthographicSize`. Done when all producers and consumers use one frozen camera mask and no path silently uses a default mask.
- [ ] Combine `GPUScene` publication and command snapshots. Preserve renderer routes and material versions published at command-buffer swap; use the accepted snapshot renderer. Inspect `IsCommandOwnedByGpuAabb` and `IsCommandOwnedByGpuCopiedAabb` for mutable live-mesh reads. Done when bounds ownership follows the accepted command snapshot.
- [ ] Combine `AdvancedGpuScenePublisher`, its material transitions, deformation rows, and cache keys. Retain physics GPU bounds, material snapshots, acceptance ordering, and identity delivery. Preserve browser `EngineSurface`, `UberBaseSurface`, `NativeVertex`, render options, and raster-state hashing. Done when initial publication and `TryReuseUnboundMaterialPlan` retain the same required companion metadata and binding counts.
- [ ] Resolve the remaining conflicts in `EngineTimer`, `RuntimeEngine`, `RenderCommandMesh3D`, `RenderFrameViewSetCapture`, deformation preparation, `VisualScene3D`, `XRMeshRenderer`, `XRViewport`, `ShaderHelper`, and `RenderableMesh`. Done when both branches' contracts remain represented, conflict markers are absent, and the targeted builds pass. Do not select one whole side across these files.
- [ ] Inspect automatically merged world-lifetime and rendering files as part of the same integration. Done when the review includes `PhysicsChainWorld.Disposal.cs` and the publication/consumer paths adjacent to the explicit conflicts.

## Decisions Needed

- Retain browser-only limitations in the existing unified-runtime TODO. Do not mark desktop checks complete from browser software-rendering results.
- Review the remaining old S08 index-preparation and octahedral capture patches for unique fixes before selecting further implementation work. See the handoff for exact refs and limits of the completed inventory.
- Keep deployment changes and dependency/submodule updates separate. Confirm their current remote state and applicable approval requirements before changes.

## Out Of Scope

- Completion of the entire WebGPU backlog as a prerequisite for integration.
- Deployment or release promotion during the paused integration.
