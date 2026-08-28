namespace XREngine.Rendering.Modeling;

public sealed class XRMeshToModelingDocumentOptions
{
    public bool IncludeNormals { get; init; } = true;
    public bool IncludeTangents { get; init; } = true;
    public bool IncludeTexCoordChannels { get; init; } = true;
    public bool IncludeColorChannels { get; init; } = true;
    public bool IncludeSkinning { get; init; } = true;
    public bool IncludeBlendshapeChannels { get; init; } = true;
}
