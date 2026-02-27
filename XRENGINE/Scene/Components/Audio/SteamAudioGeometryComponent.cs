using System.ComponentModel;
using System.Numerics;
using XREngine.Audio;
using XREngine.Audio.Steam;
using XREngine.Components.Scene.Mesh;
using XREngine.Rendering;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;

namespace XREngine.Components;

/// <summary>
/// Bridges mesh geometry from the engine's scene graph into the Steam Audio acoustic scene.
/// <para>
/// Attach this component to any <see cref="SceneNode"/> that also has a
/// <see cref="ModelComponent"/> (or <see cref="RenderableComponent"/>). On activation
/// the component extracts triangle geometry from the sibling mesh component and creates
/// an <see cref="IPLStaticMesh"/> inside the processor's <see cref="SteamAudioScene"/>.
/// </para>
/// <para>
/// For movable geometry, set <see cref="IsDynamic"/> to <c>true</c>. Dynamic geometry is
/// re-extracted when the world transform changes substantially.
/// </para>
/// </summary>
[Category("Audio")]
[DisplayName("Steam Audio Geometry")]
[Description("Feeds sibling mesh geometry into the Steam Audio acoustic scene for occlusion/reflection simulation.")]
[XRComponentEditor("XREngine.Editor.ComponentEditors.SteamAudioGeometryComponentEditor")]
public class SteamAudioGeometryComponent : XRComponent
{
    // ------------------------------------------------------------------
    //  Serialized properties
    // ------------------------------------------------------------------

    private SteamAudioMaterial _material = SteamAudioMaterial.Default;
    private bool _isDynamic;

    /// <summary>
    /// Acoustic material applied to all triangles of this geometry.
    /// </summary>
    [Category("Steam Audio")]
    [DisplayName("Acoustic Material")]
    [Description("Acoustic surface properties (absorption, scattering, transmission) for this geometry.")]
    public SteamAudioMaterial Material
    {
        get => _material;
        set
        {
            _material = value ?? SteamAudioMaterial.Default;
            // If already registered we would need to re-add the mesh — mark dirty
            if (_meshId.HasValue)
                _dirty = true;
        }
    }

    /// <summary>
    /// When <c>true</c>, the geometry is treated as movable and the acoustic mesh is rebuilt
    /// when the world transform changes. Static geometry is baked once on activation.
    /// </summary>
    [Category("Steam Audio")]
    [DisplayName("Dynamic")]
    [Description("When true, the acoustic mesh is rebuilt when the object's transform changes.")]
    public bool IsDynamic
    {
        get => _isDynamic;
        set => _isDynamic = value;
    }

    // ------------------------------------------------------------------
    //  Runtime state
    // ------------------------------------------------------------------

    private SteamAudioScene? _scene;
    private uint? _meshId;
    private bool _dirty;

    /// <summary>Whether this component has successfully registered its geometry in the scene.</summary>
    [YamlIgnore]
    [Browsable(false)]
    public bool IsRegistered => _meshId.HasValue;

    // ------------------------------------------------------------------
    //  Lifecycle
    // ------------------------------------------------------------------

    protected internal override void OnComponentActivated()
    {
        base.OnComponentActivated();
        TryRegisterGeometry();

        if (_isDynamic)
            RegisterTick(ETickGroup.Late, ETickOrder.Scene, DynamicTick);
    }

    protected internal override void OnComponentDeactivated()
    {
        base.OnComponentDeactivated();
        UnregisterGeometry();
    }

    protected override void OnDestroying()
    {
        UnregisterGeometry();
        base.OnDestroying();
    }

    // ------------------------------------------------------------------
    //  Geometry extraction
    // ------------------------------------------------------------------

