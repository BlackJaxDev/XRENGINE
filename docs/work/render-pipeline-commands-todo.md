# Render Pipeline Commands — Expansion Roadmap

Proposed new `ViewportRenderCommand` types to make pipelines fully user-composable at runtime.

## P0 — Highest Impact (enables custom passes & composition)

- [x] **VPRC_PushStencilState / VPRC_PopStencilState** — Full stencil state push/pop is implemented. Current version applies shared front/back stencil state, which is enough to unlock masking, portal, and outline workflows.
- [x] **VPRC_PushBlendState / VPRC_PopBlendState** — Blend equation and separate RGB/alpha factors are implemented. Current version is global rather than per-attachment.
- [x] **VPRC_RenderMeshesFiltered** — Implemented with a `Predicate<RenderCommand>` filter hook so custom passes can render subsets of a pass.
- [x] **VPRC_ForEach** — Implemented as a generic collection iterator with per-element configurator callback and nested command body.
- [x] **VPRC_Repeat** — Implemented with fixed or dynamic iteration count plus per-iteration configurator callback.

## P1 — Resource Wiring & Debugging

- [x] **VPRC_BindTexture** — Implemented as scoped sampler binding by pipeline texture name, sampler name, and texture unit.
- [x] **VPRC_BindBuffer** — Implemented as scoped program buffer binding by pipeline buffer name and binding location.
- [x] **VPRC_PushShaderGlobals / VPRC_PopShaderGlobals** — Implemented with scoped bool/int/uint/float/vector/matrix uniform dictionaries applied to subsequent program bindings.
- [x] **VPRC_PushMaterialOverride / VPRC_PopMaterialOverride** — Implemented using the render pipeline state's existing material override stack.
- [x] **VPRC_DebugOverlay** — Render a named texture as a debug overlay (corner/fullscreen, channel selector: R, G, B, A, depth, normals, motion vectors).
- [x] **VPRC_Annotation** — No-op command that inserts a GPU debug marker/label (shows in RenderDoc, Nsight).
- [x] **VPRC_GaussianBlur** — Separable Gaussian blur as a reusable command.
- [x] **VPRC_Tonemap** — Standalone tonemapping with selectable operator (ACES, Reinhard, AgX, custom LUT).

## P2 — Cubemaps, Plumbing & Data-Driven Pipelines

- [x] **VPRC_RenderCubemap** — Implemented as a six-face scene-capture style render over an existing render pass, with optional depth attachment and mip generation.
- [x] **VPRC_RenderToCubemapFace** — Implemented as a single-face scene render into a cubemap layer/mip, with optional depth attachment.
- [x] **VPRC_ConvolveCubemap** — Implemented using the existing engine cubemap IBL shaders to output irradiance and specular prefilter cubemaps.
- [x] **VPRC_RenderToTextureArray** — Implemented as scoped rendering to a specific array slice/mip, optionally with a camera override.
- [x] **VPRC_CopyTexture** — Implemented as a temporary-FBO texture copy/resolve path with mip and layer selection.
- [x] **VPRC_CopyTextureToBuffer / VPRC_CopyBufferToTexture** — Implemented for engine data buffers and 2D textures, with OpenGL raw texture readback and CPU-staged texture upload.
- [x] **VPRC_GenerateMipmaps** — Implemented as explicit GPU mip generation for a named texture.
- [x] **VPRC_ResolveMultisample** — Implemented as a generic temporary-FBO MSAA resolve without GBuffer-specific assumptions.
- [x] **VPRC_SetVariable** — Implemented as a persistent per-pipeline variable bag with typed shader-uniform application and resource-reference resolution for textures, buffers, and framebuffers.
- [x] **VPRC_GPUTimerBegin / VPRC_GPUTimerEnd** — Implemented as user-authored GPU profiling scopes that group subsequent commands in the existing render-pipeline GPU profiler.
- [x] **VPRC_RenderByRenderGroup** — Implemented as authored grouping over existing render-command metadata such as material name, mesh name, material render pass, or command type.
- [x] **VPRC_DownsampleChain** — Implemented as a reusable 2D fullscreen mip-chain generator with optional bright-pass thresholding on the first level.
- [x] **VPRC_UpsampleChain** — Implemented as an in-place 2D mip-chain reconstruction pass with optional additive accumulation.
- [x] **VPRC_BilateralFilter** — Implemented as a reusable separable bilateral filter with optional depth and normal guidance.
- [x] **VPRC_ColorGrading** — Implemented as a standalone fullscreen grading pass that reuses the engine's `ColorGradingSettings` model or the active camera stage.
- [x] **VPRC_ApplyLUT** — Implemented as a standalone LUT pass supporting authored 3D LUT textures and 2D strip LUT textures.
- [x] **VPRC_ComputeHistogram** — Implemented as a reusable texture histogram command with configurable channel source, numeric range, summary variables, and histogram buffer output.
- [x] **VPRC_ReadbackPixel / VPRC_ReadbackRegion** — Implemented for texture mip/layer readback into pipeline variables and float-buffer payloads.

