# Default Render Pipeline Clip-Space Status

Status: **implemented, pending manual GPU retest** (2026-06).

This note tracks the OpenGL/Vulkan Y and depth-policy work that followed the Vulkan default-pipeline bring-up. The code-owned TODOs from the previous plan are implemented; the remaining items are manual editor/GPU validation checks.

## Implemented

- Vulkan vertex input resolves attributes by semantic shader name, preserves explicit overrides, and keeps a safe legacy fallback.
- OpenGL supports shader programs with no vertex attributes when the shader uses `gl_VertexID`.
- Clip-depth range is an engine setting with `ZeroToOne` and `NegativeOneToOne`.
- OpenGL applies depth range through `glClipControl`.
- Vulkan enables `VK_EXT_depth_clip_control` when available for native `NegativeOneToOne` clip depth.
- Vulkan falls back to shader-position remapping when `VK_EXT_depth_clip_control` is unavailable:

```glsl
gl_Position.z = gl_Position.z * 0.5 + gl_Position.w * 0.5;
```

- The Vulkan fallback now selects the final position-producing shader stage per program:
  - mesh shader when present,
  - geometry shader before each `EmitVertex()`,
  - tessellation-evaluation shader when present,
  - vertex shader for ordinary vertex pipelines.
- OpenGL `ClipSpaceYDirection=YDown` is no longer ambient UI/window state:
  - renderer UI scopes force the UI-safe OpenGL clip origin,
  - ImGui main rendering is scoped,
  - ImGui platform-window rendering is scoped,
  - Ultralight/OpenGL command-list rendering is scoped,
  - native screen-space UI rendering is scoped,
  - startup presentation marker rendering is scoped.
- OpenGL viewport/scissor entry points keep engine bottom-left rectangles unchanged because `glClipControl` changes clip/window mapping, not the coordinate convention accepted by `glViewport`/`glScissor`:
  - `SetRenderArea`,
  - `CropRenderArea`,
  - `SetIndexedViewportScissors`,
  - `ClearIndexedViewportScissors`.
- OpenGL UI scopes reapply the active render-area/crop state after changing clip policy so FBO and viewport passes do not keep stale state from the previous clip origin.
- OpenGL front-face selection folds in `GL_UPPER_LEFT` polygon-area parity alongside existing `ReverseWinding`.
- Screen-space shaders have a shared helper contract through `ScreenSpaceUtils.glsl` and `DepthUtils.glsl`:
  - `XRENGINE_ScreenCoordLocal`,
  - `XRENGINE_ScreenUV`,
  - `XRENGINE_ScreenPixelLocal`,
  - `XRENGINE_ScreenNoiseCoord`.
- `ClipSpacePolicy` is a lightweight engine-uniform requirement so screen-space passes can receive `ClipSpaceYDirection` and `ClipDepthRange` without requiring a full camera uniform block.
- Default-pipeline screen-space passes that sampled or tiled by `gl_FragCoord.xy` were migrated to the helper, including deferred lighting, AO, GI composites, post, fog, mirror, water, grabpass, MSAA resolve, and debug overlays.
- Dithered transparency now normalizes its ordered-dither coordinates for clip-origin parity.

## Coordinate Contract

Engine render rectangles remain bottom-left. Backend code is responsible for adapting that contract to API-native viewport/scissor conventions.

OpenGL scene rendering maps the selected `ClipSpaceYDirection` to `glClipControl`:

- `YUp` -> `GL_LOWER_LEFT`
- `YDown` -> `GL_UPPER_LEFT`

OpenGL `glViewport` and `glScissor` calls still consume the same bottom-left rectangles. Do not flip render rectangles for `GL_UPPER_LEFT`; only the clip policy and front-face parity change.

OpenGL UI and raw UI backends render in a scoped UI-safe clip-origin policy so editor/UI presentation does not flip when scene Y policy changes.

Vulkan keeps using backend viewport/scissor adaptation:

- `YUp` -> negative-height viewport
- `YDown` -> positive-height viewport
- scissors are converted from engine bottom-left rectangles to Vulkan top-left rectangles

