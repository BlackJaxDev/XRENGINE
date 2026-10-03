using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XREngine.Rendering;

/// <summary>Independent, bounded static scene resources and transforms for browser upload or offline export.</summary>
public sealed class BrowserSceneSnapshot
{
    private const int CurrentVersion = 2;
    private static readonly BrowserSceneJsonContext JsonContext = new(new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    });

    public BrowserSceneSnapshot(IEnumerable<BrowserMeshData> meshes, IEnumerable<BrowserMaterialData> materials,
        IEnumerable<BrowserSceneInstance> instances, BrowserCameraSnapshot camera)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        ArgumentNullException.ThrowIfNull(materials);
        ArgumentNullException.ThrowIfNull(instances);
        BrowserMeshData[] meshArray = meshes.Take(4097).ToArray();
        BrowserMaterialData[] materialArray = materials.Take(4097).ToArray();
        BrowserSceneInstance[] instanceArray = instances.Take(2049).ToArray();
        if (meshArray.Length > 4096 || materialArray.Length > 4096 || instanceArray.Length > 2048 ||
            meshArray.Any(static mesh => mesh is null) || materialArray.Any(static material => material is null))
            throw new ArgumentException("Browser scene resource capacity or non-null resource requirement was violated.");
        long payloadBytes = 0;
        foreach (BrowserMeshData mesh in meshArray)
            payloadBytes += (long)mesh.VertexCount * 20 + (long)mesh.IndexCount * 4;
        foreach (BrowserMaterialData material in materialArray)
            if (material.Texture is { } texture)
                payloadBytes += (long)texture.Width * texture.Height * 4;
        if (payloadBytes > 64 * 1024 * 1024)
            throw new ArgumentException("Browser scene decoded resource payload exceeds 64 MiB.");
        foreach (BrowserSceneInstance instance in instanceArray)
        {
            if ((uint)instance.MeshIndex >= meshArray.Length || (uint)instance.MaterialIndex >= materialArray.Length ||
                !IsFinite(instance.ModelMatrix))
                throw new ArgumentException("Browser scene instance has an invalid resource index or model matrix.", nameof(instances));
        }
        if (!IsFinite(camera.View) || !IsFinite(camera.Projection))
            throw new ArgumentException("Browser camera matrices must be finite.", nameof(camera));
        Meshes = Array.AsReadOnly(meshArray);
        Materials = Array.AsReadOnly(materialArray);
        Instances = Array.AsReadOnly(instanceArray);
        Camera = camera;
    }

    public IReadOnlyList<BrowserMeshData> Meshes { get; }
    public IReadOnlyList<BrowserMaterialData> Materials { get; }
    public IReadOnlyList<BrowserSceneInstance> Instances { get; }
    public BrowserCameraSnapshot Camera { get; }

    /// <summary>Writes a versioned, engine-type-free JSON payload with copied CPU resource data.</summary>
    public string ToJson()
    {
        BrowserSceneDto dto = new()
        {
            Version = CurrentVersion,
            CameraView = MatrixToArray(Camera.View),
            CameraProjection = MatrixToArray(Camera.Projection),
            Meshes = Meshes.Select(static mesh => new BrowserSceneMeshDto { Vertices = mesh.CopyVertices(), Indices = mesh.CopyIndices() }).ToArray(),
            Materials = Materials.Select(static material => new BrowserSceneMaterialDto
            {
                Tint = [material.Tint.X, material.Tint.Y, material.Tint.Z, material.Tint.W],
                AlphaMode = material.AlphaMode, Shading = material.Shading, CullMode = material.CullMode,
                AlphaCutoff = material.AlphaCutoff, CastShadow = material.CastShadow, ReceiveShadow = material.ReceiveShadow,
                Texture = material.Texture is { } texture
                    ? new BrowserSceneTextureDto { Width = texture.Width, Height = texture.Height, Rgba = texture.CopyRgbaBytes() }
                    : null
            }).ToArray(),
            Instances = Instances.Select(static instance => new BrowserSceneInstanceDto
            {
                MeshIndex = instance.MeshIndex,
                MaterialIndex = instance.MaterialIndex,
                ModelMatrix = MatrixToArray(instance.ModelMatrix)
            }).ToArray()
        };
        string json = JsonSerializer.Serialize(dto, JsonContext.BrowserSceneDto);
        if (Encoding.UTF8.GetByteCount(json) > 16 * 1024 * 1024)
            throw new NotSupportedException("Browser scene JSON exceeds 16 MiB.");
        return json;
    }

    public static BrowserSceneSnapshot FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > 16 * 1024 * 1024 || Encoding.UTF8.GetByteCount(json) > 16 * 1024 * 1024)
            throw new ArgumentException("Browser scene JSON exceeds 16 MiB.", nameof(json));
        BrowserSceneDto dto;
        try
        {
            dto = JsonSerializer.Deserialize(json, JsonContext.BrowserSceneDto)
                ?? throw new ArgumentException("Browser scene JSON is empty.", nameof(json));
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Browser scene JSON has invalid or unknown fields.", nameof(json), exception);
        }
        if (dto.Version is not (1 or CurrentVersion) || dto.Meshes is null || dto.Materials is null || dto.Instances is null ||
            dto.Meshes.Length > 4096 || dto.Materials.Length > 4096 || dto.Instances.Length > 2048)
            throw new NotSupportedException("Browser scene version or resource capacity is unsupported.");
        BrowserMeshData[] meshes = new BrowserMeshData[dto.Meshes.Length];
        for (int i = 0; i < meshes.Length; i++)
        {
            BrowserSceneMeshDto mesh = dto.Meshes[i] ?? throw new ArgumentException($"Mesh {i} is null.", nameof(json));
            meshes[i] = new BrowserMeshData(
                mesh.Vertices ?? throw new ArgumentException($"Mesh {i} has no vertices.", nameof(json)),
                mesh.Indices ?? throw new ArgumentException($"Mesh {i} has no indices.", nameof(json)));
        }
        BrowserMaterialData[] materials = new BrowserMaterialData[dto.Materials.Length];
        for (int i = 0; i < materials.Length; i++)
        {
            BrowserSceneMaterialDto material = dto.Materials[i] ?? throw new ArgumentException($"Material {i} is null.", nameof(json));
            BrowserTextureData? texture = null;
            if (material.Texture is { } pixels)
                texture = new BrowserTextureData(pixels.Width, pixels.Height,
                    pixels.Rgba ?? throw new ArgumentException($"Material {i} texture has no pixels.", nameof(json)));
            if (dto.Version == 1 && (material.AlphaMode != "opaque" || material.Shading != "unlit" || material.CullMode != "none" ||
                material.AlphaCutoff != 0.5f || !material.CastShadow || !material.ReceiveShadow || VectorFromArray(material.Tint).W != 1))
                throw new NotSupportedException("Legacy scene materials require opaque unlit defaults.");
            materials[i] = new BrowserMaterialData(VectorFromArray(material.Tint), texture, material.AlphaMode, material.Shading,
                material.CullMode, material.AlphaCutoff, material.CastShadow, material.ReceiveShadow);
        }
        BrowserSceneInstance[] instances = new BrowserSceneInstance[dto.Instances.Length];
        for (int i = 0; i < instances.Length; i++)
        {
            BrowserSceneInstanceDto instance = dto.Instances[i] ?? throw new ArgumentException($"Instance {i} is null.", nameof(json));
            instances[i] = new BrowserSceneInstance(instance.MeshIndex, instance.MaterialIndex, MatrixFromArray(instance.ModelMatrix));
        }
        return new BrowserSceneSnapshot(meshes, materials, instances,
            new BrowserCameraSnapshot(MatrixFromArray(dto.CameraView), MatrixFromArray(dto.CameraProjection)));
    }

    private static Vector4 VectorFromArray(float[]? values)
        => values is { Length: 4 } ? new(values[0], values[1], values[2], values[3])
            : throw new ArgumentException("Material tint requires four components.");

    private static float[] MatrixToArray(Matrix4x4 m)
        => [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
            m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];

    private static Matrix4x4 MatrixFromArray(float[]? v)
        => v is { Length: 16 } ? new(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7],
            v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15])
            : throw new ArgumentException("Matrix requires sixteen components.");

    private static bool IsFinite(Matrix4x4 m)
        => float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14) &&
           float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24) &&
           float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34) &&
           float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);

}
