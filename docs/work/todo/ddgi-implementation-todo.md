# DDGI Implementation TODO

Last Updated: 2026-04-15
Current Status: the engine has a DDGI design plan, but no DDGI mode, no DDGI volume owner, no probe atlases, no DDGI passes, and no runtime path that decouples diffuse GI from the current reflection-probe and IBL path.
Scope: implement the DDGI roadmap from [../design/ddgi-integration-plan.md](../design/ddgi-integration-plan.md) as a real renderer feature, starting from honest scaffolding and ending at a usable dynamic diffuse GI path with large-scene scaling, baked fallback, and hybrid integration hooks.

## References

- [../design/ddgi-integration-plan.md](../design/ddgi-integration-plan.md)
- [../../features/gi/global-illumination.md](../../features/gi/global-illumination.md)
- [../../features/gi/light-probes.md](../../features/gi/light-probes.md)
- [../../features/gi/surfel-gi.md](../../features/gi/surfel-gi.md)
- [../../features/gi/restir-gi.md](../../features/gi/restir-gi.md)
- Morgan McGuire DDGI article series (2019), parts 1-4

## Working Rules

- Keep DDGI as a distinct diffuse GI family. Do not smuggle it into `LightProbeComponent` ownership or the current Delaunay interpolation path.
- Keep diffuse GI selection separate from specular IBL ownership. DDGI must be able to coexist with current reflection probes and sky IBL.
- Treat visibility atlases, probe relocation, and persistent atlas-backed resources as core feature work, not polish.
- No steady-state managed allocations in DDGI hot paths.
- Validate each phase with the smallest targeted build and scene coverage that proves the new behavior.

## Exit Conditions For This Work Item

- `EGlobalIlluminationMode.DDGI` produces visible, inspectable diffuse GI.
- DDGI works on the current OpenGL baseline through the GPU BVH path.
- DDGI diffuse can coexist with current reflection-probe or sky IBL specular.
- The engine has a bounded-volume path first, then a documented large-scene scaling path.
- The work doc and stable GI docs describe DDGI honestly, including known limitations such as zero-thickness wall leaks and scene-scale bias tuning.

## Cross-Phase Validation Scenes

Keep these scenes alive across the whole implementation instead of inventing a new validation scene every phase:

- [ ] Cornell-box color-bleed scene
- [ ] sealed-room versus open-room leak test
- [ ] moving dynamic-geometry stress scene with objects passing through probe locations
- [ ] day-night or color-changing key light transition scene
- [ ] reflective interior scene that combines DDGI diffuse with current specular IBL or reflections

## Phase 0 - Honest Scaffolding

Outcome: the engine exposes DDGI as a real planned mode with honest stubs and author-facing scaffolding.

- [ ] add `DDGI` to `XREngine.Data/Core/Enums/EGlobalIlluminationMode.cs`
- [ ] expose the new GI mode anywhere `UserSettings` or startup settings surface GI mode selection
- [ ] add `UsesDDGI` helpers in both default render pipelines
- [ ] add neutral DDGI resource names to both pipeline texture-name files
- [ ] add a no-op or explicit-not-implemented DDGI branch in command-chain routing with honest logging and profiler labels
- [ ] add `DDGIVolumeComponent` scaffolding under `XRENGINE/Scene/Components/Lights/`
- [ ] expose first-pass editor-visible fields: probe counts, hysteresis, normal bias, view bias, update mode, update budget, baked mode, debug flags
- [ ] add unit-testing world toggles or scene bootstrap hooks for bounded DDGI volume bring-up
- [ ] update stable GI docs only enough to describe DDGI as planned or experimental once the runtime selector exists

Acceptance criteria:

- selecting DDGI is a real code path and not a missing enum case
- the renderer states clearly that DDGI is stubbed until the real passes land
- a scene can contain a `DDGIVolumeComponent` without special-case hacks

## Phase 1 - Volume Owner And Persistent Resource Contract

Outcome: DDGI has a real runtime owner and persistent atlas-backed resource set.

- [ ] define the authoritative runtime DDGI state object owned by `DDGIVolumeComponent` or a rendering-side companion
- [ ] allocate the probe state buffer as a persistent structured buffer or SSBO
- [ ] allocate the irradiance atlas as `R11G11B10F`
- [ ] allocate the visibility atlas as `RG16F`
- [ ] allocate the screen-space DDGI output texture
- [ ] lock atlas indexing conventions for probe tile addressing, borders, and cascade slices
- [ ] define probe tile sizes explicitly: 6x6 irradiance including border, 16x16 visibility including border
- [ ] define the first persistent ray buffer and hit-buffer contracts
- [ ] plumb resize, disposal, and stereo-safe lifetime handling for all DDGI resources
- [ ] add debug names and profiler labels for every DDGI resource

