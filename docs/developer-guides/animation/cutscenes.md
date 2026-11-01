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

## Author A Cutscene

1. Open **Cutscene Sequencer** from the panel menu, next to **Animation Clip Editor**.
2. Select a scene node. Click **Add Director To Selected** if the node has no `CutsceneDirectorComponent`.
3. Click **Create Cutscene Asset**. The new asset has a length of 10 seconds.
4. Click **Start Preview**.
5. Select the node to animate, and click **Add Transform Track**. The track has one clip that covers the whole cutscene, with one key at the current transform. The node does not move yet.
6. Click the clip to select it. Move the playhead, move the node, and click **Add Key At Playhead**. **Remove Key At Playhead** removes a key within half a frame of the playhead.
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
- A scene save during a preview writes the preview values. End the preview before you save the scene.
- A move of a bound node with the gizmo during a pause stays until the next evaluation. Add a key to keep it. `End` restores the original value.

## Play A Cutscene

Assign `CutsceneDirectorComponent.Asset`, then call `OpenAsync`. The director registers its tick after input and before animation. Use `director.Player` for the transport, and call `Close` to restore the targets that have the `RestoreState` setting.

A director needs a binding resolver. It uses `BindingResolver` if the property has a value. If not, it uses the static `CutsceneDirectorComponent.DefaultBindingResolverFactory`. The editor registers this factory at startup in `CutscenePreviewSession.Initialize`, and the factory searches the root nodes of the world of the director. Another application must register its own factory. Without a resolver, the open fails and the reason is in `director.Diagnostics`.

Set `TargetViewport` to show the camera that the cut tracks select. The director claims the camera of that viewport until it closes.

## MCP Tools

| Tool | Arguments | Result |
|---|---|---|
| `cutscene_open` | `node_id`, `asset_path`, `close` | Opens a cutscene, or closes it when `close` is true. |
| `cutscene_seek` | `seconds`, `intent` (`jump` or `scrub`) | Moves the position and evaluates before the call returns. |
| `cutscene_play` | `rate`, `pause` | Plays at a rate, or holds the position. |
| `cutscene_status` | none | Reads the state. |

Each tool returns `open`, `mode` (`preview`, `director`, or `closed`), `asset`, `position`, `state`, `duration`, `rate`, `compileCount`, and `diagnostics`. Times are in seconds.

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
- The timeline has no event track type, so `CutscenePreviewHost.FireEventsInPreview` is always false.
- Bindings created by the panel use the object ID and a node path. Root node names must stay the same for the path fallback.
- The default resolver is registered by the editor only.
