namespace XREngine.Editor;

/// <summary>
/// Common SVG editor icon resource paths.
/// </summary>
public static class SvgEditorIcons
{
    public const string ICON_ROOT = "Textures/Editor/Icons";

    public static string GetIconPath(string iconFileName) 
        => Path.Combine(ICON_ROOT, iconFileName);

    public const string IconAdd = "icon_add.svg";
    public const string IconRemove = "icon_remove.svg";
    public const string IconMove = "icon_move.svg";
    public const string IconDuplicate = "icon_duplicate.svg";
    public const string IconSettings = "icon_settings.svg";
    public const string IconSearch = "icon_search.svg";
    public const string IconWarning = "icon_warning.svg";
    public const string IconError = "icon_error.svg";
    public const string IconInfo = "icon_info.svg";
    public const string IconFolder = "icon_folder.svg";
    public const string IconFile = "icon_file.svg";
    public const string IconRefresh = "icon_refresh.svg";
    public const string IconCamera = "icon_camera.svg";
    public const string IconLight = "icon_light.svg";
    public const string IconMesh = "icon_mesh.svg";
    public const string IconMaterial = "icon_material.svg";
    public const string IconPlay = "icon_play.svg";
    public const string IconPause = "icon_pause.svg";
    public const string IconStop = "icon_stop.svg";

    // Transform tool icons
    public const string IconTranslate = "icon_translate.svg";
    public const string IconRotate = "icon_rotate.svg";
    public const string IconScale = "icon_scale.svg";
    
    // Transform space icons
    public const string IconWorld = "icon_world.svg";
    public const string IconLocal = "icon_local.svg";
    public const string IconParent = "icon_parent.svg";
    public const string IconScreen = "icon_screen.svg";
    
    // Snap icons
    public const string IconSnap = "icon_snap.svg";
    public const string IconSnapGrid = "icon_snap_grid.svg";
    
    // Play mode icons
    public const string IconStepFrame = "icon_step_frame.svg";
}
