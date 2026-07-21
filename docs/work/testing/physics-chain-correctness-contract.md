# Physics-chain correctness and benchmark contract

Last updated: 2026-07-20

## Correctness tolerances

Every comparison first requires finite outputs, identical particle/bone counts,
identical parent ordering, identical collision flags for contacts farther than
the tolerance boundary, and no invalid handle delivery. Error is bounded by the
following absolute tolerances; relative position tolerance is `1e-5` for
magnitudes above 10 m so large-coordinate cases remain meaningful.

| Quantity | Scalar versus AVX2 CPU | Scalar versus GPU |
| --- | ---: | ---: |
| Particle position | 0.0001 m | 0.0005 m |
| Orientation angular error | 0.05 degrees | 0.25 degrees |
| Constraint-length error | max(0.0001 m, rest length x 0.0001) | max(0.0005 m, rest length x 0.0005) |
| Contact normal dot-product | at least 0.9999 | at least 0.999 |
| Penetration correction | 0.0001 m | 0.0005 m |
| Friction displacement | 0.0001 m | 0.0005 m |
| Palette element | 0.0001 | 0.001 |
| Conservative bounds | contains scalar particle/influence bounds | contains scalar particle/influence bounds |

Reset, teleport, spawn, slot reuse, retemplate, and backend switch additionally
require current and previous histories to match on the reset frame. NaN or
infinity is never tolerated. Degenerate zero-length input must remain finite
and use its explicit guarded behavior.

## Topology decision

Linear short chains are the optimized common family. Branched topology is
required in the first runtime release through the explicit depth-ordered
general kernel; it is not flattened and is never sent to the AVX2 linear
family. Benchmark and profiler output name the selected family so a general
fallback cannot masquerade as the optimized path.

## CPU consumer decision and audit

The consumer inventory is recorded in
[the output/readback contract](../../architecture/physics/physics-chain-output-and-readback.md).
The July 2026 source audit found no external production caller reading the
private `Particle` or `ParticleTree` runtime types. Existing call sites author
components, toggle bounded debug output, invalidate GPU-driven bindings, or
locate benchmark chains.

Normal renderers consume palettes and bounds directly. Attachments and sockets
request selected delayed matrices. Editor/debug tools request bounded selected
data. Collision events are delayed unless a caller selects strict CPU
authority. Full transform publication is explicit compatibility behavior.

## Required automated matrix

`PhysicsChainBenchmarkRequiredMatrix` defines the deterministic shardable
matrix: 100/500/1,000/2,000/5,000/10,000 chains; 4/8/16/32 dynamic segments;
linear and branched topology; none/two-simple/five-mixed/large-broadphase
collider cases; shared and unique collider ownership; 100%/50%/10% active and
sleeping/offscreen-heavy populations; no rendering, palette/bounds, identical
instanced, and diverse skinned rendering; strict and quality-tiered CPU/GPU;
disabled/sparse-socket/sparse-whole-chain/diagnostic-full readback; OpenGL and
Vulkan; and 30/60/90/120 Hz.

Unsupported backend points are emitted with an explicit reason. Each accepted
point has at least three matched Release runs, at least 1,000 settled frames
and the configured minimum duration, raw frame samples, environment metadata,
CPU/GPU stage metrics, population/draw metrics, and separately labeled
cold-start, structural-churn, and steady-state results.
