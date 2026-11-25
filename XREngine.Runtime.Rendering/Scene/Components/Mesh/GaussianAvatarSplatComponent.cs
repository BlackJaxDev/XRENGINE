using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Gaussian;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;

namespace XREngine.Components.Scene.Mesh;

/// <summary>
/// Evaluates and renders dense live surface-aligned 3D Gaussian splats for an avatar hierarchy using each source
/// submesh's authored <c>NativeUber</c> or <c>Standard</c> material.
/// </summary>
[Serializable]
public sealed class GaussianAvatarSplatComponent : XRComponent, IRenderable, IRenderCaptureReadiness
{
    /// <summary>Compute shader that emits live world-space splats and surface records from avatar submeshes.</summary>
    public const string LiveSurfaceSplatsShaderPath = "Gaussian/GaussianLiveSurfaceSplats.comp";

    /// <summary>Storage buffer binding for <see cref="GaussianSplatGpuSurface"/> elements.</summary>
    public const uint SurfacesBinding = 60u;

    /// <summary>Storage buffer binding for <see cref="AdvancedLightRecord"/> elements.</summary>
    public const uint LightsBinding = 65u;

    private const uint ComputeLocalSize = 256u;
    private const int MaxSceneLights = 16;
    private const float DepthOcclusionBiasMeters = 0.0035f;

    /// <summary>The default value of <see cref="TargetSplatCount"/>.</summary>
    public const int DefaultTargetSplatCount = 2_400_000;

    private static readonly RenderInfo.DelAddRenderCommandsCallback RejectAllRenderPassesCallback = static (_, _, _) => false;
    private static readonly uint SurfaceStrideBytes = (uint)Unsafe.SizeOf<GaussianSplatGpuSurface>();

    private readonly RenderInfo3D _renderInfo;
    private readonly RenderInfo[] _renderedObjects;
    private readonly List<SceneNode> _lightTraversalStack = new(64);
    private readonly AdvancedLightRecord[] _lightsScratch = new AdvancedLightRecord[MaxSceneLights];

    private List<GaussianAvatarSubmeshSlice> _slices = [];
    private GaussianAvatarSplatMaterialTable? _materialTable;
    private GaussianSplatRenderer? _splatRenderer;
    private XRDataBuffer? _surfacesBuffer;
    private XRDataBuffer? _lightsBuffer;
    private XRDataBuffer? _dummyStorageBuffer;
    private XRRenderProgram? _liveSurfaceProgram;
    private XRMeshRenderer? _depthQuad;
    private XRMaterial? _ownedDepthQuadMaterial;
    private UberSplatBindingPublisher? _uberDepthPublisher;
    private XRMeshRenderer? _quad;
    private XRMaterial? _ownedQuadMaterial;
    private UberSplatBindingPublisher? _uberPublisher;
    private int _totalSplats;
    private bool _hasPreparedSplats;
    private bool _hasSubmittedDraw;
    private float _splatScale = 1.0f;
    private int _targetSplatCount = DefaultTargetSplatCount;
    private bool _sampleOnActivate = true;
    private bool _hideSourceMeshes = true;
    private bool _sampleRequested;
    private readonly List<(RenderableComponent Component, bool? CastsShadows)> _hiddenSourceComponents = [];

    public GaussianAvatarSplatComponent()
    {
        _renderInfo = RenderInfo3D.New(
            this,
            new RenderCommandMethod3D((int)EDefaultRenderPass.PreRender, PrepareSplats),
            new RenderCommandMethod3D((int)EDefaultRenderPass.MaskedForward, DrawSplatDepthOcclusion),
            new RenderCommandMethod3D((int)EDefaultRenderPass.TransparentForward, DrawSplats));
        _renderInfo.CastsShadows = false;
        _renderInfo.ReceivesShadows = false;
        _renderInfo.LocalCullingVolume = new AABB(new Vector3(-16.0f, -16.0f, -16.0f), new Vector3(16.0f, 16.0f, 16.0f));
        _renderedObjects = [_renderInfo];
    }

    /// <inheritdoc />
    [Browsable(false)]
    public RenderInfo[] RenderedObjects => _renderedObjects;

    /// <summary>Total number of live surface splats sampled across the avatar.</summary>
    [Browsable(false)]
    [YamlIgnore]
    public int TotalSplats => _totalSplats;

    /// <summary>Number of interned material rows in the avatar material table.</summary>
    [Browsable(false)]
    [YamlIgnore]
    public int MaterialCount => _materialTable?.MaterialCount ?? 0;

    /// <summary>Number of interned 2D textures in the avatar material table.</summary>
    [Browsable(false)]
    [YamlIgnore]
    public int Texture2DCount => _materialTable?.Texture2DCount ?? 0;

    /// <summary>
    /// Number of material texture references that share an interned 2D slot with an equal texture.
    /// Diagnostic output uses this value to show how many slots the table saved.
    /// </summary>
    [Browsable(false)]
    [YamlIgnore]
    public int Texture2DReuseCount => _materialTable?.Texture2DReuseCount ?? 0;

