using System.Numerics;
using XREngine.Rendering;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Capture.Lights
{
    public partial class LightProbeComponent
    {
        #region Component Lifecycle

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();

            var world = WorldAs<XREngine.Rendering.IRuntimeRenderWorld>();
            if (world is not null)
            {
                if (_registeredWorld is not null && _registeredWorld != world)
                    _registeredWorld.Lights.RemoveLightProbe(this);

                world.Lights.AddLightProbe(this);
                _registeredWorld = world;
                SyncPreviewRenderCommandTransform();
            }
            ScheduleStartupCaptureIfRequested();
        }

        protected override void OnComponentDeactivated()
        {
            _startupCaptureTimer.Cancel();
            _realtimeCaptureTimer.Cancel();
            base.OnComponentDeactivated();

            if (_registeredWorld is not null)
            {
                _registeredWorld.Lights.RemoveLightProbe(this);
                _registeredWorld = null;
            }

            DestroyIblResources();
        }

        #endregion

        #region Property Change Handling

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(EnvironmentTextureEquirect):
                    if (EnvironmentTextureEquirect is not null)
                        InitializeStatic();
                    else
                    {
                        IblTexturesValid = false;
                        CaptureVersion = 0;
                    }
                    break;
                case nameof(IrradianceResolution):
                    if (EnvironmentTextureEquirect is not null)
                    {
                        InitializeStatic();
                    }
                    else if (IsActiveInHierarchy)
                    {
                        InvalidateCaptureResources();
                        EnsureCaptureResourcesInitialized();
                    }
                    break;
                case nameof(UseDirectCubemapIblGeneration):
                    if (EnvironmentTextureEquirect is null && IsActiveInHierarchy)
                    {
                        InvalidateCaptureResources();
                        EnsureCaptureResourcesInitialized();
                    }
                    CachePreviewSphere();
                    break;
                case nameof(ReleaseTransientEnvironmentTexturesAfterCapture):
                    CachePreviewSphere();
                    break;
                case nameof(PreviewDisplay):
                    CachePreviewSphere();
                    break;
                case nameof(World):
                    SyncPreviewRenderCommandTransform();
                    ScheduleStartupCaptureIfRequested();
                    break;
                case nameof(RealtimeCapture):
                    if (RealtimeCapture)
                        _realtimeCaptureTimer.StartMultiFire(QueueCapture, RealTimeCaptureUpdateInterval ?? TimeSpan.Zero);
                    else
                        _realtimeCaptureTimer.Cancel();
                    ScheduleStartupCaptureIfRequested();
                    break;
                case nameof(AutoCaptureOnActivate):
                    ScheduleStartupCaptureIfRequested();
                    break;
                case nameof(RealTimeCaptureUpdateInterval):
                    _realtimeCaptureTimer.TimeBetweenFires = RealTimeCaptureUpdateInterval ?? TimeSpan.Zero;
                    break;
            }
        }

        private void ScheduleStartupCaptureIfRequested()
        {
            _startupCaptureTimer.Cancel();
            if (!IsActiveInHierarchy || RealtimeCapture || !AutoCaptureOnActivate)
                return;

            _startupCaptureTimer.StartSingleFire(() =>
            {
                var world = WorldAs<XREngine.Rendering.IRuntimeRenderWorld>();
                if (!IsActiveInHierarchy || world is null || RealtimeCapture || !AutoCaptureOnActivate)
                    return;

                FullCapture(Resolution, CaptureDepthCubeMap);
            }, TimeSpan.FromMilliseconds(1.0f));
        }

        #endregion

        #region Transform Handling

        protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
        {
            UpdatePreviewRenderMatrix(renderMatrix);
            base.OnTransformRenderWorldMatrixChanged(transform, renderMatrix);
        }

        #endregion
    }
}
