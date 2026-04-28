# Forward Depth-Normal TransformId TODO

Last Updated: 2026-04-27
Current Status: captured from investigation; implementation not started.
Scope: `DefaultRenderPipeline`, `DefaultRenderPipeline2`, forward depth-normal prepass FBOs, prepass shaders, generated vertex shader interface trimming, and depth-normal material variants.

## Current Problem

Deferred GBuffer shaders write a per-pixel `TransformId` alongside albedo, normal, material data, and depth. The forward depth-normal prepass currently updates the main depth and normal buffers for opaque/masked forward geometry, but it does not update the main `TransformId` buffer.

That creates a mixed-pixel state after the shared forward prepass: a pixel can contain forward geometry depth and normal while still carrying the deferred object ID from whatever was behind it, or zero if no deferred object wrote there. Any pass that expects depth, normal, and object identity to describe the same surface can make the wrong decision.

Likely affected or future-sensitive paths:

- object-aware AO, denoising, or spatial reuse
- temporal history rejection and stabilization
- transform/object debug visualization
- editor picking or GPU readback tools that rely on the ID buffer
- any future per-object screen-space resolve that samples depth, normal, and `TransformId` together

## Current Evidence

- Common deferred shaders declare `layout (location = 3) out uint TransformId;` and write `TransformId = floatBitsToUint(FragTransformId);`.
- `Build/CommonAssets/Shaders/Common/DepthNormalPrePass.fs` writes only `Normal` at location 0.
- `CreateForwardDepthPrePassMergeFBO()` attaches the main `Normal` and `DepthStencil` textures, but not `TransformId`.
- The merge FBO is bound without clearing color/depth/stencil, so it is already structured to preserve existing deferred data while overlaying forward geometry.
- `VPRC_ForwardDepthNormalPrePass` forces override/variant materials, shader pipelines, and generated vertex programs, then renders the CPU path.
- Generated vertex shader code strips `FragTransformId` unless the paired fragment shader consumes it. The prepass fix must make the effective prepass fragment interface visible to that stripping decision.

## Target Outcome

At the end of this work:

- shared forward prepass pixels write depth, normal, and transform ID for the same forward surface
- deferred transform IDs remain intact for pixels not touched by forward geometry
- the dedicated forward-only debug prepass remains optional and does not clear or overwrite the main `TransformId` texture accidentally
- generic override materials and per-material depth-normal variants both write transform IDs
- generated/default vertex shaders emit `FragTransformId` whenever the effective prepass fragment shader consumes it
- OpenGL shader pipeline validation remains clean, with no vertex/fragment interface mismatch warnings

## Non-Goals

- Do not change the deferred GBuffer texture format unless the implementation proves the current `TransformId` format is insufficient.
- Do not move the forward prepass back to the full deferred GBuffer material path.
- Do not re-enable GPU-indirect dispatch for the forward depth-normal prepass as part of this task.
- Do not attach the main `TransformId` texture to the dedicated forward-only debug FBO if that FBO is cleared before rendering.

## Phase 0 - Confirm Attachment Contract

Outcome: decide and document the prepass-local color attachment layout before changing shaders.

- [ ] Confirm whether the prepass should use compact output locations (`Normal` at 0, `TransformId` at 1) or mirror the deferred GBuffer location for transform ID (`TransformId` at 3).
- [ ] Check `XRFrameBuffer` draw-buffer setup for sparse color attachments before choosing location 3.
- [ ] Prefer compact prepass locations if sparse attachments add risk; the `TransformId` texture can still be the main GBuffer texture even when attached to prepass color attachment 1.
- [ ] Document the chosen convention in the shader/FBO code near the prepass setup.

Acceptance criteria:

- the shader output locations and FBO attachments agree
- the chosen layout does not require dummy color attachments
- the merge FBO can update `Normal`, `TransformId`, and depth in one pass

## Phase 1 - Attach TransformId To The Shared Merge FBO

Outcome: the shared forward prepass can write the main transform ID texture without clearing existing deferred IDs.

- [ ] Add `TransformIdTextureName` to `CreateForwardDepthPrePassMergeFBO()` using `EnsureTextureAttachment(TransformIdTextureName, CreateTransformIdTexture)`.
- [ ] Attach it at the selected prepass color attachment location.
- [ ] Keep the merge bind path clearing disabled: `clearColor = false`, `clearDepth = false`, `clearStencil = false`.
- [ ] Verify the deferred GBuffer clear still initializes the main `TransformId` texture before deferred and forward prepass work.
- [ ] Mirror the change in `DefaultRenderPipeline2` if V2 remains present.

Acceptance criteria:

- forward pixels can overwrite transform ID in the main ID texture
- deferred-only pixels keep their original ID
- untouched pixels keep the normal pipeline clear value

