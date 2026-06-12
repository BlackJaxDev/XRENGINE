using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Engine-facing memory intent for <see cref="XRDataBuffer"/>. This describes the caller's
/// update/readback contract; render backends resolve it to OpenGL or Vulkan allocation routes.
/// </summary>
public enum XRBufferMemoryPolicy
{
    /// <summary>Prefer GPU-local storage. CPU writes are uploaded through staging or compatibility upload paths.</summary>
    GpuOnly,

    /// <summary>CPU produces data occasionally and the backend uploads it into final GPU storage.</summary>
    CpuToGpuUpload,

    /// <summary>CPU updates data frequently enough that host-visible or dynamic storage may be cheaper.</summary>
    CpuToGpuDynamic,

    /// <summary>CPU writes per-frame data through a fence-protected persistent mapped ring.</summary>
    CpuToGpuPersistentRing,

    /// <summary>GPU writes data that will be read by the CPU through explicit readback tickets.</summary>
    GpuToCpuReadback,

    /// <summary>Slow shared-memory path for diagnostics and bring-up, not production steady state.</summary>
    CpuGpuSharedDiagnostic,
}

/// <summary>
/// Caller-facing write shape for scoped buffer writers.
/// </summary>
public enum XRBufferWriteMode
{
    /// <summary>Preserve existing contents outside marked dirty ranges.</summary>
    Preserve,

    /// <summary>Discard previous contents and rewrite the requested region.</summary>
    Discard,

    /// <summary>Discard when safe, otherwise route through ring/staging behavior selected by the backend.</summary>
    DiscardOrRing,

    /// <summary>Append the requested elements to the current logical end and upload only the tail.</summary>
    Append,

    /// <summary>Caller may mark multiple dirty ranges; ranges are merged by the buffer.</summary>
    Scattered,
}

/// <summary>
/// CPU access intent that refines <see cref="XRBufferMemoryPolicy"/> where needed.
/// </summary>
[Flags]
public enum XRBufferCpuAccess
{
    None = 0,
    Read = 1,
    Write = 2,
    ReadWrite = Read | Write,
}

public enum XRBufferWriterDisposeBehavior
{
    /// <summary>Dispose commits the writer if it has not already been committed or cancelled.</summary>
    Commit = 0,

    /// <summary>Dispose cancels the writer if it has not already been committed or cancelled.</summary>
    Cancel,

    /// <summary>Dispose reports an error unless the caller explicitly called Commit or Cancel.</summary>
    RequireExplicitCommit,
}

/// <summary>
/// Backend-neutral route chosen from a memory policy. It is diagnostic state, not a CPU pointer.
/// </summary>
public enum XRBufferResolvedRoute
{
    Unknown,
    DeviceLocal,
    StagingUpload,
    HostVisible,
    PersistentMappedRing,
    Readback,
    DiagnosticShared,
    CompatibilityPush,
    Rejected,
}

public static class XRBufferPolicyResolver
{
    public static XRBufferMemoryPolicy FromUsage(EBufferUsage usage, bool shouldMap, EBufferMapStorageFlags storageFlags, EBufferMapRangeFlags rangeFlags)
    {
        if (shouldMap ||
            storageFlags.HasFlag(EBufferMapStorageFlags.Persistent) ||
            rangeFlags.HasFlag(EBufferMapRangeFlags.Persistent))
        {
            return storageFlags.HasFlag(EBufferMapStorageFlags.Read) || rangeFlags.HasFlag(EBufferMapRangeFlags.Read)
                ? XRBufferMemoryPolicy.CpuGpuSharedDiagnostic
                : XRBufferMemoryPolicy.CpuToGpuPersistentRing;
        }

        if (storageFlags.HasFlag(EBufferMapStorageFlags.Read) ||
            rangeFlags.HasFlag(EBufferMapRangeFlags.Read) ||
            usage is EBufferUsage.StaticRead or EBufferUsage.DynamicRead or EBufferUsage.StreamRead)
        {
            return XRBufferMemoryPolicy.GpuToCpuReadback;
        }

        return usage switch
        {
            EBufferUsage.StaticDraw or EBufferUsage.StaticCopy => XRBufferMemoryPolicy.GpuOnly,
            EBufferUsage.DynamicDraw or EBufferUsage.DynamicCopy => XRBufferMemoryPolicy.CpuToGpuDynamic,
            EBufferUsage.StreamDraw or EBufferUsage.StreamCopy => XRBufferMemoryPolicy.CpuToGpuPersistentRing,
            _ => XRBufferMemoryPolicy.CpuToGpuUpload,
        };
    }

