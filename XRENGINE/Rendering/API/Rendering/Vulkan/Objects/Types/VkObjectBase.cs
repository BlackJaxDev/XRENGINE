
namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    public abstract class VkObjectBase(VulkanRenderer renderer) : AbstractRenderObject<VulkanRenderer>(renderer), IRenderAPIObject
    {
        public const uint InvalidBindingId = 0;
        public abstract VkObjectType Type { get; }

        public bool IsActive => _bindingId.HasValue && _bindingId != InvalidBindingId;

        internal uint? _bindingId;

        public override void Destroy()
        {
            if (!IsActive)
                return;

            PreDeleted();
            DeleteObjectInternal();
            PostDeleted();
        }

        protected internal virtual void PreGenerated()
        {

        }

        protected internal virtual void PostGenerated()
        {

        }

        public override void Generate()
        {
            if (IsActive)
                return;

            PreGenerated();
            _bindingId = CreateObjectInternal();
            PostGenerated();
        }

        protected internal virtual void PreDeleted()
        {

        }
        protected internal virtual void PostDeleted()
        {
            _bindingId = null;
        }

        public uint BindingId
        {
            get
            {
                try
                {
                    if (_bindingId is null)
                        Generate();
                    return _bindingId!.Value;
                }
                catch
                {
                    throw new Exception($"Failed to generate object of type {Type}.");
                }
            }
        }

        GenericRenderObject IRenderAPIObject.Data => Data_Internal;
        protected abstract GenericRenderObject Data_Internal { get; }

        protected abstract uint CreateObjectInternal();
        protected abstract void DeleteObjectInternal();
    }
}