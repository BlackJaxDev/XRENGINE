using System.Numerics;
using ImGuiNET;
using XREngine.Editor.RenderPipelineWorkbench;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static readonly Vector4 WorkbenchHeatCold = new(0.2f, 0.45f, 0.9f, 0.25f);
    private static readonly Vector4 WorkbenchHeatHot = new(0.95f, 0.3f, 0.2f, 0.65f);

    /// <summary>
    /// Draws the GPU timing state and the heat overlay toggle. When timing is off, it draws a button that turns on
    /// render statistics and GPU pipeline timing in the editor preferences.
    /// </summary>
    private static void DrawWorkbenchTimingControls()
    {
        RenderPipelineWorkbenchTiming timing = _workbench.Timing;
        if (!timing.Enabled)
        {
            ImGui.TextDisabled("GPU timing off.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Enable GPU timing"))
                EnableWorkbenchGpuTiming();
            return;
        }

        if (!timing.Supported)
        {
            ImGui.TextColored(WorkbenchWarningColor, timing.StatusText.Length > 0 ? timing.StatusText : "The renderer does not support GPU pipeline timing.");
            return;
        }

        bool heat = _workbench.ShowTimingHeat;
        if (ImGui.Checkbox("Timing heat", ref heat))
            _workbench.ShowTimingHeat = heat;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Colors each command block by its GPU time in the newest frame. Red is the slowest command.");

        ImGui.SameLine();
        if (timing.TotalText.Length > 0)
            ImGui.TextColored(WorkbenchBadgeColor, timing.TotalText);
        else
            ImGui.TextDisabled(timing.StatusText.Length > 0 ? timing.StatusText : "Waiting for GPU timings of this instance.");
    }

    private static void EnableWorkbenchGpuTiming()
    {
        PersistProfilerDebugSetting(true,
            static current => current.EnableRenderStatisticsTracking,
            static overrides => overrides.EnableRenderStatisticsTrackingOverride,
            static (global, value) => global.EnableRenderStatisticsTracking = value);
        PersistProfilerDebugSetting(true,
            static current => current.EnableGpuRenderPipelineProfiling,
            static overrides => overrides.EnableGpuRenderPipelineProfilingOverride,
            static (global, value) => global.EnableGpuRenderPipelineProfiling = value);
    }

    /// <summary>Returns the heat overlay color for a normalized GPU time from 0 (fast) to 1 (slowest command).</summary>
    private static uint GetWorkbenchHeatColor(double normalized)
    {
        float t = (float)Math.Clamp(normalized, 0.0, 1.0);
        return ImGui.ColorConvertFloat4ToU32(Vector4.Lerp(WorkbenchHeatCold, WorkbenchHeatHot, t));
    }
}
