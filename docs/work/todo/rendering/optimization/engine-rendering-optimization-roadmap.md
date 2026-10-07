# Engine Rendering Optimization Roadmap

Last Updated: 2026-10-06
Status: Active index
Architecture: [Mesh Submission Strategies](../../../../architecture/rendering/mesh-submission-strategies.md), [Frame Lifecycle And Dispatch Paths](../../../../architecture/rendering/frame-lifecycle-and-dispatch-paths.md)
Validation: [GPU-Driven Submission Validation](../../../testing/rendering/gpu-driven-submission-validation.md), [Optimization 01-08 Acceptance Closeout](../../../testing/rendering/01-08-optimization-acceptance-closeout.md)

## Current State
This document is only an index. The Vulkan sequence is owned by the Vulkan core and command-recording TODOs. Workstream 06 is no longer blocked by missing implementation text in this roadmap. It is gated by accepted 03-05 evidence and by the source TODOs that own Forward+, render-tail, and occlusion work.

## Open Code Items

### Index links
- [ ] Keep this index synchronized with the active source TODOs and validation docs. This file only. Done when: each link below points to the current owner and this roadmap owns no implementation work.

## Active Owners
| Area | Owner |
|---|---|
| Numbered Vulkan sequence | [Vulkan Core Frame Loop And Resident Rendering Master TODO](../vulkan-core-frame-loop-and-resident-rendering-master-todo.md) |
| Vulkan hardening, Forward+, render-tail, and occlusion | [Vulkan Core Hardening And Device-Loss TODO](../vulkan-core-hardening-and-device-loss-todo.md) |
| GPU-driven submission architecture | [Production GPU-Driven Rendering TODO](../gpu/production-rendering-pipeline-roadmap.md) |
| Compact zero-readback | [Compact Zero-Readback Rendering TODO](compact-zero-readback-rendering-todo.md) |
| CPU direct fast path | [CPU Direct Fast Path TODO](cpu-direct-fast-path-todo.md) |
| Material binding ladder | [Material Table And Texture Binding Ladder TODO](material-table-and-texture-binding-ladder-todo.md) |
| Editor profiler observer cost | [Editor Profiler And UI Render Cost TODO](editor-profiler-ui-render-cost-todo.md) |
| Default pipeline GPU hotspots | [Default Pipeline GPU Hotspots TODO](default-pipeline-gpu-hotspots-todo.md) |
| Editor memory reduction | [Editor Memory Reduction TODO](editor-memory-reduction-todo.md) |
| VR rendering budget | [VR Rendering Performance Contract TODO](vr-rendering-performance-contract-todo.md) |
| Avatar and cooked assets | [Avatar Optimization Roadmap](../../avatar/avatar-optimization-roadmap.md) |

## Decisions Needed
- [ ] Decide whether the roadmap should remain an active TODO or move to architecture after all linked owners are stable. Owner: rendering lead.

## Out Of Scope
- Implementation details. They belong in the owner docs linked above.
- Validation matrices. They belong in testing docs.
