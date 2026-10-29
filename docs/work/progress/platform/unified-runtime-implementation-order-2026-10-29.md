# Unified Runtime Implementation Order

The user requested implementation before broad validation on 2026-10-29.
This plan covers the 41 requirements that were open at that point. It adds no
completion credit. The [requirement ledger](unified-runtime-requirement-ledger-2026-10-07.md)
owns the counts. Focused functional checks and the required publication build
gate still apply to each code change.

## Implementation Dependencies

| Requirements | Remaining work and dependency |
|---|---|
| UR02.03b, UR03.02b2 | Finish the blocking, thread, synchronous asset and physical file inventories. Move reachable operations to an asynchronous owner, native leaf, or cook/editor boundary. Reduced search counts and admission guards do not complete physical separation. Preserve source lifetime and canonical shader recovery. |
| UR03.03 | Complete platform cooking, including texture variants, audio delivery and per-asset device requirements. ASTC/ETC2 production needs a reviewed encoder supply; no pinned encoder is currently available in this environment. Preserve authored sampling during streaming. |
| UR06.09f2 | Connect the existing GPU LOD selector to normal renderable registration. Preserve native selected-variant coverage. The supported profile has one to four resident-table levels, equal primitive membership, admitted transforms/materials and coherent ownership. Do not remove rejection guards without the required producer data. |
| UR11.04 | Include the engine assets in the packaged Editor. Keep the normal CLI and Editor action. Verify that the packaged publisher selects its sidecar build graph and produces the game bundle without changing source inputs. |
| UR05.07 | Complete the remaining shader groups and fix known runtime defects. The UI player stall is an unresolved defect. Finish coherent groups before extending narrow sky coverage. Record each group's known-value milestone before closing the whole requirement. |
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
