using Silk.NET.OpenGL;
using System;
using XREngine.Data.Geometry;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    private ERenderClipSpaceYDirection _appliedClipSpaceYDirection = (ERenderClipSpaceYDirection)(-1);
    private ERenderClipDepthRange _appliedClipDepthRange = (ERenderClipDepthRange)(-1);
    private int _uiClipSpaceScopeDepth;

    private void ApplyClipSpacePolicy()
        => ApplyClipSpacePolicy(Api, force: false);

    private void ApplyClipSpacePolicy(bool force)
        => ApplyClipSpacePolicy(Api, force);

    private void ApplyClipSpacePolicy(GL api)
        => ApplyClipSpacePolicy(api, force: false);

    private void ApplyClipSpacePolicy(GL api, bool force)
    {
        ERenderClipSpaceYDirection yDirection = ResolveActiveOpenGLClipSpaceYDirection();
        ERenderClipDepthRange depthRange = RuntimeEngine.Rendering.ResolveEffectiveClipDepthRange(RuntimeGraphicsApiKind.OpenGL);
        if (!force && _appliedClipSpaceYDirection == yDirection && _appliedClipDepthRange == depthRange)
            return;

        api.ClipControl(ToGLClipOrigin(yDirection), ToGLClipDepthRange(depthRange));
        _appliedClipSpaceYDirection = yDirection;
        _appliedClipDepthRange = depthRange;
    }

    private static GLEnum ToGLClipOrigin(ERenderClipSpaceYDirection direction)
        => direction == ERenderClipSpaceYDirection.YDown ? GLEnum.UpperLeft : GLEnum.LowerLeft;

    private static GLEnum ToGLClipDepthRange(ERenderClipDepthRange range)
        => range == ERenderClipDepthRange.NegativeOneToOne ? GLEnum.NegativeOneToOne : GLEnum.ZeroToOne;

    private ERenderClipSpaceYDirection ResolveActiveOpenGLClipSpaceYDirection()
        => _uiClipSpaceScopeDepth > 0
            ? ERenderClipSpaceYDirection.YUp
            : RuntimeEngine.Rendering.Settings.ClipSpaceYDirection;

    private bool UsesUpperLeftClipOriginForCurrentDraws()
        => ResolveActiveOpenGLClipSpaceYDirection() == ERenderClipSpaceYDirection.YDown;

    public override IDisposable? PushUiClipSpacePolicy()
    {
        _uiClipSpaceScopeDepth++;
        ApplyClipSpacePolicy(force: true);
        ReapplyActiveOpenGLRenderAreaState();
        return StateObject.New(PopUiClipSpacePolicy);
    }

    private void PopUiClipSpacePolicy()
    {
        if (_uiClipSpaceScopeDepth > 0)
            _uiClipSpaceScopeDepth--;

        ApplyClipSpacePolicy(force: true);
        ReapplyActiveOpenGLRenderAreaState();
    }

    private void ReapplyActiveOpenGLRenderAreaState()
    {
        var state = RuntimeEngine.Rendering.State.RenderingPipelineState;
        if (state is null)
            return;

        BoundingRectangle renderArea = state.CurrentRenderRegion;
        if (renderArea.Width > 0 && renderArea.Height > 0)
            SetRenderArea(renderArea);

        BoundingRectangle cropArea = state.CurrentCropRegion;
        if (cropArea.Width > 0 && cropArea.Height > 0)
        {
            SetCroppingEnabled(true);
            CropRenderArea(cropArea);
        }
        else
        {
            SetCroppingEnabled(false);
        }
    }
}
