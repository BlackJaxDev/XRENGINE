using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.Shaders.Generation;

/// <summary>Retains the exact source and alternate program identities proven equivalent for ordered direct replay.</summary>
public readonly record struct EngineAuthoredOrderGateBinding(
    string SourceArtifactIdentity,
    string GateArtifactIdentity,
    EngineMaterialSemanticIdentity SourceSemantic,
    EngineMaterialVariantKey GateVariant);
