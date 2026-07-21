# Vulkan Startup And Render-Graph Black Frame Investigation

Status: In progress

## Problem

Two consecutive Math Intersections Unit Testing World launches failed on
2026-07-21:

- Vulkan terminated during startup before producing a Vulkan category log.
- OpenGL opened a black window and repeatedly threw render-graph dependency
  exceptions before executing the first frame.

## Evidence

- Vulkan session
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-21_00-54-00_pid53560`
  ended with Windows Error Reporting exception `0xC0000409`, fast-fail subcode
  7, in the bundled `vulkan-1.dll` version 1.4.309.
- The Vulkan session never created `log_vulkan.log`, and Monado did not report
  an XREngine client connection. The previous successful Vulkan launch's first
  Vulkan message followed the OBS implicit-layer availability probe.
- `VulkanRenderer.PrepareObsHookCompatibility()` currently calls
  `vkEnumerateInstanceLayerProperties` before instance creation solely to
  diagnose whether `VK_LAYER_OBS_HOOK` is installed. A native fast-fail cannot
  be recovered by managed exception handling.
- OpenGL session
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-21_00-55-09_pid106088`
  initialized OpenGL and completed 82 source shader builds with zero failures.
- Its first frame failed in
  `RenderGraphSynchronizationPlanner.TopologicallySort()` after render-resource
  commit because the graph was reported as cyclic. Rendering then backed off
  and retried without submitting scene passes, leaving the window black.
- Commit `12dd9359` added legacy resource hazard edges directly to topological
  sorting. Legacy edges are inferred from metadata declaration order, which can
  oppose explicit execution dependencies and manufacture a cycle.

## Proposed Fixes

1. Derive topological ordering only from explicit dependencies and explicitly
   versioned resource producer/consumer relationships. Continue deriving
   legacy synchronization hazards after that order is established.
2. Replace the pre-instance Vulkan OBS layer enumeration call with managed
   discovery of Windows Vulkan implicit-layer registrations and manifests.
3. Add focused regression tests for explicit-order/legacy-hazard interaction
   and Vulkan layer-manifest parsing.
4. Build and launch both backends against the Math Intersections Unit Testing
   World, capture the viewport from more than one camera position, and inspect
   the resulting logs.

## Validation Ledger

- Pending.

Ignored evidence root:
`Build/_AgentValidation/20260721-render-startup-graph-fixes/`.
