# Browser content cooker

This dependency-free .NET 10 command packages already converted assets for the browser forward renderer. It emits a versioned `manifest.json` and immutable SHA-256 addressed `payloads/<hash>.bin` files. It does not invoke native importers, texture encoders, shader tools, Python, or external processes. Import source models and encode texture variants using the existing offline asset pipeline before packaging.

```sh
dotnet run --project Tools/BrowserContentCooker -- Tools/BrowserContentCooker/Example/recipe.json <output-directory>
```

The example is source content, not an executed or qualified cook. It contains a triangle mesh, material, initial scene and separately streamed scene chunk sharing resources. Copy the resulting output directory to a same-origin static content location and select its `manifest.json` in the browser host. Keep old payloads available while clients can still reference an older manifest. Publish payloads before the manifest on remote hosts, just as the local tool does.

The tool fully checks the recipe and all source payloads before publishing files. Payload filenames derive from their bytes. Existing identical payloads are reused; a mismatched existing hashed file is an error. Manifest replacement uses a temporary file and rename in the same directory. A failed publication can leave unreferenced immutable payloads, but does not intentionally remove the previous manifest. Input and output trees must be locally controlled during the cook; symbolic links/reparse points and source paths escaping the recipe directory are rejected. This is a packaging tool, not a sandbox for concurrently hostile filesystem writers.

## Recipe and payload contract

All object properties shown below are required. Unknown and duplicate properties fail. Asset IDs match `^[a-z][a-z0-9._-]{0,63}$` and cannot contain `..`. Paths are relative to the recipe directory. Dependencies must exist, be acyclic and match the references inside material/scene payloads exactly; meshes and textures have no dependencies. Entrypoint and streamed IDs must identify scene assets, and at least one entrypoint is required.

```json
{
  "schema": 1,
  "entrypoints": ["world"],
  "streamed": ["annex"],
  "assets": [
    {
      "id": "world",
      "kind": "scene",
      "dependencies": ["triangle", "paint"],
      "variants": [{ "source": "world.json", "encoding": "json", "requiredFeatures": [] }]
    }
  ]
}
```

That abbreviated recipe requires the referenced assets to be added; the `Example` folder contains a complete recipe. The output adds `profile: "browser-forward-v1"` and `toolchain: "xrengine-browser-content-1"`. Each output variant replaces `source` with `hash`, `url` and `bytes`; other variant metadata is preserved.

The fixed profile identifies the matching browser raster shader/material ABI. Content and host must use the same profile; incompatible ABI changes require a profile version bump rather than silently reinterpreting existing payloads.

## Engine asset catalogs

Recipes with `schema: 1` and `format: "xrengine-assets"` package ordinary cooked
engine assets with `startupWorld`, optional `startupSettings`, and `assets`.
The optional `shaderArtifacts` array names exact hash-owned shader descriptor
and source paths. Material variants use explicit semantic keys; pipeline-owned
shaders use the separate optional `pipelineArtifacts` array:

```json
"pipelineArtifacts": [
  { "pass": "tonemap", "descriptorIdentity": "<lowercase SHA-256 descriptor hash>" },
  { "scope": "advanced", "pass": "tonemap", "descriptorIdentity": "<another lowercase SHA-256 descriptor hash>" }
]
```

Each placeholder must be replaced by a 64-character descriptor hash in
`shaderArtifacts`. Each entry has a bounded lowercase `pass`, an optional
bounded lowercase `scope`, and exactly one descriptor identity. A missing scope
keeps the legacy binding key equal to the pass; a scoped entry uses
`scope::pass`, while its descriptor still declares only the pass. These fields
permit any authored pass identity following the lowercase identifier grammar;
colon is excluded to prevent ambiguous bindings. Duplicate binding keys and
arrays exceeding 256 entries are rejected. The referenced hash-owned descriptor
must declare the matching pass, `target: "WebGPUWgsl"`, and complete vertex and
fragment entry points without a material variant. Malformed entries, missing
references, and mismatched descriptor metadata fail before output files are
written. Omitted or empty catalogs do not
select a shader implicitly by its name or source path. These engine catalogs
are independent of the frozen forward-renderer recipe format below.

