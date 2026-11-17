using System;
using System.Globalization;
using System.Numerics;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanXrGraphicsBinding
{
    /// <summary>
    /// Log interval in XR frames from <see cref="XREngineEnvironmentVariables.OpenXrEyeViewDiagnostics"/>,
    /// or 0 when the log is off. Read once, so the off state costs one integer compare per eye.
    /// </summary>
    private static readonly int s_openXrEyeViewDiagnosticsInterval = ResolveOpenXrEyeViewDiagnosticsInterval();

    /// <summary>Auxiliary log that receives the eye view lines. Release sessions also write it.</summary>
    private const string OpenXrEyeViewDiagnosticsLogFileName = "openxr-eye-view-diagnostics.log";

    private static int ResolveOpenXrEyeViewDiagnosticsInterval()
    {
        string? value = XREnvironment.GetValue(XREngineEnvironmentVariables.OpenXrEyeViewDiagnostics);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int interval) && interval > 0
            ? interval
            : 0;
    }

    /// <summary>
    /// Writes one line that describes how an eye is about to render, so a per-eye run and a
    /// single-pass stereo run can be compared field by field: the viewport display and internal
    /// size, the internal-to-output scale, the anti-aliasing mode and TSR scale of the last frame
    /// profile, the camera FOV angles, the camera render-matrix translation (the late eye pose), and
    /// the camera position of the matching view in the published OpenXR view set (the collect-time
    /// pose). Writes nothing unless the diagnostics interval is set and the XR frame number is a
    /// multiple of it.
    /// </summary>
    private void LogOpenXrEyeViewDiagnostics(string path, uint viewIndex, XRViewport viewport, XRCamera camera)
    {
        int interval = s_openXrEyeViewDiagnosticsInterval;
        if (interval <= 0)
            return;

        int frameNumber = Context.PendingFrameNumber;
        if (frameNumber % interval != 0)
            return;

        XRRenderPipelineInstance pipeline = viewport.RenderPipelineInstance;
        float outputScale = viewport.Width > 0 ? viewport.InternalWidth / (float)viewport.Width : 0.0f;
        Vector3 renderTranslation = camera.Transform.RenderMatrix.Translation;
        string fov = camera.Parameters is XROpenXRFovCameraParameters fovParameters
            ? FormattableString.Invariant(
                $"{fovParameters.AngleLeft:F4}/{fovParameters.AngleRight:F4}/{fovParameters.AngleUp:F4}/{fovParameters.AngleDown:F4}")
            : camera.Parameters?.GetType().Name ?? "<none>";

        string published = "<none>";
        if (RenderFrameViewSetPublication.TryGetLatest(out RenderFrameViewSet views))
        {
            for (int index = 0; index < views.ViewCount; index++)
            {
                RenderFrameViewDescriptor view = views.GetView(index);
                if (view.OpenXrViewIndex != (int)viewIndex)
                    continue;

                Vector4 position = view.CameraPositionAndNear;
                published = FormattableString.Invariant($"{position.X:F4},{position.Y:F4},{position.Z:F4}");
                break;
            }
        }

        Debug.WriteAuxiliaryLog(
            OpenXrEyeViewDiagnosticsLogFileName,
            FormattableString.Invariant(
                $"path={path} xrFrame={frameNumber} view={viewIndex} display={viewport.Width}x{viewport.Height} internal={viewport.InternalWidth}x{viewport.InternalHeight} scale={outputScale:F3} aa={pipeline.EffectiveAntiAliasingModeThisFrame?.ToString() ?? "<none>"} tsrScale={pipeline.EffectiveTsrRenderScaleThisFrame?.ToString("F3", CultureInfo.InvariantCulture) ?? "<none>"} fovLRUD={fov} renderPos={renderTranslation.X:F4},{renderTranslation.Y:F4},{renderTranslation.Z:F4} publishedPos={published}"));
    }
}
