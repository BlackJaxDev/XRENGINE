using VrHeadroomStats = XREngine.RuntimeEngine.Rendering.Stats.VrHeadroom;
using VrStats = XREngine.RuntimeEngine.Rendering.Stats.Vr;
using VulkanStats = XREngine.RuntimeEngine.Rendering.Stats.Vulkan;

namespace XREngine.Editor.Mcp
{
    public sealed partial class EditorMcpActions
    {
        /// <summary>
        /// Builds the VR headroom block for <c>get_render_profiler_stats</c>.
        /// </summary>
        /// <remarks>
        /// The VR runtime paces presented frames to the headset refresh rate. This block reports the
        /// engine limit, the runtime display rate, and the unpaced CPU frame work, so a reader can see
        /// how fast the engine could go without the runtime wait.
        /// </remarks>
        private static object BuildVrHeadroomStats()
        {
            float? configuredLimit = Engine.EffectiveSettings.VrTargetFramesPerSecond;
            float engineLimitHz = Engine.Time.ResolveVrRenderFrequency();
            RuntimeEngine.Rendering.Stats.VrHeadroomWindow window = VrHeadroomStats.GetWindow();
            double displayHz = VrHeadroomStats.DisplayRefreshHz;
            double displayPeriodMs = VrHeadroomStats.DisplayPeriodMs;
            double impliedMedianHz = VrHeadroomStats.ImpliedHz(window.MedianMs);
            double impliedP95Hz = VrHeadroomStats.ImpliedHz(window.P95Ms);

            return new
            {
                in_vr = RuntimeEngine.VRState.IsInVR,
                configured_vr_limit_fps = configuredLimit,
                engine_vr_limit_hz = engineLimitHz,
                engine_vr_limit = engineLimitHz > 0.0f ? "capped" : "uncapped",
                active_timer_render_limit_hz = Engine.Time.Timer.TargetRenderFrequency,
                runtime_display_refresh_hz = JsonFinite(displayHz),
                runtime_display_period_ms = JsonFinite(displayPeriodMs),
                presented_vr_rate_hz = JsonFinite(VrStats.VrRenderFrameRateHz),
                xr_wait_frame_block_ms = JsonFinite(VrStats.VrXrWaitFrameBlockTimeMs),
                unpaced_cpu_frame_ms = new
                {
                    last = JsonFinite(VrHeadroomStats.LastCpuWorkMs),
                    average = JsonFinite(window.AverageMs),
                    median = JsonFinite(window.MedianMs),
                    p95 = JsonFinite(window.P95Ms),
                    worst = JsonFinite(window.WorstMs),
                    sample_count = window.SampleCount,
                    total_samples = VrHeadroomStats.CpuWorkSampleCount,
                },
                implied_unpaced_cpu_hz_median = JsonFinite(impliedMedianHz),
                implied_unpaced_cpu_hz_p95 = JsonFinite(impliedP95Hz),
                cpu_headroom_ratio_median = displayHz > 0.0 && impliedMedianHz > 0.0
                    ? JsonFinite(impliedMedianHz / displayHz)
                    : null,
                gpu = new
                {
                    // Eye command buffers have no resolved GPU timestamps yet. These values are
                    // GPU-side hints, not a full eye GPU frame time.
                    eye_gpu_frame_ms = (double?)null,
                    eye_gpu_frame_status = "not-timestamped",
                    eye_completion_wait_ms = JsonFinite(VrStats.VrOpenXrEyeCompletionWaitTimeMs),
                    eye_fence_forced_waits = VrStats.VrOpenXrEyeFenceForcedWaitCount,
                    desktop_primary_gpu_ms = JsonFinite(VulkanStats.VulkanFrameGpuCommandBufferMs),
                    desktop_primary_gpu_hz = JsonFinite(VrHeadroomStats.ImpliedHz(VulkanStats.VulkanFrameGpuCommandBufferMs)),
                },
                missed_deadline_frames = VrStats.VrXrMissedDeadlineFrames,
                note = "The VR runtime paces presented frames to the headset refresh rate with xrWaitFrame. " +
                    "implied_unpaced_cpu_hz is 1000 / (CPU time from xrWaitFrame return to xrEndFrame end). " +
                    "It excludes GPU work that finishes after xrEndFrame.",
            };
        }
    }
}