    /// <summary>Number of texture bindings that point to a shared solid white, black, or flat-normal fallback slot.</summary>
    [Browsable(false)]
    [YamlIgnore]
    public int ConstantTextureBindingCount => _materialTable?.ConstantTextureBindingCount ?? 0;

    /// <summary>Number of authored textures that the table did not intern because the feature that samples them is off.</summary>
    [Browsable(false)]
    [YamlIgnore]
    public int DisabledRoleTextureCount => _materialTable?.DisabledRoleTextureCount ?? 0;

    /// <summary>
    /// Number of texture bindings that use the role default because all 2D slots were full.
    /// A value above zero means that some splat materials are missing authored textures.
    /// </summary>
    [Browsable(false)]
    [YamlIgnore]
    public int Texture2DOverflowCount => _materialTable?.Texture2DOverflowCount ?? 0;

    /// <summary>Number of sampled avatar submesh slices.</summary>
    [Browsable(false)]
    [YamlIgnore]
    public int SubmeshSliceCount => _slices.Count;

    /// <summary>True after live splat generation, depth sort, Uber quad submission, and texture residency have completed.</summary>
    [Browsable(false)]
    [YamlIgnore]
    public bool IsGpuDataReady =>
        _hasPreparedSplats &&
        _hasSubmittedDraw &&
        _splatRenderer is { HasOrder: true } &&
        (_materialTable?.AreStreamedTexturesReady ?? true);

    /// <inheritdoc />
    [Browsable(false)]
    [YamlIgnore]
    public bool IsReadyForCapture => IsGpuDataReady;

    /// <inheritdoc />
    public string DescribeCaptureReadiness()
        => $"avatarSplats(splats={_totalSplats}, sampleRequested={_sampleRequested}, prepared={_hasPreparedSplats}, " +
           $"drawSubmitted={_hasSubmittedDraw}, texturesReady={_materialTable?.AreStreamedTexturesReady ?? true})";

    /// <summary>Uniform scale factor applied to every splat standard deviation.</summary>
    [Category("Rendering")]
    public float SplatScale
    {
        get => _splatScale;
        set => SetField(ref _splatScale, float.IsFinite(value) && value > 0.0f ? value : 1.0f);
    }

    /// <summary>
    /// The number of surface splats that <see cref="SampleOwnHierarchy"/> spreads over the triangle meshes under this node.
    /// A new value takes effect at the next sample.
    /// </summary>
    [Category("Sampling")]
    public int TargetSplatCount
    {
        get => _targetSplatCount;
        set => SetField(ref _targetSplatCount, Math.Clamp(value, 1_000, GaussianSplatRenderer.MaxCapacity));
    }

    /// <summary>
    /// True to sample the meshes under this node when the component activates. The sample runs in a later app-thread task,
    /// so the model components under the node have built their runtime meshes first.
    /// </summary>
    [Category("Sampling")]
    public bool SampleOnActivate
    {
        get => _sampleOnActivate;
        set => SetField(ref _sampleOnActivate, value);
    }

    /// <summary>
    /// True to stop the sampled triangle meshes from drawing and casting shadows, so only the splats show.
    /// Deactivating the component draws the triangle meshes again.
    /// </summary>
    [Category("Sampling")]
    public bool HideSourceMeshes
    {
        get => _hideSourceMeshes;
        set => SetField(ref _hideSourceMeshes, value);
    }

    /// <summary>
    /// Samples <paramref name="targetSplatCount"/> surface splats across active triangle submeshes under
    /// <paramref name="avatarRoot"/> and uploads the per-submesh sample buffers and Uber material table.
    /// </summary>
    public void InitializeFromAvatar(SceneNode avatarRoot, int targetSplatCount = 2_400_000)
    {
        ArgumentNullException.ThrowIfNull(avatarRoot);
        ReleaseGpuResources();

        GaussianAvatarSplatMaterialTable materialTable = new();
        List<GaussianAvatarSubmeshSlice> slices = GaussianAvatarSplatSampler.SampleAvatar(
            avatarRoot,
            targetSplatCount,
            materialTable,
            out int totalSplats);

        _materialTable = materialTable;
        _slices = slices;
        _totalSplats = totalSplats;
        _hasPreparedSplats = false;
        _hasSubmittedDraw = false;
    }

    /// <summary>
    /// Samples <see cref="TargetSplatCount"/> splats over the active triangle meshes under this node, then hides those meshes
    /// when <see cref="HideSourceMeshes"/> is true. Put the component on the root node of the avatar. The splats follow the
    /// skinned pose of the meshes in each frame, so an animation or a cutscene that moves the bones moves the splats.
    /// </summary>
    public void SampleOwnHierarchy()
    {
        if (IsDestroyed || SceneNode is not { } root)
            return;

        RestoreSourceMeshes();
        InitializeFromAvatar(root, _targetSplatCount);
        if (_hideSourceMeshes)
            HideSourceMeshesUnder(root);
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        if (!_sampleOnActivate || _sampleRequested || _totalSplats > 0)
            return;

        // The model components under this node can activate after this component. A later app-thread task samples
        // them after all of them have built their runtime meshes.
        _sampleRequested = true;
        RuntimeEngine.EnqueueAppThreadTask(
            () =>
            {
                _sampleRequested = false;
                if (IsActive && _totalSplats == 0)
                    SampleOwnHierarchy();
            },
            "GaussianAvatarSplatComponent.SampleOwnHierarchy");
    }

