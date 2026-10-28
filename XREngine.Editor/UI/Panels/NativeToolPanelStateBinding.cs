using XREngine.Data.Core;
using XREngine.Editor.UI.Components;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Owns one panel's state callbacks and guarded restores.</summary>
internal sealed class NativeToolPanelStateBinding
{
    private readonly ObjectConstructionIdentity _componentIdentity;
    private readonly ObjectConstructionIdentity _nodeIdentity;
    private readonly ObjectConstructionIdentity _transformIdentity;
    private readonly ObjectConstructionIdentity _slotIdentity;
    private readonly ObjectConstructionIdentity _slotTransformIdentity;
    private readonly Func<bool>? _ownsSlot;
    private readonly Func<bool>? _initialAdmission;
    private bool _callbacksAttached;
    private bool _initializing;
    private Func<bool>? _sourceCurrent;
    private NativeAnimationPanelViewReceipt? _restoreAnimationView;

    internal NativeToolPanelStateBinding(NativeVirtualTextPanel panel, SceneNode slot,
        NativeDockLayout layout, string panelName, Action save, Action requestSave,
        Func<bool>? ownsSlot, Func<bool>? initialAdmission)
    {
        Panel = panel;
        Slot = slot;
        Layout = layout;
        PanelName = panelName;
        Save = save;
        RequestSave = requestSave;
        _ownsSlot = ownsSlot;
        _initialAdmission = initialAdmission;
        _componentIdentity = ObjectConstructionIdentity.Capture(panel);
        _nodeIdentity = ObjectConstructionIdentity.Capture(panel.SceneNode);
        _transformIdentity = ObjectConstructionIdentity.Capture(panel.Transform);
        _slotIdentity = ObjectConstructionIdentity.Capture(slot);
        _slotTransformIdentity = ObjectConstructionIdentity.Capture(slot.Transform);
    }

    internal NativeVirtualTextPanel Panel { get; }
    internal SceneNode Slot { get; }
    internal NativeDockLayout Layout { get; }
    internal string PanelName { get; }
    internal Action Save { get; }
    internal Action RequestSave { get; }
    internal ulong RestoreIntent { get; private set; }

    internal bool IsAttached => _componentIdentity.IsCurrent && _nodeIdentity.IsCurrent &&
        _transformIdentity.IsCurrent && _slotIdentity.IsCurrent &&
        _slotTransformIdentity.IsCurrent &&
        !Panel.IsDestroyed && !Panel.IsDestroyQueued &&
        !Slot.IsDestroyed && !Slot.IsDestroyQueued &&
        ReferenceEquals(Panel.SceneNode, _nodeIdentity.Target) &&
        !Panel.SceneNode.IsDestroyed && !Panel.SceneNode.IsDestroyQueued &&
        ReferenceEquals(Panel.Transform, _transformIdentity.Target) &&
        ReferenceEquals(Slot.Transform, _slotTransformIdentity.Target) &&
        ReferenceEquals(Panel.SceneNode.Parent, Slot);

    internal bool IsCurrent => IsAttached && (_ownsSlot?.Invoke() ?? true);

    internal void RequireOwnedState(ulong intent)
    {
        if (_restoreAnimationView is { } beforeView && !beforeView.IsCurrent)
            throw new InvalidOperationException("The Animation view changed during restore.");
        if (!(_initializing ? IsAttached : IsCurrent) || RestoreIntent != intent ||
            !Panel.IsCurrentStateIntent(intent))
            throw new InvalidOperationException("The panel state owner changed during restore.");
        if (_initializing && _initialAdmission is { } admission && !admission())
            throw new InvalidOperationException("The initial panel owner changed during restore.");
        if (_sourceCurrent is { } sourceCurrent && !sourceCurrent())
            throw new InvalidOperationException("The source panel state changed during restore.");
        if (!(_initializing ? IsAttached : IsCurrent) || RestoreIntent != intent ||
            !Panel.IsCurrentStateIntent(intent))
            throw new InvalidOperationException("The panel state owner changed during source validation.");
        if (_initializing && _initialAdmission is { } finalAdmission && !finalAdmission())
            throw new InvalidOperationException("The initial panel owner changed during validation.");
        if (_restoreAnimationView is { } afterView && !afterView.IsCurrent)
            throw new InvalidOperationException("The Animation view changed during validation.");
    }

    internal ulong BeginRestore()
    {
        if (!(_initializing ? IsAttached : IsCurrent))
            throw new InvalidOperationException("The panel state owner is unavailable.");
        RestoreIntent = Panel.BeginStateIntent();
        Panel.SetActiveRestore(this);
        RequireOwnedState(RestoreIntent);
        return RestoreIntent;
    }

    internal void EndRestore(ulong intent)
    {
        if (RestoreIntent == intent)
        {
            RestoreIntent = 0;
            Panel.SetActiveRestore(null);
        }
    }

