# MCP Server And Assistant

[Back to user guide](../README.md)

The editor MCP server lets AI assistants and external tools inspect and modify the active XRENGINE editor world through HTTP JSON-RPC. Use this page to enable and operate it. For protocol and implementation details, see [MCP Server Implementation](../../developer-guides/ai/mcp-server.md).

This is distinct from the optional [Local Agent Broker](local-agent-broker.md):
the editor is an HTTP MCP server exposing scene tools, while the broker is a
stdio MCP server that starts explicitly selected OpenAI API workers and acts as
an internal MCP client of one named editor session. Neither should expose the
editor endpoint publicly.

## Enable The Server

Open **Global Editor Preferences** and find the **MCP Server** category.

Important settings:

- `McpServerEnabled`: starts or stops the server.
- `McpServerPort`: default `5467`.
- `McpServerRequireAuth` and `McpServerAuthToken`: require bearer auth.
- `McpServerReadOnly`: blocks mutating tools.
- `McpServerAllowedTools` and `McpServerDeniedTools`: constrain the visible tool set.
- `McpPermissionPolicy`: controls whether tools prompt before execution.

The default endpoint is:

```text
http://localhost:5467/mcp/
```

## Isolated Editor Sessions

Use an isolated editor session for agent-driven MCP work. Each named session gets its own managed build output and intermediate files, MCP port, process identity, editor preferences, asset cache/metadata, and logs. A normal solution build can then overwrite `Build/Editor` without touching a running session.

```powershell
pwsh Tools/Manage-McpEditorSession.ps1 Start -Name agent-rendering
pwsh Tools/Manage-McpEditorSession.ps1 Start -Name agent-physics
pwsh Tools/Manage-McpEditorSession.ps1 List
```

`Start` selects an available port beginning at `5467`, builds with a session-specific .NET artifacts root, launches the Unit Testing World, and waits for that session's MCP status endpoint. Pass `-Port 5501` to require a particular port, `-NoWait` to return immediately after launch, or `-NoBuild` to reuse that stopped session's existing artifacts.

Call a named session without copying its port:

```powershell
pwsh Tools/Invoke-Mcp.ps1 -Session agent-rendering -Method ping
pwsh Tools/Invoke-Mcp.ps1 -Session agent-rendering -Method tools/list
```

Stop only the process owned by that session, then remove its disposable artifacts when they are no longer needed:

```powershell
pwsh Tools/Manage-McpEditorSession.ps1 Stop -Name agent-rendering
pwsh Tools/Manage-McpEditorSession.ps1 Remove -Name agent-rendering
```

The manager verifies the executable path, PID, and process start time before stopping anything. It first requests a graceful window close, then terminates only that verified session PID if the editor apphost does not expose a closable main-window handle. It never searches for and kills all editor processes. Pass `Stop -Force` to skip the graceful close attempt.

Session data lives under `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/<timestamp>-<name>/`; commands address the logical `<name>`. The session manager retains at most five sessions and removes the oldest stopped session before creating another. Repository source assets and `Assets/UnitTestingWorldSettings.jsonc` remain shared intentionally, so source edits are still visible across sessions. The default session permission policy is `AllowAll` for unattended local automation; use `-PermissionPolicy AllowReadOnly` when mutation is not required.

## Command Line

You can also launch the editor with MCP enabled:

```powershell
XREngine.Editor.exe --mcp
XREngine.Editor.exe --mcp --mcp-port 8080
XREngine.Editor.exe --mcp --mcp-allow-all
XREngine.Editor.exe --no-mcp
```

Use `--mcp-allow-all` only for trusted local automation because it bypasses permission prompts.
Use `--no-mcp` to force MCP off for an isolated benchmark or unattended run,
even when the saved editor preference enables it.
Command-line MCP values are process-local session overrides. They do not modify
or appear as unsaved changes in the persisted editor preference assets.
MCP clients can apply any editor preference property for only the active process
by calling `set_editor_preference` with `session_only: true`; nested dotted paths
such as `Debug.RenderMesh3DBounds` are supported. Restarting the editor discards
the value and reveals the saved global/project preference again.
`set_game_setting` supports the same `session_only: true` behavior for any writable
game setting or nested dotted path.

## VS Code

Add this workspace MCP config when you want Copilot or another MCP-aware client to connect:

```json
{
  "servers": {
    "xrengine": {
      "type": "http",
      "url": "http://localhost:5467/mcp/"
    }
  }
}
```

