using Extensions;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Shaders.Parameters;
using XREngine.Rendering.Shaders.Generator;
using static XREngine.Rendering.OpenGL.OpenGLRenderer;
using static XREngine.Rendering.XRMesh;

namespace XREngine.Rendering
{
    /// <summary>
    /// A mesh renderer is in charge of rendering one or more meshes with one or more materials.
    /// The API driver will optimize the rendering of these meshes as much as possible depending on how it's set up.
    /// </summary>
    public class XRMeshRenderer : XRAsset
    {
        /// <summary>
        /// This class holds specific information about rendering the mesh depending on the type of pass.
        /// For example:
        /// normal pass
        /// using OVR_multiview
        /// using NV_stereo_view_rendering
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
            public string? VertexShaderSource => _vertexShaderSource ??= GenerateVertexShaderSource();

            public bool AllowShaderPipelines
            {
                get => allowShaderPipelines;
                set => SetField(ref allowShaderPipelines, value);
            }

            public void ResetVertexShaderSource()
                => _vertexShaderSource = null;

            protected abstract string? GenerateVertexShaderSource();

            public delegate void DelRenderRequested(Matrix4x4 worldMatrix, XRMaterial? materialOverride, uint instances, EMeshBillboardMode billboardMode);
            /// <summary>
            /// Tells all renderers to render this mesh.
            /// </summary>
            public event DelRenderRequested? RenderRequested;

            /// <summary>
            /// Use this to render the mesh.
            /// </summary>
            /// <param name="modelMatrix"></param>
            /// <param name="materialOverride"></param>
            public void Render(Matrix4x4 modelMatrix, XRMaterial? materialOverride, uint instances, EMeshBillboardMode billboardMode)
                => RenderRequested?.Invoke(modelMatrix, materialOverride, instances, billboardMode);
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

        public Dictionary<int, BaseVersion> GeneratedVertexShaderVersions { get; set; } = [];

        /// <summary>
        /// Automatically selects the correct version of this mesh to render based on the current rendering state.
        /// </summary>
        /// <param name="forceNoStereo"></param>
        /// <returns></returns>
        private BaseVersion GetVersion(bool forceNoStereo = false)
        {
            bool stereoPass = !forceNoStereo && Engine.Rendering.State.IsStereoPass;

            BaseVersion ver;
            bool preferNV = Engine.Rendering.Settings.PreferNVStereo;
            if (stereoPass && preferNV && Engine.Rendering.State.IsNVIDIA)
                ver = GetNVStereoVersion();
            else if (stereoPass && Engine.Rendering.State.HasOvrMultiViewExtension)
                ver = GetOVRMultiViewVersion();
            else
                ver = GetDefaultVersion(); //Default version can still do VR - VRMode is set to 1 and uses VR geometry duplication shader during stereo pass

            return ver;
        }

        public BaseVersion GetDefaultVersion() => GeneratedVertexShaderVersions[0];
        public BaseVersion GetOVRMultiViewVersion() => GeneratedVertexShaderVersions[1];
        public BaseVersion GetNVStereoVersion() => GeneratedVertexShaderVersions[2];

        private static bool HasNVStereoViewRendering(XRShader x)
            => x.HasExtension(GLShader.EXT_GL_NV_STEREO_VIEW_RENDERING, XRShader.EExtensionBehavior.Require);
        private static bool HasOvrMultiView2(XRShader x)
            => x.HasExtension(GLShader.EXT_GL_OVR_MULTIVIEW2, XRShader.EExtensionBehavior.Require);
        private static bool NoSpecialExtensions(XRShader x) => 
            !x.HasExtension(GLShader.EXT_GL_OVR_MULTIVIEW2, XRShader.EExtensionBehavior.Require) && 
            !x.HasExtension(GLShader.EXT_GL_NV_STEREO_VIEW_RENDERING, XRShader.EExtensionBehavior.Require);

        public XRMeshRenderer() : this(null, null) { }
        public XRMeshRenderer(XRMesh? mesh, XRMaterial? material)
        {
            _mesh = mesh;
            _material = material;
            InitializeDrivableBuffers();
            InitializeVersions();
        }
        public XRMeshRenderer(params (XRMesh mesh, XRMaterial material)[] submeshes)
            : this((IEnumerable<(XRMesh mesh, XRMaterial material)>)submeshes) { }
        public XRMeshRenderer(IEnumerable<(XRMesh mesh, XRMaterial material)> submeshes)
        {
            foreach (var (mesh, material) in submeshes)
                Submeshes.Add(new SubMesh() { Mesh = mesh, Material = material, InstanceCount = 1 });
            InitializeDrivableBuffers();
            InitializeVersions();
        }

