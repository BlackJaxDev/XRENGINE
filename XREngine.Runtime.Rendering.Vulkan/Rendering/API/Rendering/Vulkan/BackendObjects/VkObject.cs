using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Generic Vulkan wrapper for one engine render object.
/// </summary>
internal abstract class VkObject<T> : VkObjectBase
    where T : GenericRenderObject
{
    public Device Device => BackendContext.Device;
    public PhysicalDevice PhysicalDevice => BackendContext.PhysicalDevice;

    public override string GetDescribingName()
        => $"{GetType()} {_bindingId}";

    public uint CacheObject(VkObject<T> obj)
        => BackendContext.Registry.Cache(obj);

    public VkObject<T>? GetCachedObject(uint id)
        => BackendContext.Registry.Get<T>(id);

    public void RemoveCachedObject(uint id)
        => BackendContext.Registry.Remove<T>(id);

    // Assign through the property so subclass hooks and wrapper links remain consistent.
    public VkObject(VulkanRenderer renderer, T data) : base(renderer.BackendObjectContext, renderer)
        => Data = data;

    protected VkObject(
        VulkanBackendObjectContext backendContext,
        IRenderApiWrapperOwner owner,
        T data) : base(backendContext, owner)
        => Data = data;

    /// <summary>
    /// Transitional compatibility for wrapper families that still need renderer-owned
    /// behavior. New wrappers should consume explicit backend facilities instead.
    /// </summary>
    protected VulkanRenderer Renderer => Owner as VulkanRenderer
        ?? throw new InvalidOperationException("Vulkan wrapper owner is not a VulkanRenderer.");

    protected override GenericRenderObject Data_Internal => Data;

    // Both constructors assign through the virtual Data property so wrapper links
    // are established consistently; the field is never observed before that assignment.
    private T _data = null!;
    private bool _dataLinked;
    public virtual T Data
    {
        get => _data;
        protected set
        {
            if (value == _data)
                return;

            if (_data is not null)
            {
                if (_dataLinked)
                    UnlinkData();
                _data.RemoveWrapper(this);
                _dataLinked = false;
            }

            _data = value;

            if (_data is not null)
            {
                _data.AddWrapper(this);
                LinkData();
                _dataLinked = true;
            }
        }
    }

    protected abstract void UnlinkData();
    protected abstract void LinkData();

    protected internal override void Retire()
    {
        if (_dataLinked)
        {
            UnlinkData();
            _dataLinked = false;
        }

        _data.RemoveWrapper(this);
        Destroy();
    }

    protected internal override void PostGenerated()
    {
        base.PostGenerated();
        BackendContext.Registry.Publish(BindingId, this);
    }

    protected internal override void PostDeleted()
    {
        BackendContext.Registry.Remove<T>(BindingId);
        base.PostDeleted();
    }
}
