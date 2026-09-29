using System.Numerics;
using System.Text;
using System.Text.Json;
using XREngine.Rendering;

namespace XREngine.Browser;

public sealed partial class BrowserSceneSession
{
    private const int MaximumCookedAssetBytes = 1024 * 1024;
    private const int MaximumCookedTextureBytes = 4 * 1024 * 1024;
    private const long MaximumCookedRetainedBytes = 64 * 1024 * 1024;
    private readonly HashSet<string> _cookedAssetIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BrowserMeshData> _cookedMeshes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BrowserMaterialData> _cookedMaterials = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BrowserTextureData> _cookedTextures = new(StringComparer.Ordinal);
    private bool _cookedContent;
    private bool _cookedFailed;
    private int _cookedInstances;
    private int _cookedSceneChunks;
    private long _cookedRetainedBytes;
    private long _cookedGpuBytes;
    private long _peakCookedBridgeBytes;
    private long _peakCookedScratchBytes;
    private long _peakCookedUploadBytes;
    private long _cookedUploadedBytes;

    internal void EnableCookedContent() => _cookedContent = true;

    /// <summary>Admits one immutable resource or small scene increment at a frame boundary.</summary>
    public void UploadCookedAsset(string assetId, string kind, string metadataJson, byte[] payload)
    {
        ThrowIfFrameBusy();
        if (!_cookedContent || !_graphicsInitialized || _cookedFailed)
            throw new InvalidOperationException("Cooked uploads require a ready, healthy cooked scene session.");
        try
        {
            ValidateCookedId(assetId);
            ArgumentNullException.ThrowIfNull(payload);
            ArgumentNullException.ThrowIfNull(metadataJson);
            int limit = kind == "texture" ? MaximumCookedTextureBytes : MaximumCookedAssetBytes;
            if (payload.Length == 0 || payload.Length > limit || metadataJson.Length > 16384 ||
                Encoding.UTF8.GetByteCount(metadataJson) > 16384)
                throw new ArgumentException("Cooked upload exceeds its payload or metadata budget.");
            if (_cookedAssetIds.Count >= 4096 || _cookedAssetIds.Contains(assetId))
                throw new ArgumentException("Cooked asset identity is duplicate or the registry capacity is exhausted.");
            _peakCookedBridgeBytes = Math.Max(_peakCookedBridgeBytes,
                (long)payload.Length + metadataJson.Length * 2L);
            if (kind != "texture")
            {
                using JsonDocument metadata = JsonDocument.Parse(metadataJson);
                if (metadata.RootElement.ValueKind != JsonValueKind.Object ||
                    metadata.RootElement.EnumerateObject().MoveNext())
                    throw new ArgumentException("Non-texture cooked metadata must be an empty object.");
            }
            switch (kind)
            {
                case "mesh": UploadCookedMesh(assetId, payload); break;
                case "texture": UploadCookedTexture(assetId, metadataJson, payload); break;
                case "material": UploadCookedMaterial(assetId, payload); break;
                case "animation": UploadCookedAnimation(assetId, payload); break;
                case "collision": UploadCookedCollision(assetId, payload); break;
                case "scene": UploadCookedScene(payload); break;
                default: throw new NotSupportedException("Cooked asset kind is unsupported.");
            }
            _cookedAssetIds.Add(assetId);
            _cookedUploadedBytes += payload.Length;
        }
        catch
        {
            // A GPU bridge or scene-node failure can leave partially created resources.
            // The failed session cannot render or accept more content; its owner must dispose it.
            _cookedFailed = true;
            throw;
        }
    }

    /// <summary>Requires actual essential animated instances when compute deformation was explicitly requested.</summary>
    public void CompleteCookedEssentials()
    {
        ThrowIfFrameBusy();
        if (!_cookedContent || !_graphicsInitialized || _cookedFailed || _cookedSceneChunks == 0)
            throw new InvalidOperationException("Cooked essentials require a healthy resident scene.");
        if (_computeSkinning && CookedAnimatedInstanceCount == 0)
        {
            _cookedFailed = true;
            throw new NotSupportedException("Compute skinning requires an animated instance in the essential cooked scene.");
        }
    }

