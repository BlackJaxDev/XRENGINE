using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanMeshFrameDataFamilyKey(
    int FrameDataSlot,
    EVulkanMeshFrameDataStreamKind StreamKind,
    EVulkanFrameOpContextKind ContextKind,
    int PipelineIdentity,
    int ViewportIdentity,
    int OutputFrameBufferIdentity,
    int OutputTargetIdentity,
    int CameraIdentity,
    int StereoRightEyeCameraIdentity,
    bool StereoEnabled,
    bool MultiviewEnabled,
    ulong CaptureOutputId = 0,
    RenderCaptureOwnerToken? CaptureOwnerToken = null)
{
    public static VulkanMeshFrameDataFamilyKey From(
        int frameDataSlot,
        EVulkanMeshFrameDataStreamKind streamKind,
        in FrameOpContext context,
        in PendingMeshDraw draw)
    {
        // A capture face keeps its draw range when its physical target changes.
        // The shared viewport identity cannot distinguish components or faces.
        ulong captureOutputId = context.OutputSchedulingRequest.OutputKind is
            EFrameOutputKind.SceneCapture or EFrameOutputKind.LightProbeCapture or EFrameOutputKind.ReflectionProbeCapture
                ? context.OutputSchedulingRequest.OutputId : 0UL;
        return new(
            frameDataSlot,
            streamKind,
            context.ContextKind,
            context.PipelineIdentity,
            context.ViewportIdentity,
            captureOutputId == 0 ? context.OutputFrameBufferIdentity : 0,
            captureOutputId == 0 ? context.OutputTargetIdentity : 0,
            draw.Camera is null ? 0 : RuntimeHelpers.GetHashCode(draw.Camera),
            draw.StereoRightEyeCamera is null ? 0 : RuntimeHelpers.GetHashCode(draw.StereoRightEyeCamera),
            context.StereoEnabled,
            context.MultiviewEnabled,
            captureOutputId,
            captureOutputId == 0 ? null : context.CaptureOwnerToken);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = FrameDataSlot;
            hash = (hash * 397) ^ (int)StreamKind;
            hash = (hash * 397) ^ (int)ContextKind;
            hash = (hash * 397) ^ PipelineIdentity;
            hash = (hash * 397) ^ ViewportIdentity;
            hash = (hash * 397) ^ OutputFrameBufferIdentity;
            hash = (hash * 397) ^ OutputTargetIdentity;
            hash = (hash * 397) ^ CaptureOutputId.GetHashCode();
            hash = (hash * 397) ^ (CaptureOwnerToken is null ? 0 : RuntimeHelpers.GetHashCode(CaptureOwnerToken));
            hash = (hash * 397) ^ CameraIdentity;
            hash = (hash * 397) ^ StereoRightEyeCameraIdentity;
            hash = (hash * 397) ^ (StereoEnabled ? 1 : 0);
            return (hash * 397) ^ (MultiviewEnabled ? 1 : 0);
        }
    }
}
