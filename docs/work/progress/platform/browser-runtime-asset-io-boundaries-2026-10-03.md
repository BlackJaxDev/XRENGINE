# Runtime asset I/O boundaries in browser-capable code

## Animation binary cache provider handoff (2026-10-29)

Animation cache reads and writes use the asset file provider captured for cache
metadata. Background writes retain that same instance. The optional codec handoff
leaves other cache codecs on their existing interface. Desktop and publisher
adapters implement the new binary-cache capability. Custom hosts must implement
it for animation cache payloads; a missing capability rejects the cache read and
fails the background write, which does not fall back to generic cache output.

The desktop writer keeps a unique sibling temporary file and overwrite move. It
now creates that file exclusively and cleans it only after successful creation.
A name collision fails without changing the colliding file. Failed moves clean
the owned temporary file on a best-effort basis. The captured provider has no
retirement lease, and the handoff does not pin the raw source or discovery
generation. Hosts must retain the provider until queued work finishes.

A production-method probe passes for a real clip payload, a provider swap during
a background write, readback, and desktop replacement. Four focused tests pass
for the provider handoff, missing read and write capabilities, concurrent
replacement, and cleanup after a failed move. The Linux test runner links the
production writer with a small desktop adapter; a separate production probe uses
the actual desktop provider. The publisher Release build passes. The first gate
attempt found that the headless project did not link the new writer with its
linked desktop adapter. The project now links that writer; the existing desktop
filesystem fixture passes in the rerun. Editor, Server, and VRClient Release
builds pass on Linux with Windows targeting enabled. All 19 portable manifest
projects compile for browser-wasm, and Browser publishes with native WASM and
pinned Jolt inputs. These checks do not establish Windows behavior, live browser
rendering, or a provider retirement guarantee.

The four-root direct-file and blocking counts are unchanged. Both inventory
requirements remain open, as do the separate shader and provider-lifetime issues.

## Humanoid conformance source scanner (2026-10-29)

The scanner admits host-file access and captures one discovery provider for
root checks, recursive enumeration, and file reads. It uses the existing
metadata-directory and shared-read capabilities. The desktop runner installs a
provider with both; a custom host must provide both. The original text filters,
UTF BOM detection, read sharing, line numbers, and error reporting remain.

The raw-read lease and discovery generation reject owner changes observed before
return. Discovery has no retirement lease or atomic publication guard. A host
must keep the captured provider usable and coordinate replacement through the
scan. This change does not resolve the separate YAML or shader ownership issues.

The production-path probe passes for filtering, read sharing, owner replacement,
and caller-thread admission. Seven focused tests pass for virtual-host reads,
UTF-16 BOM detection, disposal, source and discovery changes, missing capability,
read errors, and a missing root. Editor, Server, and VRClient Release builds
pass on Linux with Windows targeting enabled. The desktop filesystem headless
fixture passes. All 19 portable manifest projects compile for browser-wasm, and
Browser publishes with native WASM and pinned Jolt inputs. These checks do not
establish live conformance-runner execution, Windows behavior, or browser
rendering.

The two physical read and directory operations leave AnimationIntegration,
outside the historical four-root lexical scan. The four-root and blocking
counts stay unchanged; both inventory requirements remain open.

## Directory helper compatibility review (2026-10-29)

The previous public `XREngine.Core.Utility.EnsureDirPathExists` method is absent
from Data. Existing binaries that call it must be rebuilt, and source callers
must reference the desktop platform assembly and call
`DesktopPathUtility.EnsureDirPathExists`. A reflection check confirms the old
method is absent and the replacement is public. The desktop helper works without
host-service registration. All eight tracked C# call sites use the replacement.

A portable forwarding method backed only by an installed host service would still
fail before service registration, unlike the former method. It would not give
full behavioral compatibility. No such partial shim was added. The conformance
source scanner still mixes directory probes, discovery and file reads, and the
animation binary cache owns staged writes and replacement. Their placement needs
coherent provider and lifetime decisions; this review does not change them or
close either inventory.

## Desktop directory preparation (2026-10-29)

The directory helper moves from portable Data to `DesktopPathUtility` in the
desktop platform assembly. Desktop diagnostic capture and editor callers use
the new API. The helper retains its separator, extension, empty-path, existing
directory, and creation behavior. External callers of the former public helper
must move to the desktop API.

A desktop production-method probe passes for file-parent creation, a directory
path, an existing directory, and an empty path. Editor, Server, and VRClient
Release builds pass on Linux with Windows targeting enabled. The desktop
filesystem headless fixture passes. All 19 portable manifest projects compile
for browser-wasm, and Browser publishes with native WASM and pinned Jolt
inputs. These checks do not establish Windows behavior, a live editor capture,
or browser rendering.

The two direct directory calls leave Data. The four-root lexical scan now finds 60 matching lines in 13 files;
Core and Rendering remain at 11 lines in three files. Blocking counts do not
change. Both broad inventories remain open.

## Humanoid avatar-profile host read and debug resize readiness (2026-10-29)

The synchronous Unity humanoid avatar-profile importer captures the raw-read
source after host-file admission. It reads JSON once and holds the publication
guard through parsing and validation. UTF-8 without a BOM and UTF-16 with a BOM
produce the same profile as a desktop file in the focused tests. Source changes
during the read reject the result. The host must keep its source usable during
the read; caller-side profile migration is a separate operation.

Seven focused tests pass for the real conformance reference, custom sources,
source replacement, unsupported reads, and missing desktop files. A separate
production-path probe passed. Editor, Server, and VRClient Release builds pass
on Linux with Windows targeting enabled. The desktop filesystem headless fixture
passes. All 19 portable manifest projects compile for browser-wasm, and Browser
publishes with native WASM and pinned Jolt inputs. These checks do not establish
Windows startup, a live profile migration, or browser rendering.

This removes one direct file read in AnimationIntegration, outside the historical
four-root count. The four-root file count and blocking counts are unchanged; both
inventory requirements stay open.

A debug diagnostic resize now clears its prior ready-session stamp until settled
frames have been submitted. A local diagnostic-host probe reproduced stale
acceptance before the change and passed afterward. Browser smoke checks the
invalidation on a resize of an already-ready session. Native WebGPU execution
was not repeated in this environment. The desktop report's dark Advanced
geometry remains unresolved. The shader ownership and YAML discovery lifetime
investigations also remain open.

## Humanoid pose-audit host I/O (2026-10-29)

Pose-audit report reads and writes use admitted host services. A reference check
and its read retain one discovery installation and raw-read lease. The report
writer uses the optional shared-output capability so desktop files keep
`FileShare.Read`, UTF-8 without a BOM, and the existing gzip format. The
discovery and output providers have no retirement lease; hosts must keep them
usable during the operation.

Ten focused tests pass, including JSON and gzip round trips, component reference
loading, missing reports, caller-thread rejection, source and discovery swaps,
and output error disposal. Editor, Server, and VRClient Release builds pass on
Linux with Windows targeting enabled. The desktop filesystem headless fixture
passes. All 19 portable manifest projects compile for browser-wasm, and Browser
publishes with native WASM and pinned Jolt inputs. Gate logs contain no warning
or error diagnostics. These checks do not establish a live pose audit, Windows
startup, or browser rendering.

Four lexical direct-file matches were removed from AnimationIntegration, outside
the historical four-root count. That count and the blocking counts are unchanged;
both broad inventory requirements remain open. The [Unity static animation
investigation](../../investigations/animation/2026-10-29-unity-static-anim-import.md)
records the generator feasibility results and remaining compile and model gaps.

## VR screenshot output placement (2026-10-29)

The VR player screenshot path checks host-file admission and captures the
existing diagnostic output service before it requests an image. The callback
encodes and writes through that service, keeps the indexed PNG filename, and
disposes the image on success or failure. The captured service has no retirement
lease; the host must retain it until the callback completes.

Seven focused tests pass for captured service selection, image disposal,
failure paths, caller-thread rejection, and the real desktop output adapter.
The test and probe run without a renderer. Editor, Server, and VRClient Release
builds pass on Linux with Windows targeting enabled. The desktop filesystem
headless fixture passes. All 19 portable manifest projects compile for
browser-wasm, and Browser publishes with native WASM and pinned Jolt inputs.
Gate logs contain no warning or error diagnostics. These checks do not establish
live VR screenshot capture, Windows startup, or browser rendering.

This removes one direct file write from InputIntegration, outside the historical
four-root direct-file count. The four-root count and blocking counts are unchanged.
Both broad inventory requirements remain open. The [Unity static animation
investigation](../../investigations/animation/2026-10-29-unity-static-anim-import.md)
records a separate pre-existing importer failure and the normal import path.

## Published metadata archive probe (2026-10-29)

Published config metadata checks its archive path through the shared admitted
host-file probe. Missing archives still produce the missing-metadata diagnostic,
including when no file discovery or mapping provider is installed. The probe
rejects a raw source or discovery replacement during the check. Archive mapping
has a separate backend and lifetime; the preflight does not pin that backend.

The first Linux archive round-trip probe failed because the desktop Unix mapper
passed a named map to `MemoryMappedFile.CreateFromFile`. .NET rejected that map.
The mapper now uses an unnamed map. The real packed archive then loaded metadata
on Linux. The eight focused tests pass for missing paths and metadata, caller
admission, source and discovery replacement, real archives, and corrupt input.
The test build has no warnings or errors.

Editor, Server, and VRClient Release builds pass on Linux with Windows targeting
enabled. The desktop filesystem headless fixture passes. All 19 portable
manifest projects compile for browser-wasm, and Browser publishes with native
WASM and pinned Jolt inputs. Gate logs contain no warning or error diagnostics.
These checks do not establish Windows startup, Windows mapping, or browser
rendering. The shader and discovery provider lifetime investigations remain open.

The tracked-source scan reports 14 files / 62 direct-file matching lines across
Data, Core, Rendering, and Host. The expression includes comments and misses
target-typed constructors. Both broad inventory requirements remain open.

## Snapshot host-file probes (2026-10-29)

Snapshot asset references use the shared admitted host-file probe. A relative
scan captures one raw-read lease and discovery generation for its lazy engine,
shader, and game candidate checks. Rooted load candidates keep their existing
direct route. When host-file access is not admitted, capture treats an
untracked file-backed asset as inline data. Source or discovery replacement
rejects stale probe results. The discovery provider has no retirement lease.

Seven focused snapshot tests pass, including candidate order, lazy evaluation,
source and provider replacement, rooted checks, inline/reference selection,
and an inline snapshot serialization round trip. Seven scene-path and 11 YAML
path tests also pass after the shared probe change. The standalone round-trip
probe required the native logging backend before snapshot serialization. Editor,
Server, and VRClient Release builds pass on Linux with Windows targeting enabled.
The desktop filesystem headless fixture passes. All 19 portable manifest projects
compile for browser-wasm, and Browser publishes with native WASM and pinned Jolt
inputs. The gate logs contain no warning or error diagnostics. These checks do
not establish Windows startup, live editor snapshots, or browser rendering.

The tracked-source scan reports 15 files / 63 direct-file matching lines across
Data, Core, Rendering, and Host. The expression includes comments and misses
target-typed constructors. Both broad inventory requirements remain open.

## Scene path host-file probe (2026-10-29)

Relative host scene paths now use the admitted, source-owned discovery probe
shared with YAML path resolution. A discovery or raw-source replacement during
that probe rejects its result. Existing extensionless paths keep their names;
missing extensionless paths gain the asset extension. Rooted paths and catalog
identities keep their prior routes. Caller-thread hosts cannot run the relative
host-file probe. The discovery provider still has no retirement lease.

Seven focused scene-path tests and all 11 YAML path tests pass. They cover
extension handling, rooted paths, catalog lookup, caller-thread admission,
desktop fallback, and source and discovery replacement. A separate runtime
probe passes the bounded path checks. Editor, Server, and VRClient Release
builds pass on Linux with Windows targeting enabled. The desktop filesystem
headless fixture passes. All 19 portable manifest projects compile for
browser-wasm, and Browser publishes with native WASM and pinned Jolt inputs.
The gate logs contain no warning or error diagnostics. These checks do not
establish Windows startup, live scene loading, or browser rendering.

The tracked-source scan reports 16 files / 67 direct-file matching lines across
Data, Core, Rendering, and Host. The expression includes comments and misses
target-typed constructors. Both broad inventory requirements remain open.

## VMD host-file output placement (2026-10-29)

VMD path saving now checks host-file admission and captures the optional
`IHostAssetFileOpenWrite` capability from the installed output service. The
desktop implementation uses the prior non-truncating, non-sharing open mode
and does not create parent directories. Hosts without that capability receive
an explicit error. The captured output provider has no retirement lease.

The VMD fixture passes ten read and write tests. The save checks cover a local
file with an unchanged tail, a missing parent, captured output ownership, stream
disposal, caller-thread rejection, and a missing capability. A separate runtime
probe passes the bounded output checks. Editor, Server, and VRClient Release
builds pass on Linux with Windows targeting enabled. The desktop filesystem
headless fixture passes. All 19 portable manifest projects compile for
browser-wasm, and Browser publishes with native WASM and pinned Jolt inputs.
The gate logs contain no warning or error diagnostics. These checks do not
establish Windows startup, live animation playback, or browser rendering.

