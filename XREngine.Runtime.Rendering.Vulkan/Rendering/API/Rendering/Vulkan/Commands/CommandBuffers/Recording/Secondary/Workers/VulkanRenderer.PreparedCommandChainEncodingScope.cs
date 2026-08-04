namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly struct PreparedCommandChainEncodingScope : IDisposable
    {
        private readonly VulkanRenderer _renderer;
        private readonly VulkanCommandThreadContext _threadContext;
        private readonly VulkanPreparedWorkerPlannerStamp _plannerStamp;

        internal PreparedCommandChainEncodingScope(VulkanRenderer renderer)
        {
            _renderer = renderer;
            _threadContext = renderer.CommandThreadContext;
            if (_threadContext.PreparedCommandChainEncodingActive)
            {
                throw new InvalidOperationException(
                    "Prepared Vulkan command-chain encoding cannot be nested.");
            }
            if (renderer.HasThreadResourcePlannerRuntimeState)
            {
                throw new InvalidOperationException(
                    "Prepared Vulkan command-chain encoding cannot enter with a resource-planner scope.");
            }

            _plannerStamp = renderer.CapturePreparedWorkerPlannerStamp();
            _threadContext.PreparedCommandChainEncodingActive = true;
        }

        public void Dispose()
        {
            try
            {
                if (_renderer.HasThreadResourcePlannerRuntimeState)
                {
                    throw new InvalidOperationException(
                        "Prepared Vulkan command-chain encoding published thread-local resource-planner state.");
                }
                if (_plannerStamp !=
                    _renderer.CapturePreparedWorkerPlannerStamp())
                {
                    throw new InvalidOperationException(
                        "Prepared Vulkan command-chain encoding mutated global resource-planner state.");
                }
            }
            finally
            {
                _threadContext.PreparedCommandChainEncodingActive = false;
            }
        }
    }
}

