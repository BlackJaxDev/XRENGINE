using XREngine.Data.Rendering;

namespace XREngine.Rendering.Commands;

public sealed partial class BackendReadyFramePackage
{
    private uint[] _authoredDecalCommandKeys = [];
    private int _authoredDecalCommandCount;

    /// <summary>Frozen enabled decal identities in the selected pass's blend order.</summary>
    public ReadOnlySpan<uint> AuthoredDecalCommandKeys => _authoredDecalCommandKeys.AsSpan(0, _authoredDecalCommandCount);
    public ulong AuthoredDecalCommandSignature { get; private set; }

    private void PrepareAuthoredDecalSelection()
    {
        _authoredDecalCommandCount = 0;
        AuthoredDecalCommandSignature = 0;
        if (!NativeAuthoredDecalsEnabled) return;
        ulong hash = AddHash(14695981039346656037UL, 1u);
        if (TryGetPass((int)EDefaultRenderPass.DeferredDecals, out BackendReadyRenderPass pass))
        {
            if (_authoredDecalCommandKeys.Length < pass.CommandCount)
                Array.Resize(ref _authoredDecalCommandKeys, (int)BitOperations.RoundUpToPowerOf2((uint)pass.CommandCount));
            for (int index = 0; index < pass.CommandCount; index++)
            {
                RenderCommand command = GetPassCommand(in pass, index);
                // Preparation precedes the authoritative command swap. Retain its
                // pending enabled state rather than reading mutable commands at render.
                if (!command.Enabled) continue;
                _authoredDecalCommandKeys[_authoredDecalCommandCount++] = command.StableQueryKey;
                hash = AddHash(hash, command.StableQueryKey);
            }
        }
        AuthoredDecalCommandSignature = MixHash(AddHash(hash, _authoredDecalCommandCount));
    }
}
