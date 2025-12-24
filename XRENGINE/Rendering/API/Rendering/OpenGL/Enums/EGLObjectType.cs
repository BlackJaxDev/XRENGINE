namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        /// <summary>
        /// The type of render object that is handled by the renderer.
        /// </summary>
        public enum EGLObjectType
        {
            Buffer,
            Shader,
            Program,
            VertexArray,
            Query,
            ProgramPipeline,
            TransformFeedback,
            Sampler,
            Texture,
            Renderbuffer,
            Framebuffer,

            Material,
        }
    }
}