Acceptance criteria:

- DDGI resources are visible in renderer resource dumps and debug tooling
- resource lifetime is stable across resize, scene reload, and stereo paths
- no per-frame managed allocations are introduced by merely enabling the stub path

## Phase 2 - Probe-Ray Generation And Trace Backend

Outcome: DDGI can generate probe rays and trace them through the current baseline backend.

- [ ] define deterministic per-probe ray generation with stable stratification
- [ ] define per-ray fields: origin, direction, probe index, cascade index, and any flags needed for updates
- [ ] choose the first probe-space to world-space transform contract for bounded volumes
- [ ] integrate `BvhRaycastDispatcher` or the owning `VisualScene3D.BvhRaycasts` flow as the first DDGI trace backend
- [ ] define the unified hit contract shared by all future trace backends
- [ ] include enough hit data for direct-lighting and material shading without inventing a second lighting model
- [ ] add a DDGI trace debug view that can show ray count, valid hits, and miss ratio
- [ ] decide whether the first trace pass executes on the main render context or the secondary context and document why

Acceptance criteria:

- DDGI probe rays trace successfully on the current OpenGL baseline
- a debug view can prove rays and hits are spatially sane
- the backend seam is clean enough that hardware RT can be added later without rewriting DDGI sampling

## Phase 3 - Hit Shading, Irradiance Updates, And Visibility Updates

Outcome: traced rays become real probe data that converges over time.

- [ ] shade DDGI ray hits using the engine's existing direct-light and material logic
- [ ] include emissive in DDGI hit shading
- [ ] include previous-frame DDGI contribution in ray-hit shading for infinite-bounce convergence after warm start
- [ ] define warm-start behavior for frame zero or invalidated probes
- [ ] implement irradiance update with hysteresis around the design default of 97 percent
- [ ] implement visibility update storing distance moments for Chebyshev-based leak rejection
- [ ] verify atlas border propagation or copy behavior for bilinear-safe sampling
- [ ] add explicit invalidation paths for large lighting or geometry changes
- [ ] add debug views for irradiance tiles, visibility tiles, and per-probe update state

Acceptance criteria:

- DDGI probe atlases converge over multiple frames instead of flickering
- the sealed-room leak test materially outperforms current classic light probes
- hit shading, irradiance update, and visibility update timings are visible independently in profiling

## Phase 4 - Screen Sampling And Light Combine Integration

Outcome: DDGI becomes visible in the final frame.

- [ ] implement `sampleDDGI` as a reusable shader include callable from deferred, forward, volumetric, and ray-hit shading paths
- [ ] reconstruct world position and normal from the G-buffer where needed for deferred sampling
- [ ] implement regular-grid 8-probe neighborhood lookup
- [ ] implement normal-direction cosine weighting
- [ ] implement Chebyshev visibility weighting using the visibility atlas moments
- [ ] write the resolved result into the DDGI screen output texture
- [ ] composite DDGI diffuse into the existing light combine path without hard-coupling specular IBL ownership back to `LightProbesAndIbl`
- [ ] add a DDGI-only debug mode and a probe-neighborhood debug mode
- [ ] verify stereo correctness by sampling once per eye from the shared probe field

Acceptance criteria:

- enabling DDGI visibly changes the frame with stable diffuse GI
- DDGI sampling works in deferred rendering and stereo rendering
- diffuse DDGI and existing reflection-probe or sky IBL specular can coexist without special-case hacks

## Phase 5 - Probe Relocation, Classification, And Scheduling

Outcome: DDGI behaves robustly in the difficult cases that justify using it.

- [ ] implement relocation offsets bounded to half a cell along each axis
- [ ] prevent probes embedded in solid geometry from poisoning nearby shading
- [ ] add probe classification to detect inactive, invalid, or low-value probes
- [ ] add sleep or wake logic for stable probes
- [ ] add a budget-aware update scheduler that can choose which probes update this frame
- [ ] expose update mode as fixed-time versus fixed-quality
- [ ] wire fixed-time budget in milliseconds and fixed-quality budget in total rays
- [ ] add profiler counters for relocated, sleeping, invalidated, and updated probes

Acceptance criteria:

- moving geometry through probe locations does not cause catastrophic dark or bright leaks
- lower-end budgets increase world-space latency rather than introducing screen-space noise
- scheduling controls have measurable effect on GPU time without breaking image stability

## Phase 6 - Cascades And Large-Scene Scaling

Outcome: DDGI scales beyond a single local volume.

