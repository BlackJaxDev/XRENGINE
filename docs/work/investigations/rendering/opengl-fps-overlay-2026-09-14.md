# OpenGL native FPS overlay missing — 2026-09-14

## Problem

The bottom native FPS/debug text is absent in the user's full editor-window screenshot from MCP session `inspector-layout-0914`. The ImGui inspector shows an active `TestTextNode`, populated text, Roboto-Medium, batching enabled, and normal batched debug mode. The diagnostic text identifies OpenGL / DefaultRenderPipeline / CpuDirect.

## Confirmed cause

The full font atlas is incorrectly registered for generic imported-texture streaming when its material binds it:

1. `GLMaterial.SetTextureUniforms` calls `XRTexture2D.RecordImportedTextureMaterialBinding`.
2. `ImportedTextureStreamingManager.EnsureImportedStreamingTexturesRegistered` calls `TryRestoreImportedTextureStreamingSource`. The font atlas has a generated PNG `OriginalPath`, so it passes the generic registration check.
3. `GLTieredTextureResidencyBackend` publishes a small preview through `XRTexture2D.ApplyResidentData`, replacing the atlas mipmaps and therefore its current width/height.
4. `FontGlyphSet.TryGetLayoutResourceIssue` compares full-resolution glyph coordinates against the shrunken texture dimensions and rejects the atlas.
5. `EnsureLayoutResourcesReady` attempts one source reimport in place using default import options. The replacement atlas enters the same streaming path. With recovery already consumed, `GetQuads` clears its output on subsequent calls.
6. `UITextComponent.UpdateText` sets its draw instance count to zero while the FPS text string continues updating.

`GlyphRelativeTransforms` is an optional per-glyph animation adjustment dictionary. An empty dictionary is normal and does not indicate missing generated glyphs.

## Evidence

Evidence root: `Build/_AgentValidation/20260914-112517-fps-overlay/`.

The original pictured run's retained logs are under `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260914-110815-inspector-layout-0914/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-09-14_11-14-51_pid44952/`. Its `log_ui.log:327`, at 11:15:11.029, reports zero glyphs and 2D/3D instances, unchanged bottom overlay bounds, and a 55×64 atlas. Relevant logs are copied into the evidence root's `logs/original/`.

Matching isolated reproduction: `fps-overlay-0914`, OpenGL process 22376, log session `xrengine_2026-09-14_11-34-00_pid22376`.

- Startup: 893 font glyph definitions, a 4800×5580 bitmap atlas with 13 mip levels, and 248 drawable FPS glyphs with on-screen projected bounds.
- `log_textures.log:25`, 11:34:10.985: `ApplyResidentData` replaces the 4800×5580 / 13-mip atlas with a 55×64 / one-mip preview.
- `log_ui.log:324`: 200 successful preparations out of 203. Shader preparation delay was transient.
- The one-time recovery reimports with default Auto options and bitmap fallback, changing layout em 128 to 192 and using the source-adjacent atlas path. This is recovery triggered by streaming, not evidence of an independent editor reimport.
- The replacement atlas is also reduced from 6960×8160 to 55×64 at 11:34:22.981.
- `log_ui.log:328`, 11:34:23.248: zero glyphs/instances, bounds still 1180×210 at bottom margin 26, atlas 55×64.

## Validation

- Isolated editor build passed: 0 warnings, 0 errors.
- Current local world settings had changed to Vulkan/Advanced since the pictured run. Matching reproduction used per-session `XRE_UNIT_TEST_RENDER_API=OpenGL` and `XRE_UNIT_TEST_RENDER_PIPELINE=DefaultRenderPipeline` overrides without editing those settings.
- Viewed `mcp-captures/Screenshot_20260914_113428_868_3168a8d5356044049254d78e6b5f1844.png`, captured with `include_screen_space_ui=true`: full ImGui surface visible, bottom overlay absent.
- A second camera view was applied; subsequent readbacks timed out. A complex MCP property query also hit JSON serialization failure. Neither is used as evidence for the font diagnosis.
- `rdc doctor` passed. GPU capture was unnecessary once runtime logs identified zero drawable glyphs and the exact atlas replacement.
- Stopped only the investigation-owned `fps-overlay-0914` session. Relevant final logs were copied into the evidence root.
- Native read-only review independently confirmed the material-binding → streaming → invalid atlas → exhausted recovery sequence.
- Optional broker check returned no evidence: its selected gpt-5.6-luna API request failed because no API credits remained. An earlier context request was rejected because the broker excludes Build paths.

## Implemented correction and status

Give generated font atlases an explicit full-residency/streaming opt-out, honored by lazy imported-texture registration for both bitmap and distance-field atlases. `PreferSynchronousGpuUpload` already protects bitmap font upload timing but currently does not prevent streaming registration. Preserve atlas source provenance. Keep layout validation: removing it alone leaves glyph UV normalization using the wrong dimensions. Any future support for streaming font atlases needs stable logical atlas dimensions for UVs and validation.

The user authorized implementation after the diagnosis. The fix is now in:

- `XRTexture2D.ImportedStreaming.cs`: an internal, runtime-only `AllowAutomaticImportedTextureStreaming` policy defaults to true. `TryRestoreImportedTextureStreamingSource` returns false when the owner disables it. Explicit streaming APIs are unchanged.
- `FontGlyphSet.cs`: assigning `Atlas` disables automatic streaming before publishing the texture to property observers. The MemoryPack constructor now uses that setter too. This covers bitmap and distance-field generation, cached deserialization, and normal atlas replacement without a cache-format change.

Independent source review found no blocking issues. The initialization policy intentionally does not cancel streaming already registered by an explicit caller; generated/cached font atlases establish it before material exposure.

### Fixed-run validation

- Rebuilt the isolated editor with the OpenGL / DefaultRenderPipeline overrides: 0 warnings, 0 errors.
- Process 52024 (`xrengine_2026-09-14_11-49-37_pid52024`) loaded the existing bitmap cache with 893 font glyphs and layout em 128. No font recovery reimport or Roboto streaming transition occurred.
- After more than two minutes, the UI diagnostic summary recorded 2407 successful text preparations and only the initial pending shader preparation. The original failure occurred within about 20 seconds.
- Visually inspected the composited window capture `mcp-captures/fixed/Screenshot_20260914_115001_566_905b0d7d63074efc881ccec4bc48cbb5.png`: bottom native FPS/path/timing/draw text is clearly readable.
- Preserved fixed-run logs under `logs/fixed/`. Restarted the same isolated session with its existing binaries/cache for a second runtime check.
- No tests were added or modified, following the repository's feature-validation sequencing policy. Validation uses the relevant isolated build and live OpenGL path.

- Cached restart process 49324 (`xrengine_2026-09-14_11-52-06_pid49324`) also retained nonzero text batches beyond the old failure window, with no font recovery or Roboto streaming transitions. Its logs are in `logs/fixed-restart/`.
- Applied a different camera position and visually inspected `mcp-captures/fixed-restart/Screenshot_20260914_115239_943_d2444915a6794565ae94760e3a80f6a1.png`: the changed scene view still has readable FPS text at the bottom.
- Both validation processes were stopped through the named-session manager. Targeted diff whitespace checks passed.

Implementation and local validation are complete. The user has not yet confirmed the result in their own run.
