# Physics Chain Authoring And Runtime Modes

## Authoring model

Author physics chains through `PhysicsChainComponent`. At runtime the component
is a handle-bearing facade; `PhysicsChainWorld` owns templates, state, outputs,
commands, scheduling, and backend resources. Registration, removal, retemplate,
resize, collider rebind, and backend changes are structural operations applied
at the next world boundary. Ordinary roots, forces, relevance, and collider
poses are dynamic inputs applied before scheduling.

Chains with identical topology and solver values automatically share one
immutable template. Avoid harmless per-instance value differences when chains
are intended to batch: topology, segment count, optional features, collider
class, and quality mode determine kernel buckets.

## Shared collider sets

Use the same authored collider shape stream for chains that interact with the
same environment. Shape topology is immutable, content-deduplicated, and
referenced by stable set ID. Collider poses are a separate dynamic stream, so
moving a collider does not rebuild or reupload its shape.

- Zero through four colliders use specialized direct paths.
- Larger sets use a refitted shared BVH and swept chain bounds.
- Candidate overflow is visible and conservatively uses the full set; it never
  accepts a truncated candidate list.
- Degenerate capsule axes and plane normals are rejected during authoring.

## Runtime mode selection

Choose the runtime mode intentionally:

- **Strict CPU** uses the scalar/AVX2/depth-ordered CPU backend and CPU-owned
  current/previous outputs. It never changes physical quality automatically.
- **Strict GPU zero-readback** keeps authoritative state, activity, dispatch
  sizing, palettes, and bounds GPU-resident. It forbids blocking maps,
  `WaitForGpu`, current-frame readback, and silent CPU fallback.
- **Quality-tiered CPU/GPU** permits the explicit quality policy to reduce
  cadence or sleep eligible chains within its CPU/GPU budgets.

An unsupported or failed strict GPU backend is an observable failure. Do not
use compatibility synchronization as an implicit fallback. If gameplay needs
CPU-authoritative results, select strict CPU or request delayed outputs through
the readback service.

## Output consumers

Register only the outputs a consumer actually needs:

- palette and previous palette for rendering/motion vectors;
- conservative bounds for culling;
- CPU transform mirror for legacy hierarchy/gameplay code;
- selective particle, bone/socket, bounds, or event readback.

CPU transform mirroring is opt-in and has an explicit cadence. Its age and cost
are exposed; do not enable it merely to render a GPU chain. When no palette,
bounds, or mirror consumer is registered, that output work is skipped.

Selective readback is asynchronous. Requests are bounded by element and byte
budgets, become eligible no earlier than the next frame, expire explicitly, and
may complete out of order. Destroyed/reused instances, backend switches, arena
growth, and layout changes invalidate stale deliveries. Readback data must not
drive current-frame dispatch or rendering decisions.

## Sleep and quality authoring

Use a strict/fixed tier for gameplay-critical chains and deterministic captures.
Automatic tiers use relevance and importance plus independent CPU/GPU budgets,
hysteresis, minimum residency, and a per-frame transition cap. The policy
sleeps irrelevant chains and reduces distant cadence before lowering constraint
or collision quality.

Root teleport/acceleration, collider shape or pose changes, external force or
events, and explicit visibility/use wake a sleeping chain. Sleeping output
history is held coherently so waking does not expose an unrelated previous
palette. Diagnostics report requested/effective tier, reason, residence time,
activity error, wake reason, and delayed aggregate counts.

## Diagnostics checklist

For scale investigations record:

- selected backend and CPU/GPU kernel family;
- handle/template/collider-set IDs and generations;
- state/output slices and capacity/growth/fragmentation;
- active, rate-limited, sleeping, culled, and woken counts;
- dirty upload, GPU copy, selective readback, dispatch, and barrier counts;
- requested/effective quality and compatibility output age/cost.

Use the named-hardware and matrix contract under `docs/work/testing/` for
performance acceptance. Debug drawing, validation layers, verbose per-chain
logging, and editor-only inspection must be disabled during timing runs.
