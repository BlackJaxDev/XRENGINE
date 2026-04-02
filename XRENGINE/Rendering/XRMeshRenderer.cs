using Extensions;
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
using static XREngine.Rendering.OpenGL.OpenGLRenderer;
using static XREngine.Rendering.XRMesh;

namespace XREngine.Rendering
{
    public enum EMeshGenerationPriority
    {
        Normal = 0,
        RenderPipeline = 1,
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
        private static int _settingsRevision;

        static XRMeshRenderer()
        {
            Engine.Rendering.SettingsChanged += () => Interlocked.Increment(ref _settingsRevision);
            Engine.Rendering.AntiAliasingSettingsChanged += () => Interlocked.Increment(ref _settingsRevision);
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

            public void ResetVertexShaderSource()
            {
                _vertexShaderSource = null;
                _vertexShaderSettingsRevision = -1;
            }

            protected abstract string? GenerateVertexShaderSource();

            protected bool FragmentConsumesTransformId()
            {
                var material = Parent?.Material;
                if (material?.Shaders is null)
                    return false;

                foreach (var shader in material.Shaders)
                {
                    if (shader is null || shader.Type != EShaderType.Fragment)
                        continue;

                    string? source = shader.Source?.Text;
                    if (string.IsNullOrEmpty(source))
                        continue;

                    if (source.Contains(DefaultVertexShaderGenerator.FragTransformIdName, StringComparison.Ordinal) ||
                        source.Contains("location = 21", StringComparison.Ordinal) ||
                        source.Contains("location=21", StringComparison.Ordinal))
                        return true;
                }

                return false;
            }

            public delegate void DelRenderRequested(Matrix4x4 worldMatrix, Matrix4x4 prevWorldMatrix, XRMaterial? materialOverride, uint instances, EMeshBillboardMode billboardMode);
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
            public void Render(Matrix4x4 modelMatrix, Matrix4x4 prevModelMatrix, XRMaterial? materialOverride, uint instances, EMeshBillboardMode billboardMode)
                => RenderRequested?.Invoke(modelMatrix, prevModelMatrix, materialOverride, instances, billboardMode);
        }

        public class Version<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(XRMeshRenderer renderer, Func<XRShader, bool> vertexShaderSelector, bool allowShaderPipelines) 
            : BaseVersion(renderer, vertexShaderSelector, allowShaderPipelines) where T : ShaderGeneratorBase
        {
            protected override string? GenerateVertexShaderSource()
            {
                var m = Parent?.Mesh;
                if (m is null)
                    return null;

                var gen = (T)Activator.CreateInstance(typeof(T), m)!;

                // Strip the FragTransformId output (location 21) when no paired fragment
                // shader declares it, avoiding SPIR-V interface mismatch validation warnings.
                if (gen is DefaultVertexShaderGenerator dvsg && !FragmentConsumesTransformId())
                {
                    dvsg.EmitTransformId = false;
                    dvsg.OutputVars.Remove(DefaultVertexShaderGenerator.FragTransformIdName);
                    dvsg.UniformNames.Remove("TransformId");
                }

                return gen.Generate();
            }
        }

        [MemoryPackIgnore]
        public Dictionary<int, BaseVersion> GeneratedVertexShaderVersions { get; set; } = [];

