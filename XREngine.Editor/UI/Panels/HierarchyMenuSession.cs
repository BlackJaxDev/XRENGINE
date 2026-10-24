using System.Numerics;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Owns one menu's captured row lease and releases its callbacks on dismissal.</summary>
internal sealed class HierarchyMenuSession
{
    public HierarchyPanel? Owner;
    public HierarchyCommandContext? Context;
    public IUICanvasInputSource? Input;
    public UICanvasTransform? Canvas;
    public UIListTransform? List;
    public UIVirtualTreeProvider? Provider;
    public UIBoundableTransform? Row;
    public UIVirtualRowLease Lease;
    public ulong ProviderIncarnation;
    public SceneNode? Node;
    public XRScene? Scene;
    public uint Revision;
    public ulong Request;
    public Vector2 Position;
    public bool AwaitingShow;

    public void Rename() => Owner?.InvokeHierarchyMenuAction(this, EHierarchyMenuAction.Rename);
    public void AddChild() => Owner?.InvokeHierarchyMenuAction(this, EHierarchyMenuAction.AddChild);
    public void Focus() => Owner?.InvokeHierarchyMenuAction(this, EHierarchyMenuAction.Focus);
    public void Delete() => Owner?.InvokeHierarchyMenuAction(this, EHierarchyMenuAction.Delete);
    public void ToggleScene() => Owner?.InvokeHierarchyMenuAction(this, EHierarchyMenuAction.ToggleScene);
    public void UnloadScene() => Owner?.InvokeHierarchyMenuAction(this, EHierarchyMenuAction.UnloadScene);

    public void Release()
    {
        Owner = null;
        Context = null;
        Input = null;
        Canvas = null;
        List = null;
        Provider = null;
        Row = null;
        Node = null;
        Scene = null;
        AwaitingShow = false;
    }
}
