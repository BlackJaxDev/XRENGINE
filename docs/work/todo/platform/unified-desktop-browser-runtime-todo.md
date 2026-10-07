# Unified Desktop And Browser Runtime TODO

Last Updated: 2026-10-07
Status: Active. **122 of 163 named requirements complete; 41 open** (103/118 implementation, 13/38 verification, 6/7 owner decisions). This is the unchanged branch requirement metric, not new merge credit.
Architecture: [Portable Engine Host](../../../architecture/runtime/portable-engine-host.md), [Default Pipeline Notes](../../../architecture/rendering/default-render-pipeline-notes.md), [Mesh Submission Strategies](../../../architecture/rendering/mesh-submission-strategies.md)  Design: [Unified desktop and browser runtime](../../design/platform/unified-desktop-browser-runtime-design.md), [Modular browser pipelines](../../design/platform/modular-browser-render-pipelines-2026-10-02.md)
Validation: [Platform Validation](../../testing/platform/platform-validation.md#unified-runtime-ur-verification-view)  Requirement history: [163-ID state and evidence ledger](../../progress/platform/unified-runtime-requirement-ledger-2026-10-07.md)

## Current State

The shared Host, authored world startup, browser Jolt, editor-published RollingBall and RenderingParity, and bounded Default and mono Advanced WebGPU paths are implemented. The Advanced path includes native visibility and shading, CPU-direct and GPU-indirect/compute-meshlet submission, bounded skin/morph and material-vertex profiles, and x4 MSAA. Recorded software and physical runs qualify specific static cases; full material, deformation, shadow/decal, device, performance, and desktop comparison checks remain open. The generic meshlet path still rejects dynamic multi-LOD, and the frozen reference browser runtime remains until parity allows retirement. The [dated ledger](../../progress/platform/unified-runtime-requirement-ledger-2026-10-07.md) records precise source and evidence limits.

## Counting And Ownership

The 163-ID requirement metric contains 118 implementation, 38 verification, and 7 owner rows. This code TODO carries the 15 open implementation IDs and one open owner ID. [Platform validation](../../testing/platform/platform-validation.md#unified-runtime-ur-verification-view) carries the 25 open verification IDs. The [dated ledger](../../progress/platform/unified-runtime-requirement-ledger-2026-10-07.md#requirement-states-and-evidence) preserves all 163 states and completed evidence. Moving a row between document kinds or merging master adds no completed item. Use the original UR ID when reporting progress.

<a id="ur00--stabilize-the-branch-as-a-reference-harness"></a>
The completed UR00 reference-harness requirements and their evidence are in the [dated ledger](../../progress/platform/unified-runtime-requirement-ledger-2026-10-07.md#ur00.01).

## Open Code Items

### UR02 — Platform Host, Frame Stepping, And Scheduling

- [ ] **UR02.03b** Inventory every remaining blocking and thread-creating site in the shared closure and make each asynchronous, move it to a desktop leaf, or confine it to cook and editor code. The [current closure inventory](../../progress/platform/browser-caller-blocking-inventory-2026-10-03.md) records implemented caller-job, transform, event, Uber, image-resize and HLOD/impostor paths, plus explicit media/host-I/O admission. Guarded desktop compatibility implementations remain physically present in shared assemblies; the literal placement requirement stays open. Regenerate the inventory with the original expressions. Use the exact search expressions and historical counts in the [requirement ledger](../../progress/platform/unified-runtime-requirement-ledger-2026-10-07.md#ur02.03b). Done when every remaining browser-reachable blocking or thread-creation site has an asynchronous owner, a desktop leaf, or a cook/editor boundary.


### UR03 — Asset I/O And Per-Platform Cooking

- [ ] **UR03.02b2** Remove the remaining synchronous load wrappers and physical host-file operations from runtime-reachable paths. On 2026-09-30 the asset manager had 10 sync-over-async sites, and Core and Rendering had 136 direct `File`/`Directory`/`FileStream` call sites across 39 files that bypass the asset source. The [runtime I/O record](../../progress/platform/browser-runtime-asset-io-boundaries-2026-10-03.md) distinguishes implemented catalog/cache-only loading, identity/handoff boundaries, nonblocking shader preloads and named source-admission guards from the remaining physical separation of desktop APIs. The [asset-manager boundary record](../../progress/platform/browser-asset-manager-source-boundaries-2026-10-03.md) adds constructor/load/cache/metadata admission, sticky catalog ownership, retired watcher rejection and remote-response rollback; published browser delivery requires a registered cooked target or a feature-specific async reader. The targeted review found no new bypass in those paths and does not close the literal whole-inventory removal requirement.

- [ ] **UR03.03** Add a platform target to cooking (`CookContent` in `XREngine.Editor/ProjectBuilder.cs`). Preserve all admitted texture interpretation and sampler state through the cooked carrier: raw `XRTexture2D` currently omits `ImportedColorSpace` and `MaxAnisotropy`; material-specific color-space metadata does not close that shared carrier gap. A compatibility-preserving solution must retain non-default anisotropy rather than silently replace authored sampling. Web cooking produces:
  - WGSL shader artifacts;
  - ASTC 4×4 and ETC2 texture variants with RGBA8 fallbacks, reconciled with the [texture compression TODO](../texturing/texture-compression-and-cooked-cache-todo.md);
  - web-decodable audio;
  - per-asset capability requirements.


### UR05 — Shaders And Materials

- [ ] **UR05.07** Port the hand-written web-tier shaders from the UR05.01 list by the route chosen in D7: depth and shadow casters, forward lit surfaces, sky and environment, tonemapping and the bounded post-process set, then UI and text. Implement coherent groups with narrow cook/compile checks and run known-value rendering at end-to-end milestones; each group's known-value result must be recorded before this coverage is closed, not before work on another group begins. Desktop GLSL behavior is unchanged.


### UR06 — Modular Render Pipeline Web Support

- [ ] **UR06.09f2** Publish GPU-selected mesh/LOD decisions and use them in generic meshlet expansion, preserving authored LOD policy without CPU visibility/count readback. The initial route explicitly rejects dynamic multi-LOD sources.


### UR11 — Editor Browser Publishing On The Unified Path

- [ ] **UR11.04** Keep the CLI entry point (`--build-project <project> --build-platform BrowserWebGPU`) and the editor Build Project action stable. Browser publishing must also work from a packaged editor, not only a source checkout.


### UR13 — Performance, Runtime Mode, And Size

- [ ] **UR13.03** Reduce download size through trimming, lazy assembly loading, and streamed content, against the [readiness budgets](../../progress/rendering/mobile-browser-readiness.md#devices-and-measurable-budgets). The [download-size boundary](../../progress/platform/browser-download-size-boundary-2026-10-04.md) records the current 25.46 MiB gzip framework build-resource subtotal, implemented scene streaming and deferred scene shader delivery. Genuine authored export and production-loader checks establish bounded shader deferral; managed-assembly loading, trimming, measured published transfer and browser attachment remain open.


### UR15 — CI, Hosting, And Evidence

- [ ] **UR15.05** Publish user-facing build, publish, hosting, support-matrix, and troubleshooting docs after validation.


### UR16 — Retire The Separate Browser Runtime

- [ ] **UR16.01** Remove `BrowserMeshComponent`, `BrowserSpinComponent`, `SceneBootComponent`, and the browser registration manifest and generator.

- [ ] **UR16.02** Remove `BrowserCooked*` scene and instance DTOs and the `BrowserSceneSession` content, motion, collision, and animation paths.

- [ ] **UR16.03** Remove `BrowserRenderPipeline`, its packet types, and `browser-render-pipeline.js` once the engine pipeline covers their cases.

- [ ] **UR16.04** Remove `BrowserCpuAnimator`, `BrowserCookedAnimationPlayer`, and `BrowserKinematicCharacter`.

- [ ] **UR16.05** Remove `BrowserWorldPublishExporter`, `Tools/BrowserContentCooker`'s browser-only recipe format (keeping the generalized packager), and the Python scripts (`Tools/Generate-BrowserRegistrations.py`, `Tools/Reports/audit_browser_dependencies.py`, `Tools/Shaders/cook_browser_shaders.py`).

- [ ] **UR16.06** Keep the developer harness page only if it still exercises the unified runtime; otherwise remove it.

- [ ] **UR16.07** Close or rewrite superseded mobile TODO rows and progress docs; move durable content into stable docs.

## Decisions Needed

- [ ] **UR13.02** Qualify shipping AOT versus the untrimmed interpreter with generated serialization and registration metadata, trimming roots, build time, download size, and runtime cost. Owner: product/runtime owner. The 2026-10-01 D11 decision retains the interpreter path until measurements support a shipping choice.

## Build Gate

Use targeted compile and shader-cook checks for a coherent implementation group. Before publication, use the full Editor, Server, VRClient, portable browser compile, and Browser publish gate recorded in the [dated ledger](../../progress/platform/unified-runtime-requirement-ledger-2026-10-07.md#build-gate). Each result applies only to its tested source revision.

## Owner Decisions

D1–D16 and their dated approvals or limits are preserved in the [requirement ledger](../../progress/platform/unified-runtime-requirement-ledger-2026-10-07.md#owner-decisions). D11 leaves UR13.02 open until measured shipping-mode qualification.

## Out Of Scope

- Browser-hosted editor, immersive WebXR, WebGL2 fallback, native Android/iOS applications, PWA/offline packaging, and a multithreaded browser runtime.
