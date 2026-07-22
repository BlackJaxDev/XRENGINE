# Physics-chain skinned-mesh motion failure

## Status

Resolved in implementation and validated on the supported OpenGL path.

User confirmation: not yet reported.

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

`rdc` was not installed. The corrected OpenGL viewport, component state, and shader compilation log made a RenderDoc fallback unnecessary.

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
