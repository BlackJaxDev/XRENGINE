using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

/// <summary>
/// Explicitly owns one exact engine-uniform requirement for a persistent
/// program-binding artifact.
/// </summary>
/// <remarks>
/// Implementations must keep this declaration immutable. Their
/// <see cref="IRenderBindingPublisher.Generation"/> must advance whenever any
/// non-material value published for the requirement changes. Resource
/// publishers must additionally advance
/// <see cref="IRenderResourceBindingPublisher.ResourceGeneration"/> whenever
/// one of their explicitly published descriptor resources changes.
/// </remarks>
public interface IPersistentProgramBindingRequirementOwner :
    IRenderBindingPublisher
{
    /// <summary>
    /// Gets the single exact requirement owned by this publisher. Masks,
    /// supersets, and <see cref="EUniformRequirements.None"/> are invalid.
    /// </summary>
    EUniformRequirements OwnedPersistentArtifactRequirement { get; }
}
