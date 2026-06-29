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
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;
using XREngine.Scene.Transforms;
using Debug = XREngine.Debug;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    private const string VrEyeTransformFullName = "XREngine.Scene.Transforms.VREyeTransform";
    private static RuntimeTypeHandle _vrEyeTransformTypeHandle;
    private static bool _hasVrEyeTransformTypeHandle;

    /// <summary>
    /// Delegate for rendering to a framebuffer texture.
    /// </summary>
    /// <param name="textureHandle">OpenGL texture handle to render to.</param>
    /// <param name="viewIndex">Index of the view (eye) being rendered.</param>
    public delegate void DelRenderToFBO(uint textureHandle, uint viewIndex);

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

        // Get view configuration
        var viewConfigType = ViewConfigurationType.PrimaryStereo;
        _viewCount = 0;
        Api.EnumerateViewConfigurationView(_instance, _systemId, viewConfigType, 0, ref _viewCount, null);

        if (_viewCount != 2)
            throw new Exception($"Expected 2 views, got {_viewCount}");

        _views = new View[_viewCount];
        for (int i = 0; i < _views.Length; i++)
            _views[i].Type = StructureType.View;

        // OpenXR requires the input structs to have their Type set.
        for (int i = 0; i < _viewConfigViews.Length; i++)
            _viewConfigViews[i].Type = StructureType.ViewConfigurationView;

        fixed (ViewConfigurationView* viewConfigViewsPtr = _viewConfigViews)
        {
            Api.EnumerateViewConfigurationView(_instance, _systemId, viewConfigType, _viewCount, ref _viewCount, viewConfigViewsPtr);
        }

        for (int i = 0; i < _viewCount; i++)
        {
            uint rw = _viewConfigViews[i].RecommendedImageRectWidth;
            uint rh = _viewConfigViews[i].RecommendedImageRectHeight;
            Debug.Out($"OpenXR view[{i}] recommended size: {rw}x{rh}, samples={_viewConfigViews[i].RecommendedSwapchainSampleCount}");

            if (rw == 0 || rh == 0)
                throw new Exception($"OpenXR runtime reported an invalid recommended image rect size for view {i}: {rw}x{rh}. Cannot create swapchains.");
        }

        // Avoid stackalloc inside loops (analyzers treat that as a potential stack overflow).
        GLEnum* drawBuffers = stackalloc GLEnum[1];
        drawBuffers[0] = GLEnum.ColorAttachment0;

        // Create swapchains for each view
        for (int i = 0; i < _viewCount; i++)
        {
            uint width = (uint)_viewConfigViews[i].RecommendedImageRectWidth;
            uint height = (uint)_viewConfigViews[i].RecommendedImageRectHeight;
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

            int frameNo = Volatile.Read(ref _openXrPendingFrameNumber);
            bool logLifecycle = OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo);

            // Diagnostic: prove swapchain rendering/submission works (and swapchain texture names are valid in this context).
            // If this shows solid colors in the HMD, the issue is in mirror rendering or blit source, not OpenXR submission.
            if (OpenXrDebugClearOnly)
            {
                if (viewIndex == 0)
                    _gl.ClearColor(1f, 0f, 0f, 1f);
                else
                    _gl.ClearColor(0f, 1f, 0f, 1f);
                _gl.Clear(ClearBufferMask.ColorBufferBit);
                return;
            }

            uint width = _viewConfigViews[viewIndex].RecommendedImageRectWidth;
            uint height = _viewConfigViews[viewIndex].RecommendedImageRectHeight;
            EnsureViewportMirrorTargets(renderer, width, height);
            EnsureOpenXrPreviewTargets(renderer, width, height);

            var eyeViewport = viewIndex == 0 ? _openXrLeftViewport : _openXrRightViewport;
            var eyeCamera = viewIndex == 0 ? _openXrLeftEyeCamera : _openXrRightEyeCamera;
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

                XRTexture2D? previewTexture = viewIndex == 0 ? _previewLeftEyeTexture : _previewRightEyeTexture;
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

    private void EnsureOpenXrViewport(uint width, uint height)
    {
        // Kept for compatibility with older call sites; prefer per-eye viewports.
        EnsureOpenXrViewports(width, height);
    }

    internal bool TryRenderDesktopMirrorComposition(uint targetWidth, uint targetHeight)
    {
        if (Window?.Renderer is VulkanRenderer vulkanRenderer)
            return TryRenderVulkanDesktopMirrorComposition(vulkanRenderer, targetWidth, targetHeight);

        if (_gl is null || Window?.Renderer is not OpenGLRenderer renderer)
            return false;

        if (_viewportMirrorColor is null)
            return false;

        var srcApiTex = renderer.GetOrCreateAPIRenderObject(_viewportMirrorColor, generateNow: true) as IGLTexture;
        if (srcApiTex is null || srcApiTex.BindingId == 0)
            return false;

        if (_blitReadFbo == 0)
            _blitReadFbo = _gl.GenFramebuffer();

        int prevReadFbo = 0;
        int prevDrawFbo = 0;
        int prevReadBuffer = 0;
        bool prevScissorEnabled = false;
        bool captured = false;

        try
        {
            prevReadFbo = _gl.GetInteger(GetPName.ReadFramebufferBinding);
            prevDrawFbo = _gl.GetInteger(GetPName.DrawFramebufferBinding);
            prevReadBuffer = _gl.GetInteger(GetPName.ReadBuffer);
            prevScissorEnabled = _gl.IsEnabled(EnableCap.ScissorTest);
            captured = true;
        }
        catch
        {
            captured = false;
        }

        try
        {
            _gl.Disable(EnableCap.ScissorTest);
            _gl.ColorMask(true, true, true, true);

            _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _blitReadFbo);
            _gl.FramebufferTexture(FramebufferTarget.ReadFramebuffer, FramebufferAttachment.ColorAttachment0, srcApiTex.BindingId, 0);
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
            if (captured)
            {
                try
                {
                    if (prevScissorEnabled)
                        _gl.Enable(EnableCap.ScissorTest);
                    else
                        _gl.Disable(EnableCap.ScissorTest);

                    _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, (uint)prevReadFbo);
                    _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)prevDrawFbo);
                    _gl.ReadBuffer((GLEnum)prevReadBuffer);
                }
                catch
                {
                }
            }
        }
    }

    private bool TryRenderVulkanDesktopMirrorComposition(VulkanRenderer renderer, uint targetWidth, uint targetHeight)
    {
        if (_viewportMirrorFbo is null || _viewportMirrorColor is null)
            return false;

        uint resolvedTargetWidth = Math.Max(1u, targetWidth);
        uint resolvedTargetHeight = Math.Max(1u, targetHeight);
        if (_viewportMirrorWidth == 0 || _viewportMirrorHeight == 0)
            return false;

        try
        {
            renderer.GetOrCreateAPIRenderObject(_viewportMirrorColor, generateNow: true);
            if (_viewportMirrorDepth is not null)
                renderer.GetOrCreateAPIRenderObject(_viewportMirrorDepth, generateNow: true);
            renderer.GetOrCreateAPIRenderObject(_viewportMirrorFbo, generateNow: true);

            XRRenderPipelineInstance? mirrorPipeline =
                _openXrLeftViewport?.RenderPipelineInstance ??
                _openXrRightViewport?.RenderPipelineInstance;
            using var pipelineScope = RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(mirrorPipeline);
            using var passScope = RuntimeEngine.Rendering.State.PushRenderGraphPassIndex((int)EDefaultRenderPass.PostRender);
            renderer.Blit(
                _viewportMirrorFbo,
                null,
                0,
                0,
                _viewportMirrorWidth,
                _viewportMirrorHeight,
                0,
                0,
                resolvedTargetWidth,
                resolvedTargetHeight,
                EReadBufferMode.ColorAttachment0,
                colorBit: true,
                depthBit: false,
                stencilBit: false,
                linearFilter: true);
            renderer.TrackWindowPresentSource(_viewportMirrorColor, _viewportMirrorFbo);

            RecordSmokeDesktopMirrorComposed();
            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.DesktopMirrorCompositionFailed.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Vulkan desktop mirror composition failed: {0}",
                ex.Message);
            return false;
        }
    }

    private void EnsureOpenXrViewports(uint width, uint height)
    {
        _openXrLeftViewport ??= new XRViewport(Window)
        {
            AutomaticallyCollectVisible = false,
            AutomaticallySwapBuffers = false,
            AllowUIRender = false,
            SetRenderPipelineFromCamera = false,
            RendersToExternalSwapchainTarget = true
        };
        _openXrRightViewport ??= new XRViewport(Window)
        {
            AutomaticallyCollectVisible = false,
            AutomaticallySwapBuffers = false,
            AllowUIRender = false,
            SetRenderPipelineFromCamera = false,
            RendersToExternalSwapchainTarget = true
        };

        _openXrLeftViewport.Camera = _openXrLeftEyeCamera;
        _openXrRightViewport.Camera = _openXrRightEyeCamera;
        RuntimeEngine.VRState.LeftEyeViewport = _openXrLeftViewport;
        RuntimeEngine.VRState.RightEyeViewport = _openXrRightViewport;

        _openXrLeftViewport.CullWithFrustum = RuntimeEngine.Rendering.Settings.OpenXrCullWithFrustum;
        _openXrRightViewport.CullWithFrustum = RuntimeEngine.Rendering.Settings.OpenXrCullWithFrustum;

        // Keep them independent of editor viewport layout.
        _openXrLeftViewport.SetFullScreen();
        _openXrRightViewport.SetFullScreen();

        // Ensure pipeline sizes track our swapchain size, but keep internal resolution exact.
        if (_openXrLeftViewport.Width != (int)width || _openXrLeftViewport.Height != (int)height)
        {
            _openXrLeftViewport.Resize(width, height, setInternalResolution: false);
            _openXrLeftViewport.SetInternalResolution((int)width, (int)height, correctAspect: false);
        }
        else if (_openXrLeftViewport.InternalWidth != (int)width || _openXrLeftViewport.InternalHeight != (int)height)
        {
            _openXrLeftViewport.SetInternalResolution((int)width, (int)height, correctAspect: false);
        }

        if (_openXrRightViewport.Width != (int)width || _openXrRightViewport.Height != (int)height)
        {
            _openXrRightViewport.Resize(width, height, setInternalResolution: false);
            _openXrRightViewport.SetInternalResolution((int)width, (int)height, correctAspect: false);
        }
        else if (_openXrRightViewport.InternalWidth != (int)width || _openXrRightViewport.InternalHeight != (int)height)
        {
            _openXrRightViewport.SetInternalResolution((int)width, (int)height, correctAspect: false);
        }
    }

    private bool EnsureOpenXrEyeCameras(XRCamera baseCamera)
    {
        if (!TryResolveRequiredOpenXrVrRig(out XRCamera? leftEyeCamera, out XRCamera? rightEyeCamera, out _, out _, out string reason))
        {
            _openXrLeftEyeCamera = null;
            _openXrRightEyeCamera = null;
            Debug.RenderingWarningEvery(
                "OpenXR.EyeCameras.NoRequiredVrRig",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Eye cameras unavailable: {0}. No fallback eye cameras are created.",
                reason);
            return false;
        }

        XRCamera resolvedLeftEyeCamera = leftEyeCamera!;
        XRCamera resolvedRightEyeCamera = rightEyeCamera!;

        _openXrLeftEyeCamera = resolvedLeftEyeCamera;
        _openXrRightEyeCamera = resolvedRightEyeCamera;

        CopyCameraCommon(baseCamera, resolvedLeftEyeCamera);
        CopyCameraCommon(baseCamera, resolvedRightEyeCamera);
        return true;
    }

    private static void CopyCameraCommon(XRCamera src, XRCamera dst)
    {
        dst.CullingMask = src.CullingMask;
        dst.ShadowCollectMaxDistance = src.ShadowCollectMaxDistance;

        // Do NOT copy RenderPipeline from the desktop camera; OpenXR rendering owns its pipeline instance.

        float nearZ = src.Parameters.NearZ;
        float farZ = src.Parameters.FarZ;

        // If the app supplied a VR rig camera (e.g., VRHeadsetComponent's eye cameras), keep its parameter
        // type intact; it may be runtime-aware.
        if (dst.Parameters is XROVRCameraParameters vrParams)
        {
            vrParams.NearZ = nearZ;
            vrParams.FarZ = farZ;
            return;
        }

        if (dst.Parameters is not XROpenXRFovCameraParameters openxrParams)
        {
            openxrParams = new XROpenXRFovCameraParameters(nearZ, farZ);
            dst.Parameters = openxrParams;
        }
        else
        {
            openxrParams.NearZ = nearZ;
            openxrParams.FarZ = farZ;
        }
    }

    private float UpdateOpenXrEyeCameraFromView(XRCamera camera, uint viewIndex)
    {
        bool leftEye = viewIndex == 0;
        (float Left, float Right, float Up, float Down) fov;
        lock (_openXrPoseLock)
        {
            fov = leftEye ? _openXrPredLeftEyeFov : _openXrPredRightEyeFov;
        }

        if (!IsAppVrRigEyeTransform(camera.Transform))
        {
            Debug.RenderingWarningEvery(
                $"OpenXR.CollectPose.NonRigEye.{viewIndex}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Skipping predicted eye pose for view {0}: camera transform is not a VREyeTransform. No fallback pose path is used.",
                viewIndex);
            return 0.0f;
        }

        float paddingDegrees = 0.0f;
        if (OpenXrCollectPosePolicy == OpenXrCollectVisiblePosePolicy.PaddedFrustum)
        {
            paddingDegrees = MathF.Max(0.0f, OpenXrCollectFrustumPaddingDegrees);
            float paddingRadians = paddingDegrees * (MathF.PI / 180.0f);
            fov.Left -= paddingRadians;
            fov.Right += paddingRadians;
            fov.Down -= paddingRadians;
            fov.Up += paddingRadians;
        }

        if (camera.Parameters is XROpenXRFovCameraParameters openxrParams)
            openxrParams.SetAngles(fov.Left, fov.Right, fov.Up, fov.Down);

        return paddingDegrees;
    }

    private static bool IsAppVrRigEyeTransform(TransformBase transform)
    {
        Type transformType = transform.GetType();
        if (_hasVrEyeTransformTypeHandle)
            return transformType.TypeHandle.Equals(_vrEyeTransformTypeHandle);

        for (Type? type = transformType; type is not null; type = type.BaseType)
        {
            if (type.FullName != VrEyeTransformFullName)
                continue;

            _vrEyeTransformTypeHandle = transformType.TypeHandle;
            _hasVrEyeTransformTypeHandle = true;
            return true;
        }

        return false;
    }

    private static bool TryGetAppVrRigLocomotionRenderMatrix(XRCamera camera, out Matrix4x4 renderMatrix)
    {
        renderMatrix = Matrix4x4.Identity;

        var transform = camera.Transform;
        if (!IsAppVrRigEyeTransform(transform))
            return false;

        renderMatrix = transform.Parent?.ParentRenderMatrix ?? Matrix4x4.Identity;
        return true;
    }

    private bool TryResolveRequiredOpenXrVrRig(
        out XRCamera? leftEyeCamera,
        out XRCamera? rightEyeCamera,
        out IRuntimeRenderWorld? world,
        out TransformBase? locomotionRoot,
        out string reason)
    {
        var vrInfo = RuntimeEngine.VRState.ViewInformation;
        leftEyeCamera = vrInfo.LeftEyeCamera;
        rightEyeCamera = vrInfo.RightEyeCamera;
        world = vrInfo.World;
        locomotionRoot = vrInfo.HMDNode?.Transform.Parent;

        if (vrInfo.HMDNode is null)
        {
            reason = "VRState has no HMD node";
            return false;
        }

        if (leftEyeCamera is null || rightEyeCamera is null)
        {
            reason = $"VRState eye cameras are incomplete (left={leftEyeCamera is not null}, right={rightEyeCamera is not null})";
            return false;
        }

        if (world is null)
        {
            reason = "VRState has no render world";
            return false;
        }

        if (!IsAppVrRigEyeTransform(leftEyeCamera.Transform) || !IsAppVrRigEyeTransform(rightEyeCamera.Transform))
        {
            reason = $"VRState eye cameras are not scene-rig VREyeTransforms (left={leftEyeCamera.Transform.GetType().FullName}, right={rightEyeCamera.Transform.GetType().FullName})";
            return false;
        }

        if (!ReferenceEquals(leftEyeCamera.Transform.Parent, vrInfo.HMDNode.Transform)
            || !ReferenceEquals(rightEyeCamera.Transform.Parent, vrInfo.HMDNode.Transform))
        {
            reason = "VRState eye camera transforms are not parented directly to the HMD transform";
            return false;
        }

        LogResolvedOpenXrVrRig(vrInfo.HMDNode, leftEyeCamera, rightEyeCamera, world);

        reason = string.Empty;
        return true;
    }

    private static void LogResolvedOpenXrVrRig(
        XREngine.Scene.SceneNode hmdNode,
        XRCamera leftEyeCamera,
        XRCamera rightEyeCamera,
        IRuntimeRenderWorld world)
    {
        if (!VulkanCaptureEyeOutputs && !OpenXrDebugLifecycle)
            return;

        Debug.RenderingEvery(
            "OpenXR.Rig.Resolved",
            TimeSpan.FromSeconds(2),
            "[OpenXR] Resolved scene VR rig: hmd='{0}' hmdTransform={1} leftTransform={2} leftParentIsHmd={3} rightTransform={4} rightParentIsHmd={5} world=0x{6:X8}",
            hmdNode.Name ?? "<unnamed>",
            hmdNode.Transform.GetType().FullName ?? "<unknown>",
            leftEyeCamera.Transform.GetType().FullName ?? "<unknown>",
            ReferenceEquals(leftEyeCamera.Transform.Parent, hmdNode.Transform),
            rightEyeCamera.Transform.GetType().FullName ?? "<unknown>",
            ReferenceEquals(rightEyeCamera.Transform.Parent, hmdNode.Transform),
            world.GetHashCode());
    }

    private void ApplyOpenXrEyePoseForRenderThread(uint viewIndex)
    {
        var camera = viewIndex == 0 ? _openXrLeftEyeCamera : _openXrRightEyeCamera;
        if (camera is null)
            return;

        // Always update FOV from late cache (it can shift slightly during head movement).
        (float angleLeft, float angleRight, float angleUp, float angleDown) fov;
        lock (_openXrPoseLock)
            fov = viewIndex == 0 ? _openXrLateLeftEyeFov : _openXrLateRightEyeFov;

        if (camera.Parameters is XROpenXRFovCameraParameters openxrParams)
            openxrParams.SetAngles(fov.angleLeft, fov.angleRight, fov.angleUp, fov.angleDown);

        // If this camera is part of the app's VR rig (VREyeTransform), its transform hierarchy is
        // driven by InvokeRecalcMatrixOnDraw via VRHeadsetTransform. But we still need to update
        // the render matrix directly with the late pose to minimize latency.
        Matrix4x4 localPose;
        lock (_openXrPoseLock)
            localPose = viewIndex == 0 ? _openXrLateLeftEyeLocalPose : _openXrLateRightEyeLocalPose;

        if (!TryGetAppVrRigLocomotionRenderMatrix(camera, out Matrix4x4 rootRender))
        {
            Debug.RenderingWarningEvery(
                $"OpenXR.RenderPose.NonRigEye.{viewIndex}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Skipping late eye pose for view {0}: camera transform is not a VREyeTransform. No fallback root is used.",
                viewIndex);
            return;
        }

        Matrix4x4 eyeRender = localPose * rootRender;

        // Apply composed render matrix so rapid locomotion-root rotations can't temporarily snap the eye.
        camera.Transform.SetRenderMatrix(eyeRender, recalcAllChildRenderMatrices: false);

        if (VulkanCaptureEyeOutputs || OpenXrDebugLifecycle)
        {
            var hmdTransform = RuntimeEngine.VRState.ViewInformation.HMDNode?.Transform;
            Debug.RenderingEvery(
                $"OpenXR.RenderPose.RigEye.{viewIndex}",
                TimeSpan.FromSeconds(2),
                "[OpenXR] Applied late eye pose view {0}: transform={1} parentIsHmd={2} localTranslation=({3:F4},{4:F4},{5:F4}) rootTranslation=({6:F4},{7:F4},{8:F4}).",
                viewIndex,
                camera.Transform.GetType().FullName ?? "<unknown>",
                ReferenceEquals(camera.Transform.Parent, hmdTransform),
                localPose.M41,
                localPose.M42,
                localPose.M43,
                rootRender.M41,
                rootRender.M42,
                rootRender.M43);
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
    {
        width = Math.Max(1u, width);
        height = Math.Max(1u, height);

        if (_previewLeftEyeTexture is not null &&
            _previewRightEyeTexture is not null &&
            _previewEyeTextureWidth == width &&
            _previewEyeTextureHeight == height)
        {
            return;
        }

        DestroyOpenXrPreviewTargets();

        _previewEyeTextureWidth = width;
        _previewEyeTextureHeight = height;
        _previewLeftEyeTexture = CreateOpenXrPreviewTexture(width, height, "OpenXRPreviewLeftEyeColor");
        _previewRightEyeTexture = CreateOpenXrPreviewTexture(width, height, "OpenXRPreviewRightEyeColor");

        renderer.GetOrCreateAPIRenderObject(_previewLeftEyeTexture, generateNow: true);
        renderer.GetOrCreateAPIRenderObject(_previewRightEyeTexture, generateNow: true);
    }

    private static XRTexture2D CreateOpenXrPreviewTexture(uint width, uint height, string name)
    {
        var texture = XRTexture2D.CreateFrameBufferTexture(
            width,
            height,
            EPixelInternalFormat.Rgba8,
            EPixelFormat.Rgba,
            EPixelType.UnsignedByte,
            EFrameBufferAttachment.ColorAttachment0);
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
    }
}
