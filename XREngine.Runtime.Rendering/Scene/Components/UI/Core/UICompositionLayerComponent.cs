using XREngine.Rendering.UI.State;

namespace XREngine.Rendering.UI;

/// <summary>Assigns a composition owner to this subtree. Nested owners start separate layers.</summary>
public sealed class UICompositionLayerComponent : UIComponent
{
    private EUICompositionLayerKind _kind = EUICompositionLayerKind.Dynamic;

    /// <summary>Describes update behavior. Static layers still invalidate when their records change.</summary>
    public EUICompositionLayerKind Kind
    {
        get => _kind;
        set
        {
            if (!Enum.IsDefined(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            SetField(ref _kind, value);
        }
    }

    protected override UIChangeDomain GetUIChangeDomain(string? propertyName)
        => UIChangeDomain.Visual | UIChangeDomain.Topology;
}
