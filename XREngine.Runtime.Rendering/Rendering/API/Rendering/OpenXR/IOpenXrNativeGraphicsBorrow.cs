namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>
/// Pins the OpenXR instance and loader dispatch while a renderer resolves or invokes native graphics functions.
/// Function pointers must not outlive this borrow.
/// </summary>
public interface IOpenXrNativeGraphicsBorrow : IDisposable
{
    ulong InstanceHandle { get; }
    nint GetInstanceProcAddress(string name);
}
