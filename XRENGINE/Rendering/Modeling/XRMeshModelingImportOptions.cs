namespace XREngine.Rendering.Modeling;

public sealed class XRMeshModelingImportOptions
{
    public bool ImportNormals { get; init; } = true;
    public bool ImportTangents { get; init; } = true;
    public bool ImportTexCoordChannels { get; init; } = true;
    public bool ImportColorChannels { get; init; } = true;
    public bool ImportSkinning { get; init; } = true;
    public bool ImportBlendshapeChannels { get; init; } = true;
}
