using System.Buffers.Binary;
using System.Numerics;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Shaders.Parameters;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering;

/// <summary>Captures prepared static literals and validates live animated canonical inputs without warmed allocations.</summary>
public sealed class UberBaseSurfaceBinding
{
    private readonly XRMaterial _source;
    private readonly UberBaseMaterialProfile _profile;
    private readonly byte[] _staticValues = new byte[UberBaseParameterSchema.ByteSize];
    private readonly byte[] _values = new byte[UberBaseParameterSchema.ByteSize];
    private readonly ShaderVar?[] _members = new ShaderVar?[UberBaseParameterSchema.MemberCount];
    private readonly string[] _staticProperties;
    private readonly string[] _animatedProperties;
    private readonly string[] _enabledFeatures;
    private readonly string[] _pipelineMacros;
    private readonly UberBaseTextureBinding?[] _roles;
    private ShaderVar[]? _parameters;
    private ShaderVar?[] _parameterReferences = [];
    private string?[] _parameterNames = [];
    private ulong _staticMask;
    private ulong _valueVersion;

    private UberBaseSurfaceBinding(XRMaterial source, UberBaseMaterialProfile profile)
    {
        _source = source;
        _profile = profile;
        _staticProperties = (string[])profile.Variant.StaticProperties.Clone();
        _animatedProperties = (string[])profile.Variant.AnimatedProperties.Clone();
        _enabledFeatures = (string[])profile.Variant.EnabledFeatures.Clone();
        _pipelineMacros = (string[])profile.Variant.PipelineMacros.Clone();
        _roles = (UberBaseTextureBinding?[])profile.TextureBindings.Clone();
    }

    public uint Features => _profile.Features;
    public UberBaseMaterialProfile Profile => _profile;
    public ReadOnlySpan<UberBaseTextureBinding?> Textures => _roles;

    public static bool TryCreate(XRMaterial source, UberBaseMaterialProfile profile,
        out UberBaseSurfaceBinding? binding, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(profile);
        binding = null;
        reason = "UberBase.VariantMissing: an exact prepared variant and source identity are required.";
        if (profile.Version != 1 || profile.Variant is null || profile.Variant.IsEmpty ||
            profile.Variant.StaticProperties is null || profile.Variant.AnimatedProperties is null ||
            profile.Variant.EnabledFeatures is null || profile.Variant.PipelineMacros is null ||
            profile.TextureBindings is null || profile.TextureBindings.Length != UberBaseMaterialProfile.RoleCount ||
            profile.SourceIdentity.Length != 64) return false;
        UberBaseSurfaceBinding candidate = new(source, profile);
        if (!candidate.TryCaptureStaticValues(out reason)) return false;
        if (!candidate.TryGetValues(out _, out reason)) return false;
        binding = candidate;
        return true;
    }

    private bool TryCaptureStaticValues(out string? reason)
    {
        ReadOnlySpan<ShaderAbiMemberContract> schema = UberBaseParameterSchema.Members;
        foreach (string entry in _staticProperties)
        {
            int separator = entry.IndexOf('=');
            if (separator <= 0) { reason = "UberBase.StaticPropertyInvalid: prepared static entries require a name and literal."; return false; }
            int index = UberBaseParameterSchema.IndexOf(entry[..separator]);
            if (index < 0 || !UberBaseParameterSchema.IsActive(index, Features)) continue;
            ShaderAbiMemberContract member = schema[index];
            ulong bit = 1UL << index;
            if ((_staticMask & bit) != 0 || !UberBaseStaticLiteral.TryWrite(member.PhysicalType, entry[(separator + 1)..],
                _staticValues.AsSpan(checked((int)member.Offset), checked((int)member.Size))))
            {
                reason = $"UberBase.StaticExpressionUnsupported: '{member.ProviderName}' requires one finite canonical scalar/vector literal.";
                return false;
            }
            _staticMask |= bit;
        }
        foreach (string name in _animatedProperties)
        {
            int index = UberBaseParameterSchema.IndexOf(name);
            if (index >= 0 && (_staticMask & (1UL << index)) != 0)
            {
                reason = $"UberBase.PropertyModeConflict: '{name}' cannot be both static and animated in one prepared variant.";
                return false;
            }
        }
        reason = null;
        return true;
    }

