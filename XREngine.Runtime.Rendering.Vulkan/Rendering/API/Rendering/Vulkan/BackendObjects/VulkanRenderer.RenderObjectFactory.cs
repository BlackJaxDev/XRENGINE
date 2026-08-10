using System;
using Silk.NET.Vulkan;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan
{
    public partial class VulkanRenderer
    {
        internal VulkanBackendObjectContext BackendObjectContext
            => ResourceRuntime.GetOrCreateBackendObjectContext(
                Api!,
                _deviceContext,
                _commandRuntime,
                _framePlanner,
                _frameTelemetry,
                AllowSynchronousResourceUploads);

        protected override AbstractRenderAPIObject CreateAPIRenderObject(GenericRenderObject renderObject)
            => BackendObjectContext.CreateWrapper(renderObject);
    }
}
