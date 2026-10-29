# Cooked Asset Device Requirements

The generic Editor cooker now records texture-array layer limits from values
visited by the actual cooked serializer. This includes arrays embedded in a
material or another generic asset graph. An array above the WebGPU core
minimum of 256 layers adds `maxTextureArrayLayers` to that asset's optional
`deviceRequirements` field. Baseline assets omit the field.

The engine asset packager validates and preserves the field. The browser
catalog reads it and takes the maximum over the dependency closure of the
startup world, startup settings, essential roots and streamed roots. Device
creation checks that value against the adapter and passes it to
`requestDevice`. Streamed content does not discover a missing device limit
only after the device has been created. Existing bundles without the field
keep their current device requirements.

The current contract accepts an empty feature list and the one implemented
array-layer limit. It does not claim compressed texture variants, other
resource profiles or a generic device-capability service. A registered custom
asset codec writes an opaque payload and bypasses the generic value observer.
Such codecs need an explicit requirements contract before their nested
resources can contribute limits. UR03.03 remains open for that work and the
remaining platform cooking requirements.

## Focused Evidence

A disposable native probe uses the real generic serializer and the new
collector. It cooks materials with embedded arrays of 300 and 384 layers,
hydrates both payloads, and checks their depths, populated layer pixels and
the last layer's bytes. The production packager packages those exact material
payloads and retains the lowercase JSON field names.

A managed probe links the production device-requirements reader and uses
the shared `RuntimeAssetCatalogEntry`. It reads the packaged startup and
streamed dependency graph and returns a maximum of 384. Its session/catalog
host is a native fixture; this does not run the browser fetch transport.
A JavaScript probe calls the actual `WebGpuCanvasRenderer.initializeEngine`
with a mock adapter. The 512-layer adapter receives a request for 384 layers.
The 256-layer adapter fails before `requestDevice` is called. No GPU output
or physical adapter result is claimed.

Six malformed or unsupported metadata cases fail before a manifest is
published: a string limit, a baseline-only limit, an unknown limit, a nonempty
feature list, an empty limit object and a null requirements object. The same
recipe without requirements publishes successfully and gains no new fields.

The package tool and managed probe builds pass with zero warnings. JavaScript
syntax checks and whitespace checks pass. The combined Editor publish passes
after adding the new packager partial to `BrowserContentPublishing.props`.
The full publication gate is recorded in
[platform validation](../../testing/platform/platform-validation.md#unified-runtime-ur-verification-view).

The implementation contract and example are in the
[content cooker guide](../../../../Tools/BrowserContentCooker/README.md#engine-asset-catalogs).
