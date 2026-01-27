namespace XREngine.Rendering.UI
{
    public class UITreeTransform : UIGridTransform
    {
        public UITreeTransform()
        {
            
        }
    }
    public class UITreeItemTransform : UIBoundableTransform
    {
        public UITreeTransform? OwningTreeComponent { get; set; }
        public int TreeDepth { get; set; }

        public UITreeItemTransform()
        {

        }
    }
}