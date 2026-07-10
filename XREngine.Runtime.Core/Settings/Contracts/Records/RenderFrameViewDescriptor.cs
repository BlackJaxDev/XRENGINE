using System.Numerics;

namespace XREngine;

public readonly record struct RenderFrameViewDescriptor(
    uint ViewId,
    EVrOutputViewKind Kind,
    uint ParentViewId,
    int VisibilityGroupIndex,
    int OpenXrViewIndex,
    uint OutputLayer,
    RenderFrameViewRect ViewRect,
    Matrix4x4 ViewMatrix,
    Matrix4x4 ProjectionMatrix,
    Matrix4x4 PreviousViewProjectionMatrix,
    ViewFoveationContext Foveation,
    string? DebugName = null)
{
    public const uint InvalidViewId = uint.MaxValue;

    public bool HasParent => ParentViewId != InvalidViewId;
    public bool IsStereoEye => Kind is EVrOutputViewKind.LeftEye or EVrOutputViewKind.RightEye;
    public bool IsWideView => Kind is EVrOutputViewKind.LeftWide or EVrOutputViewKind.RightWide;
    public bool IsInsetView => Kind is EVrOutputViewKind.LeftInset or EVrOutputViewKind.RightInset;
    public bool IsQuadView => IsWideView || IsInsetView;
    public bool IsLeftEyeFamily => Kind is EVrOutputViewKind.LeftEye or EVrOutputViewKind.LeftWide or EVrOutputViewKind.LeftInset;
    public bool IsRightEyeFamily => Kind is EVrOutputViewKind.RightEye or EVrOutputViewKind.RightWide or EVrOutputViewKind.RightInset;
    public bool IsXrSubmittedView => IsStereoEye || IsQuadView;
    public Matrix4x4 ViewProjectionMatrix => ViewMatrix * ProjectionMatrix;

    public RenderFrameViewDescriptor WithVisibilityGroup(int visibilityGroupIndex)
        => this with { VisibilityGroupIndex = visibilityGroupIndex };

    public RenderFrameViewDescriptor WithParent(uint parentViewId)
        => this with { ParentViewId = parentViewId };
}
