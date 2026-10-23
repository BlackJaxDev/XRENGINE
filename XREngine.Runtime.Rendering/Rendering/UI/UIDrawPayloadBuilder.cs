using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.UI;

/// <summary>Captures bounded resource copies and reuses an unchanged owned draw.</summary>
public sealed class UIDrawPayloadBuilder : IDisposable
{
    public const long MaximumRetainedBytes = 128L * 1024 * 1024;
    public const long MaximumPayloadBytes = 16L * 1024 * 1024;
    public const int MaximumRetainedPayloads = 4096;
    private static readonly object BudgetLock = new();
    private static long _retainedBytes;
    private static int _retainedPayloads;
    private UIDrawPayload? _cached;
    private UIDrawSourceObserver? _sourceObserver;
    internal event Action? SourceChanged;
    private void NotifySourceChanged() => SourceChanged?.Invoke();
    private Vector4 _capturedBounds;
    public Vector4 Bounds { get; set; }
    public IUIDrawProducerLease? ProducerLease { get; set; }
    public int ProducerTextureSlot { get; set; }
    private IUIDrawProducerLease? _capturedProducerLease;
    private ulong _producerGeneration;
    private int _producerTextureSlot;
    private UITextureSnapshot?[] _capturedTextures = [];
    private XRMeshRenderer? _sourceRenderer;
    private XRMesh? _sourceMesh;
    private XRShader[] _sourceShaders = [];
    private XRMaterial? _sourceMaterial;
    private long _geometryRevision;
    private long[] _shaderRevisions = [];
    private KeyValuePair<string, XRDataBuffer>[] _meshBuffers = [];
    private KeyValuePair<string, XRDataBuffer>[] _rendererBuffers = [];
    private readonly Action<XRMaterialBase, XRRenderProgram>? _allowedMaterialHandler;

    internal UIDrawPayloadBuilder(Action<XRMaterialBase, XRRenderProgram>? allowedMaterialHandler)
        => _allowedMaterialHandler = allowedMaterialHandler;
    public UIDrawPayloadBuilder() { }