    private void UploadCookedAnimation(string assetId, byte[] payload)
    {
        BrowserCookedAnimationDto dto = JsonSerializer.Deserialize(payload,
            BrowserCookedJsonContext.Default.BrowserCookedAnimationDto)
            ?? throw new ArgumentException("Cooked animation payload is null.");
        ValidateCookedId(dto.Mesh);
        if (!_cookedMeshes.TryGetValue(dto.Mesh, out BrowserMeshData? mesh))
            throw new ArgumentException("Cooked animation mesh dependency is not resident.");
        BrowserCookedAnimationAsset asset = new(dto, mesh);
        ReserveCookedBytes(asset.RetainedBytes);
        RegisterCookedAnimation(assetId, asset);
        _cookedRetainedBytes += asset.RetainedBytes;
        _peakCookedScratchBytes = Math.Max(_peakCookedScratchBytes, asset.RetainedBytes);
    }

    private void UploadCookedMesh(string assetId, byte[] payload)
    {
        BrowserCookedMeshDto dto = JsonSerializer.Deserialize(payload,
            BrowserCookedJsonContext.Default.BrowserCookedMeshDto)
            ?? throw new ArgumentException("Cooked mesh payload is null.");
        ArgumentNullException.ThrowIfNull(dto.Vertices);
        ArgumentNullException.ThrowIfNull(dto.Indices);
        long bytes = ((long)dto.Vertices.Length + dto.Indices.Length) * sizeof(float);
        if (bytes > MaximumCookedAssetBytes)
            throw new ArgumentException("Cooked mesh decoded payload exceeds one MiB.");
        ReserveCookedBytes(bytes);
        _peakCookedScratchBytes = Math.Max(_peakCookedScratchBytes, bytes);
        _peakCookedUploadBytes = Math.Max(_peakCookedUploadBytes, bytes);
        BrowserMeshData mesh = new(dto.Vertices, dto.Indices);
        AcquireMesh(mesh); // Registry pin permits upload before its first instance arrives.
        _cookedMeshes.Add(assetId, mesh);
        _cookedRetainedBytes += bytes;
        _cookedGpuBytes += bytes;
    }

    private void UploadCookedTexture(string assetId, string metadataJson, byte[] payload)
    {
        BrowserCookedTextureDto dto = JsonSerializer.Deserialize(metadataJson,
            BrowserCookedJsonContext.Default.BrowserCookedTextureDto)
            ?? throw new ArgumentException("Cooked texture metadata is null.");
        if (dto.MipByteLengths is not { Length: > 0 and <= 14 } || dto.Format is null ||
            dto.NormalConvention is null || dto.AlphaMode is null || dto.Width <= 0 || dto.Height <= 0 ||
            dto.Width > _quality.MaxTextureDimension || dto.Height > _quality.MaxTextureDimension)
            throw new ArgumentException("Cooked texture metadata exceeds the selected quality policy.");
        long total = 0;
        foreach (int length in dto.MipByteLengths)
        {
            if (length <= 0)
                throw new ArgumentException("Cooked texture mip payloads must be nonempty.");
            total += length;
        }
        if (total != payload.Length)
            throw new ArgumentException("Cooked texture mip lengths do not match its payload.");
        ReserveCookedBytes(total);
        byte[][] mips = new byte[dto.MipByteLengths.Length][];
        int offset = 0;
        for (int i = 0; i < mips.Length; i++)
        {
            mips[i] = payload.AsSpan(offset, dto.MipByteLengths[i]).ToArray();
            offset += dto.MipByteLengths[i];
        }
        _peakCookedScratchBytes = Math.Max(_peakCookedScratchBytes, total);
        _peakCookedUploadBytes = Math.Max(_peakCookedUploadBytes, total);
        BrowserTextureData texture = new(dto.Width, dto.Height, dto.Format, mips, dto.NormalConvention, dto.AlphaMode);
        AcquireTexture(texture);
        _cookedTextures.Add(assetId, texture);
        _cookedRetainedBytes += total;
        _cookedGpuBytes += total;
    }

