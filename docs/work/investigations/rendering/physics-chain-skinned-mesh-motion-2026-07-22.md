# Physics-chain skinned-mesh motion failure

## Status

Resolved in the current working tree on 2026-07-23. The user's failed runtime
report was reproduced, its additional lifecycle/recording defects were fixed,
and fresh Vulkan, OpenGL, repeated Play/Edit, and RenderDoc validation now
passes.

Latest user confirmation remains the earlier failed report; the corrective
result below is agent-validated and ready for the next user run.

## Corrective validation

The failure after the initial closeout was not a physics-kernel parity gap. It
was a combined restoration, arena-reuse, and Vulkan command-buffer lifetime
failure:

1. `XRComponent.World` changed before the old active component was
   deactivated, allowing its physics-chain registration to survive edit/play
   restoration.
2. The restored component shared a persistent ID with that stale object.
   Runtime-slot and arena allocation logic treated it as new work instead of
   replacing the stale identity.
3. The empty resident arena retained its allocator high-water marks, so
   repeated transitions grew capacity from `64` to `128` to `256` despite an
   unchanged chain.
4. Ordinary descriptor-set writes updated sets referenced by reusable Vulkan
   command buffers. The old recordings were subsequently submitted and
   triggered `VUID-vkQueueSubmit2-commandBuffer-03874`.
5. Vulkan 1.2 buffer-device-address selection could enable redundant core,
   KHR, and EXT forms during startup.

The implemented correction:

- deactivates a component before replacing its old world reference and makes
  the operation idempotent;
- only registers active, attached components, evicts stale same-ID identities,
  and preserves/reuses their runtime slot and resident arena slice;
- rewinds all empty-arena allocator/upload watermarks while retaining the
  physical buffers;
- publishes exact descriptor-set content generations, tracks the primary and
  secondary command buffers that reference each ordinary set, invalidates
  those dependents after both ordinary and template writes, and re-records
  them before submit;
- normalizes buffer-device-address extension selection against the Vulkan API
  version; and
- resolves MCP component requests on their specified node before consulting
  the global persistent-ID cache.

Fresh Vulkan StandardValidation sessions:

- `Build/_AgentValidation/mcp-sessions/physics-chain-vulkan-descriptor-fixed-0723/`
  completed 12 Play/Edit cycles with slot `0`, generation `5` through `27`,
  no VUID, rejected submit, or Vulkan error. Zero-readback counters remained
  `3748/3748` while simulation continued. Sync To Bones completed
  `1890/1890` readbacks with at most two frames of latency and zero fence,
  read, stale, enqueue, or dispatch failures.
- `Build/_AgentValidation/mcp-sessions/physics-chain-vulkan-arena-final-0723/`
  completed another 12 Play/Edit cycles with capacity `1960`, live bytes
  `1744`, deferred bytes `0`, growth count `0`, and resource generation `12`
  unchanged on every cycle.

Fresh OpenGL parity session:

- `Build/_AgentValidation/mcp-sessions/physics-chain-opengl-parity-final-0723/`
  produced the same `1960/1744/0`, zero-growth arena and GPU-authored indirect
  active-work diagnostics. Zero-readback remained at zero CPU bytes and zero
  failures; Sync To Bones completed `897/897` readbacks with zero failures.

Visible current captures:

- Vulkan zero-readback:
  `Build/_AgentValidation/physics-chain-test-repair/mcp-captures/Screenshot_20260723_064434_587_5970debf6a04409b9d81f6edbab7ce52.png`;
- Vulkan Sync To Bones:
  `Screenshot_20260723_064629_274_7dcadebb4a6a4e59b7d523989c5f8464.png`
  and
  `Screenshot_20260723_064631_577_6eef3764be484b2fbcd4fec559a080f0.png`;
- OpenGL zero-readback:
  `Screenshot_20260723_065446_587_eba8d045a3934e60952d4720fd892a3c.png`
  and
  `Screenshot_20260723_065456_367_809e688ef1e9482e830635750605c3dd.png`;
