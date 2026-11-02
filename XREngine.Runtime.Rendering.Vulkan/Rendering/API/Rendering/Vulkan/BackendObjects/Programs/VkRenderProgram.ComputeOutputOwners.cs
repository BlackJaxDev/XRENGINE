namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkRenderProgram
{
    private readonly object _directComputeOutputRetirementGate = new();
    private readonly Dictionary<ulong, XRFrameBuffer> _directComputeOutputOwners = [];
    private readonly List<ulong> _destroyedDirectComputeOutputKeys = [];
    private readonly List<ComputeUniformBufferKey> _destroyedDirectComputeUniformKeys = [];
    private readonly List<ComputeUniformBuffer> _pendingLayoutComputeUniformRetirements = [];
    private readonly List<(uint ImageIndex, ulong SchemaFingerprint, ulong BindingKey)>
        _destroyedDirectComputeSignatureKeys = [];

    internal bool TryRecordDirectComputeOutputOwner(ulong bindingKey, XRFrameBuffer owner)
    {
        lock (_computeDescriptorHeapPayloadGate)
        {
            if (bindingKey == 0 || owner.IsDestroyed)
                return false;
            if (_directComputeOutputOwners.TryGetValue(bindingKey, out XRFrameBuffer? existing))
                return ReferenceEquals(existing, owner);
            _directComputeOutputOwners.Add(bindingKey, owner);
            return true;
        }
    }

    internal bool HasDirectComputeOutputOwners
    {
        get
        {
            lock (_computeDescriptorHeapPayloadGate)
                return _directComputeOutputOwners.Count != 0;
        }
    }

    private void RetireDirectComputeDescriptorPoolsForLayout()
    {
        lock (_computeDescriptorHeapPayloadGate)
        {
            _destroyedDirectComputeOutputKeys.Clear();
            foreach (ulong bindingKey in _directComputeOutputOwners.Keys)
                _destroyedDirectComputeOutputKeys.Add(bindingKey);
        }

        for (int index = 0; index < _destroyedDirectComputeOutputKeys.Count; index++)
            BackendContext.Resources.Descriptors.RetireComputeDescriptorPoolsForBindingKey(
                _destroyedDirectComputeOutputKeys[index]);
        _destroyedDirectComputeOutputKeys.Clear();
    }

    /// <summary>Retire output-owned compute resources after their framebuffer is destroyed.</summary>
    internal void RetireDestroyedDirectComputeOutputs()
    {
        lock (_directComputeOutputRetirementGate)
        {
            lock (_computeDescriptorHeapPayloadGate)
            {
                _destroyedDirectComputeOutputKeys.Clear();
                foreach ((ulong key, XRFrameBuffer owner) in _directComputeOutputOwners)
                    if (owner.IsDestroyed)
                        _destroyedDirectComputeOutputKeys.Add(key);
            }

            for (int ownerIndex = 0; ownerIndex < _destroyedDirectComputeOutputKeys.Count; ownerIndex++)
            {
                ulong bindingKey = _destroyedDirectComputeOutputKeys[ownerIndex];
                BackendContext.Resources.Descriptors.RetireComputeDescriptorPoolsForBindingKey(bindingKey);

                lock (_computeDescriptorHeapPayloadGate)
                {
                    _destroyedDirectComputeUniformKeys.Clear();
                    foreach (ComputeUniformBufferKey key in _computeUniformBuffers.Keys)
                        if (key.DispatchKey == bindingKey)
                            _destroyedDirectComputeUniformKeys.Add(key);
                }

                for (int bufferIndex = 0; bufferIndex < _destroyedDirectComputeUniformKeys.Count; bufferIndex++)
                {
                    ComputeUniformBufferKey key = _destroyedDirectComputeUniformKeys[bufferIndex];
                    ComputeUniformBuffer resource;
                    lock (_computeDescriptorHeapPayloadGate)
                    {
                        if (!_computeUniformBuffers.TryGetValue(key, out resource))
                            continue;
                    }

                    // Keep the dictionary entry if retirement rejects this handoff.
                    ReleaseComputeUniformBuffer(resource);
                    lock (_computeDescriptorHeapPayloadGate)
                    {
                        if (_computeUniformBuffers.TryGetValue(key, out ComputeUniformBuffer current) &&
                            current.Buffer.Handle == resource.Buffer.Handle)
                            _computeUniformBuffers.Remove(key);
                    }
                }

                lock (_computeDescriptorHeapPayloadGate)
                {
                    _destroyedDirectComputeSignatureKeys.Clear();
                    foreach (var key in _reusableComputeDescriptorResourceSignatures.Keys)
                        if (key.BindingKey == bindingKey)
                            _destroyedDirectComputeSignatureKeys.Add(key);
                    for (int signatureIndex = 0; signatureIndex < _destroyedDirectComputeSignatureKeys.Count; signatureIndex++)
                        _reusableComputeDescriptorResourceSignatures.Remove(
                            _destroyedDirectComputeSignatureKeys[signatureIndex]);
                    _directComputeOutputOwners.Remove(bindingKey);
                }
            }

            _destroyedDirectComputeOutputKeys.Clear();
            _destroyedDirectComputeUniformKeys.Clear();
            _destroyedDirectComputeSignatureKeys.Clear();
        }
    }
}