    protected override void OnComponentDeactivated()
    {
        RestoreSourceMeshes();
        base.OnComponentDeactivated();
    }

    private void HideSourceMeshesUnder(SceneNode root)
    {
        root.IterateComponents<RenderableComponent>(component =>
        {
            if (!component.IsActive)
                return;

            _hiddenSourceComponents.Add((component, component.MeshCastsShadows));
            component.MeshCastsShadows = false;
            foreach (RenderableMesh mesh in component.Meshes)
                mesh.RenderInfo.SetCameraIndependentPreCollectCommandsCallback(RejectAllRenderPassesCallback);
        }, iterateChildHierarchy: true);
    }

    private void RestoreSourceMeshes()
    {
        for (int i = 0; i < _hiddenSourceComponents.Count; i++)
        {
            (RenderableComponent component, bool? castsShadows) = _hiddenSourceComponents[i];
            if (component.IsDestroyed)
                continue;

            component.MeshCastsShadows = castsShadows;
            foreach (RenderableMesh mesh in component.Meshes)
                mesh.RenderInfo.SetCameraIndependentPreCollectCommandsCallback(null);
        }

        _hiddenSourceComponents.Clear();
    }

    protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
    {
        base.OnTransformRenderWorldMatrixChanged(transform, renderMatrix);
        _renderInfo.CullingOffsetMatrix = renderMatrix;
    }

    protected override void OnDestroying()
    {
        RestoreSourceMeshes();
        ReleaseGpuResources();
        base.OnDestroying();
    }

    private void PrepareSplats()
    {
        if (IsDestroyed || _totalSplats <= 0 || _slices.Count == 0 || _materialTable is null)
            return;

        try
        {
            XRCamera? camera = RuntimeEngine.Rendering.State.RenderingCamera;
            if (camera is null || !EnsureProgramReady())
                return;

            _materialTable.EnsureBuffersUploaded();
            GaussianSplatRenderer renderer = EnsureRenderer(_totalSplats);
            XRDataBuffer surfaces = EnsureSurfacesBuffer((uint)_totalSplats);
            XRDataBuffer dummy = EnsureDummyStorageBuffer();
            uint lightCount = UpdateLightsBuffer(out Vector3 globalAmbient, out Vector3 skyColor, out Vector3 groundColor);

            XRRenderProgram program = _liveSurfaceProgram!;
            for (int i = 0; i < _slices.Count; i++)
            {
                GaussianAvatarSubmeshSlice slice = _slices[i];
                slice.EnsureGpuUploaded();
                DispatchSubmeshSlice(program, slice, renderer.Splats, surfaces, dummy);
            }

            Matrix4x4 view = camera.Transform.InverseRenderMatrix;
            renderer.ModelMatrix = Matrix4x4.Identity;
            renderer.SplatScale = _splatScale;
            renderer.AllSlotsWritten = true;
            renderer.Prepare(in view);
            _hasPreparedSplats = renderer.HasOrder;

            EnsureQuads();
            UberFrameDrawState drawState = new(
                surfaces,
                _materialTable.MaterialsBuffer!,
                _materialTable.MaterialConstantsBuffer!,
                _materialTable.MaterialTextureBindingsBuffer!,
                _lightsBuffer!,
                view,
                camera.Transform.RenderMatrix,
                RuntimeRenderingHostServices.FrameTiming.ElapsedTime,
                globalAmbient,
                skyColor,
                groundColor,
                lightCount);
            _uberDepthPublisher!.SetCurrentState(in drawState);
            _uberPublisher!.SetCurrentState(in drawState);
        }
        catch (Exception error)
        {
            Debug.RenderingException(error, "Failed to prepare live avatar Gaussian splats.");
        }
    }

    private void DrawSplatDepthOcclusion()
    {
        if (IsDestroyed || !_hasPreparedSplats || _splatRenderer is not { HasOrder: true } renderer || _depthQuad is null)
            return;

        try
        {
            renderer.Draw(_depthQuad);
        }
        catch (Exception error)
        {
            Debug.RenderingException(error, "Failed to draw live avatar Gaussian splat depth occlusion.");
        }
    }

    private void DrawSplats()
    {
        if (IsDestroyed || !_hasPreparedSplats || _splatRenderer is not { HasOrder: true } renderer || _quad is null)
            return;

        try
        {
            renderer.Draw(_quad);
            _hasSubmittedDraw = true;
        }
        catch (Exception error)
        {
            _hasSubmittedDraw = false;
            Debug.RenderingException(error, "Failed to draw live avatar Gaussian splats.");
        }
    }