## Service requirements

Schema 1 recipes remain supported unchanged. Schema 2 adds exactly one required root property:

```json
"services": { "required": ["dom-ui"], "optional": ["web-audio"] }
```

Schema 2 output retains `profile: "browser-forward-v1"`, uses `toolchain: "xrengine-browser-content-2"`, and preserves the validated `services` object. Each array permits at most 16 distinct strings matching `^[a-z][a-z0-9-]{0,63}$`; the required and optional sets must be disjoint. Empty arrays are permitted. The cooker checks syntax and bounds without a hardcoded service whitelist. Runtime admission decides whether the selected host actually provides each requested service, fails unsupported required services, and reports why optional services are unavailable.

The imported profile supports `dom-ui` and `web-audio`; audio playback remains gated on a user gesture. Schema 3 can require `cpu-animation` or `character-collision` when an essential scene references the corresponding asset. Native-only services such as `native-xr`, `native-physics`, and `native-editor` receive explicit unavailability reasons. A service declaration does not serialize native engine components.

`Example/recipe-services.json` is a schema 2 source example requiring DOM UI and optionally requesting Web Audio; it reuses the existing mesh, material and scene files. Neither example claims that a cook has been executed.

Schema 3 retains the same profile and required `services` object and emits `toolchain: "xrengine-browser-content-3"`. It adds animation and collision JSON kinds, each with one feature-independent variant. Animation declares exactly its mesh dependency; collision has none. Scenes may add a `collision` asset ID at the top level and an `animation` asset ID and/or `occluder` Boolean on individual instances. Scene dependencies must exactly cover all referenced mesh, material, animation and collision IDs. Animated instances must use the animation's source mesh. Scene chunks specifying collision must agree on one world. An occluder must be static and use an opaque material. Required animation/collision services need corresponding references in an essential entrypoint scene; an unrelated asset or streamed-only reference does not qualify.

`Example/recipe-animation-collision.json` is source content with `idle-animation.json`, `ground-collision.json`, and `world-animated.json`. Its animated instance and separate static occluder illustrate the authored fields. It is not a generated cook or validation result.

Non-texture assets have one feature-independent JSON variant:

- Mesh: `{ "vertices": [x,y,z,u,v,...], "indices": [0,1,2,...] }`. Triangle indices must be in range; values must be finite. At most 65,535 vertices and 196,605 indices, also subject to the JSON byte limit.
- Material: `{ "tint": [r,g,b,a], "texture": "texture-id-or-null", "alphaMode": "opaque", "shading": "lambert", "cullMode": "back", "alphaCutoff": 0.5, "castShadow": true, "receiveShadow": true }`. Use JSON `null` for no texture. Colors/cutoff are normalized. Alpha modes are `opaque`, `masked`, `transparent`; shading is `unlit` or `lambert`; culling is `none`, `front`, `back`. Transparent materials must disable shadow casting. The texture slot is a color texture and rejects normal-map semantics.
- Scene: `{ "cameraView": [16 floats], "cameraProjection": [16 floats], "instances": [{ "mesh": "mesh-id", "material": "material-id", "modelMatrix": [16 floats] }] }`. Matrices use the existing managed `Matrix4x4` field order, with -Z forward and +Y up. Scenes contain at most 64 instances and share identical camera matrices throughout the package. The cooker checks finite matrices, not camera suitability or visual appearance. The engine additionally requires model matrices that its transform implementation can decompose into translation, rotation and scale; arbitrary shears are unsupported.

