using XREngine.Rendering.Materials;

namespace XREngine.Rendering.WebGPU;

/// <summary>Derives native standard-material offsets from the canonical publisher's live schema.</summary>
internal static class WebGpuAdvancedStandardMaterialContract
{
    internal static readonly uint BaseColorWord = Word("BaseColorOpacity");
    internal static readonly uint AlphaCutoffWord = Word("AlphaCutoff");
    internal static readonly uint FlagsWord = Word("Flags");
    internal static bool IsStandard(in AdvancedMaterialRecord material)
        => WebGpuAdvancedMaterialContract.IsStandard(in material);

    private static uint Word(string name)
    {
        if (!MaterialBindingLayouts.OpaqueDeferred.TryGetPackedMember(name, out MaterialBindingPackedMember member) ||
            !MaterialBindingLayouts.ForwardOpaque.TryGetPackedMember(name, out MaterialBindingPackedMember forward) ||
            !MaterialBindingLayouts.MaskedForward.TryGetPackedMember(name, out MaterialBindingPackedMember masked) ||
            member.WordOffset != forward.WordOffset || member.WordOffset != masked.WordOffset ||
            member.WordCount != forward.WordCount || member.WordCount != masked.WordCount)
            throw new NotSupportedException("WebGPU.Advanced.MaterialLayoutChanged: the native standard-material companion requires a matching canonical schema.");
        return member.WordOffset;
    }

}