    /// <summary>Copies callback-free authored state. Known data adapters can replace renderer callbacks.</summary>
    public bool TryCapture(RenderCommandMesh2D source, bool allowRendererCallbacks,
        out UIDrawPayload? payload, out string? failure)
    {
        payload = null;
        failure = null;
        XRMeshRenderer? renderer = source.Mesh;
        XRMaterial? material = source.MaterialOverride ?? renderer?.Material;
        if (renderer?.Mesh is not { } mesh || material is null)
        {
            failure = "UI.DrawCapture.MissingResources: a visible draw requires a mesh and material.";
            return false;
        }
        if ((!allowRendererCallbacks && (renderer.HasSettingUniformsHandlers || renderer.HasRenderDataPreparation)) ||
            renderer.BindingPublishers.Count != 0 || material.BindingPublishers.Count != 0 ||
            !material.HasOnlyPublicationUniformHandler(_allowedMaterialHandler) || material.HasSettingVertexUniformHandlers)
        {
            failure = "UI.DrawCapture.CallbackAdapterRequired: capture callback values and producer resources in TryCapturePublicationPayload.";
            return false;
        }
        if (mesh.HasSkinning || mesh.HasBlendshapes || renderer.DeformMeshRenderer is not null ||
            renderer.HasExternalSkinPaletteSource || renderer.Submeshes.Count != 0)
        {
            failure = "UI.DrawCapture.DeformationAdapterRequired: bones, blend shapes, and submeshes require an owned data adapter.";
            return false;
        }
        if (_cached is { } cached && Matches(source, renderer, material, mesh, cached))
        {
            payload = cached;
            return true;
        }

        long reservedBytes = 0;
        XRMesh? ownedMesh = null;
        XRMeshRenderer? ownedRenderer = null;
        XRMaterial? ownedMaterial = null;
        XRShader[] shaders = [];
        IUIDrawProducerLease? producerLease = null;
        UITextureSnapshot?[] capturedTextures = [];
        try
        {
            if (mesh.Buffers.Count > 256 || renderer.Buffers.Count > 256)
                throw new NotSupportedException("UI buffer count exceeds the bounded draw capture profile.");
            KeyValuePair<string, XRDataBuffer>[] meshBuffers = mesh.Buffers.CaptureBindingSnapshot(out _);
            KeyValuePair<string, XRDataBuffer>[] rendererBuffers = renderer.Buffers.CaptureBindingSnapshot(out _);
            long bytes = checked(4096L + material.Parameters.Length * 2048L + material.Textures.Count * 64L +
                (mesh.Points?.Count ?? 0) * 4L + (mesh.Lines?.Count ?? 0) * 8L + (mesh.Triangles?.Count ?? 0) * 128L);
            foreach (KeyValuePair<string, XRDataBuffer> entry in meshBuffers)
                bytes = checked(bytes + MeasureBuffer(entry.Value));
            foreach (KeyValuePair<string, XRDataBuffer> entry in rendererBuffers)
            {
                if (entry.Value.BindingIndexOverride is null)
                    throw new NotSupportedException($"UI buffer '{entry.Key}' requires a fixed binding index.");
                bytes = checked(bytes + MeasureBuffer(entry.Value));
            }
            if (material.Parameters.Length > 4096 || material.Shaders.Count > 32 || material.Textures.Count > 256)
                throw new NotSupportedException("UI material exceeds the bounded draw capture profile.");
            foreach (ShaderVar parameter in material.Parameters)
                bytes = checked(bytes + MeasureParameter(parameter));
            foreach (XRShader shader in material.Shaders)
                bytes = checked(bytes + shader.GetResolvedSource().Length * 2L + 512L);
            if (!TryReserveBudget(bytes))
            {
                failure = "UI.DrawCapture.Capacity: retained draw resources exceed the bounded capture budget; keep the previous complete publication.";
                return false;
            }
            reservedBytes = bytes;
            if (ProducerLease is { } producer)
            {
                if (producer.Generation == 0 || ProducerTextureSlot < 0 || ProducerTextureSlot >= material.Textures.Count)
                    throw new NotSupportedException("UI producer requires a nonzero generation and a valid material texture slot.");
                producer.Retain();
                producerLease = producer;
            }
            XRTexture?[] textures = new XRTexture?[material.Textures.Count];
            capturedTextures = new UITextureSnapshot?[textures.Length];
            for (int index = 0; index < textures.Length; index++)
            {
                if (producerLease is not null && index == ProducerTextureSlot)
                    textures[index] = producerLease.Texture;
                else if (material.Textures[index] is { } texture)
                {
                    if (!UITextureSnapshot.TryAcquire(texture, out UITextureSnapshot? snapshot, out string? textureFailure))
                        throw new NotSupportedException(textureFailure);
                    capturedTextures[index] = snapshot;
                    textures[index] = snapshot!.Texture;
                }
            }
            using RenderObjectPublicationScope publication = GenericRenderObject.BeginDeferredPublication();
            ShaderVar[] parameters = new ShaderVar[material.Parameters.Length];
            for (int index = 0; index < parameters.Length; index++)
                parameters[index] = UIDrawStateCapture.CloneParameter(material.Parameters[index]);
            shaders = new XRShader[material.Shaders.Count];
            long[] revisions = new long[shaders.Length];
            XRShader[] sourceShaders = new XRShader[shaders.Length];
            for (int index = 0; index < shaders.Length; index++)
            {
                XRShader shader = material.Shaders[index];
                shaders[index] = new XRShader(shader.Type, shader.GetResolvedSource())
                {
                    SourceLanguage = shader.SourceLanguage,
                    EntryPoint = shader.EntryPoint,
                    SlangOptions = shader.SlangOptions,
                    GenerateAsync = shader.GenerateAsync,
                    IsGeneratedUberVariant = shader.IsGeneratedUberVariant,
                    GeneratedUberVariantHash = shader.GeneratedUberVariantHash,
                    CookedArtifact = shader.CookedArtifact,
                    CookedArtifactIdentity = shader.CookedArtifactIdentity,
                };
                revisions[index] = shader.SourceRevision;
                sourceShaders[index] = shader;
            }
            ownedMaterial = new XRMaterial(parameters, textures, shaders)
            {
                BillboardMode = material.BillboardMode,
                TransparencyMode = material.TransparencyMode,
                AlphaCutoff = material.AlphaCutoff,
                TransparentSortPriority = material.TransparentSortPriority,
                TransparentTechniqueOverride = material.TransparentTechniqueOverride,
                RenderPass = material.RenderPass,
                RenderOptions = UIDrawStateCapture.CloneRaster(material.RenderOptions),
            };
            // Constructor shader discovery must not remove captured arrays or manually authored parameters.
            ownedMaterial.Parameters = parameters;
            // The deep clone owns all CPU buffer bytes. Shared clones are not valid here.
            ownedMesh = mesh.Clone();
            ownedRenderer = new XRMeshRenderer(ownedMesh, ownedMaterial);
            XRDataBuffer[] ownedBuffers = new XRDataBuffer[rendererBuffers.Length];
            for (int index = 0; index < rendererBuffers.Length; index++)
            {
                KeyValuePair<string, XRDataBuffer> entry = rendererBuffers[index];
                XRDataBuffer buffer = entry.Value.Clone(true, entry.Value.Target);
                buffer.DisposeOnPush = false;
                ownedRenderer.Buffers.Add(entry.Key, buffer);
                ownedBuffers[index] = buffer;
            }
            // Republish actual texture and buffer identities inside the required-resource scope.
            ownedRenderer.BindingPublishers.Add(new UIFixedDrawBindings(ownedBuffers, textures, Bounds));
            RenderingParameters? options = source.RenderOptionsOverride is { } raster
                ? UIDrawStateCapture.CloneRaster(raster) : null;
            UIDrawPayload replacement = new(ownedRenderer, ownedMaterial, options, shaders, reservedBytes, producerLease, capturedTextures);
            publication.Complete();
            UIDrawSourceObserver observer = new(source, renderer, mesh, material, sourceShaders,
                meshBuffers, rendererBuffers, capturedTextures, NotifySourceChanged);
            _sourceObserver?.Dispose();
            _sourceObserver = observer;
            UIDrawPayload? previous = _cached;
            _cached = replacement;
            _sourceRenderer = renderer;
            _sourceMesh = mesh;
            _sourceShaders = sourceShaders;
            _sourceMaterial = material;
            _geometryRevision = mesh.GeometryRevision;
            _capturedBounds = Bounds;
            _capturedProducerLease = producerLease;
            _producerGeneration = producerLease?.Generation ?? 0;
            _producerTextureSlot = ProducerTextureSlot;
            _capturedTextures = capturedTextures;
            _shaderRevisions = revisions;
            _meshBuffers = meshBuffers;
            _rendererBuffers = rendererBuffers;
            reservedBytes = 0;
            producerLease = null;
            capturedTextures = [];
            payload = replacement;
            previous?.Release();
            return true;
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException or ArgumentException or OverflowException)
        {
            failure = $"UI.DrawCapture.Unsupported: {exception.GetBaseException().Message}";
            return false;
        }
        finally
        {
            if (reservedBytes != 0)
            {
                new UIFailedDrawCapture(ownedRenderer, ownedMesh, ownedMaterial, shaders,
                    capturedTextures, producerLease, reservedBytes).Retire();
            }
        }
    }