    internal void RetainAnimationRestoreView(NativeAnimationPanelViewReceipt view,
        ulong intent)
    {
        RequireOwnedState(intent);
        if (!ReferenceEquals(Panel, view.Panel) || !view.IsCurrent)
            throw new InvalidOperationException("The Animation restore view is unavailable.");
        _restoreAnimationView = view;
        RequireOwnedState(intent);
    }

    internal NativeDockPanelStateValues CaptureLiveValues()
    {
        if (!(_initializing ? IsAttached : IsCurrent))
            throw new InvalidOperationException("The panel state owner is unavailable.");
        NativeDockPanelStateValues values = NativeDockPanelStateValues.Capture(Layout, PanelName)
            with { ScrollOffset = Panel.ScrollOffset };
        return Panel switch
        {
            AssetsPanel assets => values with
            {
                UseEngineAssets = assets.UseEngineAssets,
                AssetFolder = assets.Folder,
                AssetSearch = assets.Search,
            },
            ConsolePanel console => values with
            {
                ConsoleFilter = console.Filter,
                ConsoleCategory = console.Category,
                ConsoleAutoScroll = console.AutoScroll,
            },
            ProfilerPanel profiler => values with { ProfilerView = (int)profiler.View },
            _ => values,
        };
    }

    internal NativeDockPanelStateReceipt CaptureReceipt()
    {
        NativeDockPanelStateValues values = CaptureLiveValues();
        if (!Panel.TryCaptureScrollOwner(out NativePanelScrollOwnerReceipt? scrollOwner))
            throw new InvalidOperationException("The panel Rows list lost its owner.");
        NativeAnimationPanelViewReceipt? animationView =
            Panel is AnimationPanel animation &&
            animation.TryCaptureViewReceipt(out NativeAnimationPanelViewReceipt currentView)
                ? currentView : null;
        return new(this, values, Panel.StateIntentRevision, Panel.StateRevision,
            Panel.ScrollIntentRevision, scrollOwner, animationView);
    }

    /// <summary>Captures the unsubmitted draft of the panel's transfer field, or null.</summary>
    internal NativeTextFieldDraft? CaptureFieldDraft()
    {
        if (!IsCurrent)
            throw new InvalidOperationException("The panel state owner is unavailable.");
        return NativeTextFieldDraftTransfer.TryCapture(Panel.TransferDraftField, out NativeTextFieldDraft draft)
            ? draft : null;
    }

    internal NativeDockPanelStateReceipt Restore(NativeDockPanelStateValues values,
        Func<bool>? sourceCurrent = null,
        NativeAnimationPanelViewValues? animationView = null,
        Action<NativeDockPanelStateReceipt>? retainPartialCommit = null,
        bool restorePriorSearchFocus = false,
        NativeTextFieldDraft? fieldDraft = null)
    {
        if (values.Panel != PanelName)
            throw new InvalidOperationException("The requested panel state is for another panel.");
        if (animationView is not null &&
            (PanelName != NativeDockPanel.Animation || Panel is not AnimationPanel))
            throw new InvalidOperationException("Animation view state was sent to another panel.");
        if (RestoreIntent != 0)
            throw new InvalidOperationException("A panel state restore is already active.");
        _sourceCurrent = sourceCurrent;
        ulong intent;
        try
        {
            intent = BeginRestore();
            NativeAnimationPanelViewReceipt? appliedView = null;
            switch (Panel)
            {
                case AssetsPanel assets:
                    assets.SetScopeOwned(values.UseEngineAssets, intent, this);
                    RequireOwnedState(intent);
                    assets.SetFolderOwned(values.AssetFolder, intent, this);
                    RequireOwnedState(intent);
                    assets.SetSearchOwned(values.AssetSearch, intent, this);
                    break;
                case ConsolePanel console:
                    console.SetFilterOwned(values.ConsoleFilter, intent, this);
                    RequireOwnedState(intent);
                    console.SetCategoryOwned(values.ConsoleCategory, intent, this);
                    RequireOwnedState(intent);
                    console.SetAutoScrollOwned(values.ConsoleAutoScroll, intent, this);
                    break;
                case ProfilerPanel profiler:
                    profiler.SetViewOwned((ENativeProfilerView)values.ProfilerView, intent, this);
                    break;
                case AnimationPanel animation when animationView is { } requestedView:
                    appliedView = animation.RestoreViewOwned(requestedView, this, intent, restorePriorSearchFocus);
                    _restoreAnimationView = appliedView;
                    break;
            }
            RequireOwnedState(intent);
            // The draft is written after its committed value, so a later submit commits the draft.
            if (fieldDraft is { } draft && Panel.TransferDraftField is { } field)
            {
                NativeTextFieldDraftTransfer.Restore(field, draft, () => RequireOwnedState(intent),
                    releaseFocus: restorePriorSearchFocus);
                RequireOwnedState(intent);
            }
            if (appliedView is { } view && !view.IsCurrent)
                throw new InvalidOperationException("The Animation view changed before scroll restore.");
            Panel.SetScrollOffsetOwned(values.ScrollOffset, intent, this);
            RequireOwnedState(intent);
            if (appliedView is { } afterScroll && !afterScroll.IsCurrent)
                throw new InvalidOperationException("The Animation view changed during scroll restore.");
            NativeDockPanelStateReceipt applied = CaptureReceipt();
            if (appliedView is { } retainedView)
            {
                if (!retainedView.IsCurrent || applied.AnimationView is not { IsCurrent: true })
                    throw new InvalidOperationException("The Animation view was not retained.");
                // Keep the accepted toolbar proof through the host's later callbacks.
                applied = applied with { AnimationView = retainedView };
            }
            if (!(values with { ScrollOffset = applied.Values.ScrollOffset })
                    .Matches(applied.Values))
                throw new InvalidOperationException("The panel did not accept the transferred state.");
            return applied;
        }
        catch
        {
            try
            {
                if (_restoreAnimationView is { IsCurrent: true } partialView && IsCurrent)
                {
                    NativeDockPanelStateReceipt partial = CaptureReceipt() with { AnimationView = partialView };
                    if (partial.IsCurrent) retainPartialCommit?.Invoke(partial);
                }
            }
            catch { }
            throw;
        }
        finally
        {
            EndRestore(RestoreIntent);
            _sourceCurrent = null;
            _restoreAnimationView = null;
        }
    }

