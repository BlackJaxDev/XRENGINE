using XREngine.Rendering.Materials;

namespace XREngine.Rendering.WebGPU;

/// <summary>Derives native standard-material offsets from the canonical publisher's live schema.</summary>
internal static class WebGpuAdvancedStandardMaterialContract
{
    internal static readonly uint BaseColorWord = Word("BaseColorOpacity");
    internal static readonly uint AlphaCutoffWord = Word("AlphaCutoff");
    internal static readonly uint FlagsWord = Word("Flags");
    private static readonly ulong DeferredLayout = Hash(MaterialBindingLayouts.OpaqueDeferred.LayoutHash);
    private static readonly ulong ForwardLayout = Hash(MaterialBindingLayouts.ForwardOpaque.LayoutHash);
    private static readonly ulong MaskedLayout = Hash(MaterialBindingLayouts.MaskedForward.LayoutHash);

    internal static bool IsStandard(in AdvancedMaterialRecord material)
        => material.MaterialLayoutHash == DeferredLayout || material.MaterialLayoutHash == ForwardLayout ||
            material.MaterialLayoutHash == MaskedLayout;

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

    private static ulong Hash(string value)
    {
        ulong hash = 14695981039346656037ul;
        foreach (char character in value)
            hash = unchecked((hash ^ character) * 1099511628211ul);
        return hash;
    }
}
