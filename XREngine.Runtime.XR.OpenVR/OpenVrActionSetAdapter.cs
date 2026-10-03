using OpenVR.NET.Manifest;
using XREngine.Input;

namespace XREngine;

internal sealed class OpenVrActionSetAdapter(RuntimeOpenVrActionSetDescriptor descriptor) : IActionSet
{
    public string Path => descriptor.Path;
    public Enum Name => descriptor.Name;
    public RuntimeOpenVrActionSetDescriptor Descriptor => descriptor;
}