    /// <summary>Copies effective values when an owner explicitly needs a new GPU parameter publication.</summary>
    public bool TryWrite(Span<byte> destination, out string? reason)
    {
        if (destination.Length != UberBaseParameterSchema.ByteSize) throw new ArgumentException("Uber base parameters require 352 bytes.", nameof(destination));
        if (!TryGetValues(out ReadOnlySpan<byte> values, out reason)) return false;
        values.CopyTo(destination);
        return true;
    }

    /// <summary>Returns retained numeric bytes; unchanged frames perform no parameter-block allocation or copy.</summary>
    public bool TryGetValues(out ReadOnlySpan<byte> values, out string? reason)
    {
        values = default;
        if (!TryValidateProfile(out reason)) return false;
        if (!MatchesParameters() && !TryBindParameters(out reason)) return false;
        if (_valueVersion != 0 && _valueVersion == _source.BindingValueVersion)
        {
            values = _values;
            return true;
        }
        Span<byte> destination = _values;
        _staticValues.CopyTo(destination);
        ReadOnlySpan<ShaderAbiMemberContract> schema = UberBaseParameterSchema.Members;
        for (int index = 0; index < schema.Length; index++)
        {
            if (!UberBaseParameterSchema.IsActive(index, Features) || (_staticMask & (1UL << index)) != 0) continue;
            ShaderAbiMemberContract member = schema[index];
            if (!TryWriteParameter(_members[index], member.PhysicalType,
                destination.Slice(checked((int)member.Offset), checked((int)member.Size))))
            {
                reason = $"UberBase.ParameterInvalid: '{member.ProviderName}' requires its unique canonical {member.PhysicalType} parameter with finite components.";
                return false;
            }
        }
        if ((Features & UberBaseMaterialProfile.AdvancedSpecular) != 0 && BinaryPrimitives.ReadInt32LittleEndian(destination[248..]) != 0)
        {
            reason = "UberBase.SpecularModelUnsupported: toon and anisotropic overlays require separately qualified authored feature lowerings; the base contract admits _SpecularType=0.";
            return false;
        }
        int mode = BinaryPrimitives.ReadInt32LittleEndian(destination[112..]);
        if (mode is < 0 or > 3)
        {
            reason = "UberBase.CoverageModeUnsupported: the base contract admits canonical opaque, cutout, fade and transparent modes.";
            return false;
        }
        _valueVersion = _source.BindingValueVersion;
        values = _values;
        reason = null;
        return true;
    }

