using XREngine.Data.Core;
using XREngine.Editor.UI.Components;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Restores and saves tool-panel state through the same bindings in each native host.</summary>
internal static class NativeToolPanelState
{
    internal static void Bind(NativeVirtualTextPanel panel, SceneNode slot,
        NativeDockLayout layout, int scrollIndex, Action save, Action requestSave,
        Func<bool>? ownsSlot = null)
    {
        ObjectConstructionIdentity component = ObjectConstructionIdentity.Capture(panel);
        ObjectConstructionIdentity node = ObjectConstructionIdentity.Capture(panel.SceneNode);
        ObjectConstructionIdentity transform = ObjectConstructionIdentity.Capture(panel.Transform);
        ObjectConstructionIdentity slotIdentity = ObjectConstructionIdentity.Capture(slot);
        ObjectConstructionIdentity slotTransform = ObjectConstructionIdentity.Capture(slot.Transform);

        bool Attached() => component.IsCurrent && node.IsCurrent && transform.IsCurrent &&
            slotIdentity.IsCurrent && slotTransform.IsCurrent &&
            !panel.IsDestroyed && !panel.IsDestroyQueued &&
            !slot.IsDestroyed && !slot.IsDestroyQueued &&
            ReferenceEquals(panel.SceneNode, node.Target) &&
            !panel.SceneNode.IsDestroyed && !panel.SceneNode.IsDestroyQueued &&
            ReferenceEquals(panel.Transform, transform.Target) &&
            ReferenceEquals(slot.Transform, slotTransform.Target) &&
            ReferenceEquals(panel.SceneNode.Parent, slot);
        bool Current() => Attached() && (ownsSlot?.Invoke() ?? true);

        if (!Attached()) return;
        switch (panel)
        {
            case AssetsPanel assets:
                assets.UseEngineAssets = layout.UseEngineAssets;
                if (!Attached()) return;
                assets.Folder = layout.AssetFolder;
                if (!Attached()) return;
                assets.Search = layout.AssetSearch;
                if (!Attached()) return;
                assets.ViewStateChanged += () =>
                {
                    if (!Current()) return;
                    layout.UseEngineAssets = assets.UseEngineAssets;
                    layout.AssetFolder = assets.Folder;
                    layout.AssetSearch = assets.Search;
                    save();
                };
                break;
            case ConsolePanel console:
                console.Filter = layout.ConsoleFilter;
                if (!Attached()) return;
                console.Category = layout.ConsoleCategory;
                if (!Attached()) return;
                console.AutoScroll = layout.ConsoleAutoScroll;
                if (!Attached()) return;
                console.FilterChanged += value =>
                {
                    if (!Current()) return;
                    layout.ConsoleFilter = value;
                    save();
                };
                console.CategoryChanged += value =>
                {
                    if (!Current()) return;
                    layout.ConsoleCategory = value;
                    save();
                };
                console.AutoScrollChanged += value =>
                {
                    if (!Current()) return;
                    layout.ConsoleAutoScroll = value;
                    save();
                };
                break;
            case ProfilerPanel profiler:
                profiler.View = (ENativeProfilerView)layout.ProfilerView;
                if (!Attached()) return;
                profiler.ViewChanged += value =>
                {
                    if (!Current()) return;
                    layout.ProfilerView = (int)value;
                    save();
                };
                break;
        }
        // Restoring filters and views can change the row extent. Restore scroll last.
        if (!Attached()) return;
        panel.ScrollOffset = layout.PanelScrollOffsets[scrollIndex];
        if (!Attached()) return;
        panel.ScrollOffsetChanged += offset =>
        {
            if (!Current()) return;
            layout.PanelScrollOffsets[scrollIndex] = offset;
            requestSave();
        };
    }
}
