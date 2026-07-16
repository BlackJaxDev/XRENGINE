using XREngine.Components.Lights;

namespace XREngine.Scene
{
    public partial class Lights3DCollection
    {
        public readonly record struct CaptureWorkItem(
            SceneCaptureComponentBase Component,
            ECaptureWorkType WorkType,
            int FaceIndex = -1);
    }
}
