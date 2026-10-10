using XREngine.Extensions;
using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Gaussian;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Components.Scene.Mesh;

/// <summary>
/// Component that loads gaussian splat data and renders it using instanced point sprites.
/// </summary>
[Serializable]
public partial class GaussianSplatComponent : ModelComponent
{
    public GaussianSplatComponent()
    {
        Meshes.PostAnythingAdded += MeshAdded;
        Meshes.PostAnythingRemoved += MeshRemoved;
        PropertyChanged += ModelBindingChanged;
    }

    private string? _sourcePath;
    private long _sourcePathSetterSerial;
    private long _cloudLoadIntentSerial;
    public string? SourcePath
    {
        get => _sourcePath;
        set
        {
            long setterSerial = Interlocked.Increment(ref _sourcePathSetterSerial);
            long loadIntent = Interlocked.Increment(ref _cloudLoadIntentSerial);
            if (!SetField(ref _sourcePath, value))
                return;
            if (setterSerial != Volatile.Read(ref _sourcePathSetterSerial) ||
                loadIntent != Volatile.Read(ref _cloudLoadIntentSerial) ||
                !string.Equals(_sourcePath, value, StringComparison.Ordinal))
                return;
            CancelPendingCloudLoad();
            if (setterSerial != Volatile.Read(ref _sourcePathSetterSerial) ||
                loadIntent != Volatile.Read(ref _cloudLoadIntentSerial))
                return;
            _externalModelBinding = false;
            _externalCloudBinding = false;
            if (!string.IsNullOrWhiteSpace(value))
                LoadFromFileCore(value, loadIntent);
        }
    }

    private GaussianSplatCloud? _cloud;
    public GaussianSplatCloud? Cloud
    {
        get => _cloud;
        set
        {
            CloudLoadRequest? admission = _adoptingCloudRequest;
            _adoptingCloudRequest = null;
            bool adopting = admission is not null && ReferenceEquals(_pendingCloudLoad, admission) &&
                ReferenceEquals(value, admission.Cloud);
            if (ReferenceEquals(_cloud, value))
            {
                if (!adopting)
                {
                    Interlocked.Increment(ref _externalCloudAssignmentSerial);
                    Interlocked.Increment(ref _cloudLoadIntentSerial);
                    CancelPendingCloudLoad();
                    _externalCloudBinding = true;
                }
                return;
            }
            if (!adopting)
            {
                long cloudAssignment = Interlocked.Increment(ref _externalCloudAssignmentSerial);
                long loadIntent = Interlocked.Increment(ref _cloudLoadIntentSerial);
                CancelPendingCloudLoad();
                if (cloudAssignment != Volatile.Read(ref _externalCloudAssignmentSerial) ||
                    loadIntent != Volatile.Read(ref _cloudLoadIntentSerial))
                    return;
                _externalCloudBinding = true;
            }
            GaussianSplatCloud? previousCloud = _cloud;
            GaussianSplatCloud? previousOwned = _ownedCloud;
            long externalAssignmentSerial = Volatile.Read(ref _externalCloudAssignmentSerial);
            long modelAssignmentSerial = Volatile.Read(ref _modelAssignmentSerial);
            CloudLoadRequest? publishingRequest = adopting ? admission : null;
            publishingRequest?.CapturePriorModel(Model, _ownedGeneratedModel, _ownedGeneratedSubMesh,
                _ownedGeneratedMesh, _activeInstanceCount);
            if (!SetField(ref _cloud, value))
                return;
            if (!ReferenceEquals(_cloud, value) ||
                modelAssignmentSerial != Volatile.Read(ref _modelAssignmentSerial) ||
                (adopting && !ReferenceEquals(_pendingCloudLoad, publishingRequest)))
            {
                if (!adopting && previousOwned is not null && !ReferenceEquals(previousOwned, _cloud))
                {
                    _ownedCloud = null;
                    RetireOwnedCloud(previousOwned);
                }
                return;
            }
            CloudLoadRequest? priorPublishingRequest = _publishingCloudRequest;
            _publishingCloudRequest = publishingRequest;
            try { RebuildModel(); }
            catch
            {
                if (ReferenceEquals(_cloud, value) &&
                    externalAssignmentSerial == Volatile.Read(ref _externalCloudAssignmentSerial))
                {
                    try { SetField(ref _cloud, previousCloud); }
                    catch (Exception rollbackError)
                    {
                        System.Diagnostics.Trace.TraceError("Failed to restore a Gaussian cloud after model publication error: {0}", rollbackError);
                    }
                }
                throw;
            }
            finally { _publishingCloudRequest = priorPublishingRequest; }
            if (!adopting && previousOwned is not null && !ReferenceEquals(previousOwned, _cloud))
            {
                _ownedCloud = null;
                RetireOwnedCloud(previousOwned);
            }
        }
    }

