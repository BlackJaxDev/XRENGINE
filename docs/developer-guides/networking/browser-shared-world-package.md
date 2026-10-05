# Shared native and browser world packages

Browser realtime admission requires a verified immutable package identity. A
browser catalog hash or a join handoff cannot replace that identity. Native YAML
and browser cooked assets are different bytes; publishing both representations
under one `WorldPackageManifest` lets the existing managed admission protocol
identify the same complete package without claiming the representations have
identical file hashes.

## Explicit publisher selection

Set `BrowserSharedWorldPackageManifestPath` in the project's `.xrproj` to a
project-relative, existing `world-package.json`. Leave it unset for ordinary
local browser publishing. The setting does not connect to a server or supply
admission credentials.

The selected native package must pass canonical manifest, exact retained-byte
hash/length and complete file-inventory verification and
contain one self-contained native `.asset` world with the exact `XRWorld` type.
Its bytes and manifest-relative path must match the project's saved startup
world beneath `Assets` (native `World.asset` maps exactly to `/game/World.asset`). The built-in server's
`--create-world-package <directory> --world-name <name>` command can prepare this
bounded native input; select a byte-identical copy of its world as the project's
startup asset at the same relative path, then use the normal `BrowserWebGPU` project build.

For a small keyboard-driven network fixture, append `--network-kinematic` to that
server command. It opts the package into a plain-transform pawn that captures
W/A/S/D for the managed server's 5 m/s X/Z simulation and adds an authored
replicated landmark. The landmark has no visual mesh; the shared package's
self-contained asset rules currently exclude the native mesh and shader source
representation. The default command still creates the empty baseline.

`Tools/BrowserSmoke/prepare-network-kinematic-project.mjs` stages this generated
native package, a previously cooked canonical shader directory, and a new
project directory for the normal Editor CLI. It requires the original manifest
and world only, checks the world entry's length/hash, and copies those exact
bytes into the native input and browser startup locations. It neither cooks the
world nor changes the publisher's admission checks. The Windows browser lane
runs the real native generator and Editor publisher and compares their retained
native world identity and bytes. This step starts no server or network session
and installs no certificate.

During a later real session, `BrowserEngineExports.GetNetworkSimulationStatus()`
samples the current client state on demand. It exposes a pose only while the
client is ready and the local controller matches that client's session, player
and entity assignment. `clientAcknowledged` is the manager-wide input sequence
telemetry, not a separate per-player watermark. Suspension or manager retirement
does not expose the previous client's pose. The status contains no admission
credential and is not a substitute for `IsNetworkGameplayReady()`.

Derived game worlds, external asset dependencies, scalar object GUID/path
references, asset read converters without explicit self-containment inspection,
custom YAML shapes/tags, ambient game/engine roots and unsupported native bootstrap identifiers are
rejected with a `BrowserCook.SharedPackage*` diagnostic. These bounds apply only
when the project requests the shared package. They do not restrict ordinary
local browser publishing or establish arbitrary game/native-loader parity.
Source size, links, YAML depth and reference shapes are inspected before world
hydration. The publisher cooks the retained verified native bytes, rather than
reopening a potentially changed source file. Inline object sharing through YAML
aliases is supported; ambient asset-cache lookups are not.

## Published contract

The publisher retains the native world at its existing manifest-relative entry
point, alongside the normal `manifest.json` and immutable `payload/*.bin` files.
The catalog adds `worldPackage: "world-package.json"`. The canonical package
metadata binds `browserCatalog: "manifest.json"` and `browserStartupWorld` to the
catalog's startup asset path. The package hash covers the entire catalog and
both representations' bytes. `world-package.json` is written last inside the
existing unpublished staging directory, followed by normal site activation.

World ID, revision, asset schema, build version, package ID and native bootstrap
remain those of the selected native input. The content hash is recomputed for
the extended package. A managed worker or native client must therefore consume
the emitted content directory and its new `world-package.json`; the old input
package's handoff is intentionally incompatible. The native loader verifies all
declared files and loads its unchanged native `WorldEntryPoint`.

The complete shared package is limited to 4,096 files, 4 MiB per payload, 1 MiB
per JSON descriptor, and 64 MiB total. Its native entry path uses ASCII letters,
digits, underscores, hyphens, dots and slash-separated nonempty segments, is at
most 512 characters, and cannot occupy the reserved `payload/` directory.

## Browser verification and lifetime

The browser reads the optional descriptor through the same credential-free,
same-origin, no-redirect content transport. It validates the native canonical
length-prefixed UTF-8 hash, refetches the hash-bound catalog, and verifies every
declared native and cooked file before exposing package identity. It rejects
missing, extra, mismatched or unreferenced declared payloads and startup-binding
changes. It retains no additional package byte buffers after verification.

Only after the verified startup asset hydrates does the source register the
immutable world identity and the native world entry's exact manifest-relative
replication path. The join handoff must match that identity. Catalogs without
the opt-in remain playable locally but have no verified multiplayer identity;
join does not synthesize one from a filename, environment override or handoff.

This establishes package-byte and admission-identity binding. Native/browser
gameplay equivalence, server game registration, deployment, actual browser join,
replication and reconnect behavior require separate runtime validation.
