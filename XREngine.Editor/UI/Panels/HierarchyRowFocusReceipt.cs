using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>Retains one ordinary row focus across a stop of the same panel owner.</summary>
internal sealed class HierarchyRowFocusReceipt(
    IUICanvasInputSource input, UIInteractableComponent target, SceneNode source,
    UICanvasComponent canvas, UIBoundableTransform panel, XRWindow? window, uint lifecycle,
    ObjectConstructionIdentity[] identities)
{
    public IUICanvasInputSource Input { get; } = input;
    public UIInteractableComponent Target { get; } = target;
    public SceneNode Source { get; } = source;
    public TransformBase SourceTransform { get; } = source.Transform;
    public IRuntimeWorldContext? SourceWorld { get; } = source.World;
    public UICanvasComponent Canvas { get; } = canvas;
    public ulong CanvasLifetime { get; } = canvas.PublicationLifetime;
    public UIBoundableTransform Panel { get; } = panel;
    public TransformBase? Parent { get; } = panel.Parent;
    public IRuntimeWorldContext? World { get; } = panel.World;
    public XRWindow? Window { get; } = window;
    public RuntimeWorld? RuntimeWorld { get; } = panel.World as RuntimeWorld;
    public XRWorld? Document { get; } = (panel.World as RuntimeWorld)?.TargetWorld;
    public ObjectConstructionIdentity[] Identities { get; } = identities;
    public SceneNode PanelNode { get; } = panel.SceneNode!;
    public XRComponent InputComponent { get; } = (XRComponent)input;
    public SceneNode InputNode { get; } = ((XRComponent)input).SceneNode;
    public TransformBase InputTransform { get; } = ((XRComponent)input).SceneNode.Transform;
    public SceneNode CanvasNode { get; } = canvas.SceneNode;
    public TransformBase CanvasTransform { get; } = canvas.SceneNode.Transform;
    public uint Lifecycle { get; } = lifecycle;
    public ulong CapturedIntent { get; } = input.FocusIntentRevision;
    public ulong ClearedIntent { get; set; }
    public bool Requested { get; set; }
}
