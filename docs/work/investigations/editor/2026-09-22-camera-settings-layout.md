# Camera settings layout

## Problem

The camera inspector Settings tab used one long section for visibility, a fixed
four-column 32-layer mask, quality overrides, assets, pipeline options, and runtime
diagnostics. The layer grid dominated narrow Player Cameras panels.

## Changes

- Visibility and Image Quality are open by default. Output, Render Pipeline, and
  Diagnostics are separate collapsible sections.
- The culling mask is a summary dropdown with All, None, Invert, a hexadecimal
  value, and a bounded scrollable list of all 32 layers. Each row uses the layer
  index as its identity, including when layer names are duplicated.
- Anti-aliasing uses one selector with an explicit Use Global option. HDR output
  similarly offers global, SDR, and HDR choices. Conditional MSAA/TSR controls
  follow the effective AA mode.
- Controls use labels above available-width inputs; manual resolution fields
  and scale presets no longer share an oversized row.
- Detailed usage status, camera identity, viewport bindings, and Forward+ debug
  controls live under Diagnostics. A short rendering status stays visible.
- Settings layout is isolated in `CameraComponentEditor.Settings.cs`; projection,
  post-processing, and preview retain their existing tabs.

## Validation

- Isolated `camera-layout` editor build passed with zero warnings and errors.
- Normal editor build also passed with zero warnings and errors.
- Inspected the live Player Cameras panel with Vulkan / AdvancedRenderPipeline.
  The initial layout fits common settings and all section headings in the narrow
  panel. Expanded Output, Render Pipeline, and Diagnostics retain their controls.
- Captured and visually inspected the composited editor window at
  `mcp-captures/Screenshot_20260922_111915_931_b163f632bcd6474eb47418d8f1bc0f1d.png`.
- Read-only source review confirmed all prior settings remain accessible,
  global/override values map correctly, and mask operations preserve bit 31.
- Automated popup interaction paused when user input was detected in the editor;
  scrolling and toggling individual layers still need interactive confirmation.
- No camera settings exceptions were found in the session logs during inspection.
- The named session was left open for the user's ongoing inspection.
- Evidence root: `Build/_AgentValidation/20260922-111500-camera-layout/`.
- User validation: pending.

## Follow-up: dedicated Debug tab and Vulkan preview orientation

The user requested a separate Debug tab and reported an upside-down camera
preview with Vulkan / AdvancedRenderPipeline.

- Camera tabs now include Debug between Post Processing and Preview. Runtime-only
  cameras also expose Debug in Player Cameras.
- Camera status/bindings and Forward+ controls move out of Settings. Debug-category
  schema stages move out of the Post Processing effect selector into collapsible
  Debug sections. The backing state, stage keys, reset behavior, and setting
  ownership remain unchanged.
- Pipeline editor extensions now use `IRenderPipelineEditorUIProvider` through
  `RenderPipeline.EditorUIProvider`, with separate Post Processing and Debug hooks.
  `PipelineEditorContext` has its own file. Advanced shading controls are supplied
  by the pipeline's Debug hook and labelled as shared pipeline settings.
- The Default pipeline advertises its Forward+ overlay support. Advanced does not
  execute that overlay command and instead exposes native shading diagnostics.
- Root cause of the inverted preview: camera and cascade preview code discarded
  `EditorTexturePreviewService.TryGetHandle`'s `requiresVerticalFlip` result and
  always flipped UVs. Both inline previews and their enlarged windows now use the
  backend-provided flag, preserving OpenGL's flip and Vulkan's unflipped convention.
- RVC forwards the editor provider to its Advanced stage family when that family
  is active, otherwise retaining Default/Forward+ diagnostics.
- The isolated Vulkan / AdvancedRenderPipeline build passed with zero warnings
  and errors. Live inspection confirmed the dedicated Debug tab and its shading,
  visualization, and camera status sections.
- A composited capture in
  `mcp-captures/Screenshot_20260922_113420_011_5afaf406511447ab97032e6d8ebac8e0.png`
  was visually inspected: camera preview and scene viewport are both upright.