    private static void DispatchSubmeshSlice(
        XRRenderProgram program,
        GaussianAvatarSubmeshSlice slice,
        XRDataBuffer splatsBuffer,
        XRDataBuffer surfacesBuffer,
        XRDataBuffer dummyBuffer)
    {
        XRMesh mesh = slice.SourceMesh;
        uint vertexCount = (uint)Math.Max(mesh.VertexCount, 1);

        XRMeshRenderer.SkinnedOutputResourceSnapshot deformed = default;
        bool hasDeformedOutput = false;
        if (mesh.HasSkinning || mesh.HasBlendshapeDeformation)
        {
            hasDeformedOutput = SkinningPrepassDispatcher.Instance.TryRunForLiveSurfaceSplats(
                slice.SourceRenderer,
                out deformed,
                out _);
        }

        uint sourceMode;
        bool applyWorldMatrix;
        XRDataBuffer deformedPositions = dummyBuffer;
        XRDataBuffer deformedNormals = dummyBuffer;
        XRDataBuffer deformedTangents = dummyBuffer;
        XRDataBuffer staticPositions = dummyBuffer;
        XRDataBuffer staticNormals = dummyBuffer;
        XRDataBuffer staticTangents = dummyBuffer;
        XRDataBuffer interleavedBuffer = dummyBuffer;

        if (hasDeformedOutput && deformed.Positions is not null)
        {
            sourceMode = 0u;
            applyWorldMatrix = !mesh.HasSkinning;
            deformedPositions = deformed.Positions;
            deformedNormals = deformed.Normals ?? dummyBuffer;
            deformedTangents = deformed.Tangents ?? dummyBuffer;
        }
        else if (hasDeformedOutput && deformed.Interleaved is not null)
        {
            sourceMode = 1u;
            applyWorldMatrix = !mesh.HasSkinning;
            interleavedBuffer = deformed.Interleaved;
        }
        else if (mesh.Interleaved && mesh.InterleavedVertexBuffer is not null)
        {
            EnsureBufferResident(mesh.InterleavedVertexBuffer);
            sourceMode = 1u;
            applyWorldMatrix = true;
            interleavedBuffer = mesh.InterleavedVertexBuffer;
        }
        else
        {
            EnsureBufferResident(mesh.PositionsBuffer);
            EnsureBufferResident(mesh.NormalsBuffer);
            EnsureBufferResident(mesh.TangentsBuffer);
            sourceMode = 2u;
            applyWorldMatrix = true;
            staticPositions = mesh.PositionsBuffer ?? dummyBuffer;
            staticNormals = mesh.NormalsBuffer ?? dummyBuffer;
            staticTangents = mesh.TangentsBuffer ?? dummyBuffer;
        }

        Matrix4x4 worldMatrix = applyWorldMatrix ? slice.Component.Transform.RenderMatrix : Matrix4x4.Identity;
        Matrix4x4 normalMatrix = Matrix4x4.Identity;
        if (applyWorldMatrix)
        {
            Matrix4x4 upper = worldMatrix;
            upper.M41 = 0.0f;
            upper.M42 = 0.0f;
            upper.M43 = 0.0f;
            upper.M44 = 1.0f;
            normalMatrix = Matrix4x4.Invert(upper, out Matrix4x4 inv) ? Matrix4x4.Transpose(inv) : upper;
        }

        uint staticTangentStride = mesh.TangentsBuffer?.ComponentCount is > 0 and <= 4
            ? mesh.TangentsBuffer.ComponentCount
            : 4u;

        program.BindBuffer(slice.SamplesBuffer, 0u);
        program.BindBuffer(deformedPositions, 1u);
        program.BindBuffer(deformedNormals, 2u);
        program.BindBuffer(deformedTangents, 3u);
        program.BindBuffer(staticPositions, 4u);
        program.BindBuffer(staticNormals, 5u);
        program.BindBuffer(staticTangents, 6u);
        program.BindBuffer(interleavedBuffer, 7u);
        program.BindBuffer(splatsBuffer, GaussianSplatRenderer.SplatsBinding);
        program.BindBuffer(surfacesBuffer, SurfacesBinding);

        program.Uniform("uSampleCount", slice.SampleCount);
        program.Uniform("uVertexCount", vertexCount);
        program.Uniform("uSourceMode", sourceMode);
        program.Uniform("uHasNormals", mesh.HasNormals ? 1u : 0u);
        program.Uniform("uHasTangents", mesh.HasTangents ? 1u : 0u);
        program.Uniform("uStaticTangentStride", staticTangentStride);
        program.Uniform("uInterleavedStrideBytes", mesh.InterleavedStride);
        program.Uniform("uPositionOffsetBytes", mesh.PositionOffset);
        program.Uniform("uNormalOffsetBytes", mesh.NormalOffset ?? 0u);
        program.Uniform("uTangentOffsetBytes", mesh.TangentOffset ?? 0u);
        program.Uniform("uApplyWorldMatrix", applyWorldMatrix ? 1u : 0u);
        program.Uniform("uWorldMatrix", worldMatrix);
        program.Uniform("uNormalMatrix", normalMatrix);

        uint groupCount = Math.Max(1u, (slice.SampleCount + ComputeLocalSize - 1u) / ComputeLocalSize);
        program.DispatchCompute(groupCount, 1u, 1u, EMemoryBarrierMask.ShaderStorage);
    }

