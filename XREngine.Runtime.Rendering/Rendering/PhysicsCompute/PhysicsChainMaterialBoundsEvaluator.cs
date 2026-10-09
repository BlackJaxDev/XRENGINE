using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Resolves the declared vertex-effect bounds contract of a material for the
/// GPU-owned physics-chain bound. The generated and Uber vertex shaders apply
/// these effects after compute skinning, in skinned space. Skinned draws use an
/// identity model matrix, so skinned space is world space.
/// </summary>
/// <remarks>
/// Translation-class effects add a bounded displacement to each vertex. Their
/// sum is a valid padding in any raster order. Scale, rotation, look-at, and
/// barrel effects multiply world positions about the world origin. A padding
/// cannot bound them, so those materials reject the contract. Results are
/// cached per material and recomputed when the parameter layout, a parameter
/// value, the shader set, or the Uber authored state changes. Material
/// animation invalidates the cached contract on its next publication. A custom vertex
/// or other position-producing shader stage must declare its displacement in
/// <c>_VertexConservativeBounds</c>.
/// </remarks>
public static class PhysicsChainMaterialBoundsEvaluator
{
    /// <summary>Reason text for a scale effect that is not identity.</summary>
    public const string ScaleRejection = "vertex-effect local scale is not identity";

    /// <summary>Reason text for a static or animated rotation effect.</summary>
    public const string RotationRejection = "vertex-effect rotation is active";

    /// <summary>Reason text for a camera look-at rotation effect.</summary>
    public const string LookAtRejection = "vertex-effect look-at rotation is active";

    /// <summary>Reason text for a barrel distortion effect.</summary>
    public const string BarrelRejection = "vertex-effect barrel distortion is active";

    /// <summary>Reason text for a non-finite effect parameter.</summary>
    public const string NonFiniteRejection = "a vertex-effect parameter is not finite";

    /// <summary>Reason text for an explicit shader without a bound declaration.</summary>
    public const string UndeclaredShaderRejection = "a position-producing shader has no declared vertex bound";

    /// <summary>Reason text when a published draw material has changed.</summary>
    public const string StalePublishedMaterialRejection = "the published draw material changed before bounds publication";

    private const float EffectEnabledThreshold = 0.0001f;
    private const float MinimumRoundingInterval = 0.0001f;
    private const float OutlineWidthScale = 0.01f;
    private const int OutlineDropShadowMode = 3;

    private static readonly ConditionalWeakTable<XRMaterial, ContractCache> Caches = new();

    /// <summary>
    /// Gets the cached contract for a material. A null material has no effects.
    /// The call allocates only when it first sees a material.
    /// </summary>
    public static PhysicsChainMaterialBoundsContract Evaluate(XRMaterial? material)
    {
        if (material is null)
            return default;

        ContractCache cache = Caches.GetValue(material, static _ => new ContractCache());
        lock (cache)
        {
            ulong layoutVersion = material.BindingLayoutVersion;
            ulong valueVersion = material.BindingValueVersion;
            long uberRevision = material.UberStateRevision;
            long shaderRevision = material.ShaderStateRevision;
            if (!cache.Valid
                || cache.LayoutVersion != layoutVersion
                || cache.ValueVersion != valueVersion
                || cache.UberRevision != uberRevision
                || cache.ShaderRevision != shaderRevision
                || !cache.MatchesShaders(material))
            {
                cache.CaptureShaders(material);
                PhysicsChainMaterialBoundsContract contract = Calculate(material);
                if (!cache.MatchesShaders(material)
                    || material.BindingLayoutVersion != layoutVersion
                    || material.BindingValueVersion != valueVersion
                    || material.UberStateRevision != uberRevision
                    || material.ShaderStateRevision != shaderRevision)
                {
                    cache.Valid = false;
                    return Reject(StalePublishedMaterialRejection);
                }
                cache.Contract = contract;
                cache.LayoutVersion = layoutVersion;
                cache.ValueVersion = valueVersion;
                cache.UberRevision = uberRevision;
                cache.ShaderRevision = shaderRevision;
                cache.Valid = true;
            }

            return cache.Contract;
        }
    }