- OpenGL Sync To Bones:
  `Screenshot_20260723_065517_481_d34c79f912e34d168e7d03dc815bca5c.png`
  and
  `Screenshot_20260723_065525_383_4c7f44a80df74d2784ecba192d252c2c.png`.

All paired captures were visually inspected and show changing/deformed poses.

Fresh current-code RenderDoc evidence:

- capture:
  `Build/_AgentValidation/physics-chain-test-repair/renderdoc/corrective-final-20260723/vulkan-physics-chain-active-final.rdc`;
- Vulkan, 115 events, six compute dispatches;
- three direct setup/solver dispatches, ordered memory barriers,
  `PhysicsChain.IndirectDispatch` at EID 63 with
  `vkCmdDispatchIndirect(<1,1,1>)`, then bounds and palette dispatches;
- exported indirect arguments begin with three 32-bit values `(1,1,1)`; and
- `rdc assert-clean --min-severity info` passed with zero capture messages.

The RenderDoc frame's editor camera did not frame the chain, so visual proof
comes from the MCP pairs above. RenderDoc is used for current command ordering,
indirect arguments, and capture-message proof.

Final automated validation:

- corrective focused cohort: 162 passed;
- complete `FullyQualifiedName~PhysicsChain` filter: 350 passed;
- `XREngine.Runtime.Rendering.csproj`: zero warnings and zero errors;
- `XREngine.Editor.csproj`: zero warnings and zero errors.

OpenXR/OpenVR hardware remained unavailable and is not claimed.

## Problem

Both skinned-mesh physics-chain scenarios in the Math Intersections world rendered a displaced, collapsed-looking prism instead of a mesh that followed the simulated chain:

- `Physics Chain GPU Dispatcher Skinned Mesh Test`
- `Physics Chain GPU Dispatcher Skinned Mesh Sync To Bones Test`

The particle/debug-chain output moved correctly, which isolated the defect to skin-palette publication and test setup rather than physics simulation.

## Investigation evidence

Initial viewport captures were taken with the local Vulkan setting:

- `Build/_AgentValidation/20260722-physics-chain-skinning/mcp-captures/Screenshot_20260722_103829_147_3454d8e0ce5a427c9d0cae27fc1df28f.png`
- `Build/_AgentValidation/20260722-physics-chain-skinning/mcp-captures/Screenshot_20260722_103853_589_113292cf00374d3b899a95e6288c54fc.png`

Those captures were not used as behavioral proof: `log_physics.log` reported that batched physics-chain simulation is not implemented by `VulkanRenderer`, so the requested GPU work remained pending. The source-level matrix and renderer-ownership contracts identified the faults, and the final behavior was validated with OpenGL, where this dispatcher is implemented.

The original OpenGL investigation did not have `rdc`. The Vulkan parity follow-up
installed/verified `rdc-cli` 0.5.6 with RenderDoc 1.44, repaired the malformed
per-user Vulkan layer manifest (the original is preserved under the parity run
root), and completed a native Vulkan capture.

## Root causes

1. `PhysicsChainBonePalette.comp` read particle transform matrices with GLSL's default column-major storage. A `System.Numerics.Matrix4x4` therefore arrived as the column-vector transpose used by the shader's direction math. The shader then multiplied that matrix directly by an explicitly `row_major` inverse-bind matrix. This mixed two matrix conventions in one product and produced an invalid final skin palette.
2. The Math Intersections prism built its mesh-local inverse bind as `boneInverseWorld * visualParentWorld`. The engine's row-vector contract requires `visualParentWorld * boneInverseWorld`. Translation-only layouts can mask the ordering defect, but rotated or otherwise non-commuting parent transforms cannot.
3. The Sync To Bones scenario still registered all referenced bones as GPU-driven. `XRMeshRenderer` intentionally ignores CPU palette dirties for GPU-driven bone indices, so the supposed CPU transform-updated skinning scenario continued consuming GPU-published palette data.

## Fix

