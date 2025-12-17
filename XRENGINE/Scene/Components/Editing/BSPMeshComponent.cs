using XREngine.Components;
using XREngine.Rendering;

namespace XREngine.Scene.Components.Editing
{
    public class BSPMeshComponent : XRComponent
    {
        
    }
    public enum EIntersectionType
    {
        Union,
        Intersection,
        Subtraction,
        Merge,
        Attach,
        Insert,
    }
}
