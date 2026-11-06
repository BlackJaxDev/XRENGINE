# Cutscenes

This guide describes the cutscene timeline: how to author a cutscene, how to preview it in the editor, how to play it, and how to drive it through MCP.

## Parts

| Part | Project | Responsibility |
|---|---|---|
| `CutsceneAsset` | `XREngine.Runtime.CutsceneIntegration` | Holds the tracks, the clips, and the bindings. It holds no runtime state. |
| `CutsceneCompiler` | `XREngine.Runtime.CutsceneIntegration` | Validates an asset and builds the time index. |
| `CutscenePlayer` | `XREngine.Runtime.CutsceneIntegration` | Holds the transport and evaluates the tracks. Each evaluation uses only the time. |
| `CutsceneDirectorComponent` | `XREngine.Runtime.CutsceneIntegration` | Opens an asset in a world and ticks the player. |
| `CutscenePreviewHost` | `XREngine.Editor` | Plays an asset in edit mode and restores the scene. |
| `CutscenePreviewSession` | `XREngine.Editor` | Owns the one cutscene that the panel and the MCP tools control. |

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
| Jump seek to a new time during playback | Every active source stops, the decoder seeks, and the sources play again from the new time. |
| Pause, scrub (with or without `BeginScrub`), stop, or any rate other than 1 | The sources stop and their gain is zero. Playback at rate 1 starts again from the current time. |
| A listener comes into range | All sources of the track restart together, so they stay aligned. |
| A source runs out of buffers, for example after a long frame | The sources restart at the cutscene time, so the sound stays aligned with the picture. Until the restart, the clock does not read the old position. |
| No clip, or a time after the clip duration | The sources stop. A `Hold` clip has no sound after its duration. |

`Pause`, `BeginScrub`, and `SetRate` with a rate other than one stop the sources in the call itself, because a host can skip the ticks of a player that does not play. The edit-mode preview does this.

An audio track plays one clip at a time. An overlapping clip on the same track adds one diagnostic. A missing component and a file that does not open add one diagnostic each.

### Audio Clock

A cutscene with an audio track that has `DrivesClock` set (the default) starts its clock in `AudioDisciplined` mode. In each play step, after the clock advances and before the evaluation and the event dispatch, the player reads the heard position from the first such track of the last evaluation. The track reads the playback clock of its first source through `IAudioTransport.TryGetPlaybackClock`. The heard position is the frame count before the queue, plus the source position, minus the output latency. While that value is below the frame of the last restart, the listener does not hear the new queue yet (for example during the 300 ms NAudio output latency). The clock then stays on wall time with no correction, and this is not a loss of the clock: no diagnostic and no fallback. The same applies when the sources wait for a restart because their queues differ. A warm-up has a bound: if it lasts longer than the output latency plus 250 ms (and at least 500 ms) of play steps after the restart, the device does not reach the queue, and the player treats it as a lost clock, with the fallback and its one diagnostic below. The first valid heard position after a restart (a start, a seek, a resume, a return to rate 1, or a new listener) moves the clock to that position in one step, if the difference is 100 ms or less. During the warm-up the clock runs on wall time, so it is ahead of the sound by about the output latency. The step removes this offset at once. It is not a hard resync: it raises no `Discontinuity`, and the high-water mark below keeps a backward step from firing an event again. After that step, the clock moves toward the heard position by at most 5 percent of the frame step. When the difference is more than 100 ms, the clock jumps (a hard resync): the player raises `Discontinuity` with `Jump` intent, and the events across the jump follow the seek policy of each track.

A restart runs in the tick: it stops the sources, seeks the decoder, fills the queue, and plays. The sources start only at its end, so the player removes from the clock advance of the next tick the time that delayed the clock track: the restarts in service order up to and including the restart of the clock track. A restart of another track while the clock track plays on removes nothing. If no track drives the clock, nothing is removed. A long decoder seek therefore makes one long frame, but no hard resync. The removed time is wall time; it is not scaled by a time dilation. A track that becomes the clock track with no restart, for example when the clip of the earlier clock track ends, slews and does not step.

