using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using XREngine.Browser;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor.Publishing;

/// <summary>Extracts detached authored worlds into the existing browser content cooker input contract.</summary>
public sealed partial class BrowserWorldPublishExporter
{
    private readonly string _directory;
    private readonly CancellationToken _cancellation;
    private readonly List<(string Id, string Kind, string Source, string[] Dependencies, BrowserCookedTextureDto? Texture)> _assets = [];
    private readonly Dictionary<XRMesh, string> _meshes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<XRMaterial, string> _materials = new(ReferenceEqualityComparer.Instance);
    private readonly List<BrowserCookedInstanceDto> _instances = [];
    private readonly HashSet<SceneNode> _visited = new(ReferenceEqualityComparer.Instance);
    private CameraComponent? _camera;
    private long _bytes;

    private BrowserWorldPublishExporter(string directory, CancellationToken cancellation)
    {
        _directory = Path.GetFullPath(directory);
        _cancellation = cancellation;
    }

    /// <summary>Exports a detached startup world; unsupported authored behavior is an error, never an omitted component.</summary>
    public static string Export(XRWorld world, string recipeDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipeDirectory);
        return new BrowserWorldPublishExporter(recipeDirectory, cancellationToken).ExportWorld(world);
    }

    private string ExportWorld(XRWorld world)
    {
        if (world.DefaultGameMode is not null)
            throw Unsupported(world.Name ?? "world", "custom game mode execution");
        Directory.CreateDirectory(_directory);
        foreach (XRScene scene in world.Scenes)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (scene.IsEditorOnly || !scene.IsVisible)
                continue;
            foreach (SceneNode root in scene.RootNodes)
            {
                if (root.Parent is not null)
                    throw Unsupported(root.Name ?? "root", "a scene root references a parent outside its scene");
                Visit(root, 0);
            }
        }
        CompleteAnimations();
        if (_camera is null)
            throw Unsupported(world.Name ?? "world", "a single active CameraComponent is required");
        if (_instances.Count == 0)
            throw Unsupported(world.Name ?? "world", "no supported visible model instances");
        BrowserCameraSnapshot camera = BrowserCameraSnapshot.FromXRCamera(_camera.Camera);
        List<string> roots = [];
        // Sixteen instances also bound worst-case mesh/material/animation dependencies below sixty-four.
        for (int first = 0; first < _instances.Count; first += 16)
        {
            BrowserCookedInstanceDto[] instances = _instances.Skip(first).Take(16).ToArray();
            string id = $"scene-{roots.Count}";
            string[] dependencies = instances.SelectMany(x => x.Animation is null
                ? new[] { x.Mesh, x.Material } : new[] { x.Mesh, x.Material, x.Animation! }).Distinct(StringComparer.Ordinal).ToArray();
            WriteAsset(id, "scene", new BrowserCookedSceneDto
            {
                CameraView = Matrix(camera.View), CameraProjection = Matrix(camera.Projection), Instances = instances,
            }, BrowserPublishJsonContext.Default.BrowserCookedSceneDto, dependencies);
            roots.Add(id);
        }
        return WriteRecipe(roots);
    }

    private void Visit(SceneNode node, int depth)
    {
        _cancellation.ThrowIfCancellationRequested();
        if (node.IsEditorOnly || !node.IsActiveSelf)
            return;
        if (depth > 128 || !_visited.Add(node) || _visited.Count > 8192)
            throw Unsupported(node.Name ?? "node", "cyclic, shared, or oversized scene hierarchy");
        if (node.World is not null)
            throw Unsupported(node.GetPath(), "export requires detached authored assets, not an active runtime world");
        if (node.Transform.GetType() != typeof(Transform))
            throw Unsupported(node.GetPath(), $"transform behavior '{node.Transform.GetType().FullName}'");
        node.Transform.RecalculateMatrices(true, true);
        foreach (XRComponent component in node.Components)
        {
            if (!component.IsActive)
                continue;
            if (component.GetType() == typeof(ModelComponent))
                AddModel((ModelComponent)component);
            else if (component.GetType() == typeof(CameraComponent))
            {
                CameraComponent camera = (CameraComponent)component;
                if (_camera is not null)
                    throw Unsupported(node.GetPath(), $"multiple cameras (also '{_camera.SceneNode.GetPath()}')");
                if (camera.DefaultRenderTarget is not null || camera.GetUserInterfaceOverlay() is not null || camera.CullingCameraOverride is not null)
                    throw Unsupported(node.GetPath(), "camera render targets, native UI, or custom culling");
                _camera = camera;
            }
            else if (!IsExportedAnimationComponent(component))
                throw Unsupported(node.GetPath(), $"component '{component.GetType().FullName}'");
        }
        foreach (var child in node.Transform.Children)
            if (child?.SceneNode is SceneNode childNode)
                Visit(childNode, depth + 1);
    }

    private void AddModel(ModelComponent component)
    {
        string path = component.SceneNode.GetPath();
        if (component.Model is null)
            throw Unsupported(path, "ModelComponent without a model asset");
        foreach (var subMesh in component.Model.Meshes)
        {
            if (subMesh.LODs.Count != 1)
                throw Unsupported(path, $"submesh '{subMesh.Name}' requires exactly one LOD; authored LOD switching is unavailable");
            var lod = subMesh.LODs.Min!;
            XRMesh mesh = lod.Mesh ?? throw Unsupported(path, "missing mesh asset");
            XRMaterial material = lod.Material ?? throw Unsupported(path, "missing material asset");
            string meshId;
            string? animationId = null;
            Matrix4x4 model = component.Transform.WorldMatrix;
            if (mesh.HasSkinning || mesh.HasBlendshapes)
                (meshId, animationId, model) = AddAnimatedMesh(component, mesh);
            else if (!_meshes.TryGetValue(mesh, out meshId!))
            {
                BrowserMeshData data;
                try { data = BrowserAssetAdapter.FromXRMesh(mesh); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException)
                { throw Unsupported(path, $"mesh '{mesh.Name}': {ex.Message}"); }
                meshId = $"mesh-{_meshes.Count}";
                WriteAsset(meshId, "mesh", new BrowserCookedMeshDto { Vertices = data.CopyVertices(), Indices = data.CopyIndices() },
                    BrowserPublishJsonContext.Default.BrowserCookedMeshDto, []);
                _meshes.Add(mesh, meshId);
            }
            if (!_materials.TryGetValue(material, out string? materialId))
            {
                materialId = $"material-{_materials.Count}";
                AddMaterial(material, materialId, path);
                _materials.Add(material, materialId);
            }
            if (_instances.Count >= 2048)
                throw Unsupported(path, "more than 2048 model instances");
            _instances.Add(new BrowserCookedInstanceDto { Mesh = meshId, Material = materialId,
                ModelMatrix = Matrix(model), Animation = animationId, Occluder = false });
        }
    }

    private void WriteAsset<T>(string id, string kind, T value, JsonTypeInfo<T> type, string[] dependencies)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, type);
        WritePayload(id + ".json", bytes, 1024 * 1024);
        _assets.Add((id, kind, id + ".json", dependencies, null));
    }

    private void WritePayload(string name, byte[] bytes, int maximumBytes)
    {
        _cancellation.ThrowIfCancellationRequested();
        if (bytes.Length > maximumBytes || _bytes + bytes.Length > 64L * 1024 * 1024 || _assets.Count >= 4096)
            throw Unsupported(name, "browser asset or aggregate payload budget exceeded");
        _bytes += bytes.Length;
        File.WriteAllBytes(Path.Combine(_directory, name), bytes);
    }

    private string WriteRecipe(List<string> roots)
    {
        string path = Path.Combine(_directory, "recipe.json");
        using FileStream stream = File.Create(path);
        using Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject(); writer.WriteNumber("schema", 3);
        writer.WriteStartObject("services"); writer.WriteStartArray("required");
        if (_instances.Any(x => x.Animation is not null)) writer.WriteStringValue("cpu-animation");
        writer.WriteEndArray(); writer.WriteStartArray("optional"); writer.WriteStringValue("dom-ui"); writer.WriteEndArray(); writer.WriteEndObject();
        writer.WriteStartArray("entrypoints"); foreach (string root in roots) writer.WriteStringValue(root); writer.WriteEndArray();
        writer.WriteStartArray("streamed"); writer.WriteEndArray(); writer.WriteStartArray("assets");
        foreach (var asset in _assets)
        {
            writer.WriteStartObject(); writer.WriteString("id", asset.Id); writer.WriteString("kind", asset.Kind);
            writer.WriteStartArray("dependencies"); foreach (string dependency in asset.Dependencies) writer.WriteStringValue(dependency); writer.WriteEndArray();
            writer.WriteStartArray("variants"); writer.WriteStartObject(); writer.WriteString("source", asset.Source);
            writer.WriteString("encoding", asset.Texture is null ? "json" : "raw");
            writer.WriteStartArray("requiredFeatures"); writer.WriteEndArray();
            if (asset.Texture is not null)
                foreach (JsonProperty property in JsonSerializer.SerializeToElement(asset.Texture, BrowserPublishJsonContext.Default.BrowserCookedTextureDto).EnumerateObject())
                    property.WriteTo(writer);
            writer.WriteEndObject(); writer.WriteEndArray(); writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.WriteEndObject();
        return path;
    }

    private static float[] Matrix(Matrix4x4 m) => [m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44];
    private static NotSupportedException Unsupported(string path, string detail) => new($"Browser publish cannot preserve '{path}': {detail}.");
}
