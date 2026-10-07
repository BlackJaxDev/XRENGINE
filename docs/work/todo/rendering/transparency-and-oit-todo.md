# Transparency And OIT TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Transparency And OIT](../../../architecture/rendering/transparency-and-oit.md)  Design: [Transparency And OIT Implementation Plan](../../design/rendering/transparency-and-oit-implementation-plan.md)
Validation: [Default And Advanced Pipeline Validation](../../testing/rendering/default-and-advanced-pipeline-validation.md)

## Current State

`ETransparencyMode` defines masked, blended, weighted OIT, PPLL, depth peeling, stochastic, alpha-to-coverage, and triangle-sorted modes. `DefaultRenderPipeline` declares weighted OIT resources and exact transparency resources. Weighted OIT runs through accumulation and resolve passes. Exact transparency is an editor diagnostic path that uses PPLL and up to four depth-peeling layers. Exact transparency is disabled for stereo.

## Open Code Items

### Masked And Alpha-To-Coverage

- [ ] Add an import warning for diffuse-alpha-only materials that the importer classifies as `AlphaBlend`. Import and material diagnostics. Done when: users see an actionable warning before the material enters the blended path.
- [ ] Ensure fragment shaders output alpha correctly for alpha-to-coverage conversion. Forward material shaders and material options. Done when: masked MSAA content can use coverage without losing depth correctness.

### Exact And Experimental Modes

- [ ] Add stochastic threshold generation. Transparency shaders. Done when: the mode has a blue-noise or hash threshold source behind a disabled-by-default setting.
- [ ] Integrate stochastic transparency with TAA or TSR history. Temporal shaders and transparency pass metadata. Done when: history rejection handles stochastic transparency without ghosting.
- [ ] Add a mesh or submesh flag for triangle-sorted transparency. Mesh and material metadata. Done when: a renderer can request triangle sorting for a specific content class.
- [ ] Implement GPU compute sorting of triangle indices for flagged meshes. Transparency compute path. Done when: flagged meshes can reorder triangles without rebuilding CPU index buffers.

### Variant Management

- [ ] Define one transparency variant axis for opaque, masked, OIT, and exact modes. Shader variant descriptors and material metadata. Done when: transparency permutations are systematic across material families.
- [ ] Prevent ad-hoc transparency permutation growth. Uber and forward shader variant builders. Done when: each transparency mode maps to a documented variant key.

### Diagnostics And Telemetry

- [ ] Add transparent draw-call counters by mode. Render stats and profiler output. Done when: logs or the profiler report draw counts for masked, weighted OIT, PPLL, depth peeling, and ordinary transparent passes.
- [ ] Add a screen-space transparent overdraw estimate. Transparency diagnostics. Done when: the editor can show overdraw pressure without a RenderDoc capture.
- [ ] Add a weighted OIT resolve GPU timer. Profiler scopes. Done when: the resolve cost is visible in GPU dumps.
- [ ] Add PPLL fragment count and overflow telemetry. PPLL counter buffer and debug output. Done when: overflow and fragment count are visible in diagnostics.
- [ ] Add depth-peel pass count telemetry. Depth-peeling command path. Done when: the active layer count and rendered layer count are recorded per frame.
- [ ] Add masked versus blended material counts. Material database or render stats. Done when: classification changes can be audited.

### Documentation

- [ ] Update material API documentation for `ETransparencyMode`. Done when: each mode has a user-facing contract and known limits.
- [ ] Update import documentation for transparency options. Done when: importer choices for alpha are documented.

## Decisions Needed

- [ ] Decide whether weighted OIT is the only v1 shipping OIT path, or whether one exact mode ships as supported. Owner: rendering lead.
- [ ] Choose the content class that justifies triangle-sorted transparency. Owner: rendering lead.

## Out Of Scope

- Shipping stochastic transparency before temporal stability is proven.
- Shipping triangle sorting without a specific content need.
- Redesigning the full render graph only for transparency.
