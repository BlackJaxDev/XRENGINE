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
    public class DefaultVertexShaderGenerator(XRMesh mesh) : ShaderGeneratorBase(mesh)
    {
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

        /// <summary>
        /// Adjoint is a faster way to calculate the inverse of a matrix when the matrix is orthogonal.
        /// </summary>
        private void WriteAdjointMethod()
        {
            Line("mat3 adjoint(mat4 m)");
            using (OpenBracketState())
            {
                Line("return mat3(");
                Line("  cross(m[1].xyz, m[2].xyz),");
                Line("  cross(m[2].xyz, m[0].xyz),");
                Line("  cross(m[0].xyz, m[1].xyz));");
            }
        }

        private const string ViewMatrixName = "ViewMatrix";
        private const string ModelViewMatrixName = "mvMatrix";
        private const string ModelViewProjMatrixName = "mvpMatrix";
        private const string ViewProjMatrixName = "vpMatrix";
        private const string NormalMatrixName = "normalMatrix";

        /// <summary>
        /// Creates the vertex shader to render a typical model.
        /// </summary>
        /// <param name="info"></param>
        /// <param name="allowMeshMorphing"></param>
        /// <param name="useMorphMultiRig"></param>
        /// <param name="allowColorMorphing"></param>
        /// <returns></returns>
        public override string Generate()
        {
            WriteVersion();
            WriteExtensions();
            Line();
            WriteInputs();
            WriteAdjointMethod();
            using (StartMain())
            {
                //Normal matrix is used to transform normals, tangents, and binormals in mesh transform calculations
                if (Mesh.NormalsBuffer is not null)
                {
                    Line($"mat3 {NormalMatrixName} = adjoint({EEngineUniform.ModelMatrix});");
                    Line();
                }

                //Transform position, normals and tangents
                WriteMeshTransforms(Mesh.HasSkinning && Engine.Rendering.Settings.AllowSkinning);

                WriteColorOutputs();
                WriteTexCoordOutputs();
            }
            return End();
        }

        private void WriteExtensions()
        {
            if (UseOVRMultiView)
            {
                Line("#extension GL_OVR_multiview2 : require");
                //Line("#extension GL_EXT_multiview_tessellation_geometry_shader : enable");
            }
        }

        private void WriteInputs()
        {
            if (UseOVRMultiView)
                Line("layout(num_views = 2) in;");

            WriteBuffers();
            WriteBufferBlocks();
            WriteUniforms();
            WriteOutData();

            //For some reason, this is necessary when using shader pipelines
            if (Engine.Rendering.Settings.AllowShaderPipelines)
                WriteGLPerVertexOut();
        }

        private void WriteTexCoordOutputs()
        {
            if (Mesh.TexCoordBuffers is null)
                return;

            for (int i = 0; i < Mesh.TexCoordBuffers.Length; ++i)
                Line($"{string.Format(FragUVName, i)} = {ECommonBufferType.TexCoord}{i};");
        }

        private void WriteColorOutputs()
        {
            if (Mesh.ColorBuffers is null)
                return;

            for (int i = 0; i < Mesh.ColorBuffers.Length; ++i)
                Line($"{string.Format(FragColorName, i)} = {ECommonBufferType.Color}{i};");
        }

        private void WriteBuffers()
        {
            //uint blendshapeCount = Mesh.BlendshapeCount;
            uint location = 0u;

            WriteInVar(location++, EShaderVarType._vec3, ECommonBufferType.Position.ToString());

            if (Mesh.NormalsBuffer is not null)
                WriteInVar(location++, EShaderVarType._vec3, ECommonBufferType.Normal.ToString());

            if (Mesh.TangentsBuffer is not null)
                WriteInVar(location++, EShaderVarType._vec3, ECommonBufferType.Tangent.ToString());

            if (Mesh.TexCoordBuffers is not null)
                for (uint i = 0; i < Mesh.TexCoordBuffers.Length; ++i)
                    WriteInVar(location++, EShaderVarType._vec2, $"{ECommonBufferType.TexCoord}{i}");

            if (Mesh.ColorBuffers is not null)
                for (uint i = 0; i < Mesh.ColorBuffers.Length; ++i)
                    WriteInVar(location++, EShaderVarType._vec4, $"{ECommonBufferType.Color}{i}");

            if (Mesh.HasSkinning && Engine.Rendering.Settings.AllowSkinning)
            {
                bool optimizeTo4Weights = Engine.Rendering.Settings.OptimizeSkinningTo4Weights || (Engine.Rendering.Settings.OptimizeSkinningWeightsIfPossible && Mesh.MaxWeightCount <= 4);
                if (optimizeTo4Weights)
                {
                    EShaderVarType intVecVarType = Engine.Rendering.Settings.UseIntegerUniformsInShaders
                        ? EShaderVarType._ivec4
                        : EShaderVarType._vec4;

                    WriteInVar(location++, intVecVarType, ECommonBufferType.BoneMatrixOffset.ToString());
                    WriteInVar(location++, EShaderVarType._vec4, ECommonBufferType.BoneMatrixCount.ToString());
                }
                else
                {
                    EShaderVarType intVarType = Engine.Rendering.Settings.UseIntegerUniformsInShaders
                        ? EShaderVarType._int
                        : EShaderVarType._float;

                    WriteInVar(location++, intVarType, ECommonBufferType.BoneMatrixOffset.ToString());
                    WriteInVar(location++, intVarType, ECommonBufferType.BoneMatrixCount.ToString());
                }
            }
            if (Mesh.BlendshapeCount > 0 && !Engine.Rendering.Settings.CalculateBlendshapesInComputeShader && Engine.Rendering.Settings.AllowBlendshapes)
            {
                EShaderVarType intVarType = Engine.Rendering.Settings.UseIntegerUniformsInShaders
                    ? EShaderVarType._ivec2
                    : EShaderVarType._vec2;

                WriteInVar(location++, intVarType, ECommonBufferType.BlendshapeCount.ToString());
            }
            Line();
        }

        private void WriteUniforms()
        {
            WriteUniform(EShaderVarType._mat4, EEngineUniform.ModelMatrix.ToString());

            if (UseOVRMultiView)
            {
                WriteUniform(EShaderVarType._mat4, EEngineUniform.LeftEyeInverseViewMatrix.ToString());
                WriteUniform(EShaderVarType._mat4, EEngineUniform.RightEyeInverseViewMatrix.ToString());
                WriteUniform(EShaderVarType._mat4, EEngineUniform.LeftEyeProjMatrix.ToString());
                WriteUniform(EShaderVarType._mat4, EEngineUniform.RightEyeProjMatrix.ToString());
            }
            else
            {
                WriteUniform(EShaderVarType._mat4, EEngineUniform.InverseViewMatrix.ToString());
                WriteUniform(EShaderVarType._mat4, EEngineUniform.ProjMatrix.ToString());
            }

            //WriteUniform(EShaderVarType._vec3, EEngineUniform.CameraPosition.ToString());
            //WriteUniform(EShaderVarType._vec3, EEngineUniform.CameraForward.ToString());
            //WriteUniform(EShaderVarType._vec3, EEngineUniform.CameraUp.ToString());
            //WriteUniform(EShaderVarType._vec3, EEngineUniform.CameraRight.ToString());

            if (Mesh.SupportsBillboarding)
                WriteUniform(EShaderVarType._int, EEngineUniform.BillboardMode.ToString());

            if (!UseOVRMultiView) //Include toggle for manual stereo VR calculations in shader if not using OVR multi-view
                WriteUniform(EShaderVarType._bool, EEngineUniform.VRMode.ToString());

            //if (Mesh.HasSkinning && Engine.Rendering.Settings.AllowSkinning)
            //    WriteUniform(EShaderVarType._mat4, EEngineUniform.RootInvModelMatrix.ToString());

            Line();
        }

        /// <summary>
        /// Shader buffer objects
        /// </summary>
        private void WriteBufferBlocks()
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
        /// This information is sent to the fragment shader.
        /// </summary>
        private void WriteOutData()
        {
            WriteOutVar(0, EShaderVarType._vec3, FragPosName);

            if (Mesh.NormalsBuffer is not null)
                WriteOutVar(1, EShaderVarType._vec3, FragNormName);

            if (Mesh.TangentsBuffer is not null)
            {
                WriteOutVar(2, EShaderVarType._vec3, FragTanName);
                WriteOutVar(3, EShaderVarType._vec3, FragBinormName);
            }

            if (Mesh.TexCoordBuffers is not null)
                for (int i = 0; i < Mesh.TexCoordBuffers.Length.ClampMax(8); ++i)
                    WriteOutVar(4 + i, EShaderVarType._vec2, string.Format(FragUVName, i));

            if (Mesh.ColorBuffers is not null)
                for (int i = 0; i < Mesh.ColorBuffers.Length.ClampMax(8); ++i)
                    WriteOutVar(12 + i, EShaderVarType._vec4, string.Format(FragColorName, i));

            WriteOutVar(20, EShaderVarType._vec3, FragPosLocalName);

            Line();
        }

        /// <summary>
        /// Calculates positions, and optionally normals, tangents, and binormals for a rigged mesh.
        /// </summary>
        private void WriteMeshTransforms(bool hasSkinning)
        {
            bool hasNormals = Mesh.NormalsBuffer is not null;
            bool hasTangents = Mesh.TangentsBuffer is not null;

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

        ///// <summary>
        ///// Calculates positions, and optionally normals, tangents, and binormals for a static mesh.
        ///// </summary>
        //private void WriteStaticMeshInputs()
        //{
        //    Line($"vec4 position = vec4({ECommonBufferType.Position}, 1.0f);");
        //    if (Mesh.NormalsBuffer is not null)
        //        Line($"vec3 normal = {ECommonBufferType.Normal};");
        //    if (Mesh.TangentsBuffer is not null)
        //        Line($"vec3 tangent = {ECommonBufferType.Tangent};");
        //    Line();

        //    bool wroteBlendshapes = WriteBlendshapeCalc();
        //    if (!wroteBlendshapes)
        //    {
        //        Line("vec4 finalPosition = position;");
        //        if (Mesh.NormalsBuffer is not null)
        //            Line("vec3 finalNormal = normal;");
        //        if (Mesh.TangentsBuffer is not null)
        //            Line("vec3 finalTangent = tangent;");
        //    }

        //    ResolvePosition("position");

        //    if (Mesh.NormalsBuffer is not null)
        //    {
        //        Line($"{FragNormName} = normalize(normalMatrix * normal);");
        //        if (Mesh.TangentsBuffer is not null)
        //        {
        //            Line($"{FragTanName} = normalize(normalMatrix * tangent);");
        //            Line("vec3 binormal = cross(normal, tangent);");
        //            Line($"{FragBinormName} = normalize(normalMatrix * binormal);");
        //        }
        //    }
        //}

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

            if (UseOVRMultiView)
            {
                Line("bool leftEye = gl_ViewID_OVR == 0;");
                Line($"mat4 {EEngineUniform.InverseViewMatrix} = leftEye ? {EEngineUniform.LeftEyeInverseViewMatrix} : {EEngineUniform.RightEyeInverseViewMatrix};");
                Line($"mat4 {EEngineUniform.ProjMatrix} = leftEye ? {EEngineUniform.LeftEyeProjMatrix} : {EEngineUniform.RightEyeProjMatrix};");
            }

            const string finalPosName = "outPos";

            DeclareAndAssignFinalPosition(localInputPosName, finalPosName);
            AssignFragPosOut(finalPosName);
            AssignGL_Position(finalPosName);
        }

        private void BillboardCalc(string posName, string glPosName)
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

            Line($"vec3 {camPositionName} = {EEngineUniform.InverseViewMatrix}[3].xyz;");
            Line($"vec3 {camForwardName} = normalize({EEngineUniform.InverseViewMatrix}[2].xyz);");

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

            void AssignNoVR()
            {
                DeclareVP();
                Line($"{glPosName} = {ViewProjMatrixName} * {rotatedWorldPosName};");
            }

            //VR shaders will multiply the view and projection matrices in the geometry shader
            void AssignVR()
                => Line($"{glPosName} = {rotatedWorldPosName};");

            if (UseOVRMultiView)
                AssignNoVR(); //Multiply by view and projection right now - not in geometry shader
            else
                IfElse(EEngineUniform.VRMode.ToString(), AssignVR, AssignNoVR);
        }

        /// <summary>
        /// Declares and assigns the final position to the local input position, optionally transformed by billboarding.
        /// Transformed by the model matrix, and by the view and projection matrices if not in VR.
        /// </summary>
        /// <param name="localInputPositionName"></param>
        /// <param name="finalPositionName"></param>
        private void DeclareAndAssignFinalPosition(string localInputPositionName, string finalPositionName)
        {
            Line($"vec4 {finalPositionName};");

            void AssignNoVR()
            {
                DeclareMVP();
                Line($"{finalPositionName} = {ModelViewProjMatrixName} * {localInputPositionName};");
            }

            //VR shaders will multiply the view and projection matrices in the geometry shader
            void AssignVR()
                => Line($"{finalPositionName} = {EEngineUniform.ModelMatrix} * {localInputPositionName};");

            void NoBillboardCalc()
            {
                if (UseOVRMultiView)
                    AssignNoVR(); //Multiply by view and projection right now - not in geometry shader
                else
                    IfElse(EEngineUniform.VRMode.ToString(), AssignVR, AssignNoVR);
            }

            if (Mesh.SupportsBillboarding)
                IfElse($"{EEngineUniform.BillboardMode} != 0", () => BillboardCalc(localInputPositionName, finalPositionName), NoBillboardCalc);
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

            if (UseOVRMultiView)
                PerspDivide();
            else //No perspective divide in VR shaders - done in geometry shader
                IfElse(EEngineUniform.VRMode.ToString(), NoPerspDivide, PerspDivide);
        }

        /// <summary>
        /// Assigns gl_Position to the final position.
        /// </summary>
        /// <param name="finalPositionName"></param>
        private void AssignGL_Position(string finalPositionName)
            => Line($"gl_Position = {finalPositionName};");

        /// <summary>
        /// Creates the projection * view matrix.
        /// </summary>
        private void DeclareVP(/*bool stereoLeft*/)
        {
            //if (UseOVRMultiView)
            //{
            //    Line($"mat4 {ViewMatrixName} = inverse({(stereoLeft ? EEngineUniform.LeftEyeInverseViewMatrix : EEngineUniform.RightEyeInverseViewMatrix)});");
            //    Line($"mat4 {ViewProjMatrixName} = {(stereoLeft ? EEngineUniform.LeftEyeProjMatrix : EEngineUniform.RightEyeProjMatrix)} * {ViewMatrixName};");
            //}
            //else
            //{
                Line($"mat4 {ViewMatrixName} = inverse({EEngineUniform.InverseViewMatrix});");
                Line($"mat4 {ViewProjMatrixName} = {EEngineUniform.ProjMatrix} * {ViewMatrixName};");
            //}
        }

        /// <summary>
        /// Creates the projection * view * model matrix.
        /// </summary>
        private void DeclareMVP(/*bool stereoLeft*/)
        {
            //if (UseOVRMultiView)
            //{
            //    Line($"mat4 {ViewMatrixName} = inverse({(stereoLeft ? EEngineUniform.LeftEyeInverseViewMatrix : EEngineUniform.RightEyeInverseViewMatrix)});");
            //    Line($"mat4 {ModelViewMatrixName} = {ViewMatrixName} * {EEngineUniform.ModelMatrix};");
            //    Line($"mat4 {ModelViewProjMatrixName} = {(stereoLeft ? EEngineUniform.LeftEyeProjMatrix : EEngineUniform.RightEyeProjMatrix)} * {ModelViewMatrixName};");
            //}
            //else
            //{
                Line($"mat4 {ViewMatrixName} = inverse({EEngineUniform.InverseViewMatrix});");
                Line($"mat4 {ModelViewMatrixName} = {ViewMatrixName} * {EEngineUniform.ModelMatrix};");
                Line($"mat4 {ModelViewProjMatrixName} = {EEngineUniform.ProjMatrix} * {ModelViewMatrixName};");
            //}
        }
    }
}
