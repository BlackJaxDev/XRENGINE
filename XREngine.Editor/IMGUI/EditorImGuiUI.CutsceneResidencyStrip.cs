using System.Numerics;
using ImGuiNET;
using XREngine.Cutscenes;
using XREngine.Editor.Cutscenes;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private const float CutsceneResidencyStripHeight = 12.0f;
    private const float CutsceneLanesTop = CutsceneRulerHeight + CutsceneResidencyStripHeight;

    private static float _cutsceneT0BudgetMiB = CutsceneBakeOptions.DefaultT0BudgetBytes / (1024.0f * 1024.0f);
    private static string? _cutsceneResidencyMessage;
    private static CutsceneAsset? _cutsceneManifestStateAsset;
    private static ECutsceneManifestState _cutsceneManifestState;
    private static int _cutsceneManifestStateVersion = -1;
    private static int _cutsceneEditVersion;
    private static CutsceneResidencyManifest? _cutsceneStripManifest;
    private static CutsceneResidencyBucketCursor? _cutsceneStripCursor;
    private static EResidencyLevel[] _cutsceneStripLevels = [];

    /// <summary>Draws the manifest state, the bake controls, the playback wait option, and the planner state.</summary>
    private static void DrawCutsceneResidencyBar(CutsceneAsset asset)
    {
        CutsceneResidencySession? residency = CutscenePreviewSession.Residency;
        ECutsceneManifestState state = GetCutsceneManifestState(asset, residency);
        ImGui.TextUnformatted(state switch
        {
            ECutsceneManifestState.Current => "Manifest: baked",
            ECutsceneManifestState.Stale => "Manifest: stale (approximate)",
            _ => "Manifest: missing (approximate)",
        });

        ImGui.SameLine();
        bool canBake = EditorState.InEditMode && !CutscenePreviewSession.IsOpen && _cutsceneDirector is not null;
        ImGui.BeginDisabled(!canBake);
        if (ImGui.Button("Bake Manifest") && _cutsceneDirector is { } director)
            BakeCutsceneManifest(asset, director);
        ImGui.EndDisabled();
        if (!canBake && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("The bake runs in edit mode, with no preview open, on the selected director.");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(80.0f);
        if (ImGui.DragFloat("T0 Budget (MiB)", ref _cutsceneT0BudgetMiB, 1.0f, 1.0f, 4096.0f, "%.0f"))
            _cutsceneT0BudgetMiB = Math.Clamp(_cutsceneT0BudgetMiB, 1.0f, 4096.0f);

        DrawCutscenePlaybackWaitOption();

        if (residency is not null)
        {
            CutsceneResidencyPlanner planner = residency.Planner;
            ImGui.SameLine();
            ImGui.TextDisabled(
                $"Assets {residency.Manifest.Assets.Length}  T0 pending {planner.PendingProxyCount}  Gen {planner.Generation}  Waits timed out {planner.PlaybackWaitTimeoutCount}{(planner.IsHoldingPlayback ? "  HOLD" : string.Empty)}");
            if (residency.Backend is TextureResidencyBackend textures && textures.RejectedRequestCount > 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"Rejected {textures.RejectedRequestCount}");
            }
        }

        if (_cutsceneResidencyMessage is { } message)
            ImGui.TextWrapped(message);
    }

    private static void DrawCutscenePlaybackWaitOption()
    {
        bool hasHost = CutscenePreviewSession.Host is not null;
        CutsceneDirectorComponent? director = hasHost ? null : CutscenePreviewSession.Director;
        if (!hasHost && director is null)
            return;

        bool enabled = hasHost ? CutscenePreviewSession.Host!.PlaybackWaitEnabled : director!.PlaybackWaitEnabled;
        ImGui.SameLine();
        if (!ImGui.Checkbox("Wait At Cuts", ref enabled))
            return;

        bool value = enabled;
        EnqueueSceneEdit(() =>
        {
            if (CutscenePreviewSession.Host is { } host)
                host.PlaybackWaitEnabled = value;
            else if (CutscenePreviewSession.Director is { } openDirector)
                openDirector.PlaybackWaitEnabled = value;
        });
    }

    /// <summary>
    /// Draws the residency strip under the ruler. Each bucket is a bar. With an open session, the color shows how many
    /// entries have their target level: green for all, yellow for some, red for none. Without a session, the color shows
    /// the highest level of the bucket. A white tick marks a camera cut. A missing or stale manifest draws an orange
    /// marker over the timeline, because playback uses the approximate manifest there.
    /// </summary>
    private static void DrawCutsceneResidencyStrip(CutsceneAsset asset, ImDrawListPtr drawList, Vector2 origin, float timelineLeft, float timelineWidth, float laneAreaHeight)
    {
        CutsceneResidencySession? residency = CutscenePreviewSession.Residency;
        ECutsceneManifestState state = GetCutsceneManifestState(asset, residency);
        CutsceneResidencyManifest? manifest = residency?.Manifest ?? asset.Manifest;

        float top = origin.Y + CutsceneRulerHeight + 1.0f;
        float bottom = origin.Y + CutsceneLanesTop - 1.0f;
        drawList.AddRectFilled(new Vector2(timelineLeft, top), new Vector2(timelineLeft + timelineWidth, bottom), ImGui.GetColorU32(ImGuiCol.FrameBgHovered));

        if (manifest is not null)
        {
            PrepareCutsceneStrip(manifest, residency);
            DrawCutsceneResidencyBuckets(manifest, residency is not null, drawList, timelineLeft, top, bottom);
        }

        if (state != ECutsceneManifestState.Current)
        {
            uint marker = ImGui.ColorConvertFloat4ToU32(new Vector4(0.95f, 0.55f, 0.10f, 0.22f));
            drawList.AddRectFilled(new Vector2(timelineLeft, top), new Vector2(timelineLeft + timelineWidth, origin.Y + laneAreaHeight), marker);
            drawList.AddText(new Vector2(timelineLeft + 4.0f, top - 1.0f), ImGui.GetColorU32(ImGuiCol.Text),
                state == ECutsceneManifestState.Stale ? "stale manifest: approximate residency" : "no manifest: approximate residency");
        }

        if (residency?.Planner is { IsHoldingPlayback: true } planner)
        {
            float x = timelineLeft + ((float)planner.PlaybackHoldTime.ToSeconds() * _cutscenePixelsPerSecond);
            uint hold = ImGui.ColorConvertFloat4ToU32(new Vector4(1.0f, 0.85f, 0.10f, 1.0f));
            drawList.AddLine(new Vector2(x, top), new Vector2(x, origin.Y + laneAreaHeight), hold, 2.0f);
        }
    }

    /// <summary>
    /// Makes the decode cursor for the manifest, and with an open session reads the levels of all manifest assets in one
    /// backend call for this frame, so the strip takes no lock for each entry.
    /// </summary>
    private static void PrepareCutsceneStrip(CutsceneResidencyManifest manifest, CutsceneResidencySession? residency)
    {
        if (!ReferenceEquals(manifest, _cutsceneStripManifest))
        {
            _cutsceneStripManifest = manifest;
            _cutsceneStripCursor = new CutsceneResidencyBucketCursor(manifest);
            if (_cutsceneStripLevels.Length < manifest.Assets.Length)
                _cutsceneStripLevels = new EResidencyLevel[manifest.Assets.Length];
        }

        residency?.Backend.GetLevels(manifest.Assets, _cutsceneStripLevels);
    }

    private static void DrawCutsceneResidencyBuckets(CutsceneResidencyManifest manifest, bool live, ImDrawListPtr drawList, float timelineLeft, float top, float bottom)
    {
        Vector2 clipMin = drawList.GetClipRectMin();
        Vector2 clipMax = drawList.GetClipRectMax();
        uint cutColor = ImGui.GetColorU32(ImGuiCol.Text);
        ResidencyBucket[] buckets = manifest.Buckets;
        for (int b = 0; b < buckets.Length; b++)
        {
            ResidencyBucket bucket = buckets[b];
            float left = timelineLeft + ((float)bucket.Start.ToSeconds() * _cutscenePixelsPerSecond);
            float right = timelineLeft + ((float)bucket.End.ToSeconds() * _cutscenePixelsPerSecond);
            if (right < clipMin.X || left > clipMax.X)
                continue;

            drawList.AddRectFilled(new Vector2(left, top), new Vector2(MathF.Max(left + 1.0f, right - 1.0f), bottom), CutsceneBucketColor(b, live));
            if (bucket.StartsAtCut)
                drawList.AddLine(new Vector2(left, top - 3.0f), new Vector2(left, bottom), cutColor, 2.0f);
        }
    }

    private static uint CutsceneBucketColor(int bucket, bool live)
    {
        CutsceneResidencyBucketCursor cursor = _cutsceneStripCursor!;
        cursor.MoveTo(bucket);
        ReadOnlySpan<int> members = cursor.Members;
        if (members.Length == 0)
            return ImGui.ColorConvertFloat4ToU32(new Vector4(0.35f, 0.35f, 0.35f, 0.6f));

        if (!live)
        {
            int highest = 0;
            for (int e = 0; e < members.Length; e++)
                highest = Math.Max(highest, cursor.GetLevel(members[e]));
            return ImGui.ColorConvertFloat4ToU32(highest >= (int)EResidencyLevel.Target
                ? new Vector4(0.25f, 0.45f, 0.85f, 0.9f)
                : new Vector4(0.25f, 0.45f, 0.85f, 0.5f));
        }

        int resident = 0;
        for (int e = 0; e < members.Length; e++)
            if ((int)_cutsceneStripLevels[members[e]] >= cursor.GetLevel(members[e]))
                resident++;

        if (resident == members.Length)
            return ImGui.ColorConvertFloat4ToU32(new Vector4(0.25f, 0.75f, 0.35f, 0.9f));
        return ImGui.ColorConvertFloat4ToU32(resident > 0 ? new Vector4(0.90f, 0.75f, 0.20f, 0.9f) : new Vector4(0.80f, 0.25f, 0.20f, 0.9f));
    }

    /// <summary>
    /// Gets the manifest state for the panel. An open session gives the state of its last manifest selection. Otherwise the
    /// panel computes the hash only when the asset or the panel edit version changed, because a hash walks all the track data.
    /// </summary>
    private static ECutsceneManifestState GetCutsceneManifestState(CutsceneAsset asset, CutsceneResidencySession? residency)
    {
        if (residency is not null)
            return residency.ManifestState;

        if (!ReferenceEquals(asset, _cutsceneManifestStateAsset) || _cutsceneManifestStateVersion != _cutsceneEditVersion)
        {
            _cutsceneManifestStateAsset = asset;
            _cutsceneManifestStateVersion = _cutsceneEditVersion;
            try
            {
                _cutsceneManifestState = CutsceneManifestHash.GetState(asset);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, "The cutscene manifest state could not be computed.");
                _cutsceneManifestState = ECutsceneManifestState.Stale;
            }
        }
        return _cutsceneManifestState;
    }

    private static void BakeCutsceneManifest(CutsceneAsset asset, CutsceneDirectorComponent director)
    {
        long budget = (long)(Math.Clamp(_cutsceneT0BudgetMiB, 1.0f, 4096.0f) * 1024.0 * 1024.0);
        EnqueueSceneEdit(() =>
        {
            if (CutscenePreviewSession.IsOpen)
            {
                _cutsceneResidencyMessage = "End the preview before the bake. The bake writes to the bound nodes.";
                return;
            }
            if (director.World is not RuntimeWorld world)
            {
                _cutsceneResidencyMessage = "The bake cannot run: the director node is not in a runtime world.";
                return;
            }

            using Undo.RecordingSuppressionScope suppression = Undo.SuppressRecording();
            List<CutsceneCompileDiagnostic> diagnostics = [];
            try
            {
                CutsceneBakeOptions options = new(CutscenePreviewSession.CreateResolver(world))
                {
                    SceneRoots = world.RootNodes,
                    T0BudgetBytes = budget,
                    Diagnostics = diagnostics,
                };
                CutsceneResidencyManifest manifest = CutsceneBakeTool.Bake(asset, options, out CutsceneT0Report report);
                asset.Manifest = manifest;
                asset.ManifestContentHash = CutsceneManifestHash.Compute(asset);
                asset.MarkDirty();
                _cutsceneEditVersion++;
                _cutsceneResidencyMessage = $"Baked {manifest.Buckets.Length} buckets. {report.Describe()}{(diagnostics.Count > 0 ? $" {diagnostics.Count} problems: {diagnostics[0].Message}" : string.Empty)}";
            }
            catch (CutsceneBakeException ex)
            {
                _cutsceneResidencyMessage = ex.Message;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, $"The residency manifest of the cutscene '{asset.Name}' was not baked.");
                _cutsceneResidencyMessage = $"The bake failed: {ex.Message}";
            }
        });
    }
}
