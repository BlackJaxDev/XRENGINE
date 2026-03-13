using Silk.NET.OpenGL;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.VideoStreaming;
using XREngine.Rendering.VideoStreaming.Interfaces;
using XREngine.Rendering.Vulkan;

namespace XREngine;

internal sealed class EngineRuntimeVideoStreamingServices : IRuntimeVideoStreamingServices
{
    public IVideoFrameGpuActions CreateVideoFrameGpuActions(object renderer)
        => renderer switch
        {
            OpenGLRenderer glRenderer => new OpenGLVideoFrameGpuActions(new OpenGLVideoFrameUploadContext(glRenderer)),
            VulkanRenderer vkRenderer => new VulkanVideoFrameGpuActions(new VulkanVideoFrameUploadContext(vkRenderer)),
            _ => new NullVideoFrameGpuActions(renderer.GetType().Name)
        };

    private sealed class OpenGLVideoFrameUploadContext(OpenGLRenderer renderer) : IOpenGLVideoFrameUploadContext
    {
        public GL GL => renderer.RawGL;

        public IOpenGLVideoFrameTextureHandle? ResolveTexture(XRTexture2D texture)
            => renderer.GenericToAPI<GLTexture2D>(texture) is { } handle
                ? new OpenGLVideoFrameTextureHandle(handle)
                : null;
    }

    private sealed class OpenGLVideoFrameTextureHandle(GLTexture2D textureHandle) : IOpenGLVideoFrameTextureHandle
    {
        public bool IsGenerated => textureHandle.IsGenerated;
        public uint BindingId => textureHandle.BindingId;

        public void Generate()
            => textureHandle.Generate();

        public void ClearInvalidation()
            => textureHandle.ClearInvalidation();
    }

    private sealed class VulkanVideoFrameUploadContext(VulkanRenderer renderer) : IVulkanVideoFrameUploadContext
    {
        public IVulkanVideoFrameTextureHandle? ResolveTexture(XRTexture2D texture)
            => renderer.GenericToAPI<VulkanRenderer.VkTexture2D>(texture) is { } handle
                ? new VulkanVideoFrameTextureHandle(handle)
                : null;
    }

    private sealed class VulkanVideoFrameTextureHandle(VulkanRenderer.VkTexture2D textureHandle) : IVulkanVideoFrameTextureHandle
    {
        public bool UploadVideoFrameData(ReadOnlySpan<byte> pixelData, uint width, uint height)
            => textureHandle.UploadVideoFrameData(pixelData, width, height);
    }

    private sealed class NullVideoFrameGpuActions(string rendererName) : IVideoFrameGpuActions
    {
        public bool UploadVideoFrame(DecodedVideoFrame frame, object? targetTexture, out string? error)
        {
            error = $"Renderer '{rendererName}' cannot upload streaming video frames.";
            return false;
        }

        public void Dispose()
        {
        }
    }
}
