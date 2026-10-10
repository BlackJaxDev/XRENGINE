# Branch Integration TODO

Last Updated: 2026-10-09
Status: Merge resolved and built; not committed. Waiting for user decisions.
Architecture: [Runtime organization](../../../architecture/runtime/project-organization.md), [Physics chain world runtime](../../../architecture/physics/physics-chain-world-runtime.md), [Mesh submission contracts](../../../architecture/rendering/mesh-submission-strategies.md)
Validation: [Branch integration validation](../../testing/runtime/branch-integration-validation.md)
Resume state: [Branch integration handoff](../../progress/runtime/branch-integration-handoff.md)

## Current State

Local `branch-integration` has the physics merge `ce54e3c9b` and an uncommitted, fully resolved merge of `browser-library-boundary` at `fb827fb5a`. Editor, Server, VRClient, and unit-test builds have zero warnings and zero errors. Focused tests show one new failure, which needs a test-code change. Desktop OpenGL and Vulkan runs match the physics baseline. The items below remain open.

## Open Code Items

- [ ] Update `XREngine.UnitTests/Rendering/AdvancedGpuSceneIdentityCacheFixture.cs` (`PublishWithMissingSource`). It reads the private `GPUScene._commandIndexLookup` by reflection as a two-element tuple. The merged lookup has a third element, `GpuSceneMeshCommandSnapshot`. Done when both `MissingOrNullSourceClearsIdentityAndRecovers` cases pass. This is a test change; get user clearance first.
- [ ] Defer or reject `RuntimeWorld` disposal requested from another world's physics tick, batch worker, or scene-mutation lease. `PhysicsChainWorld.PrepareWorldDisposal` defers only for the world's own tick or worker; other callers block on its tick gate, and `EndPlay` then fails in `BeginMutation` after admission is closed. Done when such a request either fails before admission closes or runs after the outer tick or lease ends.
- [ ] Defer teardown when `PhysicsChainWorld.Dispose` runs on a thread that holds the tick gate at depth 0 (mutation lease, `ReleaseForTransfer`, `TryCaptureReadbackSource`). Done when the outer frame never continues on cleared world state.
- [ ] Run deferred owner-world disposal (`PhysicsChainWorld.DisposeOwnerWorld`) after the world's tick-queue dispatch completes, not inside it. Done when no later callback in the same tick queue runs on a torn-down world.
- [ ] Republish GPU renderer routes when only the accepted snapshot renderer changes. `GPUScene.CommandConversion.cs` updates `_commandIndexLookup` but does not mark the routes dirty when `snapshot.Renderer` changes and all other data is the same. Done when `PublishRendererCommandIndices` runs for that change.
- [ ] Confirm the morph version check for physics-covered submeshes in `AdvancedGpuDeformationResources.TryGetOrAddPoseCore`. The retained envelope uses the renderer-wide `BlendshapeWeightsVersion`, and the pose slice uses the per-mesh `inputs.MorphVersion`. Done when a submesh with blendshapes and retained physics weights is accepted, or the rejection is explicit and documented.

## Decisions Needed

- Commit the browser merge on `branch-integration`, and when to promote it to `master`. No commit or push was made for the browser merge.
- Browser source-row capture (`AdvancedBrowserGeometryBasisSource`, `AdvancedBrowserUberAttributeSource`) also runs on desktop. It is cached per mesh revision, but it keeps CPU data that only WebGPU reads. Decide whether to guard it.
- The browser `CreateRenderState` takes desktop `RenderState.CullMode` from `renderOptions.CullMode` (default Back), not from the double-sided flag. No desktop GLSL consumer was found. Decide whether this desktop change is intended.
- Review the remaining old S08 index-preparation and octahedral capture patches for unique fixes before selecting further implementation work. See the handoff for exact refs.
- Keep deployment changes and dependency/submodule updates separate. Confirm their current remote state and applicable approval requirements before changes.

## Out Of Scope

- Completion of the entire WebGPU backlog as a prerequisite for integration.
- Deployment or release promotion during the integration.