    private float _pointScale = 3.0f;
    /// <summary>
    /// Multiplier applied to each gaussian radius before computing point size.
    /// </summary>
    public float PointScale
    {
        get => _pointScale;
        set
        {
            if (SetField(ref _pointScale, value))
                RebuildModel();
        }
    }

    public XRMaterial? OverrideMaterial { get; set; }

    private int _activeInstanceCount;

    public void LoadFromFile(string path)
    {
        long loadIntent = Interlocked.Increment(ref _cloudLoadIntentSerial);
        LoadFromFileCore(path, loadIntent);
    }

    private void LoadFromFileCore(string path, long loadIntent)
    {
        if (loadIntent != Volatile.Read(ref _cloudLoadIntentSerial))
            return;
        _externalModelBinding = false;
        _externalCloudBinding = false;
        if (OperatingSystem.IsBrowser() || XREngine.Execution.RuntimeWorkScheduler.IsCallerThread)
        {
            StartCloudLoad(path, loadIntent);
            return;
        }

        try
        {
            Cloud = GaussianSplatCloud.Load(path);
        }
        catch (Exception ex)
        {
            Debug.RenderingException(ex, $"Failed to load gaussian splat data from '{path}'.");
        }
    }

    private void RebuildModel()
    {
        if (_cloudLoadTeardown)
            return;
        GeneratedOwnership priorOwned = CurrentGeneratedOwnership;
        Model? priorBinding = Model;
        int priorInstanceCount = _activeInstanceCount;
        GaussianSplatCloud? cloudSnapshot = Cloud;
        long loadSerial = Volatile.Read(ref _cloudLoadIntentSerial);
        long modelSerial = Volatile.Read(ref _modelAssignmentSerial);
        long cloudSerial = Volatile.Read(ref _externalCloudAssignmentSerial);
        GeneratedOwnership candidate = BuildGeneratedOwnership();
        if (!ReferenceEquals(Cloud, cloudSnapshot) ||
            loadSerial != Volatile.Read(ref _cloudLoadIntentSerial) ||
            modelSerial != Volatile.Read(ref _modelAssignmentSerial) ||
            cloudSerial != Volatile.Read(ref _externalCloudAssignmentSerial))
        {
            RetireGeneratedModel(candidate.Model, candidate.SubMesh, candidate.Mesh);
            return;
        }
        CloudLoadRequest? request = _publishingCloudRequest ?? _deferredCloudRequest;
        bool deferPrior = request is not null &&
            ReferenceEquals(priorOwned.Model, request.PriorGeneratedModel) &&
            ReferenceEquals(priorOwned.SubMesh, request.PriorGeneratedSubMesh) &&
            ReferenceEquals(priorOwned.Mesh, request.PriorGeneratedMesh);
        try
        {
            _activeInstanceCount = candidate.InstanceCount;
            try { PublishGeneratedModel(candidate.Model); }
            catch
            {
                if (ReferenceEquals(Model, candidate.Model) &&
                    modelSerial == Volatile.Read(ref _modelAssignmentSerial) &&
                    cloudSerial == Volatile.Read(ref _externalCloudAssignmentSerial))
                {
                    _activeInstanceCount = priorInstanceCount;
                    try { PublishGeneratedModel(priorBinding); }
                    catch (Exception rollbackError)
                    {
                        System.Diagnostics.Trace.TraceError("Failed to restore a Gaussian model after publication error: {0}", rollbackError);
                    }
                }
                throw;
            }
        }
        finally { SettleGeneratedPublication(priorOwned, priorBinding, priorInstanceCount, candidate, deferPrior); }
    }

