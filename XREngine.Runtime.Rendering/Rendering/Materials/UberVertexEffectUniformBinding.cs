using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Shaders.Parameters;

namespace XREngine.Rendering.Materials;

/// <summary>
/// Caches the small vertex-effect parameter set once and uploads it to every
/// vertex program used for the mesh, including depth, shadow, and velocity
/// overrides.
/// </summary>
public sealed class UberVertexEffectUniformBinding
{
    private static readonly string[] ParameterNames =
    [
        "_VertexEffectsEnabled",
        "_VertexManipulationLocalTranslation",
        "_VertexManipulationLocalRotation",
        "_VertexManipulationLocalRotationSpeed",
        "_VertexManipulationLocalScale",
        "_VertexManipulationWorldTranslation",
        "_VertexManipulationHeight",
        "_VertexRoundingEnabled",
        "_VertexRoundingDivision",
        "_VertexBarrelMode",
        "_VertexBarrelWidth",
        "_VertexBarrelAlpha",
        "_VertexBarrelHeight",
        "_VertexLookAtWeight",
        "_VertexLookAtAxis",
        "_VertexGlitch",
        "_VertexWave",
        "_VertexEquation",
        "_VertexDepthBulge",
        "_VertexColorPositionOffset",
        "_VertexColorNormalOffset",
        "_VertexConservativeBounds",
        "_OutlineWidth",
        "_OutlineExpansionMode",
        "_OutlineSpace",
        "_OutlinePersonaDirection",
        "_OutlineDropShadowOffset",
        "_OutlineFixedSize",
        "_OutlineUseVertexColors",
        "_OutlineZOffset",
    ];

    private readonly ShaderVar[] _parameters;

    public UberVertexEffectUniformBinding(XRMaterial material)
    {
        List<ShaderVar> parameters = new(ParameterNames.Length);
        for (int i = 0; i < ParameterNames.Length; ++i)
        {
            ShaderVar? parameter = material.Parameter<ShaderVar>(ParameterNames[i]);
            if (parameter is not null)
                parameters.Add(parameter);
        }
        _parameters = [.. parameters];
    }

    public void Apply(XRRenderProgram program)
    {
        for (int i = 0; i < _parameters.Length; ++i)
            _parameters[i].SetUniform(program);
    }
}
