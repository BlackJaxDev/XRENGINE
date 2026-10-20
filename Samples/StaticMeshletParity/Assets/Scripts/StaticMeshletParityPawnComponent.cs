using XREngine;
using XREngine.Components;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace StaticMeshletParity;

/// <summary>Retains the authored camera during gameplay without moving the comparison view.</summary>
public sealed class StaticMeshletParityPawnComponent : PawnComponent
{
    private XRCamera? _subscribedCamera;

    protected override void OnBeginPlay()
    {
        base.OnBeginPlay();
        UnsubscribeCamera();
        CameraComponent camera = SceneNode.GetComponent<CameraComponent>()
            ?? throw new InvalidOperationException("The static meshlet inspection pawn requires its saved camera.");
        CameraComponent = camera;
        XRCamera authoredCamera = camera.Camera;
        SetField(ref _subscribedCamera, authoredCamera, publishNotifications: false);
        authoredCamera.ViewportAdded += ApplySubmissionStrategy;
        foreach (XRViewport viewport in authoredCamera.Viewports)
            ApplySubmissionStrategy(authoredCamera, viewport);
    }

    private static void ApplySubmissionStrategy(XRCamera camera, XRViewport viewport)
    {
        if (!ReferenceEquals(viewport.ActiveCamera, camera))
            throw new InvalidOperationException("The static meshlet strategy requires the authored inspection camera.");
        if (Engine.UserSettings?.GPURenderDispatchOverride is not { HasOverride: true } dispatch)
            throw new InvalidOperationException("The static meshlet strategy requires the saved GPU dispatch override.");
        viewport.MeshSubmissionStrategyOverride = dispatch.Value
            ? EMeshSubmissionStrategy.GpuMeshletZeroReadback : EMeshSubmissionStrategy.CpuDirect;
    }

    protected override void OnDestroying()
    {
        UnsubscribeCamera();
        base.OnDestroying();
    }

    protected override void OnEndPlay()
    {
        UnsubscribeCamera();
        base.OnEndPlay();
    }

    private void UnsubscribeCamera()
    {
        if (_subscribedCamera is not null)
            _subscribedCamera.ViewportAdded -= ApplySubmissionStrategy;
        SetField(ref _subscribedCamera, null, publishNotifications: false);
    }
}
