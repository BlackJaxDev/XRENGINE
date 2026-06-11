# Vulkan Deferred Depth And Lighting Investigation

## Original symptom

OpenGL deferred Sponza renders opaque geometry over the skybox. Vulkan initially
showed the skybox over most deferred geometry, with only forward-rendered foliage
visible.

The first decisive finding was that Vulkan's deferred GBuffer pass produced color
data but did not preserve useful depth for the later combine/forward passes. A
temporary depth visualization in `DeferredLightCombine.fs` showed the Sponza area as
far depth (`depth == 1.0`) while forward foliage had real depth.

That depth issue explained the skybox occlusion:

1. `DeferredLightCombine.fs` discards far-depth pixels.
2. The forward/background pass then sees far depth and allows the skybox to cover the
   frame.

## Fixed depth/composite issues

- Removed the unconditional depth visualization from `DeferredLightCombine.fs`; depth
  visualization now lives behind `XRE_DEFERRED_DEBUG=5`.
- Kept the shared depth view readable for passes that sample it instead of allowing a
  later render pass restart to wipe the composited scene.
- Adjusted the forward pass clear behavior so the non-MSAA path does not clear the
  deferred depth before sky/background rendering.
- Added render graph metadata for quad passes that sample the shared depth view.

## Directional light accumulation bug

The later "colors appear, then bleed to white" symptom was a separate Vulkan feedback
bug in the non-MSAA deferred lighting path. There were two contributing edges:

1. `LightCombineFBO` sampled `LightingTexture` at material binding 5 and also wrote
   its final output to `LightingTexture`. The non-MSAA directional light pass was also
   rendering into `LightCombineFBO`. That made Vulkan sample and write the same image
   across the light/combine sequence.
2. The fullscreen light-combine and background sky materials did not all explicitly
   disable blending. On Vulkan, mesh draw submission snapshots blend state into the
   pipeline key; any material that leaves blend as inherited can accidentally pick up
   the additive state used by deferred light volumes. That made the combine/sky path
   behave as if it was still part of the lighting accumulation pass, which is why the
   skybox could also wash out toward white when the directional light was active.

Current fix:

- Added `LightingAccumTexture` and `LightingAccumFBO`.
- Non-MSAA lights now render into `LightingAccumFBO`.
- MSAA lighting resolves from `MsaaLightingFBO` into `LightingAccumFBO`.
- `LightCombineFBO` samples `LightingAccumTexture` and writes the final combined
  output to `LightingTexture`.
- `LightCombineFBO`, `SkyboxComponent`, and `AtmosphericScatteringComponent`
  explicitly use `BlendMode.Disabled()` so they cannot inherit additive light blending.
- The accumulation FBO is color-cleared for the light pass and does not request
  depth/stencil clears.
- Anti-aliasing resource invalidation now includes the accumulation FBO/texture.
- Render graph metadata declares `LightingAccumTexture` as the light-combine input
  instead of the final `LightingTexture`.

This removes the read/write feedback edge and the blend-state inheritance path while
preserving the shader's existing `LightingTexture` sampler name for compatibility.

## Runtime validation

Validated with the editor launched via:

```powershell
dotnet .\Build\Editor\Debug\AnyCPU\Debug\net10.0-windows7.0\XREngine.Editor.dll --unit-testing --mcp --mcp-allow-all --mcp-port 5467
```

MCP setup:

- Focused the Sponza node in the editor camera.
- Disabled sky auto-cycle and locked time of day.
- Captured early and late screenshots with the directional light active.
- Captured comparison frames with cascaded shadows disabled and with the directional
  light disabled.

Results:

- Early and late directional-light captures no longer brighten into white.
- The Vulkan log shows repeated rendering into `LightingAccumFBO`, confirming the new
  accumulation target is active.
- A patched 45-second run with the directional light active stayed numerically stable
  over the central viewport region:
  - mean luma: `203.934` early, `203.968` at 15s, `203.969` at 45s
  - early-to-45s mean absolute RGB delta: `0.04`
  - only `0.04%` of sampled pixels changed by more than 2 RGB levels
- Disabling cascaded shadows did not remove the remaining dark/striped pattern.
- Disabling the directional light removed the blowout but did not fully remove the
  stable pattern, so the remaining artifact appears to be below or adjacent to direct
  lighting rather than the time-accumulation feedback itself.

Captured files from this run:

- `Build/Logs/AutoValidationCaptures/VulkanLightingAccumSplit_early/Screenshot_20260610_231658.png`
- `Build/Logs/AutoValidationCaptures/VulkanLightingAccumSplit_late/Screenshot_20260610_231707.png`
- `Build/Logs/AutoValidationCaptures/VulkanLightingAccumSplit_cascadesOff/Screenshot_20260610_231849.png`
- `Build/Logs/AutoValidationCaptures/VulkanLightingAccumSplit_dirOff/Screenshot_20260610_231916.png`
- `Build/Logs/AutoValidationCaptures/SkyBlendFix_early/Screenshot_20260610_235133.png`
- `Build/Logs/AutoValidationCaptures/SkyBlendFix_mid15s/Screenshot_20260610_235148.png`
- `Build/Logs/AutoValidationCaptures/SkyBlendFix_late45s/Screenshot_20260610_235219.png`

## Remaining trail

An earlier Vulkan validation error appeared during pre-blend-fix validation:

```text
UNASSIGNED-CoreValidation-DrawState-InvalidImageLayout:
expected depth/stencil attachment layout, current layout was UNDEFINED
for aspectMask 0x2 array layer 2, mip level 0.
```

This points at a depth/stencil array layer, likely in the directional shadow/cascade
area, and is separate from the fixed lighting feedback edge. The latest patched run's
filtered Vulkan log did not show validation or error lines while capturing the
45-second comparison.

The remaining stable striping should be debugged next by restarting with
`XRE_DEFERRED_DEBUG=1`, `2`, `4`, and `5` before pipeline creation. Setting
`Debug.DeferredDebugView` live did not visibly switch the already-created combine
pipeline in this run.

## Validation commands

```powershell
dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "Name=BackgroundSkyMaterials_DisableBlendStateInheritance|Name=LightCombineQuad_UsesMaterialIdentityPredicate_InsteadOfSizeOnlyCache|Name=DeferredLightCombineMetadata_DeclaresGBufferProbeAndLightingInputs"
```

The editor build and three targeted tests pass after the accumulation split and
blend-state inheritance fixes. A broader filtered rendering test run still contains
unrelated stale source-path assertions and a fog default expectation mismatch.
