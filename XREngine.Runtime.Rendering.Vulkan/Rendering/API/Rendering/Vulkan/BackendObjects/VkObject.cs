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
        => BackendContext.Resources.BackendObjects.Cache(obj);

    public VkObject<T>? GetCachedObject(uint id)
        => BackendContext.Resources.BackendObjects.Get<T>(id);

    /// <summary>
    /// Resolves or creates a context-owned wrapper without routing through the
    /// renderer facade. Cross-wrapper dependencies therefore share the same
    /// generation-local backend registry and factory.
    /// </summary>
    protected AbstractRenderAPIObject? GetBackendWrapper(GenericRenderObject data, bool generateNow)
        => WrapperLookup.GetOrCreate(data, generateNow);

    public void RemoveCachedObject(uint id)
        => BackendContext.Resources.BackendObjects.Remove<T>(id);

    /// <summary>
    /// Creates a context-owned wrapper. The backend context is both the native
    /// facility provider and the wrapper cache owner, so no renderer backlink is
    /// retained by migrated wrapper families.
    /// </summary>
    protected VkObject(
        VulkanBackendObjectContext backendContext,
        T data) : base(backendContext, backendContext)
        => _data = data ?? throw new ArgumentNullException(nameof(data));

    protected VkObject(
        VulkanBackendObjectContext backendContext,
        IRenderApiWrapperOwner owner,
        T data) : base(backendContext, owner)
        => _data = data ?? throw new ArgumentNullException(nameof(data));

    /// <summary>
    /// Completes wrapper construction after the factory has bound the exact
    /// operation ports required by this wrapper family.
    /// </summary>
    internal override void CompleteConstruction()
    {
        if (_dataLinked)
            return;

        _data.AddWrapper(this);
        try
        {
            LinkData();
            _dataLinked = true;
        }
        catch
        {
            try { UnlinkData(); }
            finally { _data.RemoveWrapper(this); }
            throw;
        }
    }

    protected override GenericRenderObject Data_Internal => Data;

    // The factory binds narrow behavior ports before completing the data link.
    // This prevents constructor-time callbacks from observing unpublished services.
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
        BackendContext.Resources.BackendObjects.Publish(BindingId, this);
    }

    protected internal override void PostDeleted()
    {
        BackendContext.Resources.BackendObjects.Remove(Data);
        BackendContext.Resources.BackendObjects.Remove<T>(BindingId);
        base.PostDeleted();
    }
}
