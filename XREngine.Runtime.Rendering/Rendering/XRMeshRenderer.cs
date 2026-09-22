using XREngine.Extensions;
using MemoryPack;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Shaders.Parameters;
using XREngine.Rendering.Shaders.Generator;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;
using static XREngine.Rendering.XRMesh;

namespace XREngine.Rendering
{
    public enum EMeshGenerationPriority
    {
        Normal = 0,
        RenderPipeline = 1,
        Interactive = 2,
    }

    /// <summary>
    /// Represents a vertex influence from a deformer mesh.
    /// </summary>
    public struct MeshDeformInfluence
    {
        /// <summary>
        /// Index of the vertex in the deformer mesh.
        /// </summary>
        public int VertexIndex;

        /// <summary>
        /// Weight of this influence (0 to 1).
        /// </summary>
        public float Weight;

        public MeshDeformInfluence(int vertexIndex, float weight)
        {
            VertexIndex = vertexIndex;
            Weight = weight;
        }
    }

    /// <summary>
    /// A mesh renderer is in charge of rendering one or more meshes with one or more materials.
    /// The API driver will optimize the rendering of these meshes as much as possible depending on how it's set up.
    /// </summary>
    [MemoryPackable(GenerateType.NoGenerate)]
    public partial class XRMeshRenderer : XRAsset
    {
        private const string NvStereoViewRenderingExtension = "GL_NV_stereo_view_rendering";
        private const string OvrMultiview2Extension = "GL_OVR_multiview2";
        private const string ExtMultiviewExtension = "GL_EXT_multiview";
        private static int _settingsRevision;

        static XRMeshRenderer()
        {
            RuntimeEngine.Rendering.SettingsChanged += () => Interlocked.Increment(ref _settingsRevision);
            RuntimeEngine.Rendering.AntiAliasingSettingsChanged += () => Interlocked.Increment(ref _settingsRevision);
        }

        private static int CurrentSettingsRevision => Volatile.Read(ref _settingsRevision);

        // Held from each aggregate state install through root cache publication and
        // rollback/post-commit cleanup. This is deliberately separate from child
        // buffer wrapper locks: it protects the renderer's multi-buffer generations.
        private readonly object _resourcePublicationGate = new();
        private bool _resourcePublicationTerminated;
        private int _resourcePublicationDepth;
        private int _resourcePublicationThreadId;

        internal void EnterResourcePublicationLease()
        {
            Monitor.Enter(_resourcePublicationGate);
            int threadId = Environment.CurrentManagedThreadId;
            if (_resourcePublicationTerminated || IsDestroyQueued || IsDestroyed)
            {
                Monitor.Exit(_resourcePublicationGate);
                throw new ObjectDisposedException(
                    nameof(XRMeshRenderer),
                    "The mesh renderer entered teardown while resources were being prepared.");
            }

            if (_resourcePublicationDepth == 0)
                _resourcePublicationThreadId = threadId;
            else if (_resourcePublicationThreadId != threadId)
            {
                Monitor.Exit(_resourcePublicationGate);
                throw new InvalidOperationException("Mesh-renderer publication leases are thread-affine.");
            }
            _resourcePublicationDepth++;
        }

        internal void ExitResourcePublicationLease()
        {
            if (_resourcePublicationDepth <= 0 ||
                _resourcePublicationThreadId != Environment.CurrentManagedThreadId)
            {
                throw new InvalidOperationException("Mesh-renderer publication lease ownership was lost.");
            }

            _resourcePublicationDepth--;
            if (_resourcePublicationDepth == 0)
                _resourcePublicationThreadId = 0;
            Monitor.Exit(_resourcePublicationGate);
        }

        internal void ValidateResourcePublicationLease()
        {
            if (!Monitor.IsEntered(_resourcePublicationGate) ||
                _resourcePublicationThreadId != Environment.CurrentManagedThreadId)
            {
                throw new InvalidOperationException("The mesh-renderer publication lease is not held by this thread.");
            }
            if (_resourcePublicationTerminated || IsDestroyQueued || IsDestroyed)
            {
                throw new ObjectDisposedException(
                    nameof(XRMeshRenderer),
                    "The mesh renderer entered teardown during resource publication.");
            }
        }

        internal void EnterResourceTeardownGate()
            => Monitor.Enter(_resourcePublicationGate);

        internal void ExitResourceTeardownGate()
            => Monitor.Exit(_resourcePublicationGate);

        private bool IsResourcePublicationLeaseHeldByCurrentThread
            => _resourcePublicationDepth > 0 &&
               _resourcePublicationThreadId == Environment.CurrentManagedThreadId;

        /// <summary>
        /// This class holds specific information about rendering the mesh depending on the type of pass.
        /// For example:
        /// - Regular desktop pass or two-draw VR pass
        /// - VR stereo pass, using OVR_multiview
        /// - VR stereo pass, using NV_stereo_view_rendering
        /// </summary>
        public abstract class BaseVersion(XRMeshRenderer parent, Func<XRShader, bool> vertexShaderSelector, bool allowShaderPipelines) : GenericRenderObject
        {
            public XRMeshRenderer Parent
            {
                get => parent;
                //set => SetField(ref parent, value);
            }
            public Func<XRShader, bool> VertexShaderSelector
            {
                get => vertexShaderSelector;
                //set => SetField(ref vertexShaderSelector, value);
            }

            private string? _vertexShaderSource;
            private int _vertexShaderSettingsRevision = -1;
            public string? VertexShaderSource
            {
                get
                {
                    int settingsRev = CurrentSettingsRevision;
                    if (_vertexShaderSource is null || _vertexShaderSettingsRevision != settingsRev)
                    {
                        _vertexShaderSource = GenerateVertexShaderSource();
                        _vertexShaderSettingsRevision = settingsRev;
                    }
                    return _vertexShaderSource;
                }
            }

            public bool AllowShaderPipelines
            {
                get => allowShaderPipelines;
                set => SetField(ref allowShaderPipelines, value);
            }

            /// <summary>
            /// Priority bucket assigned to programs built for this version. Set by
            /// <see cref="GetOrCreateVersion"/> based on the version key (main passes get
            /// <see cref="EProgramPriority.Main"/>, interactive meshes get <see cref="EProgramPriority.Interactive"/>,
            /// shadow variants get <see cref="EProgramPriority.Shadow"/>, active VR stereo variants get
            /// <see cref="EProgramPriority.VR"/>, inactive VR stereo variants get <see cref="EProgramPriority.Deferred"/>)
            /// and propagated onto every
            /// <see cref="XRRenderProgram"/> the GL mesh renderer creates from this version.
            /// </summary>
            public EProgramPriority ProgramPriority { get; internal set; } = EProgramPriority.Main;

            /// <summary>
            /// True for generated OVR/EXT multiview versions. Their non-vertex
            /// stages must be selected with the same topology as the vertex stage.
            /// </summary>
            public bool UsesMultiview { get; internal set; }

            /// <summary>
            /// Short, human-readable label that identifies which vertex-shader variant this version
            /// represents (e.g. "Default", "OVRMultiView", "NVStereo", "DirectionalCascadeInstanced").
            /// Surfaced by the shader-program-links panel so each program tells the engineer what pass
            /// and stereo strategy it is for.
            /// </summary>
            private string? _versionKindLabel;
            public virtual string VersionKindLabel
                => _versionKindLabel ??= ResolveVersionKindLabel();

            private string ResolveVersionKindLabel()
            {
                Type type = GetType();
                if (type.IsGenericType)
                {
                    Type genericArg = type.GetGenericArguments()[0];
                    string raw = genericArg.Name;
                    const string suffix = "VertexShaderGenerator";
                    return raw.EndsWith(suffix, StringComparison.Ordinal)
                        ? raw[..^suffix.Length]
                        : raw;
                }

                return type.Name;
            }

            public void ResetVertexShaderSource()
            {
                _vertexShaderSource = null;
                _vertexShaderSettingsRevision = -1;
            }

            protected abstract string? GenerateVertexShaderSource();

            /// <summary>
            /// Use this to render the mesh.
            /// </summary>
            /// <param name="modelMatrix"></param>
            /// <param name="prevModelMatrix"></param>
            /// <param name="materialOverride"></param>
            public void Render(Matrix4x4 modelMatrix, Matrix4x4 prevModelMatrix, XRMaterial? materialOverride, RenderingParameters? renderOptionsOverride, uint instances, EMeshBillboardMode billboardMode, bool forceNoStereo, in AdvancedGpuSceneDrawIdentitySnapshot canonicalDrawIdentitySnapshot)
            {
                // Published mesh versions are backend-neutral until their first draw.
                // Resolve this draw's owner explicitly so cold versions cannot silently
                // skip rendering or broadcast a draw into another viewport's backend.
                if (EnsureApiWrapperForOwnerFirstUse() is not IApiMeshRenderer renderer)
                    throw new InvalidOperationException("The active render owner does not provide mesh submission.");
                if (RuntimeEngine.Rendering.State.RenderingPipelineState?.AdvancedMultisampleBackground == true)
                    renderOptionsOverride = (materialOverride ?? Parent?.Material)?.GetMultisampleBackgroundParameters()
                        ?? throw new InvalidOperationException("Advanced MSAA background requires an admitted material.");
                renderer.Render(modelMatrix, prevModelMatrix, materialOverride, renderOptionsOverride,
                    instances, billboardMode, forceNoStereo, canonicalDrawIdentitySnapshot);
            }
        }

        public class Version<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(XRMeshRenderer renderer, Func<XRShader, bool> vertexShaderSelector, bool allowShaderPipelines)
            : BaseVersion(renderer, vertexShaderSelector, allowShaderPipelines) where T : ShaderGeneratorBase
        {
            protected override string? GenerateVertexShaderSource()
            {
                var m = Parent?.Mesh;
                if (m is null)
                    return null;

                return ((T)Activator.CreateInstance(typeof(T), m)!).Generate();
            }
        }

        private readonly Lock _generatedVertexShaderVersionsLock = new();
        private readonly object[] _generatedVertexShaderVersionCreationGates =
        [
            new(), new(), new(), new(), new(),
            new(), new(), new(), new(), new(),
        ];
        private readonly PendingVersionPublication?[] _pendingVertexShaderVersionPublications = new PendingVersionPublication?[10];
        private readonly Dictionary<int, BaseVersion> _generatedVertexShaderVersions = [];

        private sealed class PendingVersionPublication(object batchIdentity, int ownerThreadId)
        {
            internal object BatchIdentity { get; } = batchIdentity;
            internal int OwnerThreadId { get; } = ownerThreadId;
            internal ManualResetEventSlim Completion { get; } = new(false);
            internal BaseVersion? Version { get; set; }
        }

        [MemoryPackIgnore]
        public IReadOnlyDictionary<int, BaseVersion> GeneratedVertexShaderVersions
        {
            get
            {
                lock (_generatedVertexShaderVersionsLock)
                    return new Dictionary<int, BaseVersion>(_generatedVertexShaderVersions);
            }
        }

        private bool? _shaderPipelinesAllowedForAllVersions;

        /// <summary>
        /// Overrides the shader-pipeline allowance for existing and future generated
        /// vertex-shader versions without forcing those versions to be materialized.
        /// </summary>
        public void SetShaderPipelinesAllowedForAllVersions(bool allow)
        {
            SetField(ref _shaderPipelinesAllowedForAllVersions, allow, nameof(SetShaderPipelinesAllowedForAllVersions));

            BaseVersion[] versions;
            lock (_generatedVertexShaderVersionsLock)
                versions = [.. _generatedVertexShaderVersions.Values];
            foreach (BaseVersion version in versions)
                version.AllowShaderPipelines = allow;
        }

        /// <summary>
        /// Automatically selects the correct version of this mesh to render based on the current rendering state.
        /// </summary>
        /// <param name="forceNoStereo"></param>
        /// <returns></returns>
        private bool _forceOvrMultiview;

        /// <summary>
        /// Selects the supplied OVR vertex variant for explicit multiview output,
        /// including emulated stereo without a running headset runtime.
        /// </summary>
        [YamlIgnore]
        public bool ForceOvrMultiview
        {
            get => _forceOvrMultiview;
            set => SetField(ref _forceOvrMultiview, value);
        }

        public BaseVersion GetVersion(bool forceNoStereo = false)
        {
            bool useMeshDeform = DeformMeshRenderer is not null && _meshDeformInfluences is not null;

            BaseVersion ver;
            if (!useMeshDeform && RuntimeEngine.Rendering.State.IsDirectionalCascadeInstancedLayeredShadowPass)
                return RuntimeEngine.Rendering.State.IsDirectionalCascadeAtlasGroupedShadowPass
                    ? GetDirectionalCascadeAtlasInstancedVersion()
                    : GetDirectionalCascadeInstancedVersion();
            if (!useMeshDeform && RuntimeEngine.Rendering.State.IsPointLightInstancedLayeredShadowPass)
                return RuntimeEngine.Rendering.State.IsPointLightAtlasGroupedShadowPass
                    ? GetPointLightAtlasInstancedVersion()
                    : GetPointLightInstancedVersion();

            // Emulated VR can render an Advanced OVR target while IsInVR is false.
            // Every late/editor mesh in that target must use its declared view topology.
            bool requiresOvrMultiview = ForceOvrMultiview ||
                (!RuntimeEngine.Rendering.State.IsVulkan && XRFrameBuffer.BoundForWriting?.ForceOvrMultiview == true);
            bool stereoPass =
                !forceNoStereo &&
                RuntimeEngine.Rendering.State.IsStereoPass &&
                (requiresOvrMultiview || CanUseVrSpecificVersions());
            if (!stereoPass)
                return useMeshDeform
                    ? GetMeshDeformDefaultVersion()
                    : GetDefaultVersion();

            bool allowNvStereo = !RuntimeEngine.Rendering.State.IsVulkan;
            bool preferNV = allowNvStereo && RuntimeEngine.Rendering.Settings.PreferNVStereo;
            bool hasNvMaterialVertexShader = MaterialHasMatchingVertexShader(HasNVStereoViewRendering);
            bool hasMultiViewMaterialVertexShader = MaterialHasMatchingVertexShader(HasMultiViewExtension);
            bool canUseGeneratedStereoVertexShader = !MaterialHasAnyVertexShader();
            if (requiresOvrMultiview)
            {
                if (!hasMultiViewMaterialVertexShader && !canUseGeneratedStereoVertexShader)
                    throw new InvalidOperationException("The OVR multiview target requires a matching material vertex shader or a generated stereo vertex shader.");
                return useMeshDeform ? GetMeshDeformOVRMultiViewVersion() : GetOVRMultiViewVersion();
            }

            if (useMeshDeform)
            {
                // Use mesh deform versions
                if (stereoPass && RuntimeEngine.Rendering.State.ForwardPlusEnabled && hasMultiViewMaterialVertexShader)
                    ver = GetMeshDeformOVRMultiViewVersion();
                else if (stereoPass && preferNV && RuntimeEngine.Rendering.State.IsNVIDIA && hasNvMaterialVertexShader)
                    ver = GetMeshDeformNVStereoVersion();
                else if (stereoPass && hasMultiViewMaterialVertexShader)
                    ver = GetMeshDeformOVRMultiViewVersion();
                else if (stereoPass && allowNvStereo && hasNvMaterialVertexShader)
                    ver = GetMeshDeformNVStereoVersion();
                else if (stereoPass && canUseGeneratedStereoVertexShader && preferNV && RuntimeEngine.Rendering.State.IsNVIDIA)
                    ver = GetMeshDeformNVStereoVersion();
                else if (stereoPass && canUseGeneratedStereoVertexShader && RuntimeEngine.Rendering.State.HasAnyMultiViewExtension)
                    ver = GetMeshDeformOVRMultiViewVersion();
                else
                    ver = GetMeshDeformDefaultVersion();
            }
            else
            {
                // Use standard versions
                if (stereoPass && RuntimeEngine.Rendering.State.ForwardPlusEnabled && hasMultiViewMaterialVertexShader)
                    ver = GetOVRMultiViewVersion();
                else if (stereoPass && preferNV && RuntimeEngine.Rendering.State.IsNVIDIA && hasNvMaterialVertexShader)
                    ver = GetNVStereoVersion();
                else if (stereoPass && hasMultiViewMaterialVertexShader)
                    ver = GetOVRMultiViewVersion();
                else if (stereoPass && allowNvStereo && hasNvMaterialVertexShader)
                    ver = GetNVStereoVersion();
                else if (stereoPass && canUseGeneratedStereoVertexShader && preferNV && RuntimeEngine.Rendering.State.IsNVIDIA)
                    ver = GetNVStereoVersion();
                else if (stereoPass && canUseGeneratedStereoVertexShader && RuntimeEngine.Rendering.State.HasAnyMultiViewExtension)
                    ver = GetOVRMultiViewVersion();
                else
                    ver = GetDefaultVersion();
            }

            return ver;
        }

        private bool MaterialHasMatchingVertexShader(Func<XRShader, bool> selector)
        {
            var material = Material;
            if (material?.VertexShaders is null || material.VertexShaders.Count == 0)
                return false;

            return material.VertexShaders.Any(selector);
        }

        private bool MaterialHasAnyVertexShader()
            => Material?.VertexShaders is { Count: > 0 };

        private static bool CanUseVrSpecificVersions()
            => RuntimeEngine.VRState.IsInVR || RuntimeEngine.VRState.EmulatedRenderActive;

        public BaseVersion GetDefaultVersion() => GetOrCreateVersion(0);
        public BaseVersion GetOVRMultiViewVersion() => GetOrCreateVersion(1);
        public BaseVersion GetNVStereoVersion() => GetOrCreateVersion(2);

        public void EnsureRenderPipelineVersionsCreated()
        {
            _ = GetDefaultVersion();

            // Only pre-create VR stereo variants when the engine is actually in VR.
            // Otherwise we'd allocate two extra XRRenderProgram graphs per mesh renderer
            // (one for OVR_multiview2, one for NV_stereo_view_rendering) that will never be used.
            // GetVersion() already gates VR variant selection on RuntimeEngine.VRState.IsInVR via
            // CanUseVrSpecificVersions(), so on-demand creation will pick these up if VR turns on later.
            if (!CanUseVrSpecificVersions())
                return;

            _ = GetOVRMultiViewVersion();
            _ = GetNVStereoVersion();
        }

        public BaseVersion GetMeshDeformDefaultVersion() => GetOrCreateVersion(3);
        public BaseVersion GetMeshDeformOVRMultiViewVersion() => GetOrCreateVersion(4);
        public BaseVersion GetMeshDeformNVStereoVersion() => GetOrCreateVersion(5);
        public BaseVersion GetDirectionalCascadeInstancedVersion() => GetOrCreateVersion(6);
        public BaseVersion GetPointLightInstancedVersion() => GetOrCreateVersion(7);
        public BaseVersion GetDirectionalCascadeAtlasInstancedVersion() => GetOrCreateVersion(8);
        public BaseVersion GetPointLightAtlasInstancedVersion() => GetOrCreateVersion(9);

        private static bool HasNVStereoViewRendering(XRShader x)
            => x.HasExtension(NvStereoViewRenderingExtension, XRShader.EExtensionBehavior.Require);
        private static bool HasMultiViewExtension(XRShader x)
            =>
                x.HasExtension(OvrMultiview2Extension, XRShader.EExtensionBehavior.Require) ||
                x.HasExtension(ExtMultiviewExtension, XRShader.EExtensionBehavior.Require);
        private static bool NoSpecialExtensions(XRShader x) =>
            !x.HasExtension(OvrMultiview2Extension, XRShader.EExtensionBehavior.Require) &&
            !x.HasExtension(ExtMultiviewExtension, XRShader.EExtensionBehavior.Require) &&
            !x.HasExtension(NvStereoViewRenderingExtension, XRShader.EExtensionBehavior.Require);

        public XRMeshRenderer() : this((XRMesh?)null, (XRMaterial?)null) { }
        public XRMeshRenderer(XRMesh? mesh, XRMaterial? material)
            : base(deferObjectCachePublication: true)
        {
            try
            {
                using (RenderObjectPublicationScope publication = GenericRenderObject.BeginDeferredPublication())
                {
                    JoinDeferredObjectCachePublication();
                    _mesh = mesh;
                    _material = material;
                    InitializeDrivableBuffers();
                    publication.Complete();
                }
            }
            catch
            {
                AbortRendererConstruction();
                throw;
            }
        }
        public XRMeshRenderer(params (XRMesh mesh, XRMaterial material)[] submeshes)
            : this((IEnumerable<(XRMesh mesh, XRMaterial material)>)submeshes) { }
        public XRMeshRenderer(IEnumerable<(XRMesh mesh, XRMaterial material)> submeshes)
            : base(deferObjectCachePublication: true)
        {
            try
            {
                using (RenderObjectPublicationScope publication = GenericRenderObject.BeginDeferredPublication())
                {
                    JoinDeferredObjectCachePublication();
                    foreach (var (mesh, material) in submeshes)
                        Submeshes.Add(new SubMesh() { Mesh = mesh, Material = material, InstanceCount = 1 });
                    InitializeDrivableBuffers();
                    publication.Complete();
                }
            }
            catch
            {
                AbortRendererConstruction();
                throw;
            }
        }

        /// <summary>
        /// Creates an unpublished renderer used solely to prepare replacement buffers. The asset
        /// cache deferral keeps the staging owner detached; only child buffers created while the
        /// caller's render-publication scope is open join that transaction.
        /// </summary>
        private XRMeshRenderer(bool detachedStagingRenderer)
            : base(deferObjectCachePublication: true)
        {
        }

        private void AbortRendererConstruction()
        {
            try
            {
                AbortFailedConstruction();
            }
            catch (Exception ex)
            {
                RuntimeRenderingHostServices.Diagnostics.LogException(ex);
            }
        }

        private EMeshGenerationPriority _generationPriority;
        /// <summary>
        /// Controls how aggressively render backends should cold-start this mesh's GPU resources.
        /// </summary>
        public EMeshGenerationPriority GenerationPriority
        {
            get => _generationPriority;
            set => SetField(ref _generationPriority, value);
        }

        private BaseVersion GetOrCreateVersion(int versionKey)
        {
            if ((uint)versionKey >= (uint)_generatedVertexShaderVersionCreationGates.Length)
                throw new ArgumentOutOfRangeException(nameof(versionKey), versionKey, "Unknown mesh renderer shader version.");

            object creationGate = _generatedVertexShaderVersionCreationGates[versionKey];
            while (true)
            {
                PendingVersionPublication? pendingToWaitFor = null;
                lock (creationGate)
                {
                    lock (_generatedVertexShaderVersionsLock)
                    {
                        if (_generatedVertexShaderVersions.TryGetValue(versionKey, out BaseVersion? existing))
                        {
                            existing.ProgramPriority = ResolveProgramPriority(versionKey);
                            return existing;
                        }
                    }

                    RenderObjectPublicationScope? activePublication =
                        GenericRenderObject.CurrentDeferredPublicationScope;
                    object? activeBatchIdentity = activePublication?.BatchIdentity;
                    PendingVersionPublication? pending =
                        _pendingVertexShaderVersionPublications[versionKey];
                    if (pending is not null)
                    {
                        if (activeBatchIdentity is not null
                            && ReferenceEquals(pending.BatchIdentity, activeBatchIdentity))
                        {
                            BaseVersion sameBatchVersion = pending.Version
                                ?? throw new InvalidOperationException(
                                    $"Recursive construction of mesh renderer shader version {versionKey} is not supported.");
                            sameBatchVersion.ProgramPriority = ResolveProgramPriority(versionKey);
                            return sameBatchVersion;
                        }

                        pendingToWaitFor = pending;
                    }
                    else
                    {
                        return CreateVersionForPublication(
                            versionKey,
                            activeBatchIdentity,
                            creationGate);
                    }
                }

                pendingToWaitFor!.Completion.Wait();
            }
        }

