using System.Numerics;
using XREngine.Components;
using XREngine.Rendering.UI.State;

namespace XREngine.Rendering.UI;

/// <summary>An explicitly requested blurred scene backdrop for a screen canvas panel.</summary>
public sealed class UIBackdropBlurComponent : UIMaterialComponent
{
    private bool _enabled;
    private float _blurStrength = 2.5f;

    public UIBackdropBlurComponent() => SetBatchSolidColor(Vector4.One);

    public bool Enabled
    {
        get => _enabled;
        set { if (SetField(ref _enabled, value)) InvalidateVisual(); }
    }

    public float BlurStrength
    {
        get => _blurStrength;
        set
        {
            if (!float.IsFinite(value) || value is < 0.0f or > 32.0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (SetField(ref _blurStrength, value)) InvalidateVisual();
        }
    }

    public override bool SupportsBatchedRendering => Enabled && !DisableBatching;

    public override UIRenderRecord CapturePublicationRenderRecord(in UILayoutNodeOutput output)
    {
        UIRenderRecord record = base.CapturePublicationRenderRecord(in output);
        if (!Enabled || !record.Visible)
            return record with { Visible = false };
        UICanvasComponent? canvas = BoundableTransform.ParentCanvas?.SceneNode?.GetComponent<UICanvasComponent>();
        if (canvas is null || BoundableTransform.ParentCanvas?.DrawSpace != ECanvasDrawSpace.Screen ||
            !record.SupportsBatch || record.Texture is not null || record.IsBorder)
            return record with
            {
                CaptureFailure = "UI.BackdropBlur.ScreenBatchRequired: blur requires a screen canvas and an intrinsic batched quad."
            };
        return record with { Backdrop = new UIBackdropRequest(true, BlurStrength) };
    }

    private void InvalidateVisual()
    {
        if (SceneNode is not null && !SceneNode.IsTransformNull && Transform is UITransform transform)
            transform.RecordUIChange(UIChangeDomain.Visual);
    }
}
