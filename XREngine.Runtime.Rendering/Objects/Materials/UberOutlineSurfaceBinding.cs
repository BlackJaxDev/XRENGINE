using System.Numerics;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Shaders.Parameters;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering;

/// <summary>
/// Binds a canonical outline to its original material. Cached references detect public array and
/// name edits while current ShaderVar values and supported sampler edits remain live without warmed allocations.
/// </summary>
public sealed class UberOutlineSurfaceBinding
{
    private readonly XRMaterial _source;
    private readonly UberOutlineMaterialProfile _profile;
    private UberOutlineTextureBinding[]? _requiredTextureBindings;
    private ShaderVar[]? _parameters;
    private ShaderVar?[] _parameterReferences = [];
    private string?[] _parameterNames = [];
    private readonly ShaderVar?[] _members = new ShaderVar?[UberOutlineProgramContract.SourceMemberCount];
    private ShaderFloat? _vertexEffectsEnabled;

    private UberOutlineSurfaceBinding(XRMaterial source, UberOutlineMaterialProfile profile)
    {
        _source = source;
        _profile = profile;
    }

    public uint Features => _profile.Features;
    public bool RenderTimeEnabled => _profile.RenderTimeEnabled;
    public ReadOnlySpan<UberOutlineTextureBinding> RequiredTextures => _requiredTextureBindings ?? _profile.TextureBindings;

    public static bool TryCreate(XRMaterial source, UberOutlineMaterialProfile profile,
        out UberOutlineSurfaceBinding? binding, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(profile);
        UberOutlineSurfaceBinding candidate = new(source, profile);
        if (!candidate.TryValidate(out reason))
        {
            binding = null;
            return false;
        }
        binding = candidate;
        return true;
    }

