# Standard opaque lit-texture surfaces

`EngineMaterialSemanticIdentity.StandardLitTextureV1` identifies the existing
opaque deferred PBR texture family. It is an authored semantic, not recognition
of arbitrary shader filenames. Desktop factories retain the same deferred GLSL
programs. Cooked construction creates source-free material data that resolves
an explicitly declared WebGPU program.

`XRMaterial.CreateLitTextureMaterial(texture, deferred: true)` now publishes this
semantic. `CreateLitPbrTextureMaterial` exposes the same family with optional
RGB normal, metallic and roughness maps. The model importer tags only the
compatible opaque PBR selections, after the strict shared reader validates the
complete authored material. Existing forward, transparent, specular-map,
opacity-map, height-map, emissive-map and custom shader families are not
reinterpreted.

## Shared surface contract

`StandardLitTextureSurfaceBinding` requires:

- `OpaqueDeferred`, opaque transparency and uniform `Opacity` exactly one
- Six unique parameters: `BaseColor: ShaderVector3` and `Opacity`, `Specular`,
  `Roughness`, `Metallic`, `Emission` as `ShaderFloat`
- For normal maps, exactly two additional parameters: `NormalMapMode` as
  `ShaderInt` zero and `HeightMapScale` as `ShaderFloat` zero
- A base-color 2D texture and at most one 2D texture for each optional role,
  with the exact legacy sampler-slot ordering of the selected desktop family
- UV0, identity scale/offset, zero rotation, red scalar channels, and metadata
  wrapping equal to the texture's authored U/V wrapping
- Linear normal/metallic/roughness texture formats and metadata; base-color
  hardware decoding follows the authored texture format exactly
- No custom callbacks, binding publishers, billboard, pass, Uber, transmission,
  modern emissive or normal-strength extensions, including vertex-uniform callbacks

The known engine-owned surface-emission callback is identified explicitly; its
presence does not authorize unrelated callbacks. Bounded per-read reference and
type checks detect direct edits to exposed arrays without managed allocation.

Texture alpha is ignored, matching these opaque desktop surfaces. Lower uniform
opacity would invoke desktop dithering and is rejected here. Base color is
`sample.rgb * BaseColor`; red metallic and roughness maps multiply their uniform
factors. Emission is `BaseColor * Emission`, independent of sampled albedo.
The normal/roughness desktop shader's absent metallic slot requires a zero
metallic factor. RGB normal mapping preserves the engine's flipped Y, outward-Z
clamp and authored tangent basis. The browser normal-map profile requires
float4 tangents; the desktop derivative-basis fallback for missing tangents is
not silently substituted.

## Browser cooking and rendering

Ordinary engine-world cooking uses an editor-owned material projection through
the existing binary serializer callback. It admits only an explicitly tagged
`XRMaterial` with the validated schema and exactly one GLSL `main` fragment
stage. The stage's full canonical engine path and complete source must match the
known deferred shader selected by the texture roles. Active registered snippets
must retain their complete canonical source bytes. The actual resolved stage
must then equal an independent canonical expansion through the same recursive
resolver, using a closed dictionary with no registered/file fallback or shared
cache. This preserves normal resolver annotations and duplicate-directive
handling while refusing snippet overrides or undeclared dependencies. Overriding
a snippet before material construction cannot grant stock shader admission.
Additional stages, source edits, compiler defines/includes or
generated Uber state reject. The detached
source-free copy preserves the material ID, parameters, images and render
options. YAML may hydrate a texture's legacy and semantic occurrences as
separate objects. Only this cook projection may reconcile those aliases: exact
`XRTexture2D` types, matching nonempty persistent IDs, identical complete typed
payload bytes, and identical additional sampler/import/storage settings are
required. The first legacy occurrence supplies the detached copy's canonical
reference; conflicting same-ID images fail explicitly. The shared reader uses
all its normal schema checks during comparison, and the resulting copy must
pass strict reference-identity validation. Live material validation is unchanged.
The shader-parameter YAML reader honors the writer's explicit concrete `__type`
before legacy value inference, so an authored `ShaderFloat` whose value is `1`
remains a float. Only known shader-parameter types are accepted; untagged legacy
values retain their existing inference behavior.

A per-cook cache reuses the detached material copy. Authored assets are never
rewritten, and cleanup detaches borrowed parameter subscriptions before
destroying only the temporary material. Source destruction retires owned normal
and shadow companions and their subscriptions without destroying borrowed maps.

The binary format remains `BinaryV1`. For this explicit projection, nested
MemoryPack envelopes are suppressed so embedded materials remain visible to the
serializer callback. The source-free `PublishedStandardLitTextureMaterial`
carrier uses the existing custom binary-object contract: one bounded table of
up to four unique images and checked role indices. Each image also retains
anisotropy, sampler comparison mode/function, import color space/usage/normal-Y
metadata and storage usage, which the general texture payload does not carry.
Numeric bounds and enums are validated before use. Grab passes, external-memory
images, sparse residency, GPU-written images and active PBO/progressive uploads
cannot be represented by this static-image contract and fail explicitly.
Hydration reconstructs both
the legacy sampler slots and semantic bindings from the same image objects;
the generic serializer cannot accidentally turn those aliases into separate
resources. Decode validates counts, indices, identities and the full numeric
surface before publication, with scoped cleanup of newly allocated images on
failure. Registered game serializers retain their own formats and authority;
their hydrated output must still satisfy the normal browser audit.

Seven Slang recipes cover:

- Opaque textured color with and without tangent-space normal mapping
- Each color profile with no shadows, standalone directional shadows, or the
  bounded directional/point/spot shadow receiver family
- Mapped world-normal replay into the shared depth-normal producer

Vertex profiles are `position-normal-uv-v1` and
`position-normal-tangent-uv-v1`. Color uses the existing linear HDR lighting,
ambient occlusion, shadow, bloom and presentation path. GPU deformation supplies
the same canonical position/normal/tangent streams; UVs remain the authored mesh
stream. Unmapped surfaces use the ordinary geometric-normal prepass. Opaque
textured casters use the existing opaque depth, radial point and projected spot
programs because texture alpha does not affect their coverage.

Missing programs, incompatible vertices, unsupported state, or a changed
normal-map layout reject explicitly. Optional absent scalar maps are disabled
by material controls while retaining an already owned texture binding; no
authored map is replaced or silently ignored.

The leaf shader-cooker build and all seven recipes have been compiled locally.
A saved ordinary authored world has also completed Editor projection, package
cooking and hydration: the source-free material passes the strict surface reader,
and one image reused by base-color, metallic and roughness roles retains exact
identity across all semantic and legacy occurrences. The saved authored world
remained byte-for-byte unchanged.

Those checks do not establish physical-device acceptance. Authored texture
upload, decoded appearance, normal orientation, deformation, shadows and moving
resource lifetimes still require the integrated browser qualification run.
