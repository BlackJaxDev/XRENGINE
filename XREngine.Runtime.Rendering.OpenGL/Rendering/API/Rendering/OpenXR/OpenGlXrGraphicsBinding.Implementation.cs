using Silk.NET.OpenGL;
using Silk.NET.OpenXR;
using Silk.NET.OpenXR.Extensions.KHR;
using Silk.NET.Windowing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using XREngine;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Occlusion;
using XREngine.Scene.Transforms;
using Debug = XREngine.Debug;

namespace XREngine.Rendering.OpenGL;

using OpenXrEyeSwapchainExtent = OpenXRAPI.OpenXrEyeSwapchainExtent;

internal sealed unsafe partial class OpenGlXrGraphicsBinding
{
    /// <summary>
    /// Creates an OpenXR session using OpenGL graphics binding.
    /// </summary>
    /// <exception cref="Exception">Thrown when session creation fails.</exception>
    internal void CreateOpenGLSession(OpenGLRenderer renderer)
    {
        if (Window is null)
            throw new Exception("Window is null");

        _gl = renderer.RawGL;

        // OpenXR OpenGL session creation requires the HGLRC/HDC to be current on the calling thread.
        // This method is expected to run on the window render thread (see deferred init in Initialize()).
        var w = Window.Window;

        // IMPORTANT: Do not blindly force a context switch here.
        // The windowing layer is expected to already have the correct render context current when
        // invoking the render callback. Forcing MakeCurrent can switch to a different (non-sharing)
        // WGL context, which makes engine-owned textures/shaders invalid on this thread and can
        // cascade into incomplete FBOs and black output.
        nint preHdcCurrent = wglGetCurrentDC();
        nint preHglrcCurrent = wglGetCurrentContext();
        if (preHdcCurrent == 0 || preHglrcCurrent == 0)
        {
            try
            {
                w.MakeCurrent();
            }
            catch (Exception ex)
            {
                Debug.Out($"OpenGL MakeCurrent failed (continuing): {ex.Message}");
            }
        }

        try
        {
            string glVersion = new((sbyte*)_gl.GetString(StringName.Version));
            string glVendor = new((sbyte*)_gl.GetString(StringName.Vendor));
            string glRenderer = new((sbyte*)_gl.GetString(StringName.Renderer));
            Debug.Out($"OpenGL context: {glVendor} / {glRenderer} / {glVersion}");
        }
        catch
        {
            // If the context isn't current/valid, querying strings can throw; the CreateSession call will fail anyway.
        }

        var requirements = new GraphicsRequirementsOpenGLKHR
        {
            Type = StructureType.GraphicsRequirementsOpenglKhr
        };

        if (!Api.TryGetInstanceExtension<KhrOpenglEnable>("", _instance, out var openglExtension))
            throw new Exception("Failed to get OpenGL extension");

        if (openglExtension.GetOpenGlgraphicsRequirements(_instance, _systemId, ref requirements) != Result.Success)
            throw new Exception("Failed to get OpenGL graphics requirements");

        Debug.Out($"OpenGL requirements: Min {requirements.MinApiVersionSupported}, Max {requirements.MaxApiVersionSupported}");
        _ = TryResolveOpenXrFoveation(ERenderLibrary.OpenGL, out _);

        int glMajor = 0;
        int glMinor = 0;
        try
        {
            glMajor = _gl.GetInteger(GetPName.MajorVersion);
            glMinor = _gl.GetInteger(GetPName.MinorVersion);
        }
        catch
        {
            // Ignore; we'll still try to create the session and report handles.
        }

        nint hdcFromWindow = w.Native?.Win32?.HDC ?? 0;
        nint hglrcFromWindow = w.GLContext?.Handle ?? 0;
        nint hdcCurrent = wglGetCurrentDC();
        nint hglrcCurrent = wglGetCurrentContext();

        Debug.Out($"OpenGL binding (window): HDC=0x{(nuint)hdcFromWindow:X}, HGLRC=0x{(nuint)hglrcFromWindow:X}");
        Debug.Out($"OpenGL binding (current): HDC=0x{(nuint)hdcCurrent:X}, HGLRC=0x{(nuint)hglrcCurrent:X}");

        if (preHglrcCurrent != 0 && hglrcCurrent != 0 && preHglrcCurrent != hglrcCurrent)
        {
            Debug.Out(
                $"OpenGL context changed during OpenXR session init. " +
                $"Before(HDC=0x{(nuint)preHdcCurrent:X}, HGLRC=0x{(nuint)preHglrcCurrent:X}) " +
                $"After(HDC=0x{(nuint)hdcCurrent:X}, HGLRC=0x{(nuint)hglrcCurrent:X}).");
        }

        if ((hglrcCurrent == 0 || hdcCurrent == 0) && (hglrcFromWindow == 0 || hdcFromWindow == 0))
            throw new Exception("Cannot create OpenXR session: no valid OpenGL handles available (both current and window handles are null). Ensure OpenXR OpenGL session creation runs on the window render thread and the GL context is created.");

        // Some runtimes are picky about which exact handles they accept. We'll attempt session creation using both
        // the current WGL handles and the window-reported handles (if different), and report both results.
        (nint hdc, nint hglrc, string tag)[] candidates =
        [
            (hdcCurrent, hglrcCurrent, "current"),
            (hdcFromWindow, hglrcFromWindow, "window"),
        ];

        var attemptResults = new List<string>(2);
        Result lastResult = Result.Success;
        nint selectedHdc = 0;
        nint selectedHglrc = 0;
        string selectedTag = string.Empty;

        // Validate GL version against runtime requirements if we can decode versions.
        try
        {
            static (ushort major, ushort minor, uint patch) DecodeVersion(ulong v)
            {
                ulong raw = v;
                ushort major = (ushort)((raw >> 48) & 0xFFFF);
                ushort minor = (ushort)((raw >> 32) & 0xFFFF);
                uint patch = (uint)(raw & 0xFFFFFFFF);
                return (major, minor, patch);
            }

            var (minMajor, minMinor, _) = DecodeVersion(requirements.MinApiVersionSupported);
            var (maxMajor, maxMinor, _) = DecodeVersion(requirements.MaxApiVersionSupported);

            bool hasGLVersion = glMajor > 0;
            bool hasMax = maxMajor != 0 || maxMinor != 0;

            if (hasGLVersion)
            {
                bool belowMin = glMajor < minMajor || (glMajor == minMajor && glMinor < minMinor);
                bool aboveMax = hasMax && (glMajor > maxMajor || (glMajor == maxMajor && glMinor > maxMinor));
                if (belowMin || aboveMax)
                {
                    throw new Exception(
                        $"Cannot create OpenXR session: current OpenGL version {glMajor}.{glMinor} is outside runtime requirements " +
                        $"[{minMajor}.{minMinor} .. {(hasMax ? $"{maxMajor}.{maxMinor}" : "(no max)")}].");
                }
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"OpenXR OpenGL preflight failed: {ex.Message}");
        }

        foreach (var (candidateHdc, candidateHglrc, tag) in candidates)
        {
            if (candidateHdc == 0 || candidateHglrc == 0)
                continue;

            // Skip duplicate handle pairs.
            if (selectedHdc == candidateHdc && selectedHglrc == candidateHglrc)
                continue;

            _session = default;

            var glBinding = new GraphicsBindingOpenGLWin32KHR
            {
                Type = StructureType.GraphicsBindingOpenglWin32Khr,
                HDC = candidateHdc,
                HGlrc = candidateHglrc
            };
            var createInfo = new SessionCreateInfo
            {
                Type = StructureType.SessionCreateInfo,
                SystemId = _systemId,
                Next = &glBinding
            };

            var r = CheckResult(Api.CreateSession(_instance, ref createInfo, ref _session), "xrCreateSession");
            attemptResults.Add($"{tag}: {r} (HDC=0x{(nuint)candidateHdc:X}, HGLRC=0x{(nuint)candidateHglrc:X})");
            lastResult = r;
            if (r == Result.Success)
            {
                selectedHdc = candidateHdc;
                selectedHglrc = candidateHglrc;
                selectedTag = tag;
                break;
            }
        }

        if (_session.Handle == 0)
        {
            string activeRuntime = TryGetOpenXRActiveRuntime() ?? "<unknown>";
            throw new Exception(
                $"Failed to create OpenXR session: {lastResult}. GL={glMajor}.{glMinor}. ActiveRuntime={activeRuntime}. " +
                $"Attempts: {string.Join("; ", attemptResults)}. " +
                "SteamVR commonly has limited/fragile OpenGL OpenXR support; Vulkan is usually more reliable.");
        }

        _openXrSessionHdc = selectedHdc;
        _openXrSessionHglrc = selectedHglrc;
        _openXrSessionGlBindingTag = selectedTag;
        Debug.Out($"OpenXR session created using {selectedTag} OpenGL handles. HDC=0x{(nuint)selectedHdc:X}, HGLRC=0x{(nuint)selectedHglrc:X}");
    }

