using Extensions;
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
    /// <summary>
    /// Generates a typical vertex shader for use with most models.
    /// </summary>
    public class DefaultVertexShaderGenerator : ShaderGeneratorBase
    {
        public DefaultVertexShaderGenerator(XRMesh mesh) : base(mesh)
        {
            HelperMethodWriters.Add(WriteAdjointMethod);
            AddUniforms();
            AddOutputs();
            AddInputs();
        }

        private void AddInputs()
        {
            uint location = 0u;

            InputVars.Add(ECommonBufferType.Position.ToString(), (location++, EShaderVarType._vec3));

            if (Mesh.HasNormals)
                InputVars.Add(ECommonBufferType.Normal.ToString(), (location++, EShaderVarType._vec3));

            if (Mesh.HasTangents)
                InputVars.Add(ECommonBufferType.Tangent.ToString(), (location++, EShaderVarType._vec3));

            if (Mesh.HasTexCoords)
                for (uint i = 0; i < Mesh.TexCoordCount; ++i)
                    InputVars.Add($"{ECommonBufferType.TexCoord}{i}", (location++, EShaderVarType._vec2));

            if (Mesh.HasColors)
                for (uint i = 0; i < Mesh.ColorCount; ++i)
                    InputVars.Add($"{ECommonBufferType.Color}{i}", (location++, EShaderVarType._vec4));

            if (Mesh.HasSkinning && Engine.Rendering.Settings.AllowSkinning)
            {
                bool optimizeTo4Weights = Engine.Rendering.Settings.OptimizeSkinningTo4Weights || (Engine.Rendering.Settings.OptimizeSkinningWeightsIfPossible && Mesh.MaxWeightCount <= 4);
                if (optimizeTo4Weights)
                {
                    EShaderVarType intVecVarType = Engine.Rendering.Settings.UseIntegerUniformsInShaders
                        ? EShaderVarType._ivec4
                        : EShaderVarType._vec4;

                    InputVars.Add(ECommonBufferType.BoneMatrixOffset.ToString(), (location++, intVecVarType));
                    InputVars.Add(ECommonBufferType.BoneMatrixCount.ToString(), (location++, EShaderVarType._vec4));
                }
                else
                {
                    EShaderVarType intVarType = Engine.Rendering.Settings.UseIntegerUniformsInShaders
                        ? EShaderVarType._int
                        : EShaderVarType._float;

                    InputVars.Add(ECommonBufferType.BoneMatrixOffset.ToString(), (location++, intVarType));
                    InputVars.Add(ECommonBufferType.BoneMatrixCount.ToString(), (location++, intVarType));
                }
            }

            if (Mesh.BlendshapeCount > 0 && !Engine.Rendering.Settings.CalculateBlendshapesInComputeShader && Engine.Rendering.Settings.AllowBlendshapes)
            {
                EShaderVarType intVarType = Engine.Rendering.Settings.UseIntegerUniformsInShaders
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

            if (Mesh.HasTangents)
            {
                OutputVars.Add(FragTanName, (2, EShaderVarType._vec3));
                OutputVars.Add(FragBinormName, (3, EShaderVarType._vec3)); //Binormal is created in vertex shader if tangents exist
            }

            if (Mesh.HasTexCoords)
                for (int i = 0; i < Mesh.TexCoordCount.ClampMax(8); ++i)
                    OutputVars.Add(string.Format(FragUVName, i), (4u + (uint)i, EShaderVarType._vec2));

            if (Mesh.HasColors)
                for (int i = 0; i < Mesh.ColorCount.ClampMax(8); ++i)
                    OutputVars.Add(string.Format(FragColorName, i), (12u + (uint)i, EShaderVarType._vec4));

            OutputVars.Add(FragPosLocalName, (20, EShaderVarType._vec3)); //Local position in model space
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
                // ViewMatrix is the actual view matrix (InverseRenderMatrix from camera transform).
                // InverseViewMatrix is the camera's world transform (RenderMatrix), kept for compatibility.
                // Using ViewMatrix directly avoids single-precision inverse() computation in shader,
                // which can cause motion vector precision issues for distant objects.
                UniformNames.Add($"{EEngineUniform.ViewMatrix}{VertexUniformSuffix}", (EShaderVarType._mat4, false));
                UniformNames.Add($"{EEngineUniform.InverseViewMatrix}{VertexUniformSuffix}", (EShaderVarType._mat4, false));
                UniformNames.Add($"{EEngineUniform.ProjMatrix}{VertexUniformSuffix}", (EShaderVarType._mat4, false));
            }

            if (Mesh.SupportsBillboarding)
                UniformNames.Add(EEngineUniform.BillboardMode.ToString(), (EShaderVarType._int, false));

            if (!UseOVRMultiView && !UseNVStereo) //Include toggle for manual stereo VR calculations in shader if not using OVR multi-view or NV stereo
                UniformNames.Add(EEngineUniform.VRMode.ToString(), (EShaderVarType._bool, false));
        }

        //Buffers leaving the vertex shader for each vertex
        public const string FragPosLocalName = "FragPosLocal";
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

        public virtual bool UseOVRMultiView => false;
        public virtual bool UseNVStereo => false;

        private const string ViewMatrixName = "ViewMatrix";
        private const string ModelViewMatrixName = "mvMatrix";
        private const string ModelViewProjMatrixName = "mvpMatrix";
        private const string ViewProjMatrixName = "vpMatrix";
        private const string NormalMatrixName = "normalMatrix";

        protected override void WriteMain()
        {
            //Normal matrix is used to transform normals, tangents, and binormals in mesh transform calculations
            if (Mesh.HasNormals)
            {
                Line($"mat3 {NormalMatrixName} = adjoint({EEngineUniform.ModelMatrix});");
                Line();
            }

            //Transform position, normals and tangents
            WriteMeshTransforms(Mesh.HasSkinning && Engine.Rendering.Settings.AllowSkinning);
            if (UseNVStereo)
                Line("gl_Layer = 0;");

            // Ensure FragNorm is always initialized to a sensible default when normals are absent
            if (!Mesh.HasNormals)
                Line($"{FragNormName} = vec3(0.0f, 0.0f, 1.0f);");

            if (Mesh.ColorCount != 0)
                for (int i = 0; i < Mesh.ColorCount; ++i)
                    Line($"{string.Format(FragColorName, i)} = {ECommonBufferType.Color}{i};");

            if (Mesh.TexCoordCount != 0)
                for (int i = 0; i < Mesh.TexCoordCount; ++i)
                    Line($"{string.Format(FragUVName, i)} = {ECommonBufferType.TexCoord}{i};");
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
                Line("#extension GL_OVR_multiview2 : require");
                //multiview tess/geo extension is not supported on nvidia gpus (I assume because you should just use nv stereo)
                //Line("#extension GL_EXT_multiview_tessellation_geometry_shader : enable");
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

        public const string VertexUniformSuffix = "_VTX";

        protected override void WriteUniforms()
        {
            WriteUniformBufferBlocks();
            base.WriteUniforms();
        }

        /// <summary>
        /// Shader buffer objects
        /// </summary>
        private void WriteUniformBufferBlocks()
        {
            //These buffers have to be in this order to work - GPU boundary alignment is picky as f

            bool wroteAnything = false;

            int binding = 0;
            if (Mesh.BlendshapeCount > 0 && !Engine.Rendering.Settings.CalculateBlendshapesInComputeShader && Engine.Rendering.Settings.AllowBlendshapes)
            {
                EShaderVarType intVarType = Engine.Rendering.Settings.UseIntegerUniformsInShaders
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
            bool skinning = Mesh.HasSkinning && Engine.Rendering.Settings.AllowSkinning;
            if (skinning)
            {
                using (StartShaderStorageBufferBlock($"{ECommonBufferType.BoneMatrices}Buffer", binding++))
                    WriteUniform(EShaderVarType._mat4, ECommonBufferType.BoneMatrices.ToString(), true);

                using (StartShaderStorageBufferBlock($"{ECommonBufferType.BoneInvBindMatrices}Buffer", binding++))
                    WriteUniform(EShaderVarType._mat4, ECommonBufferType.BoneInvBindMatrices.ToString(), true);

                bool optimizeTo4Weights = Engine.Rendering.Settings.OptimizeSkinningTo4Weights || (Engine.Rendering.Settings.OptimizeSkinningWeightsIfPossible && Mesh.MaxWeightCount <= 4);
                if (!optimizeTo4Weights)
                {
                    using (StartShaderStorageBufferBlock($"{ECommonBufferType.BoneMatrixIndices}Buffer", binding++))
                        WriteUniform(EShaderVarType._int, ECommonBufferType.BoneMatrixIndices.ToString(), true);

                    using (StartShaderStorageBufferBlock($"{ECommonBufferType.BoneMatrixWeights}Buffer", binding++))
                        WriteUniform(EShaderVarType._float, ECommonBufferType.BoneMatrixWeights.ToString(), true);
                }

                wroteAnything = true;
            }

            if (wroteAnything)
                Line();
        }

        /// <summary>
        /// Calculates positions, and optionally normals, tangents, and binormals for a rigged mesh.
        /// </summary>
        private void WriteMeshTransforms(bool hasSkinning)
        {
            bool hasNormals = Mesh.HasNormals;
            bool hasTangents = Mesh.HasTangents;

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

        private bool NeedsSkinningCalc()
            => Mesh.HasSkinning && !Engine.Rendering.Settings.CalculateSkinningInComputeShader;

        private bool NeedsBlendshapeCalc()
            => Mesh.BlendshapeCount > 0 && !Engine.Rendering.Settings.CalculateBlendshapesInComputeShader;

        private bool WriteSkinningCalc()
        {
            if (Engine.Rendering.Settings.CalculateSkinningInComputeShader)
                return false;

            bool optimizeTo4Weights = Engine.Rendering.Settings.OptimizeSkinningTo4Weights || (Engine.Rendering.Settings.OptimizeSkinningWeightsIfPossible && Mesh.MaxWeightCount <= 4);
            if (optimizeTo4Weights)
            {
                Line($"for (int i = 0; i < 4; i++)");
                using (OpenBracketState())
                {
                    Line($"int boneIndex = int({ECommonBufferType.BoneMatrixOffset}[i]);");
                    Line($"float weight = {ECommonBufferType.BoneMatrixCount}[i];");
                    Line($"mat4 boneMatrix = {ECommonBufferType.BoneInvBindMatrices}[boneIndex] * {ECommonBufferType.BoneMatrices}[boneIndex];"); // * {EEngineUniform.RootInvModelMatrix}
                    Line($"{FinalPositionName} += (boneMatrix * vec4({BasePositionName}, 1.0f)) * weight;");
                    Line("mat3 boneMatrix3 = adjoint(boneMatrix);");
                    Line($"{FinalNormalName} += (boneMatrix3 * {BaseNormalName}) * weight;");
                    Line($"{FinalTangentName} += (boneMatrix3 * {BaseTangentName}) * weight;");
                }
            }
            else
            {
                Line($"for (int i = 0; i < int({ECommonBufferType.BoneMatrixCount}); i++)");
                using (OpenBracketState())
                {
                    Line($"int index = int({ECommonBufferType.BoneMatrixOffset}) + i;");
                    Line($"int boneIndex = int({ECommonBufferType.BoneMatrixIndices}[index]);");
                    Line($"float weight = {ECommonBufferType.BoneMatrixWeights}[index];");
                    Line($"mat4 boneMatrix = {ECommonBufferType.BoneInvBindMatrices}[boneIndex] * {ECommonBufferType.BoneMatrices}[boneIndex];"); // * {EEngineUniform.RootInvModelMatrix}
                    Line($"{FinalPositionName} += (boneMatrix * vec4({BasePositionName}, 1.0f)) * weight;");
                    Line("mat3 boneMatrix3 = adjoint(boneMatrix);");
                    Line($"{FinalNormalName} += (boneMatrix3 * {BaseNormalName}) * weight;");
                    Line($"{FinalTangentName} += (boneMatrix3 * {BaseTangentName}) * weight;");
                }
            }

            return true;
        }
        
        private bool WriteBlendshapeCalc()
        {
            if (Engine.Rendering.Settings.CalculateBlendshapesInComputeShader || Mesh.BlendshapeCount == 0 || !Engine.Rendering.Settings.AllowBlendshapes)
                return false;

            bool absolute = Engine.Rendering.Settings.UseAbsoluteBlendshapePositions;

            const string minWeight = "0.0001f";
            if (Mesh.MaxBlendshapeAccumulation)
            {
                // MAX blendshape accumulation
                Line("vec3 maxPositionDelta = vec3(0.0f);");
                Line("vec3 maxNormalDelta = vec3(0.0f);");
                Line("vec3 maxTangentDelta = vec3(0.0f);");
                Line($"for (int i = 0; i < int({ECommonBufferType.BlendshapeCount}.y); i++)");
                using (OpenBracketState())
                {
                    Line($"int index = int({ECommonBufferType.BlendshapeCount}.x) + i;");
                    if (Engine.Rendering.Settings.UseIntegerUniformsInShaders)
                        Line($"ivec4 blendshapeIndices = {ECommonBufferType.BlendshapeIndices}[index];");
                    else
                        Line($"vec4 blendshapeIndices = {ECommonBufferType.BlendshapeIndices}[index];");
                    Line($"int blendshapeIndex = int(blendshapeIndices.x);");
                    Line($"float weight = {ECommonBufferType.BlendshapeWeights}[blendshapeIndex];");
                    Line($"if (weight > {minWeight})");
                    using (OpenBracketState())
                    {
                        Line($"int blendshapeDeltaPosIndex = int(blendshapeIndices.y);");
                        Line($"int blendshapeDeltaNrmIndex = int(blendshapeIndices.z);");
                        Line($"int blendshapeDeltaTanIndex = int(blendshapeIndices.w);");
                        Line($"maxPositionDelta = max(maxPositionDelta, {ECommonBufferType.BlendshapeDeltas}[blendshapeDeltaPosIndex].xyz * weight);");
                        Line($"maxNormalDelta = max(maxNormalDelta, {ECommonBufferType.BlendshapeDeltas}[blendshapeDeltaNrmIndex].xyz * weight);");
                        Line($"maxTangentDelta = max(maxTangentDelta, {ECommonBufferType.BlendshapeDeltas}[blendshapeDeltaTanIndex].xyz * weight);");
                    }
                }
                Line($"{BasePositionName} += maxPositionDelta;");
                Line($"{BaseNormalName} += maxNormalDelta;");
                Line($"{BaseTangentName} += maxTangentDelta;");
            }
            else
            {
                Line($"for (int i = 0; i < int({ECommonBufferType.BlendshapeCount}.y); i++)");
                using (OpenBracketState())
                {
                    Line($"int index = int({ECommonBufferType.BlendshapeCount}.x) + i;");
                    if (Engine.Rendering.Settings.UseIntegerUniformsInShaders)
                        Line($"ivec4 blendshapeIndices = {ECommonBufferType.BlendshapeIndices}[index];");
                    else
                        Line($"vec4 blendshapeIndices = {ECommonBufferType.BlendshapeIndices}[index];");
                    Line($"int blendshapeIndex = int(blendshapeIndices.x);");
                    Line($"int blendshapeDeltaPosIndex = int(blendshapeIndices.y);");
                    Line($"int blendshapeDeltaNrmIndex = int(blendshapeIndices.z);");
                    Line($"int blendshapeDeltaTanIndex = int(blendshapeIndices.w);");
                    Line($"float weight = {ECommonBufferType.BlendshapeWeights}[blendshapeIndex];");
                    Line($"if (weight > {minWeight})");
                    using (OpenBracketState())
                    {
                        Line($"{BasePositionName} += {ECommonBufferType.BlendshapeDeltas}[blendshapeDeltaPosIndex].xyz * weight;");
                        Line($"{BaseNormalName} += ({ECommonBufferType.BlendshapeDeltas}[blendshapeDeltaNrmIndex].xyz * weight);");
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

                AssignFragPosOut(finalPosName);
                Assign_GL_Position(finalPosName);
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
                IfElse(EEngineUniform.VRMode.ToString(), AssignModelSpace, AssignCameraSpace);
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
                    IfElse(EEngineUniform.VRMode.ToString(), AssignModelSpace, AssignCameraSpace);
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
        /// Assigns fragment position out to the final position.
        /// Performs perspective divide here if not in VR.
        /// </summary>
        /// <param name="finalPositionName"></param>
        private void AssignFragPosOut(string finalPositionName)
        {
            void PerspDivide()
                => Line($"{FragPosName} = {finalPositionName}.xyz / {finalPositionName}.w;");

            void NoPerspDivide()
                => Line($"{FragPosName} = {finalPositionName}.xyz;");

            if (UseOVRMultiView || UseNVStereo)
                PerspDivide();
            else //No perspective divide in VR shaders - done in geometry shader
                IfElse(EEngineUniform.VRMode.ToString(), NoPerspDivide, PerspDivide);
        }

        /// <summary>
        /// Assigns gl_Position to the final position.
        /// </summary>
        /// <param name="finalPositionName"></param>
        private void Assign_GL_Position(string finalPositionName)
            => Line($"gl_Position = {finalPositionName};");

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
            // Use the precomputed ViewMatrix uniform for better precision in motion vectors
            // ViewMatrix uniform contains camera.Transform.InverseRenderMatrix, computed on CPU with higher precision
            string viewMatrixUniform = $"{EEngineUniform.ViewMatrix}{VertexUniformSuffix}";
            Line($"mat4 {viewMatrixName} = {viewMatrixUniform};");
            Line($"mat4 {viewProjMatrixName} = {projMatrixName} * {viewMatrixName};");
        }

        /// <summary>
        /// Creates the projection * view * model matrix.
        /// Uses ViewMatrix uniform directly (precomputed on CPU) for better precision.
        /// </summary>
        private void DeclareMVP(string viewMatrixName, string invViewMatrixName, string projMatrixName, string modelViewMatrixName, string modelViewProjMatrixName)
        {
            // Use the precomputed ViewMatrix uniform for better precision in motion vectors
            // ViewMatrix uniform contains camera.Transform.InverseRenderMatrix, computed on CPU with higher precision
            string viewMatrixUniform = $"{EEngineUniform.ViewMatrix}{VertexUniformSuffix}";
            Line($"mat4 {viewMatrixName} = {viewMatrixUniform};");
            Line($"mat4 {modelViewMatrixName} = {viewMatrixName} * {EEngineUniform.ModelMatrix};");
            Line($"mat4 {modelViewProjMatrixName} = {projMatrixName} * {modelViewMatrixName};");
        }
    }
}