    private bool Matches(RenderCommandMesh2D source, XRMeshRenderer renderer, XRMaterial material, XRMesh mesh, UIDrawPayload cached)
    {
        if ((ProducerLease is not null && ProducerTextureSlot != _producerTextureSlot) ||
            !ReferenceEquals(ProducerLease, _capturedProducerLease) || (ProducerLease?.Generation ?? 0) != _producerGeneration ||
            (ProducerLease is not null && !ReferenceEquals(ProducerLease.Texture, cached.Material.Textures[ProducerTextureSlot])) ||
            Bounds != _capturedBounds || !ReferenceEquals(mesh, _sourceMesh) || !MeshMetadataEquals(mesh, cached.Renderer.Mesh!) ||
            !ReferenceEquals(renderer, _sourceRenderer) || !ReferenceEquals(material, _sourceMaterial) ||
            mesh.GeometryRevision != _geometryRevision || material.Shaders.Count != _shaderRevisions.Length ||
            material.Parameters.Length != cached.Material.Parameters.Length || material.Textures.Count != cached.Material.Textures.Count ||
            material.BillboardMode != cached.Material.BillboardMode || material.TransparencyMode != cached.Material.TransparencyMode ||
            material.RenderPass != cached.Material.RenderPass || material.AlphaCutoff != cached.Material.AlphaCutoff || material.TransparentSortPriority != cached.Material.TransparentSortPriority ||
            material.TransparentTechniqueOverride != cached.Material.TransparentTechniqueOverride ||
            !UIDrawStateCapture.RasterEquals(material.RenderOptions, cached.Material.RenderOptions) ||
            !UIDrawStateCapture.RasterEquals(source.RenderOptionsOverride, cached.RenderOptions))
            return false;
        for (int index = 0; index < _shaderRevisions.Length; index++)
            if (!ReferenceEquals(material.Shaders[index], _sourceShaders[index]) || material.Shaders[index].SourceRevision != _shaderRevisions[index] ||
                material.Shaders[index].Type != cached.Material.Shaders[index].Type)
                return false;
        for (int index = 0; index < material.Parameters.Length; index++)
            if (!UIDrawStateCapture.ParameterEquals(material.Parameters[index], cached.Material.Parameters[index]))
                return false;
        for (int index = 0; index < material.Textures.Count; index++)
        {
            if (ProducerLease is not null && index == ProducerTextureSlot)
                continue;
            XRTexture? texture = material.Textures[index];
            if (texture is null ? cached.Material.Textures[index] is not null :
                _capturedTextures[index]?.Matches(texture) != true)
                return false;
        }
        return BuffersEqual(mesh.Buffers, cached.Renderer.Mesh!.Buffers, _meshBuffers) &&
            BuffersEqual(renderer.Buffers, cached.Renderer.Buffers, _rendererBuffers);
    }

