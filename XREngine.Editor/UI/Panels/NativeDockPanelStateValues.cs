namespace XREngine.Editor;

/// <summary>Stores only the layout values owned by one native tool panel.</summary>
internal readonly record struct NativeDockPanelStateValues(
    string Panel,
    bool UseEngineAssets,
    string AssetFolder,
    string AssetSearch,
    string ConsoleFilter,
    ELogCategory? ConsoleCategory,
    bool ConsoleAutoScroll,
    int ProfilerView,
    float ScrollOffset)
{
    internal static int ScrollIndex(string panel) => panel switch
    {
        NativeDockPanel.Assets => 0,
        NativeDockPanel.Console => 1,
        NativeDockPanel.Profiler => 2,
        NativeDockPanel.Animation => 3,
        _ => -1,
    };

    internal static NativeDockPanelStateValues Capture(NativeDockLayout layout, string panel)
    {
        int index = ScrollIndex(panel);
        float scroll = index >= 0 && layout.PanelScrollOffsets is { Length: 4 } offsets
            ? offsets[index] : 0;
        return new(panel, layout.UseEngineAssets, layout.AssetFolder,
            layout.AssetSearch, layout.ConsoleFilter, layout.ConsoleCategory,
            layout.ConsoleAutoScroll, layout.ProfilerView, scroll);
    }

    internal void WriteTo(NativeDockLayout layout)
    {
        switch (Panel)
        {
            case NativeDockPanel.Assets:
                layout.UseEngineAssets = UseEngineAssets;
                layout.AssetFolder = AssetFolder;
                layout.AssetSearch = AssetSearch;
                break;
            case NativeDockPanel.Console:
                layout.ConsoleFilter = ConsoleFilter;
                layout.ConsoleCategory = ConsoleCategory;
                layout.ConsoleAutoScroll = ConsoleAutoScroll;
                break;
            case NativeDockPanel.Profiler:
                layout.ProfilerView = ProfilerView;
                break;
        }
        int index = ScrollIndex(Panel);
        if (index >= 0 && layout.PanelScrollOffsets is { Length: 4 } offsets)
            offsets[index] = ScrollOffset;
    }

    internal bool Matches(NativeDockLayout layout)
    {
        int index = ScrollIndex(Panel);
        if (index >= 0 &&
            (layout.PanelScrollOffsets is not { Length: 4 } offsets ||
             offsets[index] != ScrollOffset))
            return false;
        return Panel switch
        {
            NativeDockPanel.Assets => layout.UseEngineAssets == UseEngineAssets &&
                layout.AssetFolder == AssetFolder && layout.AssetSearch == AssetSearch,
            NativeDockPanel.Console => layout.ConsoleFilter == ConsoleFilter &&
                layout.ConsoleCategory == ConsoleCategory &&
                layout.ConsoleAutoScroll == ConsoleAutoScroll,
            NativeDockPanel.Profiler => layout.ProfilerView == ProfilerView,
            _ => true,
        };
    }

    internal bool Matches(in NativeDockPanelStateValues other)
    {
        if (Panel != other.Panel || ScrollOffset != other.ScrollOffset)
            return false;
        return Panel switch
        {
            NativeDockPanel.Assets => UseEngineAssets == other.UseEngineAssets &&
                AssetFolder == other.AssetFolder && AssetSearch == other.AssetSearch,
            NativeDockPanel.Console => ConsoleFilter == other.ConsoleFilter &&
                ConsoleCategory == other.ConsoleCategory &&
                ConsoleAutoScroll == other.ConsoleAutoScroll,
            NativeDockPanel.Profiler => ProfilerView == other.ProfilerView,
            _ => true,
        };
    }
}
