namespace XREngine.Components;

/// <summary>
/// Authored relevance band used only when a chain permits automatic quality
/// changes. Fixed quality tiers ignore this value.
/// </summary>
public enum PhysicsChainAutomaticRelevance
{
    Important,
    Relevant,
    Distant,
    Irrelevant,
}
