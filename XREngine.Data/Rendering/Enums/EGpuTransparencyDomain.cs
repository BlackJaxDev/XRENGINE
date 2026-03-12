namespace XREngine.Data.Rendering
{
    public enum EGpuTransparencyDomain : uint
    {
        OpaqueOrOther = 0,
        Masked = 1,
        TransparentApproximate = 2,
        TransparentExact = 3,
    }
}