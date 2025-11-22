using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering;

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
    bool SupportsAliasing = false)
    : RenderResourceDescriptor(Name, Lifetime, SizePolicy)
{
    public static TextureResourceDescriptor FromTexture(XRTexture texture, RenderResourceLifetime lifetime = RenderResourceLifetime.Persistent)
    {
        Vector3 dims = texture.WidthHeightDepth;
        uint width = (uint)Math.Max(1, (int)MathF.Round(dims.X));
        uint height = (uint)Math.Max(1, (int)MathF.Round(dims.Y));
        uint depth = (uint)Math.Max(1, (int)MathF.Round(dims.Z));

        RenderResourceSizePolicy sizePolicy = RenderResourceSizePolicy.Absolute(width, height);
        string format = ResolveFormat(texture);
        bool stereo = TryGetStereoFlag(texture);

        return new TextureResourceDescriptor(
            texture.Name ?? string.Empty,
            lifetime,
            sizePolicy,
            format,
            stereo,
            depth,
            SupportsAliasing: false);
    }

    private static string ResolveFormat(XRTexture texture)
        => texture switch
        {
            XRTexture2D tex2D => tex2D.SizedInternalFormat.ToString(),
            XRTexture2DArray texArray => texArray.SizedInternalFormat.ToString(),
            XRTexture3D tex3D => tex3D.SizedInternalFormat.ToString(),
            XRTextureCube texCube => texCube.SizedInternalFormat.ToString(),
            _ => texture.GetType().Name
        };

    private static bool TryGetStereoFlag(XRTexture texture)
    {
        if (texture is XRTexture2DArray texArray && texArray.OVRMultiViewParameters is { NumViews: > 1 })
            return true;
        return false;
    }
}

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
    : RenderResourceDescriptor(Name, Lifetime, SizePolicy)
{
    public static FrameBufferResourceDescriptor FromFrameBuffer(
        XRFrameBuffer frameBuffer,
        RenderResourceLifetime lifetime = RenderResourceLifetime.Persistent)
    {
        uint width = Math.Max(frameBuffer.Width, 1u);
        uint height = Math.Max(frameBuffer.Height, 1u);
        RenderResourceSizePolicy sizePolicy = RenderResourceSizePolicy.Absolute(width, height);

        List<FrameBufferAttachmentDescriptor> attachments = [];
        if (frameBuffer.Targets is not null)
        {
            foreach (var (target, attachment, mipLevel, layerIndex) in frameBuffer.Targets)
            {
                string resourceName = target switch
                {
                    XRTexture tex => tex.Name ?? tex.GetDescribingName(),
                    _ => target?.GetType().Name ?? string.Empty
                };
                attachments.Add(new FrameBufferAttachmentDescriptor(resourceName, attachment, mipLevel, layerIndex));
            }
        }

        return new FrameBufferResourceDescriptor(
            frameBuffer.Name ?? string.Empty,
            lifetime,
            sizePolicy,
            attachments);
    }
}

public sealed class RenderTextureResource
{
    public TextureResourceDescriptor Descriptor { get; private set; }
    public XRTexture? Instance { get; private set; }

    internal RenderTextureResource(TextureResourceDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    public void UpdateDescriptor(TextureResourceDescriptor descriptor)
        => Descriptor = descriptor;

    public void Bind(XRTexture texture)
    {
        if (Instance == texture)
            return;

        Instance?.Destroy();
        Instance = texture;
    }

    public void DestroyInstance()
    {
        Instance?.Destroy();
        Instance = null;
    }
}

public sealed class RenderFrameBufferResource
{
    public FrameBufferResourceDescriptor Descriptor { get; private set; }
    public XRFrameBuffer? Instance { get; private set; }

    internal RenderFrameBufferResource(FrameBufferResourceDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    public void UpdateDescriptor(FrameBufferResourceDescriptor descriptor)
        => Descriptor = descriptor;

    public void Bind(XRFrameBuffer frameBuffer)
    {
        if (Instance == frameBuffer)
            return;

        Instance?.Destroy();
        Instance = frameBuffer;
    }

    public void DestroyInstance()
    {
        Instance?.Destroy();
        Instance = null;
    }
}

public sealed class RenderResourceRegistry
{
    private readonly Dictionary<string, RenderTextureResource> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RenderFrameBufferResource> _frameBuffers = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, RenderTextureResource> TextureRecords => _textures;
    public IReadOnlyDictionary<string, RenderFrameBufferResource> FrameBufferRecords => _frameBuffers;