    /// <summary>
    /// Extracts all triangle geometry from the sibling <see cref="RenderableComponent"/>
    /// and registers it with the active Steam Audio scene.
    /// </summary>
    public void TryRegisterGeometry()
    {
        // Already registered
        if (_meshId.HasValue)
            return;

        // Find the scene from the active listener's effects processor
        var scene = FindActiveScene();
        if (scene is null)
        {
            Debug.Out("[SteamAudioGeometryComponent] No active SteamAudioScene found — skipping geometry registration.");
            return;
        }

        // Find sibling mesh component
        var renderable = GetSiblingComponent<RenderableComponent>();
        if (renderable is null)
        {
            Debug.Out("[SteamAudioGeometryComponent] No RenderableComponent sibling found — skipping.");
            return;
        }

        // Collect vertex and triangle data from all mesh LODs
        if (!ExtractGeometry(renderable, out var vertices, out var indices))
        {
            Debug.Out("[SteamAudioGeometryComponent] No triangle geometry available — skipping.");
            return;
        }

        SteamAudioMaterial[] materials = [_material];
        try
        {
            _meshId = scene.AddStaticMesh(vertices, indices, materials);
            _scene = scene;
            scene.Commit();
            Debug.Out($"[SteamAudioGeometryComponent] Registered {vertices.Length} verts, {indices.Length / 3} tris.");
        }
        catch (Exception ex)
        {
            Debug.Out($"[SteamAudioGeometryComponent] Failed to register geometry: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes previously registered geometry from the acoustic scene.
    /// </summary>
    public void UnregisterGeometry()
    {
        if (!_meshId.HasValue || _scene is null)
            return;

        try
        {
            _scene.RemoveStaticMesh(_meshId.Value);
            _scene.Commit();
            Debug.Out($"[SteamAudioGeometryComponent] Unregistered mesh #{_meshId.Value}.");
        }
        catch (Exception ex)
        {
            Debug.Out($"[SteamAudioGeometryComponent] Error unregistering: {ex.Message}");
        }
        finally
        {
            _meshId = null;
            _scene = null;
            _dirty = false;
        }
    }

    // ------------------------------------------------------------------
    //  Dynamic geometry update
    // ------------------------------------------------------------------

    private void DynamicTick()
    {
        if (!_dirty)
            return;

        // Rebuild: remove old mesh, re-add with current data
        UnregisterGeometry();
        TryRegisterGeometry();
        _dirty = false;
    }

    protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
    {
        if (_isDynamic && _meshId.HasValue)
            _dirty = true;
    }

    // ------------------------------------------------------------------
    //  Geometry extraction helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Walks the <see cref="RenderableComponent"/>'s meshes and collects world-space
    /// vertices and triangle indices. Uses the highest-detail LOD for each sub-mesh.
    /// </summary>
    private bool ExtractGeometry(
        RenderableComponent renderable,
        out Vector3[] vertices,
        out int[] indices)
    {
        vertices = [];
        indices = [];

        // Aggregate across all renderable meshes
        var allVertices = new List<Vector3>();
        var allIndices = new List<int>();

        Matrix4x4 worldMatrix = Transform.WorldMatrix;

        foreach (var rm in renderable.Meshes)
        {
            XRMesh? mesh = rm.CurrentLODMesh;
            if (mesh?.Triangles is null || mesh.Triangles.Count == 0)
                continue;

            int baseVertex = allVertices.Count;

            // Transform vertices to world space
            for (uint v = 0; v < mesh.VertexCount; v++)
            {
                Vector3 localPos = mesh.GetPosition(v);
                Vector3 worldPos = Vector3.Transform(localPos, worldMatrix);
                allVertices.Add(worldPos);
            }

            // Collect triangle indices (offset by base vertex)
            foreach (var tri in mesh.Triangles)
            {
                allIndices.Add(baseVertex + tri.Point0);
                allIndices.Add(baseVertex + tri.Point1);
                allIndices.Add(baseVertex + tri.Point2);
            }
        }

        if (allVertices.Count == 0 || allIndices.Count < 3)
            return false;

        vertices = [.. allVertices];
        indices = [.. allIndices];
        return true;
    }

    /// <summary>
    /// Finds the active <see cref="SteamAudioScene"/> from any listener in the current world
    /// that has a <see cref="SteamAudioProcessor"/> effects processor with a scene attached.
    /// If no scene exists yet, creates one on the first Steam Audio processor found.
    /// </summary>
    private SteamAudioScene? FindActiveScene()
    {
        if (World is null)
            return null;

        foreach (var listener in World.Listeners)
        {
            if (listener.EffectsProcessor is not SteamAudioProcessor processor)
                continue;

            // If the processor already has a scene, use it
            if (processor.Scene is not null)
                return processor.Scene;

            // Otherwise, create a new scene and attach it
            var scene = processor.CreateScene();
            scene.Commit(); // Commit the empty scene so SetScene doesn't reject it
            processor.SetScene(scene);
            return scene;
        }

        return null;
    }
}
