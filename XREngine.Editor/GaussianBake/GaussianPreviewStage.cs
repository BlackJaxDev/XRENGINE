using System.Numerics;
using XREngine.Components.Lights;
using XREngine.Data.Colors;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor.GaussianBake;

/// <summary>
/// Builds the default preview stage that lights avatars and items during a gaussian preview bake.
/// The stage has no floor, so only the content shows in the captures.
/// </summary>
public static class GaussianPreviewStage
{
    /// <summary>The name of the root node of the default stage.</summary>
    public const string RootName = "GaussianPreviewStage";

    /// <summary>The intensity of the key light.</summary>
    public const float KeyLightIntensity = 3.0f;

    /// <summary>The intensity of the fill light.</summary>
    public const float FillLightIntensity = 1.0f;

    /// <summary>
    /// The intensity of the white ambient light. The engine keeps ambient light in the world settings, not in a
    /// component, so <see cref="BakeWorldFactory"/> applies this value to the bake world of an avatar or an item.
    /// </summary>
    public const float AmbientIntensity = 0.3f;

    /// <summary>The direction in which the key light travels.</summary>
    public static readonly Vector3 KeyLightDirection = new(-0.4f, -1.0f, -0.3f);

    /// <summary>The direction in which the fill light travels.</summary>
    public static readonly Vector3 FillLightDirection = new(0.5f, -0.3f, 0.6f);

    /// <summary>
    /// Creates the default stage: a directional key light, a directional fill light, and no floor.
    /// The lights cast no shadows, so that no shadow is baked into the splat colors.
    /// The ambient light is <see cref="AmbientIntensity"/>.
    /// </summary>
    public static SceneNode CreateDefault()
    {
        SceneNode root = new(RootName);
        AddDirectionalLight(root, "KeyLight", KeyLightDirection, KeyLightIntensity);
        AddDirectionalLight(root, "FillLight", FillLightDirection, FillLightIntensity);
        return root;
    }

    private static void AddDirectionalLight(SceneNode root, string name, Vector3 direction, float intensity)
    {
        SceneNode node = root.NewChild(name);
        Transform transform = node.SetTransform<Transform>();
        transform.Rotation = RotationFacing(Vector3.Normalize(direction));

        DirectionalLightComponent light = node.AddComponent<DirectionalLightComponent>(name)
            ?? throw new InvalidOperationException($"The stage cannot add the directional light '{name}'.");
        light.Color = new ColorF3(1.0f, 1.0f, 1.0f);
        light.DiffuseIntensity = intensity;
        light.CastsShadows = false;
    }

    /// <summary>
    /// Returns the rotation that turns the engine forward axis (−Z) to <paramref name="direction"/>.
    /// A light travels along the forward axis of its transform.
    /// </summary>
    private static Quaternion RotationFacing(Vector3 direction)
    {
        Vector3 up = MathF.Abs(Vector3.Dot(direction, Vector3.UnitY)) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(Matrix4x4.CreateWorld(Vector3.Zero, direction, up)));
    }
}