        private BaseVersion CreateVersionForPublication(
            int versionKey,
            object? activeBatchIdentity,
            object creationGate)
        {
            PendingVersionPublication? pending = null;
            if (activeBatchIdentity is not null)
            {
                pending = new PendingVersionPublication(
                    activeBatchIdentity,
                    Environment.CurrentManagedThreadId);
                _pendingVertexShaderVersionPublications[versionKey] = pending;
            }

            bool lifetimeLeaseHeld = false;
            try
            {
                using RenderObjectPublicationScope publication = GenericRenderObject.BeginDeferredPublication();
                BaseVersion created = versionKey switch
                {
                    0 => new Version<DefaultVertexShaderGenerator>(this, NoSpecialExtensions, ResolveShaderPipelinesAllowedForVersion(versionKey)),
                    1 => new Version<OVRMultiViewVertexShaderGenerator>(this, HasMultiViewExtension, ResolveShaderPipelinesAllowedForVersion(versionKey)),
                    2 => new Version<NVStereoVertexShaderGenerator>(this, HasNVStereoViewRendering, ResolveShaderPipelinesAllowedForVersion(versionKey)),
                    3 => new MeshDeformVersion(this, NoSpecialExtensions, ResolveShaderPipelinesAllowedForVersion(versionKey)),
                    4 => new MeshDeformVersion(this, HasMultiViewExtension, ResolveShaderPipelinesAllowedForVersion(versionKey)) { UseOVRMultiView = true },
                    5 => new MeshDeformVersion(this, HasNVStereoViewRendering, ResolveShaderPipelinesAllowedForVersion(versionKey)) { UseNVStereo = true },
                    6 => new Version<DirectionalCascadeInstancedVertexShaderGenerator>(this, NoSpecialExtensions, ResolveShaderPipelinesAllowedForVersion(versionKey)),
                    7 => new Version<PointLightInstancedVertexShaderGenerator>(this, NoSpecialExtensions, ResolveShaderPipelinesAllowedForVersion(versionKey)),
                    8 => new Version<DirectionalCascadeAtlasInstancedVertexShaderGenerator>(this, NoSpecialExtensions, ResolveShaderPipelinesAllowedForVersion(versionKey)),
                    9 => new Version<PointLightAtlasInstancedVertexShaderGenerator>(this, NoSpecialExtensions, ResolveShaderPipelinesAllowedForVersion(versionKey)),
                    _ => throw new ArgumentOutOfRangeException(nameof(versionKey), versionKey, "Unknown mesh renderer shader version."),
                };

                created.ProgramPriority = ResolveProgramPriority(versionKey);
                created.UsesMultiview = versionKey is 1 or 4;
                if (pending is not null)
                    pending.Version = created;

                publication.Complete(
                    () =>
                    {
                        EnterResourcePublicationLease();
                        lifetimeLeaseHeld = true;
                        lock (_generatedVertexShaderVersionsLock)
                            _generatedVertexShaderVersions.Add(versionKey, created);
                    },
                    () =>
                    {
                        try
                        {
                            if (lifetimeLeaseHeld)
                            {
                                lock (_generatedVertexShaderVersionsLock)
                                    if (_generatedVertexShaderVersions.TryGetValue(versionKey, out BaseVersion? published)
                                        && ReferenceEquals(published, created))
                                    {
                                        _generatedVertexShaderVersions.Remove(versionKey);
                                    }
                            }
                        }
                        finally
                        {
                            if (lifetimeLeaseHeld)
                            {
                                lifetimeLeaseHeld = false;
                                ExitResourcePublicationLease();
                            }
                            CompletePendingVersionPublication(versionKey, pending, creationGate);
                        }
                    },
                    () =>
                    {
                        if (lifetimeLeaseHeld)
                        {
                            lifetimeLeaseHeld = false;
                            ExitResourcePublicationLease();
                        }
                        CompletePendingVersionPublication(versionKey, pending, creationGate);
                    },
                    () => CompletePendingVersionPublication(versionKey, pending, creationGate));
                return created;
            }
            catch
            {
                if (lifetimeLeaseHeld)
                {
                    lifetimeLeaseHeld = false;
                    ExitResourcePublicationLease();
                }
                CompletePendingVersionPublication(versionKey, pending, creationGate);
                throw;
            }
        }

        private void CompletePendingVersionPublication(
            int versionKey,
            PendingVersionPublication? pending,
            object creationGate)
        {
            if (pending is null)
                return;

            lock (creationGate)
            {
                if (ReferenceEquals(_pendingVertexShaderVersionPublications[versionKey], pending))
                    _pendingVertexShaderVersionPublications[versionKey] = null;
                pending.Completion.Set();
            }
        }

        /// <summary>
        /// Resolves the program priority for a given shader version key.
        /// </summary>
        /// <param name="versionKey">The version key of the shader.</param>
        /// <returns>The program priority for the specified shader version.</returns>
        private EProgramPriority ResolveProgramPriority(int versionKey)
        {
            if (IsVrVersionKey(versionKey))
            {
                if (!RuntimeEngine.VRState.IsInVR)
                    return EProgramPriority.Deferred;

                return GenerationPriority == EMeshGenerationPriority.Interactive
                    ? EProgramPriority.Interactive
                    : EProgramPriority.VR;
            }

            if (GenerationPriority == EMeshGenerationPriority.Interactive)
                return EProgramPriority.Interactive;

            return versionKey switch
            {
                0 or 3 => EProgramPriority.Main,
                6 or 7 or 8 or 9 => EProgramPriority.Shadow,
                _ => EProgramPriority.Main,
            };
        }

        /// <summary>
        /// Resolves whether shader pipelines are allowed for a given shader version key.
        /// </summary>
        /// <param name="versionKey">The version key of the shader.</param>
        /// <returns>True if shader pipelines are allowed for the specified shader version; otherwise, false.</returns>
        private bool ResolveShaderPipelinesAllowedForVersion(int versionKey)
            => _shaderPipelinesAllowedForAllVersions ?? DefaultShaderPipelinesAllowedForVersion(versionKey);

        private static bool DefaultShaderPipelinesAllowedForVersion(int versionKey)
            => !IsVrVersionKey(versionKey);

        private static bool IsVrVersionKey(int versionKey)
            => versionKey is 1 or 2 or 4 or 5;

        /// <summary>
        /// Specialized version for mesh deformation that passes deform parameters to the generator.
        /// </summary>
        /// <param name="parent">The parent XRMeshRenderer instance.</param>
        /// <param name="vertexShaderSelector">A function to select the appropriate vertex shader.</param>
        /// <param name="allowShaderPipelines">Indicates whether shader pipelines are allowed.</param>
        public class MeshDeformVersion(XRMeshRenderer parent, Func<XRShader, bool> vertexShaderSelector, bool allowShaderPipelines) : BaseVersion(parent, vertexShaderSelector, allowShaderPipelines)
        {
            public bool UseOVRMultiView { get; set; }
            public bool UseNVStereo { get; set; }

            public override string VersionKindLabel
            {
                get
                {
                    if (UseOVRMultiView) return "MeshDeformOVRMultiView";
                    if (UseNVStereo) return "MeshDeformNVStereo";
                    return "MeshDeformDefault";
                }
            }

            protected override string? GenerateVertexShaderSource()
            {
                var parent = Parent;
                var m = parent?.Mesh;
                if (m is null || parent is null)
                    return null;

                int maxInfluences = parent.MaxMeshDeformInfluences;
                bool optimizeToVec4 = parent.OptimizeMeshDeformToVec4;

                MeshDeformVertexShaderGenerator generator;
                if (UseOVRMultiView)
                    generator = new OVRMultiViewMeshDeformVertexShaderGenerator(m, maxInfluences, optimizeToVec4);
                else if (UseNVStereo)
                    generator = new NVStereoMeshDeformVertexShaderGenerator(m, maxInfluences, optimizeToVec4);
                else
                    generator = new MeshDeformVertexShaderGenerator(m, maxInfluences, optimizeToVec4);

                return generator.Generate();
            }
        }

        // Phase C: default to async generation. Renderers that genuinely
        // require their programs/buffers to be ready on the same frame
        // they are constructed (full-screen pipeline passes, FBO quads,
        // light-volume combine renderers, indirect render pass setup)
        // must opt out by setting GenerateAsync = false explicitly.
        private bool _generateAsync = true;
        /// <summary>
        /// If true, the mesh will be generated for rendering asynchronously.
        /// False by default.
        /// </summary>
        public bool GenerateAsync
        {
            get => _generateAsync;
            set => SetField(ref _generateAsync, value);
        }

        private bool _captureUniformsOnRender;
        /// <summary>
        /// When true, queued render backends snapshot renderer uniform callbacks when
        /// <see cref="Render(Matrix4x4, Matrix4x4, XRMaterial?, uint, bool, RenderingParameters?)"/>
        /// is called, instead of waiting until command recording.
        /// </summary>
        public bool CaptureUniformsOnRender
        {
            get => _captureUniformsOnRender;
            set => SetField(ref _captureUniformsOnRender, value);
        }

        public delegate void DelSetUniforms(XRRenderProgram vertexProgram, XRRenderProgram materialProgram);
        /// <summary>
        /// Subscribe to this event to send your own uniforms to the material.
        /// </summary>
        [MemoryPackIgnore]
        private DelSetUniforms? _settingUniforms;

        public event DelSetUniforms? SettingUniforms
        {
            add => _settingUniforms += value;
            remove => _settingUniforms -= value;
        }

        internal bool HasSettingUniformsHandlers => _settingUniforms is not null;

        /// <summary>
        /// Typed, generation-owned numeric binding publishers eligible for
        /// immutable backend capture and frequency-scoped reuse.
        /// </summary>
        [RuntimeOnly, YamlIgnore, MemoryPackIgnore]
        [field: RuntimeOnly, YamlIgnore, MemoryPackIgnore]
        public RenderBindingPublisherCollection BindingPublishers { get; } = new();

        public delegate void DelPrepareRenderData();
        /// <summary>
        /// Subscribe to this event to upload renderer-owned dynamic data before readiness checks.
        /// </summary>
        [MemoryPackIgnore]
        private DelPrepareRenderData? _preparingRenderData;

        public event DelPrepareRenderData? PreparingRenderData
        {
            add => _preparingRenderData += value;
            remove => _preparingRenderData -= value;
        }

        internal bool HasRenderDataPreparation
            => _preparingRenderData is not null;

        public delegate ShaderVar DelParameterRequested(int index);

        public class SubMesh : XRBase
        {
            private XRMesh? _mesh;
            /// <summary>
            /// The mesh to render for this submesh.
            /// </summary>
            public XRMesh? Mesh
            {
                get => _mesh;
                set => SetField(ref _mesh, value);
            }

            private XRMaterial? _material;
            /// <summary>
            /// The material to use when rendering this submesh.
            /// </summary>
            public XRMaterial? Material
            {
                get => _material;
                set => SetField(ref _material, value);
            }

            private uint _instanceCount;
            /// <summary>
            /// How many instances of this submesh to render.
            /// </summary>
            public uint InstanceCount
            {
                get => _instanceCount;
                set => SetField(ref _instanceCount, value);
            }
        }

        private EventList<SubMesh> _submeshes = [];
        /// <summary>
        /// Represents multiple submeshes, each with their own mesh and material.
        /// Use for the optimized case multiple meshes with multiple materials.
        /// </summary>
        public EventList<SubMesh> Submeshes
        {
            get => _submeshes;
            set => SetField(ref _submeshes, value);
        }

        [MemoryPackIgnore]
        [YamlIgnore]
        private XREngine.Rendering.Models.SubMesh? _sourceSubMeshAsset;
        [MemoryPackIgnore]
        [YamlIgnore]
        public XREngine.Rendering.Models.SubMesh? SourceSubMeshAsset
        {
            get => _sourceSubMeshAsset;
            set => SetField(ref _sourceSubMeshAsset, value);
        }

        private XRMesh? _mesh;
        /// <summary>
        /// Represents the sole mesh this renderer will render.
        /// Use for the most common case of a single mesh with a single material.
        /// </summary>
        public XRMesh? Mesh
        {
            get => _mesh;
            set => SetMeshTransactionally(value);
        }

        private void SetMeshTransactionally(XRMesh? value)
        {
            XRMesh? previous = _mesh;
            if (ReferenceEquals(previous, value) ||
                !OnPropertyChanging(nameof(Mesh), previous, value))
            {
                return;
            }

            using RenderObjectPublicationScope rootPublication = GenericRenderObject.BeginDeferredPublication();
            bool lifetimeLeaseHeld = false;
            bool stateInstalled = false;
            using (RenderObjectPublicationScope meshPublication = GenericRenderObject.BeginDeferredPublication())
            {
                meshPublication.Complete(
                    () =>
                    {
                        EnterResourcePublicationLease();
                        lifetimeLeaseHeld = true;
                        if (!ReferenceEquals(_mesh, previous))
                            throw new InvalidOperationException("The renderer mesh changed while replacement resources were being prepared.");
                        InstallMeshWithoutNotification(value);
                        stateInstalled = true;
                    },
                    () =>
                    {
                        if (!lifetimeLeaseHeld)
                            return;
                        try
                        {
                            if (stateInstalled)
                                InstallMeshWithoutNotification(previous);
                        }
                        finally
                        {
                            stateInstalled = false;
                            lifetimeLeaseHeld = false;
                            ExitResourcePublicationLease();
                        }
                    },
                    () =>
                    {
                        if (!lifetimeLeaseHeld)
                            return;
                        try
                        {
                            OnPropertyChanged(nameof(Mesh), previous, value);
                        }
                        finally
                        {
                            lifetimeLeaseHeld = false;
                            ExitResourcePublicationLease();
                        }
                    });
            }

            InitializeDrivableBuffers(value);
            rootPublication.Complete();
        }

        private void InstallMeshWithoutNotification(XRMesh? value)
        {
            using (XRBase.SuppressPropertyNotifications())
                SetField(ref _mesh, value, nameof(Mesh));
        }

        private XRMaterial? _material;
        /// <summary>
        /// Represents the sole material this renderer will use to render the mesh.
        /// Use for the most common case of a single mesh with a single material.
        /// </summary>
        public XRMaterial? Material
        {
            get => _material;
            set => SetField(ref _material, value);
        }

        [MemoryPackIgnore]
        private RenderBone[]? _bones;
        [MemoryPackIgnore]
        public RenderBone[]? Bones => _bones;

        // Transform callbacks can mark bones dirty while the render thread uploads them.
        [MemoryPackIgnore]
        private readonly object _dirtyBoneSyncRoot = new();

        [MemoryPackIgnore]
        private Dictionary<TransformBase, RenderBone>? _boneByTransform;
        [MemoryPackIgnore]
        private List<uint>? _dirtyBoneIndices;
        [MemoryPackIgnore]
        private bool[]? _dirtyBoneFlags;
        [MemoryPackIgnore]
        private Matrix4x4[]? _dirtyBoneMatrices;

        #region Mesh Deformation

        private XRMeshRenderer? _deformMeshRenderer;
        /// <summary>
        /// The mesh renderer that provides deformation data.
        /// When set, vertices of this mesh will be deformed based on vertex positions from the deformer mesh.
        /// </summary>
        [MemoryPackIgnore]
        public XRMeshRenderer? DeformMeshRenderer
        {
            get => _deformMeshRenderer;
            set => ChangeMeshDeformConfiguration(
                value,
                _meshDeformInfluences,
                _maxMeshDeformInfluences,
                _optimizeMeshDeformToVec4);
        }

        private MeshDeformInfluence[][]? _meshDeformInfluences;
        /// <summary>
        /// Per-vertex array of deformation influences.
        /// Each vertex can have multiple influences from deformer mesh vertices.
        /// Outer array index = vertex index in this mesh.
        /// Inner array = influences affecting that vertex.
        /// </summary>
        [MemoryPackIgnore]
        public MeshDeformInfluence[][]? MeshDeformInfluences
        {
            get => _meshDeformInfluences;
            set => ChangeMeshDeformConfiguration(
                _deformMeshRenderer,
                value,
                _maxMeshDeformInfluences,
                _optimizeMeshDeformToVec4);
        }

        private int _maxMeshDeformInfluences = 8;
        /// <summary>
        /// Maximum number of deformer mesh vertices that can influence each vertex.
        /// Default is 8. Lower values use more efficient vec4 packing when = 4.
        /// </summary>
        public int MaxMeshDeformInfluences
        {
            get => _maxMeshDeformInfluences;
            set => ChangeMeshDeformConfiguration(
                _deformMeshRenderer,
                _meshDeformInfluences,
                Math.Max(1, value),
                _optimizeMeshDeformToVec4);
        }

        private uint _meshDeformLastTargetVertexCount;
        private uint _meshDeformLastDeformerVertexCount;
        private int _meshDeformLastInvalidInfluenceCount;
        private int _meshDeformLastTruncatedVertexCount;

        [Category("Mesh Deform Diagnostics")]
        [DisplayName("Mesh Deform Enabled")]
        public bool MeshDeformEnabled => DeformMeshRenderer is not null && _meshDeformInfluences is not null;

        [Category("Mesh Deform Diagnostics")]
        [DisplayName("Mesh Deform Influence Mode")]
        public string MeshDeformInfluenceMode
            => OptimizeMeshDeformToVec4 && MaxMeshDeformInfluences <= 4 ? "Vec4" : "SSBO";

        [Category("Mesh Deform Diagnostics")]
        [DisplayName("Mesh Deform Source Path")]
        public string MeshDeformSourcePath
        {
            get
            {
                if (DeformMeshRenderer is null)
                    return "None";
                if (DeformMeshRenderer.SkinnedPositionsBuffer is not null)
                    return "ComputeSkinnedSeparateBuffers";
                if (DeformMeshRenderer.SkinnedInterleavedBuffer is not null)
                    return "ComputeSkinnedInterleavedFallbackCopy";
                return "MeshBuffers";
            }
        }

        [Category("Mesh Deform Diagnostics")]
        [DisplayName("Mesh Deform Validation")]
        public string MeshDeformValidationSummary
            => !MeshDeformEnabled
                ? "Inactive"
                : $"targetVerts={_meshDeformLastTargetVertexCount}, deformerVerts={_meshDeformLastDeformerVertexCount}, invalidInfluences={_meshDeformLastInvalidInfluenceCount}, truncatedVertices={_meshDeformLastTruncatedVertexCount}";

        [Category("Mesh Deform Diagnostics")]
        [DisplayName("Mesh Deform Normal Source")]
        public string MeshDeformNormalSource
            => DeformerNormalsBuffer is null ? "NotBound" : ResolveMeshDeformChannelSource(DeformMeshRenderer?.SkinnedNormalsBuffer, DeformMeshRenderer?.SkinnedInterleavedBuffer, DeformMeshRenderer?.Mesh?.NormalOffset.HasValue == true);

        [Category("Mesh Deform Diagnostics")]
        [DisplayName("Mesh Deform Tangent Source")]
        public string MeshDeformTangentSource
            => DeformerTangentsBuffer is null ? "NotBound" : ResolveMeshDeformChannelSource(DeformMeshRenderer?.SkinnedTangentsBuffer, DeformMeshRenderer?.SkinnedInterleavedBuffer, DeformMeshRenderer?.Mesh?.TangentOffset.HasValue == true);

        private bool _optimizeMeshDeformToVec4 = true;
        /// <summary>
        /// If true and MaxMeshDeformInfluences = 4, packs indices and weights into vec4 attributes for better performance.
        /// </summary>
        public bool OptimizeMeshDeformToVec4
        {
            get => _optimizeMeshDeformToVec4;
            set => ChangeMeshDeformConfiguration(
                _deformMeshRenderer,
                _meshDeformInfluences,
                _maxMeshDeformInfluences,
                value);
        }

        private void ChangeMeshDeformConfiguration(
            XRMeshRenderer? deformer,
            MeshDeformInfluence[][]? influences,
            int maxInfluences,
            bool optimizeToVec4)
        {
            maxInfluences = Math.Max(1, maxInfluences);
            XRMeshRenderer? previousDeformer = _deformMeshRenderer;
            MeshDeformInfluence[][]? previousInfluences = _meshDeformInfluences;
            int previousMaxInfluences = _maxMeshDeformInfluences;
            bool previousOptimizeToVec4 = _optimizeMeshDeformToVec4;
            bool deformerChanged = !ReferenceEquals(previousDeformer, deformer);
            bool influencesChanged = !ReferenceEquals(previousInfluences, influences);
            bool maxChanged = previousMaxInfluences != maxInfluences;
            bool optimizeChanged = previousOptimizeToVec4 != optimizeToVec4;
            if (!deformerChanged && !influencesChanged && !maxChanged && !optimizeChanged)
                return;

            if ((deformerChanged && !OnPropertyChanging(nameof(DeformMeshRenderer), previousDeformer, deformer)) ||
                (influencesChanged && !OnPropertyChanging(nameof(MeshDeformInfluences), previousInfluences, influences)) ||
                (maxChanged && !OnPropertyChanging(nameof(MaxMeshDeformInfluences), previousMaxInfluences, maxInfluences)) ||
                (optimizeChanged && !OnPropertyChanging(nameof(OptimizeMeshDeformToVec4), previousOptimizeToVec4, optimizeToVec4)))
            {
                return;
            }

            using RenderObjectPublicationScope rootPublication = GenericRenderObject.BeginDeferredPublication();
            bool lifetimeLeaseHeld = false;
            bool stateInstalled = false;
            using (RenderObjectPublicationScope configurationPublication = GenericRenderObject.BeginDeferredPublication())
            {
                configurationPublication.Complete(
                    () =>
                    {
                        EnterResourcePublicationLease();
                        lifetimeLeaseHeld = true;
                        if (!ReferenceEquals(_deformMeshRenderer, previousDeformer) ||
                            !ReferenceEquals(_meshDeformInfluences, previousInfluences) ||
                            _maxMeshDeformInfluences != previousMaxInfluences ||
                            _optimizeMeshDeformToVec4 != previousOptimizeToVec4)
                        {
                            throw new InvalidOperationException("Mesh-deformation configuration changed while replacement buffers were being prepared.");
                        }

                        InstallMeshDeformConfigurationWithoutNotification(
                            deformer,
                            influences,
                            maxInfluences,
                            optimizeToVec4);
                        stateInstalled = true;
                    },
                    () =>
                    {
                        if (!lifetimeLeaseHeld)
                            return;
                        try
                        {
                            if (stateInstalled)
                            {
                                InstallMeshDeformConfigurationWithoutNotification(
                                    previousDeformer,
                                    previousInfluences,
                                    previousMaxInfluences,
                                    previousOptimizeToVec4);
                            }
                        }
                        finally
                        {
                            stateInstalled = false;
                            lifetimeLeaseHeld = false;
                            ExitResourcePublicationLease();
                        }
                    },
                    () =>
                    {
                        if (!lifetimeLeaseHeld)
                            return;
                        try
                        {
                            if (maxChanged || optimizeChanged)
                                ResetMeshDeformVersionShaders();
                            if (deformerChanged)
                                OnPropertyChanged(nameof(DeformMeshRenderer), previousDeformer, deformer);
                            if (influencesChanged)
                                OnPropertyChanged(nameof(MeshDeformInfluences), previousInfluences, influences);
                            if (maxChanged)
                                OnPropertyChanged(nameof(MaxMeshDeformInfluences), previousMaxInfluences, maxInfluences);
                            if (optimizeChanged)
                                OnPropertyChanged(nameof(OptimizeMeshDeformToVec4), previousOptimizeToVec4, optimizeToVec4);
                        }
                        finally
                        {
                            lifetimeLeaseHeld = false;
                            ExitResourcePublicationLease();
                        }
                    });
            }

            RebuildMeshDeformBuffers(_mesh, deformer, influences, maxInfluences, optimizeToVec4);
            rootPublication.Complete();
        }

