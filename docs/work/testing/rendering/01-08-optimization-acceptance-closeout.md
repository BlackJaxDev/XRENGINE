# Optimization 01-08 Acceptance Closeout Validation

Scope: Validate final Vulkan optimization promotion after the 03-05 baseline is accepted and later work is complete.

Architecture: [Mesh Submission Strategies](../../../architecture/rendering/mesh-submission-strategies.md), [Frame Lifecycle And Dispatch Paths](../../../architecture/rendering/frame-lifecycle-and-dispatch-paths.md), [Vulkan Command Recording](../../../architecture/rendering/vulkan-command-recording.md).
Code todos: [Engine Rendering Optimization Roadmap](../../todo/rendering/optimization/engine-rendering-optimization-roadmap.md), [Vulkan Core Hardening And Device-Loss TODO](../../todo/rendering/vulkan-core-hardening-and-device-loss-todo.md).
Parent validation: [GPU-Driven Submission Validation](gpu-driven-submission-validation.md).

## Setup

Use Release editor builds for promotion evidence. Use Debug only for diagnostics that cannot run in Release.

Tasks: `Build-Editor-Release`, `Measurement-GameLoopRenderPipeline-Release-All`, `Benchmark-Vulkan-Gate`, `Benchmark-Vulkan-Clean-Desktop`, `Benchmark-Vulkan-Clean-OpenXR`, and `Test-VulkanPhase3-Regression`.

Launch profiles: `Editor (Unit Testing World)`, `Editor (Unit Testing OpenXR SteamVR)`, and `Editor (Unit Testing World, Validation Layers)`.

Freeze `XRE_FORCE_MESH_SUBMISSION_STRATEGY`, `XRE_ZERO_READBACK_MATERIAL_DRAW_PATH`, validation-layer state, stereo mode, resolution, power plan, scene, runtime, and driver before each comparison. Use `XRE_SKIP_IMGUI=1` for benchmark runs unless a check measures editor UI.

## Checks

### Accepted pre-06 baseline

Architecture: [Mesh Submission Strategies](../../../architecture/rendering/mesh-submission-strategies.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Accept the 03-05 gate. | Complete the 03-05 section in [GPU-Driven Submission Validation](gpu-driven-submission-validation.md). | The accepted revision, hardware, runtime, settings, reports, captures, tests, and logs are named in a durable investigation or progress doc. | Open | none |
| Preserve manifests. | Copy or link accepted zero-readback, frame-preparation, data-publication, and command-recording manifests into the final closeout record. | The closeout can reproduce the exact baseline inputs. | Open | none |
| Rerun invalidated local gates only. | Identify changes after the accepted 03-05 baseline. | Each invalidated gate is rerun. Unchanged evidence is not repeated. | Open | none |
| Keep absolute budgets as inputs. | Read the 5.00 ms desktop and 8.33 ms RVC results from the accepted baseline. | They feed final workstream-08 decisions and do not by themselves promote the full renderer. | Open | none |

### Final promotion

Architecture: [Vulkan Command Recording](../../../architecture/rendering/vulkan-command-recording.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Confirm implementation completeness. | Inspect source todo links and the final revision. | Later work is implementation complete or explicitly rejected with evidence. | Open | none |
| Map deferred criteria. | Build a table for each deferred criterion. | Each criterion links to a report, capture, test, or capability result. | Open | none |
| Run canonical comparisons. | Run desktop, RVC, allocation, validation, and RenderDoc gates. | No comparison exceeds its variance or regression threshold. | Open | none |
| Update source TODOs. | Update each owned source TODO after evidence passes. | Each invalidated earlier gate and source TODO is acceptance complete or explicitly deferred. | Open | none |
| Promote or reject. | Write the final closeout record. | The optimization sequence is promoted or rejected with retained evidence and risk notes. | Open | none |

## Hardware Matrix
| Runtime | Desktop | OpenXR or RVC | Required result |
|---|---|---|---|
| NVIDIA | Required | Required when hardware is present | Pass or explicit blocker. |
| AMD | Required when available | Required when available | Pass, not tested, or explicit blocker. |
| Intel | Required when available | Required when available | Pass, not tested, or explicit blocker. |

## Failures
| Check | Symptom | Investigation or code item |
|---|---|---|
| Baseline | Baseline cannot be accepted because a local gate fails. | Fix the source code item before this closeout runs. |
