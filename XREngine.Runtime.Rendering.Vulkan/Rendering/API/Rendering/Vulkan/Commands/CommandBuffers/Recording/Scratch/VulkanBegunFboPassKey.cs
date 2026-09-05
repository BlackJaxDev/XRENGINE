using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;

/// <summary>Identifies one real framebuffer begin within a frozen graph pass.</summary>
internal readonly struct VulkanBegunFboPassKey : IEquatable<VulkanBegunFboPassKey>
{
    private readonly XRFrameBuffer _target;
    private readonly int _passIndex;
    private readonly int _schedulingIdentity;

    internal VulkanBegunFboPassKey(
        XRFrameBuffer target,
        int passIndex,
        int schedulingIdentity)
    {
        _target = target;
        _passIndex = passIndex;
        _schedulingIdentity = schedulingIdentity;
    }

    public bool Equals(VulkanBegunFboPassKey other)
        => ReferenceEquals(_target, other._target) &&
           _passIndex == other._passIndex && _schedulingIdentity == other._schedulingIdentity;

    public override bool Equals(object? obj)
        => obj is VulkanBegunFboPassKey other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(RuntimeHelpers.GetHashCode(_target), _passIndex, _schedulingIdentity);
}