    /// <summary>
    /// Initializes OpenGL swapchains for stereo rendering.
    /// </summary>
    /// <param name="renderer">The OpenGL renderer to use.</param>
    /// <exception cref="Exception">Thrown when swapchain creation fails.</exception>
    internal unsafe void InitializeOpenGLSwapchains(OpenGLRenderer renderer)
    {
        if (_gl is null)
            throw new Exception("OpenGL context not initialized for OpenXR");

        // Query supported swapchain formats for the active OpenXR runtime (for OpenGL these are GL internal format enums).
        uint formatCount = 0;
        var formatResult = Api.EnumerateSwapchainFormats(_session, 0, ref formatCount, null);
        if (formatResult != Result.Success || formatCount == 0)
            throw new Exception($"Failed to enumerate OpenXR swapchain formats for OpenGL. Result={formatResult}, Count={formatCount}");

        var formats = new long[formatCount];
        fixed (long* formatsPtr = formats)
        {
            formatResult = Api.EnumerateSwapchainFormats(_session, formatCount, ref formatCount, formatsPtr);
        }
        if (formatResult != Result.Success || formatCount == 0)
            throw new Exception($"Failed to enumerate OpenXR swapchain formats for OpenGL. Result={formatResult}, Count={formatCount}");

        static IEnumerable<long> GetPreferredFormats(long[] available)
        {
            // Prefer sRGB when available, fall back to linear RGBA8.
            long[] preferred =
            [
                (long)GLEnum.Srgb8Alpha8,
                (long)GLEnum.Rgba8,
            ];

            foreach (var pref in preferred)
                if (available.Contains(pref))
                    yield return pref;

            foreach (var f in available)
                if (!preferred.Contains(f))
                    yield return f;
        }

        var supportedFormatsLog = string.Join(", ", formats.Select(f => $"0x{f:X}"));
        Debug.Out($"OpenXR OpenGL supported swapchain formats: {supportedFormatsLog}");

        InitializeOpenXrViewsForActiveConfiguration("OpenXR OpenGL");

        // Avoid stackalloc inside loops (analyzers treat that as a potential stack overflow).
        GLEnum* drawBuffers = stackalloc GLEnum[1];
        drawBuffers[0] = GLEnum.ColorAttachment0;

        // Create swapchains for each view
        for (int i = 0; i < _viewCount; i++)
        {
            OpenXrEyeSwapchainExtent extent = ResolveOpenXrEyeSwapchainExtent((uint)i);
            LogOpenXrEyeSwapchainExtent("OpenGL", (uint)i, extent);
            uint width = extent.Width;
            uint height = extent.Height;
            uint recommendedSamples = _viewConfigViews[i].RecommendedSwapchainSampleCount;

            Result lastResult = Result.Success;
            bool created = false;
            long createdFormat = 0;
            uint createdSamples = 0;

            foreach (var format in GetPreferredFormats(formats))
            {
                foreach (var usage in new[] { SwapchainUsageFlags.ColorAttachmentBit | SwapchainUsageFlags.SampledBit, SwapchainUsageFlags.ColorAttachmentBit })
                {
                    foreach (var samples in recommendedSamples > 1 ? [recommendedSamples, 1u] : new[] { 1u })
                    {
                        var swapchainCreateInfo = new SwapchainCreateInfo
                        {
                            Type = StructureType.SwapchainCreateInfo,
                            UsageFlags = usage,
                            Format = format,
                            SampleCount = samples,
                            Width = width,
                            Height = height,
                            FaceCount = 1,
                            ArraySize = 1,
                            MipCount = 1
                        };

                        fixed (Swapchain* swapchainPtr = &_swapchains[i])
                        {
                            lastResult = Api.CreateSwapchain(_session, in swapchainCreateInfo, swapchainPtr);
                        }

                        if (lastResult == Result.Success)
                        {
                            Debug.Out($"OpenXR swapchain[{i}] created. Format=0x{format:X}, Samples={samples}, Usage={usage}, Size={width}x{height}");
                            createdFormat = format;
                            createdSamples = samples;
                            RecordOpenXrSwapchainExtent((uint)i, width, height);
                            created = true;
                            break;
                        }
                    }

                    if (created)
                        break;
                }

                if (created)
                    break;
            }

            if (!created)
                throw new Exception($"Failed to create swapchain for view {i}. LastResult={lastResult}, RecommendedSamples={recommendedSamples}, Size={width}x{height}, SupportedFormats={supportedFormatsLog}");

            // Get swapchain images
            uint imageCount = 0;
            var enumerateResult = CheckResult(Api.EnumerateSwapchainImages(_swapchains[i], 0, &imageCount, null), "xrEnumerateSwapchainImages(OpenGL count)");
            if (enumerateResult != Result.Success || imageCount == 0)
                throw new Exception($"Failed to enumerate OpenXR OpenGL swapchain image count for view {i}. Result={enumerateResult}, Count={imageCount}");

            int imageCountInt = checked((int)imageCount);
            int imageBytes = checked(imageCountInt * sizeof(SwapchainImageOpenGLKHR));
            SwapchainImageOpenGLKHR* swapchainImages = (SwapchainImageOpenGLKHR*)Marshal.AllocHGlobal(imageBytes);

            var swapchainImageSpan = new Span<SwapchainImageOpenGLKHR>(swapchainImages, imageCountInt);
            swapchainImageSpan.Clear();
            for (int j = 0; j < imageCountInt; j++)
                swapchainImages[j].Type = StructureType.SwapchainImageOpenglKhr;

            enumerateResult = CheckResult(Api.EnumerateSwapchainImages(_swapchains[i], imageCount, &imageCount, (SwapchainImageBaseHeader*)swapchainImages), "xrEnumerateSwapchainImages(OpenGL images)");
            if (enumerateResult != Result.Success || imageCount == 0 || imageCount > (uint)imageCountInt)
            {
                Marshal.FreeHGlobal((nint)swapchainImages);
                throw new Exception($"Failed to enumerate OpenXR OpenGL swapchain images for view {i}. Result={enumerateResult}, Count={imageCount}, Capacity={imageCountInt}");
            }

            imageCountInt = checked((int)imageCount);
            _swapchainImagesGL[i] = swapchainImages;
            _swapchainImageCounts[i] = imageCount;
            uint[] framebuffers = new uint[imageCountInt];
            _swapchainFramebuffers[i] = framebuffers;
            RecordSmokeSwapchain("OpenGL", i, width, height, createdFormat, createdSamples, imageCount);

            for (int j = 0; j < imageCountInt; j++)
            {
                uint image = swapchainImages[j].Image;
                if (image == 0)
                    throw new Exception($"OpenXR OpenGL swapchain view {i} image {j} has a zero GL texture handle.");

                if (!_gl.IsTexture(image))
                    throw new Exception($"OpenXR OpenGL swapchain view {i} image {j} returned GL texture {image}, but it is not valid in the current context.");

                uint fbo = _gl.GenFramebuffer();
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
                // Attach without assuming the underlying texture target (2D vs 2DMS etc).
                _gl.FramebufferTexture(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, image, 0);

                // Make the swapchain FBO robust against global ReadBuffer/DrawBuffers state changes.
                // Some engine passes intentionally set ReadBuffer=None; if that leaks, subsequent operations can become no-ops.
                _gl.NamedFramebufferDrawBuffers(fbo, 1, drawBuffers);
                _gl.NamedFramebufferReadBuffer(fbo, GLEnum.ColorAttachment0);

                var framebufferStatus = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
                if (framebufferStatus != GLEnum.FramebufferComplete)
                    throw new Exception($"OpenXR OpenGL swapchain view {i} image {j} framebuffer is incomplete: {framebufferStatus}.");

                framebuffers[j] = fbo;
            }

            Console.WriteLine($"Created swapchain {i} with {imageCount} images ({width}x{height})");
        }
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        RecordSmokeSwapchainsCreated();
    }

