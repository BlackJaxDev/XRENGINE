using XREngine.Data.Rendering;

namespace XREngine.Rendering
{
    public class XRRenderQuery : GenericRenderObject
    {
        public EQueryTarget? CurrentQuery { get; set; } = null;
    }
}
