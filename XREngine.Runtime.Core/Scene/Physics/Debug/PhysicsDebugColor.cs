using XREngine.Data.Colors;

namespace XREngine.Scene.Physics.DebugVisualization;

/// <summary>
/// Converts engine colors to the RGBA8 layout consumed by debug shaders.
/// </summary>
public static class PhysicsDebugColor
{
    public static uint Pack(ColorF4 color)
    {
        uint r = PackChannel(color.R);
        uint g = PackChannel(color.G);
        uint b = PackChannel(color.B);
        uint a = PackChannel(color.A);
        return r | (g << 8) | (b << 16) | (a << 24);
    }

    private static uint PackChannel(float value)
        => (uint)(Math.Clamp(value, 0.0f, 1.0f) * 255.0f + 0.5f);
}
