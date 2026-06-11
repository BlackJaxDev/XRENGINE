# Vulkan Deferred GBuffer Depth-Write Investigation

## Problem

Under **OpenGL**, the deferred Sponza scene renders correctly: the building draws
opaque pixels over the procedural skybox.

Under **Vulkan**, the deferred Sponza geometry does **not** appear over the skybox.
The skybox covers the whole frame and only the forward-rendered vegetation shows up.

## Decisive Finding

The deferred **GBuffer pass writes color attachments (albedo / normal / RMSE) but
does NOT write depth** to the shared `DepthStencilTexture`.

Evidence: an unconditional depth-visualization in `DeferredLightCombine.fs`
(`OutLo = vec4(vec3(depth), 1); return;`) with the skybox disabled showed the entire
Sponza building area as **white (depth == 1.0 / cleared far value)**. Only the
forward-rendered vegetation had real (non-white) depth.

Consequences of the empty depth buffer:

1. `DeferredLightCombine.fs` runs `if (depth >= 1.0) discard;`, so every Sponza pixel
   is discarded → dark / black composite.
2. The forward pass loads depth == 1.0 everywhere, so the skybox (drawn at the far
   plane) passes the depth test across the whole screen and overwrites the composite.

So the skybox-occlusion symptom is **downstream** of the real bug: the Vulkan deferred
GBuffer is not writing depth.

## Confirmed NOT the cause

- GBuffer geometry **is** drawn (albedo/normal are visible, shapes recognizable).
- Depth `Load` is preserved (not demoted to `DontCare`); tracked layout enters as
  `DepthStencilReadOnlyOptimal` for both GBuffer and forward passes.
- The barrier planner canonicalizes both FBOs' depth resource names to the shared
  texture, so cross-pass depth sync can chain.
- Culling / bounds / materials / render matrices (OpenGL path works with the same data).
- Adding a global `VPRC_DepthWrite.Allow = true` to the GBuffer command chain
  (mirroring the forward pass) made **no difference** — so the global VPRC depth-write
  state is not the gate.

## Current Suspicions

The Vulkan graphics-pipeline depth-stencil state for deferred meshes is being built
with `DepthWriteEnable = false`. Candidate reasons:

1. **Read-only depth demotion.** `ResolveAttachmentCompatibleDrawState` forces
   `DepthWriteEnabled = false` when `depthStencilReadOnly` (from
   `UsesReadOnlyDepthStencilForPass`) or `PassUsesReadOnlyDepthStencil(passIndex)` is
   true. If the GBuffer pass's depth usage resolves to `ERenderGraphAccess.Read`
   (instead of `ReadWrite`) — or the reference layout resolves to
   `DepthStencilReadOnlyOptimal` — depth writes are silently dropped.
   - Static analysis says the GBuffer binds `Write = true`, so `DepthAccess` should be
     `ReadWrite`. Needs runtime confirmation.
2. **Per-material depth derivation.** `VkMeshRenderer` sets
   `depthWriteEnabled = depthTestEnabled && dt.UpdateDepth`. Opaque deferred materials
   have `UpdateDepth = true`, so this path should yield `true`. Needs runtime
   confirmation that opaque deferred draws actually take this branch (and not the
   global-state fallback `Renderer.GetDepthWriteEnabled()`).

## Active Diagnostic (in tree — must be reverted)

A `Debug.Out("[XRE-DIAG] Pipeline depth-stencil: ...")` was added in
`VkMeshRenderer.Pipeline.cs` just before `PipelineDepthStencilStateCreateInfo` to log,
per program/pass: `depthStencilReadOnly`, `draw.DepthWriteEnabled`,
`effectiveDraw.DepthTestEnabled`, `effectiveDraw.DepthWriteEnabled`, and the compare op.

This will reveal whether the deferred program's pipeline is created with
`DepthWriteEnable = false`, and whether the read-only demotion path is the cause.

## Next Steps

1. Read the new `[XRE-DIAG]` log for the deferred/GBuffer program and confirm the
   reported `effWrite` value.
2. If `effWrite == false` due to read-only demotion → fix the GBuffer depth usage so
   its access is `ReadWrite` / writable reference layout (root cause in render-graph
   describe or in `UsesReadOnlyDepthStencilForPass` / `PassUsesReadOnlyDepthStencil`).
3. If `effWrite == false` because the draw fell back to global state → ensure the
   deferred draw honors the material's `UpdateDepth = true`.
4. Implement the localized fix, rebuild, and verify Sponza renders over the skybox.

## Cleanup Checklist (before finishing)

- [ ] Remove the `[XRE-DIAG]` log in `VkMeshRenderer.Pipeline.cs`.
- [ ] Remove the unconditional depth-viz line in `DeferredLightCombine.fs`
      (keep only the `XRE_DEFERRED_DEBUG == 5` conditional).
- [ ] Restore `Assets/UnitTestingWorldSettings.jsonc` `"Skybox": true`.
- [ ] Ensure `XRE_DEFERRED_DEBUG=0` and `XRE_VK_TRACE_DRAW=0`.
