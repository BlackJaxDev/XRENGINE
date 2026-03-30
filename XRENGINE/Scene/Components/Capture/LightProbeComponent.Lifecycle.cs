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

            var world = WorldAs<XREngine.Rendering.XRWorldInstance>();
            Debug.Out($"[LightProbe] OnComponentActivated: world={world is not null}, RealtimeCapture={RealtimeCapture}, Cubemap={EnvironmentTextureCubemap is not null}, Octa={EnvironmentTextureOctahedral is not null}, IrrFBO={_irradianceFBO is not null}");
            if (world is not null)
            {
                if (_registeredWorld is not null && _registeredWorld != world)
                    _registeredWorld.Lights.RemoveLightProbe(this);

                world.Lights.AddLightProbe(this);
                _registeredWorld = world;
            }
            if (!RealtimeCapture && AutoCaptureOnActivate)
            {
                _startupCaptureTimer.StartSingleFire(() =>
                {
                    var w = WorldAs<XREngine.Rendering.XRWorldInstance>();
                    Debug.Out($"[LightProbe] Startup timer fired: IsActiveInHierarchy={IsActiveInHierarchy}, world={w is not null}");
                    if (!IsActiveInHierarchy || w is null)
                        return;

                    FullCapture(128, false);
                    Debug.Out($"[LightProbe] FullCapture queued. Cubemap={EnvironmentTextureCubemap is not null}, Res={Resolution}, Octa={EnvironmentTextureOctahedral is not null}, IrradianceFBO={_irradianceFBO is not null}");
                }, TimeSpan.FromMilliseconds(1.0f));
            }
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
                    break;
                case nameof(UseDirectCubemapIblGeneration):
                    if (EnvironmentTextureEquirect is null && IsActiveInHierarchy)
                        InitializeForCapture();
                    CachePreviewSphere();
                    break;
                case nameof(PreviewDisplay):
                    CachePreviewSphere();
                    break;
                case nameof(RealtimeCapture):
                    if (RealtimeCapture)
                        _realtimeCaptureTimer.StartMultiFire(QueueCapture, RealTimeCaptureUpdateInterval ?? TimeSpan.Zero);
                    else
                        _realtimeCaptureTimer.Cancel();
                    break;
                case nameof(RealTimeCaptureUpdateInterval):
                    _realtimeCaptureTimer.TimeBetweenFires = RealTimeCaptureUpdateInterval ?? TimeSpan.Zero;
                    break;
            }
        }

        #endregion

        #region Transform Handling

        protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
        {
            _visualRC.WorldMatrix = renderMatrix;
            base.OnTransformRenderWorldMatrixChanged(transform, renderMatrix);
        }

        #endregion
    }
}
