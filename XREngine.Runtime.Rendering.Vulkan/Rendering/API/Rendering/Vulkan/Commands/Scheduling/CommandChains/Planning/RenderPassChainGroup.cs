namespace XREngine.Rendering.Vulkan;

internal sealed class RenderPassChainGroup
{
    private CommandChainKey[] _chainKeys = [];
    private int _chainKeyCount;

    public RenderPassChainGroup()
    {
    }

    public RenderPassChainGroup(
        int passIndex,
        int targetIdentity,
        string targetName,
        ReadOnlyMemory<CommandChainKey> chainKeys,
        ulong structuralSignature,
        bool supportsSecondaryCommandBuffers,
        bool dynamicOverlay)
        => Reset(
            passIndex,
            targetIdentity,
            targetName,
            chainKeys.Span,
            structuralSignature,
            supportsSecondaryCommandBuffers,
            dynamicOverlay);

    public int PassIndex { get; private set; }
    public int TargetIdentity { get; private set; }
    public string TargetName { get; private set; } = string.Empty;
    public ReadOnlyMemory<CommandChainKey> ChainKeys => _chainKeys.AsMemory(0, _chainKeyCount);
    public ulong StructuralSignature { get; private set; }
    public bool SupportsSecondaryCommandBuffers { get; private set; }
    public bool DynamicOverlay { get; private set; }

    public void Reset(
        int passIndex,
        int targetIdentity,
        string targetName,
        ReadOnlySpan<CommandChainKey> chainKeys,
        ulong structuralSignature,
        bool supportsSecondaryCommandBuffers,
        bool dynamicOverlay)
    {
        if (_chainKeys.Length < chainKeys.Length)
        {
            int capacity = Math.Max(
                chainKeys.Length,
                _chainKeys.Length == 0 ? 8 : _chainKeys.Length * 2);
            Array.Resize(ref _chainKeys, capacity);
        }

        chainKeys.CopyTo(_chainKeys);
        _chainKeyCount = chainKeys.Length;
        PassIndex = passIndex;
        TargetIdentity = targetIdentity;
        TargetName = targetName;
        StructuralSignature = structuralSignature;
        SupportsSecondaryCommandBuffers = supportsSecondaryCommandBuffers;
        DynamicOverlay = dynamicOverlay;
    }
}
