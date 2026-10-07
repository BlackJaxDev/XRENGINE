# Default Pipeline Depth Of Field TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Default Render Pipeline Notes](../../../architecture/rendering/default-render-pipeline-notes.md)
Validation: [Default And Advanced Pipeline Validation](../../testing/rendering/default-and-advanced-pipeline-validation.md)

## Current State

`DefaultRenderPipeline` and `AdvancedRenderPipeline` declare `DepthOfFieldCopyFBO` and `DepthOfFieldFBO`. `DepthOfField.fs` and `DepthOfFieldStereo.fs` share `DepthOfField.glslinc`. The current shader still performs a full-resolution 12-tap gather and recomputes circle of confusion for the center and each tap. The physical path uses `XRENGINE_LinearizeDepth`, but artist near-blur gating still compares raw depth against `FocusDepth`. No signed CoC texture or half-resolution blur chain exists.

## Open Code Items

### Correctness And Gating

- [ ] Make artist-mode near and far blur sign depth-mode aware. `DepthOfField.glslinc`, camera uniform bindings. Done when: near blur affects the foreground in both normal and reversed depth.
- [ ] Confirm physical mode matches the active camera depth mapping. `DepthOfField.glslinc`, `DepthOfFieldSettings`. Done when: physical focus distance matches normal and reversed depth cameras.
- [ ] Make the stereo policy explicit for both pipelines. `DepthOfFieldStereo.fs`, FBO factories, diagnostics. Done when: stereo renders correctly or reports a clear disabled reason.
- [ ] Add tests for disabled, capture, light-probe, and stereo DoF gates. `XREngine.UnitTests/`. Done when: unsupported contexts cannot sample the wrong texture shape.
- [ ] Add a regression test for near-blur sign in both depth modes. Done when: the test fails if foreground and background are swapped.

### Signed CoC And Blur Chain

- [ ] Add a signed CoC texture, preferably `R16F`, at internal resolution. Resource declarations and texture factories. Done when: the CoC resource is declared and sampled by later passes.
- [ ] Add a `DepthOfFieldCoC.fs` pass. Done when: near blur is negative, far blur is positive, and in-focus pixels are zero.
- [ ] Move physical and artist CoC calculation out of the gather shader. Done when: gather or composite shaders sample CoC rather than recomputing it per tap.
- [ ] Precompute physical camera coefficients on the CPU where practical. Done when: per-pixel shader math avoids repeated focal-length and aperture work.
- [ ] Add a CoC debug visualization mode. Done when: debug output distinguishes near, far, and focused pixels.
- [ ] Add half-resolution color and CoC textures. Done when: the blur chain can run below full resolution.
- [ ] Add a CoC-aware downsample pass. Done when: foreground blur and max far blur survive downsample without background leaks.
- [ ] Split near and far blur into separate buffers or equivalent signed layers. Done when: foreground and background blur can composite independently.
- [ ] Keep the legacy full-resolution gather selectable during validation. Done when: A/B comparison is possible before removal.
- [ ] Add a full-resolution composite with edge-aware foreground handling. Done when: source color, near blur, far blur, signed CoC, and depth discontinuities are combined without silhouette bleeding.
- [ ] Handle composite alpha intentionally. Done when: the new composite does not force alpha without a documented reason.
- [ ] Add fast paths for tiny CoC pixels and large-radius tile classification or thresholds. Done when: small blur is cheap and large blur is bounded.

### Post-FX And Authoring

- [ ] Replace the copy-then-render-back pattern with a generic post-FX ping-pong sequence if it fits the pipeline. `DefaultRenderPipeline.CommandChain.cs`, `AdvancedRenderPipeline.LateAndPostCommands.cs`. Done when: DoF avoids an unnecessary full-resolution blit.
- [ ] Evaluate shared motion-blur and DoF ping-pong infrastructure. Done when: pass order remains clear and code duplication is reduced only if safe.
- [ ] Add quality presets `Low`, `Medium`, `High`, and `Cinematic`. `DepthOfFieldSettings`. Done when: preset settings are visible in the post-process schema.
- [ ] Add bokeh shape, highlight, autofocus, and physical-camera controls. `DepthOfFieldSettings`, editor UI, shaders. Done when: controls are visible only when relevant.
- [ ] Add optional temporal or stochastic sampling for high-quality bokeh. Done when: history rejection works with TAA and TSR.
- [ ] Add debug views for CoC, near blur, far blur, focus plane, tile classification, and sample cost. Done when: incorrect focus and high cost can be diagnosed in the editor.

### Tests And Docs

- [ ] Add shader-contract tests for required DoF samplers and uniforms. `XREngine.UnitTests/`. Done when: refactors cannot remove required bindings silently.
- [ ] Document the intended DoF pass order relative to bloom, motion blur, TAA, TSR, and tonemapping. `docs/architecture/rendering/default-render-pipeline-notes.md`. Done when: the pass order is a stable architecture fact.
- [ ] Update `docs/developer-guides/rendering/render-pipelines/default-render-pipeline.xrs` after pass graph changes. Done when: exported script documentation matches the code.

## Decisions Needed

- [ ] Decide whether DoF runs before or after bloom. Owner: rendering lead.
- [ ] Decide whether high-quality DoF runs before or after temporal accumulation. Owner: rendering lead.
- [ ] Decide whether VR defaults DoF to disabled after stereo support exists. Owner: rendering lead.
- [ ] Decide whether physical DoF aperture mirrors exposure aperture or can be decoupled per camera. Owner: rendering lead.

## Out Of Scope

- Replacing the whole post-process graph.
- Adding cinematic controls before the signed CoC and blur chain are stable.
