# Transparency And OIT

This document describes transparent surface routing, weighted blended order-independent transparency (OIT), and exact transparency experiments in `DefaultRenderPipeline` and `AdvancedRenderPipeline`.

Related checks: [Default And Advanced Pipeline Validation](../../work/testing/rendering/default-and-advanced-pipeline-validation.md). Open work: [Transparency And OIT TODO](../../work/todo/rendering/transparency-and-oit-todo.md).

## Type And File Map
| Type or shader | File | Responsibility |
|---|---|---|
| `ETransparencyMode` | `XREngine.Runtime.Rendering/Materials/ETransparencyMode.cs` | Defines material transparency modes. |
| `DefaultRenderPipeline` transparency commands | `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.CommandChain.cs` | Runs scene copy, deferred transparency blur, weighted OIT accumulation, OIT resolve, and exact transparency when enabled. |
| `DefaultRenderPipeline` transparency resources | `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.Resources.cs` | Declares weighted OIT textures, exact transparency buffers, and depth-peel targets. |
| `DefaultRenderPipeline.ExactTransparency` | `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.ExactTransparency.cs` | Owns PPLL and depth-peeling commands, uniforms, and debug resources. |
| `ExactTransparencyPpll.glsl` | `Build/CommonAssets/Shaders/Snippets/ExactTransparencyPpll.glsl` | Stores per-pixel linked-list fragments and overflow counters. |
| `ExactTransparencyDepthPeel.glsl` | `Build/CommonAssets/Shaders/Snippets/ExactTransparencyDepthPeel.glsl` | Discards fragments from prior peel layers. |
| `ForwardDepthNormalVariantFactory` | `XREngine.Runtime.Rendering/Shaders/ForwardDepthNormalVariantFactory.cs` | Keeps transparent and AO-only statements out of depth-normal variants. |

## Material Modes

`ETransparencyMode` has these modes:

- `Opaque` writes the opaque path.
- `Masked` writes depth and uses alpha cutout.
- `AlphaBlend`, `PremultipliedAlpha`, and `Additive` use sorted or ordinary blended forward behavior.
- `WeightedBlendedOit` routes the material to the weighted OIT pass.
- `PerPixelLinkedList` routes the material to the PPLL exact pass.
- `DepthPeeling` routes the material to the depth-peeling exact pass.
- `Stochastic`, `AlphaToCoverage`, and `TriangleSorted` are authored modes or planned modes. Their production behavior still needs validation or more code.

Masked content must not enter the blended transparency path. Alpha-to-coverage keeps masked depth behavior and smooths coverage only when MSAA can consume it.

## Weighted Blended OIT

Weighted blended OIT is the default order-independent path for general transparent materials.

The pipeline first copies the scene color to `TransparentSceneCopyTexture`. It can blur deferred transparency from albedo, opacity, and depth. It then clears `TransparentAccumTexture` to transparent and `TransparentRevealageTexture` to white. The weighted pass renders `EDefaultRenderPass.WeightedBlendedOitForward` with depth test enabled and depth writes disabled. The resolve pass writes back to `HDRSceneTexture`.

The resources are stereo-compatible. Exact transparency disables stereo today, but weighted OIT can use layered resources through the declared resource profile.

## Exact Transparency

Exact transparency is an editor diagnostic feature. It is disabled for stereo and for the OpenXR Vulkan desktop startup-safe path. The editor preference `EnableExactTransparencyTechniques` gates the passes.

PPLL uses:

- `PpllHeadPointerTex`, an `R32ui` storage image.
- `PpllNodeBuffer`, a storage buffer with `XRE_PpllNode` records.
- `PpllCounterBuffer`, a storage buffer with emitted-node and overflow counters.
- `PpllResolveFBO`, which resolves into `HDRSceneTexture` and writes `PpllFragmentCountTex`.

The fragment limit in the resolve is 16. Overflow is counted in the PPLL counter buffer and status flags.

Depth peeling uses up to four color and depth layers. Each layer renders `EDefaultRenderPass.DepthPeelingForward`. Layers after the first sample the prior peel depth through `PrevPeelDepth`. `DepthPeelReversedDepth` selects the correct depth comparison for reversed-depth cameras. `DepthPeelingResolveFBO` resolves the peel textures into `HDRSceneTexture`.

## Pass Order

Transparency runs after the forward pass and before velocity and later post-process work. Motion-vector replay includes weighted OIT, PPLL, and depth-peeling passes for participating transparent content. This keeps temporal effects informed when a transparent mesh opts in.

The ordinary `TransparentForward` pass still exists. It is not the same as weighted OIT or exact transparency.

## Resource Ownership

All default-pipeline transparency resources are declared through `DescribeResources(...)`. Frame execution must not create or resize them. Resource predicates select these resources only when a pass, debug view, or exact-transparency setting needs them.

Exact transparency resources are persistent where counters and buffers need stable storage. Depth-peeling FBOs are transient per layer.

## Known Limits

- Exact transparency is not stereo-ready.
- PPLL has a bounded node buffer and a 16-fragment resolve limit.
- Depth peeling supports at most four layers.
- Stochastic transparency needs mature temporal reconstruction before it can be a default path.
- Per-triangle sorting needs a proven content case before it should ship.
- Import classification for diffuse-alpha-only materials still needs a warning diagnostic.
- Quality and GPU cost comparisons between weighted OIT and exact modes are not complete.
