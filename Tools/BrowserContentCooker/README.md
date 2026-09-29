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

## Service requirements

Schema 1 recipes remain supported unchanged. Schema 2 adds exactly one required root property:

```json
"services": { "required": ["dom-ui"], "optional": ["web-audio"] }
```

Schema 2 output retains `profile: "browser-forward-v1"`, uses `toolchain: "xrengine-browser-content-2"`, and preserves the validated `services` object. Each array permits at most 16 distinct strings matching `^[a-z][a-z0-9-]{0,63}$`; the required and optional sets must be disjoint. Empty arrays are permitted. The cooker checks syntax and bounds without a hardcoded service whitelist. Runtime admission decides whether the selected host actually provides each requested service, fails unsupported required services, and reports why optional services are unavailable.

The current imported static-scene profile supports `dom-ui` and `web-audio`. Audio availability does not bypass browser activation policy: playback remains gated on a user gesture. `cpu-animation` and `character-collision` are implemented for the built-in reference scene only, so imported packages cannot require them. Native-only services such as `native-xr`, `native-physics`, and `native-editor` are excluded from this browser profile and receive explicit unavailability reasons. Declaring a service does not introduce an asset kind or serialize native engine components; this tool still accepts only mesh, texture, material and scene assets.

`Example/recipe-services.json` is a schema 2 source example requiring DOM UI and optionally requesting Web Audio; it reuses the existing mesh, material and scene files. Neither example claims that a cook has been executed.

Non-texture assets have one feature-independent JSON variant:

- Mesh: `{ "vertices": [x,y,z,u,v,...], "indices": [0,1,2,...] }`. Triangle indices must be in range; values must be finite. At most 65,535 vertices and 196,605 indices, also subject to the JSON byte limit.
- Material: `{ "tint": [r,g,b,a], "texture": "texture-id-or-null", "alphaMode": "opaque", "shading": "lambert", "cullMode": "back", "alphaCutoff": 0.5, "castShadow": true, "receiveShadow": true }`. Use JSON `null` for no texture. Colors/cutoff are normalized. Alpha modes are `opaque`, `masked`, `transparent`; shading is `unlit` or `lambert`; culling is `none`, `front`, `back`. Transparent materials must disable shadow casting. The texture slot is a color texture and rejects normal-map semantics.
- Scene: `{ "cameraView": [16 floats], "cameraProjection": [16 floats], "instances": [{ "mesh": "mesh-id", "material": "material-id", "modelMatrix": [16 floats] }] }`. Matrices use the existing managed `Matrix4x4` field order, with -Z forward and +Y up. Scenes contain at most 64 instances and share identical camera matrices throughout the package. The cooker checks finite matrices, not camera suitability or visual appearance. The engine additionally requires model matrices that its transform implementation can decompose into translation, rotation and scale; arbitrary shears are unsupported.

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