    /// <summary>Checks modeled feature admission without inspecting shader files or assuming callbacks survive cooking.</summary>
    public static bool TryAdmitAuthoredSource(XRMaterial source, out uint features,
        out bool renderTimeEnabled, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!TryValidateCommonSource(source, out features, out renderTimeEnabled, out reason))
            return false;
        ReadOnlySpan<ShaderAbiMemberContract> schema = UberOutlineProgramContract.SourceMembers;
        for (int memberIndex = 0; memberIndex < schema.Length; memberIndex++)
        {
            if (!UberOutlineProgramContract.IsSourceMemberActive(memberIndex, features)) continue;
            ShaderAbiMemberContract member = schema[memberIndex];
            ShaderVar? found = null;
            foreach (ShaderVar? parameter in source.Parameters)
            {
                if (parameter?.Name != member.ProviderName) continue;
                if (found is not null)
                {
                    reason = $"UberOutline.ParameterDuplicate: active field '{member.ProviderName}' must occur exactly once.";
                    return false;
                }
                found = parameter;
            }
            if (!TryValidateValue(found, member, out reason)) return false;
        }
        return TryValidateVertexEffects(source.Parameters, out _, out reason);
    }

    /// <summary>Validates current values, original texture roles, and in-place layout changes without per-draw asset or descriptor parsing.</summary>
    public bool TryValidate(out string? reason)
    {
        if (!TryValidateCommonSource(_source, out uint features, out bool renderTime, out reason)) return false;
        if (features != _profile.Features || renderTime != _profile.RenderTimeEnabled)
        {
            reason = "UberOutline.FeatureProfileChanged: the authored pre-outline feature closure or render-time mode requires a new cooked profile.";
            return false;
        }
        if (!_profile.TryValidateTextureTable(_source, out reason)) return false;
        if (_requiredTextureBindings is null)
            _requiredTextureBindings = (UberOutlineTextureBinding[])_profile.TextureBindings.Clone();
        else
            for (int index = 0; index < _requiredTextureBindings.Length; index++)
                if (!ReferenceEquals(_requiredTextureBindings[index], _profile.TextureBindings[index]))
                {
                    reason = "UberOutline.TextureProfileChanged: replacing a required source-slot mapping requires a new binding and owned outline material.";
                    return false;
                }
        for (int index = 0; index < _profile.TextureBindings.Length; index++)
        {
            XRTexture2D texture = _profile.TextureBindings[index].Texture;
            try
            {
                // This is a value-type capture. It validates current image/sampler state while allowing
                // supported live sampler changes to flow through ordinary resource generations.
                PublishedStandardLitTextureSettings settings = PublishedStandardLitTextureSettings.Capture(texture);
                if (settings.EnableComparison)
                {
                    reason = "UberOutline.ComparisonSamplerUnsupported: canonical outline images require ordinary filtering samplers.";
                    return false;
                }
            }
            catch (InvalidDataException exception)
            {
                reason = exception.Message;
                return false;
            }
        }

        ShaderVar[] parameters = _source.Parameters;
        if (!MatchesParameters(parameters) && !TryBindParameters(parameters, out reason)) return false;
        ReadOnlySpan<ShaderAbiMemberContract> schema = UberOutlineProgramContract.SourceMembers;
        for (int index = 0; index < schema.Length; index++)
            if (UberOutlineProgramContract.IsSourceMemberActive(index, Features) &&
                !TryValidateValue(_members[index], schema[index], out reason))
                return false;
        if (_vertexEffectsEnabled is not null && (!float.IsFinite(_vertexEffectsEnabled.Value) || _vertexEffectsEnabled.Value != 0))
        {
            reason = "UberOutline.VertexEffectsUnsupported: _VertexEffectsEnabled must remain finite and exactly zero because the canonical vertex stage does not prune it.";
            return false;
        }
        reason = null;
        return true;
    }

    private bool MatchesParameters(ShaderVar[] parameters)
    {
        if (!ReferenceEquals(parameters, _parameters) || parameters.Length != _parameterReferences.Length) return false;
        for (int index = 0; index < parameters.Length; index++)
            if (!ReferenceEquals(parameters[index], _parameterReferences[index]) || parameters[index]?.Name != _parameterNames[index])
                return false;
        return true;
    }

    private bool TryBindParameters(ShaderVar[] parameters, out string? reason)
    {
        // A failed rebuild must not leave the prior array accepted with a partially cleared schema.
        _parameters = null;
        Array.Clear(_members);
        ReadOnlySpan<ShaderAbiMemberContract> schema = UberOutlineProgramContract.SourceMembers;
        foreach (ShaderVar? parameter in parameters)
        {
            if (parameter is null) continue;
            int index = SourceMemberIndex(parameter.Name);
            if (index < 0 || !UberOutlineProgramContract.IsSourceMemberActive(index, Features)) continue;
            if (_members[index] is not null)
            {
                reason = $"UberOutline.ParameterDuplicate: active field '{schema[index].ProviderName}' must occur exactly once.";
                return false;
            }
            _members[index] = parameter;
        }
        for (int index = 0; index < schema.Length; index++)
            if (UberOutlineProgramContract.IsSourceMemberActive(index, Features) &&
                !TryValidateValue(_members[index], schema[index], out reason))
                return false;
        if (!TryValidateVertexEffects(parameters, out _vertexEffectsEnabled, out reason)) return false;
        if (_parameterReferences.Length != parameters.Length)
        {
            _parameterReferences = new ShaderVar?[parameters.Length];
            _parameterNames = new string?[parameters.Length];
        }
        for (int index = 0; index < parameters.Length; index++)
        {
            _parameterReferences[index] = parameters[index];
            _parameterNames[index] = parameters[index]?.Name;
        }
        _parameters = parameters;
        reason = null;
        return true;
    }

    private static bool TryValidateCommonSource(XRMaterial source, out uint features, out bool renderTime, out string? reason)
    {
        features = 0;
        renderTime = true;
        reason = "UberOutline.BindingExtensionUnsupported: canonical lowering requires an ordinary XRMaterial without unknown uniform callbacks, vertex callbacks, shadow callbacks, or binding publishers.";
        // Reflection hydration attaches the exact engine emission handler through surface setters.
        // It publishes only post-outline SurfaceEmission inputs, which the canonical early return never consumes.
        if (source.GetType() != typeof(XRMaterial) || source.HasSettingUniformsHandlers && !source.HasOnlyStandardSurfaceUniformHandlers ||
            source.HasSettingVertexUniformHandlers || source.HasSettingShadowUniformHandlers || source.BindingPublishers.Count != 0)
            return false;
        UberMaterialFeatureState[] declared = source.UberAuthoredState.Features;
        for (int index = 0; index < declared.Length; index++)
        {
            UberMaterialFeatureState? feature = declared[index];
            if (feature is null || string.IsNullOrEmpty(feature.Id))
            {
                reason = "UberOutline.FeatureStateInvalid: authored feature records require nonempty unique identities.";
                return false;
            }
            for (int other = index + 1; other < declared.Length; other++)
                if (declared[other]?.Id == feature.Id)
                {
                    reason = $"UberOutline.FeatureStateDuplicate: feature '{feature.Id}' occurs more than once.";
                    return false;
                }
            if (feature.Id == "render-time") renderTime = feature.Enabled;
            if (!feature.Enabled) continue;
            if (!TryAdmitFeature(feature.Id, out reason)) return false;
            if (feature.Id == "alpha-masks") features |= UberOutlineMaterialProfile.AlphaMasks;
            if (feature.Id == "dissolve") features |= UberOutlineMaterialProfile.Dissolve;
        }
        foreach (string id in source.ActiveUberVariant.EnabledFeatures)
            if (!TryAdmitFeature(id, out reason)) return false;
        foreach (string id in source.RequestedUberVariant.EnabledFeatures)
            if (!TryAdmitFeature(id, out reason)) return false;
        foreach (UberMaterialPropertyState? property in source.UberAuthoredState.Properties)
        {
            if (property is null)
            {
                reason = "UberOutline.PropertyStateInvalid: authored property records cannot be null.";
                return false;
            }
            // Canonical Animated properties publish their ShaderVar even if an older editor value left a literal behind.
            if (property.Mode == EShaderUiPropertyMode.Animated || string.IsNullOrWhiteSpace(property.StaticLiteral)) continue;
            int memberIndex = SourceMemberIndex(property.Name);
            if (property.Name == "_VertexEffectsEnabled" || memberIndex >= 0 &&
                UberOutlineProgramContract.IsSourceMemberActive(memberIndex, features))
            {
                reason = $"UberOutline.StaticLiteralUnsupported: active field '{property.Name}' overrides its live ShaderVar with a GLSL literal; exact equivalent lowering has not been proven.";
                return false;
            }
        }
        foreach (ShaderVar? parameter in source.Parameters)
        {
            if (parameter is null) continue;
            Type type = parameter.GetType();
            if (type != typeof(ShaderBool) && type != typeof(ShaderInt) && type != typeof(ShaderUInt) && type != typeof(ShaderFloat) &&
                type != typeof(ShaderVector2) && type != typeof(ShaderVector3) && type != typeof(ShaderVector4) &&
                type != typeof(ShaderIVector3) && type != typeof(ShaderIVector4) && type != typeof(ShaderMat4))
            {
                reason = $"UberOutline.ParameterPublisherUnsupported: '{parameter.Name}' uses a parameter type outside the canonical scalar/vector/matrix publishers.";
                return false;
            }
            if (parameter?.Name is "OutlineFeatures" or "ModelMatrix" or "ViewMatrix_VTX" or
                "ProjMatrix_VTX" or "ViewProjectionMatrix_VTX" or "CameraPosition" or "ScreenWidth" or
                "ScreenHeight" or "RenderTime" or "ViewProjection" or "NormalMatrix" or "u_Time" or "u_ScreenParams")
            {
                reason = $"UberOutline.EngineUniformOverrideUnsupported: '{parameter.Name}' replaces a coupled engine view or transform input whose authored override this lowering does not model.";
                return false;
            }
        }
        reason = null;
        return true;
    }

    private static bool TryAdmitFeature(string id, out string? reason)
    {
        if (id is "normal-map" or "color-adjustments" or "detail-textures" or "parallax" or
            "surface-extensions" or "global-masks-themes" or "layered-decals" or "extended-effects" or
            "view-context" or "audiolink" or "environment-lighting" or "vertex-effects")
        {
            reason = $"UberOutline.PreOutlineFeatureUnsupported: active feature '{id}' changes canonical pre-outline behavior that this lowering does not model.";
            return false;
        }
        if (id is not ("render-time" or "alpha-masks" or "dissolve" or "outline" or "stylized-shading" or
            "forward-ambient-occlusion" or "forward-shadows" or
            "material-ao" or "shadow-masks" or "emission" or "matcap" or "rim-lighting" or "advanced-specular" or
            "backface" or "glitter" or "flipbook" or "subsurface" or "advanced-stylized-lighting" or
            "advanced-pbr" or "layered-matcap-rim" or "layered-emission" or "texture-array-flipbook"))
        {
            reason = $"UberOutline.FeatureUnknown: enabled feature '{id}' has no proven canonical outline ordering.";
            return false;
        }
        reason = null;
        return true;
    }

    private static int SourceMemberIndex(string? name)
    {
        ReadOnlySpan<ShaderAbiMemberContract> schema = UberOutlineProgramContract.SourceMembers;
        for (int index = 0; index < schema.Length; index++)
            if (name == schema[index].ProviderName) return index;
        return -1;
    }

    private static bool TryValidateVertexEffects(ShaderVar[] parameters, out ShaderFloat? vertexEffects, out string? reason)
    {
        vertexEffects = null;
        foreach (ShaderVar? parameter in parameters)
        {
            if (parameter?.Name != "_VertexEffectsEnabled") continue;
            if (vertexEffects is not null || parameter.GetType() != typeof(ShaderFloat) ||
                parameter is not ShaderFloat value || !float.IsFinite(value.Value) || value.Value != 0)
            {
                reason = "UberOutline.VertexEffectsUnsupported: _VertexEffectsEnabled must be a unique ShaderFloat with finite value exactly zero, even when its declared feature is disabled.";
                return false;
            }
            vertexEffects = value;
        }
        reason = null;
        return true;
    }

    private static bool TryValidateValue(ShaderVar? parameter, ShaderAbiMemberContract member, out string? reason)
    {
        bool valid = member.PhysicalType switch
        {
            "i32" => parameter?.GetType() == typeof(ShaderInt),
            "f32" => parameter?.GetType() == typeof(ShaderFloat) && float.IsFinite(((ShaderFloat)parameter).Value),
            "vec2<f32>" => parameter?.GetType() == typeof(ShaderVector2) && Finite(((ShaderVector2)parameter).Value),
            "vec3<f32>" => parameter?.GetType() == typeof(ShaderVector3) && Finite(((ShaderVector3)parameter).Value),
            "vec4<f32>" => parameter?.GetType() == typeof(ShaderVector4) && Finite(((ShaderVector4)parameter).Value),
            _ => false,
        };
        reason = valid ? null : $"UberOutline.ParameterInvalid: active field '{member.ProviderName}' requires its exact canonical {member.PhysicalType} ShaderVar type and finite components.";
        return valid;
    }

    private static bool Finite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool Finite(Vector4 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
}
