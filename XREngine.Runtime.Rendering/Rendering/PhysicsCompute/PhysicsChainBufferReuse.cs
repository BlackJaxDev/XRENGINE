namespace XREngine.Rendering.Compute;

/// <summary>
/// Decides from native content-reuse status whether physics chain GPU buffers can be
/// overwritten. The checks do not wait and do not read GPU memory.
/// </summary>
internal static class PhysicsChainBufferReuse
{
    /// <summary>
    /// Returns true when the backend reports that all uses of <paramref name="buffer"/> are complete.
    /// A missing buffer, or a buffer with no native object and no uploaded bytes, has no GPU use
    /// and is ready.
    /// </summary>
    public static bool IsReady(IGpuBufferContentReuseCapability capability, XRDataBuffer? buffer)
    {
        if (buffer is null)
            return true;
        XRBufferStateSnapshot state = buffer.GetStateSnapshot();
        // Client storage can have bytes before any native buffer exists.
        if (!state.IsApiObjectGenerated && state.UploadedByteCount == 0u)
            return true;
        return capability.QueryBufferContentReuse(buffer) == EGpuBufferContentReuseStatus.Ready;
    }

    /// <summary>
    /// Decides whether the buffers of a free output page permit reuse. The caller must first make
    /// sure that the page is not published, not history, not retained, and that its producer fence
    /// permits reuse.
    /// </summary>
    /// <remarks>
    /// Production creates or resizes the bounds atlas and slot metadata lazily and is the only path
    /// that makes them ready. When a production attempt ends before that, the backend reports
    /// <see cref="EGpuBufferContentReuseStatus.Unsupported"/> because it cannot capture the buffer
    /// without an upload. Waiting cannot fix this, because the page must be reusable before production
    /// runs again. Such a bounds buffer does not block reuse. It is reported for release, and production
    /// recreates it. Palettes are never released, so a palette that is not ready blocks reuse. When any
    /// buffer blocks reuse, no buffer is reported for release.
    /// </remarks>
    public static bool TryEvaluateFreeOutputPage(IGpuBufferContentReuseCapability capability,
        XRDataBuffer? boundsAtlas, XRDataBuffer? slotMetadata,
        XRDataBuffer? currentPalette, XRDataBuffer? previousPalette,
        out bool releaseBoundsAtlas, out bool releaseSlotMetadata)
    {
        releaseSlotMetadata = false;
        if (IsReusableBoundsBuffer(capability, boundsAtlas, out releaseBoundsAtlas)
            && IsReusableBoundsBuffer(capability, slotMetadata, out releaseSlotMetadata)
            && IsReady(capability, currentPalette)
            && IsReady(capability, previousPalette))
            return true;

        releaseBoundsAtlas = false;
        releaseSlotMetadata = false;
        return false;
    }

    private static bool IsReusableBoundsBuffer(IGpuBufferContentReuseCapability capability,
        XRDataBuffer? buffer, out bool unprepared)
    {
        unprepared = false;
        if (IsReady(capability, buffer))
            return true;
        unprepared = capability.QueryBufferContentReuse(buffer!) == EGpuBufferContentReuseStatus.Unsupported;
        return unprepared;
    }
}
