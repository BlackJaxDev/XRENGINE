# Vulkan Stall Separate Findings TODO

Updated: 2026-10-05.
Status: **Pending.** No item is active.

The Vulkan stall validation found these issues. They are not proven causes of
the stall, so they are not part of the
[stall remediation checklist](vulkan-stall-remediation-todo.md). Reproduce the
current state of each issue and assign an owner before you change code. Follow
the [validation protocol](../../testing/rendering/vulkan-stall-validation.md)
when an item changes rendering behavior.

- [ ] Resolve world snapshot/restore calls that do not return after long settles.
- [ ] Review YAML `OmitDefaults` dropping `false` on true-initialized booleans
  without `[DefaultValue(true)]`.
- [ ] Resolve shared material GUIDs in `duplicate_scene_node` clones.
- [ ] Account for the roughly 7.5-second hover-highlight dirty traffic in stationary
  automation. Record any suppression in both comparison conditions.
- [ ] Review the bounded 1,024-entry deferred presentation ring retaining the
  prior renderer generation until overwrite.
- [ ] Track missing replacement-GI contract coverage with its owner and test policy.
- [ ] Report unsupported canonical command rejection reasons/counts instead of
  silently committing empty output; inspect `TryGetCanonicalCompatibilityReason`.
- [ ] Attribute the bounded 320 extra descriptor sets caused by per-frame
  auto-uniform arena-view identity on masked Sponza cascade casters.
- [ ] Rename phase-named publication telemetry types, MCP tools and environment
  variables by responsibility. Update docs and regenerate MCP documentation.
- [ ] Attribute later Play restore cost of 0.8-1.0 seconds versus the earlier
  0.25-0.33 seconds; investigate stair-stepped directional shadow boundaries.
- [ ] Attribute capture sequences to the rendered camera snapshot, not the live
  transform two frames ahead. Reconcile collect/render `TemporalHistoryValid` flags.
- [ ] Investigate exposure settling after repeated history resets, black upper
  sky with `klippad_sunrise_2_4k`, and per-launch environment-lighting variation.
  Compare only matched maps or an explicit procedural-sky fixture.
- [ ] Release the OpenGL shared-context hidden window on its owner thread. Owner:
  OpenGL rendering. The worker's exit path releases the window on the worker
  thread. In collapsed mode, `EnqueueWindowThreadTask` runs the task inline, so
  the backend's owner-thread check throws and terminates the process at editor
  close. The final-window close fix exposed this path; before it, OpenGL close
  hung. Require a design review, because this changes native lifetime. See the
  [finding](../../investigations/rendering/2026-10-05-vulkan-stall-monado.md#new-finding-opengl-shared-context-worker-releases-its-native-window-on-the-wrong-thread).
- [ ] Remove the remaining timer dependencies from window close. The split-pump
  prototype (`XRE_WINDOW_PUMP_HOST=sdl`) still disposes through render-thread
  jobs that need a timer dispatch. A `RequestClose` call from another thread
  after a terminal timer fault also cannot start the native close.
  `RuntimeEngine.IsDispatchingRenderFrame` is never assigned, so the close
  deferral at the frame boundary never runs.
