# Runtime asset I/O boundaries in browser-capable code

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

Texture source import now consults the rendering asset owner's synchronous-work capability, caller-thread execution and installed asset-source capability before touching host files. Direct 2D/array imports, asynchronous import wrappers, preview/load jobs, grid imports, path-based mip constructors and host streaming-cache entry points reject unavailable source work as `TextureSource.HostFileImportUnavailable`. The asynchronous wrappers check before creating their background operation; source readers also check when deferred residency work executes. This keeps the existing desktop import/cache path available without exposing it as a browser catalog loader.

Automatic imported streaming restoration now skips retained `OriginalPath` values on a nonblocking/catalog host. Material binding therefore continues to consume its existing cooked or resident image instead of resolving a desktop source, probing its timestamp or substituting filler because that file is absent. Explicit imported-streaming registration requires host-file capability. The resident-data reuse cache skips host metadata probes when that capability is unavailable. The procedural filler image uses its in-memory pixels on those hosts; byte-decoded images and cooked serialization/hydration stay on their existing paths. No texture storage bytes, sampler defaults or import metadata format changed.

`AssetManager.LoadAsync` already delegates catalog identities to `LoadFromRuntimeSourceAsync`, which reads and publishes the cooked texture through the captured asset owner. These frontend source import methods do not replace that route. This slice does not implement asynchronous host-file residency on a caller-thread host, migrate omitted raw-image metadata, or guarantee atomic cancellation of a desktop import already admitted before service retirement. Other asset-manager import/cache entry points and their broader ownership inventory remain open.

The focused Rendering and Host builds pass with zero warnings and errors. An ignored production-method probe passes 35 checks and verifies named rejection before texture path/mipmap mutation or job scheduling, absence of source reads, preservation of loaded bytes, skipped automatic source restoration, in-memory decoding and filler generation, host capability rejection, unchanged desktop synchronous/asynchronous/grid/mipmap imports, synchronous catalog rejection and exact object/render-registry preservation after a denied path constructor. These checks do not execute a browser texture streaming session.

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
