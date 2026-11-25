using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Retains a committed query and root-bound candidate position for one live transfer.</summary>
internal readonly record struct NativeAnimationCandidateView(SceneNode? Root,
    ObjectConstructionIdentity? RootIdentity, string Query, int Page, string? SelectedId)
{
    internal bool IsCurrent => Query is not null && Page >= 0 &&
        (Root is null ? RootIdentity is null && Page == 0 && SelectedId is null :
            RootIdentity is { IsCurrent: true } identity && ReferenceEquals(identity.Target, Root) &&
            !Root.IsDestroyed && !Root.IsDestroyQueued);

}
