namespace XREngine.Rendering.OpenGL;

public unsafe partial class OpenGLRenderer
{
    public interface IGLObject : IRenderAPIObject
    {
        bool IsGenerated { get; }
        uint BindingId { get; }
        void Generate();
        void Destroy();
    }
}