namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    //struct UniformBufferObject
    //{
    //    public Matrix4X4<float> model;
    //    public Matrix4X4<float> view;
    //    public Matrix4X4<float> proj;
    //}

    //private void DestroyUniformBuffers()
    //{
    //    for (int i = 0; i < uniformBuffers!.Length; i++)
    //        uniformBuffers![i].Destroy();
    //}

    //private void CreateUniformBuffers()
    //{
    //    uniformBuffers = new VkBuffer<UniformBufferObject>[swapChainImages!.Length];
    //    for (int i = 0; i < swapChainImages.Length; i++)
    //    {
    //        var buf = new VkBuffer<UniformBufferObject>(this, 1)
    //        {
    //            Usage = BufferUsageFlags.UniformBufferBit,
    //            Properties = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
    //        };
    //        buf.Generate();
    //        uniformBuffers[i] = buf;
    //    }
    //}

    //private void SubmitUniformBuffers(uint currentImage)
    //{
    //    //TODO: this is example code

    //    var time = (float)Window!.Time;

    //    UniformBufferObject ubo = new()
    //    {
    //        model = 
    //            Matrix4X4.CreateFromAxisAngle(
    //                new Vector3D<float>(0, 0, 1),
    //                time * XRMath.DegToRad(90.0f)),

    //        view = Matrix4X4.CreateLookAt(
    //            new Vector3D<float>(2, 2, 2),
    //            new Vector3D<float>(0, 0, 0),
    //            new Vector3D<float>(0, 0, 1)),

    //        proj = Matrix4X4.CreatePerspectiveFieldOfView(
    //            XRMath.DegToRad(45.0f),
    //            (float)swapChainExtent.Width / swapChainExtent.Height,
    //            0.1f,
    //            10.0f),
    //    };
    //    ubo.proj.M22 *= -1;

    //    uniformBuffers![currentImage].Set(0, ubo);
    //}
}