    private void RenderViewportsToSwapchain(uint textureHandle, uint viewIndex)
    {
        if (Window is null)
            return;

        if (_openXrFrameWorld is null)
            return;

        if (_openXrLeftViewport is null || _openXrRightViewport is null)
            return;

        if (_openXrLeftEyeCamera is null || _openXrRightEyeCamera is null)
            return;

        if (Window.Renderer is OpenGLRenderer renderer)
        {
            if (_gl is null)
                return;

            int frameNo = _openXrPendingFrameNumber;
            bool logLifecycle = OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo);

            // Diagnostic: prove swapchain rendering/submission works (and swapchain texture names are valid in this context).
            // If this shows solid colors in the HMD, the issue is in mirror rendering or blit source, not OpenXR submission.
            if (OpenXrDebugClearOnly)
            {
                if (IsLeftEyeLikeOpenXrView(viewIndex))
                    _gl.ClearColor(1f, 0f, 0f, 1f);
                else
                    _gl.ClearColor(0f, 1f, 0f, 1f);
                _gl.Clear(ClearBufferMask.ColorBufferBit);
                return;
            }

            uint width = GetOpenXrSwapchainWidth(viewIndex);
            uint height = GetOpenXrSwapchainHeight(viewIndex);
            EnsureViewportMirrorTargets(renderer, width, height);
            EnsureOpenXrPreviewTargets(renderer, width, height);

            var eyeViewport = GetOpenXrEyeViewport(viewIndex);
            var eyeCamera = GetOpenXrEyeCamera(viewIndex);
            if (eyeViewport is null || eyeCamera is null || _openXrFrameWorld is null)
            {
                Debug.RenderingWarningEvery(
                    $"OpenXR.OpenGL.RenderEye.NoVrRig.{viewIndex}",
                    TimeSpan.FromSeconds(1),
                    "[OpenXR] Skipping OpenGL eye render for view {0}: viewport={1}, camera={2}, world={3}. No fallback eye rendering is enabled.",
                    viewIndex,
                    eyeViewport is not null,
                    eyeCamera is not null,
                    _openXrFrameWorld is not null);
                return;
            }

            EnsureOpenXrViewportExtent(eyeViewport, width, height);

            // IMPORTANT: the render pipeline (and GLMaterial lighting uniforms) derive RenderingWorld from
            // RenderState.WindowViewport.World. When rendering OpenXR eyes, we often pass a worldOverride
            // but the eye viewport itself may not be associated with a scene node/world.
            // If WorldInstanceOverride isn't set, RenderingWorld becomes null and forward lighting is skipped
            // (meshes appear black while skybox can still render).
            eyeViewport.WorldInstanceOverride = _openXrFrameWorld;

            var previous = AbstractRenderer.Current;
            bool previousRendererActive = renderer.Active;
            try
            {
                renderer.Active = true;
                AbstractRenderer.Current = renderer;
                OcclusionGpuElapsedTiming.Instance.Resolve(renderer, RuntimeEngine.Rendering.State.RenderFrameId);

                // Make sure the eye pose reflects the latest locomotion-root rotation for *this* render.
                ApplyOpenXrEyePoseForRenderThread(viewIndex);

                using (renderer.EnterOpenXrExternalSwapchainRenderScope(width, height))
                {
                    // CollectVisible/SwapBuffers are handled on the engine's CollectVisible thread.
                    eyeViewport.Render(_viewportMirrorFbo, _openXrFrameWorld, eyeCamera, shadowPass: false, forcedMaterial: null);
                }

                var srcApiTex = TryGetValidOpenXrTexture(renderer, _viewportMirrorColor, "mirror color", viewIndex);
                if (srcApiTex is null || srcApiTex.BindingId == 0)
                    return;

                XRTexture2D? previewTexture = GetOpenXrPreviewTexture(viewIndex);
                var previewApiTex = previewTexture is null
                    ? null
                    : TryGetValidOpenXrTexture(renderer, previewTexture, "preview", viewIndex);

                if (logLifecycle)
                {
                    bool srcIsTex = _gl.IsTexture(srcApiTex.BindingId);
                    bool dstIsTex = textureHandle != 0 && _gl.IsTexture(textureHandle);
                    Debug.Out($"OpenXR[{frameNo}] GLBlit: view={viewIndex} srcTex={srcApiTex.BindingId} valid={srcIsTex} dstTex={textureHandle} valid={dstIsTex} dstFbo={_openXrCurrentSwapchainFramebuffer} size={width}x{height}");
                }

                if (OpenXrDebugGl)
                {
                    bool srcIsTex = _gl.IsTexture(srcApiTex.BindingId);
                    bool dstIsTex = _gl.IsTexture(textureHandle);
                    int dbg = Interlocked.Increment(ref _openXrDebugFrameIndex);
                    if (dbg == 1 || (dbg % OpenXrDebugLogEveryNFrames) == 0)
                    {
                        Debug.Out($"OpenXR GL: view={viewIndex} srcTex={srcApiTex.BindingId} valid={srcIsTex} dstTex={textureHandle} valid={dstIsTex}");
                    }
                }

                // These utility FBOs must be created in (and used with) the current GL context.
                // Some runtimes/drivers use a distinct context for OpenXR rendering; reusing cached FBO ids from a
                // different context will trigger GL_INVALID_OPERATION and result in black output.
                var hglrcCurrent = wglGetCurrentContext();
                if (_blitFboHglrc != 0 && _blitFboHglrc != hglrcCurrent)
                {
                    _blitReadFbo = 0;
                    _blitDrawFbo = 0;
                }
                _blitFboHglrc = hglrcCurrent;

                if (_blitReadFbo == 0)
                    _blitReadFbo = _gl.GenFramebuffer();
                if (_blitDrawFbo == 0)
                    _blitDrawFbo = _gl.GenFramebuffer();

                // Blit can be clipped by scissor/masks if left enabled by previous passes.
                _gl.Disable(EnableCap.ScissorTest);
                _gl.ColorMask(true, true, true, true);

                _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _blitReadFbo);
                // Some engine passes intentionally set ReadBuffer=None; if that leaks, blits can become no-ops.
                _gl.ReadBuffer(GLEnum.ColorAttachment0);
                // Attach without assuming the underlying texture target (2D vs 2DMS etc).
                _gl.FramebufferTexture(FramebufferTarget.ReadFramebuffer, FramebufferAttachment.ColorAttachment0, srcApiTex.BindingId, 0);

                _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _blitDrawFbo);
                unsafe
                {
                    GLEnum* drawBuffers = stackalloc GLEnum[1] { GLEnum.ColorAttachment0 };
                    _gl.DrawBuffers(1, drawBuffers);
                }
                // Attach without assuming the underlying texture target (2D vs 2DMS etc).
                uint previewTextureId = previewApiTex?.BindingId ?? 0;
                bool previewTextureValid = previewTextureId != 0 && _gl.IsTexture(previewTextureId);
                if (previewTextureValid)
                {
                    _gl.FramebufferTexture(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0, previewTextureId, 0);
                    var previewDrawStatus = _gl.CheckFramebufferStatus(FramebufferTarget.DrawFramebuffer);
                    if (previewDrawStatus == GLEnum.FramebufferComplete)
                    {
                        _gl.BlitFramebuffer(
                            0, 0, (int)width, (int)height,
                            0, 0, (int)width, (int)height,
                            ClearBufferMask.ColorBufferBit,
                            BlitFramebufferFilter.Linear);
                    }
                    else
                    {
                        Debug.OpenGLWarningEvery(
                            $"OpenXR.OpenGL.InvalidPreviewFramebuffer.{viewIndex}",
                            TimeSpan.FromSeconds(1),
                            "[OpenXR] Skipping eye preview blit for view {0}: preview FBO status={1}, texture={2}.",
                            viewIndex,
                            previewDrawStatus,
                            previewTextureId);
                    }
                }
                else if (previewTextureId != 0)
                {
                    Debug.OpenGLWarningEvery(
                        $"OpenXR.OpenGL.InvalidPreviewTexture.{viewIndex}",
                        TimeSpan.FromSeconds(1),
                        "[OpenXR] Skipping eye preview blit for view {0}: texture name {1} is not valid in the current GL context.",
                        viewIndex,
                        previewTextureId);
                }