        private void InstallMeshDeformConfigurationWithoutNotification(
            XRMeshRenderer? deformer,
            MeshDeformInfluence[][]? influences,
            int maxInfluences,
            bool optimizeToVec4)
        {
            using (XRBase.SuppressPropertyNotifications())
            {
                SetField(ref _deformMeshRenderer, deformer, nameof(DeformMeshRenderer));
                SetField(ref _meshDeformInfluences, influences, nameof(MeshDeformInfluences));
                SetField(ref _maxMeshDeformInfluences, maxInfluences, nameof(MaxMeshDeformInfluences));
                SetField(ref _optimizeMeshDeformToVec4, optimizeToVec4, nameof(OptimizeMeshDeformToVec4));
            }
        }

        private void ResetMeshDeformVersionShaders()
        {
            // Reset the mesh deform version shader sources so they regenerate with new parameters
            lock (_generatedVertexShaderVersionsLock)
            {
                if (_generatedVertexShaderVersions.TryGetValue(3, out BaseVersion? v3))
                    v3.ResetVertexShaderSource();
                if (_generatedVertexShaderVersions.TryGetValue(4, out BaseVersion? v4))
                    v4.ResetVertexShaderSource();
                if (_generatedVertexShaderVersions.TryGetValue(5, out BaseVersion? v5))
                    v5.ResetVertexShaderSource();
            }
        }

