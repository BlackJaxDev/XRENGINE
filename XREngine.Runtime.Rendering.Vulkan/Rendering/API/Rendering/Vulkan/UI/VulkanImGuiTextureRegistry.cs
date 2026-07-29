using Silk.NET.Vulkan;
using XREngine.Rendering.Models.Materials.Textures;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns ImGui texture identities and their descriptor payloads for one renderer.
/// </summary>
internal sealed class VulkanImGuiTextureRegistry
{
    internal Dictionary<nint, DescriptorSet> DescriptorSets { get; } = [];
    internal Dictionary<nint, VulkanRenderer.DescriptorHeapPushDataPayload> DescriptorHeapPushData { get; } = [];
    internal Dictionary<XRTexture, VulkanImGuiTextureRegistration> Registrations { get; } = [];
    internal Dictionary<nint, XRTexture> TexturesById { get; } = [];
    internal nint NextTextureId { get; set; } = 2;

    internal void Clear()
    {
        DescriptorSets.Clear();
        DescriptorHeapPushData.Clear();
        Registrations.Clear();
        TexturesById.Clear();
        NextTextureId = 2;
    }
}
