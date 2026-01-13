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
using XREngine.Scene.Transforms;
using Debug = XREngine.Debug;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
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
    private void CreateOpenGLSession(OpenGLRenderer renderer)
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

            var r = Api.CreateSession(_instance, ref createInfo, ref _session);
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
    private unsafe void InitializeOpenGLSwapchains(OpenGLRenderer renderer)
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
            Api.EnumerateSwapchainImages(_swapchains[i], 0, &imageCount, null);

            _swapchainImagesGL[i] = (SwapchainImageOpenGLKHR*)Marshal.AllocHGlobal((int)imageCount * sizeof(SwapchainImageOpenGLKHR));

            _swapchainImageCounts[i] = imageCount;
            _swapchainFramebuffers[i] = new uint[imageCount];

            for (uint j = 0; j < imageCount; j++)
                _swapchainImagesGL[i][j].Type = StructureType.SwapchainImageOpenglKhr;

            Api.EnumerateSwapchainImages(_swapchains[i], imageCount, &imageCount, (SwapchainImageBaseHeader*)_swapchainImagesGL[i]);

            for (uint j = 0; j < imageCount; j++)
            {
                uint fbo = _gl.GenFramebuffer();
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
                // Attach without assuming the underlying texture target (2D vs 2DMS etc).
                _gl.FramebufferTexture(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, _swapchainImagesGL[i][j].Image, 0);

                // Make the swapchain FBO robust against global ReadBuffer/DrawBuffers state changes.
                // Some engine passes intentionally set ReadBuffer=None; if that leaks, subsequent operations can become no-ops.
                _gl.NamedFramebufferDrawBuffers(fbo, 1, drawBuffers);
                _gl.NamedFramebufferReadBuffer(fbo, GLEnum.ColorAttachment0);
                _swapchainFramebuffers[i][j] = fbo;
            }

            Console.WriteLine($"Created swapchain {i} with {imageCount} images ({width}x{height})");
        }
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
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

            // Preserve GL state so the OpenXR blit path cannot clobber the engine's expected bindings.
            // In particular, RenderEye() binds the swapchain framebuffer before invoking this callback.
            int prevReadFbo = 0;
            int prevDrawFbo = 0;
            int prevReadBuffer = 0;
            bool prevScissorEnabled = false;
            bool capturedGlState = false;
            try
            {
                prevReadFbo = _gl.GetInteger(GetPName.ReadFramebufferBinding);
                prevDrawFbo = _gl.GetInteger(GetPName.DrawFramebufferBinding);
                prevReadBuffer = _gl.GetInteger(GetPName.ReadBuffer);
                prevScissorEnabled = _gl.IsEnabled(EnableCap.ScissorTest);
                capturedGlState = true;
            }
            catch
            {
                // Best-effort only; some drivers/contexts can throw if queried at the wrong time.
            }

            int frameNo = Volatile.Read(ref _openXrPendingFrameNumber);
            bool logLifecycle = OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo);

            static string DetectTextureTarget(GL gl, uint tex)
            {
                // Heuristic: try binding the texture name to common targets and see which one succeeds.
                // (Binding to an incompatible target yields GL_INVALID_OPERATION.)
                gl.GetError();
                gl.BindTexture(TextureTarget.Texture2D, tex);
                var e2d = gl.GetError();
                if (e2d == GLEnum.NoError)
                    return "Texture2D";

                gl.GetError();
                gl.BindTexture(TextureTarget.Texture2DArray, tex);
                var e2da = gl.GetError();
                if (e2da == GLEnum.NoError)
                    return "Texture2DArray";
                gl.GetError();
                gl.BindTexture(TextureTarget.Texture2DMultisample, tex);
                var e2dms = gl.GetError();
                if (e2dms == GLEnum.NoError)
                    return "Texture2DMultisample";

                return $"Unknown(err2D={e2d}, err2DA={e2da}, err2DMS={e2dms})";
            }

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

            var eyeViewport = viewIndex == 0 ? _openXrLeftViewport : _openXrRightViewport;
            var eyeCamera = viewIndex == 0 ? _openXrLeftEyeCamera : _openXrRightEyeCamera;

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

                // CollectVisible/SwapBuffers are handled on the engine's CollectVisible thread.
                eyeViewport.Render(_viewportMirrorFbo, _openXrFrameWorld, eyeCamera, shadowPass: false, forcedMaterial: null);

                var srcApiTex = renderer.GetOrCreateAPIRenderObject(_viewportMirrorColor, generateNow: true) as IGLTexture;
                if (srcApiTex is null || srcApiTex.BindingId == 0)
                    return;

                if (logLifecycle)
                {
                    string srcTarget = DetectTextureTarget(_gl, srcApiTex.BindingId);
                    string dstTarget = DetectTextureTarget(_gl, textureHandle);
                    Debug.Out($"OpenXR[{frameNo}] GLBlit: view={viewIndex} srcTex={srcApiTex.BindingId}({srcTarget}) dstTex={textureHandle}({dstTarget}) size={width}x{height}");
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
                _gl.FramebufferTexture(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0, textureHandle, 0);

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
                if (capturedGlState)
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
                        // Never allow state restoration failures to crash the render thread.
                    }
                }

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

    private void EnsureOpenXrViewports(uint width, uint height)
    {
        _openXrLeftViewport ??= new XRViewport(Window)
        {
            AutomaticallyCollectVisible = false,
            AutomaticallySwapBuffers = false,
            AllowUIRender = false,
            SetRenderPipelineFromCamera = false
        };
        _openXrRightViewport ??= new XRViewport(Window)
        {
            AutomaticallyCollectVisible = false,
            AutomaticallySwapBuffers = false,
            AllowUIRender = false,
            SetRenderPipelineFromCamera = false
        };

        _openXrLeftViewport.Camera = _openXrLeftEyeCamera;
        _openXrRightViewport.Camera = _openXrRightEyeCamera;

        // Diagnostic default: disable frustum culling while OpenXR is still being validated.
        // If this makes the world appear, the remaining issue is almost certainly frustum/projection/pose conversion.
        _openXrLeftViewport.CullWithFrustum = false;
        _openXrRightViewport.CullWithFrustum = false;

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

    private void EnsureOpenXrEyeCameras(XRCamera baseCamera)
    {
        // Prefer VRState-provided eye cameras when a VR rig exists.
        // The rig's transforms/parameters are responsible for choosing OpenVR vs OpenXR data sources.
        var vrInfo = Engine.VRState.ViewInformation;
        bool hasVrRig = Engine.VRState.IsInVR && (vrInfo.LeftEyeCamera is not null || vrInfo.RightEyeCamera is not null);
        _openXrLeftEyeCamera ??= (hasVrRig ? vrInfo.LeftEyeCamera : null) ?? new XRCamera(new Transform());
        _openXrRightEyeCamera ??= (hasVrRig ? vrInfo.RightEyeCamera : null) ?? new XRCamera(new Transform());

        // Eye transforms live under the locomotion root. If none is provided, anchor to the base camera.
        var locomotionRoot = _openXrLocomotionRoot ?? baseCamera.Transform;
        if (locomotionRoot is not null)
        {
            // Only reparent if the camera is currently unparented or parented to the base camera;
            // preserve custom hierarchies when the app provides its own VR rig.
            if (!ReferenceEquals(_openXrLeftEyeCamera.Transform, locomotionRoot) &&
                (_openXrLeftEyeCamera.Transform.Parent is null || ReferenceEquals(_openXrLeftEyeCamera.Transform.Parent, baseCamera.Transform)))
                _openXrLeftEyeCamera.Transform.SetParent(locomotionRoot, preserveWorldTransform: false, EParentAssignmentMode.Immediate);
            if (!ReferenceEquals(_openXrRightEyeCamera.Transform, locomotionRoot) &&
                (_openXrRightEyeCamera.Transform.Parent is null || ReferenceEquals(_openXrRightEyeCamera.Transform.Parent, baseCamera.Transform)))
                _openXrRightEyeCamera.Transform.SetParent(locomotionRoot, preserveWorldTransform: false, EParentAssignmentMode.Immediate);
        }

        CopyCameraCommon(baseCamera, _openXrLeftEyeCamera);
        CopyCameraCommon(baseCamera, _openXrRightEyeCamera);
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

    private void UpdateOpenXrEyeCameraFromView(XRCamera camera, uint viewIndex)
    {
        var pose = _views[viewIndex].Pose;
        var fov = _views[viewIndex].Fov;

        // OpenXR way: render the world directly from the per-eye view pose returned by xrLocateViews
        // in the same reference space we submit in the projection layer (layer.Space == _appSpace).
        // This keeps the rendered images consistent with the submitted projectionViews[*].Pose and
        // avoids timewarp/reprojection artifacts from pose-space mismatches.
        Vector3 eyePos = new(pose.Position.X, pose.Position.Y, pose.Position.Z);
        Quaternion eyeRot = Quaternion.Normalize(new Quaternion(
            pose.Orientation.X,
            pose.Orientation.Y,
            pose.Orientation.Z,
            pose.Orientation.W));

        Matrix4x4 eyeLocalMatrix = Matrix4x4.CreateFromQuaternion(eyeRot);
        eyeLocalMatrix.Translation = eyePos;

        // Only directly drive the transform when the camera isn't part of an app-provided VR rig.
        // (Rig eye cameras use VREyeTransform + VRHeadsetTransform and are updated via InvokeRecalcMatrixOnDraw.)
        if (camera.Transform is not XREngine.Scene.Transforms.VREyeTransform)
            camera.Transform.DeriveLocalMatrix(eyeLocalMatrix, networkSmoothed: false);

        if (camera.Parameters is XROpenXRFovCameraParameters openxrParams)
            openxrParams.SetAngles(fov.AngleLeft, fov.AngleRight, fov.AngleUp, fov.AngleDown);
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

        var root = _openXrLocomotionRoot;
        Matrix4x4 rootRender = root?.RenderMatrix ?? Matrix4x4.Identity;
        Matrix4x4 eyeRender = localPose * rootRender;

        // Apply composed render matrix so rapid locomotion-root rotations can't temporarily snap the eye.
        camera.Transform.SetRenderMatrix(eyeRender, recalcAllChildRenderMatrices: false);
    }

    private void EnsureViewportMirrorTargets(OpenGLRenderer renderer, uint width, uint height)
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
}