    private static bool MeshMetadataEquals(XRMesh source, XRMesh owned)
    {
        if (source.Interleaved != owned.Interleaved || source.InterleavedStride != owned.InterleavedStride ||
            source.PositionOffset != owned.PositionOffset || source.NormalOffset != owned.NormalOffset ||
            source.TangentOffset != owned.TangentOffset || source.ColorOffset != owned.ColorOffset ||
            source.TexCoordOffset != owned.TexCoordOffset || source.ColorCount != owned.ColorCount ||
            source.TexCoordCount != owned.TexCoordCount || source.VertexCount != owned.VertexCount ||
            source.Type != owned.Type || source.PatchVertices != owned.PatchVertices || source.Bounds.Min != owned.Bounds.Min || source.Bounds.Max != owned.Bounds.Max ||
            source.BindRootMatrix != owned.BindRootMatrix ||
            (source.Points?.Count ?? 0) != (owned.Points?.Count ?? 0) ||
            (source.Lines?.Count ?? 0) != (owned.Lines?.Count ?? 0) ||
            (source.Triangles?.Count ?? 0) != (owned.Triangles?.Count ?? 0))
            return false;
        if (source.Points is { } points)
            for (int index = 0; index < points.Count; index++)
                if (points[index] != owned.Points![index])
                    return false;
        if (source.Lines is { } lines)
            for (int index = 0; index < lines.Count; index++)
                if (lines[index].Point0 != owned.Lines![index].Point0 || lines[index].Point1 != owned.Lines[index].Point1)
                    return false;
        if (source.Triangles is { } triangles)
            for (int index = 0; index < triangles.Count; index++)
                if (!triangles[index].Equals(owned.Triangles![index]))
                    return false;
        return true;
    }