Start the editor, enable the server, then check the client tool picker for XRENGINE tools such as `list_worlds`, `list_scene_nodes`, `capture_viewport_screenshot`, and `start_viewport_sequence_capture`.

## In-Editor Assistant

The ImGui editor includes **Tools > MCP Assistant**. It can use provider keys from editor preferences or environment variables such as `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, `GEMINI_API_KEY`, and `GITHUB_TOKEN`.

For scene or material edits, prefer prompts that ask the assistant to inspect the current world, make a bounded change, and verify with read-back or a viewport screenshot.

## Editor Camera Pipeline Assets

`set_editor_camera_render_pipeline_asset` replaces the active editor camera's
pipeline asset by asset ID, path, or loaded-asset name. It uses the same
`XRCamera.ReplaceRenderPipelineAsset` path as the ImGui camera inspector, so the
runtime transition is applied on the render thread and every camera-bound
viewport follows the new asset. Reassigning the exact same asset reference is a
no-op and the response reports `changed: false`.

Use `get_render_state` after the call to verify the active pipeline type,
pipeline revision, resource generation, pending/failure state, and enabled pass
ownership. For visual acceptance, also capture or inspect the live window; a
successful tool response alone does not prove that a new pipeline presented.

`get_camera_post_process` reads the selected camera's pipeline stage keys,
parameter values, ranges and enum options. Select a desktop viewport with
`window_index`/`viewport_index`, a camera with `camera_node_id`, or an XR output
with `vr_eye` (`left`, `right`, or `stereo`).
`set_camera_post_process_parameter` changes one declared `stage_key` and
`parameter_name` through the existing backing settings, then returns its value.
It requires mutation permission. Numeric ranges and enum values are validated;
vectors use finite numeric `x`/`y`/`z`/`w` object members as appropriate.
For reproducible captures, for example, set `colorGrading.AutoExposure` to
`false`, `colorGrading.Exposure` to `1`, and `tonemapping.Tonemapping` to the
returned ACES enum value (`6`). Capture the selected output to verify the
rendered effect; this command changes only the selected camera's settings.

`set_camera_tsr_render_scale` uses the same output selectors and accepts a
finite `scale` from `0.5` to `1.0`; omit it to clear the camera override.
It changes the internal resolution only while TSR is active. The next frame
publication applies resource/history changes, so inspect `get_render_state`
for the accepted internal extent and history generation before capturing.
`get_camera_post_process` also reports `tsrRenderScaleOverride`.

`get_transform_tool_state` reads the active selection gizmo without creating
one. It reports the target, mode, tool scale, root/billboard render matrices
and reference camera. Use it with captures to distinguish invalid transforms
from a shader or submission failure.

`create_primitive_shape` accepts `transparent: true` to use the built-in
sorted transparent lit material with Advanced motion/reactive variants.
Supply a color with alpha below 1 to create translucent coverage. The default
remains an opaque lit primitive; normal scene undo applies to either choice.
Colors accept finite RGB(A) objects (`R`, `G`, `B`, optional `A`) or
`#RRGGBB`/`#RRGGBBAA`; invalid values fail before creating a node.
The built-in temporal variants require rigid, single-instance geometry without
skinning, morphs, billboarding or per-draw render-option overrides.
`get_material_uniforms` reports `advancedTemporal.sourceValid` and its rejection
reason. Edit the existing `MatColor` uniform to preserve shared coverage;
replacing its parameter object or changing the source shader requires recreating
the matching temporal variants.

For authored sky materials, `advancedBackground` reports mono/stereo admission
and rejection reasons, including missing environment textures, stale shader
receipts, and render state that would overwrite native depth or alpha. See the
[Advanced background contract](../../architecture/rendering/advanced-background-rendering.md).

Generic component/object numeric values use complete component objects:
vectors use `x`/`y`/`z`/`w` as appropriate, quaternions use all four, and matrices
use `m11` through `m44`. Input names are case-insensitive; components must be
finite. Missing, duplicate or unknown components fail instead of creating a
zero value. Component read-back includes these fields so callers can verify the
actual assigned value.

`capture_render_pipeline_texture` accepts `companion_texture_names` for up to
seven additional named resources. All captures execute in the same window
post-render callback, using the selected layer, mip and export options. The
response's `companions` array contains their paths, hashes and statistics.
Use this for comparisons such as `HDRSceneTex` with `MotionBlur`,
`Velocity` and `DepthView`; separate requests can observe different frames.
Capture each stereo layer explicitly. EXR uses uncompressed FLOAT32 RGBA channels
and preserves raw floating-point values, including negative and nonfinite data,
without color conversion. Files are larger than compressed HALF exports but do
not overflow large diagnostic values. PNG is intended for visual inspection.

