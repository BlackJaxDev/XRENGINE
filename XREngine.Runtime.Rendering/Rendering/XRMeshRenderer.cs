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
            /// Short, human-readable label that identifies which vertex-shader variant this version
            /// represents (e.g. "Default", "OVRMultiView", "NVStereo", "DirectionalCascadeInstanced").
            /// Surfaced by the shader-program-links panel so each program tells the engineer what pass
            /// and stereo strategy it is for.
            /// </summary>
            public virtual string VersionKindLabel
            {
                get
                {
                    Type t = GetType();
                    if (t.IsGenericType)
                    {
                        Type genericArg = t.GetGenericArguments()[0];
                        string raw = genericArg.Name;
                        const string suffix = "VertexShaderGenerator";
                        return raw.EndsWith(suffix, StringComparison.Ordinal)
                            ? raw[..^suffix.Length]
                            : raw;
                    }
                    return t.Name;
                }
            }

            public void ResetVertexShaderSource()
            {
                _vertexShaderSource = null;
                _vertexShaderSettingsRevision = -1;
            }

            protected abstract string? GenerateVertexShaderSource();

            public delegate void DelRenderRequested(Matrix4x4 worldMatrix, Matrix4x4 prevWorldMatrix, XRMaterial? materialOverride, RenderingParameters? renderOptionsOverride, uint instances, EMeshBillboardMode billboardMode, bool forceNoStereo);
            /// <summary>
            /// Tells all renderers to render this mesh.
            /// </summary>
            public event DelRenderRequested? RenderRequested;

            /// <summary>
            /// Use this to render the mesh.
            /// </summary>
            /// <param name="modelMatrix"></param>
            /// <param name="prevModelMatrix"></param>
            /// <param name="materialOverride"></param>
            public void Render(Matrix4x4 modelMatrix, Matrix4x4 prevModelMatrix, XRMaterial? materialOverride, RenderingParameters? renderOptionsOverride, uint instances, EMeshBillboardMode billboardMode, bool forceNoStereo)
                => RenderRequested?.Invoke(modelMatrix, prevModelMatrix, materialOverride, renderOptionsOverride, instances, billboardMode, forceNoStereo);
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

        [MemoryPackIgnore]
        public Dictionary<int, BaseVersion> GeneratedVertexShaderVersions { get; set; } = [];

        private bool? _shaderPipelinesAllowedForAllVersions;

        /// <summary>
        /// Overrides the shader-pipeline allowance for existing and future generated
        /// vertex-shader versions without forcing those versions to be materialized.
        /// </summary>
        public void SetShaderPipelinesAllowedForAllVersions(bool allow)
        {
            SetField(ref _shaderPipelinesAllowedForAllVersions, allow, nameof(SetShaderPipelinesAllowedForAllVersions));

            foreach (BaseVersion version in GeneratedVertexShaderVersions.Values)
                version.AllowShaderPipelines = allow;
        }

        /// <summary>
        /// Automatically selects the correct version of this mesh to render based on the current rendering state.
        /// </summary>
        /// <param name="forceNoStereo"></param>
        /// <returns></returns>
        private BaseVersion GetVersion(bool forceNoStereo = false)
        {
            bool stereoPass = !forceNoStereo && RuntimeEngine.Rendering.State.IsStereoPass && CanUseVrSpecificVersions();
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

            bool allowNvStereo = !RuntimeEngine.Rendering.State.IsVulkan;
            bool preferNV = allowNvStereo && RuntimeEngine.Rendering.Settings.PreferNVStereo;
            bool hasNvMaterialVertexShader = MaterialHasMatchingVertexShader(HasNVStereoViewRendering);
            bool hasMultiViewMaterialVertexShader = MaterialHasMatchingVertexShader(HasMultiViewExtension);
            bool canUseGeneratedStereoVertexShader = !MaterialHasAnyVertexShader();
            
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
            => RuntimeEngine.VRState.IsInVR;

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

        public XRMeshRenderer() : this(null, null) { }
        public XRMeshRenderer(XRMesh? mesh, XRMaterial? material)
        {
            _mesh = mesh;
            _material = material;
            InitializeDrivableBuffers();
        }
        public XRMeshRenderer(params (XRMesh mesh, XRMaterial material)[] submeshes)
            : this((IEnumerable<(XRMesh mesh, XRMaterial material)>)submeshes) { }
        public XRMeshRenderer(IEnumerable<(XRMesh mesh, XRMaterial material)> submeshes)
        {
            foreach (var (mesh, material) in submeshes)
                Submeshes.Add(new SubMesh() { Mesh = mesh, Material = material, InstanceCount = 1 });
            InitializeDrivableBuffers();
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
            if (GeneratedVertexShaderVersions.TryGetValue(versionKey, out var existing))
            {
                existing.ProgramPriority = ResolveProgramPriority(versionKey);
                return existing;
            }

            bool allowShaderPipelines = ResolveShaderPipelinesAllowedForVersion(versionKey);
            BaseVersion created = versionKey switch
            {
                0 => new Version<DefaultVertexShaderGenerator>(this, NoSpecialExtensions, allowShaderPipelines),
                1 => new Version<OVRMultiViewVertexShaderGenerator>(this, HasMultiViewExtension, allowShaderPipelines),
                2 => new Version<NVStereoVertexShaderGenerator>(this, HasNVStereoViewRendering, allowShaderPipelines),
                3 => new MeshDeformVersion(this, NoSpecialExtensions, allowShaderPipelines),
                4 => new MeshDeformVersion(this, HasMultiViewExtension, allowShaderPipelines) { UseOVRMultiView = true },
                5 => new MeshDeformVersion(this, HasNVStereoViewRendering, allowShaderPipelines) { UseNVStereo = true },
                6 => new Version<DirectionalCascadeInstancedVertexShaderGenerator>(this, NoSpecialExtensions, allowShaderPipelines),
                7 => new Version<PointLightInstancedVertexShaderGenerator>(this, NoSpecialExtensions, allowShaderPipelines),
                8 => new Version<DirectionalCascadeAtlasInstancedVertexShaderGenerator>(this, NoSpecialExtensions, allowShaderPipelines),
                9 => new Version<PointLightAtlasInstancedVertexShaderGenerator>(this, NoSpecialExtensions, allowShaderPipelines),
                _ => throw new ArgumentOutOfRangeException(nameof(versionKey), versionKey, "Unknown mesh renderer shader version."),
            };

            // Assign a priority bucket so the shared-context shader-link worker queue can serve
            // user-visible main-pass programs before shadow / VR variants.
            created.ProgramPriority = ResolveProgramPriority(versionKey);

            GeneratedVertexShaderVersions.Add(versionKey, created);
            return created;
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
            set => SetField(ref _mesh, value);
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
            set => SetField(ref _deformMeshRenderer, value);
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
            set
            {
                if (SetField(ref _meshDeformInfluences, value))
                    RebuildMeshDeformBuffers();
            }
        }

        private int _maxMeshDeformInfluences = 8;
        /// <summary>
        /// Maximum number of deformer mesh vertices that can influence each vertex.
        /// Default is 8. Lower values use more efficient vec4 packing when = 4.
        /// </summary>
        public int MaxMeshDeformInfluences
        {
            get => _maxMeshDeformInfluences;
            set
            {
                if (SetField(ref _maxMeshDeformInfluences, Math.Max(1, value)))
                {
                    ResetMeshDeformVersionShaders();
                    RebuildMeshDeformBuffers();
                }
            }
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
            set
            {
                if (SetField(ref _optimizeMeshDeformToVec4, value))
                {
                    ResetMeshDeformVersionShaders();
                    RebuildMeshDeformBuffers();
                }
            }
        }

        private void ResetMeshDeformVersionShaders()
        {
            // Reset the mesh deform version shader sources so they regenerate with new parameters
            if (GeneratedVertexShaderVersions.TryGetValue(3, out var v3))
                v3.ResetVertexShaderSource();
            if (GeneratedVertexShaderVersions.TryGetValue(4, out var v4))
                v4.ResetVertexShaderSource();
            if (GeneratedVertexShaderVersions.TryGetValue(5, out var v5))
                v5.ResetVertexShaderSource();
        }

        #endregion

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Mesh):
                    InitializeDrivableBuffers();
                    break;
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
                    case nameof(Mesh):
                        ResetDrivableBuffers();
                        break;
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
        public void Render(Matrix4x4 modelMatrix, Matrix4x4 prevModelMatrix, XRMaterial? materialOverride = null, uint instances = 1u, bool forceNoStereo = false, RenderingParameters? renderOptionsOverride = null)
            => GetVersion(forceNoStereo).Render(modelMatrix, prevModelMatrix, materialOverride, renderOptionsOverride, instances, Material?.BillboardMode ?? EMeshBillboardMode.None, forceNoStereo);

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
            if (BlendshapeWeights is null || index >= (Mesh?.BlendshapeCount ?? 0u))
                return 0.0f;

            return BlendshapeWeights.GetFloat(index);
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
            if (BlendshapeWeights is null || index >= (Mesh?.BlendshapeCount ?? 0u))
                return;

            float previous = BlendshapeWeights.GetFloat(index);
            if (previous.Equals(weight))
                return;

            BlendshapeWeights.SetFloat(index, weight);
            MarkBlendshapeWeightDirty(index);
            MarkSkinnedOutputDirty();
        }

        private void InitializeDrivableBuffers()
        {
            ResetDrivableBuffers();

            if ((Mesh?.HasSkinning ?? false) && RuntimeEngine.Rendering.Settings.AllowSkinning)
                PopulateBoneMatrixBuffers();

            if ((Mesh?.HasBlendshapes ?? false) && RuntimeEngine.Rendering.Settings.AllowBlendshapes)
                PopulateBlendshapeWeightsBuffer();
        }

        /// <summary>
        /// Ensures skinning buffers (bone matrices) are initialized if the mesh has skinning data.
        /// Call this before attempting to bind GPU-driven bone palette shaders if there's a chance
        /// the buffers weren't created during renderer construction.
        /// </summary>
        /// <returns>True if the renderer skin palette is available after this call; false otherwise.</returns>
        public bool EnsureSkinningBuffers(bool logWarnings = true)
        {
            if (BoneMatricesBuffer is not null && BoneInvBindMatricesBuffer is not null && SkinPaletteBuffer is not null)
                return true;

            if (BoneMatricesBuffer is not null || BoneInvBindMatricesBuffer is not null || SkinPaletteBuffer is not null)
            {
                RemoveMeshDeformBuffer(BoneMatricesBuffer);
                BoneMatricesBuffer?.Destroy();
                BoneMatricesBuffer = null;

                RemoveMeshDeformBuffer(BoneInvBindMatricesBuffer);
                BoneInvBindMatricesBuffer?.Destroy();
                BoneInvBindMatricesBuffer = null;

                RemoveMeshDeformBuffer(SkinPaletteBuffer);
                SkinPaletteBuffer?.Destroy();
                SkinPaletteBuffer = null;
            }

            if (Mesh is null)
            {
                if (logWarnings)
                    Debug.LogWarning($"[XRMeshRenderer] EnsureSkinningBuffers: Mesh is null, cannot initialize skinning buffers. Renderer={GetHashCode():X}");
                return false;
            }

            if (!Mesh.HasSkinning)
            {
                if (logWarnings)
                    Debug.LogWarning($"[XRMeshRenderer] EnsureSkinningBuffers: Mesh '{Mesh.Name}' has no skinning data. Renderer={GetHashCode():X}");
                return false;
            }

            if (!RuntimeEngine.Rendering.Settings.AllowSkinning)
            {
                if (logWarnings)
                    Debug.LogWarning($"[XRMeshRenderer] EnsureSkinningBuffers: Skinning is disabled in render settings. Mesh='{Mesh.Name}', Renderer={GetHashCode():X}");
                return false;
            }

            if (logWarnings)
                Debug.LogWarning($"[XRMeshRenderer] EnsureSkinningBuffers: BoneMatricesBuffer was null, initializing late. This may indicate a timing issue with mesh/renderer creation. Mesh='{Mesh.Name}', UtilizedBones={Mesh.UtilizedBones?.Length ?? 0}, Renderer={GetHashCode():X}");

            PopulateBoneMatrixBuffers();

            if (BoneMatricesBuffer is null || BoneInvBindMatricesBuffer is null || SkinPaletteBuffer is null)
            {
                if (logWarnings)
                    Debug.LogWarning($"[XRMeshRenderer] EnsureSkinningBuffers: PopulateBoneMatrixBuffers did not create buffer. Mesh='{Mesh.Name}', Renderer={GetHashCode():X}");
                return false;
            }

            return true;
        }

        public bool EnsureBlendshapeBuffers(bool logWarnings = true)
        {
            if (BlendshapeWeights is not null)
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
        {
            uint blendshapeCount = Mesh?.BlendshapeCount ?? 0;
            BlendshapeWeights = new XRDataBuffer($"{ECommonBufferType.BlendshapeWeights}Buffer", EBufferTarget.ShaderStorageBuffer, blendshapeCount.Align(4), EComponentType.Float, 1, false, false)
            {
                Usage = EBufferUsage.DynamicDraw,
                DisposeOnPush = false
            };

            for (uint i = 0; i < blendshapeCount; i++)
                BlendshapeWeights.Set(i, 0.0f);

            Buffers.Add(BlendshapeWeights.AttributeName, BlendshapeWeights);

            BlendshapeActiveWeights = new XRDataBuffer($"{ECommonBufferType.BlendshapeActiveWeights}Buffer", EBufferTarget.ShaderStorageBuffer, blendshapeCount.Align(4), EComponentType.Float, 2, false, false)
            {
                Usage = EBufferUsage.DynamicDraw,
                DisposeOnPush = false
            };
            Buffers.Add(BlendshapeActiveWeights.AttributeName, BlendshapeActiveWeights);

            _activeBlendshapeCount = 0;
            _blendshapeDirtyStartIndex = uint.MaxValue;
            _blendshapeDirtyEndIndex = 0u;
            unchecked
            {
                _blendshapeWeightsVersion++;
                _precombinedBlendshapeInputVersion++;
            }
            SetBlendshapesInvalidated(false);
            _blendshapeActiveListInvalidated = false;

            if (RuntimeEngine.Rendering.Settings.EnableBlendshapePrecombinePass && Mesh is { VertexCount: > 0 } mesh)
                EnsurePrecombinedBlendshapeBuffers(mesh);
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
            BoneMatricesBuffer = null;

            RemoveMeshDeformBuffer(BoneInvBindMatricesBuffer);
            BoneInvBindMatricesBuffer?.Destroy();
            BoneInvBindMatricesBuffer = null;

            RemoveMeshDeformBuffer(SkinPaletteBuffer);
            SkinPaletteBuffer?.Destroy();
            SkinPaletteBuffer = null;

            RemoveMeshDeformBuffer(PreviousSkinPaletteBuffer);
            PreviousSkinPaletteBuffer?.Destroy();
            PreviousSkinPaletteBuffer = null;

            RemoveMeshDeformBuffer(BlendshapeWeights);
            BlendshapeWeights?.Destroy();
            BlendshapeWeights = null;

            RemoveMeshDeformBuffer(BlendshapeActiveWeights);
            BlendshapeActiveWeights?.Destroy();
            BlendshapeActiveWeights = null;
            DestroyPrecombinedBlendshapeBuffers();
            _activeBlendshapeCount = 0;
            _blendshapeDirtyStartIndex = uint.MaxValue;
            _blendshapeDirtyEndIndex = 0u;
            unchecked
            {
                _blendshapeWeightsVersion++;
                _precombinedBlendshapeInputVersion++;
            }
            SetBlendshapesInvalidated(false);
            _blendshapeActiveListInvalidated = false;

            ResetMeshDeformBuffers();
        }

        private void ResetMeshDeformBuffers()
        {
            RemoveMeshDeformBuffer(DeformerPositionsBuffer);
            DeformerPositionsBuffer?.Destroy();
            DeformerPositionsBuffer = null;

            RemoveMeshDeformBuffer(DeformerRestPositionsBuffer);
            DeformerRestPositionsBuffer?.Destroy();
            DeformerRestPositionsBuffer = null;

            RemoveMeshDeformBuffer(DeformerNormalsBuffer);
            DeformerNormalsBuffer?.Destroy();
            DeformerNormalsBuffer = null;

            RemoveMeshDeformBuffer(DeformerTangentsBuffer);
            DeformerTangentsBuffer?.Destroy();
            DeformerTangentsBuffer = null;

            RemoveMeshDeformBuffer(MeshDeformIndicesBuffer);
            MeshDeformIndicesBuffer?.Destroy();
            MeshDeformIndicesBuffer = null;

            RemoveMeshDeformBuffer(MeshDeformWeightsBuffer);
            MeshDeformWeightsBuffer?.Destroy();
            MeshDeformWeightsBuffer = null;

            RemoveMeshDeformBuffer(MeshDeformVertexIndicesBuffer);
            MeshDeformVertexIndicesBuffer?.Destroy();
            MeshDeformVertexIndicesBuffer = null;

            RemoveMeshDeformBuffer(MeshDeformVertexWeightsBuffer);
            MeshDeformVertexWeightsBuffer?.Destroy();
            MeshDeformVertexWeightsBuffer = null;

            RemoveMeshDeformBuffer(MeshDeformVertexOffsetBuffer);
            MeshDeformVertexOffsetBuffer?.Destroy();
            MeshDeformVertexOffsetBuffer = null;

            RemoveMeshDeformBuffer(MeshDeformVertexCountBuffer);
            MeshDeformVertexCountBuffer?.Destroy();
            MeshDeformVertexCountBuffer = null;

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
        public XRDataBuffer? BoneMatricesBuffer { get; private set; }

        /// <summary>
        /// All bone inverse bind matrices for the mesh.
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? BoneInvBindMatricesBuffer { get; private set; }

        /// <summary>
        /// Precomposed final skin palette stored as three vec4 rows per bone.
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? SkinPaletteBuffer { get; private set; }

        [MemoryPackIgnore]
        public XRDataBuffer? PreviousSkinPaletteBuffer { get; private set; }

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
        public XRDataBuffer? BlendshapeWeights { get; private set; }

        /// <summary>
        /// Dense active blendshape index/weight pairs for compact shader paths.
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? BlendshapeActiveWeights { get; private set; }

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
        public XRDataBuffer? SkinnedPositionsBuffer { get; internal set; }

        /// <summary>
        /// Output buffer for skinned normals from compute shader pre-pass.
        /// When set, this buffer is used instead of the mesh's normal buffer for rendering.
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? SkinnedNormalsBuffer { get; internal set; }

        /// <summary>
        /// Output buffer for skinned tangents from compute shader pre-pass.
        /// When set, this buffer is used instead of the mesh's tangent buffer for rendering.
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? SkinnedTangentsBuffer { get; internal set; }

        /// <summary>
        /// Output buffer for skinned interleaved data from compute shader pre-pass.
        /// When set, this buffer is used instead of the mesh's interleaved buffer for rendering.
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? SkinnedInterleavedBuffer { get; internal set; }

        /// <summary>
        /// Precombined position deltas for active blendshapes. The compute pre-pass writes this and
        /// final skinning/direct vertex paths add it once per vertex when the heuristic selects it.
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? PrecombinedBlendshapePositionsBuffer { get; internal set; }

        /// <summary>
        /// Precombined normal deltas for active blendshapes.
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? PrecombinedBlendshapeNormalsBuffer { get; internal set; }

        /// <summary>
        /// Precombined tangent deltas for active blendshapes.
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? PrecombinedBlendshapeTangentsBuffer { get; internal set; }

        private bool _skinnedOutputDirty = true;
        private ulong _skinnedOutputVersion;
        private XRMesh? _precombinedBlendshapeMesh;
        private int _precombinedBlendshapeVertexCount;
        private bool _precombinedBlendshapeHasNormals;
        private bool _precombinedBlendshapeHasTangents;
        private bool _hasValidPrecombinedBlendshapeOutput;
        private ulong _precombinedBlendshapeInputVersion;
        private ulong _precombinedBlendshapeOutputVersion;

        [MemoryPackIgnore]
        internal bool SkinnedOutputDirty => _skinnedOutputDirty;

        [MemoryPackIgnore]
        internal ulong SkinnedOutputVersion => _skinnedOutputVersion;

        [MemoryPackIgnore]
        internal bool HasValidPrecombinedBlendshapeDeltas
            => _hasValidPrecombinedBlendshapeOutput
            && _precombinedBlendshapeMesh is not null
            && ReferenceEquals(_precombinedBlendshapeMesh, Mesh)
            && _precombinedBlendshapeVertexCount == (Mesh?.VertexCount ?? 0)
            && _precombinedBlendshapeOutputVersion == _precombinedBlendshapeInputVersion;

        [MemoryPackIgnore]
        internal bool HasPendingComputeSkinningInputChanges
            => _bonesInvalidated || _blendshapesInvalidated || _meshDeformInvalidated;

        internal void MarkSkinnedOutputDirty()
        {
            if (!_skinnedOutputDirty)
            {
                bool dirty = true;
                SetField(ref _skinnedOutputDirty, dirty);
            }

            unchecked
            {
                _skinnedOutputVersion++;
            }
        }

        internal void MarkSkinnedOutputClean()
        {
            if (_skinnedOutputDirty)
            {
                bool dirty = false;
                SetField(ref _skinnedOutputDirty, dirty);
            }
        }

        internal bool EnsurePrecombinedBlendshapeBuffers(XRMesh mesh)
        {
            int vertexCount = mesh.VertexCount;
            if (vertexCount <= 0)
                return false;

            bool buffersExist = PrecombinedBlendshapePositionsBuffer is not null
                && (!mesh.HasNormals || PrecombinedBlendshapeNormalsBuffer is not null)
                && (!mesh.HasTangents || PrecombinedBlendshapeTangentsBuffer is not null);
            if (buffersExist
                && ReferenceEquals(_precombinedBlendshapeMesh, mesh)
                && _precombinedBlendshapeVertexCount == vertexCount
                && _precombinedBlendshapeHasNormals == mesh.HasNormals
                && _precombinedBlendshapeHasTangents == mesh.HasTangents)
            {
                return true;
            }

            DestroyPrecombinedBlendshapeBuffers();

            _precombinedBlendshapeMesh = mesh;
            _precombinedBlendshapeVertexCount = vertexCount;
            _precombinedBlendshapeHasNormals = mesh.HasNormals;
            _precombinedBlendshapeHasTangents = mesh.HasTangents;

            PrecombinedBlendshapePositionsBuffer = CreatePrecombinedBlendshapeBuffer("PrecombinedBlendshapePositionDeltas", vertexCount);
            if (mesh.HasNormals)
                PrecombinedBlendshapeNormalsBuffer = CreatePrecombinedBlendshapeBuffer("PrecombinedBlendshapeNormalDeltas", vertexCount);
            if (mesh.HasTangents)
                PrecombinedBlendshapeTangentsBuffer = CreatePrecombinedBlendshapeBuffer("PrecombinedBlendshapeTangentDeltas", vertexCount);

            _hasValidPrecombinedBlendshapeOutput = false;
            _precombinedBlendshapeOutputVersion = 0UL;
            return true;
        }

        internal void MarkPrecombinedBlendshapeDeltasValid(XRMesh mesh)
        {
            _precombinedBlendshapeMesh = mesh;
            _precombinedBlendshapeVertexCount = mesh.VertexCount;
            _precombinedBlendshapeHasNormals = mesh.HasNormals;
            _precombinedBlendshapeHasTangents = mesh.HasTangents;
            _precombinedBlendshapeOutputVersion = _precombinedBlendshapeInputVersion;
            _hasValidPrecombinedBlendshapeOutput = true;
        }

        internal void InvalidatePrecombinedBlendshapeDeltas()
        {
            if (!_hasValidPrecombinedBlendshapeOutput)
                return;

            bool valid = false;
            SetField(ref _hasValidPrecombinedBlendshapeOutput, valid);
        }

        private static XRDataBuffer CreatePrecombinedBlendshapeBuffer(string name, int vertexCount)
            => new(name, EBufferTarget.ShaderStorageBuffer, (uint)vertexCount, EComponentType.Float, 4, true, false)
            {
                Usage = EBufferUsage.DynamicDraw,
                DisposeOnPush = false,
            };

        private void DestroyPrecombinedBlendshapeBuffers()
        {
            PrecombinedBlendshapePositionsBuffer?.Destroy();
            PrecombinedBlendshapeNormalsBuffer?.Destroy();
            PrecombinedBlendshapeTangentsBuffer?.Destroy();
            PrecombinedBlendshapePositionsBuffer = null;
            PrecombinedBlendshapeNormalsBuffer = null;
            PrecombinedBlendshapeTangentsBuffer = null;
            _precombinedBlendshapeMesh = null;
            _precombinedBlendshapeVertexCount = 0;
            _precombinedBlendshapeHasNormals = false;
            _precombinedBlendshapeHasTangents = false;
            _hasValidPrecombinedBlendshapeOutput = false;
            _precombinedBlendshapeOutputVersion = 0UL;
        }

        #region Mesh Deform Buffers

        /// <summary>
        /// Current positions of deformer mesh vertices (SSBO).
        /// Updated each frame from DeformMeshRenderer.
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? DeformerPositionsBuffer { get; private set; }

        /// <summary>
        /// Rest positions of deformer mesh vertices (SSBO).
        /// Static buffer containing original bind pose positions.
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? DeformerRestPositionsBuffer { get; private set; }

        /// <summary>
        /// Current normals of deformer mesh vertices (SSBO).
        /// Updated each frame from DeformMeshRenderer.
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? DeformerNormalsBuffer { get; private set; }

        /// <summary>
        /// Current tangents of deformer mesh vertices (SSBO).
        /// Updated each frame from DeformMeshRenderer.
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? DeformerTangentsBuffer { get; private set; }

        /// <summary>
        /// SSBO containing all deformer vertex indices for all vertices (SSBO mode only).
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? MeshDeformIndicesBuffer { get; private set; }

        /// <summary>
        /// SSBO containing all deformer weights for all vertices (SSBO mode only).
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? MeshDeformWeightsBuffer { get; private set; }

        /// <summary>
        /// Per-vertex vec4 containing up to 4 deformer vertex indices (vec4 optimized mode).
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? MeshDeformVertexIndicesBuffer { get; private set; }

        /// <summary>
        /// Per-vertex vec4 containing up to 4 deformer weights (vec4 optimized mode).
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? MeshDeformVertexWeightsBuffer { get; private set; }

        /// <summary>
        /// Per-vertex offset into MeshDeformIndicesBuffer/MeshDeformWeightsBuffer (SSBO mode).
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? MeshDeformVertexOffsetBuffer { get; private set; }

        /// <summary>
        /// Per-vertex count of influences (SSBO mode).
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? MeshDeformVertexCountBuffer { get; private set; }

        private bool _meshDeformInvalidated = false;

        #endregion

        private void PopulateBoneMatrixBuffers()
        {
            //using var timer = RuntimeEngine.Profiler.Start();

            // Finalize the mesh's skinning bone ordering BEFORE sizing/seeding the palette.
            // RebuildSkinningBuffersFromVertices (invoked lazily for non-canonical meshes) can
            // reorder/extend UtilizedBones while packing the per-vertex core bone indices. If we
            // build the palette from a pre-rebuild ordering and that rebuild happens afterwards
            // (e.g. during the compute pre-pass), the indices reference the wrong palette slots and
            // the mesh explodes until a skinning toggle forces a palette rebuild. Finalizing here
            // guarantees the palette and the core indices share the same bone order from frame one.
            Mesh?.EnsureSkinningBoneOrderFinalized();

            uint boneCount = (uint)(Mesh?.UtilizedBones?.Length ?? 0);

            BoneMatricesBuffer = new($"{ECommonBufferType.BoneMatrices}Buffer", EBufferTarget.ShaderStorageBuffer, boneCount + 1, EComponentType.Float, 16, false, false)
            {
                //RangeFlags = EBufferMapRangeFlags.Write | EBufferMapRangeFlags.Persistent | EBufferMapRangeFlags.Coherent;
                //StorageFlags = EBufferMapStorageFlags.Write | EBufferMapStorageFlags.Persistent | EBufferMapStorageFlags.Coherent | EBufferMapStorageFlags.ClientStorage;
                Usage = EBufferUsage.StreamDraw,
                DisposeOnPush = false
            };
            BoneInvBindMatricesBuffer = new($"{ECommonBufferType.BoneInvBindMatrices}Buffer", EBufferTarget.ShaderStorageBuffer, boneCount + 1, EComponentType.Float, 16, false, false)
            {
                Usage = EBufferUsage.StaticCopy
            };
            SkinPaletteBuffer = new($"{ECommonBufferType.SkinPalette}Buffer", EBufferTarget.ShaderStorageBuffer, boneCount + 1, EComponentType.Float, 12, false, false)
            {
                Usage = EBufferUsage.StreamDraw,
                DisposeOnPush = false
            };

            BoneMatricesBuffer.Set(0, Matrix4x4.Identity);
            BoneInvBindMatricesBuffer.Set(0, Matrix4x4.Identity);
            SkinPaletteBuffer.Set(0, SkinPaletteMatrix.Identity);

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
            Matrix4x4 rootBindMtx = Mesh!.BindRootMatrix ?? Matrix4x4.Identity;

            for (int i = 0; i < _bones.Length; i++)
            {
                var (tfm, invBindWorldMtx) = Mesh!.UtilizedBones[i];
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
            if (_bones is null || BoneMatricesBuffer is null || SkinPaletteBuffer is null)
            {
                MarkSkinnedOutputDirty();
                return;
            }

            foreach (RenderBone bone in _bones)
            {
                uint index = bone.Index;
                Matrix4x4 renderMatrix = GetCurrentBoneMatrix(bone.Transform);
                BoneMatricesBuffer.Set(index, renderMatrix);
                SkinPaletteBuffer.Set(index, SkinPaletteMatrix.FromRowVectorMatrix(ComposeSkinPaletteMatrix(index, renderMatrix)));
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
            BoneMatricesBuffer?.CommitDirtyElements(dirtyBoneStart, dirtyElementCount);
            SkinPaletteBuffer?.CommitDirtyElements(dirtyBoneStart, dirtyElementCount);
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

            if (BoneMatricesBuffer is null)
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
                    BoneMatricesBuffer.Set(index, currentMatrix);
                    if (SkinPaletteBuffer is not null)
                    {
                        Matrix4x4 composed = ComposeSkinPaletteMatrix(index, currentMatrix);
                        DetectSkinPaletteExplosion(index, currentMatrix, composed);
                        SkinPaletteBuffer.Set(index, SkinPaletteMatrix.FromRowVectorMatrix(composed));
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
            if (BlendshapeWeights is null || BlendshapeActiveWeights is null || Mesh is null)
                return;

            uint blendshapeCount = Mesh.BlendshapeCount;
            int activeCount = 0;
            for (uint i = 0; i < blendshapeCount; i++)
            {
                float weight = BlendshapeWeights.GetFloat(i);
                if (!IsBlendshapeWeightActive(weight) || !IsBlendshapeAllowedByLod((int)i))
                    continue;

                BlendshapeActiveWeights.SetVector2((uint)activeCount, new Vector2(i, weight));
                activeCount++;
            }

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
            if (BlendshapeWeights is null)
                return;

            long blendshapeWeightBytes = 0L;
            if (_blendshapesInvalidated)
            {
                if (_blendshapeDirtyStartIndex != uint.MaxValue && _blendshapeDirtyEndIndex >= _blendshapeDirtyStartIndex)
                {
                    int offset = checked((int)(_blendshapeDirtyStartIndex * BlendshapeWeights.ElementSize));
                    uint length = checked((_blendshapeDirtyEndIndex - _blendshapeDirtyStartIndex + 1u) * BlendshapeWeights.ElementSize);
                    BlendshapeWeights.CommitDirtyBytes(checked((uint)offset), length);
                    blendshapeWeightBytes = length;
                }
                else
                {
                    BlendshapeWeights.CommitDirtyBytes(0u, BlendshapeWeights.Length);
                    blendshapeWeightBytes = BlendshapeWeights.Length;
                }

                _blendshapeDirtyStartIndex = uint.MaxValue;
                _blendshapeDirtyEndIndex = 0u;
                SetBlendshapesInvalidated(false);
            }

            long activeListBytes = 0L;
            if (_blendshapeActiveListInvalidated && BlendshapeActiveWeights is not null)
            {
                if (_activeBlendshapeCount > 0)
                {
                    uint activeElementCount = checked((uint)_activeBlendshapeCount);
                    BlendshapeActiveWeights.CommitDirtyElements(0u, activeElementCount);
                    activeListBytes = checked(activeElementCount * BlendshapeActiveWeights.ElementSize);
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
        {
            ResetMeshDeformBuffers();

            if (_meshDeformInfluences is null || DeformMeshRenderer?.Mesh is null || Mesh is null)
                return;

            PopulateMeshDeformBuffers();
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
        {
            _deformMeshRenderer = deformerRenderer;
            _meshDeformInfluences = influences;
            RebuildMeshDeformBuffers();
        }

        /// <summary>
        /// Clears mesh deformation, reverting to standard rendering.
        /// </summary>
        public void ClearMeshDeformation()
        {
            _deformMeshRenderer = null;
            _meshDeformInfluences = null;
            ResetMeshDeformBuffers();
        }

        private void PopulateMeshDeformBuffers()
        {
            if (_meshDeformInfluences is null || DeformMeshRenderer?.Mesh is null || Mesh is null)
                return;

            var deformerMesh = DeformMeshRenderer.Mesh;
            uint deformerVertexCount = (uint)deformerMesh.VertexCount;
            uint vertexCount = (uint)Mesh.VertexCount;

            ValidateMeshDeformConfiguration(vertexCount, deformerVertexCount);

            // Create deformer position buffers
            DeformerPositionsBuffer = new XRDataBuffer(
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

            DeformerRestPositionsBuffer = new XRDataBuffer(
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
                DeformerPositionsBuffer.SetVector4(i, new Vector4(pos, 1.0f));
                DeformerRestPositionsBuffer.SetVector4(i, new Vector4(pos, 1.0f));
            }

            if (TryCopySkinnedDeformerPositions(deformerMesh))
                _meshDeformInvalidated = true;

            Buffers.Add(DeformerPositionsBuffer.AttributeName, DeformerPositionsBuffer);
            Buffers.Add(DeformerRestPositionsBuffer.AttributeName, DeformerRestPositionsBuffer);

            // Create deformer normal buffer if mesh has normals
            if (Mesh.HasNormals && deformerMesh.HasNormals)
            {
                DeformerNormalsBuffer = new XRDataBuffer(
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
                    DeformerNormalsBuffer.SetVector4(i, new Vector4(nrm, 0.0f));
                }

                TryCopySkinnedDeformerNormals(deformerMesh);

                Buffers.Add(DeformerNormalsBuffer.AttributeName, DeformerNormalsBuffer);
            }

            // Create deformer tangent buffer if mesh has tangents
            if (Mesh.HasTangents && deformerMesh.HasTangents)
            {
                DeformerTangentsBuffer = new XRDataBuffer(
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
                    DeformerTangentsBuffer.SetVector4(i, deformerMesh.GetTangentWithSign(i));
                }

                TryCopySkinnedDeformerTangents(deformerMesh);

                Buffers.Add(DeformerTangentsBuffer.AttributeName, DeformerTangentsBuffer);
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
            MeshDeformVertexIndicesBuffer = new XRDataBuffer(
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

            MeshDeformVertexWeightsBuffer = new XRDataBuffer(
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
                    MeshDeformVertexIndicesBuffer.SetDataRawAtIndex(v, new IVector4((int)indices.X, (int)indices.Y, (int)indices.Z, (int)indices.W));
                }
                else
                {
                    MeshDeformVertexIndicesBuffer.SetDataRawAtIndex(v, indices);
                }
                MeshDeformVertexWeightsBuffer.SetDataRawAtIndex(v, weights);
            }

            Buffers.Add(MeshDeformVertexIndicesBuffer.AttributeName, MeshDeformVertexIndicesBuffer);
            Buffers.Add(MeshDeformVertexWeightsBuffer.AttributeName, MeshDeformVertexWeightsBuffer);
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
            MeshDeformIndicesBuffer = new XRDataBuffer(
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

            MeshDeformWeightsBuffer = new XRDataBuffer(
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
            MeshDeformVertexOffsetBuffer = new XRDataBuffer(
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

            MeshDeformVertexCountBuffer = new XRDataBuffer(
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
                    MeshDeformVertexOffsetBuffer.Set(v, (int)currentOffset);
                    MeshDeformVertexCountBuffer.Set(v, count);
                }
                else
                {
                    MeshDeformVertexOffsetBuffer.Set(v, (float)currentOffset);
                    MeshDeformVertexCountBuffer.Set(v, (float)count);
                }

                if (influences is not null)
                {
                    int writeIndex = 0;
                    for (int i = 0; i < influences.Length; i++)
                    {
                        if (!IsValidMeshDeformInfluence(influences[i], DeformerPositionsBuffer?.ElementCount ?? 0u))
                            continue;

                        MeshDeformIndicesBuffer.Set(currentOffset + (uint)writeIndex, influences[i].VertexIndex);
                        MeshDeformWeightsBuffer.Set(currentOffset + (uint)writeIndex, influences[i].Weight);
                        writeIndex++;
                    }
                }

                currentOffset += (uint)count;
            }

            Buffers.Add(MeshDeformIndicesBuffer.AttributeName, MeshDeformIndicesBuffer);
            Buffers.Add(MeshDeformWeightsBuffer.AttributeName, MeshDeformWeightsBuffer);
            Buffers.Add(MeshDeformVertexOffsetBuffer.AttributeName, MeshDeformVertexOffsetBuffer);
            Buffers.Add(MeshDeformVertexCountBuffer.AttributeName, MeshDeformVertexCountBuffer);
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
    }
}
