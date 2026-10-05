# OpenXR Stereo Flicker And Render-Target Duplication

Status: two causes fixed and live-validated, startup transition black and the
remaining memory still open. Last updated 2026-10-04.

## Problem

With OpenXR rendering single-pass stereo (2688×2688 per eye, user settings):

- The stats HUD alternated between two sets of values.
- The headset image flickered to black about once a second, in step with the
  HUD.
- The editor process committed 25–30 GB.

## How it was measured

- **Headset output.** SteamVR's *VR View* window was captured with
  `PrintWindow(PW_RENDERFULLCONTENT)`, which works while the window is covered,
  at 17–27 captures per second. A frame counts as black when its mean luminance
  is below 5 of 255. Eye-preview captures take about 7 s each and cannot resolve
  flicker.
- **Submission.** OpenXR submitted and no-layer frame counters were sampled on the
  same clock, so black frames can be attributed to frames ended without layers
  or to submitted black content.
- **HUD.** The HUD region of the editor window was hashed on every capture. A
  *revisit* is a HUD that shows an earlier state again after a different one
  (A, B, A), which is the signature of stale data.
- **Memory.** VMA statistics (`get_vulkan_memory_statistics`), the planner table
  listing (`get_vulkan_resource_planner_states`, added for this investigation),
  and process counters were recorded.

## Cause 1: stale descriptor sets after a native buffer is replaced

`XRDataBuffer.Resize` makes the next upload recreate the `VkBuffer`.
`VkMeshRenderer` republished its buffer fingerprint only when its buffer
collection changed, so descriptor sets written before the replacement kept
binding the retired buffer.

A renderer owns several descriptor allocations (per draw-uniform slot and
frame-data family, including the OpenXR streams). The output therefore
alternated between fresh and stale data. The batched HUD text buffers grow
whenever the stats text changes, so the HUD showed the effect most.

**Fix.** A mesh renderer records the backend `NativeBufferBindingRevision` it
last observed. In `EnsureBuffers` a changed revision refreshes the buffer
identity snapshot. When the snapshot actually changed, the renderer republishes
its fingerprint and marks descriptors, vertex input and legacy command buffers
dirty (`VkMeshRenderer.Buffers.cs`).

| Build | HUD changes in 6 s | Revisits |
| --- | --- | --- |
| Fix disabled | 92 | 68 |
| Fix enabled | 23 | 0 |

## Cause 2: stereo render targets held three times

Each resource-planner state owns one allocator holding a complete physical image
set. The stereo pipeline's set is 38 targets, about 2.3 GB at 2688² with two
layers. Three sets were alive:

- **Prepared but never used.** A resource-generation commit prepares the new
  generation's set and publishes it into the published (desktop) switching
  table. OpenXR planners render from their own thread-scoped tables, so the
  first eye render after each commit missed and allocated a second set. Eye
  rendering never read the prepared set.
- **Superseded.** Superseded generations are removed only from the published
  table at commit, so an OpenXR table kept every earlier generation's set alive.
- **In use.** The set XR actually rendered with.

Planner states during XR (user settings, before the fixes):

| Table | Generation | Allocator | Last used |
| --- | --- | --- | --- |
| Published | 5 | 30 | 12005 |
| OpenXR mirror, nested | 3 | 32 | 29602 |
| OpenXR mirror, nested | 5 | 34 | 277950 |

**Fixes.**

- **Supersession in every table.** `RemoveSupersededGenerationStates` and
  `IsAllocatorOwned` moved to `VulkanResourcePlannerSessionService`. When a
  readback scope publishes a state into any switching table, it removes older
  generations of the same pipeline, viewport, output and logical view. The frame
  loop then retires their allocators unless another state in that table or the
  published table still owns them. Destruction is deferred through the
  retirement queue, and image groups that the new allocator reuses are kept.
- **Sharing the committed state.** On a miss in a thread-scoped table, the
  readback scope looks for the state a commit prepared for the same planner key
  (including pass metadata) in the published table, and shares its allocator
  instead of allocating a new set. The published entry keeps ownership, so
  supersession or eviction there retires the set and the scoped table then
  prepares its own. The lookup runs only on the render thread, which is the only
  writer of the published table.

Three startups of each build, measured 30 frames into the OpenXR session:

| Build | Black time in first 45 s | Planner image memory | Private memory |
| --- | --- | --- | --- |
| Before (HUD fix only) | 31.6 s, 16.7 s, 4.3 s | 7.5 GB | 29.1–35.3 GB |
| Supersession + sharing | 4.1 s, 2.6 s, 2.5 s | 2.9 GB | 24.3–26.0 GB |

In steady state, VMA device-local usage fell from 12.43 GB to 7.41 GB. A
15-second VR View capture has no dark frames. Settled private memory is
24.7 GB, against 28.3–28.6 GB before.

## Ruled out

- **Retaining the previous allocator at commit.** One change stopped a commit
  made inside an OpenXR planner scope from retiring that scope's live allocator.
  With it, every eye frame failed plan sealing ("did not expose exactly one
  executable non-deferrable terminal") and only no-layer frames were submitted.
  It was reverted. The commit's retirement of the thread-scoped previous state
  remains, and still forces one reallocation per transition (see remaining work).
- **Desktop presentation replay** (`PresentLastCompletedContent`) is not the
  source of the black frames.
- **Sharing as the cause of black startup frames.** Early runs with only the
  supersession fix sometimes showed a long black period during generation 3.
  The controlled trials show the same behavior without the fixes (up to 31.6 s),
  so it predates them.

## Remaining work

1. **Black at the startup generation transition (about 2.5 s).** The stereo
   pipeline commits generations 3, 4 and 5 during startup. Around the last one,
   submission stops for about 1.7 s and the frame loop then ends only no-layer
   frames until rendering resumes. SteamVR shows black for those frames.

   A commit issued while frame operations are captured inside the OpenXR planner
   scope retires the live nested allocator: `Transaction.Commit` retires its
   `previousState` when the published table does not own it. The frame that was
   already captured must then allocate a fresh set for the old generation. In
   one probe the nested generation-3 entry moved from allocator 22 to 28 right
   after the generation-4 commit.

   A fresh set also starts without auto-exposure history, which fits the black
   and overexposed startup frames. Fixing this needs the commit to leave
   thread-scoped live state to its table *and* the plan-seal failure above
   understood.
2. **Pass metadata differs between commit and first eye render.** At generation 3
   the committed key's pass-metadata signature did not match the eye render, so
   sharing could not apply and a second set was allocated. Find what changes the
   stereo pipeline's pass list between preparation and render, and why startup
   commits three generations.
3. **Eviction of a shared state.** The published table's 12-entry LRU can evict
   a shared stereo state, because only the share marks it used. The eye planner
   then reallocates, the previous behavior, with a stall.
4. **Remaining memory.** See the
   [memory record](2026-10-04-editor-memory-retention.md).
5. **XR frame rate.** In steady state the user configuration submits about 30
   frames/s with about 4.6 missed deadlines/s.

## Tools added

- MCP `get_vulkan_memory_statistics`: VMA statistics document.
- MCP `get_vulkan_resource_planner_states`: published and OpenXR planner tables
  with keys, allocator ownership and image memory per allocator. It runs on the
  render thread between frames.

## Validation

- Release builds: zero warnings and errors.
- Live: the HUD revisit check, VR View captures (steady state and three startups
  per build), planner tables, VMA statistics and process counters, as above.
