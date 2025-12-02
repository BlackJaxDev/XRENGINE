using System.Collections.Generic;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

public sealed class AmbientOcclusionInspectorController : UIComponent
{
    private readonly HashSet<AmbientOcclusionSettings> _subscribedTargets = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    private AmbientOcclusionSettings[]? _targets;

    public AmbientOcclusionSettings[]? Targets
    {
        get => _targets;
        set
        {
            if (ReferenceEquals(_targets, value))
                return;

            UnsubscribeTargets();
            _targets = value;

            if (IsActiveInHierarchy)
                SubscribeTargets();

            UpdateVisibility();
        }
    }

    public SceneNode? ScreenSpaceGroup { get; set; }
    public SceneNode? MultiViewGroup { get; set; }
    public SceneNode? SpatialHashGroup { get; set; }
    public SceneNode? MultipleTypeMessage { get; set; }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        SubscribeTargets();
        UpdateVisibility();
    }

    protected override void OnComponentDeactivated()
    {
        base.OnComponentDeactivated();
        UnsubscribeTargets();
    }

    protected override void OnDestroying()
    {
        base.OnDestroying();
        UnsubscribeTargets();
    }

    private void SubscribeTargets()
    {
        if (_targets is null)
            return;

        foreach (var target in _targets)
        {
            if (target is null)
                continue;

            if (_subscribedTargets.Add(target))
                target.PropertyChanged += OnTargetPropertyChanged;
        }
    }

    private void UnsubscribeTargets()
    {
        foreach (var target in _subscribedTargets)
            target.PropertyChanged -= OnTargetPropertyChanged;

        _subscribedTargets.Clear();
    }

    private void OnTargetPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AmbientOcclusionSettings.Type))
            UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        bool hasTargets = false;
        bool unsupportedType = false;
        var consistentType = DetermineConsistentType(ref hasTargets, ref unsupportedType);

        SetActive(ScreenSpaceGroup, consistentType == AmbientOcclusionSettings.EType.ScreenSpace);
        SetActive(MultiViewGroup, consistentType == AmbientOcclusionSettings.EType.MultiViewAmbientOcclusion);
        SetActive(SpatialHashGroup, consistentType == AmbientOcclusionSettings.EType.SpatialHashRaytraced);

        bool showWarning = (hasTargets && consistentType is null) || unsupportedType;
        SetActive(MultipleTypeMessage, showWarning);

        if (!showWarning && consistentType is null)
        {
            SetActive(ScreenSpaceGroup, false);
            SetActive(MultiViewGroup, false);
            SetActive(SpatialHashGroup, false);
        }
    }

    private AmbientOcclusionSettings.EType? DetermineConsistentType(ref bool hasTargets, ref bool unsupportedType)
    {
        AmbientOcclusionSettings.EType? type = null;
        hasTargets = false;
        unsupportedType = false;

        if (_targets is null)
            return null;

        foreach (var target in _targets)
        {
            if (target is null)
                continue;

            hasTargets = true;

            if (!IsSupportedType(target.Type))
            {
                unsupportedType = true;
                return null;
            }

            if (type is null)
            {
                type = target.Type;
                continue;
            }

            if (type != target.Type)
                return null;
        }

        return type;
    }

    private static bool IsSupportedType(AmbientOcclusionSettings.EType type)
        => type == AmbientOcclusionSettings.EType.ScreenSpace
        || type == AmbientOcclusionSettings.EType.MultiViewAmbientOcclusion
        || type == AmbientOcclusionSettings.EType.SpatialHashRaytraced;

    private static void SetActive(SceneNode? node, bool active)
    {
        if (node is null)
            return;

        node.IsActiveSelf = active;
    }
}
