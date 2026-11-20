namespace XREngine.Scene.Importers.SourceToon;

/// <summary>
/// Identifies one major and minor release line of the source toon shader.
/// </summary>
/// <param name="Major">The major version of the line.</param>
/// <param name="Minor">The minor version of the line.</param>
/// <param name="ShaderGuid">The asset GUID of the main toon shader of the line. All releases of a line keep this GUID.</param>
/// <param name="LastRelease">The last published release of the line.</param>
/// <param name="BehaviorVersion">
/// The verified version whose conversion rules apply to all releases of the line. Use the verified version that is
/// nearest in property names and feature behavior.
/// </param>
public sealed record SourceToonReleaseLine(
    int Major,
    int Minor,
    string ShaderGuid,
    SourceToonShaderVersion LastRelease,
    SourceToonShaderVersion BehaviorVersion)
{
    /// <summary>Returns true when the version is a release of this line.</summary>
    public bool Contains(SourceToonShaderVersion version)
        => version.Major == Major && version.Minor == Minor;

    /// <inheritdoc />
    public override string ToString()
        => $"{Major}.{Minor}";
}
