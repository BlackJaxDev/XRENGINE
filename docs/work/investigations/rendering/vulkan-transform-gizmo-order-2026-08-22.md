# Vulkan Transform Gizmo Render Order

## Problem

Selecting a scene node in the ImGui editor created no visible 3D transform tool when the Vulkan backend was active. The symptom persisted across unrelated render settings.

## Root Cause

The editor selection path and transform-tool mesh collection were working. A baseline MCP query reported 13 enabled mesh commands in `OnTopForward`, and Vulkan prepared the `GizmoLine` and `GizmoArrowHead` pipelines with depth testing disabled.

The Vulkan frame-operation trace exposed the ordering error:

- final window presentation was recorded at operation 50;
- the 13 `OnTopForward` draws were recorded afterward at operations 51-63;
- those draws wrote to `ForwardPassFBO`, after its postprocessed result had already been presented.

The default render pipeline executes the `OnTopForward` mesh bucket a second time late in the command chain. Both executions shared core render-pass index 9. That core pass is held behind the full default mesh-pass dependency chain, while independent synthetic postprocess and presentation passes remain ready. Vulkan therefore sorted the late execution after presentation.

## Solution

- Added an optional command-local synthetic render-graph pass to `VPRC_RenderMeshesPassShared`. Mesh collection still uses the authored scene render-pass index, while backend operations can use the command's resolved scheduling pass.
- Assigned the default pipeline's late `OnTopForward` executions to `LateOnTopForward`.
- Added an explicit dependency from `LateOnTopForward` to `VPRC_BuildAccelerationStructure`, matching the authored command order and preventing the overlay from moving ahead of preceding scene work.
- Kept the FBO capture command path on the existing core pass because it has a different command sequence.

## Validation

- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore` passed with 0 warnings and 0 errors.
- Isolated Vulkan editor session: `vulkan-gizmo-order-baseline`.
- Baseline screenshot: `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260822-005229-vulkan-gizmo-order-baseline/mcp-captures/baseline/` showed no transform tool after selecting and focusing Sponza.
- Fixed screenshots from two camera positions under `mcp-captures/fixed/` and `mcp-captures/fixed-angle2/` show the red, green, and blue translation axes at the selected model.
- Fixed frame trace records the 13 `LateOnTopForward` draws at operations 45-57 and final `RenderToWindow_FxaaOutputTexture` at operation 63.
- The latest session logs contain no Vulkan validation errors, fatal errors, or nonzero dropped-draw/operation diagnostics.

## User Validation

Automated live-editor validation passes. Awaiting confirmation from the user's normal editor workflow.
