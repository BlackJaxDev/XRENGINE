# Unity Avatar Package Import Investigation

Status: Both native FBX and Assimp can render a composed Unity avatar package in
Vulkan with the Advanced pipeline. Some source assets can remain unavailable.
The latest paired Release run is below 90 Hz. Recent CPU fixes have build
validation only. Exact source inventory and run details are in local accessory
notes.

Guide: [Unity Conversion](../../../developer-guides/assets/unity-conversion-integrations.md)
Validation: [Unity Avatar Package Validation](../../testing/assets/unity-avatar-package-validation.md)

## Package Composition and Source Limits

Unity scene and prefab files can refer to assets from companion packages. The
JSONC resolver accepts explicit VPM entries and direct Unity package archives.
It composes their serialized assets before import. Unity editor scripts and
platform-specific avatar behavior do not execute in the engine. A matching
package version does not change shader text embedded in a source material.

Strict import rejects missing visual references. Partial import records the
missing references and can publish supported siblings. Some scene material
overrides and external FBX images were absent from all available source
archives. A file with a similar name is not proof of equivalent image content.
The import keeps visible missing-asset markers for color textures and uses a
neutral placeholder for missing normal maps. Shader conversion cannot recreate
an absent material or image.

The source also contained nested effect models with unsupported units. The
coordinate check runs before either FBX geometry producer. Partial import
records the rejection and omits that hierarchy. Strict import retains the
error. The check must not be bypassed without correct coordinate conversion.

## Shader Conversion and Initial Rendering

Locked source shaders require an exact supported version marker, expected
property signature, and original shader identity. Version markers take
precedence over a GUID-only match. Unsupported shader versions or enabled
features retain explicit fallback diagnostics. The conversion retains source
values and reports unsupported controls; it does not disable authored features
to force native Uber admission.

The first paired captures exposed a vertically inverted scene. World-space
marker cubes showed that this was a display problem, not an avatar transform.
RenderDoc located the inversion in the transparent resolve pass, which sampled
scene textures without the framebuffer Y-direction policy. The resolve now
uses the shared framebuffer UV helper for mono and stereo output.

The native FBX producer also misread a texture connected to
`TransparencyFactor` as a diffuse texture. It selected weighted transparency
for materials whose source used alpha coverage. The importer now recognizes
that connection and gives an explicit mask priority over the blend hint.
It also applies `EmissiveFactor` to the imported emission strength. These
changes restored visible coverage and removed false white emission in both
routes. Source images that are absent still limit visual parity.

Assimp could not open a dependency FBX at a long Windows cache path. Direct
parser checks isolated its file-open path. `AssimpModelFileImporter` now uses
managed IO for Windows paths of at least 260 characters and retains the source
directory for sidecar lookup. Short paths and non-Windows imports keep their
existing path. Both routes then reached the same composed scene.

## Geometry and Material Input Differences

Some source meshes provide normals and UVs but no tangents. Assimp generated
tangents while native FBX did not. Native now generates missing tangents from
final positions and UV0, with skin records and tangent handedness retained.
It also preserves the shared roughness default when FBX omits roughness.
These fixes reduced a visible shading difference. They do not establish a
pixel-exact reference match.

A live-edit report first identified the wrong visible shoe material. Draw
identity and image hashes ruled out the reported disappearance. A control on
the actual visible material changed its pixels and restored them. This check
is a reminder to verify source slot, renderer slot, and final draw identity
before assigning a visual defect to a material.

Rapid texture edits then exposed a real stale-output defect. Replacing a 2D
image in the same binding slot changed the binding-layout revision. That
changed scene topology and forced large morph input preparation while geometry
was unchanged. `SurfaceTextureBindings` and the legacy material texture list
now compare ordered binding metadata. Same-slot replacements advance value
and resource revisions. Binding count, target, sampler identity, null state,
or incompatible texture type still change layout. Paired live edits restored
the baseline images in both routes.

Static deformation preparation also used scene topology as its source
generation. Material and visibility changes could therefore rebuild unchanged
mesh input. `AdvancedPreparationExtractor` now compares the exact bounded mesh
inputs, including geometry, skin, morph data, revisions, and preparation
policy. A validated retained row can survive a membership change. Pinned GPU
ownership and copy-on-write rules remain active. Camera and membership checks
no longer reproduced the repeated full morph rebuild after this change.

## Authored Shading and Coverage

