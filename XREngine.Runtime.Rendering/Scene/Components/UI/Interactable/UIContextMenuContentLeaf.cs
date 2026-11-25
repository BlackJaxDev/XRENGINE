using XREngine.Components;
using XREngine.Scene;

namespace XREngine.Rendering.UI;

/// <summary>Configures one guarded popup node before its parent adopts it.</summary>
public sealed class UIContextMenuContentLeaf
{
    private readonly UIContextMenuLeafConstruction _construction;

    internal UIContextMenuContentLeaf(UIContextMenuLeafConstruction construction)
        => _construction = construction;

    public SceneNode Node => _construction.Node;
    public UIBoundableTransform Transform => _construction.Transform;
    public T Add<T>(Func<T> factory) where T : XRComponent => _construction.Add(factory);
    public void Write(Action write) => _construction.Write(write);
}
