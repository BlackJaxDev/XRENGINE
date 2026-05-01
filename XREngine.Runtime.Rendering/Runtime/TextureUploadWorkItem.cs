using System;

namespace XREngine.Rendering;

public enum TextureUploadKind
{
    Preview,
    Promotion,
    Repair,
    Demotion,
    RenderTargetInit,
}

public enum TextureUploadSourceKind
{
    CpuPointer,
    CookedMip,
    SparsePage,
    Pbo,
}

public enum TextureUploadPriorityClass
{
    VisibleNow,
    NearVisible,
    Background,
    Demotion,
}

internal readonly record struct TextureUploadWorkItem(
    WeakReference<XRTexture2D> Texture,
    TextureUploadKind UploadKind,
    TextureUploadSourceKind SourceKind,
    int FirstMipLevel,
    int MipLevelCount,
    long EstimatedBytes,
    int CapturedStorageGeneration,
    TextureUploadPriorityClass PriorityClass,
    long QueueTimestamp);
