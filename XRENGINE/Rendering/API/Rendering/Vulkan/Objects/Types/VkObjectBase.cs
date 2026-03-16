
namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    public abstract class VkObjectBase(VulkanRenderer renderer) : AbstractRenderObject<VulkanRenderer>(renderer), IRenderAPIObject
    {
        public const uint InvalidBindingId = 0;
        public abstract VkObjectType Type { get; }

        public bool IsActive => _bindingId.HasValue && _bindingId != InvalidBindingId;

        internal uint? _bindingId;

        /// <summary>
        /// Tracks whether <see cref="CreateObjectInternal"/> failed. When set, <see cref="Generate"/>
        /// becomes a no-op to avoid retrying a deterministically failing creation every frame.
        /// Reset by <see cref="ResetGenerationFailure"/> (e.g. after shader source changes).
        /// </summary>
        private bool _generationFailed;

        /// <summary>
        /// Clears the generation-failure flag so the next <see cref="Generate"/> call will retry.
        /// Call this when the underlying data changes (e.g. shader source reloaded).
        /// </summary>
        public void ResetGenerationFailure() => _generationFailed = false;

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
            if (IsActive || _generationFailed)
                return;

            PreGenerated();
            try
            {
                _bindingId = CreateObjectInternal();
            }
            catch
            {
                _generationFailed = true;
                throw;
            }
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