        /// <summary>
        /// Automatically selects the correct version of this mesh to render based on the current rendering state.
        /// </summary>
        /// <param name="forceNoStereo"></param>
        /// <returns></returns>
        private BaseVersion GetVersion(bool forceNoStereo = false)
        {
            bool stereoPass = !forceNoStereo && Engine.Rendering.State.IsStereoPass && CanUseVrSpecificVersions();
            bool useMeshDeform = DeformMeshRenderer is not null && _meshDeformInfluences is not null;

            BaseVersion ver;
            bool preferNV = Engine.Rendering.Settings.PreferNVStereo;
            bool hasNvMaterialVertexShader = MaterialHasMatchingVertexShader(HasNVStereoViewRendering);
            bool hasMultiViewMaterialVertexShader = MaterialHasMatchingVertexShader(HasMultiViewExtension);
            
            if (useMeshDeform)
            {
                // Use mesh deform versions
                if (stereoPass && Engine.Rendering.State.ForwardPlusEnabled && hasMultiViewMaterialVertexShader)
                    ver = GetMeshDeformOVRMultiViewVersion();
                else if (stereoPass && preferNV && Engine.Rendering.State.IsNVIDIA && hasNvMaterialVertexShader)
                    ver = GetMeshDeformNVStereoVersion();
                else if (stereoPass && hasMultiViewMaterialVertexShader)
                    ver = GetMeshDeformOVRMultiViewVersion();
                else if (stereoPass && hasNvMaterialVertexShader)
                    ver = GetMeshDeformNVStereoVersion();
                else if (stereoPass && preferNV && Engine.Rendering.State.IsNVIDIA)
                    ver = GetMeshDeformNVStereoVersion();
                else if (stereoPass && Engine.Rendering.State.HasAnyMultiViewExtension)
                    ver = GetMeshDeformOVRMultiViewVersion();
                else
                    ver = GetMeshDeformDefaultVersion();
            }
            else
            {
                // Use standard versions
                if (stereoPass && Engine.Rendering.State.ForwardPlusEnabled && hasMultiViewMaterialVertexShader)
                    ver = GetOVRMultiViewVersion();
                else if (stereoPass && preferNV && Engine.Rendering.State.IsNVIDIA && hasNvMaterialVertexShader)
                    ver = GetNVStereoVersion();
                else if (stereoPass && hasMultiViewMaterialVertexShader)
                    ver = GetOVRMultiViewVersion();
                else if (stereoPass && hasNvMaterialVertexShader)
                    ver = GetNVStereoVersion();
                else if (stereoPass && preferNV && Engine.Rendering.State.IsNVIDIA)
                    ver = GetNVStereoVersion();
                else if (stereoPass && Engine.Rendering.State.HasAnyMultiViewExtension)
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

        private static bool CanUseVrSpecificVersions()
            => Engine.VRState.IsInVR;

        public BaseVersion GetDefaultVersion() => GetOrCreateVersion(0);
        public BaseVersion GetOVRMultiViewVersion() => GetOrCreateVersion(1);
        public BaseVersion GetNVStereoVersion() => GetOrCreateVersion(2);

        public void EnsureRenderPipelineVersionsCreated()
        {
            _ = GetDefaultVersion();
            _ = GetOVRMultiViewVersion();
            _ = GetNVStereoVersion();
        }
        
        public BaseVersion GetMeshDeformDefaultVersion() => GetOrCreateVersion(3);
        public BaseVersion GetMeshDeformOVRMultiViewVersion() => GetOrCreateVersion(4);
        public BaseVersion GetMeshDeformNVStereoVersion() => GetOrCreateVersion(5);

        private static bool HasNVStereoViewRendering(XRShader x)
            => x.HasExtension(GLShader.EXT_GL_NV_STEREO_VIEW_RENDERING, XRShader.EExtensionBehavior.Require);
        private static bool HasMultiViewExtension(XRShader x)
            =>
                x.HasExtension(GLShader.EXT_GL_OVR_MULTIVIEW2, XRShader.EExtensionBehavior.Require) ||
                x.HasExtension(GLShader.EXT_GL_EXT_MULTIVIEW, XRShader.EExtensionBehavior.Require);
        private static bool NoSpecialExtensions(XRShader x) => 
            !x.HasExtension(GLShader.EXT_GL_OVR_MULTIVIEW2, XRShader.EExtensionBehavior.Require) && 
            !x.HasExtension(GLShader.EXT_GL_EXT_MULTIVIEW, XRShader.EExtensionBehavior.Require) &&
            !x.HasExtension(GLShader.EXT_GL_NV_STEREO_VIEW_RENDERING, XRShader.EExtensionBehavior.Require);

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
                return existing;

            BaseVersion created = versionKey switch
            {
                0 => new Version<DefaultVertexShaderGenerator>(this, NoSpecialExtensions, true),
                1 => new Version<OVRMultiViewVertexShaderGenerator>(this, HasMultiViewExtension, false),
                2 => new Version<NVStereoVertexShaderGenerator>(this, HasNVStereoViewRendering, false),
                3 => new MeshDeformVersion(this, NoSpecialExtensions, true),
                4 => new MeshDeformVersion(this, HasMultiViewExtension, false) { UseOVRMultiView = true },
                5 => new MeshDeformVersion(this, HasNVStereoViewRendering, false) { UseNVStereo = true },
                _ => throw new ArgumentOutOfRangeException(nameof(versionKey), versionKey, "Unknown mesh renderer shader version."),
            };

            GeneratedVertexShaderVersions.Add(versionKey, created);
            return created;
        }

        /// <summary>
        /// Specialized version for mesh deformation that passes deform parameters to the generator.
        /// </summary>
        public class MeshDeformVersion : BaseVersion
        {
            public bool UseOVRMultiView { get; set; }
            public bool UseNVStereo { get; set; }

            public MeshDeformVersion(XRMeshRenderer parent, Func<XRShader, bool> vertexShaderSelector, bool allowShaderPipelines)
                : base(parent, vertexShaderSelector, allowShaderPipelines) { }

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

                if (!FragmentConsumesTransformId())
                {
                    generator.EmitTransformId = false;
                    generator.OutputVars.Remove(MeshDeformVertexShaderGenerator.FragTransformIdName);
                    generator.UniformNames.Remove("TransformId");
                }

                return generator.Generate();
            }
        }

