# Runtime Modularization Phase 3 - Complete

Reference design: [runtime-modularization-plan.md](../design/runtime-modularization-plan.md)

Rendering follow-on: [runtime-modularization-phase4-todo.md](runtime-modularization-phase4-todo.md)

Progress and validation record: [runtime-modularization-phase3-progress-2026-07-19.md](../progress/runtime/runtime-modularization-phase3-progress-2026-07-19.md)

Completed: 2026-07-22

## Goal

Carve the host-independent runtime kernel and its lower physics, gameplay, transform,
prefab-metadata, settings-contract, and host-service prerequisites out of the legacy
`XRENGINE` dependency sink without creating a `Runtime.Core -> Runtime.Rendering`
edge.

Phase 3 intentionally keeps `XRENGINE` as the compatibility/composition facade. The
reference design assigns rendering ownership to Phase 4, subsystem adapters to Phase
5, and final facade deletion or reduction to Phase 6. Earlier revisions of this
tracker incorrectly repeated those later-phase gates as Phase 3 work.

## Completion Checklist

### Preparation and graph

- [x] Complete the physical ownership, dependency, package, native-content, generated-source, and application-reference inventory.
- [x] Record the Runtime.Core and integration-layer ownership graph and close the animation/audio validation debt.
- [x] Keep `XREngine.Runtime.Core` limited to project references on `XREngine.Data` and `XREngine.Extensions`.

### Runtime.Core carve-out

- [x] Move backend-neutral physics contracts, authoring data, rigid-body values, joints, geometry inputs, replication policy, and world/thread dispatch seams to Runtime.Core.
- [x] Move Jolt, Jitter2, and the complete non-rendering PhysX scene/backend/controller/actor/joint/geometry implementation to Runtime.Core.
- [x] Move CharacterControllerComponent, StaticRigidBodyComponent, runtime collider primitives, and dependency-ready gameplay physics ownership to Runtime.Core.
- [x] Keep only the renderer-owned PhysX instanced diagnostic visualizer in the facade for Phase 4.
- [x] Move dependency-independent movement, networking, volume, interaction, scripting, spline, prefab-metadata, transform, game-mode, and pawn contracts to their lower runtime owners.
- [x] Move VR/model height scaling and optional input-set ownership to Runtime.InputIntegration.
- [x] Move editor-only editing/debug ownership to Editor and classify the remaining renderer-bound components for Phase 4.
- [x] Move value/runtime settings contracts, reflection/type metadata, serialization contracts, lifecycle enums, networking values, and generic runtime tools to Runtime.Core.
- [x] Transfer Jolt, Jitter2, MagicPhysX, and the MagicPhysX native runtime ownership required by Runtime.Core.

### Host-service prerequisites

- [x] Extract lower timing, thread dispatch, transform, physics, input, maintenance, animation, audio, networking, scene-streaming, world-object, scene-node, pawn, player-controller, and VR seams.
- [x] Migrate completed lower-runtime callers away from direct legacy Engine access.
- [x] Remove the direct `Runtime.AudioIntegration -> XRENGINE` and `Runtime.AnimationIntegration -> XRENGINE` edges.
- [x] Preserve the legacy Engine implementation only as a composition adapter for capabilities still owned by Phases 4 through 6.

### Validation and closeout

- [x] Restore the solution after package-ownership changes and validate Runtime.Core with zero warnings.
- [x] Validate the transitional XRENGINE facade after the PhysX move and namespace migration with zero warnings.
- [x] Run the targeted Core/PhysX/Jolt/geometry/serialization/replication/timing/boundary matrix (73/73 passed).
- [x] Start an isolated Editor session, verify the MCP endpoint responds, review startup logs, and stop the owned session cleanly.
- [x] Verify the Core project graph contains no Runtime.Rendering, XRENGINE, Editor, application, or integration-project reference.
- [x] Update the physics architecture guide, reference design status, Phase 4 handoff, and durable progress record.
- [x] Reclassify every remaining facade-owned file and dependency under its owning Phase 4, Phase 5, or Phase 6 gate.

## Later-Phase Handoff

The following work is deliberately not a Phase 3 completion condition:

| Ownership | Remaining work |
|---|---|
| Phase 4 | Rendering/UI/function graphs, render-world split, GPU physics and physics-chain presentation, renderer-bound movement/pawn/input components, rendering settings/import bridges, PhysX instanced diagnostics, and rendering Engine partials. |
| Phase 5 | Feature-specific animation/audio/input/modeling/VR adapter cleanup that cannot live in the stable Core or Rendering kernels. |
| Phase 6 | Migrate Bootstrap and application/tool/test consumers off the compatibility facade, transfer its final package/content ownership, then delete or deliberately re-scope `XRENGINE`. |

Branch integration is a repository workflow action, not an architecture acceptance
criterion. It should be performed when the complete working tree is intentionally
committed and reviewed; Phase 3 does not merge or commit unrelated local work.