    private GeneratedOwnership BuildGeneratedOwnership()
    {
        if (Cloud is not { Count: > 0 } cloud)
            return default;
        XRMesh? mesh = null;
        SubMesh? subMesh = null;
        Model? model = null;
        try
        {
            GaussianMeshBuilder builder = new(cloud, PointScale);
            (XRMesh builtMesh, AABB bounds, int count) = builder.Build(
                failedMesh => RetireGeneratedModel(null, null, failedMesh));
            mesh = builtMesh;
            XRMaterial material = OverrideMaterial ?? GaussianMaterialFactory.Create();
            subMesh = new SubMesh(new SubMeshLOD(material, builtMesh, float.PositiveInfinity))
            {
                Bounds = bounds,
                CullingBounds = bounds,
            };
            model = new Model(subMesh);
            GeneratedOwnership built = new(model, subMesh, builtMesh, count);
            model = null;
            subMesh = null;
            mesh = null;
            return built;
        }
        finally
        {
            if (model is not null || subMesh is not null || mesh is not null)
                RetireGeneratedModel(model, subMesh, mesh);
        }
    }

    private void SettleGeneratedPublication(GeneratedOwnership priorOwned, Model? priorBinding,
        int priorInstanceCount, GeneratedOwnership candidate, bool deferPrior)
    {
        GeneratedOwnership installed = CurrentGeneratedOwnership;
        if (ReferenceEquals(Model, candidate.Model))
        {
            CurrentGeneratedOwnership = candidate;
            _activeInstanceCount = candidate.InstanceCount;
            if (!installed.SameAssets(priorOwned) && !installed.SameAssets(candidate))
                QueueGeneratedRetirement(installed.Model, installed.SubMesh, installed.Mesh);
            if (!deferPrior && !priorOwned.SameAssets(candidate))
                QueueGeneratedRetirement(priorOwned.Model, priorOwned.SubMesh, priorOwned.Mesh);
            FlushGeneratedRetirements();
            return;
        }

        QueueGeneratedRetirement(candidate.Model, candidate.SubMesh, candidate.Mesh);
        if (ReferenceEquals(Model, priorBinding))
        {
            _activeInstanceCount = priorInstanceCount;
            if (!installed.SameAssets(priorOwned))
                QueueGeneratedRetirement(installed.Model, installed.SubMesh, installed.Mesh);
            CurrentGeneratedOwnership = priorOwned;
        }
        else if (!installed.IsEmpty && ReferenceEquals(Model, installed.Model))
        {
            if (!deferPrior && !installed.SameAssets(priorOwned))
                QueueGeneratedRetirement(priorOwned.Model, priorOwned.SubMesh, priorOwned.Mesh);
        }
        else
        {
            _activeInstanceCount = 0;
            CurrentGeneratedOwnership = default;
            if (!installed.SameAssets(priorOwned))
                QueueGeneratedRetirement(installed.Model, installed.SubMesh, installed.Mesh);
            if (!deferPrior)
                QueueGeneratedRetirement(priorOwned.Model, priorOwned.SubMesh, priorOwned.Mesh);
        }
        FlushGeneratedRetirements();
    }

    private readonly record struct GeneratedOwnership(Model? Model, SubMesh? SubMesh, XRMesh? Mesh, int InstanceCount)
    {
        public bool IsEmpty => Model is null && SubMesh is null && Mesh is null;
        public bool SameAssets(GeneratedOwnership other)
            => ReferenceEquals(Model, other.Model) && ReferenceEquals(SubMesh, other.SubMesh) &&
                ReferenceEquals(Mesh, other.Mesh);
    }

    private GeneratedOwnership CurrentGeneratedOwnership
    {
        get => new(_ownedGeneratedModel, _ownedGeneratedSubMesh, _ownedGeneratedMesh, _activeInstanceCount);
        set
        {
            _ownedGeneratedModel = value.Model;
            _ownedGeneratedSubMesh = value.SubMesh;
            _ownedGeneratedMesh = value.Mesh;
        }
    }

    private Model? _ownedGeneratedModel;
    private SubMesh? _ownedGeneratedSubMesh;
    private XRMesh? _ownedGeneratedMesh;
    private CloudLoadRequest? _publishingCloudRequest;
    private CloudLoadRequest? _deferredCloudRequest;
    private bool _publishingGeneratedModel;
    private Model? _publishingGeneratedModelIdentity;
    private bool _externalModelBinding;
    private bool _externalCloudBinding;
    private CloudLoadRequest? _adoptingCloudRequest;
    private long _externalCloudAssignmentSerial;
    private long _modelAssignmentSerial;

    private void PublishGeneratedModel(Model? model)
    {
        bool priorPublishing = _publishingGeneratedModel;
        Model? priorIdentity = _publishingGeneratedModelIdentity;
        _publishingGeneratedModel = true;
        _publishingGeneratedModelIdentity = model;
        try { Model = model; }
        finally
        {
            _publishingGeneratedModel = priorPublishing;
            _publishingGeneratedModelIdentity = priorIdentity;
        }
    }