The tracked-source scan reports 17 files / 68 direct-file matching lines across
Data, Core, Rendering, and Host. The expression includes comments and misses
target-typed constructors. Both broad inventory requirements remain open.

## VMD host-file read placement (2026-10-29)

Synchronous VMD animation loading now checks host-file admission and captures
`IRuntimeHostFileReadBackend` before opening its input. The stream keeps the
previous `FileShare.Read` mode and is disposed after parsing. Custom hosts must
provide the capability and retain the provider during the load. VMD path saving
still uses its existing direct file output and remains in the inventory.

Five focused tests pass for provider capture, sharing and disposal, caller-thread
rejection, a missing provider, invalid input, and a local-file load. A separate
runtime probe passes the same bounded checks. Editor, Server, and VRClient
Release builds pass on Linux with Windows targeting enabled. The desktop
filesystem headless fixture passes. All 19 portable manifest projects compile
for browser-wasm, and Browser publishes with native WASM and pinned Jolt inputs.
The gate logs contain no warning or error diagnostics. These checks do not
establish Windows startup, live animation playback, or browser rendering.

The tracked-source scan reports 18 files / 69 direct-file matching lines across
Data, Core, Rendering, and Host. The expression includes comments and misses
target-typed constructors. Both broad inventory requirements remain open.

## YAML discovery generation (2026-10-29)

YAML path probes now reject a discovery installation change while the raw-read
source has a publication reservation. This covers replacement, reinstallation
of the same provider, and provider installation during the raw-read fallback.
The check does not retain a replaced provider's lifetime. The [investigation](../../investigations/platform/2026-10-29-yaml-asset-path-probe.md)
records why provider disposal needs a separate coordinated host contract.

The focused YAML fixture passes 11 tests. Editor, Server, and VRClient Release
builds pass on Linux with Windows targeting enabled. The desktop filesystem
headless fixture passes. All 19 portable manifest projects compile for
browser-wasm, and Browser publishes with native WASM and pinned Jolt inputs.
The gate logs contain no warning or error diagnostics. These checks do not
establish Windows startup or browser rendering. The direct-file and blocking
inventory counts are unchanged, and both broad requirements remain open.

## YAML asset path admission (2026-10-29)

YAML asset path lookup uses the captured raw-read owner and its host-file
admission rules. Catalog metadata remains available without synchronous byte
reads. An installed host-file provider handles desktop probes; the raw-read
source supplies the fallback when that provider is absent. A source swap during
the probe rejects the result. The [investigation](../../investigations/platform/2026-10-29-yaml-asset-path-probe.md)
records the reproduced gap, the fix, and the remaining provider lifetime limit.

Eight focused tests pass, including desktop and caller-thread YAML deserialization,
catalog lookup, custom and missing providers, missing files, and source swap.
Editor, Server, and VRClient Release builds pass on Linux with Windows targeting
enabled. The desktop filesystem headless fixture passes. All 19 portable
manifest projects compile for browser-wasm, and Browser publishes with native
WASM and pinned Jolt inputs. The gate logs contain no warning or error
diagnostics. These checks do not establish Windows startup or browser rendering.

The tracked-source scan reports 18 files / 70 direct-file matching lines across
Data, Core, Rendering, and Host. Core plus Rendering have 3 files / 11 lines.
The expression includes comments and misses target-typed constructors. Both
broad inventory requirements remain open.

## Cache-root and rendering fallback placement (2026-10-29)

Implementation commit `16e33a906b738e5b9f963f24b91f253e16ef060c` moves
the convex-hull cache-root directory probe to `IAssetMetadataFileBackend` and
the uninstalled rendering facade's direct byte read to
`IRuntimeHostFileReadBackend`. Cache overrides and roots named `Assets` return
before metadata lookup. The uninstalled facade admits host-file access before
reading. The [composition contract](../../../architecture/runtime/project-organization.md)
records the custom-host requirements.

A disposable executable against compiled Core and Rendering passes ten checks.
They cover nested and missing `Assets` directories, explicit and environment
cache overrides, missing metadata capability, an `Assets`-named root,
async-only host admission, uninstalled facade bytes, and missing or inadmissible
read capabilities. The executable uses local files and fake providers; it does
not generate convex hulls or render a scene. Core and the final harness build in
Release with zero warnings and errors. Independent source review found no
internal regression and confirmed desktop registration precedes cache-root
resolution. No tracked tests changed. Evidence is in
`Build/_AgentValidation/20261028-120000-webgpu-io/logs/cache-root-build.log`,
`logs/cache-root-smoke-build.log`, `logs/cache-root-smoke.log`, and
`reports/cache-root-inventory.json`. An initial concurrent harness build logged
a transient copy retry; the final serial shader-boundary probe build has zero
warnings and errors.

The [Uber canonical source investigation](../../investigations/rendering/2026-10-29-uber-canonical-source-owner.md)
reproduces a provider swap between the shader probe and text read, and a missing
file after a positive probe producing null shader text. It records the unresolved
publication, retry, encoding, and object-lifetime constraints. This batch does
not change that shader behavior.

The full local publication gate passes. Editor, Server, and VRClient Release
builds report zero warnings and errors with `EnableWindowsTargeting=true` on
Linux. The unchanged desktop filesystem fixture passes one test. All 19
manifest projects compile for `browser-wasm`; Browser publishes with native
WASM and pinned Jolt inputs. The browser gate reports no warning or error
diagnostics. Logs use the `cache-root-` prefix under the same run's `logs/`;
the fresh output is `cache-root-browser-publish/wwwroot/`. These checks do not
establish Windows startup, actual hull generation, rendered browser parity, or
post-push CI. Shader sources and recipes did not change.

The committed-source scan reports 19 files / 71 direct-file matching lines in
the four roots. Core plus Rendering total 4 files / 12 lines. It removes two
matching lines and two files. Blocking counts are unchanged. Both broad inventory
requirements remain open.

## Renderer file probe placement (2026-10-29)

Implementation commit `0d400c37a1a95aa1e69c6c73394b9829f414bdbc` moves
renderer module assembly existence and hash reads to
`IRuntimeHostFileReadBackend`. One captured provider owns both operations and
the hash stream uses `FileShare.Read` and is disposed. Locationless assemblies
still return an empty hash before host admission or provider lookup.

Uber canonical shader recovery uses the shader resolver's captured file backend
for its existence probe. In-memory canonical and non-generated shaders still
return before the probe. The subsequent `TextFile` load has its own read lease,
so the probe and text read are not an atomic provider snapshot. The
[composition contract](../../../architecture/runtime/project-organization.md)
records custom-host requirements and the separate path owners.

A disposable executable against compiled Rendering passes ten checks. It covers
SHA-256 value, shared read and disposal, provider replacement, missing files,
read errors, locationless and null assemblies, missing providers, non-synchronous
host rejection, generated shader recovery and fields, the named non-host
canonical-source rejection, and in-memory canonical early return. It uses a fake
shader provider and local files. The final harness and Rendering build in Release
with zero warnings and errors. Independent source review confirmed desktop
composition installs the required providers before renderer registration. No
tracked tests changed. Evidence is in
`Build/_AgentValidation/20261028-120000-webgpu-io/logs/render-file-build.log`,
`logs/render-file-smoke-build.log`, `logs/render-file-smoke.log`, and
`reports/render-file-inventory.json`.

The full local publication gate passes. Editor, Server, and VRClient Release
builds report zero warnings and errors with `EnableWindowsTargeting=true` on
Linux. The unchanged desktop filesystem fixture passes one test. All 19
manifest projects compile for `browser-wasm`; Browser publishes with native
WASM and pinned Jolt inputs. The browser gate reports no warning or error
diagnostics. Logs use the `render-file-` prefix under the same run's `logs/`;
the fresh output is `render-file-browser-publish/wwwroot/`. These checks do
not establish Windows startup, rendered browser parity, live shader hot reload,
or post-push CI. Shader sources and recipes did not change.

The committed-source scan reports 21 files / 73 direct-file matching lines in
the four roots. Core plus Rendering total 6 files / 14 lines. It removes three
matching lines and two files. Blocking counts are unchanged. Both broad inventory
requirements remain open.

## Texture cache replacement (2026-10-29)

Implementation commit `1ad03bf11f1ab8e8219492b7ebaf59ffee8f5ee2` adds the
optional `ITextureStreamingCacheReplaceBackend` to the existing cache-file
backend. Desktop and the smoke publisher use it. Each call writes a unique
same-directory file, applies the timestamp before publication on a best-effort
basis, then moves it over the destination. The operation cleans up its own
staging file on failure when possible. Final paths, raw payload bytes, and cache
format are unchanged. The payload lease stays alive until the replacement call
returns. A legacy custom backend keeps its existing staged-write path. The
[composition contract](../../../architecture/runtime/project-organization.md)
records the requirements and remaining limitations.

A disposable executable against compiled Rendering passes 14 checks. It covers
legacy backend order and provider replacement, exact large payload bytes and
stamp, missing capability, host admission, native replacement, failed replacement
onto an existing directory and staging cleanup, replacement-backend provider
retention, and two concurrent writers with a concurrent reader. The reader
observed only complete payloads; after 40 writes the final payload and timestamp
matched, and no replacement staging files remained. The harness links the same
native helper used by desktop and the publisher and uses local Linux files. It
does not test a locked destination file, native timestamp failure, process crash,
Windows sharing, a network filesystem, or hardware rendering. The final harness
and smoke publisher build in Release with zero warnings and errors. No tracked
test code or assertion changed. Independent review found no concrete defect.
Evidence is in `Build/_AgentValidation/20261028-120000-webgpu-io/logs/texture-replace-smoke-build.log`,
`logs/texture-replace-smoke.log`, `logs/texture-replace-publisher-build.log`, and
`reports/texture-replace-inventory.json`.

The full local publication gate passes. Editor, Server, and VRClient Release
builds report zero warnings and errors with `EnableWindowsTargeting=true` on
Linux. The unchanged desktop filesystem fixture passes one test. All 19
manifest projects compile for `browser-wasm`; Browser publishes with native
WASM and pinned Jolt inputs. The browser gate reports no warning or error
diagnostics. Logs use the `texture-replace-` prefix under the same run's `logs/`;
the fresh output is `texture-replace-browser-publish/wwwroot/`. These checks do
not establish Windows startup, rendered browser parity, actual GPU texture
appearance, network filesystem behavior, or post-push CI. Shader sources and
recipes did not change.

Concurrent replacement is last-writer-wins. An older cook may still publish
last; timestamp precision can limit stale-cache detection. A filesystem can
reject replacement of an open file. Existing custom backends without the new
capability retain their earlier shared temporary path and delete-then-move gap.
Provider capture is not a retirement lease, and the operation does not claim
crash durability. The committed-source scan is unchanged: 23 files / 76
matching direct-file lines across four roots, including 8 files / 17 lines in
Core and Rendering. Blocking counts are unchanged. Both broad inventory
requirements remain open.

## Texture cache write placement (2026-10-29)

Implementation commit `7983f642e439080071f7947cf31dd6accaa68a78` moves
staged texture cache file operations to the optional
`ITextureStreamingCacheFileBackend` on the installed asset file system. The
writer captures one backend and retains the original order: prepare the parent,
rent and serialize the payload, write the temporary file, release the payload
lease, then publish and stamp. Desktop and the smoke publisher use the same
native helper. The [composition contract](../../../architecture/runtime/project-organization.md)
records custom-host requirements and the later replacement capability.

The cache path, binary payload, temporary-file flags, delete-then-move order,
and best-effort timestamp remain unchanged. This does not add an atomic replace,
a provider-retirement lease, or same-path write serialization. A failed move can
leave the previous file deleted, and failed writes can leave the temporary file.
The payload span must be consumed before the backend returns. The host admission
check remains outside the cache-write failure catch.

A disposable executable against compiled Rendering passes nine checks. They
cover exact large payload bytes, directory creation, timestamp, staged publish,
replacement of an existing cache, retained provider through global replacement,
prepare/write/publish failure handling, missing file system and optional
capability, and async-only host rejection before backend calls. The fake backend
links the same native helper used by desktop and the publisher. It does not
exercise a failed native delete, move, or timestamp operation, or concurrent writes.
The final harness builds with zero warnings and errors. The smoke publisher
builds in Release with zero warnings and errors. Independent review found no
format migration requirement and identified the existing replacement gap. No
test code or assertion changed. The headless test project links the new native
helper because it already links the desktop filesystem source. Evidence is in
`Build/_AgentValidation/20261028-120000-webgpu-io/logs/texture-write-smoke-build.log`,
`logs/texture-write-smoke.log`, `logs/texture-write-publisher-build.log`, and
`reports/texture-write-inventory.json`.

The initial full gate passed Editor, Server, and VRClient but the headless
build failed: its source-linked desktop filesystem did not include the new
helper. Commit `3ae35e0e0` adds that source link. The corrected headless build
passes with zero warnings and errors, and the unchanged desktop filesystem
fixture passes one test. The desktop builds do not consume that headless project
and remain valid for the same implementation source. The browser gate resumes
separately after the fix. All 19 manifest projects compile for `browser-wasm`;
Browser publishes with native WASM and pinned Jolt inputs. The browser gate
reports no warning or error diagnostics. Logs are
`logs/texture-write-full-gate.log`, `logs/texture-write-headless-build.log`,
`logs/texture-write-headless-test.log`, and `logs/texture-write-browser-gate.log`.
Fresh output is `texture-write-browser-publish/wwwroot/`. These checks do not
establish Windows startup, actual GPU texture appearance, rendered browser
parity, or post-push CI. Shader sources and recipes did not change.

