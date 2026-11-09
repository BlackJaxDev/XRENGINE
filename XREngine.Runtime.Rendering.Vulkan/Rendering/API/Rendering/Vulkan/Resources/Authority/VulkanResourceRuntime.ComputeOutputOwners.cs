namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanResourceRuntime
{
    private readonly object _directComputeOutputProgramsGate = new();
    private readonly object _directComputeOutputSweepGate = new();
    private readonly HashSet<VkRenderProgram> _directComputeOutputPrograms = [];
    private readonly List<VkRenderProgram> _directComputeOutputProgramsScratch = [];

    internal bool TryRegisterDirectComputeOutputOwner(
        VkRenderProgram program,
        ulong bindingKey,
        XRFrameBuffer owner)
    {
        lock (_directComputeOutputProgramsGate)
        {
            if (!program.TryRecordDirectComputeOutputOwner(bindingKey, owner))
                return false;
            _directComputeOutputPrograms.Add(program);
            return true;
        }
    }

    /// <summary>Release resources for destroyed output owners before compute preparation.</summary>
    private void DrainDestroyedDirectComputeOutputs()
    {
        lock (_directComputeOutputSweepGate)
        {
            lock (_directComputeOutputProgramsGate)
            {
                _directComputeOutputProgramsScratch.Clear();
                foreach (VkRenderProgram program in _directComputeOutputPrograms)
                    _directComputeOutputProgramsScratch.Add(program);
            }

            for (int index = 0; index < _directComputeOutputProgramsScratch.Count; index++)
                _directComputeOutputProgramsScratch[index].RetireDestroyedDirectComputeOutputs();

            lock (_directComputeOutputProgramsGate)
            {
                for (int index = 0; index < _directComputeOutputProgramsScratch.Count; index++)
                {
                    VkRenderProgram program = _directComputeOutputProgramsScratch[index];
                    if (!program.HasDirectComputeOutputOwners)
                        _directComputeOutputPrograms.Remove(program);
                }
            }
            _directComputeOutputProgramsScratch.Clear();
        }
    }
}