- User confirmation: "Both are upright" for Preview and Open Preview Window.
- OpenGL and cascade previews were checked against their backend orientation
  contract in source, but were not separately exercised live.
- A missing `XREngine.Editor.Services` import in concurrent PropertyEditor work
  initially blocked the normal build; the import was restored.
- Final normal build after the RVC correction passed with zero warnings and errors.
- The named validation session was stopped after inspection.

## Follow-up: pipeline-owned camera controls

The user requested an audit of General, Post Processing, and Debug controls so
the camera editor only assumes universal camera behavior, or displays controls
supplied by the pipeline being used or explicitly selected for editing.

### Findings and implementation

- The generic Settings tab previously hardcoded AA modes, HDR, directional shadow
  choices, and a forward pre-pass hint. The generic Debug tab owned Forward+
  widgets, and Post Processing displayed a temporal-AA banner tied to the main
  player camera rather than the inspected target. These assumptions are removed.
- `IRenderPipelineEditorUIProvider.DrawCameraSettings` supplies pipeline-specific
  camera controls. Default and Advanced explicitly opt into shared standard
  AA/HDR/shadow controls; Default alone supplies Forward+ diagnostics. RVC selects
  the provider for its actual command family. Unsupported pipelines get no such
  controls by default.
- General camera UI retains visibility, resolution, output binding, and pipeline
  assignment. Runtime-only cameras now use this typed UI instead of a raw camera
  object inspector. Player Cameras passes the entry's actual pipeline into the
  component inspector, avoiding a first-viewport guess.
- All three settings tabs share Target Pipeline selection. The selector edits
  without rebinding. Opt-in `[RenderPipelineCameraSetting]` properties now come
  from the selected instance, not always the active instance. Instance-shared
  properties and camera-wide overrides are labelled. The latter are disabled
  when viewing a separate pipeline so configuring it cannot silently change the
  active camera's shared AA/HDR/Forward+ settings.
- `PipelineEditorSection` is explicit schema metadata. Stages default to Post
  Processing; nullable parameter metadata inherits the stage or overrides it.
  The builder supports `InEditorSection`, `AddParameter(editorSection: ...)`, and
  `SetUniformEditorSection`. Generic UI no longer compares built-in stage/category
  keys. Reset affects only parameters routed to the displayed section, and custom
  headers/footers stay with their stage's primary section.
- GPU BVH visualization is a Debug stage. Temporal debug, bloom-only, atmosphere,
  and volumetric-fog debug parameters are routed individually to Debug. Effect
  settings and all existing state keys remain unchanged. Temporal applicability
  is declared by the schema (TAA, TSR, or DLAA), not a main-camera banner.
- Removed the raw Advanced Post Processing inspector that bypassed schema routing.
  Built-in depth-of-field/color-grading drawers now match exact schema backing
  types instead of hardcoded stage-name aliases. Explicit custom drawers and
  registered stage overrides retain priority. DOF helpers support runtime cameras.
- Generic Default Properties and the separate Pipeline/Viewport object inspectors
  remain explicit object-inspection tools, not the curated camera-settings UI.
- Discovered type-only pipeline entries are labelled read-only previews. They
  use editor-owned temporary state rather than adding unstable pipeline IDs to
  camera state. Concrete assigned/bound pipelines remain editable. This uses the
  concurrent type-catalog work's fallback-state lifetime management.
- "Edit Another Pipeline Asset" selects an existing asset independently of camera
  assignment. The picker does not expose a raw inline inspector; selected asset
  controls still pass through provider/schema routing. Pipeline properties affect
  all views using that asset; camera effect state is keyed to the selected asset.
- Live AA/HDR stage-applicability gates are skipped for inactive editing targets,
  allowing preconfiguration; parameter visibility still uses the selected backing
  state. Any pipeline bound to this camera counts as active for shared overrides,
  not only its first viewport. Forward+ counters explicitly identify their global
  last-render scope, which can be another camera.

### Validation in progress