- Convert the shader's reconstructed column-vector bone world matrix back to the engine's row-vector matrix before composing `inverseBind * boneWorld`.
- Compose the test mesh's inverse bind in mesh-local-to-world-to-bone order.
- Add `PhysicsChainComponent.UseGpuDrivenSkinning`. Disabling it clears renderer ownership and prevents subsequent GPU palette registration.
- Configure the Sync To Bones skinned scenario with GPU-driven skinning disabled, leaving the zero-readback scenario enabled.
- Add regression coverage for the shader source contract, the palette ownership toggle, and a rotated-parent bind pose.

## Validation ledger

- Build through the final focused test invocation: succeeded with 0 errors. Two unrelated `VPRC_SurfelGIPass` unassigned-field warnings were present in the concurrently modified worktree.
- Exact regression tests for shader composition, renderer ownership, and rotated-parent inverse bind: 3 passed.
- Broader selected physics-chain classes: 29 passed and 2 unrelated shader-contract tests failed because their string expectations no longer match the current dispatcher and `SkipUpdateParticles.comp` sources.
- OpenGL zero-readback scenario: `UseGpuDrivenSkinning=true`, `GpuSyncToBones=false`; the mesh deforms and follows the chain between:
  - `Build/_AgentValidation/20260722-physics-chain-skinning/mcp-captures/Screenshot_20260722_110522_479_e789d9ddee4b45d3a884bb965b6936ff.png`
  - `Build/_AgentValidation/20260722-physics-chain-skinning/mcp-captures/Screenshot_20260722_110523_866_62cf46fecc664096b4badcc9969a13b5.png`
- OpenGL Sync To Bones scenario: `UseGpuDrivenSkinning=false`, `GpuSyncToBones=true`; the CPU-authored palette deforms and follows the chain between:
  - `Build/_AgentValidation/20260722-physics-chain-skinning/mcp-captures/Screenshot_20260722_110554_335_79d0cc7308d34a4191e2d94bf797e7a1.png`
  - `Build/_AgentValidation/20260722-physics-chain-skinning/mcp-captures/Screenshot_20260722_110555_748_35171acfe74347728247eee10b3a1a7b.png`
