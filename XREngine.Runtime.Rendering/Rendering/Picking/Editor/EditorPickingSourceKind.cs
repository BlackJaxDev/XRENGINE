namespace XREngine.Rendering;

/// <summary>Identifies the raster path that wrote an editor picking pixel.</summary>
public enum EditorPickingSourceKind : ushort
{
    None = 0,
    NativeDraw = 1,
    AuthoredCommand = 2,
}