        private bool _generateAsync = false;
        /// <summary>
        /// If true, the mesh will be generated for rendering asynchronously.
        /// False by default.
        /// </summary>
        public bool GenerateAsync
        {
            get => _generateAsync;
            set => SetField(ref _generateAsync, value);
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
        private XREngine.Rendering.Models.SubMesh? _sourceSubMeshAsset;
        [MemoryPackIgnore]
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
        public void Render(XRMaterial? materialOverride = null, uint instances = 1u)
            => Render(Matrix4x4.Identity, Matrix4x4.Identity, materialOverride, instances);

        /// <summary>
        /// Use this to render the mesh.
        /// </summary>
        /// <param name="modelMatrix"></param>
        /// <param name="materialOverride"></param>
        public void Render(Matrix4x4 modelMatrix, Matrix4x4 prevModelMatrix, XRMaterial? materialOverride = null, uint instances = 1u, bool forceNoStereo = false)
            => GetVersion(forceNoStereo).Render(modelMatrix, prevModelMatrix, materialOverride, instances, Material?.BillboardMode ?? EMeshBillboardMode.None);

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

            BlendshapeWeights.SetFloat(index, weight);
            _blendshapesInvalidated = true;
        }

        private void InitializeDrivableBuffers()
        {
            ResetDrivableBuffers();

            if ((Mesh?.HasSkinning ?? false) && Engine.Rendering.Settings.AllowSkinning)
                PopulateBoneMatrixBuffers();

            if ((Mesh?.HasBlendshapes ?? false) && Engine.Rendering.Settings.AllowBlendshapes)
                PopulateBlendshapeWeightsBuffer();
        }

        /// <summary>
        /// Ensures skinning buffers (bone matrices) are initialized if the mesh has skinning data.
        /// Call this before attempting to bind GPU-driven bone palette shaders if there's a chance
        /// the buffers weren't created during renderer construction.
        /// </summary>
        /// <returns>True if BoneMatricesBuffer is available after this call; false otherwise.</returns>
        public bool EnsureSkinningBuffers()
        {
            if (BoneMatricesBuffer is not null)
                return true;

            if (Mesh is null)
            {
                Debug.LogWarning($"[XRMeshRenderer] EnsureSkinningBuffers: Mesh is null, cannot initialize skinning buffers. Renderer={GetHashCode():X}");
                return false;
            }

            if (!Mesh.HasSkinning)
            {
                Debug.LogWarning($"[XRMeshRenderer] EnsureSkinningBuffers: Mesh '{Mesh.Name}' has no skinning data. Renderer={GetHashCode():X}");
                return false;
            }

            if (!Engine.Rendering.Settings.AllowSkinning)
            {
                Debug.LogWarning($"[XRMeshRenderer] EnsureSkinningBuffers: Skinning is disabled in render settings. Mesh='{Mesh.Name}', Renderer={GetHashCode():X}");
                return false;
            }

            Debug.LogWarning($"[XRMeshRenderer] EnsureSkinningBuffers: BoneMatricesBuffer was null, initializing late. This may indicate a timing issue with mesh/renderer creation. Mesh='{Mesh.Name}', UtilizedBones={Mesh.UtilizedBones?.Length ?? 0}, Renderer={GetHashCode():X}");
            PopulateBoneMatrixBuffers();

            if (BoneMatricesBuffer is null)
            {
                Debug.LogWarning($"[XRMeshRenderer] EnsureSkinningBuffers: PopulateBoneMatrixBuffers did not create buffer. Mesh='{Mesh.Name}', Renderer={GetHashCode():X}");
                return false;
            }

            return true;
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
        }

