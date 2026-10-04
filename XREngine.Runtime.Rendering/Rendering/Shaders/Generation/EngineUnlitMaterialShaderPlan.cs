namespace XREngine.Rendering.Shaders.Generation;

/// <summary>Canonical source and surface selection for one cooked ordinary unlit material.</summary>
public readonly record struct EngineUnlitMaterialShaderPlan(string Name, EngineMaterialSemanticIdentity Semantic,
    string SemanticSchemaIdentity, string SlangSource, string Surface, bool UsesTexture, bool UsesTextureArray,
    bool UsesAlphaCutoff, bool UsesSortedOrder)
{
    public string Pass => EngineUnlitMaterialShaderGenerator.Pass;
}