The clock reads the first source of the track, so only that source controls the clock reading and a restart of all sources. If the first source stops before the end of the data, all sources restart. The first such stop after a valid clock reading, for example after one long frame, does not count toward the warm-up bound. Later consecutive stops with no valid reading between them keep the warm-up time, so a first source that never plays reaches the bound and the fallback. Their restart time is not removed from the clock.

Another source that stops (for example, the source of a second listener) restarts alone, once. It starts at the buffer that the first source plays, so it can be up to one buffer (2048 frames) behind. It gets another restart only after it plays a buffer, or after a restart of all sources. This does not need a clock reading, so it also works on a track that does not drive the clock. A seek or a resume starts a new count of starves for the warm-up bound. If it stops again, the track adds one diagnostic for that listener and gives the source no sound until the next restart of all sources. The first source and the clock continue. After the sources play the last buffer of the clip and stop, the player does not read the heard position. The listener still hears the output latency there, so the value stays behind the clock.

Play dispatch keeps a high-water mark: a play step never fires an event at or below the highest position that playback reached. So a clock correction never fires an event twice. A seek, a hard resync, and a stop move the mark to the new position. Reverse playback followed by forward playback does not fire the crossed events again. A seek back does.

| Transport | Clock |
|---|---|
| OpenAL | `AL_SAMPLE_OFFSET_LATENCY_SOFT` from `AL_SOFT_source_latency`. Without the extension, there is no clock. |
| NAudio | The mixer position. The latency is the `DesiredLatency` of the `WaveOutEvent` output, which is an estimate. |
| WebAudio | No clock. |

If the transport has no clock, the player changes the clock to `Wall` mode, sets `CutscenePlayer.IsAudioClockFallback`, and adds one diagnostic. The clock goes back to `AudioDisciplined` when the transport reports a clock again, and also at a stop.

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

## Residency

`CutsceneResidencyPlanner` decides which level of each asset must be resident, and by which time, around the playhead. The levels are T0 (proxy), T1 (coarse), and T2 (target). The planner sends requests to an `IResidencyBackend` and does no IO itself. It is not connected to `CutscenePlayer` or the director yet, and no texture or mesh backend exists yet.

- **Input:** a `CutsceneResidencyManifest` holds a shared asset table and `ResidencyBucket` entries in time order, with no overlap. Each `ResidencyEntry` gives an asset index, a target level, and a screen coverage. A bucket can mark a camera cut at its start.
- **Calls:** call `Open` one time. It requests T0 for each asset. Then call `Update(position, rate, seek, deltaSeconds)` one time in each frame on the update thread. `seek` is null when no seek occurred. A backend reports complete work with `NotifyCompleted`, from any thread. The next update applies it and raises `LoadCompleted`.
- **Plan:** an update plans again only when the playhead goes into another bucket, the window covers other buckets, the rate changes, a seek occurs, the dwell gate changes, or a load completes. Otherwise it allocates nothing and sends nothing. The window is 5 seconds ahead at rate 1, scaled by the absolute rate, in the direction of travel, plus 0.5 seconds behind. At rate 0 (a pause), the window is the forward window of rate 1, so a pause does not cancel the prefetch. The planner requests one level above the resident level at a time. Requests sort by level, then by the time until the playhead needs them, then by screen coverage in descending order. A bucket behind the playhead sorts after the buckets ahead of it. Running work of the current generation stays while the window needs its level or a higher one, so a load that completes during an update is not cancelled. A jump, an asset that leaves the window, and a closed dwell gate cancel the running work.
- **Jump:** a `Jump` increments `Generation` and cancels the work of older generations. A late result of an older generation runs no callback.
- **Scrub:** a `Scrub` sends nothing above T0 until the playhead stays within 50 ms of timeline time for 120 ms of wall time. A new drag cancels the T1 and T2 work that did not complete. A `Jump` does not use this gate.
- **Eviction:** `Evict(maxCount)` releases T1 and T2 data. First it releases the assets outside the request window, farthest from the playhead first. Then it releases the assets in the window, farthest deadline first. It never releases T0.
- **Backend contract:** `IResidencyBackend.Cancel` on a complete ticket releases the data that the ticket holds. Set the level that `GetLevel` reports before the call to `NotifyCompleted`. A level that only a cache supplies, with no ticket, is not held.
- **Playback wait:** `PlaybackWaitEnabled` is off by default. When it is on, forward play that crosses a camera cut sets `IsHoldingPlayback` until the next shot has its target levels or `PlaybackWaitMaximumSeconds` (default 1 second) passes. The caller holds the clock at `PlaybackHoldTime`. A jump, a scrub, and reverse play never hold. `PlaybackWaitTimeoutCount` counts the waits that reached the maximum.
- **Close:** `Close` cancels all work and releases all levels, T0 included. After it, a completion runs no callback and `Update` does nothing.

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
| `Ctrl` + mouse wheel | Changes the zoom of the time axis. |