    private void ModelBindingChanged(object? _, XREngine.Data.Core.IXRPropertyChangedEventArgs change)
    {
        if (change.PropertyName == nameof(Model) &&
            (!_publishingGeneratedModel || !ReferenceEquals(Model, _publishingGeneratedModelIdentity)))
        {
            Interlocked.Increment(ref _modelAssignmentSerial);
            Interlocked.Increment(ref _cloudLoadIntentSerial);
            _externalModelBinding = true;
            CancelPendingCloudLoad();
        }
    }

    private readonly List<(Model? Model, SubMesh? SubMesh, XRMesh? Mesh)> _retiredGeneratedModels = [];
    private bool _retiringGeneratedModels;

    private void RetireGeneratedModel(Model? model, SubMesh? subMesh, XRMesh? mesh)
    {
        QueueGeneratedRetirement(model, subMesh, mesh);
        FlushGeneratedRetirements();
    }

    private void QueueGeneratedRetirement(Model? model, SubMesh? subMesh, XRMesh? mesh)
    {
        if (model is not null || subMesh is not null || mesh is not null)
        {
            bool alreadyRetained = false;
            for (int i = 0; i < _retiredGeneratedModels.Count; i++)
            {
                var retained = _retiredGeneratedModels[i];
                if (ReferenceEquals(retained.Model, model) &&
                    ReferenceEquals(retained.SubMesh, subMesh) && ReferenceEquals(retained.Mesh, mesh))
                {
                    alreadyRetained = true;
                    break;
                }
            }
            if (!alreadyRetained)
                _retiredGeneratedModels.Add((model, subMesh, mesh));
        }
    }

    private void FlushGeneratedRetirements()
    {
        if (_retiringGeneratedModels)
            return;
        _retiringGeneratedModels = true;
        try
        {
            for (int i = 0; i < _retiredGeneratedModels.Count;)
            {
                var owned = _retiredGeneratedModels[i];
                if (!_cloudLoadTeardown && owned.Model is not null && ReferenceEquals(owned.Model, Model))
                {
                    _retiredGeneratedModels.RemoveAt(i);
                    continue;
                }
                TryDestroy(owned.Model);
                TryDestroy(owned.SubMesh);
                TryDestroy(owned.Mesh);
                if ((owned.Model?.IsDestroyed ?? true) && (owned.SubMesh?.IsDestroyed ?? true) &&
                    (owned.Mesh?.IsDestroyed ?? true))
                    _retiredGeneratedModels.RemoveAt(i);
                else
                    i++;
            }
        }
        finally { _retiringGeneratedModels = false; }
    }