The committed-source scan reports 23 files / 76 direct-file matching lines in
the four roots. Core plus Rendering total 8 files / 17 lines. It removes six
matching lines and one file. The target-typed temporary `FileStream` constructor
was not counted by the historical expression. Blocking counts are unchanged.
Both broad inventory requirements remain open.

## Texture source probe placement (2026-10-29)

Implementation commit `f85fb46daca8856a5ec242f10d43243dceb38854` moves
texture source, batch, cache-preparation, and streaming fallback existence
probes to `IRuntimeHostFileReadBackend`. Source fallback retains its captured
probe provider through recursion. Texture-asset marker inspection uses the read
provider with `FileShare.ReadWrite`, and replays the first byte without seeking
so a non-seekable stream and byte-order mark work. The source stream is disposed
once. Cache usability obtains its length from the existing file-mapping backend.
The [composition contract](../../../architecture/runtime/project-organization.md)
records the custom-host requirements and limits of separate providers.

A disposable executable against compiled Rendering passes 17 checks. They cover
cache preparation and mip count; missing and empty source paths; binary and YAML
marker inspection, shared stream disposal, non-seekable UTF-8 BOM input and I/O
failure; provider retention across source fallback; batch decoding and cancellation;
valid, missing and empty mapped cache usability; fallback resident data and its
provider-free repeat; missing read provider; and async-only host admission. The
harness uses a proxy rendering host, linked Magick codec, test mapping backend,
and local files. It does not establish desktop GPU texture rendering, actual
DirectStorage performance, or full application behavior. No tracked tests changed.
The final harness builds with zero warnings and errors. Independent review found
and prompted the non-seekable stream fix, then found no defect in that fix.
Evidence is in `Build/_AgentValidation/20261028-120000-webgpu-io/logs/texture-probe-smoke-build.log`,
`logs/texture-probe-smoke.log`, and `reports/texture-probe-inventory.json`.

The full local publication gate passes. Editor, Server, and VRClient Release
builds report zero warnings and errors with `EnableWindowsTargeting=true` on
Linux. The unchanged desktop filesystem fixture passes one test. All 19
manifest projects compile for `browser-wasm`; Browser publishes with native
WASM and pinned Jolt inputs. The browser gate reports no warning or error
diagnostics. Logs use the `texture-probe-` prefix under the same run's `logs/`;
the fresh output is `texture-probe-browser-publish/wwwroot/`. These checks do
not establish Windows startup, rendered browser parity, actual GPU texture
appearance, or post-push CI. Shader sources and recipes did not change.

The scan reports 24 files / 82 direct-file matching lines across the four roots.
Core plus Rendering total 9 files / 23 lines. It removes six matching lines and
two files. Two target-typed file constructions moved by this batch were not
counted by the historical expression. Blocking counts are unchanged. Both broad
inventory requirements remain open.

## Send-local-copy source consistency (2026-10-29)

Implementation commit `31c38ac57ad29b2e5459be3219fa61a55bfe51c4` keeps
send-local-copy existence probes and asynchronous bytes on the asset manager's
captured runtime source. Global `DirectStorageIO.Source` replacement no longer
retargets the bytes after the manager captured its owner. The source keeps its
own read implementation, including acceleration. The manager checks its owner
epoch after the probe and read and checks cancellation before remote submission.
The [composition contract](../../../architecture/runtime/project-organization.md)
records custom-source semantics and the provider-lifetime limit.

When the manager has no source, the existing host read capability probes the
path. A missing file still sends a request with no payload; an existing file
still fails because an asset source is required. Custom hosts must provide a
host read capability for this null-source probe. A custom source's `Exists`
result now determines whether its bytes are requested, and errors from `Exists`
propagate. Desktop `DirectStorageAssetSource.Exists` retains physical
`File.Exists` behavior. This deliberately pairs a source's probe with its own
read and does not claim a source-retirement lease for independent global swaps.

A disposable executable against compiled Core and a real one-worker job manager
passes ten checks. They cover source replacement during the probe, source bytes
and null payloads, probe and read errors, cancellation including a source that
ignores its token, owner retirement during a delayed read, null-source missing
and existing files, and rejection of an async-only source before probing. The
transport is controlled in memory and does not use network sockets. Core and the
final harness build in Release with zero warnings and errors. Independent review
found no concrete regression. No tracked tests changed. Evidence is in
`Build/_AgentValidation/20261028-120000-webgpu-io/logs/send-copy-build.log`,
`logs/send-copy-smoke-build.log`, `logs/send-copy-smoke.log`, and
`reports/send-copy-inventory.json`. The initial gate was stopped after the
cancellation check was narrowed to this mode; the final gate uses the committed
source.

The final local publication gate passes. Editor, Server, and VRClient Release
builds report zero warnings and errors with `EnableWindowsTargeting=true` on
Linux. The unchanged desktop filesystem fixture passes one test. All 19
manifest projects compile for `browser-wasm`; Browser publishes with native
WASM and pinned Jolt inputs. The browser gate reports no warning or error
diagnostics. Logs use the `send-copy-` prefix under the same run's `logs/`;
the fresh output is `send-copy-browser-publish/wwwroot/`. These checks do not
establish Windows startup, rendered browser parity, accelerated hardware reads,
network interoperability, or post-push CI. Shader sources and recipes did not
change.

The committed-source scan reports 26 files / 88 direct-file matching lines in
the four roots. Core plus Rendering total 11 files / 29 lines. Blocking counts
are unchanged. Both broad inventory requirements remain open.

## Remote download output placement (2026-10-29)

Implementation commit `fed0e8a33b008856e62d462083b276f50244ae28` moves
remote path and ID download persistence to `IHostAssetFileAsyncOutput`, an
optional capability on the installed `IHostAssetFileOutput`. The desktop
adapter creates the parent directory and delegates to `File.WriteAllBytesAsync`
with the caller's token. Output is captured after a successful, nonempty remote
response and after host-file admission. Existing catches keep output errors and
write cancellation as false or null. The
[composition contract](../../../architecture/runtime/project-organization.md)
records the custom-host requirement and provider-lifetime limit.

A disposable executable uses compiled Core, a real one-worker job manager, a
controlled remote transport, and the desktop output source linked into the harness. Nine
checks pass: path and ID downloads write expected bytes; replacement of output
during a write does not retarget it; empty or failed responses do not call the
output; missing output or async capability follows the existing failure route;
cancellation reaches both write routes and produces their existing false or
null result; and desktop output creates the parent before a pre-cancelled write.
The fake transport does not use network sockets. No tracked tests changed.
Core and the harness build in Release with zero warnings and errors. Evidence
is in `Build/_AgentValidation/20261028-120000-webgpu-io/logs/remote-output-build.log`,
`logs/remote-output-smoke-build.log`, `logs/remote-output-smoke.log`, and
`reports/remote-output-inventory.json`.

Review identified that moving the separate send-local-copy probe alone could
mix a host-file provider with the existing DirectStorage byte owner. That probe
remains unchanged. The output extraction preserves the existing host admission,
response checks, path normalization, cancellation handling, and failure catches.
No output provider retirement lease or atomic write was added.

The full local publication gate passes. Editor, Server, and VRClient Release
builds report zero warnings and errors with `EnableWindowsTargeting=true` on
Linux. The unchanged desktop filesystem fixture passes one test. All 19
manifest projects compile for `browser-wasm`; Browser publishes with native
WASM and pinned Jolt inputs. The browser gate reports no warning or error
diagnostics. Logs use the `remote-output-` prefix under the same run's `logs/`;
the fresh output is `remote-output-browser-publish/wwwroot/`. These checks do
not establish Windows startup, rendered browser parity, or post-push CI.
Shader sources and recipes did not change.

The committed-source scan reports 27 files / 89 direct-file matching lines in
the four roots. Core plus Rendering total 12 files / 30 lines. Blocking counts
are unchanged. Both broad inventory requirements remain open.

## Remote asset serving file placement (2026-10-29)

Implementation commit `db24bf998c8a49ba3725dda3800a5f9696dbb9fe` moves the
source-null remote-serving existence probes and asynchronous byte read to host
capabilities. The operation retains one `IRuntimeHostFileReadBackend` for all
host probes, then uses the same object as `IRuntimeHostFileAsyncReadBackend`.
Desktop and smoke-publisher adapters implement the new asynchronous capability
with `File.ReadAllBytesAsync` and the supplied token. Source-backed and catalog
paths retain their existing ownership and read behavior. The
[composition contract](../../../architecture/runtime/project-organization.md)
records the custom-host requirement and provider-lifetime limit.

A disposable executable against compiled Core passes seven checks: successful
probe/read retain one provider through replacement; missing files stop before
read or publication; empty paths need no provider; missing read or asynchronous
capabilities fail explicitly; caller cancellation reaches the read and prevents
publication; and a read completed after owner disposal is rejected before
publication. The fake read backend uses the unchanged local publisher adapter
for real file bytes. An initial retirement check incorrectly expected disposal
to cancel a pending host read and waited indefinitely. The final check releases
the read and verifies rejection by the advanced owner epoch. It does not claim
that provider replacement cancels or leases a provider.

Core and the final harness build in Release with zero warnings and errors.
Independent source review found no definite lifetime, cancellation, or
reservation regression. No tracked tests changed. Evidence is in
`Build/_AgentValidation/20261028-120000-webgpu-io/logs/remote-serve-build.log`,
`logs/remote-serve-smoke-build.log`, `logs/remote-serve-smoke.log`, and
`reports/remote-serve-inventory.json`. The harness does not exercise catalog or
asynchronous-source serving, network transport, or physical remote persistence.

The full local publication gate passes. Editor, Server, and VRClient Release
builds report zero warnings and errors with `EnableWindowsTargeting=true` on
Linux. The unchanged desktop filesystem fixture passes one test. All 19
manifest projects compile for `browser-wasm`; Browser publishes with native
WASM and pinned Jolt inputs. The browser gate reports no warning or error
diagnostics. Logs use the `remote-serve-` prefix under the same run's `logs/`;
the fresh output is `remote-serve-browser-publish/wwwroot/`. These checks do
not establish Windows startup, rendered browser parity, or post-push CI.
Shader sources and recipes did not change.

The committed-source scan reports 27 files / 93 direct-file matching lines in
the four roots. Core plus Rendering total 12 files / 34 lines. Blocking counts
are unchanged. Both broad inventory requirements remain open.

## Published archive probe placement (2026-10-29)

Implementation commit `1ac20558d3b8177afd11d1719f64e9d3bce6647b` moves
published archive existence probes to the host-file read capability. One provider
serves all three roots. Empty configuration needs no provider. A nonempty
configuration with no read capability now fails before any root is changed,
including when a supplied path is malformed. The
[composition contract](../../../architecture/runtime/project-organization.md)
records this custom-host requirement. Per-path invalid or inaccessible results
still clear that path. Archive opening and lifetime remain unchanged.

A disposable executable checks the compiled Core path: three root probes retain
one provider through replacement and preserve missing-root behavior; missing
capability leaves existing roots unchanged; empty configuration clears roots
without a provider. Independent source review found no concrete internal
regression. Core builds in Release with zero warnings and errors. Evidence is in
`Build/_AgentValidation/20261028-120000-webgpu-io/logs/archive-probe-build.log`,
`logs/archive-probe-smoke-build.log`, `logs/archive-probe-smoke.log`, and
`reports/archive-probe-inventory.json`.

The executable also exercises the save overwrite callback added in the preceding
batch. The callback replaces the global provider and denies overwrite. The
existing save path selects `item (2).asset` using its captured provider and
passes that path to an overridden serialization method. This checks the actual
callback and save-name flow; it does not test the physical serialization write.
No tracked tests changed.

The full local publication gate passes. Editor, Server, and VRClient Release
builds report zero warnings and errors with `EnableWindowsTargeting=true` on
Linux. The unchanged desktop filesystem fixture passes one test. All 19
manifest projects compile for `browser-wasm`; Browser publishes with native
WASM and pinned Jolt inputs. The browser gate reports no warning or error
diagnostics. Logs use the `archive-probe-` prefix under the same run's `logs/`;
the fresh output is `archive-probe-browser-publish/wwwroot/`. These checks do
not establish Windows startup, rendered browser parity, or post-push CI.
Shader sources and recipes did not change.

The committed-source scan reports 28 files / 97 direct-file matching lines in
the four roots. Core plus Rendering total 13 files / 38 lines. Blocking counts
are unchanged. Both broad inventory requirements remain open.

## Asset save-name probe placement (2026-10-29)

Implementation commit `4c469c75ab6f39ec123e993d09a92c05fbbdaf3e` moves
save collision and unique-name probes to `IRuntimeHostFileReadBackend`. The
save operation retains the captured provider across the overwrite callback and
all unique-name probes. Direct unique-name calls capture after host-file
admission. The [composition contract](../../../architecture/runtime/project-organization.md)
records the custom-host requirement and the limit of this capture.

