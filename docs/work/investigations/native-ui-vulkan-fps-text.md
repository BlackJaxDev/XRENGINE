# Native UI Vulkan FPS Text Investigation

Status: resolved locally
Last updated: 2026-06-23

## Problem

The native UI FPS debug text renders on OpenGL but did not render on Vulkan. Disabling native UI batching did not fix it.

The editor should remain in ImGui mode for this repro. `Assets/UnitTestingWorldSettings.jsonc` stays on `"EditorType": "IMGUI"`; the FPS debug text is still emitted through the native screen-space UI path in that mode.

## Repro Target

- World: Unit Testing World, default scene
- Editor UI: ImGui
- Debug text node: `TestTextNode`
- Renderer: Vulkan
- Run evidence root: `Build/_AgentValidation/20260622-162937-native-ui-vulkan-fps-text/`

## Source Findings

- `EditorUnitTests.UserInterface.AddFPSText` creates `TestTextNode` after the selected editor UI is created.
- The FPS text uses `UITextComponent`, `RenderPass = OnTopForward`, `ZIndex = int.MaxValue`, the default bitmap font, and batching enabled by default.
- Screen-space UI is rendered from the main render pipeline by `VPRC_RenderScreenSpaceUI`.
- `VPRC_RenderScreenSpaceUI` pushes a synthetic parent render-graph pass before calling `ui.RenderScreenSpace(...)`.
- The nested `UserInterfaceRenderPipeline` metadata contains UI pass indices such as `OnTopForward` (`9`), but it does not contain the parent synthetic pass index.
- `VkMeshRenderer.OnRenderRequested` captures `RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex` into the Vulkan `MeshDrawOp`.

## Root Causes

### 1. Nested UI Pass Ordering

`VPRC_RenderUIBatched` preserved the parent screen-space UI render-graph pass when rendering to the swapchain. That meant batched FPS text draws inherited the parent synthetic pass index instead of the UI pipeline's `OnTopForward` pass.

On Vulkan, the draw op was then validated against `UserInterfaceRenderPipeline` metadata. The inherited parent pass was missing from that metadata, so Vulkan logged an invalid render-graph pass warning and fell the draw back to pass `-1`. The FPS text was alive, collected, and submitted, but its pass identity was wrong for Vulkan scheduling.

### 2. Bitmap Atlas Coverage

After the pass/overlay ordering fixes, solid glyph debug mode could render magenta glyph-shaped quads. That proved the draw, SSBO glyph payload, instance indexing, and screen-space matrix path were alive. Normal text still disappeared because the bitmap font atlas was configured as `R8`, while the generated/cached PNG was loaded as RGBA. The generic Vulkan RGBA-to-R8 upload path takes the red byte; bitmap glyph coverage is semantically alpha coverage. If cached atlas pixels have `RGB=0` and nonzero alpha, Vulkan uploads zero coverage and normal text is fully transparent.

## Fixes

`VPRC_RenderUIBatched.Execute` now always pushes its own `_renderPass` while rendering batched UI commands. This keeps the swapchain target unchanged, but gives Vulkan a pass index that belongs to the nested UI pipeline metadata.

Native UI text secondary command buffers are also submitted after the ImGui overlay when the scene primary preserves the swapchain for ImGui. This keeps native screen-space debug text from being hidden under ImGui when the editor is in ImGui mode.

Bitmap font atlases are normalized to one-channel `R8` coverage data at the font/atlas layer. Newly generated bitmap atlases are created as `R8/Red` coverage from alpha, and loaded legacy bitmap atlases are converted before upload. Generic Vulkan RGBA-to-R8 texture behavior remains red-channel based for non-font textures.

## Validation

- Built the editor with `dotnet build .\XREngine.Editor\XREngine.Editor.csproj /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary`.
- Validation run used `EditorType=IMGUI`; startup logged `CreateEditorUI begin: DrawSpace=Screen, EditorType=IMGUI, Rive=False`.
- After the fix, Vulkan draw trace logs show `UIBatchTextQuadMesh: CmdDrawIndexed(6) pass=9 target=<swapchain> dynRender=True ... blend=True depthTest=False`.
- The final validation log had zero matches for `pass 100085 is missing`, `invalid render-graph pass index`, or `OpDroppedNoPass`.
- MCP viewport screenshot capture was saved under the run root, but it did not reliably include swapchain/UI overlay content, so logs were the authoritative validation signal for this pass-index bug.

Additional 2026-06-23 validation:

