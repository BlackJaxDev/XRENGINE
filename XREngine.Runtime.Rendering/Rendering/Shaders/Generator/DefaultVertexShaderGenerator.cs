using System;
using XREngine.Extensions;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Shaders.Generator
{
    public class OVRMultiViewVertexShaderGenerator(XRMesh mesh) : DefaultVertexShaderGenerator(mesh)
    {
        public override bool UseOVRMultiView => true;
    }
    public class NVStereoVertexShaderGenerator(XRMesh mesh) : DefaultVertexShaderGenerator(mesh)
    {
        public override bool UseNVStereo => true;
    }
    public class DirectionalCascadeInstancedVertexShaderGenerator(XRMesh mesh) : DefaultVertexShaderGenerator(mesh)
    {
        public override bool UseDirectionalCascadeInstancedLayering => true;
    }
    public class DirectionalCascadeAtlasInstancedVertexShaderGenerator(XRMesh mesh) : DefaultVertexShaderGenerator(mesh)
    {
        public override bool UseDirectionalCascadeInstancedLayering => true;
        public override bool UseDirectionalCascadeAtlasInstancedLayering => true;
    }
    public class PointLightInstancedVertexShaderGenerator(XRMesh mesh) : DefaultVertexShaderGenerator(mesh)
    {
        public override bool UsePointLightInstancedLayering => true;
    }
    public class PointLightAtlasInstancedVertexShaderGenerator(XRMesh mesh) : DefaultVertexShaderGenerator(mesh)
    {
        public override bool UsePointLightInstancedLayering => true;
        public override bool UsePointLightAtlasInstancedLayering => true;
    }
    /// <summary>
    /// Generates a typical vertex shader for use with most models.
    /// </summary>
    public class DefaultVertexShaderGenerator : ShaderGeneratorBase
    {
        private const uint MaxVertexAttribs = 16;

        private bool _useNormals;
        private bool _useTangents;
        private bool _useSkinningInputs;
        private bool _useBlendshapeInput;
        private int _texCoordsUsed;
        private int _colorsUsed;

        public DefaultVertexShaderGenerator(XRMesh mesh) : base(mesh)
        {
            HelperMethodWriters.Add(WriteAdjointMethod);

            ComputeAttributeBudget();
            AddUniforms();
            AddOutputs();
            AddInputs();
        }

        private void ComputeAttributeBudget()
        {
            uint location = 0u; //Reserve locations in the same order we will emit attributes

            // Position is mandatory
            location++;

            _useNormals = Mesh.HasNormals && location < MaxVertexAttribs;
            if (_useNormals)
                location++;

            _useTangents = Mesh.HasTangents && location < MaxVertexAttribs;
            if (_useTangents)
                location++;

            bool needSkinning = Mesh.HasSkinning && RuntimeEngine.Rendering.Settings.AllowSkinning && !UseComputeSkinning;
            uint skinningSlots = needSkinning ? 2u : 0u; //Both code paths consume two attributes

            if (needSkinning)
            {
                //Make room by dropping lower-priority tangents/normals if needed
                if (location + skinningSlots > MaxVertexAttribs && _useTangents)
                {
                    _useTangents = false;
                    location--;
                }
                if (location + skinningSlots > MaxVertexAttribs && _useNormals)
                {
                    _useNormals = false;
                    location--;
                }

                _useSkinningInputs = location + skinningSlots <= MaxVertexAttribs;
                if (_useSkinningInputs)
                    location += skinningSlots;
            }
            else
                _useSkinningInputs = false;

            bool needBlendshapeAttr = Mesh.BlendshapeCount > 0 && !UseComputeBlendshapes && RuntimeEngine.Rendering.Settings.AllowBlendshapes;
            _useBlendshapeInput = needBlendshapeAttr && location < MaxVertexAttribs;
            if (_useBlendshapeInput)
                location++;

            uint remaining = MaxVertexAttribs - location;

            _texCoordsUsed = Mesh.HasTexCoords ? (int)Math.Min(remaining, (uint)Mesh.TexCoordCount) : 0;
            location += (uint)_texCoordsUsed;
            remaining = MaxVertexAttribs - location;

            _colorsUsed = Mesh.HasColors ? (int)Math.Min(remaining, (uint)Mesh.ColorCount) : 0;
        }

        private void AddInputs()
        {
            uint location = 0u;

            InputVars.Add(ECommonBufferType.Position.ToString(), (location++, EShaderVarType._vec3));

            if (_useNormals)
                InputVars.Add(ECommonBufferType.Normal.ToString(), (location++, EShaderVarType._vec3));

            if (_useTangents)
                InputVars.Add(ECommonBufferType.Tangent.ToString(), (location++, EShaderVarType._vec4));

            if (_useSkinningInputs && Mesh.HasSkinning && RuntimeEngine.Rendering.Settings.AllowSkinning && !UseComputeSkinning)
            {
                InputVars.Add(ECommonBufferType.BoneInfluenceCoreIndices.ToString(), (location++, EShaderVarType._uvec4));
                InputVars.Add(ECommonBufferType.BoneInfluenceCoreWeights.ToString(), (location++, EShaderVarType._vec4));
            }

            if (_texCoordsUsed > 0)
                for (uint i = 0; i < _texCoordsUsed; ++i)
                    InputVars.Add($"{ECommonBufferType.TexCoord}{i}", (location++, EShaderVarType._vec2));

            if (_colorsUsed > 0)
                for (uint i = 0; i < _colorsUsed; ++i)
                    InputVars.Add($"{ECommonBufferType.Color}{i}", (location++, EShaderVarType._vec4));

            if (_useBlendshapeInput)
            {
                EShaderVarType intVarType = RuntimeEngine.Rendering.Settings.UseIntegerUniformsInShaders
                    ? EShaderVarType._ivec2
                    : EShaderVarType._vec2;

                InputVars.Add(ECommonBufferType.BlendshapeCount.ToString(), (location++, intVarType));
            }
        }

        private void AddOutputs()
        {
            OutputVars.Add(FragPosName, (0, EShaderVarType._vec3));

            // Always provide FragNorm to satisfy fragment inputs expecting it
            OutputVars.Add(FragNormName, (1, EShaderVarType._vec3));

            if (_useTangents)
            {
                OutputVars.Add(FragTanName, (2, EShaderVarType._vec3));
                OutputVars.Add(FragBinormName, (3, EShaderVarType._vec3)); //Binormal is created in vertex shader if tangents exist
            }

            OutputVars.Add(string.Format(FragUVName, 0), (4, EShaderVarType._vec2));
            if (_texCoordsUsed > 1)
                for (int i = 1; i < _texCoordsUsed.ClampMax(8); ++i)
                    OutputVars.Add(string.Format(FragUVName, i), (4u + (uint)i, EShaderVarType._vec2));

            OutputVars.Add(string.Format(FragColorName, 0), (12, EShaderVarType._vec4));
            if (_colorsUsed > 1)
                for (int i = 1; i < _colorsUsed.ClampMax(8); ++i)
                    OutputVars.Add(string.Format(FragColorName, i), (12u + (uint)i, EShaderVarType._vec4));

            OutputVars.Add(FragPosLocalName, (20, EShaderVarType._vec3)); //Local position in model space

            // A per-draw identifier used by deferred passes that need object/transform stability.
            // Stored as float bits to avoid requiring 'flat' interpolation qualifiers for integer varyings.
            // Only emitted when the paired fragment shader actually consumes the varying.
            if (EmitTransformId)
                OutputVars.Add(FragTransformIdName, (21, EShaderVarType._float));

            OutputVars.Add(FragViewIndexName, (22, EShaderVarType._float));
        }

        private void AddUniforms()
        {
            UniformNames.Add(EEngineUniform.ModelMatrix.ToStringFast(), (EShaderVarType._mat4, false));

            if (Mesh.HasSkinning && RuntimeEngine.Rendering.Settings.AllowSkinning && !UseComputeSkinning)
            {
                UniformNames.Add("skinPaletteBase", (EShaderVarType._uint, false));
                UniformNames.Add("skinPaletteCount", (EShaderVarType._uint, false));
            }

            // Used when gl_BaseInstance is 0 (non-indirect draw path).
            if (EmitTransformId)
                UniformNames.Add("TransformId", (EShaderVarType._uint, false));

            if (UseOVRMultiView || UseNVStereo)
            {
                UniformNames.Add($"{EEngineUniform.LeftEyeInverseViewMatrix}{VertexUniformSuffix}", (EShaderVarType._mat4, false));
                UniformNames.Add($"{EEngineUniform.RightEyeInverseViewMatrix}{VertexUniformSuffix}", (EShaderVarType._mat4, false));
                UniformNames.Add($"{EEngineUniform.LeftEyeProjMatrix}{VertexUniformSuffix}", (EShaderVarType._mat4, false));
                UniformNames.Add($"{EEngineUniform.RightEyeProjMatrix}{VertexUniformSuffix}", (EShaderVarType._mat4, false));
            }
            else
            {
                // ViewMatrix is the actual view matrix (InverseRenderMatrix from camera transform).
                // InverseViewMatrix is the camera's world transform (RenderMatrix), kept for compatibility.
                // Using ViewMatrix directly avoids single-precision inverse() computation in shader,
                // which can cause motion vector precision issues for distant objects.
                UniformNames.Add($"{EEngineUniform.ViewMatrix}{VertexUniformSuffix}", (EShaderVarType._mat4, false));
                UniformNames.Add($"{EEngineUniform.InverseViewMatrix}{VertexUniformSuffix}", (EShaderVarType._mat4, false));
                UniformNames.Add($"{EEngineUniform.ProjMatrix}{VertexUniformSuffix}", (EShaderVarType._mat4, false));
            }

            if (Mesh.SupportsBillboarding)
                UniformNames.Add(EEngineUniform.BillboardMode.ToStringFast(), (EShaderVarType._int, false));

            if (!UseOVRMultiView && !UseNVStereo) //Include toggle for manual stereo VR calculations in shader if not using OVR multi-view or NV stereo
                UniformNames.Add(EEngineUniform.VRMode.ToStringFast(), (EShaderVarType._bool, false));
        }

        //Buffers leaving the vertex shader for each vertex
        public const string FragPosLocalName = "FragPosLocal";
        public const string FragTransformIdName = "FragTransformId";
        public const string FragViewIndexName = "FragViewIndex";
        public const string FragPosName = "FragPos";
        public const string FragNormName = "FragNorm";
        public const string FragTanName = "FragTan";
        public const string FragBinormName = "FragBinorm"; //Binormal is created in vertex shader if tangents exist
        public const string FragColorName = "FragColor{0}";
        public const string FragUVName = "FragUV{0}";

        public const string BasePositionName = "basePosition";
        public const string BaseNormalName = "baseNormal";
        public const string BaseTangentName = "baseTangent";

        public const string FinalPositionName = "finalPosition";
        public const string FinalNormalName = "finalNormal";
        public const string FinalTangentName = "finalTangent";
        public const string FinalBinormalName = "finalBinormal";

        /// <summary>
        /// When <c>true</c>, the generated vertex shader emits <c>FragTransformId</c> at location 21
        /// and the corresponding <c>TransformId</c> uniform. Set to <c>false</c> when the paired
        /// fragment shader does not consume this varying, avoiding Vulkan SPIR-V interface warnings.
        /// </summary>
        public bool EmitTransformId { get; set; } = true;

        public virtual bool UseOVRMultiView => false;
        public virtual bool UseNVStereo => false;
        public virtual bool UseDirectionalCascadeInstancedLayering => false;
        public virtual bool UseDirectionalCascadeAtlasInstancedLayering => false;
        public virtual bool UsePointLightInstancedLayering => false;
        public virtual bool UsePointLightAtlasInstancedLayering => false;

        private bool UseComputeSkinning => RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader && Mesh.HasSkinning && RuntimeEngine.Rendering.Settings.AllowSkinning;
        private bool UseComputeBlendshapes => Mesh.BlendshapeCount > 0
            && RuntimeEngine.Rendering.Settings.AllowBlendshapes
            && (RuntimeEngine.Rendering.Settings.CalculateBlendshapesInComputeShader || UseComputeSkinning);
        private bool UseComputeDeformation => UseComputeSkinning || UseComputeBlendshapes;
        private bool UseExplicitRowVectorSkinningConvention => Mesh.SkinningShaderConvention == ESkinningShaderConvention.ExplicitRowMajorRowVector;

        private const int ComputeInterleavedBinding = 9;
        private const int ComputePositionBinding = 11;
        private const int ComputeNormalBinding = 12;
        private const int ComputeTangentBinding = 15;

        private const string ComputeInterleavedBufferName = "SkinnedInterleaved";
        private const string ComputePositionBufferName = "SkinnedPositions";
        private const string ComputeNormalBufferName = "SkinnedNormals";
        private const string ComputeTangentBufferName = "SkinnedTangents";

        private const string ViewMatrixName = "ViewMatrix";
        private const string ModelViewMatrixName = "mvMatrix";
        private const string ModelViewProjMatrixName = "mvpMatrix";
        private const string ViewProjMatrixName = "vpMatrix";
        private const string NormalMatrixName = "normalMatrix";

        protected override void WriteMain()
        {
            //Normal matrix is used to transform normals, tangents, and binormals in mesh transform calculations
            if (_useNormals)
            {
                Line($"mat3 {NormalMatrixName} = adjoint({EEngineUniform.ModelMatrix});");
                Line();
            }

            //Transform position, normals and tangents
            WriteMeshTransforms(_useSkinningInputs && Mesh.HasSkinning && RuntimeEngine.Rendering.Settings.AllowSkinning && !UseComputeSkinning);
            if (UseNVStereo)
                Line("gl_Layer = 0;");

            // Ensure FragNorm is always initialized to a sensible default when normals are absent
            if (!_useNormals)
                Line($"{FragNormName} = vec3(0.0f, 0.0f, 1.0f);");

            if (_colorsUsed != 0)
                for (int i = 0; i < _colorsUsed.ClampMax(8); ++i)
                    Line($"{string.Format(FragColorName, i)} = {ECommonBufferType.Color}{i};");
            else
                Line($"{string.Format(FragColorName, 0)} = vec4(1.0f);");

            if (_texCoordsUsed != 0)
                for (int i = 0; i < _texCoordsUsed.ClampMax(8); ++i)
                    Line($"{string.Format(FragUVName, i)} = {ECommonBufferType.TexCoord}{i};");
            else
                Line($"{string.Format(FragUVName, 0)} = vec2(0.0f);");

            // FragTransformId is forwarded to the fragment shader so a single FS can be paired
            // with either VS variant: the CPU-direct VS (this generator, using the ModelMatrix
            // uniform) or the GPU-indirect substitute VS built by
            // HybridRenderingManager.CreateGpuIndirectVertexShader, which reads the world matrix
            // from scene.TransformBuffer via DrawMetadata[gl_BaseInstance].TransformID. The FS
            // uses FragTransformId for material / draw-metadata lookups in both paths.
            // CPU-direct: gl_BaseInstance is 0, so we fall back to the per-draw TransformId
            // uniform set by the CPU. GPU-indirect: HybridRenderingManager substitutes this VS
            // entirely, so this branch is not the indirect transform-fetch site.
            if (EmitTransformId)
            {
                Line("uint _xreTransformId = uint(gl_BaseInstance);");
                Line("if (_xreTransformId == 0u) _xreTransformId = TransformId;");
                Line($"{FragTransformIdName} = uintBitsToFloat(_xreTransformId);");
            }
        }

        protected override void WriteOutputs()
        {
            if (UseNVStereo)
                Line("layout(secondary_view_offset = 1) out highp int gl_Layer;"); //Apply secondary view offset to the layer output

            base.WriteOutputs();
        }

        protected override void WriteExtensions()
        {
            if (UseOVRMultiView)
            {
                if (RuntimeEngine.Rendering.State.IsVulkan)
                    Line("#extension GL_EXT_multiview : require");
                else
                    Line("#extension GL_OVR_multiview2 : require");
                //multiview tess/geo extension is not supported on nvidia gpus (I assume because you should just use nv stereo)
                //Line("#extension GL_EXT_multiview_tessellation_geometry_shader : enable");
            }
            else if (UseNVStereo)
            {
                Line("#extension GL_NV_viewport_array2 : require");
                Line("#extension GL_NV_stereo_view_rendering : require");
            }
            else if ((UseDirectionalCascadeInstancedLayering || UsePointLightInstancedLayering) && !RuntimeEngine.Rendering.State.IsVulkan)
            {
                Line("#extension GL_ARB_shader_viewport_layer_array : require");
            }
        }

        protected override void WriteInputs()
        {
            if (UseOVRMultiView)
                Line("layout(num_views = 2) in;");

            base.WriteInputs();
        }

        public const string VertexUniformSuffix = "_VTX";

        protected override void WriteUniforms()
        {
            WriteUniformBufferBlocks();
            base.WriteUniforms();
            if (UseDirectionalCascadeInstancedLayering)
            {
                Line("uniform int CascadeLayerCount;");
                Line("uniform mat4 CascadeViewProjectionMatrices[8];");
            }
            if (UsePointLightInstancedLayering)
            {
                Line("uniform int PointShadowFaceCount;");
                Line("uniform int PointShadowFaceIndices[6];");
                Line("uniform mat4 PointShadowViewProjectionMatrices[6];");
            }
        }

        /// <summary>
        /// Shader buffer objects
        /// </summary>
        private void WriteUniformBufferBlocks()
        {
            //These buffers have to be in this order to work - GPU boundary alignment is picky as f

            bool wroteAnything = false;

            int binding = 0;
            if (Mesh.BlendshapeCount > 0 && !UseComputeBlendshapes && RuntimeEngine.Rendering.Settings.AllowBlendshapes)
            {
                EShaderVarType intVarType = RuntimeEngine.Rendering.Settings.UseIntegerUniformsInShaders
                    ? EShaderVarType._ivec4
                    : EShaderVarType._vec4;

                using (StartShaderStorageBufferBlock($"{ECommonBufferType.BlendshapeDeltas}Buffer", binding++))
                    WriteUniform(EShaderVarType._vec4, ECommonBufferType.BlendshapeDeltas.ToString(), true);

                using (StartShaderStorageBufferBlock($"{ECommonBufferType.BlendshapeIndices}Buffer", binding++))
                    WriteUniform(intVarType, ECommonBufferType.BlendshapeIndices.ToString(), true);

                using (StartShaderStorageBufferBlock($"{ECommonBufferType.BlendshapeWeights}Buffer", binding++))
                    WriteUniform(EShaderVarType._float, ECommonBufferType.BlendshapeWeights.ToString(), true);

                wroteAnything = true;
            }
            bool skinning = Mesh.HasSkinning && RuntimeEngine.Rendering.Settings.AllowSkinning && !UseComputeSkinning;
            if (skinning)
            {
                using (StartShaderStorageBufferBlock($"{ECommonBufferType.SkinPalette}Buffer", binding++))
                    WriteUniform(EShaderVarType._vec4, "SkinPaletteRows", true);

                using (StartShaderStorageBufferBlock(ECommonBufferType.BoneInfluenceSpillHeaders.ToString(), binding++))
                    WriteUniform(EShaderVarType._uint, ECommonBufferType.BoneInfluenceSpillHeaders.ToString(), true);

                using (StartShaderStorageBufferBlock(ECommonBufferType.BoneInfluenceSpillEntries.ToString(), binding++))
                    WriteUniform(EShaderVarType._uint, ECommonBufferType.BoneInfluenceSpillEntries.ToString(), true);

                wroteAnything = true;
            }

            if (UseComputeDeformation)
            {
                WriteComputeResultBuffers();
                wroteAnything = true;
            }

            if (wroteAnything)
                Line();
        }

        private void WriteComputeResultBuffers()
        {
            if (Mesh.Interleaved)
            {
                using (StartShaderStorageBufferBlock($"{ComputeInterleavedBufferName}Input", ComputeInterleavedBinding))
                    WriteUniform(EShaderVarType._float, ComputeInterleavedBufferName, true);
                return;
            }

            using (StartShaderStorageBufferBlock($"{ComputePositionBufferName}Input", ComputePositionBinding))
                WriteUniform(EShaderVarType._vec4, ComputePositionBufferName, true);

            if (_useNormals)
            {
                using (StartShaderStorageBufferBlock($"{ComputeNormalBufferName}Input", ComputeNormalBinding))
                    WriteUniform(EShaderVarType._vec4, ComputeNormalBufferName, true);
            }

            if (_useTangents)
            {
                using (StartShaderStorageBufferBlock($"{ComputeTangentBufferName}Input", ComputeTangentBinding))
                    WriteUniform(EShaderVarType._vec4, ComputeTangentBufferName, true);
            }
        }

        /// <summary>
        /// Calculates positions, and optionally normals, tangents, and binormals for a rigged mesh.
        /// </summary>
        private void WriteMeshTransforms(bool hasSkinning)
        {
            bool hasNormals = _useNormals;
            bool hasTangents = _useTangents;

            Line($"vec4 {FinalPositionName} = vec4(0.0f);");
            Line($"vec3 {BasePositionName} = {ECommonBufferType.Position};");

            if (hasNormals)
            {
                Line($"vec3 {FinalNormalName} = vec3(0.0f);");
                Line($"vec3 {BaseNormalName} = {ECommonBufferType.Normal};");
            }

            if (hasTangents)
            {
                Line($"vec3 {FinalTangentName} = vec3(0.0f);");
                Line($"vec3 {BaseTangentName} = {ECommonBufferType.Tangent}.xyz;");
            }

            Line();
            if (UseComputeDeformation)
            {
                WriteComputeBaseAssignments(hasNormals, hasTangents);

                if (UseComputeSkinning)
                {
                    Line($"{FinalPositionName} = vec4({BasePositionName}, 1.0f);");
                    if (hasNormals)
                        Line($"{FinalNormalName} = {BaseNormalName};");
                    if (hasTangents)
                        Line($"{FinalTangentName} = {BaseTangentName};");
                }
                else if (!hasSkinning || !WriteSkinningCalc())
                {
                    Line($"{FinalPositionName} = vec4({BasePositionName}, 1.0f);");
                    if (hasNormals)
                        Line($"{FinalNormalName} = {BaseNormalName};");
                    if (hasTangents)
                        Line($"{FinalTangentName} = {BaseTangentName};");
                }
            }
            else
            {
                //Blendshape calc directly updates base position, normal, and tangent
                WriteBlendshapeCalc();

                if (!hasSkinning || !WriteSkinningCalc())
                {
                    Line($"{FinalPositionName} = vec4({BasePositionName}, 1.0f);");
                    if (hasNormals)
                        Line($"{FinalNormalName} = {BaseNormalName};");
                    if (hasTangents)
                        Line($"{FinalTangentName} = {BaseTangentName};");
                }
            }

            Line();
            if (hasNormals)
            {
                Line($"{FragNormName} = normalize({NormalMatrixName} * {FinalNormalName});");
                if (hasTangents)
                {
                    Line($"{FragTanName} = normalize({NormalMatrixName} * {FinalTangentName});");
                    Line($"vec3 {FinalBinormalName} = cross({FinalNormalName}, {FinalTangentName}) * {ECommonBufferType.Tangent}.w;");
                    Line($"{FragBinormName} = normalize({NormalMatrixName} * {FinalBinormalName});");
                }
            }

            ResolvePosition(FinalPositionName);
        }

        private void WriteComputeBaseAssignments(bool hasNormals, bool hasTangents)
        {
            if (Mesh.Interleaved)
            {
                uint strideFloats = Mesh.InterleavedStride / 4u;
                Line($"uint interleavedStrideFloats = {strideFloats}u;");
                Line("uint baseInterleavedIndex = interleavedStrideFloats * uint(gl_VertexID);");

                uint posOffsetFloats = Mesh.PositionOffset / 4u;
                Line($"{BasePositionName} = vec3({ComputeInterleavedBufferName}[baseInterleavedIndex + {posOffsetFloats}u], {ComputeInterleavedBufferName}[baseInterleavedIndex + {posOffsetFloats + 1u}u], {ComputeInterleavedBufferName}[baseInterleavedIndex + {posOffsetFloats + 2u}u]);");

                if (hasNormals)
                {
                    if (Mesh.NormalOffset.HasValue)
                    {
                        uint nrmOffsetFloats = Mesh.NormalOffset.Value / 4u;
                        Line($"{BaseNormalName} = vec3({ComputeInterleavedBufferName}[baseInterleavedIndex + {nrmOffsetFloats}u], {ComputeInterleavedBufferName}[baseInterleavedIndex + {nrmOffsetFloats + 1u}u], {ComputeInterleavedBufferName}[baseInterleavedIndex + {nrmOffsetFloats + 2u}u]);");
                    }
                    else
                        Line($"{BaseNormalName} = vec3(0.0f);");
                }

                if (hasTangents)
                {
                    if (Mesh.TangentOffset.HasValue)
                    {
                        uint tanOffsetFloats = Mesh.TangentOffset.Value / 4u;
                        Line($"{BaseTangentName} = vec3({ComputeInterleavedBufferName}[baseInterleavedIndex + {tanOffsetFloats}u], {ComputeInterleavedBufferName}[baseInterleavedIndex + {tanOffsetFloats + 1u}u], {ComputeInterleavedBufferName}[baseInterleavedIndex + {tanOffsetFloats + 2u}u]);");
                    }
                    else
                        Line($"{BaseTangentName} = vec3(0.0f);");
                }

                return;
            }

            Line($"{BasePositionName} = {ComputePositionBufferName}[gl_VertexID].xyz;");
            if (hasNormals)
                Line($"{BaseNormalName} = {ComputeNormalBufferName}[gl_VertexID].xyz;");
            if (hasTangents)
                Line($"{BaseTangentName} = {ComputeTangentBufferName}[gl_VertexID].xyz;");
        }

        private bool NeedsSkinningCalc()
            => Mesh.HasSkinning && RuntimeEngine.Rendering.Settings.AllowSkinning && !RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader;

        private bool NeedsBlendshapeCalc()
            => Mesh.BlendshapeCount > 0 && RuntimeEngine.Rendering.Settings.AllowBlendshapes && !RuntimeEngine.Rendering.Settings.CalculateBlendshapesInComputeShader;

        private bool WriteSkinningCalc()
        {
            if (RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader)
                return false;

            bool hasNormals = _useNormals;
            bool hasTangents = _useTangents;

            if (Mesh.SkinningInfluenceEncoding != SkinningInfluenceEncoding.Core4Spill)
                return false;

            Line($"vec3 xreSkinBasePosition = {BasePositionName};");
            if (hasNormals)
                Line($"vec3 xreSkinBaseNormal = {BaseNormalName};");
            if (hasTangents)
                Line($"vec3 xreSkinBaseTangent = {BaseTangentName};");
            Line("float xreTotalSkinWeight = 0.0f;");

            void EmitInfluenceContribution(string boneIndexExpression, string weightExpression)
            {
                Line($"uint boneIndex = uint({boneIndexExpression});");
                Line($"float weight = {weightExpression};");
                Line("if (boneIndex > 0u && weight > 0.0f && boneIndex < skinPaletteCount)");
                using (OpenBracketState())
                {
                    Line("uint paletteIndex = skinPaletteBase + boneIndex;");
                    Line("uint skinRowBase = paletteIndex * 3u;");
                    Line("vec4 skinPosition = vec4(xreSkinBasePosition, 1.0f);");
                    Line("vec3 skinnedPosition = vec3(");
                    Line("    dot(SkinPaletteRows[skinRowBase + 0u], skinPosition),");
                    Line("    dot(SkinPaletteRows[skinRowBase + 1u], skinPosition),");
                    Line("    dot(SkinPaletteRows[skinRowBase + 2u], skinPosition));");
                    Line($"{FinalPositionName} += vec4(skinnedPosition, 1.0f) * weight;");
                    Line("xreTotalSkinWeight += weight;");
                    if (hasNormals || hasTangents)
                    {
                        Line("vec3 skinRow0 = SkinPaletteRows[skinRowBase + 0u].xyz;");
                        Line("vec3 skinRow1 = SkinPaletteRows[skinRowBase + 1u].xyz;");
                        Line("vec3 skinRow2 = SkinPaletteRows[skinRowBase + 2u].xyz;");
                        Line("vec3 skinCofactor0 = cross(skinRow1, skinRow2);");
                        Line("vec3 skinCofactor1 = cross(skinRow2, skinRow0);");
                        Line("vec3 skinCofactor2 = cross(skinRow0, skinRow1);");
                    }
                    if (hasNormals)
                    {
                        Line("vec3 skinnedNormal = vec3(");
                        Line("    dot(skinCofactor0, xreSkinBaseNormal),");
                        Line("    dot(skinCofactor1, xreSkinBaseNormal),");
                        Line("    dot(skinCofactor2, xreSkinBaseNormal));");
                        Line($"{FinalNormalName} += skinnedNormal * weight;");
                    }
                    if (hasTangents)
                    {
                        Line("vec3 skinnedTangent = vec3(");
                        Line("    dot(skinCofactor0, xreSkinBaseTangent),");
                        Line("    dot(skinCofactor1, xreSkinBaseTangent),");
                        Line("    dot(skinCofactor2, xreSkinBaseTangent));");
                        Line($"{FinalTangentName} += skinnedTangent * weight;");
                    }
                }
            }

            Line("for (int i = 0; i < 4; i++)");
            using (OpenBracketState())
                EmitInfluenceContribution($"{ECommonBufferType.BoneInfluenceCoreIndices}[i]", $"{ECommonBufferType.BoneInfluenceCoreWeights}[i]");

            Line($"uint xreSpillHeader = {ECommonBufferType.BoneInfluenceSpillHeaders}[gl_VertexID];");
            Line("uint xreSpillOffset = xreSpillHeader & 0x00FFFFFFu;");
            Line("uint xreSpillCount = xreSpillHeader >> 24;");
            Line("for (uint i = 0u; i < xreSpillCount; i++)");
            using (OpenBracketState())
            {
                Line($"uint xreSpillEntry = {ECommonBufferType.BoneInfluenceSpillEntries}[xreSpillOffset + i];");
                EmitInfluenceContribution("xreSpillEntry & 0xFFFFu", "float((xreSpillEntry >> 16) & 0xFFu) * (1.0f / 255.0f)");
            }

            Line("if (xreTotalSkinWeight <= 0.0001f)");
            using (OpenBracketState())
            {
                Line($"{FinalPositionName} = vec4({BasePositionName}, 1.0f);");
                if (hasNormals)
                    Line($"{FinalNormalName} = {BaseNormalName};");
                if (hasTangents)
                    Line($"{FinalTangentName} = {BaseTangentName};");
            }
            return true;
        }
        
        private bool WriteBlendshapeCalc()
        {
            if (RuntimeEngine.Rendering.Settings.CalculateBlendshapesInComputeShader || Mesh.BlendshapeCount == 0 || !RuntimeEngine.Rendering.Settings.AllowBlendshapes || !_useBlendshapeInput)
                return false;

            bool hasNormals = _useNormals;
            bool hasTangents = _useTangents;

            bool absolute = RuntimeEngine.Rendering.Settings.UseAbsoluteBlendshapePositions;

            const string minWeight = "0.0001f";
            if (Mesh.MaxBlendshapeAccumulation)
            {
                // MAX blendshape accumulation
                Line("vec3 maxPositionDelta = vec3(0.0f);");
                if (hasNormals)
                    Line("vec3 maxNormalDelta = vec3(0.0f);");
                if (hasTangents)
                    Line("vec3 maxTangentDelta = vec3(0.0f);");
                Line($"for (int i = 0; i < int({ECommonBufferType.BlendshapeCount}.y); i++)");
                using (OpenBracketState())
                {
                    Line($"int index = int({ECommonBufferType.BlendshapeCount}.x) + i;");
                    if (RuntimeEngine.Rendering.Settings.UseIntegerUniformsInShaders)
                        Line($"ivec4 blendshapeIndices = {ECommonBufferType.BlendshapeIndices}[index];");
                    else
                        Line($"vec4 blendshapeIndices = {ECommonBufferType.BlendshapeIndices}[index];");
                    Line($"int blendshapeIndex = int(blendshapeIndices.x);");
                    Line($"float weight = {ECommonBufferType.BlendshapeWeights}[blendshapeIndex];");
                    Line($"if (weight > {minWeight})");
                    using (OpenBracketState())
                    {
                        Line($"int blendshapeDeltaPosIndex = int(blendshapeIndices.y);");
                        if (hasNormals)
                            Line($"int blendshapeDeltaNrmIndex = int(blendshapeIndices.z);");
                        if (hasTangents)
                            Line($"int blendshapeDeltaTanIndex = int(blendshapeIndices.w);");
                        Line($"maxPositionDelta = max(maxPositionDelta, {ECommonBufferType.BlendshapeDeltas}[blendshapeDeltaPosIndex].xyz * weight);");
                        if (hasNormals)
                            Line($"maxNormalDelta = max(maxNormalDelta, {ECommonBufferType.BlendshapeDeltas}[blendshapeDeltaNrmIndex].xyz * weight);");
                        if (hasTangents)
                            Line($"maxTangentDelta = max(maxTangentDelta, {ECommonBufferType.BlendshapeDeltas}[blendshapeDeltaTanIndex].xyz * weight);");
                    }
                }
                Line($"{BasePositionName} += maxPositionDelta;");
                if (hasNormals)
                    Line($"{BaseNormalName} += maxNormalDelta;");
                if (hasTangents)
                    Line($"{BaseTangentName} += maxTangentDelta;");
            }
            else
            {
                Line($"for (int i = 0; i < int({ECommonBufferType.BlendshapeCount}.y); i++)");
                using (OpenBracketState())
                {
                    Line($"int index = int({ECommonBufferType.BlendshapeCount}.x) + i;");
                    if (RuntimeEngine.Rendering.Settings.UseIntegerUniformsInShaders)
                        Line($"ivec4 blendshapeIndices = {ECommonBufferType.BlendshapeIndices}[index];");
                    else
                        Line($"vec4 blendshapeIndices = {ECommonBufferType.BlendshapeIndices}[index];");
                    Line($"int blendshapeIndex = int(blendshapeIndices.x);");
                    Line($"int blendshapeDeltaPosIndex = int(blendshapeIndices.y);");
                    if (hasNormals)
                        Line($"int blendshapeDeltaNrmIndex = int(blendshapeIndices.z);");
                    if (hasTangents)
                        Line($"int blendshapeDeltaTanIndex = int(blendshapeIndices.w);");
                    Line($"float weight = {ECommonBufferType.BlendshapeWeights}[blendshapeIndex];");
                    Line($"if (weight > {minWeight})");
                    using (OpenBracketState())
                    {
                        Line($"{BasePositionName} += {ECommonBufferType.BlendshapeDeltas}[blendshapeDeltaPosIndex].xyz * weight;");
                        if (hasNormals)
                            Line($"{BaseNormalName} += ({ECommonBufferType.BlendshapeDeltas}[blendshapeDeltaNrmIndex].xyz * weight);");
                        if (hasTangents)
                            Line($"{BaseTangentName} += ({ECommonBufferType.BlendshapeDeltas}[blendshapeDeltaTanIndex].xyz * weight);");
                    }
                }
            }
            return true;
        }

        private void ResolvePosition(string localInputPosName)
        {
            Line($"{FragPosLocalName} = {localInputPosName}.xyz;");

            if (UseNVStereo)
            {
                const string finalPosLeftName = "outPosLeft";
                const string finalPosRightName = "outPosRight";

                DeclareAndAssignFinalPosition(
                    localInputPosName,
                    finalPosLeftName,
                    $"{EEngineUniform.LeftEyeInverseViewMatrix}{VertexUniformSuffix}",
                    $"{EEngineUniform.LeftEyeProjMatrix}{VertexUniformSuffix}",
                    0);

                DeclareAndAssignFinalPosition(
                    localInputPosName,
                    finalPosRightName,
                    $"{EEngineUniform.RightEyeInverseViewMatrix}{VertexUniformSuffix}",
                    $"{EEngineUniform.RightEyeProjMatrix}{VertexUniformSuffix}",
                    1);

                AssignFragPosOut(localInputPosName);
                Assign_GL_Position(finalPosLeftName);
                Assign_GL_SecondaryPositionNV(finalPosRightName);
            }
            else
            {
                string invViewMatrixName = $"{EEngineUniform.InverseViewMatrix}{VertexUniformSuffix}";
                string projMatrixName = $"{EEngineUniform.ProjMatrix}{VertexUniformSuffix}";
                if (UseOVRMultiView)
                {
                    Line("bool leftEye = gl_ViewID_OVR == 0;");
                    Line($"mat4 {invViewMatrixName} = leftEye ? {EEngineUniform.LeftEyeInverseViewMatrix}{VertexUniformSuffix} : {EEngineUniform.RightEyeInverseViewMatrix}{VertexUniformSuffix};");
                    Line($"mat4 {projMatrixName} = leftEye ? {EEngineUniform.LeftEyeProjMatrix}{VertexUniformSuffix} : {EEngineUniform.RightEyeProjMatrix}{VertexUniformSuffix};");
                }

                const string finalPosName = "outPos";

                DeclareAndAssignFinalPosition(
                    localInputPosName,
                    finalPosName,
                    invViewMatrixName,
                    projMatrixName,
                    0);

                AssignFragPosOut(localInputPosName);
                Assign_GL_Position(finalPosName);
                if (UseDirectionalCascadeInstancedLayering)
                    AssignDirectionalCascadeLayeredPosition(localInputPosName);
                if (UsePointLightInstancedLayering)
                    AssignPointLightLayeredPosition(localInputPosName);
            }
        }

        private void BillboardCalc(string posName, string glPosName, string invViewMatrixName, string projMatrixName, int index)
        {
            Comment($"'{EEngineUniform.BillboardMode}' uniform: 0 = none, 1 = camera-facing (perspective), 2 = camera plane (orthographic)");

            const string pivotName = "pivot";
            const string deltaName = "delta";
            const string lookDirName = "lookDir";
            const string worldUpName = "worldUp";
            const string rightName = "right";
            const string upName = "up";
            const string rotationMatrixName = "rotationMatrix";
            const string rotatedDeltaName = "rotatedDelta";
            const string rotatedWorldPosName = "rotatedWorldPos";
            const string camPositionName = "camPosition";
            const string camForwardName = "camForward";

            Line($"vec3 {camPositionName} = {invViewMatrixName}[3].xyz;");
            Line($"vec3 {camForwardName} = normalize({invViewMatrixName}[2].xyz);");

            //Extract rotation pivot from ModelMatrix
            Line($"vec3 {pivotName} = {EEngineUniform.ModelMatrix}[3].xyz;");

            //Calculate offset from pivot in world space
            Line($"vec3 {deltaName} = ({EEngineUniform.ModelMatrix} * {posName}).xyz - {pivotName};");

            //Calculate direction to look at the camera
            Line($"vec3 {lookDirName} = {EEngineUniform.BillboardMode} == 1 ? normalize({camPositionName} - {pivotName}) : normalize(-{camForwardName});");

            //Calculate right and up vectors
            Line($"vec3 {worldUpName} = vec3(0.0, 1.0f, 0.0);");
            Line($"vec3 {rightName} = normalize(cross({worldUpName}, {lookDirName}));");
            Line($"vec3 {upName} = cross({lookDirName}, {rightName});");

            //Create rotation matrix using vectors
            Line($"mat3 {rotationMatrixName} = mat3({rightName}, {upName}, {lookDirName});");

            //Rotate delta and add pivot back to get final position
            Line($"vec3 {rotatedDeltaName} = {rotationMatrixName} * {deltaName};");
            Line($"vec4 {rotatedWorldPosName} = vec4({pivotName} + {rotatedDeltaName}, 1.0f);");

            //Model matrix is already multipled into rotatedWorldPos, so don't multiply it again. Use as-is, or multiply by only view and projection matrices
            //VR shaders will multiply the view and projection matrices in the geometry shader

            void AssignCameraSpace()
            {
                DeclareVP(ViewMatrixName + index, invViewMatrixName, projMatrixName, ViewProjMatrixName + index);
                Line($"{glPosName} = {ViewProjMatrixName + index} * {rotatedWorldPosName};");
            }

            //VR shaders will multiply the view and projection matrices in the geometry shader
            void AssignModelSpace()
                => Line($"{glPosName} = {rotatedWorldPosName};");

            if (UseOVRMultiView || UseNVStereo)
                AssignCameraSpace(); //Multiply by view and projection right now - not in geometry shader
            else
                IfElse(EEngineUniform.VRMode.ToStringFast(), AssignModelSpace, AssignCameraSpace);
        }

        /// <summary>
        /// Declares and assigns the final position to the local input position, optionally transformed by billboarding.
        /// Transformed by the model matrix, and by the view and projection matrices if not in VR.
        /// </summary>
        /// <param name="localInputPositionName"></param>
        /// <param name="finalPositionName"></param>
        private void DeclareAndAssignFinalPosition(string localInputPositionName, string finalPositionName, string invViewMatrixName, string projMatrixName, int index)
        {
            Line($"vec4 {finalPositionName};");

            void AssignCameraSpace()
            {
                DeclareMVP(ViewMatrixName + index, invViewMatrixName, projMatrixName, ModelViewMatrixName + index, ModelViewProjMatrixName + index);
                Line($"{finalPositionName} = {ModelViewProjMatrixName + index} * {localInputPositionName};");
            }

            //Non-extension VR shaders will multiply the view and projection matrices in the geometry shader
            void AssignModelSpace()
                => Line($"{finalPositionName} = {EEngineUniform.ModelMatrix} * {localInputPositionName};");

            void NoBillboardCalc()
            {
                if (UseOVRMultiView || UseNVStereo)
                    AssignCameraSpace(); //Multiply by view and projection right now - not in geometry shader
                else
                    IfElse(EEngineUniform.VRMode.ToStringFast(), AssignModelSpace, AssignCameraSpace);
            }

            if (Mesh.SupportsBillboarding)
            {
                void BillboardCalc() 
                    => this.BillboardCalc(localInputPositionName, finalPositionName, invViewMatrixName, projMatrixName, index);

                IfElse($"{EEngineUniform.BillboardMode} != 0", BillboardCalc, NoBillboardCalc);
            }
            else
                NoBillboardCalc();
        }

        /// <summary>
        /// Assigns fragment position out to world-space position.
        /// Forward lighting shaders expect FragPos in world space for specular/shadow calculations.
        /// </summary>
        /// <param name="localInputPositionName">The local/model-space position variable.</param>
        private void AssignFragPosOut(string localInputPositionName)
        {
            // Always output world-space position for FragPos - required by forward lighting shaders.
            // gl_Position still gets clip-space position for rasterization.
            Line($"{FragPosName} = ({EEngineUniform.ModelMatrix} * {localInputPositionName}).xyz;");
            if (UseOVRMultiView)
                Line($"{FragViewIndexName} = float(gl_ViewID_OVR);");
            else
                Line($"{FragViewIndexName} = 0.0f;");
        }

        /// <summary>
        /// Assigns gl_Position to the final position.
        /// </summary>
        /// <param name="finalPositionName"></param>
        private void Assign_GL_Position(string finalPositionName)
            => Line($"gl_Position = {finalPositionName};");

        private void AssignDirectionalCascadeLayeredPosition(string localInputPositionName)
        {
            Line("int xreCascadeLayerCount = clamp(CascadeLayerCount, 1, 8);");
            Line("int xreCascadeLayer = gl_InstanceID % xreCascadeLayerCount;");
            Line($"vec3 xreCascadeWorldPos = ({EEngineUniform.ModelMatrix} * {localInputPositionName}).xyz;");
            Line("gl_Position = CascadeViewProjectionMatrices[xreCascadeLayer] * vec4(xreCascadeWorldPos, 1.0f);");
            if (UseDirectionalCascadeAtlasInstancedLayering)
                Line("gl_ViewportIndex = xreCascadeLayer;");
            else
                Line("gl_Layer = xreCascadeLayer;");
        }

        private void AssignPointLightLayeredPosition(string localInputPositionName)
        {
            Line("int xrePointShadowFaceCount = clamp(PointShadowFaceCount, 1, 6);");
            Line("int xrePointShadowSlot = gl_InstanceID % xrePointShadowFaceCount;");
            Line("int xrePointShadowFace = clamp(PointShadowFaceIndices[xrePointShadowSlot], 0, 5);");
            Line($"vec3 xrePointShadowWorldPos = ({EEngineUniform.ModelMatrix} * {localInputPositionName}).xyz;");
            Line("gl_Position = PointShadowViewProjectionMatrices[xrePointShadowSlot] * vec4(xrePointShadowWorldPos, 1.0f);");
            if (UsePointLightAtlasInstancedLayering)
                Line("gl_ViewportIndex = xrePointShadowSlot;");
            else
                Line("gl_Layer = xrePointShadowFace;");
        }

        /// <summary>
        /// Assigns gl_SecondaryPositionNV to the final right eye position.
        /// </summary>
        /// <param name="finalPositionName"></param>
        private void Assign_GL_SecondaryPositionNV(string finalPositionName)
            => Line($"gl_SecondaryPositionNV = {finalPositionName};");

        /// <summary>
        /// Creates the projection * view matrix.
        /// Uses ViewMatrix uniform directly (precomputed on CPU) for better precision.
        /// </summary>
        private void DeclareVP(string viewMatrixName, string invViewMatrixName, string projMatrixName, string viewProjMatrixName)
        {
            // Stereo variants only bind per-eye inverse-view matrices, so derive the matching
            // view matrix from the selected eye instead of referencing the mono ViewMatrix_VTX uniform.
            string viewMatrixExpression = UseOVRMultiView || UseNVStereo
                ? $"inverse({invViewMatrixName})"
                : $"{EEngineUniform.ViewMatrix}{VertexUniformSuffix}";
            Line($"mat4 {viewMatrixName} = {viewMatrixExpression};");
            Line($"mat4 {viewProjMatrixName} = {projMatrixName} * {viewMatrixName};");
        }

        /// <summary>
        /// Creates the projection * view * model matrix.
        /// Uses ViewMatrix uniform directly (precomputed on CPU) for better precision.
        /// </summary>
        private void DeclareMVP(string viewMatrixName, string invViewMatrixName, string projMatrixName, string modelViewMatrixName, string modelViewProjMatrixName)
        {
            // Stereo variants only bind per-eye inverse-view matrices, so derive the matching
            // view matrix from the selected eye instead of referencing the mono ViewMatrix_VTX uniform.
            string viewMatrixExpression = UseOVRMultiView || UseNVStereo
                ? $"inverse({invViewMatrixName})"
                : $"{EEngineUniform.ViewMatrix}{VertexUniformSuffix}";
            Line($"mat4 {viewMatrixName} = {viewMatrixExpression};");
            Line($"mat4 {modelViewMatrixName} = {viewMatrixName} * {EEngineUniform.ModelMatrix};");
            Line($"mat4 {modelViewProjMatrixName} = {projMatrixName} * {modelViewMatrixName};");
        }
    }
}
