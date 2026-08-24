using System;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable-by-publication snapshot of the exact native target inherited by a
/// recorded packet. Copies share the bounded exact-count attachment array after
/// all <see cref="SetAttachment"/> calls complete.
/// </summary>
internal struct VulkanRecordedRenderTargetSnapshot : IEquatable<VulkanRecordedRenderTargetSnapshot>
{
    public const int MaxAttachmentCount = 16;
    private const int InlineAttachmentCapacity = 2;

    private VulkanNativeAttachmentIdentity _firstAttachment;
    private VulkanNativeAttachmentIdentity _secondAttachment;
    private VulkanNativeAttachmentIdentity[]? _overflowAttachments;

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
        if (!IsComplete || attachmentCount <= InlineAttachmentCapacity)
            return;

        if (_overflowAttachments is null ||
            _overflowAttachments.Length < attachmentCount)
        {
            _overflowAttachments = new VulkanNativeAttachmentIdentity[
                Math.Min(MaxAttachmentCount, Math.Max(attachmentCount, 4))];
        }
    }

    public void SetAttachment(int index, in VulkanNativeAttachmentIdentity attachment)
    {
        if ((uint)index >= (uint)AttachmentCount || index >= MaxAttachmentCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        if (AttachmentCount > InlineAttachmentCapacity)
            _overflowAttachments![index] = attachment;
        else if (index == 0)
            _firstAttachment = attachment;
        else
            _secondAttachment = attachment;
        IsComplete &= attachment.IsComplete;
    }

    public readonly VulkanNativeAttachmentIdentity GetAttachment(int index)
    {
        if ((uint)index >= (uint)AttachmentCount || index >= MaxAttachmentCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        if (AttachmentCount > InlineAttachmentCapacity)
            return _overflowAttachments![index];

        return index == 0 ? _firstAttachment : _secondAttachment;
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
            if (GetAttachment(i) != other.GetAttachment(i))
                return false;
        }

        return true;
    }

    public override readonly bool Equals(object? obj)
        => obj is VulkanRecordedRenderTargetSnapshot other && Equals(other);

    /// <summary>
    /// Describes the first physical target field that differs. Used only by
    /// rejected-frame diagnostics, where the string allocation is acceptable.
    /// </summary>
    internal readonly string DescribeFirstMismatch(in VulkanRecordedRenderTargetSnapshot other)
    {
        if (FramebufferHandle != other.FramebufferHandle)
            return $"FramebufferHandle 0x{FramebufferHandle:X}->0x{other.FramebufferHandle:X}";
        if (FramebufferGeneration != other.FramebufferGeneration)
            return $"FramebufferGeneration {FramebufferGeneration}->{other.FramebufferGeneration}";
        if (Width != other.Width || Height != other.Height)
            return $"Extent {Width}x{Height}->{other.Width}x{other.Height}";
        if (ViewMask != other.ViewMask)
            return $"ViewMask 0x{ViewMask:X}->0x{other.ViewMask:X}";
        if (AttachmentCount != other.AttachmentCount)
            return $"AttachmentCount {AttachmentCount}->{other.AttachmentCount}";
        if (IsComplete != other.IsComplete)
            return $"IsComplete {IsComplete}->{other.IsComplete}";

        for (int i = 0; i < AttachmentCount; i++)
        {
            VulkanNativeAttachmentIdentity current = GetAttachment(i);
            VulkanNativeAttachmentIdentity expected = other.GetAttachment(i);
            if (current != expected)
            {
                return $"Attachment[{i}] " +
                    $"image=0x{current.ImageHandle:X}/{current.ImageGeneration}->" +
                    $"0x{expected.ImageHandle:X}/{expected.ImageGeneration} " +
                    $"view=0x{current.ImageViewHandle:X}/{current.ImageViewGeneration}->" +
                    $"0x{expected.ImageViewHandle:X}/{expected.ImageViewGeneration} " +
                    $"layout={current.ExpectedLayout}->{expected.ExpectedLayout}";
            }
        }

        return "<none>";
    }

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
            hash.Add(GetAttachment(i));
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
