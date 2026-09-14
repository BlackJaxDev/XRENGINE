using XREngine.Rendering;

namespace XREngine.Components.Lights;

/// <summary>Two persistent capture allocations belong to one exact source camera.</summary>
internal sealed class MirrorCameraBank(XRCamera camera)
{
    internal readonly XRCamera Camera = camera;
    internal readonly MirrorCaptureSlot[] Slots = new MirrorCaptureSlot[MirrorCaptureComponent.CaptureSlotsPerCamera];
    internal MirrorCaptureSlot? Published;
    internal uint Width;
    internal uint Height;
}
