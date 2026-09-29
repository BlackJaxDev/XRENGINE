# Browser cooked content delivery

**Status:** Source implementation. Build, cooker execution, browser/GPU runs,
cache/network fault exercises and device qualification are deferred by request.

The browser host can load a cooked world from a same-origin manifest URL. The
existing local snapshot import and built-in demo remain available. The new path
consumes bytes directly through generated interop; it does not create downloaded
files, enumerate folders, invoke native importers or wait synchronously on tasks.

## Package and delivery contract

`Tools/BrowserContentCooker` packages already-converted mesh/material/scene JSON
and GPU-native texture mip bytes. Its standalone .NET project has no engine or
native importer dependency. Model import, image conversion/compression, mip
generation, collision cooking, shader cooking and font-atlas generation remain
offline work. The example inputs are source fixtures, not an executed cook result.

The manifest declares schema 1, toolchain `xrengine-browser-content-1`, renderer
profile `browser-forward-v1`, opaque logical IDs, explicit dependencies and ordered
payload variants. Each variant specifies its byte count, SHA-256, relative
`payloads/<hash>.bin` URL, encoding and required device features. Texture variants
also carry format, dimensions, per-mip byte lengths, normal convention and alpha
convention. Materials and scene instances reference asset IDs rather than GPU handles.

The renderer profile binds these payloads to the shipped forward shader/material
ABI. A shader/layout or incompatible material interpretation change must bump the
profile and ship matching runtime code and content together. This package does not
accept arbitrary user WGSL; the selected module shaders warm before content loads.

Publish immutable payloads before replacing the bootstrap manifest. The cooker
writes hash-addressed payloads and replaces the manifest only after packaging
succeeds. HTTP deployment should revalidate the bootstrap (`Cache-Control:
no-cache`) and serve payloads with `Cache-Control: public, max-age=31536000,
immutable`. The loader explicitly revalidates the manifest, permits HTTP caching
for immutable payloads, and verifies every consumed payload's length and hash,
including cached responses. Keep the previous deployment's payloads available for
clients still holding its manifest. Hash checks provide integrity relative to the
manifest; trusted same-origin HTTPS hosting remains the authenticity boundary.

## Scheduling and lifetime

Entrypoint dependencies load before the first rendered world frame. Streamed roots
load afterward. Both use dependency order, bounded concurrent downloads and a yield
between asset-consumption operations. An upload/deserialization operation is
indivisible; per-payload byte limits and a maximum of 64 instances per scene chunk
bound its work. This is a scheduling budget, not a measured millisecond guarantee.
The content path allocates at these explicit mutation boundaries; normal frame
collection/submission retains its existing arenas.

The managed session creates engine mesh/material/texture descriptors and pins their
GPU resources once. Scene chunks add ordinary engine components and share those
descriptors. All chunks in one package use the same camera. Stop, restart, page
teardown and device failure abort outstanding fetches and reject stale callbacks;
resource teardown releases the session's pins and scene references. A corrupt or
incompatible streamed asset fails the session explicitly instead of leaving a
silently incomplete world. Restart reloads from the retained manifest URL.

## Texture policy

| Variant | Required feature | Fallback |
| --- | --- | --- |
| ASTC 4×4 RGBA, linear or sRGB | `texture-compression-astc` | Matching RGBA8 |
| ETC2 RGBA8, linear or sRGB | `texture-compression-etc2` | Matching RGBA8 |
| RGBA8, linear or sRGB | Core device | Required for every texture |

Startup requests supported optional compression features; variant selection uses
the enabled device's features. Variants must agree on dimensions, full mip count,
color space, normal convention and straight-alpha semantics. Compressed base
dimensions must be block-aligned. Each mip's tightly packed block bytes are checked
before upload; small physical mip extents are rounded to the compression block.
The selected native blocks upload without runtime decompression or transcoding.

The fixed material sampler uses linear minification/magnification/mip filtering
and clamp-to-edge addressing. The selected forward material uses color textures
with hardware sRGB decode when authored as sRGB. Normal-map metadata can be
represented as linear tangent-Y-positive data, but the current forward material
rejects its use as albedo; richer normal-map shading is not implied. Premultiplied
alpha and unsupported sampling modes fail rather than change meaning. Authors
must generate linear-light color mips and coverage-preserving cutout mips offline.

This is a browser delivery envelope for preconverted mip payloads. It does not
replace the [XRTS texture cache roadmap](../../todo/texturing/texture-compression-and-cooked-cache-todo.md)
or duplicate its native import/cache implementation. KTX2/Basis containers,
transcoding, additional ASTC block sizes and ETC2/EAC channel formats remain outside
the selected profile. Offline tooling may later populate the same delivery envelope
from the native cooked cache or a KTX2 conversion.

## Limits and diagnostics

The manifest is capped at 1 MiB, 4096 asset IDs, 64 dependencies per asset and depth
32. JSON payloads are capped at 1 MiB, texture payloads at 4 MiB, and selected
package payloads at 64 MiB. There are at most three concurrent downloads and a
12 MiB payload staging budget. Requests use deadlines and bounded retry/backoff;
schema, hash and dependency errors include the affected asset when applicable.
Managed material/instance and retained-resource budgets also apply independently.

URLs must stay on the application's origin and use HTTPS (localhost HTTP is
allowed). Credentials, redirects, path traversal and arbitrary payload paths are
rejected. Payload URLs must match their declared hashes. Missing dependencies,
cycles, duplicate IDs, unknown fields/types and incompatible variants fail before
asset consumption. The browser never accepts a native path from these packages.

Counter snapshots separate received payload bytes, compressed versus uncompressed
payload bytes, active staging, managed retained resource payloads and estimated GPU
resource bytes. GPU figures are logical estimates, not physical allocation queries.
Compressed bytes remain compressed on the CPU; decoded-equivalent texture size is
not a CPU allocation. Fetch buffers are released after consumption; the managed
immutable resource descriptors retain the selected representation needed by the
current resource API. Full-resolution source images and alternate variants are not
downloaded or retained. The manifest URL/hash recipe supports explicit reload;
automatic device-loss reconstruction remains later work.

Persistent CacheStorage/IndexedDB caching is deliberately absent. HTTP caching is
optional: eviction, disabled caching or a cold cache simply causes a new verified
download. No partial payload is installed as a usable asset. Offline operation and
storage-quota management are not claimed.

## Use and remaining acceptance

Run the standalone cooker as described in its README, publish the resulting
directory beside the application, then enter its manifest URL in **Cooked world**
or launch with `?world=./content/manifest.json`. **Demo scene** returns to the fixture;
**Restart** repeats the selected content load. **Capture counters** includes both
network/content and managed resource snapshots.

Acceptance still requires cold/warm cache loads, throttling, cancellation during
each stage, missing/corrupt/deep manifests, quality incompatibilities, compressed
fallback and small mip rendering, failed uploads, teardown/restart, and memory/frame
budgets on the reference devices. No Python was used and no validation was executed.

API reference: [WebGPU compressed texture and copy rules](https://www.w3.org/TR/webgpu/).