    private void UploadCookedMaterial(string assetId, byte[] payload)
    {
        if (_cookedMaterials.Count >= 256)
            throw new ArgumentException("Cooked material capacity is exhausted.");
        BrowserCookedMaterialDto dto = JsonSerializer.Deserialize(payload,
            BrowserCookedJsonContext.Default.BrowserCookedMaterialDto)
            ?? throw new ArgumentException("Cooked material payload is null.");
        if (dto.Tint is not { Length: 4 } || dto.AlphaMode is null || dto.Shading is null || dto.CullMode is null)
            throw new ArgumentException("Cooked material requires a tint and explicit surface policy.");
        BrowserTextureData? texture = null;
        if (dto.Texture is not null)
        {
            ValidateCookedId(dto.Texture);
            if (!_cookedTextures.TryGetValue(dto.Texture, out texture))
                throw new ArgumentException("Cooked material texture dependency is not resident.");
        }
        ReserveCookedBytes(32);
        BrowserMaterialData material = new(new Vector4(dto.Tint[0], dto.Tint[1], dto.Tint[2], dto.Tint[3]),
            texture, dto.AlphaMode, dto.Shading, dto.CullMode, dto.AlphaCutoff, dto.CastShadow, dto.ReceiveShadow);
        AcquireMaterial(material);
        _cookedMaterials.Add(assetId, material);
        _cookedRetainedBytes += 32;
        _cookedGpuBytes += 32;
        _peakCookedScratchBytes = Math.Max(_peakCookedScratchBytes, 16);
    }

    private void UploadCookedScene(byte[] payload)
    {
        BrowserCookedSceneDto dto = JsonSerializer.Deserialize(payload,
            BrowserCookedJsonContext.Default.BrowserCookedSceneDto)
            ?? throw new ArgumentException("Cooked scene payload is null.");
        if (dto.Instances is null || dto.Instances.Length > 64 ||
            _cookedInstances + dto.Instances.Length > 2048)
            throw new ArgumentException("Cooked scene chunks permit at most 64 instances and 2048 per session.");
        BrowserCameraSnapshot camera = new(CookedMatrix(dto.CameraView), CookedMatrix(dto.CameraProjection));
        if (_cookedSceneChunks != 0 && _cameraOverride != camera)
            throw new ArgumentException("Cooked scene chunks must use the same camera.");
        ValidateCookedCollisionReference(dto.Collision);
        long bytes = dto.Instances.Length * 64L;
        ReserveCookedBytes(bytes);
        Matrix4x4[] transforms = new Matrix4x4[dto.Instances.Length];
        int animatedInstances = 0, animatedBones = 0, animatedVertices = 0;
        for (int i = 0; i < dto.Instances.Length; i++)
        {
            BrowserCookedInstanceDto instance = dto.Instances[i]
                ?? throw new ArgumentException("Cooked scene instance is null.");
            ValidateCookedId(instance.Mesh);
            ValidateCookedId(instance.Material);
            if (!_cookedMeshes.ContainsKey(instance.Mesh) || !_cookedMaterials.ContainsKey(instance.Material))
                throw new ArgumentException("Cooked scene instance dependencies are not resident.");
            if (instance.Occluder && (instance.Animation is not null || _cookedMaterials[instance.Material].AlphaMode != "opaque"))
                throw new ArgumentException("Cooked depth occluders must be opaque rigid instances.");
            if (instance.Animation is not null)
            {
                ValidateCookedId(instance.Animation);
                BrowserCookedAnimationAsset animation = GetCookedAnimationAsset(instance.Animation);
                if (animation.MeshId != instance.Mesh)
                    throw new ArgumentException("Cooked animation must reference the instance's bind mesh.");
                bytes += animation.PlayerRetainedBytes + animation.OutputMeshBytes;
                animatedInstances++;
                animatedBones += animation.BoneCount;
                animatedVertices += animation.VertexCount;
            }
            transforms[i] = CookedMatrix(instance.ModelMatrix);
            ValidateImportTransform(transforms[i], out _, out _, out _);
        }
        ValidateCookedAnimationCapacity(animatedInstances, animatedBones, animatedVertices);
        ReserveCookedBytes(bytes);
        _peakCookedScratchBytes = Math.Max(_peakCookedScratchBytes, bytes * 2 + 128);
        // Validate the whole chunk before adopting any nodes; bridge failures poison the session.
        for (int i = 0; i < dto.Instances.Length; i++)
        {
            BrowserCookedInstanceDto instance = dto.Instances[i];
            BrowserCookedAnimationAsset? animation = instance.Animation is null ? null : GetCookedAnimationAsset(instance.Animation);
            BrowserMeshData mesh = animation?.CreateOutputMesh() ?? _cookedMeshes[instance.Mesh];
            BrowserMeshComponent component = AddRenderableCore(mesh, _cookedMaterials[instance.Material],
                ImportTransform(transforms[i]), _renderables);
            component.IsOcclusionOccluder = instance.Occluder;
            if (animation is not null)
            {
                AttachCookedAnimation(component, animation);
                _cookedGpuBytes += animation.OutputMeshBytes + (_computeSkinning ? animation.ComputeGpuBytes : 0);
            }
        }
        if (_cookedSceneChunks == 0)
            SetCamera(camera);
        if (dto.Collision is not null)
            InstallCookedCollision(dto.Collision);
        _cookedInstances += dto.Instances.Length;
        _cookedSceneChunks++;
        _cookedRetainedBytes += bytes;
    }

