using System.Numerics;
using ImGuiNET;
using XREngine.Data.Rendering;
using XREngine.Rendering.Occlusion;

namespace XREngine.Editor;

/// <summary>
/// Lightweight ImGui panel for verifying occlusion culling is actually doing work.
/// Pulls directly from <see cref="OcclusionTelemetry"/> — no packet plumbing required.
/// Toggle with the View menu or by setting <see cref="_showOcclusionPanel"/>.
/// </summary>
public static partial class EditorImGuiUI
{
    private static bool _showOcclusionPanel = false;

    internal static void ToggleOcclusionPanel() => _showOcclusionPanel = !_showOcclusionPanel;

    internal static void RenderOcclusionPanelOverlay()
    {
        if (!_showOcclusionPanel)
            return;

        ImGui.SetNextWindowSize(new Vector2(420f, 260f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Occlusion", ref _showOcclusionPanel))
        {
            ImGui.End();
            return;
        }

        var mode = OcclusionTelemetry.LastEffectiveMode;
        var strategy = OcclusionTelemetry.LastSubmissionStrategy;
        var configuredMode = Engine.EffectiveSettings.GpuOcclusionCullingMode;

        Vector4 modeColor = mode == EOcclusionCullingMode.Disabled
            ? new Vector4(1.0f, 0.4f, 0.4f, 1.0f)
            : new Vector4(0.4f, 1.0f, 0.6f, 1.0f);
        ImGui.TextColored(modeColor, $"Applied Mode: {mode}");
        ImGui.SameLine();
        ImGui.Text($"  Strategy: {strategy}");
        ImGui.Text($"Configured Mode (settings): {configuredMode}");
        if (mode == EOcclusionCullingMode.Disabled && configuredMode != EOcclusionCullingMode.Disabled)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.3f, 1.0f),
                "Configured but not applied — see skip reasons below.");
        }
        ImGui.Separator();

        // CPU path
        int cpuTested = OcclusionTelemetry.CpuTested;
        int cpuCulled = OcclusionTelemetry.CpuCulled;
        int cpuRendered = OcclusionTelemetry.CpuRendered;
        int cpuPasses = OcclusionTelemetry.CpuPassesActive;
        double cpuRate = cpuTested > 0 ? (double)cpuCulled / cpuTested : 0.0;