A disposable executable against compiled Core verifies three cases: the unique
name advances past two existing files while retaining its provider through a
global replacement; an unused name returns unchanged; and an absent provider
fails explicitly. Core and the executable build in Release with zero warnings
and errors. Independent source review found no concrete regression and confirms
the callback order and retained provider. The original harness did not execute the callback path. The later
archive-probe executable above exercises it. No tracked tests changed. Evidence is in
`Build/_AgentValidation/20261028-120000-webgpu-io/logs/save-probe-build.log`,
`logs/save-probe-smoke-build.log`, `logs/save-probe-smoke.log`, and
`reports/save-probe-inventory.json`.

The full local publication gate passes. Editor, Server, and VRClient Release
builds report zero warnings and errors with `EnableWindowsTargeting=true` on
Linux. The unchanged desktop filesystem fixture passes one test. All 19
manifest projects compile for `browser-wasm`; Browser publishes with native
WASM and the pinned Jolt inputs. The browser gate reports no warning or error
diagnostics. Logs use the `save-probe-` prefix under the same run's `logs/`;
the fresh output is `save-probe-browser-publish/wwwroot/`. These checks do not
establish Windows startup, rendered browser parity, or post-push CI. Shader
sources and recipes did not change.

The immutable-source scan reports 29 files / 98 direct-file matching lines across
the four roots. Core plus Rendering have 14 files / 39 matching lines. The scan
removes three matching lines and one file. Blocking counts remain unchanged.
Both broad inventory requirements remain open.

## Texture cache inspection placement (2026-10-28)

Implementation commit `fb66b55ab29f882c4fe6d68a63680a679cd97c69` moves
all five physical file operations in `TextureStreamingCacheCodec` to existing
host capabilities. Binary marker reads capture `IRuntimeHostFileReadBackend`
after host-file admission. Recognized cache reads use that same object as
`IAssetMetadataFileBackend` for the timestamp. Source-path resolution captures
metadata after its blank-path and authored-asset exits, then retains it for the
existence probe and source timestamp. The
[composition contract](../../../architecture/runtime/project-organization.md)
records the custom-host capability requirement.

Marker streams retain `FileShare.ReadWrite` and deterministic disposal. Empty,
text, and whitespace markers remain misses. An I/O or access failure during
inspection remains a rejection, and direct loading claims the file to prevent
YAML fallback. Timestamp exceptions retain their caller-owned boundary. Invalid
shapes, stored source timestamps on write, and non-texture direct requests keep
their lazy exits. Payload mapping, nested texture preparation, cache writing,
and usability probes keep their existing owners. No payload format, cache key,
schema, dependency, tracked test, shader, native UI, or reference-runtime source
changed. Provider retention is not an atomic snapshot or retirement lease.

A disposable executable passes 29 checks against compiled Rendering:

- Valid binary cache reads preserve payload bytes, paths, and timestamps. Marker
  and timestamp calls retain one provider through replacement; marker streams
  keep their share mode and are disposed. Payload mapping is also disposed.
- Stale cache reads stop before mapping. Empty, text, tab, newline, and carriage
  return markers miss without timestamp access.
- I/O and access failures reject cache reads and keep direct files claimed;
  recognized corrupt payloads also remain claimed. Timestamp errors retain the
  existing exception boundary.
- Direct binary reads need no timestamp lookup. Wrong types, valid streaming
  shapes, blank paths, authored-asset paths, invalid write shapes, and stored
  source timestamps preserve their relevant provider-free exits.
- Write source probes and timestamps retain one provider through replacement,
  and output keeps the source timestamp. Missing sources stop resolution.
- Preparation retains its source provider before the existing missing
  render-asset-host rejection. This harness does not install the full render
  asset host. An initial broader expectation of successful source-image
  preparation failed at that existing boundary; the final check covers only
  provider retention and the unchanged rejection result.
- Text markers and direct binary reads accept a read-only capability. A
  recognized cache read rejects the missing metadata capability explicitly.
  Missing read providers fail explicitly, and non-synchronous sources reject
  before a host read.

The harness uses an injected file provider and a disposable memory-mapping
backend over real temporary files. It links the unchanged pinned Magick codec
for fixture creation; it does not qualify the Windows desktop mapping adapter,
GPU cooking, or full source-image preparation. Rendering and the final harness
build in Release with zero warnings and errors. Independent source review found
no regression. No tracked test or assertion changed. Evidence under
`Build/_AgentValidation/20261028-120000-webgpu-io/` includes
`logs/texture-codec-rendering-build.log`, `logs/texture-codec-smoke-build.log`,
`logs/texture-codec-smoke.log`, and `reports/texture-codec-inventory.json`.

The full local publication gate passes at this implementation source. Editor,
Server, and VRClient Release builds report zero warnings and errors with
`EnableWindowsTargeting=true` on Linux. The unchanged
`DesktopAssetFileSystemTests` fixture passes one test with zero failures,
warnings, or skips. All 19 portable manifest projects compile for `browser-wasm`.
Browser publishes into a fresh directory with `WasmBuildNative=true`, pinned
Jolt inputs, and unchanged interpreter mode. The browser gate reports no
warning or error diagnostics.

Evidence is in `logs/texture-codec-gate-Editor.log`,
`logs/texture-codec-gate-Server.log`, `logs/texture-codec-gate-VRClient.log`,
`logs/texture-codec-headless-build.log`, `logs/texture-codec-headless-test.log`,
`reports/texture-codec-host-TestResult.xml`, and
`logs/texture-codec-full-gate.log`. Fresh output is
`texture-codec-browser-publish/wwwroot/`.
`reports/texture-codec-browser-publish-hashes.json` records the native runtime,
Browser, and Rendering WASM sizes and SHA-256 hashes. This does not establish
Windows startup, rendered browser parity, GPU cooking, or post-push CI. Shader
sources and recipes did not change, so the previous production cook was not
repeated.

The repeatable committed-source scan reports 30 files / 101 direct-file matching
lines across the four original roots, and 15 files / 42 lines in Core and
Rendering. It removes four matching lines and one file. The fifth removed
physical operation used a target-typed `FileStream` constructor, which the
historical expression does not match. Blocking counts are unchanged. Both broad
inventory requirements remain open; the checklist stays 122/163 complete.

## Font atlas and cache file placement (2026-10-28)

Implementation commit `dc4d8ed39292bfb02c192ef5c7eaf2f8c9995933` moves
all direct file operations from `FontGlyphSet` to existing host capabilities.
Distance-field output loading retains one metadata provider for existence probes
and JSON text, then uses that same object as the read provider for atlas bytes.
Bitmap output loading uses the read provider. Recovery and Windows font probes
remain lazy; stale-cache deletion retains one metadata provider inside its
best-effort catch. The [composition contract](../../../architecture/runtime/project-organization.md)
records which capabilities a custom host must supply.

The glyph parser, atlas formats, mip generation, sampling, cooked serialization,
font generators, and cache schema are unchanged. The existing bitmap helper
signature and source-test call expression remain intact. Valid cooked layout
returns before provider lookup. Missing-layout recovery keeps its named
`Font.CookedLayoutMissing` rejection on non-synchronous sources. These captures
do not create an atomic snapshot or source-retirement lease.

A disposable executable passes 17 checks against compiled Rendering:

- MSDF and MTSDF metadata/image reads retain one provider through replacement;
  both preserve glyph metrics, distance parameters, atlas size, and texture format
  (six checks).
- Missing outputs retain the diagnostic reprobes on that provider, and missing
  atlas metadata leaves the texture uninitialized.
- Bitmap reads preserve alpha coverage, R8 format, mip levels, and sampling.
- Cache cleanup retains its provider through deletion; deletion errors remain
  best effort.
- Missing-layout recovery uses the read provider; valid cooked layout needs none.
- Non-synchronous missing layout keeps its named failure, and a missing bitmap
  read provider fails explicitly.
- The Windows fallback exits before provider lookup on this non-Windows host.

All nine unchanged `FontGlyphSetSerializationTests` pass through NUnitLite, with
zero failures, warnings, or skips. The harness links the existing fixture and
uses the public Data, Core, and Rendering serializer registrations. It links the
unchanged `MagickRuntimeImageCodec` and resource policy with the already-pinned
`Magick.NET-Q16-HDRI-x64` version `14.16.0`; no repository dependency or target
framework changed. The image-codec project targets Windows and cannot be a
reference from this `net10.0` Linux harness. An initial harness run also lacked
the native logging registration; the final harness installs that existing
backend before the checks. This evidence covers the linked codec and compiled
portable rendering paths, not the full Windows imaging application.

Rendering and the final harness build in Release with zero warnings and errors.
Independent source review passed. No tracked test or assertion changed. Evidence
uses `Build/_AgentValidation/20261028-120000-webgpu-io/`:
`logs/font-rendering-build.log`, `logs/font-smoke-build.log`,
`logs/font-smoke.log`, `reports/font-TestResult.xml`, and
`reports/font-inventory.json`. Actual Windows UI-font probing, native font
rasterization, browser rendering, and GPU text appearance were not exercised.

The full local publication gate passes at this implementation source. Editor,
Server, and VRClient Release builds report zero warnings and errors with
`EnableWindowsTargeting=true` on Linux. All 19 manifest projects compile for
`browser-wasm`. Browser publishes into a fresh directory with
`WasmBuildNative=true`, the pinned Jolt inputs, and the unchanged interpreter
mode. The browser gate reports no warning or error diagnostics. The unchanged
`DesktopAssetFileSystemTests` fixture also passes: one test, zero failures,
warnings, or skips.

Logs are `logs/font-gate-Editor.log`, `logs/font-gate-Server.log`,
`logs/font-gate-VRClient.log`, `logs/font-headless-build.log`,
`logs/font-headless-test.log`, and `logs/font-full-gate.log`. The fresh output is
`font-browser-publish/wwwroot/`; `reports/font-browser-publish-hashes.json`
records one native runtime, Browser, and Rendering WASM resource with sizes and
hashes. This does not establish Windows startup, GPU parity, or post-push CI.
No shader source or recipe changed, so the previous production cook was not
repeated.

The source scan removes nine matching lines and one file. Core and Rendering
now total 16 files / 46 direct-file matching lines. The four-root total is
31 files / 105 lines. Blocking counts are unchanged. Both inventory requirements
remain open; the checklist stays 122/163 complete, with 41 open requirements.

## Asset-load probe placement (2026-10-28)

Implementation commit `cdd62f785d060b6621748a54717e1d8421841a12` moves
eight direct existence checks from the native load APIs, core loader, and remote
ID loader to the existing `IRuntimeHostFileReadBackend`. Each public load retains
its preflight provider through remote download and job dispatch, then uses it
for the core-load probe. The local-only private core entry captures lazily after
cache and archive returns. ID loads capture at the first resolved-path probe.
No interface, serializer, dependency, scheduler, native UI, or reference-runtime
source changed.

The [composition contract](../../../architecture/runtime/project-organization.md)
records the custom-host impact. Native public loads now require the read
capability before their existing preflight, including when a later path-cache
or archive lookup would succeed. The order is unchanged: the preflight previously
called `File.Exists` directly. Catalog routes, non-synchronous path-cache hits,
empty IDs, and matching loaded IDs still return before provider capture.
Nested metadata and payload operations retain their separate owners. This is
probe ownership, not a graph-wide source lease or an atomic file snapshot.
Physical remote payload persistence and guarded synchronous waits remain open.

A disposable runtime executable passes 20 checks against compiled Core and the
native worker leaf. It uses the existing publisher file adapter, a recording
provider, and a controlled in-memory remote transport:

- Generic and type-based synchronous/asynchronous loads retain the provider
  when the first probe replaces the installed provider (four checks).
- Each corresponding path-cache hit performs only the existing preflight
  probe (four checks).
- Synchronous and asynchronous native worker dispatch retain that provider.
- A provider's missing-file result prevents physical source import.
- A missing native read backend fails explicitly.
- Empty and matching cached remote IDs need no provider.
- Non-synchronous cached loads need no provider; a missing asset rejects first.
- A remote ID response persists and loads through provider probes (two checks).
- Synchronous and asynchronous remote downloads retain the preflight provider
  across their wait/await (two checks).

Core and the final executable build in Release with zero warnings and errors.
No network server or socket is used. No tracked test or assertion changed.
Independent source review confirmed all core-load callers and the custom-host
requirement above. Evidence is under
`Build/_AgentValidation/20261028-120000-webgpu-io/`: `logs/load-core-build.log`,
`logs/load-smoke-build.log`, `logs/load-smoke.log`, and
`reports/load-inventory.json`. The initial disposable harness incorrectly used
`JobManager` in a `using` declaration and had an unused field warning. It now
calls `Shutdown()` and initializes that field; production source was unaffected.

The full local publication gate passes at this implementation source. Editor,
Server, and VRClient Release builds report zero warnings and errors with
`EnableWindowsTargeting=true` on Linux. All 19 manifest projects compile for
`browser-wasm`. A fresh Browser publish passes with `WasmBuildNative=true`,
the pinned Jolt inputs, and the unchanged interpreter mode. The browser gate
reports no warning or error diagnostics. The unchanged
`DesktopAssetFileSystemTests` fixture passes: one test, zero failures, warnings,
or skips. No full asset, networking, or scheduler fixture run is claimed.

