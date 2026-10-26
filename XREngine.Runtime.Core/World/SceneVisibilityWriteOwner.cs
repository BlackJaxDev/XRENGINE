using XREngine.Data.Core;

namespace XREngine.Scene;

/// <summary>Checks the lifetime and latest intent of one scene visibility write.</summary>
internal readonly record struct SceneVisibilityWriteOwner(
    XRScene Scene, ObjectConstructionIdentity Identity, ulong Intent, bool Destroyed, bool Queued)
{
    internal void RequireCurrent()
    {
        if (!Identity.IsCurrent || Scene.IsDestroyed != Destroyed || Scene.IsDestroyQueued != Queued ||
            Scene.VisibilityWriteIntentRevision != Intent)
            throw new InvalidOperationException("The scene visibility lifetime or intent changed during assignment.");
    }
}
