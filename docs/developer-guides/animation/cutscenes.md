# Cutscenes

This guide describes the cutscene timeline: how to author a cutscene, how to preview it in the editor, how to play it, and how to drive it through MCP. The [cutscene system architecture](../../architecture/animation/cutscene-system.md) describes the design: the ownership, the tick order and the clock, the residency planner and its backends, the invariants, and the known limits.

## Parts

| Part | Project | Responsibility |
|---|---|---|
| `CutsceneAsset` | `XREngine.Runtime.CutsceneIntegration` | Holds the tracks, the clips, and the bindings. It holds no runtime state. |
| `CutsceneCompiler` | `XREngine.Runtime.CutsceneIntegration` | Validates an asset and builds the time index. |
| `CutscenePlayer` | `XREngine.Runtime.CutsceneIntegration` | Holds the transport and evaluates the tracks. Each evaluation uses only the time. |
| `CutsceneDirectorComponent` | `XREngine.Runtime.CutsceneIntegration` | Opens an asset in a world and ticks the player. |
| `CutscenePreviewHost` | `XREngine.Editor` | Plays an asset in edit mode and restores the scene. |
| `CutscenePreviewSession` | `XREngine.Editor` | Owns the one cutscene that the panel and the MCP tools control. |
| `CutsceneResidencySession` | `XREngine.Runtime.CutsceneIntegration` | Connects the residency planner and its backend to a player for one open cutscene. |
| `CutsceneBakeTool` | `XREngine.Editor` | Bakes the residency manifest of an asset with the real player. |

## Animation Clip Tracks

An `AnimationClipTrack` plays `AnimationClipTrackClip` entries on its bound node. `CutsceneClipInstancer` makes one copy of each clip for each binding when the cutscene opens. The frame path makes no copies. A runtime slot must have its node before the open: call `CutsceneDirectorComponent.SetRuntimeSlot`. The value stays across a close and a later open, until a call with null clears it or the director is destroyed. If the slot has no node at the open, or gets another node later, the track writes nothing and adds a diagnostic.

Each frame runs in this order: store write, humanoid solve, root placement, and one IK solve. A VRIK target keeps its own scheduled solve.

- **Weights:** a total of zero leaves the original value. A total above one normalizes. A total below one blends from the original value. For a humanoid, the original value of a muscle is its live raw value at the first write, and the original value of an IK goal weight is its live weight. A target that has bones away from the bind pose and no readable muscle values fades from the neutral muscle pose. The first write adds a diagnostic for it.
- **Root placement:** a humanoid root is the start transform, then `RootOffsetPosition` and `RootOffsetRotation`, then the clip motion of this frame. The result is absolute, so repeated seeks do not drift. A target without a `HumanoidComponent` has no root placement. A root offset on such a target adds a diagnostic.
- **Lease:** while a clip has weight, the lease sets `EvaluationSuspended` on a sibling `AnimStateMachineComponent` and `AnimationClipComponent`. This flag is separate from `SuspendedByClip`. The lease puts back the earlier flag at clip exit and at stop. Playback time and the play and pause state of those components do not change.
- **No clip:** a target that has no clip with weight returns to its original values. For a humanoid, this includes each transform under the node and each member that the clips write.
- **Finish behavior:** a `KeepState` stop keeps the pose, and the next run records it as the new original value. If two tracks share a binding, the slot is restored if any of the tracks has `RestoreState`. This includes the root transform, the bone and muscle pose, and the clip members.
- **Pose preview:** if `HumanoidComponent.PosePreviewMode` is not `AnimatedPose`, the preview mode owns the pose. The track writes nothing and adds one diagnostic.

These features are not available from a sampled store. The open adds a diagnostic for each case:

| Case | Result |
|---|---|
| Additive clip | Plays as an absolute pose, without its reference pose. |
| Flip settings on a sibling `AnimationClipComponent` | The clips do not use them. |
| A track that calls code at the sample time | The member is not sampled. |

## Property Tracks

A `PropertyTrack` animates one member of a component on its bound node. Set these values:

- `ComponentType`: the short or full type name of the component. An empty value selects the scene node itself.
- `MemberPath`: the property or field to animate. Separate nested members with a period, for example `Parameters.VerticalFieldOfView`. Each member before the last must hold an object of a reference type.

The member must be a `float`, `Vector3`, or `Quaternion`. Each `PropertyTrackClip` has a `FloatCurve`, a `Vector3Curve`, and a `QuaternionCurve`. The type of the member selects the curve, and the other curves have no effect.

- **Binding:** the track finds the member at the first frame with weight, and again when the node of the slot changes. Each time a clip becomes active, the track walks the member path again. If an object on the path was replaced, the track writes the new object and adds one diagnostic. A replacement while a clip stays active is not seen until the next clip becomes active. A missing component, a missing member, or an unsupported type adds one diagnostic, and the track writes nothing. The write uses the typed setter of an `AnimationMember`, so a frame does not box the value.
- **Weights:** the rules of the transform track apply. A total of zero leaves the original value. A total above one normalizes. A total below one blends from the original value. Rotations blend as a normalized sum in one hemisphere.
- **Shared member:** tracks with the same slot, `ComponentType`, and `MemberPath` blend by weight and share one original value. The member is restored if any of the tracks has `RestoreState`.
- **No clip and stop:** a member with no clip with weight gets its original value back. A stop restores or keeps the value by the finish behavior, through `CutsceneStateSnapshot`.

## Audio Tracks

An `AudioTrack` plays `AudioTrackClip` entries through the `AudioSourceComponent` on its bound node. Set these values on a clip:

- `SourcePath`: the Ogg Vorbis (`.ogg`) or WAV (`.wav`) file to stream. A relative path starts at the folder of the cutscene asset file.
- `Volume`: multiplies the gain of the component and the blend weight of the clip.
- `ClipOffset`: skips the start of the file. `Speed` must be 1, and `Extrapolation` must not be `Loop`. A clip with another value adds one diagnostic and plays nothing.

The track streams the active clip with an `IStreamingAudioDecoder` (`VorbisStreamingDecoder` or `WavStreamingDecoder`), and it feeds the sources through `AudioSourceComponent.EnqueueStreamingBuffer`. The track keeps its own list of the sources and rebuilds it only when the listeners change, so a refill does not allocate. Each buffer holds 2048 frames, and the track keeps 4 buffers in the queue. The track also keeps the last 6 buffers of decoded frames, so a restart near the current position (a resume, a return to rate 1, or a new listener) does not seek the decoder backward. While a clip plays, the track sets `ExternalBufferManagement` on the component and turns off `AutoPlayOnQueue` on its sources. A stop or the end of the clip puts back these values and the source gain.

| State | Result |
|---|---|
| Forward playback at rate 1 | The sources play the clip from the cutscene time. |
| Jump seek to a new time during playback | Every active source stops, the decoder seeks, and the sources play again from the new time. A clip with an audio proxy plays the proxy at once and changes to the full decoder later (see [Tier Change](#tier-change)). |
| Pause, scrub (with or without `BeginScrub`), stop, or any rate other than 1 | The sources stop streaming. With no proxy, their gain is zero. With a proxy, a move of the playhead plays a scrub grain (see [Scrub Grains](#scrub-grains)). Playback at rate 1 starts again from the current time. |
| A listener comes into range | All sources of the track restart together, so they stay aligned. |
| A source runs out of buffers, for example after a long frame | The sources restart at the cutscene time, so the sound stays aligned with the picture. Until the restart, the clock does not read the old position. |
| No clip, or a time after the clip duration | The sources stop. A `Hold` clip has no sound after its duration. |

`Pause`, `BeginScrub`, and `SetRate` with a rate other than one stop the sources in the call itself, because a host can skip the ticks of a player that does not play. The edit-mode preview does this.

An audio track plays one clip at a time. An overlapping clip on the same track adds one diagnostic. A missing component and a file that does not open add one diagnostic each.

### Audio Proxies

The manifest bake makes one `AudioProxyAsset` for each audio clip source file and stores it in `CutsceneAsset.AudioProxies`. A proxy is mono IMA ADPCM at 22050 Hz, about 11 KB for each second. It lets the player play sound in the same frame after a far jump, before the full decoder reaches the new position, and it gives the scrub grains. A source file needs a portable reference (a file in the game or engine asset folders, or a relative `SourcePath`), or it gets no proxy and one diagnostic. A later bake uses a stored proxy again when its source and settings did not change. A proxy whose source file changed after the bake is stale: it never plays, and the clip is silent after a jump until the full decoder is ready. Bake the manifest again after you change an audio source. The [architecture document](../../architecture/animation/cutscene-system.md#audio-proxies) gives the format, the encoder, and the source conversion.

### Tier Change

A clip whose source has a proxy plays the proxy (T0) at once after a jump, seeks the full decoder on a background task, and crossfades to the full decoder over 20 ms at a buffer boundary. The audio clock does not jump at the change. A clip with no proxy seeks the full decoder on the update thread, so the frame of the jump is long. If the background seek fails, the clip plays the proxy to its end and adds one diagnostic. The [architecture document](../../architecture/animation/cutscene-system.md#tier-change) gives the seek lead, the crossfade gains, and the alignment rules.

### Scrub Grains

While the transport does not stream (a pause, a scrub, a stop, or a rate other than 1) and the playhead moves, a track with a proxy plays one grain from the proxy at the playhead (`AudioScrubGrainPlayer`). A grain is 60 ms long, at the rate and the channel count of the full decoder, with a squared-sine fade of 10 ms at each end, so its first and last frames are zero. A move during a grain waits for the end of that grain, and then the newest position plays. A playhead that does not move plays nothing more. The grain buffers are allocated when the clip opens, so a grain does not allocate. Reverse playback at a rate of 2 or faster (`AudioTrackEvaluator.SilentReverseRate`) plays no grains; slower reverse playback does. A jump seek (for example a skip or a move to the start while paused) and a gate hold play no grains. A clip with no proxy, or with a stale proxy, is silent.

### Audio Clock

A cutscene with an audio track that has `DrivesClock` set (the default) follows the heard audio position: the clock runs on wall time, and the first such track corrects it in small steps. A difference of more than 100 ms is a hard resync: the clock jumps, the player raises `Discontinuity` with the `ClockResync` kind, and the events across the jump follow the seek policy of each track. Play dispatch never fires an event twice after a correction.

If the audio transport has no playback clock (WebAudio, or OpenAL without `AL_SOFT_source_latency`), the player uses wall time, sets `CutscenePlayer.IsAudioClockFallback`, and adds one diagnostic. The [architecture document](../../architecture/animation/cutscene-system.md#audio-clock) gives the warm-up, restart, and per-transport rules.

## Video Tracks

A `VideoTrack` shows `VideoTrackClip` entries on the `IVideoFrameTarget` component of its bound node. `UIVideoComponent` is that component: `SubmitFrame` copies the frame into a back buffer, and the next render pass uploads it through the same `IVideoFrameGpuActions` path as a streamed frame. While a track supplies frames, the component does not drain its own stream. `ClearFrame` gives the component back to its stream. A component with no `StreamUrl` streams nothing and logs no warning, so it can serve as a cutscene surface only. A node with no such component adds one diagnostic, and the track shows nothing. Set these values on a clip:

- `SourcePath`: the local video file. A relative path starts at the folder of the cutscene asset file. Any container and codec that the FFmpeg media backend decodes can play.
- `ClipOffset` and `Speed`: the clip time is the time in the file, as for the other tracks.

The frame cache of each track holds at most `VideoTrack.FrameCacheCapacity` frames (default 24) and at most `VideoTrack.FrameCacheBudgetBytes` bytes (default 64 MiB), and always one frame. A 1080p source uses about 143 MiB in total with a residency session, and a 4K source about 332 MiB (see [Frame Cache And Memory](../../architecture/animation/cutscene-system.md#frame-cache-and-memory)).

Each track opens one media session for its active clip, on a background task, with `StreamOpenOptions.ClockDrivenPresentation` set and the audio stream off. The audio of a video file does not play: put the sound on an `AudioTrack`. The video follows the cutscene time, which is the director time: the track passes it to `IMediaStreamSession.TryDequeueVideoFrame` in each tick.

| Transport | Frame |
|---|---|
| Forward playback at a positive rate | One exact seek, then the decoder runs forward. The cutscene time releases each frame. The track seeks again only after a jump, a move before the decoded range, or when the decoder falls more than 1 s behind (`CutsceneVideoChannel.MaxDecoderLagTicks`). |
| Drag of the playhead (a scrub seek), or reverse playback | A keyframe seek (`EVideoSeekMode.NearestKeyframe`) shows the keyframe at or before the time. The keyframe stays for a later time between it and its seek target, with no new seek. |
| A drag whose wanted time stays the same for 120 ms of wall time (`CutsceneVideoChannel.DragDwellSeconds`, the dwell of the residency planner), a pause, a gate hold, or a jump | An exact seek (`EVideoSeekMode.Exact`) shows the frame of the time. The editor sends a scrub seek in each frame while the ruler is held, so a held playhead reaches the exact frame by the dwell, not by the end of the seeks. |

A file that does not open, a source with no seek, and a failed seek each add one diagnostic and stop the clip. The track does not fake a seek: a live stream, a network stream, and an HLS playlist report `CanSeek == false`, so the clip shows nothing. With no media backend registered, the open fails with its diagnostic. A video track shows one clip at a time; an overlapping clip adds one diagnostic.

## Event Tracks

An `EventTrack` holds a list of `CutsceneEvent` entries. Each event has a `Time`, a `Key`, and a `Payload`. Events are the only side effect of a cutscene. They never change state that a seek or a scrub needs, so put that state on a sampled track.

Subscribe to `CutscenePlayer.EventFired` or `CutsceneDirectorComponent.EventFired`. The director subscription stays across a close and a later open. Each call gets a `CutsceneFiredEvent` with the track index, the event index, the event, and `FromSeek`.

| Movement | Result |
|---|---|
| Forward playback | Fires each crossed event once, in time order. An event at the end of a frame fires in that frame and not in the next one. After the open, a stop, or a backward move, an event at the start position also fires. |
| Reverse playback | Fires nothing. |
| Forward jump seek | Fires by `SeekPolicy`: `Skip` (default) fires nothing, `FireLatest` fires the last crossed event of each key, and `FireAll` fires all crossed events in time order. |
| Backward seek | Fires nothing. Forward playback fires the events again when it crosses them. |
| Scrub | Fires nothing, unless the track sets `FireDuringScrub`. Then a forward scrub step fires by `SeekPolicy`. |

The events of a tick fire after the evaluation, so a handler sees the state of the new position. A handler that throws adds one diagnostic for its track, and the other events still fire. A handler can call `Stop`, `Seek`, or `Play`. A stop or a seek ends the tick at once: the remaining events of the step do not fire, and `Finished` is not raised for that step. The state of the player is final before `Finished` is raised, so a `Finished` handler can stop or restart the player.

The editor preview sets `CutscenePlayer.IsPreview`. A preview fires only the tracks that set `FireInPreview`, so gameplay script does not run while an author moves the playhead. `CutscenePreviewHost.EventFired` gives those events to the editor. A recompile after an edit does not fire the events before the position again, and an event at the position that has not fired yet still fires. With a loop range, a play step stops at the loop edge, so events after the loop end do not fire.

An event track does not use its binding slot. The binding table does not check it.

### Completion

Put completion logic on `CutscenePlayer.Finished` or `CutsceneDirectorComponent.Finished`, not on an event at the end of a clip. The player raises `Finished` from its state: once when forward playback or a jump seek, for example a skip, reaches the end. A skip that jumps over the last events with `Skip` still raises it. A scrub and a stop do not raise it. A later move away from the end lets it be raised again.

## Simulation Control Tracks

A physics chain or a particle emitter keeps state from frame to frame, so its state is not a function of the time. A `SimulationControlTrack` tells what the simulations on its bound node do after a discontinuity. The track has no clips, and it applies for the full cutscene. It controls each `PhysicsChainComponent` and each `ParticleEmitterComponent` of the node. A discontinuity for the simulations is a jump seek, a scrub step, a camera cut, or a stop. A hard resync to the audio inside one shot is a small time correction: it resets only the temporal render history (see [Discontinuity Resets](#discontinuity-resets)), and the simulations continue. A resync and a cut in the same tick count as a cut.

| `Policy` | After a discontinuity |
|---|---|
| `SettleFromReset` (default) | The simulation resets to the current pose and then runs normally. |
| `WarmUp` | The simulation resets and then runs fixed steps for `WarmUpSeconds` (default 1 s). The steps use at most `WarmUpBudgetMilliseconds` (default 2 ms) of wall time in each frame, and the rest continue in the next frames. |

`Seed` (default 0) is the random seed of a particle emitter at a reset or a warm-up, so each run gives the same particles. Each emitter of a node adds its index times 7919 to the seed, so two emitters on one node do not repeat the same random pattern.

| Target | Reset | Warm-up | Hold |
|---|---|---|---|
| `PhysicsChainComponent` | `ResetSimulationToCurrentPose()`. The chain applies the reset at the start of its next simulation step, after it reads the current pose of its bones, and the reset replaces that step. A GPU chain then dispatches one step with a zero time scale, so the GPU particles and the GPU bone palette show the reset pose in that frame. The cutscene tick writes no bone. A chain with weight zero or disabled by distance drops the request, because it resets itself when it updates again. | None. A chain with `WarmUp` settles from the reset, and the track adds one diagnostic. | A reset request in each held frame. |
| `ParticleEmitterComponent` | `Prewarm(0, Seed, 0)`: clears the particles and sets the seed. | `Prewarm(WarmUpSeconds, Seed, WarmUpBudgetMilliseconds)`. The emitter runs steps of `PrewarmStepSeconds` (1/60 s), at least one in each call, and continues the rest in its own update in place of the normal step. All steps use the emitter transform of the `Prewarm` call, so the result does not depend on how the budget splits the steps across frames. | `BeginSimulationHold` and `EndSimulationHold`. A held emitter does not step. The hold is separate from `IsSimulating`, `Pause`, and `Resume`, and it does not change the tick registration. |

The player applies the resets and the warm-ups at the end of `Tick`, never in `Seek` or `Stop`. A stop releases a hold at once (this changes no user state), and its reset waits for the next tick. One discontinuity gives one reset of each simulation, also when a cut and a jump occur in the same tick. If two tracks bind the same node, the first track controls each simulation, and the second track adds one diagnostic. A node with no simulation adds one diagnostic. The targets are found again only when the bound node of a track changes, so a component that you add to or remove from the same node during playback is not seen until then.

During a scrub, each step resets the simulations, and they hold the reset pose. A held emitter is cleared once, not at each step. The hold ends, and a `WarmUp` track starts its warm-up, when the player plays at a rate within 0.05 of one (`SimulationControlEvaluator.ResumeRateTolerance`), when `EndScrub` ends a scrub, or at a jump seek (also during `BeginScrub`, for example the jump of a preview recompile). A scrub with only `UpdateScrub` seeks and no `BeginScrub` holds until playback at a rate near one or a jump. A tick with no discontinuity and no hold does no simulation work and does not allocate.

The warm-up budget measures the CPU time that records the emitter steps. The GPU runs the compute work later, so the budget does not limit the GPU cost. The budget applies to each emitter, so N emitters on a node can use N times the budget in one frame.

## Discontinuity Resets

At each `Discontinuity`, the camera router resets the target viewport once, after it claimed the camera of the new shot. A `Full` discontinuity (a seek, a camera cut, or a stop) resets the temporal render history (TAA, TSR, camera motion vectors, and temporal AO) and snaps auto exposure to the new shot. A `ClockResync` (a hard resync to the audio with no camera change) resets only the temporal render history. When the preview or the director gives the viewport camera back, the returning camera snaps to its own exposure. GI history and per-mesh motion history do not reset. Without a target viewport, no render state resets. The [architecture document](../../architecture/animation/cutscene-system.md#discontinuity-resets) lists each reset and its effect in each pipeline.

## Residency

The residency planner keeps the assets that the playhead needs resident in time. Each asset has three levels: T0 (proxy), T1 (coarse), and T2 (target). T0 of every asset loads at the open and stays until the close. The planner then requests T1 and T2 for the window around the playhead. Textures, audio sources, video sources, and sub-meshes have a backend. The planner, the session, and the backends are described in the [architecture document](../../architecture/animation/cutscene-system.md#residency).

The planner reads the baked manifest of the asset. A missing, stale, invalid, or unmatched manifest gives one diagnostic, and playback uses an approximate manifest. Bake the manifest again after each change to the asset or the scene.

### Residency In Playback

`CutsceneDirectorComponent` and `CutscenePreviewHost` each own one `CutsceneResidencySession` while a cutscene is open. The session closes at `Close`, at component deactivation, at `OnEndPlay`, and at destroy.

- **Backend:** the director uses `ResidencyBackendFactory`, or the static `CutsceneDirectorComponent.DefaultResidencyBackendFactory`. The editor registers the default in `CutscenePreviewSession.Initialize` as `new TextureResidencyBackend(Engine.Assets)`, and the preview session gives the same factory to each preview host. The windowed desktop composition (`RuntimeRenderingBootstrap`, with asset services) registers the same factory through `CutsceneResidencyRegistration.InstallDefaultBackendFactory`, so a game executable also gets texture residency. A headless host calls `CutsceneResidencyRegistration.InstallUnavailable`, so a director with no factory opens without residency and without the per-open diagnostic. If `ResidencyEnabled` is true (the default) and no factory gives a backend, the director opens without residency and adds a diagnostic. A `CutscenePreviewHost` created without a factory runs without residency.
- **Playback wait:** `PlaybackWaitEnabled` and `PlaybackWaitMaximumSeconds` on the director and on the preview host set the planner values. Both are off by default; the panel option is **Wait At Cuts**. When forward play crosses a camera cut whose shot is not resident, the gate limits the step to the last flick before the cut, so the frame never shows the new shot. The player stops the audio sources at once and keeps them stopped while `player.IsHeldByGate` is true. The hold ends when the targets are resident or the maximum wait passes; the sources start again in the same tick as the release. An event at the cut fires only after the release.

### Manifest Bake

`CutsceneBakeTool.Bake(asset, options)` runs the compiled timeline with the real `CutscenePlayer` and a `Manual` clock. It samples 4 times each second and at the start of each camera cut clip. For each sample, it updates the world matrices, finds the assets of each shown node with an `ICutsceneResidencyAssetCollector`, and computes the screen coverage of the bounding sphere in the view of the active camera (`CutsceneResidencyView`). A sphere behind the near plane covers nothing, so the camera orientation counts. An asset needs T2 at or above `TargetCoverage` (default 0.1) and T1 below it. Each sample gives one bucket that ends at the next sample, and a bucket that starts at a camera cut has the cut flag. Because the bake uses the real evaluator, the result includes the animated poses, the clip starts and offsets, and the spawned objects. The bake restores every recorded value at the end and destroys the spawned instances.

- **Inputs:** `CutsceneBakeOptions` takes the binding resolver, the runtime slots, the scene roots (so that objects that no track binds are also seen), the collector (default `CutsceneRenderableAssetCollector`, which reads the sub-meshes and the 2D textures of `ModelComponent` LOD materials; it skips a texture with no file, such as a generated material default, because streaming cannot load it), and the audio decoder factory.
- **Asset keys:** the default collector records stable keys, not runtime IDs, so a manifest stays valid after a new import of the same model, on another machine, and in another project folder. A texture key uses its persisted ID or its portable source reference and sampler name. A sub-mesh key uses its place in the scene: the scene, the node path, the component index, and the sub-mesh index. A renamed texture file, a renamed node on a sub-mesh path, and a file outside the game and engine asset folders change the keys. See [Asset Keys](../../architecture/animation/cutscene-system.md#asset-keys).
- **T0 budget:** the T0 size of a texture is a 64 px uncompressed preview with mips (about 21 KB). The T0 size of a sub-mesh is the payload size of its lowest LOD (`SubMeshCookedCodec.GetCoarsestLodPayloadLength`): the `PayloadLength` of the cooked LOD descriptor, or for an editor sub-mesh the length that a cook would write for that mesh. A fixed size for each vertex is wrong for avatars, whose blendshape and skinning data make a vertex much larger. The T0 size of an audio proxy is the length of its encoded data (see [Audio Proxies](#audio-proxies)). The bake returns a `CutsceneT0Report`. If the total is above `T0BudgetBytes` (default 64 MiB), the bake throws `CutsceneBakeException`, whose message lists the largest assets. The bake does not remove assets to fit.
- **Mesh LOD warnings:** T0 of a sub-mesh is its coarsest LOD, so progressive mesh loading needs LODs. Automatic LOD generation stays off by default, also for cutscene models. `CutsceneMeshLodBakeCheck` gives one warning with the count of sub-meshes that have one LOD, and one warning with the sub-meshes whose lowest LOD has more than 50% (`ProxyReductionThreshold`) of the LOD 0 index count, with the first 5 names.
- **Store:** the bake does not change the asset. Set `asset.Manifest` to the result, `asset.ManifestContentHash` to `CutsceneManifestHash.Compute(asset)`, and `asset.AudioProxies` to the proxies of `Bake(asset, options, out report, out audioProxies)`. The panel **Bake Manifest** button does this.
- **Stale manifest:** any change to the asset after the bake makes the manifest stale, and playback then uses the approximate manifest for the whole timeline. An edit inside a referenced asset, for example an animation clip, does not make the manifest stale. See [Manifest, Keys, And Hash](../../architecture/animation/cutscene-system.md#manifest-keys-and-hash).
- **Cook:** `CutsceneCookCheck.GetRejection(asset, shippingCook: true)` gives the reason to reject an asset whose manifest is missing, stale, or invalid. A development cook accepts it. `ProjectBuilder` does not call this check, so a cook does not reject such an asset. Call the check in your own cook step if a shipping build must not contain one.

## Author A Cutscene

1. Open **Cutscene Sequencer** from the panel menu, next to **Animation Clip Editor**.
2. Select a scene node. Click **Add Director To Selected** if the node has no `CutsceneDirectorComponent`.
3. Click **Create Cutscene Asset**. The new asset has a length of 10 seconds.
4. Click **Start Preview**.
5. Select the node to animate, and click **Add Transform Track**. The track has one clip that covers the whole cutscene, with one key at the current transform. The node does not move yet.
6. Click the clip to select it. Move the playhead, move the bound node, and click **Add Key At Playhead**. The key always uses the transform of the node that is bound to the track. **Remove Key At Playhead** removes a key within half a frame of the playhead.
7. Select a node that has a `CameraComponent`, and click **Add Camera Track** to show that camera while the clip is active.
8. Click **Save Asset**. A new asset goes to the `Cutscenes` folder of the game assets.

Save the scene after you end the preview. The scene stores the director and its reference to the asset.

## Panel Controls

| Control | Action |
|---|---|
| Ruler drag | Scrubs. The position snaps to a frame at the **FPS** value. |
| Clip body drag | Moves the clip. |
| Clip edge drag | Trims the clip. A trim of the start keeps the content in place. |
| `J` / `L` | Plays backward or forward. Another press in the same direction goes to the next rate: 1, 2, then 4. |
| `K` | Pauses. |
| `Space` | Plays or pauses. |
| Left / Right arrow | Pauses and moves one frame. |
| **Loop** | Keeps preview playback inside the start and end values. |
| **Bake Manifest** | Bakes the residency manifest in edit mode, with no preview open, with the **T0 Budget (MiB)** value. The result message gives the T0 total and the largest assets. |
| **Wait At Cuts** | Sets the playback wait of the open preview or director. |
| `Ctrl` + mouse wheel | Changes the zoom of the time axis. |

Move, trim, key, track, and duration edits use `Undo.RecordStructuralChange`, so Ctrl+Z reverts them. The asset is read-only in play mode.

The residency strip is under the ruler. Each bucket of the manifest is a bar, and a white tick marks a camera cut. While a preview or a director is open, the color shows how many entries of the bucket have their target level: green for all, yellow for some, and red for none. Without an open cutscene, the color shows the highest level of the bucket. A missing, stale, invalid, or unmatched manifest draws an orange marker over the timeline, because playback uses the approximate manifest. The manifest state is the same with and without a preview: with no preview, the panel computes the state that a session would select (`TextureResidencyBackend.GetManifestState`), which also checks that the baked textures are loaded. **Unmatched** ("does not match the scene") means that the hash is current but too many baked textures are not loaded; a new bake helps only when the assets moved or are outside the asset folders. A yellow line shows the cut at which the playback wait holds. The strip reads the levels of all manifest assets in one `IResidencyBackend.GetLevels` call for each frame, and decodes the visible buckets with a cursor. The panel computes the manifest state only after a bake, after a model import ends, or after edits from the panel stop for 0.25 s, so a clip drag runs it one time. The status line is cached and built again only when one of its values changes. The line above the strip shows the manifest state, the asset count, the pending T0 loads, the generation, the timed-out waits, and the rejected texture requests. A bake does not record an undo step.

## Edit-Mode Preview

`CutscenePreviewHost` has this contract:

- `Begin(asset)` compiles the asset and shows position zero at the first tick.
- `Seek` stores a target. The last seek before a tick wins.
- `Tick` applies an edit and a seek, then evaluates once.
- `NotifyAssetEdited` asks for a recompile at the next tick. The position, the rate, and the transport state stay. The position is limited to the new duration.
- `End` gives the camera back to the viewport, destroys spawned instances, and restores every recorded value. `KeepState` and `ForceRestoreAll` have no effect on a preview.

All host calls must run on the update thread. `EditorImGuiUI.EnqueueSceneEdit` moves a panel request to that thread.

Edit mode is not a stopped world. `RuntimeWorldHost.BeginEditModeAsync` turns off physics and the game mode, and then starts the world. The world ticks, and `PostUpdateFrame` runs `ProcessDirtyTransforms`, so a transform that the preview writes during `UpdateFrame` is flushed in the same frame.

The preview does not mark a scene as changed, and it stops the undo system from recording its writes. Because the values are live, these rules apply:

- Entering play mode ends the preview before the world snapshot.
- Closing the panel ends a preview after the panel was shown.
- A save of a scene or a world during a preview fails with an error that tells you to end the preview. `AssetManager.SaveGuard` does this check on all save paths. A save of a cutscene asset is allowed.
- A move of a bound node with the gizmo during a pause stays until the next evaluation. Add a key to keep it. `End` restores the original value.
- `End` removes the transform changes of bound nodes that the undo history recorded since `Begin`, because those entries hold preview values. Other changes and key edits stay. After a preview, Ctrl+Z cannot undo a gizmo move of a bound node that you made during the preview.
- A change of the world closes the preview and restores the values. The reason is in the diagnostics.

## Play A Cutscene

Assign `CutsceneDirectorComponent.Asset`, then call `OpenAsync`. The director registers its tick after input and before animation. Use `director.Player` for the transport, and call `Close` to restore the targets that have the `RestoreState` setting.

A director needs a binding resolver. It uses `BindingResolver` if the property has a value. If not, it uses the static `CutsceneDirectorComponent.DefaultBindingResolverFactory`. A windowed desktop host (the editor, `XREngine.VRClient`, and a published game) registers this factory in `RuntimeRenderingBootstrap` through `CutsceneBindingRegistration.InstallSceneResolverFactory`, so a director opens with no game code. The editor sets its own factory in `CutscenePreviewSession.Initialize`. The bootstrap does not replace a factory that the editor or game code set first. Each factory searches the root nodes of the world of the director. `CutsceneSceneBindingResolver` finds a scene object only under those root nodes. The world copy of play mode keeps the object identifiers of the edit world, and the object cache can hold the edit node, so the resolver does not use a cached node from another world. A headless host registers no factory, so an application there must register its own. Without a resolver, the open fails and the reason is in `director.Diagnostics`.

Set `TargetViewport` to show the camera that the cut tracks select. The director claims the camera of that viewport until it closes.

A claim changes only the camera that shows the scene. The screen-space UI of the viewport stays on the camera from before the claim, so the editor panels and a player HUD stay visible. A gameplay system must hide a HUD itself if a cutscene must not show it.

### Progressive Open

To open with T0 ready for an instant seek, use `new CutsceneOpenSequence(director).RunAsync(progress, cancellationToken)` in place of `OpenAsync`. The sequence opens the director, finds the proxy materials (the material of the lowest LOD of each sub-mesh of the bound nodes and their descendants, listed in `ProxyMaterials`), and requests their programs at once. At the end of each director frame it reads `CutsceneResidencySession.PendingProxyCount` and the program state of each proxy material. When both are complete, it sets `CutscenePlayer.ReadyForInstantSeek`, requests the programs of the other LOD materials as lower-priority work, and the task gives true. T0 stays resident until the close, so a later seek loads no T0 data.

- The editor uses the sequence for both open paths: the edit-mode preview (`CutscenePreviewHost.BeginWithOpenSequence`, from `CutscenePreviewSession.OpenPreview`) and the play-mode director (`CutscenePreviewSession.OpenDirector`). Both run it without waiting on its task. MCP `cutscene_status` and the residency strip of the sequencer panel show `readyForInstantSeek`, the pending T0 loads, and the pending and failed proxy programs. `cutscene_open` with `close` before the sequence finished cancels it and restores the scene.
- The sequence works on any `ICutsceneOpenHost`. It advances at the end of each host frame, on the update thread. It never waits for the render thread, and a caller must not block on its task.
- The proxy material of a renderable mesh is its material override when it has one, otherwise the material of the lowest LOD.
- Without a residency session, T0 residency is not known: the sequence adds one diagnostic, `ReadyForInstantSeek` stays false, and the task gives false.
- A preview recompile makes a new player, whose `ReadyForInstantSeek` is false. Open the preview again to measure it.
- The default prewarmer (`CutsceneMaterialProgramPrewarmer`) uses the asynchronous uber variant build of the material and its state. A material without uber state is ready at once. This layer cannot see the backend program link or a Vulkan pipeline compile, which the backend mesh renderer makes at the first draw. Pass another `ICutsceneProgramPrewarmer` to change this.
- A failed proxy program adds a diagnostic to `director.Diagnostics` with the material name and the reason. The task gives false, `ReadyForInstantSeek` stays false, and the cutscene stays open.
- A cancel closes the host at the next host frame, which restores the bound nodes and destroys the spawned instances. The task is then canceled. A `Close` from another caller also cancels the task.

### VR Camera Modes

In VR, a cutscene never takes head tracking away from the user. No mode writes an eye camera or the head pose, and no mode changes the VR view mode, the VR render pipeline, or the desktop mirror mode (see the exact VR pipeline selection contract in [OpenXR VR rendering](../../architecture/rendering/openxr-vr-rendering.md)). `CutsceneAsset.VrCameraMode` selects the mode:

| Mode | Behavior |
|---|---|
| `SpectatorOnly` (default) | The shot camera shows only on `TargetViewport`, the desktop viewport. The headset view does not change. |
| `RigAnchor` | The rig origin follows the position and the heading of the shot camera, and the user is head-tracked relative to the shot. Each camera cut and each full discontinuity (a seek, a scrub step, or a stop) fades the headset through black. |
| `WorldScreen` | Not supported. The open fails in VR (see below). |

The mode applies only when a VR session runs at the open (`ICutsceneVrHost.IsInVR`). Outside VR, each mode shows the shots on the desktop viewport only. The director reads VR state through `CutsceneDirectorComponent.VrHost`; if it is null, it uses `RuntimeCutsceneVrHost.Instance`, which reads `RuntimeEngine.VRState` and the presentation settings.

**Failures.** `CutsceneVrCameraRouter.Validate` checks the mode before the open changes anything. A mode that cannot run fails the open: the director adds the reason to `Diagnostics` and stays closed. It does not claim a viewport, move the rig, or use another mode. These cases fail:

- `TargetViewport` renders a headset eye (in any mode). A cutscene claims only a desktop viewport.
- `RigAnchor` when the VR runtime gives no headset fade. Only OpenVR has one (the compositor fade), so `RigAnchor` fails on OpenXR.
- `RigAnchor` with no `VrRigOrigin`, or with a rig origin that is not a `Transform`.
- `RigAnchor` with a rig origin that is the tracked head (`ICutsceneVrHost.HeadTransform`, the HMD node of `RuntimeVrState.ViewInformation`), an eye camera transform, or not an ancestor of the head. A move of such a transform would write the head pose or an eye camera, or would not carry the user. If the session published no head, the open also fails, because the check cannot run.
- `WorldScreen`. The engine can render a camera to a texture (`AdvancedOffscreenTextureCaptureComponent`), but no component shows a completed capture on a surface in the world: no material binds the capture lease in the eye pass and releases it after the GPU read. The mode stays unsupported until that consumer exists.
- A mode value that is not known.

If the VR mirror mode is `Off`, `BlitSubmittedEye`, or `CyclopeanReconstruct`, the desktop does not show the claimed viewport. The open then adds one note to `Diagnostics` and does not change the mirror mode.

**RigAnchor.** Set `CutsceneDirectorComponent.VrRigOrigin` before the open. Use an ancestor of the tracked headset, and that no other system writes during the cutscene, for example the root of a flying VR pawn. A character pawn root is a physics body, and `VRPlayerCharacterComponent` writes its playspace root in each frame, so neither is a good rig origin.

In each frame, the rig origin follows the position and the heading of the shot camera, and it stays upright. A cut or a `Full` discontinuity fades the headset to black, moves the rig, holds black for at least two render frames, and fades back in over `CutsceneAsset.RigCutFadeSeconds` (default 0.5 s). A transport stop keeps the rig at the last shot; only the close puts the rig back. A fade failure or a loss of the OpenVR compositor puts the rig back at once and adds one diagnostic. The [architecture document](../../architecture/animation/cutscene-system.md#vr-modes) gives the fade, hold, teleport, and close rules.

## MCP Tools

| Tool | Arguments | Result |
|---|---|---|
| `cutscene_open` | `node_id`, `asset_path`, `close` | Opens a cutscene, or closes it when `close` is true. |
| `cutscene_seek` | `seconds`, `intent` (`jump` or `scrub`) | Moves the position and evaluates before the call returns. |
| `cutscene_play` | `rate`, `pause` | Plays at a rate, or holds the position. |
| `cutscene_status` | none | Reads the state. |

Each tool returns `open`, `mode` (`preview`, `director`, or `closed`), `asset`, `position`, `state`, `duration`, `rate`, `compileCount`, and `diagnostics`. Times are in seconds.

While a cutscene is open, each tool also returns `audioClock`: the clock `mode`, `fallback`, `muted`, `differenceMs` (the heard position minus the clock position before the correction, from the last play step that read the clock), `reads` (the number of play steps that read the clock), `hardResyncs`, and `latencyMs` (the output latency of the last reading). The same values are on `CutscenePlayer`: `AudioClockDifference`, `AudioClockReadCount`, `AudioHardResyncCount`, and `AudioClockLatencyNanoseconds`. The counts start at zero when the player is created, so a recompile of a preview resets them.

In edit mode, `cutscene_open` starts a preview. In play mode, it opens a director, and `asset_path` is not available. Without `node_id` or `asset_path`, the tool uses the selected node, and then the only director in the world.

The tools run on the update thread. A capture after `cutscene_seek` shows the new time, because the transform flush runs later in the same frame.

```text
cutscene_open { "node_id": "<director-node-id>" }
cutscene_seek { "seconds": 2.5 }
capture_viewport_screenshot { "output_dir": "<absolute-capture-folder>" }
cutscene_open { "close": true }
```

## Limits

- The panel has no split, snap to other clips, or multi-select.
- The panel has no buttons for event tracks, property tracks, audio tracks, or simulation control tracks. Add them in the asset.
- A property track supports only `float`, `Vector3`, and `Quaternion` members.
- An audio track decodes on the update thread, in the director tick. A clip with no audio proxy has no scrub sound, and a jump starts its sound after the decoder seek on the update thread: the clock does not count the seek time, but the frame of the seek is long. A clip with a current proxy plays the proxy at once and seeks in the background (see [Tier Change](#tier-change)). The proxy is mono, so a stereo clip is mono until the crossfade.
- The first refill after the first restart on a listener allocates about 700 bytes, with or without a proxy: the listener buffer pool makes its fifth `AudioBuffer`, because the restart keeps four buffers in the queue and the refill takes one more before the pool has one. Later restarts, play steps, and scrub grains do not allocate.
- An Ogg Vorbis seek decodes and drops the frames up to the target, because the NVorbis seek is not exact. A forward seek decodes from the current position, so its cost grows with the distance. A backward seek decodes the file again from its start, so its cost grows with the target time. Both cost about 1.1 to 1.6 ms per second of skipped 48 kHz stereo audio: 140 ms for a jump to 120 s and 310 to 390 ms for a jump of 240 to 280 s. A WAV seek is immediate.
- NVorbis 0.10.5 allocates on the heap for each decoded packet (new sample arrays, floor data, and packet objects). Our decoder adds no allocation, but Ogg Vorbis playback allocates about 21 KB for each 2048 decoded frames. At 48 kHz stereo this is about 0.5 MB for each second of audio, or about 8 KB for each 60 Hz frame. A seek allocates the same amount for each skipped second: about 53 MB for a jump to 120 s and 106 to 122 MB for a jump of 240 to 280 s. WAV playback and WAV seeks do not allocate. Use WAV for long cutscene audio if the garbage collector cost matters.
- `AudioData` imports WAV, Ogg Vorbis, and MP3. A FLAC import returns false, because no decoder supports FLAC.
- Bindings created by the panel use the object ID and a node path. Root node names must stay the same for the path fallback.
- A headless host registers no default resolver.
- Progressive mesh loading needs cooked sub-meshes of codec version 2 with more than one LOD. The world load policy is `All`, so a normal world load makes every LOD resident, and the mesh backend has nothing to load for that content. Only content that a host loads with `SubMeshLodPayloads.UseLoadPolicy(ELodPayloadLoadPolicy.ProxyFirst)` starts with the coarsest LOD only. The director instantiates spawn bindings with the default policy, so they also load all LODs. The editor preview reads the source assets, so it always has all LODs. A cooked `Model` writes its sub-meshes in version 2 too, through the generic nested-object hook (see [Mesh LOD Payloads](../../architecture/assets/cooked-asset-aot-and-io.md#mesh-lod-payloads)). A play-mode snapshot or a clone keeps the residency of each LOD.
- A video track plays local files only. It does not play the audio of the file. Reverse playback and a drag show keyframes, so they are not smooth for a codec with long keyframe intervals; a return to a cached frame is smooth. A moving drag and steady forward playback allocate nothing on the update thread. The residency backend decodes with its own session, so a source can decode two times. The T0 size of a video asset in the manifest is 0, because the frame size is not known before the file opens. A bake of a cutscene with video tracks opens real media sessions in the background, and closes them at the end. The panel has no button for video tracks; add them in the asset.
- The inter-frame keyframe seek is checked on QuickTime files only, because the FFmpeg build of the project has no encoders and only its QuickTime muxer keeps the hand-written qtrle frames (see [Seek Accuracy](../../architecture/animation/cutscene-system.md#seek-accuracy)).
- The `Cutscene` budget profile limits do not change the refine time on OpenGL or on Vulkan (see [Budget Profiles](../../architecture/rendering/texture-streaming.md#budget-profiles)). On OpenGL, each texture upload of a cold jump adds about 5 to 9 ms to one frame, with either profile.
- The T0 budget default (64 MiB) is checked against one scene only: Sponza (37 textures, 25 sub-meshes) needs 15.6 MiB, and most of it is mesh data.
- The design limits of the system (Vulkan, video upload, texture refine, mesh LOD loading, and VR) are in [Known Limits](../../architecture/animation/cutscene-system.md#known-limits).