## Phase 2 - Update The Generic Depth-Normal Prepass Shader

Outcome: the fallback override material writes transform ID whenever no per-material depth-normal variant exists.

- [ ] Add a `uint TransformId` fragment output at the selected prepass location in `DepthNormalPrePass.fs`.
- [ ] Add `layout(location = 21) in float FragTransformId;`.
- [ ] Write `TransformId = floatBitsToUint(FragTransformId);` next to the normal output.
- [ ] Keep the normal output format unchanged.

Acceptance criteria:

- generic forward prepass fallback renders normal and transform ID
- no existing normal consumers need to change sampler decoding

## Phase 3 - Update Depth-Normal Material Variants

Outcome: source-material variants preserve their custom alpha/normal logic and still write transform ID.

- [ ] Update `ForwardDepthNormalVariantFactory` so generated fragment variants inject both normal and transform ID outputs.
- [ ] Ensure generated variants declare `FragTransformId` if the source shader did not already declare it.
- [ ] Write `TransformId = floatBitsToUint(FragTransformId);` in the replacement main body.
- [ ] Update explicit `XRENGINE_DEPTH_NORMAL_PREPASS` branches in common forward shaders to declare and write transform ID.
- [ ] Verify masked forward variants still preserve discard/alpha-cutoff behavior before writing outputs.
- [ ] Verify unlit forward variants either write a meaningful transform ID with a fallback normal or intentionally opt out if no prepass variant should exist.

Acceptance criteria:

- normal-mapped forward variants continue to write material-correct normals
- masked variants still discard correctly
- every successful depth-normal variant writes transform ID

## Phase 4 - Fix Generated Vertex Interface Selection

Outcome: generated vertex programs emit `FragTransformId` based on the effective prepass fragment material, not only the mesh renderer's source material.

- [ ] Audit `FragmentConsumesTransformId()` in `XRMeshRenderer` and mesh-deform generator paths.
- [ ] Make the check consider the active effective material when render state uses an override material or a depth-normal prepass variant.
- [ ] Avoid caching a forced generated vertex program that was built without `FragTransformId` and then reusing it for a prepass material that consumes it.
- [ ] Consider adding a small render-state flag for passes that require transform ID output if that is cleaner than passing material identity into shader generation.
- [ ] Preserve the original interface-trimming goal: do not emit `FragTransformId` into fragments that truly do not consume it.

Acceptance criteria:

- forward depth-normal prepass fragments link with generated vertex programs that provide `FragTransformId`
- other passes do not regain avoidable SPIR-V/OpenGL pipeline interface warnings
- generated vertex program caching remains deterministic across material override passes

## Phase 5 - Optional Dedicated Debug Texture

Outcome: decide whether the forward-only debug prepass needs its own transform ID target.

- [ ] If the dedicated forward-only prepass debug view needs IDs, add a separate `ForwardPrePassTransformId` texture.
- [ ] Attach that separate texture to `CreateForwardDepthPrePassFBO()`.
- [ ] Do not attach the main `TransformId` texture to the dedicated FBO while it clears color/depth/stencil before rendering.
- [ ] Add or update any debug visualization selector labels if this texture becomes inspectable.

Acceptance criteria:

- debug-only forward prepass data can be inspected without mutating the main GBuffer ID texture
- the shared merge path remains the only path that writes forward IDs into the main GBuffer ID texture

## Phase 6 - Tests And Validation

Outcome: shader rewriting, compilation, and editor rendering are covered by targeted validation.

- [ ] Update `ForwardDepthNormalVariantTests` to assert generated variants include transform ID output and `FragTransformId` input.
- [ ] Add explicit-shader-mode variant tests for `XRENGINE_DEPTH_NORMAL_PREPASS` common forward shaders.
- [ ] Add a regression test that verifies unsupported forward shaders still return `null` instead of producing a broken variant.
- [ ] Run the shader compilation regression tests that cover common forward/deferred shaders.
- [ ] Build the editor.
- [ ] Run the editor with a scene containing overlapping deferred and forward opaque/masked geometry, then inspect the `TransformId` debug output if available.

Acceptance criteria:

- targeted unit tests pass
- shader compilation regression passes for updated shaders
- editor build succeeds
- forward geometry writes matching depth, normal, and transform ID in the shared prepass

## Implementation Notes

- The merge FBO already binds with clear disabled, which is the correct behavior for preserving deferred IDs.
- The main risk is shader interface mismatch: fragment variants may consume `FragTransformId` while the forced generated vertex program was cached from a source material that did not.
- If the prepass uses compact output locations, keep the shader names and comments clear that location 1 is a prepass attachment location, not the deferred GBuffer's color attachment 1 semantic.
- If the implementation adds a new texture for the dedicated debug FBO, use nearest filtering and the same integer format as the main `TransformId` texture.