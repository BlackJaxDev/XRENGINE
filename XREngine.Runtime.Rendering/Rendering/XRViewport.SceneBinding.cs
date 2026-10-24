using System.Runtime.ExceptionServices;
using XREngine.Components;

namespace XREngine.Rendering;

public partial class XRViewport
{
    private bool _detachingSceneBinding;
    private ulong _sceneBindingRevision;

    /// <summary>
    /// Retires the camera, world, and window binding without a property-change veto.
    /// Notifications run after all owner fields are empty. Cleanup attempts every owner.
    /// </summary>
    public void DetachSceneBinding()
    {
        if (_detachingSceneBinding)
            return;
        _detachingSceneBinding = true;
        _sceneBindingRevision++;
        CameraComponent? component = _cameraComponent;
        XRCamera? camera = _camera;
        XRWindow? window = _window;
        Exception? failure = null;
        try
        {
            SetField(ref _cameraComponent, null, publishNotifications: false);
            SetField(ref _camera, null, publishNotifications: false);
            SetField(ref _window, null, publishNotifications: false);
            WorldInstanceOverride = null;
            try { SetSwapBuffersSubscription(false); }
            catch (Exception exception) { failure = exception; }
            try { SetCollectVisibleSubscription(false); }
            catch (Exception exception) { failure ??= exception; }
            try { camera?.Viewports.RemoveExactNonVetoable(this); }
            catch (Exception exception) { failure ??= exception; }
            try { RefreshRenderPipelineOutputBinding(); }
            catch (Exception exception) { failure ??= exception; }
            try
            {
                if (component is not null)
                    base.OnPropertyChanged<CameraComponent?>(nameof(CameraComponent), component, null);
            }
            catch (Exception exception) { failure ??= exception; }
            try
            {
                if (camera is not null)
                    base.OnPropertyChanged<XRCamera?>(nameof(Camera), camera, null);
            }
            catch (Exception exception) { failure ??= exception; }
            try
            {
                if (window is not null)
                    base.OnPropertyChanged<XRWindow?>(nameof(Window), window, null);
            }
            catch (Exception exception) { failure ??= exception; }
        }
        finally
        {
            // This legacy override has no notification hook. Clear callback writes too.
            WorldInstanceOverride = null;
            _detachingSceneBinding = false;
        }
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