    /// <summary>Calculates the contract from the current parameter values without the cache.</summary>
    public static PhysicsChainMaterialBoundsContract Calculate(XRMaterial material)
    {
        if (RequiresDeclaredBounds(material)
            && material.Parameter<ShaderVector4>("_VertexConservativeBounds") is null)
            return Reject(UndeclaredShaderRejection);

        float padding = 0.0f;
        Vector4 declared = GetVector4(material, "_VertexConservativeBounds");
        if (!IsFinite(declared))
            return Reject(NonFiniteRejection);

        float enabled = GetFloat(material, "_VertexEffectsEnabled");
        if (!float.IsFinite(enabled))
            return Reject(NonFiniteRejection);

        if (enabled > EffectEnabledThreshold)
        {
            Vector3 scale = GetVector3(material, "_VertexManipulationLocalScale", Vector3.One);
            Vector3 rotation = GetVector3(material, "_VertexManipulationLocalRotation", Vector3.Zero);
            Vector3 rotationSpeed = GetVector3(material, "_VertexManipulationLocalRotationSpeed", Vector3.Zero);
            Vector3 lookAtAxis = GetVector3(material, "_VertexLookAtAxis", Vector3.Zero);
            Vector3 localTranslation = GetVector3(material, "_VertexManipulationLocalTranslation", Vector3.Zero);
            Vector3 worldTranslation = GetVector3(material, "_VertexManipulationWorldTranslation", Vector3.Zero);
            Vector4 vertexColorOffset = GetVector4(material, "_VertexColorPositionOffset");
            Vector4 vertexColorNormalOffset = GetVector4(material, "_VertexColorNormalOffset");
            Vector4 glitch = GetVector4(material, "_VertexGlitch");
            Vector4 wave = GetVector4(material, "_VertexWave");
            Vector4 equation = GetVector4(material, "_VertexEquation");
            Vector4 depthBulge = GetVector4(material, "_VertexDepthBulge");
            float height = GetFloat(material, "_VertexManipulationHeight");
            float lookAtWeight = GetFloat(material, "_VertexLookAtWeight");
            float roundingEnabled = GetFloat(material, "_VertexRoundingEnabled");
            float roundingDivision = GetFloat(material, "_VertexRoundingDivision");
            float barrelMode = GetFloat(material, "_VertexBarrelMode");
            float barrelWidth = GetFloat(material, "_VertexBarrelWidth");
            float barrelAlpha = GetFloat(material, "_VertexBarrelAlpha");
            float barrelHeight = GetFloat(material, "_VertexBarrelHeight");
            if (!IsFinite(scale) || !IsFinite(rotation) || !IsFinite(rotationSpeed)
                || !IsFinite(lookAtAxis)
                || !IsFinite(localTranslation) || !IsFinite(worldTranslation)
                || !IsFinite(vertexColorOffset) || !IsFinite(vertexColorNormalOffset)
                || !IsFinite(glitch) || !IsFinite(wave)
                || !IsFinite(equation) || !IsFinite(depthBulge)
                || !float.IsFinite(height) || !float.IsFinite(lookAtWeight)
                || !float.IsFinite(roundingEnabled) || !float.IsFinite(roundingDivision)
                || !float.IsFinite(barrelMode) || !float.IsFinite(barrelWidth)
                || !float.IsFinite(barrelAlpha) || !float.IsFinite(barrelHeight))
                return Reject(NonFiniteRejection);

            // The shaders clamp each scale axis to 0.0001 before they apply it.
            if (Vector3.Max(Vector3.Abs(scale), new Vector3(0.0001f)) != Vector3.One)
                return Reject(ScaleRejection);

            if (rotation != Vector3.Zero || rotationSpeed != Vector3.Zero)
                return Reject(RotationRejection);
            if (MathF.Abs(lookAtWeight) > EffectEnabledThreshold)
                return Reject(LookAtRejection);
            if (barrelMode > 0.5f && barrelWidth != 0.0f)
                return Reject(BarrelRejection);

            Vector3 axes =
                Vector3.Abs(localTranslation)
                + Vector3.Abs(worldTranslation)
                // Vertex colors are normalized, so each color channel is at most one.
                + Vector3.Abs(new Vector3(vertexColorOffset.X, vertexColorOffset.Y, vertexColorOffset.Z))
                    * MathF.Abs(vertexColorOffset.W)
                + new Vector3(MathF.Abs(glitch.X), 0.0f, 0.0f);

            // Each of these terms moves the vertex along a unit normal, so it
            // moves each axis by at most its magnitude.
            float normalOffset =
                MathF.Abs(height)
                + MathF.Abs(wave.W)
                + MathF.Abs(equation.Z)
                + MathF.Abs(depthBulge.W);

            // Uber shaders scale translation terms by the enable weight.
            axes = (axes + new Vector3(normalOffset)) * MathF.Max(1.0f, enabled);

            // Rounding moves each axis by at most half of one interval.
            if (roundingEnabled > 0.5f)
                axes += new Vector3(0.5f * MathF.Max(
                    MathF.Abs(roundingDivision), MinimumRoundingInterval));

            if (!IsFinite(axes))
                return Reject(NonFiniteRejection);
            padding = MathF.Max(axes.X, MathF.Max(axes.Y, axes.Z));
        }

        // The declaration also applies when the built-in effects are disabled.
        padding += MathF.Max(MathF.Abs(declared.X), MathF.Max(MathF.Abs(declared.Y), MathF.Abs(declared.Z)))
            + MathF.Abs(declared.W);
        if (!float.IsFinite(padding))
            return Reject(NonFiniteRejection);

        if (material.IsUberFeatureEnabled("outline", defaultEnabled: false))
        {
            // This matches the CPU culling contract. Screen-space outline width
            // is not a world distance; covered CPU collection does not cull
            // the CPU-rendered outline command with this bound.
            float outline = MathF.Abs(GetFloat(material, "_OutlineWidth")) * OutlineWidthScale;
            if (GetInt(material, "_OutlineExpansionMode") == OutlineDropShadowMode)
            {
                Vector3 offset = Vector3.Abs(GetVector3(material, "_OutlineDropShadowOffset", Vector3.Zero));
                outline *= MathF.Max(1.0f, MathF.Max(offset.X, MathF.Max(offset.Y, offset.Z)));
            }

            if (!float.IsFinite(outline))
                return Reject(NonFiniteRejection);
            padding += outline;
            if (!float.IsFinite(padding))
                return Reject(NonFiniteRejection);
        }

        return new(padding, null);
    }