- Focused tests passed:
  - `FontGlyphSetSerializationTests`
  - `Mipmap2DTests`
  - `SwapchainContextCoalescingTests`
  - `VulkanP0ValidationTests.CommandBufferReuse_InvalidatesOnFrameOpsPlannerRevisionResourcesAndViewport`
- Unit-testing settings remained `EditorType=IMGUI` and `RenderAPI=Vulkan`.
- Unit-testing Vulkan MCP run: `Build/_AgentValidation/20260623-continue-native-ui-vulkan-fps-text/`.
- Logs showed `TestTextNode` alive and batching normal text (`debugMode=0`) with 40-78 glyphs, on-screen projected NDC bounds, and `atlasType=Bitmap`.
- `font-diagnostics.log` showed the Roboto bitmap atlas normalized as `4800x5580`, one mip, `R8`, linear filtering, no mip generation.
- `log_vulkan.log` showed the bitmap atlas uploaded as a dedicated `R8Unorm` image (`4800x5580`) and the `UITextBatched.vs/fs` pipeline compiling for `UIBatchTextMaterial`.
- MCP viewport screenshots captured the scene buffer but still did not include overlay/UI content, so visual confirmation remains better done from the live editor window or RenderDoc rather than MCP screenshots.

Follow-up:

- If glyphs are magenta, check `UITextComponent.BatchedDebugMode`. `SolidGlyphQuads` intentionally sets `TextDebugMode=1`, and `UITextBatched.fs` renders glyph-shaped quads as magenta to prove geometry/SSBO/indexing are alive.
- The Unit Testing World FPS text was accidentally left on `EBatchedTextDebugMode.SolidGlyphQuads` during diagnosis. Resetting it to `None` returns normal atlas coverage/color rendering.

Additional magenta recheck:

- A later live run still showed magenta glyph blocks while source had `BatchedDebugMode=None` and fresh logs showed `debugMode=0`.
- The remaining cause was an unconditional diagnostic probe at the top of `Build/CommonAssets/Shaders/Common/UITextBatched.fs`:
  `FragColor = vec4(1.0, 0.0, 1.0, 1.0); return;`
- Removing that unconditional return leaves only the explicit `TextDebugMode == 1` magenta branch. The apparent triangle flicker is consistent with seeing the two triangles that make each glyph quad while the fragment shader is forced solid magenta.
- Rebuilt `XREngine.Editor` successfully afterward. Fresh Vulkan UI logs show FPS text batches and render groups with `debugMode=0`; desktop window capture no longer showed the magenta glyph blocks in the visible editor surface.

Final descriptor-readiness fix:

- A later atlas-coverage capture still showed black glyph quads, which meant the text draw and descriptor binding existed but `Texture0` sampled zero coverage from the atlas.
- `VkMeshRenderer.Descriptors.TryResolveImage` and related material descriptor paths were creating a Vulkan image/view/sampler via `GetOrCreateAPIRenderObject(..., generateNow: true)` and immediately writing the descriptor. `generateNow` creates handles but does not guarantee CPU mip data has been uploaded.
- `VkTexture.IsDescriptorReady` now requires the texture to be generated, descriptor-clean, not invalidated, and uploaded. `IVkImageDescriptorSource.TryEnsureDescriptorReadyForUse(...)` lets descriptor writers force the upload/readiness path before publishing image descriptors.
- Mesh material descriptors, standalone material descriptors, compute descriptors, and the bindless material texture table now call that readiness hook before writing sampled/storage descriptors.

Final validation:

- Build passed: `dotnet build .\XREngine.Editor\XREngine.Editor.csproj /m:1 /nodeReuse:false /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary`.
- Atlas coverage debug capture after the descriptor-readiness fix:
  `Build/_AgentValidation/20260623-1228-native-ui-vulkan-fps-text/mcp-captures/window_editor_atlas_coverage_descriptor_ready_20260623_1358.png`
  showed real glyph-shaped coverage instead of solid black rectangles.
- Normal debug mode was forced with `XRE_FPS_TEXT_BATCHED_DEBUG_MODE=0` and captured from the foreground editor window:
  `Build/_AgentValidation/20260623-1228-native-ui-vulkan-fps-text/mcp-captures/window_editor_normal_debug0_descriptor_ready_foreground_20260623_1413.png`.
  The Vulkan FPS overlay rendered readable text with no magenta quads.
