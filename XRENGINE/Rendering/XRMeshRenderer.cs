using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using XREngine.Core.Files;
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
    /// A mesh renderer takes a mesh and a material and renders it.
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

        public class Version<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(XRMeshRenderer renderer, Func<XRShader, bool> vertexShaderSelector, bool allowShaderPipelines) : BaseVersion(renderer, vertexShaderSelector, allowShaderPipelines) where T : ShaderGeneratorBase
        {
            protected override string? GenerateVertexShaderSource()
            {
                var m = Parent?.Mesh;
                if (m is null)
                    return null;

                return ((T)Activator.CreateInstance(typeof(T), m)!).Generate();
            }
        }

        public Dictionary<int, BaseVersion> Versions { get; set; } = [];

        public BaseVersion GetDefaultVersion() => Versions[0];
        public BaseVersion GetOVRMultiViewVersion() => Versions[1];
        public BaseVersion GetNVStereoVersion() => Versions[2];

        private static bool HasNVStereoViewRendering(XRShader x)
            => x.HasExtension(GLShader.EXT_GL_NV_STEREO_VIEW_RENDERING, XRShader.EExtensionBehavior.Require);
        private static bool HasOvrMultiView2(XRShader x)
            => x.HasExtension(GLShader.EXT_GL_OVR_MULTIVIEW2, XRShader.EExtensionBehavior.Require);
        private static bool NoSpecialExtensions(XRShader x) => 
            !x.HasExtension(GLShader.EXT_GL_OVR_MULTIVIEW2, XRShader.EExtensionBehavior.Require) && 
            !x.HasExtension(GLShader.EXT_GL_NV_STEREO_VIEW_RENDERING, XRShader.EExtensionBehavior.Require);

        public XRMeshRenderer(XRMesh? mesh, XRMaterial? material)
        {
            _mesh = mesh;
            _material = material;
            ReinitializeBones();

            Versions.Add(0, new Version<DefaultVertexShaderGenerator>(this, NoSpecialExtensions, true));
            Versions.Add(1, new Version<OVRMultiViewVertexShaderGenerator>(this, HasOvrMultiView2, false));
            Versions.Add(2, new Version<NVStereoVertexShaderGenerator>(this, HasNVStereoViewRendering, false));
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

        private BaseVersion GetVersion(bool forceNoStereo = false)
        {
            bool stereoPass = !forceNoStereo && Engine.Rendering.State.IsStereoPass;

            BaseVersion ver;
            bool preferNV = Engine.Rendering.Settings.PreferNVStereo;
            if (stereoPass && preferNV && Engine.Rendering.State.HasNVStereoExtension)
                ver = GetNVStereoVersion();
            else if (stereoPass && Engine.Rendering.State.HasOvrMultiViewExtension)
                ver = GetOVRMultiViewVersion();
            else
                ver = GetDefaultVersion(); //Default version can still do VR - VRMode is set to 1 and uses VR geometry duplication shader during stereo pass

            return ver;
        }

        private XRMesh? _mesh;
        private XRMaterial? _material;

        public class SubMesh : XRBase
        {
            private XRMesh? _mesh;
            private XRMaterial? _material;

            public XRMesh? Mesh
            {
                get => _mesh;
                set => SetField(ref _mesh, value);
            }
            public XRMaterial? Material
            {
                get => _material;
                set => SetField(ref _material, value);
            }
        }

        public SubMesh[] Submeshes { get; set; } = [];

        public XRMesh? Mesh 
        {
            get => _mesh;
            set => SetField(ref _mesh, value);
        }
        public XRMaterial? Material
        {
            get => _material;
            set => SetField(ref _material, value);
        }

        private RenderBone[]? _bones;
        //private ConcurrentDictionary<uint, Matrix4x4> _modifiedBonesRendering = [];
        //private ConcurrentDictionary<uint, Matrix4x4> _modifiedBonesUpdating = [];

        private void ReinitializeBones()
        {
            //using var timer = Engine.Profiler.Start();

            ResetBoneInfo();

            if ((Mesh?.HasSkinning ?? false) && Engine.Rendering.Settings.AllowSkinning)
            {
                PopulateBoneMatrixBuffers();
                //Engine.Time.Timer.SwapBuffers += SwapBuffers;
            }
        }

        private void ResetBoneInfo()
        {
            //Engine.Time.Timer.SwapBuffers -= SwapBuffers;
            _bones = null;
            //SingleBind = null;
            BoneMatricesBuffer?.Destroy();
            BoneMatricesBuffer = null;
            BoneInvBindMatricesBuffer?.Destroy();
            BoneInvBindMatricesBuffer = null;
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

        private void PopulateBoneMatrixBuffers()
        {
            //using var timer = Engine.Profiler.Start();

            uint boneCount = (uint)(Mesh?.UtilizedBones?.Length ?? 0);

            BoneMatricesBuffer = new($"{ECommonBufferType.BoneMatrices}Buffer", EBufferTarget.ShaderStorageBuffer, boneCount + 1, EComponentType.Float, 16, false, false)
            {
                //RangeFlags = EBufferMapRangeFlags.Write | EBufferMapRangeFlags.Persistent | EBufferMapRangeFlags.Coherent;
                //StorageFlags = EBufferMapStorageFlags.Write | EBufferMapStorageFlags.Persistent | EBufferMapStorageFlags.Coherent | EBufferMapStorageFlags.ClientStorage;
                Usage = EBufferUsage.StreamDraw
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
                rb.TransformUpdated += BoneTransformUpdated;
                _bones[i] = rb;

                BoneMatricesBuffer.Set(boneIndex, tfm.WorldMatrix);
                BoneInvBindMatricesBuffer.Set(boneIndex, invBindWorldMtx);
            }

            Buffers.Add(BoneMatricesBuffer.BindingName, BoneMatricesBuffer);
            Buffers.Add(BoneInvBindMatricesBuffer.BindingName, BoneInvBindMatricesBuffer);
        }

        private bool _bonesInvalidated = false;
        private void BoneTransformUpdated(RenderBone bone)
        {
            BoneMatricesBuffer?.Set(bone.Index, bone.Transform.WorldMatrix);
            _bonesInvalidated = true;
            //This swapping method seems to be overkill, just set directly to the buffer
            //_modifiedBonesUpdating.AddOrUpdate(bone.Index, x => bone.Transform.WorldMatrix, (x, y) => bone.Transform.WorldMatrix);
        }

        //public void SwapBuffers()
        //{
        //    //(_modifiedBonesRendering, _modifiedBonesUpdating) = (_modifiedBonesUpdating, _modifiedBonesRendering);
        //    //_modifiedBonesUpdating.Clear();
        //}

        //TODO: use mapped buffer for constant streaming
        public void PushBoneMatricesToGPU()
        {
            if (BoneMatricesBuffer is null || !_bonesInvalidated)
                return;

            ////TODO: what's faster, pushing sub data per matrix, or pushing all? or mapping?
            //foreach (var bone in _modifiedBonesRendering)
            //{
            //    BoneMatricesBuffer.Set(bone.Key, bone.Value);

            //    //This doesn't work, and I don't know why
            //    //var elemSize = BoneMatricesBuffer.ElementSize;
            //    //BoneMatricesBuffer.PushSubData((int)(bone.Key * elemSize), elemSize);
            //}

            _bonesInvalidated = false;
            BoneMatricesBuffer.PushSubData();
        }

        private bool _generateAsync = false;
        public bool GenerateAsync
        {
            get => _generateAsync;
            set => SetField(ref _generateAsync, value);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Mesh):
                    ReinitializeBones();
                    break;
            }
        }

        public delegate void DelSetUniforms(XRRenderProgram vertexProgram, XRRenderProgram materialProgram);
        /// <summary>
        /// Subscribe to this event to send your own uniforms to the material.
        /// </summary>
        public event DelSetUniforms? SettingUniforms;

        public delegate ShaderVar DelParameterRequested(int index);

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
    }
}