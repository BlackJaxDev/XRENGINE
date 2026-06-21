# Vulkan Editor Gizmo And Depth Hit Investigation

## Problem

- Vulkan editor transform gizmo renders without expected axis colors.
- Zoom-to-depth-hit works on OpenGL but not Vulkan.
- Latest user report: neither issue is fixed. The transform tool 3D should be visible at the selected object but is effectively invisible in Vulkan; the ImGui editor also appears to have lost spacing between items; zooming toward a depth hit still fails.

## Findings

- The editor depth hit path binds the render pipeline forward FBO, then calls `XRViewport.GetDepth(...)`.
- OpenGL depth readback honors the bound read FBO through `glReadPixels`.
- Vulkan pixel and screenshot readback already prefer `_boundReadFrameBuffer`.
- Vulkan synchronous `GetDepth(int x, int y)` was still reading `_swapchainDepthImage`, so editor depth picking could sample stale or unrelated window depth instead of the editor viewport FBO.
- The transform gizmo line and arrowhead materials still assign red/green/blue `MatColor` parameters. Their Vulkan geometry shaders are rewritten into auto-uniform blocks and compile successfully.
- Common Vulkan push-constant ranges were only visible to vertex, fragment, and compute stages. Geometry-backed gizmo programs should not depend on a layout that hides common push constants from geometry shaders.
- Runtime validation showed the first push-constant visibility attempt was incomplete: pipeline layouts were widened, but `vkCmdPushConstants` was still called with the old stage mask.
- Vulkan framebuffer readback uses top-left image coordinates while the editor depth-hit cursor path produces bottom-left coordinates for OpenGL.
- After the gizmo constructor crash was fixed, MCP screenshots showed the transform gizmo geometry does render in Vulkan, but its color is nearly black/muted. That points at the default pipeline on-top/composite state rather than absent geometry.
- Recent Vulkan logs showed `OnTopForward` referencing missing `FullOverdrawCountFBO` metadata while full-overdraw visualization was disabled.
- `VPRC_IfElse`/`VPRC_ConditionalRender` describe disabled branches for graph planning, and `VPRC_RenderFullOverdrawPass` reused the normal scene render pass indices while writing the full-overdraw count FBO. Because render pass metadata is keyed by pass index, this polluted the normal `OnTopForward` metadata with disabled debug FBO attachments.
- MCP captures from the current Vulkan run show the selected-object transform tool is still not visibly colored in `HDRSceneTex`, `PostProcessOutputTexture`, `FinalPostProcessOutputTexture`, or the final screenshot.
- The current Vulkan draw trace records `GizmoLine.gs/GizmoLine.fs` and `GizmoArrowHead.gs/GizmoTriangle.fs` draws. Those draw calls use `LineList`, blending enabled, depth test/write disabled, all color channels writable, and the expected full viewport/scissor. That rules out missing command collection, missing geometry shader pipeline creation, and color-write masking as the primary cause.
- Current captures show the transform tool geometry is faintly present but nearly black in HDR/final output. Since the command state is sane, this now points more strongly at draw-time data feeding the gizmo materials, especially `MatColor`/line-width auto-uniforms for geometry-stage programs, than at missing pass submission.
- The current diagnostic patch logs target/pass names for Vulkan mesh draws. It should either be removed or guarded appropriately once the root cause is fixed.
- Descriptor cache/resource fingerprint ordering was a real correctness issue, but MCP validation after that change still showed the gizmo nearly black. That attempted fix is not sufficient.
- The latest log folder has an empty `log_vulkan.log`; the useful trace is landing in `log_meshes.log` and `log_general.log`. The expected `VkMaterialAutoUniform`/`MatColor` logs were absent, so the next isolation step is to trace snapshot capture and auto-uniform member writes directly.
- Focused mesh diagnostics from `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-20_17-02-20_pid38656` show `GizmoLine` and `GizmoArrowHead` auto-uniform blocks are present and populated. `MatColor`, `LineWidth`, `ArrowHeadLengthPixels`, and `ArrowHeadHalfWidthPixels` all report `wrote=True` with the expected red/green/blue/gray values.
- That rules out missing material parameter upload as the current primary cause. The remaining suspect is render-pass ordering or compositing: the same trace shows successful gizmo line/arrow draw calls into `ForwardPassFBO`, but their visual result remains nearly black/invisible in `HDRSceneTex` and final screenshots.
- The current trace also shows early `EnsurePipeline FAILED` entries for gizmo line-list pipelines before later successful draws. This looks like async pipeline warmup noise unless it correlates with the invisible frame; it does not explain persistent faint output after successful draws.
- The draw trace has a concerning ordering clue: `OnTopForward`/gizmo draws appear in the log near `pass=9 target=ForwardPassFBO` before a later `Skybox.FullscreenTriangle` draw at `pass=0 target=ForwardPassFBO`. The next step is to verify whether `VulkanRenderGraphCompiler.SortFrameOps` or pass metadata can allow on-top editor geometry to be recorded before scene/background passes that overwrite it.
- User visual validation after preserving render-graph declaration order reports the transform tool is visible and colored again.
- The remaining white/half-quad artifact and bloom suppression line up with the gizmo stencil bypass: `XRMaterial.ConfigureGizmoMaterial` writes stencil bit `0x80`, and `PostProcess.fs` / `PostProcessStereo.fs` return raw `HDRSceneTex` for those pixels before adding bloom. For transparent generated line/arrow quads, that can stamp a postprocess-bypass footprint larger than the visible colored pixels.
- MCP viewport PNG captures are SDR readbacks and do not match the HDR monitor output path. They can understate or hide HDR-only overbright artifacts, so white-triangle validation needs the live HDR editor window until MCP gains an HDR/EXR capture path.
- After the bloom/stencil fix, the remaining white triangle is likely not bloom suppression but undefined geometry-shader varyings. `GizmoLine.gs` emitted a triangle strip after setting flat color/half-width outputs only once; GLSL geometry shader outputs become undefined after each `EmitVertex()`, so one half of the quad could receive garbage values that show up as white in HDR.
- Added MCP depth-hit diagnostics: `probe_editor_depth_hit`, `zoom_editor_camera_at_depth_hit`, `probe_render_pipeline_depth`, and `sweep_render_pipeline_depth`. The render-pipeline probes can include raw Vulkan depth bytes to distinguish decode/binding problems from real attachment contents.
- Raw Vulkan probing showed `ForwardPassFBO`/`DepthStencil` contained real scene depth at visible pixels, while `XRViewport.GetDepth(fbo, ...)` still returned `1.0`. Example: raw bytes decoded to about `0.963`, but the generic helper returned clear depth.
- Root cause of the Vulkan-only zoom-to-depth-hit failure: Vulkan `VkFrameBuffer` did not subscribe to `XRFrameBuffer.BindForReadRequested`/unbind events. OpenGL's framebuffer wrapper does, so `XRViewport.GetDepth(fbo, ...)` correctly binds the requested FBO under OpenGL. Vulkan therefore fell through to swapchain/default depth instead of the provided render-pipeline FBO.
- User reported the first Vulkan FBO event fix made the scene randomly flicker black around camera movement. Cause is likely that the initial fix hooked read, write, and combined framebuffer bind events. Vulkan rendering does not rely on the old OpenGL-style write/full FBO event path, and mutating `_boundDrawFrameBuffer` from those generic events can perturb queued render state.