        private void ResetDrivableBuffers()
        {
            if (_boneByTransform is not null)
            {
                foreach (var pair in _boneByTransform)
                    pair.Key.RenderMatrixChanged -= BoneTransformRenderMatrixChanged;
                _boneByTransform.Clear();
            }
            _dirtyBoneIndices?.Clear();
            _dirtyBoneFlags = null;
            _dirtyBoneMatrices = null;
            _gpuDrivenBoneRefCounts = null;
            _gpuDrivenBoneCount = 0;

            _bones = null;

            BoneMatricesBuffer?.Destroy();
            BoneMatricesBuffer = null;

            BoneInvBindMatricesBuffer?.Destroy();
            BoneInvBindMatricesBuffer = null;

            BlendshapeWeights?.Destroy();
            BlendshapeWeights = null;

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

        [MemoryPackIgnore]
        public XRDataBuffer? ActiveBoneMatricesBuffer => BoneMatricesBuffer;

        /// <summary>
        /// All bone inverse bind matrices for the mesh.
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? BoneInvBindMatricesBuffer { get; private set; }

        [MemoryPackIgnore]
        public XRDataBuffer? ActiveBoneInvBindMatricesBuffer => BoneInvBindMatricesBuffer;

        [MemoryPackIgnore]
        public uint ActiveBoneMatrixBase => 0u;

        [MemoryPackIgnore]
        public uint ActiveBoneMatrixCount => (uint)(Mesh?.UtilizedBones?.Length ?? 0) + 1u;

        [MemoryPackIgnore]
        public bool HasGpuDrivenBoneSource => _gpuDrivenBoneCount > 0;

        /// <summary>
        /// All blendshape weights for the mesh.
        /// </summary>
        [MemoryPackIgnore]
        public XRDataBuffer? BlendshapeWeights { get; private set; }

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
            //using var timer = Engine.Profiler.Start();

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

            BoneMatricesBuffer.Set(0, Matrix4x4.Identity);
            BoneInvBindMatricesBuffer.Set(0, Matrix4x4.Identity);

            _bones = new RenderBone[boneCount];
            _boneByTransform = new Dictionary<TransformBase, RenderBone>((int)boneCount);
            _dirtyBoneIndices = new List<uint>((int)boneCount);
            _dirtyBoneFlags = new bool[boneCount + 1];
            _dirtyBoneMatrices = new Matrix4x4[boneCount + 1];
            _gpuDrivenBoneRefCounts = new int[boneCount + 1];
            for (int i = 0; i < _bones.Length; i++)
            {
                var (tfm, invBindWorldMtx) = Mesh!.UtilizedBones[i];
                uint boneIndex = (uint)i + 1u;

                var rb = new RenderBone(tfm, invBindWorldMtx, boneIndex);
                _boneByTransform[tfm] = rb;
                tfm.RenderMatrixChanged += BoneTransformRenderMatrixChanged;
                _bones[i] = rb;

                BoneMatricesBuffer.Set(boneIndex, tfm.WorldMatrix);
                BoneInvBindMatricesBuffer.Set(boneIndex, invBindWorldMtx);
            }

            Buffers.Add(BoneMatricesBuffer.AttributeName, BoneMatricesBuffer);
            Buffers.Add(BoneInvBindMatricesBuffer.AttributeName, BoneInvBindMatricesBuffer);
        }

        private bool _bonesInvalidated = false;
        private bool _blendshapesInvalidated = false;
    private int[]? _gpuDrivenBoneRefCounts;
    private int _gpuDrivenBoneCount;

        private void BoneTransformRenderMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
        {
            if (BoneMatricesBuffer is null)
                return;

            if (_boneByTransform is null || !_boneByTransform.TryGetValue(transform, out var bone))
                return;

            if (_dirtyBoneFlags is null || _dirtyBoneIndices is null || _dirtyBoneMatrices is null)
                return;

            int index = (int)bone.Index;
            if (IsBoneGpuDriven(index))
                return;

            _dirtyBoneMatrices[index] = renderMatrix;
            if (!_dirtyBoneFlags[index])
            {
                _dirtyBoneFlags[index] = true;
                _dirtyBoneIndices.Add(bone.Index);
            }
            _bonesInvalidated = true;
        }

