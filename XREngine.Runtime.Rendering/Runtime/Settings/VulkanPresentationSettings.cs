using MemoryPack;
using System.ComponentModel;
using XREngine.Data.Core;
using XREngine.Rendering.Vulkan;

namespace XREngine;

/// <summary>Configures Vulkan desktop presentation independently of renderer workload policy.</summary>
[Serializable]
[MemoryPackable]
public partial class VulkanPresentationSettings : XRBase
{
    private EVulkanPresentationProfile _profile = EVulkanPresentationProfile.Stable;
    private float _targetRefreshHz;
    private int _maximumFramesAhead = 1;
    private float _limiterSpinThresholdMilliseconds = 0.25f;

    [Category("Vulkan Presentation")]
    [Description("Selects Stable, LowLatency, Uncapped, or FrameGeneration desktop presentation policy. Environment variable XRE_VULKAN_PRESENTATION_PROFILE has highest priority.")]
    public EVulkanPresentationProfile Profile
    {
        get => _profile;
        set => SetField(ref _profile, value);
    }

    [Category("Vulkan Presentation")]
    [Description("Target desktop presentation cadence in hertz. Zero resolves from XRE_TARGET_REFRESH_HZ and then the window/display cadence.")]
    public float TargetRefreshHz
    {
        get => _targetRefreshHz;
        set => SetField(ref _targetRefreshHz, float.IsFinite(value) ? Math.Clamp(value, 0.0f, 1000.0f) : 0.0f);
    }

    [Category("Vulkan Presentation")]
    [Description("Maximum queued application frames for bounded-latency profiles. LowLatency is always clamped to one.")]
    public int MaximumFramesAhead
    {
        get => _maximumFramesAhead;
        set => SetField(ref _maximumFramesAhead, Math.Clamp(value, 0, 8));
    }

    [Category("Vulkan Presentation")]
    [Description("Final hybrid-limiter spin window in milliseconds after coarse sleep/yield pacing.")]
    public float LimiterSpinThresholdMilliseconds
    {
        get => _limiterSpinThresholdMilliseconds;
        set => SetField(ref _limiterSpinThresholdMilliseconds, float.IsFinite(value) ? Math.Clamp(value, 0.0f, 2.0f) : 0.25f);
    }
}
