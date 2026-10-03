namespace XREngine.Rendering;

/// <summary>Borrowed storage layout consumed identically by authored raster and GPU instance culling.</summary>
/// <remarks>
/// Native instance_index selects the same row in both consumers. Raster applies
/// ModelMatrix * CurrentTransform * finalPreInstancePosition, and the previous equivalents
/// for temporal output. This declaration never changes the command's instance count.
/// </remarks>
public readonly record struct AuthoredMeshInstanceSource(
    string BindingName,
    XRDataBuffer Buffer,
    uint Count,
    uint StrideBytes,
    uint CurrentTransformOffsetBytes,
    uint PreviousTransformOffsetBytes,
    uint PreInstanceBoundsOffsetBytes,
    bool AcceptsSharedDeformedGeometry = false)
{
    /// <summary>Raster ABI with engine row-vector matrix bytes interpreted as WGSL columns.</summary>
    public const string RasterSchemaIdentity = "xrengine.engine.authored-instances.v1";
    /// <summary>The same matrix ABI, with previous instance/model/view transforms used for temporal output.</summary>
    public const string TemporalSchemaIdentity = "xrengine.engine.authored-instances-temporal.v1";

    /// <summary>Checks complete records without reading matrix, bounds, or visibility contents.</summary>
    public bool HasValidLayout
        => !string.IsNullOrWhiteSpace(BindingName) && Buffer is { IsDestroyed: false } &&
           Buffer.Target == Data.Rendering.EBufferTarget.ShaderStorageBuffer &&
           string.Equals(Buffer.AttributeName, BindingName, StringComparison.Ordinal) &&
           Count != 0 && StrideBytes >= 144 && (StrideBytes | CurrentTransformOffsetBytes |
               PreviousTransformOffsetBytes | PreInstanceBoundsOffsetBytes | Buffer.Length) % 16 == 0 &&
           CurrentTransformOffsetBytes <= StrideBytes - 64 && PreviousTransformOffsetBytes <= StrideBytes - 64 &&
           PreInstanceBoundsOffsetBytes <= StrideBytes - 16 &&
           Disjoint(CurrentTransformOffsetBytes, 64, PreviousTransformOffsetBytes, 64) &&
           Disjoint(CurrentTransformOffsetBytes, 64, PreInstanceBoundsOffsetBytes, 16) &&
           Disjoint(PreviousTransformOffsetBytes, 64, PreInstanceBoundsOffsetBytes, 16) &&
           Count <= Buffer.Length / StrideBytes;

    private static bool Disjoint(uint a, uint aSize, uint b, uint bSize)
        => a < b ? b - a >= aSize : a - b >= bSize;
}
