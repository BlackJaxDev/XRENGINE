namespace XREngine.Cutscenes;

/// <summary>
/// The kind of a cutscene residency asset. The kind selects the residency backend that loads the asset.
/// </summary>
public enum EResidencyAssetKind
{
    /// <summary>A texture. <see cref="TextureResidencyBackend"/> loads its levels.</summary>
    Texture = 0,

    /// <summary>A sub-mesh with its LOD table. No mesh backend exists yet.</summary>
    Mesh = 1,

    /// <summary>An audio clip. No audio backend exists yet.</summary>
    Audio = 2,
}
