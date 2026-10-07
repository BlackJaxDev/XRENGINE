# Modular GI Architecture TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Global Illumination Ownership And Selection](../../../../architecture/rendering/global-illumination-ownership.md), [Global Illumination Providers guide](../../../../developer-guides/gi/global-illumination.md)
Validation: [Global Illumination Validation](../../../testing/rendering/global-illumination-validation.md#modular-gi-contract)

## Current State

Both pipelines resolve one `GlobalIlluminationPlan` through `GlobalIlluminationProviderRegistry` and host the same modules through `DefaultGlobalIlluminationHostAdapter` and `AdvancedGlobalIlluminationHostAdapter`. Light probes and IBL and DDGI are modules. `VPRC_GlobalIlluminationCompositePass` does neutral composition. Advanced admission reads the plan. The old `IGlobalIlluminationPipelineProvider`, `UsesX` flags, host feature bits, and host pass trains are removed. Radiance Cascades has a module but is rejected at runtime. Other methods are unavailable descriptors. Every DDGI command scope on the Vulkan compute path still allocates managed memory each frame (about 1,248 to 4,360 bytes per command).

## Open Code Items

### Performance

- [ ] Remove recurring managed allocations from the Vulkan compute command path: binding snapshot capture (largest source), frame-operation rent, and semantic resource-use preparation. Link, generation context, snapshot validation, and queue insertion are already allocation-free. Done when: the DDGI command scopes report zero managed bytes in a full 240-sample allocation window, or each remaining allocation has a recorded reason.

### Second Representation

- [ ] Prove the common boundary with a second working representation. The candidate is Radiance Cascades; its producer work is in [Radiance Cascades Runtime Completion TODO](radiance-cascades-runtime-completion-todo.md). Done when: the registry marks a second provider supported without new host flags, provider-type checks, or algorithm resource factories in either pipeline.

### Tests (Owner Clearance Required)

- [ ] Replace source-string and bit-position assertions in `XREngine.UnitTests/Rendering/DDGIScaffoldingContractTests.cs` and Advanced rendering contract tests with behavioral provider and adapter contracts. Done when: no test asserts class layout or `UsesX` members.
- [ ] Add tests for registry resolution, explicit rejection, graph resource declarations, output semantics, mode, volume, and layout changes, abort publication, disposal, owner restart, stereo, two viewports, and two authored volumes. Done when: each behavior has a deterministic test.

## Decisions Needed

- [ ] Define a coverage and energy policy before more than one diffuse provider can be active, and before multi-volume blending. Owner: rendering lead.