Vulkan ImGui is not part of scene clip-space policy. Its backend resets a positive-height top-left viewport before drawing so docked editor UI stays upright under either scene Y direction.

Depth range remains a clip-space policy. Camera projection reacts to depth range, not Y direction.

## Validation Run

Completed:

```powershell
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~UberShaderForwardContractTests --no-restore --logger "trx;LogFileName=UberShaderForwardContractTests.trx" -- NUnit.NumberOfTestWorkers=1
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~SecondaryPassShaderContractTests --no-restore --logger "trx;LogFileName=SecondaryPassShaderContractTests.trx" -- NUnit.NumberOfTestWorkers=1
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~RuntimeRenderingHostServicesTests --no-restore --logger "trx;LogFileName=RuntimeRenderingHostServicesTests.trx" -- NUnit.NumberOfTestWorkers=1
dotnet build .\XRENGINE.slnx --no-restore
git diff --check
```

All completed successfully. The final solution build reported 0 warnings and 0 errors. `git diff --check` exited cleanly, with only Git's CRLF normalization warnings for touched text files.

Additional OpenGL Y-down UI/rectangle correction validation (2026-06-05):

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~SecondaryPassShaderContractTests --no-restore --logger "trx;LogFileName=SecondaryPassShaderContractTests.trx" -- NUnit.NumberOfTestWorkers=1
dotnet build .\XRENGINE.slnx --no-restore
git diff --check
```

The focused contract suite passed 23/23, and the full solution build reported 0 warnings and 0 errors. `git diff --check` reported no whitespace errors, only Git's existing CRLF normalization warnings.

Additional Vulkan ImGui viewport correction validation (2026-06-05):

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~SecondaryPassShaderContractTests --no-restore --logger "trx;LogFileName=SecondaryPassShaderContractTests.trx" -- NUnit.NumberOfTestWorkers=1
dotnet build .\XRENGINE.slnx --no-restore
git diff --check
```

The focused contract suite passed 23/23, and the full solution build reported 0 warnings and 0 errors. `git diff --check` reported no whitespace errors, only Git's existing CRLF normalization warnings.

## Remaining Manual Retest

These are not code TODOs; they require running the editor on the relevant GPU/backend.

OpenGL:

- `ClipSpaceYDirection=YUp`: scene, skybox, Sponza, editor UI, transform gizmo, debug lines, debug points, and text remain unchanged.
- `ClipSpaceYDirection=YDown`: editor UI and ImGui platform windows are not upside down.
- `ClipSpaceYDirection=YDown`: scene viewport is not vertically inverted relative to camera controls.
- Cropped render areas and viewport-panel rendering cover the expected pixels.
- Cascaded/point-light atlas indexed viewports draw into expected atlas tiles.
- Mirror/capture/light-probe passes do not double-flip.

Vulkan:

- `ClipSpaceYDirection=YUp`: negative-height viewport path renders upright.
- `ClipSpaceYDirection=YDown`: native positive-height viewport path renders upright.
- ImGui/editor UI stays upright under both Vulkan scene Y-direction modes.
- `ClipDepthRange=NegativeOneToOne` uses `VK_EXT_depth_clip_control` when available.
- Without `VK_EXT_depth_clip_control`, shader-position remap renders scene and skybox correctly.
- Mesh, geometry, tessellation-evaluation, and ordinary vertex pipelines render correctly under the shader-remap fallback.

Regression checks:

- No magenta placeholder background in the default pipeline.
- No exploded transform gizmo or over-scaled debug primitive regression.
- No missing opaque overdraw visualization regression.
- Native FPS/debug text still renders correctly.
- No `vkInvalidateMappedMemoryRanges` allocation-bound validation errors.

## Open Questions

- Should `ClipSpaceYDirection` remain user-facing for normal editor use, or become an advanced backend compatibility/debug setting?
- Do we need a separate `FramebufferOrigin` or `PresentationOrigin` setting so UI/window-space policy is not conflated with scene clip-space policy?
- If a future project wants projection-space Y flipping, it should be implemented explicitly in camera projection generation and shader clip output rather than by leaking backend window-origin state.
