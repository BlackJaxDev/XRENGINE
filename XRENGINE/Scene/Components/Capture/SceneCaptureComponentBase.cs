namespace XREngine.Components.Lights
{
    public abstract class SceneCaptureComponentBase : XRComponent
    {
        public abstract void CollectVisible();
        public abstract void SwapBuffers();
        public abstract void Render();
    }
}
