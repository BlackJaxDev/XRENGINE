# Vulkan Prefab Texture Cancellation and Publication Freeze (2026-08-16)

## Problem

Importing the configured Unity avatar prefab through the Unit Testing World completed,
but Visual Studio reported repeated first-chance `OperationCanceledException` and
`TaskCanceledException` instances. Texture residency did not converge: about 175
transitions remained pending after the prefab import.

A later run exposed two adjacent problems. Frame cadence stayed below 100 Hz while
the converter emitted thousands of progress messages and the render thread drained
completed texture transfers in one unbounded batch. When the converted hierarchy
became renderable, an Uber-shader draw referenced 66 unique resources, exceeded the
frame-operation packet's fixed capacity of 64, and put desktop recording into a
repeated failure/recovery loop that appeared to freeze the editor.

## Baseline Evidence

Source run:
`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-08-16_19-49-20_pid41612`.

- The Unity prefab import completed with 52 models, 41 materials, and 37
  Poiyomi-to-Uber conversions.
- `log_textures.log` recorded 2,553 forced `Clearing stuck pending transition`
  warnings and 2,557 canceled decode/upload events while the editor was active.
- Cancellation occurred before/during cache reads and at stale Vulkan upload or
  descriptor-publication boundaries. The backend catches these cancellations, so
  they were first-chance debugger notifications rather than unhandled failures.
- The final texture summary retained roughly 175 pending transitions and 151
  textures without resident data.

## Root Cause

`ImportedTextureStreamingManager.Evaluate` considered stuck recovery safe by checking
only the two OpenGL residency backends. It omitted the Vulkan dense backend's active
and queued decode counters, its active GPU-upload counter, and supplemental work held
inside `VulkanTextureUploadService`. At high frame rates, legitimate Vulkan requests
waiting behind the two-slot decode gate reached the 300-frame recovery threshold.
The manager canceled and immediately re-queued them, producing cancellation churn
and preventing the avatar's texture set from converging.

The same manager repeated hard-coded backend counter sums in startup diagnostics,
telemetry, and OpenXR upload checks, making future omissions likely.

The apparent OpenGL texture activity on Vulkan came from that hard-coded accounting,
not from an OpenGL upload being required by Vulkan. Some source assets also have
names such as `BasicTee_Normal_OpenGL.png`; those names describe the normal-map
convention and are not a backend selection. The corrected run processes those files
through `VulkanDenseTextureResidencyBackend`.

The publication freeze was independent of texture cancellation. The frame-operation
resource-use list and its dependency scratch were fixed at 64 entries. The converted
Poiyomi material legitimately produced 46 sampler units, 74 named sampler bindings,
seven buffer bindings, and 66 unique resources. `FrameOpResourceUseList.Add` threw
before Vulkan command-buffer recording, so swapchain recreation could not repair the
failure and only repeated it.

## Implemented Solution

- Added an allocation-free activity snapshot supplied by each registered texture
  streaming provider.
- Changed the manager to query only the current renderer's provider, so Vulkan
  scheduling no longer relies on or consults OpenGL activity counters.
- Included Vulkan upload-service queues/publications in the activity contract.
- Changed stuck recovery, startup diagnostics, and telemetry to use the shared
  activity snapshot.
- Routed OpenXR blocking-work checks through the active provider.
- Renamed the manager's OpenGL backend properties so they are not mistaken for
  backend-neutral work.
- Made Vulkan dense upload terminal completion idempotent and established that a
  rejected provider request owns its terminal callback. This prevents rejection
  from decrementing the active-upload counter or invoking cancellation twice.
- Made decode-to-upload activity handoffs destination-first so the watchdog never
  observes a false zero while work moves between queues.
- Fixed descriptor-publication counter cleanup and transfer-removal accounting so
  exceptions and drain races cannot leak or underflow Vulkan activity counters.
- Routed profiler summaries through the current provider and made active import
  scopes participate in startup readiness.
- Changed the priority decode gate to complete canceled waiters with an explicit
  `false` result instead of a canceled task. Normal transition supersession no
  longer throws `TaskCanceledException`/`OperationCanceledException` while waiting
  for a decode slot; permit grant/cancel races are resolved atomically.
- Changed the window-initialization watchdog's normal canceled-delay exit to use a
  non-throwing await, removing an unrelated startup first-chance cancellation that
  appeared when focused cancellation tracing was enabled.
- Connected `ModelsToImport` prefab entries to the Unity prefab converter and made
  the finalization path validate the expected Poiyomi-to-Uber conversion result.
- Replaced the fixed 64-entry frame-operation resource list with retained high-water
  storage. The plan builder's producer-dependency scratch now follows the same
  retained-capacity rule, avoiding a new per-frame allocation after growth.
