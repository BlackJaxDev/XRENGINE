namespace XREngine.Rendering.Vulkan;
internal interface IVkObject : IRenderAPIObject
{
    bool IsActive { get; }
    //uint BindingId { get; }
    void Generate();
    void Destroy();
}
