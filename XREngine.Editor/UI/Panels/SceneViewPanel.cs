using XREngine.Components;
using XREngine.Rendering;

namespace XREngine.Editor;

public class SceneViewPanel : EditorPanel
{
    public XRCamera? Camera => CameraComponent?.Camera;
    public CameraComponent? CameraComponent { get; set; } = null;
}