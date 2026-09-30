namespace XREngine.Rendering;

/// <summary>
/// Native-callable addresses supplied by a non-collectible host assembly. Renderer modules hand
/// these to native libraries; each address dispatches back through
/// <see cref="RendererNativeCallbackBridge"/> or the installed clipboard service.
/// </summary>
public interface IRendererNativeCallbackEntryPoints
{
    nint StreamlineLog { get; }
    nint GetClipboardText { get; }
    nint SetClipboardText { get; }
    nint VulkanDebug { get; }
}