## P3 — Advanced & Future-Proof

- [x] **VPRC_FrustumCullToBuffer** — Implemented as a CPU-authored frustum test over pass mesh commands that writes visible source-command indices into a named data buffer.
- [x] **VPRC_OcclusionQuery** — Implemented as an authored hardware occlusion-query wrapper around a nested command body, publishing availability and visibility results into pipeline variables.
- [x] **VPRC_ConditionalRender** — Implemented as a variable-driven nested command body that executes when a pipeline variable resolves truthy.
- [x] **VPRC_WaitForCompute** — Implemented as a convenience memory-barrier command for compute-to-graphics synchronization.
- [x] **VPRC_Fence** — Implemented as a named authored synchronization marker over the existing memory-barrier path, with completion published into pipeline variables.
- [x] **VPRC_RenderToWindow** — Implemented as a composable present/composite command that draws a named texture or FBO color attachment into the active renderer window, with optional viewport-region targeting.
- [x] **VPRC_CaptureFrame** — Implemented for named textures, named FBO color attachments, or the current output FBO color attachment, with optional buffer export and optional file write.
- [x] **VPRC_FXAA** — Implemented as a standalone fullscreen FXAA pass over a named texture or FBO color attachment.
- [x] **VPRC_SMAA** — Implemented as a lookup-free SMAA-style three-pass AA chain with edge detection, blend-weight estimation, and neighborhood blending.
- [x] **VPRC_DebugWireframe** — Implemented as an OpenGL-backed scoped wireframe raster-mode toggle over a nested sub-chain.
- [x] **VPRC_DebugHeatmap** — Implemented as a composable false-color texture/FBO overlay for scalar debug channels such as overdraw, density, or light-count buffers.
- [x] **VPRC_BuildAccelerationStructure** — Implemented against the engine's existing scene GPU BVH build/update path, publishing node/range/morton buffers and readiness variables.
- [x] **VPRC_DispatchRays** — Implemented over the existing native `RestirGI` ray-dispatch bridge with authored pipeline and shader-binding-table parameters.
- [x] **VPRC_TraceRaysIndirect** — Implemented as an indirect variant that resolves dispatch dimensions from a named pipeline buffer before invoking the native ray-dispatch bridge.
- [x] **VPRC_ForEachCascade** — Implemented as a directional-light cascade iterator that publishes per-cascade state and optionally binds/pushes each cascade shadow target.

## Recommended Next Implementation Order

- [x] **VPRC_PushMaterialOverride / VPRC_PopMaterialOverride** — This composes directly with `VPRC_RenderMeshesFiltered` and immediately unlocks depth-only, normals, UV, wireframe, and stylized subset passes without changing scene materials.
- [x] **VPRC_BindTexture** — Needed to make `VPRC_Repeat` and `VPRC_ForEach` practical for ping-pong, blur, post-process, and multi-target workflows.
- [x] **VPRC_BindBuffer** — Same rationale as texture binding, but for compute-driven and GPU-data-driven passes.
- [x] **VPRC_PushShaderGlobals / VPRC_PopShaderGlobals** — Gives the iteration commands a clean way to pass per-iteration/per-element parameters into shaders.
- [x] **VPRC_Annotation** — Very cheap to add and high value for RenderDoc/Nsight/debugging once users start composing their own pipelines.
- [x] **VPRC_DebugOverlay** — High leverage for pipeline authoring because users need to inspect intermediate textures quickly while the game is running.
- [x] **VPRC_GaussianBlur** — Common reusable building block that becomes much easier to express now that `VPRC_Repeat` exists.
- [x] **VPRC_Tonemap** — Common standalone post-process that benefits from the new binding/global-state primitives.

## Recommended Next Commands

- [x] **VPRC_SetVariable** — Highest leverage remaining plumbing command. It lets runtime-authored pipelines pass scalar/resource state between commands without requiring custom C# configurators.
- [x] **VPRC_DownsampleChain** — Foundational post-process primitive for bloom, luminance pyramids, cheap blur prepasses, and many screen-space effects.
- [x] **VPRC_UpsampleChain** — Natural pair for `VPRC_DownsampleChain`; together they unlock bloom-style reconstruction and more flexible multi-resolution pipelines.
- [x] **VPRC_RenderByRenderGroup** — Implemented as a serializable render-group selector layered over current material, mesh, pass, and command metadata.
- [x] **VPRC_ComputeHistogram** — Implemented as a CPU histogram pass sourced from renderer texture readback, with summary metrics stored back into pipeline variables.
- [x] **VPRC_ReadbackPixel / VPRC_ReadbackRegion** — Implemented as texture readback commands for single-pixel sampling and rectangular float-region extraction.
- [x] **VPRC_GPUTimerBegin / VPRC_GPUTimerEnd** — Implemented on top of the existing OpenGL render-pipeline GPU profiler as cross-command user timing scopes.
- [x] **VPRC_BilateralFilter** — Implemented as a guided edge-preserving fullscreen filter for AO, GI, denoise, and similar screen-space passes.