    private uint UpdateLightsBuffer(out Vector3 globalAmbient, out Vector3 skyColor, out Vector3 groundColor)
    {
        RuntimeWorld? runtimeWorld = World as RuntimeWorld;
        XRWorld? world = runtimeWorld?.TargetWorld ?? World as XRWorld;
        if (world is not null)
        {
            Vector3 ambientColor = new(
                world.Settings.AmbientLightColor.R,
                world.Settings.AmbientLightColor.G,
                world.Settings.AmbientLightColor.B);
            globalAmbient = ambientColor * MathF.Max(world.Settings.AmbientLightIntensity, 0.0f);
        }
        else
        {
            globalAmbient = new Vector3(0.3f, 0.3f, 0.3f);
        }

        skyColor = Vector3.Max(globalAmbient * 1.45f, new Vector3(0.28f, 0.31f, 0.36f));
        groundColor = Vector3.Max(globalAmbient * 0.55f, new Vector3(0.10f, 0.10f, 0.12f));

        int count = 0;
        _lightTraversalStack.Clear();
        if (runtimeWorld is not null)
        {
            IReadOnlyList<SceneNode> roots = runtimeWorld.RootNodes;
            for (int r = roots.Count - 1; r >= 0; r--)
            {
                if (roots[r] is { IsActiveSelf: true } root)
                    _lightTraversalStack.Add(root);
            }
        }
        else if (world is not null)
        {
            IReadOnlyList<XRScene> scenes = world.Scenes;
            for (int s = scenes.Count - 1; s >= 0; s--)
            {
                XRScene? scene = scenes[s];
                if (scene is null)
                    continue;
                IReadOnlyList<SceneNode> roots = scene.SceneRoots;
                for (int r = roots.Count - 1; r >= 0; r--)
                {
                    if (roots[r] is { IsActiveSelf: true } root)
                        _lightTraversalStack.Add(root);
                }
            }
        }

        while (_lightTraversalStack.Count > 0 && count < MaxSceneLights)
        {
            int lastIdx = _lightTraversalStack.Count - 1;
            SceneNode node = _lightTraversalStack[lastIdx];
            _lightTraversalStack.RemoveAt(lastIdx);

            IReadOnlyList<XRComponent> components = node.Components;
            for (int c = 0; c < components.Count && count < MaxSceneLights; c++)
            {
                XRComponent comp = components[c];
                if (!comp.IsActive)
                    continue;

                if (comp is DirectionalLightComponent dirLight)
                {
                    Vector3 forward = Vector3.TransformNormal(-Vector3.UnitZ, dirLight.Transform.RenderMatrix);
                    forward = forward.LengthSquared() > 1.0e-8f ? Vector3.Normalize(forward) : new Vector3(0.0f, -1.0f, 0.0f);
                    _lightsScratch[count++] = new AdvancedLightRecord
                    {
                        StableLightId = (uint)count,
                        Generation = 1u,
                        Type = EAdvancedLightType.Directional,
                        Flags = EAdvancedLightRecordFlags.Enabled,
                        DirectionAndOuterCone = new Vector4(forward, 0.0f),
                        ColorAndIntensity = new Vector4(dirLight.Color.R, dirLight.Color.G, dirLight.Color.B, dirLight.DiffuseIntensity),
                        LayerMask = 0xFFFFFFFFu,
                        ViewMaskLo = 0xFFFFFFFFu,
                    };
                }
                else if (comp is SpotLightComponent spotLight)
                {
                    Vector3 forward = Vector3.TransformNormal(-Vector3.UnitZ, spotLight.Transform.RenderMatrix);
                    forward = forward.LengthSquared() > 1.0e-8f ? Vector3.Normalize(forward) : new Vector3(0.0f, 0.0f, -1.0f);
                    Vector3 pos = spotLight.Transform.RenderTranslation;
                    float localBrightness = spotLight.Brightness == 0.0f ? 1.0f : spotLight.Brightness;
                    _lightsScratch[count++] = new AdvancedLightRecord
                    {
                        StableLightId = (uint)count,
                        Generation = 1u,
                        Type = EAdvancedLightType.Spot,
                        Flags = EAdvancedLightRecordFlags.Enabled,
                        PositionAndRadius = new Vector4(pos, MathF.Max(spotLight.Distance, 0.01f)),
                        DirectionAndOuterCone = new Vector4(forward, MathF.Cos(spotLight.OuterCutoffAngleDegrees * (MathF.PI / 180.0f))),
                        ColorAndIntensity = new Vector4(spotLight.Color.R, spotLight.Color.G, spotLight.Color.B, spotLight.DiffuseIntensity * localBrightness),
                        ShapeAndInnerCone = new Vector4(0.0f, 0.0f, 0.0f, MathF.Cos(spotLight.InnerCutoffAngleDegrees * (MathF.PI / 180.0f))),
                        LayerMask = 0xFFFFFFFFu,
                        ViewMaskLo = 0xFFFFFFFFu,
                    };
                }
                else if (comp is PointLightComponent pointLight)
                {
                    Vector3 pos = pointLight.Transform.RenderTranslation;
                    float localBrightness = pointLight.Brightness == 0.0f ? 1.0f : pointLight.Brightness;
                    _lightsScratch[count++] = new AdvancedLightRecord
                    {
                        StableLightId = (uint)count,
                        Generation = 1u,
                        Type = EAdvancedLightType.Point,
                        Flags = EAdvancedLightRecordFlags.Enabled,
                        PositionAndRadius = new Vector4(pos, MathF.Max(pointLight.Radius, 0.01f)),
                        ColorAndIntensity = new Vector4(pointLight.Color.R, pointLight.Color.G, pointLight.Color.B, pointLight.DiffuseIntensity * localBrightness),
                        LayerMask = 0xFFFFFFFFu,
                        ViewMaskLo = 0xFFFFFFFFu,
                    };
                }
            }

            IReadOnlyList<TransformBase> children = node.Transform.Children;
            for (int ch = children.Count - 1; ch >= 0; ch--)
            {
                if (children[ch].SceneNode is { IsActiveSelf: true } childNode)
                    _lightTraversalStack.Add(childNode);
            }
        }

        // Place the strongest directional light first so ShadeNativeUber selects the key light as primary.
        int bestDirIdx = -1;
        float bestDirIntensity = -1.0f;
        for (int i = 0; i < count; i++)
        {
            if (_lightsScratch[i].Type == EAdvancedLightType.Directional &&
                _lightsScratch[i].ColorAndIntensity.W > bestDirIntensity)
            {
                bestDirIntensity = _lightsScratch[i].ColorAndIntensity.W;
                bestDirIdx = i;
            }
        }
        if (bestDirIdx > 0)
            (_lightsScratch[0], _lightsScratch[bestDirIdx]) = (_lightsScratch[bestDirIdx], _lightsScratch[0]);

        if (count == 0)
        {
            Vector3 defaultDir = Vector3.Normalize(new Vector3(-0.4f, -1.0f, -0.3f));
            _lightsScratch[0] = new AdvancedLightRecord
            {
                StableLightId = 0u,
                Generation = 1u,
                Type = EAdvancedLightType.Directional,
                DirectionAndOuterCone = new Vector4(defaultDir, 0.0f),
                ColorAndIntensity = new Vector4(1.0f, 1.0f, 1.0f, 2.5f),
                LayerMask = 0xFFFFFFFFu,
                ViewMaskLo = 0xFFFFFFFFu,
            };
            count = 1;
        }

        _lightsBuffer ??= new XRDataBuffer("GaussianAvatarSplatLights", EBufferTarget.ShaderStorageBuffer, false)
        {
            BindingIndexOverride = LightsBinding,
            Usage = EBufferUsage.DynamicDraw,
            DisposeOnPush = false,
            PadEndingToVec4 = true,
        };
        _lightsBuffer.EnsureRawCapacity<AdvancedLightRecord>((uint)MaxSceneLights, growCapacityGeometrically: false);
        uint byteLength = _lightsBuffer.WriteDataRaw<AdvancedLightRecord>(_lightsScratch.AsSpan(0, count), 0u);
        _lightsBuffer.PushSubData(0, byteLength);
        return (uint)count;
    }

