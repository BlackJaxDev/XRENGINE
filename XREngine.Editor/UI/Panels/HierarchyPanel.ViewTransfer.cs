using System.Numerics;
using XREngine.Data.Core;
using XREngine.Editor.UI;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public partial class HierarchyPanel
{
    private ulong _hierarchyViewIntent;
    private Action? _requireHierarchyViewRestore;

    internal ulong HierarchyViewIntent => _hierarchyViewIntent;

    private void BeginHierarchyViewEdit()
    {
        checked { _hierarchyViewIntent++; }
    }

    internal bool HasHierarchyViewDocument(NativeHierarchyPanelViewValues values)
        => !_hierarchyDestroying && values.HasCurrentDocument &&
            ReferenceEquals(TryGetActiveWorld(), values.World);

    private float EffectiveHierarchyScroll => _treeListTransform is { } list &&
        NativePanelScrollOwnerReceipt.Capture(list, Transform) is not null
            ? list.ContentScrollOffset : _scrollOffset;

    private void RetainHierarchyEffectiveScroll()
    {
        float scroll = EffectiveHierarchyScroll;
        if (float.IsFinite(scroll)) _scrollOffset = MathF.Max(0, scroll);
    }

    internal bool TryCaptureHierarchyView(SceneNode slot, SceneNode hostRoot,
        bool allowInactive, out NativeHierarchyPanelViewReceipt? receipt)
    {
        receipt = null;
        if (_hierarchyDestroying || IsDestroyed || IsDestroyQueued ||
            !ReferenceEquals(SceneNode.Parent, slot) ||
            !allowInactive && (!CanUseHierarchy || !IsActiveInHierarchy) ||
            TryGetActiveWorld() is not { } world || _requireHierarchyViewRestore is not null)
            return false;
        NativePanelScrollOwnerReceipt? scroll = _treeListTransform is { } list
            ? NativePanelScrollOwnerReceipt.Capture(list, Transform) : null;
        if (_treeListTransform is not null && (scroll is null ||
                _scrollbarTrackTransform is null || _scrollbarThumbTransform is null) ||
            !allowInactive && (_virtualTree is null || scroll is null)) return false;
        XRWorld? document = world.TargetWorld;
        NativeHierarchyPanelViewValues values = new(world, document,
            document is null ? null : ObjectConstructionIdentity.Capture(document),
            [.. _collapsedNodes], [.. _collapsedSections], EffectiveHierarchyScroll, _showEditorScene);
        try
        {
            receipt = new(this, slot, hostRoot, values, _hierarchyViewIntent,
                _hierarchyRevision, scroll, scroll?.List.ContentScrollIntentRevision ?? 0,
                _virtualTree, _virtualTree?.Revision ?? 0,
                _treeListTransform?.VirtualProviderIncarnation ?? 0, CaptureHierarchyScrollbarAdmission());
            return receipt.IsCurrent;
        }
        catch (InvalidOperationException) { receipt = null; return false; }
    }

    internal bool IsHierarchyViewCurrent(NativeHierarchyPanelViewReceipt receipt)
        => _hierarchyRevision == receipt.Lifecycle && HasHierarchyViewDocument(receipt.Values) &&
            _collapsedNodes.SetEquals(receipt.Values.CollapsedNodes) &&
            _collapsedSections.SetEquals(receipt.Values.CollapsedSections) &&
            _showEditorScene == receipt.Values.ShowEditorScene &&
            EffectiveHierarchyScroll == receipt.Values.ScrollOffset &&
            ReferenceEquals(_virtualTree, receipt.Provider) &&
            (_virtualTree?.Revision ?? 0) == receipt.ProviderRevision &&
            (receipt.Scroll is { } scroll
                ? _treeListTransform is { } list && scroll.IsCurrent(list, Transform) &&
                    list.ContentScrollIntentRevision == receipt.ScrollIntent &&
                    list.VirtualProviderIncarnation == receipt.ProviderIncarnation &&
                    ReferenceEquals(list.VirtualProvider, receipt.Provider)
                : _treeListTransform is null);

    internal NativeHierarchyPanelViewReceipt RestoreHierarchyView(
        NativeHierarchyPanelViewValues values, SceneNode slot, SceneNode hostRoot,
        Func<bool> requireSourceCurrent,
        Action<NativeHierarchyPanelViewReceipt>? retainPartialCommit = null)
    {
        if (_requireHierarchyViewRestore is not null || _remakingHierarchy || _releasingHierarchy ||
            _activatingHierarchy || !CanUseHierarchy || !IsActiveInHierarchy ||
            !HasHierarchyViewDocument(values) ||
            !TryCaptureHierarchyView(slot, hostRoot, false, out var before) || before is null ||
            !requireSourceCurrent() || !before.IsCurrent)
            throw new InvalidOperationException("The hierarchy view cannot begin restore.");
        BeginHierarchyViewEdit();
        ulong intent = _hierarchyViewIntent;
        uint lifecycle = _hierarchyRevision;
        var path = EditorVirtualRowControlLease.Capture(this, hostRoot);
        float expectedScroll = values.ScrollOffset;
        NativePanelScrollOwnerReceipt originalScroll = before.Scroll!.Value;
        UIListTransform originalList = originalScroll.List;
        ulong expectedScrollIntent = originalList.ContentScrollIntentRevision;
        UIBoundableTransform track = _scrollbarTrackTransform
            ?? throw new InvalidOperationException("The hierarchy scrollbar track is unavailable.");
        UIBoundableTransform thumb = _scrollbarThumbTransform
            ?? throw new InvalidOperationException("The hierarchy scrollbar thumb is unavailable.");
        Func<bool> trackCurrent = CaptureHierarchyViewTransform(track);
        Func<bool> thumbCurrent = CaptureHierarchyViewTransform(thumb);
        ulong expectedTrackVisibility = track.VisibilityWriteIntentRevision;
        ulong expectedThumbVisibility = thumb.VisibilityWriteIntentRevision;
        ulong expectedThumbHeight = thumb.HeightWriteIntentRevision;
        ulong expectedThumbTranslation = thumb.TranslationWriteIntentRevision;
        bool assigned = false;
        void RequireCurrent()
        {
            bool LocalCurrent() => _hierarchyViewIntent == intent &&
                _hierarchyRevision == lifecycle && CanUseHierarchy && IsActiveInHierarchy &&
                HasHierarchyViewDocument(values) && path.IsCurrent(hostRoot) &&
                ReferenceEquals(SceneNode.Parent, slot) &&
                ReferenceEquals(_treeListTransform, originalList) &&
                originalScroll.IsCurrent(originalList, Transform) &&
                originalList.ContentScrollIntentRevision == expectedScrollIntent &&
                originalList.VirtualProviderIncarnation == before.ProviderIncarnation &&
                ReferenceEquals(originalList.VirtualProvider, before.Provider) &&
                ReferenceEquals(_scrollbarTrackTransform, track) && trackCurrent() &&
                ReferenceEquals(_scrollbarThumbTransform, thumb) && thumbCurrent() &&
                track.VisibilityWriteIntentRevision == expectedTrackVisibility &&
                thumb.VisibilityWriteIntentRevision == expectedThumbVisibility &&
                thumb.HeightWriteIntentRevision == expectedThumbHeight &&
                thumb.TranslationWriteIntentRevision == expectedThumbTranslation &&
                (!assigned || _collapsedNodes.SetEquals(values.CollapsedNodes) &&
                    _collapsedSections.SetEquals(values.CollapsedSections) &&
                    _showEditorScene == values.ShowEditorScene && _scrollOffset == expectedScroll);
            if (!LocalCurrent() || !requireSourceCurrent() || !LocalCurrent())
                throw new InvalidOperationException("The hierarchy view changed during restore.");
        }
        _requireHierarchyViewRestore = RequireCurrent;
        try
        {
            RequireCurrent();
            _collapsedNodes.Clear();
            _collapsedNodes.UnionWith(values.CollapsedNodes);
            _collapsedSections.Clear();
            _collapsedSections.UnionWith(values.CollapsedSections);
            _showEditorScene = values.ShowEditorScene;
            _scrollOffset = expectedScroll;
            assigned = true;
            RemakeChildren();
            RequireCurrent();
            if (_treeListTransform is not { } list ||
                NativePanelScrollOwnerReceipt.Capture(list, Transform) is not { } scroll)
                throw new InvalidOperationException("The restored hierarchy has no owned scroll list.");
            object? provider = list.VirtualProvider;
            ulong incarnation = list.VirtualProviderIncarnation;
            void RequireScroll()
            {
                RequireCurrent();
                if (!ReferenceEquals(_treeListTransform, list) || !scroll.IsCurrent(list, Transform) ||
                    !ReferenceEquals(list.VirtualProvider, provider) || list.VirtualProviderIncarnation != incarnation)
                    throw new InvalidOperationException("The hierarchy scroll owner changed during restore.");
            }
            expectedScroll = Math.Clamp(expectedScroll, 0, GetMaxScrollOffset());
            _scrollOffset = expectedScroll;
            checked { expectedScrollIntent++; }
            list.SetContentScrollOffsetGuarded(expectedScroll, (Action)RequireScroll, static require => require());
            RequireScroll();
            if (list.ContentScrollOffset != expectedScroll)
                throw new InvalidOperationException("The hierarchy scroll restore was rejected.");
            ulong scrollIntent = list.ContentScrollIntentRevision;
            void RequireFinalScroll()
            {
                RequireScroll();
                if (list.ContentScrollIntentRevision != scrollIntent || list.ContentScrollOffset != expectedScroll)
                    throw new InvalidOperationException("A newer hierarchy scroll request replaced the restored view.");
            }
            list.SetVirtualBoundsGuarded(GetViewportHeight(), 0, (Action)RequireFinalScroll,
                static require => require());
            RequireFinalScroll();
            float viewport = GetViewportHeight();
            float maxOffset = GetMaxScrollOffset(viewport);
            float contentHeight = GetContentHeight();
            float trackHeight = track.ActualHeight > 0 ? track.ActualHeight : viewport;
            bool canScroll = maxOffset > 0 && contentHeight > 0;
            checked { expectedTrackVisibility++; }
            track.SetVisibilityGuarded(canScroll ? EVisibility.Visible : EVisibility.Hidden,
                (Action)RequireFinalScroll, static require => require());
            RequireFinalScroll();
            checked { expectedThumbVisibility++; }
            thumb.SetVisibilityGuarded(canScroll ? EVisibility.Visible : EVisibility.Hidden,
                (Action)RequireFinalScroll, static require => require());
            RequireFinalScroll();
            if (canScroll)
            {
                float height = MathF.Max(MinScrollbarThumbHeight, viewport / contentHeight * trackHeight);
                float travel = MathF.Max(0, trackHeight - height);
                float ratio = expectedScroll / maxOffset;
                checked { expectedThumbHeight++; }
                thumb.SetHeightGuarded(height, (Action)RequireFinalScroll, static require => require());
                RequireFinalScroll();
                checked { expectedThumbTranslation++; }
                thumb.SetTranslationGuarded(new Vector2(0, -travel * ratio),
                    (Action)RequireFinalScroll, static require => require());
                RequireFinalScroll();
            }
            _requireHierarchyViewRestore = null;
            if (!TryCaptureHierarchyView(slot, hostRoot, false, out var applied) || applied is null ||
                !requireSourceCurrent() || !applied.IsCurrent)
                throw new InvalidOperationException("The restored hierarchy view lost its owner.");
            return applied;
        }
        catch
        {
            // A post-change observer can throw after a field has committed. Retain
            // only our exact progress; a newer intent or owner cancels compensation.
            try
            {
                RequireCurrent();
                if (originalList.ContentScrollOffset != expectedScroll &&
                    originalList.ContentScrollOffset != before.Values.ScrollOffset)
                    throw new InvalidOperationException("The partial hierarchy scroll changed.");
                _requireHierarchyViewRestore = null;
                if (TryCaptureHierarchyView(slot, hostRoot, false, out var partial) && partial is not null &&
                    requireSourceCurrent() && partial.IsCurrent)
                    retainPartialCommit?.Invoke(partial);
            }
            catch
            {
                // Preserve the original restore failure and leave newer state untouched.
            }
            throw;
        }
        finally { _requireHierarchyViewRestore = null; }
    }

    private Func<bool> CaptureHierarchyScrollbarAdmission()
    {
        UIBoundableTransform? track = _scrollbarTrackTransform;
        UIBoundableTransform? thumb = _scrollbarThumbTransform;
        if (track is null && thumb is null)
            return () => _scrollbarTrackTransform is null && _scrollbarThumbTransform is null;
        if (track is null || thumb is null) return static () => false;
        Func<bool> trackCurrent = CaptureHierarchyViewTransform(track);
        Func<bool> thumbCurrent = CaptureHierarchyViewTransform(thumb);
        ulong trackVisibility = track.VisibilityWriteIntentRevision;
        ulong thumbVisibility = thumb.VisibilityWriteIntentRevision;
        ulong height = thumb.HeightWriteIntentRevision;
        ulong translation = thumb.TranslationWriteIntentRevision;
        return () => ReferenceEquals(_scrollbarTrackTransform, track) &&
            ReferenceEquals(_scrollbarThumbTransform, thumb) && trackCurrent() && thumbCurrent() &&
            track.VisibilityWriteIntentRevision == trackVisibility &&
            thumb.VisibilityWriteIntentRevision == thumbVisibility &&
            thumb.HeightWriteIntentRevision == height && thumb.TranslationWriteIntentRevision == translation;
    }

    private Func<bool> CaptureHierarchyViewTransform(UIBoundableTransform? value)
    {
        if (value is null) return static () => false;
        List<TransformBase> path = [];
        for (TransformBase? current = value; current is not null; current = current.Parent)
        {
            path.Add(current);
            if (ReferenceEquals(current, Transform)) break;
        }
        TransformBase[] transforms = [.. path];
        ObjectConstructionIdentity[] identities = transforms.Select(ObjectConstructionIdentity.Capture).ToArray();
        SceneNode?[] nodes = transforms.Select(transform => transform.SceneNode).ToArray();
        ObjectConstructionIdentity?[] nodeIdentities = nodes.Select(node => node is null
            ? (ObjectConstructionIdentity?)null : ObjectConstructionIdentity.Capture(node)).ToArray();
        return () =>
        {
            if (transforms.Length == 0 || !ReferenceEquals(transforms[^1], Transform)) return false;
            for (int index = 0; index < transforms.Length; index++)
            {
                TransformBase transform = transforms[index];
                if (!identities[index].IsCurrent || transform.IsDestroyed || transform.IsDestroyQueued ||
                    !ReferenceEquals(transform.SceneNode, nodes[index]) ||
                    nodes[index] is { } node && (nodeIdentities[index]?.IsCurrent != true ||
                        node.IsDestroyed || node.IsDestroyQueued || !ReferenceEquals(node.Transform, transform)) ||
                    index + 1 < transforms.Length && !ReferenceEquals(transform.Parent, transforms[index + 1]))
                    return false;
            }
            return true;
        };
    }
}
