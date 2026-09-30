using System.Runtime.InteropServices;
using XREngine.Rendering.GI;

namespace XREngine.Rendering.OpenGL;

/// <summary>Owns the GL_NV_ray_tracing native bridge and its capability diagnostics.</summary>
internal sealed class OpenGlRestirRayTracingBackend : IRestirRayTracingBackend
{
    private bool _initialized;
    private bool _supportLogged;
    private bool _supported;

    public string? LastFailure { get; private set; }

    public bool VerifySupport(bool logSuccess)
    {
        if (AbstractRenderer.Current?.BackendId != RendererBackendId.OpenGL)
        {
            LastFailure = "The ReSTIR native bridge requires an active OpenGL renderer and context.";
            return false;
        }
        if (_supportLogged)
            return _supported;

        try
        {
            _supported = IsSupportedNative();
            if (!_supported)
                LastFailure = "GL_NV_ray_tracing is unavailable on the active OpenGL device and context.";
            else if (logSuccess)
                Debug.Out(EOutputVerbosity.Normal, false, "ReSTIR NV ray tracing: GL_NV_ray_tracing reported by driver.");
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            LastFailure = $"The ReSTIR native bridge is unavailable: {exception.Message}";
        }
        _supportLogged = true;
        if (!_supported)
            Debug.LogWarning(LastFailure!);
        return _supported;
    }

    public bool TryInitialize()
    {
        if (!VerifySupport(false))
            return false;
        if (_initialized)
            return true;
        _initialized = InitializeNative();
        if (!_initialized)
            LastFailure = "The ReSTIR native bridge could not initialize its OpenGL ray tracing functions.";
        return _initialized;
    }

    public bool TryBind(uint pipeline)
    {
        if (!TryInitialize())
            return false;
        bool bound = BindNative(pipeline);
        if (!bound)
            LastFailure = "The ReSTIR native bridge could not bind the authored ray tracing pipeline.";
        return bound;
    }

    public bool TryDispatch(in RestirGI.TraceParameters parameters)
    {
        if (!TryInitialize())
            return false;
        bool dispatched = TraceNative(
            parameters.RaygenBuffer, parameters.RaygenOffset, parameters.RaygenStride,
            parameters.MissBuffer, parameters.MissOffset, parameters.MissStride,
            parameters.HitGroupBuffer, parameters.HitGroupOffset, parameters.HitGroupStride,
            parameters.CallableBuffer, parameters.CallableOffset, parameters.CallableStride,
            parameters.Width, parameters.Height, parameters.Depth);
        if (!dispatched)
            LastFailure = "The ReSTIR native bridge could not dispatch the authored shader binding tables.";
        return dispatched;
    }

    [DllImport("RestirGI.Native.dll", EntryPoint = "InitReSTIRRayTracingNV")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool InitializeNative();

    [DllImport("RestirGI.Native.dll", EntryPoint = "IsReSTIRRayTracingSupportedNV")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool IsSupportedNative();

    [DllImport("RestirGI.Native.dll", EntryPoint = "BindReSTIRPipelineNV")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool BindNative(uint pipeline);

    [DllImport("RestirGI.Native.dll", EntryPoint = "TraceRaysNVWrapper")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool TraceNative(
        uint raygenBuffer, uint raygenOffset, uint raygenStride,
        uint missBuffer, uint missOffset, uint missStride,
        uint hitGroupBuffer, uint hitGroupOffset, uint hitGroupStride,
        uint callableBuffer, uint callableOffset, uint callableStride,
        uint width, uint height, uint depth);
}
