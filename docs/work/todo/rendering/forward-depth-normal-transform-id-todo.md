# Forward Depth-Normal TransformId TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Default Render Pipeline Notes](../../../architecture/rendering/default-render-pipeline-notes.md)
Validation: [Default And Advanced Pipeline Validation](../../testing/rendering/default-and-advanced-pipeline-validation.md)

## Current State

Deferred shaders write `TransformId`, but `Build/CommonAssets/Shaders/Common/DepthNormalPrePass.fs` writes only `Normal`. `CreateForwardDepthPrePassMergeFBO()` in `DefaultRenderPipeline.FBOs.cs` merges normal and depth without `TransformId`. `ForwardDepthNormalVariantFactory` exists, and `ForwardDepthNormalVariantTests` cover variant rewriting, but the shared forward prepass still cannot update the main identity texture.

## Open Code Items

### FBO Contract

- [ ] Select the prepass-local color attachment layout for `Normal` and `TransformId`. `DefaultRenderPipeline.FBOs.cs`, `AdvancedRenderPipeline.FBOs.cs`, `DepthNormalPrePass.fs`. Done when: shader output locations and FBO attachments agree without dummy attachments.
- [ ] Attach the main transform ID texture to the shared forward depth-normal merge FBO. `CreateForwardDepthPrePassMergeFBO()`, `CreateTransformIdTexture`. Done when: forward pixels can overwrite `TransformId`, and deferred-only pixels keep their prior ID.
- [ ] Preserve the no-clear merge behavior for color, depth, and stencil. `DefaultRenderPipeline.CommandChain.cs`. Done when: the merge path keeps existing deferred data outside touched forward pixels.
- [ ] Mirror the merge FBO contract in `AdvancedRenderPipeline` if its depth-normal path stays active. Done when: both pipelines use the same identity contract or document a deliberate difference.

### Shader Variants

- [ ] Add `TransformId` output and `FragTransformId` input to `DepthNormalPrePass.fs`. Done when: the fallback override material writes normal and transform ID.
- [ ] Update `ForwardDepthNormalVariantFactory` to inject transform ID outputs into generated fragment variants. Done when: normal-mapped and masked variants keep their existing normal and discard behavior and also write transform ID.
- [ ] Update explicit `XRENGINE_DEPTH_NORMAL_PREPASS` branches in common forward shaders. Done when: explicit shader variants write transform ID or clearly opt out.

### Generated Vertex Interface

- [ ] Make generated vertex programs emit `FragTransformId` for the effective prepass fragment material. `XRMeshRenderer`, mesh-deform generator paths. Done when: forced generated vertex programs link with depth-normal prepass fragments that consume transform ID.
- [ ] Keep interface trimming for passes that do not consume transform ID. Done when: other passes do not regain avoidable OpenGL or SPIR-V interface warnings.
- [ ] Make generated vertex program caching deterministic across material override passes. Done when: a cache hit cannot reuse a program that lacks a required transform ID output.

### Debug And Tests

- [ ] Decide whether the dedicated forward-only debug prepass needs a separate `ForwardPrePassTransformId` texture. Done when: the debug FBO can show identity without clearing or mutating the main GBuffer ID texture.
- [ ] Extend `ForwardDepthNormalVariantTests` for transform ID output and `FragTransformId` input. Done when: generated and explicit depth-normal variants are covered.
- [ ] Add a regression test for unsupported forward shaders. Done when: unsupported shaders return `null` instead of producing a broken variant.

## Decisions Needed

- [ ] Choose compact prepass attachment locations or deferred GBuffer mirror locations. Owner: rendering lead.
- [ ] Decide whether a render-state flag should require transform ID output instead of passing material identity into shader generation. Owner: rendering lead.

## Out Of Scope

- Changing the deferred GBuffer texture format.
- Moving the forward prepass back to the full deferred GBuffer material path.
- Re-enabling GPU-indirect dispatch for the forward depth-normal prepass.
