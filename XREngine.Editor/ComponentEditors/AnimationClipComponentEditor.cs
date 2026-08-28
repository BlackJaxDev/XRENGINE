using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using XREngine.Animation.Importers;
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
        DrawSourceImportReport(clipComponent);
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

    private static void DrawSourceImportReport(AnimationClipComponent component)
    {
        if (!ImGui.CollapsingHeader("Unity Import / Playback Report", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var clip = component.Animation;
        ImportedAnimationImportManifest? manifest = clip?.SourceImportManifest;
        if (clip is null)
        {
            ImGui.TextDisabled("No animation clip is assigned.");
            return;
        }

        if (manifest is null)
        {
            ImGui.TextDisabled("This clip was not imported from a Unity .anim source.");
            return;
        }

        Vector4 statusColor = manifest.IsExecutable
            ? new Vector4(0.60f, 0.85f, 0.60f, 1.00f)
            : new Vector4(0.95f, 0.65f, 0.30f, 1.00f);
        ImGui.TextColored(statusColor, manifest.IsExecutable
            ? "Native path: all present source domains are executable"
            : "Native path: playback is blocked; source data was preserved");
        ImGui.TextDisabled($"Manifest schema: {manifest.SchemaVersion}");
        ImGui.TextDisabled($"Unity serializedVersion: {manifest.SourceIdentity.SerializedVersion}");
        ImGui.TextDisabled($"Source SHA-256: {AbbreviateHash(manifest.SourceIdentity.SourceContentSha256)}");
        ImGui.TextDisabled($"Import settings SHA-256: {AbbreviateHash(manifest.SourceIdentity.ImportSettingsSha256)}");
        ImGui.TextDisabled($"Coordinate contract: {manifest.CoordinateContract.ContractId}");
        ImGui.TextDisabled($"Humanoid target required: {manifest.RequiresHumanoidAvatar}");

        if (!string.IsNullOrWhiteSpace(component.PlaybackCapabilityDiagnostic))
            ImGui.TextWrapped($"Playback blocked: {component.PlaybackCapabilityDiagnostic}");

        if (component.FlipMuscleLeftRight
            || component.FlipMuscleZ
            || component.FlipIKPositionLeftRight
            || component.FlipIKPositionZ
            || component.FlipIKRotationLeftRight
            || component.FlipIKRotationZ)
        {
            ImGui.TextWrapped(
                "Diagnostic override active: one or more manual muscle/IK flips modify the persisted coordinate contract at playback time.");
        }

        if (ImGui.Button("Validate Playback Now"))
            EnqueueSceneEdit(() => component.TryValidatePlaybackCapabilities(out _));

        if (ImGui.BeginTable(
            "UnityAnimationDomains",
            5,
            ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
        {
            ImGui.TableSetupColumn("Domain");
            ImGui.TableSetupColumn("State");
            ImGui.TableSetupColumn("Source");
            ImGui.TableSetupColumn("Applied");
            ImGui.TableSetupColumn("Preserved");
            ImGui.TableHeadersRow();
            for (int i = 0; i < manifest.Domains.Length; i++)
            {
                ImportedAnimationDomainCapability domain = manifest.Domains[i];
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(domain.Domain.ToString());
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(domain.State.ToString());
                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(domain.SourceItemCount.ToString());
                ImGui.TableSetColumnIndex(3);
                ImGui.TextUnformatted(domain.AppliedItemCount.ToString());
                ImGui.TableSetColumnIndex(4);
                ImGui.TextUnformatted(domain.PreservedItemCount.ToString());
            }
            ImGui.EndTable();
        }

        if (ImGui.TreeNode($"Diagnostics ({CountDiagnostics(manifest)})"))
        {
            for (int i = 0; i < manifest.Domains.Length; i++)
            {
                ImportedAnimationDomainCapability domain = manifest.Domains[i];
                for (int j = 0; j < domain.Diagnostics.Length; j++)
                    ImGui.BulletText($"{domain.Domain}: {domain.Diagnostics[j]}");
            }
            ImGui.TreePop();
        }

        ImGui.TextDisabled(
            $"Bindings: {manifest.Bindings.Length}; preserved payloads: {manifest.PreservedPayloads.Length}");
    }

    private static int CountDiagnostics(ImportedAnimationImportManifest manifest)
    {
        int count = 0;
        for (int i = 0; i < manifest.Domains.Length; i++)
            count += manifest.Domains[i].Diagnostics.Length;
        return count;
    }

    private static string AbbreviateHash(string value)
        => value.Length <= 16 ? value : $"{value[..12]}...{value[^4..]}";

    private static void DrawAdvancedSection(AnimationClipComponent component, HashSet<object> visited)
    {
        if (!ImGui.CollapsingHeader("Advanced"))
            return;

        DrawDefaultComponentInspector(component, visited);
    }
}
