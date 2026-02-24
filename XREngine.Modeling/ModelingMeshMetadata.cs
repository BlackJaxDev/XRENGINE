namespace XREngine.Modeling;

public enum ModelingPrimitiveType
{
    Unknown = 0,
    Triangles,
    Lines,
    Points
}

public sealed class ModelingMeshMetadata
{
    public ModelingPrimitiveType SourcePrimitiveType { get; set; } = ModelingPrimitiveType.Unknown;
    public bool SourceInterleaved { get; set; }
    public int SourceColorChannelCount { get; set; }
    public int SourceTexCoordChannelCount { get; set; }
    public bool HasSkinning { get; set; }
    public bool HasBlendshapes { get; set; }

    public ModelingMeshMetadata Clone()
    {
        return new ModelingMeshMetadata
        {
            SourcePrimitiveType = SourcePrimitiveType,
            SourceInterleaved = SourceInterleaved,
            SourceColorChannelCount = SourceColorChannelCount,
            SourceTexCoordChannelCount = SourceTexCoordChannelCount,
            HasSkinning = HasSkinning,
            HasBlendshapes = HasBlendshapes
        };
    }
}
