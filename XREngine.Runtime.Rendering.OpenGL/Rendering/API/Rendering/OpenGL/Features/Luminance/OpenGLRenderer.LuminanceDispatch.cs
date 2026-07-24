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
    public override bool UpdateAutoExposureGpu(XRTexture sourceTex, XRTexture2D exposureTex, ColorGradingSettings settings, float deltaTime, bool generateMipmapsNow)
    {
        using var prof = RuntimeEngine.Profiler.Start("GLRenderer.UpdateAutoExposureGpu");

        EnsureAutoExposureComputeResources();
        if (!_autoExposureComputeInitialized)
        {
            // Prevent repeated attempts if compilation failed (ensures CPU fallback is used).
            _supportsGpuAutoExposure = false;
            return false;
        }

        var glExposure = GetOrCreateAPIRenderObject(exposureTex, generateNow: true) as GLTexture2D;
        if (glExposure is null)
        {
            string exposureTextureName = exposureTex.Name ?? "<unnamed>";

            // An invalid image binding causes expensive GL debug-driver error handling
            // and repeated stalls. Drop back to the CPU exposure path for this session.
            Debug.OpenGLWarningEvery(
                $"AutoExposure.InvalidExposure.{exposureTextureName}",
                TimeSpan.FromSeconds(1),
                "[AutoExposure] Exposure texture is not ready for image load/store. Texture='{0}', BindingId={1}.",
                exposureTextureName,
                glExposure?.BindingId ?? 0);
            return false;
        }

        // Framebuffer-only textures may still only be a generated GL name here.
        // Bind once to force the driver to materialize the texture object and allocate
        // immutable storage before we use it as an image.
        IGLTexture? previousBoundTexture = BoundTexture;
        glExposure.Bind();
        if (previousBoundTexture is null || ReferenceEquals(previousBoundTexture, glExposure))
            glExposure.Unbind();
        else
            previousBoundTexture.Bind();

        uint exposureBindingId = glExposure.BindingId;
        if (exposureBindingId == GLObjectBase.InvalidBindingId || !Api.IsTexture(exposureBindingId))
        {
            string exposureTextureName = exposureTex.Name ?? "<unnamed>";

            Debug.OpenGLWarningEvery(
                $"AutoExposure.InvalidExposure.{exposureTextureName}",
                TimeSpan.FromSeconds(1),
                "[AutoExposure] Exposure texture is not ready for image load/store. Texture='{0}', BindingId={1}.",
                exposureTextureName,
                exposureBindingId);
            return false;
        }

        int smallestMip;
        GLRenderProgram? glProgram;

        uint bindTarget;
        uint sourceBindingId;
        int layerCount = 1;

        if (sourceTex is XRTexture2D source2D)
        {
            var glSource = GetOrCreateAPIRenderObject(source2D, generateNow: true) as GLTexture2D;
            if (glSource is null || !glSource.TryGetBindingId(out sourceBindingId) || !Api.IsTexture(sourceBindingId))
            {
                _supportsGpuAutoExposure = false;
                Debug.OpenGLWarning($"[AutoExposure] Disabling GPU auto exposure because the source texture is invalid. Texture='{sourceTex.Name}', BindingId={glSource?.BindingId ?? 0}.");
                return false;
            }

            if (generateMipmapsNow)
                glSource.GenerateMipmaps();

            smallestMip = XRTexture.GetSmallestMipmapLevel(source2D.Width, source2D.Height, source2D.SmallestAllowedMipmapLevel);
            glProgram = GenericToAPI<GLRenderProgram>(_autoExposureComputeProgram2D);
            bindTarget = (uint)TextureTarget.Texture2D;
        }
        else if (sourceTex is XRTexture2DArray source2DArray)
        {
            var glSource = GetOrCreateAPIRenderObject(source2DArray, generateNow: true) as GLTexture2DArray;
            if (glSource is null || !glSource.TryGetBindingId(out sourceBindingId) || !Api.IsTexture(sourceBindingId))
            {
                _supportsGpuAutoExposure = false;
                Debug.OpenGLWarning($"[AutoExposure] Disabling GPU auto exposure because the array source texture is invalid. Texture='{sourceTex.Name}', BindingId={glSource?.BindingId ?? 0}.");
                return false;
            }

            if (generateMipmapsNow)
                glSource.GenerateMipmaps();

            smallestMip = XRTexture.GetSmallestMipmapLevel(source2DArray.Width, source2DArray.Height, source2DArray.SmallestAllowedMipmapLevel);
            layerCount = (int)source2DArray.Depth;
            glProgram = GenericToAPI<GLRenderProgram>(_autoExposureComputeProgram2DArray);
            bindTarget = (uint)TextureTarget.Texture2DArray;
        }
        else
        {
            return false;
        }

        if (glProgram is null)
        {
            Debug.OpenGLWarningEvery(
                $"AutoExposure.NullProgram.{sourceTex.GetType().Name}",
                TimeSpan.FromSeconds(1),
                "[AutoExposure] Failed to resolve GL compute program for source texture type '{0}'.",
                sourceTex.GetType().Name);
            return false;
        }

        // The compute program may not be linked yet because linkNow fires before
        // the GL wrapper exists (the event has no subscribers during the constructor).
        // Attempt a deferred link here, mirroring what UseRequested does.
        if (!glProgram.Use())
        {
            if (glProgram.IsAsyncBuildPending || !glProgram.LinkReady)
                return false;

            Debug.OpenGLWarningEvery(
                $"AutoExposure.ProgramNotReady.{glProgram.Hash}",
                TimeSpan.FromSeconds(1),
                "[AutoExposure] Compute program hash {0} is not ready yet. LinkReady={1}, IsLinked={2}.",
                glProgram.Hash,
                glProgram.LinkReady,
                glProgram.IsLinked);
            return false;
        }

        int meteringMip = smallestMip;
        if (settings.AutoExposureMetering != ColorGradingSettings.AutoExposureMeteringMode.Average)
        {
            int targetSize = Math.Clamp(settings.AutoExposureMeteringTargetSize, 1, 64);
            uint pow2 = 1u << BitOperations.Log2((uint)targetSize);
            int offset = BitOperations.Log2(pow2);
            meteringMip = Math.Clamp(smallestMip - offset, 0, smallestMip);
        }

        glProgram.Uniform("SmallestMip", smallestMip);
        glProgram.Uniform("LuminanceWeights", settings.AutoExposureLuminanceWeights);
        glProgram.Uniform("AutoExposureBias", settings.AutoExposureBias);
        glProgram.Uniform("AutoExposureScale", settings.AutoExposureScale);
        glProgram.Uniform("ExposureDividend", settings.ExposureDividend);
        settings.GetResolvedExposureBounds(out float minExposure, out float maxExposure);

        glProgram.Uniform("MinExposure", minExposure);
        glProgram.Uniform("MaxExposure", maxExposure);
        glProgram.Uniform("ExposureBase", settings.ExposureMode == ColorGradingSettings.ExposureControlMode.Physical
            ? settings.ComputePhysicalExposureMultiplier()
            : 1.0f);
        float fallbackExposure = settings.ExposureMode == ColorGradingSettings.ExposureControlMode.Physical
            ? settings.ComputePhysicalExposureMultiplier()
            : settings.Exposure;
        glProgram.Uniform("FallbackExposure", Math.Clamp(fallbackExposure, minExposure, maxExposure));

        glProgram.Uniform("MeteringMode", (int)settings.AutoExposureMetering);
        glProgram.Uniform("MeteringMip", meteringMip);
        glProgram.Uniform("IgnoreTopPercent", settings.AutoExposureIgnoreTopPercent);
        glProgram.Uniform("CenterWeightStrength", settings.AutoExposureCenterWeightStrength);
        glProgram.Uniform("CenterWeightPower", settings.AutoExposureCenterWeightPower);

        // Calculate time-based lerp factor
        // alpha = 1 - exp(-speed * dt)
        float alpha = 1.0f - MathF.Exp(-settings.ExposureTransitionSpeed * deltaTime);
        glProgram.Uniform("ExposureTransitionSpeed", alpha);

        if (sourceTex is XRTexture2DArray)
            glProgram.Uniform("LayerCount", layerCount);

        SetActiveTextureUnit(0);
        Api.BindTexture((TextureTarget)bindTarget, sourceBindingId);
        glProgram.Uniform("SourceTex", 0);

        // Bind exposure texture as an image for read/write
        Api.BindImageTexture(0, exposureBindingId, 0, false, 0, BufferAccessARB.ReadWrite, InternalFormat.R32f);

        Api.DispatchCompute(1, 1, 1);
        Api.MemoryBarrier((uint)(MemoryBarrierMask.ShaderImageAccessBarrierBit | MemoryBarrierMask.TextureFetchBarrierBit));

        // Ensure that the compute shader write is visible to subsequent reads (by the fragment shader or the next compute dispatch)
        Api.MemoryBarrier(MemoryBarrierMask.ShaderImageAccessBarrierBit | MemoryBarrierMask.TextureFetchBarrierBit);
        return true;
    }

    /// <summary>
    /// Calculates average luminance using a compute shader for parallel reduction.
    /// This is an alternative to the mipmap-based approach that can be more efficient for large textures.
    /// </summary>
    public unsafe override void CalcDotLuminanceFrontAsyncCompute(BoundingRectangle region, bool withTransparency, Vector3 luminance, Action<bool, float> callback)
    {
        using var prof = RuntimeEngine.Profiler.Start("GLRenderer.CalcDotLuminanceFrontAsyncCompute");

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

        EnsureLuminanceComputeResources();
        if (!_luminanceComputeInitialized || _luminanceComputeProgram is null)
        {
            // Fall back to mipmap method
            CalcDotLuminanceFrontAsync(region, withTransparency, luminance, callback);
            return;
        }

        // First, blit front buffer to texture (reuse existing cached texture)
        Api.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        Api.ReadBuffer(ReadBufferMode.Front);

        int mipLevels = 1; // No mipmaps needed for compute path
        if (_luminanceFrontTex == 0 || _luminanceFrontTexWidth != w || _luminanceFrontTexHeight != h)
        {
            if (_luminanceFrontTex != 0)
                Api.DeleteTexture(_luminanceFrontTex);
            if (_luminanceFrontFbo != 0)
                Api.DeleteFramebuffer(_luminanceFrontFbo);

            _luminanceFrontTex = Api.GenTexture();
            Api.BindTexture(TextureTarget.Texture2D, _luminanceFrontTex);
            Api.TexStorage2D(TextureTarget.Texture2D, (uint)mipLevels, GLEnum.Rgba8, w, h);

            _luminanceFrontFbo = Api.GenFramebuffer();
            Api.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _luminanceFrontFbo);
            Api.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _luminanceFrontTex, 0);

            _luminanceFrontTexWidth = w;
            _luminanceFrontTexHeight = h;
            _luminanceFrontMipLevels = mipLevels;
        }
        else
        {
            Api.BindTexture(TextureTarget.Texture2D, _luminanceFrontTex);
            Api.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _luminanceFrontFbo);
            Api.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _luminanceFrontTex, 0);
        }

        Api.BlitFramebuffer(
            region.X, region.Y, region.X + (int)w, region.Y + (int)h,
            0, 0, (int)w, (int)h,
            ClearBufferMask.ColorBufferBit,
            GLEnum.Linear);

        // Clear result buffer
        Vector4 zero = Vector4.Zero;
        Api.BindBuffer(GLEnum.ShaderStorageBuffer, _luminanceResultBuffer);
        Api.BufferSubData(GLEnum.ShaderStorageBuffer, IntPtr.Zero, 16, &zero);

        // Bind resources and dispatch compute
        var glProgram = GenericToAPI<GLRenderProgram>(_luminanceComputeProgram);
        if (glProgram is null || !glProgram.Use())
        {
            callback(false, 0.0f);
            return;
        }

        try
        {
            glProgram.Uniform("textureSize", new Data.Vectors.IVector2((int)w, (int)h));
            glProgram.Uniform("luminanceWeights", luminance);

            SetActiveTextureUnit(0);
            Api.BindTexture(TextureTarget.Texture2D, _luminanceFrontTex);
            glProgram.Uniform("inputTexture", 0);

            Api.BindBufferBase(GLEnum.ShaderStorageBuffer, 0, _luminanceResultBuffer);

            uint groupsX = (w + 15) / 16;
            uint groupsY = (h + 15) / 16;
            Api.DispatchCompute(groupsX, groupsY, 1);

            Api.MemoryBarrier((uint)MemoryBarrierMask.ShaderStorageBarrierBit);
        }
        finally
        {
            Api.BindBufferBase(GLEnum.ShaderStorageBuffer, 0, 0);
            Api.BindBuffer(GLEnum.ShaderStorageBuffer, 0);
        }

        // Async readback from result buffer
        IntPtr sync = Api.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);

        _lastFrontLuminanceRequestTicks = nowTicks;
        _pendingFrontLuminanceReadback = new PendingFrontLuminanceReadback
        {
            Callback = callback,
            Mode = FrontLuminanceReadbackMode.Compute,
            LuminanceWeights = luminance,
            Sync = sync,
            StartedTicks = nowTicks,
            LastPollTicks = nowTicks
        };
        RuntimeEngine.AddMainThreadCoroutine(PollPendingFrontLuminanceReadback, "GLRenderer.FrontLuminanceReadback");
    }
}
