# Documentation Index

Start here for XRENGINE documentation. The main handwritten docs are split by audience:

- [Architecture](architecture/README.md): engine internals, subsystem boundaries, lifecycle, data flow, invariants, and design tradeoffs.
- [Developer Guides](developer-guides/README.md): code-facing guides for implemented features, extension points, diagnostics, tests, and implementation references.
- [User Guide](user-guide/README.md): surface-level engine concepts, editor-facing settings, workflows, and common usage.
- [Legal and Licensing](../LEGAL/README.md): license terms, commercial
  information, contribution terms, and release guidance.
- [Work Docs](work/README.md): active design docs, TODOs, audits, testing notes, and historical implementation plans.

[Runtime Project Organization](architecture/runtime/project-organization.md) maps the shared `net10.0` libraries, native modules, application composition, and asset ownership. [Portable Project Rules](developer-guides/runtime/portable-projects.md) describes the package/source policies and browser compile lane. Outstanding integration acceptance is tracked in the [native subsystem debugging and validation TODO](work/todo/platform/native-subsystem-project-split-todo.md); completed local browser, desktop and native callback smokes, full test execution and qualification limits are recorded in the [reference harness investigation](work/investigations/rendering/desktop-browser-reference-harness.md).

## Architecture

- [Architecture Overview](architecture/README.md)
- [Getting Started In The Codebase](architecture/getting-started-in-codebase.md)
- [Runtime Project Organization](architecture/runtime/project-organization.md)
- [Rendering Architecture](architecture/rendering/README.md)
- [Rendering Runtime Overview](architecture/rendering/runtime-overview.md)
- [Advanced TSR Sampling, History, And Diagnostics](architecture/rendering/default-render-pipeline-notes.md#advanced-tsr-sample-and-history-contract)
- [Frame Lifecycle And Dispatch Paths](architecture/rendering/frame-lifecycle-and-dispatch-paths.md), including ordered snapshot-input edges and callback ownership
- [Mesh Submission Strategies](architecture/rendering/mesh-submission-strategies.md)
- [WebGPU Sky Backgrounds](architecture/rendering/webgpu-sky-background.md)
- [Native Authored Material Vertex Functions](work/progress/rendering/browser-native-material-vertices-2026-10-03.md)

- [Vulkan Scene Preparation And Publication](architecture/rendering/vulkan-scene-preparation-and-publication.md)
- [Renderer Backend Hot Reload](architecture/rendering/renderer-backend-hot-reload.md)
- [Vulkan Pipeline Compilation, Readiness And Depth-Only Coverage](architecture/rendering/vulkan-pipeline-compilation.md)
- [CPU Scene BVH](architecture/rendering/cpu-scene-bvh.md)
- [GPU Scene BVH](architecture/rendering/gpu-scene-bvh.md)
- [Scene Architecture](architecture/scene/overview.md)
- [Transform Architecture](architecture/scene/transforms.md)
- [Physics Architecture](architecture/physics/overview.md)
- [Audio Architecture](architecture/audio/audio-architecture.md)
- [Networking Overview](architecture/networking/overview.md)
- [Control Plane Runtime Architecture](architecture/runtime/control-plane.md)
- [Modeling XRMesh Editing](architecture/modeling/xrmesh-editing.md)
- [Play Mode Architecture](architecture/editor/play-mode-architecture.md)
- [Editor Undo System](architecture/editor/undo-system.md)
- [Editor Background Preparation](architecture/editor/background-preparation.md)

## Developer Guides

- [Software Vulkan Correctness Validation](developer-guides/testing/software-vulkan-validation.md)
- [Continuous Integration And Releases](developer-guides/ci-cd.md)
- [Windows CI Build Preparation Investigation](work/investigations/platform/windows-ci-build-preparation-2026-10-05.md)
- [Windows CI Test Coverage](developer-guides/testing/windows-ci.md)
- [MCP Server Implementation](developer-guides/ai/mcp-server.md)
- [MCP Assistant](developer-guides/ai/mcp-assistant.md)
- [Local Agent Broker Implementation](developer-guides/ai/local-agent-broker.md)
- [Animation API](developer-guides/animation/animation-api.md)
- [Model Import](developer-guides/assets/model-import.md): import backends, warning logs, and missing-texture reporting after import.
- [Native FBX Import And Export](developer-guides/assets/native-fbx-import-export.md)
- [OpenAL Streaming Audio](developer-guides/audio/openal-streaming-audio.md)
- [Component API](developer-guides/components/component-api.md)
- [Atmospheric Scattering Component](developer-guides/components/atmospheric-scattering.md)
- [Global Illumination](developer-guides/gi/global-illumination.md)
- [Networking](developer-guides/networking/networking.md)
- [Browser Realtime Transport](developer-guides/networking/browser-realtime.md)
- [Control Plane](developer-guides/networking/control-plane.md)
- [Physics API](developer-guides/physics/physics-api.md)
- [Scene Graph Developer Guide](developer-guides/scene/scene-graph.md)
- [Engine API](developer-guides/runtime/engine-api.md)
- [Portable Project Rules](developer-guides/runtime/portable-projects.md)
- [Runtime Environment Settings](developer-guides/runtime/runtime-environment-settings.md)
- [Hot-Path Memory Control](developer-guides/runtime/hot-path-memory.md)
- [Job System](developer-guides/runtime/job-system.md)
- [Profiler](developer-guides/diagnostics/profiler.md)
- [Dedicated Vulkan RenderBench](developer-guides/diagnostics/profiler.md#dedicated-vulkan-renderbench)
- [Runtime Data Layout Measurements](developer-guides/diagnostics/runtime-data-layout-measurements.md)
- [Runtime Regression And NativeAOT Hardening](work/todo/runtime-regression-and-nativeaot-hardening-todo.md): current software regression and strict packaged-player acceptance, with an active [progress ledger](work/progress/runtime/runtime-regression-and-nativeaot-hardening-progress.md).
- [Self-Iterating Rendering Performance Loop](developer-guides/diagnostics/self-iterating-performance-loop.md)
- [Skinning](developer-guides/rendering/skinning.md)
- [Blendshaping](developer-guides/rendering/blendshaping.md)
- [Poiyomi Toon Material Conversion](developer-guides/rendering/poiyomi-toon-material-conversion.md)
- [Maintaining Poiyomi Toon Support](developer-guides/rendering/poiyomi-toon-maintenance.md)
- [Vulkan OBS Hook Compatibility](developer-guides/rendering/vulkan-obs-hook-compatibility.md)
- [Surface Detail And Forward Shadows](developer-guides/rendering/shadows/surface-detail-forward-shadows.md)
- [OpenXR Runtime](developer-guides/vr/openxr-runtime.md)
- [OpenXR Body Tracking and Spectator Integration](developer-guides/vr/openxr-body-tracking.md)
- [Full-Body VR Calibration](developer-guides/vr/full-body-calibration.md)
- [VR Body Measurements](developer-guides/vr/body-measurements.md)
- [VR Spectator Camera](developer-guides/vr/spectator-camera.md)
- [VR Calibration Session Continuity](developer-guides/vr/calibration-session-continuity.md)
- [VR Developer Guide](developer-guides/vr/vr-development.md)

## User Guide

- [Engine](user-guide/engine.md)
- [MCP Server And Assistant](user-guide/ai/mcp-server.md)
- [Local Agent Broker](user-guide/ai/local-agent-broker.md)
- [Scene System](user-guide/scene.md)
- [Component System](user-guide/components.md)
- [Transforms](user-guide/transforms.md)
- [Animation](user-guide/animation.md)
- [Physics](user-guide/physics.md)
- [Rendering](user-guide/rendering.md)
- [VR Development](user-guide/vr-development.md)
- [Full-body VR calibration](user-guide/vr/full-body-calibration.md)
- [Shader Editor](user-guide/editor/shader-editor.md)
- [Prefab Workflow](user-guide/prefab-workflow.md)
- [Job System](user-guide/job-system.md)

## Work Docs

- [Work Docs Index](work/README.md)
- [Shadow and Pipeline Validation — Completed](work/todo/rendering/shadow-and-pipeline-validation-failures-todo.md)
- [Editor OpenXR Toggle, Rendering, And Import Responsiveness Todo](work/todo/rendering/vr/editor-openxr-toggle-and-rendering-todo.md)
- [Six-Device VR Calibration Baseline](work/investigations/avatar/vr-calibration-baseline-2026-09-24.md)
- [OpenXR Full-body Implementation Record](work/progress/avatar/openxr-full-body-calibration-spectator-implementation.md)
- [OpenXR Calibration and Spectator Implementation](work/progress/avatar/openxr-calibration-spectator-implementation-2026-09-30.md)
- [OpenXR Calibration and Spectator Acceptance](work/testing/avatar/openxr-calibration-spectator-validation.md)
- [Windows Calibration and Spectator Validation](work/investigations/avatar/openxr-calibration-spectator-validation-2026-10-01.md)
- [Animation and IK Stability](work/investigations/avatar/animation-ik-stability-2026-09-30.md)
- [Tracked Body Solver Validation](work/investigations/avatar/tracked-body-solver-2026-09-30.md)
- [Spectator and Calibration Feedback Validation](work/investigations/avatar/spectator-validation-2026-09-30.md)
- [Vulkan Lifecycle Evidence Harness](work/testing/rendering/vulkan-lifecycle-evidence-harness.md)
- [Vulkan Stall Remediation: Remaining Tasks](work/todo/rendering/vulkan-stall-remediation-todo.md)
- [Vulkan Stall Separate Findings](work/todo/rendering/vulkan-stall-separate-findings-todo.md)
- [Vulkan Stall Remediation Results](work/progress/rendering/vulkan-stall-remediation-results.md)
- [Vulkan Stall Validation Protocol](work/testing/rendering/vulkan-stall-validation.md)
- [Control Plane Managed Server Instances and Client Synchronization Todo](work/todo/networking/control-plane-managed-server-instances-todo.md)
- [Apple Platform and MoltenVK Support Design](work/design/platform/apple-platform-moltenvk-support-design.md)
- [Runtime Modularization Plan](work/design/runtime-modularization-plan.md)
- [Texture Runtime, Streaming, And Virtual Texturing Design](work/design/texturing/texture-runtime-streaming-virtual-texturing-design.md)
- [Transparency And OIT Implementation Plan](work/design/rendering/transparency-and-oit-implementation-plan.md)
- [Exact VR Pipeline Selection](work/design/rendering/vr-pipeline-selection-design.md)
- [GPU Softbody Mesh Rigging Plan](work/design/rendering/gpu/gpu-softbody-mesh-rigging-plan.md)
- [Production Rendering Pipeline Roadmap](work/todo/rendering/gpu/production-rendering-pipeline-roadmap.md)

## Generated API Reference

The DocFX project lives in `docs/docfx/docfx.json` and targets `XRENGINE.sln` to produce browsable API reference from XML doc comments.

Build the site:

```powershell
dotnet tool restore
dotnet docfx docs/docfx/docfx.json
```

Preview locally:

```powershell
dotnet docfx docs/docfx/docfx.json --serve --port 8080
```

Generated output stays in `docs/docfx/_site`, which is ignored by Git.

## Browser runtime planning

[Shared native/browser world packages](developer-guides/networking/browser-shared-world-package.md)
documents the opt-in publisher contract that binds original native and cooked
browser representations under one verified managed-admission identity.

[Portable engine host ownership](work/progress/platform/portable-engine-host-ownership.md) records the shared facade, timer, services and desktop composition boundary. The [host validation record](work/investigations/platform/portable-engine-host-validation.md) contains the desktop/browser build gate, Editor camera/UI restore and Server/VRClient startup evidence, including current limitations. Browser engine composition is now present in source; the [unified runtime checklist](work/todo/platform/unified-desktop-browser-runtime-todo.md) and [2026-10-01 implementation checkpoint](work/progress/platform/unified-browser-checkpoint-2026-10-01.md) distinguish that work from still-open live browser and renderer acceptance.

[Portable browser engine boot qualification](work/progress/platform/portable-browser-engine-boot.md) records the earlier startup and asset round-trip boundaries. The [current checkpoint](work/progress/platform/unified-browser-checkpoint-2026-10-01.md) records subsequent shared-world lifecycle, native browser Jolt and rendered-game evidence, together with the remaining acceptance limits.

[Browser output source and companion ownership](work/progress/rendering/browser-output-lifetime-2026-10-04.md) records generated pipeline-source ownership, auxiliary material options and terminal command-container cleanup, with bounded production-method lifetime evidence.

[Browser project publishing](work/progress/rendering/browser-project-publishing.md) connects the existing editor/CLI build flow to saved-world export, a shared content packager and a static browser bundle. The genuine Editor-published RollingBall bundle now renders in Chromium/SwiftShader, but complete gameplay and physical-device acceptance remain open; packaged-editor publishing also remains open. See the [current checkpoint](work/progress/platform/unified-browser-checkpoint-2026-10-01.md) and [publishing record](work/progress/rendering/browser-project-publishing.md) for exact evidence and limits.

[Browser static hosting](developer-guides/runtime/browser-static-hosting.md) documents production HTTPS, MIME, compression, cache, CSP and conditional cross-origin isolation requirements. It is deployment guidance, not evidence that a production host or physical device was tested.

[Browser compute reuse audit and integration](work/progress/rendering/browser-compute-reuse-audit.md) maps existing skinning, GPU scene and Hi-Z contracts to their WebGPU backend implementations, experimental controls and deferred qualification. Canonical palette/bounds types are shared; no second production animator or scene database is introduced.

[Browser compute and indirect commands](work/progress/rendering/browser-compute-indirect.md) records device-limited compute, indirect draw submission, usage-scope checks, opt-in offscreen references and honest scene strategy selection. Runtime and mobile performance acceptance remain deferred.

[Browser interaction and runtime services](work/progress/rendering/browser-interactive-services.md) records touch/IME/gamepad input, CPU skeletal animation, bounded character collision, Web Audio and required-service declarations. Runtime acceptance remains deferred.

[Browser cooked content delivery](work/progress/rendering/browser-cooked-content.md) records the offline packager, hash-addressed manifest, asynchronous budgeted engine uploads, texture variants and optional HTTP caching policy. Source implementation remains unvalidated.

[Focused browser forward pipeline](work/progress/rendering/browser-focused-pipeline.md) records CPU-direct opaque/masked/transparent rendering, bounded material bindings, directional shadows, HDR/SDR composition, engine UI and mobile quality settings. Runtime acceptance remains deferred.

[WebGPU module and engine snapshot bridge](work/progress/rendering/browser-webgpu-module-assets.md) records real catalog registration, module-owned browser assets and static XRMesh/material/camera export and import. Source implementation remains unvalidated.

[Cooked browser shader artifacts](work/progress/rendering/browser-shader-artifacts.md) records target-tagged shader results, deterministic WGSL packaging, integrity/layout checks and requirement-aware startup. The TODO now separately checks off completed code and pending acceptance.

[Browser mesh and packet bridge](work/progress/rendering/browser-mesh-packet-bridge.md) records indexed geometry, textured unlit materials, depth testing, generation-stamped resources and batched submissions. Build and runtime validation remain deferred.

[Browser WebGPU canvas host](work/progress/rendering/browser-webgpu-canvas-host.md) records the new canvas, startup, scheduling and diagnostic draw implementation. Validation was explicitly deferred for this change.

[Portable browser scene boot](work/progress/rendering/portable-browser-scene-boot.md) records the implemented shared runtime, browser validation, build instructions and remaining rendering work.

[Mobile browser readiness](work/progress/rendering/mobile-browser-readiness.md) records the source dependency audit, sample contract, budgets and unvalidated device matrix for the [WebGPU runtime TODO](work/todo/rendering/mobile-webgpu-runtime-todo.md).


[Canvas output, visibility and live resource updates](work/progress/rendering/browser-webgpu-frame-output.md) records portable output metadata, the explicit canvas pass, per-view culling and resource replacement. Source implementation remains unvalidated.

[Portable browser host implementation](work/progress/rendering/browser-portable-host-completion.md) records generated registration, portability/API guards, focused renderer capabilities and explicit frame publication. Code completion is separate from deferred acceptance evidence.

[Batched browser upload bridge](work/progress/rendering/browser-upload-bridge.md) records reusable upload arenas, typed resource updates, ownership/overflow rules and bridge diagnostics. Exact-runtime lifetime and performance acceptance remains deferred.

[Browser engine resource acceptance](work/progress/rendering/browser-engine-resource-acceptance-2026-10-03.md) records retained physical-resource requests and transfers, one acceptance import for complete or pending scenes, cached queue completion, lifetime witnesses and remaining browser acceptance.

[Browser canonical shader staging](work/investigations/rendering/browser-canonical-shader-staging-2026-10-04.md) tracks full-inventory source closure, portable provenance and manifest-capacity integration checks.

[Browser attachment-clear scope unwind](work/investigations/rendering/browser-scissored-clear-unwind-2026-10-04.md) records pending-command crop/framebuffer scope leaks, bounded restoration, and the remaining exact-source browser check.

[Caller-thread physics-chain scheduling](work/progress/platform/browser-physics-chain-scheduling-2026-10-03.md) records inline CPU range execution, preserved solver selection, deterministic world cleanup and zero-allocation transform notification evidence on native .NET and the Node-hosted browser-wasm interpreter. Whole-browser allocation and lifetime acceptance remain separate.

[Caller-thread blocking-site inventory](work/progress/platform/browser-caller-blocking-inventory-2026-10-03.md) records queued mesh/BVH work, immediate transform publication, deferred shader-version readiness, browser event behavior and the remaining shared-code scheduling boundaries.

[Asset-manager source boundaries](work/progress/platform/browser-asset-manager-source-boundaries-2026-10-03.md) records catalog-only owner lifetime, host-file admission, retired watcher rejection and nonblocking remote-response publication with exact rollback ownership.

[Lower-level asset source admission](work/progress/platform/browser-lower-asset-source-boundary-2026-10-03.md) records captured raw text reads, source retirement, caller-thread admission and early file-mapping capability checks.

[Browser shader cooking and material generation](work/progress/rendering/browser-shader-cooking.md) records the C# cooker, typed WGSL generation, optional pinned Slang route, coordinate/layout contracts and bounded startup. Compiler/layout qualification and runtime acceptance remain deferred.

[Shader cook diagnostics](work/progress/platform/browser-shader-cook-diagnostics-2026-10-04.md) records material/pass/source context, mapped compiler locations, conservative fallback errors and genuine failed-cook evidence.

[Authored color coverage](work/progress/platform/browser-authored-color-coverage-2026-10-03.md) records explicit generated PBR color coverage, preserved opacity/cutoff/blending, exact normal/shadow companions, unchanged older texture payloads, and the separate device-acceptance boundary.

[Authored two-image alpha materials](work/progress/platform/browser-authored-textured-alpha-2026-10-03.md) records exact diffuse-alpha/red-mask coverage, additive authored and cooked carriers, matching receiver and auxiliary passes, native material roles, and real export/package evidence. Rendered coverage remains open.

[Authored forward normal and specular texture families](work/progress/platform/browser-authored-textured-families-2026-10-03.md) records six exact forward material families, shared RGB/height normal mapping, feature-specific raster companions, additive serialization and native source-basis preservation. Rendered acceptance remains separate.

[Native generated PBR surfaces](work/progress/platform/browser-native-authored-lit-2026-10-03.md) records exact cooked provenance, owner-scoped native admission, frozen factors/maps/coverage and raster-state rejection, with GPU acceptance kept separate.

[Generated opaque PBR shadow companions](work/progress/platform/browser-authored-opaque-shadows-2026-10-03.md) records exact generated V1 receiver/caster lowering, source and physical ABI validation, per-material shadow audits, and shared Default/Advanced export evidence.

[Browser GPU resources and ordered submission](work/progress/rendering/browser-gpu-resources-submission.md) records selected-device capabilities, buffers and texture subresources, framebuffer lowering, bounded pipeline caches, reusable commands and cancellable readback. Runtime acceptance remains deferred.

[Browser engine-frame diagnostics](work/progress/rendering/browser-engine-frame-diagnostics-2026-10-02.md) records bounded per-frame validation receipts, retained labels, explicit WebGPU/Promise object counts and opt-in managed allocation/crossing measurements.

[Modular browser pipeline requirements](work/design/platform/modular-browser-render-pipelines-2026-10-02.md) records the shared Default, Advanced and authored-pipeline scope, including GPU-driven zero-readback submission. The [implementation record](work/progress/rendering/browser-modular-pipeline-contracts-2026-10-02.md) distinguishes generic asset/catalog contracts from pending Advanced stage execution.

[Engine color MSAA resolve](work/progress/rendering/browser-color-msaa-resolve-2026-10-02.md) records shared renderbuffer ownership, retained four-to-one color resolves, precise unsupported combinations and remaining GPU acceptance.

[Default and generic browser MSAA](work/progress/rendering/browser-default-generic-msaa-2026-10-04.md) records x4 forward/prepass attachments, nearest-sample GTAO sidecars, ordered color resolve, preserved submission strategies and the separate generic reversed-depth gap.

[Generic browser reversed depth](work/progress/rendering/browser-generic-reversed-depth-2026-10-04.md) records frozen camera comparisons and clears, sky/postprocess ABI updates, custom callback compatibility and remaining known-value rendering.

[Advanced browser depth primitives](work/progress/rendering/browser-advanced-depth-stages-2026-10-02.md) records exact GTAO and conservative depth reduction and explicit storage encodings. [Native-family admission](work/progress/rendering/browser-advanced-admission-2026-10-02.md) records selected-device and program validation, output reservations, completion ownership, and remaining browser evidence.

[Recovered Advanced browser static integration](work/progress/rendering/browser-advanced-static-integration-2026-10-03.md) records the reconstructed native/output/effects family, authored-state preservation, fresh build/cook/probe and saved-world hydration results, unsupported profiles, and pending full browser acceptance.

[Advanced browser per-sample MSAA](work/progress/rendering/browser-advanced-msaa-2026-10-03.md) records exact packed16 visibility, canonical nearest-sample resolve, cohort union, f32 sample shading, resource costs, and the pending browser acceptance boundary.

[Browser view-history submission](work/progress/rendering/browser-view-history-submission-2026-10-03.md) records bounded metadata receipts, exact terminal-write proof, accepted-submission commits, and the separate motion/recovery acceptance boundary.

[WebGPU indirect submission](architecture/rendering/webgpu-indirect-submission.md) records GPU-written argument/count contracts, completion ownership, and the distinction between native Advanced compute meshlets and hardware task/mesh stages.

[Browser authored meshlet indexed submission](work/progress/rendering/browser-authored-meshlet-indexed-2026-10-03.md) records material-independent resident publication, conservative GPU cull/refit/expansion, exact authored raster reuse, completion ownership, explicit remaining profiles, and the pending runtime acceptance boundary.

[Native browser standalone shadows](work/progress/rendering/browser-native-standalone-shadow-2026-10-03.md) records typed depth/color banks, immutable producer receipts, exact receiver controls, shared cold admission, and the remaining live shadow acceptance.

[Mipmap caller-thread resizing](work/progress/platform/browser-mipmap-caller-resize-2026-10-03.md) records request-owned pixels, cancellation and unchanged serialization evidence. [Uber caller preparation](work/progress/rendering/browser-uber-caller-preparation-2026-10-03.md) records the implemented job route and owner-observed scheduler failures and remaining cooked-program boundary.

[Canonical Uber base materials](work/progress/rendering/browser-uber-base-material-contract-2026-10-04.md) records the exact source subset, target-only material/probe carriers, native companion tables, final cook/hydration evidence and remaining rendered acceptance.

[Ordinary engine Unlit materials](work/progress/rendering/browser-engine-unlit-materials-2026-10-04.md) records canonical authored cooks, runtime factory variants, target-only reflection ownership, genuine publication/hydration and remaining rendered acceptance.

[Runtime asset wrappers](work/progress/platform/browser-runtime-asset-wrapper-boundaries-2026-10-03.md) record serialization-owner admission. [Gaussian and baked DDGI consumers](work/progress/platform/browser-gaussian-ddgi-asset-consumers-2026-10-04.md) record captured asynchronous reads, owner-side adoption and reviewed cancellation lifetimes. [Caller frame identity](work/progress/rendering/browser-caller-scene-frame-identity-2026-10-03.md) records canonical collection, scene and retained-package identity. [Octahedral billboard preparation](work/progress/rendering/browser-octahedral-impostor-2026-10-03.md) and [asynchronous scene capture](work/progress/platform/browser-hlod-impostor-capture-boundary-2026-10-03.md) distinguish shader/geometry implementation from capture lifetime and physical rendering acceptance.

[Runtime I/O admission](work/progress/platform/browser-runtime-io-admission-2026-10-04.md) records captured remote-response ownership, cooked-font diagnostics and host-only capture, profiling, archive and transport boundaries.

[Browser download size](work/progress/platform/browser-download-size-boundary-2026-10-04.md) separates current framework build-resource measurements from final transfer acceptance and records implemented scene-shader deferral and the remaining managed-assembly boundaries.
