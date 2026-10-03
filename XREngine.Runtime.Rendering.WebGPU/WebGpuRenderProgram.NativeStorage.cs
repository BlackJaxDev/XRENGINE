using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRenderProgram
{
    /// <summary>Publishes a canonical backend-owned arena without constructing a duplicate engine buffer.</summary>
    internal void BindStorageBuffer(uint binding, WebGpuOwnedStorageBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (!IsGenerated || buffer.Owner != Renderer || !buffer.IsGenerated || buffer.IsRetired)
            throw UnsupportedBinding(Data.Name ?? "program", "native storage must be ready and owned by the same live renderer");
        int selected = -1;
        for (int index = 0; index < Artifact.Resources.Length; index++)
        {
            ShaderStageResourceLayout resource = Artifact.Resources[index];
            if (resource.Contract.Kind != ShaderAbiResourceKind.StorageBuffer || resource.Contract.Binding != binding)
                continue;
            if (selected >= 0)
                throw UnsupportedBinding(resource.Contract.Name, "numeric native storage binding is ambiguous across groups");
            selected = index;
        }
        if (selected < 0)
            throw UnsupportedBinding(Data.Name ?? "program", "the native storage binding is absent from the cooked layout");
        ShaderStageResourceLayout target = Artifact.Resources[selected];
        if (buffer.ByteLength < target.Contract.ByteSize ||
            target.RuntimeArray && buffer.ByteLength % target.Contract.ByteSize != 0)
            throw UnsupportedBinding(target.Contract.Name, "the native arena range does not match its cooked storage ABI");
        _resourceHandles[selected] = buffer.ResourceHandle;
        _resourceSizes[selected] = buffer.ByteLength;
        _resourceOwners[selected] = buffer;
    }
}
