# S14d: publish the scene after a play exit

Status: Validated for its gate (September 27, 2026): every entry and exit
publishes the scene within 4.6 s. The steady-state slowdown after play and a
device-memory growth per transition are opened as S14g; see
[Disposition](#disposition).

Gate record for
[S14d](../../todo/rendering/vulkan-stall-remediation-todo.md#evidence-index)
under the todo document's one-by-one protocol. Opened by S14a's validation;
the first observations are in the
[S14a record](2026-09-27-s14a-play-transitions.md#after-an-exit-the-viewport-publishes-no-scene-frame-opened-as-s14d).
Evidence root: `Build/_AgentValidation/20260927-095558-s14-core-update/`
(ignored and disposable; findings are copied here).

## Entry evidence

- After a play exit the viewport cannot be captured for its current resource
  generation for at least 60 s; before play and in play it can. Present on
  every S14 build.
- The renderer's own record of the last frame, more than a minute after an
  exit (Release, S13a fixture): outcome `Rejected`, reason
  `PresentNowReadinessRetry`, failure `AdmissionDeferred` at `ResourcePrepare`;
  readiness stage `MeshMaterialization`, ticket `visible-mesh-cold-admission`,
  560 ms spent in the frame, the last progress 0.06 ms before its end, and
  "deferred=250 unavailable=0 requests=407 warm=156 cold=251". Admission is
  moving, slowly, and every frame costs over half a second.
- The profiler's render-stall detector runs only with frame logging on, so
  the 0.5 to 0.8 s frames it recorded after an exit in one session are this
  stall seen by the detector, not a separate effect of frame logging.

## Code reading

`VulkanFrameLoop.MaterializeQueuedMeshRenderRequestsCore` prepares the
visible mesh requests of a frame. A request whose preparation signature is
already warm is materialized at normal throughput; a cold one is charged to a
4 ms per-frame slice (`ColdMeshPreparationSliceTicks`), and requests past the
slice are deferred and resumed next frame from a cursor. The frame is
published only when no request is deferred or unavailable ("yielded before
publishing a partial scene"), so until every visible mesh is warm the viewport
keeps presenting its last completed image. The signature includes the
identities of the renderer, material and mesh, and a restore creates new ones
for all 393 meshes, so after every entry and exit the whole scene is cold.

## Measurement declared before any change

On the S13a fixture in a Release session, enter play and later exit, and after
each transition sample the renderer's present-now failure record and CPU stage
counters every 2 s until a viewport capture succeeds (or 5 minutes pass),
attempting a capture every 10 s. Record the time to the first successful
capture, the deferred, warm and cold counts over time, the time spent per
frame, and where that time goes. Question to answer: why a cohort that is
being admitted takes minutes to converge, and whether exit differs from
entry.

## Measurement

Release session on the S13a fixture, one play round trip; the renderer's
present-now failure record sampled every 2 s after each transition.
Evidence: `reports/s14d-observe-1`, `reports/s14d-trace`.

| Seconds after exit | Warm requests (of 407) | Time of the last deferred frame |
| --- | --- | --- |
| 0 | 116 | 384 ms |
| 64 | 130 | 494 ms |
| 129 | 186 | 688 ms |
| 201 | 220 | 1,694 ms |
| 283 | 243 | 1,852 ms |

- **The scene did not publish within 5 minutes of the exit.** Every frame was
  rejected with `PresentNowReadinessRetry`; about one request turned warm per
  frame, and each frame got slower as the warm count grew, from 0.4 s to
  1.9 s. The editor ran below one frame per second with the scene frozen.
- **Entry behaves the same way.** The frame cost rose from 8 ms to 171 ms as
  the first 58 requests turned warm. A capture succeeded 11 s after entry
  while 347 requests were still deferred, so a successful capture alone does
  not show that the scene was published.
- **Where the time goes.** A 12 s CPU sample during the post-exit stall put
  11.98 s of the render thread inside
  `VulkanFrameLoop.MaterializeQueuedMeshRenderRequestsCore`, 98% of it in
  `ComputeDispatchSnapshot.HashUniformNames`, reached from
  `VkMeshRenderer.CaptureProgramBindingSnapshot` for every warm request:
  68% in `ConcurrentDictionary<string, ulong>.Count` and 30% in `GetOrAdd`,
  nearly all of it acquiring locks.

## Cause

`FrameOpSignatureHasher.GetStableStringSignature` caches string signatures in
a `ConcurrentDictionary` keyed by reference identity, capped at 4,096 entries.
On every miss it calls `Count`, which acquires every lock of the dictionary,
then inserts, and it clears the cache when full. Before play the scene's
uniform names are shared string instances, so lookups hit. A restore
deserializes a separate string for every uniform name of every material copy
(the snapshot unshares 25 materials into 393, S14f), about 20,000 instances,
so nearly every lookup misses, the cache refills and clears continuously,
and every miss takes all the locks while the recording threads hash strings
concurrently. Every deferred frame re-materializes all warm requests, so the
cost grows with the warm count. The profiler's render-stall detector runs only
with frame logging on, which is why the 0.5 to 0.8 s frames were first seen
in a session with frame logging enabled.

## Change 1 predeclared

`FrameOpSignatureHasher` counts its cached entries with an interlocked
counter instead of `ConcurrentDictionary.Count`, so a miss costs the
signature computation and one bucket lock; the cache still clears when the
count passes 4,096. Hits are unchanged.

Hypothesis: the post-transition stall is this cache's miss path.

Acceptance, over three round trips on the S13a fixture with the observer
counting a transition as settled only when the latest frame completes and a
capture succeeds: the scene publishes within 10 s of each entry and each
exit, and no deferred frame takes more than 50 ms.

Falsifier: publication takes more than 30 s or deferred frames still exceed
100 ms; then another cost dominates, and the render thread is sampled again.

## Change 1: result

Three round trips on the Release build with change 1 (`reports/s14d-observe-fix`):

| Transition | Settled after | Longest sampled deferred frame |
| --- | --- | --- |
| Entry 1 | 18.7 s | 110 ms |
| Exit 1 | 25.2 s | 111 ms |
| Entry 2 | 25.6 s | 114 ms |
| Exit 2 | 25.2 s | 103 ms |
| Entry 3 | 25.1 s | 107 ms |
| Exit 3 | 27.4 s | 159 ms |

Every transition now publishes, where the exit did not within 5 minutes
before, but the acceptance is not met (10 s, 50 ms) and the falsifier applies
(deferred frames above 100 ms), so the render thread was sampled again
across an exit (`reports/s14d-trace/post-exit-fix`). Of 11.7 s inside mesh
materialization in 20 s:

- 78.5% in `CaptureProgramBindingSnapshot`, of which the string signature
  cache still takes about 40%: every miss inserts (`TryAdd` 31%, with bucket
  lock contention against the recording threads) and each clear regrows the
  table (6%);
- 10.3% reading an environment variable: `SetMaterialUniforms` applies shadow
  uniforms, and `DirectionalLightComponent.TryResolveVulkanDirectionalCascadesOverride`
  reads and parses `XRE_VULKAN_DIRECTIONAL_CASCADES` on every call, once per
  mesh request;
- 10.6% zeroing the binding capture's dictionaries (`BindingCaptureState.Clear`);
- 15.5% cold program preparation, including a directory scan per shader
  compile (7.8%), whose count follows from the 786 unshared shader copies
  (S14f).

All of the warm-request costs are spent again in every deferred frame and
then thrown away: a present-now frame whose cohort is incomplete resets its
authored operations and retries, and the primary path drops every static
operation and keeps only dynamic UI.

Steady state on this build after four round trips (`reports/s14d-steady-change1*.json`):
207 to 216 frames per second stationary, and 14.9 during camera motion,
where the visible cohort changes and requests are materialized again.

## Changes 2 to 4 predeclared

- **Change 2: the string signature cache is removed.** A signature is
  computed directly from the characters; for uniform-length names that costs
  about what a dictionary lookup did, and it takes no lock, allocates nothing
  and cannot thrash. This replaces change 1's counter.
- **Change 3: a frame that cannot publish stops materializing.** In the modes
  that defer cold requests, once the 4 ms cold slice has deferred a cold
  request, the remaining requests of that frame are deferred without being
  materialized, except dynamic UI overlays, which the primary path still
  records. The cursor still resumes at the first deferred request, so the
  next frame continues the cold admission; the frame that completes the
  cohort materializes every request in order as before.
- **Change 4: the cascade override is read once.**
  `XRE_VULKAN_DIRECTIONAL_CASCADES` is parsed on first use and cached, like
  other launch-time switches.

Acceptance, on a fresh Release session with three round trips: every entry
and exit settles within 10 s with no sampled deferred frame above 50 ms;
stationary frame rate before play is at least 95% of change 1's (197 fps);
and camera-motion frame rate after the round trips is at least 90% of the
same session's before play.

Falsifier: settling above 30 s, deferred frames above 100 ms, or either frame
rate criterion missed; then sample again.

## Changes 2 to 4: result

Fresh Release session, steady state before play, three observed round trips,
steady state after (`reports/s14d-observe-fix2`, `reports/s14d-steady-fix2-*`):

| Transition | Settled after | Longest sampled deferred frame |
| --- | --- | --- |
| Entry 1 | 6.8 s | 4.9 ms |
| Exit 1 | 17.2 s | 59 ms |
| Entry 2 | 17.0 s | 71 ms |
| Exit 2 | 16.9 s | 58 ms |
| Entry 3 | 16.9 s | 55 ms |
| Exit 3 | 17.1 s | 63 ms |

| Frames per second | Before play (fresh) | After three round trips |
| --- | --- | --- |
| Stationary | 127.7 | 214.4 |
| Camera motion | 29.9 | 20.8 |

- The first entry now settles in 6.8 s with deferred frames under 5 ms, so a
  frame that cannot publish costs little. Later transitions settle in about
  17 s with deferred frames up to 71 ms, and camera motion after the round
  trips runs at 70% of its rate before play.
- Against the declaration: settling within 10 s and deferred frames under
  50 ms are not met after the first entry; the motion criterion (90%) is not
  met. The stationary criterion was underspecified: change 1's 207 to 216 fps
  came from a long-running session after round trips, which this build
  matches (214.4); the fresh-session value right after load is not
  comparable. The falsifier applies, so the render thread was sampled during
  camera motion after the round trips (`reports/s14d-trace/motion-after-rounds`).
- That sample shows the environment still read per request: with the cascade
  override cached, `CanRenderDirectionalCascadesForCurrentBackend` falls
  through to `IsKnownMonadoOpenXrRuntime`, which reads `XR_RUNTIME_JSON` and
  `XRE_UNIT_TEST_OPENXR_RUNTIME_JSON` on every call (8% of the render thread).
  The rest is per-request recording work repeated for every material copy
  (binding snapshots, descriptor acquisition locks, sealed copies and the
  collections they allocate), which grows with the number of distinct
  materials: 393 after a restore against 25 before play. That part belongs
  to S14f, which restores the sharing; S14d's acceptance is measured again
  after it.

## Change 5 predeclared

`IsKnownMonadoOpenXrRuntime` evaluates the environment at most once per render
frame and reuses the answer within the frame. The editor switches
`XR_RUNTIME_JSON` at run time (the OpenXR test toggle), so the answer is not
cached across frames.

Acceptance: camera-motion samples after round trips show no environment reads
under shadow uniform application, and stationary and motion frame rates after
round trips do not fall. S14d's transition criteria are measured again with
S14f.

## Change 5: result

Fresh Release session, the same round as changes 2 to 4
(`reports/s14d-observe-change5`, `reports/s14d-steady-change5-*`,
`reports/s14d-trace/motion-change5`):

| Transition | Settled after | Longest sampled deferred frame |
| --- | --- | --- |
| Entry 1 | 5.2 s | 6.6 ms |
| Exit 1 | 16.9 s | 57 ms |
| Entry 2 | 16.9 s | 56 ms |
| Exit 2 | 16.9 s | 56 ms |
| Entry 3 | 16.7 s | 57 ms |
| Exit 3 | 17.2 s | 55 ms |

| Frames per second | Before play (fresh) | After three round trips |
| --- | --- | --- |
| Stationary | 131.6 | 208.8 |
| Camera motion | 30.2 | 20.7 |

- **Met.** The 10 s camera-motion sample after the round trips has no
  environment read under shadow uniforms (789 ms of the render thread before,
  none now; the only one left is 1.5 ms in the pipeline's OpenXR check), and
  neither frame rate fell (208.8 against 214.4 stationary is within
  run-to-run spread; 20.7 against 20.8 in motion).
- **The motion rate did not rise either**, so the render thread was not
  limited by those reads alone. In the same sample 44% of the render thread
  is mesh materialization, 91% of that in `CaptureProgramBindingSnapshot`:
  compute snapshots and binding-layout signatures (39%), material uniform
  application (31%), monitor contention (26%), texture descriptor signatures
  (21%) and clearing the binding capture (18%). Whether this cost follows
  the number of distinct materials (25 before play, 393 after a restore) is
  what the measurement on the S14f build shows.

## Measurement on the S14f build (change 1 of S14f)

Fresh Release session, same round (`reports/s14d-observe-s14f`,
`reports/s14d-steady-s14f-*`, `reports/s14d-trace/motion-s14f`):

| Transition | Settled after | Longest sampled deferred frame |
| --- | --- | --- |
| Entry 1 | 2.3 s | none sampled |
| Exit 1 | 4.1 s | 33 ms |
| Entry 2 | 4.3 s | 34 ms |
| Exit 2 | 4.2 s | 33 ms |
| Entry 3 | 4.2 s | 34 ms |
| Exit 3 | 4.1 s | 32 ms |

| Frames per second | Before play (fresh) | After three round trips |
| --- | --- | --- |
| Stationary | 130.5 | 195.6 |
| Camera motion | 30.2 | 22.8 |

A second fresh session gave 30.3 before play and 23.5 after three round
trips (`reports/s14d-steady-motion-before-s14f.json`,
`reports/s14d-steady-s14f-stats-after.json`).

- **The transition criteria are met:** every entry and exit settles within
  4.3 s, and no sampled deferred frame exceeds 34 ms.
- **Camera motion after the round trips is still 75 to 78% of before play.**
  The 10 s motion samples put mesh materialization at 1.32 s of the render
  thread before play (`motion-before-s14f`, 4.4 ms per frame) and 3.84 s
  after the round trips (16.8 ms per frame), with the same 406 requests:
  compute snapshots and binding-layout signatures 0.48 s to 2.06 s, texture
  descriptor signatures 0.20 s to 1.13 s, monitor contention 0.59 s to
  1.33 s. Material uniforms fell to 0.30 s with shared materials, so the
  remaining growth is not per material.
- **Every transition leaves the scene it replaced registered.** The renderer
  statistics of one session before play and after three round trips
  (`reports/s14d-stats-before-play.json`, `-after-rounds.json`):

  | | Before play | After three round trips |
  | --- | --- | --- |
  | Device-local memory | 1.32 GB | 6.15 GB |
  | Live renderer resources | 10,076 | 40,375 |
  | Mesh descriptor sets | 2,530 | 13,740 |
  | Mesh descriptor pools | 12 | 51 |
  | Tracked descriptor sets | 6,391 | 19,340 |

  `WorldStateSnapshot.RestoreScene` unloads the roots it replaces and never
  destroys them, and nothing else does: render objects stay in
  `GenericRenderObject`'s static cache with their renderer wrappers until
  destroyed, and a destroyed `ModelComponent` does not dispose its renderable
  meshes either. Each transition therefore leaves one more scene copy
  (meshes, materials, renderers, their buffers and descriptor sets)
  registered, about 0.8 GB of device memory each.

## Change 6 predeclared

1. **A restore owns the copy it creates, and the next restore of the same
   scene destroys it.** After swapping the restored roots in,
   `RestoreScene` destroys the roots it replaced and the render objects
   (meshes, materials, shaders, generated textures) that the previous restore
   of that scene deserialized, through the engine's deferred destruction at
   the end of the update frame. Content a restore did not create (the world
   as loaded before the first entry) can share objects with other owners,
   such as import caches and engine defaults, and is left as it is, so one
   extra copy remains per editor session.
2. **A destroyed model component disposes its renderable meshes.**
   `ModelComponent` releases its renderers and runtime meshes when destroyed,
   as it already does when its model changes.

Hypothesis: the growth is every replaced copy staying registered, and the
slower mesh admission during motion follows from it.

Acceptance, on a fresh Release session with three round trips: device-local
memory and live renderer resources stop growing (after the third round trip
within 10% of after the first); camera motion after the round trips is at
least 90% of before play; the transition criteria stay met (10 s, 50 ms);
the S14a gate holds and no transition fails.

Falsifier: the growth continues (another owner keeps the copies), or it stops
and motion stays below 90% (then the admission cost has another cause).

## Change 6: result

The first build recorded only render objects as created, which missed the
meshes: `XRMesh` is an `XRAsset` that reads its buffers in its own cooked
format, and destroying it is what retires them. The measured build records
render objects and meshes (424 per restore: 393 meshes, 25 materials, 4
shaders, 2 generated textures). Fresh Release session
(`reports/s14d-c6b.out`, `reports/s14d-stats-c6b-*.json`,
`reports/s14d-trace/motion-c6b`); every restore after the first logged
"Released the previous restore ... roots=1 renderAssets=424 failures=0".

| | Before play | After 1 round trip | After 3 round trips | Before the change, after 3 |
| --- | --- | --- | --- | --- |
| Live renderer resources | 10,097 | 16,214 | 16,773 | 40,375 |
| Active device allocations | 2,282 | 4,450 | 4,693 | 14,528 |
| Mesh descriptor sets | 2,550 | 3,895 | 3,935 | 13,740 |
| Mesh descriptor pools | 13 | 18 | 18 | 51 |
| Device-local memory | 1.32 GB | 3.36 GB | 6.15 GB | 6.15 GB |

Transitions settled in 2.3 to 4.6 s with deferred frames up to 44 ms; camera
motion ran at 30.6 fps before play and 23.6 after three round trips (77%).

- **The copies are released:** live resources, allocations and descriptor
  sets stop growing after the first round trip (3.4% more from the first to
  the third). The first round trip still adds one copy, the world as loaded
  before play, which no restore created and which is left alone by design.
- **Device memory still grows about 0.7 GB per transition,** with about 60
  allocations and 140 live resources per transition, which is 2 allocations
  and 5 resources for each of the 27 light probes. `SceneCaptureComponent`
  allocates its capture targets (environment cubemap, depth cubemap or
  render buffer, octahedral texture, face framebuffers) on activation and
  releases them only when it re-initializes or after IBL generation when
  asked to; deactivation and destruction release nothing, so each destroyed
  copy leaves its probes' capture targets behind. That is the "another owner"
  of the falsifier.
- **Motion stays at 77%, and not because of the copies.** Over the same 10 s
  motion window (`reports/s14d-counters-c6b-*.json`), before play and after
  three round trips materialize the same work per frame (0.37 and 0.40 full
  cohort materializations, 0.62 and 0.60 reuses, 78.6 and 77.7 hole
  requests), but a hole request costs 24 us before play and 92 us after:
  1.87 against 7.14 ms per frame. The render thread's extra time is in
  monitor slow paths inside the texture descriptor signature
  (`VulkanTextureDescriptorSignaturePlan.AddSignature`, 0.20 s to 1.07 s in
  10 s) and in clearing the binding capture (0.36 s to 0.95 s); no other
  sampled thread works in the texture wrappers, so the cause is not yet
  known.

## Change 7 predeclared

**A deactivated scene capture releases its capture targets.** Activation
allocates them; deactivation now releases them on the render thread once no
capture writer or consumer is in flight (the same deferral the light probe
already uses for its IBL resources on deactivation), unless the component was
activated again meanwhile. The next activation or capture allocates them
again, as it does after a resolution change. A light probe already destroys
its IBL output on deactivation, so a deactivated probe loses nothing it could
still use.

Hypothesis: the remaining per-transition growth is the light probes' capture
targets.

Acceptance, on a fresh Release session with three round trips: device-local
memory, active allocations and live resources after the third round trip are
within 10% of after the first; light probes still capture after each
transition (27 probes with IBL output before and after); transitions stay
within 10 s and 50 ms.

Falsifier: device memory keeps growing, which would leave another owner.

## Change 7: result, reverted

Fresh Release session, the same round (`reports/s14d-c7.out`,
`reports/s14d-stats-c7-*.json`, `reports/s14d-probes-c7-*.json`,
`reports/s14d-counters-c7-*.json`): device-local memory read exactly the
same bytes as every build before it (3,361,719,296 after one round trip,
6,146,306,560 after three) and live resources the same as change 6 (16,199
and 16,758). The falsifier applies, and the premise was wrong: a light probe
overrides `ShouldInitializeCaptureResourcesOnActivate` to false and allocates
capture targets only when it captures, and no probe of this fixture ever
captures (`IblTexturesValid` false and `CaptureVersion` 0 on all 27, before
and after play). The change had nothing to release here and was not
validated, so it was reverted.

Two conclusions follow. The device-local figure is identical to the byte with
and without releasing the copies, so it does not track the scene copies or
any object this item manages; what grows it (about 60 allocations and 140
live resources per transition after the first round trip) is not identified.
And the per-request slowdown has to be looked for elsewhere.

## Where the motion slowdown is not

The admission counters over the same 10 s motion window
(`reports/s14d-counters-c7-*.json`, `reports/s14d-original-copy-*.json`):

| | Hole requests per frame | Hole materialization per frame | Per request |
| --- | --- | --- | --- |
| Before play | 68.6 to 79.6 | 1.67 to 1.88 ms | 24 us |
| After one round trip | 81.0 to 81.7 | 7.71 to 7.86 ms | 95 us |
| After three round trips | 82.4 | 8.01 ms | 97 us |
| After one round trip, original copy destroyed | 80.0 | 7.63 ms | 95 us |

- **It is a one-time shift at the first round trip, not a growth:** the cost
  per request is the same after one and after three round trips.
- **It is not the original world's retained copy.** The original root still
  owns its identity in the object cache (see the S14f record), so it could be
  destroyed through `invoke_method` after a round trip; the live scene kept
  its 34 nodes and the cost did not move.
- It sits in monitor slow paths inside the texture descriptor getters and in
  clearing the binding capture, on file-backed textures that the original
  world and every copy share. What changes at the first entry is not known.

## Disposition

- **Gate met.** Within 10 s of every entry and exit the viewport's current
  resource generation renders and can be captured, and no sampled deferred
  frame exceeds 50 ms, over three round trips on every build since S14f's
  change 1 (settled in 2.3 to 4.6 s, deferred frames up to 44 ms), where
  before this item the scene did not publish within 5 minutes of an exit.
- **Changes kept:** 2 (no string signature cache), 3 (a frame that cannot
  publish stops materializing), 4 (cascade override read once), 5 (OpenXR
  runtime check once per frame) and 6 (a restore releases the copy it
  replaces; a destroyed model component disposes its renderable meshes).
  Change 1 was superseded by change 2; change 7 was reverted.
- **Not met, opened as S14g:** camera motion after the round trips runs at 77
  to 82% of before play, from a one-time rise in per-request admission cost
  at the first round trip; and device-local memory grows about 0.7 GB per
  transition from an owner outside the scene copies.
