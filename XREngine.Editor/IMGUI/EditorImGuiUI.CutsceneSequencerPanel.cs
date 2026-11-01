using System.Numerics;
using ImGuiNET;
using XREngine.Components;
using XREngine.Cutscenes;
using XREngine.Editor.Cutscenes;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private const float CutsceneRulerHeight = 24.0f;
    private const float CutsceneLaneHeight = 26.0f;
    private const float CutsceneLaneHeaderWidth = 180.0f;
    private const float CutsceneTrimHandleWidth = 6.0f;

    private static readonly double[] _cutsceneShuttleRates = [1.0, 2.0, 4.0];
    private static readonly List<CutsceneCompileDiagnostic> _cutsceneDiagnostics = [];

    private static CutsceneDirectorComponent? _cutsceneDirector;
    private static CutsceneClip? _cutsceneSelectedClip;
    private static int _cutsceneSelectedTrack = -1;
    private static int _cutsceneFramesPerSecond = 30;
    private static float _cutscenePixelsPerSecond = 80.0f;
    private static bool _cutsceneScrubbing;
    private static string? _cutsceneOpenMessage;
    private static bool _cutscenePanelWasShown;
    private static bool _cutsceneLoopEnabled;
    private static float _cutsceneLoopStartSeconds;
    private static float _cutsceneLoopEndSeconds = 1.0f;
    private static CutsceneTime _cutsceneDurationBeforeEdit;

    private static ECutsceneClipDrag _cutsceneDragMode;
    private static CutsceneClip? _cutsceneDragClip;
    private static CutsceneClipSpan _cutsceneDragOriginal;
    private static CutsceneClipSpan _cutsceneDragCurrent;
    private static float _cutsceneDragMouseStartX;

    /// <summary>Opens the sequencer panel on a director.</summary>
    /// <param name="director">The director to show.</param>
    public static void OpenCutsceneSequencer(CutsceneDirectorComponent director)
    {
        ArgumentNullException.ThrowIfNull(director);
        _cutsceneDirector = director;
        _showCutsceneSequencer = true;
    }

    private static void DrawCutsceneSequencerPanel()
    {
        if (!_showCutsceneSequencer)
        {
            // A preview must not stay live after the author closes the panel that shows it.
            if (_cutscenePanelWasShown)
            {
                _cutscenePanelWasShown = false;
                EnqueueSceneEdit(CutscenePreviewSession.Close);
            }
            return;
        }

        _cutscenePanelWasShown = true;
        ImGui.SetNextWindowSize(new Vector2(1100.0f, 420.0f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Cutscene Sequencer", ref _showCutsceneSequencer))
        {
            ImGui.End();
            return;
        }

        DrawCutsceneAssetBar();

        CutsceneAsset? asset = CutscenePreviewSession.Asset ?? _cutsceneDirector?.Asset;
        if (asset is null)
        {
            ImGui.TextDisabled("Select a node, add a director, and create a cutscene asset.");
            ImGui.End();
            return;
        }

        DrawCutsceneTransportBar(asset);
        DrawCutsceneAuthoringBar(asset);
        DrawCutsceneTimeline(asset);
        DrawCutsceneDiagnostics();
        HandleCutsceneShortcuts();
        ImGui.End();
    }

    private static void DrawCutsceneAssetBar()
    {
        SceneNode? selected = Selection.LastSceneNode;
        if (selected?.GetComponent<CutsceneDirectorComponent>() is { } selectedDirector)
            _cutsceneDirector = selectedDirector;

        CutsceneDirectorComponent? director = _cutsceneDirector;
        ImGui.TextUnformatted(director is null ? "Director: none" : $"Director: {director.SceneNode?.Name}");

        if (director is null && selected is not null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Add Director To Selected"))
                AddCutsceneDirector(selected);
        }

        if (director is not null && director.Asset is null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Create Cutscene Asset"))
                CreateCutsceneAsset(director);
        }

        if (director?.Asset is not { } asset)
            return;

        ImGui.SameLine();
        ImGui.TextUnformatted($"Asset: {asset.Name}{(asset.IsDirty ? " *" : string.Empty)}");

        ImGui.SameLine();
        if (CutscenePreviewSession.IsOpen)
        {
            bool isPreview = CutscenePreviewSession.Host is not null;
            if (ImGui.Button(isPreview ? "End Preview" : "Close Director"))
                EnqueueSceneEdit(CutscenePreviewSession.Close);
        }
        else if (ImGui.Button(EditorState.InEditMode ? "Start Preview" : "Open Director"))
        {
            EnqueueSceneEdit(() => OpenCutsceneInSession(director));
        }

        ImGui.SameLine();
        if (ImGui.Button("Save Asset"))
            SaveCutsceneAsset(asset);

        if (_cutsceneOpenMessage is { } openMessage)
        {
            ImGui.SameLine();
            ImGui.TextWrapped(openMessage);
        }

        if (CutscenePreviewSession.Host is not null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("Preview values are live. End the preview before you save the scene.");
        }
    }

    private static void OpenCutsceneInSession(CutsceneDirectorComponent director)
    {
        if (!EditorState.InEditMode)
        {
            CutscenePreviewSession.OpenDirector(director);
            return;
        }

        if (director.World is not RuntimeWorld world)
        {
            _cutsceneOpenMessage = "The preview cannot start: the director node is not in a runtime world.";
            return;
        }

        _cutsceneOpenMessage = null;
        if (director.Asset is { } asset)
            CutscenePreviewSession.OpenPreview(asset, world);
    }

    private static void AddCutsceneDirector(SceneNode node)
        => EnqueueSceneEdit(() =>
        {
            using IDisposable interaction = Undo.BeginUserInteraction();
            using Undo.ChangeScope scope = Undo.BeginChange("Add Cutscene Director");
            _cutsceneDirector = node.AddComponent<CutsceneDirectorComponent>();
            MarkSceneHierarchyDirty(node, null, node.World as RuntimeWorld);
        });

    private static void CreateCutsceneAsset(CutsceneDirectorComponent director)
        => EnqueueSceneEdit(() =>
        {
            CutsceneAsset asset = new() { Name = $"{director.SceneNode?.Name ?? "New"} Cutscene", Duration = CutsceneTime.FromSeconds(10.0) };
            asset.MarkDirty();
            director.Asset = asset;
            MarkSceneHierarchyDirty(director.SceneNode, null, director.World as RuntimeWorld);
        });

    private static void SaveCutsceneAsset(CutsceneAsset asset)
    {
        // The asset manager clears the dirty flag only after a save that succeeds.
        try
        {
            if (string.IsNullOrEmpty(asset.FilePath))
                Engine.Assets.SaveGameAssetTo(asset, "Cutscenes");
            else
                Engine.Assets.Save(asset);
            _cutsceneOpenMessage = null;
        }
        catch (Exception ex)
        {
            Debug.LogException(ex, $"The cutscene asset '{asset.Name}' was not saved.");
            _cutsceneOpenMessage = $"The asset was not saved: {ex.Message}";
        }
    }

    private static void DrawCutsceneTransportBar(CutsceneAsset asset)
    {
        if (!CutscenePreviewSession.IsOpen)
        {
            ImGui.TextDisabled("The timeline is not open. Start a preview to scrub and play.");
            return;
        }

        if (ImGui.Button("|<"))
            EnqueueCutsceneSeek(CutsceneTime.Zero);
        ImGui.SameLine();
        if (ImGui.Button("<"))
            StepCutsceneFrames(-1);
        ImGui.SameLine();

        bool playing = CutscenePreviewSession.State == ECutsceneTransportState.Playing;
        if (ImGui.Button(playing ? "Pause" : "Play"))
            ToggleCutscenePlay();
        ImGui.SameLine();
        if (ImGui.Button(">"))
            StepCutsceneFrames(1);
        ImGui.SameLine();
        if (ImGui.Button(">|"))
            EnqueueCutsceneSeek(asset.Duration);
        ImGui.SameLine();

        ImGui.TextUnformatted(
            $"{CutscenePreviewSession.Position.ToSeconds():0.000} / {asset.Duration.ToSeconds():0.000} s  {CutscenePreviewSession.State}  x{CutscenePreviewSession.Rate:0.##}");
        ImGui.SameLine();
        ImGui.TextDisabled("J K L shuttle, arrows step");

        if (CutscenePreviewSession.Host is { } host)
            DrawCutsceneLoopControls(host, asset);
    }

    private static void DrawCutsceneLoopControls(CutscenePreviewHost host, CutsceneAsset asset)
    {
        float maxSeconds = (float)asset.Duration.ToSeconds();
        bool changed = ImGui.Checkbox("Loop", ref _cutsceneLoopEnabled);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90.0f);
        changed |= ImGui.DragFloat("Start##CutsceneLoop", ref _cutsceneLoopStartSeconds, 0.05f, 0.0f, maxSeconds, "%.2f");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90.0f);
        changed |= ImGui.DragFloat("End##CutsceneLoop", ref _cutsceneLoopEndSeconds, 0.05f, 0.0f, maxSeconds, "%.2f");
        if (!changed)
            return;

        bool enabled = _cutsceneLoopEnabled;
        CutsceneTime start = CutsceneTime.FromSeconds(_cutsceneLoopStartSeconds);
        CutsceneTime end = CutsceneTime.FromSeconds(_cutsceneLoopEndSeconds);
        EnqueueSceneEdit(() =>
        {
            if (enabled)
                host.SetLoopRange(start, end);
            else
                host.ClearLoopRange();
        });
    }

    private static void DrawCutsceneAuthoringBar(CutsceneAsset asset)
    {
        if (!EditorState.InEditMode)
        {
            ImGui.TextDisabled("The asset is read-only in play mode.");
            return;
        }

        SceneNode? selected = Selection.LastSceneNode;

        ImGui.BeginDisabled(selected is null);
        if (ImGui.Button("Add Transform Track") && selected is not null)
            AddCutsceneTrack(asset, selected, camera: false);
        ImGui.EndDisabled();

        ImGui.SameLine();
        bool hasCamera = selected?.GetComponent<CameraComponent>() is not null;
        ImGui.BeginDisabled(!hasCamera);
        if (ImGui.Button("Add Camera Track") && selected is not null)
            AddCutsceneTrack(asset, selected, camera: true);
        ImGui.EndDisabled();

        ImGui.SameLine();
        bool canRemoveTrack = _cutsceneSelectedTrack >= 0 && _cutsceneSelectedTrack < asset.Tracks.Count;
        ImGui.BeginDisabled(!canRemoveTrack);
        if (ImGui.Button("Remove Track") && canRemoveTrack)
            RemoveCutsceneTrack(asset, _cutsceneSelectedTrack);
        ImGui.EndDisabled();

        ImGui.SameLine();
        TransformTrackClip? keyClip = _cutsceneSelectedClip as TransformTrackClip;
        int keySlot = canRemoveTrack ? asset.Tracks[_cutsceneSelectedTrack].BindingSlot : -1;
        bool canAddKey = keyClip is not null && CutscenePreviewSession.GetBoundNode(keySlot) is not null;
        ImGui.BeginDisabled(!canAddKey);
        if (ImGui.Button("Add Key At Playhead") && keyClip is not null)
            AddCutsceneKey(asset, keyClip, keySlot);
        ImGui.EndDisabled();
        if (!canAddKey && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Start a preview and select a transform clip whose track has a live bound node. The key uses the transform of that node.");

        ImGui.SameLine();
        ImGui.BeginDisabled(keyClip is null || !CutscenePreviewSession.IsOpen);
        if (ImGui.Button("Remove Key At Playhead") && keyClip is not null)
            RemoveCutsceneKey(asset, keyClip);
        ImGui.EndDisabled();

        ImGui.SameLine();
        float duration = (float)asset.Duration.ToSeconds();
        ImGui.SetNextItemWidth(90.0f);
        if (ImGui.DragFloat("Duration", ref duration, 0.05f, 0.1f, 36000.0f, "%.2f"))
            EnqueueCutsceneDuration(asset, duration);
        if (ImGui.IsItemDeactivatedAfterEdit())
            RecordCutsceneDurationUndo(asset);
        else if (ImGui.IsItemActivated())
            _cutsceneDurationBeforeEdit = asset.Duration;

        ImGui.SameLine();
        ImGui.SetNextItemWidth(60.0f);
        if (ImGui.InputInt("FPS", ref _cutsceneFramesPerSecond, 0))
            _cutsceneFramesPerSecond = Math.Clamp(_cutsceneFramesPerSecond, 1, 240);
    }

    private static void EnqueueCutsceneDuration(CutsceneAsset asset, float seconds)
    {
        CutsceneTime duration = CutsceneTime.FromSeconds(Math.Max(0.1f, seconds));
        EnqueueSceneEdit(() =>
        {
            asset.Duration = duration;
            MarkCutsceneEdited(asset);
        });
    }

    private static void RecordCutsceneDurationUndo(CutsceneAsset asset)
    {
        CutsceneTime before = _cutsceneDurationBeforeEdit;
        EnqueueSceneEdit(() =>
        {
            CutsceneTime after = asset.Duration;
            RecordCutsceneUndo("Change Cutscene Duration", asset, () => asset.Duration = before, () => asset.Duration = after);
        });
    }

    private static void AddCutsceneTrack(CutsceneAsset asset, SceneNode node, bool camera)
        => EnqueueSceneEdit(() =>
        {
            int bindingsBefore = asset.Bindings.Count;
            CutsceneTime durationBefore = asset.Duration;
            CutsceneTrack track = camera
                ? CutsceneAssetEditing.AddCameraCutTrack(asset, node)
                : CutsceneAssetEditing.AddTransformTrack(asset, node);
            CutsceneTime durationAfter = asset.Duration;
            CutsceneBindingReference? addedBinding = asset.Bindings.Count > bindingsBefore ? asset.Bindings[bindingsBefore] : null;
            MarkCutsceneEdited(asset);

            RecordCutsceneUndo(
                camera ? "Add Camera Track" : "Add Transform Track",
                asset,
                () =>
                {
                    asset.Tracks.Remove(track);
                    if (addedBinding is not null)
                        asset.Bindings.Remove(addedBinding);
                    asset.Duration = durationBefore;
                },
                () =>
                {
                    if (addedBinding is not null)
                        asset.Bindings.Add(addedBinding);
                    asset.Tracks.Add(track);
                    asset.Duration = durationAfter;
                });
        });

    private static void RemoveCutsceneTrack(CutsceneAsset asset, int trackIndex)
        => EnqueueSceneEdit(() =>
        {
            if (trackIndex >= asset.Tracks.Count)
                return;

            CutsceneTrack track = asset.Tracks[trackIndex];
            asset.Tracks.RemoveAt(trackIndex);
            _cutsceneSelectedTrack = -1;
            _cutsceneSelectedClip = null;
            MarkCutsceneEdited(asset);

            RecordCutsceneUndo(
                "Remove Cutscene Track",
                asset,
                () => asset.Tracks.Insert(Math.Min(trackIndex, asset.Tracks.Count), track),
                () => asset.Tracks.Remove(track));
        });

    private static void AddCutsceneKey(CutsceneAsset asset, TransformTrackClip clip, int bindingSlot)
        => EnqueueSceneEdit(() =>
        {
            if (CutscenePreviewSession.GetBoundNode(bindingSlot)?.Transform is not Transform transform)
            {
                Debug.LogWarning("The bound node of the track has no standard transform, so it cannot give a cutscene key.");
                return;
            }

            CutsceneTime local = CutsceneAssetEditing.ToClipLocalTime(clip, CutscenePreviewSession.Position);
            TransformClipKeys before = TransformClipKeys.Capture(clip);
            CutsceneAssetEditing.SetTransformKey(clip, local, transform.Translation, transform.Rotation, transform.Scale);
            TransformClipKeys after = TransformClipKeys.Capture(clip);
            MarkCutsceneEdited(asset);
            RecordCutsceneUndo("Add Cutscene Key", asset, () => before.Apply(clip), () => after.Apply(clip));
        });

    private static void RemoveCutsceneKey(CutsceneAsset asset, TransformTrackClip clip)
        => EnqueueSceneEdit(() =>
        {
            CutsceneTime local = CutsceneAssetEditing.ToClipLocalTime(clip, CutscenePreviewSession.Position);
            CutsceneTime tolerance = new(CutsceneTime.FlicksPerSecond / (2L * _cutsceneFramesPerSecond));
            TransformClipKeys before = TransformClipKeys.Capture(clip);
            if (!CutsceneAssetEditing.RemoveTransformKey(clip, local, tolerance))
                return;

            TransformClipKeys after = TransformClipKeys.Capture(clip);
            MarkCutsceneEdited(asset);
            RecordCutsceneUndo("Remove Cutscene Key", asset, () => before.Apply(clip), () => after.Apply(clip));
        });

    /// <summary>
    /// Records one undo step. Undo can run on another thread, so both actions move to the update thread.
    /// That is the thread on which the preview reads the asset.
    /// </summary>
    private static void RecordCutsceneUndo(string description, CutsceneAsset asset, Action undo, Action redo)
    {
        using IDisposable interaction = Undo.BeginUserInteraction();
        Undo.RecordStructuralChange(
            description,
            () => EnqueueSceneEdit(() =>
            {
                undo();
                MarkCutsceneEdited(asset);
            }),
            () => EnqueueSceneEdit(() =>
            {
                redo();
                MarkCutsceneEdited(asset);
            }));
    }

    private static void MarkCutsceneEdited(CutsceneAsset asset)
    {
        asset.MarkDirty();
        CutscenePreviewSession.NotifyAssetEdited();
    }

    private static void DrawCutsceneTimeline(CutsceneAsset asset)
    {
        float laneAreaHeight = CutsceneRulerHeight + (Math.Max(1, asset.Tracks.Count) * CutsceneLaneHeight) + 8.0f;
        ImGui.BeginChild("CutsceneTimeline", new Vector2(0.0f, Math.Min(laneAreaHeight + 24.0f, 280.0f)), ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar);

        Vector2 origin = ImGui.GetCursorScreenPos();
        float timelineLeft = origin.X + CutsceneLaneHeaderWidth;
        float durationSeconds = (float)asset.Duration.ToSeconds();
        float timelineWidth = Math.Max(1.0f, durationSeconds * _cutscenePixelsPerSecond);
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();

        DrawCutsceneRuler(drawList, origin, timelineLeft, timelineWidth, durationSeconds);
        HandleCutsceneRulerInput(asset, origin, timelineLeft, timelineWidth);

        for (int trackIndex = 0; trackIndex < asset.Tracks.Count; trackIndex++)
            DrawCutsceneLane(asset, drawList, trackIndex, origin, timelineLeft);

        DrawCutscenePlayhead(drawList, origin, timelineLeft, laneAreaHeight);

        // The hit areas above moved the cursor, so the content size starts from the origin again.
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(CutsceneLaneHeaderWidth + timelineWidth, laneAreaHeight));

        // Ctrl and the mouse wheel change the zoom of the time axis.
        if (ImGui.IsWindowHovered() && ImGui.GetIO().KeyCtrl && ImGui.GetIO().MouseWheel != 0.0f)
            _cutscenePixelsPerSecond = Math.Clamp(_cutscenePixelsPerSecond * (1.0f + (ImGui.GetIO().MouseWheel * 0.1f)), 5.0f, 2000.0f);

        ImGui.EndChild();
    }

    private static void DrawCutsceneRuler(ImDrawListPtr drawList, Vector2 origin, float timelineLeft, float timelineWidth, float durationSeconds)
    {
        uint background = ImGui.GetColorU32(ImGuiCol.FrameBg);
        uint tick = ImGui.GetColorU32(ImGuiCol.TextDisabled);
        uint text = ImGui.GetColorU32(ImGuiCol.Text);
        drawList.AddRectFilled(new Vector2(timelineLeft, origin.Y), new Vector2(timelineLeft + timelineWidth, origin.Y + CutsceneRulerHeight), background);

        // Keep labels at least 60 pixels apart at every zoom.
        float stepSeconds = 1.0f;
        while (stepSeconds * _cutscenePixelsPerSecond < 60.0f)
            stepSeconds *= 2.0f;
        while (stepSeconds > 0.25f && stepSeconds * _cutscenePixelsPerSecond >= 120.0f)
            stepSeconds *= 0.5f;

        for (float second = 0.0f; second <= durationSeconds + 0.0001f; second += stepSeconds)
        {
            float x = timelineLeft + (second * _cutscenePixelsPerSecond);
            drawList.AddLine(new Vector2(x, origin.Y + 12.0f), new Vector2(x, origin.Y + CutsceneRulerHeight), tick);
            drawList.AddText(new Vector2(x + 3.0f, origin.Y + 1.0f), text, $"{second:0.##}");
        }

        if (!_cutsceneLoopEnabled)
            return;

        float loopLeft = timelineLeft + (_cutsceneLoopStartSeconds * _cutscenePixelsPerSecond);
        float loopRight = timelineLeft + (_cutsceneLoopEndSeconds * _cutscenePixelsPerSecond);
        uint loopColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.8f, 0.4f, 0.35f));
        drawList.AddRectFilled(new Vector2(loopLeft, origin.Y + 18.0f), new Vector2(loopRight, origin.Y + CutsceneRulerHeight), loopColor);
    }

    private static void HandleCutsceneRulerInput(CutsceneAsset asset, Vector2 origin, float timelineLeft, float timelineWidth)
    {
        ImGui.SetCursorScreenPos(new Vector2(timelineLeft, origin.Y));
        ImGui.InvisibleButton("##CutsceneRuler", new Vector2(timelineWidth, CutsceneRulerHeight));

        if (!CutscenePreviewSession.IsOpen)
            return;

        if (ImGui.IsItemActivated())
        {
            _cutsceneScrubbing = true;
            EnqueueSceneEdit(CutscenePreviewSession.BeginScrub);
        }

        if (_cutsceneScrubbing && ImGui.IsItemActive())
        {
            float seconds = (ImGui.GetMousePos().X - timelineLeft) / _cutscenePixelsPerSecond;
            CutsceneTime target = SnapCutsceneTime(CutsceneTime.FromSeconds(Math.Max(0.0f, seconds))).Clamp(CutsceneTime.Zero, asset.Duration);
            EnqueueSceneEdit(() => CutscenePreviewSession.Seek(target, ESeekIntent.Scrub));
        }

        if (_cutsceneScrubbing && ImGui.IsItemDeactivated())
        {
            _cutsceneScrubbing = false;
            EnqueueSceneEdit(CutscenePreviewSession.EndScrub);
        }
    }

    private static void DrawCutsceneLane(CutsceneAsset asset, ImDrawListPtr drawList, int trackIndex, Vector2 origin, float timelineLeft)
    {
        if (trackIndex >= asset.Tracks.Count)
            return;

        CutsceneTrack track = asset.Tracks[trackIndex];
        float laneTop = origin.Y + CutsceneRulerHeight + (trackIndex * CutsceneLaneHeight);
        Vector2 headerMin = new(origin.X, laneTop);

        ImGui.SetCursorScreenPos(headerMin);
        if (ImGui.Selectable($"{track.Name}##CutsceneTrack{trackIndex}", _cutsceneSelectedTrack == trackIndex, ImGuiSelectableFlags.None, new Vector2(CutsceneLaneHeaderWidth - 8.0f, CutsceneLaneHeight - 2.0f)))
            _cutsceneSelectedTrack = trackIndex;

        for (int clipIndex = 0; clipIndex < track.Clips.Count; clipIndex++)
            DrawCutsceneClip(asset, drawList, track, trackIndex, clipIndex, timelineLeft, laneTop);
    }

    private static void DrawCutsceneClip(CutsceneAsset asset, ImDrawListPtr drawList, CutsceneTrack track, int trackIndex, int clipIndex, float timelineLeft, float laneTop)
    {
        if (clipIndex >= track.Clips.Count)
            return;

        CutsceneClip clip = track.Clips[clipIndex];
        float left = timelineLeft + ((float)clip.Start.ToSeconds() * _cutscenePixelsPerSecond);
        float width = Math.Max(4.0f, (float)clip.Duration.ToSeconds() * _cutscenePixelsPerSecond);
        Vector2 min = new(left, laneTop + 2.0f);
        Vector2 max = new(left + width, laneTop + CutsceneLaneHeight - 2.0f);

        bool selected = ReferenceEquals(clip, _cutsceneSelectedClip);
        uint fill = ImGui.ColorConvertFloat4ToU32(CutsceneClipColor(clip, selected));
        drawList.AddRectFilled(min, max, fill, 3.0f);
        drawList.AddRect(min, max, ImGui.GetColorU32(selected ? ImGuiCol.Text : ImGuiCol.Border), 3.0f);
        if (clip is TransformTrackClip transformClip)
            DrawCutsceneKeyMarks(drawList, transformClip, left, min.Y, max.Y);

        bool editable = EditorState.InEditMode;
        float handleWidth = Math.Min(CutsceneTrimHandleWidth, width * 0.25f);

        ImGui.PushID(trackIndex * 4096 + clipIndex);
        DrawCutsceneClipHitArea(asset, "##Start", clip, trackIndex, ECutsceneClipDrag.TrimStart, new Vector2(left, min.Y), new Vector2(handleWidth, max.Y - min.Y), editable);
        DrawCutsceneClipHitArea(asset, "##Body", clip, trackIndex, ECutsceneClipDrag.Move, new Vector2(left + handleWidth, min.Y), new Vector2(Math.Max(1.0f, width - (2.0f * handleWidth)), max.Y - min.Y), editable);
        DrawCutsceneClipHitArea(asset, "##End", clip, trackIndex, ECutsceneClipDrag.TrimEnd, new Vector2(left + width - handleWidth, min.Y), new Vector2(handleWidth, max.Y - min.Y), editable);
        ImGui.PopID();
    }

    private static void DrawCutsceneClipHitArea(CutsceneAsset asset, string id, CutsceneClip clip, int trackIndex, ECutsceneClipDrag mode, Vector2 position, Vector2 size, bool editable)
    {
        ImGui.SetCursorScreenPos(position);
        ImGui.InvisibleButton(id, size);

        if (mode != ECutsceneClipDrag.Move && ImGui.IsItemHovered())
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);

        if (ImGui.IsItemActivated())
        {
            _cutsceneSelectedClip = clip;
            _cutsceneSelectedTrack = trackIndex;
            if (editable)
            {
                _cutsceneDragMode = mode;
                _cutsceneDragClip = clip;
                _cutsceneDragOriginal = CutsceneClipSpan.Capture(clip);
                _cutsceneDragCurrent = _cutsceneDragOriginal;
                _cutsceneDragMouseStartX = ImGui.GetMousePos().X;
            }
        }

        if (!ReferenceEquals(_cutsceneDragClip, clip) || _cutsceneDragMode != mode)
            return;

        if (ImGui.IsItemActive())
            UpdateCutsceneClipDrag(asset, clip);

        if (ImGui.IsItemDeactivated())
            FinishCutsceneClipDrag(asset, clip);
    }

    /// <summary>
    /// Applies a drag from the span at the start of the drag, so a rounding error does not grow.
    /// A frame in which the mouse did not change the span queues no edit, so an idle drag does not recompile.
    /// </summary>
    private static void UpdateCutsceneClipDrag(CutsceneAsset asset, CutsceneClip clip)
    {
        double deltaSeconds = (ImGui.GetMousePos().X - _cutsceneDragMouseStartX) / _cutscenePixelsPerSecond;
        CutsceneTime minimum = new(CutsceneTime.FlicksPerSecond / _cutsceneFramesPerSecond);
        CutsceneClipSpan original = _cutsceneDragOriginal;
        CutsceneTime delta = CutsceneTime.FromSeconds(deltaSeconds);
        CutsceneClipSpan next = _cutsceneDragMode switch
        {
            ECutsceneClipDrag.Move => CutsceneAssetEditing.ComputeMove(original, SnapCutsceneTime(original.Start + delta)),
            ECutsceneClipDrag.TrimStart => CutsceneAssetEditing.ComputeTrimStart(original, clip.Speed, SnapCutsceneTime(original.Start + delta), minimum),
            ECutsceneClipDrag.TrimEnd => CutsceneAssetEditing.ComputeTrimEnd(original, SnapCutsceneTime(original.Start + original.Duration + delta), minimum),
            _ => original,
        };

        if (next == _cutsceneDragCurrent)
            return;

        _cutsceneDragCurrent = next;
        EnqueueSceneEdit(() =>
        {
            next.Apply(clip);
            MarkCutsceneEdited(asset);
        });
    }

    private static void FinishCutsceneClipDrag(CutsceneAsset asset, CutsceneClip clip)
    {
        CutsceneClipSpan before = _cutsceneDragOriginal;
        CutsceneClipSpan after = _cutsceneDragCurrent;
        string description = _cutsceneDragMode == ECutsceneClipDrag.Move ? "Move Cutscene Clip" : "Trim Cutscene Clip";
        _cutsceneDragClip = null;
        _cutsceneDragMode = ECutsceneClipDrag.None;
        if (before == after)
            return;

        EnqueueSceneEdit(() => RecordCutsceneUndo(description, asset, () => before.Apply(clip), () => after.Apply(clip)));
    }

    private static void DrawCutsceneKeyMarks(ImDrawListPtr drawList, TransformTrackClip clip, float clipLeft, float top, float bottom)
    {
        CutsceneTime[] times = clip.Position.Times;
        uint color = ImGui.GetColorU32(ImGuiCol.Text);
        float middle = (top + bottom) * 0.5f;
        for (int i = 0; i < times.Length; i++)
        {
            double clipSeconds = (times[i] - clip.ClipOffset).ToSeconds() / clip.Speed;
            if (clipSeconds < 0.0 || clipSeconds > clip.Duration.ToSeconds())
                continue;

            float x = clipLeft + ((float)clipSeconds * _cutscenePixelsPerSecond);
            drawList.AddQuadFilled(new Vector2(x, middle - 4.0f), new Vector2(x + 4.0f, middle), new Vector2(x, middle + 4.0f), new Vector2(x - 4.0f, middle), color);
        }
    }

    private static Vector4 CutsceneClipColor(CutsceneClip clip, bool selected)
    {
        Vector4 color = clip switch
        {
            TransformTrackClip => new Vector4(0.22f, 0.50f, 0.78f, 1.0f),
            CameraCutClip => new Vector4(0.72f, 0.45f, 0.20f, 1.0f),
            CameraPropertyClip => new Vector4(0.62f, 0.38f, 0.70f, 1.0f),
            ActivationClip => new Vector4(0.30f, 0.62f, 0.38f, 1.0f),
            _ => new Vector4(0.45f, 0.45f, 0.45f, 1.0f),
        };
        return selected ? new Vector4(color.X * 1.25f, color.Y * 1.25f, color.Z * 1.25f, 1.0f) : color;
    }

    private static void DrawCutscenePlayhead(ImDrawListPtr drawList, Vector2 origin, float timelineLeft, float height)
    {
        if (!CutscenePreviewSession.IsOpen)
            return;

        float x = timelineLeft + ((float)CutscenePreviewSession.Position.ToSeconds() * _cutscenePixelsPerSecond);
        uint color = ImGui.ColorConvertFloat4ToU32(new Vector4(0.95f, 0.30f, 0.25f, 1.0f));
        drawList.AddLine(new Vector2(x, origin.Y), new Vector2(x, origin.Y + height), color, 2.0f);
    }

    private static void DrawCutsceneDiagnostics()
    {
        _cutsceneDiagnostics.Clear();
        CutscenePreviewSession.CopyDiagnostics(_cutsceneDiagnostics);
        if (_cutsceneDiagnostics.Count == 0)
            return;

        if (!ImGui.CollapsingHeader($"Diagnostics ({_cutsceneDiagnostics.Count})###CutsceneDiagnostics", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        for (int i = 0; i < _cutsceneDiagnostics.Count; i++)
            ImGui.TextWrapped(_cutsceneDiagnostics[i].Message);
    }

    private static void HandleCutsceneShortcuts()
    {
        if (!CutscenePreviewSession.IsOpen
            || !ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)
            || ImGui.GetIO().WantTextInput)
            return;

        if (ImGui.IsKeyPressed(ImGuiKey.J, false))
            ShuttleCutscene(-1);
        if (ImGui.IsKeyPressed(ImGuiKey.K, false))
            EnqueueSceneEdit(CutscenePreviewSession.Pause);
        if (ImGui.IsKeyPressed(ImGuiKey.L, false))
            ShuttleCutscene(1);
        if (ImGui.IsKeyPressed(ImGuiKey.Space, false))
            ToggleCutscenePlay();
        if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow))
            StepCutsceneFrames(-1);
        if (ImGui.IsKeyPressed(ImGuiKey.RightArrow))
            StepCutsceneFrames(1);
    }

    /// <summary>
    /// Maps J and L to rate steps. A press in the current direction moves to the next faster rate.
    /// A press in the other direction starts at normal speed in that direction.
    /// </summary>
    private static void ShuttleCutscene(int direction)
    {
        bool playing = CutscenePreviewSession.State == ECutsceneTransportState.Playing;
        double current = CutscenePreviewSession.Rate;
        double speed = _cutsceneShuttleRates[0];
        if (playing && Math.Sign(current) == direction)
        {
            for (int i = 0; i < _cutsceneShuttleRates.Length; i++)
            {
                if (_cutsceneShuttleRates[i] <= Math.Abs(current))
                    continue;

                speed = _cutsceneShuttleRates[i];
                break;
            }

            if (Math.Abs(current) >= _cutsceneShuttleRates[^1])
                speed = _cutsceneShuttleRates[^1];
        }

        double rate = speed * direction;
        EnqueueSceneEdit(() => CutscenePreviewSession.Play(rate));
    }

    private static void ToggleCutscenePlay()
    {
        if (CutscenePreviewSession.State == ECutsceneTransportState.Playing)
        {
            EnqueueSceneEdit(CutscenePreviewSession.Pause);
            return;
        }

        double rate = CutscenePreviewSession.Rate;
        EnqueueSceneEdit(() => CutscenePreviewSession.Play(rate));
    }

    private static void StepCutsceneFrames(int frames)
    {
        // Frame steps always hold the position, as an editor expects.
        CutsceneTime target = SnapCutsceneTime(CutscenePreviewSession.Position + CutsceneTime.FromFrames(frames, _cutsceneFramesPerSecond));
        EnqueueSceneEdit(() =>
        {
            CutscenePreviewSession.Pause();
            CutscenePreviewSession.Seek(target, ESeekIntent.Jump);
        });
    }

    private static void EnqueueCutsceneSeek(CutsceneTime target)
        => EnqueueSceneEdit(() => CutscenePreviewSession.Seek(target, ESeekIntent.Jump));

    private static CutsceneTime SnapCutsceneTime(CutsceneTime time)
    {
        long frameFlicks = CutsceneTime.FlicksPerSecond / _cutsceneFramesPerSecond;
        long frame = (long)Math.Round((double)time.Flicks / frameFlicks);
        return new CutsceneTime(frame * frameFlicks);
    }
}