- Normal editor build passed with zero warnings/errors after integration.
- Optional evidence-only broker review failed with an internal access-denied
  error; its partial output is not counted as validation. Native source review
  and local build/runtime validation are used instead.
- No test files were added or modified; feature validation precedes test work.
- The isolated editor build passed with zero warnings/errors. Its Settings layout
  was inspected live and in the composited screenshot
  `mcp-captures/Screenshot_20260922_115710_663_4749fad56c9b4c46bf9dac096ba731a4.png`.
- Automated native clicks did not reliably switch tabs at the session's low frame
  rate. User confirmation of Debug contents was requested. The later read-only
  type-preview and multi-viewport ownership refinements were source-reviewed but
  are not in this running session's build.
- A later full build hit a concurrent Vulkan AA integration error:
  `VulkanRenderer.CommandBufferRecording.Primary.Preparation.cs` referenced
  `VulkanAdvancedVisibilityStageRequest.MsaaSampleCount` before that member was
  available. The targeted editor build with project-reference builds disabled
  succeeded with zero warnings/errors. Final validation status is recorded below.

### Final validation

- Full editor build subsequently passed with zero warnings/errors after the
  concurrent Vulkan integration settled. `git diff --check` is clean.
- Final bounded ownership review found no introduced correctness defect. It
  verified all three state paths, asset-picker isolation, read-only type previews,
  active-target detection, section-scoped reset, and global-counter labelling.
- No camera inspector/provider/custom-drawer exceptions or ImGui assertions were
  found in the isolated session logs.
- The validation session remains open for the requested user interaction check;
  it contains the initial provider/schema integration, not the later ownership
  guards and independent asset picker. These final refinements are build- and
  source-validated; their live interaction check remains pending.

## Follow-up: shared camera header

The user approved moving Projection and Preview out of the tab bar, with one
global preview preference and one pipeline editing target shared by all tabs.

- Camera editors now show a collapsed-by-default Projection section, the global
  Show Camera Previews toggle, optional inline output preview, and the shared
  Target Pipeline selector above Settings / Post Processing / Debug.
- All three tab bodies receive the same `PipelineEditorContext`; they no longer
  resolve or draw separate pipeline selectors. Projection remains camera-owned.
- `EditorPreferences.ShowCameraPreviews` is global, defaults off, and is saved
  immediately when toggled in the camera editor. It is also exposed in Global
  Editor Preferences under Inspector. No project or camera-specific override is
  introduced.
- Hidden previews skip discovery and GPU-handle resolution. Turning the preference
  off closes camera-tagged enlarged preview windows without closing material,
  light, or other component preview windows.
- Component and runtime cameras share one layout. Player Cameras supplies its
  actual viewport/pipeline instance to preview discovery. Preview labels identify
  the actual rendered source, never the independently selected editing target.
- Open Preview Window and backend-specific vertical orientation are preserved.
  Optional cascade previews are collapsed by default. Preview implementation is
  isolated in `CameraComponentEditor.Preview.cs`, and header/tab composition in
  `CameraComponentEditor.Layout.cs`.
- Full editor and isolated-session builds passed with zero warnings/errors; no
  tests were added or changed. The shared header with previews hidden was
  inspected live and in a composited screenshot. Source checks confirm one
  selector and the same editing context for all three tabs.
- Preview visibility preference changes succeeded through the named session API.
  The preview-enabled screenshot had the outer Pipeline tab selected, so it does
  not validate the inline preview. Another validation editor took foreground;
  desktop interaction was stopped to avoid interfering with concurrent work.
  Full interactive verification of the shared selector and enlarged preview
  remains pending. The earlier user confirmation of upright previews still
  applies to the preserved orientation implementation.
- Audio log stack traces associated with toggling the preference were existing
  diagnostic warnings stating that audio settings were already current, not
  camera UI exceptions. No audio changes were made for this layout task.
- Work was wrapped up at the user's request. The session was already unreachable
  when restoring the original disabled preview preference was attempted; that
  restoration could not be confirmed. The API validation change was not explicitly
  saved. The session manager confirmed `camera-layout` stopped; other sessions
  were left alone.
