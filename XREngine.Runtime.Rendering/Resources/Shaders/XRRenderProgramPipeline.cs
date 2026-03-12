namespace XREngine.Rendering
{
    public class XRRenderProgramPipeline : GenericRenderObject
    {
        public XRRenderProgramPipeline() { }

        public EventList<XRRenderProgram> Programs { get; } = [];
    }
}
