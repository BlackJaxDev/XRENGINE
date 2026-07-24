using XREngine.Extensions;
using ImageMagick;
using ImGuiNET;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ARB;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.OpenGL.Extensions.NV;
using Silk.NET.OpenGL.Extensions.OVR;
using Silk.NET.OpenGLES.Extensions.EXT;
using Silk.NET.OpenGLES.Extensions.NV;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Textures;
using XREngine.Rendering.UI;
using XREngine.Rendering.Shaders.Generator;
using PixelFormat = Silk.NET.OpenGL.PixelFormat;
using XREngine.Components;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    private enum FrontLuminanceReadbackMode
    {
        Mipmap,
        Compute
    }

    private sealed class PendingFrontLuminanceReadback
    {
        public required Action<bool, float> Callback { get; init; }
        public required FrontLuminanceReadbackMode Mode { get; init; }
        public required Vector3 LuminanceWeights { get; init; }
        public required IntPtr Sync { get; init; }
        public required long StartedTicks { get; init; }
        public required long LastPollTicks { get; set; }
    }

    private const double FrontLuminanceReadbackMinIntervalMs = 66.0;
    private const double FrontLuminanceReadbackPollIntervalMs = 33.0;
    private const double FrontLuminanceReadbackTimeoutMs = 250.0;

    private PendingFrontLuminanceReadback? _pendingFrontLuminanceReadback;
    private long _lastFrontLuminanceRequestTicks;
    private bool _hasFrontLuminanceSample;
    private float _lastFrontLuminanceSample;

    private static long FrontLuminanceMillisecondsToTicks(double milliseconds)
        => (long)(System.Diagnostics.Stopwatch.Frequency * (milliseconds / 1000.0));

    private void QueueFrontLuminanceCallback(Action<bool, float> callback, bool success, float dot)
        => RuntimeEngine.EnqueueAppThreadTask(() => callback(success, dot), "GLRenderer.FrontLuminance.Callback");

    private void QueueCachedFrontLuminanceCallback(Action<bool, float> callback)
        => QueueFrontLuminanceCallback(callback, _hasFrontLuminanceSample, _hasFrontLuminanceSample ? _lastFrontLuminanceSample : 0.0f);

    private void CompletePendingFrontLuminanceReadback(PendingFrontLuminanceReadback pending, bool success, float dot)
    {
        _pendingFrontLuminanceReadback = null;
        QueueFrontLuminanceCallback(pending.Callback, success, dot);
    }

    private void CancelPendingFrontLuminanceReadback()
    {
        if (_pendingFrontLuminanceReadback is not { } pending)
            return;

        if (!ShouldOrphanGLHandlesForShutdown)
            Api.DeleteSync(pending.Sync);
        _pendingFrontLuminanceReadback = null;
    }

    private unsafe bool TryServicePendingFrontLuminanceReadback(long nowTicks)
    {
        if (_pendingFrontLuminanceReadback is not { } pending)
            return false;

        long timeoutTicks = FrontLuminanceMillisecondsToTicks(FrontLuminanceReadbackTimeoutMs);
        if (nowTicks - pending.StartedTicks >= timeoutTicks)
        {
            Api.DeleteSync(pending.Sync);
            CompletePendingFrontLuminanceReadback(pending, _hasFrontLuminanceSample, _hasFrontLuminanceSample ? _lastFrontLuminanceSample : 0.0f);
            return false;
        }

        long pollIntervalTicks = FrontLuminanceMillisecondsToTicks(FrontLuminanceReadbackPollIntervalMs);
        if (nowTicks - pending.LastPollTicks < pollIntervalTicks)
            return true;

        pending.LastPollTicks = nowTicks;

        switch (pending.Mode)
        {
            case FrontLuminanceReadbackMode.Mipmap:
                {
                    if (!GetData(_luminanceFrontPboSize, _rgbDataForAsync(ref _asyncBuffer), pending.Sync, _luminanceFrontPbo))
                        return true;

                    Api.DeleteSync(pending.Sync);

                    float r = _asyncBuffer[0] / 255.0f;
                    float g = _asyncBuffer[1] / 255.0f;
                    float b = _asyncBuffer[2] / 255.0f;
                    if (float.IsNaN(r) || float.IsNaN(g) || float.IsNaN(b))
                    {
                        CompletePendingFrontLuminanceReadback(pending, _hasFrontLuminanceSample, _hasFrontLuminanceSample ? _lastFrontLuminanceSample : 0.0f);
                        return false;
                    }

                    _lastFrontLuminanceSample = new Vector3(r, g, b).Dot(pending.LuminanceWeights);
                    _hasFrontLuminanceSample = true;
                    CompletePendingFrontLuminanceReadback(pending, true, _lastFrontLuminanceSample);
                    return false;
                }

            case FrontLuminanceReadbackMode.Compute:
                {
                    var waitResult = Api.ClientWaitSync(pending.Sync, 0u, 0u);
                    if (!(waitResult == GLEnum.AlreadySignaled || waitResult == GLEnum.ConditionSatisfied))
                        return true;

                    Api.DeleteSync(pending.Sync);

                    Vector4 average;
                    Api.BindBuffer(GLEnum.ShaderStorageBuffer, _luminanceResultBuffer);
                    Api.GetBufferSubData(GLEnum.ShaderStorageBuffer, IntPtr.Zero, 16, &average);
                    Api.BindBuffer(GLEnum.ShaderStorageBuffer, 0);

                    if (float.IsNaN(average.X) || float.IsNaN(average.Y) || float.IsNaN(average.Z))
                    {
                        CompletePendingFrontLuminanceReadback(pending, _hasFrontLuminanceSample, _hasFrontLuminanceSample ? _lastFrontLuminanceSample : 0.0f);
                        return false;
                    }

                    _lastFrontLuminanceSample = new Vector3(average.X, average.Y, average.Z).Dot(pending.LuminanceWeights);
                    _hasFrontLuminanceSample = true;
                    CompletePendingFrontLuminanceReadback(pending, true, _lastFrontLuminanceSample);
                    return false;
                }

            default:
                return false;
        }
    }

    private bool PollPendingFrontLuminanceReadback()
    {
        if (_pendingFrontLuminanceReadback is null)
            return true;

        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        return !TryServicePendingFrontLuminanceReadback(nowTicks);
    }

    public override unsafe void CalcDotLuminanceAsync(XRTexture2DArray texture, Action<bool, float> callback, Vector3 luminance, bool genMipmapsNow = true)
    {
        using var prof = RuntimeEngine.Profiler.Start("GLRenderer.CalcDotLuminanceAsync");

        if (IsGpuZeroReadbackActive())
        {
            callback(false, 0.0f);
            return;
        }

        var glTex = GenericToAPI<GLTexture2DArray>(texture);
        if (glTex is null)
        {
            callback(false, 0.0f);
            return;
        }

        if (genMipmapsNow)
            glTex.GenerateMipmaps();

        int mipLevel = XRTexture.GetSmallestMipmapLevel(texture.Width, texture.Height, texture.SmallestAllowedMipmapLevel);
        int layerCount = (int)texture.Depth;
        if (layerCount <= 0)
        {
            callback(false, 0.0f);
            return;
        }

        uint byteSize = (uint)(sizeof(Vector4) * layerCount);
        uint pbo = Api.GenBuffer();
        Api.BindBuffer(GLEnum.PixelPackBuffer, pbo);
        Api.BufferData(GLEnum.PixelPackBuffer, byteSize, null, GLEnum.StreamRead);

        Api.GetTextureSubImage(
            glTex.BindingId,
            mipLevel,
            0, 0, 0,
            1, 1, (uint)layerCount,
            GLObjectBase.ToGLEnum(EPixelFormat.Rgba),
            GLObjectBase.ToGLEnum(EPixelType.Float),
            byteSize,
            (void*)IntPtr.Zero);

        IntPtr sync = Api.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);
        Api.BindBuffer(GLEnum.PixelPackBuffer, 0);

        bool FenceCheck()
        {
            if (!GetData(byteSize, _rgbDataForAsync(ref _asyncBuffer), sync, pbo))
                return false;

            Api.DeleteSync(sync);
            Api.DeleteBuffer(pbo);

            Span<Vector4> samples = MemoryMarshal.Cast<byte, Vector4>(_asyncBuffer.AsSpan(0, (int)byteSize));
            Vector3 accum = Vector3.Zero;
            for (int i = 0; i < layerCount; i++)
            {
                Vector4 s = samples[i];
                if (float.IsNaN(s.X) || float.IsNaN(s.Y) || float.IsNaN(s.Z))
                {
                    callback(false, 0.0f);
                    return true;
                }
                accum += s.XYZ();
            }

            Vector3 average = accum / layerCount;
            callback(true, average.Dot(luminance));
            return true;
        }

        RuntimeEngine.AddMainThreadCoroutine(FenceCheck);
    }
    public override unsafe void CalcDotLuminanceAsync(XRTexture2D texture, Action<bool, float> callback, Vector3 luminance, bool genMipmapsNow = true)
    {
        using var prof = RuntimeEngine.Profiler.Start("GLRenderer.CalcDotLuminanceAsync");

        if (IsGpuZeroReadbackActive())
        {
            callback(false, 0.0f);
            return;
        }

        var glTex = GenericToAPI<GLTexture2D>(texture);
        if (glTex is null)
        {
            callback(false, 0.0f);
            return;
        }

        if (genMipmapsNow)
            glTex.GenerateMipmaps();

        int mipLevel = XRTexture.GetSmallestMipmapLevel(texture.Width, texture.Height, texture.SmallestAllowedMipmapLevel);

        uint byteSize = (uint)sizeof(Vector4);
        uint pbo = Api.GenBuffer();
        Api.BindBuffer(GLEnum.PixelPackBuffer, pbo);
        Api.BufferData(GLEnum.PixelPackBuffer, byteSize, null, GLEnum.StreamRead);

        Api.GetTextureImage(
            glTex.BindingId,
            mipLevel,
            GLObjectBase.ToGLEnum(EPixelFormat.Rgba),
            GLObjectBase.ToGLEnum(EPixelType.Float),
            byteSize,
            (void*)IntPtr.Zero);

        IntPtr sync = Api.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);
        Api.BindBuffer(GLEnum.PixelPackBuffer, 0);

        bool FenceCheck()
        {
            if (!GetData(byteSize, _rgbDataForAsync(ref _asyncBuffer), sync, pbo))
                return false;

            Api.DeleteSync(sync);
            Api.DeleteBuffer(pbo);

            Vector3 rgb;
            unsafe
            {
                fixed (byte* ptr = _asyncBuffer)
                {
                    float* fptr = (float*)ptr;
                    rgb = new(fptr[0], fptr[1], fptr[2]);
                }
            }

            if (float.IsNaN(rgb.X) || float.IsNaN(rgb.Y) || float.IsNaN(rgb.Z))
            {
                callback(false, 0.0f);
                return true;
            }

            callback(true, rgb.Dot(luminance));
            return true;
        }

        RuntimeEngine.AddMainThreadCoroutine(FenceCheck);
    }

    public override unsafe bool CalcDotLuminance(XRTexture2DArray texture, Vector3 luminance, out float dotLuminance, bool genMipmapsNow = true)
    {
        using var prof = RuntimeEngine.Profiler.Start("GLRenderer.CalcDotLuminance");

        dotLuminance = 1.0f;
        if (IsGpuZeroReadbackActive())
            return false;

        var glTex = GenericToAPI<GLTexture2DArray>(texture);
        if (glTex is null)
            return false;

        if (genMipmapsNow)
            glTex.GenerateMipmaps();

        int layerCount = (int)texture.Depth;
        if (layerCount <= 0)
            return false;

        Span<Vector4> samples = layerCount <= 8
            ? stackalloc Vector4[layerCount]
            : new Vector4[layerCount];

        int mipLevel = XRTexture.GetSmallestMipmapLevel(texture.Width, texture.Height, texture.SmallestAllowedMipmapLevel);

        fixed (Vector4* ptr = samples)
        {
            uint byteSize = (uint)(sizeof(Vector4) * layerCount);
            Api.GetTextureImage(
                glTex.BindingId,
                mipLevel,
                GLObjectBase.ToGLEnum(EPixelFormat.Rgba),
                GLObjectBase.ToGLEnum(EPixelType.Float),
                byteSize,
                ptr);
        }

        Vector3 accum = Vector3.Zero;
        for (int i = 0; i < samples.Length; i++)
        {
            Vector4 sample = samples[i];
            if (float.IsNaN(sample.X) || float.IsNaN(sample.Y) || float.IsNaN(sample.Z))
                return false;

            accum += sample.XYZ();
        }

        Vector3 average = accum / layerCount;
        dotLuminance = average.Dot(luminance);
        return true;
    }
    public override unsafe bool CalcDotLuminance(XRTexture2D texture, Vector3 luminance, out float dotLuminance, bool genMipmapsNow = true)
    {
        using var prof = RuntimeEngine.Profiler.Start("GLRenderer.CalcDotLuminance");

        dotLuminance = 1.0f;
        if (IsGpuZeroReadbackActive())
            return false;

        var glTex = GenericToAPI<GLTexture2D>(texture);
        if (glTex is null)
            return false;

        //Calculate average color value using 1x1 mipmap of scene
        if (genMipmapsNow)
            glTex.GenerateMipmaps();

        int mipLevel = XRTexture.GetSmallestMipmapLevel(texture.Width, texture.Height, texture.SmallestAllowedMipmapLevel);

        //Get the average color from the scene texture
        Vector4 rgb = Vector4.Zero;
        void* addr = &rgb;
        Api.GetTextureImage(glTex.BindingId, mipLevel, GLObjectBase.ToGLEnum(EPixelFormat.Rgba), GLObjectBase.ToGLEnum(EPixelType.Float), (uint)sizeof(Vector4), addr);

        if (float.IsNaN(rgb.X) ||
            float.IsNaN(rgb.Y) ||
            float.IsNaN(rgb.Z))
            return false;

        //Calculate luminance factor off of the average color
        dotLuminance = rgb.XYZ().Dot(luminance);
        return true;
    }

    /// <inheritdoc/>
    public override unsafe float ReadTextureCenterRedMip0(XRTexture2D texture)
    {
        if (IsGpuZeroReadbackActive())
            return 0.0f;

        var glTex = GenericToAPI<GLTexture2D>(texture);
        if (glTex is null || !glTex.IsGenerated)
            return 0.0f;

        uint w = texture.Width;
        uint h = texture.Height;
        if (w == 0 || h == 0)
            return 0.0f;

        // Read a single center pixel at mip 0 via glGetTextureSubImage (DSA, no binding changes).
        int cx = (int)(w / 2);
        int cy = (int)(h / 2);
        float pixel = 0.0f;
        Api.GetTextureSubImage(
            glTex.BindingId,
            0,              // mip level 0
            cx, cy, 0,      // offset
            1u, 1u, 1u,     // size: 1x1x1
            GLObjectBase.ToGLEnum(EPixelFormat.Red),
            GLObjectBase.ToGLEnum(EPixelType.Float),
            (uint)sizeof(float),
            &pixel);

        return float.IsNaN(pixel) ? 0.0f : pixel;
    }

    public override unsafe void CalcDotLuminanceFrontAsync(BoundingRectangle region, bool withTransparency, Vector3 luminance, Action<bool, float> callback)
    {
        using var prof = RuntimeEngine.Profiler.Start("GLRenderer.CalcDotLuminanceFrontAsync");

        if (IsGpuZeroReadbackActive())
        {
            QueueCachedFrontLuminanceCallback(callback);
            return;
        }

        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        TryServicePendingFrontLuminanceReadback(nowTicks);
        if (_pendingFrontLuminanceReadback is not null)
        {
            QueueCachedFrontLuminanceCallback(callback);
            return;
        }

        long minIntervalTicks = FrontLuminanceMillisecondsToTicks(FrontLuminanceReadbackMinIntervalMs);
        if (_hasFrontLuminanceSample && nowTicks - _lastFrontLuminanceRequestTicks < minIntervalTicks)
        {
            QueueCachedFrontLuminanceCallback(callback);
            return;
        }

        uint w = (uint)region.Width;
        uint h = (uint)region.Height;
        if (w == 0 || h == 0)
        {
            QueueFrontLuminanceCallback(callback, false, 0.0f);
            return;
        }

        // Copy the requested front buffer region into a cached FBO-backed texture, generate mipmaps, then read the 1x1 mip via ReadPixels.
        Api.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        Api.ReadBuffer(ReadBufferMode.Front);

        // Check if we need to reallocate the cached texture/FBO (dimensions changed or not yet allocated)
        int mipLevels = 1 + (int)MathF.Floor(MathF.Log2(MathF.Max(w, h)));
        if (mipLevels < 1)
            mipLevels = 1;

        if (_luminanceFrontTex == 0 || _luminanceFrontTexWidth != w || _luminanceFrontTexHeight != h)
        {
            // Clean up old resources if they exist
            if (_luminanceFrontTex != 0)
                Api.DeleteTexture(_luminanceFrontTex);
            if (_luminanceFrontFbo != 0)
                Api.DeleteFramebuffer(_luminanceFrontFbo);
            if (_luminanceFrontPbo != 0)
                Api.DeleteBuffer(_luminanceFrontPbo);

            // Create new texture with immutable storage
            _luminanceFrontTex = Api.GenTexture();
            Api.BindTexture(TextureTarget.Texture2D, _luminanceFrontTex);
            Api.TexStorage2D(TextureTarget.Texture2D, (uint)mipLevels, GLEnum.Rgba8, w, h);

            // Create FBO and attach texture
            _luminanceFrontFbo = Api.GenFramebuffer();
            Api.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _luminanceFrontFbo);
            Api.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _luminanceFrontTex, 0);

            // Create cached PBO for readback (4 bytes for RGBA8)
            _luminanceFrontPbo = Api.GenBuffer();
            _luminanceFrontPboSize = 4;
            Api.BindBuffer(GLEnum.PixelPackBuffer, _luminanceFrontPbo);
            var nullPtr = IntPtr.Zero;
            Api.BufferData(GLEnum.PixelPackBuffer, _luminanceFrontPboSize, in nullPtr, GLEnum.StreamRead);
            Api.BindBuffer(GLEnum.PixelPackBuffer, 0);

            _luminanceFrontTexWidth = w;
            _luminanceFrontTexHeight = h;
            _luminanceFrontMipLevels = mipLevels;
        }
        else
        {
            Api.BindTexture(TextureTarget.Texture2D, _luminanceFrontTex);
            Api.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _luminanceFrontFbo);
            // Re-attach mip 0 for the blit target
            Api.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _luminanceFrontTex, 0);
        }

        Api.BlitFramebuffer(
            region.X, region.Y, region.X + (int)w, region.Y + (int)h,
            0, 0, (int)w, (int)h,
            ClearBufferMask.ColorBufferBit,
            GLEnum.Linear);

        Api.BindTexture(TextureTarget.Texture2D, _luminanceFrontTex);
        Api.GenerateMipmap(TextureTarget.Texture2D);

        int mipLevel = XRTexture.GetSmallestMipmapLevel(w, h);

        // Re-attach the smallest mip for readback.
        Api.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _luminanceFrontFbo);
        Api.FramebufferTexture2D(FramebufferTarget.ReadFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _luminanceFrontTex, mipLevel);
        Api.ReadBuffer(ReadBufferMode.ColorAttachment0);

        // Use cached PBO for async readback
        uint pbo = _luminanceFrontPbo;
        uint byteSize = _luminanceFrontPboSize;
        Api.BindBuffer(GLEnum.PixelPackBuffer, pbo);

        Api.ReadPixels(0, 0, 1, 1, GLObjectBase.ToGLEnum(EPixelFormat.Rgba), GLObjectBase.ToGLEnum(EPixelType.UnsignedByte), (void*)IntPtr.Zero);

        IntPtr sync = Api.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);
        Api.BindBuffer(GLEnum.PixelPackBuffer, 0);

        _lastFrontLuminanceRequestTicks = nowTicks;
        _pendingFrontLuminanceReadback = new PendingFrontLuminanceReadback
        {
            Callback = callback,
            Mode = FrontLuminanceReadbackMode.Mipmap,
            LuminanceWeights = luminance,
            Sync = sync,
            StartedTicks = nowTicks,
            LastPollTicks = nowTicks
        };
        RuntimeEngine.AddMainThreadCoroutine(PollPendingFrontLuminanceReadback, "GLRenderer.FrontLuminanceReadback");
    }
}
