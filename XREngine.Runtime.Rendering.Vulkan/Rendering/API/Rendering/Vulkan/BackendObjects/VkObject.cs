using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Generic Vulkan wrapper for one engine render object.
/// </summary>
public abstract class VkObject<T> : VkObjectBase
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

        //We want to set the property instead of the field here just in case subclasses override it.
        //It will never be set to null because the constructor requires a non-null value.
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public VkObject(VulkanRenderer renderer, T data) : base(renderer)
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
        => Data = data;

    protected override GenericRenderObject Data_Internal => Data;

    private T _data;
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
