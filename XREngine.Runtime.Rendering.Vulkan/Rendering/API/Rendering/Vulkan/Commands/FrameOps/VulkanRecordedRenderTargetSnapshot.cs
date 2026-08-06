using System;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Allocation-free immutable-by-publication snapshot of the exact native target
/// inherited by a recorded packet.
/// </summary>
internal struct VulkanRecordedRenderTargetSnapshot : IEquatable<VulkanRecordedRenderTargetSnapshot>
{
    public const int MaxAttachmentCount = 16;

    private VulkanNativeAttachmentIdentityBuffer _attachments;

    public ulong FramebufferHandle { get; private set; }
    public ulong FramebufferGeneration { get; private set; }
    public uint Width { get; private set; }
    public uint Height { get; private set; }
    public uint ViewMask { get; private set; }
    public int AttachmentCount { get; private set; }
    public bool IsComplete { get; private set; }

    public void Initialize(
        ulong framebufferHandle,
        ulong framebufferGeneration,
        uint width,
        uint height,
        uint viewMask,
        int attachmentCount)
    {
        FramebufferHandle = framebufferHandle;
        FramebufferGeneration = framebufferGeneration;
        Width = width;
        Height = height;
        ViewMask = viewMask;
        AttachmentCount = attachmentCount;
        IsComplete = width > 0u &&
            height > 0u &&
            attachmentCount > 0 &&
            attachmentCount <= MaxAttachmentCount &&
            (framebufferHandle == 0UL || framebufferGeneration != 0UL);
    }

    public void SetAttachment(int index, in VulkanNativeAttachmentIdentity attachment)
    {
        if ((uint)index >= (uint)AttachmentCount || index >= MaxAttachmentCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        _attachments[index] = attachment;
        IsComplete &= attachment.IsComplete;
    }

    public readonly VulkanNativeAttachmentIdentity GetAttachment(int index)
    {
        if ((uint)index >= (uint)AttachmentCount || index >= MaxAttachmentCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return _attachments[index];
    }

    public readonly bool Equals(VulkanRecordedRenderTargetSnapshot other)
    {
        if (FramebufferHandle != other.FramebufferHandle ||
            FramebufferGeneration != other.FramebufferGeneration ||
            Width != other.Width ||
            Height != other.Height ||
            ViewMask != other.ViewMask ||
            AttachmentCount != other.AttachmentCount ||
            IsComplete != other.IsComplete)
        {
            return false;
        }

        for (int i = 0; i < AttachmentCount; i++)
        {
            if (_attachments[i] != other._attachments[i])
                return false;
        }

        return true;
    }

    public override readonly bool Equals(object? obj)
        => obj is VulkanRecordedRenderTargetSnapshot other && Equals(other);

    public override readonly int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(FramebufferHandle);
        hash.Add(FramebufferGeneration);
        hash.Add(Width);
        hash.Add(Height);
        hash.Add(ViewMask);
        hash.Add(AttachmentCount);
        hash.Add(IsComplete);
        for (int i = 0; i < AttachmentCount; i++)
            hash.Add(_attachments[i]);
        return hash.ToHashCode();
    }

    public static bool operator ==(
        in VulkanRecordedRenderTargetSnapshot left,
        in VulkanRecordedRenderTargetSnapshot right)
        => left.Equals(right);

    public static bool operator !=(
        in VulkanRecordedRenderTargetSnapshot left,
        in VulkanRecordedRenderTargetSnapshot right)
        => !left.Equals(right);
}