- Latest normal-mode log: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-23_14-07-35_pid42648/`.
  `log_ui.log` shows FPS text batches/render groups with `debugMode=0`, and `log_vulkan.log` shows the text `Texture0` descriptor using the real `R8Unorm` atlas image.
- Residual validation noise in that scene run includes unrelated depth/stencil descriptor layout errors (`VUID-vkCmdDraw-None-09600`); they did not block the native UI FPS text overlay.

Bitmap anti-aliasing follow-up:

- The remaining aliasing was traced to the bitmap atlas sampling path, not premultiplied alpha. `UITextBatched.fs` outputs straight alpha, and UI text uses straight-alpha blending, so changing only the fragment output to premultiplied alpha would be mismatched.
- Bitmap atlases were still normalized as single-mip `R8` textures with `MinFilter=Linear` and `SmallestAllowedMipmapLevel=0`. The FPS overlay draws a 128 px layout-em bitmap font at roughly 22 px, so Vulkan had to minify directly from mip 0.
- `FontGlyphSet` now builds explicit CPU `R8/Red/UnsignedByte` coverage mip chains for newly generated bitmap atlases and for loaded/cached bitmap atlases that are missing the expected chain. The downsampler box-filters alpha coverage, including odd texture edges, and bitmap atlases now use `LinearMipmapLinear` minification with the full mip range exposed.
- This stays on the bitmap path; distance-field/MTSDF atlas handling is unchanged.

Bitmap anti-aliasing validation:

- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~FontGlyphSetSerializationTests|FullyQualifiedName~VulkanP0ValidationTests.BitmapFontAtlas_ExtractsAlphaCoverageIntoR8RedChannel" /m:1 /nodeReuse:false /p:UseSharedCompilation=false` passed 9/9.
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /m:1 /nodeReuse:false /p:UseSharedCompilation=false /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary` passed with 0 warnings and 0 errors.

Overlay layout and draw-stat follow-up:

- The native FPS/debug overlay now reserves three stable primary rows:
  - networking: RTT, packets/sec, and bytes/sec;
  - frame rates: render Hz/ms plus Vulkan CPU/GPU frame timings, update ms, and fixed ms;
  - draw stats: draw calls, multi-draw calls, triangles, and GPU fallback events.
- Numeric fields are formatted into fixed-width slots, and the FPS text transform now uses a fixed overlay width/height. This keeps the text region from resizing as values gain or lose digits.
- Vulkan direct mesh draw stats were unreliable because the counters were partly recorded while command buffers were recorded. Fast command-buffer reuse can skip that recording path, and indexed `CmdDrawIndexed` only added triangle counts, not draw-call counts.
- Vulkan draw stats are now published at `FrameOp` enqueue time for `MeshDrawOp`, `IndirectDrawOp`, and mesh-task indirect-count ops. Command recording no longer mutates the public frame draw counters, so reused and freshly recorded Vulkan frames report through the same path.

Overlay layout and draw-stat validation:

- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~VulkanP0ValidationTests.VulkanFrameDrawStats_PublishFromFrameOpsInsteadOfCommandRecording|FullyQualifiedName~VulkanP0ValidationTests.NativeFpsOverlay_UsesStableThreeRowLayout" /m:1 /nodeReuse:false /p:UseSharedCompilation=false` passed 2/2.
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /m:1 /nodeReuse:false /p:UseSharedCompilation=false /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary` passed with 0 warnings and 0 errors.

Overlay compact multiline follow-up:

- The previous fixed-width overlay was visually too wide. The overlay now keeps networking and draw stats compact and uses tighter numeric slots.
- The FPS text component already used the native text outline path; the stroke is now fully opaque black with a slightly thicker outline for readability on bright backgrounds.
- A monospaced font would be ideal for this debug overlay, but the committed common font assets currently provide Roboto/Lato and symbol fallbacks rather than a dedicated monospace face. This pass keeps the existing font source and relies on explicit value padding.

Overlay alignment and outline follow-up:

- The update/fixed timings now share one `loop` row, while draw stats use clearer labels (`calls`, `multi`, `tris`, `cpu fallback`) instead of terse abbreviations.
- The overlay text itself is now left-aligned inside the fixed centered box. Center alignment was still recentering each line based on measured glyph width, so changing proportional or monospace digits could shift the line even when fields were padded.
- The black outline did not reliably appear because batched glyph quads were still tight around the glyph rect. The batched text vertex shaders now expand outlined glyph quads and UVs by the outline radius while keeping the original UV bounds for bleed-safe stroke sampling.

Overlay font/background correction:

- A foreground screenshot over a bright green/high-frequency mesh showed that the short-lived panel and monospace font made the overlay feel worse.
- The FPS overlay is back to a plain text node using the default UI bitmap font. There is no black background/backing component now; contrast is intended to come from the text's black glyph stroke.
- The line layout remains stable and left-aligned, with update/fixed on the same `loop` row. Draw stats now spell out `cpu fallback` instead of `fb` or `gpu fallback`.
- The outline thickness is increased, and the batched shader still expands outlined glyph quads so the stroke has room to render.

Overlay compact multiline validation:

- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~VulkanP0ValidationTests.VulkanFrameDrawStats_PublishFromFrameOpsInsteadOfCommandRecording|FullyQualifiedName~VulkanP0ValidationTests.NativeFpsOverlay_UsesStableCompactMultilineLayout" /m:1 /nodeReuse:false /p:UseSharedCompilation=false` passed 2/2.
- A normal editor build could not overwrite `Build\Editor\...\XREngine.dll` because a live `XREngine.Editor` process was holding the file.
- Isolated editor compile passed with 0 warnings and 0 errors using `OutDir=Build\_AgentValidation\20260623-1451-fps-overlay-layout\temp-build\editor\`.
- After the alignment/outline follow-up, the same focused test filter passed 2/2 again, and `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /m:1 /nodeReuse:false /p:UseSharedCompilation=false /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary` passed with 0 warnings and 0 errors.

Overlay readability correction:

- The overlay now uses the pre-monospace font behavior again: `AddFPSText` assigns the optional font argument directly and lets `UITextComponent` resolve the normal default UI font when it is null.
- The text is centered again instead of left-aligned inside the fixed overlay region.
- The short-lived black offset-copy stroke has been removed. It made horizontal glyph edges look heavier because full bitmap copies stacked above and below each glyph.
- Bitmap outline sampling is now shader-based in screen-pixel units. `UITextBatched.fs` and `Text.fs` use `dFdx/dFdy(FragUV0)` to convert stroke sample offsets into UV offsets, so `OutlineThickness=2` means roughly two output pixels rather than two source-atlas texels.
- This fixes the earlier weak shader outline: the first bitmap shader implementation sampled neighboring atlas texels, which was far too small after minifying the large bitmap font atlas down to the FPS overlay size.
- Increasing outline thickness later made glyphs look compressed because each expanded glyph quad was still rendering fill and outline in one transparent instanced draw. Expanded quads overlap neighboring glyphs, so one glyph's outline could blend over another glyph's fill depending on instance order.
- Batched bitmap text with outlines now renders in two ordered layers: all outline coverage first, then all fill coverage. The shader keeps the combined path for non-split cases, but the FPS overlay path uses the split layers so foreground glyph coverage is always on top of the black dilation.

Outline thickness follow-up:

- The first split-layer pass still let the vertex shader expand both the outline draw and the fill draw. That meant the fill layer could still run over the enlarged stroke rectangle instead of the original glyph rectangle.
- The batched text vertex shaders now read `TextRenderLayer`: outline/combined draws expand the glyph rect and UV range, while the fill draw keeps the original glyph rect/UVs.
- `UIBatchTextRenderer` now sets `CaptureUniformsOnRender = true` so Vulkan snapshots `TextRenderLayer` separately for the queued outline and fill draws. This avoids both queued draws collapsing to whichever mutable material value happens to be current when command buffers are recorded.
- Reusable Vulkan command buffers now refresh frame data for the dynamic UI text secondary ops as well as the primary/static ops. That keeps per-draw text material uniforms such as `TextRenderLayer` current when the dynamic UI secondary command buffer is reused.

Outline thickness correction:

- The split-layer fix also needed the vertex-stage layer selector to be an explicit vertex uniform (`TextRenderLayer_VTX`). Vulkan's material/auto-uniform path can otherwise resolve the fragment and vertex stage values differently enough that the fill draw still risks using outline-expanded geometry.
- The bitmap outline mask now treats the stroke as an outside dilation: `outlineMask = stroke * OutsideGlyphMask(fill)`. The previous `stroke - fill` formula still left black under partially transparent glyph edge pixels, so the fill pass blended over black and made glyphs look compressed.
- The same outside-mask stroke formula is applied to `Text.fs` so non-batched bitmap text outlines do not inherit the old edge-darkening behavior.
- The outside mask is intentionally strict (`smoothstep(0.0, 0.02, fill)`), so black stroke coverage is suppressed as soon as the glyph contributes visible fill coverage. This avoids the stroke showing through the bitmap antialiasing ramp.

Signed glyph expansion correction:

- Bitmap glyph layout stores height as a negative signed size (`scaleY = -glyph.Size.Y * scale`). The outline vertex expansion was adding `+2 * thickness` to both size components, which shrank negative-height glyph quads vertically instead of expanding them.
- The batched vertex shaders now expand along each component's sign: positive widths grow with `+2t`, negative heights grow with `-2t`, and the origin moves in the matching signed direction. UV expansion remains based on `abs(glyphSize)` so the original glyph-to-atlas slope stays stable.
- With signed expansion fixed, batched bitmap text no longer needs the separate outline/fill draw path. It renders once in the combined shader path, so the outline is composited around the fill in one glyph draw instead of being drawn as a black underlay pass.

Outline isotropy correction:

- The bitmap stroke sampler used an integer disk kernel. That makes cardinal offsets reach the requested radius, but diagonal offsets land at shorter effective distances unless the next integer diagonal sample fits inside the disk.
- `UITextBatched.fs` and `Text.fs` now sample stroke dilation as screen-space radial rings with 16 normalized directions. Diagonal and off-axis samples use normalized unit vectors, so the outline radius is much more even around slanted glyph edges while staying on the bitmap atlas path.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~VulkanP0ValidationTests.NativeFpsOverlay_UsesStableCompactMultilineLayout" --logger "console;verbosity=minimal" /m:1 /nodeReuse:false /p:UseSharedCompilation=false /p:OutDir="Build\_AgentValidation\20260624-1026-fps-outline-isotropy\temp-build\tests\"` passed 1/1.