Schema 3 animation payloads contain `schemaVersion: 1`, `mesh`, parent-before-child `parents` (1–128), 10-float local TRS `bindPose` per bone, row-major `inverseBindMatrices`, Core4 `coreIndexFormat` (1 or 2), packed `coreIndices` and `coreWeights`, spill, normal and tangent arrays, sparse morph arrays (`shapeRanges`, `sparseRecords`, `quantizedDeltas`, `quantizationMetadata`), `defaultClip`, and up to eight named `clips`. Each clip supplies `framesPerSecond`, `frameCount`, `loop`, packed absolute-local-TRS `frames`, and `morphWeights`. Optional `influenceCap`, `maximumMorphAccumulation` and `morphWeightThreshold` retain runtime defaults. The cooker checks mesh vertex count, array strides, finite values, transform bounds, referenced bones/deltas, sparse ranges, clip cadence and the 1 MiB JSON limit. The runtime verifies bind/inverse-bind consistency and owns final skinning admission.

Schema 3 collision payloads contain 1–64 positive finite AABBs as `boxes: [{ "minimum": [x,y,z], "maximum": [x,y,z] }]`, plus `spawn`, `yaw` and `pitch`. Coordinates are within ±10,000; spawn must not overlap a box expanded by the character's `(0.25, 0.85, 0.25)` half extents. Yaw is within ±π and pitch within ±1.4 radians. A package has at most one collision world; every scene chunk repeats its reference or all chunks omit collision. The runtime owns final collision admission and movement.

## Texture delivery variants

Texture variants use `encoding: "raw"` and require `width`, `height`, `format`, `mipByteLengths`, `normalConvention`, and `alphaMode`. Payload bytes concatenate complete mip levels largest-first without row padding. Include the full chain through 1×1; each next dimension is `max(1, floor(previous / 2))`.

| Format | Required feature | Mip storage |
| --- | --- | --- |
| `rgba8unorm`, `rgba8unorm-srgb` | none (`[]`) | width × height × 4 |
| `astc-4x4-unorm`, `astc-4x4-unorm-srgb` | `texture-compression-astc` | ceil(width / 4) × ceil(height / 4) × 16 |
| `etc2-rgba8unorm`, `etc2-rgba8unorm-srgb` | `texture-compression-etc2` | ceil(width / 4) × ceil(height / 4) × 16 |

Compressed base dimensions must be multiples of four. Smaller mips still occupy complete blocks. Every texture must include a dimension-, color-space-, mip-count- and semantic-matched RGBA8 fallback. Variant formats must be unique. The browser selects a supported format explicitly; no decoder is downloaded or run at runtime.

`normalConvention` is `none` or `tangent-y-positive`. Normal textures use linear formats; the current material color slot rejects them. `alphaMode` is `straight`; premultiplication is not silently converted. Convert image orientation, normal channels, alpha convention and color space offline. Content hashing checks byte identity, not the correctness of the encoder's declared semantic metadata.

The delivery envelope is separate from the engine's XRTS v2 native warm cache. It does not replace that cache or provide KTX2/Basis transcoding. ASTC/ETC2 payloads must come from offline conversion and are consumed only on capable devices, with RGBA8 as the explicit alternative.

## Limits and acceptance

Recipes/manifests and individual JSON assets are limited to 1 MiB; raw texture payloads to 4 MiB. A mesh's decoded vertex/index arrays also cannot exceed 1 MiB. Recipes allow 4,096 assets, 256 materials, 2,048 scene instances in total, 64 dependencies per asset, 32 dependency levels and up to eight variants per texture. The conservative sum of each asset's largest variant must not exceed 64 MiB. Texture dimensions are 1–8,192 and must additionally fit runtime/device/quality limits. Streaming divides scene construction and request scheduling; it does not bypass the manifest's total selected-content budget.

Source implementation is complete. Build execution, example cooking, serving, compressed texture rendering, cache behavior and end-to-end browser acceptance remain deferred. No generated payloads are checked in as evidence of a successful cook.
