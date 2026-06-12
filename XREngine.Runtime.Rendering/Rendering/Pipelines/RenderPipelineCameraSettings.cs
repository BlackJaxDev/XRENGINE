using System;

namespace XREngine.Rendering;

/// <summary>
/// Marks a render-pipeline property as safe to surface on camera editor panels.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class RenderPipelineCameraSettingAttribute : Attribute
{
    public int Order { get; init; }
}

public enum EDepthNormalPrePassResolution
{
    Full = 0,
    Half = 1,
    Quarter = 2,
}

public interface IForwardDepthNormalPrePassSettings
{
    bool ForwardDepthPrePassEnabled { get; }
    bool ForwardPrePassSharesGBufferTargets { get; }
    EDepthNormalPrePassResolution ForwardDepthNormalPrePassResolution { get; }
}
