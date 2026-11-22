using Newtonsoft.Json;

namespace XREngine.Runtime.Bootstrap;

/// <summary>
/// Settings of the avatar crowd benchmark in the unit-testing world. With
/// <see cref="Enabled"/> on, the world makes <see cref="AvatarCount"/> copies of the first
/// animated model import and puts them on rings around <see cref="Center"/>, so that the
/// avatar render paths can be measured at known distances.
/// </summary>
public class UnitTestingCrowdBenchmarkSettings
{
    /// <summary>The ring radii that <see cref="RingRadii"/> uses by default, in meters.</summary>
    public static readonly float[] DefaultRingRadii = [1.5f, 3.0f, 6.0f, 12.0f, 25.0f, 50.0f];

    /// <summary>
    /// Makes the copies of the first animated model import. False (the default) imports the
    /// avatar once, as before.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The number of avatars, the imported one included. The world clamps the value to
    /// 1 through <see cref="MaxAvatarCount"/>.
    /// </summary>
    public int AvatarCount { get; set; } = 25;

    /// <summary>
    /// The ring radii in meters. Avatar <c>i</c> goes on ring <c>i</c> modulo the ring count,
    /// so each ring gets the same number of avatars, plus or minus one. An empty list uses
    /// <see cref="DefaultRingRadii"/>.
    /// </summary>
    /// <remarks>
    /// This is a list, not an array: the editor copies the settings through JSON with preserved
    /// references, and the JSON reader cannot read a preserved array back. The read replaces the
    /// default list, so the saved radii do not add to the default radii.
    /// </remarks>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<float> RingRadii { get; set; } = [.. DefaultRingRadii];

    /// <summary>The world position at the center of the rings. Every avatar faces it.</summary>
    public UnitTestingWorldSettings.TranslationXYZ Center { get; set; } = new();

    /// <summary>
    /// The avatar render mode. A value other than
    /// <see cref="UnitTestCrowdBenchmarkMode.UseRenderSettings"/> replaces the virtual geometry
    /// values of <see cref="UnitTestingRenderSettings"/> while the benchmark is on.
    /// </summary>
    public UnitTestCrowdBenchmarkMode Mode { get; set; } = UnitTestCrowdBenchmarkMode.UseRenderSettings;

    /// <summary>The largest avatar count that the world accepts.</summary>
    public const int MaxAvatarCount = 512;

    /// <summary>
    /// Returns the virtual geometry values of <see cref="Mode"/>. Returns false when the
    /// benchmark is off or the mode keeps the render settings; the caller then keeps them.
    /// </summary>
    public bool TryResolveRenderMode(
        out bool virtualGeometry,
        out bool skinnedVirtualGeometry,
        out bool skinnedDeformationLod)
    {
        virtualGeometry = false;
        skinnedVirtualGeometry = false;
        skinnedDeformationLod = false;
        if (!Enabled)
            return false;

        switch (Mode)
        {
            case UnitTestCrowdBenchmarkMode.Triangles:
                return true;
            case UnitTestCrowdBenchmarkMode.ClusterCut:
                virtualGeometry = true;
                skinnedVirtualGeometry = true;
                return true;
            case UnitTestCrowdBenchmarkMode.DeformationLod:
                virtualGeometry = true;
                skinnedVirtualGeometry = true;
                skinnedDeformationLod = true;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Computes the world position and the yaw (radians, about +Y) of avatar
    /// <paramref name="index"/>. The avatar stands on its ring and faces the center.
    /// </summary>
    public void ResolvePlacement(int index, int avatarCount, out System.Numerics.Vector3 position, out float yaw)
    {
        IReadOnlyList<float> radii = RingRadii is { Count: > 0 } configured ? configured : DefaultRingRadii;
        int ringCount = radii.Count;
        int ring = index % ringCount;
        int slot = index / ringCount;
        // Avatars of the ring: ceil((avatarCount - ring) / ringCount).
        int ringAvatarCount = Math.Max(1, (avatarCount - ring + ringCount - 1) / ringCount);
        // Offset each ring by half a step of the first ring, so near avatars do not hide far ones.
        float angle = MathF.Tau * (slot + 0.5f * ring / ringCount) / ringAvatarCount;
        float radius = MathF.Max(0.0f, radii[ring]);
        System.Numerics.Vector3 center = new(Center.X, Center.Y, Center.Z);
        position = center + new System.Numerics.Vector3(MathF.Sin(angle) * radius, 0.0f, MathF.Cos(angle) * radius);
        // Face the center: the avatar's forward axis (+Z) points from its position to the center.
        yaw = angle + MathF.PI;
    }
}
