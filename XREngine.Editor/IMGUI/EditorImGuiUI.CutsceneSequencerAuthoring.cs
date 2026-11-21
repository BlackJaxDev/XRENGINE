using System.Diagnostics;
using ImGuiNET;
using XREngine.Components;
using XREngine.Cutscenes;
using XREngine.Editor.Cutscenes;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

// The field of view, float property, key interpolation, and recording controls of the cutscene sequencer.
public static partial class EditorImGuiUI
{
    private const string CutscenePropertyTrackPopupId = "CutscenePropertyTrackPopup";
    private static readonly string[] CutsceneInterpolationNames = [nameof(ECutsceneCurveInterpolation.Linear), nameof(ECutsceneCurveInterpolation.Step)];
    private static string? _cutsceneRecordMessage;

    /// <summary>
    /// Shows the components of the selected node that have float members. Picking a member adds a property track with one
    /// key at the current member value.
    /// </summary>
    private static void DrawCutscenePropertyTrackPopup(CutsceneAsset asset, SceneNode node)
    {
        if (!ImGui.BeginPopup(CutscenePropertyTrackPopupId))
            return;

        bool any = false;
        foreach (XRComponent component in node.Components)
        {
            Type componentType = component.GetType();
            IReadOnlyList<string> members = CutsceneFloatMemberAccess.GetFloatMembers(componentType);
            if (members.Count == 0)
                continue;

            any = true;
            if (!ImGui.BeginMenu(componentType.Name))
                continue;

            for (int i = 0; i < members.Count; i++)
            {
                if (ImGui.MenuItem(members[i]))
                    AddCutscenePropertyTrack(asset, node, componentType.Name, members[i]);
            }

            ImGui.EndMenu();
        }

        if (!any)
            ImGui.TextDisabled("The selected node has no component with a float property.");

        ImGui.EndPopup();
    }

    private static void AddCutscenePropertyTrack(CutsceneAsset asset, SceneNode node, string componentType, string memberPath)
        => EnqueueSceneEdit(() =>
        {
            if (!CutsceneFloatMemberAccess.TryReadFloat(node, componentType, memberPath, out float value))
            {
                Debug.LogWarning($"The member '{componentType}.{memberPath}' of '{node.Name}' cannot be read as a float.");
                return;
            }

            AddTrackWithUndo(asset, "Add Property Track", () => CutsceneAssetEditing.AddFloatPropertyTrack(asset, node, componentType, memberPath, value));
        });

    private static void AddCutsceneFieldOfViewTrack(CutsceneAsset asset, SceneNode node)
        => EnqueueSceneEdit(() =>
        {
            if (!CutsceneFloatMemberAccess.TryReadFieldOfView(node, out float degrees))
            {
                Debug.LogWarning($"The camera of '{node.Name}' has no perspective field of view.");
                return;
            }

            AddTrackWithUndo(asset, "Add Field Of View Track", () => CutsceneAssetEditing.AddFieldOfViewTrack(asset, node, degrees));
        });