    public RenderTextureResource RegisterTextureDescriptor(TextureResourceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (!_textures.TryGetValue(descriptor.Name, out RenderTextureResource? record))
        {
            record = new RenderTextureResource(descriptor);
            _textures.Add(descriptor.Name, record);
        }
        else
        {
            record.UpdateDescriptor(descriptor);
        }

        return record;
    }

    public RenderFrameBufferResource RegisterFrameBufferDescriptor(FrameBufferResourceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (!_frameBuffers.TryGetValue(descriptor.Name, out RenderFrameBufferResource? record))
        {
            record = new RenderFrameBufferResource(descriptor);
            _frameBuffers.Add(descriptor.Name, record);
        }
        else
        {
            record.UpdateDescriptor(descriptor);
        }

        return record;
    }

    public void BindTexture(XRTexture texture, TextureResourceDescriptor? descriptor = null)
    {
        ArgumentNullException.ThrowIfNull(texture);
        string name = texture.Name ?? throw new InvalidOperationException("Texture name must be set before binding to the registry.");

        descriptor ??= TextureResourceDescriptor.FromTexture(texture);
        descriptor = descriptor with { Name = name };

        RenderTextureResource record = RegisterTextureDescriptor(descriptor);
        record.Bind(texture);
    }

    public void BindFrameBuffer(XRFrameBuffer frameBuffer, FrameBufferResourceDescriptor? descriptor = null)
    {
        ArgumentNullException.ThrowIfNull(frameBuffer);
        string name = frameBuffer.Name ?? throw new InvalidOperationException("FrameBuffer name must be set before binding to the registry.");

        descriptor ??= FrameBufferResourceDescriptor.FromFrameBuffer(frameBuffer);
        descriptor = descriptor with { Name = name };

        RenderFrameBufferResource record = RegisterFrameBufferDescriptor(descriptor);
        record.Bind(frameBuffer);
    }

    public bool TryGetTexture(string name, [NotNullWhen(true)] out XRTexture? texture)
    {
        if (_textures.TryGetValue(name, out RenderTextureResource? record) && record.Instance is XRTexture instance)
        {
            texture = instance;
            return true;
        }

        texture = null;
        return false;
    }

    public bool TryGetFrameBuffer(string name, [NotNullWhen(true)] out XRFrameBuffer? frameBuffer)
    {
        if (_frameBuffers.TryGetValue(name, out RenderFrameBufferResource? record) && record.Instance is XRFrameBuffer instance)
        {
            frameBuffer = instance;
            return true;
        }

        frameBuffer = null;
        return false;
    }

    public IEnumerable<XRTexture> EnumerateTextureInstances()
    {
        foreach (RenderTextureResource record in _textures.Values)
            if (record.Instance is XRTexture tex)
                yield return tex;
    }

    public IEnumerable<XRFrameBuffer> EnumerateFrameBufferInstances()
    {
        foreach (RenderFrameBufferResource record in _frameBuffers.Values)
            if (record.Instance is XRFrameBuffer fbo)
                yield return fbo;
    }

    public void RemoveTexture(string name)
    {
        if (_textures.Remove(name, out RenderTextureResource? record))
            record.DestroyInstance();
    }

    public void RemoveFrameBuffer(string name)
    {
        if (_frameBuffers.Remove(name, out RenderFrameBufferResource? record))
            record.DestroyInstance();
    }

    public void DestroyAllPhysicalResources(bool retainDescriptors = false)
    {
        foreach (RenderTextureResource record in _textures.Values)
            record.DestroyInstance();
        foreach (RenderFrameBufferResource record in _frameBuffers.Values)
            record.DestroyInstance();

        if (!retainDescriptors)
        {
            _textures.Clear();
            _frameBuffers.Clear();
        }
    }
}