To inspect a live texture outside the pipeline resource table, supply `texture_id`
instead of `texture_name`. Use the GUID returned by component/object property
inspection, for example a light probe's irradiance or prefilter texture. Resolution
occurs at the capture boundary and a missing generation fails explicitly. The
same mip/layer/export options apply. `probe_render_pipeline_depth` also accepts
`include_stencil: true` to read the independent stencil aspect beside depth.

For a scene/probe, mirror, or standalone texture capture, `capture_owner_id` selects
the component's assigned offscreen viewport and its resource table. Finish the
owner's writer first, then use this selector with `texture_name` (and optional
companions) to inspect its native HDR/depth resources. It cannot be combined
with a camera, VR, or non-default window/viewport selector.
`list_render_pipeline_resources` and `get_advanced_profile_diagnostics` accept
the same owner selector, including minimal depth/visibility profiles.

`MirrorCaptureComponent.GetMirrorCaptureDiagnostics` exposes capture version,
authoring attempts/disposition, writer/package state, consumer-fence count,
retirement/quarantine state and the current viewport/texture identity. It reads
owner state without polling GPU fences and is not an atomic frame snapshot.
A completed writer proves texture authoring, not reflected-image correctness;
inspect the exported output before treating a mirror validation as passed.

For `ThumbnailCaptureComponent`, `PortalCaptureComponent`,
`DepthCaptureComponent` and `VisibilityCaptureComponent`, invoke
`QueueCapture`, then wait for `CaptureVersion` to increase. `IsCaptureQueued`
and `LastCaptureFailure` distinguish pending work from a terminal failure.
The queue authors inside the owning world's window frame and polls its GPU
receipt. Direct `TryCapture` and `TryCompleteCapture` calls require the render
thread; MCP's `MainThread` preference selects the application thread.

Depth captures export only the depth aspect of a D32F/S8 target; stencil is not
defined by that product. Visibility captures export canonical RG32_UINT surface
identity. Both disable color shading, temporal history and post processing, and
use nearest texture filtering. Use `Advanced.Visibility.DepthStencil` or
`Advanced.Visibility.Identity` as a companion to compare the native source.
Integer EXR/PNG captures convert values numerically to floats, which cannot
preserve every possible 32-bit integer exactly; use the integer picking path for
exact handle resolution. OpenGL cube captures select faces 0–5 with `layer_index`
and accept `mip_level`; out-of-range faces fail explicitly.
`AdvancedShading.ShadingDiagnostics` preserves reconstruction rejection in bit16
and its `EAdvancedReconstructionInvalidReason` value in bits20–27. Capture it as
a companion to HDR/identity when native shading produces the error color.
For a nonfinite rejection, bits28–30 identify the first affected attribute:
1=world position, 2=normal, 3=tangent, 4=bitangent, 5=UV, 6=UV dx, 7=UV dy.
After completion, `CompletedOutputTextureId` provides the GUID for an exported
texture capture. Keep the owner idle during inspection. Engine GPU consumers
can use `TryAcquireCompletedOutput` and retain its lease through their own
completion; a diagnostic texture GUID grants no lifetime ownership.
For a texture consumed through canonical Advanced scene materials, call
`TryPublishCompletedOutput` on the render thread before binding it. Scene
publications then retain that exact content generation automatically. A
published texture cannot be refreshed or resized, even between readers.
Remove/replace its consumers and call `WithdrawPublishedOutput` on the render
thread to allow refresh after all retained scene publications complete. Owner
deactivation also withdraws publication and defers destruction until its
readers and writer settle. `GetCaptureDiagnostics` reports `canonicalPublished`
and `canonicalReferences` independently of direct-lease references.

`list_vulkan_image_allocation_diagnostics` also reports buffer allocation
groups by lifetime owner/state, so a steady image count is not mistaken for
steady total GPU memory usage.

`get_render_state.advancedProfile.temporalHistory` exposes the existing
published temporal snapshot: isolation policy, input extent, readiness and
the profile/per-eye reset and seeded generations. Compare snapshots before
and after a camera cut or resize to verify invalidation and subsequent
reseeding. Reading this field neither creates nor resets history.

