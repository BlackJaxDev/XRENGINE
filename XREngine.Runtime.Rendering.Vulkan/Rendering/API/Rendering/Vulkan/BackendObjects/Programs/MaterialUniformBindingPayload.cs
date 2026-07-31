using System.Collections.Generic;
using XREngine.Rendering.Models.Materials.Shaders.Parameters;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable numeric bindings owned by one material revision. The payload is
/// shared by frame-local snapshots instead of copying the material dictionary
/// for every frame or draw.
/// </summary>
internal sealed class MaterialUniformBindingPayload(Dictionary<string, ProgramUniformValue> uniforms)
{
    internal Dictionary<string, ProgramUniformValue> Uniforms { get; } = uniforms;
}
