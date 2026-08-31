namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>Signals a retryable native-buffer publication change while sealing a frame plan.</summary>
internal sealed class VulkanNativeBufferBindingSupersededException(string message) : Exception(message);