Bright-background edge support:

- The previous outside-only outline mask removed black coverage as soon as bitmap fill coverage became non-zero. That kept interiors clean, but left the antialiased white edge blending directly into bright backgrounds.
- The bitmap outline mask now fades out across the glyph edge instead: full stroke outside the glyph, dark support under low/medium antialias coverage, and no stroke under solid fill. This keeps the text as plain stroked glyphs without reintroducing a panel/background.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~VulkanP0ValidationTests.NativeFpsOverlay_UsesStableCompactMultilineLayout" --logger "console;verbosity=minimal" /m:1 /nodeReuse:false /p:UseSharedCompilation=false /p:OutDir="Build\_AgentValidation\20260624-1036-fps-outline-edge-support\temp-build\tests\"` passed 1/1.

Non-batched outline parity:

- Disabling batching still looked worse because `Text.vs` kept non-batched glyph quads tight to the original glyph rect. The fragment shader could sample the stroke, but pixels outside the original glyph quad were clipped before the fragment stage.
- Non-batched text vertex shaders (`Text.vs`, rotatable, stereo, and rotatable stereo) now expand glyph quads and UVs by `OutlineThickness` using the same signed-size logic as `UITextBatched.vs`, while preserving original `GlyphUVBounds` for atlas-bleed protection.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~VulkanP0ValidationTests.NativeFpsOverlay_UsesStableCompactMultilineLayout" --logger "console;verbosity=minimal" /m:1 /nodeReuse:false /p:UseSharedCompilation=false /p:OutDir="Build\_AgentValidation\20260624-1050-text-outline-batching-parity\temp-build\tests\"` passed 1/1.
- `glslangValidator -S vert -V -R` could not validate these shaders directly because their existing engine default uniforms are not declared as explicit Vulkan UBO bindings. That validation blocker predates this outline change.

