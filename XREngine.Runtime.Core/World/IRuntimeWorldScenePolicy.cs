using XREngine.Scene;

namespace XREngine;

/// <summary>
/// Host-provided policy for scene roots that Core must not understand, such as
/// editor-only hidden scenes.  The policy never owns node world identity.
/// </summary>
public interface IRuntimeWorldScenePolicy
{
    /// <summary>
    /// Gives a host the opportunity to own a root before Core adds it to the
    /// visible runtime root set.  Returning true means the host attached it.
    /// </summary>
    bool TryAttachSceneRoot(RuntimeWorld world, XRScene scene, SceneNode root);

    /// <summary>
    /// Gives a host the opportunity to remove a root it previously attached.
    /// Returning true means the host detached it.
    /// </summary>
    bool TryDetachSceneRoot(RuntimeWorld world, XRScene scene, SceneNode root);

    /// <summary>Returns whether a root participates in the current play lifecycle.</summary>
    bool ShouldParticipateInPlay(RuntimeWorld world, SceneNode root);

    /// <summary>Notifies the host before Core removes a destroyed root from its context.</summary>
    void OnRootNodeDestroying(RuntimeWorld world, SceneNode root);
}
