using XREngine.Data.Rendering;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

/// <summary>Captures explicit material behavior without inferring shader semantics from names or live backend state.</summary>
public static class AdvancedMaterialSourceContract
{
    public static EAdvancedMaterialSourceContract Classify(XRMaterial? material)
    {
        if (material is null) return EAdvancedMaterialSourceContract.Unclassified;
        if (material.TessCtrlShaders.Count != 0 || material.TessEvalShaders.Count != 0)
            return EAdvancedMaterialSourceContract.TessellatedVertexProgram;
        if (material.GeometryShaders.Count != 0 || material.MeshShaders.Count != 0 || material.TaskShaders.Count != 0)
            return EAdvancedMaterialSourceContract.TopologyChangingProgram;
        if (material.BillboardMode != EMeshBillboardMode.None || material.HasSettingVertexUniformHandlers)
            return EAdvancedMaterialSourceContract.CustomVertexProgram;
        if (material is AdvancedProjectiveMirrorMaterial) return EAdvancedMaterialSourceContract.ProjectiveMirror;
        foreach (MaterialPassDefinition pass in material.PassSet.Passes)
        {
            if (!pass.Enabled) continue;
            if (!string.IsNullOrWhiteSpace(pass.VertexShaderPath)) return EAdvancedMaterialSourceContract.CustomVertexProgram;
            if (!string.IsNullOrWhiteSpace(pass.FragmentShaderPath)) return EAdvancedMaterialSourceContract.CustomSurfaceProgram;
        }
        EngineMaterialSemanticIdentity semantic = material.EngineSemantic;
        if (semantic.IsUnlit() && EngineUnlitNativeAdmission.TryRead(material, out EngineUnlitSurface unlit, out _))
            return unlit.TransparencyMode == (semantic == EngineMaterialSemanticIdentity.UnlitAlphaTextureV4
                    ? ETransparencyMode.Masked : ETransparencyMode.Opaque)
                ? EAdvancedMaterialSourceContract.EngineGeneratedSurface
                : EAdvancedMaterialSourceContract.CustomSurfaceProgram;
        if (semantic == EngineMaterialSemanticIdentity.UberBaseV1 && EngineUberBaseNativeAdmission.TryRead(material, out _, out _))
            return EAdvancedMaterialSourceContract.UberBaseSurface;
        if (material.Shaders.Count == 0 &&
            (semantic == EngineMaterialSemanticIdentity.StandardLitColorV1 ||
             semantic == EngineMaterialSemanticIdentity.StandardLitColorV2 ||
             semantic == EngineMaterialSemanticIdentity.StandardLitTextureV1))
            return EAdvancedMaterialSourceContract.StandardSurface;
        if (semantic.IsAuthoredLit() && material.Shaders.Count is 1 or 2)
        {
            if (semantic == EngineMaterialSemanticIdentity.AuthoredLitTextureAlphaV1 &&
                EngineAuthoredLitNativeAdmission.TryReadTexturedAlpha(material, out _, out _))
                return EAdvancedMaterialSourceContract.EngineGeneratedSurface;
            if (semantic == EngineMaterialSemanticIdentity.AuthoredLitTexturedV1 &&
                EngineAuthoredLitNativeAdmission.TryReadAuthoredTextured(material, out _, out _))
                return EAdvancedMaterialSourceContract.EngineGeneratedSurface;
            if (EngineAuthoredLitNativeAdmission.TryRead(material, out _, out _, out _, out _))
                return EAdvancedMaterialSourceContract.EngineGeneratedSurface;
            string? identity = null;
            int fragments = 0;
            bool exact = true;
            for (int index = 0; index < material.Shaders.Count; index++)
            {
                XRShader shader = material.Shaders[index];
                if (shader.Type is not (EShaderType.Vertex or EShaderType.Fragment) ||
                    shader.CookedArtifactIdentity is null || identity is not null && identity != shader.CookedArtifactIdentity)
                    exact = false;
                identity = shader.CookedArtifactIdentity;
                if (shader.Type == EShaderType.Fragment) fragments++;
            }
            if (exact && fragments == 1) return EAdvancedMaterialSourceContract.AuthoredStandardSurface;
        }
        if (material.VertexShaders.Count != 0) return EAdvancedMaterialSourceContract.CustomVertexProgram;
        return material.Shaders.Count != 0 ? EAdvancedMaterialSourceContract.CustomSurfaceProgram :
            EAdvancedMaterialSourceContract.Unclassified;
    }
}
