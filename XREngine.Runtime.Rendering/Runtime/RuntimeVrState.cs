using XREngine.Rendering;
using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine;

internal sealed class RuntimeVrState
{
    public bool IsInVR { get; set; }
    public bool IsOpenXRActive { get; set; }
    public XRViewport? LeftEyeViewport { get; set; }
    public XRViewport? RightEyeViewport { get; set; }
    public OpenXRAPI? OpenXRApi { get; set; }
    public RuntimeOpenVrApi OpenVRApi { get; } = new();
    public (XRCamera? LeftEyeCamera, XRCamera? RightEyeCamera, IRuntimeRenderWorld? World, SceneNode? HMDNode) ViewInformation { get; set; }
    public void InvokeRecalcMatrixOnDraw(RuntimeVrPoseTiming timing)
    {
    }
}