    private bool TryValidateProfile(out string? reason)
    {
        reason = "UberBase.SourceChanged: the authored state, prepared variant axes, or immutable role mapping changed; an exact recook is required.";
        if (_source.GetType() != typeof(XRMaterial) && _source.GetType() != typeof(PublishedUberBaseMaterial) || !_profile.AuthoredState.Equals(_source.UberAuthoredState) ||
            !_profile.Variant.StaticProperties.AsSpan().SequenceEqual(_staticProperties) ||
            !_profile.Variant.AnimatedProperties.AsSpan().SequenceEqual(_animatedProperties) ||
            !_profile.Variant.EnabledFeatures.AsSpan().SequenceEqual(_enabledFeatures) ||
            !_profile.Variant.PipelineMacros.AsSpan().SequenceEqual(_pipelineMacros) ||
            _source.RenderPass != _profile.Variant.RenderPass ||
            !_source.RequestedUberVariant.IsEmpty && !_source.RequestedUberVariant.Equals(_profile.Variant)) return false;
        if (!UberBaseMaterialProfile.TryResolveFeatures(_enabledFeatures, out uint features, out reason) || features != Features) return false;
        if (!UberShaderVariantBuilder.MatchesForwardPipelineRequirements(_source, _enabledFeatures, _pipelineMacros))
        {
            reason = "UberBase.PipelineRequirementsChanged: live lighting, ambient occlusion, shadow, contact-shadow or PBR requirements differ from the exact cooked variant; recook the requested source variant.";
            return false;
        }
        for (int role = 0; role < _roles.Length; role++)
            if (!ReferenceEquals(_roles[role], _profile.TextureBindings[role])) return false;
        if (!_profile.TryValidateTextureTable(_source, out reason)) return false;
        reason = "UberBase.BindingExtensionUnsupported: canonical base lowering requires ordinary parameters without user callbacks or binding publishers.";
        if (_source.HasSettingUniformsHandlers && !_source.HasOnlyStandardSurfaceUniformHandlers ||
            _source.HasSettingVertexUniformHandlers || _source.HasSettingShadowUniformHandlers || _source.BindingPublishers.Count != 0) return false;
        if (_source.PassSet.Passes.Length != 0)
        {
            reason = "UberBase.MaterialPassUnsupported: additional authored material passes require their own exact source companions.";
            return false;
        }
        bool contactDisabled = false;
        foreach (string macro in _pipelineMacros)
            if (macro == "XRENGINE_UBER_DISABLE_FORWARD_CONTACT_SHADOWS") contactDisabled = true;
        if (!contactDisabled)
        {
            reason = "UberBase.ContactShadowsUnsupported: selected contact shadows require a separately qualified source operation.";
            return false;
        }
        foreach (ShaderVar? parameter in _source.Parameters)
        {
            if (parameter is null)
            {
                reason = "UberBase.ParameterNull: the canonical parameter table cannot contain null publishers.";
                return false;
            }
            Type type = parameter.GetType();
            if (type != typeof(ShaderBool) && type != typeof(ShaderInt) && type != typeof(ShaderUInt) && type != typeof(ShaderFloat) &&
                type != typeof(ShaderVector2) && type != typeof(ShaderVector3) && type != typeof(ShaderVector4) &&
                type != typeof(ShaderIVector3) && type != typeof(ShaderIVector4) && type != typeof(ShaderMat4))
            {
                reason = $"UberBase.ParameterPublisherUnsupported: '{parameter.Name}' does not use a canonical scalar/vector/matrix publisher.";
                return false;
            }
            if (parameter?.Name == "_VertexEffectsEnabled" && parameter is not ShaderFloat { Value: 0 })
            {
                reason = "UberBase.VertexEffectsUnsupported: the canonical vertex stage requires _VertexEffectsEnabled exactly zero even when its feature is disabled.";
                return false;
            }
            if (parameter?.Name is "ModelMatrix" or "ViewMatrix_VTX" or "ProjMatrix_VTX" or "ViewProjectionMatrix_VTX" or
                "CameraPosition" or "ScreenWidth" or "ScreenHeight" or "RenderTime" or "ViewProjection" or "NormalMatrix")
            {
                reason = $"UberBase.EngineUniformOverrideUnsupported: '{parameter.Name}' overrides a coupled engine transform or view input.";
                return false;
            }
        }
        reason = null;
        return true;
    }

    private bool MatchesParameters()
    {
        ShaderVar[] parameters = _source.Parameters;
        if (!ReferenceEquals(parameters, _parameters) || parameters.Length != _parameterReferences.Length) return false;
        for (int index = 0; index < parameters.Length; index++)
            if (!ReferenceEquals(parameters[index], _parameterReferences[index]) || parameters[index]?.Name != _parameterNames[index]) return false;
        return true;
    }

    private bool TryBindParameters(out string? reason)
    {
        _parameters = null;
        _valueVersion = 0;
        Array.Clear(_members);
        ShaderVar[] parameters = _source.Parameters;
        foreach (ShaderVar? parameter in parameters)
        {
            int index = UberBaseParameterSchema.IndexOf(parameter?.Name);
            if (index < 0 || !UberBaseParameterSchema.IsActive(index, Features)) continue;
            if (_members[index] is not null)
            {
                reason = $"UberBase.ParameterDuplicate: '{parameter!.Name}' occurs more than once.";
                return false;
            }
            _members[index] = parameter;
        }
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

    private static bool TryWriteParameter(ShaderVar? parameter, string type, Span<byte> destination)
    {
        if (type == "i32" && parameter?.GetType() == typeof(ShaderInt))
        {
            BinaryPrimitives.WriteInt32LittleEndian(destination, ((ShaderInt)parameter).Value);
            return true;
        }
        Vector4 values;
        int count;
        if (type == "f32" && parameter?.GetType() == typeof(ShaderFloat)) { values = new(((ShaderFloat)parameter).Value, 0, 0, 0); count = 1; }
        else if (type == "vec2<f32>" && parameter?.GetType() == typeof(ShaderVector2)) { values = new(((ShaderVector2)parameter).Value, 0, 0); count = 2; }
        else if (type == "vec4<f32>" && parameter?.GetType() == typeof(ShaderVector4)) { values = ((ShaderVector4)parameter).Value; count = 4; }
        else return false;
        for (int index = 0; index < count; index++)
        {
            float value = values[index];
            if (!float.IsFinite(value)) return false;
            BinaryPrimitives.WriteSingleLittleEndian(destination[(index * 4)..], value);
        }
        return true;
    }
}
