using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>Authorizes one exact internal shader-list replacement.</summary>
internal sealed record UberMaterialShaderTransition(
    EventList<XRShader> Previous, EventList<XRShader> Replacement,
    XRShader[] PreviousStages, XRShader[] ReplacementStages,
    XRShader PreviousFragment, XRShader NextFragment,
    long ShaderRevision, long UberRevision, EngineMaterialSemanticIdentity Semantic)
{
    internal bool Matches(XRMaterial material, object? previous, object? replacement)
        => ReferenceEquals(previous, Previous) && ReferenceEquals(replacement, Replacement) &&
            MatchesState(material, ShaderRevision);

    internal bool MatchesAfterAdoption(XRMaterial material)
        => MatchesState(material, ShaderRevision == long.MaxValue ? 1 : ShaderRevision + 1);

    private bool MatchesState(XRMaterial material, long expectedRevision)
        => ReferenceEquals(material.Shaders, Replacement) && material.ShaderStateRevision == expectedRevision &&
            material.UberStateRevision == UberRevision && material.EngineSemantic == Semantic &&
            MatchesStages(Previous, PreviousStages) && MatchesStages(Replacement, ReplacementStages) &&
            UberShaderVariantBuilder.IsCompatibleInternalReplacement(PreviousFragment, NextFragment);

    private static bool MatchesStages(EventList<XRShader> shaders, XRShader[] expected)
    {
        if (shaders.Count != expected.Length)
            return false;
        for (int i = 0; i < expected.Length; i++)
            if (!ReferenceEquals(shaders[i], expected[i]))
                return false;
        return true;
    }
}