- Added high-water diagnostics with renderer, mesh, material, shader, and binding
  counts. Invalid content operations are quarantined individually so one malformed
  imported draw cannot abort the rest of the frame; trusted engine operations still
  fail loudly.
- Suspended imported model publication while attaching the converted hierarchy,
  then enabled at most two model components per application frame. This avoids a
  single 52-model visibility and renderer-materialization burst.
- Limited completed Vulkan texture-transfer publication to one transfer per render
  coroutine iteration. Completion state remains queued between iterations, so this
  bounds render-thread work without dropping uploads.
- Throttled Unity prefab progress reporting to four updates per second plus the final
  completion update. The same import now emits 59 progress records instead of 1,023.
- Avoided repeating renderer-family draw-slot resolution for command chains whose
  frame data was already refreshed; the refresh step has already published the exact
  slot needed by recording.

## Validation Plan

- Build the rendering kernel, Vulkan renderer, OpenGL renderer, and editor.
- Start a named isolated Vulkan Unit Testing World session using the configured
  `jax2026.prefab` import.
- Wait beyond the former 300-frame recovery boundary and prefab completion.
- Confirm the import and Poiyomi conversion counts remain correct.
- Confirm there are no forced stuck-transition warnings or decode-task cancellation
  storm, and that pending/no-data texture counts converge instead of cycling.
- Stop only the named session and inspect its persisted logs.

## Validation Evidence

Final isolated session:
`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260816-202526-vulkan-prefab-cancel-verified`.

The session ran with `XRE_FIRST_CHANCE_EXCEPTIONS=CanceledException`, so both
`OperationCanceledException` and `TaskCanceledException` would have been written to
the engine logs. Evidence was copied to
`Build/_AgentValidation/20260816-201500-vulkan-texture-cancellation/logs/verified-vulkan-prefab-import/`.

- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore` succeeded
  with zero warnings and zero errors.
- The Unity prefab import completed with 52 models, 41 materials, 37
  Poiyomi-to-Uber conversions, and 37 Poiyomi Pro downgrades.
- The final texture summary at frame 10,860 reported 240 tracked textures,
  `pending=0`, `uploading=0`, `noData=0`, and `failed=0`.
- There were zero `Clearing stuck pending transition` warnings, zero Vulkan upload
  events with `state=Canceled`, and zero `[FirstChance]` cancellation entries.
- Thirteen queued transition supersessions were logged as ordinary
  `Texture.TransitionCanceled` state changes. They produced no exception entries
  and did not prevent full convergence.

Freeze/performance verification session:
`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260816-204806-vulkan-avatar-freeze-fix`.

- The editor build again completed with zero warnings and zero errors.
- The real configured `jax2026.prefab` completed with 52 models, 41 materials,
  37 Poiyomi-to-Uber conversions, and 37 Poiyomi Pro downgrades.
- Model publication was spread across 26 application frames and completed in about
  0.71 seconds. The import window evaluated 8,640 frames in 48.24 seconds, or
  179.1 Hz, compared with about 71 Hz in the failing run.
- The former failing Uber draw raised the retained resource high-water mark to 66.
  Its diagnostic reported 46 sampler units, 74 sampler names, and seven buffer
  bindings, and the draw proceeded without a capacity exception.
- Completed texture-transfer drain jobs were normally 1-3 ms, with a 13.64 ms
  outlier. The previous unbounded drain reached 151 ms.
- The final texture state reported 240 tracked textures with `pending=0`,
  `uploading=0`, `noData=0`, and `failed=0`.
- Log counts were zero for `OperationCanceledException`, `TaskCanceledException`,
  frame-operation capacity failures, command-recording failures, swapchain recovery,
  device loss, and quarantined imported operations. Asset filenames containing
  `_Normal_OpenGL` were uploaded by the Vulkan dense backend; there were no
  `backend=OpenGL` texture records.
- MCP remained responsive for more than six minutes after the former failure point.
  A focused viewport capture showed the imported avatar rendered in Sponza:
  `Build/_AgentValidation/20260816-204800-vulkan-avatar-freeze/mcp-captures/Screenshot_20260816_205217_211_53d43435345a4e53a7b1e41a75333cf6.png`.

Cold Vulkan graphics-pipeline compilation can still intentionally present the last
completed scene while a new avatar variant materializes. These are bounded
`RejectedDesktopFrame` publications, keep ImGui responsive, and are distinct from
the former exception/recovery loop. A RenderDoc capture was not needed for this
failure because the exception occurred in CPU frame-plan construction before Vulkan
command recording began.

## User Verification

Runtime validation is complete. The user's normal Visual Studio debugger workflow
remains useful as an independent confirmation that its exception break settings are
quiet with the same prefab configuration.
