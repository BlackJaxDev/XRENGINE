namespace XREngine.Components.Physics;

/// <summary>
/// Groups convex-hull inputs from one authored mesh source.
/// </summary>
internal readonly record struct ConvexHullInputBatch(
    ConvexHullInputSource Source,
    List<ConvexHullInput> Inputs,
    int SourceMeshCount)
{
    public string SourceLabel => Source switch
    {
        ConvexHullInputSource.RuntimeMeshes => "runtime render meshes",
        ConvexHullInputSource.AssetMeshes => "asset submeshes",
        _ => "collision meshes",
    };

    public int InputCount => Inputs.Count;
    public int VertexCount => Inputs.Sum(static input => input.Positions.Length);
    public int TriangleCount => Inputs.Sum(static input => input.Indices.Length / 3);
}
