# Authored textured-alpha material contract

The additive `AuthoredLitTextureAlphaV1` family models the exact
`Common/LitTexturedAlphaForward.fs` surface. It has its own explicit constructor,
reader, generated shader schema and cook-only carrier. Existing
`StandardLitTextureV1`, `AuthoredLitV1`, desktop GLSL, and standalone texture
payloads retain their prior identities and formats.

## Authored meaning

- `Texture0` supplies diffuse RGBA at untransformed UV0
- `Texture1` is required and supplies opacity from its linear red channel at UV0
- Coverage is `Texture0.a * Texture1.r`; every admitted pass discards strictly
  below `AlphaCutoff`, including sorted alpha-blended color
- Color uses the engine forward PBR lighting, AO and directional/point/spot
  shadow equations with `MatSpecularIntensity`, `Roughness`, `Metallic` and
  `Emission`. `MatShininess` is preserved but remains unused
- Masked forward and sorted alpha blend are admitted. There is no extra tint,
  opacity multiplier, premultiplication, optional mask, normal map, specular map,
  displaced vertex function or automatic conversion of another authored family

Canonical admission checks the exact source path and normalized source text,
complete active snippet resolution, a closed pinned desktop/snippet graph, the
pinned generated Slang graph, and the complete reflected physical ABI. Arbitrary
GLSL and modified same-name engine shaders do not acquire this semantic by
matching parameter names.

## Publication and replay

`PublishedTexturedAlphaMaterial` serializes six float parameters, coverage mode,
render options, sort priority, exact retained stage identities and a unique image
table with two roles. It reuses `PublishedStandardLitTextureSettings`; the raw
texture codec and the prior opaque carrier are unchanged. YAML aliases are
joined only after matching identity, raw payload and all carried settings. The
projection borrows authored objects and creates an independently owned carrier.

The new `AuthoredTexturedAlphaMaterial` carries its image settings under the
dedicated `!xre-textured-alpha-v1` YAML tag. This tag is registered by the
rendering serializer, allowing a concrete `XRMaterial` property to preserve the
new subtype without changing the general asset reader. Metadata can precede the
image lists: restoration waits until both roles arrive. Missing metadata,
unresolved image identities, and conflicting settings on shared image instances
are rejected. The snapshot property is hidden from the property inspector;
ordinary sampler editing continues through the images. This is necessary because
the unchanged raw texture YAML payload does not encode every sampler/import
setting. No field is added to ordinary `XRMaterial` serialization.

The generic WebGPU route uses exact per-material local-shadow receivers and
source-owned depth-normal, directional, point and spot companions. Sorted draws
use the existing frozen order/rank infrastructure through an exact textured-alpha
ordering companion. Unsupported mode/profile combinations retain named failures.

Advanced browser publication adds a fifth, independent opacity role to its
browser-only engine-surface companion. The companion is schema 3, 368 bytes;
mono and x4 visibility both sample diffuse alpha times opacity red. Every
shading, surface-export, and depth-comparison recipe consuming this companion is
versioned. OpenGL/Vulkan record and binding layouts are unchanged. Cached older
browser recipes require a recook and are rejected before binding.

## Validation boundary

The ignored production witnesses passed:

- Editor, WebGPU, ShaderCooker and BrowserContentCooker builds: zero warnings/errors
- Eight new alpha artifacts and ten affected native artifacts cooked successfully
- Canonical source/ABI/provenance: 32 checks; 10,000 warmed proof calls allocate zero bytes
- Actual YAML authoring: 26 checks; an additional 14-check schema pass covers
  metadata-first ordering, actual prefab hierarchy cloning, missing/conflicting
  metadata, material/image identity changes, and unknown/incompatible tags
- Genuine `ExportAuthoredWorld` and cooked hydration: 80 checks across masked,
  sorted blend, and shared-image roles, including unchanged authored YAML and
  exact image payload/settings preservation
- A fresh process hydrates all three materials under verified Published metadata
- Native publication/runtime selection: 1,197 checks, including independent and
  aliased role lifetime, receiver/four auxiliary selection, mono/x4 ABI rejection,
  and zero warmed allocations
- All six frozen opaque V1/V2 carrier payloads remain byte-identical; 208 legacy
  codec/metadata checks pass
- The actual content packager succeeds; the unmodified engine-assets loader,
  using an offline file-fetch adapter, reads and hashes all 203 payloads and
  rejects five selector mutations. The actual packager also rejects those five
  unsupported selector profiles

Evidence lives under the existing ignored lit-surface run's `scratch/textured-alpha`,
`scratch/native-opacity`, `native-opacity-shaders`, and `logs` directories. A
reproducible shader/pipeline browser probe is retained for the separately
controlled CI lane; the local host could not launch Chromium because of its
Unix-socket restriction. No physical GPU validation is claimed here.

Physical browser pixels, deformation, MSAA and full Default/Advanced/custom
pipeline acceptance remain separate rendered gates. Other authored
normal/specular/Uber families and desktop native separate-mask promotion are not
covered by this family.
