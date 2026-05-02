using Silk.NET.OpenGL;
using XREngine.Data;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    private const double TextureStreamingCacheGpuCookReadbackTimeoutMs = 30000.0;

    private sealed record PendingTextureStreamingCacheMipReadback(
        int MipLevel,
        uint Width,
        uint Height,
        uint Pbo,
        uint ByteSize);

    private sealed class PendingTextureStreamingCacheMipChainReadback
    {
        public required PendingTextureStreamingCacheMipReadback[] Mips { get; init; }
        public required IntPtr Sync { get; init; }
        public required long StartedTicks { get; init; }
        public required GLTexture2D Texture { get; init; }
        public required Action<bool, Mipmap2D[]?, string> Callback { get; init; }
    }

    public override void TryBuildTexture2DMipChainRgba8Async(
        XRTexture2D texture,
        Action<bool, Mipmap2D[]?, string> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        if (!Engine.IsRenderThread)
        {
            RuntimeRenderingHostServices.Current.EnqueueRenderThreadTask(
                () => TryBuildTexture2DMipChainRgba8Async(texture, callback),
                "OpenGL.TextureStreamingCacheGpuCook");
            return;
        }

        if (texture is null)
        {
            callback(false, null, "Texture was null.");
            return;
        }

        if (texture.MultiSample)
        {
            callback(false, null, "Multisample textures cannot be cooked into a streaming mip chain.");
            return;
        }

        if (texture.Mipmaps is not { Length: > 0 } || texture.Mipmaps[0].HasData() != true)
        {
            callback(false, null, "Texture does not have a CPU-backed base mip.");
            return;
        }

        if (GetOrCreateAPIRenderObject(texture, generateNow: true) is not GLTexture2D glTexture)
        {
            callback(false, null, "Texture could not be resolved to an OpenGL texture object.");
            return;
        }

        bool previousAutoGenerate = texture.AutoGenerateMipmaps;
        bool previousResizable = texture.Resizable;
        int previousSmallestAllowedMip = texture.SmallestAllowedMipmapLevel;

        try
        {
            texture.AutoGenerateMipmaps = true;
            texture.Resizable = false;
            texture.SmallestAllowedMipmapLevel = 1000;
            texture.SizedInternalFormat = ESizedInternalFormat.Rgba8;

            if (!glTexture.TryPushBaseLevelAndGenerateMipmapsForTextureStreamingCacheCook(out string pushFailure))
            {
                callback(false, null, pushFailure);
                return;
            }

            if (!glTexture.TryGetBindingId(out uint textureId) || textureId == GLObjectBase.InvalidBindingId)
            {
                callback(false, null, "Texture did not produce a valid OpenGL binding.");
                return;
            }

            int mipCount = Math.Max(1, XRTexture.GetSmallestMipmapLevel(texture.Width, texture.Height) + 1);
            if (!TryBeginTextureStreamingCacheMipChainReadback(
                    textureId,
                    texture.Width,
                    texture.Height,
                    mipCount,
                    glTexture,
                    callback,
                    out string readbackFailure))
            {
                callback(false, null, readbackFailure);
            }
        }
        catch (Exception ex)
        {
            callback(false, null, ex.Message);
        }
        finally
        {
            texture.AutoGenerateMipmaps = previousAutoGenerate;
            texture.Resizable = previousResizable;
            texture.SmallestAllowedMipmapLevel = previousSmallestAllowedMip;
        }
    }

    private unsafe bool TryBeginTextureStreamingCacheMipChainReadback(
        uint textureId,
        uint baseWidth,
        uint baseHeight,
        int mipCount,
        GLTexture2D glTexture,
        Action<bool, Mipmap2D[]?, string> callback,
        out string failure)
    {
        failure = string.Empty;
        if (textureId == GLObjectBase.InvalidBindingId || baseWidth == 0u || baseHeight == 0u || mipCount <= 0)
        {
            failure = "Texture readback request had invalid dimensions or binding.";
            return false;
        }

        PendingTextureStreamingCacheMipReadback[] readbacks = new PendingTextureStreamingCacheMipReadback[mipCount];
        int createdPbos = 0;
        long totalBytes = 0L;

        try
        {
            Api.PixelStore(PixelStoreParameter.PackAlignment, 1);

            for (int mipLevel = 0; mipLevel < mipCount; mipLevel++)
            {
                uint width = Math.Max(1u, baseWidth >> mipLevel);
                uint height = Math.Max(1u, baseHeight >> mipLevel);
                long byteSizeLong = checked((long)width * height * 4L);
                if (byteSizeLong > int.MaxValue)
                {
                    failure = $"Mip {mipLevel} readback is too large ({byteSizeLong} bytes).";
                    CleanupTextureStreamingCacheMipReadbackBuffers(readbacks, createdPbos, sync: IntPtr.Zero);
                    return false;
                }

                uint byteSize = (uint)byteSizeLong;
                uint pbo = Api.GenBuffer();
                Api.BindBuffer(GLEnum.PixelPackBuffer, pbo);
                Api.BufferData(GLEnum.PixelPackBuffer, byteSize, null, GLEnum.StreamRead);
                Api.GetTextureSubImage(
                    textureId,
                    mipLevel,
                    0,
                    0,
                    0,
                    width,
                    height,
                    1,
                    GLEnum.Rgba,
                    GLEnum.UnsignedByte,
                    byteSize,
                    null);

                readbacks[mipLevel] = new PendingTextureStreamingCacheMipReadback(mipLevel, width, height, pbo, byteSize);
                createdPbos++;
                totalBytes += byteSizeLong;
            }

            IntPtr sync = Api.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);
            Api.BindBuffer(GLEnum.PixelPackBuffer, 0);

            if (sync == IntPtr.Zero)
            {
                failure = "glFenceSync returned an invalid handle for texture mip-chain readback.";
                CleanupTextureStreamingCacheMipReadbackBuffers(readbacks, createdPbos, sync);
                return false;
            }

            Engine.Rendering.Stats.RecordGpuReadbackBytes(totalBytes);

            PendingTextureStreamingCacheMipChainReadback pending = new()
            {
                Mips = readbacks,
                Sync = sync,
                StartedTicks = System.Diagnostics.Stopwatch.GetTimestamp(),
                Texture = glTexture,
                Callback = callback
            };

            RuntimeRenderingHostServices.Current.EnqueueRenderThreadCoroutine(
                () => PollTextureStreamingCacheMipChainReadback(pending),
                "OpenGL.TextureStreamingCacheMipReadback");
            return true;
        }
        catch (Exception ex)
        {
            Api.BindBuffer(GLEnum.PixelPackBuffer, 0);
            CleanupTextureStreamingCacheMipReadbackBuffers(readbacks, createdPbos, sync: IntPtr.Zero);
            failure = ex.Message;
            return false;
        }
    }

    private unsafe bool PollTextureStreamingCacheMipChainReadback(PendingTextureStreamingCacheMipChainReadback pending)
    {
        double elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - pending.StartedTicks)
            * 1000.0
            / System.Diagnostics.Stopwatch.Frequency;
        if (elapsedMs >= TextureStreamingCacheGpuCookReadbackTimeoutMs)
        {
            CleanupTextureStreamingCacheMipReadbackBuffers(pending.Mips, pending.Mips.Length, pending.Sync);
            pending.Callback(false, null, "Timed out waiting for texture mip-chain GPU readback.");
            return true;
        }

        GLEnum waitResult = Api.ClientWaitSync(pending.Sync, 0u, 0u);
        if (waitResult != GLEnum.AlreadySignaled && waitResult != GLEnum.ConditionSatisfied)
            return false;

        try
        {
            Mipmap2D[] mipmaps = new Mipmap2D[pending.Mips.Length];
            for (int i = 0; i < pending.Mips.Length; i++)
            {
                PendingTextureStreamingCacheMipReadback readback = pending.Mips[i];
                byte[] data = new byte[(int)readback.ByteSize];
                Api.BindBuffer(GLEnum.PixelPackBuffer, readback.Pbo);
                fixed (byte* ptr = data)
                {
                    Api.GetBufferSubData(GLEnum.PixelPackBuffer, IntPtr.Zero, readback.ByteSize, ptr);
                }

                mipmaps[readback.MipLevel] = new Mipmap2D(
                    readback.Width,
                    readback.Height,
                    EPixelInternalFormat.Rgba8,
                    EPixelFormat.Rgba,
                    EPixelType.UnsignedByte,
                    allocateData: false)
                {
                    Data = new DataSource(data)
                };
            }

            Api.BindBuffer(GLEnum.PixelPackBuffer, 0);
            CleanupTextureStreamingCacheMipReadbackBuffers(pending.Mips, pending.Mips.Length, pending.Sync);
            pending.Texture.ClearInvalidation();
            pending.Callback(true, mipmaps, string.Empty);
            return true;
        }
        catch (Exception ex)
        {
            Api.BindBuffer(GLEnum.PixelPackBuffer, 0);
            CleanupTextureStreamingCacheMipReadbackBuffers(pending.Mips, pending.Mips.Length, pending.Sync);
            pending.Callback(false, null, ex.Message);
            return true;
        }
    }

    private void CleanupTextureStreamingCacheMipReadbackBuffers(
        PendingTextureStreamingCacheMipReadback[] readbacks,
        int count,
        IntPtr sync)
    {
        if (sync != IntPtr.Zero)
            Api.DeleteSync(sync);

        for (int i = 0; i < count && i < readbacks.Length; i++)
        {
            uint pbo = readbacks[i].Pbo;
            if (pbo != 0)
                Api.DeleteBuffer(pbo);
        }
    }
}