Native Uber now supports the relevant multilayer lighting and vertex-mode
UDIM discard paths. Vertex-mode discard uses a deterministic triangle decision
from original UV corners. Authored paths without the required bindings reject
the feature explicitly. The border map uses source remapping and filtering
rules. Flat-mask lighting carries a source-space direction per draw; a shared
material does not store a mesh-specific direction. Both routes passed
controlled color, mask, direction, and restore checks. Dynamic imported chains
can move between captures, so exact image restoration requires a static
transform control.

The outline companion initially lost its generated fragment during material
initialization. It now retains that fragment. Equal distance-fade limits
disable the fade according to the source rule. An emission-layer review found
that conversion replaced emission texture RGB with base color and omitted
texture alpha. The corrected native and generated shading paths retain the
source multiplication. No authored emission was removed for the visual check.

Some clothing images existed in the composed source but were bound as
placeholders. Exact GUID and metadata resolution restored those bindings.
Both routes later matched the recovered image identities and dimensions.
Other source image references remain absent. The local accessory notes retain
the exact assets and hashes.

## Shadow Submission and Preparation

The initial local-shadow fixture rendered an opaque caster twice: once in the
shared indirect lane and once through its authored shadow variant. The shared
lane now accepts only proven opaque casters. Coverage and deformation retain
their authored variants. Shared opaque shadows use the light's depth encoding
and state. Main-view material eligibility is unchanged.

First point-light activation exposed a registration race. `LightComponent`
could publish the light before its shadow camera rig existed. It now prepares
the resources before insertion into `DynamicPointLights`. The world-transfer
guard remains active.

Shadow command preparation could report success after a recoverable backend
deferral. Offscreen viewport factories now propagate command exceptions so
the atlas retries an incomplete tile. Point shadow planning also selects
sequential GPU face passes when shared indirect submission cannot expand one
instance into six faces. This retains the GPU path and reports the capability
reason.

Shared shadow ownership now matches supported generated passes exactly.
A wildcard pre-render request could otherwise wait for a layout that cannot
exist. Separately, `u_SourceUdimTriangleCount` had material frequency even
though each renderer supplies a different count. Its Object-frequency
declaration prevents one draw from overwriting another draw's count.

Both routes later passed first point-light activation, coverage change and
restore, opaque caster removal and restore, and resident sequential point
faces. The restored point and spot atlas images matched their baselines.
Standard and synchronization validation reported zero errors. These checks
establish the tested fixture behavior, not universal shadow parity.

## Release Performance and CPU Investigation

The latest accepted paired 60-second Release measurements used Vulkan, the
Advanced pipeline, GPU indirect submission, a visible 1920x1080 viewport,
uncapped frame pacing, warm caches, and settled textures. Local shadows and
continuous probe capture were off. Native FBX averaged 47.78 Hz; Assimp
averaged 52.06 Hz by accepted `QueuePresent` cadence. Neither meets 90 Hz.
These are application-present measurements, not headset delivery rates.

The Assimp sample reported 10.26 ms of active render dispatch, 8.88 ms of
render wait, and about 0.90 ms of collection work. Collection and render
timings overlap; adding them would misstate total frame cost. No fixed pacing
delay was found. GPU timing history was unavailable, so these values do not
prove a single remaining cause.

A visible-output CPU sample found repeated material preflight in
`TryPreflightTransition` and `TryGetUberMaterialState`. The strict shader
manifest check visited files and search directories even when publication
later reused a retained material. A retained-source proof now requires the
same committed material, variant handle, source revisions, shader and provider
identity, and snippet version. New or changed sources still receive strict
validation. The proof refreshes after successful preflight even when unchanged
publication skips commit. Prepared and unchanged-row checks verify it too.

The sampled frame also found auto-uniform schema mismatches for
`SourceSHEnabled`. The shader declared source SH values as material constants,
while the engine captured them as runtime lighting callbacks. This rejected
the cached write plan and caused full uniform-block writes. All eight source
SH uniforms now map to the Lights requirement. Texture signature reads also
use one current descriptor snapshot to avoid repeated publication locks; a
failed snapshot keeps the existing getter path.

The latest Release build passed with zero warnings and errors. Live validation
stopped before the new visual, live-edit, and paired performance checks. Thus
47.78 Hz and 52.06 Hz remain the latest valid measurements. The next run must
check both routes with the new binary, compare CPU recording and wait stages,
and measure settled Release present cadence again. No performance gain can be
inferred from the build alone.
