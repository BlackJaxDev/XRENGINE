
namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Base contract for a Vulkan wrapper associated with one renderer generation.
/// Device identity and wrapper binding identity are obtained through narrow
/// renderer-owned contexts rather than static caches.
/// </summary>
internal abstract class VkObjectBase(
    VulkanBackendObjectContext backendContext,
    IRenderApiWrapperOwner owner) :
    AbstractRenderAPIObject(owner),
    IRenderAPIObject
{
    internal VulkanBackendObjectContext BackendContext { get; } = backendContext;
    /// <summary>
    /// Deferred program wrapper port.  Wrapper identity may be constructed by the
    /// base renderer before device composition; the port becomes operational only
    /// after the generation publishes its command and planner authorities.
    /// </summary>
    // Wrapper base identity deliberately retains only creation-time services.
    // Frame planner, command recording, telemetry, and output facts are supplied
    // by their owners at an operation boundary, never retained by every wrapper.
    private VulkanProgramCreationPort? _programCreationPort;
    private VulkanWrapperLookupPort? _wrapperLookup;

    protected VulkanProgramCreationPort ProgramCreationPort => _programCreationPort ?? throw new InvalidOperationException("This wrapper has no program-creation port.");
    protected Silk.NET.Vulkan.Vk Api => BackendContext.Api;

    internal void BindDeferredPorts(VulkanWrapperPortBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        _programCreationPort = binding.TryGetProgramCreation();
        VulkanWrapperLookupPort lookup = binding.Lookup;
        if (Interlocked.CompareExchange(ref _wrapperLookup, lookup, null) is { } currentLookup && !ReferenceEquals(currentLookup, lookup))
            throw new InvalidOperationException("The Vulkan wrapper already belongs to a different lookup boundary.");
        BindOperationPorts(binding);
    }

    /// <summary>
    /// Allows a wrapper family to consume the narrow port it owns.  This hook is
    /// intentionally after the base creation binding so no common wrapper base
    /// becomes a retained all-authorities service bag.
    /// </summary>
    protected virtual void BindOperationPorts(VulkanWrapperPortBinding binding) { }

    /// <summary>Links engine data only after the factory has bound operation ports.</summary>
    internal abstract void CompleteConstruction();

    /// <summary>Returns only wrapper lookup behavior, never the port publisher.</summary>
    protected VulkanWrapperLookupPort WrapperLookup
        => Volatile.Read(ref _wrapperLookup) ?? throw new InvalidOperationException("The Vulkan wrapper lookup boundary has not been bound.");

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

        if (!BackendContext.IsLogicalDeviceReady || !BackendContext.IsDeviceOperational)
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
        => _bindingId = null;

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