    private GaussianSplatRenderer EnsureRenderer(int capacity)
    {
        if (_splatRenderer is { } existing && existing.Capacity == capacity)
            return existing;

        _hasPreparedSplats = false;
        _splatRenderer?.Dispose();
        GaussianSplatRenderer created = new(capacity)
        {
            AllSlotsWritten = true,
            ModelMatrix = Matrix4x4.Identity,
        };
        created.VisibleCount.GpuProduced = false;
        created.VisibleCount.SetDataRawAtIndex(0u, (uint)capacity);
        created.VisibleCount.PushSubData();
        created.Overflow.GpuProduced = false;
        created.Overflow.SetDataRawAtIndex(0u, 0u);
        created.Overflow.PushSubData();
        _splatRenderer = created;
        return created;
    }

    private XRDataBuffer EnsureSurfacesBuffer(uint capacity)
    {
        if (_surfacesBuffer is { } existing && existing.ElementCount >= capacity)
            return existing;

        _surfacesBuffer?.Dispose();
        _surfacesBuffer = new XRDataBuffer(
            "GaussianAvatarSplatSurfaces",
            EBufferTarget.ShaderStorageBuffer,
            capacity,
            EComponentType.Struct,
            SurfaceStrideBytes,
            false,
            false,
            allocateClientSideSource: false)
        {
            GpuProduced = true,
            BindingIndexOverride = SurfacesBinding,
            Usage = EBufferUsage.StaticCopy,
            DisposeOnPush = false,
            PadEndingToVec4 = true,
        };
        return _surfacesBuffer;
    }

