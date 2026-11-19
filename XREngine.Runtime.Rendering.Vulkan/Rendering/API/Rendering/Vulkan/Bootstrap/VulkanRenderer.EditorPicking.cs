using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public sealed partial class VulkanRenderer
{
    private int _editorPickingFormatSupport;

    public override bool SupportsEditorPickingIdentity(out string? failure)
    {
        if (PhysicalDevice.Handle == 0)
        {
            failure = "The Vulkan physical device is unavailable for editor picking.";
            return false;
        }
        int support = Volatile.Read(ref _editorPickingFormatSupport);
        if (support == 0)
        {
            Api!.GetPhysicalDeviceFormatProperties(PhysicalDevice, Format.R16G16B16A16Unorm,
                out FormatProperties properties);
            const FormatFeatureFlags required = FormatFeatureFlags.StorageImageBit |
                FormatFeatureFlags.ColorAttachmentBit | FormatFeatureFlags.ColorAttachmentBlendBit |
                FormatFeatureFlags.TransferSrcBit;
            support = (properties.OptimalTilingFeatures & required) == required &&
                _deviceContext.IndependentBlendEnabled ? 1 : -1;
            Volatile.Write(ref _editorPickingFormatSupport, support);
        }
        failure = support > 0 ? null :
            "Editor picking requires RGBA16 UNORM storage, color attachment, blend, transfer source, and independent blending support.";
        return support > 0;
    }

    public override bool TryQueueEditorPickingReadback(
        XRTexture identity,
        in AdvancedPickingQuery query,
        ulong requestToken,
        Action<ulong, EditorPickingEncodedPixel> callback,
        out string? failure)
    {
        if (!SupportsEditorPickingIdentity(out failure))
            return false;
        return _frameLoop.TryQueueEditorPickingReadback(identity, query, requestToken, callback, out failure);
    }
}
