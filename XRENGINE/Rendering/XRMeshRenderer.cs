﻿using Extensions;
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
            InitializeDrivableBuffers();

            Versions.Add(0, new Version<DefaultVertexShaderGenerator>(this, NoSpecialExtensions, true));
            Versions.Add(1, new Version<OVRMultiViewVertexShaderGenerator>(this, HasOvrMultiView2, false));
            Versions.Add(2, new Version<NVStereoVertexShaderGenerator>(this, HasNVStereoViewRendering, false));
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
        public RenderBone[]? Bones => _bones;

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Mesh):
                    InitializeDrivableBuffers();
                    break;
            }
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
    }
}