## Attempted Fixes

- Make Vulkan synchronous depth readback prefer the currently bound read FBO, matching color/screenshot readback. Fall back to swapchain depth only when no bound FBO depth can be resolved.
- Expand common push-constant stage visibility to include tessellation and geometry shader stages in both combined programs and program pipelines.
- Share the widened push-constant stage mask with `vkCmdPushConstants` call sites.
- Convert editor depth-hit readback Y coordinates for Vulkan while preserving the normalized bottom-left coordinate used for unprojection.
- Reverted the transform gizmo shader-source detour; the gizmo issue is now being treated as a default render pipeline / render-graph compositing problem.
- Fixed `VPRC_RenderFullOverdrawPass` to use synthetic full-overdraw graph passes while still selecting meshes from the source scene render pass. This keeps full-overdraw count FBO metadata out of normal passes like `OnTopForward`.
- Marked post-process output FBO metadata as color-only so late debug overlay planning does not try to attach destination depth/stencil to `PostProcessOutputFBO`/`FinalPostProcessOutputFBO`.
- Added focused render-pipeline metadata tests for post-process output depth/stencil sampling versus attachment behavior.
- Added temporary Vulkan material uniform handling for gizmo `MatColor`, `LineWidth`, `ArrowHeadLengthPixels`, and `ArrowHeadHalfWidthPixels`. This confirmed the values are written but did not solve the visible transform tool issue, so the fix should remain pipeline/compositing-oriented.
- Added draw-trace target/pass logging around `RecordDraw` to prove the gizmo reaches `OnTopForward`/`ForwardPassFBO`.
- Changed Vulkan mesh descriptor setup to materialize auto/engine uniform buffers before computing descriptor resource fingerprints and allocation keys. This keeps descriptor-set cache identity aligned with the buffers actually written into the sets.
- Added focused gizmo binding and auto-uniform diagnostics routed to `log_meshes.log`. These diagnostics confirmed the per-draw material values are correct, so they should be removed or left behind a narrow diagnostic guard after the compositor/order fix is found.
- Changed Vulkan render-graph ready-pass tie breaking to use command declaration order instead of low numeric pass index, preserving the default pipeline's intended scene/postprocess/on-top sequence.
- Disabled the transform tool line/arrow materials' gizmo postprocess stencil bypass while leaving them in `OnTopForward`, so they no longer cut holes through bloom with their transparent generated quad footprint.
- Updated `GizmoLine.gs` to write all line varyings for every emitted vertex instead of relying on values set before the first `EmitVertex()`.
- Extended MCP `capture_render_pipeline_texture` with `output_format=png|exr|hdr`. PNG can now apply engine tonemapping modes (`Linear`, `Gamma`, `Clip`, `Reinhard`, `Hable`, `Mobius`, `ACES`, `Neutral`, `Filmic`, `AgX`, `GT7`) with `exposure`, `gamma`, `mobius_transition`, and `encode_srgb`; EXR/HDR export direct HDR texture values.
- Added raw-depth output to the MCP render-pipeline depth probes for Vulkan.
- Hooked Vulkan `VkFrameBuffer` read bind/unbind event handlers to `VulkanRenderer.BindFrameBuffer(...)`, matching the part of OpenGL's `XRFrameBuffer` event behavior needed by `XRViewport.GetDepth(fbo, ...)`.
- Removed the write/full Vulkan FBO event hooks after they correlated with black-frame flicker during camera movement.

