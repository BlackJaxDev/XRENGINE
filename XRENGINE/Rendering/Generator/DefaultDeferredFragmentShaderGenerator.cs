using Extensions;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Shaders.Generator
{
    public class DefaultDeferredFragmentShaderGenerator(XRMesh mesh) : ShaderGeneratorBase(mesh)
    {
        public string? AlbedoTextureUniformName { get; set; }
        public string? NormalTextureUniformName { get; set; }
        public string? RoughnessTextureUniformName { get; set; }
        public string? MetallicTextureUniformName { get; set; }
        public string? EmissionTextureUniformName { get; set; }
        public string? SpecularTextureUniformName { get; set; }
        public string? OpacityTextureUniformName { get; set; }
        public string? AmbientOcclusionTextureUniformName { get; set; }

        protected override void WriteMain()
        {
            Line("Normal = normalize(FragNorm);");
            Line("AlbedoOpacity = vec4(BaseColor, Opacity);");
            Line("RMSE = vec4(Roughness, Metallic, Specular, Emission);");
        }

        protected override void WriteExtensions()
        {

        }

        protected override void WriteOutputs()
        {
            WriteOutVar(0, EShaderVarType._vec4, "AlbedoOpacity");
            WriteOutVar(1, EShaderVarType._vec3, "Normal");
            WriteOutVar(2, EShaderVarType._vec4, "RMSE");
        }

        protected override void WriteInputs()
        {
            WriteInVar(0, EShaderVarType._vec3, DefaultVertexShaderGenerator.FragPosName);

            if (Mesh.HasNormals)
                WriteInVar(1, EShaderVarType._vec3, DefaultVertexShaderGenerator.FragNormName);

            if (Mesh.HasTangents)
            {
                WriteInVar(2, EShaderVarType._vec3, DefaultVertexShaderGenerator.FragTanName);
                WriteInVar(3, EShaderVarType._vec3, DefaultVertexShaderGenerator.FragBinormName);
            }

            if (Mesh.HasTexCoords)
                for (int i = 0; i < Mesh.TexCoordCount.ClampMax(8); ++i)
                    WriteInVar(4u + (uint)i, EShaderVarType._vec2, string.Format(DefaultVertexShaderGenerator.FragUVName, i));

            if (Mesh.HasColors)
                for (int i = 0; i < Mesh.ColorCount.ClampMax(8); ++i)
                    WriteInVar(12u + (uint)i, EShaderVarType._vec4, string.Format(DefaultVertexShaderGenerator.FragColorName, i));

            WriteInVar(20, EShaderVarType._vec3, DefaultVertexShaderGenerator.FragPosLocalName);
        }

        protected override void WriteUniforms()
        {
            WriteUniform(EShaderVarType._vec3, "BaseColor");
            WriteUniform(EShaderVarType._float, "Opacity");
            WriteUniform(EShaderVarType._float, "Specular");
            WriteUniform(EShaderVarType._float, "Roughness");
            WriteUniform(EShaderVarType._float, "Metallic");
            WriteUniform(EShaderVarType._float, "Emission");

            if (AlbedoTextureUniformName != null)
                WriteUniform(EShaderVarType._sampler2D, AlbedoTextureUniformName);

            if (NormalTextureUniformName != null)
                WriteUniform(EShaderVarType._sampler2D, NormalTextureUniformName);

            if (RoughnessTextureUniformName != null)
                WriteUniform(EShaderVarType._sampler2D, RoughnessTextureUniformName);

            if (MetallicTextureUniformName != null)
                WriteUniform(EShaderVarType._sampler2D, MetallicTextureUniformName);

            if (EmissionTextureUniformName != null)
                WriteUniform(EShaderVarType._sampler2D, EmissionTextureUniformName);

            if (SpecularTextureUniformName != null)
                WriteUniform(EShaderVarType._sampler2D, SpecularTextureUniformName);

            if (OpacityTextureUniformName != null)
                WriteUniform(EShaderVarType._sampler2D, OpacityTextureUniformName);

            if (AmbientOcclusionTextureUniformName != null)
                WriteUniform(EShaderVarType._sampler2D, AmbientOcclusionTextureUniformName);
        }
    }
}