    private void ReserveCookedBytes(long bytes)
    {
        if (bytes < 0 || _cookedRetainedBytes + bytes > MaximumCookedRetainedBytes)
            throw new ArgumentException("Cooked scene retained resource payload exceeds 64 MiB.");
    }

    private static Matrix4x4 CookedMatrix(float[]? values)
    {
        if (values is not { Length: 16 })
            throw new ArgumentException("Cooked matrix requires sixteen finite components.");
        Matrix4x4 matrix = new(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7],
            values[8], values[9], values[10], values[11], values[12], values[13], values[14], values[15]);
        if (!IsFinite(matrix))
            throw new ArgumentException("Cooked matrix requires sixteen finite components.");
        return matrix;
    }

    private static void ValidateCookedId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64 || id[0] is < 'a' or > 'z' ||
            id.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Cooked asset identity is invalid.");
        foreach (char c in id)
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-'))
                throw new ArgumentException("Cooked asset identity is invalid.");
    }

    /// <summary>Returns cold-path resource payload estimates, not process or driver memory measurements.</summary>
    public BrowserCookedContentStatistics CaptureCookedContentStatistics()
        => new(_cookedAssetIds.Count, _cookedMeshes.Count, _cookedMaterials.Count, _cookedTextures.Count,
            _cookedInstances, _cookedRetainedBytes, _cookedGpuBytes, _peakCookedBridgeBytes,
            _peakCookedScratchBytes, _peakCookedUploadBytes, _cookedUploadedBytes, _cookedFailed)
        {
            AnimationAssets = CookedAnimationAssetCount,
            AnimatedInstances = CookedAnimatedInstanceCount,
            CollisionBoxes = HasCookedCollision ? CookedCollisionBoxCount : 0
        };

    private void ClearCookedContent()
    {
        // ReleaseResources owns the physical registry pins and scene references together.
        _cookedAssetIds.Clear();
        _cookedMeshes.Clear();
        _cookedMaterials.Clear();
        _cookedTextures.Clear();
        ClearCookedCollision();
        _cookedRetainedBytes = 0;
        _cookedGpuBytes = 0;
    }
}
