# Uber Shader Sponza Link Failures Todo

## Issue

Sponza uber material variants can generate very large fragment shaders and hit the OpenGL shared-context link timeout. Once a variant hash is marked failed, later materials with the same variant skip linking and only report that the hash failed. The visible result is that some meshes either never adopt the lit forward uber variant or appear as black/untextured fallback/prepass output.

Known symptom from logs:

- `MaterialVariant:eb56623a83258409` timed out during shared-context program link after 30 seconds.
- The editor later surfaced only `Hash is marked failed.`
- Materials showed loaded textures, but active shaders had missing sampler uniforms or never reached the lit variant.

## Current Patch State

- Added physical pruning for known uber feature guards and pass-axis conditionals in `UberShaderVariantBuilder`.
- Disabled feature properties are excluded from static/animated property lists and sampler counts.
- Failed variant hashes now publish the original failure reason when available.
- Static literals now prefer authored/live material values and can fall back to already baked active/requested literals.
- In-memory/generated shader canonical handling still needs cleanup before the full uber material fixture is reliable.

## Remaining Todos

1. Fix shader source identity for embedded/generated shaders.
   - `TextFile.FilePath` delegates to the containing `SourceAsset` for embedded assets, which hides the direct shader path in some material-embedded cases.
   - Add a clean way to read the shader source's direct/local path, or store an explicit shader source authority path on generated variants.
   - Use that identity consistently in `TryGetUberMaterialState`, canonical generated-fragment recovery, and variant cache keys.

2. Stabilize the uber variant cache for tests and runtime reloads.
   - Avoid pruning or reusing cached variants across unrelated in-memory sources that share the display path `UberShader.frag`.
   - Consider a test-only cache reset hook or a cache key that includes direct source identity plus source version.
   - Recheck async rebuild/adoption paths where status can become active while the material still exposes canonical source.

3. Finish and verify conditional pruning.
   - Validate `#if/#ifdef/#ifndef/#elif/#else/#endif` pruning against the real `Build/CommonAssets/Shaders/Uber/UberShader.frag`.
   - Confirm disabled feature branches are physically removed and unknown preprocessor logic is preserved.
   - Measure generated source size before/after pruning for Sponza materials.

4. Validate Sponza runtime behavior.
   - Clear failed variant hashes/cache, load Sponza, and confirm variants no longer hit the 30 second GL link timeout.
   - Confirm linked meshes render through the lit forward pass, not only prepass/depth/fallback output.
   - Check that albedo/normal sampler uniforms are present for active lit variants and no active material reports `sampler-uniform-missing`.

5. Harden tests.
   - Repair `UberMaterialVariantTests` failures around embedded canonical shader recovery and cache isolation.
   - Add focused tests for explicit `//@feature(default=...)` overriding raw `XRENGINE_UBER_DISABLE_*` guards.
   - Add tests that prove disabled-feature samplers and static uniforms are removed from generated source.
   - Add a regression test that failed-hash UI/status reports the original link timeout reason.

## Acceptance Criteria

- Sponza uber variants link without timeout on the OpenGL backend.
- Generated uber fragment source is substantially smaller for disabled-feature variants.
- Lit Sponza meshes show lighting and textures after variant adoption.
- Failed variant inspector text includes the concrete backend failure reason.
- `UberMaterialVariantTests` passes reliably without order/cache dependence.
