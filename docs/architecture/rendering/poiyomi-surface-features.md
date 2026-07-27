# Poiyomi Surface And Layer Rendering Contract

This document records the native XRENGINE behavior implemented for surface and layer features
of the pinned Poiyomi Toon 9.3 conversion.

## Surface, Sampling, Masks, And Themes

The `poiyomi-surface` shader family owns main color adjustment, alpha modes,
normal correction, backface shading, detail blending, projection modes, and
stochastic sampling. UV0-UV3, object/world projection, panosphere, polar,
Deliot-Heitz, and hex-tile paths retain explicit derivatives for mip-correct
sampling. Normal and mask imports retain their authored linear-data intent.

The `poiyomi-masks-themes` family exposes four RGBA global-mask textures,
sixteen selectable channels, channel remapping/inversion, view and surface
modifiers, four themes, and RGBA color-mask contributions to color, normal,
PBR, and emission.

AudioLink modulation is deliberately neutral when no phase-9 provider exists.
The importer preserves authored values and emits a provider diagnostic rather
than sampling an uninitialized resource.

## Lighting And Surface Response

The phase-6 shader families use the engine Forward+ light lists, shadow data,
ambient/IBL inputs, contact depth, and reflection probes:

- Poiyomi additive lights map to Forward+ direct-light enumeration.
- Unity ambient, lightmap, and probe intent maps to engine ambient, irradiance,
  and reflection-probe inputs; a per-material cubemap remains an override.
- Material AO and screen-space AO are supported. Unity/VRChat world-AO blocker
  volumes are classified as an external scene-lighting input and are not
  silently approximated.
- Directional, forced, minimum/capped, monochromatic, detail-shadow, SDF,
  multilayer, skin, cloth, realistic, and flat responses are evaluated in the
  main forward path.

Specular lobe two, anisotropy, clear coat, stylized reflection, environmental
rim, backlight, four matcap slots, a second rim, and contact-depth rim are
specialized under their phase-6 feature families.

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
