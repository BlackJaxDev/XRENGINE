using System.Runtime.InteropServices;
using XREngine.Rendering.OpenGL;

namespace XREngine.Rendering.UI;

public sealed partial class UltralightGpuWebRendererBackend
{
    private XRWindow? _ownerWindow;
    private IRuntimeWindowGlContext? _ownerContext;
    private OpenGLRenderer? _ownerRenderer;
    private long _ownerGeneration;
    private long _ownerRendererGeneration;
    private int _ownerThreadId;

    [DllImport("opengl32.dll", ExactSpelling = true)]
    private static extern nint wglGetCurrentContext();

    [DllImport("opengl32.dll", ExactSpelling = true)]
    private static extern nint wglGetCurrentDC();

    [DllImport("opengl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool wglMakeCurrent(nint deviceContext, nint context);

    private void CaptureOwnerContext(OpenGLRenderer renderer)
    {
        if (!OperatingSystem.IsWindows() ||
            !renderer.TryGetDesktopWindowHost(out IRuntimeRenderWindowHost? host) ||
            host is not XRWindow window || window.DesktopGlContext is not { } context ||
            !ReferenceEquals(window.Renderer, renderer))
            throw new InvalidOperationException("UI.Web.GpuContextUnavailable: a live desktop GL owner is required.");

        context.AssertOwnerThread();
        if (context.ContextHandle == 0 || context.DeviceContextHandle == 0 ||
            wglGetCurrentContext() != context.ContextHandle ||
            wglGetCurrentDC() != context.DeviceContextHandle)
            throw new InvalidOperationException("UI.Web.GpuContextMismatch: the web renderer requires its window GL context to be current.");

        _ownerWindow = window;
        _ownerContext = context;
        _ownerRenderer = renderer;
        _ownerGeneration = context.OwnerGeneration;
        _ownerRendererGeneration = renderer.BackendGeneration;
        _ownerThreadId = Environment.CurrentManagedThreadId;
    }

    public void ExecuteOnOwner(XRWindow window, Action action)
    {
        if (!OperatingSystem.IsWindows() || window.DesktopGlContext is not { } context ||
            window.Renderer is not OpenGLRenderer renderer || !renderer.AcceptsBackendWork)
            throw new InvalidOperationException("UI.Web.GpuContextUnavailable: the selected output has no live OpenGL owner.");
        if (_ownerContext is not null &&
            (!ReferenceEquals(_ownerWindow, window) || !ReferenceEquals(_ownerContext, context) ||
             !ReferenceEquals(_ownerRenderer, renderer) || _ownerRendererGeneration != renderer.BackendGeneration ||
             _ownerThreadId != Environment.CurrentManagedThreadId))
            throw new InvalidOperationException("UI.Web.GpuOwnerChanged: the web backend cannot migrate to another renderer generation.");
        context.AssertOwnerThread();
        using var scope = AbstractRenderer.EnterThreadCurrentScope(renderer);
        WithWglContext(context, action);
    }

    public void AssertCurrentOwner()
    {
        IRuntimeWindowGlContext context = _ownerContext
            ?? throw new InvalidOperationException("UI.Web.GpuContextUnavailable: no initialized web context exists.");
        if (!ReferenceEquals(_ownerWindow?.DesktopGlContext, context) ||
            context.OwnerGeneration != _ownerGeneration ||
            !ReferenceEquals(_ownerWindow?.Renderer, _ownerRenderer) ||
            _ownerRenderer?.BackendGeneration != _ownerRendererGeneration ||
            _ownerThreadId != Environment.CurrentManagedThreadId ||
            wglGetCurrentContext() != context.ContextHandle ||
            wglGetCurrentDC() != context.DeviceContextHandle)
            throw new InvalidOperationException("UI.Web.GpuContextChanged: the web producer lost its original GL owner.");
    }

    private void WithOwnerContext(Action action)
    {
        if (_view is null && _renderer is null && _readFramebufferId == 0)
        {
            Volatile.Write(ref _inputLifetime, null);
            while (_pendingInputEvents.TryDequeue(out _)) { }
            return;
        }

        IRuntimeWindowGlContext context = _ownerContext
            ?? throw new InvalidOperationException("UI.Web.GpuRetirementDeferred: no captured GL owner remains.");
        OpenGLRenderer? ownerRenderer = _ownerRenderer;
        if (!OperatingSystem.IsWindows() || _ownerWindow?.DesktopGlContext is not { } live ||
            !ReferenceEquals(live, context) || live.OwnerGeneration != _ownerGeneration ||
            ownerRenderer is null || !ReferenceEquals(_ownerWindow.Renderer, ownerRenderer) ||
            ownerRenderer.BackendGeneration != _ownerRendererGeneration ||
            !ownerRenderer.AcceptsBackendWork ||
            _ownerThreadId != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("UI.Web.GpuRetirementDeferred: the original GL window generation is unavailable.");

        context.AssertOwnerThread();
        WithWglContext(context, action);
    }

    private static void WithWglContext(IRuntimeWindowGlContext context, Action action)
    {
        nint previousContext = wglGetCurrentContext();
        nint previousDevice = wglGetCurrentDC();
        try
        {
            if (previousContext != context.ContextHandle || previousDevice != context.DeviceContextHandle)
                context.MakeCurrent();
            if (wglGetCurrentContext() != context.ContextHandle ||
                wglGetCurrentDC() != context.DeviceContextHandle)
                throw new InvalidOperationException("UI.Web.GpuContextMismatch: the original GL context could not be made current.");
            action();
        }
        finally
        {
            if ((wglGetCurrentContext() != previousContext || wglGetCurrentDC() != previousDevice) &&
                !wglMakeCurrent(previousDevice, previousContext))
                throw new InvalidOperationException("UI.Web.GpuContextRestoreFailed: the prior GL context could not be restored.");
        }
    }
}
