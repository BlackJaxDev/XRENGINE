using XREngine.Rendering;

namespace XREngine.Components.Lights;

/// <summary>Persistent triple-buffered capture allocations belonging to one exact source camera.</summary>
internal sealed class MirrorCameraBank(XRCamera camera)
{
    internal readonly XRCamera Camera = camera;
    internal readonly MirrorCaptureSlot[] Slots = new MirrorCaptureSlot[MirrorCaptureComponent.CaptureSlotsPerCamera];
    internal MirrorCaptureSlot? Published;
    internal uint Width;
    internal uint Height;
}
