using XREngine.Data.Rendering;
using static XREngine.Rendering.XRTexture;

namespace XREngine.Rendering.Resources;

public enum RenderResourceLifetime
{
    Persistent,
    Transient,
    External
}

public enum RenderResourceSizeClass
{
    AbsolutePixels,
    InternalResolution,
    WindowResolution,
    Custom
}

/// <summary>
/// Describes the intended GPU-side access pattern for a buffer resource.
/// </summary>
public enum EBufferAccessPattern
{
    ReadOnly,
    WriteOnly,
    ReadWrite
}

public readonly record struct RenderResourceSizePolicy(
    RenderResourceSizeClass SizeClass,
    float ScaleX = 1f,
    float ScaleY = 1f,
    uint Width = 0,
    uint Height = 0)
{
    public static RenderResourceSizePolicy Absolute(uint width, uint height)
        => new(RenderResourceSizeClass.AbsolutePixels, 1f, 1f, width, height);

    public static RenderResourceSizePolicy Internal(float scale = 1f)
        => new(RenderResourceSizeClass.InternalResolution, scale, scale);

    public static RenderResourceSizePolicy Window(float scale = 1f)
        => new(RenderResourceSizeClass.WindowResolution, scale, scale);

    public static RenderResourceSizePolicy Custom(float scaleX, float scaleY)
        => new(RenderResourceSizeClass.Custom, scaleX, scaleY);
}

public abstract record RenderResourceDescriptor(
    string Name,
    RenderResourceLifetime Lifetime,
    RenderResourceSizePolicy SizePolicy);

public sealed record TextureResourceDescriptor(
    string Name,
    RenderResourceLifetime Lifetime,
    RenderResourceSizePolicy SizePolicy,
    string? FormatLabel = null,
    bool StereoCompatible = false,
    uint ArrayLayers = 1,
    bool SupportsAliasing = false,
    bool RequiresStorageUsage = false)
    : RenderResourceDescriptor(Name, Lifetime, SizePolicy);

public readonly record struct FrameBufferAttachmentDescriptor(
    string ResourceName,
    EFrameBufferAttachment Attachment,
    int MipLevel,
    int LayerIndex);

public sealed record FrameBufferResourceDescriptor(
    string Name,
    RenderResourceLifetime Lifetime,
    RenderResourceSizePolicy SizePolicy,
    IReadOnlyList<FrameBufferAttachmentDescriptor> Attachments)
    : RenderResourceDescriptor(Name, Lifetime, SizePolicy);

public sealed record RenderBufferResourceDescriptor(
    string Name,
    RenderResourceLifetime Lifetime,
    RenderResourceSizePolicy SizePolicy,
    ERenderBufferStorage StorageFormat,
    uint MultisampleCount = 1,
    EFrameBufferAttachment? DefaultAttachment = null)
    : RenderResourceDescriptor(Name, Lifetime, SizePolicy);

public sealed record BufferResourceDescriptor(
    string Name,
    RenderResourceLifetime Lifetime,
    ulong SizeInBytes,
    EBufferTarget Target,
    EBufferUsage Usage,
    bool SupportsAliasing = true,
    uint ElementStride = 0,
    uint ElementCount = 0,
    EBufferAccessPattern AccessPattern = EBufferAccessPattern.ReadWrite);
