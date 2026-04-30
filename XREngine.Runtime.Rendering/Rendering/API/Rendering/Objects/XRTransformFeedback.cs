using XREngine.Data.Rendering;

namespace XREngine.Rendering
{
    public enum EFeedbackType
    {
        OutValues,
        PerVertex,
    }

    /// <summary>
    /// Render object used for retreiving data from the GPU.
    /// </summary>
    /// <param name="type"></param>
    /// <param name="bindingLocation"></param>
    /// <param name="names"></param>
    public class XRTransformFeedback(EFeedbackType type, uint bindingLocation, params string[] names) : GenericRenderObject()
    {
        public uint BindingLocation
        {
            get => bindingLocation;
            set => bindingLocation = value;
        }
        public string[] Names
        {
            get => names;
            set => names = value;
        }
        public EFeedbackType Type
        {
            get => type;
            set => type = value;
        }
        public XRDataBuffer FeedbackBuffer { get; } = new("", EBufferTarget.TransformFeedbackBuffer, false);
    }
}