    internal void RestoreInitial(NativeDockPanelStateValues values)
    {
        if (!IsAttached || Panel.StateIntentRevision != 0 ||
            _initialAdmission is { } admission && !admission())
            throw new InvalidOperationException("The newly created panel state changed before binding.");
        _initializing = true;
        try { _ = Restore(values); }
        finally { _initializing = false; }
        if (_initialAdmission is { } finalAdmission && !finalAdmission())
            throw new InvalidOperationException("The newly created panel owner changed after binding.");
    }

    internal void AttachCallbacks()
    {
        if (_callbacksAttached || !IsAttached)
            return;
        _callbacksAttached = true;
        switch (Panel)
        {
            case AssetsPanel assets:
                assets.ViewStateChanged += OnAssetsChanged;
                break;
            case ConsolePanel console:
                console.FilterChanged += OnConsoleFilterChanged;
                console.CategoryChanged += OnConsoleCategoryChanged;
                console.AutoScrollChanged += OnConsoleAutoScrollChanged;
                break;
            case ProfilerPanel profiler:
                profiler.ViewChanged += OnProfilerViewChanged;
                break;
        }
        Panel.ScrollOffsetChanged += OnScrollOffsetChanged;
    }

    private bool CanPersist()
    {
        if (!IsCurrent)
            return false;
        if (RestoreIntent != 0 && Panel.IsCurrentStateIntent(RestoreIntent))
            return false;
        Panel.PublishStateWrite();
        return true;
    }

    private void OnAssetsChanged()
    {
        if (!CanPersist() || Panel is not AssetsPanel assets) return;
        Layout.UseEngineAssets = assets.UseEngineAssets;
        Layout.AssetFolder = assets.Folder;
        Layout.AssetSearch = assets.Search;
        Save();
    }

    private void OnConsoleFilterChanged(string _)
    {
        if (!CanPersist() || Panel is not ConsolePanel console) return;
        Layout.ConsoleFilter = console.Filter;
        Save();
    }

    private void OnConsoleCategoryChanged(ELogCategory? _)
    {
        if (!CanPersist() || Panel is not ConsolePanel console) return;
        Layout.ConsoleCategory = console.Category;
        Save();
    }

    private void OnConsoleAutoScrollChanged(bool _)
    {
        if (!CanPersist() || Panel is not ConsolePanel console) return;
        Layout.ConsoleAutoScroll = console.AutoScroll;
        Save();
    }

    private void OnProfilerViewChanged(ENativeProfilerView _)
    {
        if (!CanPersist() || Panel is not ProfilerPanel profiler) return;
        Layout.ProfilerView = (int)profiler.View;
        Save();
    }

    private void OnScrollOffsetChanged(float _)
    {
        int index = NativeDockPanelStateValues.ScrollIndex(PanelName);
        if (index < 0 || Layout.PanelScrollOffsets is not { Length: 4 }) return;
        float live = Panel.ScrollOffset;
        if (Layout.PanelScrollOffsets[index] == live || !CanPersist()) return;
        Layout.PanelScrollOffsets[index] = live;
        RequestSave();
    }
}