    /// <summary>Adds a track on the update thread and records one undo step that removes the track and its new binding.</summary>
    private static void AddTrackWithUndo(CutsceneAsset asset, string description, Func<CutsceneTrack> add)
    {
        int bindingsBefore = asset.Bindings.Count;
        CutsceneTime durationBefore = asset.Duration;
        CutsceneTrack track = add();
        CutsceneTime durationAfter = asset.Duration;
        CutsceneBindingReference? addedBinding = asset.Bindings.Count > bindingsBefore ? asset.Bindings[bindingsBefore] : null;
        MarkCutsceneEdited(asset);

        RecordCutsceneUndo(
            description,
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
    }

    /// <summary>
    /// Adds a key at the playhead to the float curve of a field of view or property clip. The value is the current value of
    /// the bound node, so an author sets the value in the inspector and then adds the key.
    /// </summary>
    private static void AddCutsceneFloatKey(CutsceneAsset asset, CutsceneTrack track, CutsceneClip clip, CutsceneFloatCurve curve, int bindingSlot)
        => EnqueueSceneEdit(() =>
        {
            if (CutscenePreviewSession.GetBoundNode(bindingSlot) is not { } node)
                return;

            bool read = track switch
            {
                CameraPropertyTrack => CutsceneFloatMemberAccess.TryReadFieldOfView(node, out float fov) && SetKey(fov),
                PropertyTrack property => CutsceneFloatMemberAccess.TryReadFloat(node, property.ComponentType, property.MemberPath, out float value) && SetKey(value),
                _ => false,
            };
            if (!read)
                Debug.LogWarning($"The current value of track '{track.Name}' cannot be read from '{node.Name}'.");

            bool SetKey(float value)
            {
                CutsceneTime local = CutsceneAssetEditing.ToClipLocalTime(clip, CutscenePreviewSession.Position);
                (CutsceneTime[] Times, float[] Values) before = (curve.Times, curve.Values);
                CutsceneAssetEditing.SetFloatKey(curve, local, value);
                (CutsceneTime[] Times, float[] Values) after = (curve.Times, curve.Values);
                MarkCutsceneEdited(asset);
                RecordCutsceneUndo(
                    "Add Cutscene Key",
                    asset,
                    () => (curve.Times, curve.Values) = before,
                    () => (curve.Times, curve.Values) = after);
                return true;
            }
        });

    private static void RemoveCutsceneFloatKey(CutsceneAsset asset, CutsceneClip clip, CutsceneFloatCurve curve)
        => EnqueueSceneEdit(() =>
        {
            CutsceneTime local = CutsceneAssetEditing.ToClipLocalTime(clip, CutscenePreviewSession.Position);
            CutsceneTime tolerance = new(CutsceneTime.FlicksPerSecond / (2L * _cutsceneFramesPerSecond));
            (CutsceneTime[] Times, float[] Values) before = (curve.Times, curve.Values);
            if (!CutsceneAssetEditing.RemoveFloatKey(curve, local, tolerance))
                return;

            (CutsceneTime[] Times, float[] Values) after = (curve.Times, curve.Values);
            MarkCutsceneEdited(asset);
            RecordCutsceneUndo(
                "Remove Cutscene Key",
                asset,
                () => (curve.Times, curve.Values) = before,
                () => (curve.Times, curve.Values) = after);
        });

    private static void SetCutsceneClipInterpolation(CutsceneAsset asset, CutsceneClip clip, ECutsceneCurveInterpolation before, ECutsceneCurveInterpolation after)
        => EnqueueSceneEdit(() =>
        {
            if (!CutsceneAssetEditing.SetClipInterpolation(clip, after))
                return;

            MarkCutsceneEdited(asset);
            RecordCutsceneUndo(
                "Change Key Interpolation",
                asset,
                () => CutsceneAssetEditing.SetClipInterpolation(clip, before),
                () => CutsceneAssetEditing.SetClipInterpolation(clip, after));
        });

    /// <summary>
    /// Starts <c>--record-cutscene</c> in a separate editor process on the saved world and director. The recording reads
    /// the files on disk, so it does not change the open scene, and unsaved edits are not in the video.
    /// </summary>
    private static void StartCutsceneRecording(CutsceneAsset asset)
    {
        CutsceneDirectorComponent? director = _cutsceneDirector;
        string? worldPath = (director?.World as RuntimeWorld)?.TargetWorld?.FilePath;
        if (director?.SceneNode is not { } directorNode || string.IsNullOrWhiteSpace(worldPath))
        {
            _cutsceneRecordMessage = "Save the world before the recording. The recorder loads the saved world.";
            return;
        }

        if (string.IsNullOrEmpty(asset.FilePath) || asset.IsDirty)
        {
            _cutsceneRecordMessage = "Save the cutscene asset before the recording.";
            return;
        }

        string? executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            _cutsceneRecordMessage = "The editor executable path is not known, so the recorder cannot start.";
            return;
        }

        string outputRoot = Path.Combine(Path.GetDirectoryName(Engine.Assets.GameAssetsPath) ?? Engine.Assets.GameAssetsPath, "Recordings");
        string outputDirectory = Path.Combine(outputRoot, $"{asset.Name ?? "cutscene"}-{DateTime.Now:yyyyMMdd-HHmmss}");
        ProcessStartInfo startInfo = new() { FileName = executable, UseShellExecute = false };
        startInfo.ArgumentList.Add(XREngine.Editor.CutsceneRecording.RecordCutsceneCommand.CommandFlag);
        if (Engine.CurrentProject?.FilePath is { Length: > 0 } projectPath)
        {
            startInfo.ArgumentList.Add("--project");
            startInfo.ArgumentList.Add(projectPath);
        }

        startInfo.ArgumentList.Add("--world");
        startInfo.ArgumentList.Add(worldPath);
        startInfo.ArgumentList.Add("--director");
        startInfo.ArgumentList.Add(BuildCutsceneNodePath(directorNode));
        startInfo.ArgumentList.Add("--output-dir");
        startInfo.ArgumentList.Add(outputDirectory);

        try
        {
            Process.Start(startInfo)?.Dispose();
            _cutsceneRecordMessage = $"Recording to '{outputDirectory}'.";
        }
        catch (Exception ex)
        {
            Debug.LogException(ex, "The cutscene recorder did not start.");
            _cutsceneRecordMessage = $"The recorder did not start: {ex.Message}";
        }
    }

    private static string BuildCutsceneNodePath(SceneNode node)
    {
        string path = node.Name ?? string.Empty;
        for (TransformBase? parent = node.Transform.Parent; parent is not null; parent = parent.Parent)
            path = $"{parent.SceneNode?.Name}/{path}";
        return path;
    }
}
