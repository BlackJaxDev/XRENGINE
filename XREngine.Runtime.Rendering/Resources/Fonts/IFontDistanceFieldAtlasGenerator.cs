namespace XREngine.Rendering;

/// <summary>Generates the image and metadata consumed by the font importer.</summary>
public interface IFontDistanceFieldAtlasGenerator
{
    FontDistanceFieldAtlasResult Generate(in FontDistanceFieldAtlasRequest request);
}
