namespace XREngine.Rendering.Compute;

/// <summary>
/// Identifies the collider rows that the collider input catalog holds for one request: the arena
/// offset, the arena allocation generation, and the content signature. The default value is not
/// valid, so a cleared stamp makes the next stage copy the rows again.
/// </summary>
internal readonly record struct PhysicsChainColliderCatalogStamp(
    int Offset,
    int ArenaGeneration,
    int Signature,
    bool Valid);