- OpenGL log session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-22_11-04-14_pid49508/`. `PhysicsChainBonePalette.comp` compiled and linked with `error=<none>`; no physics-chain shader or dispatch errors were logged.

## Vulkan parity implementation

The dispatcher now resolves a backend-neutral OpenGL or Vulkan adapter. Direct
dispatch, indirect dispatch, buffer copies, barriers, and submission markers are
enqueued transactionally for each dispatch group; a partial group is rolled back
instead of being reported as submitted. Vulkan records the work in the normal
frame command stream, outside active graphics rendering scopes.

The Vulkan implementation adds:

- explicit enqueue results for ready, pending, invalid descriptor/resource,
  missing pass context, unsupported, and device-lost states;
- `vkCmdDispatchIndirect` and `vkCmdCopyBuffer` frame operations with immutable
  buffer generations and usage/range validation;
- a dispatch-indirect argument arena carrying
  `VK_BUFFER_USAGE_INDIRECT_BUFFER_BIT`;
- submission-resolved timeline fences, including frame-recording abort and
  device-loss failure paths;
- pooled mapped readback staging with coherent handling and aligned
  noncoherent invalidation;
- a dedicated transfer-write-to-host-read barrier;
- independent core zero-readback and asynchronous-readback capability gates;
- Vulkan-valid palette seeding, bounds publication, selective/full readback,
  standalone dispatch groups, and backend-neutral debug generation; and
- backend epoch invalidation so old renderer completions cannot be applied
  after a renderer switch.

All nine physics-chain shaders compile through the runtime Vulkan path. Shader
sources were corrected where OpenGL-accepted identifier shadowing and local
scope differed under SPIR-V compilation.

## Vulkan validation evidence

Run root:

`Build/_AgentValidation/physics-chain-test-repair/vulkan-parity-20260722/`

Desktop Vulkan zero-readback (`UseGpuDrivenSkinning=true`,
`GpuSyncToBones=false`) was visually inspected at multiple times and camera
positions:

- `mcp-captures/batch-final-zero/Screenshot_20260722_223530_524_7521dfa851ab4932a226eb047cb615c9.png`
- `mcp-captures/batch-final-zero/Screenshot_20260722_223545_434_f5a469fba5fe46dab77da5e83e045c12.png`
- `mcp-captures/batch-final-zero/Screenshot_20260722_223547_715_a96d8bb072c24d4499a971b96fab5d97.png`

Desktop Vulkan Sync To Bones (`UseGpuDrivenSkinning=false`,
`GpuSyncToBones=true`) visibly changes pose without a render-thread wait:

- `mcp-captures/shader-fixed-sync/Screenshot_20260722_221158_990_b2b7c69466cc461d8a741f0057281644.png`
- `mcp-captures/shader-fixed-sync/Screenshot_20260722_221202_268_624ca5f888ed4151a9144166fd3bbd05.png`

The final named live session
`Build/_AgentValidation/mcp-sessions/physics-chain-marker-abort-final/`
reported, after warmup, 7,281 submitted readbacks, 7,279 completed, zero
enqueue/read failures, zero stale drops, maximum one-frame completion latency,
and zero dispatch failures. Two startup readbacks were explicitly failed when
their frame recording was deferred; the count remained unchanged during the
steady-state observation window. The zero-readback scenario held submitted and
completed readback counters constant while submission IDs and visible poses
continued advancing.

The final capability-gate session
`Build/_AgentValidation/mcp-sessions/physics-chain-capability-final/`
validated the tightened requirement that the selected Vulkan graphics queue
actually advertises compute support. In steady state it reached 2,521 submitted
and 2,518 completed readbacks with one in flight, a one-frame maximum latency,
zero enqueue/read/stale-result/dispatch failures, and the same two stable
startup marker failures. Its sampled Vulkan validation error/message count was
zero. The active scene was visually inspected in:

- `mcp-captures/capability-final/Screenshot_20260722_234742_595_e0228a6f28f44532a7f05ca14f799833.png`
- `mcp-captures/capability-final/Screenshot_20260722_234759_001_d6f2c24b8dd24d309ba85b6b2df47b0b.png`

RenderDoc capture:

`renderdoc/vulkan-physics-chain-active_frame6788.rdc`

The capture reports Vulkan, 127 events, six compute dispatches, and no RenderDoc
API/debug messages. Its compute sequence contains three direct solver setup
dispatches, `PhysicsChain.IndirectDispatch` at EID 65 with GPU-authored
`<1,1,1>` arguments, then bounds and palette dispatches, separated by explicit
memory barriers. Descriptor inspection showed the expected SSBO sets for the
indirect solver, bounds, and palette stages. Raw replay exports under
`renderdoc/buffers/` resolve those descriptors to the actual resources:
indirect arguments are `(1,1,1)`, the active tree counter is one, particles and
transforms contain the current simulated positions, bounds contain finite
published extents, and the skin palette contains valid matrix rows. The replay
session was closed after inspection. The final RenderDoc image export was also
inspected; that capture's editor camera did not frame the chain, so
visible-motion proof comes from the MCP captures above rather than the black
scene viewport in that one frame.

Focused parity validation passed 137/137 tests. The expanded
`PhysicsChain|MathIntersectionsPhysicsChainSkinning|VulkanCommandChainDataModel`
filter passed 422/422 after repairing four tests that assigned a world to an
already-activated component instead of its scene node, one unattached collider
fixture, and an allocation probe that now distinguishes recurring allocation
from a one-time runtime charge with repeated steady-state windows. The final
complete unit-test run discovered 4,188 tests and executed 4,140: 3,851 passed,
289 failed, and 48 were not executed; its TRX is
`reports/full-tests-post-fix/full.trx`. None of its failed test names match the
focused physics-chain or Vulkan command-chain cohorts. The remaining failures
include unrelated moved-file/stale-source contracts and existing fixtures.
The final `XRENGINE.slnx` build completed with zero errors and 42 existing
Magick.NET vulnerability/version-conflict warnings in Server, VRClient, and
Benchmarks.

OpenXR/OpenVR hardware/runtime validation was unavailable for this pass. Queue
markers use the actual containing submit's timeline signal in both desktop and
shared Vulkan submission code, but no VR runtime claim is made here.
