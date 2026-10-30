# Unified Runtime Implementation Order

The user requested implementation before broad validation on 2026-10-29.
This plan covers the 41 requirements that were open at that point. It adds no
completion credit. The [requirement ledger](unified-runtime-requirement-ledger-2026-10-07.md)
owns the counts. Focused functional checks and the required publication build
gate still apply to each code change.

## Implementation Dependencies

| Requirements | Remaining work and dependency |
|---|---|
| UR02.03b, UR03.02b2 | Finish the blocking, thread, synchronous asset and physical file inventories. Move reachable operations to an asynchronous owner, native leaf, or cook/editor boundary. Reduced search counts and admission guards do not complete physical separation. Archive writing now belongs to Editor. Canonical recovery retains one source installation through publication; successful shared generations still need a safe retirement contract. Asset-save transactions now belong to the desktop output provider. The native execution leaf now owns the three engine loop threads. Continue with the remaining file and wait boundaries. |
| UR03.03 | Complete platform cooking, including texture variants, audio delivery and per-asset device requirements. ASTC/ETC2 production needs a reviewed encoder supply; no pinned encoder is currently available in this environment. Live streaming now preserves cooked sampling and retains raw-image target edits, with separate preview and fade state. |
| UR06.09f2 | The GPU LOD selector is connected to normal renderable registration. Native selected-variant coverage is preserved. The supported profile has one to four resident-table levels, equal primitive membership, admitted transforms/materials and coherent ownership. Do not remove rejection guards without the required producer data. |
| UR11.04 | Engine assets and the source sidecar are included in the packaged Editor. The native publisher host selects the sidecar and produces the game bundle without changing source inputs. Windows CLI and live Editor action acceptance remain open. |
| UR05.07 | Complete the remaining shader groups and fix known runtime defects. The saved UI player stall is fixed and its normal published bundle passes the unchanged software-browser harness. Finish coherent groups before extending narrow sky coverage. Record each group's known-value milestone before closing the whole requirement. |
| UR13.03 | Complete trimming, lazy managed assembly loading and streamed delivery. Measure total published transfer. Retain the approved interpreter until the runtime-mode decision changes. |
| UR15.05 | Finish user build, hosting, support and troubleshooting documentation when validated limits are known. |
| UR16.01–UR16.07 | Remove the separate runtime only after unified replacements reach parity. Preserve the reference components, content, pipeline, animation, exporter and harness until that dependency is met. |

The critical order is runtime boundaries, complete cooking and publishing,
remaining shader functionality, broad acceptance, then reference retirement.
Independent cooking, LOD and packaging changes can proceed in parallel. One
coordinator owns commits and publication.

## Deferred Validation And Owner Decision

These checks remain open. They must not become implementation completion claims.

| Requirements | Validation dependency |
|---|---|
| UR01.05, UR03.06 | Asset round trips, delivery, cache, cancellation and failure handling after the cook and I/O boundaries are complete. |
| UR02.06 | Desktop and VR frame timing after shared scheduling changes. |
| UR04.08, UR05.06, UR06.07 | Renderer contracts, coordinates and backend/pipeline comparisons. |
| UR06.05b, UR06.11d | Numeric deformation, then visibility, reconstruction and motion across submission modes. |
| UR06.11a2, UR06.11b2b | Pipeline/source/settings/output isolation and authored submission modes. |
| UR06.11c, UR06.11e, UR06.11f3c, UR06.12b | Material values, Advanced MSAA, custom sidecars and asynchronous capture/impostor lifetime. |
| UR07.03, UR07.05 | Physics parity and device timing. |
| UR08.03 | Cooked audio across browsers, including Safari. |
| UR09.06 | UI, input and accessibility after the player stall is fixed. |
| UR11.05 | Normally published worlds compared with desktop, including packaged CLI execution on Windows. Linux lacks the required Windows Desktop runtime. |
| UR12.03 | Production server, throttling, disconnect and application switching. |
| UR13.01, UR13.04, UR13.05 | Interpreter cost, steady-state allocations, memory and repeated load/unload. |
| UR14.01, UR15.04 | Recovery, lifecycle and physical devices. |