    private static void TryDestroy(XREngine.Data.Core.XRObjectBase? asset)
    {
        if (asset is null || asset.IsDestroyed)
            return;
        try { asset.Destroy(now: true); }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError("Failed to retire generated Gaussian asset: {0}", error);
        }
    }

    private void MeshAdded(RenderableMesh mesh)
    {
        foreach (RenderableMesh.RenderableLOD lod in mesh.GetLodSnapshot())
            lod.Renderer.SettingUniforms += RendererOnSettingUniforms;

        UpdateRenderCommandInstances(mesh);
    }

    private void MeshRemoved(RenderableMesh mesh)
    {
        foreach (RenderableMesh.RenderableLOD lod in mesh.GetLodSnapshot())
            lod.Renderer.SettingUniforms -= RendererOnSettingUniforms;
    }

    private void UpdateRenderCommandInstances(RenderableMesh mesh)
    {
        uint instances = _activeInstanceCount > 0 ? (uint)_activeInstanceCount : 1u;

        foreach (var command in mesh.RenderInfo.RenderCommands)
            if (command is RenderCommandMesh3D meshCommand)
                meshCommand.Instances = instances;
    }

    private static void RendererOnSettingUniforms(XRRenderProgram vertexProgram, XRRenderProgram _)
    {
        var viewport = RuntimeEngine.Rendering.State.RenderingViewport;
        if (viewport is null)
            return;

        vertexProgram.Uniform(EEngineUniform.ScreenWidth.ToStringFast(), (float)viewport.Width);
        vertexProgram.Uniform(EEngineUniform.ScreenHeight.ToStringFast(), (float)viewport.Height);
    }

    private sealed record GaussianMeshBuilder(GaussianSplatCloud Cloud, float RadiusScale)
    {
        public (XRMesh mesh, AABB bounds, int instanceCount) Build(Action<XRMesh> retainFailedMesh)
        {
            int count = Cloud.Count;
            if (count == 0)
                return (new XRMesh([]), new AABB(), 0);

            var splats = Cloud.Splats;

            Vector3[] positions = new Vector3[count];
            Vector4[] colors = new Vector4[count];
            Vector4[] scales = new Vector4[count];
            Vector4[] rotations = new Vector4[count];

            Vector3 min = new(float.PositiveInfinity);
            Vector3 max = new(float.NegativeInfinity);

            for (int i = 0; i < count; i++)
            {
                var splat = splats[i];
                Vector3 position = splat.Position;
                Vector3 scaledExtents = splat.Scale * RadiusScale;

                positions[i] = position;
                colors[i] = splat.ColorWithOpacity;
                scales[i] = new Vector4(scaledExtents, scaledExtents.Length());
                rotations[i] = splat.Rotation.ToVector4();

                Vector3 localMin = position - scaledExtents;
                Vector3 localMax = position + scaledExtents;
                min = Vector3.Min(min, localMin);
                max = Vector3.Max(max, localMax);
            }

            AABB bounds = new(min, max);

            XRMesh mesh = XRMesh.CreatePoints(Vector3.Zero);
            try
            {
                mesh.SupportsBillboarding = false;
                mesh.Points = [0];
                ConfigureInstancedBuffers(mesh, positions, colors, scales, rotations);
                return (mesh, bounds, count);
            }
            catch
            {
                retainFailedMesh(mesh);
                throw;
            }
        }

        private static void ConfigureInstancedBuffers(
            XRMesh mesh,
            IList<Vector3> positions,
            IList<Vector4> colors,
            IList<Vector4> scales,
            IList<Vector4> rotations)
        {
            mesh.Buffers.RemoveBuffer(ECommonBufferType.InterleavedVertex.ToString());
            mesh.Buffers.RemoveBuffer(ECommonBufferType.Position.ToString());
            mesh.Buffers.RemoveBuffer($"{ECommonBufferType.Color}0");
            mesh.Buffers.RemoveBuffer($"{ECommonBufferType.Color}1");
            mesh.Buffers.RemoveBuffer($"{ECommonBufferType.Color}2");

            mesh.InterleavedVertexBuffer?.Destroy();
            mesh.Interleaved = false;

            XRDataBuffer positionBuffer = mesh.Buffers.SetBufferRaw(
                positions,
                ECommonBufferType.Position.ToString(),
                instanceDivisor: 1);
            mesh.PositionsBuffer = positionBuffer;

            XRDataBuffer color0 = mesh.Buffers.SetBufferRaw(
                colors,
                $"{ECommonBufferType.Color}0",
                instanceDivisor: 1);
            XRDataBuffer color1 = mesh.Buffers.SetBufferRaw(
                scales,
                $"{ECommonBufferType.Color}1",
                instanceDivisor: 1);
            XRDataBuffer color2 = mesh.Buffers.SetBufferRaw(
                rotations,
                $"{ECommonBufferType.Color}2",
                instanceDivisor: 1);

            mesh.ColorBuffers = [color0, color1, color2];
            mesh.ColorCount = (uint)mesh.ColorBuffers.Length;
        }
    }

    private static class GaussianMaterialFactory
    {
        private static XRMaterial? _material;

        public static XRMaterial Create()
        {
            if (_material != null)
                return _material;

            XRShader vertex = ShaderHelper.GaussianSplatVertex()!;
            XRShader fragment = ShaderHelper.GaussianSplatFragment()!;

            XRMaterial material = new([fragment])
            {
                RenderPass = (int)EDefaultRenderPass.TransparentForward,
                RenderOptions = new RenderingParameters()
                {
                    CullMode = ECullMode.None,
                    DepthTest = new DepthTest()
                    {
                        Enabled = ERenderParamUsage.Enabled,
                        Function = EComparison.Lequal,
                        UpdateDepth = false,
                    },
                    BlendModeAllDrawBuffers = BlendMode.EnabledTransparent(),
                    RequiredEngineUniforms = EUniformRequirements.Camera | EUniformRequirements.ViewportDimensions,
                }
            };

            material.Shaders.Add(vertex);

            _material = material;
            return material;
        }
    }
}