        ImGui.TextColored(new Vector4(0.6f, 0.9f, 1.0f, 1.0f), "CPU-Query Path (hardware AnySamplesPassed):");
        int cpuSkipNoCam = OcclusionTelemetry.CpuPassesSkippedNoCamera;
        int cpuSkipShadow = OcclusionTelemetry.CpuPassesSkippedShadow;
        int cpuSkipMode = OcclusionTelemetry.CpuPassesSkippedModeOff;
        if (cpuPasses == 0)
        {
            ImGui.TextDisabled("  Not active this frame.");
            if (cpuSkipNoCam + cpuSkipShadow + cpuSkipMode > 0)
            {
                ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.3f, 1.0f), "  Skip reasons (RenderCPU calls bypassed):");
                if (cpuSkipMode > 0)
                    ImGui.Text($"    mode != CpuQueryAsync : {cpuSkipMode}");
                if (cpuSkipNoCam > 0)
                    ImGui.Text($"    camera null           : {cpuSkipNoCam}");
                if (cpuSkipShadow > 0)
                    ImGui.Text($"    shadow pass           : {cpuSkipShadow}");
            }
            else
            {
                ImGui.TextDisabled("  (no RenderCPU calls observed — pipeline may be using GPU dispatch or scene is empty)");
            }
        }
        else
        {
            ImGui.Text($"  Passes Active : {cpuPasses}");
            if (cpuSkipNoCam + cpuSkipShadow + cpuSkipMode > 0)
                ImGui.Text($"  Passes Skipped: noCam={cpuSkipNoCam} shadow={cpuSkipShadow} modeOff={cpuSkipMode}");
            ImGui.Text($"  Tested        : {cpuTested:N0}");
            Vector4 culledColor = cpuCulled > 0
                ? new Vector4(0.4f, 1.0f, 0.6f, 1.0f)
                : new Vector4(1.0f, 0.7f, 0.3f, 1.0f);
            ImGui.TextColored(culledColor, $"  Culled        : {cpuCulled:N0}  ({cpuRate * 100.0:F1}%)");
            ImGui.Text($"  Rendered      : {cpuRendered:N0}");
            if (cpuTested > 0 && cpuCulled == 0)
                ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.3f, 1.0f),
                    "  Nothing culled — occluders may not be in view, or first-frame warmup.");

            // Decision distribution — diagnoses whether occlusion is failing at the
            // hardware-query level (VisibleQuery dominates) or the decision-policy level
            // (Hysteresis / Probe / Cached dominating).
            int dSeed = OcclusionTelemetry.CpuDecisionSeed;
            int dCached = OcclusionTelemetry.CpuDecisionCached;
            int dVQ = OcclusionTelemetry.CpuDecisionVisibleQuery;
            int dVH = OcclusionTelemetry.CpuDecisionVisibleHysteresis;
            int dProbe = OcclusionTelemetry.CpuDecisionProbe;
            int dSkip = OcclusionTelemetry.CpuDecisionSkip;
            int dTotal = dSeed + dCached + dVQ + dVH + dProbe + dSkip;
            if (dTotal > 0)
            {
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.8f, 0.9f, 1.0f, 1.0f), "  Decision Distribution:");
                static string Pct(int n, int t) => t > 0 ? $"{(double)n / t * 100.0:F1}%" : "0.0%";
                ImGui.Text($"    Seed (first-seen)     : {dSeed:N0}  ({Pct(dSeed, dTotal)})");
                ImGui.Text($"    Cached (prepass reuse): {dCached:N0}  ({Pct(dCached, dTotal)})");
                ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.3f, 1.0f),
                    $"    Visible (query passed): {dVQ:N0}  ({Pct(dVQ, dTotal)})");
                ImGui.Text($"    Visible (hysteresis)  : {dVH:N0}  ({Pct(dVH, dTotal)})");
                ImGui.Text($"    Probe (depth-only)    : {dProbe:N0}  ({Pct(dProbe, dTotal)})");
                ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.6f, 1.0f),
                    $"    Skip (occluded)       : {dSkip:N0}  ({Pct(dSkip, dTotal)})");
                if (dVQ > 0 && dSkip == 0)
                    ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.3f, 1.0f),
                        "    Hardware queries report samples passing on every command —");
                ImGui.TextDisabled(
                    "    high VisibleQuery + low Skip = mesh-granularity ceiling (per-mesh hardware queries can't");
                ImGui.TextDisabled(
                    "    cull meshes whose AABB has ANY visible pixel; split large meshes or add a software pre-pass).");
            }
        }
        ImGui.Separator();

        // GPU path
        int gpuCands = OcclusionTelemetry.GpuCandidates;
        int gpuOccl = OcclusionTelemetry.GpuOccluded;
        int gpuPasses = OcclusionTelemetry.GpuPassesActive;
        int gpuRb = OcclusionTelemetry.GpuPassesWithReadback;
        double gpuRate = gpuCands > 0 ? (double)gpuOccl / gpuCands : 0.0;
        bool gpuRbAvail = OcclusionTelemetry.LastFrameGpuOcclusionAvailable;

        ImGui.TextColored(new Vector4(0.6f, 0.9f, 1.0f, 1.0f), "GPU Hi-Z Path (compute cull):");
        if (gpuPasses == 0)
        {
            ImGui.TextDisabled("  Not active this frame.");
        }
        else
        {
            ImGui.Text($"  Passes Active     : {gpuPasses}");
            ImGui.Text($"  Passes w/ Readback: {gpuRb}");
            int passthroughDirty = OcclusionTelemetry.GpuPassesPassthroughDirty;
            if (passthroughDirty > 0)
            {
                ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.4f, 1.0f),
                    $"  Passes Passthrough: {passthroughDirty}  (dirty temporal state â€” pyramid built from stale depth, cull skipped this frame)");
            }
            ImGui.Text($"  Candidates        : {gpuCands:N0}");
            if (gpuRbAvail)
            {
                Vector4 culledColor = gpuOccl > 0
                    ? new Vector4(0.4f, 1.0f, 0.6f, 1.0f)
                    : new Vector4(1.0f, 0.7f, 0.3f, 1.0f);
                ImGui.TextColored(culledColor, $"  Occluded          : {gpuOccl:N0}  ({gpuRate * 100.0:F1}%)");
            }
            else
            {
                ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.3f, 1.0f),
                    "  Occluded          : (unavailable — zero-readback strategy)");
                ImGui.TextWrapped(
                    "  To verify GPU Hi-Z culling counts, temporarily switch the mesh submission strategy " +
                    "to GpuIndirectInstrumented; counts are CPU-readable in that mode.");
            }
            if (gpuRbAvail && gpuCands > 0 && gpuOccl == 0)
                ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.3f, 1.0f),
                    "  Nothing occluded — depth pyramid may be empty, or no occluders in view.");
        }

        ImGui.End();
    }
}
