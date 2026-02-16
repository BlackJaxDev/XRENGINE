using System;
using Extensions;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Shaders.Generator
{
    /// <summary>
    /// Generates a vertex shader that deforms mesh vertices based on influences from another mesh's vertices.
    /// Instead of traditional bone-based skinning, each vertex can be influenced by up to X vertices from a deformer mesh.
    /// This is useful for mesh deformation, lattice deformation, cage deformation, and similar effects.
    /// </summary>
    public class MeshDeformVertexShaderGenerator : ShaderGeneratorBase
    {
        private const uint MaxVertexAttribs = 16;

        /// <summary>
        /// Maximum number of mesh vertex influences per vertex.
        /// Each influence requires: index (int/float) + weight (float).
        /// Default is 8, which allows for smooth deformation while maintaining reasonable performance.
        /// </summary>
        public int MaxMeshInfluences { get; }

        /// <summary>
        /// If true, packs indices and weights into vec4s for optimized transfer.
        /// When MaxMeshInfluences is 4 or less, uses two vec4 attributes (indices + weights).
        /// When MaxMeshInfluences is greater than 4, uses SSBOs for arbitrary influence counts.
        /// </summary>
        public bool OptimizeToVec4 { get; }

        private bool _useNormals;
        private bool _useTangents;
        private bool _useMeshDeformInputs;
        private int _texCoordsUsed;
        private int _colorsUsed;

        public virtual bool UseOVRMultiView => false;
        public virtual bool UseNVStereo => false;

        /// <summary>
        /// Creates a new mesh deform vertex shader generator.
        /// </summary>
        /// <param name="mesh">The mesh to generate the shader for.</param>
        /// <param name="maxMeshInfluences">Maximum number of deformer mesh vertices that can influence each vertex. Default is 8.</param>
        /// <param name="optimizeToVec4">If true and maxMeshInfluences is 4 or less, packs data into vec4 attributes for optimization.</param>
        public MeshDeformVertexShaderGenerator(XRMesh mesh, int maxMeshInfluences = 8, bool optimizeToVec4 = true) : base(mesh)
        {
            MaxMeshInfluences = Math.Max(1, maxMeshInfluences);
            OptimizeToVec4 = optimizeToVec4 && MaxMeshInfluences <= 4;

            HelperMethodWriters.Add(WriteAdjointMethod);

            ComputeAttributeBudget();
            AddUniforms();
            AddOutputs();
            AddInputs();
        }

        private void ComputeAttributeBudget()
        {
            uint location = 0u;

            // Position is mandatory
            location++;

            _useNormals = Mesh.HasNormals && location < MaxVertexAttribs;
            if (_useNormals)
                location++;

            _useTangents = Mesh.HasTangents && location < MaxVertexAttribs;
            if (_useTangents)
                location++;

            // Mesh deformation input slots
            // When optimized: 2 vec4 attributes (indices + weights)
            // When using SSBOs: 2 attributes (offset + count)
            uint meshDeformSlots = OptimizeToVec4 ? 2u : 2u;

            if (location + meshDeformSlots > MaxVertexAttribs && _useTangents)
            {
                _useTangents = false;
                location--;
            }
            if (location + meshDeformSlots > MaxVertexAttribs && _useNormals)
            {
                _useNormals = false;
                location--;
            }

            _useMeshDeformInputs = location + meshDeformSlots <= MaxVertexAttribs;
            if (_useMeshDeformInputs)
                location += meshDeformSlots;

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
                InputVars.Add(ECommonBufferType.Tangent.ToString(), (location++, EShaderVarType._vec3));

            if (_useMeshDeformInputs)
            {
                if (OptimizeToVec4)
                {
                    // Pack up to 4 indices and weights into vec4s
                    EShaderVarType intVecVarType = Engine.Rendering.Settings.UseIntegerUniformsInShaders
                        ? EShaderVarType._ivec4
                        : EShaderVarType._vec4;

                    InputVars.Add(MeshDeformVertexIndicesAttrName, (location++, intVecVarType));
                    InputVars.Add(MeshDeformVertexWeightsAttrName, (location++, EShaderVarType._vec4));
                }
                else
                {
                    // Use offset + count into SSBO
                    EShaderVarType intVarType = Engine.Rendering.Settings.UseIntegerUniformsInShaders
                        ? EShaderVarType._int
                        : EShaderVarType._float;

                    InputVars.Add(MeshDeformVertexOffsetAttrName, (location++, intVarType));
                    InputVars.Add(MeshDeformVertexCountAttrName, (location++, intVarType));
                }
            }

            if (_texCoordsUsed > 0)
                for (uint i = 0; i < _texCoordsUsed; ++i)
                    InputVars.Add($"{ECommonBufferType.TexCoord}{i}", (location++, EShaderVarType._vec2));

            if (_colorsUsed > 0)
                for (uint i = 0; i < _colorsUsed; ++i)
                    InputVars.Add($"{ECommonBufferType.Color}{i}", (location++, EShaderVarType._vec4));
        }

        private void AddOutputs()
        {
            OutputVars.Add(FragPosName, (0, EShaderVarType._vec3));
            OutputVars.Add(FragNormName, (1, EShaderVarType._vec3));

            if (_useTangents)
            {
                OutputVars.Add(FragTanName, (2, EShaderVarType._vec3));
                OutputVars.Add(FragBinormName, (3, EShaderVarType._vec3));
            }

            if (_texCoordsUsed > 0)
                for (int i = 0; i < _texCoordsUsed.ClampMax(8); ++i)
                    OutputVars.Add(string.Format(FragUVName, i), (4u + (uint)i, EShaderVarType._vec2));

            if (_colorsUsed > 0)
                for (int i = 0; i < _colorsUsed.ClampMax(8); ++i)
                    OutputVars.Add(string.Format(FragColorName, i), (12u + (uint)i, EShaderVarType._vec4));

            OutputVars.Add(FragPosLocalName, (20, EShaderVarType._vec3));
        }

        private void AddUniforms()
        {
            UniformNames.Add(EEngineUniform.ModelMatrix.ToString(), (EShaderVarType._mat4, false));

            if (UseOVRMultiView || UseNVStereo)
            {
                UniformNames.Add($"{EEngineUniform.LeftEyeInverseViewMatrix}{VertexUniformSuffix}", (EShaderVarType._mat4, false));
                UniformNames.Add($"{EEngineUniform.RightEyeInverseViewMatrix}{VertexUniformSuffix}", (EShaderVarType._mat4, false));
                UniformNames.Add($"{EEngineUniform.LeftEyeProjMatrix}{VertexUniformSuffix}", (EShaderVarType._mat4, false));
                UniformNames.Add($"{EEngineUniform.RightEyeProjMatrix}{VertexUniformSuffix}", (EShaderVarType._mat4, false));
            }
            else
            {
                UniformNames.Add($"{EEngineUniform.ViewMatrix}{VertexUniformSuffix}", (EShaderVarType._mat4, false));
                UniformNames.Add($"{EEngineUniform.InverseViewMatrix}{VertexUniformSuffix}", (EShaderVarType._mat4, false));
                UniformNames.Add($"{EEngineUniform.ProjMatrix}{VertexUniformSuffix}", (EShaderVarType._mat4, false));
            }

            if (!UseOVRMultiView && !UseNVStereo)
                UniformNames.Add(EEngineUniform.VRMode.ToString(), (EShaderVarType._bool, false));

            if (Mesh.SupportsBillboarding)
                UniformNames.Add(EEngineUniform.BillboardMode.ToString(), (EShaderVarType._int, false));
        }

        #region Constants

        // Output variable names
        public const string FragPosLocalName = "FragPosLocal";
        public const string FragPosName = "FragPos";
        public const string FragNormName = "FragNorm";
        public const string FragTanName = "FragTan";
        public const string FragBinormName = "FragBinorm";
        public const string FragColorName = "FragColor{0}";
        public const string FragUVName = "FragUV{0}";

        // Base variable names for deformation
        public const string BasePositionName = "basePosition";
        public const string BaseNormalName = "baseNormal";
        public const string BaseTangentName = "baseTangent";

        // Final variable names after deformation
        public const string FinalPositionName = "finalPosition";
        public const string FinalNormalName = "finalNormal";
        public const string FinalTangentName = "finalTangent";
        public const string FinalBinormalName = "finalBinormal";

        // Mesh deform attribute names (optimized vec4 path)
        public const string MeshDeformVertexIndicesAttrName = "MeshDeformVertexIndices";
        public const string MeshDeformVertexWeightsAttrName = "MeshDeformVertexWeights";

        // Mesh deform attribute names (SSBO path)
        public const string MeshDeformVertexOffsetAttrName = "MeshDeformVertexOffset";
        public const string MeshDeformVertexCountAttrName = "MeshDeformVertexCount";

        // SSBO buffer names
        public const string DeformerPositionsBufferName = "DeformerPositions";
        public const string DeformerNormalsBufferName = "DeformerNormals";
        public const string DeformerTangentsBufferName = "DeformerTangents";
        public const string DeformerRestPositionsBufferName = "DeformerRestPositions";
        public const string MeshDeformIndicesBufferName = "MeshDeformIndices";
        public const string MeshDeformWeightsBufferName = "MeshDeformWeights";

        // Matrix names
        private const string ViewMatrixName = "ViewMatrix";
        private const string ModelViewMatrixName = "mvMatrix";
        private const string ModelViewProjMatrixName = "mvpMatrix";
        private const string NormalMatrixName = "normalMatrix";

        public const string VertexUniformSuffix = "_VTX";

        #endregion

        protected override void WriteExtensions()
        {
            if (UseOVRMultiView)
            {
                if (Engine.Rendering.State.IsVulkan)
                    Line("#extension GL_EXT_multiview : require");
                else
                    Line("#extension GL_OVR_multiview2 : require");
            }
            else if (UseNVStereo)
            {
                Line("#extension GL_NV_viewport_array2 : require");
                Line("#extension GL_NV_stereo_view_rendering : require");
            }
        }

        protected override void WriteInputs()
        {
            if (UseOVRMultiView)
                Line("layout(num_views = 2) in;");

            base.WriteInputs();
        }

        protected override void WriteOutputs()
        {
            if (UseNVStereo)
                Line("layout(secondary_view_offset = 1) out highp int gl_Layer;");

            base.WriteOutputs();
        }

        protected override void WriteUniforms()
        {
            WriteUniformBufferBlocks();
            base.WriteUniforms();
        }

        private void WriteUniformBufferBlocks()
        {
            int binding = 0;

            // Deformer mesh current positions (animated/deformed positions)
            using (StartShaderStorageBufferBlock($"{DeformerPositionsBufferName}Buffer", binding++))
                WriteUniform(EShaderVarType._vec4, DeformerPositionsBufferName, true);

            // Deformer mesh rest positions (original bind pose)
            using (StartShaderStorageBufferBlock($"{DeformerRestPositionsBufferName}Buffer", binding++))
                WriteUniform(EShaderVarType._vec4, DeformerRestPositionsBufferName, true);

            if (_useNormals)
            {
                using (StartShaderStorageBufferBlock($"{DeformerNormalsBufferName}Buffer", binding++))
                    WriteUniform(EShaderVarType._vec4, DeformerNormalsBufferName, true);
            }

            if (_useTangents)
            {
                using (StartShaderStorageBufferBlock($"{DeformerTangentsBufferName}Buffer", binding++))
                    WriteUniform(EShaderVarType._vec4, DeformerTangentsBufferName, true);
            }

            // If not optimized to vec4, we need SSBOs for indices and weights
            if (!OptimizeToVec4 && _useMeshDeformInputs)
            {
                using (StartShaderStorageBufferBlock($"{MeshDeformIndicesBufferName}Buffer", binding++))
                    WriteUniform(EShaderVarType._int, MeshDeformIndicesBufferName, true);

                using (StartShaderStorageBufferBlock($"{MeshDeformWeightsBufferName}Buffer", binding++))
                    WriteUniform(EShaderVarType._float, MeshDeformWeightsBufferName, true);
            }

            Line();
        }

        protected override void WriteMain()
        {
            if (_useNormals)
            {
                Line($"mat3 {NormalMatrixName} = adjoint({EEngineUniform.ModelMatrix});");
                Line();
            }

            WriteMeshDeformTransforms();

            if (!_useNormals)
                Line($"{FragNormName} = vec3(0.0f, 0.0f, 1.0f);");

            if (_colorsUsed != 0)
                for (int i = 0; i < _colorsUsed; ++i)
                    Line($"{string.Format(FragColorName, i)} = {ECommonBufferType.Color}{i};");

            if (_texCoordsUsed != 0)
                for (int i = 0; i < _texCoordsUsed; ++i)
                    Line($"{string.Format(FragUVName, i)} = {ECommonBufferType.TexCoord}{i};");
        }

        /// <summary>
        /// Writes the mesh deformation transform calculations.
        /// Uses delta-based deformation: calculates the displacement from rest pose in the deformer mesh
        /// and applies that displacement to the deformed mesh vertices.
        /// </summary>
        private void WriteMeshDeformTransforms()
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
                Line($"vec3 {BaseTangentName} = {ECommonBufferType.Tangent};");
            }

            Line();

            if (_useMeshDeformInputs)
            {
                WriteMeshDeformCalc(hasNormals, hasTangents);
            }
            else
            {
                // No mesh deform inputs, just pass through
                Line($"{FinalPositionName} = vec4({BasePositionName}, 1.0f);");
                if (hasNormals)
                    Line($"{FinalNormalName} = {BaseNormalName};");
                if (hasTangents)
                    Line($"{FinalTangentName} = {BaseTangentName};");
            }

            Line();

            if (hasNormals)
            {
                Line($"{FragNormName} = normalize({NormalMatrixName} * {FinalNormalName});");
                if (hasTangents)
                {
                    Line($"{FragTanName} = normalize({NormalMatrixName} * {FinalTangentName});");
                    Line($"vec3 {FinalBinormalName} = cross({FinalNormalName}, {FinalTangentName});");
                    Line($"{FragBinormName} = normalize({NormalMatrixName} * {FinalBinormalName});");
                }
            }

            ResolvePosition(FinalPositionName);
        }

        /// <summary>
        /// Writes the mesh deformation calculation loop.
        /// For each influence, calculates the delta between the deformer's current and rest positions,
        /// and applies that weighted delta to the base position.
        /// </summary>
        private void WriteMeshDeformCalc(bool hasNormals, bool hasTangents)
        {
            Comment("Mesh-based vertex deformation");
            Comment("Each vertex is influenced by vertices from a deformer mesh.");
            Comment("The deformation is calculated as: finalPos = basePos + sum(weight * (deformerPos - deformerRestPos))");
            Line();

            Line("float totalWeight = 0.0f;");
            Line("vec3 positionDelta = vec3(0.0f);");
            if (hasNormals)
                Line("vec3 normalDelta = vec3(0.0f);");
            if (hasTangents)
                Line("vec3 tangentDelta = vec3(0.0f);");

            Line();

            if (OptimizeToVec4)
            {
                // Optimized path: indices and weights packed in vec4 attributes
                Line($"for (int i = 0; i < {MaxMeshInfluences}; i++)");
                using (OpenBracketState())
                {
                    Line($"int deformerIndex = int({MeshDeformVertexIndicesAttrName}[i]);");
                    Line($"float weight = {MeshDeformVertexWeightsAttrName}[i];");
                    Line();
                    Line("// Skip zero-weight or invalid influences");
                    Line("if (weight <= 0.0001f || deformerIndex < 0) continue;");
                    Line();
                    Line("totalWeight += weight;");
                    Line();
                    Line("// Get current and rest positions of the deformer vertex");
                    Line($"vec3 deformerPos = {DeformerPositionsBufferName}[deformerIndex].xyz;");
                    Line($"vec3 deformerRestPos = {DeformerRestPositionsBufferName}[deformerIndex].xyz;");
                    Line();
                    Line("// Calculate displacement and apply weighted to delta");
                    Line("vec3 displacement = deformerPos - deformerRestPos;");
                    Line("positionDelta += displacement * weight;");

                    if (hasNormals)
                    {
                        Line();
                        Line("// Accumulate normal influence");
                        Line($"vec3 deformerNormal = {DeformerNormalsBufferName}[deformerIndex].xyz;");
                        Line("normalDelta += deformerNormal * weight;");
                    }

                    if (hasTangents)
                    {
                        Line();
                        Line("// Accumulate tangent influence");
                        Line($"vec3 deformerTangent = {DeformerTangentsBufferName}[deformerIndex].xyz;");
                        Line("tangentDelta += deformerTangent * weight;");
                    }
                }
            }
            else
            {
                // SSBO path: variable number of influences per vertex
                Line($"int influenceCount = int({MeshDeformVertexCountAttrName});");
                Line($"int startOffset = int({MeshDeformVertexOffsetAttrName});");
                Line();
                Line("for (int i = 0; i < influenceCount; i++)");
                using (OpenBracketState())
                {
                    Line("int idx = startOffset + i;");
                    Line($"int deformerIndex = {MeshDeformIndicesBufferName}[idx];");
                    Line($"float weight = {MeshDeformWeightsBufferName}[idx];");
                    Line();
                    Line("// Skip zero-weight influences");
                    Line("if (weight <= 0.0001f) continue;");
                    Line();
                    Line("totalWeight += weight;");
                    Line();
                    Line("// Get current and rest positions of the deformer vertex");
                    Line($"vec3 deformerPos = {DeformerPositionsBufferName}[deformerIndex].xyz;");
                    Line($"vec3 deformerRestPos = {DeformerRestPositionsBufferName}[deformerIndex].xyz;");
                    Line();
                    Line("// Calculate displacement and apply weighted to delta");
                    Line("vec3 displacement = deformerPos - deformerRestPos;");
                    Line("positionDelta += displacement * weight;");

                    if (hasNormals)
                    {
                        Line();
                        Line("// Accumulate normal influence");
                        Line($"vec3 deformerNormal = {DeformerNormalsBufferName}[deformerIndex].xyz;");
                        Line("normalDelta += deformerNormal * weight;");
                    }

                    if (hasTangents)
                    {
                        Line();
                        Line("// Accumulate tangent influence");
                        Line($"vec3 deformerTangent = {DeformerTangentsBufferName}[deformerIndex].xyz;");
                        Line("tangentDelta += deformerTangent * weight;");
                    }
                }
            }

            Line();
            Line("// Apply accumulated deltas to base position");
            Line("// Normalize by total weight if weights don't sum to 1.0");
            Line("if (totalWeight > 0.0001f)");
            using (OpenBracketState())
            {
                Line("float invWeight = 1.0f / totalWeight;");
                Line($"{FinalPositionName} = vec4({BasePositionName} + positionDelta * invWeight, 1.0f);");
                
                if (hasNormals)
                {
                    Line();
                    Line("// Blend normal from base normal towards accumulated deformer normals");
                    Line($"{FinalNormalName} = normalize(mix({BaseNormalName}, normalize(normalDelta), clamp(totalWeight, 0.0f, 1.0f)));");
                }

                if (hasTangents)
                {
                    Line();
                    Line("// Blend tangent from base tangent towards accumulated deformer tangents");
                    Line($"{FinalTangentName} = normalize(mix({BaseTangentName}, normalize(tangentDelta), clamp(totalWeight, 0.0f, 1.0f)));");
                }
            }
            Line("else");
            using (OpenBracketState())
            {
                Line("// No valid influences, use base values");
                Line($"{FinalPositionName} = vec4({BasePositionName}, 1.0f);");
                if (hasNormals)
                    Line($"{FinalNormalName} = {BaseNormalName};");
                if (hasTangents)
                    Line($"{FinalTangentName} = {BaseTangentName};");
            }
        }

        private void ResolvePosition(string localInputPosName)
        {
            Line($"{FragPosLocalName} = {localInputPosName}.xyz;");

            if (UseNVStereo)
            {
                Line("gl_Layer = 0;");

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
                Line($"gl_SecondaryPositionNV = {finalPosRightName};");
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

                DeclareAndAssignFinalPosition(localInputPosName, finalPosName, invViewMatrixName, projMatrixName, 0);
                AssignFragPosOut(localInputPosName);
                Assign_GL_Position(finalPosName);
            }
        }

        private void DeclareAndAssignFinalPosition(string localInputPositionName, string finalPositionName, string invViewMatrixName, string projMatrixName, int index)
        {
            Line($"vec4 {finalPositionName};");

            void AssignCameraSpace()
            {
                string viewMatrixUniform = $"{EEngineUniform.ViewMatrix}{VertexUniformSuffix}";
                Line($"mat4 {ViewMatrixName}{index} = {viewMatrixUniform};");
                Line($"mat4 {ModelViewMatrixName}{index} = {ViewMatrixName}{index} * {EEngineUniform.ModelMatrix};");
                Line($"mat4 {ModelViewProjMatrixName}{index} = {projMatrixName} * {ModelViewMatrixName}{index};");
                Line($"{finalPositionName} = {ModelViewProjMatrixName}{index} * {localInputPositionName};");
            }

            void AssignModelSpace()
                => Line($"{finalPositionName} = {EEngineUniform.ModelMatrix} * {localInputPositionName};");

            if (UseOVRMultiView || UseNVStereo)
                AssignCameraSpace();
            else
                IfElse(EEngineUniform.VRMode.ToString(), AssignModelSpace, AssignCameraSpace);
        }

        private void AssignFragPosOut(string localInputPositionName)
        {
            Line($"{FragPosName} = ({EEngineUniform.ModelMatrix} * {localInputPositionName}).xyz;");
        }

        private void Assign_GL_Position(string finalPositionName)
            => Line($"gl_Position = {finalPositionName};");
    }

    /// <summary>
    /// OVR MultiView variant of the mesh deform vertex shader generator.
    /// </summary>
    public class OVRMultiViewMeshDeformVertexShaderGenerator : MeshDeformVertexShaderGenerator
    {
        public OVRMultiViewMeshDeformVertexShaderGenerator(XRMesh mesh, int maxMeshInfluences = 8, bool optimizeToVec4 = true)
            : base(mesh, maxMeshInfluences, optimizeToVec4) { }

        public override bool UseOVRMultiView => true;
    }

    /// <summary>
    /// NV Stereo variant of the mesh deform vertex shader generator.
    /// </summary>
    public class NVStereoMeshDeformVertexShaderGenerator : MeshDeformVertexShaderGenerator
    {
        public NVStereoMeshDeformVertexShaderGenerator(XRMesh mesh, int maxMeshInfluences = 8, bool optimizeToVec4 = true)
            : base(mesh, maxMeshInfluences, optimizeToVec4) { }

        public override bool UseNVStereo => true;
    }
}
