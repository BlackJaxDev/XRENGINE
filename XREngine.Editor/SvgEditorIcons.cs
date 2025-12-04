namespace XREngine.Editor;

/// <summary>
/// Common SVG editor icon resource paths.
/// </summary>
public static class SvgEditorIcons
{
    public const string ICON_ROOT = "Textures/Icons/svg/";

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
}
