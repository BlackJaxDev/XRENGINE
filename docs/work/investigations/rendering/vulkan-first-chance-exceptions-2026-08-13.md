# Vulkan First-Chance Exceptions (2026-08-13)

## Problem

The editor's Vulkan run completed normally, but Visual Studio reported repeated
`InvalidOperationException` instances, a `ReflectionTypeLoadException`, and a
final `AggregateException`. The investigation targets the exceptions themselves,
not debugger suppression.

## Latest-Run Evidence

Source run:
`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-08-13_19-28-09_pid5240`.

- Detached ImGui viewport recording published command buffers whose frozen
  texture dependencies had entered retirement (`log_vulkan.log` lines 544-658).
- Point-light shadow operations reached primary recording before the Vulkan
  framebuffer wrapper for the new shadow-map generation existed
  (`log_vulkan.log` lines 875, 1019, and 1104).
- Orderly editor close established a GPU-idle boundary, but reverse-order
  teardown produced nine lifetime-readiness failures and aggregated them
  (`log_rendering.log` lines 843-925). The first failed drain left 117 VMA
  allocations live, so the later device and instance failures were cascading.
- The persisted logs do not contain the `ReflectionTypeLoadException`; it is a
  caught first-chance exception from one of the assembly-wide type discovery
  paths and requires live tracing to identify its caller and loader exception.

## Root Causes

1. Forced teardown retirement and the simple-resource ledger disagree about
   whether the teardown boundary authorizes destruction. A drain can remove and
   destroy a native object, then reject the corresponding ledger completion.
2. Expected generation changes are represented as exceptions in two recording
   paths instead of explicit not-ready/rejected outcomes.
3. The reported reflection exception has no persisted stack or loader-exception
   evidence. A traced full loaded-assembly scan did not reproduce it, so no
   reflection caller has been changed without evidence.

## Implemented Solutions

- Made forced retirement authoritative in both lifetime-ledger completion paths.
- Moved Vulkan frame, target, command-worker, texture-preparation, pipeline, and
  readback quiescence ahead of the external device-idle wait. Submitted uploads
  remain untouched until after that proven GPU boundary.
- Added an explicit non-throwing command-buffer publication result for recoverable
  ImGui dependency-generation changes; detached viewports now discard and rebuild.
- Materialized frame-operation framebuffer targets in resource preparation so
  clear-only and newly rebuilt point-light shadow targets cannot first appear in
  lookup-only command recording.
- Fixed the MCP session retention script's one-item PowerShell array unwrapping,
  which blocked repeatable isolated validation.

## Validation Plan

- Build the Vulkan renderer and editor projects.
- Start a named isolated Unit Testing World editor session.
- Exercise normal rendering, detached ImGui viewports, point-light shadow-map
  generation changes, and orderly close.
- Inspect `log_vulkan.log` and `log_rendering.log` for the exception types,
  lifetime failures, live VMA allocations, and stale framebuffer warnings.
- Trace `ReflectionTypeLoadException` during the isolated run and record the
  responsible assembly/caller if it recurs.

## Validation Evidence

- `dotnet build XREngine.Editor/XREngine.Editor.csproj --no-restore`: succeeded
  with zero warnings and zero errors.
- Isolated Vulkan session `vulkan-exceptions-1950` reached MCP readiness and
  completed three HDR screenshot readbacks from camera-dependent views.
- With `XRE_FIRST_CHANCE_EXCEPTIONS=ReflectionTypeLoadException`, an explicit
  full loaded-assembly `search_types` scan completed without reproducing it.
- The final session exited through `EditorImGuiUI.ForceAllowWindowCloseForShutdown`
  plus `Engine.ShutDown` in under eight seconds. Its captured stdout/stderr had no
  matching first-chance, framebuffer-resolution, tracking-publication, VMA-live,
  cleanup-step, or aggregate-cleanup diagnostics.

## User Verification

Pending the user's normal Visual Studio workflow, especially detached ImGui
viewport recreation and any asset/plugin action that originally triggered the
uncaptured reflection exception.