`get_render_state.activeViewports[].viewHistoryLedger` reports a locked value
snapshot of each desktop viewport's history generation, committed sequence and
source frame, pending count/range, and effective commit/discard counters. Compare
successive snapshots to distinguish accepted history from merely authored work.
The counters count effective candidate transitions, not pixels or presentation
success; committed history does not by itself certify the rendered image.

`list_render_pipeline_resources.pipeline.asset_id` identifies the active
pipeline asset for property inspection or `set_object_property`. This also
works for runtime-created pipeline assets that are absent from the loaded-asset
inventory. Re-query it after replacing the pipeline or restarting the editor.

## Capture Subsequent Viewport Frames

For animation, physics, flicker, streaming, and temporal-rendering issues, ask the assistant to start a viewport sequence capture. The assistant can capture an exact number of subsequent frames or sample for a bounded number of seconds, poll `get_viewport_sequence_capture`, and inspect the resulting individual PNGs, `contact-sheet.png`, and `manifest.json`.

Example prompts:

- *"Capture the next 12 consecutive viewport frames and inspect the contact sheet for flicker."*
- *"Sample the editor viewport at 10 FPS for five seconds and identify which frames differ most."*
- *"List active viewport sequence captures and cancel the one still running."*

The default overflow policy fails rather than silently omitting a requested consecutive frame. Captures are bounded by frame, duration, pixel, memory, and contact-sheet limits. Both OpenGL and Vulkan are supported. Vulkan capture uses bounded GPU staging slots and nonblocking fence polling; the manifest reports GPU completion time, CPU conversion time, source format, queue slot, and whether an MSAA resolve was needed. If the renderer queue is full, `overflow_policy: "fail"` stops the sequence while `"drop"` records the skipped frame. There is no silent CPU or OS-window fallback.

On Vulkan, an unsignaled capture fence produces a warning after two seconds and fails the requesting capture after ten seconds without blocking the render thread. The slot stays quarantined until the GPU finishes or the renderer is recreated. This protects the editor-side workflow from hanging, while the operating system's GPU watchdog remains responsible for recovering a GPU submission that is genuinely stuck.

## Occlusion Validation

`set_editor_camera_depth_mode(reversed_depth)` changes the active editor camera's
depth convention and invalidates its viewport. It does not change saved project
defaults. Verify both normal and reversed depth against an occlusion-disabled
capture at the same pose before accepting a culling change.

For Vulkan GPU cost measurements, use
`get_render_profiler_stats.vulkan.frame_lifecycle.gpu_command_buffer_timing`.
Its coherent snapshot separates current query availability from `last_completed`,
which identifies the submitted render frame, sample sequence, image slot, age
and elapsed nanoseconds. Count each completed sequence once and reject samples
whose source frame precedes the workload change. The legacy
`gpu_command_buffer_ms` scalar alone does not identify the measured frame.

`evaluate_gpu_hiz_crossover(samples_json, requirements_json)` evaluates supplied
matched Disabled/Full/Coarse GPU timings without changing engine settings. Each
sample names the GPU, backend, extent, workload, depth convention, parity proof,
cohort and timestamp scope. Requirements specify minimum observations and cost
margins. The tool checks internal consistency; it does not independently verify
the caller's parity proof or manufacture a profitable threshold. Missing,
insufficient, ambiguous or non-winning evidence never promotes an occlusion mode.

## Texture Streaming Diagnostics

`get_render_state` includes the active window's owner-published event/surface
snapshots and effective clip-depth range. These read-only snapshots help
distinguish suspended output from renderer failure without controlling the
desktop; a sampled event-pump stack alone does not establish minimized state.
`get_time_state.terminalFault` retains the first exception that stopped the
current timer run, including the loop phase and visibility-publication sequence
numbers. It remains available in Release builds even when category logs are
compiled out, and resets only when a new timer run starts.

`get_texture_streaming_summary` includes `backend_upload_diagnostics`, an
on-demand text snapshot of the active backend's upload counters. Vulkan reports
worker preparation and retained ownership separately from queued transfers and
descriptor publication. These are backend-wide counters, not an atomic frame
sample. Use repeated observations and completed uploads to establish progress.

## Safety Notes

- Use read-only mode for inspection-only sessions.
- Require auth when exposing the server beyond trusted local processes.
- Keep mutating and destructive tools behind prompts unless you are running controlled automation.
- Use allowed/denied tool lists for constrained workflows.

## Deeper Docs

- [MCP Server Implementation](../../developer-guides/ai/mcp-server.md)
- [MCP Assistant Developer Guide](../../developer-guides/ai/mcp-assistant.md)
