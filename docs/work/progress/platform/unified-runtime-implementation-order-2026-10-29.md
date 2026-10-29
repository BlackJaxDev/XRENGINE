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
pacing waits and other rendering worker domains remain in shared code.

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
