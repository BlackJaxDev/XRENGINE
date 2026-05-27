# Uber Shader Sponza Link Failures Todo

## Issue

Sponza uber material variants can generate very large fragment shaders and hit the OpenGL shared-context link timeout. Once a variant hash is marked failed, later materials with the same variant skip linking and only report that the hash failed. The visible result is that some meshes either never adopt the lit forward uber variant or appear as black/untextured fallback/prepass output.

Known symptom from logs:

- `MaterialVariant:eb56623a83258409` timed out during shared-context program link after 30 seconds.
- The editor later surfaced only `Hash is marked failed.`
- Materials showed loaded textures, but active shaders had missing sampler uniforms or never reached the lit variant.

## Current Patch State

- Added physical pruning for known uber feature guards and pass-axis conditionals in `UberShaderVariantBuilder`.
- Embedded `TextFile` shader sources now retain a direct/local `FilePath`, so generated uber variants can recover the canonical `UberShader.frag` instead of treating generated source as the authority.
- Variant cache keys include source identity plus source text version, stale pruning skips pathless/in-memory display names, and the focused fixture has an explicit cache reset hook.
- Disabled feature properties are excluded from static/animated property lists and sampler counts.
- Explicit `//@feature` metadata now wins over later inferred `#ifdef/#ifndef XRENGINE_UBER_DISABLE_*` fallback branches.
- Enabling an uber feature primes default manifest parameter values/textures when the existing shader parameter is still at its language default.
- Failed variant hashes now publish the original failure reason when available.
- Static literals now prefer authored/live material values and can fall back to already baked active/requested literals.
- `UberMaterialVariantTests` passes reliably in the focused run after covering generated-fragment adoption, canonical recovery, pruning, and cache isolation.

## Remaining Todos

1. Verify conditional pruning against the production uber shader.
   - Validate `#if/#ifdef/#ifndef/#elif/#else/#endif` pruning against the real `Build/CommonAssets/Shaders/Uber/UberShader.frag`.
   - Confirm disabled feature branches are physically removed and unknown preprocessor logic is preserved.
   - Measure generated source size before/after pruning for Sponza materials.

2. Validate Sponza runtime behavior.
   - Clear failed variant hashes/cache, load Sponza, and confirm variants no longer hit the 30 second GL link timeout.
   - Confirm linked meshes render through the lit forward pass, not only prepass/depth/fallback output.
   - Check that albedo/normal sampler uniforms are present for active lit variants and no active material reports `sampler-uniform-missing`.

3. Add the remaining status/UI regression coverage.
   - Add a regression test that failed-hash UI/status reports the original link timeout reason.
   - Keep focused tests for explicit `//@feature(default=...)` overriding raw `XRENGINE_UBER_DISABLE_*` guards.
   - Keep tests that prove disabled-feature samplers and static uniforms are removed from generated source.

## Acceptance Criteria

- Sponza uber variants link without timeout on the OpenGL backend.
- Generated uber fragment source is substantially smaller for disabled-feature variants.
- Lit Sponza meshes show lighting and textures after variant adoption.
- Failed variant inspector text includes the concrete backend failure reason.
- `UberMaterialVariantTests` passes reliably without order/cache dependence.