    public static EBufferUsage ToUsage(XRBufferMemoryPolicy policy, EBufferUsage previousUsage)
        => policy switch
        {
            XRBufferMemoryPolicy.GpuOnly => previousUsage is EBufferUsage.StaticRead ? EBufferUsage.StaticCopy : EBufferUsage.StaticDraw,
            XRBufferMemoryPolicy.CpuToGpuUpload => EBufferUsage.StaticCopy,
            XRBufferMemoryPolicy.CpuToGpuDynamic => EBufferUsage.DynamicDraw,
            XRBufferMemoryPolicy.CpuToGpuPersistentRing => EBufferUsage.StreamDraw,
            XRBufferMemoryPolicy.GpuToCpuReadback => EBufferUsage.StreamRead,
            XRBufferMemoryPolicy.CpuGpuSharedDiagnostic => EBufferUsage.DynamicCopy,
            _ => previousUsage,
        };

    public static XRBufferResolvedRoute ResolveOpenGL(
        XRBufferMemoryPolicy policy,
        EBufferMapStorageFlags storageFlags,
        EBufferMapRangeFlags rangeFlags,
        bool uploadQueueEnabled,
        uint byteCount)
        => policy switch
        {
            XRBufferMemoryPolicy.GpuOnly => uploadQueueEnabled && byteCount > 64u * 1024u
                ? XRBufferResolvedRoute.StagingUpload
                : XRBufferResolvedRoute.CompatibilityPush,
            XRBufferMemoryPolicy.CpuToGpuUpload => XRBufferResolvedRoute.CompatibilityPush,
            XRBufferMemoryPolicy.CpuToGpuDynamic => XRBufferResolvedRoute.HostVisible,
            XRBufferMemoryPolicy.CpuToGpuPersistentRing => HasPersistentIntent(storageFlags, rangeFlags)
                ? XRBufferResolvedRoute.PersistentMappedRing
                : XRBufferResolvedRoute.HostVisible,
            XRBufferMemoryPolicy.GpuToCpuReadback => XRBufferResolvedRoute.Readback,
            XRBufferMemoryPolicy.CpuGpuSharedDiagnostic => XRBufferResolvedRoute.DiagnosticShared,
            _ => XRBufferResolvedRoute.Unknown,
        };

    public static XRBufferResolvedRoute ResolveVulkan(
        XRBufferMemoryPolicy policy,
        bool supportsPersistentRing,
        bool supportsDeviceLocal)
        => policy switch
        {
            XRBufferMemoryPolicy.GpuOnly => supportsDeviceLocal
                ? XRBufferResolvedRoute.DeviceLocal
                : XRBufferResolvedRoute.StagingUpload,
            XRBufferMemoryPolicy.CpuToGpuUpload => XRBufferResolvedRoute.StagingUpload,
            XRBufferMemoryPolicy.CpuToGpuDynamic => XRBufferResolvedRoute.HostVisible,
            XRBufferMemoryPolicy.CpuToGpuPersistentRing => supportsPersistentRing
                ? XRBufferResolvedRoute.PersistentMappedRing
                : XRBufferResolvedRoute.HostVisible,
            XRBufferMemoryPolicy.GpuToCpuReadback => XRBufferResolvedRoute.Readback,
            XRBufferMemoryPolicy.CpuGpuSharedDiagnostic => XRBufferResolvedRoute.DiagnosticShared,
            _ => XRBufferResolvedRoute.Unknown,
        };

    private static bool HasPersistentIntent(EBufferMapStorageFlags storageFlags, EBufferMapRangeFlags rangeFlags)
        => storageFlags.HasFlag(EBufferMapStorageFlags.Persistent) ||
           rangeFlags.HasFlag(EBufferMapRangeFlags.Persistent);
}
