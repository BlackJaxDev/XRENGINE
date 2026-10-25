namespace XREngine.Editor;

/// <summary>Stores a window's native tool tabs and scene split.</summary>
public sealed class NativeDockLayout
{
    public int SelectedTab { get; set; }
    public float BottomSize { get; set; } = 240;
    public int OpenMask { get; set; } = 15;
    public int[] TabOrder { get; set; } = [0, 1, 2, 3];
    public float[] PanelScrollOffsets { get; set; } = [0, 0, 0, 0];
    public string AssetFolder { get; set; } = string.Empty;
    public bool UseEngineAssets { get; set; }
    public string AssetSearch { get; set; } = string.Empty;
    public string ConsoleFilter { get; set; } = string.Empty;
    /// <summary>Stores the selected native profiler view as its enum value.</summary>
    public int ProfilerView { get; set; }
    public NativeDockTree? DockTree { get; set; }

    public NativeDockLayout Sanitize()
    {
        BottomSize = float.IsFinite(BottomSize) ? Math.Clamp(BottomSize, 96, 2000) : 240;
        OpenMask &= 15;
        if (TabOrder is null || TabOrder.Length != 4 || TabOrder.Distinct().Count() != 4 || TabOrder.Any(i => i is < 0 or > 3))
            TabOrder = [0, 1, 2, 3];
        if (PanelScrollOffsets is null || PanelScrollOffsets.Length != 4)
            PanelScrollOffsets = [0, 0, 0, 0];
        for (int i = 0; i < PanelScrollOffsets.Length; i++)
            if (!float.IsFinite(PanelScrollOffsets[i]) || PanelScrollOffsets[i] < 0)
                PanelScrollOffsets[i] = 0;
        AssetFolder ??= string.Empty;
        AssetSearch ??= string.Empty;
        ConsoleFilter ??= string.Empty;
        if (!Enum.IsDefined((ENativeProfilerView)ProfilerView))
            ProfilerView = (int)ENativeProfilerView.CpuTimings;
        SelectedTab = Math.Clamp(SelectedTab, 0, 3);
        if ((OpenMask & (1 << SelectedTab)) == 0)
        {
            int firstOpenSlot = Array.FindIndex(TabOrder, i => (OpenMask & (1 << i)) != 0);
            SelectedTab = firstOpenSlot >= 0 ? TabOrder[firstOpenSlot] : -1;
        }
        try
        {
            DockTree = (DockTree ?? NativeDockTree.FromLegacy(this)).Sanitize();
        }
        catch (InvalidOperationException exception)
        {
            Debug.UI($"[NativeDock] Saved dock tree is invalid: {exception.Message}");
            DockTree = NativeDockTree.FromLegacy(this).Sanitize();
        }
        return this;
    }
}
