using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Creates one retained state binding for a native tool-panel slot.</summary>
internal static class NativeToolPanelState
{
    internal static NativeToolPanelStateBinding? Bind(NativeVirtualTextPanel panel,
        SceneNode slot, NativeDockLayout layout, int scrollIndex, Action save,
        Action requestSave, Func<bool>? ownsSlot = null,
        Func<bool>? initialAdmission = null)
        => Bind(panel, slot, layout, scrollIndex switch
        {
            0 => NativeDockPanel.Assets,
            1 => NativeDockPanel.Console,
            2 => NativeDockPanel.Profiler,
            3 => NativeDockPanel.Animation,
            _ => throw new ArgumentOutOfRangeException(nameof(scrollIndex)),
        }, save, requestSave, ownsSlot, initialAdmission);

    internal static NativeToolPanelStateBinding? Bind(NativeVirtualTextPanel panel,
        SceneNode slot, NativeDockLayout layout, string panelName, Action save,
        Action requestSave, Func<bool>? ownsSlot = null,
        Func<bool>? initialAdmission = null)
    {
        var binding = new NativeToolPanelStateBinding(panel, slot, layout,
            panelName, save, requestSave, ownsSlot, initialAdmission);
        if (!binding.IsAttached)
            return null;
        binding.RestoreInitial(NativeDockPanelStateValues.Capture(layout, panelName));
        if (!binding.IsAttached || initialAdmission is not null && !initialAdmission())
            return null;
        binding.AttachCallbacks();
        return binding;
    }
}
