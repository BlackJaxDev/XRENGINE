using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exact publisher identity and generation state retained by one persistent
/// program-binding artifact. Arrays are created only when an artifact is built;
/// stable lookups compare in place without allocating.
/// </summary>
internal sealed class RenderBindingPublisherGenerationSnapshot
{
    private readonly PublisherState[] _materialPublishers;
    private readonly PublisherState[] _meshPublishers;

    private RenderBindingPublisherGenerationSnapshot(
        PublisherState[] materialPublishers,
        PublisherState[] meshPublishers)
    {
        _materialPublishers = materialPublishers;
        _meshPublishers = meshPublishers;
    }

    internal static RenderBindingPublisherGenerationSnapshot Capture(
        IRenderBindingPublisher[] materialPublishers,
        IRenderBindingPublisher[] meshPublishers)
        => new(
            Capture(materialPublishers),
            Capture(meshPublishers));

    internal bool Matches(
        IRenderBindingPublisher[] materialPublishers,
        IRenderBindingPublisher[] meshPublishers)
        => Matches(_materialPublishers, materialPublishers) &&
            Matches(_meshPublishers, meshPublishers);

    private static PublisherState[] Capture(
        IRenderBindingPublisher[] publishers)
    {
        if (publishers.Length == 0)
            return [];

        PublisherState[] states = new PublisherState[publishers.Length];
        for (int index = 0; index < publishers.Length; index++)
            states[index] = PublisherState.Capture(publishers[index]);
        return states;
    }

    private static bool Matches(
        PublisherState[] expected,
        IRenderBindingPublisher[] publishers)
    {
        if (expected.Length != publishers.Length)
            return false;

        for (int index = 0; index < publishers.Length; index++)
            if (!expected[index].Matches(publishers[index]))
                return false;
        return true;
    }

    private readonly record struct PublisherState(
        IRenderBindingPublisher Publisher,
        ERenderBindingFrequency Frequency,
        ulong Generation,
        ulong ResourceGeneration,
        bool PublishesResources,
        EUniformRequirements OwnedPersistentArtifactRequirement,
        bool OwnsPersistentArtifactRequirement)
    {
        internal static PublisherState Capture(
            IRenderBindingPublisher publisher)
        {
            bool publishesResources =
                publisher is IRenderResourceBindingPublisher;
            ulong resourceGeneration = publishesResources
                ? ((IRenderResourceBindingPublisher)publisher)
                    .ResourceGeneration
                : 0UL;
            bool ownsPersistentArtifactRequirement = publisher is
                IPersistentProgramBindingRequirementOwner;
            EUniformRequirements ownedPersistentArtifactRequirement =
                ownsPersistentArtifactRequirement
                    ? ((IPersistentProgramBindingRequirementOwner)publisher)
                        .OwnedPersistentArtifactRequirement
                    : EUniformRequirements.None;
            return new PublisherState(
                publisher,
                publisher.Frequency,
                publisher.Generation,
                resourceGeneration,
                publishesResources,
                ownedPersistentArtifactRequirement,
                ownsPersistentArtifactRequirement);
        }

        internal bool Matches(IRenderBindingPublisher publisher)
        {
            if (!ReferenceEquals(Publisher, publisher) ||
                Frequency != publisher.Frequency ||
                Generation != publisher.Generation)
            {
                return false;
            }

            if (publisher is
                IPersistentProgramBindingRequirementOwner requirementOwner)
            {
                if (!OwnsPersistentArtifactRequirement ||
                    OwnedPersistentArtifactRequirement !=
                        requirementOwner.OwnedPersistentArtifactRequirement)
                {
                    return false;
                }
            }
            else if (OwnsPersistentArtifactRequirement ||
                     OwnedPersistentArtifactRequirement !=
                         EUniformRequirements.None)
            {
                return false;
            }

            if (publisher is IRenderResourceBindingPublisher resourcePublisher)
                return PublishesResources &&
                    ResourceGeneration == resourcePublisher.ResourceGeneration;

            return !PublishesResources && ResourceGeneration == 0UL;
        }
    }
}
