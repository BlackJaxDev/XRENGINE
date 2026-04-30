namespace XREngine.Rendering.OpenGL
{
    public class PrePushDataCallback
    {
        public bool ShouldPush { get; set; } = true;
        public bool AllowPostPushCallback { get; set; } = true;
    }
}