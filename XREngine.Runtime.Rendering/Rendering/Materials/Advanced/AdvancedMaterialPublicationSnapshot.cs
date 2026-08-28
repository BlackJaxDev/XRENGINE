namespace XREngine.Rendering;

/// <summary>
/// Retained, database-owned material payload image. Its arrays are allocated at
/// setup/growth time; sealing only copies canonical bytes so later mutations can
/// never change a previously published material payload.
/// </summary>
public sealed class AdvancedMaterialPublicationSnapshot
{
    private readonly AdvancedGpuHandle[] _layoutHandles;
    private readonly uint[] _constantWords;
    private readonly AdvancedMaterialTextureBinding[] _textureBindings;

    internal AdvancedMaterialPublicationSnapshot(int layoutHandleCapacity, int constantWordCapacity, int textureBindingCapacity)
    {
        _layoutHandles = new AdvancedGpuHandle[layoutHandleCapacity];
        _constantWords = new uint[constantWordCapacity];
        _textureBindings = new AdvancedMaterialTextureBinding[textureBindingCapacity];
    }

    public ulong Sequence { get; private set; }
    public AdvancedMaterialDatabaseGenerations Generations { get; private set; }
    public ReadOnlySpan<AdvancedGpuHandle> MaterialLayoutHandles => _layoutHandles;
    public ReadOnlySpan<uint> ConstantWords => _constantWords;
    public ReadOnlySpan<AdvancedMaterialTextureBinding> TextureBindings => _textureBindings;

    internal bool TryCapture(ulong sequence, ReadOnlySpan<AdvancedGpuHandle> layoutHandles, ReadOnlySpan<uint> constantWords,
        ReadOnlySpan<AdvancedMaterialTextureBinding> textureBindings, ulong materialGeneration, ulong kernelGeneration, ulong layoutGeneration)
    {
        if (sequence == 0u || layoutHandles.Length > _layoutHandles.Length || constantWords.Length > _constantWords.Length || textureBindings.Length > _textureBindings.Length)
            return false;
        layoutHandles.CopyTo(_layoutHandles);
        constantWords.CopyTo(_constantWords);
        textureBindings.CopyTo(_textureBindings);
        Sequence = sequence;
        Generations = new AdvancedMaterialDatabaseGenerations(materialGeneration, kernelGeneration, layoutGeneration);
        return true;
    }
}