## Example Authored Chain

```csharp
var query = commands.Add<VPRC_OcclusionQuery>();
query.ResultAvailableVariableName = "PortalQueryReady";
query.ResultVariableName = "PortalVisible";
query.Body = new ViewportRenderCommandContainer(pipeline);
query.Body.Add<VPRC_RenderMeshesFiltered>().RenderPass = (int)EDefaultRenderPass.OpaqueForward;

var conditional = commands.Add<VPRC_ConditionalRender>();
conditional.VariableName = "PortalVisible";
conditional.Comparison = EConditionalRenderComparison.Equal;
conditional.BoolValue = true;
conditional.Body = new ViewportRenderCommandContainer(pipeline);
conditional.Body.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.TransparentForward, false);
```

Notes:
- `VPRC_OcclusionQuery` publishes visibility into pipeline variables.
- `VPRC_ConditionalRender` can branch on bool, int, uint, float, string, vector, and matrix variables.
- For `Vector2`, `Vector3`, `Vector4`, and `Matrix4x4` variables, `Equal` and `NotEqual` use approximate comparison with `FloatTolerance`; ordered comparisons use magnitude.

## Example Cull-To-Conditional Chain

```csharp
var cull = commands.Add<VPRC_FrustumCullToBuffer>();
cull.RenderPass = (int)EDefaultRenderPass.OpaqueForward;
cull.DestinationBufferName = "VisibleOpaqueCommandIndices";
cull.VisibleCountVariableName = "VisibleOpaqueCount";
cull.CandidateCountVariableName = "OpaqueCandidateCount";

var conditional = commands.Add<VPRC_ConditionalRender>();
conditional.VariableName = "VisibleOpaqueCount";
conditional.Comparison = EConditionalRenderComparison.Greater;
conditional.IntValue = 0;
conditional.Body = new ViewportRenderCommandContainer(pipeline);
conditional.Body.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.TransparentForward, false);
```

Notes:
- `VPRC_FrustumCullToBuffer` writes visible source-command indices to a named buffer and can also publish visible-count variables for later branching.
- This pattern is useful when a later authored sub-chain should only run if a pass has any visible work.
- The current `VPRC_RenderMeshesPass` does not yet consume the authored visible-index buffer directly; the example shows the current high-value use, which is count-driven conditional execution.

## Example FXAA-To-Window Chain

```csharp
var fxaa = commands.Add<VPRC_FXAA>();
fxaa.SourceFBOName = DefaultRenderPipeline.PostProcessOutputFBOName;
fxaa.DestinationFBOName = DefaultRenderPipeline.FxaaFBOName;

var present = commands.Add<VPRC_RenderToWindow>();
present.SourceFBOName = DefaultRenderPipeline.FxaaFBOName;
present.UseTargetViewportRegion = true;
```

Notes:
- `VPRC_FXAA` can be used as a standalone authored post-process instead of relying on the default pipeline's built-in AA branch.
- `VPRC_SMAA` now follows the same pattern in the default render pipeline when the camera or effective AA mode resolves to `SMAA`.
- `VPRC_RenderToWindow` then composites the chosen final source into the active renderer window backbuffer.
- If the source is already a named texture instead of an FBO, set `SourceTextureName` instead.

## Example Capture-After-PostProcess Branch

```csharp
var tonemapOrLut = commands.Add<VPRC_IfElse>();
tonemapOrLut.ConditionEvaluator = () => useLutPreview;

var lutBranch = new ViewportRenderCommandContainer(pipeline);
lutBranch.Add<VPRC_ApplyLUT>().SetOptions(
	DefaultRenderPipeline.PostProcessOutputTextureName,
	"PreviewLutTexture",
	"PreviewPostProcessTexture");
tonemapOrLut.TrueCommands = lutBranch;

var tonemapBranch = new ViewportRenderCommandContainer(pipeline);
var tonemap = tonemapBranch.Add<VPRC_Tonemap>();
tonemap.SourceTextureName = DefaultRenderPipeline.HDRSceneTextureName;
tonemap.DestinationFBOName = DefaultRenderPipeline.PostProcessOutputFBOName;
tonemapBranch.Add<VPRC_CaptureFrame>().SourceFBOName = DefaultRenderPipeline.PostProcessOutputFBOName;
tonemapOrLut.FalseCommands = tonemapBranch;

var capture = commands.Add<VPRC_CaptureFrame>();
capture.SourceTextureName = "PreviewPostProcessTexture";
capture.OutputFilePath = Path.Combine("Build", "Captures", "preview-postprocess.png");
capture.WidthVariableName = "PreviewCaptureWidth";
capture.HeightVariableName = "PreviewCaptureHeight";
capture.SuccessVariableName = "PreviewCaptureSucceeded";
```

Notes:
- `VPRC_CaptureFrame` can run after a conditional authored post-process branch, as long as the chosen branch publishes a stable texture or FBO name for capture.
- The same command can write to disk, a pipeline buffer, or both.
- Width/height/success variables make it straightforward to branch or log off the capture result later in the same frame.
