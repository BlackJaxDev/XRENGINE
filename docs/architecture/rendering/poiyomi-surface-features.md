# Poiyomi Surface And Layer Rendering Contract

This document records the native XRENGINE behavior implemented for surface and layer features
of the pinned Poiyomi Toon 9.3 conversion.

## Surface, Sampling, Masks, And Themes

The `surface-extensions` shader family owns main color adjustment, alpha modes,
normal correction, backface shading, detail blending, projection modes, and
stochastic sampling. UV0-UV3, object/world projection, panosphere, polar,
Deliot-Heitz, and hex-tile paths retain explicit derivatives for mip-correct
sampling. Normal and mask imports retain their authored linear-data intent.

`color-adjustments` activates from the `_MainColorAdjustToggle` section toggle
(`COLOR_GRADING_HDR`) or non-zero hue, saturation, or brightness adjustments,
not from the `_ColorGradingToggle` gradation sub-toggle. When a material enables
`color-adjustments` without `_MainColorAdjustTexture`, the importer binds a
white `1x1` mask texture so forward and compute Uber paths evaluate full-strength
mask channels `(1, 1, 1, 1)`.

For transparent GrabPass materials (`_SourceGrabPassBlendMode > 0`), both
`UberShader.frag` and `ShadeNativeUber.glslinc` scale pre-GrabPass base color by
`1 - b^2` (`_GrabSrcBlend == SrcAlpha`, mode `1`), `1 - b` (`_GrabSrcBlend == Zero`,
mode `2`), or `1` (`_GrabSrcBlend == One`, mode `3`), where
`b = _SourceGrabPassBlendFactor * (1 - alpha)`. Subsequent matcap and source toon
rim `Replace`, `Multiply`, `Screen`, and `Mixed` blends attenuate the tracked
destination transmission `b^2` so final output opacity `1 - transmission`
matches source toon post-GrabPass compositing under Weighted Blended OIT and splat
alpha blending.

The `global-masks-themes` family exposes four RGBA global-mask textures,
sixteen selectable channels, channel remapping/inversion, view and surface
modifiers, four themes, and RGBA color-mask contributions to color, normal,
PBR, and emission.

AudioLink modulation is neutral when no runtime AudioLink provider is active.
The importer preserves authored values and emits a provider diagnostic rather
than sampling an uninitialized resource.

## Lighting And Surface Response

The lighting and surface-response shader families use the engine Forward+ light
lists, shadow data, ambient/IBL inputs, contact depth, and reflection probes:

- Source toon additive lights map to Forward+ direct-light enumeration and honor
  `_DisableDirectionalInAdd` for secondary directional lights.
- Unity ambient, lightmap, and probe intent maps to engine ambient, irradiance,
  and reflection-probe inputs; a per-material cubemap remains an override. When
  authored spherical harmonics are absent, fallback world ambient enters the
  canonical SH L0 term once and does not add diffuse probe irradiance a second
  time. Environment probe specular reflection applies on source-lit toon
  materials only when Mochie BRDF reflections (`_SourceMochieSecond.w > 0.5`)
  are enabled.
- Material AO and screen-space AO are supported. Unity/VRChat world-AO blocker
  volumes are classified as an external scene-lighting input and are not
  silently approximated.
- Directional, forced, minimum/capped, monochromatic, detail-shadow, SDF,
  multilayer, skin, cloth, realistic, and flat responses are evaluated in the
  main forward and compute `NativeUber` paths.

Specular lobe two, anisotropy, clear coat, UTS stylized specular
(`_StylizedSpecular` with `_StylizedReflectionMode == 0`, packed into
`_SourceUtsSettings`, `_SourceUtsLayer1`, `_SourceUtsLayer2`, `_SourceUtsColor`,
and `_SourceUtsFlags`), environmental rim, backlight, four matcap slots, a
second rim, and contact-depth rim are specialized under their respective Uber
feature families. When Unity standard `_SpecularHighlights` is `0`, the importer
sets `_SpecularSmoothness` to `0` so dormant smoothness values do not activate
direct specular lobes.

## Repeated Layers And Flipbooks

Decals and emissions use shared four-slot contracts and deterministic
0-to-3 order. Disabled families are removed from generated shader variants,
including their sampler bindings.

Texture arrays remain native `sampler2DArray` / `XRTexture2DArray` resources.
Unity array metadata preserves layer order and sampling settings. When the
source is a single image with explicit `flipbookRows` and `flipbookColumns`,
the importer performs an explicit row-major grid conversion. It never silently
interprets an arbitrary 2D texture as a sprite sheet. Animated image files use
their source frame order.

Video, TPS, and AudioLink consumers remain optional adapter inputs. Until those
providers are implemented, conversion reports the missing adapter and leaves
the authored non-adapter contribution intact.
