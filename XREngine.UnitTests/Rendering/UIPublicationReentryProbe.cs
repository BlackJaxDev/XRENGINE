using System.Numerics;
using XREngine.Rendering.Info;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

public sealed class UIPublicationReentryProbe : UIRenderableComponent
{
    public Action? OnCapture { get; set; }
    public int CaptureCount { get; private set; }

    public override UIRenderRecord CapturePublicationRenderRecord(in UILayoutNodeOutput output)
    {
        CaptureCount++;
        OnCapture?.Invoke();
        RenderInfo2D info = RenderInfo2D;
        return new UIRenderRecord
        {
            RenderInfo = info,
            MembershipId = info.MembershipId,
            Identity = BoundableTransform.ElementIdentity,
            WorldMatrix = Matrix4x4.Identity,
            Bounds = new Vector4(0, 0, output.ActualSize.X, output.ActualSize.Y),
            RenderPass = 0,
            ZIndex = 0,
            Material = null,
            Visible = false,
        };
    }
}
