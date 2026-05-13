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
    private void InitGL(GL api)
    {
        string version;
        unsafe
        {
            version = new((sbyte*)api.GetString(StringName.Version));
            string vendor = new((sbyte*)api.GetString(StringName.Vendor));
            string renderer = new((sbyte*)api.GetString(StringName.Renderer));
            string shadingLanguageVersion = new((sbyte*)api.GetString(StringName.ShadingLanguageVersion));
            Debug.OpenGL($"OpenGL Version: {version}");
            Debug.OpenGL($"OpenGL Vendor: {vendor}");
            Debug.OpenGL($"OpenGL Renderer: {renderer}");
            Debug.OpenGL($"OpenGL Shading Language Version: {shadingLanguageVersion}");

            Engine.Rendering.State.IsNVIDIA = vendor.Contains("NVIDIA");
            Engine.Rendering.State.IsIntel = vendor.Contains("Intel");
            Engine.Rendering.State.IsVulkan = false;
            Engine.Rendering.State.OpenGLVendor = vendor;
            Engine.Rendering.State.OpenGLRendererName = renderer;
            Engine.Rendering.State.VulkanDeviceName = null;
            Engine.Rendering.State.VulkanVendorId = 0;
            Engine.Rendering.State.VulkanDeviceId = 0;

            // Cache full extension list once so ImGui/debug panels can display it without re-enumerating each frame.
            // Also probe for GL_NV_ray_tracing support early so features can decide whether to attempt the RT path.
            string[] extensions = [];
            try
            {
                int extCount = api.GetInteger(GLEnum.NumExtensions);
                if (extCount > 0)
                {
                    var list = new string[extCount];
                    for (uint i = 0; i < extCount; i++)
                        list[i] = new((sbyte*)api.GetString(StringName.Extensions, i));
                    extensions = list;
                }
            }
            catch (Exception ex)
            {
                Debug.OpenGLWarning($"Failed to query GL extensions for NV ray tracing: {ex.Message}");
            }

            Engine.Rendering.State.OpenGLExtensions = extensions;
            int glMajor = 0;
            int glMinor = 0;
            try
            {
                glMajor = api.GetInteger(GLEnum.MajorVersion);
                glMinor = api.GetInteger(GLEnum.MinorVersion);
            }
            catch (Exception ex)
            {
                Debug.OpenGLWarning($"Failed to query GL version integers for layered-rendering capability detection: {ex.Message}");
            }

            bool VersionAtLeast(int major, int minor)
                => glMajor > major || (glMajor == major && glMinor >= minor);

            bool HasExtension(string extensionName)
                => extensions.Any(e => string.Equals(e, extensionName, StringComparison.Ordinal));

            Engine.Rendering.State.SupportsOpenGLLayeredFramebuffers =
                VersionAtLeast(3, 2) ||
                HasExtension("GL_ARB_geometry_shader4") ||
                HasExtension("GL_EXT_geometry_shader4");
            Engine.Rendering.State.SupportsOpenGLGeometryShaderLayeredRendering =
                VersionAtLeast(3, 2) ||
                HasExtension("GL_ARB_geometry_shader4") ||
                HasExtension("GL_EXT_geometry_shader4");
            Engine.Rendering.State.SupportsOpenGLViewportArray =
                VersionAtLeast(4, 1) ||
                HasExtension("GL_ARB_viewport_array") ||
                HasExtension("GL_NV_viewport_array");
            Engine.Rendering.State.SupportsOpenGLViewportScissorArray =
                Engine.Rendering.State.SupportsOpenGLViewportArray;
            Engine.Rendering.State.SupportsOpenGLVertexShaderLayeredRendering =
                VersionAtLeast(4, 5) ||
                HasExtension("GL_ARB_shader_viewport_layer_array") ||
                HasExtension("GL_AMD_vertex_shader_layer") ||
                HasExtension("GL_NV_viewport_array2");
            Engine.Rendering.State.SupportsOpenGLVertexShaderViewportIndex =
                Engine.Rendering.State.SupportsOpenGLViewportArray &&
                Engine.Rendering.State.SupportsOpenGLVertexShaderLayeredRendering;
            Engine.Rendering.State.SupportsOpenGLGeometryShaderViewportIndex =
                Engine.Rendering.State.SupportsOpenGLViewportArray &&
                Engine.Rendering.State.SupportsOpenGLGeometryShaderLayeredRendering;
            try
            {
                Engine.Rendering.State.MaxOpenGLViewports = Math.Max(1, api.GetInteger(GLEnum.MaxViewports));
            }
            catch (Exception ex)
            {
                Engine.Rendering.State.MaxOpenGLViewports = 1;
                Debug.OpenGLWarning($"Failed to query GL_MAX_VIEWPORTS: {ex.Message}");
            }

            bool hasExtMultiview = false;
            for (int i = 0; i < extensions.Length; i++)
            {
                if (string.Equals(extensions[i], "GL_EXT_multiview", StringComparison.Ordinal))
                {
                    hasExtMultiview = true;
                    break;
                }
            }
            Engine.Rendering.State.HasOvrMultiViewExtension |= hasExtMultiview;

            ConfigureParallelShaderCompile(api, extensions);

            InitializeSparseTextureSupport(extensions);

            // Ray tracing / DLSS / XeSS are Vulkan-focused; do not probe GL_NV_ray_tracing on OpenGL startup.
            Engine.Rendering.State.HasNvRayTracing = false;
            Engine.Rendering.State.HasVulkanRayTracing = false;
            Engine.Rendering.State.HasVulkanMemoryDecompression = false;
            Engine.Rendering.State.HasVulkanCopyMemoryIndirect = false;
            Engine.Rendering.State.HasVulkanRtxIo = false;
            Engine.Rendering.State.HasVulkanMultiView = false;
        }

        Engine.Rendering.RefreshVulkanUpscaleBridgeCapabilitySnapshot(this);

        GLRenderProgram.ReadBinaryShaderCache(api);

        // Initialize async program binary upload via a shared GL context.
        InitAsyncProgramBinaryUpload();

        api.Enable(EnableCap.Multisample);
        api.Enable(EnableCap.TextureCubeMapSeamless);
        api.FrontFace(FrontFaceDirection.Ccw);

        // Avoid debug-layer warnings when clearing integer framebuffers.
        // Dithering is enabled by default in OpenGL but isn't meaningful for integer attachments.
        api.Disable(EnableCap.Dither);

        api.ClipControl(GLEnum.LowerLeft, GLEnum.NegativeOneToOne);

        // Enable framebuffer-sRGB: only sRGB-formatted color attachments
        // (e.g. AlbedoOpacityTexture as Srgb8Alpha8) get linear<->sRGB
        // conversion on read/write. Non-sRGB attachments (RGBA16F,
        // R11fG11fB10f, RGBA8 unorm, depth, etc.) are unaffected per the
        // OpenGL spec, so the existing "manual gamma in PostProcess.fs"
        // path for the LDR display output remains correct.
        api.Enable(EnableCap.FramebufferSrgb);

        api.PixelStore(PixelStoreParameter.PackAlignment, 1);
        api.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        api.PointSize(1.0f);
        api.LineWidth(1.0f);

        api.UseProgram(0);

        SetupDebug(api);
    }

    public override void MemoryBarrier(EMemoryBarrierMask mask)
    {
        Api.MemoryBarrier(ToGLMask(mask));
    }

    public override void WaitForGpu()
    {
        if (ShouldSkipGpuWaitForShutdown(
            out int pendingBinaryUploads,
            out int pendingSourceLinks,
            out bool pendingAsyncPrograms))
        {
            _shutdownAbandonedAsyncShaderWork = true;
            Debug.OpenGL(
                "[OpenGLShutdown] Skipped glFinish because async shader program work is still active during window shutdown; " +
                $"pendingSourceLinks={pendingSourceLinks} pendingBinaryUploads={pendingBinaryUploads} " +
                $"pendingAsyncPrograms={pendingAsyncPrograms}.");
            DisposeAsyncShaderProgramWorkForShutdown();
            return;
        }

        Api.Finish();
    }

    private bool ShouldSkipGpuWaitForShutdown(
        out int pendingBinaryUploads,
        out int pendingSourceLinks,
        out bool pendingAsyncPrograms)
    {
        pendingBinaryUploads = 0;
        pendingSourceLinks = 0;
        pendingAsyncPrograms = false;

        bool windowClosing = XRWindow.IsDisposing || XRWindow.IsDisposed;
        try
        {
            windowClosing |= Window.IsClosing;
        }
        catch
        {
        }

        return windowClosing &&
               HasPendingShaderProgramShutdownWork(
                   out pendingBinaryUploads,
                   out pendingSourceLinks,
                   out pendingAsyncPrograms);
    }

    public override void ColorMask(bool red, bool green, bool blue, bool alpha)
    {
        Api.ColorMask(red, green, blue, alpha);
    }

    private uint ToGLMask(EMemoryBarrierMask mask)
    {
        if (mask.HasFlag(EMemoryBarrierMask.All))
            return uint.MaxValue;

        uint glMask = 0;
        if (mask.HasFlag(EMemoryBarrierMask.VertexAttribArray))
            glMask |= (uint)MemoryBarrierMask.VertexAttribArrayBarrierBit;
        if (mask.HasFlag(EMemoryBarrierMask.ElementArray))
            glMask |= (uint)MemoryBarrierMask.ElementArrayBarrierBit;
        if (mask.HasFlag(EMemoryBarrierMask.Uniform))
            glMask |= (uint)MemoryBarrierMask.UniformBarrierBit;
        if (mask.HasFlag(EMemoryBarrierMask.TextureFetch))
            glMask |= (uint)MemoryBarrierMask.TextureFetchBarrierBit;
        if (mask.HasFlag(EMemoryBarrierMask.ShaderGlobalAccess))
            glMask |= (uint)MemoryBarrierMask.ShaderGlobalAccessBarrierBitNV;
        if (mask.HasFlag(EMemoryBarrierMask.ShaderImageAccess))
            glMask |= (uint)MemoryBarrierMask.ShaderImageAccessBarrierBit;
        if (mask.HasFlag(EMemoryBarrierMask.Command))
            glMask |= (uint)MemoryBarrierMask.CommandBarrierBit;
        if (mask.HasFlag(EMemoryBarrierMask.PixelBuffer))
            glMask |= (uint)MemoryBarrierMask.PixelBufferBarrierBit;
        if (mask.HasFlag(EMemoryBarrierMask.TextureUpdate))
            glMask |= (uint)MemoryBarrierMask.TextureUpdateBarrierBit;
        if (mask.HasFlag(EMemoryBarrierMask.BufferUpdate))
            glMask |= (uint)MemoryBarrierMask.BufferUpdateBarrierBit;
        if (mask.HasFlag(EMemoryBarrierMask.Framebuffer))
            glMask |= (uint)MemoryBarrierMask.FramebufferBarrierBit;
        if (mask.HasFlag(EMemoryBarrierMask.TransformFeedback))
            glMask |= (uint)MemoryBarrierMask.TransformFeedbackBarrierBit;
        if (mask.HasFlag(EMemoryBarrierMask.AtomicCounter))
            glMask |= (uint)MemoryBarrierMask.AtomicCounterBarrierBit;
        if (mask.HasFlag(EMemoryBarrierMask.ShaderStorage))
            glMask |= (uint)MemoryBarrierMask.ShaderStorageBarrierBit;
        if (mask.HasFlag(EMemoryBarrierMask.ClientMappedBuffer))
            glMask |= (uint)MemoryBarrierMask.ClientMappedBufferBarrierBit;
        if (mask.HasFlag(EMemoryBarrierMask.QueryBuffer))
            glMask |= (uint)MemoryBarrierMask.QueryBufferBarrierBit;
        return glMask;
    }
}