    private XRDataBuffer EnsureDummyStorageBuffer()
    {
        if (_dummyStorageBuffer is not null)
            return _dummyStorageBuffer;

        XRDataBuffer dummy = new("GaussianAvatarSplatDummyStorage", EBufferTarget.ShaderStorageBuffer, 4u, EComponentType.UInt, 1u, false, true)
        {
            Usage = EBufferUsage.StaticDraw,
            DisposeOnPush = false,
            PadEndingToVec4 = true,
        };
        dummy.SetDataRawAtIndex(0u, 0u);
        dummy.PushSubData();
        _dummyStorageBuffer = dummy;
        return dummy;
    }

    private void EnsureQuads()
    {
        if (_depthQuad is null || _uberDepthPublisher is null)
        {
            _ownedDepthQuadMaterial = _materialTable!.CreateUberDepthOcclusionQuadMaterial();
            XRMeshRenderer depthQuad = GaussianSplatRenderer.CreateQuadRenderer(_ownedDepthQuadMaterial);
            UberSplatBindingPublisher depthPublisher = new(DepthOcclusionBiasMeters);
            depthQuad.BindingPublishers.Add(depthPublisher);
            _depthQuad = depthQuad;
            _uberDepthPublisher = depthPublisher;
        }

        if (_quad is null || _uberPublisher is null)
        {
            _ownedQuadMaterial = _materialTable!.CreateUberQuadMaterial();
            XRMeshRenderer quad = GaussianSplatRenderer.CreateQuadRenderer(_ownedQuadMaterial);
            UberSplatBindingPublisher publisher = new(0.0f);
            quad.BindingPublishers.Add(publisher);
            _quad = quad;
            _uberPublisher = publisher;
        }
    }

    private bool EnsureProgramReady()
    {
        _liveSurfaceProgram ??= new XRRenderProgram(
            true,
            false,
            ShaderHelper.LoadEngineShader(LiveSurfaceSplatsShaderPath, EShaderType.Compute))
        {
            Name = "GaussianLiveSurfaceSplats",
        };

        if (_liveSurfaceProgram.IsLinked)
            return true;

        _liveSurfaceProgram.Link();
        return _liveSurfaceProgram.IsLinked;
    }

    private static void EnsureBufferResident(XRDataBuffer? buffer)
    {
        if (buffer is null)
            return;

        if (buffer.TryGetApiBufferForCurrentOwner(out IApiDataBuffer? apiBuffer) &&
            !apiBuffer.BackendIsReadyForGpuUse)
        {
            apiBuffer.EnsureStorageAllocatedForGpuUse();
        }
    }

    private void ReleaseGpuResources()
    {
        List<GaussianAvatarSubmeshSlice> slices = _slices;
        GaussianAvatarSplatMaterialTable? materialTable = _materialTable;
        GaussianSplatRenderer? renderer = _splatRenderer;
        XRDataBuffer? surfacesBuffer = _surfacesBuffer;
        XRDataBuffer? lightsBuffer = _lightsBuffer;
        XRDataBuffer? dummyBuffer = _dummyStorageBuffer;
        XRRenderProgram? program = _liveSurfaceProgram;
        XRMeshRenderer? depthQuad = _depthQuad;
        XRMaterial? ownedDepthMaterial = _ownedDepthQuadMaterial;
        XRMeshRenderer? quad = _quad;
        XRMaterial? ownedMaterial = _ownedQuadMaterial;

        _slices = [];
        _materialTable = null;
        _splatRenderer = null;
        _surfacesBuffer = null;
        _lightsBuffer = null;
        _dummyStorageBuffer = null;
        _liveSurfaceProgram = null;
        _depthQuad = null;
        _ownedDepthQuadMaterial = null;
        _uberDepthPublisher = null;
        _quad = null;
        _ownedQuadMaterial = null;
        _uberPublisher = null;
        _totalSplats = 0;
        _hasPreparedSplats = false;
        _hasSubmittedDraw = false;

        RuntimeEngine.InvokeOnMainThread(
            () =>
            {
                for (int i = 0; i < slices.Count; i++)
                    slices[i].Dispose();
                materialTable?.Dispose();
                renderer?.Dispose();
                surfacesBuffer?.Dispose();
                lightsBuffer?.Dispose();
                dummyBuffer?.Dispose();
                program?.Destroy();
                XRMesh? depthQuadMesh = depthQuad?.Mesh;
                TryDestroy(depthQuad);
                TryDestroy(depthQuadMesh);
                TryDestroy(ownedDepthMaterial);
                XRMesh? quadMesh = quad?.Mesh;
                TryDestroy(quad);
                TryDestroy(quadMesh);
                TryDestroy(ownedMaterial);
            },
            "GaussianAvatarSplatComponent.ReleaseGpuResources",
            executeNowIfAlreadyMainThread: true);
    }

