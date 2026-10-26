using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.Editor;

public partial class AnimationPanel
{
    private NativeAnimationCandidateView? _pendingCandidateView;

    private NativeAnimationCandidateView CaptureCandidateView()
    {
        if (_pendingCandidateView is { } pending && pending.Query == _pathSearch &&
            pending.Page == _candidatePage && _selectedCandidate is null)
            return pending;
        SceneNode? root = Selection.SceneNode;
        bool current = root is not null && ReferenceEquals(_candidateRoot, root);
        return new(root, root is null ? null : ObjectConstructionIdentity.Capture(root),
            _pathSearch, current ? _candidatePage : 0, current ? _selectedCandidate?.Id : null);
    }

    private void StageCandidateView(NativeAnimationCandidateView view)
    {
        SetField(ref _pathSearch, view.Query, publishNotifications: false);
        SetField(ref _candidatePage, view.Page, publishNotifications: false);
        SetField(ref _selectedCandidate, null, publishNotifications: false);
        SetField(ref _pendingCandidateView, view, publishNotifications: false);
    }

    private void SetCandidatePage(int page)
    {
        SetField(ref _candidatePage, page, publishNotifications: false);
        if (_pendingCandidateView is { } pending)
            SetField(ref _pendingCandidateView, pending with { Page = page }, publishNotifications: false);
    }

    private bool RebuildCandidates()
    {
        SceneNode? root = Selection.SceneNode;
        ObjectConstructionIdentity? rootIdentity = root is null ? null : ObjectConstructionIdentity.Capture(root);
        ObjectConstructionIdentity panelIdentity = ObjectConstructionIdentity.Capture(this);
        ulong epoch = _authoringEpoch;
        NativeAnimationCandidateView? pending = _pendingCandidateView;
        IReadOnlyList<EditorImGuiUI.AnimationClipPathCandidate> candidates = root is null
            ? [] : EditorImGuiUI.DiscoverAnimationClipPaths(root);
        if (_destroying || IsDestroyed || IsDestroyQueued || !panelIdentity.IsCurrent ||
            _authoringEpoch != epoch || !ReferenceEquals(Selection.SceneNode, root) ||
            rootIdentity is { } identity && (!identity.IsCurrent || root!.IsDestroyed || root.IsDestroyQueued))
        {
            SetField(ref _projectionDirty, true, publishNotifications: false);
            return false;
        }
        int page = 0;
        EditorImGuiUI.AnimationClipPathCandidate? selected = null;
        if (pending is { } view && _pendingCandidateView == pending &&
            view.IsCurrent && ReferenceEquals(view.Root, root) && view.Query == _pathSearch)
            (page, selected) = ResolveCandidateView(view, candidates, RowPageSize);
        SetField(ref _candidateRoot, root, publishNotifications: false);
        SetField(ref _candidates, candidates, publishNotifications: false);
        SetField(ref _selectedCandidate, selected, publishNotifications: false);
        SetField(ref _candidatePage, page, publishNotifications: false);
        SetField(ref _pendingCandidateView, null, publishNotifications: false);
        _ids.Clear();
        return true;
    }

    internal static (int Page, EditorImGuiUI.AnimationClipPathCandidate? Selected) ResolveCandidateView(
        NativeAnimationCandidateView view, IReadOnlyList<EditorImGuiUI.AnimationClipPathCandidate> candidates, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        int count = 0;
        EditorImGuiUI.AnimationClipPathCandidate? selected = null;
        foreach (var candidate in candidates)
        {
            if (candidate.Id == view.SelectedId) selected = candidate;
            if (view.Query.Length == 0 || candidate.DisplayPath.Contains(view.Query, StringComparison.OrdinalIgnoreCase))
                count++;
        }
        return (Math.Clamp(view.Page, 0, Math.Max(0, (count - 1) / pageSize)), selected);
    }
}
