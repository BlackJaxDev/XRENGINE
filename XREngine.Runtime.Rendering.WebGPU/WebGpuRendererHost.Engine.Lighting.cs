using System.Numerics;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Scene;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private const int MaximumForwardDirectionalLights = 4;
    private const int MaximumForwardPointLights = 8;
    private const int MaximumForwardSpotLights = 8;
    private static readonly string[] DirectionNames = CreateLightNames("ForwardDirectional", MaximumForwardDirectionalLights, "Direction");
    private static readonly string[] DirectionColorNames = CreateLightNames("ForwardDirectional", MaximumForwardDirectionalLights, "ColorIntensity");
    private static readonly string[] PointPositionNames = CreateLightNames("ForwardPoint", MaximumForwardPointLights, "PositionRadius");
    private static readonly string[] PointColorNames = CreateLightNames("ForwardPoint", MaximumForwardPointLights, "ColorIntensity");
    private static readonly string[] PointBrightnessNames = CreateLightNames("ForwardPoint", MaximumForwardPointLights, "Brightness");
    private static readonly string[] SpotPositionNames = CreateLightNames("ForwardSpot", MaximumForwardSpotLights, "PositionRadius");
    private static readonly string[] SpotColorNames = CreateLightNames("ForwardSpot", MaximumForwardSpotLights, "ColorIntensity");
    private static readonly string[] SpotDirectionNames = CreateLightNames("ForwardSpot", MaximumForwardSpotLights, "DirectionExponent");
    private static readonly string[] SpotCutoffNames = CreateLightNames("ForwardSpot", MaximumForwardSpotLights, "CutoffsBrightness");

    private static string[] CreateLightNames(string prefix, int count, string field)
    {
        string[] names = new string[count];
        for (int i = 0; i < count; i++) names[i] = $"{prefix}{i}{field}";
        return names;
    }

    /// <summary>Reads the same engine light collections and numeric fields as the desktop forward shader.</summary>
    internal void PublishForwardLights(WebGpuRenderProgram program)
    {
        IRuntimeRenderWorld world = RuntimeEngine.Rendering.State.RenderingWorld
            ?? throw new InvalidOperationException("WebGPU.Lighting.WorldMissing: forward lighting requires the active engine render world.");
        Lights3DCollection lights = world.Lights;
        int directionalCount = lights.DynamicDirectionalLights.Count;
        int pointCount = lights.DynamicPointLights.Count;
        int spotCount = lights.DynamicSpotLights.Count;
        if (directionalCount > MaximumForwardDirectionalLights || pointCount > MaximumForwardPointLights || spotCount > MaximumForwardSpotLights)
            throw new NotSupportedException("WebGPU.Lighting.CapacityExceeded: the selected forward profile supports four directional, eight point, and eight spot lights; no light is silently dropped.");
        program.SetVector4("ForwardLightCounts", new Vector4(directionalCount, pointCount, spotCount, 0));
        program.SetVector4("GlobalAmbient", new Vector4(world.GetEffectiveAmbientColor(), 0));
        for (int i = 0; i < directionalCount; i++)
        {
            DirectionalLightComponent light = lights.DynamicDirectionalLights[i];
            RequireUnshadowedLight(light);
            program.SetVector4(DirectionNames[i], new Vector4(light.Transform.WorldForward, 0));
            program.SetVector4(DirectionColorNames[i], new Vector4(light.Color, light.DiffuseIntensity));
        }
        for (int i = 0; i < pointCount; i++)
        {
            PointLightComponent light = lights.DynamicPointLights[i];
            RequireUnshadowedLight(light);
            program.SetVector4(PointPositionNames[i], new Vector4(light.Transform.RenderTranslation, light.Radius));
            program.SetVector4(PointColorNames[i], new Vector4(light.Color, light.DiffuseIntensity));
            program.SetVector4(PointBrightnessNames[i], new Vector4(light.Brightness, 0, 0, 0));
        }
        for (int i = 0; i < spotCount; i++)
        {
            SpotLightComponent light = lights.DynamicSpotLights[i];
            RequireUnshadowedLight(light);
            program.SetVector4(SpotPositionNames[i], new Vector4(light.Transform.RenderTranslation, light.Distance));
            program.SetVector4(SpotColorNames[i], new Vector4(light.Color, light.DiffuseIntensity));
            program.SetVector4(SpotDirectionNames[i], new Vector4(light.Transform.RenderForward, light.Exponent));
            program.SetVector4(SpotCutoffNames[i], new Vector4(light.InnerCutoff, light.OuterCutoff, light.Brightness, 0));
        }
    }

    private static void RequireUnshadowedLight(LightComponent light)
    {
        if (light.CastsShadows)
            throw new NotSupportedException($"WebGPU.Lighting.ShadowProfileRequired: light '{light.Name}' requires a cooked shadow receiver and matching shadow attachments.");
    }
}
