using System.Globalization;
using System.Text;
using XREngine.Rendering.Materials;

namespace XREngine.Rendering.Shaders;

/// <summary>
/// Generates native material shader offsets from the layouts used by canonical publication.
/// A changed CPU packing cannot silently reinterpret alpha coverage or shading constants.
/// </summary>
internal static class AdvancedStandardMaterialShaderContract
{
    internal static void AppendDefines(StringBuilder source)
    {
        MaterialBindingLayout layout = MaterialBindingLayouts.OpaqueDeferred;
        AppendMember(source, layout, "BaseColorOpacity", "XR_ADV_STANDARD_BASE_COLOR_WORD");
        AppendMember(source, layout, "RMSE", "XR_ADV_STANDARD_RMSE_WORD");
        AppendMember(source, layout, "AlphaCutoff", "XR_ADV_STANDARD_ALPHA_CUTOFF_WORD");
        AppendMember(source, layout, "Flags", "XR_ADV_STANDARD_FLAGS_WORD");
        Append(source, "XR_ADV_STANDARD_WORD_COUNT", layout.RowWordCount);
        AppendLayout(source, "XR_ADV_STANDARD_DEFERRED_LAYOUT", layout);
        AppendLayout(source, "XR_ADV_STANDARD_FORWARD_LAYOUT", MaterialBindingLayouts.ForwardOpaque);
        AppendLayout(source, "XR_ADV_STANDARD_MASKED_LAYOUT", MaterialBindingLayouts.MaskedForward);
    }

    private static void AppendMember(StringBuilder source, MaterialBindingLayout layout, string name, string define)
    {
        if (!layout.TryGetPackedMember(name, out MaterialBindingPackedMember member) ||
            !MaterialBindingLayouts.ForwardOpaque.TryGetPackedMember(name, out MaterialBindingPackedMember forward) ||
            !MaterialBindingLayouts.MaskedForward.TryGetPackedMember(name, out MaterialBindingPackedMember masked) ||
            member.WordOffset != forward.WordOffset || member.WordOffset != masked.WordOffset ||
            member.WordCount != forward.WordCount || member.WordCount != masked.WordCount)
            throw new InvalidOperationException($"Native material field '{name}' requires an explicit shader translation for the published layout.");
        Append(source, define, member.WordOffset);
    }

    private static void AppendLayout(StringBuilder source, string name, MaterialBindingLayout layout)
    {
        // AdvancedGpuMaterialPublisher publishes this FNV-1a hash of LayoutHash.
        ulong hash = 14695981039346656037ul;
        foreach (char character in layout.LayoutHash)
            hash = unchecked((hash ^ character) * 1099511628211ul);
        Append(source, name + "_LO", (uint)hash);
        Append(source, name + "_HI", (uint)(hash >> 32));
    }

    private static void Append(StringBuilder source, string name, uint value)
        => source.Append("#define ").Append(name).Append(' ')
            .Append(value.ToString(CultureInfo.InvariantCulture)).AppendLine("u");
}
