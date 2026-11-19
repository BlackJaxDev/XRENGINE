# Poiyomi Surface And Layer Rendering Contract

This document records the native XRENGINE behavior implemented for surface and layer features
of the pinned Poiyomi Toon 9.3 conversion.

## Surface, Sampling, Masks, And Themes

The `surface-extensions` shader family owns main color adjustment, alpha modes,
normal correction, backface shading, detail blending, projection modes, and
stochastic sampling. UV0-UV3, object/world projection, panosphere, polar,
Deliot-Heitz, and hex-tile paths retain explicit derivatives for mip-correct
sampling. Normal and mask imports retain their authored linear-data intent.

Unmasked Fresnel alpha uses the source view-normal threshold curve. It uses the
absolute view-normal dot product, blends the curve by intensity, then applies
inversion before it multiplies alpha. The source global-mask selection for this
alpha effect is not bound by this shader path.

Source toon imports set `_SourceAlphaClip`. This makes the authored cutoff apply to
Fade and Transparent materials in the forward, depth, shadow, and selection
coverage paths. Engine-authored transparent materials keep their existing
cutoff behavior. The common coverage test runs before late alpha changes from
backface and depth-intersection effects; those source combinations need a
separate coverage match.

The `global-masks-themes` family exposes four RGBA global-mask textures,
sixteen selectable channels, channel remapping/inversion, view and surface
modifiers, four themes, and RGBA color-mask contributions to color, normal,
PBR, and emission.

AudioLink modulation is neutral when no AudioLink provider exists.
The importer preserves authored values and emits a provider diagnostic rather
than sampling an uninitialized resource.

## Lighting And Surface Response

The lighting shader families use the engine Forward+ light lists, shadow data,
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

Source overlays change the base color in this order: anisotropy, matcaps,
cubemap, first rim, and second rim. Forward and native shading use the same
order. These stages must not move across each other: their base-color mix
and replacement terms can change later stages. Additive anisotropy light
remains a separate contribution after the source BRDF.

Specular lobe two, anisotropy, clear coat, stylized reflection, environmental
rim, backlight, four matcap slots, a second rim, and contact-depth rim have
specialized feature paths. Supported source style 0 second rim changes the base
color before lighting and adds its emission once. Other active source second
rim styles require a reported conversion failure.

The source UnityChan stylized specular path uses two high-color lobes. The
importer keeps each lobe size and strength, normal-map blend, tint, color map,
mask channel, mask level, and shadow controls. The forward and native paths
use the source lobe curve and add the result to the lit color. A source
material with another reflection mode, high-color blend mode, themed tint,
non-UV0 high-color map, or map pan fails conversion. Engine-authored specular
materials continue to use the engine specular path.

Source cubemap masks blend the selected global-mask channel with the local mask
before inversion and light masking. Native and forward shading use the same
mask order. The importer preserves the selected channel and blend mode. The
current local cubemap mask contract requires UV0 and zero pan; unsupported
active values produce a conversion failure. Engine-authored cubemap behavior
does not use this source-only mask composition.

## Repeated Layers And Flipbooks

Decals and emissions use shared four-slot contracts and deterministic
0-to-3 order. Disabled families are removed from generated shader variants,
including their sampler bindings.

Source decal alpha uses each slot's authored override operation: off, replace,
multiply, add, subtract, minimum, or maximum. The source selector can weight
the operation by its decal mask, require strict decal bounds, or use both.
Tiled source decals skip the bounds check. A disabled source slot cannot change
alpha. Engine-authored decals keep their existing alpha blend weight. The
forward Uber and native Uber coverage paths use the same source operation.
The source alpha parameters store the operation, selector, tiled flag, and one
plus the global-mask blend mode. A zero fourth value selects the engine-authored
decal contract. The native material layout is `NativeUberOpaqueV2` or
`NativeUberMaskedV2`. Material conversion version 1.3.1 marks older converted
source materials for reimport. Source decal alpha intensity affects a separate
per-layer global-mask output after the alpha override in Unity. That output is
not implemented here; alpha intensity does not change the source alpha override.

Texture arrays remain native `sampler2DArray` / `XRTexture2DArray` resources.
Unity array metadata preserves layer order and sampling settings. When the
source is a single image with explicit `flipbookRows` and `flipbookColumns`,
the importer performs an explicit row-major grid conversion. It never silently
interprets an arbitrary 2D texture as a sprite sheet. Animated image files use
their source frame order.

Video, TPS, and AudioLink consumers remain optional adapter inputs. Until those
providers are implemented, conversion reports the missing adapter and leaves
the authored non-adapter contribution intact.
