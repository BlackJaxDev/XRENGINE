using XREngine.Scene;

namespace XREngine.Rendering.UI;

/// <summary>
/// Scene component that attaches a serializable <see cref="UINavigationFlowMap"/> to a UI menu or container node
/// so runtime canvas input and future UI editors share one authoritative navigation graph.
/// </summary>
public sealed class UINavigationFlowMapComponent : UIComponent
{
    private UINavigationFlowMap _flowMap = new();
    /// <summary>
    /// Navigation graph defining allowed directions, edges, initial focus, and repeat settings for this UI scope.
    /// </summary>
    public UINavigationFlowMap FlowMap
    {
        get => _flowMap;
        set => SetField(ref _flowMap, value ?? new UINavigationFlowMap());
    }

    private SceneNode? _scopeRoot;
    /// <summary>
    /// Optional root node limiting spatial fallback candidates. Defaults to this component's <see cref="XRComponent.SceneNode"/>.
    /// </summary>
    public SceneNode? ScopeRoot
    {
        get => _scopeRoot;
        set => SetField(ref _scopeRoot, value);
    }

    /// <summary>
    /// Returns the effective scene-node scope root for candidate discovery.
    /// </summary>
    public SceneNode? EffectiveScopeRoot => _scopeRoot ?? SceneNode;
}