    private static unsafe bool BuffersEqual(XRMesh.BufferCollection source, XRMesh.BufferCollection owned,
        KeyValuePair<string, XRDataBuffer>[] previous)
    {
        if (source.Count != previous.Length || owned.Count != previous.Length)
            return false;
        foreach (KeyValuePair<string, XRDataBuffer> entry in previous)
        {
            if (!source.TryGetValue(entry.Key, out XRDataBuffer? current) || !ReferenceEquals(current, entry.Value) ||
                !owned.TryGetValue(entry.Key, out XRDataBuffer? copy) || current.Length != copy.Length ||
                current.Target != copy.Target || current.BindingIndexOverride != copy.BindingIndexOverride ||
                current.ComponentType != copy.ComponentType || current.ComponentCount != copy.ComponentCount ||
                current.ElementCount != copy.ElementCount || current.Normalize != copy.Normalize ||
                current.InstanceDivisor != copy.InstanceDivisor || current.ClientSideSource is not { } data ||
                copy.ClientSideSource is not { } frozen || data.Length != frozen.Length ||
                !new ReadOnlySpan<byte>(data.Address.Pointer, checked((int)data.Length)).SequenceEqual(
                    new ReadOnlySpan<byte>(frozen.Address.Pointer, checked((int)frozen.Length))))
                return false;
        }
        return true;
    }

    private static long MeasureParameter(ShaderVar parameter, int depth = 0)
    {
        if (depth > 8)
            throw new NotSupportedException("UI parameter nesting exceeds the capture limit.");
        long bytes = 2048L + parameter.Name.Length * 2L;
        if (parameter is not ShaderArrayBase)
            return bytes;
        ShaderVar[]? values = ((ShaderArrayBase)parameter).CaptureValues;
        if (values is null)
            return bytes;
        if (values.Length > UIDrawStateCapture.MaximumArrayLength)
            throw new NotSupportedException("UI parameter array exceeds the capture limit.");
        foreach (ShaderVar? child in values)
            if (child is ShaderVar variable)
                bytes = checked(bytes + MeasureParameter(variable, depth + 1));
        return bytes;
    }

    private static long MeasureBuffer(XRDataBuffer buffer)
    {
        if (buffer.ClientSideSource is not { } source || source.Length < buffer.Length || buffer.GpuCompressedSource is not null)
            throw new NotSupportedException($"UI buffer '{buffer.AttributeName}' requires complete uncompressed CPU-owned bytes.");
        return checked(source.Length + 512L);
    }

    private static bool TryReserveBudget(long bytes)
    {
        lock (BudgetLock)
        {
            if (bytes > MaximumPayloadBytes || _retainedBytes + bytes > MaximumRetainedBytes ||
                _retainedPayloads >= MaximumRetainedPayloads)
                return false;
            _retainedBytes += bytes;
            _retainedPayloads++;
            return true;
        }
    }

    internal static void ReleaseBudget(long bytes)
    {
        lock (BudgetLock)
        {
            _retainedBytes -= bytes;
            _retainedPayloads--;
        }
    }

    public void Dispose()
    {
        _sourceObserver?.Dispose();
        _sourceObserver = null;
        UIDrawPayload? previous = _cached;
        _cached = null;
        _sourceRenderer = null;
        _sourceMesh = null;
        _sourceShaders = [];
        _sourceMaterial = null;
        _capturedProducerLease = null;
        _capturedTextures = [];
        ProducerLease = null;
        _meshBuffers = [];
        _rendererBuffers = [];
        previous?.Release();
    }
}
