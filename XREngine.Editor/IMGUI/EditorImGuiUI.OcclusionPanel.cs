using System;
using System.Numerics;
using ImGuiNET;
using XREngine;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Occlusion;
using XREngine.Runtime;

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
        bool legacyCpuSocEnabled = Engine.EffectiveSettings.EnableCpuSoftwareOcclusionCulling;

        Vector4 modeColor = mode == EOcclusionCullingMode.Disabled
            ? new Vector4(1.0f, 0.4f, 0.4f, 1.0f)
            : new Vector4(0.4f, 1.0f, 0.6f, 1.0f);
        ImGui.TextColored(modeColor, $"Applied Mode: {mode}");
        ImGui.SameLine();
        ImGui.Text($"  Strategy: {strategy}");
        ImGui.Text($"Configured Mode (settings): {configuredMode}");

        // Meshlet downgrade banner: when the user asked for a meshlet strategy that the
        // active backend can't dispatch, the resolver swaps it; surface that here so the
        // dropdown choice doesn't appear silently ignored.
        var meshletRequested = Engine.Rendering.LastMeshletDowngradeRequested;
        var meshletResolved = Engine.Rendering.LastMeshletDowngradeResolved;
        var meshletReason = Engine.Rendering.LastMeshletDowngradeReason;
        if (meshletRequested.HasValue && meshletResolved.HasValue && meshletRequested.Value != meshletResolved.Value)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.4f, 1.0f),
                $"Meshlet downgrade: {meshletRequested.Value} -> {meshletResolved.Value}");
            ImGui.TextWrapped($"  Backend={Engine.Rendering.LastResolvedRendererBackend}, dialect={Engine.Rendering.LastResolvedMeshShaderDialect}.");
            if (!string.IsNullOrEmpty(meshletReason))
                ImGui.TextWrapped($"  Reason: {meshletReason}");
            ImGui.TextDisabled("  Mesh shaders require Vulkan with VK_EXT_mesh_shader, or OpenGL with GL_EXT_mesh_shader (rare on current drivers).");
        }

        if (legacyCpuSocEnabled && configuredMode != EOcclusionCullingMode.CpuSoftwareOcclusion)
            ImGui.Text("Legacy CPU SOC toggle: enabled");
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
        int cpuSkipDepthPrepass = OcclusionTelemetry.CpuPassesSkippedDepthNormalPrePass;
        int cpuSkipMode = OcclusionTelemetry.CpuPassesSkippedModeOff;
        if (cpuPasses == 0)
        {
            ImGui.TextDisabled("  Not active this frame.");
            if (cpuSkipNoCam + cpuSkipShadow + cpuSkipDepthPrepass + cpuSkipMode > 0)
            {
                ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.3f, 1.0f), "  Skip reasons (RenderCPU calls bypassed):");
                if (cpuSkipMode > 0)
                    ImGui.Text($"    mode != CpuQueryAsync : {cpuSkipMode}");
                if (cpuSkipNoCam > 0)
                    ImGui.Text($"    camera null           : {cpuSkipNoCam}");
                if (cpuSkipShadow > 0)
                    ImGui.Text($"    shadow pass           : {cpuSkipShadow}");
                if (cpuSkipDepthPrepass > 0)
                    ImGui.Text($"    depth-normal prepass  : {cpuSkipDepthPrepass}");
            }
            else
            {
                ImGui.TextDisabled("  (no RenderCPU calls observed — pipeline may be using GPU dispatch or scene is empty)");
            }
        }
        else
        {
            ImGui.Text($"  Passes Active : {cpuPasses}");
            if (cpuSkipNoCam + cpuSkipShadow + cpuSkipDepthPrepass + cpuSkipMode > 0)
                ImGui.Text($"  Passes Skipped: noCam={cpuSkipNoCam} shadow={cpuSkipShadow} depthPre={cpuSkipDepthPrepass} modeOff={cpuSkipMode}");
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
            int dForced = OcclusionTelemetry.CpuDecisionForcedVisible;
            int dTotal = dSeed + dCached + dVQ + dVH + dProbe + dSkip + dForced;
            if (dTotal > 0)
            {
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.8f, 0.9f, 1.0f, 1.0f), "  Decision Distribution:");
                static string Pct(int n, int t) => t > 0 ? $"{(double)n / t * 100.0:F1}%" : "0.0%";
                ImGui.Text($"    Seed (first-seen)     : {dSeed:N0}  ({Pct(dSeed, dTotal)})");
                ImGui.Text($"    Cached (prepass reuse): {dCached:N0}  ({Pct(dCached, dTotal)})");
                ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.4f, 1.0f),
                    $"    Forced visible       : {dForced:N0}  ({Pct(dForced, dTotal)})");
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

        int cpuHealthSignals =
            OcclusionTelemetry.CpuPendingQueries +
            OcclusionTelemetry.CpuQuerySubmittedTotal +
            OcclusionTelemetry.CpuQueryResolvedTotal +
            OcclusionTelemetry.CpuBudgetSkippedTotal +
            OcclusionTelemetry.CpuForcedVisibleTotal +
            OcclusionTelemetry.CpuGlobalConservativeFrames +
            OcclusionTelemetry.CpuUnsupportedStereoQueryMode;
        if (cpuPasses > 0 || cpuHealthSignals > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.8f, 0.9f, 1.0f, 1.0f), "  CPU Query Health:");
            ImGui.Text($"    Scope / Motion      : {OcclusionTelemetry.CpuActiveViewScope} / {OcclusionTelemetry.CpuMotionTier}");
            ImGui.Text($"    Pending Queries     : {OcclusionTelemetry.CpuPendingQueries:N0}");
            ImGui.Text($"    Submitted / Resolved: {OcclusionTelemetry.CpuQuerySubmittedTotal:N0} / {OcclusionTelemetry.CpuQueryResolvedTotal:N0}");
            if (OcclusionTelemetry.CpuQueryLatencySamples > 0)
                ImGui.Text($"    Latency Frames      : avg={OcclusionTelemetry.CpuQueryLatencyAverageFrames:F1} max={OcclusionTelemetry.CpuQueryLatencyMaxFrames}");
            if (OcclusionTelemetry.CpuGlobalConservativeFrames > 0)
                ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.4f, 1.0f),
                    $"    Conservative Frames : {OcclusionTelemetry.CpuGlobalConservativeFrames:N0}");
            if (OcclusionTelemetry.CpuUnsupportedStereoQueryMode > 0)
                ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.3f, 1.0f),
                    $"    Unsupported Stereo  : {OcclusionTelemetry.CpuUnsupportedStereoQueryMode:N0}");

            int forcedTotal = OcclusionTelemetry.CpuForcedVisibleTotal;
            if (forcedTotal > 0)
            {
                ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.4f, 1.0f), $"    Forced Visible      : {forcedTotal:N0}");
                foreach (ECpuOcclusionForceVisibleReason reason in Enum.GetValues<ECpuOcclusionForceVisibleReason>())
                {
                    int count = OcclusionTelemetry.GetCpuForcedVisibleCount(reason);
                    if (count > 0)
                        ImGui.Text($"      {reason,-24}: {count:N0}");
                }
            }

            int skippedTotal = OcclusionTelemetry.CpuBudgetSkippedTotal;
            if (skippedTotal > 0)
            {
                ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.3f, 1.0f), $"    Budget/Policy Skips : {skippedTotal:N0}");
                foreach (ECpuOcclusionQueryReason reason in Enum.GetValues<ECpuOcclusionQueryReason>())
                {
                    int count = OcclusionTelemetry.GetCpuBudgetSkippedCount(reason);
                    if (count > 0)
                        ImGui.Text($"      {reason,-24}: {count:N0}");
                }
            }
        }
        ImGui.Separator();

        // CPU software occlusion path
        int socTested = OcclusionTelemetry.CpuSocTested;
        int socCulled = OcclusionTelemetry.CpuSocCulled;
        int socSelected = OcclusionTelemetry.CpuSocOccludersSelected;
        int socRasterized = OcclusionTelemetry.CpuSocOccludersRasterized;
        int socSelfSkipped = OcclusionTelemetry.CpuSocSelfOccluderSkipped;
        double socRate = socTested > 0 ? (double)socCulled / socTested : 0.0;

        ImGui.TextColored(new Vector4(0.6f, 0.9f, 1.0f, 1.0f), "CPU SOC Path (software raster):");
        if (socTested + socSelected + socRasterized + socSelfSkipped == 0)
        {
            ImGui.TextDisabled("  Not active this frame.");
            if (configuredMode == EOcclusionCullingMode.CpuSoftwareOcclusion || legacyCpuSocEnabled)
            {
                ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.3f, 1.0f),
                    "  Enabled, but no eligible CPU opaque pass submitted SOC work this frame.");
            }
        }
        else
        {
            ImGui.Text($"  Occluders Selected  : {socSelected:N0}");
            ImGui.Text($"  Occluders Rasterized: {socRasterized:N0}");
            ImGui.Text($"  Tiles Closed        : {OcclusionTelemetry.CpuSocTilesClosed:N0}");
            ImGui.Text($"  Tested              : {socTested:N0}");
            if (socSelfSkipped > 0)
                ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.4f, 1.0f),
                    $"  Self-Occluder Skips : {socSelfSkipped:N0}");
            Vector4 socColor = socCulled > 0
                ? new Vector4(0.4f, 1.0f, 0.6f, 1.0f)
                : new Vector4(1.0f, 0.7f, 0.3f, 1.0f);
            ImGui.TextColored(socColor, $"  Culled              : {socCulled:N0}  ({socRate * 100.0:F1}%)");
            ImGui.Text($"  Time ms             : begin={OcclusionTelemetry.CpuSocBeginMilliseconds:F3} raster={OcclusionTelemetry.CpuSocRasterMilliseconds:F3} test={OcclusionTelemetry.CpuSocTestMilliseconds:F3}");
            if (socSelfSkipped > 0 && socTested == 0)
                ImGui.TextWrapped(
                    "  SOC built an occluder mask, but every eligible command was also one of the occluders. " +
                    "Split merged geometry into separate render commands to let software occlusion remove hidden commands.");
            if (OcclusionTelemetry.CpuSocForceVisible)
                ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.4f, 1.0f), "  Force visible is enabled.");
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
            int depthHistory = OcclusionTelemetry.GpuDepthSourceHistory;
            int depthCurrent = OcclusionTelemetry.GpuDepthSourceCurrent;
            ImGui.Text($"  Depth Source      : history={depthHistory} current={depthCurrent}");
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
            if (gpuRbAvail && gpuCands > 0 && gpuOccl == 0 && (depthHistory > 0 || depthCurrent > 0))
            {
                string reason = depthHistory > 0
                    ? "  Nothing occluded - history depth was sampled; tested mesh bounds may still overlap visible pixels."
                    : "  Nothing occluded - current depth may be empty this early in the frame, or no occluders are in view.";
                ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.3f, 1.0f), reason);
            }
            if (gpuRbAvail && gpuCands > 0 && gpuOccl == 0 && depthHistory == 0 && depthCurrent == 0)
                ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.3f, 1.0f),
                    "  Nothing occluded — depth pyramid may be empty, or no occluders in view.");
        }

        // Per-reason HiZ skip breakdown — when GpuHiZ is configured but counts read zero,
        // these tell you whether the pass bailed (missing shaders, no depth view, etc.)
        // rather than ran and culled nothing.
        int hizSkipTotal = OcclusionTelemetry.GpuHiZSkippedTotal;
        if (hizSkipTotal > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.3f, 1.0f), "  GPU Hi-Z Skip Reasons:");
            for (int i = 0; i < (int)EGpuHiZSkipReason.Count; ++i)
            {
                int count = OcclusionTelemetry.GetGpuHiZSkippedCount((EGpuHiZSkipReason)i);
                if (count > 0)
                    ImGui.Text($"    {(EGpuHiZSkipReason)i,-22}: {count}");
            }
        }

        // CpuQueryAsync GPU-dispatch path (proxy-AABB hardware queries on the GPU dispatch path).
        // The earlier CPU-Query block reports CPU-direct submission counters; this block reports
        // the GPU-dispatch flavor where the cull decision is made via async hardware queries.
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.6f, 0.9f, 1.0f, 1.0f),
            "CPU-Query Path on GPU Dispatch (async hardware queries):");
        int cqaSubmitted = OcclusionTelemetry.CpuQueryAsyncSubmitted;
        int cqaResolved = OcclusionTelemetry.CpuQueryAsyncResolved;
        int cqaOccluded = OcclusionTelemetry.CpuQueryAsyncOccluded;
        if (cqaSubmitted + cqaResolved + cqaOccluded == 0)
        {
            ImGui.TextDisabled("  Not active this frame (effective mode != CpuQueryAsync on GPU dispatch, or no candidates).");
        }
        else
        {
            ImGui.Text($"  Submitted (this frame) : {cqaSubmitted:N0}");
            ImGui.Text($"  Resolved (prev frames) : {cqaResolved:N0}");
            Vector4 cqaCullColor = cqaOccluded > 0
                ? new Vector4(0.4f, 1.0f, 0.6f, 1.0f)
                : new Vector4(1.0f, 0.7f, 0.3f, 1.0f);
            ImGui.TextColored(cqaCullColor, $"  Occluded (temporal)    : {cqaOccluded:N0}");
            ImGui.TextDisabled(
                "  Submissions are capped per frame; results land next frame and pass through a hysteresis filter.");
        }

        ImGui.End();
    }
}
