using System.Globalization;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Reads the Advanced virtual geometry settings once for the process. Two consumers must agree in
/// every frame: the preparation extractor, which gives a static meshlet draw (and, with the skinned
/// setting, a skinned meshlet draw) of a cluster hierarchy geometry the full meshlet range of every
/// level, and the Vulkan visibility runtime, which compiles
/// the task stage with the cluster cut that selects meshlets from that range. A change while they
/// run could give one frame a full range without the cut (every level drawn at once), so the first
/// read latches both values and a later change takes effect at the next start.
/// </summary>
/// <remarks>
/// Only the Vulkan Advanced path has the cut. On another backend <see cref="Enabled"/> is false and
/// the extractor keeps the level-0 range, so the OpenGL and WebGPU paths draw as before.
/// </remarks>
public static class AdvancedVirtualGeometryPolicy
{
    private static readonly Lock Sync = new();
    private static volatile bool _latched;
    private static bool _enabled;
    private static bool _skinnedEnabled;
    private static bool _deformationLodEnabled;
    private static float _pixelThreshold = 1.0f;

    /// <summary>
    /// True when the Vulkan Advanced path selects a cluster cut for static meshlet draws of cluster
    /// hierarchy geometry. Reads and latches the settings on the first call with a Vulkan renderer.
    /// </summary>
    public static bool Enabled
    {
        get
        {
            if (!_latched)
                Latch();
            return _latched && _enabled;
        }
    }

    /// <summary>
    /// True when skinned meshlet draws also get the cluster cut: <see cref="Enabled"/> and the
    /// skinned setting are both on. The extractor then gives a skinned draw of a cluster hierarchy
    /// geometry the full meshlet range, and the task stage compiles the skinned cut. The cut of a
    /// skinned draw uses one distance for every cluster, from the draw's posed bounds.
    /// </summary>
    public static bool SkinnedEnabled
    {
        get
        {
            if (!_latched)
                Latch();
            return _latched && _enabled && _skinnedEnabled;
        }
    }

    /// <summary>
    /// True when far skinned draws deform only the vertices of their coarsest allowed cluster
    /// level: <see cref="SkinnedEnabled"/> and the deformation level of detail setting are both
    /// on. The extractor then chooses a minimum level for each skinned draw, the deformation job
    /// deforms the prefix of the cooked deformation order for that level, and the skinned cut is
    /// clamped to that level. Both must agree in every frame, so the value is latched with the
    /// other settings.
    /// </summary>
    public static bool DeformationLodEnabled
    {
        get
        {
            if (!_latched)
                Latch();
            return _latched && _enabled && _skinnedEnabled && _deformationLodEnabled;
        }
    }

    /// <summary>The latched cut threshold, in render pixels of projected error.</summary>
    public static float PixelThreshold
    {
        get
        {
            if (!_latched)
                Latch();
            return _pixelThreshold;
        }
    }

    /// <summary>
    /// Defines that select the cut variant of the meshlet task stage, or an empty string when the
    /// cut is off. The off variant therefore compiles from the same source text as before.
    /// <c>XR_ADV_CLUSTER_LOD_CUT_SKINNED</c> extends the cut to skinned meshlet draws.
    /// </summary>
    public static string TaskPreamble
        => Enabled
            ? "#define XR_ADV_CLUSTER_LOD_CUT 1\n#define XR_ADV_CLUSTER_LOD_PIXEL_THRESHOLD " +
              PixelThreshold.ToString("0.0###", CultureInfo.InvariantCulture) + "\n" +
              (SkinnedEnabled ? "#define XR_ADV_CLUSTER_LOD_CUT_SKINNED 1\n" : string.Empty)
            : string.Empty;

    private static void Latch()
    {
        // Before the renderer exists the backend is unknown; read again on the next call.
        if (!RuntimeEngine.Rendering.State.IsVulkan)
            return;
        lock (Sync)
        {
            if (_latched)
                return;
            _enabled = RuntimeEngine.Rendering.Settings.AdvancedVirtualGeometry;
            _skinnedEnabled = RuntimeEngine.Rendering.Settings.AdvancedSkinnedVirtualGeometry;
            _deformationLodEnabled = RuntimeEngine.Rendering.Settings.AdvancedSkinnedDeformationLod;
            _pixelThreshold = RuntimeEngine.Rendering.Settings.AdvancedVirtualGeometryPixelThreshold;
            _latched = true;
            XREngine.Debug.Rendering(
                $"Advanced virtual geometry {(_enabled ? "on" : "off")}, skinned {(_enabled && _skinnedEnabled ? "on" : "off")}, skinned deformation level of detail {(_enabled && _skinnedEnabled && _deformationLodEnabled ? "on" : "off")}, threshold {_pixelThreshold:0.###} px (read once; changes apply at the next start).");
        }
    }
}