Move, trim, key, track, and duration edits use `Undo.RecordStructuralChange`, so Ctrl+Z reverts them. The asset is read-only in play mode.

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

A director needs a binding resolver. It uses `BindingResolver` if the property has a value. If not, it uses the static `CutsceneDirectorComponent.DefaultBindingResolverFactory`. The editor registers this factory at startup in `CutscenePreviewSession.Initialize`, and the factory searches the root nodes of the world of the director. `CutsceneSceneBindingResolver` finds a scene object only under those root nodes. The world copy of play mode keeps the object identifiers of the edit world, and the object cache can hold the edit node, so the resolver does not use a cached node from another world. Another application must register its own factory. Without a resolver, the open fails and the reason is in `director.Diagnostics`.

Set `TargetViewport` to show the camera that the cut tracks select. The director claims the camera of that viewport until it closes.

A claim changes only the camera that shows the scene. The screen-space UI of the viewport stays on the camera from before the claim, so the editor panels and a player HUD stay visible. A gameplay system must hide a HUD itself if a cutscene must not show it.

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
- The panel has no buttons for event tracks, property tracks, or audio tracks. Add them in the asset.
- A property track supports only `float`, `Vector3`, and `Quaternion` members.
- An audio track decodes on the update thread, in the director tick. It has no proxy audio and no scrub sound, so a jump starts the sound after the decoder seek. The clock does not count the seek time, but the frame of the seek is long.
- An Ogg Vorbis seek decodes and drops the frames up to the target, because the NVorbis seek is not exact. A forward seek decodes from the current position, so its cost grows with the distance. A backward seek decodes the file again from its start, so its cost grows with the target time. Both cost about 1.1 to 1.6 ms per second of skipped 48 kHz stereo audio: 140 ms for a jump to 120 s and 310 to 390 ms for a jump of 240 to 280 s. A WAV seek is immediate.
- NVorbis 0.10.5 allocates on the heap for each decoded packet (new sample arrays, floor data, and packet objects). Our decoder adds no allocation, but Ogg Vorbis playback allocates about 21 KB for each 2048 decoded frames. At 48 kHz stereo this is about 0.5 MB for each second of audio, or about 8 KB for each 60 Hz frame. A seek allocates the same amount for each skipped second: about 53 MB for a jump to 120 s and 106 to 122 MB for a jump of 240 to 280 s. WAV playback and WAV seeks do not allocate. Use WAV for long cutscene audio if the garbage collector cost matters.
- `AudioData` imports WAV, Ogg Vorbis, and MP3. A FLAC import returns false, because no decoder supports FLAC.
- Bindings created by the panel use the object ID and a node path. Root node names must stay the same for the path fallback.
- The default resolver is registered by the editor only.
