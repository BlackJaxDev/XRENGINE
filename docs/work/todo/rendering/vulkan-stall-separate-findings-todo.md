# Vulkan Stall Separate Findings TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Vulkan Renderer](../../../architecture/rendering/vulkan-renderer.md), [Frame Loop Design](../../../architecture/rendering/frame-loop-design.md)
Validation: [Vulkan Core Validation](../../testing/rendering/vulkan-core-validation.md#stall-remediation-fixture-coverage)

## Current State

The Vulkan stall work left a set of side findings that are not proven root causes of the original stall. Each item needs reproduction, owner assignment, and a focused code item before implementation. Use the Vulkan core validation checks when an item changes rendering behavior.

## Open Code Items

### Scene And Asset State

- [ ] Resolve world snapshot and restore calls that do not return after long settles. World snapshot and restore code. Done when: the call returns or reports a bounded failure in the reproduction case.
- [ ] Review YAML `OmitDefaults` dropping `false` on true-initialized booleans without `[DefaultValue(true)]`. Serialization defaults. Done when: affected boolean defaults round-trip or the serializer reports why they cannot.
- [ ] Resolve shared material GUIDs in `duplicate_scene_node` clones. Scene clone and material identity code. Done when: cloned nodes do not share material GUIDs unless sharing is explicit.
- [ ] Track missing replacement-GI contract coverage with its owner and test policy. Global illumination replacement contracts. Done when: the owner and allowed test scope are recorded in the correct GI todo or test plan.

### Rendering Diagnostics And Publication

- [ ] Account for the approximately 7.5-second hover-highlight dirty traffic in stationary automation. Editor hover-highlight diagnostics. Done when: comparison runs either suppress the traffic or record it in both conditions.
- [ ] Review the bounded 1,024-entry deferred presentation ring that retains the prior renderer generation until overwrite. Deferred presentation ring. Done when: retention is intentional and bounded, or a narrower retirement path exists.
- [ ] Report unsupported canonical command rejection reasons and counts instead of silently committing empty output. `TryGetCanonicalCompatibilityReason` and canonical command commit code. Done when: each unsupported reason is visible in telemetry or diagnostics.
- [ ] Attribute the bounded 320 extra descriptor sets caused by per-frame auto-uniform arena-view identity on masked Sponza cascade casters. Descriptor and auto-uniform arena diagnostics. Done when: the extra sets have a named owner or are removed.
- [ ] Rename responsibility-mismatched publication telemetry types, MCP tools, and environment variables. Publication telemetry, MCP tools, and environment variable names. Done when: names describe responsibility, docs are updated, and MCP docs are regenerated.

### Editor And Window Lifetime

- [ ] Attribute later Play restore cost of 0.8 to 1.0 seconds versus the earlier 0.25 to 0.33 seconds, and investigate stair-stepped directional shadow boundaries. Editor Play restore and directional shadow diagnostics. Done when: the cost and shadow-boundary owner are known.
- [ ] Attribute capture sequences to the rendered camera snapshot, not the live transform two frames ahead. Capture sequencing and collect/render history code. Done when: captures carry the rendered camera identity and collect/render `TemporalHistoryValid` flags agree.
- [ ] Investigate exposure settling after repeated history resets, black upper sky with `klippad_sunrise_2_4k`, and per-launch environment-lighting variation. Exposure and environment-lighting code. Done when: comparisons use matched maps or an explicit procedural-sky fixture, and the cause is assigned.
- [ ] Release the OpenGL shared-context hidden window on its owner thread. OpenGL shared-context worker and hidden-window lifetime. Done when: close runs on the owner thread in collapsed and split modes without terminating the process. See [the finding](../../investigations/rendering/2026-10-05-vulkan-stall-monado.md#new-finding-opengl-shared-context-worker-releases-its-native-window-on-the-wrong-thread).
- [ ] Remove the remaining timer dependencies from window close. Split pump prototype, close deferral, and `RuntimeEngine.IsDispatchingRenderFrame`. Done when: close does not require timer dispatch after a terminal timer fault, and render-frame boundary deferral runs when needed.

## Decisions Needed

- [ ] Decide the owner for each finding before code changes start. Owner: Rendering.

## Out Of Scope

- Stall acceptance checks. [Vulkan Core Validation](../../testing/rendering/vulkan-core-validation.md) owns them.
- Completed stall-remediation history.
