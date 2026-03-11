using System;
using System.Collections.Generic;
using System.IO;
using ImGuiNET;
using XREngine;
using XREngine.Components;
using XREngine.Editor.UI;

namespace XREngine.Editor.ComponentEditors;

public sealed class Audio2Face3DComponentEditor : IXRComponentEditor
{
    private const string CsvDialogFilter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*";

    public void DrawInspector(XRComponent component, HashSet<object> visited)
    {
        if (component is not Audio2Face3DComponent audio2Face)
        {
            EditorImGuiUI.DrawDefaultComponentInspector(component, visited);
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        if (!ComponentEditorLayout.DrawInspectorModeToggle(audio2Face, visited, "Audio2Face-3D"))
        {
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        ImGui.PushID(audio2Face.GetHashCode());
        DrawStatusSection(audio2Face);
        DrawSourceSection(audio2Face);
        DrawEmotionSection(audio2Face);
        DrawAdvancedSection(audio2Face, visited);
        ImGui.PopID();
        ComponentEditorLayout.DrawActivePreviewDialog();
    }

    private static void DrawStatusSection(Audio2Face3DComponent component)
    {
        if (!ImGui.CollapsingHeader("Status", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextDisabled($"Source: {component.SourceMode}");
        ImGui.TextDisabled($"Blendshapes: {component.BlendshapeCount}");
        ImGui.TextDisabled($"Emotion Curves: {component.EmotionCurveCount}");
        if (component.SourceMode == EAudio2Face3DSourceMode.CsvPlayback)
        {
            ImGui.TextDisabled($"Duration: {component.Duration:0.###}s");
            ImGui.TextDisabled($"Playback: {(component.IsPlaying ? "Running" : "Stopped")}");
        }
        else
        {
            ImGui.TextDisabled($"Live Connection: {(component.IsLiveConnected ? "Connected" : "Disconnected")}");
            if (!Audio2Face3DLiveClientRegistry.HasAdapter)
            {
                ImGui.TextWrapped(Audio2Face3DLiveClientRegistry.MissingAdapterMessage);
            }
        }

        if (!string.IsNullOrWhiteSpace(component.LastLoadError))
            ImGui.TextWrapped($"CSV Error: {component.LastLoadError}");
        if (!string.IsNullOrWhiteSpace(component.LastLiveError))
            ImGui.TextWrapped($"Live Error: {component.LastLiveError}");
    }

    private static void DrawEmotionSection(Audio2Face3DComponent component)
    {
        if (!ImGui.CollapsingHeader("Emotion", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextDisabled("Official Audio2Emotion channels: angry, disgust, fear, happy, neutral, sad");
        ImGui.TextDisabled("Targets accept comma-, semicolon-, or pipe-separated blendshape names.");

        DrawEmotionTargetInput("Angry Targets", component.AngryBlendshapeTargets, component, static (audio2Face, value) => audio2Face.AngryBlendshapeTargets = value, "Set angry emotion targets");
        DrawEmotionTargetInput("Disgust Targets", component.DisgustBlendshapeTargets, component, static (audio2Face, value) => audio2Face.DisgustBlendshapeTargets = value, "Set disgust emotion targets");
        DrawEmotionTargetInput("Fear Targets", component.FearBlendshapeTargets, component, static (audio2Face, value) => audio2Face.FearBlendshapeTargets = value, "Set fear emotion targets");
        DrawEmotionTargetInput("Happy Targets", component.HappyBlendshapeTargets, component, static (audio2Face, value) => audio2Face.HappyBlendshapeTargets = value, "Set happy emotion targets");
        DrawEmotionTargetInput("Neutral Targets", component.NeutralBlendshapeTargets, component, static (audio2Face, value) => audio2Face.NeutralBlendshapeTargets = value, "Set neutral emotion targets");
        DrawEmotionTargetInput("Sad Targets", component.SadBlendshapeTargets, component, static (audio2Face, value) => audio2Face.SadBlendshapeTargets = value, "Set sad emotion targets");
    }

    private static void DrawSourceSection(Audio2Face3DComponent component)
    {
        if (!ImGui.CollapsingHeader("Source", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        int sourceMode = (int)component.SourceMode;
        string[] sourceNames = ["CSV Playback", "Live Stream"];
        if (ImGui.Combo("Mode", ref sourceMode, sourceNames, sourceNames.Length))
        {
            using var _ = Undo.TrackChange("Audio2Face Source Mode", component);
            component.SourceMode = (EAudio2Face3DSourceMode)sourceMode;
        }

        if (component.SourceMode == EAudio2Face3DSourceMode.CsvPlayback)
            DrawCsvSourceSection(component);
        else
            DrawLiveSourceSection(component);
    }

    private static void DrawCsvSourceSection(Audio2Face3DComponent component)
    {
        ImGui.TextUnformatted(string.IsNullOrWhiteSpace(component.AnimationCsvPath) ? "No CSV selected." : component.AnimationCsvPath);

        if (ImGui.Button("Browse CSV..."))
        {
            ImGuiFileBrowser.OpenFile(
                $"Audio2FaceCsv_{component.GetHashCode()}",
                "Choose Audio2Face Animation CSV",
                result =>
                {
                    if (!result.Success || string.IsNullOrWhiteSpace(result.SelectedPath))
                        return;

                    string assignedPath = MakeProjectRelativeIfPossible(result.SelectedPath);
                    using var _ = Undo.TrackChange("Set Audio2Face CSV", component);
                    component.AnimationCsvPath = assignedPath;
                },
                CsvDialogFilter,
                ResolveInitialDirectory(component.AnimationCsvPath));
        }

        ImGui.SameLine();
        if (ImGui.Button("Reload CSV"))
            component.ReloadAnimation();

        ImGui.SameLine();
        if (ImGui.Button("Import To Project"))
            ImportCsvIntoProject(component);

        if (component.BlendshapeCount > 0)
        {
            if (ImGui.Button("Play From Start"))
                component.PlayFromStart();

            ImGui.SameLine();
            if (ImGui.Button("Stop"))
                component.StopPlayback(clearWeights: true);
        }
    }

    private static void DrawLiveSourceSection(Audio2Face3DComponent component)
    {
        string endpoint = component.LiveEndpoint;
        if (ImGui.InputText("Endpoint", ref endpoint, 512))
        {
            using var _ = Undo.TrackChange("Set Audio2Face Endpoint", component);
            component.LiveEndpoint = endpoint;
        }

        bool autoConnect = component.AutoConnectLiveOnActivation;
        if (ImGui.Checkbox("Auto Connect On Activation", ref autoConnect))
        {
            using var _ = Undo.TrackChange("Toggle Audio2Face Auto Connect", component);
            component.AutoConnectLiveOnActivation = autoConnect;
        }

        if (ImGui.Button("Connect"))
            component.TryConnectLiveClient();

        ImGui.SameLine();
        if (ImGui.Button("Disconnect"))
            component.DisconnectLiveClient();
    }

    private static void DrawAdvancedSection(Audio2Face3DComponent component, HashSet<object> visited)
    {
        if (!ImGui.CollapsingHeader("Advanced"))
            return;

        EditorImGuiUI.DrawDefaultComponentInspector(component, visited);
    }

    private static void DrawEmotionTargetInput(string label, string currentValue, Audio2Face3DComponent component, Action<Audio2Face3DComponent, string> setter, string undoLabel)
    {
        string value = currentValue;
        if (!ImGui.InputText(label, ref value, 1024))
            return;

        using var _ = Undo.TrackChange(undoLabel, component);
        setter(component, value);
    }

    private static string ResolveInitialDirectory(string currentPath)
    {
        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            string resolved = Audio2Face3DComponent.ResolveAnimationCsvPath(currentPath, Engine.CurrentProject?.ProjectDirectory, Directory.GetCurrentDirectory());
            if (File.Exists(resolved))
                return Path.GetDirectoryName(resolved) ?? Directory.GetCurrentDirectory();
        }

        return Engine.CurrentProject?.AssetsDirectory ?? Directory.GetCurrentDirectory();
    }

    private static string MakeProjectRelativeIfPossible(string selectedPath)
    {
        string fullPath = Path.GetFullPath(selectedPath);
        string? projectDirectory = Engine.CurrentProject?.ProjectDirectory;
        if (string.IsNullOrWhiteSpace(projectDirectory))
            return fullPath;

        string fullProjectDirectory = Path.GetFullPath(projectDirectory);
        if (!fullPath.StartsWith(fullProjectDirectory, StringComparison.OrdinalIgnoreCase))
            return fullPath;

        return Path.GetRelativePath(fullProjectDirectory, fullPath).Replace('\\', '/');
    }

    private static void ImportCsvIntoProject(Audio2Face3DComponent component)
    {
        string sourcePath = Audio2Face3DComponent.ResolveAnimationCsvPath(component.AnimationCsvPath, Engine.CurrentProject?.ProjectDirectory, Directory.GetCurrentDirectory());
        if (!File.Exists(sourcePath))
        {
            component.ReloadAnimation();
            return;
        }

        string? assetsDirectory = Engine.CurrentProject?.AssetsDirectory;
        if (string.IsNullOrWhiteSpace(assetsDirectory))
            return;

        string destinationDirectory = Path.Combine(assetsDirectory, "Audio2Face");
        Directory.CreateDirectory(destinationDirectory);
        string destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(sourcePath));
        File.Copy(sourcePath, destinationPath, true);

        string relativePath = Path.GetRelativePath(Engine.CurrentProject!.ProjectDirectory!, destinationPath).Replace('\\', '/');
        using var _ = Undo.TrackChange("Import Audio2Face CSV", component);
        component.AnimationCsvPath = relativePath;
        component.ReloadAnimation();
    }
}