        #endregion

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Submeshes):
                    //Link added and removed events
                    Submeshes.PostAnythingAdded += Submeshes_PostAnythingAdded;
                    Submeshes.PostAnythingRemoved += Submeshes_PostAnythingRemoved;
                    break;
            }
        }
        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(Submeshes):
                        //Unlink added and removed events
                        Submeshes.PostAnythingAdded -= Submeshes_PostAnythingAdded;
                        Submeshes.PostAnythingRemoved -= Submeshes_PostAnythingRemoved;
                        break;
                }
            }
            return change;
        }

        private void Submeshes_PostAnythingRemoved(SubMesh item)
            => UpdateIndirectDrawBuffer();
        private void Submeshes_PostAnythingAdded(SubMesh item)
            => UpdateIndirectDrawBuffer();

        private void UpdateIndirectDrawBuffer()
        {
            //Initialize the indirect draw buffer to render each submesh with a single draw call
            switch (IndirectDrawBuffer)
            {
                case null:
                    uint componentCount = (uint)Marshal.SizeOf<DrawElementsIndirectCommand>() / sizeof(uint);
                    IndirectDrawBuffer = new XRDataBuffer(
                        $"{ECommonBufferType.IndirectDraw}Buffer",
                        EBufferTarget.DrawIndirectBuffer,
                        (uint)Submeshes.Count,
                        EComponentType.UInt,
                        componentCount,
                        false,
                        true)
                    {
                        Usage = EBufferUsage.StaticCopy,
                        DisposeOnPush = false
                    };
                    break;
                default:
                    if (_submeshes.Count == 0)
                    {
                        IndirectDrawBuffer.Destroy();
                        IndirectDrawBuffer = null;
                        return;
                    }
                    IndirectDrawBuffer.Resize((uint)_submeshes.Count, false);
                    break;
            }

            int indicesIndex = 0;
            int verticesIndex = 0;
            for (int i = 0; i < _submeshes.Count; i++)
            {
                var submesh = _submeshes[i];
                var mesh = submesh?.Mesh;

                // Use the current accumulated offsets (order of appearance)
                int firstIndex = indicesIndex;
                int baseVertex = verticesIndex;

                DrawElementsIndirectCommand cmd = submesh is null || mesh is null
                    ? new DrawElementsIndirectCommand()
                    {
                        Count = 0,
                        InstanceCount = 0,
                        FirstIndex = 0,
                        BaseVertex = 0,
                        BaseInstance = (uint)i
                    }
                    : new DrawElementsIndirectCommand()
                    {
                        Count = (uint)mesh.IndexCount,
                        InstanceCount = submesh.InstanceCount,
                        FirstIndex = (uint)firstIndex,
                        BaseVertex = baseVertex,
                        BaseInstance = (uint)i
                    };

                IndirectDrawBuffer.Set((uint)i, cmd);

                // Advance offsets after writing the command, preserving order of appearance
                if (mesh is not null)
                {
                    indicesIndex += mesh.IndexCount;
                    verticesIndex += mesh.VertexCount;
                }
            }

        }

        public XRDataBuffer? GenerateCombinedIndexBuffer()
        {
            // Generate a combined index buffer for all submeshes
            if (Submeshes.Count == 0)
                return null;

            int totalIndexCount = 0;
            foreach (var submesh in Submeshes)
                if (submesh.Mesh is not null)
                    totalIndexCount += submesh.Mesh.IndexCount;
            if (totalIndexCount == 0)
                return null;

            using RenderObjectPublicationScope publication = GenericRenderObject.BeginDeferredPublication();

            IndexSize indexSize = IndexSize.Byte;
            if (totalIndexCount > ushort.MaxValue)
                indexSize = IndexSize.FourBytes;
            else if (totalIndexCount > byte.MaxValue)
                indexSize = IndexSize.TwoBytes;

            XRDataBuffer combinedIndexBuffer = new(
                "CombinedIndexBuffer",
                EBufferTarget.ElementArrayBuffer,
                (uint)totalIndexCount,
                EComponentType.UInt,
                1,
                false,
                true)
            {
                Usage = EBufferUsage.StaticCopy,
                DisposeOnPush = false
            };

            int currentIndex = 0;
            foreach (var submesh in Submeshes)
            {
                if (submesh.Mesh is null)
                    continue;

                int indexCount = submesh.Mesh.IndexCount;
                if (indexCount == 0)
                    continue;

                var success = submesh.Mesh.PopulateIndexBuffer(EPrimitiveType.Triangles, combinedIndexBuffer, indexSize);
                if (!success)
                {
                    Debug.LogWarning($"Failed to populate index buffer for submesh {submesh.Mesh.Name} in XRMeshRenderer.");
                    continue;
                }
                currentIndex += indexCount;
            }
            combinedIndexBuffer.PushSubData();
            publication.Complete();
            return combinedIndexBuffer;
        }

        /// <summary>
        /// Use this to render the mesh with an identity transform matrix.
        /// </summary>
        public void Render(XRMaterial? materialOverride = null, uint instances = 1u, RenderingParameters? renderOptionsOverride = null)
            => Render(Matrix4x4.Identity, Matrix4x4.Identity, materialOverride, instances, renderOptionsOverride: renderOptionsOverride);

        /// <summary>
        /// Use this to render the mesh.
        /// </summary>
        /// <param name="modelMatrix"></param>
        /// <param name="materialOverride"></param>
        public void Render(Matrix4x4 modelMatrix, Matrix4x4 prevModelMatrix, XRMaterial? materialOverride = null, uint instances = 1u, bool forceNoStereo = false, RenderingParameters? renderOptionsOverride = null, AdvancedGpuSceneDrawIdentitySnapshot canonicalDrawIdentitySnapshot = default)
            => GetVersion(forceNoStereo).Render(modelMatrix, prevModelMatrix, materialOverride, renderOptionsOverride, instances, Material?.BillboardMode ?? EMeshBillboardMode.None, forceNoStereo, canonicalDrawIdentitySnapshot);

        public AbstractRenderAPIObject? EnsureApiRenderObject(bool forceNoStereo = false)
            => AbstractRenderer.Current?.GetOrCreateAPIRenderObject(GetVersion(forceNoStereo));

        public bool TryPrepareForRendering(bool forceNoStereo = false)
        {
            BaseVersion version = GetVersion(forceNoStereo);
            AbstractRenderAPIObject? apiObject = AbstractRenderer.Current?.GetOrCreateAPIRenderObject(version);
            if (apiObject is IRenderPreparationState preparationState)
                return preparationState.TryPrepareForRendering();

            return apiObject is not null;
        }

        /// <summary>
        /// Same as <see cref="TryPrepareForRendering(bool)"/> but also returns the most recent
        /// preparation stage result (e.g. "Ready", "ProgramsPending", "BuffersPending").
        /// </summary>
        public bool TryPrepareForRendering(out string reason, bool forceNoStereo = false)
        {
            BaseVersion version = GetVersion(forceNoStereo);
            AbstractRenderAPIObject? apiObject = AbstractRenderer.Current?.GetOrCreateAPIRenderObject(version);
            if (apiObject is IRenderPreparationState preparationState)
                return preparationState.TryPrepareForRendering(out reason);

            reason = apiObject is null ? "NoApiObject" : "NoPreparationState";
            return apiObject is not null;
        }

        /// <summary>
        /// Supplemental detail captured by the underlying API object on the most recent
        /// <see cref="TryPrepareForRendering(out string, bool)"/> call. Empty when not available.
        /// </summary>
        public string GetLastPrepareDetail(bool forceNoStereo = false)
        {
            BaseVersion version = GetVersion(forceNoStereo);
            AbstractRenderAPIObject? apiObject = AbstractRenderer.Current?.GetOrCreateAPIRenderObject(version);
            if (apiObject is IRenderPreparationState preparationState)
                return preparationState.LastPrepareDetail;
            return string.Empty;
        }

        /// <summary>
        /// Get the weight of a blendshape by name, with the weight returned being a percentage from 0 to 100.
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public float GetBlendshapeWeight(string name)
            => GetBlendshapeIndex(name, out uint index) ? GetBlendshapeWeight(index) : 0.0f;

        /// <summary>
        /// Get the weight of a blendshape by name, with the weight returned being a normalized value from 0 to 1.
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public float GetBlendshapeWeightNormalized(string name)
            => GetBlendshapeIndex(name, out uint index) ? GetBlendshapeWeightNormalized(index) : 0.0f;

        /// <summary>
        /// Set the weight of a blendshape by name, with weight being a percentage from 0 to 100. Exceeding this range is allowed.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="weight"></param>
        public void SetBlendshapeWeight(string name, float weight)
        {
            if (GetBlendshapeIndex(name, out uint index))
                SetBlendshapeWeight(index, weight);
        }

        /// <summary>
        /// Set the weight of a blendshape by name, with weight being a normalized value from 0 to 1. Exceeding this range is allowed.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="weight"></param>
        public void SetBlendshapeWeightNormalized(string name, float weight)
        {
            if (GetBlendshapeIndex(name, out uint index))
                SetBlendshapeWeightNormalized(index, weight);
        }

        public bool GetBlendshapeIndex(string name, out uint index)
        {
            index = 0;
            return Mesh is not null && Mesh.GetBlendshapeIndex(name, out index);
        }

        /// <summary>
        /// Get the weight of a blendshape by index, with the weight returned being a percentage from 0 to 100.
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        public float GetBlendshapeWeight(uint index)
            => GetBlendshapeWeightNormalized(index) * 100.0f;

        /// <summary>
        /// Get the weight of a blendshape by index, with the weight returned being a normalized value from 0 to 1.
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        public float GetBlendshapeWeightNormalized(uint index)
        {
            XRDataBuffer? weights = CaptureBlendshapeResources().Weights;
            if (weights is null || index >= (Mesh?.BlendshapeCount ?? 0u))
                return 0.0f;

            return weights.GetFloat(index);
        }

        /// <summary>
        /// Set the weight of a blendshape by index, with weight being a percentage from 0 to 100. Exceeding this range is allowed.
        /// </summary>
        /// <param name="index"></param>
        /// <param name="weight"></param>
        public void SetBlendshapeWeight(uint index, float weight)
            => SetBlendshapeWeightNormalized(index, weight / 100.0f);

        /// <summary>
        /// Set the weight of a blendshape by index, with weight being a normalized value from 0 to 1. Exceeding this range is allowed.
        /// </summary>
        /// <param name="index"></param>
        /// <param name="weight"></param>
        public void SetBlendshapeWeightNormalized(uint index, float weight)
        {
            XRDataBuffer? weights = CaptureBlendshapeResources().Weights;
            if (weights is null || index >= (Mesh?.BlendshapeCount ?? 0u))
                return;

            float previous = weights.GetFloat(index);
            if (previous.Equals(weight))
                return;

            weights.SetFloat(index, weight);
            MarkBlendshapeWeightDirty(index);
            MarkSkinnedOutputDirty();
        }

        private void InitializeDrivableBuffers()
            => InitializeDrivableBuffers(_mesh);

        private void InitializeDrivableBuffers(XRMesh? mesh)
        {
            if ((mesh?.HasSkinning ?? false) && RuntimeEngine.Rendering.Settings.AllowSkinning)
                PopulateBoneMatrixBuffers(mesh);
            else
                ClearBoneMatrixBuffersTransactionally(mesh);

            if ((mesh?.HasBlendshapes ?? false) && RuntimeEngine.Rendering.Settings.AllowBlendshapes)
                PopulateBlendshapeWeightsBuffer(mesh);
            else
                ClearBlendshapeBuffersTransactionally(mesh);

            RebuildMeshDeformBuffers(
                mesh,
                _deformMeshRenderer,
                _meshDeformInfluences,
                _maxMeshDeformInfluences,
                _optimizeMeshDeformToVec4);
        }

        public override void Destroy(bool now = false)
        {
            if (!now || IsDestroyed)
            {
                base.Destroy(now);
                return;
            }

            if (IsResourcePublicationLeaseHeldByCurrentThread)
            {
                // Teardown requested synchronously by a publication observer must
                // run after this generation either commits or rolls back.
                base.Destroy(now: false);
                return;
            }

            Monitor.Enter(_resourcePublicationGate);
            try
            {
                _resourcePublicationTerminated = true;
                base.Destroy(now: true);
            }
            finally
            {
                if (!IsDestroyed)
                    _resourcePublicationTerminated = false;
                Monitor.Exit(_resourcePublicationGate);
            }
        }

        protected override void OnDestroying()
        {
            try
            {
                ResetDrivableBuffers();
                IndirectDrawBuffer?.Dispose();
                IndirectDrawBuffer = null;

                BaseVersion[] versions;
                lock (_generatedVertexShaderVersionsLock)
                {
                    versions = [.. _generatedVertexShaderVersions.Values];
                    _generatedVertexShaderVersions.Clear();
                }
                foreach (BaseVersion version in versions)
                    version.Destroy(now: true);
            }
            finally
            {
                base.OnDestroying();
            }
        }

        /// <summary>
        /// Ensures skinning buffers (bone matrices) are initialized if the mesh has skinning data.
        /// Call this before attempting to bind GPU-driven bone palette shaders if there's a chance
        /// the buffers weren't created during renderer construction.
        /// </summary>
        /// <returns>True if the renderer skin palette is available after this call; false otherwise.</returns>
        public bool EnsureSkinningBuffers(bool logWarnings = true)
        {
            BoneResourceSnapshot resources = CaptureBoneResources();
            if (resources.BoneMatrices is not null
                && resources.InverseBindMatrices is not null
                && resources.SkinPalette is not null)
                return true;

            if (resources.BoneMatrices is not null
                || resources.InverseBindMatrices is not null
                || resources.SkinPalette is not null)
                ClearBoneMatrixBuffersTransactionally(_mesh);

            XRMesh? mesh = Mesh;
            if (mesh is null)
            {
                if (logWarnings)
                    Debug.LogWarning($"[XRMeshRenderer] EnsureSkinningBuffers: Mesh is null, cannot initialize skinning buffers. Renderer={GetHashCode():X}");
                return false;
            }

            if (!mesh.HasSkinning)
            {
                if (logWarnings)
                    Debug.LogWarning($"[XRMeshRenderer] EnsureSkinningBuffers: Mesh '{mesh.Name}' has no skinning data. Renderer={GetHashCode():X}");
                return false;
            }

            if (!RuntimeEngine.Rendering.Settings.AllowSkinning)
            {
                if (logWarnings)
                    Debug.LogWarning($"[XRMeshRenderer] EnsureSkinningBuffers: Skinning is disabled in render settings. Mesh='{mesh.Name}', Renderer={GetHashCode():X}");
                return false;
            }

            if (logWarnings)
                Debug.LogWarning($"[XRMeshRenderer] EnsureSkinningBuffers: BoneMatricesBuffer was null, initializing late. This may indicate a timing issue with mesh/renderer creation. Mesh='{mesh.Name}', UtilizedBones={mesh.GetSkinningBufferStateSnapshot().UtilizedBones.Length}, Renderer={GetHashCode():X}");

            PopulateBoneMatrixBuffers(mesh);

            resources = CaptureBoneResources();
            if (resources.BoneMatrices is null
                || resources.InverseBindMatrices is null
                || resources.SkinPalette is null)
            {
                if (logWarnings)
                    Debug.LogWarning($"[XRMeshRenderer] EnsureSkinningBuffers: PopulateBoneMatrixBuffers did not create buffer. Mesh='{mesh.Name}', Renderer={GetHashCode():X}");
                return false;
            }

            return true;
        }

        public bool EnsureBlendshapeBuffers(bool logWarnings = true)
        {
            if (CaptureBlendshapeResources().Weights is not null)
                return true;

            if (Mesh is null)
            {
                if (logWarnings)
                    Debug.LogWarning($"[XRMeshRenderer] EnsureBlendshapeBuffers: Mesh is null, cannot initialize blendshape buffers. Renderer={GetHashCode():X}");
                return false;
            }

            if (!Mesh.HasBlendshapes)
            {
                if (logWarnings)
                    Debug.LogWarning($"[XRMeshRenderer] EnsureBlendshapeBuffers: Mesh '{Mesh.Name}' has no blendshape data. Renderer={GetHashCode():X}");
                return false;
            }

            if (!RuntimeEngine.Rendering.Settings.AllowBlendshapes)
            {
                if (logWarnings)
                    Debug.LogWarning($"[XRMeshRenderer] EnsureBlendshapeBuffers: Blendshapes are disabled in render settings. Mesh='{Mesh.Name}', Renderer={GetHashCode():X}");
                return false;
            }

            PopulateBlendshapeWeightsBuffer();
            return BlendshapeWeights is not null;
        }

        private void PopulateBlendshapeWeightsBuffer()
            => PopulateBlendshapeWeightsBuffer(_mesh);

        private void PopulateBlendshapeWeightsBuffer(XRMesh? sourceMesh)
        {
            XRMesh.BufferCollection targetBuffers = Buffers;
            XRMesh.BufferCollection.PreparedBufferTicket preparationTicket =
                targetBuffers.CapturePreparationTicket(BlendshapeBufferState.CollectionKeys);
            int expectedSettingsRevision = CurrentSettingsRevision;
            XRMeshRenderer staging = new(detachedStagingRenderer: true)
            {
                _mesh = sourceMesh,
            };
            BlendshapeBufferState prepared;
            try
            {
                using RenderObjectPublicationScope publication = GenericRenderObject.BeginDeferredPublication();
                staging.PopulateBlendshapeWeightsBufferCore();
                prepared = staging.CaptureBlendshapeBufferState();
                BlendshapeBufferState? previous = null;
                XRMesh.BufferCollection.PreparedBufferBatch? batch = null;
                bool lifetimeLeaseHeld = false;
                publication.Complete(
                    () =>
                    {
                        EnterResourcePublicationLease();
                        lifetimeLeaseHeld = true;
                        if (!ReferenceEquals(_mesh, sourceMesh) ||
                            CurrentSettingsRevision != expectedSettingsRevision ||
                            !RuntimeEngine.Rendering.Settings.AllowBlendshapes)
                        {
                            throw new InvalidOperationException("Blendshape configuration changed while renderer buffers were being prepared.");
                        }

                        batch = targetBuffers.SwapPreparedBatch(
                            prepared.EnumerateCollectionBuffers(),
                            BlendshapeBufferState.CollectionKeys,
                            expectedTicket: preparationTicket,
                            installState: () =>
                            {
                                previous = CaptureBlendshapeBufferState();
                                ApplyBlendshapeBufferState(prepared);
                            },
                            restoreStateOnFailure: () =>
                            {
                                if (previous is not null)
                                    ApplyBlendshapeBufferState(previous);
                            });
                        ValidateResourcePublicationLease();
                    },
                    () =>
                    {
                        if (!lifetimeLeaseHeld)
                            return;
                        try
                        {
                            if (batch is not null)
                                targetBuffers.RestorePreparedBatch(
                                    batch,
                                    () =>
                                    {
                                        if (previous is not null)
                                            ApplyBlendshapeBufferState(previous);
                                    });
                        }
                        finally
                        {
                            lifetimeLeaseHeld = false;
                            ExitResourcePublicationLease();
                        }
                    },
                    () =>
                    {
                        if (!lifetimeLeaseHeld)
                            return;
                        try
                        {
                            if (batch is not null)
                                targetBuffers.DisposeReplacedBuffers(batch);
                            previous?.DestroyPrecombinedBuffers();
                        }
                        finally
                        {
                            lifetimeLeaseHeld = false;
                            ExitResourcePublicationLease();
                        }
                    });
                staging.DetachBlendshapeBufferState();
            }
            finally
            {
                staging.AbortRendererConstruction();
            }
        }

        private void PopulateBlendshapeWeightsBufferCore()
        {
            uint blendshapeCount = Mesh?.BlendshapeCount ?? 0;
            XRDataBuffer weights = new($"{ECommonBufferType.BlendshapeWeights}Buffer", EBufferTarget.ShaderStorageBuffer, blendshapeCount.Align(4), EComponentType.Float, 1, false, false)
            {
                Usage = EBufferUsage.DynamicDraw,
                DisposeOnPush = false
            };

            for (uint i = 0; i < blendshapeCount; i++)
                weights.Set(i, 0.0f);
            Buffers.Add(weights.AttributeName, weights);
            XRDataBuffer activeWeights = new($"{ECommonBufferType.BlendshapeActiveWeights}Buffer", EBufferTarget.ShaderStorageBuffer, blendshapeCount.Align(4), EComponentType.Float, 2, false, false)
            {
                Usage = EBufferUsage.DynamicDraw,
                DisposeOnPush = false
            };
            Buffers.Add(activeWeights.AttributeName, activeWeights);
            BlendshapeBufferState prepared = CloneBlendshapeBufferState(
                Volatile.Read(ref _blendshapeBufferState));
            prepared.GenerationId = 0L;
            prepared.Weights = weights;
            prepared.ActiveWeights = activeWeights;
            prepared.ActiveCount = 0;
            prepared.DirtyStart = uint.MaxValue;
            prepared.DirtyEnd = 0u;
            prepared.WeightsVersion++;
            prepared.PrecombinedInputVersion++;
            prepared.Invalidated = false;
            prepared.ActiveListInvalidated = false;
            ApplyBlendshapeBufferState(prepared);
            if (RuntimeEngine.Rendering.Settings.EnableBlendshapePrecombinePass && Mesh is { VertexCount: > 0 } mesh)
                EnsurePrecombinedBlendshapeBuffers(mesh);
        }

        private void ClearBlendshapeBuffersTransactionally(XRMesh? expectedMesh)
        {
            XRMesh.BufferCollection targetBuffers = Buffers;
            XRMesh.BufferCollection.PreparedBufferTicket preparationTicket =
                targetBuffers.CapturePreparationTicket(BlendshapeBufferState.CollectionKeys);
            BlendshapeBufferState? previous = null;
            BlendshapeBufferState? empty = null;
            using RenderObjectPublicationScope publication = GenericRenderObject.BeginDeferredPublication();
            XRMesh.BufferCollection.PreparedBufferBatch? batch = null;
            bool lifetimeLeaseHeld = false;
            publication.Complete(
                () =>
                {
                    EnterResourcePublicationLease();
                    lifetimeLeaseHeld = true;
                    if (!ReferenceEquals(_mesh, expectedMesh))
                        throw new InvalidOperationException("The renderer mesh changed while blendshape buffers were being cleared.");
                    batch = targetBuffers.SwapPreparedBatch(
                        [],
                        BlendshapeBufferState.CollectionKeys,
                        expectedTicket: preparationTicket,
                        installState: () =>
                        {
                            previous = CaptureBlendshapeBufferState();
                            empty = new BlendshapeBufferState
                            {
                                DirtyStart = uint.MaxValue,
                                WeightsVersion = previous.WeightsVersion + 1UL,
                                PrecombinedInputVersion = previous.PrecombinedInputVersion + 1UL,
                            };
                            ApplyBlendshapeBufferState(empty);
                        },
                        restoreStateOnFailure: () =>
                        {
                            if (previous is not null)
                                ApplyBlendshapeBufferState(previous);
                        });
                    ValidateResourcePublicationLease();
                },
                () =>
                {
                    if (!lifetimeLeaseHeld)
                        return;
                    try
                    {
                        if (batch is not null)
                            targetBuffers.RestorePreparedBatch(
                                batch,
                                () =>
                                {
                                    if (previous is not null)
                                        ApplyBlendshapeBufferState(previous);
                                });
                    }
                    finally
                    {
                        lifetimeLeaseHeld = false;
                        ExitResourcePublicationLease();
                    }
                },
                () =>
                {
                    if (!lifetimeLeaseHeld)
                        return;
                    try
                    {
                        if (batch is not null)
                            targetBuffers.DisposeReplacedBuffers(batch);
                        previous?.DestroyPrecombinedBuffers();
                    }
                    finally
                    {
                        lifetimeLeaseHeld = false;
                        ExitResourcePublicationLease();
                    }
                });
        }

        private void ResetDrivableBuffers()
        {
            if (_boneByTransform is not null)
            {
                foreach (var pair in _boneByTransform)
                    pair.Key.RenderMatrixChanged -= BoneTransformRenderMatrixChanged;
                _boneByTransform.Clear();
            }
            lock (_dirtyBoneSyncRoot)
            {
                _dirtyBoneIndices?.Clear();
                _dirtyBoneFlags = null;
                _dirtyBoneMatrices = null;
                _bonesInvalidated = false;
                _gpuDrivenBoneRefCounts = null;
                _gpuDrivenBoneCount = 0;
            }
            ClearGpuDrivenSkinPaletteSource();

            _bones = null;

            RemoveMeshDeformBuffer(BoneMatricesBuffer);
            BoneMatricesBuffer?.Destroy();

            RemoveMeshDeformBuffer(BoneInvBindMatricesBuffer);
            BoneInvBindMatricesBuffer?.Destroy();

            RemoveMeshDeformBuffer(SkinPaletteBuffer);
            SkinPaletteBuffer?.Destroy();

            RemoveMeshDeformBuffer(PreviousSkinPaletteBuffer);
            PreviousSkinPaletteBuffer?.Destroy();
            Volatile.Write(ref _boneBufferState, new BoneBufferState());

            BlendshapeBufferState previousBlendshapeState =
                Volatile.Read(ref _blendshapeBufferState);
            RemoveMeshDeformBuffer(previousBlendshapeState.Weights);
            RemoveMeshDeformBuffer(previousBlendshapeState.ActiveWeights);
            Volatile.Write(ref _validPrecombinedGenerationId, 0L);
            ApplyBlendshapeBufferState(new BlendshapeBufferState
            {
                DirtyStart = uint.MaxValue,
                WeightsVersion = previousBlendshapeState.WeightsVersion + 1UL,
                PrecombinedInputVersion = previousBlendshapeState.PrecombinedInputVersion + 1UL,
            });
            previousBlendshapeState.Destroy();

            ResetMeshDeformBuffers();
        }

        private void ResetMeshDeformBuffers()
        {
            RemoveMeshDeformBuffer(DeformerPositionsBuffer);
            DeformerPositionsBuffer?.Destroy();

            RemoveMeshDeformBuffer(DeformerRestPositionsBuffer);
            DeformerRestPositionsBuffer?.Destroy();

            RemoveMeshDeformBuffer(DeformerNormalsBuffer);
            DeformerNormalsBuffer?.Destroy();

            RemoveMeshDeformBuffer(DeformerTangentsBuffer);
            DeformerTangentsBuffer?.Destroy();

            RemoveMeshDeformBuffer(MeshDeformIndicesBuffer);
            MeshDeformIndicesBuffer?.Destroy();

            RemoveMeshDeformBuffer(MeshDeformWeightsBuffer);
            MeshDeformWeightsBuffer?.Destroy();

            RemoveMeshDeformBuffer(MeshDeformVertexIndicesBuffer);
            MeshDeformVertexIndicesBuffer?.Destroy();

            RemoveMeshDeformBuffer(MeshDeformVertexWeightsBuffer);
            MeshDeformVertexWeightsBuffer?.Destroy();

            RemoveMeshDeformBuffer(MeshDeformVertexOffsetBuffer);
            MeshDeformVertexOffsetBuffer?.Destroy();

            RemoveMeshDeformBuffer(MeshDeformVertexCountBuffer);
            MeshDeformVertexCountBuffer?.Destroy();
            Volatile.Write(ref _meshDeformBufferState, new MeshDeformBufferState());

            _meshDeformLastTargetVertexCount = 0;
            _meshDeformLastDeformerVertexCount = 0;
            _meshDeformLastInvalidInfluenceCount = 0;
            _meshDeformLastTruncatedVertexCount = 0;
        }

        [MemoryPackIgnore]
        public BufferCollection Buffers { get; private set; } = [];

        /// <summary>
        /// All bone matrices for the mesh.
        /// Stream-write buffer.
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? BoneMatricesBuffer => Volatile.Read(ref _boneBufferState).Matrices;

        /// <summary>
        /// All bone inverse bind matrices for the mesh.
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? BoneInvBindMatricesBuffer => Volatile.Read(ref _boneBufferState).InverseBindMatrices;

        /// <summary>
        /// Precomposed final skin palette stored as three vec4 rows per bone.
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? SkinPaletteBuffer => Volatile.Read(ref _boneBufferState).Palette;

        [MemoryPackIgnore]
        public XRDataBuffer? PreviousSkinPaletteBuffer => Volatile.Read(ref _boneBufferState).PreviousPalette;

        private BoneBufferState _boneBufferState = new();

        /// <summary>Captures one coherent skinning-resource generation for backend binding.</summary>
        public readonly record struct BoneResourceSnapshot(
            XRDataBuffer? BoneMatrices,
            XRDataBuffer? InverseBindMatrices,
            XRDataBuffer? SkinPalette,
            XRDataBuffer? PreviousSkinPalette);

        public BoneResourceSnapshot CaptureBoneResources()
        {
            BoneBufferState state = Volatile.Read(ref _boneBufferState);
            return new(state.Matrices, state.InverseBindMatrices, state.Palette, state.PreviousPalette);
        }

        private sealed class BoneBufferState
        {
            internal static readonly string[] CollectionKeys =
            [
                $"{ECommonBufferType.BoneMatrices}Buffer",
                $"{ECommonBufferType.BoneInvBindMatrices}Buffer",
                $"{ECommonBufferType.SkinPalette}Buffer",
            ];

            internal XRDataBuffer? Matrices;
            internal XRDataBuffer? InverseBindMatrices;
            internal XRDataBuffer? Palette;
            internal XRDataBuffer? PreviousPalette;
            internal RenderBone[]? Bones;
            internal Dictionary<TransformBase, RenderBone>? BoneByTransform;
            internal List<uint>? DirtyIndices;
            internal bool[]? DirtyFlags;
            internal Matrix4x4[]? DirtyMatrices;
            internal bool BonesInvalidated;
            internal int[]? GpuDrivenRefCounts;
            internal int GpuDrivenCount;
            internal int PaletteSignature;
            internal bool PaletteOrderChecked;
            internal bool PaletteStaleReported;

            internal IEnumerable<KeyValuePair<string, XRDataBuffer>> EnumerateCollectionBuffers()
            {
                if (Matrices is not null)
                    yield return new(Matrices.AttributeName, Matrices);
                if (InverseBindMatrices is not null)
                    yield return new(InverseBindMatrices.AttributeName, InverseBindMatrices);
                if (Palette is not null)
                    yield return new(Palette.AttributeName, Palette);
            }

            internal void Destroy()
            {
                Matrices?.Destroy();
                InverseBindMatrices?.Destroy();
                Palette?.Destroy();
            }
        }

        private BoneBufferState CaptureBoneBufferState()
        {
            BoneBufferState buffers = Volatile.Read(ref _boneBufferState);
            lock (_dirtyBoneSyncRoot)
            {
                return new BoneBufferState
                {
                    Matrices = buffers.Matrices,
                    InverseBindMatrices = buffers.InverseBindMatrices,
                    Palette = buffers.Palette,
                    PreviousPalette = buffers.PreviousPalette,
                    Bones = _bones,
                    BoneByTransform = _boneByTransform,
                    DirtyIndices = _dirtyBoneIndices,
                    DirtyFlags = _dirtyBoneFlags,
                    DirtyMatrices = _dirtyBoneMatrices,
                    BonesInvalidated = _bonesInvalidated,
                    GpuDrivenRefCounts = _gpuDrivenBoneRefCounts,
                    GpuDrivenCount = _gpuDrivenBoneCount,
                    PaletteSignature = _paletteBoneOrderSignature,
                    PaletteOrderChecked = _bonePaletteOrderChecked,
                    PaletteStaleReported = _bonePaletteStaleReported,
                };
            }
        }

        private void ApplyBoneBufferState(BoneBufferState state)
        {
            Volatile.Write(ref _boneBufferState, state);
            _bones = state.Bones;
            _boneByTransform = state.BoneByTransform;
            lock (_dirtyBoneSyncRoot)
            {
                _dirtyBoneIndices = state.DirtyIndices;
                _dirtyBoneFlags = state.DirtyFlags;
                _dirtyBoneMatrices = state.DirtyMatrices;
                _bonesInvalidated = state.BonesInvalidated;
                _gpuDrivenBoneRefCounts = state.GpuDrivenRefCounts;
                _gpuDrivenBoneCount = state.GpuDrivenCount;
            }
            _paletteBoneOrderSignature = state.PaletteSignature;
            _bonePaletteOrderChecked = state.PaletteOrderChecked;
            _bonePaletteStaleReported = state.PaletteStaleReported;
        }

        private void AttachBoneSubscriptions(BoneBufferState state)
        {
            if (state.BoneByTransform is null)
                return;
            foreach (TransformBase transform in state.BoneByTransform.Keys)
            {
                transform.RenderMatrixChanged -= BoneTransformRenderMatrixChanged;
                transform.RenderMatrixChanged += BoneTransformRenderMatrixChanged;
            }
        }

        private void DetachBoneSubscriptions(BoneBufferState state)
        {
            if (state.BoneByTransform is null)
                return;
            foreach (TransformBase transform in state.BoneByTransform.Keys)
                transform.RenderMatrixChanged -= BoneTransformRenderMatrixChanged;
        }

        private void DetachBoneBufferState()
        {
            Volatile.Write(ref _boneBufferState, new BoneBufferState());
            _bones = null;
            _boneByTransform = null;
            lock (_dirtyBoneSyncRoot)
            {
                _dirtyBoneIndices = null;
                _dirtyBoneFlags = null;
                _dirtyBoneMatrices = null;
                _gpuDrivenBoneRefCounts = null;
                _gpuDrivenBoneCount = 0;
            }
            Buffers = [];
        }

        [MemoryPackIgnore]
        public XRDataBuffer? ActiveSkinPaletteBuffer => HasExternalSkinPaletteSource ? _externalSkinPaletteBuffer : SkinPaletteBuffer;

        [MemoryPackIgnore]
        public XRDataBuffer? ActivePreviousSkinPaletteBuffer => HasExternalSkinPaletteSource ? _externalPreviousSkinPaletteBuffer : PreviousSkinPaletteBuffer;

        private SkinningLodProfile? _skinningLodProfile;
        private int _activeSkinningLodTier;
        private int _skinningInfluenceCapOverride;

        [MemoryPackIgnore]
        public SkinningLodProfile? SkinningLodProfile
        {
            get => _skinningLodProfile;
            set
            {
                SetField(ref _skinningLodProfile, value);
                MarkSkinnedOutputDirty();
            }
        }

        [MemoryPackIgnore]
        public int ActiveSkinningLodTier
        {
            get => _activeSkinningLodTier;
            set
            {
                int normalized = Math.Max(0, value);
                SetField(ref _activeSkinningLodTier, normalized);
                MarkSkinnedOutputDirty();
            }
        }

        [MemoryPackIgnore]
        public int SkinningInfluenceCapOverride
        {
            get => _skinningInfluenceCapOverride;
            set
            {
                int normalized = Math.Clamp(value, 0, 4 + Math.Max(0, Mesh?.MaxSpillInfluenceCount ?? 0));
                SetField(ref _skinningInfluenceCapOverride, normalized);
                MarkSkinnedOutputDirty();
            }
        }

        [MemoryPackIgnore]
        internal int ActiveSkinningInfluenceCap
        {
            get
            {
                if (_skinningInfluenceCapOverride > 0)
                    return _skinningInfluenceCapOverride;
                if (_skinningLodProfile is not null && _skinningLodProfile.TryGetTier(_activeSkinningLodTier, out SkinningLodTier tier))
                    return Math.Max(0, tier.InfluenceCap);
                return 0;
            }
        }

        [MemoryPackIgnore]
        internal BoneRemap? ActiveSkinningBoneRemap
            => _skinningLodProfile is not null && _skinningLodProfile.TryGetTier(_activeSkinningLodTier, out SkinningLodTier tier)
                ? tier.BoneRemap
                : null;

        [MemoryPackIgnore]
        public uint ActiveSkinPaletteBase => HasExternalSkinPaletteSource ? _externalSkinPaletteBase : 0u;

        [MemoryPackIgnore]
        public uint ActiveSkinPaletteCount => HasExternalSkinPaletteSource ? _externalSkinPaletteCount : (uint)(Mesh?.UtilizedBones?.Length ?? 0) + 1u;

        [MemoryPackIgnore]
        public bool HasGpuDrivenBoneSource => _gpuDrivenBoneCount > 0;

        [MemoryPackIgnore]
        public bool HasExternalSkinPaletteSource
            => RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader
                && _externalSkinPaletteBuffer is not null
                && _externalSkinPaletteCount > 0u;

        /// <summary>
        /// All blendshape weights for the mesh.
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? BlendshapeWeights => Volatile.Read(ref _blendshapeBufferState).Weights;

        /// <summary>
        /// Dense active blendshape index/weight pairs for compact shader paths.
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? BlendshapeActiveWeights => Volatile.Read(ref _blendshapeBufferState).ActiveWeights;

        private BlendshapeBufferState _blendshapeBufferState = new();
        private static long _nextBlendshapeGenerationId;
        private long _validPrecombinedGenerationId;
        private ulong _precombinedBlendshapeInputVersion;
        private ulong _precombinedBlendshapeOutputVersion;

        /// <summary>Captures one coherent blendshape-resource generation for backend binding.</summary>
        public readonly record struct BlendshapeResourceSnapshot(
            XRDataBuffer? Weights,
            XRDataBuffer? ActiveWeights,
            XRDataBuffer? PrecombinedPositions,
            XRDataBuffer? PrecombinedNormals,
            XRDataBuffer? PrecombinedTangents,
            bool HasValidPrecombinedOutput,
            XRMesh? PrecombinedMesh,
            int PrecombinedVertexCount,
            bool PrecombinedHasNormals,
            bool PrecombinedHasTangents,
            int ActiveCount,
            ulong WeightsVersion,
            ulong PrecombinedInputVersion,
            ulong PrecombinedOutputVersion,
            long GenerationId)
        {
            public bool IsPrecombinedValidFor(XRMesh mesh)
                => HasValidPrecombinedOutput
                    && ReferenceEquals(PrecombinedMesh, mesh)
                    && PrecombinedVertexCount == mesh.VertexCount
                    && PrecombinedHasNormals == mesh.HasNormals
                    && PrecombinedHasTangents == mesh.HasTangents
                    && PrecombinedOutputVersion == PrecombinedInputVersion;
        }

        public BlendshapeResourceSnapshot CaptureBlendshapeResources()
        {
            BlendshapeBufferState state = Volatile.Read(ref _blendshapeBufferState);
            return new(
                state.Weights,
                state.ActiveWeights,
                state.PrecombinedPositions,
                state.PrecombinedNormals,
                state.PrecombinedTangents,
                state.GenerationId != 0L
                    && Volatile.Read(ref _validPrecombinedGenerationId) == state.GenerationId
                    && _precombinedBlendshapeOutputVersion == _precombinedBlendshapeInputVersion,
                state.PrecombinedMesh,
                state.PrecombinedVertexCount,
                state.PrecombinedHasNormals,
                state.PrecombinedHasTangents,
                _activeBlendshapeCount,
                _blendshapeWeightsVersion,
                _precombinedBlendshapeInputVersion,
                _precombinedBlendshapeOutputVersion,
                state.GenerationId);
        }

        private sealed class BlendshapeBufferState
        {
            internal static readonly string[] CollectionKeys =
            [
                $"{ECommonBufferType.BlendshapeWeights}Buffer",
                $"{ECommonBufferType.BlendshapeActiveWeights}Buffer",
            ];

            internal XRDataBuffer? Weights;
            internal XRDataBuffer? ActiveWeights;
            internal XRDataBuffer? PrecombinedPositions;
            internal XRDataBuffer? PrecombinedNormals;
            internal XRDataBuffer? PrecombinedTangents;
            internal XRMesh? PrecombinedMesh;
            internal int PrecombinedVertexCount;
            internal bool PrecombinedHasNormals;
            internal bool PrecombinedHasTangents;
            internal bool HasValidPrecombinedOutput;
            internal ulong PrecombinedOutputVersion;
            internal int ActiveCount;
            internal uint DirtyStart;
            internal uint DirtyEnd;
            internal ulong WeightsVersion;
            internal ulong PrecombinedInputVersion;
            internal bool Invalidated;
            internal bool ActiveListInvalidated;
            internal long GenerationId;

            internal IEnumerable<KeyValuePair<string, XRDataBuffer>> EnumerateCollectionBuffers()
            {
                if (Weights is not null)
                    yield return new(Weights.AttributeName, Weights);
                if (ActiveWeights is not null)
                    yield return new(ActiveWeights.AttributeName, ActiveWeights);
            }

            internal void Destroy()
            {
                Weights?.Destroy();
                ActiveWeights?.Destroy();
                DestroyPrecombinedBuffers();
            }

            internal void DestroyPrecombinedBuffers()
            {
                PrecombinedPositions?.Destroy();
                PrecombinedNormals?.Destroy();
                PrecombinedTangents?.Destroy();
            }
        }

        private BlendshapeBufferState CaptureBlendshapeBufferState()
        {
            BlendshapeBufferState snapshot = CloneBlendshapeBufferState(
                Volatile.Read(ref _blendshapeBufferState));
            snapshot.ActiveCount = _activeBlendshapeCount;
            snapshot.DirtyStart = _blendshapeDirtyStartIndex;
            snapshot.DirtyEnd = _blendshapeDirtyEndIndex;
            snapshot.WeightsVersion = _blendshapeWeightsVersion;
            snapshot.PrecombinedInputVersion = _precombinedBlendshapeInputVersion;
            snapshot.PrecombinedOutputVersion = _precombinedBlendshapeOutputVersion;
            snapshot.Invalidated = _blendshapesInvalidated;
            snapshot.ActiveListInvalidated = _blendshapeActiveListInvalidated;
            return snapshot;
        }

        private static BlendshapeBufferState CloneBlendshapeBufferState(BlendshapeBufferState source)
            => new()
            {
                Weights = source.Weights, ActiveWeights = source.ActiveWeights,
                PrecombinedPositions = source.PrecombinedPositions, PrecombinedNormals = source.PrecombinedNormals,
                PrecombinedTangents = source.PrecombinedTangents, PrecombinedMesh = source.PrecombinedMesh,
                PrecombinedVertexCount = source.PrecombinedVertexCount, PrecombinedHasNormals = source.PrecombinedHasNormals,
                PrecombinedHasTangents = source.PrecombinedHasTangents, PrecombinedOutputVersion = source.PrecombinedOutputVersion,
                ActiveCount = source.ActiveCount, DirtyStart = source.DirtyStart, DirtyEnd = source.DirtyEnd,
                WeightsVersion = source.WeightsVersion, PrecombinedInputVersion = source.PrecombinedInputVersion,
                Invalidated = source.Invalidated, ActiveListInvalidated = source.ActiveListInvalidated,
                GenerationId = source.GenerationId,
            };

        private void ApplyBlendshapeBufferState(BlendshapeBufferState state)
        {
            PublishBlendshapeBufferState(state);
            _activeBlendshapeCount = state.ActiveCount;
            _blendshapeDirtyStartIndex = state.DirtyStart;
            _blendshapeDirtyEndIndex = state.DirtyEnd;
            _blendshapeWeightsVersion = state.WeightsVersion;
            _precombinedBlendshapeInputVersion = state.PrecombinedInputVersion;
            _precombinedBlendshapeOutputVersion = state.PrecombinedOutputVersion;
            _blendshapesInvalidated = state.Invalidated;
            _blendshapeActiveListInvalidated = state.ActiveListInvalidated;
        }

        private void PublishBlendshapeBufferState(BlendshapeBufferState state)
        {
            if (state.GenerationId == 0L)
                state.GenerationId = Interlocked.Increment(ref _nextBlendshapeGenerationId);
            Volatile.Write(ref _blendshapeBufferState, state);
        }

        private void DetachBlendshapeBufferState()
        {
            Volatile.Write(ref _blendshapeBufferState, new BlendshapeBufferState());
            Volatile.Write(ref _validPrecombinedGenerationId, 0L);
            Buffers = [];
        }

        [MemoryPackIgnore]
        public int ActiveBlendshapeCount => _activeBlendshapeCount;

        [MemoryPackIgnore]
        public bool HasActiveBlendshapes => _activeBlendshapeCount > 0;

        [MemoryPackIgnore]
        public ulong BlendshapeWeightsVersion => _blendshapeWeightsVersion;

        [MemoryPackIgnore]
        public float BlendshapeActiveWeightThreshold
        {
            get => _blendshapeActiveWeightThreshold;
            set
            {
                float normalized = Math.Max(0.0f, value);
                if (!SetField(ref _blendshapeActiveWeightThreshold, normalized))
                    return;

                RebuildActiveBlendshapeList();
                MarkSkinnedOutputDirty();
            }
        }

        private BlendshapeLodProfile? _blendshapeLodProfile;
        private int _activeBlendshapeLodTier;
        private float _lastBlendshapeLodDistance;
        private float _lastBlendshapeLodScreenCoverage;
        private BlendshapeLodAvatarRole _lastBlendshapeLodAvatarRole;

        [MemoryPackIgnore]
        public BlendshapeLodProfile? BlendshapeLodProfile
        {
            get => _blendshapeLodProfile;
            set
            {
                SetField(ref _blendshapeLodProfile, value);
                RebuildActiveBlendshapeList();
                MarkSkinnedOutputDirty();
            }
        }

        [MemoryPackIgnore]
        public int ActiveBlendshapeLodTier
        {
            get => _activeBlendshapeLodTier;
            set
            {
                int normalized = Math.Max(0, value);
                if (!SetField(ref _activeBlendshapeLodTier, normalized))
                    return;
                RebuildActiveBlendshapeList();
                MarkSkinnedOutputDirty();
            }
        }

        [MemoryPackIgnore]
        public float LastBlendshapeLodDistance => _lastBlendshapeLodDistance;

        [MemoryPackIgnore]
        public float LastBlendshapeLodScreenCoverage => _lastBlendshapeLodScreenCoverage;

        [MemoryPackIgnore]
        public BlendshapeLodAvatarRole LastBlendshapeLodAvatarRole => _lastBlendshapeLodAvatarRole;

        [MemoryPackIgnore]
        public string BlendshapeLodDiagnosticSummary
            => $"tier={_activeBlendshapeLodTier} role={_lastBlendshapeLodAvatarRole} distance={_lastBlendshapeLodDistance:0.###} screen={_lastBlendshapeLodScreenCoverage:0.###} active={_activeBlendshapeCount}";

        public bool UpdateBlendshapeLodSelection(float distance, float screenCoverage, BlendshapeLodAvatarRole role = BlendshapeLodAvatarRole.Primary)
        {
            SetField(ref _lastBlendshapeLodDistance, Math.Max(0.0f, distance));
            SetField(ref _lastBlendshapeLodScreenCoverage, Math.Clamp(screenCoverage, 0.0f, 1.0f));
            SetField(ref _lastBlendshapeLodAvatarRole, role);

            if (_blendshapeLodProfile is null)
                return false;

            int selectedTier = _blendshapeLodProfile.SelectTier(_lastBlendshapeLodDistance, _lastBlendshapeLodScreenCoverage, role);
            if (selectedTier == _activeBlendshapeLodTier)
                return false;

            ActiveBlendshapeLodTier = selectedTier;
            return true;
        }

        /// <summary>
        /// Indirect draw buffer for the mesh - renders multiple meshes with a single draw call.
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? IndirectDrawBuffer { get; private set; }

        /// <summary>
        /// Output buffer for skinned positions from compute shader pre-pass.
        /// When set, this buffer is used instead of the mesh's position buffer for rendering.
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? SkinnedPositionsBuffer
        {
            get => Volatile.Read(ref _skinnedOutputResourceState).Positions;
            internal set { SkinnedOutputResourceSnapshot state = CaptureSkinnedOutputResources(); InstallSkinnedOutputResources(value, state.Normals, state.Tangents, state.Interleaved); }
        }

        /// <summary>
        /// Output buffer for skinned normals from compute shader pre-pass.
        /// When set, this buffer is used instead of the mesh's normal buffer for rendering.
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? SkinnedNormalsBuffer
        {
            get => Volatile.Read(ref _skinnedOutputResourceState).Normals;
            internal set { SkinnedOutputResourceSnapshot state = CaptureSkinnedOutputResources(); InstallSkinnedOutputResources(state.Positions, value, state.Tangents, state.Interleaved); }
        }

        /// <summary>
        /// Output buffer for skinned tangents from compute shader pre-pass.
        /// When set, this buffer is used instead of the mesh's tangent buffer for rendering.
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? SkinnedTangentsBuffer
        {
            get => Volatile.Read(ref _skinnedOutputResourceState).Tangents;
            internal set { SkinnedOutputResourceSnapshot state = CaptureSkinnedOutputResources(); InstallSkinnedOutputResources(state.Positions, state.Normals, value, state.Interleaved); }
        }

        /// <summary>
        /// Output buffer for skinned interleaved data from compute shader pre-pass.
        /// When set, this buffer is used instead of the mesh's interleaved buffer for rendering.
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? SkinnedInterleavedBuffer
        {
            get => Volatile.Read(ref _skinnedOutputResourceState).Interleaved;
            internal set { SkinnedOutputResourceSnapshot state = CaptureSkinnedOutputResources(); InstallSkinnedOutputResources(state.Positions, state.Normals, state.Tangents, value); }
        }

        private SkinnedOutputResourceState _skinnedOutputResourceState = new();

        private sealed class SkinnedOutputResourceState
        {
            internal XRDataBuffer? Positions;
            internal XRDataBuffer? Normals;
            internal XRDataBuffer? Tangents;
            internal XRDataBuffer? Interleaved;
        }

        /// <summary>Captures one coherent generation of compute-skinning outputs.</summary>
        public readonly record struct SkinnedOutputResourceSnapshot(
            XRDataBuffer? Positions,
            XRDataBuffer? Normals,
            XRDataBuffer? Tangents,
            XRDataBuffer? Interleaved);

        public SkinnedOutputResourceSnapshot CaptureSkinnedOutputResources()
        {
            SkinnedOutputResourceState state = Volatile.Read(ref _skinnedOutputResourceState);
            return new(state.Positions, state.Normals, state.Tangents, state.Interleaved);
        }

        /// <summary>
        /// Atomically publishes a complete compute-skinning output generation. Callers must
        /// create and validate every member before invoking this method.
        /// </summary>
        internal void InstallSkinnedOutputResources(
            XRDataBuffer? positions,
            XRDataBuffer? normals,
            XRDataBuffer? tangents,
            XRDataBuffer? interleaved)
            => Volatile.Write(ref _skinnedOutputResourceState, new SkinnedOutputResourceState
            {
                Positions = positions,
                Normals = normals,
                Tangents = tangents,
                Interleaved = interleaved,
            });

        /// <summary>
        /// Precombined position deltas for active blendshapes. The compute pre-pass writes this and
        /// final skinning/direct vertex paths add it once per vertex when the heuristic selects it.
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? PrecombinedBlendshapePositionsBuffer
            => Volatile.Read(ref _blendshapeBufferState).PrecombinedPositions;

        /// <summary>
        /// Precombined normal deltas for active blendshapes.
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? PrecombinedBlendshapeNormalsBuffer
            => Volatile.Read(ref _blendshapeBufferState).PrecombinedNormals;

        /// <summary>
        /// Precombined tangent deltas for active blendshapes.
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? PrecombinedBlendshapeTangentsBuffer
            => Volatile.Read(ref _blendshapeBufferState).PrecombinedTangents;

        private readonly SkinnedOutputInvalidationState _skinnedOutputInvalidation = new();
        [MemoryPackIgnore]
        internal bool SkinnedOutputDirty => _skinnedOutputInvalidation.IsDirty;

        [MemoryPackIgnore]
        internal ulong SkinnedOutputVersion => _skinnedOutputInvalidation.Version;

        [MemoryPackIgnore]
        internal bool HasValidPrecombinedBlendshapeDeltas
        {
            get
            {
                BlendshapeBufferState state = Volatile.Read(ref _blendshapeBufferState);
                return state.GenerationId != 0L
                    && Volatile.Read(ref _validPrecombinedGenerationId) == state.GenerationId
                    && state.PrecombinedMesh is not null
                    && ReferenceEquals(state.PrecombinedMesh, Mesh)
                    && state.PrecombinedVertexCount == (Mesh?.VertexCount ?? 0)
                    && state.PrecombinedHasNormals == (Mesh?.HasNormals ?? false)
                    && state.PrecombinedHasTangents == (Mesh?.HasTangents ?? false)
                    && _precombinedBlendshapeOutputVersion == _precombinedBlendshapeInputVersion;
            }
        }

        [MemoryPackIgnore]
        internal bool HasPendingComputeSkinningInputChanges
            => _bonesInvalidated || _blendshapesInvalidated || _meshDeformInvalidated;

        internal void MarkSkinnedOutputDirty()
            => _skinnedOutputInvalidation.MarkDirty();

        internal void MarkSkinnedOutputClean(ulong dispatchedVersion)
            => _skinnedOutputInvalidation.MarkClean(dispatchedVersion);

        internal bool EnsurePrecombinedBlendshapeBuffers(XRMesh mesh)
        {
            EnterResourcePublicationLease();
            try
            {
                int vertexCount = mesh.VertexCount;
                if (vertexCount <= 0)
                    return false;

                BlendshapeBufferState current = Volatile.Read(ref _blendshapeBufferState);
                bool buffersExist = current.PrecombinedPositions is not null
                    && (!mesh.HasNormals || current.PrecombinedNormals is not null)
                    && (!mesh.HasTangents || current.PrecombinedTangents is not null);
                if (buffersExist
                    && ReferenceEquals(current.PrecombinedMesh, mesh)
                    && current.PrecombinedVertexCount == vertexCount
                    && current.PrecombinedHasNormals == mesh.HasNormals
                    && current.PrecombinedHasTangents == mesh.HasTangents)
                {
                    return true;
                }

                BlendshapeBufferState next = CloneBlendshapeBufferState(current);
                next.GenerationId = 0L;
                next.PrecombinedMesh = mesh;
                next.PrecombinedVertexCount = vertexCount;
                next.PrecombinedHasNormals = mesh.HasNormals;
                next.PrecombinedHasTangents = mesh.HasTangents;
                next.PrecombinedPositions = CreatePrecombinedBlendshapeBuffer("PrecombinedBlendshapePositionDeltas", vertexCount);
                if (mesh.HasNormals)
                    next.PrecombinedNormals = CreatePrecombinedBlendshapeBuffer("PrecombinedBlendshapeNormalDeltas", vertexCount);
                if (mesh.HasTangents)
                    next.PrecombinedTangents = CreatePrecombinedBlendshapeBuffer("PrecombinedBlendshapeTangentDeltas", vertexCount);
                next.PrecombinedOutputVersion = 0UL;
                Volatile.Write(ref _validPrecombinedGenerationId, 0L);
                _precombinedBlendshapeOutputVersion = 0UL;
                PublishBlendshapeBufferState(next);
                current.DestroyPrecombinedBuffers();
                return true;
            }
            finally
            {
                ExitResourcePublicationLease();
            }
        }

        internal void MarkPrecombinedBlendshapeDeltasValid(XRMesh mesh, long expectedGenerationId)
        {
            BlendshapeBufferState current = Volatile.Read(ref _blendshapeBufferState);
            if (current.GenerationId != expectedGenerationId
                || !ReferenceEquals(current.PrecombinedMesh, mesh))
                return;
            _precombinedBlendshapeOutputVersion = _precombinedBlendshapeInputVersion;
            Volatile.Write(ref _validPrecombinedGenerationId, expectedGenerationId);
        }

        internal void InvalidatePrecombinedBlendshapeDeltas()
        {
            if (Volatile.Read(ref _validPrecombinedGenerationId) == 0L)
                return;
            Volatile.Write(ref _validPrecombinedGenerationId, 0L);
        }

        private static XRDataBuffer CreatePrecombinedBlendshapeBuffer(string name, int vertexCount)
            => new(name, EBufferTarget.ShaderStorageBuffer, (uint)vertexCount, EComponentType.Float, 4, true, false)
            {
                Usage = EBufferUsage.DynamicDraw,
                DisposeOnPush = false,
            };

        private void DestroyPrecombinedBlendshapeBuffers()
        {
            BlendshapeBufferState previous = Volatile.Read(ref _blendshapeBufferState);
            BlendshapeBufferState empty = CloneBlendshapeBufferState(previous);
            empty.GenerationId = 0L;
            empty.PrecombinedPositions = null;
            empty.PrecombinedNormals = null;
            empty.PrecombinedTangents = null;
            empty.PrecombinedMesh = null;
            empty.PrecombinedVertexCount = 0;
            empty.PrecombinedHasNormals = false;
            empty.PrecombinedHasTangents = false;
            empty.PrecombinedOutputVersion = 0UL;
            Volatile.Write(ref _validPrecombinedGenerationId, 0L);
            _precombinedBlendshapeOutputVersion = 0UL;
            PublishBlendshapeBufferState(empty);
            previous.DestroyPrecombinedBuffers();
        }

        #region Mesh Deform Buffers

        /// <summary>
        /// Current positions of deformer mesh vertices (SSBO).
        /// Updated each frame from DeformMeshRenderer.
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? DeformerPositionsBuffer => Volatile.Read(ref _meshDeformBufferState).Positions;

        /// <summary>
        /// Rest positions of deformer mesh vertices (SSBO).
        /// Static buffer containing original bind pose positions.
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? DeformerRestPositionsBuffer => Volatile.Read(ref _meshDeformBufferState).RestPositions;

        /// <summary>
        /// Current normals of deformer mesh vertices (SSBO).
        /// Updated each frame from DeformMeshRenderer.
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? DeformerNormalsBuffer => Volatile.Read(ref _meshDeformBufferState).Normals;

        /// <summary>
        /// Current tangents of deformer mesh vertices (SSBO).
        /// Updated each frame from DeformMeshRenderer.
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? DeformerTangentsBuffer => Volatile.Read(ref _meshDeformBufferState).Tangents;

        /// <summary>
        /// SSBO containing all deformer vertex indices for all vertices (SSBO mode only).
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? MeshDeformIndicesBuffer => Volatile.Read(ref _meshDeformBufferState).Indices;

        /// <summary>
        /// SSBO containing all deformer weights for all vertices (SSBO mode only).
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? MeshDeformWeightsBuffer => Volatile.Read(ref _meshDeformBufferState).Weights;

        /// <summary>
        /// Per-vertex vec4 containing up to 4 deformer vertex indices (vec4 optimized mode).
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? MeshDeformVertexIndicesBuffer => Volatile.Read(ref _meshDeformBufferState).VertexIndices;

        /// <summary>
        /// Per-vertex vec4 containing up to 4 deformer weights (vec4 optimized mode).
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? MeshDeformVertexWeightsBuffer => Volatile.Read(ref _meshDeformBufferState).VertexWeights;

        /// <summary>
        /// Per-vertex offset into MeshDeformIndicesBuffer/MeshDeformWeightsBuffer (SSBO mode).
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? MeshDeformVertexOffsetBuffer => Volatile.Read(ref _meshDeformBufferState).VertexOffsets;

        /// <summary>
        /// Per-vertex count of influences (SSBO mode).
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? MeshDeformVertexCountBuffer => Volatile.Read(ref _meshDeformBufferState).VertexCounts;

        private bool _meshDeformInvalidated = false;

        private MeshDeformBufferState _meshDeformBufferState = new();

        /// <summary>Captures one coherent mesh-deform resource generation for backend binding.</summary>
        public readonly record struct MeshDeformResourceSnapshot(
            XRDataBuffer? Positions,
            XRDataBuffer? RestPositions,
            XRDataBuffer? Normals,
            XRDataBuffer? Tangents,
            XRDataBuffer? Indices,
            XRDataBuffer? Weights,
            XRDataBuffer? VertexIndices,
            XRDataBuffer? VertexWeights,
            XRDataBuffer? VertexOffsets,
            XRDataBuffer? VertexCounts);

        public MeshDeformResourceSnapshot CaptureMeshDeformResources()
        {
            MeshDeformBufferState state = Volatile.Read(ref _meshDeformBufferState);
            return new(state.Positions, state.RestPositions, state.Normals, state.Tangents, state.Indices, state.Weights, state.VertexIndices, state.VertexWeights, state.VertexOffsets, state.VertexCounts);
        }

        private sealed class MeshDeformBufferState
        {
            internal static readonly string[] CollectionKeys =
            [
                $"{MeshDeformVertexShaderGenerator.DeformerPositionsBufferName}Buffer",
                $"{MeshDeformVertexShaderGenerator.DeformerRestPositionsBufferName}Buffer",
                $"{MeshDeformVertexShaderGenerator.DeformerNormalsBufferName}Buffer",
                $"{MeshDeformVertexShaderGenerator.DeformerTangentsBufferName}Buffer",
                $"{MeshDeformVertexShaderGenerator.MeshDeformIndicesBufferName}Buffer",
                $"{MeshDeformVertexShaderGenerator.MeshDeformWeightsBufferName}Buffer",
                MeshDeformVertexShaderGenerator.MeshDeformVertexIndicesAttrName,
                MeshDeformVertexShaderGenerator.MeshDeformVertexWeightsAttrName,
                MeshDeformVertexShaderGenerator.MeshDeformVertexOffsetAttrName,
                MeshDeformVertexShaderGenerator.MeshDeformVertexCountAttrName,
            ];

            internal XRDataBuffer? Positions;
            internal XRDataBuffer? RestPositions;
            internal XRDataBuffer? Normals;
            internal XRDataBuffer? Tangents;
            internal XRDataBuffer? Indices;
            internal XRDataBuffer? Weights;
            internal XRDataBuffer? VertexIndices;
            internal XRDataBuffer? VertexWeights;
            internal XRDataBuffer? VertexOffsets;
            internal XRDataBuffer? VertexCounts;
            internal bool Invalidated;
            internal uint TargetVertexCount;
            internal uint DeformerVertexCount;
            internal int InvalidInfluenceCount;
            internal int TruncatedVertexCount;

            internal IEnumerable<KeyValuePair<string, XRDataBuffer>> EnumerateCollectionBuffers()
            {
                XRDataBuffer?[] buffers = [Positions, RestPositions, Normals, Tangents, Indices, Weights, VertexIndices, VertexWeights, VertexOffsets, VertexCounts];
                foreach (XRDataBuffer? buffer in buffers)
                    if (buffer is not null)
                        yield return new(buffer.AttributeName, buffer);
            }

            internal void Destroy()
            {
                foreach (XRDataBuffer? buffer in new XRDataBuffer?[] { Positions, RestPositions, Normals, Tangents, Indices, Weights, VertexIndices, VertexWeights, VertexOffsets, VertexCounts })
                    buffer?.Destroy();
            }
        }

        private MeshDeformBufferState CaptureMeshDeformBufferState()
            => new()
            {
                Positions = DeformerPositionsBuffer,
                RestPositions = DeformerRestPositionsBuffer,
                Normals = DeformerNormalsBuffer,
                Tangents = DeformerTangentsBuffer,
                Indices = MeshDeformIndicesBuffer,
                Weights = MeshDeformWeightsBuffer,
                VertexIndices = MeshDeformVertexIndicesBuffer,
                VertexWeights = MeshDeformVertexWeightsBuffer,
                VertexOffsets = MeshDeformVertexOffsetBuffer,
                VertexCounts = MeshDeformVertexCountBuffer,
                Invalidated = _meshDeformInvalidated,
                TargetVertexCount = _meshDeformLastTargetVertexCount,
                DeformerVertexCount = _meshDeformLastDeformerVertexCount,
                InvalidInfluenceCount = _meshDeformLastInvalidInfluenceCount,
                TruncatedVertexCount = _meshDeformLastTruncatedVertexCount,
            };

        private void ApplyMeshDeformBufferState(MeshDeformBufferState state)
        {
            Volatile.Write(ref _meshDeformBufferState, state);
            _meshDeformInvalidated = state.Invalidated;
            _meshDeformLastTargetVertexCount = state.TargetVertexCount;
            _meshDeformLastDeformerVertexCount = state.DeformerVertexCount;
            _meshDeformLastInvalidInfluenceCount = state.InvalidInfluenceCount;
            _meshDeformLastTruncatedVertexCount = state.TruncatedVertexCount;
        }

        private void DetachMeshDeformBufferState()
        {
            Volatile.Write(ref _meshDeformBufferState, new MeshDeformBufferState());
            Buffers = [];
        }

        #endregion

        private void PopulateBoneMatrixBuffers()
            => PopulateBoneMatrixBuffers(_mesh);

        private void PopulateBoneMatrixBuffers(XRMesh? sourceMesh)
        {
            XRMesh.BufferCollection targetBuffers = Buffers;
            XRMesh.BufferCollection.PreparedBufferTicket preparationTicket =
                targetBuffers.CapturePreparationTicket(BoneBufferState.CollectionKeys);
            int expectedSettingsRevision = CurrentSettingsRevision;
            XRMeshRenderer staging = new(detachedStagingRenderer: true)
            {
                _mesh = sourceMesh,
            };
            BoneBufferState prepared;
            try
            {
                using RenderObjectPublicationScope publication = GenericRenderObject.BeginDeferredPublication();
                staging.PopulateBoneMatrixBuffersCore();
                prepared = staging.CaptureBoneBufferState();
                staging.DetachBoneSubscriptions(prepared);
                BoneBufferState? previous = null;
                XRMesh.BufferCollection.PreparedBufferBatch? batch = null;
                bool lifetimeLeaseHeld = false;
                publication.Complete(
                    () =>
                    {
                        EnterResourcePublicationLease();
                        lifetimeLeaseHeld = true;
                        if (!ReferenceEquals(_mesh, sourceMesh) ||
                            CurrentSettingsRevision != expectedSettingsRevision ||
                            !RuntimeEngine.Rendering.Settings.AllowSkinning)
                        {
                            throw new InvalidOperationException("Skinning configuration changed while renderer buffers were being prepared.");
                        }

                        batch = targetBuffers.SwapPreparedBatch(
                            prepared.EnumerateCollectionBuffers(),
                            BoneBufferState.CollectionKeys,
                            expectedTicket: preparationTicket,
                            installState: () =>
                            {
                                previous = CaptureBoneBufferState();
                                DetachBoneSubscriptions(previous);
                                ApplyBoneBufferState(prepared);
                                AttachBoneSubscriptions(prepared);
                            },
                            restoreStateOnFailure: () =>
                            {
                                DetachBoneSubscriptions(prepared);
                                if (previous is not null)
                                {
                                    ApplyBoneBufferState(previous);
                                    AttachBoneSubscriptions(previous);
                                }
                            });
                        ValidateResourcePublicationLease();
                    },
                    () =>
                    {
                        if (!lifetimeLeaseHeld)
                            return;
                        try
                        {
                            if (batch is not null)
                                targetBuffers.RestorePreparedBatch(
                                    batch,
                                    () =>
                                    {
                                        DetachBoneSubscriptions(prepared);
                                        if (previous is not null)
                                        {
                                            ApplyBoneBufferState(previous);
                                            AttachBoneSubscriptions(previous);
                                        }
                                    });
                        }
                        finally
                        {
                            lifetimeLeaseHeld = false;
                            ExitResourcePublicationLease();
                        }
                    },
                    () =>
                    {
                        if (!lifetimeLeaseHeld)
                            return;
                        try
                        {
                            if (batch is not null)
                                targetBuffers.DisposeReplacedBuffers(batch);
                        }
                        finally
                        {
                            lifetimeLeaseHeld = false;
                            ExitResourcePublicationLease();
                        }
                    });
                staging.DetachBoneBufferState();
            }
            finally
            {
                staging.AbortRendererConstruction();
            }
        }

        private void PopulateBoneMatrixBuffersCore()
        {
            //using var timer = RuntimeEngine.Profiler.Start();

            // Finalize the mesh's skinning bone ordering BEFORE sizing/seeding the palette.
            // RebuildSkinningBuffersFromVertices (invoked lazily for non-canonical meshes) can
            // reorder/extend UtilizedBones while packing the per-vertex core bone indices. If we
            // build the palette from a pre-rebuild ordering and that rebuild happens afterwards
            // (e.g. during the compute pre-pass), the indices reference the wrong palette slots and
            // the mesh explodes until a skinning toggle forces a palette rebuild. Finalizing here
            // guarantees the palette and the core indices share the same bone order from frame one.
            XRMesh? mesh = Mesh;
            (TransformBase tfm, Matrix4x4 invBindWorldMtx)[] utilizedBones =
                mesh?.GetSkinningBoneOrderForPreparation() ?? [];
            uint boneCount = (uint)utilizedBones.Length;

            Volatile.Read(ref _boneBufferState).Matrices = new($"{ECommonBufferType.BoneMatrices}Buffer", EBufferTarget.ShaderStorageBuffer, boneCount + 1, EComponentType.Float, 16, false, false)
            {
                //RangeFlags = EBufferMapRangeFlags.Write | EBufferMapRangeFlags.Persistent | EBufferMapRangeFlags.Coherent;
                //StorageFlags = EBufferMapStorageFlags.Write | EBufferMapStorageFlags.Persistent | EBufferMapStorageFlags.Coherent | EBufferMapStorageFlags.ClientStorage;
                Usage = EBufferUsage.StreamDraw,
                DisposeOnPush = false
            };
            Volatile.Read(ref _boneBufferState).InverseBindMatrices = new($"{ECommonBufferType.BoneInvBindMatrices}Buffer", EBufferTarget.ShaderStorageBuffer, boneCount + 1, EComponentType.Float, 16, false, false)
            {
                Usage = EBufferUsage.StaticCopy
            };
            Volatile.Read(ref _boneBufferState).Palette = new($"{ECommonBufferType.SkinPalette}Buffer", EBufferTarget.ShaderStorageBuffer, boneCount + 1, EComponentType.Float, 12, false, false)
            {
                Usage = EBufferUsage.StreamDraw,
                DisposeOnPush = false
            };

            BoneMatricesBuffer!.Set(0, Matrix4x4.Identity);
            BoneInvBindMatricesBuffer!.Set(0, Matrix4x4.Identity);
            SkinPaletteBuffer!.Set(0, SkinPaletteMatrix.Identity);

            _bones = new RenderBone[boneCount];
            _boneByTransform = new Dictionary<TransformBase, RenderBone>((int)boneCount);
            lock (_dirtyBoneSyncRoot)
            {
                _dirtyBoneIndices = new List<uint>((int)boneCount);
                _dirtyBoneFlags = new bool[boneCount + 1];
                _dirtyBoneMatrices = new Matrix4x4[boneCount + 1];
                _bonesInvalidated = false;
                _gpuDrivenBoneRefCounts = new int[boneCount + 1];
                _gpuDrivenBoneCount = 0;
            }

            // Vertices live in root-local space (from geometryTransform), but InverseBindMatrix
            // maps from world space to bone-local space. Pre-multiply by the root's BindMatrix
            // so InvBind correctly maps: root-local → world → bone-local.
            Matrix4x4 rootBindMtx = mesh!.BindRootMatrix ?? Matrix4x4.Identity;

            for (int i = 0; i < _bones.Length; i++)
            {
                var (tfm, invBindWorldMtx) = utilizedBones[i];
                uint boneIndex = (uint)i + 1u;

                var rb = new RenderBone(tfm, invBindWorldMtx, boneIndex);
                _boneByTransform[tfm] = rb;
                tfm.RenderMatrixChanged += BoneTransformRenderMatrixChanged;
                _bones[i] = rb;

                Matrix4x4 currentMatrix = GetCurrentBoneMatrix(tfm);
                BoneMatricesBuffer.Set(boneIndex, currentMatrix);
                Matrix4x4 adjustedInvBind = rootBindMtx * invBindWorldMtx;
                BoneInvBindMatricesBuffer.Set(boneIndex, adjustedInvBind);
                SkinPaletteBuffer.Set(boneIndex, SkinPaletteMatrix.FromRowVectorMatrix(adjustedInvBind * currentMatrix));
                MarkBoneMatrixDirty(boneIndex, currentMatrix);
            }

            Buffers.Add(BoneMatricesBuffer.AttributeName, BoneMatricesBuffer);
            Buffers.Add(BoneInvBindMatricesBuffer.AttributeName, BoneInvBindMatricesBuffer);
            Buffers.Add(SkinPaletteBuffer.AttributeName, SkinPaletteBuffer);

            // Record the exact bone ordering this palette was built against. The per-vertex core
            // bone indices index into this same ordering. If the mesh's UtilizedBones get reordered
            // afterwards (e.g. a deferred RebuildSkinningBuffersFromVertices from meshlet/island
            // processing) without rebuilding this palette, the indices point at the wrong slots and
            // the mesh explodes. We snapshot the signature here to detect that drift at dispatch.
            _paletteBoneOrderSignature = ComputeBoneOrderSignature(Mesh);
            _bonePaletteOrderChecked = false;
            _bonePaletteStaleReported = false;
            MarkSkinnedOutputDirty();
        }

        private void ClearBoneMatrixBuffersTransactionally(XRMesh? expectedMesh)
        {
            XRMesh.BufferCollection targetBuffers = Buffers;
            XRMesh.BufferCollection.PreparedBufferTicket preparationTicket =
                targetBuffers.CapturePreparationTicket(BoneBufferState.CollectionKeys);
            BoneBufferState? previous = null;
            BoneBufferState empty = new();
            using RenderObjectPublicationScope publication = GenericRenderObject.BeginDeferredPublication();
            XRMesh.BufferCollection.PreparedBufferBatch? batch = null;
            bool lifetimeLeaseHeld = false;
            publication.Complete(
                () =>
                {
                    EnterResourcePublicationLease();
                    lifetimeLeaseHeld = true;
                    if (!ReferenceEquals(_mesh, expectedMesh))
                        throw new InvalidOperationException("The renderer mesh changed while skinning buffers were being cleared.");
                    batch = targetBuffers.SwapPreparedBatch(
                        [],
                        BoneBufferState.CollectionKeys,
                        expectedTicket: preparationTicket,
                        installState: () =>
                        {
                            previous = CaptureBoneBufferState();
                            DetachBoneSubscriptions(previous);
                            ApplyBoneBufferState(empty);
                        },
                        restoreStateOnFailure: () =>
                        {
                            if (previous is not null)
                            {
                                ApplyBoneBufferState(previous);
                                AttachBoneSubscriptions(previous);
                            }
                        });
                    ValidateResourcePublicationLease();
                },
                () =>
                {
                    if (!lifetimeLeaseHeld)
                        return;
                    try
                    {
                        if (batch is not null)
                            targetBuffers.RestorePreparedBatch(
                                batch,
                                () =>
                                {
                                    if (previous is not null)
                                    {
                                        ApplyBoneBufferState(previous);
                                        AttachBoneSubscriptions(previous);
                                    }
                                });
                    }
                    finally
                    {
                        lifetimeLeaseHeld = false;
                        ExitResourcePublicationLease();
                    }
                },
                () =>
                {
                    if (!lifetimeLeaseHeld)
                        return;
                    try
                    {
                        if (batch is not null)
                            targetBuffers.DisposeReplacedBuffers(batch);
                        previous?.PreviousPalette?.Destroy();
                    }
                    finally
                    {
                        lifetimeLeaseHeld = false;
                        ExitResourcePublicationLease();
                    }
                });
        }

        private int _paletteBoneOrderSignature;
        private bool _bonePaletteOrderChecked;
        private bool _bonePaletteStaleReported;

        private static int ComputeBoneOrderSignature(XRMesh? mesh)
        {
            var bones = mesh?.UtilizedBones;
            if (bones is null || bones.Length == 0)
                return 0;
            var hash = new HashCode();
            hash.Add(bones.Length);
            for (int i = 0; i < bones.Length; i++)
                hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(bones[i].tfm));
            return hash.ToHashCode();
        }

        /// <summary>
        /// One-shot diagnostic: verifies the renderer's bone palette was built against the same
        /// UtilizedBones ordering the mesh currently exposes (and that the per-vertex core indices
        /// were packed against). A mismatch means the palette is stale relative to the indices and
        /// the mesh will render corrupted/exploded until a full palette rebuild.
        /// </summary>
        internal void VerifyBonePaletteOrderMatchesMesh()
        {
            if (Mesh is null || _bones is null)
                return;

            int current = ComputeBoneOrderSignature(Mesh);
            int meshBones = Mesh.UtilizedBones?.Length ?? 0;
            bool stale = current != _paletteBoneOrderSignature || meshBones != _bones.Length;

            if (!_bonePaletteOrderChecked)
            {
                _bonePaletteOrderChecked = true;
                if (!stale)
                {
                    Debug.LogWarning(
                        $"[SkinPaletteOk] Palette bone order matches mesh. Mesh='{Mesh.Name ?? "<null>"}' bones={_bones.Length} sig={current:X8}.");
                }
            }

            // Re-check EVERY dispatch (not one-shot): a deferred RebuildSkinningBuffersFromVertices
            // from meshlet/island/LOD processing can reorder UtilizedBones AFTER the first dispatch,
            // desyncing this palette from the per-vertex core indices. Report the first transition
            // into the stale state so we catch a late reorder in the act.
            if (stale && !_bonePaletteStaleReported)
            {
                _bonePaletteStaleReported = true;
                Debug.LogWarning(
                    $"[SkinPaletteStale] Palette bone order DIFFERS from current mesh order at dispatch. " +
                    $"Mesh='{Mesh.Name ?? "<null>"}' paletteBones={_bones.Length} meshBones={meshBones} " +
                    $"paletteSig={_paletteBoneOrderSignature:X8} meshSig={current:X8}. Palette is stale -> skinning corruption.");
            }
        }

        private static Matrix4x4 GetCurrentBoneMatrix(TransformBase transform)
        {
            // Imported skeletons can be skinned before the first render snapshot is published.
            Matrix4x4 renderMatrix = transform.RenderMatrix;
            if (!renderMatrix.Equals(Matrix4x4.Identity))
                return renderMatrix;

            Matrix4x4 worldMatrix = transform.WorldMatrix;
            return worldMatrix.Equals(Matrix4x4.Identity) ? renderMatrix : worldMatrix;
        }

        private bool _bonesInvalidated = false;
        private bool _blendshapesInvalidated = false;
        private bool _blendshapeActiveListInvalidated;
        private uint _blendshapeDirtyStartIndex = uint.MaxValue;
        private uint _blendshapeDirtyEndIndex;
        private int _activeBlendshapeCount;
        private ulong _blendshapeWeightsVersion;
        private float _blendshapeActiveWeightThreshold;
        private int[]? _gpuDrivenBoneRefCounts;
        private int _gpuDrivenBoneCount;
        private object? _externalSkinPaletteSourceOwner;
        private XRDataBuffer? _externalSkinPaletteBuffer;
        private XRDataBuffer? _externalPreviousSkinPaletteBuffer;
        private uint _externalSkinPaletteBase;
        private uint _externalSkinPaletteCount;

        private void BoneTransformRenderMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
        {
            if (BoneMatricesBuffer is null)
                return;

            if (_boneByTransform is null || !_boneByTransform.TryGetValue(transform, out var bone))
                return;

            if (_dirtyBoneFlags is null || _dirtyBoneIndices is null || _dirtyBoneMatrices is null)
                return;

            if (!MarkBoneMatrixDirty(bone.Index, renderMatrix))
                return;

            MarkSkinnedOutputDirty();
        }

        /// <summary>
        /// Diagnostic: logs how many utilized bones still have an identity (unpublished/stale)
        /// render and world matrix. Used to confirm whether the skinning explosion is caused by
        /// bones being seeded before their transforms publish a valid pose. One-shot per renderer.
        /// </summary>
        internal void LogBoneSeedStalenessDiagnostics(string context)
        {
            if (_boneSeedDiagnosticsLogged || _bones is null)
                return;
            _boneSeedDiagnosticsLogged = true;

            int identityRender = 0;
            int identityWorld = 0;
            int divergent = 0;
            int total = _bones.Length;
            string? firstStaleBone = null;
            string? firstDivergentBone = null;
            float maxDelta = 0f;
            foreach (RenderBone bone in _bones)
            {
                Matrix4x4 render = bone.Transform.RenderMatrix;
                Matrix4x4 world = bone.Transform.WorldMatrix;
                bool rIdentity = render.Equals(Matrix4x4.Identity);
                bool wIdentity = world.Equals(Matrix4x4.Identity);
                if (rIdentity)
                {
                    identityRender++;
                    firstStaleBone ??= bone.Transform.Name;
                }
                if (wIdentity)
                    identityWorld++;

                // The render-thread snapshot (RenderMatrix) lags the authoritative app-thread
                // WorldMatrix. If the palette is seeded from a stale-but-non-identity RenderMatrix,
                // the mesh skins to the wrong pose and the cached compute output never refreshes for
                // a static skeleton (no RenderMatrixChanged fires). Measure translation divergence.
                if (!rIdentity && !wIdentity)
                {
                    float delta = Vector3.Distance(render.Translation, world.Translation);
                    if (delta > 0.001f)
                    {
                        divergent++;
                        firstDivergentBone ??= bone.Transform.Name;
                        if (delta > maxDelta)
                            maxDelta = delta;
                    }
                }
            }

            Debug.LogWarning($"[SkinSeed/{context}] Mesh '{Mesh?.Name ?? "<null>"}': {identityRender}/{total} IDENTITY RenderMatrix, {identityWorld}/{total} IDENTITY WorldMatrix, {divergent}/{total} RenderMatrix DIVERGES from WorldMatrix (maxDelta={maxDelta:F3}, firstDivergent={firstDivergentBone ?? "<none>"}). firstStaleBone={firstStaleBone ?? "<none>"}.");
        }

        private bool _boneSeedDiagnosticsLogged;

        internal void RefreshBoneMatricesFromRenderState()
        {
            BoneResourceSnapshot resources = CaptureBoneResources();
            XRDataBuffer? boneMatrices = resources.BoneMatrices;
            XRDataBuffer? skinPalette = resources.SkinPalette;
            if (_bones is null || boneMatrices is null || skinPalette is null)
            {
                MarkSkinnedOutputDirty();
                return;
            }

            foreach (RenderBone bone in _bones)
            {
                uint index = bone.Index;
                Matrix4x4 renderMatrix = GetCurrentBoneMatrix(bone.Transform);
                boneMatrices.Set(index, renderMatrix);
                skinPalette.Set(index, SkinPaletteMatrix.FromRowVectorMatrix(ComposeSkinPaletteMatrix(index, renderMatrix)));
                MarkBoneMatrixDirty(index, renderMatrix);
            }

            MarkSkinnedOutputDirty();
        }

        /// <summary>
        /// Cheap hash of every utilized bone's CURRENT posed matrix (full 16 floats). Used by the
        /// compute-skinning dispatcher to detect when the skeleton pose has stabilized: a runtime-
        /// imported avatar publishes intermediate bone poses for several frames before settling,
        /// and if the renderer subscribed to RenderMatrixChanged after the final pose was published
        /// (init-order race) no further event fires, so a static mesh stays cached at the wrong
        /// intermediate pose until a bone is manually moved. Re-seeding until this hash is stable
        /// captures the final pose without permanently re-dispatching.
        /// </summary>
        internal int ComputeCurrentBonePoseHash()
        {
            if (_bones is null)
                return 0;
            var hash = new HashCode();
            foreach (RenderBone bone in _bones)
            {
                Matrix4x4 m = GetCurrentBoneMatrix(bone.Transform);
                hash.Add(m.M11); hash.Add(m.M12); hash.Add(m.M13); hash.Add(m.M14);
                hash.Add(m.M21); hash.Add(m.M22); hash.Add(m.M23); hash.Add(m.M24);
                hash.Add(m.M31); hash.Add(m.M32); hash.Add(m.M33); hash.Add(m.M34);
                hash.Add(m.M41); hash.Add(m.M42); hash.Add(m.M43); hash.Add(m.M44);
            }
            return hash.ToHashCode();
        }

        private bool _skinPaletteSeededOnce;
        private int _lastSkinPaletteSeedPoseHash;

        /// <summary>
        /// Number of times <see cref="ReseedSkinPaletteUntilPoseStable"/> has re-pushed the palette.
        /// Exposed for the compute dispatcher's settle diagnostics.
        /// </summary>
        internal int SkinPaletteReseedCount { get; private set; }

        /// <summary>
        /// Shared pose-settle re-seed used by BOTH the vertex draw path (<see cref="RenderableMesh"/>)
        /// and the compute-skinning dispatcher. Re-pushes every bone matrix into the CPU-built skin
        /// palette from the current bone render state and reports whether the skeleton pose has
        /// stabilized (the pose hash is unchanged since the previous re-seed).
        /// <para>
        /// A runtime-imported avatar publishes several frames of intermediate bone poses at startup.
        /// If this renderer subscribed to <c>RenderMatrixChanged</c> after the final pose was already
        /// published (init-order race), no further event fires, so a statically-posed mesh would latch
        /// the intermediate pose into the palette and render wrong ("exploded") until a bone is
        /// manually moved. Callers keep invoking this each frame until it returns <c>true</c>, which
        /// deterministically captures the final settled pose without re-seeding forever.
        /// </para>
        /// </summary>
        /// <returns>True once the pose has been observed stable across two consecutive re-seeds.</returns>
        internal bool ReseedSkinPaletteUntilPoseStable()
        {
            int poseHash = ComputeCurrentBonePoseHash();
            bool poseStable = _skinPaletteSeededOnce && poseHash == _lastSkinPaletteSeedPoseHash;

            RefreshBoneMatricesFromRenderState();

            _lastSkinPaletteSeedPoseHash = poseHash;
            _skinPaletteSeededOnce = true;
            SkinPaletteReseedCount++;
            return poseStable;
        }

        /// <summary>
        /// Clears the shared pose-settle latch so the next <see cref="ReseedSkinPaletteUntilPoseStable"/>
        /// behaves like a fresh first seed. Called when the skin palette / skinned output buffers are
        /// rebuilt (e.g. the mesh changed) so a stale prior pose hash cannot make the re-seed settle
        /// prematurely on the rebuilt palette.
        /// </summary>
        internal void ResetSkinPaletteSeedState()
        {
            _skinPaletteSeededOnce = false;
            _lastSkinPaletteSeedPoseHash = 0;
            SkinPaletteReseedCount = 0;
        }

        private bool MarkBoneMatrixDirty(uint boneIndex, in Matrix4x4 renderMatrix)
        {
            int index = (int)boneIndex;
            if (index <= 0)
                return false;

            lock (_dirtyBoneSyncRoot)
            {
                if (IsBoneGpuDriven(index))
                    return false;

                if (_dirtyBoneFlags is null || _dirtyBoneIndices is null || _dirtyBoneMatrices is null)
                    return false;

                if (index >= _dirtyBoneFlags.Length || index >= _dirtyBoneMatrices.Length)
                    return false;

                _dirtyBoneMatrices[index] = renderMatrix;
                if (!_dirtyBoneFlags[index])
                {
                    _dirtyBoneFlags[index] = true;
                    _dirtyBoneIndices.Add(boneIndex);
                }

                _bonesInvalidated = true;
            }
            return true;
        }

        internal void SyncDirtyBoneMatricesToClientBuffer()
            => WriteDirtyBoneMatricesToClientBuffer(clearDirtyState: false, logDiagnostics: false, out _, out _, out _);

        //TODO: use mapped buffer for constant streaming
        public void PushBoneMatricesToGPU()
        {
            if (!WriteDirtyBoneMatricesToClientBuffer(
                    clearDirtyState: true,
                    logDiagnostics: true,
                    out int dirtyBoneCount,
                    out uint dirtyBoneStart,
                    out uint dirtyBoneEnd))
                return;

            uint dirtyElementCount = dirtyBoneEnd - dirtyBoneStart + 1u;
            BoneResourceSnapshot resources = CaptureBoneResources();
            resources.BoneMatrices?.CommitDirtyElements(dirtyBoneStart, dirtyElementCount);
            resources.SkinPalette?.CommitDirtyElements(dirtyBoneStart, dirtyElementCount);
            long skinPaletteBytes = dirtyBoneCount * 12L * sizeof(float);
            RuntimeEngine.Rendering.Stats.RecordSkinningUpload(skinPaletteBytes, 0L, skinPaletteBytes: skinPaletteBytes);
        }

        private bool WriteDirtyBoneMatricesToClientBuffer(
            bool clearDirtyState,
            bool logDiagnostics,
            out int dirtyBoneCount,
            out uint dirtyBoneStart,
            out uint dirtyBoneEnd)
        {
            dirtyBoneCount = 0;
            dirtyBoneStart = uint.MaxValue;
            dirtyBoneEnd = 0u;

            BoneResourceSnapshot resources = CaptureBoneResources();
            XRDataBuffer? boneMatrices = resources.BoneMatrices;
            XRDataBuffer? skinPalette = resources.SkinPalette;
            if (boneMatrices is null)
                return false;

            lock (_dirtyBoneSyncRoot)
            {
                if (!_bonesInvalidated)
                    return false;

                if (_dirtyBoneIndices is null || _dirtyBoneFlags is null || _dirtyBoneMatrices is null)
                    return false;

                dirtyBoneCount = _dirtyBoneIndices.Count;
                if (dirtyBoneCount == 0)
                {
                    if (clearDirtyState)
                        _bonesInvalidated = false;
                    return false;
                }

                if (logDiagnostics && _skinningDiagnosticsEnabled && _bones is not null)
                    LogDirtyBoneDiagnostics();

                for (int dirtyIndex = 0; dirtyIndex < dirtyBoneCount; dirtyIndex++)
                {
                    uint index = _dirtyBoneIndices[dirtyIndex];
                    int i = (int)index;
                    if (index < dirtyBoneStart)
                        dirtyBoneStart = index;
                    if (index > dirtyBoneEnd)
                        dirtyBoneEnd = index;

                    Matrix4x4 currentMatrix = _dirtyBoneMatrices[i];
                    boneMatrices.Set(index, currentMatrix);
                    if (skinPalette is not null)
                    {
                        Matrix4x4 composed = ComposeSkinPaletteMatrix(index, currentMatrix);
                        DetectSkinPaletteExplosion(index, currentMatrix, composed);
                        skinPalette.Set(index, SkinPaletteMatrix.FromRowVectorMatrix(composed));
                    }
                    if (clearDirtyState)
                        _dirtyBoneFlags[i] = false;
                }

                if (clearDirtyState)
                {
                    _bonesInvalidated = false;
                    _dirtyBoneIndices.Clear();
                }
            }

            return true;
        }

        private Matrix4x4 ComposeSkinPaletteMatrix(uint boneIndex, in Matrix4x4 currentWorldMatrix)
        {
            if (boneIndex == 0u || _bones is null || boneIndex > (uint)_bones.Length)
                return currentWorldMatrix;

            RenderBone bone = _bones[(int)boneIndex - 1];
            Matrix4x4 rootBindMtx = Mesh?.BindRootMatrix ?? Matrix4x4.Identity;
            return rootBindMtx * bone.InvBindMatrix * currentWorldMatrix;
        }

        // Evidence-gathering: the composed skin-palette matrix maps a vertex from root-local bind
        // space into its posed position. Its translation must stay within model bounds. A huge
        // translation or NaN here is the DIRECT cause of an exploded mesh. Always-on, globally
        // rate-limited so it catches the offending bone the moment animation drives it bad.
        private static int _skinPaletteExplosionLogged;
        private const int SkinPaletteExplosionMaxLogs = 40;
        private void DetectSkinPaletteExplosion(uint boneIndex, in Matrix4x4 currentMatrix, in Matrix4x4 composed)
        {
            if (_skinPaletteExplosionLogged >= SkinPaletteExplosionMaxLogs)
                return;

            Vector3 t = composed.Translation;
            bool nan = float.IsNaN(t.X) || float.IsNaN(t.Y) || float.IsNaN(t.Z)
                || float.IsNaN(composed.M11) || float.IsNaN(currentMatrix.M11);
            bool huge = MathF.Abs(t.X) > 50f || MathF.Abs(t.Y) > 50f || MathF.Abs(t.Z) > 50f;
            if (!nan && !huge)
                return;

            _skinPaletteExplosionLogged++;
            RenderBone? rb = (boneIndex > 0 && boneIndex <= (uint)(_bones?.Length ?? 0)) ? _bones![(int)boneIndex - 1] : null;
            Vector3 ct = currentMatrix.Translation;
            Vector3 ib = rb?.InvBindMatrix.Translation ?? default;

            // Decisive frame-mismatch probe: walk the offending bone up to the skeleton root and
            // capture the root's CURRENT world translation. If composedT (== the constant residual G)
            // equals this root world translation, then the skin palette is baking the avatar's root
            // placement into every bone (because invBind was captured at a different/origin frame
            // than current world matrices), and ModelMatrix (== that same root world) re-applies it
            // at draw => the double-transform explosion. Also report BindRootMatrix so we can confirm
            // it is null/identity for this mesh.
            Vector3 rootWorldT = default;
            string rootName = "<none>";
            TransformBase? walk = rb?.Transform;
            int guard = 0;
            while (walk?.Parent is TransformBase p && guard++ < 256)
                walk = p;
            if (walk is not null)
            {
                rootWorldT = walk.WorldMatrix.Translation;
                rootName = walk.SceneNode?.Name ?? "<unnamed>";
            }
            Vector3 brT = (Mesh?.BindRootMatrix ?? Matrix4x4.Identity).Translation;
            bool brIsIdentity = (Mesh?.BindRootMatrix ?? Matrix4x4.Identity).Equals(Matrix4x4.Identity);
            float gVsRoot = Vector3.Distance(t, rootWorldT);
            Debug.LogWarning(
                $"[SkinExplode] {(nan ? "NaN" : "HUGE")} palette bone idx={boneIndex} " +
                $"name='{rb?.Transform.SceneNode?.Name ?? "?"}' mesh='{Mesh?.Name ?? "<null>"}' verts={Mesh?.VertexCount ?? 0} " +
                $"composedT=({t.X:F2},{t.Y:F2},{t.Z:F2}) currentT=({ct.X:F2},{ct.Y:F2},{ct.Z:F2}) invBindT=({ib.X:F2},{ib.Y:F2},{ib.Z:F2}) " +
                $"rootName='{rootName}' rootWorldT=({rootWorldT.X:F2},{rootWorldT.Y:F2},{rootWorldT.Z:F2}) " +
                $"|composedT-rootWorldT|={gVsRoot:F2} BindRootMatrix={(brIsIdentity ? "IDENTITY/null" : $"T=({brT.X:F2},{brT.Y:F2},{brT.Z:F2})")}.");
        }

        /// <summary>
        /// When true, logs diagnostic info about bone matrices every time dirty bones are pushed to the GPU.
        /// Enable via <see cref="EnableSkinningDiagnostics"/>.
        /// </summary>
        private bool _skinningDiagnosticsEnabled = false;
        private int _skinningDiagLogCount = 0;
        private const int SkinningDiagMaxLogs = 5;

        /// <summary>
        /// Enables one-shot skinning diagnostics that log bone matrix info the next few frames bones are dirty.
        /// Call from editor or debugger to diagnose skinning issues.
        /// </summary>
        public void EnableSkinningDiagnostics()
        {
            _skinningDiagnosticsEnabled = true;
            _skinningDiagLogCount = 0;
            Debug.Out("[SkinDiag] Skinning diagnostics enabled. Will log next " + SkinningDiagMaxLogs + " dirty pushes.");
        }

        private void LogDirtyBoneDiagnostics()
        {
            if (_skinningDiagLogCount >= SkinningDiagMaxLogs)
            {
                _skinningDiagnosticsEnabled = false;
                Debug.Out("[SkinDiag] Max log count reached, disabling diagnostics.");
                return;
            }
            _skinningDiagLogCount++;

            Debug.Out($"[SkinDiag] Frame push #{_skinningDiagLogCount}: {_dirtyBoneIndices!.Count} dirty bone(s) for mesh '{Mesh?.Name}'");

            int logged = 0;
            foreach (var index in _dirtyBoneIndices!)
            {
                if (logged >= 3) { Debug.Out($"  ... ({_dirtyBoneIndices.Count - logged} more)"); break; }
                int i = (int)index;
                Matrix4x4 current = _dirtyBoneMatrices![i];

                // Find the matching RenderBone to get invBind
                RenderBone? rb = (i > 0 && i <= _bones!.Length) ? _bones[i - 1] : null;
                if (rb is null) { Debug.Out($"  Bone[{index}]: no RenderBone found!"); logged++; continue; }

                Matrix4x4 invBind = rb.InvBindMatrix;
                // C# row-vector: delta = InvBind * Current
                Matrix4x4 delta = invBind * current;
                Vector3 deltaT = delta.Translation;
                float traceRot = delta.M11 + delta.M22 + delta.M33; // trace of rotation part; ~3.0 at identity

                bool hasNaN = float.IsNaN(current.M11) || float.IsNaN(current.M41) || float.IsNaN(invBind.M11) || float.IsNaN(delta.M11);
                bool largeTranslation = deltaT.Length() > 100f;
                string flag = hasNaN ? " *** NaN ***" : largeTranslation ? " *** LARGE ***" : "";

                Debug.Out($"  Bone[{index}] '{rb.Transform.SceneNode?.Name ?? "?"}'{flag}:");
                Debug.Out($"    Current  T=({current.M41:F3},{current.M42:F3},{current.M43:F3})");
                Debug.Out($"    InvBind  T=({invBind.M41:F3},{invBind.M42:F3},{invBind.M43:F3})");
                Debug.Out($"    Delta    T=({deltaT.X:F3},{deltaT.Y:F3},{deltaT.Z:F3}) rotTrace={traceRot:F3}");
                logged++;
            }
        }

        internal void RegisterGpuDrivenBoneIndices(IReadOnlyList<uint> boneIndices)
        {
            if (_gpuDrivenBoneRefCounts is null || boneIndices.Count == 0)
                return;

            lock (_dirtyBoneSyncRoot)
            {
                if (_gpuDrivenBoneRefCounts is null)
                    return;

                for (int i = 0; i < boneIndices.Count; ++i)
                {
                    uint boneIndex = boneIndices[i];
                    if (boneIndex >= (uint)_gpuDrivenBoneRefCounts.Length)
                        continue;

                    if (_gpuDrivenBoneRefCounts[boneIndex]++ == 0)
                        ++_gpuDrivenBoneCount;

                    ClearDirtyBoneIndex((int)boneIndex);
                }
            }
        }

        internal void UnregisterGpuDrivenBoneIndices(IReadOnlyList<uint> boneIndices)
        {
            if (_gpuDrivenBoneRefCounts is null || boneIndices.Count == 0)
                return;

            lock (_dirtyBoneSyncRoot)
            {
                if (_gpuDrivenBoneRefCounts is null)
                    return;

                for (int i = 0; i < boneIndices.Count; ++i)
                {
                    uint boneIndex = boneIndices[i];
                    if (boneIndex >= (uint)_gpuDrivenBoneRefCounts.Length)
                        continue;

                    int refCount = _gpuDrivenBoneRefCounts[boneIndex];
                    if (refCount <= 0)
                        continue;

                    refCount -= 1;
                    _gpuDrivenBoneRefCounts[boneIndex] = refCount;
                    if (refCount == 0 && _gpuDrivenBoneCount > 0)
                        --_gpuDrivenBoneCount;
                }
            }
        }

        internal void SetGpuDrivenSkinPaletteSource(
            object owner,
            XRDataBuffer skinPalette,
            XRDataBuffer? previousSkinPalette,
            uint baseElement,
            uint elementCount)
        {
            _externalSkinPaletteSourceOwner = owner;
            _externalSkinPaletteBuffer = skinPalette;
            _externalPreviousSkinPaletteBuffer = previousSkinPalette;
            _externalSkinPaletteBase = baseElement;
            _externalSkinPaletteCount = elementCount;
            MarkSkinnedOutputDirty();
        }

        internal void ClearGpuDrivenSkinPaletteSource(object owner)
        {
            if (!ReferenceEquals(_externalSkinPaletteSourceOwner, owner))
                return;

            ClearGpuDrivenSkinPaletteSource();
        }

        private void ClearGpuDrivenSkinPaletteSource()
        {
            _externalSkinPaletteSourceOwner = null;
            _externalSkinPaletteBuffer = null;
            _externalPreviousSkinPaletteBuffer = null;
            _externalSkinPaletteBase = 0u;
            _externalSkinPaletteCount = 0u;
            MarkSkinnedOutputDirty();
        }

        private bool IsBoneGpuDriven(int index)
            => _gpuDrivenBoneRefCounts is not null
                && index >= 0
                && index < _gpuDrivenBoneRefCounts.Length
                && _gpuDrivenBoneRefCounts[index] > 0;

        private void ClearDirtyBoneIndex(int index)
        {
            lock (_dirtyBoneSyncRoot)
            {
                if (_dirtyBoneFlags is null || _dirtyBoneIndices is null)
                    return;

                if (index < 0 || index >= _dirtyBoneFlags.Length || !_dirtyBoneFlags[index])
                    return;

                _dirtyBoneFlags[index] = false;
                _dirtyBoneIndices.Remove((uint)index);
                _bonesInvalidated = _dirtyBoneIndices.Count > 0;
            }
        }

        private void SetBlendshapesInvalidated(bool value)
            => SetField(ref _blendshapesInvalidated, value);

        private void MarkBlendshapeWeightDirty(uint index)
        {
            InvalidatePrecombinedBlendshapeDeltas();
            SetBlendshapesInvalidated(true);
            if (_blendshapeDirtyStartIndex == uint.MaxValue || index < _blendshapeDirtyStartIndex)
                _blendshapeDirtyStartIndex = index;
            if (index > _blendshapeDirtyEndIndex)
                _blendshapeDirtyEndIndex = index;

            unchecked
            {
                _blendshapeWeightsVersion++;
                _precombinedBlendshapeInputVersion++;
            }

            RebuildActiveBlendshapeList();
        }

        private void RebuildActiveBlendshapeList()
        {
            BlendshapeResourceSnapshot resources = CaptureBlendshapeResources();
            XRDataBuffer? weights = resources.Weights;
            XRDataBuffer? activeWeights = resources.ActiveWeights;
            if (weights is null || activeWeights is null || Mesh is null)
                return;

            uint blendshapeCount = Mesh.BlendshapeCount;
            int activeCount = 0;
            for (uint i = 0; i < blendshapeCount; i++)
            {
                float weight = weights.GetFloat(i);
                if (!IsBlendshapeWeightActive(weight) || !IsBlendshapeAllowedByLod((int)i))
                    continue;

                activeWeights.SetVector2((uint)activeCount, new Vector2(i, weight));
                activeCount++;
            }

            InvalidatePrecombinedBlendshapeDeltas();
            SetField(ref _activeBlendshapeCount, activeCount);
            _blendshapeActiveListInvalidated = true;
            unchecked
            {
                _precombinedBlendshapeInputVersion++;
            }
        }

        private bool IsBlendshapeWeightActive(float weight)
            => MathF.Abs(weight) > _blendshapeActiveWeightThreshold;

        private bool IsBlendshapeAllowedByLod(int blendshapeIndex)
        {
            if (_blendshapeLodProfile is null || !_blendshapeLodProfile.TryGetTier(_activeBlendshapeLodTier, out BlendshapeLodTier tier))
                return true;

            if (tier.Evaluation == BlendshapeLodEvaluation.Disabled)
                return false;
            if (tier.Evaluation == BlendshapeLodEvaluation.Full)
                return true;

            IReadOnlyList<int>? shapeIndices = tier.ShapeIndices;
            if (shapeIndices is not null)
            {
                for (int i = 0; i < shapeIndices.Count; i++)
                    if (shapeIndices[i] == blendshapeIndex)
                        return true;
            }

            string[]? names = Mesh?.BlendshapeNames;
            if (names is null || (uint)blendshapeIndex >= (uint)names.Length)
                return false;

            string name = names[blendshapeIndex];
            IReadOnlyList<string>? protectedNames = tier.ProtectedShapeNames;
            if (protectedNames is not null)
            {
                for (int i = 0; i < protectedNames.Count; i++)
                    if (string.Equals(protectedNames[i], name, StringComparison.OrdinalIgnoreCase))
                        return true;
            }

            return false;
        }

        public void PushBlendshapeWeightsToGPU()
        {
            BlendshapeResourceSnapshot resources = CaptureBlendshapeResources();
            XRDataBuffer? weights = resources.Weights;
            XRDataBuffer? activeWeights = resources.ActiveWeights;
            if (weights is null)
                return;

            long blendshapeWeightBytes = 0L;
            if (_blendshapesInvalidated)
            {
                if (_blendshapeDirtyStartIndex != uint.MaxValue && _blendshapeDirtyEndIndex >= _blendshapeDirtyStartIndex)
                {
                    int offset = checked((int)(_blendshapeDirtyStartIndex * weights.ElementSize));
                    uint length = checked((_blendshapeDirtyEndIndex - _blendshapeDirtyStartIndex + 1u) * weights.ElementSize);
                    weights.CommitDirtyBytes(checked((uint)offset), length);
                    blendshapeWeightBytes = length;
                }
                else
                {
                    weights.CommitDirtyBytes(0u, weights.Length);
                    blendshapeWeightBytes = weights.Length;
                }

                _blendshapeDirtyStartIndex = uint.MaxValue;
                _blendshapeDirtyEndIndex = 0u;
                SetBlendshapesInvalidated(false);
            }

            long activeListBytes = 0L;
            if (_blendshapeActiveListInvalidated && activeWeights is not null)
            {
                if (_activeBlendshapeCount > 0)
                {
                    uint activeElementCount = checked((uint)_activeBlendshapeCount);
                    activeWeights.CommitDirtyElements(0u, activeElementCount);
                    activeListBytes = checked(activeElementCount * activeWeights.ElementSize);
                }

                _blendshapeActiveListInvalidated = false;
            }

            if (blendshapeWeightBytes > 0 || activeListBytes > 0)
            {
                RuntimeEngine.Rendering.Stats.RecordSkinningUpload(
                    0L,
                    blendshapeWeightBytes,
                    blendshapeActiveListUploadBytes: activeListBytes,
                    blendshapeAuthoredShapeCount: (int)(Mesh?.BlendshapeCount ?? 0u),
                    blendshapeActiveShapeCount: _activeBlendshapeCount,
                    compactedActiveBlendshapeCount: _activeBlendshapeCount);
            }
        }

        #region Mesh Deformation Methods

        /// <summary>
        /// Rebuilds the mesh deform buffers when influences or settings change.
        /// </summary>
        private void RebuildMeshDeformBuffers()
            => RebuildMeshDeformBuffers(
                _mesh,
                _deformMeshRenderer,
                _meshDeformInfluences,
                _maxMeshDeformInfluences,
                _optimizeMeshDeformToVec4);

        private void RebuildMeshDeformBuffers(
            XRMesh? targetMesh,
            XRMeshRenderer? deformer,
            MeshDeformInfluence[][]? influences,
            int maxInfluences,
            bool optimizeToVec4)
        {
            if (influences is null || deformer?.Mesh is null || targetMesh is null)
            {
                ClearMeshDeformBuffersTransactionally(
                    targetMesh,
                    deformer,
                    influences,
                    maxInfluences,
                    optimizeToVec4);
                return;
            }

            XRMesh.BufferCollection targetBuffers = Buffers;
            XRMesh.BufferCollection.PreparedBufferTicket preparationTicket =
                targetBuffers.CapturePreparationTicket(MeshDeformBufferState.CollectionKeys);
            XRMeshRenderer staging = new(detachedStagingRenderer: true)
            {
                _mesh = targetMesh,
                _deformMeshRenderer = deformer,
                _meshDeformInfluences = influences,
                _maxMeshDeformInfluences = maxInfluences,
                _optimizeMeshDeformToVec4 = optimizeToVec4,
            };
            MeshDeformBufferState prepared;
            try
            {
                using RenderObjectPublicationScope publication = GenericRenderObject.BeginDeferredPublication();
                staging.PopulateMeshDeformBuffers();
                prepared = staging.CaptureMeshDeformBufferState();
                MeshDeformBufferState? previous = null;
                XRMesh.BufferCollection.PreparedBufferBatch? batch = null;
                bool lifetimeLeaseHeld = false;
                publication.Complete(
                    () =>
                    {
                        EnterResourcePublicationLease();
                        lifetimeLeaseHeld = true;
                        ValidateMeshDeformConfigurationIdentity(
                            targetMesh,
                            deformer,
                            influences,
                            maxInfluences,
                            optimizeToVec4);
                        batch = targetBuffers.SwapPreparedBatch(
                            prepared.EnumerateCollectionBuffers(),
                            MeshDeformBufferState.CollectionKeys,
                            expectedTicket: preparationTicket,
                            installState: () =>
                            {
                                previous = CaptureMeshDeformBufferState();
                                ApplyMeshDeformBufferState(prepared);
                            },
                            restoreStateOnFailure: () =>
                            {
                                if (previous is not null)
                                    ApplyMeshDeformBufferState(previous);
                            });
                        ValidateResourcePublicationLease();
                    },
                    () =>
                    {
                        if (!lifetimeLeaseHeld)
                            return;
                        try
                        {
                            if (batch is not null)
                                targetBuffers.RestorePreparedBatch(
                                    batch,
                                    () =>
                                    {
                                        if (previous is not null)
                                            ApplyMeshDeformBufferState(previous);
                                    });
                        }
                        finally
                        {
                            lifetimeLeaseHeld = false;
                            ExitResourcePublicationLease();
                        }
                    },
                    () =>
                    {
                        if (!lifetimeLeaseHeld)
                            return;
                        try
                        {
                            if (batch is not null)
                                targetBuffers.DisposeReplacedBuffers(batch);
                        }
                        finally
                        {
                            lifetimeLeaseHeld = false;
                            ExitResourcePublicationLease();
                        }
                    });
                staging.DetachMeshDeformBufferState();
            }
            finally
            {
                staging.AbortRendererConstruction();
            }
        }

        private void ClearMeshDeformBuffersTransactionally()
            => ClearMeshDeformBuffersTransactionally(
                _mesh,
                _deformMeshRenderer,
                _meshDeformInfluences,
                _maxMeshDeformInfluences,
                _optimizeMeshDeformToVec4);

        private void ClearMeshDeformBuffersTransactionally(
            XRMesh? targetMesh,
            XRMeshRenderer? deformer,
            MeshDeformInfluence[][]? influences,
            int maxInfluences,
            bool optimizeToVec4)
        {
            XRMesh.BufferCollection targetBuffers = Buffers;
            XRMesh.BufferCollection.PreparedBufferTicket preparationTicket =
                targetBuffers.CapturePreparationTicket(MeshDeformBufferState.CollectionKeys);
            MeshDeformBufferState? previous = null;
            MeshDeformBufferState empty = new();
            using RenderObjectPublicationScope publication = GenericRenderObject.BeginDeferredPublication();
            XRMesh.BufferCollection.PreparedBufferBatch? batch = null;
            bool lifetimeLeaseHeld = false;
            publication.Complete(
                () =>
                {
                    EnterResourcePublicationLease();
                    lifetimeLeaseHeld = true;
                    ValidateMeshDeformConfigurationIdentity(
                        targetMesh,
                        deformer,
                        influences,
                        maxInfluences,
                        optimizeToVec4);
                    batch = targetBuffers.SwapPreparedBatch(
                        [],
                        MeshDeformBufferState.CollectionKeys,
                        expectedTicket: preparationTicket,
                        installState: () =>
                        {
                            previous = CaptureMeshDeformBufferState();
                            ApplyMeshDeformBufferState(empty);
                        },
                        restoreStateOnFailure: () =>
                        {
                            if (previous is not null)
                                ApplyMeshDeformBufferState(previous);
                        });
                    ValidateResourcePublicationLease();
                },
                () =>
                {
                    if (!lifetimeLeaseHeld)
                        return;
                    try
                    {
                        if (batch is not null)
                            targetBuffers.RestorePreparedBatch(
                                batch,
                                () =>
                                {
                                    if (previous is not null)
                                        ApplyMeshDeformBufferState(previous);
                                });
                    }
                    finally
                    {
                        lifetimeLeaseHeld = false;
                        ExitResourcePublicationLease();
                    }
                },
                () =>
                {
                    if (!lifetimeLeaseHeld)
                        return;
                    try
                    {
                        if (batch is not null)
                            targetBuffers.DisposeReplacedBuffers(batch);
                    }
                    finally
                    {
                        lifetimeLeaseHeld = false;
                        ExitResourcePublicationLease();
                    }
                });
        }

        private void ValidateMeshDeformConfigurationIdentity(
            XRMesh? targetMesh,
            XRMeshRenderer? deformer,
            MeshDeformInfluence[][]? influences,
            int maxInfluences,
            bool optimizeToVec4)
        {
            if (!ReferenceEquals(_mesh, targetMesh) ||
                !ReferenceEquals(_deformMeshRenderer, deformer) ||
                !ReferenceEquals(_meshDeformInfluences, influences) ||
                _maxMeshDeformInfluences != maxInfluences ||
                _optimizeMeshDeformToVec4 != optimizeToVec4)
            {
                throw new InvalidOperationException("Mesh-deformation configuration changed while renderer buffers were being prepared.");
            }
        }

        /// <summary>
        /// Sets up mesh-to-mesh deformation using another renderer as the deformer source.
        /// </summary>
        /// <param name="deformerRenderer">Renderer supplying the live deformer positions. Its mesh defines the deformer vertex index space referenced by <paramref name="influences"/>.</param>
        /// <param name="influences">Per-target-vertex influence lists. The outer array is indexed by this renderer's mesh vertex index. Each inner list contains deformer vertex indices and weights. Invalid or non-positive influences are ignored during buffer build.</param>
        /// <remarks>
        /// Contract summary:
        /// - The target mesh is this renderer's <see cref="Mesh"/>.
        /// - The deformer mesh is <paramref name="deformerRenderer"/>'s <see cref="Mesh"/>.
        /// - Influence array length should match the target mesh vertex count. Missing entries are treated as uninfluenced vertices.
        /// - When <see cref="OptimizeMeshDeformToVec4"/> is enabled and <see cref="MaxMeshDeformInfluences"/> is 4 or less, only the first 4 valid influences per vertex are serialized.
        /// - Positions are always required. Normals and tangents are only bound when both meshes expose those channels.
        /// - If the deformer renderer has compute-skinned outputs, the mesh-deform path prefers those over the static mesh buffers.
        /// </remarks>
        public void SetupMeshDeformation(XRMeshRenderer deformerRenderer, MeshDeformInfluence[][] influences)
            => ChangeMeshDeformConfiguration(
                deformerRenderer,
                influences,
                _maxMeshDeformInfluences,
                _optimizeMeshDeformToVec4);

        /// <summary>
        /// Clears mesh deformation, reverting to standard rendering.
        /// </summary>
        public void ClearMeshDeformation()
            => ChangeMeshDeformConfiguration(
                null,
                null,
                _maxMeshDeformInfluences,
                _optimizeMeshDeformToVec4);

        private void PopulateMeshDeformBuffers()
        {
            if (_meshDeformInfluences is null || DeformMeshRenderer?.Mesh is null || Mesh is null)
                return;

            var deformerMesh = DeformMeshRenderer.Mesh;
            uint deformerVertexCount = (uint)deformerMesh.VertexCount;
            uint vertexCount = (uint)Mesh.VertexCount;

            ValidateMeshDeformConfiguration(vertexCount, deformerVertexCount);

            // Create deformer position buffers
            Volatile.Read(ref _meshDeformBufferState).Positions = new XRDataBuffer(
                $"{MeshDeformVertexShaderGenerator.DeformerPositionsBufferName}Buffer",
                EBufferTarget.ShaderStorageBuffer,
                deformerVertexCount,
                EComponentType.Float,
                4, // vec4
                false,
                false)
            {
                Usage = EBufferUsage.StreamDraw,
                DisposeOnPush = false
            };

            Volatile.Read(ref _meshDeformBufferState).RestPositions = new XRDataBuffer(
                $"{MeshDeformVertexShaderGenerator.DeformerRestPositionsBufferName}Buffer",
                EBufferTarget.ShaderStorageBuffer,
                deformerVertexCount,
                EComponentType.Float,
                4, // vec4
                false,
                false)
            {
                Usage = EBufferUsage.StaticCopy,
                DisposeOnPush = false
            };

            // Initialize deformer positions from deformer mesh
            for (uint i = 0; i < deformerVertexCount; i++)
            {
                var pos = deformerMesh.GetPosition(i);
                DeformerPositionsBuffer!.SetVector4(i, new Vector4(pos, 1.0f));
                DeformerRestPositionsBuffer!.SetVector4(i, new Vector4(pos, 1.0f));
            }

            if (TryCopySkinnedDeformerPositions(deformerMesh))
                _meshDeformInvalidated = true;

            Buffers.Add(DeformerPositionsBuffer!.AttributeName, DeformerPositionsBuffer);
            Buffers.Add(DeformerRestPositionsBuffer!.AttributeName, DeformerRestPositionsBuffer);

            // Create deformer normal buffer if mesh has normals
            if (Mesh.HasNormals && deformerMesh.HasNormals)
            {
                Volatile.Read(ref _meshDeformBufferState).Normals = new XRDataBuffer(
                    $"{MeshDeformVertexShaderGenerator.DeformerNormalsBufferName}Buffer",
                    EBufferTarget.ShaderStorageBuffer,
                    deformerVertexCount,
                    EComponentType.Float,
                    4, // vec4
                    false,
                    false)
                {
                    Usage = EBufferUsage.StreamDraw,
                    DisposeOnPush = false
                };

                for (uint i = 0; i < deformerVertexCount; i++)
                {
                    var nrm = deformerMesh.GetNormal(i);
                    DeformerNormalsBuffer!.SetVector4(i, new Vector4(nrm, 0.0f));
                }

                TryCopySkinnedDeformerNormals(deformerMesh);

                Buffers.Add(DeformerNormalsBuffer!.AttributeName, DeformerNormalsBuffer);
            }

            // Create deformer tangent buffer if mesh has tangents
            if (Mesh.HasTangents && deformerMesh.HasTangents)
            {
                Volatile.Read(ref _meshDeformBufferState).Tangents = new XRDataBuffer(
                    $"{MeshDeformVertexShaderGenerator.DeformerTangentsBufferName}Buffer",
                    EBufferTarget.ShaderStorageBuffer,
                    deformerVertexCount,
                    EComponentType.Float,
                    4, // vec4
                    false,
                    false)
                {
                    Usage = EBufferUsage.StreamDraw,
                    DisposeOnPush = false
                };

                for (uint i = 0; i < deformerVertexCount; i++)
                {
                    DeformerTangentsBuffer!.SetVector4(i, deformerMesh.GetTangentWithSign(i));
                }

                TryCopySkinnedDeformerTangents(deformerMesh);

                Buffers.Add(DeformerTangentsBuffer!.AttributeName, DeformerTangentsBuffer);
            }

            // Create per-vertex influence buffers
            bool useVec4Optimization = OptimizeMeshDeformToVec4 && MaxMeshDeformInfluences <= 4;

            if (useVec4Optimization)
            {
                PopulateMeshDeformVec4Buffers(vertexCount);
            }
            else
            {
                PopulateMeshDeformSSBOBuffers(vertexCount);
            }

            _meshDeformInvalidated = true;
        }

        private void PopulateMeshDeformVec4Buffers(uint vertexCount)
        {
            // Create per-vertex vec4 buffers for indices and weights
            Volatile.Read(ref _meshDeformBufferState).VertexIndices = new XRDataBuffer(
                MeshDeformVertexShaderGenerator.MeshDeformVertexIndicesAttrName,
                EBufferTarget.ArrayBuffer,
                vertexCount,
                RuntimeEngine.Rendering.Settings.UseIntegerUniformsInShaders ? EComponentType.Int : EComponentType.Float,
                4, // ivec4 or vec4
                false,
                false)
            {
                Usage = EBufferUsage.StaticDraw,
                DisposeOnPush = false
            };

            Volatile.Read(ref _meshDeformBufferState).VertexWeights = new XRDataBuffer(
                MeshDeformVertexShaderGenerator.MeshDeformVertexWeightsAttrName,
                EBufferTarget.ArrayBuffer,
                vertexCount,
                EComponentType.Float,
                4, // vec4
                false,
                false)
            {
                Usage = EBufferUsage.StaticDraw,
                DisposeOnPush = false
            };

            // Fill per-vertex data
            for (uint v = 0; v < vertexCount; v++)
            {
                var influences = v < _meshDeformInfluences!.Length ? _meshDeformInfluences[v] : null;

                Vector4 indices = new(-1, -1, -1, -1);
                Vector4 weights = Vector4.Zero;

                if (influences is not null)
                {
                    int writeIndex = 0;
                    for (int i = 0; i < influences.Length && writeIndex < 4; i++)
                    {
                        if (!IsValidMeshDeformInfluence(influences[i], DeformerPositionsBuffer?.ElementCount ?? 0u))
                            continue;

                        switch (writeIndex)
                        {
                            case 0:
                                indices.X = influences[i].VertexIndex;
                                weights.X = influences[i].Weight;
                                break;
                            case 1:
                                indices.Y = influences[i].VertexIndex;
                                weights.Y = influences[i].Weight;
                                break;
                            case 2:
                                indices.Z = influences[i].VertexIndex;
                                weights.Z = influences[i].Weight;
                                break;
                            case 3:
                                indices.W = influences[i].VertexIndex;
                                weights.W = influences[i].Weight;
                                break;
                        }

                        writeIndex++;
                    }
                }

                if (RuntimeEngine.Rendering.Settings.UseIntegerUniformsInShaders)
                {
                    MeshDeformVertexIndicesBuffer!.SetDataRawAtIndex(v, new IVector4((int)indices.X, (int)indices.Y, (int)indices.Z, (int)indices.W));
                }
                else
                {
                    MeshDeformVertexIndicesBuffer!.SetDataRawAtIndex(v, indices);
                }
                MeshDeformVertexWeightsBuffer!.SetDataRawAtIndex(v, weights);
            }

            Buffers.Add(MeshDeformVertexIndicesBuffer!.AttributeName, MeshDeformVertexIndicesBuffer);
            Buffers.Add(MeshDeformVertexWeightsBuffer!.AttributeName, MeshDeformVertexWeightsBuffer);
        }

        private void PopulateMeshDeformSSBOBuffers(uint vertexCount)
        {
            // Calculate total influence count
            uint totalInfluences = 0;
            for (int v = 0; v < _meshDeformInfluences!.Length; v++)
            {
                var influences = _meshDeformInfluences[v];
                if (influences is null)
                    continue;

                for (int i = 0; i < influences.Length; i++)
                {
                    if (IsValidMeshDeformInfluence(influences[i], DeformerPositionsBuffer?.ElementCount ?? 0u))
                        totalInfluences++;
                }
            }

            // Create SSBO buffers for indices and weights
            Volatile.Read(ref _meshDeformBufferState).Indices = new XRDataBuffer(
                $"{MeshDeformVertexShaderGenerator.MeshDeformIndicesBufferName}Buffer",
                EBufferTarget.ShaderStorageBuffer,
                Math.Max(1, totalInfluences),
                EComponentType.Int,
                1,
                false,
                false)
            {
                Usage = EBufferUsage.StaticDraw,
                DisposeOnPush = false
            };

            Volatile.Read(ref _meshDeformBufferState).Weights = new XRDataBuffer(
                $"{MeshDeformVertexShaderGenerator.MeshDeformWeightsBufferName}Buffer",
                EBufferTarget.ShaderStorageBuffer,
                Math.Max(1, totalInfluences),
                EComponentType.Float,
                1,
                false,
                false)
            {
                Usage = EBufferUsage.StaticDraw,
                DisposeOnPush = false
            };

            // Create per-vertex offset and count attribute buffers
            Volatile.Read(ref _meshDeformBufferState).VertexOffsets = new XRDataBuffer(
                MeshDeformVertexShaderGenerator.MeshDeformVertexOffsetAttrName,
                EBufferTarget.ArrayBuffer,
                vertexCount,
                RuntimeEngine.Rendering.Settings.UseIntegerUniformsInShaders ? EComponentType.Int : EComponentType.Float,
                1,
                false,
                false)
            {
                Usage = EBufferUsage.StaticDraw,
                DisposeOnPush = false
            };

            Volatile.Read(ref _meshDeformBufferState).VertexCounts = new XRDataBuffer(
                MeshDeformVertexShaderGenerator.MeshDeformVertexCountAttrName,
                EBufferTarget.ArrayBuffer,
                vertexCount,
                RuntimeEngine.Rendering.Settings.UseIntegerUniformsInShaders ? EComponentType.Int : EComponentType.Float,
                1,
                false,
                false)
            {
                Usage = EBufferUsage.StaticDraw,
                DisposeOnPush = false
            };

            // Fill buffers
            uint currentOffset = 0;
            for (uint v = 0; v < vertexCount; v++)
            {
                var influences = v < _meshDeformInfluences!.Length ? _meshDeformInfluences[v] : null;
                int count = 0;
                if (influences is not null)
                {
                    for (int i = 0; i < influences.Length; i++)
                    {
                        if (IsValidMeshDeformInfluence(influences[i], DeformerPositionsBuffer?.ElementCount ?? 0u))
                            count++;
                    }
                }

                if (RuntimeEngine.Rendering.Settings.UseIntegerUniformsInShaders)
                {
                    MeshDeformVertexOffsetBuffer!.Set(v, (int)currentOffset);
                    MeshDeformVertexCountBuffer!.Set(v, count);
                }
                else
                {
                    MeshDeformVertexOffsetBuffer!.Set(v, (float)currentOffset);
                    MeshDeformVertexCountBuffer!.Set(v, (float)count);
                }

                if (influences is not null)
                {
                    int writeIndex = 0;
                    for (int i = 0; i < influences.Length; i++)
                    {
                        if (!IsValidMeshDeformInfluence(influences[i], DeformerPositionsBuffer?.ElementCount ?? 0u))
                            continue;

                        MeshDeformIndicesBuffer!.Set(currentOffset + (uint)writeIndex, influences[i].VertexIndex);
                        MeshDeformWeightsBuffer!.Set(currentOffset + (uint)writeIndex, influences[i].Weight);
                        writeIndex++;
                    }
                }

                currentOffset += (uint)count;
            }

            Buffers.Add(MeshDeformIndicesBuffer!.AttributeName, MeshDeformIndicesBuffer);
            Buffers.Add(MeshDeformWeightsBuffer!.AttributeName, MeshDeformWeightsBuffer);
            Buffers.Add(MeshDeformVertexOffsetBuffer!.AttributeName, MeshDeformVertexOffsetBuffer);
            Buffers.Add(MeshDeformVertexCountBuffer!.AttributeName, MeshDeformVertexCountBuffer);
        }

        /// <summary>
        /// Updates the deformer positions buffer from the deformer mesh's current vertex positions.
        /// Call this each frame when the deformer mesh is animated.
        /// </summary>
        public void UpdateDeformerPositions()
        {
            if (DeformerPositionsBuffer is null || DeformMeshRenderer?.Mesh is null)
                return;

            var deformerMesh = DeformMeshRenderer.Mesh;

            if (TryCopySkinnedDeformerPositions(deformerMesh))
            {
                _meshDeformInvalidated = true;
                return;
            }

            // Otherwise, get positions from mesh data
            uint count = DeformerPositionsBuffer.ElementCount;
            for (uint i = 0; i < count; i++)
            {
                var pos = deformerMesh.GetPosition(i);
                DeformerPositionsBuffer.SetVector4(i, new Vector4(pos, 1.0f));
            }

            _meshDeformInvalidated = true;
        }

        /// <summary>
        /// Updates the deformer normals buffer from the deformer mesh's current vertex normals.
        /// </summary>
        public void UpdateDeformerNormals()
        {
            if (DeformerNormalsBuffer is null || DeformMeshRenderer?.Mesh is null)
                return;

            var deformerMesh = DeformMeshRenderer.Mesh;

            if (TryCopySkinnedDeformerNormals(deformerMesh))
            {
                _meshDeformInvalidated = true;
                return;
            }

            uint count = DeformerNormalsBuffer.ElementCount;
            for (uint i = 0; i < count; i++)
            {
                var nrm = deformerMesh.GetNormal(i);
                DeformerNormalsBuffer.SetVector4(i, new Vector4(nrm, 0.0f));
            }

            _meshDeformInvalidated = true;
        }

        /// <summary>
        /// Updates the deformer tangents buffer from the deformer mesh's current vertex tangents.
        /// </summary>
        public void UpdateDeformerTangents()
        {
            if (DeformerTangentsBuffer is null || DeformMeshRenderer?.Mesh is null)
                return;

            var deformerMesh = DeformMeshRenderer.Mesh;

            if (TryCopySkinnedDeformerTangents(deformerMesh))
            {
                _meshDeformInvalidated = true;
                return;
            }

            uint count = DeformerTangentsBuffer.ElementCount;
            for (uint i = 0; i < count; i++)
            {
                DeformerTangentsBuffer.SetVector4(i, deformerMesh.GetTangentWithSign(i));
            }

            _meshDeformInvalidated = true;
        }

        /// <summary>
        /// Pushes mesh deform buffers to GPU if they have been invalidated.
        /// </summary>
        public void PushMeshDeformBuffersToGPU()
        {
            if (!_meshDeformInvalidated)
                return;

            _meshDeformInvalidated = false;

            DeformerPositionsBuffer?.PushSubData();
            DeformerNormalsBuffer?.PushSubData();
            DeformerTangentsBuffer?.PushSubData();
        }

        private void ValidateMeshDeformConfiguration(uint vertexCount, uint deformerVertexCount)
        {
            if (_meshDeformInfluences is null)
                return;

            _meshDeformLastTargetVertexCount = vertexCount;
            _meshDeformLastDeformerVertexCount = deformerVertexCount;

            if (_meshDeformInfluences.Length != vertexCount)
            {
                Debug.LogWarning($"XRMeshRenderer mesh-deform influence array length ({_meshDeformInfluences.Length}) does not match target mesh vertex count ({vertexCount}). Missing entries will be treated as uninfluenced vertices.");
            }

            int invalidInfluenceCount = 0;
            int truncatedVertices = 0;
            bool usingVec4Optimization = OptimizeMeshDeformToVec4 && MaxMeshDeformInfluences <= 4;

            foreach (var influences in _meshDeformInfluences)
            {
                if (influences is null || influences.Length == 0)
                    continue;

                int validCount = 0;
                for (int i = 0; i < influences.Length; i++)
                {
                    if (IsValidMeshDeformInfluence(influences[i], deformerVertexCount))
                        validCount++;
                    else
                        invalidInfluenceCount++;
                }

                if (usingVec4Optimization && validCount > 4)
                    truncatedVertices++;
            }

            _meshDeformLastInvalidInfluenceCount = invalidInfluenceCount;
            _meshDeformLastTruncatedVertexCount = truncatedVertices;

            if (invalidInfluenceCount > 0)
            {
                Debug.LogWarning($"XRMeshRenderer mesh-deform setup skipped {invalidInfluenceCount} invalid influence(s) that referenced missing deformer vertices or had non-positive weights.");
            }

            if (truncatedVertices > 0)
            {
                Debug.LogWarning($"XRMeshRenderer mesh-deform vec4 optimization truncates influences to 4 per vertex. {truncatedVertices} vertex/vertices exceed that limit.");
            }
        }

        private static bool IsValidMeshDeformInfluence(MeshDeformInfluence influence, uint deformerVertexCount)
            => influence.Weight > 0.0001f && influence.VertexIndex >= 0 && influence.VertexIndex < deformerVertexCount;

        private bool TryCopySkinnedDeformerPositions(XRMesh deformerMesh)
        {
            if (DeformerPositionsBuffer is null || DeformMeshRenderer is null)
                return false;

            if (TryCopyVector4Buffer(DeformMeshRenderer.SkinnedPositionsBuffer, DeformerPositionsBuffer))
                return true;

            return TryCopyInterleavedVector3Buffer(DeformMeshRenderer.SkinnedInterleavedBuffer, DeformerPositionsBuffer, deformerMesh.InterleavedStride, deformerMesh.PositionOffset, 1.0f);
        }

        private bool TryCopySkinnedDeformerNormals(XRMesh deformerMesh)
        {
            if (DeformerNormalsBuffer is null || DeformMeshRenderer is null)
                return false;

            if (TryCopyVector4Buffer(DeformMeshRenderer.SkinnedNormalsBuffer, DeformerNormalsBuffer))
                return true;

            if (deformerMesh.NormalOffset.HasValue)
                return TryCopyInterleavedVector3Buffer(DeformMeshRenderer.SkinnedInterleavedBuffer, DeformerNormalsBuffer, deformerMesh.InterleavedStride, deformerMesh.NormalOffset.Value, 0.0f);

            return false;
        }

        private bool TryCopySkinnedDeformerTangents(XRMesh deformerMesh)
        {
            if (DeformerTangentsBuffer is null || DeformMeshRenderer is null)
                return false;

            if (TryCopyVector4Buffer(DeformMeshRenderer.SkinnedTangentsBuffer, DeformerTangentsBuffer))
                return true;

            if (deformerMesh.TangentOffset.HasValue)
                return TryCopyInterleavedVector4Buffer(DeformMeshRenderer.SkinnedInterleavedBuffer, DeformerTangentsBuffer, deformerMesh.InterleavedStride, deformerMesh.TangentOffset.Value);

            return false;
        }

        private static unsafe bool TryCopyVector4Buffer(XRDataBuffer? source, XRDataBuffer target)
        {
            if (source is null || source.ComponentType != EComponentType.Float || source.ComponentCount != 4 || source.ClientSideSource is null)
                return false;

            uint copyCount = Math.Min(source.ElementCount, target.ElementCount);
            if (copyCount == 0)
                return false;

            Memory.Move(target.Address, source.Address, copyCount * target.ElementSize);
            return true;
        }

        private static bool TryCopyInterleavedVector3Buffer(XRDataBuffer? source, XRDataBuffer target, uint strideBytes, uint offsetBytes, float w)
        {
            if (source?.ClientSideSource is null)
                return false;

            if (strideBytes == 0)
                return false;

            uint vertexCount = Math.Min(target.ElementCount, source.Length / strideBytes);
            if (vertexCount == 0)
                return false;

            for (uint i = 0; i < vertexCount; i++)
            {
                uint byteOffset = i * strideBytes + offsetBytes;
                target.SetVector4(i, new Vector4(source.GetVector3AtOffset(byteOffset), w));
            }

            return true;
        }

        private static bool TryCopyInterleavedVector4Buffer(XRDataBuffer? source, XRDataBuffer target, uint strideBytes, uint offsetBytes)
        {
            if (source?.ClientSideSource is null)
                return false;

            if (strideBytes == 0)
                return false;

            uint vertexCount = Math.Min(target.ElementCount, source.Length / strideBytes);
            if (vertexCount == 0)
                return false;

            for (uint i = 0; i < vertexCount; i++)
            {
                uint byteOffset = i * strideBytes + offsetBytes;
                target.SetVector4(i, source.GetVector4AtOffset(byteOffset));
            }

            return true;
        }

        private void RemoveMeshDeformBuffer(XRDataBuffer? buffer)
        {
            if (buffer is null || string.IsNullOrWhiteSpace(buffer.AttributeName))
                return;

            Buffers.Remove(buffer.AttributeName);
        }

        private static string ResolveMeshDeformChannelSource(XRDataBuffer? separateSource, XRDataBuffer? interleavedSource, bool hasInterleavedLayout)
        {
            if (separateSource is not null)
                return "ComputeSkinnedSeparateBuffers";
            if (interleavedSource is not null && hasInterleavedLayout)
                return "ComputeSkinnedInterleavedFallbackCopy";
            return "MeshBuffers";
        }

        #endregion

        public T? Parameter<T>(int index) where T : ShaderVar
            => Material?.Parameter<T>(index);
        public T? Parameter<T>(string name) where T : ShaderVar
            => Material?.Parameter<T>(name);

        public void SetParameter(int index, ColorF4 color) => Parameter<ShaderVector4>(index)?.SetValue(color);
        public void SetParameter(int index, int value) => Parameter<ShaderInt>(index)?.SetValue(value);
        public void SetParameter(int index, float value) => Parameter<ShaderFloat>(index)?.SetValue(value);
        public void SetParameter(int index, Vector2 value) => Parameter<ShaderVector2>(index)?.SetValue(value);
        public void SetParameter(int index, Vector3 value) => Parameter<ShaderVector3>(index)?.SetValue(value);
        public void SetParameter(int index, Vector4 value) => Parameter<ShaderVector4>(index)?.SetValue(value);
        public void SetParameter(int index, Matrix4x4 value) => Parameter<ShaderMat4>(index)?.SetValue(value);

        public void SetParameter(string name, ColorF4 color) => Parameter<ShaderVector4>(name)?.SetValue(color);
        public void SetParameter(string name, int value) => Parameter<ShaderInt>(name)?.SetValue(value);
        public void SetParameter(string name, float value) => Parameter<ShaderFloat>(name)?.SetValue(value);
        public void SetParameter(string name, Vector2 value) => Parameter<ShaderVector2>(name)?.SetValue(value);
        public void SetParameter(string name, Vector3 value) => Parameter<ShaderVector3>(name)?.SetValue(value);
        public void SetParameter(string name, Vector4 value) => Parameter<ShaderVector4>(name)?.SetValue(value);
        public void SetParameter(string name, Matrix4x4 value) => Parameter<ShaderMat4>(name)?.SetValue(value);

        internal void OnSettingUniforms(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
        {
            var renderState = RuntimeEngine.Rendering.State.RenderingPipelineState;
            renderState?.ApplyScopedProgramBindings(vertexProgram);

            if (!ReferenceEquals(vertexProgram, materialProgram))
                renderState?.ApplyScopedProgramBindings(materialProgram);

            Material?.OnSettingVertexUniforms(vertexProgram);
            vertexProgram.Uniform("blendshapeActiveCount", ActiveBlendshapeCount);
            vertexProgram.Uniform("blendshapeWeightThreshold", BlendshapeActiveWeightThreshold);
            if (!ReferenceEquals(vertexProgram, materialProgram))
            {
                materialProgram.Uniform("blendshapeActiveCount", ActiveBlendshapeCount);
                materialProgram.Uniform("blendshapeWeightThreshold", BlendshapeActiveWeightThreshold);
            }

            _settingUniforms?.Invoke(vertexProgram, materialProgram);
        }

        internal void OnPreparingRenderData()
            => _preparingRenderData?.Invoke();

        /// <summary>
        /// Retrieve all meshes and materials used by this renderer.
        /// </summary>
        /// <returns></returns>
        public (XRMesh? mesh, XRMaterial? material)[] GetMeshes()
        {
            if (Submeshes.Count <= 0)
                return [(Mesh, Material)];
            else
            {
                var arr = new (XRMesh? mesh, XRMaterial? material)[Submeshes.Count];
                for (int i = 0; i < Submeshes.Count; i++)
                {
                    var sm = Submeshes[i];
                    arr[i] = (sm.Mesh, sm.Material);
                }
                return arr;
            }
        }

        /// <summary>
        /// Resolves one render primitive without allocating the compatibility
        /// array returned by <see cref="GetMeshes"/>.
        /// </summary>
        public bool TryGetMesh(
            int primitiveIndex,
            out XRMesh? mesh,
            out XRMaterial? material)
        {
            if (primitiveIndex < 0)
            {
                mesh = null;
                material = null;
                return false;
            }

            if (Submeshes.Count == 0)
            {
                mesh = Mesh;
                material = Material;
                return primitiveIndex == 0;
            }

            if ((uint)primitiveIndex >= (uint)Submeshes.Count)
            {
                mesh = null;
                material = null;
                return false;
            }

            SubMesh submesh = Submeshes[primitiveIndex];
            mesh = submesh.Mesh;
            material = submesh.Material;
            return true;
        }
    }
}