- [ ] implement the first camera-relative or scrolling cascade path after bounded volumes are proven correct
- [ ] start from the article-aligned finest cascade target of 32 x 4 x 32 probes where appropriate
- [ ] support multiple cascades with different spatial resolution and update frequency
- [ ] implement cascade-local indexing and resource addressing
- [ ] fade out and then disable visibility storage on very coarse cascades where justified
- [ ] validate the per-cascade memory budget and total peak budget
- [ ] add debug views for cascade coverage and active update budget per cascade
- [ ] document when bounded local volumes remain preferable to cascades

Acceptance criteria:

- large scenes no longer require every probe to update every frame
- DDGI cost can be constrained by budget rather than exploding with world scale
- cascade coverage, cost, and artifact tradeoffs are documented and inspectable

## Phase 7 - Baked And Infinite-Latency DDGI

Outcome: DDGI has a real fallback tier for budget or legacy platforms.

- [ ] define the baked DDGI asset format for irradiance and visibility atlases
- [ ] serialize and load baked probe atlases through the same runtime sampling path used by dynamic DDGI
- [ ] expose per-volume baked, slow-update, or fully dynamic mode selection
- [ ] ensure baked DDGI still uses the same visibility-based leak rejection path
- [ ] add editor workflow notes for baking or regenerating DDGI volumes
- [ ] document feature limits for baked DDGI: no dynamic light or dynamic geometry capture

Acceptance criteria:

- a DDGI volume can load and render without runtime tracing
- baked DDGI still benefits from the visibility atlas and avoids classic probe leak behavior
- the runtime does not fork into a second unrelated DDGI shading code path

## Phase 8 - Hybrid Integrations And Productization

Outcome: DDGI fits naturally into the rest of the renderer and docs.

- [ ] evaluate whether ReSTIR hit shading should sample DDGI for diffuse fallback or secondary diffuse lighting
- [ ] evaluate whether glossy RT and DDGI ray work should share a dispatch or packed buffer path
- [ ] decide whether a short-range AO or other near-field detail layer should complement DDGI
- [ ] finalize support language: experimental, advanced, or production-supported
- [ ] update stable GI docs with accurate setup, limits, budgets, and debug guidance once DDGI becomes usable
- [ ] add or update focused unit tests under `XREngine.UnitTests/Rendering/` for atlas indexing, octahedral math, hysteresis, visibility weighting, and relocation
- [ ] run targeted validation on stereo and unit-testing-world entry points

Acceptance criteria:

- DDGI is no longer an isolated experimental branch with unclear interaction against the rest of the lighting stack
- stable docs describe how DDGI relates to light probes, ReSTIR, Surfel GI, and specular IBL
- unit tests exist for the core math and atlas plumbing that can regress silently

## Key Files

Expected touched files during implementation:

- `XREngine.Data/Core/Enums/EGlobalIlluminationMode.cs`
- `XREngine.Data/Core/UserSettings.cs`
- `XRENGINE/Settings/GameStartupSettings.cs`
- `XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.cs`
- `XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.SecondaryContext.cs`
- `XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs`
- `XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.Textures.cs`
- `XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.cs`
- `XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.Textures.cs`
- `XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.CommandChain.cs`
- `XRENGINE/Rendering/Compute/BvhRaycastDispatcher.cs`
- `XRENGINE/Scene/Components/Capture/LightProbeGridSpawnerComponent.cs`
- `Assets/UnitTestingWorldSettings.jsonc`

Expected new files:

- `XRENGINE/Scene/Components/Lights/DDGIVolumeComponent.cs`
- `XRENGINE/Rendering/Pipelines/Commands/Features/GI/VPRC_DDGITracePass.cs`
- `XRENGINE/Rendering/Pipelines/Commands/Features/GI/VPRC_DDGIUpdatePass.cs`
- `XRENGINE/Rendering/Pipelines/Commands/Features/GI/VPRC_DDGICompositePass.cs`
- `XRENGINE/Rendering/Pipelines/Commands/Features/GI/VPRC_DDGIDebugVisualization.cs`
- shader files under `Compute/DDGI/` and `Scene3D/DDGI*`
- `XREngine.UnitTests/Rendering/DdgiComputeIntegrationTests.cs`

## Suggested Next Step

Start with Phase 0 and Phase 1 together:

- land the enum, mode routing, resource names, and `DDGIVolumeComponent` scaffolding
- allocate persistent DDGI resources in both default pipelines
- add an honest stub path with profiler labels and logging
- wire a bounded DDGI unit-testing-world scene so the later trace and update work has a stable place to land

That sequence creates the minimum truthful DDGI seam in the engine without pretending that the actual GI feature already exists.