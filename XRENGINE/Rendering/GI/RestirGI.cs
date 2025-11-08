using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.GI;

public static class RestirGI
{
    [DllImport("RestirGI.Native.dll", EntryPoint = "InitReSTIRRayTracingNV")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool InitReSTIRRayTracingNVNative();

    [DllImport("RestirGI.Native.dll", EntryPoint = "BindReSTIRPipelineNV")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool BindReSTIRPipelineNVNative(uint pipeline);

    [DllImport("RestirGI.Native.dll", EntryPoint = "TraceRaysNVWrapper")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool TraceRaysNVNative(
        uint raygenBuffer, uint raygenOffset, uint raygenStride,
        uint missBuffer, uint missOffset, uint missStride,
        uint hitGroupBuffer, uint hitGroupOffset, uint hitGroupStride,
        uint callableBuffer, uint callableOffset, uint callableStride,
        uint width, uint height, uint depth);

    private static bool _initialized;

    public static bool TryInit()
    {
        if (_initialized)
            return true;

        _initialized = InitReSTIRRayTracingNVNative();
        return _initialized;
    }

    public static void Init()
    {
        if (!TryInit())
            throw new InvalidOperationException("GL_NV_ray_tracing is not available on the current device.");
    }

    public static bool TryBind(uint pipeline)
    {
        if (!TryInit())
            return false;

        return BindReSTIRPipelineNVNative(pipeline);
    }

    public static void Bind(int pipeline)
    {
        if (!TryBind((uint)pipeline))
            throw new InvalidOperationException("Failed to bind the ReSTIR ray tracing pipeline.");
    }

    public static bool TryDispatch(in TraceParameters parameters)
    {
        if (!TryInit())
            return false;

        return TraceRaysNVNative(
            parameters.RaygenBuffer, parameters.RaygenOffset, parameters.RaygenStride,
            parameters.MissBuffer, parameters.MissOffset, parameters.MissStride,
            parameters.HitGroupBuffer, parameters.HitGroupOffset, parameters.HitGroupStride,
            parameters.CallableBuffer, parameters.CallableOffset, parameters.CallableStride,
            parameters.Width, parameters.Height, parameters.Depth);
    }

    public static void Dispatch(in TraceParameters parameters)
    {
        if (!TryDispatch(parameters))
            throw new InvalidOperationException("Failed to dispatch ReSTIR ray tracing rays.");
    }

    public static void Dispatch(int sbtBuffer, int sbtStride, int width, int height, int depth = 1)
    {
        var parameters = TraceParameters.CreateSingleTable(
            (uint)sbtBuffer,
            0u,
            (uint)sbtStride,
            (uint)width,
            (uint)height,
            (uint)depth);

        Dispatch(parameters);
    }

    public readonly struct TraceParameters
    {
        public uint RaygenBuffer { get; init; }
        public uint RaygenOffset { get; init; }
        public uint RaygenStride { get; init; }
        public uint MissBuffer { get; init; }
        public uint MissOffset { get; init; }
        public uint MissStride { get; init; }
        public uint HitGroupBuffer { get; init; }
        public uint HitGroupOffset { get; init; }
        public uint HitGroupStride { get; init; }
        public uint CallableBuffer { get; init; }
        public uint CallableOffset { get; init; }
        public uint CallableStride { get; init; }
        public uint Width { get; init; }
        public uint Height { get; init; }
        public uint Depth { get; init; }

        public static TraceParameters CreateSingleTable(
            uint buffer,
            uint offset,
            uint stride,
            uint width,
            uint height,
            uint depth = 1)
        {
            return new TraceParameters
            {
                RaygenBuffer = buffer,
                RaygenOffset = offset,
                RaygenStride = stride,
                MissBuffer = buffer,
                MissOffset = offset,
                MissStride = stride,
                HitGroupBuffer = buffer,
                HitGroupOffset = offset,
                HitGroupStride = stride,
                CallableBuffer = buffer,
                CallableOffset = offset,
                CallableStride = stride,
                Width = width,
                Height = height,
                Depth = depth
            };
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public unsafe struct Reservoir
    {
        public Vector3 Li;              // sampled radiance
        public float Pdf;               // sample’s PDF

        public Vector3 SampleDir;       // sampled direction in world space
        public float W;                 // reservoir weight sum

        public int M;                   // count of accepted samples
        public fixed byte Padding[12];  // pad to 32 bytes
    }
}