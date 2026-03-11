using System;
using System.Collections.Generic;
using ImGuiNET;
using XREngine.Components;
using XREngine.Components.Animation;
using static XREngine.Editor.EditorImGuiUI;

namespace XREngine.Editor.ComponentEditors;

public sealed class AnimationClipComponentEditor : IXRComponentEditor
{
    public void DrawInspector(XRComponent component, HashSet<object> visited)
    {
        if (component is not AnimationClipComponent clipComponent)
        {
            DrawDefaultComponentInspector(component, visited);
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        if (!ComponentEditorLayout.DrawInspectorModeToggle(clipComponent, visited, "Animation Clip"))
        {
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        DrawPlaybackSection(clipComponent);
        DrawAdvancedSection(clipComponent, visited);
        ComponentEditorLayout.DrawActivePreviewDialog();
    }

    private static void DrawPlaybackSection(AnimationClipComponent component)
    {
        if (!ImGui.CollapsingHeader("Playback", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var clip = component.Animation;
        if (clip is null)
        {
            ImGui.TextDisabled("Assign an AnimationClip to enable scrubbing.");
            return;
        }

        float clipLength = MathF.Max(0.0f, clip.LengthInSeconds);
        float playbackTime = Math.Clamp(component.PlaybackTime, 0.0f, clipLength);
        bool canStepFrames = clip.SampleRate > 0;
        float frameDuration = canStepFrames ? 1.0f / clip.SampleRate : 0.0f;

        ImGui.TextDisabled($"Clip: {clip.Name}");
        ImGui.TextDisabled($"Duration: {clipLength:0.###} s");
        ImGui.TextDisabled($"Sample Rate: {clip.SampleRate} fps");
        string stateLabel = component.IsPaused ? "Paused" : component.IsPlaying ? "Playing" : "Stopped";
        ImGui.TextDisabled($"State: {stateLabel}");

        if (ImGui.Button(component.IsPlaying ? "Restart" : "Play"))
            EnqueueSceneEdit(component.Play);

        ImGui.SameLine();
        if (component.IsPlaying && !component.IsPaused)
        {
            if (ImGui.Button("Pause"))
                EnqueueSceneEdit(component.Pause);
        }
        else if (component.IsPaused)
        {
            if (ImGui.Button("Resume"))
                EnqueueSceneEdit(component.Resume);
        }

        ImGui.SameLine();
        if (ImGui.Button("Stop"))
            EnqueueSceneEdit(component.StopPlayback);

        ImGui.SameLine();
        if (ImGui.Button("Jump To Start"))
            EnqueueSceneEdit(() => component.EvaluateAtTime(0.0f));

        ImGui.SameLine();
        if (ImGui.Button("Jump To End"))
            EnqueueSceneEdit(() => component.EvaluateAtTime(clipLength));

        if (canStepFrames)
        {
            if (ImGui.Button("-1 Frame"))
            {
                float stepTime = GetSteppedFrameTime(playbackTime, frameDuration, -1, clipLength);
                EnqueueSceneEdit(() => component.EvaluateAtTime(stepTime));
            }

            ImGui.SameLine();
            if (ImGui.Button("+1 Frame"))
            {
                float stepTime = GetSteppedFrameTime(playbackTime, frameDuration, 1, clipLength);
                EnqueueSceneEdit(() => component.EvaluateAtTime(stepTime));
            }
        }

        if (clipLength <= 0.0f)
        {
            ImGui.TextDisabled("Clip length is zero, so no scrub range is available.");
            return;
        }

        ImGui.SetNextItemWidth(MathF.Min(420.0f, ImGui.GetContentRegionAvail().X));
        if (ImGui.SliderFloat("Time", ref playbackTime, 0.0f, clipLength, "%.3f s"))
        {
            float scrubTime = playbackTime;
            EnqueueSceneEdit(() => component.EvaluateAtTime(scrubTime));
        }

        int currentFrame = clip.SampleRate > 0
            ? (int)Math.Round(playbackTime * clip.SampleRate)
            : 0;
        int totalFrames = clip.SampleRate > 0
            ? Math.Max(0, (int)Math.Round(clipLength * clip.SampleRate))
            : 0;
        ImGui.TextDisabled($"Frame: {currentFrame} / {totalFrames}");
    }

    private static float GetSteppedFrameTime(float playbackTime, float frameDuration, int direction, float clipLength)
    {
        if (frameDuration <= 0.0f || !float.IsFinite(frameDuration))
            return Math.Clamp(playbackTime, 0.0f, clipLength);

        int currentFrame = (int)Math.Round(playbackTime / frameDuration);
        int targetFrame = Math.Max(0, currentFrame + direction);
        float targetTime = targetFrame * frameDuration;
        return Math.Clamp(targetTime, 0.0f, clipLength);
    }

    private static void DrawAdvancedSection(AnimationClipComponent component, HashSet<object> visited)
    {
        if (!ImGui.CollapsingHeader("Advanced"))
            return;

        DrawDefaultComponentInspector(component, visited);
    }
}