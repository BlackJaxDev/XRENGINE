using XREngine.Rendering;
namespace XREngine.UnitTests.VR;
internal sealed class TestEyeCamera : IRuntimeVrEyeCamera
{
    public float Near { get; set; }
    public float Far { get; set; }
    public int CullingMask { get; set; }
}
