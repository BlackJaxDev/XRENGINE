# Browser published type metadata startup

## Behavior

The authored engine-assets recipe declares one standalone
`/engine/Metadata/AotRuntimeMetadata.bin` asset and a `publishedMetadata` manifest
reference. The existing content packager owns its byte length, SHA-256 identity,
same-origin immutable URL, and payload budget. The payload is the existing
MemoryPack `AotRuntimeMetadata` model, including known types, registered cooked
asset types, transforms, redirects, replication properties, and YAML converter
types. The authored world payload and cooked-binary version are unchanged.

The Editor publisher enumerates the actual managed assembly names shipped under
the browser framework and scans their compiled DLLs in an isolated cook-time
load context. This prevents the Editor's desktop assemblies, including its Jolt
variant, from silently supplying browser type metadata. The active authoring
`GAME` assembly is built directly with the same `Any CPU` game-project target
as the browser project reference, then loaded from that exact output. Its
module version must match both the built and browser-linked game DLLs. During
authored world export, a temporary development type-resolution scope prefers
that active game for YAML discriminators before the old authoring cache; the
scope is synchronous and thread-local, and ends before publication. The cooker rejects a root or nested game object
from an older assembly context before its serializer can write bytes. Built-in cooked codecs come from the existing composed runtime asset
registrations, scoped against the scanned browser type closure. Game codec
entries must belong to that exact active game assembly; the browser-only bitmap
font codec is included explicitly. Runtime-binary roots in the cooked recipe
must have a matching active codec. Browser type scanning reads attribute data
without constructing attribute instances or running the game's module
initializer in another authoring context.

`BrowserEngineExports.StartCoreAsync` opens the structurally validated catalog,
reads the hash-verified metadata asset, deserializes and pins its fingerprint,
then selects Published mode. Asset binding, game registration, shader catalog
loading, and cooked world hydration follow. A failed or canceled fetch before
installation leaves the metadata slot unpinned. Once installed, the same type
metadata supports subsequent world restarts; different published type metadata
requires a page reload. This fingerprint is not the identity of the full
content bundle. Replication, redirects, and transform discovery report a named
startup-order failure if a browser development scan occurred earlier.

Published interpreter type lookup also admits bounded closed generic and array
identities used by cooked member values. It requires the generic definition and
every non-framework component type to belong to the published type table.
Lengths, bracket nesting, component count, recursive depth, and array rank are
bounded; pointers, byrefs, and open generic shapes are rejected. NativeAOT's
existing lookup remains separate.

## Lifecycle smoke fixture

`Tools/BrowserRuntimeMetadataCooker` uses the same cook-time metadata builder
as the Editor. It cooks the checked-in authored YAML smoke world into the
published binary format and writes metadata to an ignored validation directory.
The smoke recipe references those generated files; the effects-recipe staging
script takes the fixture directory as its third input and copies them before
packaging. No generated binary fixture is checked in. Its `--verify` mode
installs metadata in a fresh process, selects Published, and hydrates the cooked
`XRWorld` through `CookedAssetReader`.

## Validation and remaining qualification

- Metadata cooker built with zero warnings and errors; its fresh Published
  verification hydrated the lifecycle smoke world
- The exact-output scanner inspected 17 shipped browser assemblies, 7,726 type
  definitions, 42 transforms, 15 redirects, and 12 registered asset types
  without loader failures
- The shared content packager accepted the staged engine-effects recipe with
  36 assets, including the metadata and cooked world payloads
- JavaScript syntax and repository diff whitespace checks passed for the
  touched package code
- The Release Editor compiled with zero warnings and errors after enabling
  Windows targeting against the existing cached reference pack
- The Release Browser compiled with zero warnings and errors using the existing
  ignored in-process WebAssembly SDK task override required by this local
  sandbox; ordinary CI does not use that override
- The genuine staged Editor publisher passed through the browser-owned game
  build, authored RollingBall world cook, WebAssembly application publish,
  browser metadata scan, package, and activated output. The final package has
  33 hash-owned assets, including a 1,102,102-byte metadata payload and a
  2,724-byte cooked RollingBall world. Both asset byte lengths and SHA-256
  hashes were checked against the activated manifest. The metadata fingerprint
  is `1422f196b0b6fab4d5515500a8e32eb36ece319d8646e521e62269c5ca99809f`.
  The exact final log is
  `Build/_AgentValidation/20261001-225000-lit-surface/full-publisher/logs/metadata-publisher-threadlocal.log`
- This sandbox's child MSBuild silently failed its project graph unless passed
  `/m:1`; `/nodeReuse:false` alone did not help. The publisher offers opt-in
  `XRE_BROWSER_PUBLISH_SINGLE_MSBUILD_NODE=1` for its two browser-owned child
  commands under that local constraint; ordinary build scheduling is unchanged
- The activated metadata-bearing RollingBall bundle passed three headless
  WebAssembly StartAsync/120-frame Step/StopAsync cycles in one interpreter
  through the real linked-game exports, published metadata install, Jolt,
  and cooked startup world. The file-backed Node probe used that bundle's
  manifest and content, with no substituted world. Evidence is
  `Build/_AgentValidation/20261001-225000-lit-surface/full-publisher/logs/metadata-wasm-three-cycle.log`
- The complete frozen source passes Editor, Server, VRClient, WebGPU,
  RenderingParity, all nineteen portable browser compile rows and fresh native-
  Jolt browser publication with zero compiler warnings/errors
- Chromium world-play and the Windows Editor authored bundle must still be
  qualified in their existing CI lanes; no browser rendering outcome is claimed
