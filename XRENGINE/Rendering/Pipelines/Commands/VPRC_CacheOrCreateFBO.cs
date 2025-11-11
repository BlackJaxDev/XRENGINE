namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_CacheOrCreateFBO : ViewportRenderCommand
    {
        /// <summary>
        /// The name of the FBO in the pipeline.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Factory method to create the FBO when it is not cached.
        /// </summary>
        public Func<XRFrameBuffer>? FrameBufferFactory { get; set; }

        /// <summary>
        /// This action is called when the FBO is already cached.
        /// Return the size the FBO should be with this action and the FBO will be resized if necessary.
        /// </summary>
        public Func<(uint x, uint y)>? SizeVerifier { get; set; }

        public void SetOptions(string name, Func<XRFrameBuffer> factory, Func<(uint x, uint y)>? sizeVerifier)
        {
            Name = name;
            FrameBufferFactory = factory;
            SizeVerifier = sizeVerifier;
        }

        protected override void Execute()
        {
            if (Name is null)
                return;

            if (ActivePipelineInstance.TryGetFBO(Name, out var fbo))
            {
                if (fbo is null || SizeVerifier is null)
                    return;

                (uint x, uint y) = SizeVerifier();
                if (fbo!.Width != x ||
                    fbo.Height != y)
                    fbo.Resize(x, y);
            }
            else if (FrameBufferFactory is not null)
            {
                fbo = FrameBufferFactory();
                fbo.Name = Name;
                ActivePipelineInstance.SetFBO(fbo);
            }
        }
    }
}
