namespace XREngine.Rendering.Models.Caching;

/// <summary>Stable identity of one cooked model/submesh/LOD mesh payload.</summary>
internal readonly record struct ModelBinaryMeshletSectionKey(string ModelIdentity, uint SubMeshIndex, uint LodIndex)
    : IComparable<ModelBinaryMeshletSectionKey>
{
    public int CompareTo(ModelBinaryMeshletSectionKey other)
    {
        int comparison = StringComparer.Ordinal.Compare(ModelIdentity, other.ModelIdentity);
        if (comparison != 0)
            return comparison;

        comparison = SubMeshIndex.CompareTo(other.SubMeshIndex);
        return comparison != 0 ? comparison : LodIndex.CompareTo(other.LodIndex);
    }
}
