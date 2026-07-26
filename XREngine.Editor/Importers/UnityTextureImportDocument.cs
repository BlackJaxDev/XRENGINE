namespace XREngine.Scene.Importers;

/// <summary>
/// Rendering-relevant settings preserved from a Unity TextureImporter metadata document.
/// Numeric sampler values retain Unity's serialized enum values.
/// </summary>
public sealed record UnityTextureImportDocument
{
    public string? SourcePath { get; init; }
    public string RawYaml { get; init; } = string.Empty;
    public int? SerializedVersion { get; init; }
    public bool IsSrgb { get; init; } = true;
    public int TextureType { get; init; }
    public bool IsNormalMap { get; init; }
    public bool FlipGreenChannel { get; init; }
    public int NormalMapChannel { get; init; }
    public int AlphaSource { get; init; }
    public bool AlphaIsTransparency { get; init; }
    public int WrapU { get; init; }
    public int WrapV { get; init; }
    public int WrapW { get; init; }
    public int FilterMode { get; init; } = 1;
    public bool GenerateMipMaps { get; init; } = true;
    public float MipBias { get; init; }
    public int Anisotropy { get; init; } = 1;
    public int RawShape { get; init; } = 1;
    public UnityTextureShape Shape { get; init; } = UnityTextureShape.Texture2D;
    public Dictionary<string, string> UnknownSerializedFields { get; init; } = new(StringComparer.Ordinal);

    public string SerializeDiagnosticRoundTrip()
        => RawYaml;
}
