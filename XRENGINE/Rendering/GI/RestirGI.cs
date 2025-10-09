using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.GI;

public static class RestirGI
{
    [DllImport("RestirGI.Native.dll")]
    public static extern void InitReSTIRRayTracingNV();

    [DllImport("RestirGI.Native.dll")]
    public static extern void BindReSTIRPipelineNV(uint pipeline);

    [DllImport("RestirGI.Native.dll")]
    public static extern void TraceRaysNVWrapper(uint sbtBuffer, uint sbtOffset, uint sbtStride, uint width, uint height);

    public static void Init()
        => InitReSTIRRayTracingNV();
    public static void Bind(int pipeline)
        => BindReSTIRPipelineNV((uint)pipeline);
    public static void Dispatch(int sbt, int w, int h)
        => TraceRaysNVWrapper((uint)sbt, 0, 0, (uint)w, (uint)h);

    public unsafe struct Reservoir
    {
        public Vector3 Li;              // sampled radiance
        public float Pdf;               // sample’s PDF

        public Vector3 Lprev;           // prev frame radiance (for temporal reuse)
        public float W;                 // reservoir weight

        public int M;                   // count of accepted samples
        public fixed byte Padding[12];  // pad to 32 bytes
    }
}