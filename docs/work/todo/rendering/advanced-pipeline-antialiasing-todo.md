# Advanced Pipeline Antialiasing TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Advanced Render Pipeline](../../../architecture/rendering/advanced-render-pipeline.md)
Validation: [Default And Advanced Pipeline Validation](../../testing/rendering/default-and-advanced-pipeline-validation.md)

## Current State

`AdvancedRenderPipeline` has TAA, TSR, MSAA, and DLAA plumbing. Prior fixes added temporal jitter publication, TAA accumulation outputs, DLAA inputs, MSAA raw resources, visibility and depth resolves, and background alpha handling. Remaining code work is execution recovery, resource publication progress, temporal input correctness, and regression tests after live validation is complete.

## Open Code Items

### Execution Recovery

- [ ] Diagnose the material-table publication limit that can freeze canonical scene publication. Advanced material tables and publication retention. Done when: stale texture source rejections can recover without unsafe old-output disposal.
- [ ] Fix the framebuffer construction and publication race. Advanced framebuffer/resource publication code. Done when: backend execution never uses an incomplete framebuffer wrapper.
- [ ] Establish why the isolated editor can exit before AA mode validation finishes. Editor session and renderer shutdown paths. Done when: validation runs can complete or report a precise terminal reason.
- [ ] Add diagnostics for the first OpenGL native stage rejection and fix its root cause. OpenGL Advanced visibility execution. Done when: a rejected family can recover on a later frame after readiness.
- [ ] Add publication tracing for OpenGL AA resource materialization, activation, and old-generation retirement. Advanced resource manager and publication code. Done when: each requested AA profile becomes active or reports a specific unsupported or failed result.
- [ ] Repair any demonstrated OpenGL publication progress dependency. Done when: superseded requests retire and failed generations expose a specific reason.

### Temporal Inputs

- [ ] Convert native NDC velocity and temporal jitter displacements into framebuffer texture-coordinate convention before TAA or TSR history lookup. Temporal shaders. Done when: horizontal and vertical motion validate independently.
- [ ] Keep previous-frame depth until TSR resolves. Temporal history code. Done when: TSR reads matching color and depth history.
- [ ] Correct DLAA and DLSS vendor motion direction. Native temporal buffers and bridge dispatch. Done when: vendor input uses current-to-previous displacement.
- [ ] Remove the duplicated X jitter stratum and verify zero-mean full-cycle sample coverage. Temporal jitter code. Done when: static subpixel edges behave across render scales.
- [ ] Propagate frame-view history invalidation into TAA and TSR readiness for projection, FOV, and explicit history resets. Done when: stale history is rejected after those changes.
- [ ] Make closest-depth velocity selection respect normal and reversed depth in mono and stereo temporal shaders. Done when: depth convention tests pass.

### Tests

- [ ] Add or update regression tests only after live feature validation and explicit clearance. `XREngine.UnitTests/Rendering/`. Done when: test changes cover the fixed defects without replacing live validation.
- [ ] Review tests that assume TAA native jitter is absent. Done when: expectations match the current temporal contract.

## Decisions Needed

- [ ] Decide the supported behavior for quad-view temporal post state. Owner: rendering lead.
- [ ] Decide whether Vulkan DLAA is supported now or remains an explicit unsupported result. Owner: rendering lead.

## Out Of Scope

- Increasing scene-storage limits as a substitute for publication correctness.
- Assessing AA quality on a stale, black, or stage-rejected frame.
- Adding tests while feature validation remains open without explicit clearance.