                uint destinationFramebuffer = _openXrCurrentSwapchainFramebuffer;
                if (destinationFramebuffer == 0)
                {
                    Debug.RenderingWarningEvery(
                        $"OpenXR.OpenGL.NoCurrentSwapchainFramebuffer.{GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "[OpenXR] OpenGL eye blit skipped because no acquired swapchain framebuffer is active for view {0}.",
                        viewIndex);
                    return;
                }

                _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, destinationFramebuffer);
                unsafe
                {
                    GLEnum* drawBuffers = stackalloc GLEnum[1] { GLEnum.ColorAttachment0 };
                    _gl.DrawBuffers(1, drawBuffers);
                }

                if (OpenXrDebugGl)
                {
                    var readStatus = _gl.CheckFramebufferStatus(FramebufferTarget.ReadFramebuffer);
                    var drawStatus = _gl.CheckFramebufferStatus(FramebufferTarget.DrawFramebuffer);
                    var err = _gl.GetError();
                    int dbg = Interlocked.Increment(ref _openXrDebugFrameIndex);
                    if (dbg == 1 || (dbg % OpenXrDebugLogEveryNFrames) == 0)
                    {
                        Debug.Out($"OpenXR GL: FBO status read={readStatus} draw={drawStatus} glGetError={err}");
                    }
                }

                if (logLifecycle)
                {
                    var readStatus = _gl.CheckFramebufferStatus(FramebufferTarget.ReadFramebuffer);
                    var drawStatus = _gl.CheckFramebufferStatus(FramebufferTarget.DrawFramebuffer);
                    var err = _gl.GetError();
                    Debug.Out($"OpenXR[{frameNo}] GLBlit: view={viewIndex} FBO read={readStatus} draw={drawStatus} glErr={err}");
                }

                _gl.BlitFramebuffer(
                    0, 0, (int)width, (int)height,
                    0, 0, (int)width, (int)height,
                    ClearBufferMask.ColorBufferBit,
                    BlitFramebufferFilter.Linear);

                if (logLifecycle)
                {
                    var err = _gl.GetError();
                    Debug.Out($"OpenXR[{frameNo}] GLBlit: view={viewIndex} post-blit glErr={err}");
                }

                if (OpenXrDebugGl)
                {
                    var err = _gl.GetError();
                    int dbg = Interlocked.Increment(ref _openXrDebugFrameIndex);
                    if (dbg == 1 || (dbg % OpenXrDebugLogEveryNFrames) == 0)
                    {
                        Debug.Out($"OpenXR GL: post-blit glGetError={err}");
                    }
                }
            }
            finally
            {
                renderer.Active = previousRendererActive;
                AbstractRenderer.Current = previous;
            }
        }
    }

    private bool TryRenderDesktopMirrorComposition(uint targetWidth, uint targetHeight)
    {
        if (_gl is null || Window?.Renderer is not OpenGLRenderer renderer)
            return false;
        if (_viewportMirrorColor is null)
            return false;

        IGLTexture? sourceTexture =
            renderer.GetOrCreateAPIRenderObject(_viewportMirrorColor, generateNow: true) as IGLTexture;
        if (sourceTexture is null || sourceTexture.BindingId == 0)
            return false;

        if (_blitReadFbo == 0)
            _blitReadFbo = _gl.GenFramebuffer();

        int previousReadFramebuffer = 0;
        int previousDrawFramebuffer = 0;
        int previousReadBuffer = 0;
        bool previousScissorEnabled = false;
        bool capturedState = false;
        try
        {
            previousReadFramebuffer = _gl.GetInteger(GetPName.ReadFramebufferBinding);
            previousDrawFramebuffer = _gl.GetInteger(GetPName.DrawFramebufferBinding);
            previousReadBuffer = _gl.GetInteger(GetPName.ReadBuffer);
            previousScissorEnabled = _gl.IsEnabled(EnableCap.ScissorTest);
            capturedState = true;
        }
        catch
        {
            // Continue with deterministic state and restore only when capture succeeded.
        }

        try
        {
            _gl.Disable(EnableCap.ScissorTest);
            _gl.ColorMask(true, true, true, true);
            _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _blitReadFbo);
            _gl.FramebufferTexture(
                FramebufferTarget.ReadFramebuffer,
                FramebufferAttachment.ColorAttachment0,
                sourceTexture.BindingId,
                0);
            _gl.ReadBuffer(GLEnum.ColorAttachment0);
            _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
            _gl.BlitFramebuffer(
                0,
                0,
                (int)_viewportMirrorWidth,
                (int)_viewportMirrorHeight,
                0,
                0,
                (int)Math.Max(1u, targetWidth),
                (int)Math.Max(1u, targetHeight),
                ClearBufferMask.ColorBufferBit,
                BlitFramebufferFilter.Linear);
            RecordSmokeDesktopMirrorComposed();
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (capturedState)
            {
                try
                {
                    if (previousScissorEnabled)
                        _gl.Enable(EnableCap.ScissorTest);
                    else
                        _gl.Disable(EnableCap.ScissorTest);
                    _gl.BindFramebuffer(
                        FramebufferTarget.ReadFramebuffer,
                        (uint)previousReadFramebuffer);
                    _gl.BindFramebuffer(
                        FramebufferTarget.DrawFramebuffer,
                        (uint)previousDrawFramebuffer);
                    _gl.ReadBuffer((GLEnum)previousReadBuffer);
                }
                catch
                {
                    // Best-effort state restoration.
                }
            }
        }
    }

    private void EnsureViewportMirrorTargets(AbstractRenderer renderer, uint width, uint height)
    {
        width = Math.Max(1u, width);
        height = Math.Max(1u, height);

        if (_viewportMirrorFbo is not null && _viewportMirrorWidth == width && _viewportMirrorHeight == height)
            return;

        try
        {
            _viewportMirrorFbo?.Destroy();
            _viewportMirrorFbo = null;
            _viewportMirrorDepth?.Destroy();
            _viewportMirrorDepth = null;
            _viewportMirrorColor?.Destroy();
            _viewportMirrorColor = null;
        }
        catch
        {
            // Best-effort cleanup.
        }

        _viewportMirrorWidth = width;
        _viewportMirrorHeight = height;

        _viewportMirrorColor = XRTexture2D.CreateFrameBufferTexture(
            width,
            height,
            EPixelInternalFormat.Rgba8,
            EPixelFormat.Rgba,
            EPixelType.UnsignedByte,
            EFrameBufferAttachment.ColorAttachment0);
        _viewportMirrorColor.Resizable = true;
        _viewportMirrorColor.MinFilter = ETexMinFilter.Linear;
        _viewportMirrorColor.MagFilter = ETexMagFilter.Linear;
        _viewportMirrorColor.UWrap = ETexWrapMode.ClampToEdge;
        _viewportMirrorColor.VWrap = ETexWrapMode.ClampToEdge;
        _viewportMirrorColor.Name = "OpenXRViewportMirrorColor";

        _viewportMirrorDepth = new XRRenderBuffer(width, height, ERenderBufferStorage.Depth24Stencil8, EFrameBufferAttachment.DepthStencilAttachment)
        {
            Name = "OpenXRViewportMirrorDepth"
        };

        _viewportMirrorFbo = new XRFrameBuffer(
            (_viewportMirrorColor, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (_viewportMirrorDepth, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
        {
            Name = "OpenXRViewportMirrorFBO"
        };

        // Ensure GPU objects are created on this renderer/context.
        renderer.GetOrCreateAPIRenderObject(_viewportMirrorColor, generateNow: true);
        renderer.GetOrCreateAPIRenderObject(_viewportMirrorDepth, generateNow: true);
        renderer.GetOrCreateAPIRenderObject(_viewportMirrorFbo, generateNow: true);
    }

    private void EnsureOpenXrPreviewTargets(AbstractRenderer renderer, uint width, uint height)
        => EnsureOpenXrPreviewTargets(
            renderer,
            width,
            height,
            EPixelInternalFormat.Rgba8,
            ESizedInternalFormat.Rgba8);

    private void EnsureOpenXrPreviewTargets(
        AbstractRenderer renderer,
        uint width,
        uint height,
        EPixelInternalFormat internalFormat,
        ESizedInternalFormat sizedFormat)
    {
        width = Math.Max(1u, width);
        height = Math.Max(1u, height);

        if (_previewLeftEyeTexture is not null &&
            _previewRightEyeTexture is not null &&
            _previewEyeTextureWidth == width &&
            _previewEyeTextureHeight == height &&
            _previewEyeTextureInternalFormat == internalFormat &&
            _previewEyeTextureSizedFormat == sizedFormat)
        {
            return;
        }

        DestroyOpenXrPreviewTargets();

        _previewEyeTextureWidth = width;
        _previewEyeTextureHeight = height;
        _previewEyeTextureInternalFormat = internalFormat;
        _previewEyeTextureSizedFormat = sizedFormat;
        _previewLeftEyeTexture = CreateOpenXrPreviewTexture(width, height, internalFormat, sizedFormat, "OpenXRPreviewLeftEyeColor");
        _previewRightEyeTexture = CreateOpenXrPreviewTexture(width, height, internalFormat, sizedFormat, "OpenXRPreviewRightEyeColor");

        renderer.GetOrCreateAPIRenderObject(_previewLeftEyeTexture, generateNow: true);
        renderer.GetOrCreateAPIRenderObject(_previewRightEyeTexture, generateNow: true);
    }

    private static XRTexture2D CreateOpenXrPreviewTexture(
        uint width,
        uint height,
        EPixelInternalFormat internalFormat,
        ESizedInternalFormat sizedFormat,
        string name)
    {
        var texture = XRTexture2D.CreateFrameBufferTexture(
            width,
            height,
            internalFormat,
            EPixelFormat.Rgba,
            EPixelType.UnsignedByte,
            EFrameBufferAttachment.ColorAttachment0);
        texture.SizedInternalFormat = sizedFormat;
        texture.Resizable = true;
        texture.MinFilter = ETexMinFilter.Linear;
        texture.MagFilter = ETexMagFilter.Linear;
        texture.UWrap = ETexWrapMode.ClampToEdge;
        texture.VWrap = ETexWrapMode.ClampToEdge;
        texture.Name = name;
        return texture;
    }

    private IGLTexture? TryGetValidOpenXrTexture(OpenGLRenderer renderer, XRTexture2D? texture, string label, uint viewIndex)
    {
        if (texture is null || _gl is null)
            return null;

        var apiTexture = renderer.GetOrCreateAPIRenderObject(texture, generateNow: true) as IGLTexture;
        if (apiTexture is null)
            return null;

        uint textureId = apiTexture.BindingId;
        if (textureId != 0 && _gl.IsTexture(textureId))
            return apiTexture;

        if (apiTexture is AbstractRenderAPIObject apiObject)
        {
            Debug.OpenGLWarningEvery(
                $"OpenXR.OpenGL.RegenerateInvalidTexture.{label}.{viewIndex}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Regenerating {0} texture for view {1}: GL name {2} is not valid in the current context.",
                label,
                viewIndex,
                textureId);
            apiObject.Destroy();
            apiTexture = renderer.GetOrCreateAPIRenderObject(texture, generateNow: true) as IGLTexture;
            textureId = apiTexture?.BindingId ?? 0;
        }

        return textureId != 0 && _gl.IsTexture(textureId)
            ? apiTexture
            : null;
    }

    private void DestroyOpenXrPreviewTargets()
    {
        try
        {
            _previewLeftEyeTexture?.Destroy();
            _previewLeftEyeTexture = null;
            _previewRightEyeTexture?.Destroy();
            _previewRightEyeTexture = null;
        }
        catch
        {
            // Best-effort cleanup.
        }

        _previewEyeTextureWidth = 0;
        _previewEyeTextureHeight = 0;
        _previewEyeTextureInternalFormat = EPixelInternalFormat.Rgba8;
        _previewEyeTextureSizedFormat = ESizedInternalFormat.Rgba8;
    }
}