## Validation

- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~VulkanDynamicRenderingMigrationTests|FullyQualifiedName~VulkanShaderCompilationRegressionTests"` passed: 68 tests.
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore` passed with 0 warnings and 0 errors.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~RenderPipelineResourceLifecycleTests"` passed: 24 tests.
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore` passed with 0 warnings and 0 errors after the descriptor-cache ordering change.
- MCP validation after the descriptor-cache ordering change captured `HDRSceneTex`, `FinalPostProcessOutputTexture`, and the viewport screenshot under `Build\McpCaptures\vulkan-gizmo-depthhit\descriptor-cache-fix\`; the transform tool was still nearly black/invisible.
- MCP captures from the latest focused run are under `Build\McpCaptures\vulkan-gizmo-depthhit\gizmo-binding-trace-meshlog\`. The final screenshot still shows the transform tool as tiny/dark against the selected object.
- User reported the transform tool visibility/colors are working after the declaration-order render-graph change.
- Visual MCP captures after disabling the transform tool stencil bypass are under `Build\McpCaptures\vulkan-gizmo-depthhit\stencil-bypass-off\`. The transform tool remains colored, and the bright-floor capture no longer shows the white/half-quad postprocess footprint or a local bloom cutout around the gizmo.
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore` passed after the `GizmoLine.gs` per-vertex output change. SDR MCP sanity capture is under `Build\McpCaptures\vulkan-gizmo-depthhit\line-gs-per-vertex-outputs\`; live HDR editor validation is still needed because SDR PNGs are darker than the actual HDR output.
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore` passed with 0 warnings and 0 errors after the MCP HDR capture change.
- MCP validation captured `HDRSceneTex` to `Build\McpCaptures\vulkan-gizmo-depthhit\hdr-capture\RenderPipeline_HDRSceneTex_20260620_180544.exr`, `.hdr`, and ACES-tonemapped `.png`. Output sizes were 16.6 MB, 8.3 MB, and 3.0 MB respectively, and the PNG opened successfully for visual inspection.
- User reported the first MCP HDR captures were upside down. Cause: `capture_render_pipeline_texture` defaulted `flip_vertically=true`, which was a caller-side guess. The tool now auto-resolves vertical export orientation from the active render backend and `RenderClipSpacePolicy.FramebufferTextureYDirection(...)`; `flip_vertically` is nullable and only forces an override when explicitly supplied. The MCP response reports backend, clip-space Y, framebuffer texture Y, auto flip, and effective flip.
- MCP auto-orientation validation captured `HDRSceneTex` under `Build\McpCaptures\vulkan-gizmo-depthhit\hdr-capture-auto-orientation\`. The Vulkan/YUp run reported `framebuffer_texture_y_direction=YDown`, `auto_flip_vertically=false`, `flipped_vertically=false`, and the ACES PNG was upright.
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore` passed with 0 warnings and 0 errors after the Vulkan framebuffer bind-event fix.
- MCP `probe_render_pipeline_depth` with `include_raw=true` now reports matching generic and raw values from `ForwardPassFBO`: center sample depth `0.98433745`, valid depth true, raw `D24UnormS8Uint` bytes `89-FD-FB-00`.
- MCP `zoom_editor_camera_at_depth_hit` at the viewport center reported `usedDepthHit=true` and `transformChanged=true`; a follow-up probe remained valid (`0.9825972`). This validates the editor pawn can now zoom toward a Vulkan depth hit.
- RenderDoc was not needed for this iteration because the raw MCP probe proved the FBO depth attachment contents were valid and isolated the bug to Vulkan's generic FBO binding path. Use RenderDoc next if future symptoms show mismatched attachment contents, store/load problems, or depth/stencil data disagreement with MCP raw bytes.
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore` passed with 0 warnings and 0 errors after narrowing the Vulkan FBO hooks to read-only.
- MCP read-only hook validation captured non-black frames before and after a depth-hit zoom under `Build\McpCaptures\vulkan-depthhit-zoom\read-bind-only\` and `Build\McpCaptures\vulkan-depthhit-zoom\read-bind-only-after-zoom\`. The center depth probe remained valid (`0.98433745` before zoom, `0.9825972` after zoom).

## Next Isolation Steps

- If the transform tool color needs to bypass tonemap without affecting scene bloom, move editor gizmos to a true late overlay after final postprocess instead of using an in-scene stencil early-out.
- Ask the user to validate mouse-wheel zoom-to-depth-hit in the live Vulkan editor, because MCP confirms the depth-hit and transform movement path but does not physically exercise OS mouse-wheel input.
