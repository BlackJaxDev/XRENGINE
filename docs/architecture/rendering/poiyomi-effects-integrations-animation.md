# Poiyomi Effects, Integrations, And Animation

Effects, integrations, and animation complete Poiyomi's effect/pass behavior, runtime integration
contracts, vertex deformation path, and Unity material animation bridge.

## Effects and passes

`extended_effects.glsl` owns UV/face discard, pathing, proximity, touch glow,
internal parallax, video blending, CRT/Gameboy/Voronoi/Truchet effects, and the
provider-backed environment/audio/view inputs. Coverage-affecting work runs
before the depth, shadow, and depth-normal exits.

Dissolve supports linear/basic, spherical, point-to-point, center-out, and UV
tile modes with base/detail noise, masks, edge gradient/texture, continuous
mode, coordinate selection, inversion, hue, and emission.

The outline companion remains a real inverse-hull draw. Mono, OVR multiview,
and NVIDIA stereo vertex variants share the same expansion helper and support
object/world/screen sizing, basic/rim/directional/drop-shadow expansion,
vertex-color width, fixed pixel width, depth offset, masks, texture, hue,
distance alpha, lighting, emission, and the imported pass's cull/depth/stencil/
blend/queue state.

## Vertex contract

`vertex_effects.glsl` runs after compute skinning and morph inputs in
the canonical vertex variants. The generated mesh vertex shader carries the
same core local transform, rounding, barrel, Uzumore, and world-translation
contract into override passes. Material vertex values are uploaded through a
cached `SettingVertexUniforms` binding; the draw loop does not allocate.

Materials using these effects opt out of GPU-indirect substitution because
that path owns a material-indexed vertex program. `_VertexConservativeBounds`
preserves the authored expansion hint for CPU culling. This is an explicit
submission classification, not a silent deformation fallback.

## Runtime adapters

`UberMaterialRuntimeAdapters` exposes optional providers:

- `IAudioLinkProvider` owns one stable texture. Bands are columns,
  newest-to-oldest history is rows, and scalar timing/history state is supplied
  with `AudioLinkFrame`. Providers update the resource in place.
- `IMaterialEnvironmentProvider` supplies native diffuse/specular/blacklight
  state for LTCGI and light-volume mappings.
- `PushViewContext` classifies main-camera, mirror, capture, stereo, and eye
  views with an allocation-free nested scope.

When a required provider is absent, import leaves its feature compiled out and
emits one actionable diagnostic. No constant or uninitialized input is
invented. Beat Saber state is preserved only as a missing-adapter diagnostic
because the pinned source does not define an engine-independent provider.

## Material animation

The Unity `.anim` importer recognizes `material.*`,
`materials.Array.data[n].*`, and `m_Materials.Array.data[n].*` bindings. Float,
integer-compatible, vector, and color curves reuse the existing tangent,
interpolation, wrap, and authored-frame path. Texture/object-reference curves
are retained as source metadata and report that resolved `XRTexture` key values
are required.

Animation member registration caches a `MaterialAnimationBinding`. Binding:

1. resolves the material slot and semantic property;
2. decodes locked rename suffixes by longest manifest-property prefix;
3. enables the property's feature/dependency closure;
4. promotes only that property to `Animated`;
5. prewarms the Uber variant and companion pass set;
6. applies values without a per-frame lookup allocation.

The binding re-resolves if model packing replaces its material. Original node
path, attribute, slot, source property, component, type, and class ID remain in
`AnimationClip.SourceMaterialBindings`; ambiguous and object-reference cases
remain in `MaterialBindingDiagnostics`.