Outline spacing option:

- `UITextComponent` and standalone `UIText` now expose `OutlineAffectsSpacing`. When enabled, layout adds horizontal glyph spacing equal to `OutlineThickness` and adds the same amount to line spacing.
- The FPS debug overlay enables this setting so the black stroke has breathing room without changing the default behavior for other text.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~VulkanP0ValidationTests.NativeFpsOverlay_UsesStableCompactMultilineLayout" --logger "console;verbosity=minimal" /m:1 /nodeReuse:false /p:UseSharedCompilation=false /p:OutDir="Build\_AgentValidation\20260624-1110-text-outline-affects-spacing\temp-build\tests\"` passed 1/1.

Outline spacing unit correction:

- Horizontal glyph spacing in `FontGlyphSet.GetQuads` is applied in font layout units before scaling by `fontSize / LayoutEmSize`, while line spacing is already in final output units.
- `OutlineAffectsSpacing` now converts the requested output-pixel spacing back into font layout units for horizontal spacing, so `OutlineThickness=2` contributes approximately two screen/layout pixels between glyph advances instead of being scaled down to a fraction of a pixel.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~VulkanP0ValidationTests.NativeFpsOverlay_UsesStableCompactMultilineLayout" --logger "console;verbosity=minimal" /m:1 /nodeReuse:false /p:UseSharedCompilation=false /p:OutDir="Build\_AgentValidation\20260624-1130-text-outline-spacing-units\temp-build\tests\"` passed 1/1.