    private static PhysicsChainMaterialBoundsContract Reject(string reason)
        => new(0.0f, reason);

    private static bool RequiresDeclaredBounds(XRMaterial material)
    {
        for (int i = 0; i < material.Shaders.Count; ++i)
        {
            XRShader? shader = material.Shaders[i];
            if (shader is null)
                continue;
            switch (shader.Type)
            {
                case EShaderType.Vertex:
                    if (!ShaderHelper.HasTrustedUberVertexSource(shader))
                        return true;
                    break;
                case EShaderType.Geometry:
                case EShaderType.TessControl:
                case EShaderType.TessEvaluation:
                case EShaderType.Mesh:
                case EShaderType.Task:
                    return true;
            }
        }

        return false;
    }

    private static bool IsFinite(in Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(in Vector4 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y)
            && float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static float GetFloat(XRMaterial material, string name)
        => material.Parameter<ShaderFloat>(name)?.Value ?? 0.0f;

    private static int GetInt(XRMaterial material, string name)
        => material.Parameter<ShaderInt>(name)?.Value ?? 0;

    private static Vector3 GetVector3(XRMaterial material, string name, in Vector3 fallback)
        => material.Parameter<ShaderVector3>(name)?.Value ?? fallback;

    private static Vector4 GetVector4(XRMaterial material, string name)
        => material.Parameter<ShaderVector4>(name)?.Value ?? Vector4.Zero;

    private sealed class ContractCache
    {
        private readonly record struct ShaderWitness(XRShader? Shader, long SourceRevision,
            bool TrustedUberVertex);

        private ShaderWitness[] _shaders = [];
        private int _shaderCount;

        public bool Valid;
        public ulong LayoutVersion;
        public ulong ValueVersion;
        public long UberRevision;
        public long ShaderRevision;
        public PhysicsChainMaterialBoundsContract Contract;

        public void CaptureShaders(XRMaterial material)
        {
            int count = material.Shaders.Count;
            if (_shaders.Length < count)
                _shaders = new ShaderWitness[count];
            _shaderCount = count;
            for (int index = 0; index < count; ++index)
            {
                XRShader? shader = material.Shaders[index];
                _shaders[index] = new(shader, shader?.SourceRevision ?? 0L,
                    shader?.Type == EShaderType.Vertex &&
                    ShaderHelper.HasTrustedUberVertexSource(shader));
            }
        }

        public bool MatchesShaders(XRMaterial material)
        {
            if (material.Shaders.Count != _shaderCount)
                return false;
            for (int index = 0; index < _shaderCount; ++index)
            {
                XRShader? shader = material.Shaders[index];
                ShaderWitness witness = _shaders[index];
                if (!ReferenceEquals(shader, witness.Shader) ||
                    (shader?.SourceRevision ?? 0L) != witness.SourceRevision ||
                    (shader?.Type == EShaderType.Vertex &&
                     ShaderHelper.HasTrustedUberVertexSource(shader)) != witness.TrustedUberVertex)
                    return false;
            }
            return true;
        }
    }
}
