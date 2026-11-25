using System.Numerics;
using XREngine.Rendering.Info;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;

namespace XREngine.UnitTests.Rendering;

public sealed class UIPublicationMutationProbe : UIRenderableComponent
{
    public bool MutateDuringCapture { get; set; }
    public int CaptureCount { get; private set; }

    public override UIRenderRecord CapturePublicationRenderRecord(in UILayoutNodeOutput output)
    {
        CaptureCount++;
        if (MutateDuringCapture)
            BoundableTransform.RecordUIChange(UIChangeDomain.Visual);

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