        //TODO: use mapped buffer for constant streaming
        public void PushBoneMatricesToGPU()
        {
            if (BoneMatricesBuffer is null || !_bonesInvalidated)
                return;

            if (_dirtyBoneIndices is null || _dirtyBoneFlags is null || _dirtyBoneMatrices is null)
                return;

            _bonesInvalidated = false;

            if (_dirtyBoneIndices.Count == 0)
                return;

            foreach (var index in _dirtyBoneIndices)
            {
                int i = (int)index;
                BoneMatricesBuffer.Set(index, _dirtyBoneMatrices[i]);
                _dirtyBoneFlags[i] = false;
            }

            PushDirtyBoneRanges(BoneMatricesBuffer, _dirtyBoneIndices);
            _dirtyBoneIndices.Clear();
        }

        private static void PushDirtyBoneRanges(XRDataBuffer buffer, List<uint> dirtyBoneIndices)
        {
            uint elementSize = buffer.ElementSize;
            if (elementSize == 0)
            {
                buffer.PushSubData();
                return;
            }

            dirtyBoneIndices.Sort();

            uint rangeStart = dirtyBoneIndices[0];
            uint previous = rangeStart;
            for (int i = 1; i < dirtyBoneIndices.Count; ++i)
            {
                uint current = dirtyBoneIndices[i];
                if (current == previous + 1u)
                {
                    previous = current;
                    continue;
                }

                PushDirtyBoneRange(buffer, rangeStart, previous, elementSize);
                rangeStart = current;
                previous = current;
            }

            PushDirtyBoneRange(buffer, rangeStart, previous, elementSize);
        }

        private static void PushDirtyBoneRange(XRDataBuffer buffer, uint startIndex, uint endIndex, uint elementSize)
        {
            int byteOffset = checked((int)(startIndex * elementSize));
            uint byteLength = checked((endIndex - startIndex + 1u) * elementSize);
            buffer.PushSubData(byteOffset, byteLength);
        }

        internal void RegisterGpuDrivenBoneIndices(IReadOnlyList<uint> boneIndices)
        {
            if (_gpuDrivenBoneRefCounts is null || boneIndices.Count == 0)
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

        internal void UnregisterGpuDrivenBoneIndices(IReadOnlyList<uint> boneIndices)
        {
            if (_gpuDrivenBoneRefCounts is null || boneIndices.Count == 0)
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

        private bool IsBoneGpuDriven(int index)
            => _gpuDrivenBoneRefCounts is not null
                && index >= 0
                && index < _gpuDrivenBoneRefCounts.Length
                && _gpuDrivenBoneRefCounts[index] > 0;

        private void ClearDirtyBoneIndex(int index)
        {
            if (_dirtyBoneFlags is null || _dirtyBoneIndices is null)
                return;

            if (index < 0 || index >= _dirtyBoneFlags.Length || !_dirtyBoneFlags[index])
                return;

            _dirtyBoneFlags[index] = false;
            _dirtyBoneIndices.Remove((uint)index);
            _bonesInvalidated = _dirtyBoneIndices.Count > 0;
        }

        public void PushBlendshapeWeightsToGPU()
        {
            if (BlendshapeWeights is null || !_blendshapesInvalidated)
                return;

            _blendshapesInvalidated = false;
            BlendshapeWeights.PushSubData();
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
                Engine.Rendering.Settings.UseIntegerUniformsInShaders ? EComponentType.Int : EComponentType.Float,
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

                if (Engine.Rendering.Settings.UseIntegerUniformsInShaders)
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
                Engine.Rendering.Settings.UseIntegerUniformsInShaders ? EComponentType.Int : EComponentType.Float,
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
                Engine.Rendering.Settings.UseIntegerUniformsInShaders ? EComponentType.Int : EComponentType.Float,
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

                if (Engine.Rendering.Settings.UseIntegerUniformsInShaders)
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
            var renderState = Engine.Rendering.State.RenderingPipelineState;
            renderState?.ApplyScopedProgramBindings(vertexProgram);

            if (!ReferenceEquals(vertexProgram, materialProgram))
                renderState?.ApplyScopedProgramBindings(materialProgram);

            _settingUniforms?.Invoke(vertexProgram, materialProgram);
        }

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