# Poiyomi Toon 9.3 Conversion - Phase 2 Baseline Complete

- Date: 2026-07-23
- Status: Implementation complete; visual UV reference capture pending
- Scope: Correctness of the existing Poiyomi-to-uber conversion baseline
- Target: Poiyomi Toon 9.3.64

## Outcome

Phase 2 replaces the converter's ambiguous texture aliases with independent
runtime contracts:

- `_ToonRamp`, first shade, and second shade textures have separate samplers,
  transforms, panning, and UV selectors.
- Metallic and smoothness inputs have separate samplers, packed-channel
  selection, inversion, and scalar controls.
- Rim color and rim mask textures are sampled independently.
- Dissolve base noise, detail noise, mask, edge gradient, and edge texture are
  sampled independently. Base, detail, mask, and edge textures also honor
  their own UV selectors.

The uber vertex contract now carries UV0 through UV3 in the standard, OpenVR,
OpenXR, generated deformation, and GPU-generated submission paths. A missing
requested channel uses UV0; a mesh with no UV0 uses `(0,0)`. Scene import emits
`POI0013` when a material requests a channel the attached mesh does not have.

## Runtime Metadata And Defaults

Unity TextureImporter metadata now reaches `XRTexture`/`XRTexture2D`:

- sRGB versus linear sampling intent;
- color, data, or normal semantic usage;
- normal-map green-channel inversion;
- alpha-as-transparency;
- U/V wrapping and min/mag filtering;
- mip generation, mip bias, and anisotropy.

OpenGL and Vulkan sampler creation consume the imported anisotropy value.
Generated sampler fallbacks are semantic: normal maps use flat-normal data,
metallic and dissolve-detail data use black, color/mask inputs use white, and
the toon ramp uses a two-texel black-to-white identity ramp.

## Activation And Diagnostics

Feature activation now respects authoritative section toggles first, then
recognized keywords, textures, and non-default authored evidence. An
explicitly disabled section no longer becomes active merely because Unity
left a texture assigned.

Serialized enums pass through named mappings. Unsupported values emit
`POI0014` and use a deterministic fallback. Poiyomi recognition continues to
use the Phase 0 pinned GUID/version/signature matcher rather than shader paths.

Outline authoring is preserved but the outline feature remains disabled until
the inverse-hull pass exists. LTCGI, AudioLink, and mirror integrations are
reported as unavailable; the conversion report does not claim those paths
execute.

## Mesh And Lightmap Ownership Audit

UV channels remain individual mesh attributes and fragment varyings. The
lightmapper reads its selected `LightmapUvChannel` from the mesh and does not
overwrite or reserve a Poiyomi UV varying. Fixed and generated vertex outputs
use locations 4-7 for `FragUV0`-`FragUV3`; color remains at location 12 and
local position/transform/view data remain at locations 20-22.

## Validation

- `dotnet build .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore -m:1 -nr:false`
  - Passed with 0 warnings and 0 errors.
- Focused Phase 0-2 and legacy importer suite
  - Passed: 21/21.
- Enabled Phase 2 uber feature combination
  - Compiled successfully to Vulkan SPIR-V.
- Four-UV mesh regression
  - Preserves four distinct mesh channels and verifies fixed/generated shader
    routing plus the UV0 fallback contract.

The remaining unchecked acceptance item is a rendered UV0-UV3 reference image.
The data and shader contracts are covered, but no editor viewport capture was
produced in this phase, so the tracker intentionally does not claim that
visual evidence.
