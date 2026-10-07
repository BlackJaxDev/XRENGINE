# Ambient Occlusion Validation

Scope: Validate AO mode quality, cost, editor visibility, and readiness for `DefaultRenderPipeline` and `AdvancedRenderPipeline`.

Architecture: [Ambient Occlusion](../../../developer-guides/gi/ambient-occlusion.md), [Default Render Pipeline Notes](../../../architecture/rendering/default-render-pipeline-notes.md)  Code todos: [Default Pipeline GPU Hotspots TODO](../../todo/rendering/optimization/default-pipeline-gpu-hotspots-todo.md)

## Setup

Use task `Build-Editor` before live checks. Use task `Start-Editor-NoDebug` for desktop checks. Use launch profile `Editor (Unit Testing World)` or `XRE_WORLD_MODE=UnitTesting` for controlled scenes. Use `XRE_GL_DEBUG=1` for OpenGL diagnostics and `XRE_VULKAN_VALIDATION=1` for Vulkan diagnostics. Use profiler output and GPU captures for cost comparisons.

## Checks

### HBAO+
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Editor visibility | Inspect AO settings in the camera post-process editor. | Every AO type appears only when supported. | Open | none |
| Indoor quality | Capture indoor contact shadows and crevices. | HBAO+ gives stable contact and crevice occlusion. | Open | none |
| Outdoor quality | Capture large-radius outdoor occlusion. | Large-scale occlusion is stable and not over-dark. | Open | none |
| Foliage | Capture alpha-tested foliage with detail AO on and off. | Foliage has no severe halos or leaks. | Open | none |
| Borders and bias | Capture screen borders and self-occluding geometry. | Border leaks and self-occlusion are within tolerance. | Open | none |
| Cost | Measure GPU cost against SSAO and MVAO. | Cost supports or rejects default-readiness. | Open | none |
| Default decision | Review quality and cost. | A decision exists for `ScreenSpace` versus `HorizonBasedPlus` as default. | Open | none |

### Non-HBAO Modes
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| MVAO policy | Review MVAO quality and maintenance cost. | MVAO is supported, experimental, or on a deprecation path. | Open | none |
| MSVO | Validate MSVO gather and blur against canonical expectations. | Claims are kept or narrowed to match observed behavior. | Open | none |
| Spatial Hash AO motion | Move the camera and geometry. | No visible ghosting or lagging artifacts appear. | Open | none |
| Spatial Hash AO edge cases | Test screen edges, thin geometry, and low sample counts. | Artifacts are documented or fixed. | Open | none |
| GTAO policy | Compare GTAO with HBAO+. | GTAO is exposed beside HBAO+ or selected as the preferred modern screen-space path. | Open | none |
| Readiness matrix | Publish mode readiness. | User-facing modes have a stable readiness matrix. | Open | none |

### VXAO Gates
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Voxel ownership | Review shared voxel ownership, coverage, and transform contract with VCT. | Ownership and transforms are defined. | Open | none |
| Volume budget | Define coverage, memory, payload, and update expectations. | VXAO has a clear resource contract. | Open | none |
| Fine-detail fallback | Define how VXAO blends with short-range screen-space AO. | Fine details remain covered. | Open | none |
| Pipeline ownership | Decide default pipeline, Advanced option, or research-only status. | VXAO is not presented as finished until gates pass. | Open | none |

## Failures
| Check | Symptom | Investigation or code item |
|---|---|---|
