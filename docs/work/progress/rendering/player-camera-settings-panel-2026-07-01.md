# Player Camera Settings Panel - 2026-07-01

## Problem

Camera and render pipeline tweaks were scattered between the Hierarchy/Inspector path for component-backed desktop cameras and runtime-only XR eye cameras that do not have their own scene nodes.

## Implemented

- Added an ImGui `View > Player Cameras` panel.
- The panel enumerates active desktop/window viewports, local player camera components, and XR left/right eye cameras.
- Each camera entry exposes component or runtime camera settings, projection settings, schema-backed post-processing settings, render pipeline asset details, live render pipeline instance details, and viewport details.
- XR eye entries are marked as runtime-owned because headset pose and projection are refreshed by OpenXR during frame collection.
- OpenXR eye AA/HDR/render-scale overrides now synchronize only between the active left/right eye cameras.
- OpenXR eye cameras now share one post-process state collection, so bloom, AO, and other schema-backed post-process settings stay identical between the eyes without being copied from the desktop/editor or cyclopean camera.
- Runtime eye entries edit the eye camera state directly and show that the settings are shared with the OpenXR stereo eye pair.
- Panel visibility is persisted in the editor ImGui panel visibility state.

## Findings

- OpenXR still resolves a source viewport/camera to choose a render world and a matching pipeline type/config.
- The source camera no longer owns eye visual tuning; AA/HDR/render-scale and post-process settings are owned by the stereo eye pair.
- Direct edits to either eye camera's post-processing settings are shared with the sibling eye and do not mutate the editor view or cyclopean camera.

## Validation

- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore -v:minimal -p:OutDir="Build\_AgentValidation\20260701-player-camera-panel-build\temp-build\"`
  - Passed.
  - Existing `Magick.NET-Q16-HDRI-AnyCPU` NU1902/NU1903 vulnerability warnings remain.
- A normal build to `Build\Editor\...` was blocked while the editor/debug adapter had output assemblies locked.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~VrViewRenderModeContractTests"`
  - Blocked before executing tests because the running editor locked normal output assemblies.
- Retrying the same test with redirected `OutDir` avoided the locks but exhausted local disk space while copying native runtime assets; the failed temp output was deleted.
