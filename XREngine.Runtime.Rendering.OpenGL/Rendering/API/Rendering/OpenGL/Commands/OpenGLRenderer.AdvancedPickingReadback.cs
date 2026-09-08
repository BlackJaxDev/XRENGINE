using Silk.NET.OpenGL;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    private const double AdvancedPickingReadbackTimeoutMilliseconds = 5_000.0;

    public override unsafe bool TryQueueAdvancedPickingReadback(
        XRTexture identity,
        XRTexture metadata,
        XRTexture selection,
        in AdvancedPickingQuery query,
        Action<AdvancedVisibilityEncodedSurface> callback,
        out string? failure)
    {
        ArgumentNullException.ThrowIfNull(callback);
        failure = null;
        if (!RuntimeEngine.IsRenderThread)
        {
            failure = "OpenGL Advanced picking readback must be queued on the render thread.";
            return false;
        }

        if (!TryResolvePickingTexture(identity, query, out uint identityTexture) ||
            !TryResolvePickingTexture(metadata, query, out uint metadataTexture) ||
            !TryResolvePickingTexture(selection, query, out uint selectionTexture))
        {
            failure = "OpenGL Advanced picking requires the complete, already-realized visibility texture set in the current context at the requested coordinate and view.";
            return false;
        }

        uint pbo = 0u;
        IntPtr sync = IntPtr.Zero;
        int previousPackBuffer = Api.GetInteger(GLEnum.PixelPackBufferBinding);
        try
        {
            Api.CreateBuffers(1, out pbo);
            if (pbo == GLObjectBase.InvalidBindingId)
            {
                failure = "OpenGL could not allocate an Advanced picking staging buffer.";
                return false;
            }

            Api.NamedBufferData(
                pbo,
                AdvancedPickingContract.ReadbackByteCount,
                (void*)null,
                GLEnum.StreamRead);
            Api.BindBuffer(GLEnum.PixelPackBuffer, pbo);
            MemoryBarrier(
                EMemoryBarrierMask.Framebuffer |
                EMemoryBarrierMask.TextureFetch |
                EMemoryBarrierMask.TextureUpdate |
                EMemoryBarrierMask.PixelBuffer);

            int layer = checked((int)query.ViewIndex);
            Api.GetTextureSubImage(
                identityTexture,
                0,
                checked((int)query.CoordX),
                checked((int)query.CoordY),
                layer,
                1u,
                1u,
                1u,
                GLEnum.RGInteger,
                GLEnum.UnsignedInt,
                2u * sizeof(uint),
                (void*)0);
            Api.GetTextureSubImage(
                metadataTexture,
                0,
                checked((int)query.CoordX),
                checked((int)query.CoordY),
                layer,
                1u,
                1u,
                1u,
                GLEnum.RedInteger,
                GLEnum.UnsignedInt,
                sizeof(uint),
                (void*)(2u * sizeof(uint)));
            Api.GetTextureSubImage(
                selectionTexture,
                0,
                checked((int)query.CoordX),
                checked((int)query.CoordY),
                layer,
                1u,
                1u,
                1u,
                GLEnum.RedInteger,
                GLEnum.UnsignedInt,
                sizeof(uint),
                (void*)(3u * sizeof(uint)));
            GLEnum readbackError = Api.GetError();
            if (readbackError != GLEnum.NoError)
                throw new InvalidOperationException($"Visibility texture transfer failed with {readbackError}.");
            sync = Api.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);
            if (sync == IntPtr.Zero)
            {
                Api.DeleteBuffer(pbo);
                failure = "OpenGL could not create the Advanced picking completion fence.";
                return false;
            }
        }
        catch (Exception ex)
        {
            if (sync != IntPtr.Zero)
                Api.DeleteSync(sync);
            if (pbo != GLObjectBase.InvalidBindingId)
                Api.DeleteBuffer(pbo);
            failure = $"OpenGL could not queue Advanced picking readback: {ex.Message}";
            return false;
        }
        finally { Api.BindBuffer(GLEnum.PixelPackBuffer, (uint)previousPackBuffer); }

        uint ownedPbo = pbo;
        IntPtr ownedSync = sync;
        long startedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        (XRTexture Identity, XRTexture Metadata, XRTexture Selection) retainedSources =
            (identity, metadata, selection);
        bool PollCompletion()
        {
            double elapsedMilliseconds =
                (System.Diagnostics.Stopwatch.GetTimestamp() - startedTimestamp) *
                1000.0 /
                System.Diagnostics.Stopwatch.Frequency;
            if (elapsedMilliseconds >= AdvancedPickingReadbackTimeoutMilliseconds)
            {
                GC.KeepAlive(retainedSources);
                ReleasePickingReadback(ownedPbo, ownedSync);
                callback(AdvancedVisibilityEncodedSurface.Invalid);
                return true;
            }

            GLEnum status = Api.ClientWaitSync(ownedSync, 0u, 0u);
            if (status is not GLEnum.AlreadySignaled and not GLEnum.ConditionSatisfied)
            {
                if (status != GLEnum.WaitFailed)
                    return false;

                GC.KeepAlive(retainedSources);
                ReleasePickingReadback(ownedPbo, ownedSync);
                callback(AdvancedVisibilityEncodedSurface.Invalid);
                return true;
            }

            Span<uint> words = stackalloc uint[checked((int)AdvancedPickingContract.ReadbackWordCount)];
            fixed (uint* destination = words)
            {
                Api.GetNamedBufferSubData(
                    ownedPbo,
                    IntPtr.Zero,
                    AdvancedPickingContract.ReadbackByteCount,
                    destination);
            }
            RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuReadbackBytes(
                AdvancedPickingContract.ReadbackByteCount);
            GC.KeepAlive(retainedSources);
            ReleasePickingReadback(ownedPbo, ownedSync);
            callback(new AdvancedVisibilityEncodedSurface(
                new AdvancedVisibilityPayloadWords(words[0], words[1]),
                new AdvancedVisibilityMetadataWord(words[2]),
                words[3]));
            return true;
        }

        RuntimeEngine.AddMainThreadCoroutine(PollCompletion);
        return true;
    }

    private bool TryResolvePickingTexture(
        XRTexture texture,
        in AdvancedPickingQuery query,
        out uint textureId)
    {
        textureId = 0u;
        // A readback must not generate a fresh texture name for retired/unrendered
        // storage. Freeze the existing native name and validate it in this context.
        if (GetOrCreateAPIRenderObject(texture, generateNow: false) is not GLObjectBase candidate ||
            !candidate.TryGetBindingId(out uint bindingId) || !Api.IsTexture(bindingId))
            return false;

        Api.GetTextureLevelParameter(bindingId, 0, GLEnum.TextureWidth, out int width);
        Api.GetTextureLevelParameter(bindingId, 0, GLEnum.TextureHeight, out int height);
        Api.GetTextureLevelParameter(bindingId, 0, GLEnum.TextureDepth, out int depth);
        if (query.CoordX >= (uint)Math.Max(0, width) ||
            query.CoordY >= (uint)Math.Max(0, height) ||
            query.ViewIndex >= (uint)Math.Max(1, depth))
        {
            return false;
        }

        textureId = bindingId;
        return true;
    }

    private void ReleasePickingReadback(uint pbo, IntPtr sync)
    {
        Api.DeleteSync(sync);
        Api.DeleteBuffer(pbo);
    }
}
