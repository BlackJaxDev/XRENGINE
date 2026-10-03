using System.Runtime.InteropServices;
using XREngine.Rendering.Materials;

namespace XREngine.Rendering.WebGPU;

/// <summary>Exact 160-byte native shader uniform image derived from canonical material layouts.</summary>
internal static class WebGpuAdvancedShadingParameters
{
    internal static readonly ulong DeferredHash = Hash(MaterialBindingLayouts.OpaqueDeferred.LayoutHash);
    internal static readonly ulong ForwardHash = Hash(MaterialBindingLayouts.ForwardOpaque.LayoutHash);
    internal static readonly ulong MaskedHash = Hash(MaterialBindingLayouts.MaskedForward.LayoutHash);
    internal static readonly ulong MirrorHash = Hash(MaterialBindingLayouts.ProjectiveMirror.LayoutHash);
    private static readonly uint[] StandardWords = [
        Word("BaseColorOpacity"), Word("RMSE"), Word("Flags"), Word("EmissionColor"),
        Word("EmissionStrength"), Word("EmissionTextureMetadata"), Word("EmissionUvScaleOffset"), Word("EmissionUvRotation")];

    internal static void Write(Span<uint> words, in AdvancedVisibilityStageBackendRequest request,
        WebGpuAdvancedVisibilityFrame visibility, WebGpuAdvancedShadingFrame frame, uint cohort)
    {
        words.Clear();
        words[0] = frame.Width; words[1] = frame.Height;
        words[2] = (frame.Width + 15) / 16; words[3] = (frame.Height + 15) / 16;
        words[4] = request.NativeViewIndex; words[5] = (uint)request.Views.ViewCount;
        words[6] = frame.Cohorts[cohort]!.Kernel;
        words[7] = (request.RequireNativeOutput ? 2u : 0u) | (request.EnableLightProbesAndIbl ? 8u : 0u) |
            ((uint)request.ShadingDebugView << 8);
        words[8] = request.FroxelDepthSlices;
        words[10] = (uint)visibility.Scene!.Snapshot.GlobalResources.Lights.PhysicalRecords.Length;
        words[11] = frame.TileCapacity;
        words[16] = cohort; words[17] = (uint)frame.CohortCount; words[18] = frame.TileCapacity;
        words[19] = (uint)frame.Cohorts[cohort]!.PairCount;
        Span<ulong> hashes = MemoryMarshal.Cast<uint, ulong>(words.Slice(20, 8));
        hashes[0] = DeferredHash; hashes[1] = ForwardHash; hashes[2] = MaskedHash; hashes[3] = MirrorHash;
        words[28] = MaterialBindingLayouts.OpaqueDeferred.RowWordCount;
        StandardWords.CopyTo(words[29..]);
    }

    private static uint Word(string name)
    {
        if (!MaterialBindingLayouts.OpaqueDeferred.TryGetPackedMember(name, out MaterialBindingPackedMember member) ||
            !MaterialBindingLayouts.ForwardOpaque.TryGetPackedMember(name, out MaterialBindingPackedMember forward) ||
            !MaterialBindingLayouts.MaskedForward.TryGetPackedMember(name, out MaterialBindingPackedMember masked) ||
            member.WordOffset != forward.WordOffset || member.WordOffset != masked.WordOffset ||
            member.WordCount != forward.WordCount || member.WordCount != masked.WordCount)
            throw new NotSupportedException("WebGPU.Advanced.ShadingLayoutChanged: standard material schemas no longer share native shader offsets.");
        return member.WordOffset;
    }
    private static ulong Hash(string value)
    {
        ulong hash = 14695981039346656037ul;
        foreach (char character in value) hash = unchecked((hash ^ character) * 1099511628211ul);
        return hash;
    }
}