UR13.02 remains the owner decision for shipping AOT versus interpreter. It
depends on measured build time, download size, runtime cost and metadata/trimming
behavior. D11 keeps the interpreter until those measurements support a change.

The [platform validation view](../../testing/platform/platform-validation.md#unified-runtime-ur-verification-view)
retains the detailed acceptance conditions. No new dependency version, tracing
approval or reference-runtime retirement follows from this scheduling change.

## Current Boundary Evidence

Archive authoring now resides in Editor. The shared Data assembly keeps the
read API and archive format helpers. All 22 archive tests and the common-assets
cook-and-pack test passed against the compiled Editor implementation. The
archive format and the moved writer algorithms remain unchanged.

Live texture publication now carries cooked sampling through resident payloads
and cache reuse. Raw-image publication preserves target edits. The preview and
promotion-fade lifetimes remain separate. The 20 preview/publication tests and
50 related payload/cache tests passed.

Canonical shader recovery passed 42 focused source-owner and publication checks.
Successful shared generations still need borrower-aware retirement; the
[canonical investigation](../../investigations/rendering/2026-10-29-uber-canonical-source-owner.md)
records the measured retained objects and the remaining ownership gap. These
increments do not close the whole portable I/O or platform-cooking requirements.

The normal saved UI publication passes two fresh browser contexts with 72 and
14 checkpoints. This includes image, glyph, input, resize and lifecycle checks.
All 24 desktop/portable/native Browser publication gates passed with zero
compiler warnings and errors. Physical-device acceptance remains open.

Asset saves now capture the installed transaction output provider before YAML
serialization. The desktop leaf owns the adjacent temporary file, durable flush,
replacement retry and cleanup. Eleven focused checks cover creation, replacement,
serializer and replacement failures, provider capture and admission. The existing
animation asset save-and-reload test also passes through the real YAML serializer. The shared
asset type no longer creates files or sleeps during save. The larger physical
I/O inventory remains open.

The native execution leaf owns the update, visibility and fixed-update thread
handles, start order and bounded joins. The timer keeps callbacks and pacing.
Startup failure and shutdown timeout retain incomplete ownership; stop requests
during startup remain effective. All 27 focused timer tests pass. Physical
pacing waits remain in shared code. Later increments move the render worker domains.

YAML writing now captures the serialization owner for asset references and the
file owner for backing-text comparisons. Edited text remains inline when the
backing file is unreadable or unsupported. The generic converter preserves that
decision. All 28 write-boundary tests, 11 path-admission tests and five existing
material/reference tests pass. The 11 save tests also pass after integration.
The four-root lexical file scan now finds 14 lines in eight files, including
comments and working-directory lookups. The static Unity animation import still needs placement work. The later mapped
stream extraction removes native mapping creation from the shared asset type.

Raw reads without an installed asset source now capture the host file provider
for the lease lifetime. Missing synchronous or asynchronous read capabilities
fail explicitly. The desktop implementations retain cancellable asynchronous
reads. Provider replacement rejects stale completion; short publication
reservations prevent partial host installation. Installed raw sources retain
their independent ownership. The focused headless checks pass, along with all
22 existing scene, snapshot and metadata checks and five material/reference
checks. This change removes the three direct file calls from the shared raw-read
lease. It does not close the whole physical I/O inventory.

Asset mapped streaming now uses an owned desktop mapping lease. The public
stream API retains its whole-file read-write behavior. Open captures the backend
and source owner; failed or stale publication disposes the candidate. Unsupported
capabilities leave an existing view intact. Open and close reject overlapping
operations. All 16 focused mapping checks pass, together with 70 related raw-read,
YAML and save checks. This moves mapping creation and disposal into the desktop
leaf; it does not close the remaining physical I/O inventory.

The desktop window factory now supplies the native render-loop owner and the
secondary graphics owner. Each secondary run owns its queue, worker, hidden
window and renderer through final disposal. A bounded stop retains incomplete
ownership. Collapsed cleanup uses the captured render queue and checks the
native owner thread; replacement requires confirmed native backend release.
All 20 managed owner checks pass. Native GL transfer and teardown remain later
device checks. The repeated portable source scan reports 189 wait/result/join
lines in 103 files, one thread constructor, and 60 task/pool lines in 29 files.
These lexical counts do not establish complete browser reachability.

The normal animation importer now uses one captured read and discovery owner
for root and additive clips, headers and metadata. A deferred object-cache batch
prevents failed or retired imports from becoming discoverable. All 13 focused
owner checks pass. The separate static Unity importer still needs its generated
context repair.

The hydrated-world audio audit now checks the existing 8 MiB output budget for
active listener-relative static attachments. It counts each source buffer,
including repeated references to one clip, across visible scenes in that world.
All 29 focused audio checks pass. This bounds a known static residency case;
positional overlap, runtime changes, overlapping streamed worlds, encoded
delivery and segmentation remain open.

The compiled Editor audio audit and existing animation import and collapsed-loop
contract checks pass (14 checks). All 24 publication gates pass with zero
compiler warnings and errors. The separate Linux integration harness reports
nine platform warnings from Windows-annotated Editor and Vulkan APIs; all 14
checks execute and pass. This does not establish Windows native execution.

Default WebGPU now admits FXAA through an exact cooked raster program. The
selected resource generation owns its display-size RGBA8 texture, framebuffer
and quad. Tonemap writes the intermediate, FXAA writes the output, and overlays
and UI follow. None and x4 MSAA retain their direct tonemap routes. Eight
managed checks cover admission, inactive graph branches, pass dependencies,
format, display sizing and resize. The complete canonical cook produces 185
shader artifacts and 85 pipeline artifacts, including the unscoped FXAA pass.
The Default FXAA known-value browser milestone remains open.

A secondary worker now closes queue admission before failure diagnostics or
cleanup can block. It retains its running lifetime until cleanup finishes.
All 22 managed render-owner checks pass, including both initialization and job
failure during blocked cleanup.

The FXAA increment also passes all 24 publication gates with zero compiler
warnings and errors. The Editor package contains 1,335 engine assets and 5,636
publisher sidecar files, all verified against their recorded hashes.

The native execution leaf now owns render lanes, pooled batches, bounded queues,
calibration, cancellation and shutdown. Core retains the public facade, result
types, backend attachments and generation-checked lease contract. The scheduler
passes its captured factory; unsupported render capability fails explicitly.
All 25 existing scheduler checks and two capability checks pass, including
worker overlap, stale leases, shutdown retry and the unchanged zero-allocation
assertion. Fourteen timer and blocking-backend checks also pass. The current
portable scan finds 182 wait/result/join lines in 100 files, zero explicit thread
constructors and 60 task/pool lines in 29 files. These lexical counts do not
establish complete browser reachability.

The render-worker move passes all 24 publication gates with zero compiler
warnings and errors. The Editor package contains 1,335 engine assets and 5,633
publisher sidecar files, with every recorded hash verified.

Conformance manifest and mapping-correction reads now use captured host providers
for JSON, file probes, repository metadata and streamed SHA-256. Correction
application uses the already validated source hash. Conformance and pose-audit
publication reserve both the raw source and discovery installation, including
an installed host-capable source. Eleven loader checks and nineteen scanner
and pose-audit checks pass.

Audio2Face CSV reload now captures file and audio host installations, path and
project directory. A newer reload supersedes older results and diagnostics.
Adapter, rendering, application and browser composition reserve host changes
before mutation or teardown; publication rejection retains ownership for retry.
Ten CSV-owner checks, eight existing component checks and eight composition/profile
checks pass. The separate Linux integration harness has nineteen platform
annotation warnings from Windows-only bootstrap APIs; all 27 checks execute.
The headless build has zero warnings and errors and all 29 checks pass. External
mutation that restores a host project-directory property to its prior value
remains outside the installation signal. Broader browser and device lifecycle
acceptance stays deferred.

The conformance and audio increment passes all 24 publication gates with zero
compiler warnings and errors. The Editor package contains 1,335 engine assets
and 5,635 publisher sidecar files; all recorded hashes match. These changes do
not close the full portable file and blocking-operation requirement.
