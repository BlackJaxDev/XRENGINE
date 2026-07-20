namespace XREngine.Core.Reflection.Attributes
{
    public class DragRange : Attribute
    {
        public float Minimum { get; set; }
        public float Maximum { get; set; }
        public DragRange(float min, float max)
        {
            Minimum = min;
            Maximum = max;
        }
    }
}