        private void InitializeVersions()
        {
            GeneratedVertexShaderVersions.Add(0, new Version<DefaultVertexShaderGenerator>(this, NoSpecialExtensions, true));
            GeneratedVertexShaderVersions.Add(1, new Version<OVRMultiViewVertexShaderGenerator>(this, HasOvrMultiView2, false));
            GeneratedVertexShaderVersions.Add(2, new Version<NVStereoVertexShaderGenerator>(this, HasNVStereoViewRendering, false));
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
        public event DelSetUniforms? SettingUniforms;

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

        private RenderBone[]? _bones;
        public RenderBone[]? Bones => _bones;

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
            => Render(Matrix4x4.Identity, materialOverride, instances);

        /// <summary>
        /// Use this to render the mesh.
        /// </summary>
        /// <param name="modelMatrix"></param>
        /// <param name="materialOverride"></param>
        public void Render(Matrix4x4 modelMatrix, XRMaterial? materialOverride = null, uint instances = 1u, bool forceNoStereo = false)
            => GetVersion(forceNoStereo).Render(modelMatrix, materialOverride, instances, Material?.BillboardMode ?? EMeshBillboardMode.None);

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
            _bones = null;

            BoneMatricesBuffer?.Destroy();
            BoneMatricesBuffer = null;

            BoneInvBindMatricesBuffer?.Destroy();
            BoneInvBindMatricesBuffer = null;

            BlendshapeWeights?.Destroy();
            BlendshapeWeights = null;
        }

        public BufferCollection Buffers { get; private set; } = [];

        /// <summary>
        /// All bone matrices for the mesh.
        /// Stream-write buffer.
        /// </summary>
        public XRDataBuffer? BoneMatricesBuffer { get; private set; }

        /// <summary>
        /// All bone inverse bind matrices for the mesh.
        /// </summary>
        public XRDataBuffer? BoneInvBindMatricesBuffer { get; private set; }

        /// <summary>
        /// All blendshape weights for the mesh.
        /// </summary>
        public XRDataBuffer? BlendshapeWeights { get; private set; }

        /// <summary>
        /// Indirect draw buffer for the mesh - renders multiple meshes with a single draw call.
        /// </summary>
        public XRDataBuffer? IndirectDrawBuffer { get; private set; }

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
            for (int i = 0; i < _bones.Length; i++)
            {
                var (tfm, invBindWorldMtx) = Mesh!.UtilizedBones[i];
                uint boneIndex = (uint)i + 1u;

                var rb = new RenderBone(tfm, invBindWorldMtx, boneIndex);
                rb.RenderTransformUpdated += BoneRenderTransformUpdated;
                _bones[i] = rb;

                BoneMatricesBuffer.Set(boneIndex, tfm.WorldMatrix);
                BoneInvBindMatricesBuffer.Set(boneIndex, invBindWorldMtx);
            }

            Buffers.Add(BoneMatricesBuffer.AttributeName, BoneMatricesBuffer);
            Buffers.Add(BoneInvBindMatricesBuffer.AttributeName, BoneInvBindMatricesBuffer);
        }

        private bool _bonesInvalidated = false;
        private bool _blendshapesInvalidated = false;

        private unsafe void BoneRenderTransformUpdated(RenderBone bone, Matrix4x4 renderMatrix)
        {
            if (BoneMatricesBuffer is null)
                return;

            //using var timer = Engine.Profiler.Start();
            //float* boneBuf = (float*)BoneMatricesBuffer.Address;
            //uint index = bone.Index * 16u;
            //boneBuf[index + 0] = bone.Transform.RenderMatrix.M11;
            //boneBuf[index + 1] = bone.Transform.RenderMatrix.M12;
            //boneBuf[index + 2] = bone.Transform.RenderMatrix.M13;
            //boneBuf[index + 3] = bone.Transform.RenderMatrix.M14;
            //boneBuf[index + 4] = bone.Transform.RenderMatrix.M21;
            //boneBuf[index + 5] = bone.Transform.RenderMatrix.M22;
            //boneBuf[index + 6] = bone.Transform.RenderMatrix.M23;
            //boneBuf[index + 7] = bone.Transform.RenderMatrix.M24;
            //boneBuf[index + 8] = bone.Transform.RenderMatrix.M31;
            //boneBuf[index + 9] = bone.Transform.RenderMatrix.M32;
            //boneBuf[index + 10] = bone.Transform.RenderMatrix.M33;
            //boneBuf[index + 11] = bone.Transform.RenderMatrix.M34;
            //boneBuf[index + 12] = bone.Transform.RenderMatrix.M41;
            //boneBuf[index + 13] = bone.Transform.RenderMatrix.M42;
            //boneBuf[index + 14] = bone.Transform.RenderMatrix.M43;
            //boneBuf[index + 15] = bone.Transform.RenderMatrix.M44;
            //if (bone.Transform.Name == "Hair_1_3")
            //    Debug.Out("");
            BoneMatricesBuffer?.Set(bone.Index, renderMatrix);
            _bonesInvalidated = true;
        }

        //TODO: use mapped buffer for constant streaming
        public void PushBoneMatricesToGPU()
        {
            if (BoneMatricesBuffer is null || !_bonesInvalidated)
                return;

            _bonesInvalidated = false;
            BoneMatricesBuffer.PushSubData();
        }
        public void PushBlendshapeWeightsToGPU()
        {
            if (BlendshapeWeights is null || !_blendshapesInvalidated)
                return;

            _blendshapesInvalidated = false;
            BlendshapeWeights.PushSubData();
        }

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
            => SettingUniforms?.Invoke(vertexProgram, materialProgram);

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