Logs are `logs/load-gate-Editor.log`, `logs/load-gate-Server.log`,
`logs/load-gate-VRClient.log`, `logs/load-headless-build.log`,
`logs/load-headless-test.log`, and `logs/load-full-gate.log`. The fresh output is
`load-browser-publish/wwwroot/`. `reports/load-browser-publish-hashes.json`
records one native runtime, Browser, and Core WASM resource with their sizes
and hashes. These results do not establish Windows startup, browser rendering,
GPU parity, or post-push CI. Shader source and recipes did not change.

The source scan now reports 17 Core/Rendering files with 55 direct-file matching
lines, and 32 files / 114 lines across the original four roots. This removes eight
lines and three files from the preceding cache snapshot. Blocking counts are
unchanged. The whole-inventory requirements remain open at 122/163 complete,
with 41 open requirements.

## Third-party cache metadata placement (2026-10-28)

Implementation commit `d4930493443b3740e54f172d7b7d28a363fd530d` moves
all direct file probes, timestamps, corrupt-cache deletion, and directory
creation out of `AssetManager.Loading.SerializationAndCache`. The existing
`IAssetMetadataFileBackend` owns these operations. No interface, dependency,
cache format, shader, native UI, or reference-runtime source changed.

Each load or authority operation captures one provider after host-file admission.
The same provider serves source, cache, and import-options metadata, including
cleanup and directory setup after asynchronous work. Deferred warmup captures a
provider when its job starts. Cache codec decisions, timestamp ordering, generic
corruption cleanup, and best-effort write catches retain their existing behavior.
Custom native hosts now need this capability for the affected cache entry points,
even for missing files. The [composition contract](../../../architecture/runtime/project-organization.md)
records that requirement. Payload readers, codecs, importers, and serialization
keep their own owners. This change does not create a graph-wide source lease or
an atomic file snapshot.

A disposable executable invokes the compiled Core cache methods with the existing
publisher file adapter and a recording metadata wrapper. Its 15 checks pass:

- Cold import and cache-directory creation.
- Generic, type-based, and asynchronous warm-cache hits.
- Source timestamp invalidation and reimport.
- Corrupt generic-cache deletion and reimport.
- Retained metadata ownership through synchronous and asynchronous provider replacement.
- Fresh-cache acceptance, newer import-options rejection, and missing-cache rejection.
- Best-effort directory failure with the imported asset returned.
- Explicit missing-provider failure and non-synchronous-source rejection.
- Cooked authority-path bypass without a provider.

Core and the disposable executable compile in Release with zero warnings and
errors. Independent source review found no missed helper caller or remaining
direct `File` or `Directory` operation in this file. No tracked test source or
assertion changed. Evidence uses
`Build/_AgentValidation/20261028-120000-webgpu-io/`: `logs/cache-core-build.log`,
`logs/cache-smoke-build.log`, `logs/cache-smoke.log`, and
`reports/cache-inventory.json`. An initial disposable harness compile used
`AssetManager` in a `using` declaration; the harness now calls its explicit
`Dispose()` method. That correction changed no production source.

The full local publication gate passes at that implementation source. Editor,
Server, and VRClient Release builds report zero warnings and errors, with
`EnableWindowsTargeting=true` on Linux. All 19 manifest projects compile for
`browser-wasm`; Browser publishes with `WasmBuildNative=true`, the pinned Jolt
managed sources and native archives, and the existing interpreter mode. The
browser gate reports no warning or error diagnostics. The unchanged
`DesktopAssetFileSystemTests` fixture passes through NUnitLite: one test, zero
failures, warnings, or skips. The full `AssetCacheTests` fixture was not run.

Logs are `logs/cache-gate-Editor.log`, `logs/cache-gate-Server.log`,
`logs/cache-gate-VRClient.log`, `logs/cache-headless-build.log`,
`logs/cache-headless-test.log`, and `logs/cache-full-gate.log` under the same run.
The fresh output is `cache-browser-publish/wwwroot/`; its native runtime, Browser,
and Core WASM sizes and hashes are in `reports/cache-browser-publish-hashes.json`.
The output has one resource for each of those identities. These checks do not
establish Windows startup, live browser rendering, GPU parity, or post-push CI.
No shader source changed, so the previous production cook was not repeated.

The immutable-source inventory removes 16 matching lines and one matched file.
Core plus Rendering now have 20 files / 63 matching lines; the four-root total
is 35 files / 122 lines. Blocking counts remain 108 files / 198 lines, with four
explicit thread constructors and 61 task/parallel/pool lines across 30 files.
These are lexical counts, not browser reachability or execution results.
UR02.03b and UR03.02b2 remain open; the checklist stays 122/163 with 41 open.

## World identity file placement (2026-10-28)

At implementation commit `9ccdbf7654bf320d1fa9c9de6d7c5d122664677e`,
`WorldAssetIdentityProvider` no longer performs physical existence checks,
timestamp reads, or file opens. It uses the existing metadata and read
capabilities of one lazily captured `IAssetFileSystem`. The
[composition contract](../../../architecture/runtime/project-organization.md)
describes the custom-native-host requirement. Desktop and smoke-publisher
adapters already implement both capabilities; no adapter API changed.

Verified package identity still returns first. Browser, non-synchronous source,
and runtime-catalog worlds with paths still reject an unverified identity before
provider lookup. Revision and hash overrides keep their order. Original
last-write metadata still precedes a file timestamp, and a missing file still
uses the existing generated fingerprint. Hashing uses a disposed
`FileShare.Read` stream. Provider replacement during a callback does not retarget
the current operation; this does not guarantee an atomic source-file snapshot
or cancel an identity when its provider is retired.

