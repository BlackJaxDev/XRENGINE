# Uber Shader UI Annotations

The Uber shader inspector is now driven by shader-source annotations instead of a parallel C# metadata table. The curated authoring surface in the ImGui material inspector only shows properties that opt in with explicit `//@property(...)` metadata, while the parser emits warnings for missing metadata on user-facing Uber controls.

## Authoring Rules

- Use `//@feature(...)` immediately before the guard block that owns an optional feature family.
- Use `//@property(...)` immediately before each sampler or value that should appear in the custom Uber inspector.
- Keep transport helpers such as `_ST`, `Pan`, `UV`, and enable toggles unannotated unless they intentionally belong in the curated UI.
- Prefer explicit `display`, `range`, `enum`, and `tooltip` metadata over inferred names for any property a material author is expected to edit.

## Supported Directives

- `//@category("...")`: starts a top-level UI grouping.
- `//@subcategory("...")`: refines grouping inside the current category.
- `//@feature(id="...", name="...", default=on|off, cost=low|medium|high)`: describes an optional compile-time module.
- `//@depends("feature-a|feature-b")`: declares features that must be enabled first.
- `//@conflicts("feature-a|feature-b")`: declares features that must be disabled first.
- `//@property(name="...", display="...", mode=static|animated, slot=texture, range=[min,max], enum="...")`: declares a curated property surface.
- `//@tooltip("...")`: attaches descriptive text to the next feature or property.

## Validation Behavior

When `XRShader` parses a shader under `Build/CommonAssets/Shaders/Uber/`, the manifest parser adds Uber-specific warnings for:

- feature families that still rely on inferred `XRENGINE_UBER_DISABLE_*` guard metadata instead of explicit `//@feature(...)`
- user-facing Uber properties that lack explicit `//@property(...)` metadata

That validation is intentionally narrow: helper uniforms used for UV transforms, panning, enable toggles, or engine transport state are ignored so warnings stay focused on the custom inspector surface.
Known legacy compatibility uniforms that are not part of the curated Uber authoring experience are also excluded from the required-annotation warning path.

## Runtime Contract

- `XRMaterial.UberAuthoredState` remains the source of truth for feature enablement, property modes, and preserved static literals.
- The inspector uses explicit property metadata to decide what belongs in the curated Uber UI.
- Missing annotation coverage is surfaced as validation warnings in the shader inspector and the Uber material inspector instead of silently falling back to inferred metadata.

## Editing Workflow

1. Add or rename uniforms in the Uber shader source.
2. Add matching `//@property(...)` and `//@tooltip(...)` directives for any control that belongs in the custom UI.
3. Add or update the owning `//@feature(...)` block if the uniform is part of an optional module.
4. Rebuild the editor and confirm the manifest validation panel stays clean or only shows deliberate helper-field omissions.