    private static void TryDestroy(XREngine.Data.Core.XRObjectBase? asset)
    {
        if (asset is null || asset.IsDestroyed)
            return;
        try
        {
            asset.Destroy(now: true);
        }
        catch
        {
        }
    }

    private readonly record struct UberFrameDrawState(
        XRDataBuffer? SurfacesBuffer,
        XRDataBuffer? MaterialsBuffer,
        XRDataBuffer? MaterialConstantsBuffer,
        XRDataBuffer? MaterialTextureBindingsBuffer,
        XRDataBuffer? LightsBuffer,
        Matrix4x4 ViewMatrix,
        Matrix4x4 InverseViewMatrix,
        float EngineTime,
        Vector3 GlobalAmbient,
        Vector3 SkyColor,
        Vector3 GroundColor,
        uint LightCount)
    {
        public bool IsValid =>
            SurfacesBuffer is not null &&
            MaterialsBuffer is not null &&
            MaterialConstantsBuffer is not null &&
            MaterialTextureBindingsBuffer is not null &&
            LightsBuffer is not null;
    }

    /// <summary>
    /// Publishes per-splat surface records, the avatar material table, scene lights, and camera/environment shading
    /// uniforms for both immediate OpenGL draws and deferred Vulkan draws without per-frame heap allocations.
    /// </summary>
    private sealed class UberSplatBindingPublisher(float depthBiasMeters) : IRenderResourceBindingPublisher
    {
        private readonly float _depthBiasMeters = depthBiasMeters;
        private UberFrameDrawState _currentState;
        private ulong _uniformGeneration = 1UL;
        private ulong _resourceGeneration = 1UL;
        private XRDataBuffer? _lastSurfaces;
        private XRDataBuffer? _lastMaterials;

        public ERenderBindingFrequency Frequency => ERenderBindingFrequency.Object;

        public bool RequiresReadyDescriptorResources => true;

        public ulong Generation
        {
            get
            {
                unchecked
                {
                    _uniformGeneration++;
                    if (_uniformGeneration == 0UL)
                        _uniformGeneration = 1UL;
                    return _uniformGeneration;
                }
            }
        }

        public ulong ResourceGeneration
        {
            get
            {
                UberFrameDrawState state = _currentState;
                if (!state.IsValid)
                    return _resourceGeneration;

                if (ReferenceEquals(_lastSurfaces, state.SurfacesBuffer) &&
                    ReferenceEquals(_lastMaterials, state.MaterialsBuffer))
                {
                    return _resourceGeneration;
                }

                _lastSurfaces = state.SurfacesBuffer;
                _lastMaterials = state.MaterialsBuffer;
                unchecked
                {
                    _resourceGeneration++;
                    if (_resourceGeneration == 0UL)
                        _resourceGeneration = 1UL;
                }

                return _resourceGeneration;
            }
        }

        public void SetCurrentState(in UberFrameDrawState state)
            => _currentState = state;

        public void PublishUniforms(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
        {
            UberFrameDrawState state = _currentState;
            if (!state.IsValid)
                return;

            vertexProgram.Uniform("uDepthBiasMeters", _depthBiasMeters);
            materialProgram.Uniform("ViewMatrix", state.ViewMatrix);
            materialProgram.Uniform("InverseViewMatrix", state.InverseViewMatrix);
            materialProgram.Uniform("EngineTime", state.EngineTime);
            materialProgram.Uniform("GlobalAmbient", state.GlobalAmbient);
            materialProgram.Uniform("SourceSHEnabled", false);
            materialProgram.Uniform("uSplatLightCount", state.LightCount);
            materialProgram.Uniform("uSplatShadeFlags", 0u);
            materialProgram.Uniform("uSplatSkyColor", state.SkyColor);
            materialProgram.Uniform("uSplatGroundColor", state.GroundColor);
        }

        public void PublishResources(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
        {
            UberFrameDrawState state = _currentState;
            if (!state.IsValid)
                return;

            vertexProgram.BindBuffer(state.SurfacesBuffer!, SurfacesBinding);
            materialProgram.BindBuffer(state.MaterialsBuffer!, GaussianAvatarSplatMaterialTable.MaterialsBinding);
            materialProgram.BindBuffer(state.MaterialConstantsBuffer!, GaussianAvatarSplatMaterialTable.MaterialConstantsBinding);
            materialProgram.BindBuffer(state.MaterialTextureBindingsBuffer!, GaussianAvatarSplatMaterialTable.MaterialTextureBindingsBinding);
            materialProgram.BindBuffer(state.LightsBuffer!, LightsBinding);
        }
    }
}