The repeatable [source inventory](browser-caller-blocking-inventory-2026-10-03.md#committed-source-refresh-2026-10-28)
finds four fewer direct-file matching lines and one fewer matched file. The
whole file-I/O and blocking requirements remain open. No thread implementation,
reference browser runtime, shader, cooking format, or native UI source changed.

### Bounded validation

On Debian 13 x64, the pinned SDK `10.0.401` and PowerShell `7.6.8` run the
serialized build wrapper from the existing environment checkout. Release builds
of Core, the shared WebGPU closure, the headless test executable, and a disposable
identity smoke executable pass with zero warnings and errors. The smoke invokes
the actual compiled Core method and the publisher's unchanged local file adapter.
All 13 checks pass:

- Native UTC timestamp and the known SHA-256 of the bytes `abc`.
- `FileShare.Read` and stream disposal after hashing.
- Identity source metadata and required build version.
- One retained provider when timestamp lookup replaces the installed provider.
- Explicit failure when a file-backed identity has no file provider.
- Revision and hash overrides without a file provider.
- Original timestamp precedence with a hash override and no provider.
- A pathless generated world without a provider.
- A missing source file with the unchanged generated fingerprint.
- Unverified path-backed rejection with a non-synchronous source.
- A pathless world with that non-synchronous source.
- Verified package identity without host-file capabilities.
- Isolation of the returned verified-identity metadata copy.

The unchanged `DesktopAssetFileSystemTests` fixture passes through NUnitLite:
one test, zero failures, warnings, or skips. No test fixture or assertion changed.
The smoke and logs are disposable evidence under
`Build/_AgentValidation/20261028-120000-webgpu-io/`:
`logs/core-build.log`, `logs/webgpu-build.log`, `logs/identity-smoke-build.log`,
`logs/identity-smoke.log`, `logs/headless-build.log`, `logs/headless-test.log`,
and `reports/TestResult.xml`. The scan report is
`reports/current-inventory.json`; the prior published scan is
`reports/baseline-inventory.json`.

These checks establish bounded managed identity behavior and source placement.
They do not establish Windows application startup, a rendered browser frame,
native graphics parity, or the full publication gate.

### Initial publication gate attempt

The four broader commands ran through the same build wrapper at the local
implementation source, in Release, with one task-owned artifacts directory:

| Target | Observed result |
| --- | --- |
| Editor | Failed: the pinned `OpenVR.NET` and `OscCore-NET9` submodule projects are absent from this checkout. Their missing types cause compilation errors. |
| Server | Failed with `NETSDK1100`: Windows targeting is required on this Linux host. |
| VRClient | Failed on the same missing OpenVR and Osc project/type prerequisites. |
| Browser publish with `XREngineJoltBrowser=true` | Failed in `JoltBrowserNative.targets`: the pinned browser Jolt native archives are missing. |

The logs are `logs/gate-Editor.log`, `logs/gate-Server.log`,
`logs/gate-VRClient.log`, and `logs/gate-Browser.log` under the run above.
At that attempt, `dotnet workload list` reported no installed workloads. The full 19-project
`browser-wasm` compile lane was not run with these missing prerequisites.
The source scan is not a substitute for that lane. No dependency pin, submodule
revision, browser supply path, or reference runtime changed to bypass a gate.
At this initial attempt, no change was pushed and the branch remained at
`ff0eabf95`. These were environment/build prerequisite failures, not approval
denials. The follow-up below records their recovery.

### Build prerequisite recovery (2026-10-28)

The follow-up prepared the missing inputs without changing repository pins or
submodule revisions:

- Used the workflow's `git submodule update --init --checkout` command for
  `Build/Submodules/OpenVR.NET` and `Build/Submodules/OscCore-NET9`. Their clean
  checkouts match the recorded gitlinks `e55a8cc0978ad65dfc2cc08d0a9ed9e86881ad35`
  and `2be09497ffa8560b700d4793c3e4d344c49caf49`.
- Installed `wasm-tools` from the repository root under the existing SDK and
  workload-set pins. The resulting WebAssembly packs are `10.0.12`, with
  Emscripten `3.1.56`.
- Prepared the Jolt managed sources with
  `Tools/Dependencies/Prepare-JoltBrowserManaged.ps1`. All checked source,
  license, and patch hashes passed.
- Downloaded local CMake `4.4.5` and Ninja `1.13.2` binaries from the official
  [CMake release](https://github.com/Kitware/CMake/releases/tag/v4.4.5) and
  [Ninja release](https://github.com/ninja-build/ninja/releases/tag/v1.13.2).
  Both downloads matched their published SHA-256 values. The local package
  metadata query and GitHub API metadata route were unavailable; the official
  release download route worked. No system package configuration changed.
- Ran `Tools/Dependencies/Build-JoltBrowser.ps1` with those tools and the pinned
  .NET Emscripten packs. Both native archives built successfully.

The downloaded CMake archive has SHA-256
`ed85135c5a5135b45e50423cb406dea04ca3338da0b8ef3c9ede963559680be0`;
the Ninja ZIP has
`5749cbc4e668273514150a80e387a957f933c6ed3f5f11e03fb30955e2bbead6`.
The resulting `libJolt.a` is 4,771,436 bytes, SHA-256
`b4bea8094ae79e882ef2c8c929aeff37cf175156cc25ce81f6bd4eeb33ed718f`.
`libjoltc.a` is 473,360 bytes, SHA-256
`2e516d4104c6bbc71a868f58a8e2dd23fa02b019b9ccffbf5179084983437ae8`.
These hashes identify this local build; they do not assert cross-host byte parity.
The source pins and supply checks remain in force.

The Editor, Server, and VRClient Release builds now pass with zero warnings and
errors. All three commands use the serialized build wrapper and
`-p:EnableWindowsTargeting=true` for Linux cross-compilation. No source, target
framework, or build-policy check was changed to get these results. This is
Windows-targeted compilation on Linux, not Windows application startup or
native GPU/XR qualification.

Evidence uses the same run: `logs/workload-install.log`,
`logs/jolt-managed-prepare.log`, `logs/jolt-native-build.log`,
`logs/prerequisite-Editor.log`, `logs/prerequisite-Server.log`,
`logs/prerequisite-VRClient.log`, and `reports/build-tool-provenance.json`.
The prepared managed sources and native archives are under `jolt/` in that run.

### Recovered build gate and shader cooking

All 19 projects in `Build/Portable/PortableProjects.tsv` compile for
`browser-wasm` in Release, with the original project list, full source policy,
and pinned Jolt source/archive properties. The run uses the same arguments as
`Tools/Test-PortableBrowserCompile.ps1`, routed through the serialized build
wrapper. The original helper invokes raw `dotnet`, so the disposable driver
wraps each project build instead. A fresh Browser publish with
`WasmBuildNative=true` also passes, including the native Jolt link and WASM
optimization. The log is `logs/browser-full-gate.log`.

The first Browser compile reported two existing `CS8601` warnings in
`EngineMeshDiagnosticFixture`. The guard already rejects missing raster entry
points, but nullable flow analysis does not preserve that result through the
fixture's mode fields. Implementation commit
`9cd52eb02ae2b7af144d3cc5df3ee69a64fe2fcc` captures both required entry-point
strings before constructing either shader. The original admission check remains;
no warning suppression or null-forgiving assignment was added. No unit-test
assertion, native UI, or frozen reference-runtime feature changed.

The affected Browser project was rebuilt and published after that source edit.
Both commands passed with no warnings or errors; see
`logs/browser-final-check.log`. The code files tested by these commands are the
files recorded in the implementation commit; committing them changed no source.
The other 18 manifest projects and all desktop build inputs were unchanged.
Together with the shared WebGPU and desktop results above, the local compilation
and publish gate is complete. This does not claim a post-publication CI result.

The offline shader check uses the workflow-pinned Slang `2026.8` Linux archive,
verified against SHA-256
`b23af8f2569e7961ca143c6ecd9abf6d7ac14a9bda739a1c6281694f774fa3eb`.
`ShaderCooker` builds in Release with zero warnings and errors. The cook uses the
same production recipe selection and source staging as
`Tools/Cook-EngineBrowserShaders.ps1`, but builds through the wrapper and executes
the built DLL. All 184 production recipes cook successfully; both probe recipes
are excluded. The manifest has 184 artifact entries and is 76,452 bytes, SHA-256
`af241f900eafa26616b68136efe9a1e8719579561b6c5d665cc31a791203ac0d`.
No shader source or recipe changed in this batch.

The cooker log is `logs/production-shader-cook.log`; output is
`production-shaders/manifest.json` under the same run. This establishes the
existing selected-source cook and ABI checks. Browser execution, rendered
known-value checks, Windows startup, and device performance remain separate.
No whole-inventory or shader-coverage checkbox is closed by these results.

The final Browser output is `browser-publish-clean/wwwroot/` under the run.
It was published from implementation commit `9cd52eb02` into an empty directory,
with no warning or error diagnostics. The earlier reused output retained an old
hashed managed resource, so it is not the final artifact. A packaging-only
`--no-build` attempt failed with `NETSDK1085` because this graph invokes project
builds during publishing; the normal publish command resolved this without any
build-target change. See `logs/browser-clean-output.log` for that failed attempt
and `logs/browser-clean-publish.log` for the successful command.
The clean output has exactly one native runtime WASM and one managed Browser
resource. Their sizes and SHA-256 values are in
`reports/browser-publish-hashes.json`.

## Archive staleness source reads (2026-10-08)

The source-file probes and byte reads in `AssetPacker.GetStalePaths` use one
lazily captured host-read provider. Native adapters call `File.ReadAllBytes`
directly. Archive validation, empty-archive behavior, content hashing, and the
missing-source result keep their existing order. Custom native hosts need the
read capability for nonempty source comparisons. Exact [042bfdd5](https://github.com/BlackJaxDev/XRENGINE/commit/042bfdd523a3ef17233d83d8b40bd0aa05e1d79d)
[run 37844731874](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37844731874)
passes all 103 selected tests, including the unchanged pack/repack/compact/stale
round trip. Its test artifact `11583440050` is 23,375 bytes with SHA-256
`cdac79c0b9838f60bf459bc22c255c98e8c2f0c13880594ea4ebb3ff92d00d52`.
This qualifies the bounded native case, not every archive failure path or the
remaining whole-file I/O inventory.

## Authored YAML stream placement (2026-10-08)

The five direct YAML streams, two bounded header scans, and three prefab file
probes now use the optional host-file read provider described in
[project organization](../../../architecture/runtime/project-organization.md).
The shared code keeps parsing, admission, context scopes, and load ordering.
Each direct sequence captures its provider before queued work. Nested references
start separate operations; this does not add graph-wide ownership or source-epoch
cancellation. Native and smoke-publisher adapters preserve sharing, encoding,
lazy line enumeration, and stream disposal. Exact [0a58f739](https://github.com/BlackJaxDev/XRENGINE/commit/0a58f73950e48ad3b61c725ea06d87f568833445)
[run 37844113118](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37844113118)
passed all 102 selected tests, including both unchanged partial-load and
reference-hydration prefab cases. Test artifact `11579824339` is 22,736 bytes
with SHA-256 `d22ba0791a6b1c72aeeb323449dc2494bd37cce4f746a323b3c8470d36098c68`.
This is bounded native YAML-read evidence; it does not close the full I/O
inventory or establish browser source-replacement acceptance.

## Project and settings file placement (2026-10-08)

Shared project and settings code now captures the installed
`IAssetMetadataFileBackend` before file existence checks, directory creation,
quarantine moves, and project descriptor text reads and writes. The desktop
provider and the smoke publisher's local provider own those file operations.
`XRProject` retains its raw-read lease and source check for third-party project
loads. Each direct multi-step operation uses one captured provider. The
existing path guards, settings serialization, save order, and catch boundaries
remain in place.

The desktop platform registration used by Editor, Bootstrap, and tests, and
the smoke publisher's local registration, install this capability. Standalone
or custom native hosts must install an `IAssetFileSystem` that also implements
`IAssetMetadataFileBackend` before they use project or settings file operations.
These methods no longer provide an
implicit `System.IO` path for the moved operations. A missing installation or
capability reports an explicit error. Browser and caller-thread host-file
admission still fails before provider lookup.

The placement was checked at [b2a25ba19](https://github.com/BlackJaxDev/XRENGINE/commit/b2a25ba19b68d47a4f5776760ea45297b30077f5)
in [run 37830083935](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37830083935).
The native test artifact `11573209733` records 96 passed tests, with no failed or
skipped tests. This includes project creation, game settings persistence, and
engine defaults persistence. Its 21,361-byte ZIP has SHA-256
`7d5461eb2f9770b6d584dc00e2c9cc6460b29e570b610971a243f689f8427911`.
The portable build/browser qualification and Windows Editor publication jobs
passed. The complete run had seven passed jobs and two failed jobs: the existing
UI frame-progress failure and unsplit native-shadow compile-wait timeout. This evidence does not qualify
browser host-file access or close the broader file-operation inventory.

## Shader source file providers (2026-10-07)

The shared resolver and Uber source cache now use captured shader-file and asset
discovery providers instead of direct physical file operations. The desktop
shader adapter retains the existing file-existence, UTC metadata, UTF-8/BOM,
and directory timestamp behavior. The standalone browser smoke publisher has a
local adapter and one startup registration. Its inputs, dependencies, and test
assertions are unchanged. External implementations of
`IShaderSourceFileBackend` must add the new synchronous operations and rebuild.
Standalone file-backed consumers must install both providers. The
[project organization contract](../../../architecture/runtime/project-organization.md)
describes this composition requirement.

Provider identity and installation generations now own resolver, XRShader, and
Uber cache entries, including empty and negative entries. Every XRShader cache
keeps its own file and directory dependencies. Cache validation runs provider
callbacks outside cache locks, then rechecks the captured entry and source key
before returning it. The UI parser uses the path of the resolved payload.
Empty source returns before search-root discovery. Existing browser, catalog,
and caller-thread admission remains in force; registered in-memory snippets do
not require a file provider.

Independent source and callback-lifetime review passed. Release builds of
Rendering, Host, Desktop platform, and the standalone publisher passed with zero
warnings and errors. The normal Windows workflow now selects the existing
resolver caching, dependency hot-reload, and Vulkan preprocess parity fixtures
without changing their assertions. At commit
`2b11b0c2fff378a7df0d1b2079ccab458ef226ee`, the Windows job in
[run 37574310936](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37574310936)
passed all 38 selected tests without failures or skips. These include ten
resolver-cache tests, eleven dependency/hot-reload cases, and two Vulkan
preprocess parity tests. Artifact `11462409168` is 6,790 bytes with SHA-256
`78c5dad12e0857188576f318f007e92bb39c501a967d0e66ddaca8131ab52cab`;
the downloaded TRX contains all 23 passed shader cases. This run precedes the
native metadata extraction described next.
No live shader reload, browser file access, or runtime performance result is
claimed. No requirement state changed.

The following native-frontend extraction removes the remaining metadata read
from `XRShader.Frontend.cs`. Its only caller is the Vulkan Slang compiler. That
caller captures the file backend and installation generation before compilation,
then normalizes each dependency path and requests its UTC timestamp and length
through the captured provider. Missing metadata fails instead of registering
zero values. A source revision or provider change observed after the reads
rejects the request. Shared registration consumes these neutral records and the
compile request's original source path and immutable include roots. It still
updates the index for an empty dependency list. No provider callback runs under
the index lock. The existing gap between final admission and index replacement
is not a strict atomic retirement guarantee. The broader physical-I/O inventory
remains open. Vulkan and Host Release builds pass with zero warnings and errors;
independent source review passes. These checks do not execute a native shader
reload or qualify a rendered frame.

## Buffer spill host storage (2026-10-07)

Buffer spill file operations and memory-mapped view ownership now belong to the
desktop and browser platform leaves. Shared rendering retains eligibility,
upload and write revision checks, the weak pending queue, settle and grace
periods, the `DataSource` facade, and best-effort failure handling. It captures
one installed provider per drain attempt. Provider replacement does not close
an existing mapping. The existing `Map(FileStream, uint)` test entry point
delegates to that provider; no test assertions or fixture setup changed.

Desktop bootstrap and browser renderer composition register stable providers.
Browser registration occurs once, so later canvas creation does not overwrite
an installed replacement or explicit uninstallation. A provider still uses
the temporary-directory fallback when the asset host is absent. Standalone
Rendering-only consumers must now register a spill provider. Without one, the
existing failure path records the missing capability and retains CPU storage.

The browser provider attempts the same copy-on-write mapping operation. The
[memory ownership contract](../../../architecture/rendering/cpu-memory-ownership.md)
links exact upstream .NET and Emscripten sources and explains why a MEMFS spill
can add JS and WASM copies. Source support does not establish installed-pack
byte identity, actual browser spill success, or memory savings.

Release builds of Host, WebGPU, Desktop platform, and Browser platform pass
with zero warnings and errors. Independent source/lifetime review passes.
The six unchanged `XRBufferClientSpillTests` passed in the Windows job of
[run 37566337192](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37566337192)
at commit `684585eb33cdda9513c99213306f743d5f2ddb90`. The complete filtered TRX
has 15 passed tests and no failures or skips. Artifact `11459706677` has
SHA-256 `b8610c11f7911090cf9075d89078a7370f305ad4cf7106b5eb737ff02878fecc`.
These tests cover private clones, explicit disposal, source retention during a
spill read, write-race rejection, scoped writers, and dynamic-buffer exclusion.
They do not establish browser mapping success, timer behavior, finalizer
execution, or memory reduction. Shared timer placement and the broader
physical-I/O inventory remain open. No requirement state changed.

The browser world loader already uses the asset manager's asynchronous catalog path for the startup world, settings, default font, essential roots and streamed scenes. The catalog loader resolves dependency closures before a bounded deserialize/publication batch and owns the resulting objects until its source is unbound. Synchronous `AssetManager.Load` wrappers can return only an already cached catalog asset; unloaded packaged paths require `LoadAsync` or `LoadFromRuntimeSourceAsync`. The prefab loader follows that asynchronous catalog route. These facts narrow, but do not close, the remaining shared-code I/O inventory.

`WorldAssetIdentityProvider.Create` now returns a verified package identity before considering host files. A browser, non-synchronous asset source or runtime catalog cannot probe a world path through `File.Exists`, read its timestamp or hash it as a host file. A world with such a path but without a registered verified identity fails as `WorldAssetIdentity.UnverifiedPackagedWorld`. A genuinely in-memory world with no path keeps its existing generated local fingerprint. Desktop file-backed identities retain the same file hash, timestamp and source metadata behavior.

The optional realtime join handoff file is configuration outside the cooked asset catalog. If requested on a browser, non-synchronous source or catalog host, it fails as `RealtimeJoinHandoff.FileSourceUnavailable` before resolving or reading the path. Inline JSON handoff remains available on those hosts. Desktop handoff-file reading is unchanged. This boundary does not imply browser support for every realtime transport.

`ShaderHelper.LoadEngineShader` now consults the installed shader service's synchronous-work capability before starting an asset task or waiting for a pending one. On a caller-thread, browser, runtime-catalog or non-synchronous asset-source host, a completed helper task still returns its shader (or its original fault/cancellation), and an already published engine shader can be taken directly from the asset manager's cache. The cache-only lookup checks catalog membership, source teardown, destroyed state and type. An unloaded or pending shader without that published asset fails as `ShaderLoad.AsyncRequired`; callers must preload it with `LoadEngineShaderAsync`. The engine service reports this capability from its captured asset manager's bound source and execution mode. Desktop hosts with synchronous asset work keep their existing inline render-thread load and off-thread wait behavior.

Shader load and preload no longer warm GLSL source through `ShaderSourceResolver` on a host without synchronous shader work. That warm step can probe and read host files for includes and snippets even after the root asset arrives asynchronously. The async asset load and cached cooked shader remain usable. Helper task caches exchange when the shader service or asset-source epoch changes, retiring old cache graphs. A task retains its original service and asset manager. If its source epoch has changed or its result is destroyed, it fails as `ShaderLoad.StaleAssetOwner` before shader configuration. Direct source-resolution boundaries are described below.

Direct GLSL source resolution now checks the installed shader service, installed asset source, caller-thread execution, and browser platform before using host files. This uses the actual execution and source capability, so an Editor cook targeting WebGPU still resolves trusted source files when its desktop service permits synchronous work. A nonblocking host may resolve plain source text and programmatically registered snippets entirely in memory. A `#include` fails as `ShaderSource.HostFileIncludeUnavailable`, and a snippet not registered in memory fails as `ShaderSource.HostFileSnippetUnavailable`, before shader root discovery, file lookup, directory indexing, or file timestamp reads. Canonical snippet expansion still uses only its explicit dependency dictionary; cooked artifact selection still uses the serialized identity and artifact resolver. Neither path performs runtime GLSL compilation.

The source-resolution call-site inventory is: `ShaderSourcePreprocessor.ResolveSource` (including Vulkan compiler use), `XRShader.TryGetResolvedShaderSource` and its resolved/optimized/UI getters, `UberShaderVariantBuilder.ResolveShaderSourceCached`, and the `ShaderSnippets.TryGet`, `GetAllNames`, and `ResolveSnippets` facade methods. The two facade paths skip host-root discovery when file access is unavailable. The resolver's include, snippet, root-index and cache-validation branches are guarded. File-dependent cache entries are invalid on nonblocking hosts without probing their dependencies. XRShader source, optimized-source and UI cache hits, and the Uber resolved-source cache, require the same host-file capability and registered-snippet version; a desktop expansion cannot cross to a runtime catalog owner or survive a changed registered snippet. On these hosts the `Try` source API reports failure, while `GetResolvedSource`/`GetResolvedShaderSource` surface the named error instead of returning unresolved text. `GetOptimizedSource` and `GetUiManifest` likewise reject failed source resolution. The Uber variant builder rejects failed nonblocking resolution before constructing a variant from raw source. Desktop file-backed expansion and dependency refresh are unchanged.

Direct `XRShader.Reload`, `Load3rdParty` and `Load3rdPartyAsync` now check the same host-file capability before changing the shader frontend or constructing a file-backed source. A nonblocking catalog host receives `ShaderSource.HostFileImportUnavailable`; it must preload a cooked artifact through its asset owner. The asynchronous import entry point is still a host-file importer, not an asset-catalog loader. Desktop source import and reload keep their existing implementation.

Shader file notifications do not enqueue host-file work when that capability is unavailable. Each subscription captures its shader-service identity and installation generation, so a callback already copied by a detached source cannot borrow a replacement owner. A queued notification also retains the asset-source epoch. Those values are checked after debounce, before each file-stabilization attempt, before collecting invalidations and again when a deferred frame-swap publication runs. Replacing the owner, changing its source epoch or temporarily replacing and reinstalling the same service cannot revive old file work. Explicit in-memory snippet changes retain their existing invalidation path.

File-change publication admits each shader separately. Capability and asset-epoch getters run outside the installation gate; only the final service identity and installation-generation check shares the gate with replacement. This is the linearization point for one synchronous notification. A getter that replaces the service cannot admit stale work, and replacement from a shader callback prevents the next shader in the same job from being admitted. An already admitted notification completes its existing cooked-artifact, property-veto, cache and source-event sequence after the gate is released, using its captured owner for logging. No callback, shader cache lock, file access or wait is added to that gate. Retirement therefore rejects notifications not yet admitted; it does not promise that all physical writes or callbacks stop when the service setter returns. The asset epoch remains an admission-time snapshot because its owner changes that epoch independently of the shader installation gate. Strict atomicity with asset-source retirement would require a separate shared ownership contract.

The later shared-program convenience review below covers `XRRenderProgramDescriptor`, program interface inspection and the generated GPU vertex identity; other material/source-generation families still need their own runtime reachability checks. OpenGL and Vulkan compiler paths are desktop backend paths; this source boundary does not make them browser compilers. This work does not close the broader runtime I/O inventory.

## Focused validation

- Runtime.Core Release build through the shared narrow-build script: zero warnings and errors.
- An ignored probe called the production identity and handoff methods with an installed non-synchronous `IRuntimeAssetSource`. It verified named rejection without source reads, generated in-memory identity, verified package identity cloning, inline JSON handoff, and the unchanged desktop file hash and handoff-file behavior.
- Runtime.Host Release narrow build: zero warnings and errors. An ignored shader-boundary probe called production `ShaderHelper` with a controlled nonblocking shader service and verified unloaded/pending rejection without starting or waiting for asset work, published and completed cache hits, original fault/cancellation, async preload, destroyed results both before and after task completion, skipped host-file source warm, service replacement and catalog-epoch retirement. The probe did not instantiate the browser host or a live runtime catalog.
- A separate ignored source-resolution probe called production `XRShader`, `ShaderSourcePreprocessor`, `ShaderSnippets`, and the Uber builder's resolved-source method. It verified desktop file include and disk snippet expansion; rejection of cached desktop expansions, direct file includes and missing file-backed snippets with a nonblocking shader service; plain in-memory source and registered snippet expansion; and registered-snippet replacement across XRShader and Uber cache hits. It used a controlled service and host file-discovery adapter, not the browser host or a live runtime catalog.
- The extended source probe and its shared Rendering dependency build pass with zero warnings or errors. Direct synchronous/asynchronous import and reload reject before changing the shader's type or source on a nonblocking host. Current desktop synchronous/asynchronous imports and file-change invalidation still work. Pending notifications retire across service replacement, replacement followed by reinstalling the original service, and asset-source epoch changes. The actual deferred-publication helper also rejects an expired owner before changing shader revisions. These are managed production-method checks; they do not claim a browser file watcher or a rendered shader reload.
- An independent production-method reproduction exposed two publication races: replacing the owner from the first shader's callback still invalidated a second shader, and replacement from the last cache-epoch getter admitted one stale shader. After per-shader admission, the same cases report one completed notification with no second-shader revision change, and zero notifications with no stale revision change, respectively. An expanded ignored owner probe also verifies final capability/epoch getter retirement, concurrent replacement, ABA rejection, unchanged cooked-artifact property ordering and both property veto paths, unchanged suppressed notifications, reentrant publication, captured logging, and completion of a concurrent setter from a property callback without the installation gate being held. The complete source-I/O probe still passes; its Rendering build and the owner-probe build both report zero warnings and errors. This validates the admitted-operation contract above, not a strict atomic boundary with independent asset-epoch retirement.
- `git diff --check` passed for the identity/handoff and shader-boundary production edits. The staged source set was not changed.

This local capability witness does not execute the browser host. Later slices reviewed and guarded direct GLSL source/snippet resolution and shader convenience calls (above), texture import and residency (below), and network host-file transfer (below). The [asset-manager boundary](browser-asset-manager-source-boundaries-2026-10-03.md) guards constructor/load/cache/metadata/watcher and remote-response paths; the [lower raw-I/O boundary](browser-lower-asset-source-boundary-2026-10-03.md) guards base asset wrappers and adds asynchronous `TextFile` reads; the [remaining asset wrappers](browser-runtime-asset-wrapper-boundaries-2026-10-03.md) and [runtime I/O admission group](browser-runtime-io-admission-2026-10-04.md) cover their named scene/project/serialization, Gaussian/DDGI, font, diagnostic, archive and native-TLS routes. The [caller blocking inventory](browser-caller-blocking-inventory-2026-10-03.md) classifies named transform and capture waits. Pure path manipulation, cooked/editor import and metadata operations, and already cache-only synchronous references are outside a browser source-bypass claim.

In the current browser path, an unloaded packaged identity enters `AssetManager.LoadFromRuntimeSourceAsync`; synchronous `Load` can retrieve only a published cached catalog object. Direct noncatalog `Load` and `LoadAsync` fail host-file admission before probing or scheduling work (`Assets/Loading/AssetManager.Loading.Api.Core.cs`); synchronous remote overloads reject caller execution before `GetResult` (`Assets/Loading/AssetManager.Loading.Remote.Api.cs`). `XRAsset`'s base async import/reload wrappers still delegate to admitted desktop host operations, and its serialization/file-replace retry remains host-file gated (`XREngine.Data/Core/Assets/XRAsset.cs`). These methods remain compiled in portable projects. The reviewed wrappers do not form a generic async importer for arbitrary raw files: a browser asset must have a registered cooked catalog target, or a feature-specific captured async reader such as Gaussian/DDGI. That is a deliberate delivery boundary, not an uncovered requirement to fetch arbitrary host paths.

No new browser-reachable synchronous file read or sync-over-async wait was found in the named asset paths in this targeted source review. `UR03.02b` remains open under its literal removal wording because synchronous desktop APIs and direct file operations still occupy shared runtime assemblies behind admission; a decision to physically separate or remove them is distinct from proving Browser call-path safety. This record does not establish a complete source audit of every shared method, browser page playback or live feature output.

## Shared shader convenience boundaries

`XRRenderProgramDescriptor.FromShaders` is used by `HybridRenderingManager` for a combined GPU-driven program. It now hashes optimized, resolved source when source inspection is available, or uses an explicit stage cooked identity on a nonblocking host. A failed source resolution can no longer turn raw include/snippet text into a valid program identity. The manager's generated vertex identity likewise rejects failed source resolution. Desktop/Editor file-backed shader resolution remains available when its installed service allows synchronous host-file work.

`XRRenderProgram.Link`, uniform/texture binding inspection, engine-uniform requirement inspection, and `HasUniform` use the exact resolved WebGPU artifact's physical resource ABI on a nonblocking host. A declared cooked identity without its artifact fails as `ShaderSource.CookedInterfaceUnavailable` instead of reporting an empty or raw-source interface. An in-memory shader with no cooked identity still uses source inspection and the direct source boundary above. Program interface caching tracks the host-file capability and the current cooked resolver, and a changed program cooked identity or companion dirties the cache. Stage-identity changes already invalidate it through the shader subscription.

An ignored production-method source probe now covers plain in-memory descriptor/interface inspection, named rejection of unresolved includes and absent cooked artifacts, physical-ABI binding selection over conflicting authored source, program-identity changes, and retirement of a resolver-backed interface when its owner disappears. Its synthetic ABI checks the shared mapping and ownership boundary; the separate verified-artifact admission probes cover descriptor integrity and WebGPU pipeline admission.

## Host-file texture import boundary

Texture source import now consults the rendering asset owner's synchronous-work capability, caller-thread execution and installed asset-source capability before touching host files. The admission check covers the installed `RuntimeAssetReadServices.Source`, including Data-only providers that have no `DirectStorageIO.Source` wrapper. Direct 2D/array imports, asynchronous import wrappers, preview/load jobs, grid imports, path-based mip constructors and host streaming-cache entry points reject unavailable source work as `TextureSource.HostFileImportUnavailable`. The asynchronous wrappers check before creating their background operation; source readers also check when deferred residency work executes. This keeps the existing desktop import/cache path available without exposing it as a browser catalog loader.

Automatic imported streaming restoration now skips retained `OriginalPath` values on a nonblocking/catalog host. Material binding therefore continues to consume its existing cooked or resident image instead of resolving a desktop source, probing its timestamp or substituting filler because that file is absent. Explicit imported-streaming registration requires host-file capability. The resident-data reuse cache skips host metadata probes when that capability is unavailable. The procedural filler image uses its in-memory pixels on those hosts; byte-decoded images and cooked serialization/hydration stay on their existing paths. No texture storage bytes, sampler defaults or import metadata format changed.

The direct encoded-image reads for preview, fallback art, explicit mip paths, array frames, grid frames, and third-party streaming residency now use `RuntimeAssetReadLease` after host-file admission. A read keeps its installed source through byte loading and checks that source before it publishes decoded data. Explicit mip paths use one lease for the whole set. A retired read cancels instead of publishing a partial texture or successful placeholder. `XREngine.Data/Core/Files/RuntimeAssetReadLease.cs` still uses physical `File.ReadAllBytes` when no source is installed; an installed native source retains its read route. Other texture cache reads and file probes retain their existing owners; this change does not move all physical I/O out of shared code.

`AssetManager.LoadAsync` already delegates catalog identities to `LoadFromRuntimeSourceAsync`, which reads and publishes the cooked texture through the captured asset owner. These frontend source import methods do not replace that route. This slice does not implement asynchronous host-file residency on a caller-thread host or migrate omitted raw-image metadata. Other asset-manager import/cache entry points and their broader ownership inventory remain open.

Before this source-lease change, the focused Rendering and Host builds passed with zero warnings and errors. An ignored production-method probe passed 35 checks and verified named rejection before texture path/mipmap mutation or job scheduling, absence of source reads, preservation of loaded bytes, skipped automatic source restoration, in-memory decoding and filler generation, host capability rejection, unchanged desktop synchronous/asynchronous/grid/mipmap imports, synchronous catalog rejection and exact object/render-registry preservation after a denied path constructor. Those checks did not execute a browser texture streaming session and do not validate the new source-lease change.

The source-lease change was checked at [131bc474](https://github.com/BlackJaxDev/XRENGINE/commit/131bc47470f757e1f66618a13cc77d7652e55075)
in [run 37832524898](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37832524898).
Test artifact `11576302041` records 97 passed tests, with no failed or skipped
tests. This includes the existing unreadable-cache-to-original-texture fallback
case. Its 21,525-byte ZIP has SHA-256
`8d36da02ac8733749db22c862ea270b771d9181d1cb2fe54c245ed577b7c01aa`.
Both build/publication jobs and five browser game jobs passed. The existing UI
frame-progress check and unsplit native-shadow compile-wait check failed.
This is bounded native fallback coverage, not a new source-retirement test or
browser texture-streaming acceptance.

Independent texture-boundary review found two inherited wrappers that scheduled a background job before source admission: `Import3rdPartyAsync` and `ReloadAsync(string)`. Both 2D and array textures now check the capability before delegating; the final 35-check probe covers those calls. The reviewed delayed source readers recheck at residency/preview entry. The final Rendering/probe build reports zero warnings and errors.

The same inherited async-entry review also covers `XRShader` context-aware loading, import and reload. Those wrappers now perform source admission before delegating to the existing desktop background implementation. Stage cooked-companion changes retain the existing `SourceChanged` invalidation semantics, including retirement of an earlier program companion. The expanded production probe checks replacement, missing-stage identity, all inherited async admissions and resolver retirement. Its unique output assembly is `ShaderSourceBoundaryProbe`; historical generic `Probe` outputs are not used for this final run.

## Diagnostic capture file output

Rendering now encodes diagnostic PNG data and uses an optional host file output service for physical writes. The desktop backend installs the service during bootstrap, including headless bootstrap. `VPRC_CaptureFrame` checks host-file admission and selects the writer before synchronous readback when output is due. The desktop writer resolves the full path, creates its parent directory, writes the PNG, reopens that file for SHA-256, and writes indented metrics JSON after the metrics fields are set. Pipeline texture and framebuffer exports check their renderer capability first, then check host-file admission and select the writer before requesting asynchronous capture. The callbacks keep their existing naming, PNG encoding, image disposal, and timing. The desktop writer checks host-file admission when each output method runs, including after an asynchronous callback, so a retained desktop service rejects writes admitted after host-file access becomes unavailable. A synchronous write that was already admitted can finish after a concurrent source change. Browser hosts have no installed writer. This bounded change does not close UR02.03b or UR03.02b.

The synchronous host-file admission helper now retains the source binding's
reader count without creating a read lease or linked cancellation source.
It checks cancellation and source identity before and after the capability
predicate, then releases the reader in `finally`. Actual read leases and
publication reservations are unchanged. This removes new per-capture admission
allocations; diagnostic image encoding and metrics still allocate their output.

Independent source review passed for the output boundary and the admission
helper. A Release build of the desktop platform leaf and its shared dependencies
passed with zero warnings and zero errors. No capture-output runtime check or
new test was run for this move.

Speed profile file output now uses an optional capability on the same registered diagnostic capture file output service. `Engine.ProfileCapture` keeps the host-file guard, `Debug.EnsureLogRunDirectory`, path naming, timestamp generation, label cleaning, JSON payloads, and sample timing. It asks the desktop platform leaf to create the profile root before it generates the timestamp, create the capture directory, apply retention, and write text. Retention still keeps three directories by descending UTC creation time. The desktop leaf normalizes each candidate path and checks that it is inside the profile root before deletion. Enumeration failure remains opportunistic; each deletion failure remains isolated. Text output still creates its directory before it joins the file name, then appends or replaces with `Encoding.UTF8`. Empty text still returns before file access. An unregistered standalone host or a custom diagnostic writer now needs this optional capability to start a capture. Its capture-start error follows the existing error path; later write failures remain swallowed. Host and Desktop platform Release builds pass with zero warnings and errors. Independent source review confirms the retention, encoding, ordering, and ownership contracts. This is source ownership only. No capture, profiler, or trace was run for this move.

## Network host-file transfer boundary

`BaseNetworkingManager.SendFileAsync` and `ReceiveFileAsync` now reject operating-system paths before metadata, transport admission or file creation on browser, caller-thread, runtime-catalog and non-synchronous asset-source hosts. Both also recheck immediately before opening a file after an awaited transport operation. The diagnostic is `NetworkFileTransfer.HostFileUnavailable`; applications with an already opened stream retain the existing stream-transfer entry points when their installed transport supports them. The capability check does not add raw TCP support to the browser transport or change realtime WebSocket framing.

An ignored production-method probe passes 17 checks with controlled in-memory transports: non-synchronous and synchronous-catalog rejection before transport or destination access, caller-thread rejection, unchanged desktop file payloads, retirement while connect/accept is pending, and available preopened-stream transfers under a non-synchronous asset source. Core and the probe compile with zero warnings/errors. No sockets or real server are used. This is admission and resumed-file-open evidence, not atomic cancellation of file operations already admitted on a desktop owner; the broader runtime I/O and blocking-site inventory remains open.

## File mapping file access

Both `FileMap` facades keep host-file admission and mapping ownership in Data.
They now ask the installed `IFileMappingBackend` to open a requested file or a
temporary file. The desktop backend owns path probing, file creation, file
copying, and stream opening. It reports a fallback before it copies the file,
through each facade's existing warning channel. Mapping still uses the same
stream, offset, length, and protection. A mapping failure disposes a stream
opened by a facade. `FromStream` keeps the caller's stream ownership.

External `IFileMappingBackend` implementations must add `OpenFile` and
`OpenTemporaryFile`, then rebuild against this pre-v1 interface. No browser
backend is installed by this change. The wider runtime I/O inventory and
`UR03.02b` remain open.

Independent source review confirmed file options, fallback timing, warning
channels and stream ownership against the prior implementation. The desktop
platform leaf and shared dependencies built with zero warnings and zero errors.
No runtime mapping probe or new test was run for this move. External backend
implementations and browser execution remain outside this evidence.

## Archive metadata admission

`PublishedArchiveHandle.Open` and `CookedPayloadOwner.MapFile` now check the
existing host-file capability after argument validation and path normalization,
before `File.Exists` or `FileInfo` probes. This also guards the empty-file return
that previously did not enter `FileMap`. Browser, caller-thread, and non-host
asset-source callers fail before physical metadata access. The null/default
source on an ordinary desktop retains its existing behavior.

Missing, oversized, and empty files keep their existing permitted-host results.
Archive parsing, mapping ownership, disposal, public signatures, and serialized
bytes are unchanged. The successful guard creates no read lease or linked
cancellation source. It admits the entry only; it does not hold a publication
reservation across the later synchronous probes. Independent source review
passed. The physical operations remain in Data, so this correction does not
close the remaining physical I/O placement requirement.

The targeted Data Release build passed with zero warnings and zero errors.
No new test or runtime mapping probe was run for this admission correction.

The archive and cooked-payload metadata probes now use the captured
`IFileMappingBackend`. The desktop implementation keeps `File.Exists` behavior
for archive paths and `FileInfo.Exists` followed by `FileInfo.Length` behavior
for cooked payloads. Each caller validates its path and checks host-file access
before it captures the backend. It then uses that same backend for metadata,
file opening, and mapping. The `FileMap` facade checks host-file access again
before it opens the file. Missing archives and payloads, oversized payloads,
and empty payloads keep their existing results. An empty payload remains pooled
and does not open a mapping. Parsing, mapped storage, disposal, and serialized
bytes do not change when the desktop backend is installed. A host without an
installed mapping backend now gets the mapping-service error before any file
metadata result. This includes missing archives or payloads, the empty-payload
return, and the oversized-payload exception, because no backend exists to
perform those checks.

External pre-v1 `IFileMappingBackend` implementations must add `FileExists` and
`TryGetFileLength` and rebuild. `FileExists` must return the same result as
`File.Exists`. `TryGetFileLength` must perform the equivalent of creating a
`FileInfo`, checking `Exists`, and reading `Length` only when the file exists;
it returns `false` for a missing file. Existing `OpenFile` and `Map` behavior
remains required. This extraction moves the two physical metadata probes out
of Data. Other shared file-I/O paths and `UR03.02b` remain open.

The focused Data and Desktop platform Release builds passed with zero warnings
and zero errors. Both used the existing restored package assets. No runtime
mapping probe or new test was run for this extraction.

## HiZ diagnostic text output

HiZ stage summaries and crash breadcrumbs now use the installed desktop
diagnostic writer for directory creation and text appends. The shared caller
checks host-file admission, then captures one writer with the optional
`IRuntimeDiagnosticTextFileOutput` capability. The same instance serves path
setup and append even if the installed writer changes during that operation.
Desktop and headless bootstrap already install the implementing writer.
External capture writers need this optional capability only when these text
diagnostics are enabled. A missing writer or text capability fails explicitly
outside the existing best-effort file-operation catches.
Host-file-capable standalone hosts must now install that writer to use these
diagnostics; the previous implementation wrote directly without one.

The shared static path caches and their first-use current-directory selection
remain unchanged. Stage summaries attempt directory creation before formatting
on each flush; breadcrumbs attempt it only during their locked first path
initialization. File names, line text, feature flags, locks, catches, and stats
clearing keep their previous behavior. Desktop methods recheck host-file
admission immediately before physical operations. The existing capture
interface, scene/render work, and scheduling are unchanged. This extraction
does not close the broader physical I/O inventory.

The combined Desktop/shared Release build passed with zero warnings and zero
errors. No new test or live diagnostic-file capture was run for this move.

## Authored metadata file operations

`AssetManager.Metadata` and its metadata-ID fallback now use the optional
`IAssetMetadataFileBackend` capability on the installed `IAssetFileSystem`.
The Desktop provider and the smoke publisher's existing local adapter implement
the eleven file-operation and GUID-read members. The publisher keeps its
existing registration, inputs, project references, and target framework.

Core retains the metadata YAML serializer, GUID policy, path naming, importer
selection, locks, and best-effort pruning. Physical reads, writes, moves, copies,
deletes, timestamps, and directory operations move to the host providers. The
GUID scanner keeps its BOM-aware shared read and three attempts with 15 ms and
30 ms waits. `DirectoryInfo` in the sibling-root lookup only walks path names
and parents; the captured backend performs its directory-existence probe.

Each operation captures one discovery/backend pair. Each inner asset ensure
still checks host-file admission before its lock. Watcher source/epoch checks
and cached-ID publication checks remain. This does not create an atomic lease
against a provider or asset-source change during admitted file I/O. A custom
native provider must implement the capability for direct metadata maintenance;
an absent capability fails explicitly. Metadata-ID fallback keeps its warning
and false result. YAML parse errors retain their previous invalid-metadata
result, while an unsupported provider read escapes that parse fallback.

Independent source and ownership review passed. Core, Desktop platform, and the
standalone publisher Release builds passed with zero warnings and errors on
the pinned SDK. The CI filter adds the two existing metadata-sync cases, the
sibling-root ID lookup case, and `DesktopAssetFileSystemTests`; their assertions
and setup are unchanged. On exact commit
`3e3df765b8966a9679e4ef7b3225da494e873911`, this filter passed **80/80** in
[run 37588179202](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37588179202),
including all four newly selected existing cases. Artifact `11468536973` is
17,278 bytes with SHA-256
`ccd130ff7a244cd756240e9c241068f35fad8f7affb0053102286e90805944e0`.
GitHub artifact metadata identifies the source commit and run; the downloaded
ZIP matches that digest, and its TRX records zero failed, skipped, aborted, or
inconclusive cases. This verifies the selected native provider behavior. Browser
metadata support and the full runtime-I/O and blocking-site inventories remain
open.

The same run finished with seven successful jobs, including both build/publish
jobs and the five baseline game cases. UI still failed its animation-frame check.
Shadow ON exceeded the 45-second first-frame limit while
`engine-advanced-shade-native-depth-no-decals` remained pending for 38.2 seconds;
the recorded GPU resource list had no failure. Those browser checks remain open.

## GL submit trace file placement

Shared `GLSubmitTracer` now obtains an owned text log through the optional
`IRuntimeGLSubmitTraceFileOutput` capability on the installed diagnostic output
service. The existing Desktop provider creates the directory and persistent
file resources. The path remains `Build/Logs/gl-submit-trace.log` under the
current working directory. It keeps Create mode, write access, read sharing,
the 4096-byte WriteThrough stream, BOM-free UTF-8, AutoFlush, and LF lines.
The public levels, environment activation, headers, timestamps, and line format
remain in the shared tracer. No startup registration or fixture changes were
needed.

The tracer owns a returned lease before reading its writer. A failed open closes
partial resources, and the desktop lease attempts writer and stream disposal
separately. Close detaches shared ownership before disposal. Active logs retain
their owner when the installed provider changes. Missing capability leaves this
optional trace off; custom hosts must install it before first use or request the
level again afterward.

Provider calls run under the tracer's lock and must not wait for another thread
to enter it. Callback level changes wait until the active transition or trace
line finishes. The last requested level runs next, with a four-transition bound
per request that leaves tracing off if a provider continues changing it. Recursive trace
writes return immediately. These rules protect the new host callback boundary
without changing the ordinary Desktop writer's serialized path.

Independent source and lifetime review passed. Focused Rendering, Desktop, Host,
and OpenGL builds qualify the affected source closure; no trace was activated,
no output probe was run, and no test was added or changed. Actual log output,
flush behavior, and crash evidence remain untested for this move. The broader
runtime-I/O inventory remains open.

Exact commit `4422849bde5585b0ca30060134d4f06060af1b48` completed the normal
[build and browser run 37596449857](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37596449857)
with seven successful jobs and **80/80** existing regression cases passed.
Artifact `11472726000` is 17,380 bytes with SHA-256
`435168eaaa5728877ca423606c8524cbf4b724b0a7941be82ea380e3c93a73d0`.
GitHub artifact metadata identifies the commit and run; the downloaded ZIP
matches that digest. These cases do not exercise GL trace output.
UI still failed its animation-frame check,
and shadow ON exceeded 45 seconds with its native-depth-no-decals pipeline pending
for 37.4 seconds and no recorded GPU resource failure. GL trace output and the
two failing browser cases remain open, with no requirement-count change.
