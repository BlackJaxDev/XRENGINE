using System.Collections.Concurrent;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Shaders.Parameters;
using YamlDotNet.Serialization;

namespace XREngine.Rendering;

public partial class XRMaterial
{
    private const int UberVariantDebounceMilliseconds = 40;
    private const int UberConstantPropertyEditDebounceMilliseconds = 180;
    private const string ShaderProgramMaterialVariantKind = "MaterialVariant";
    private static readonly ConcurrentDictionary<string, XRTexture2D> UberDefaultSamplerTextures = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, XRTexture2DArray> UberDefaultArraySamplerTextures = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, XRTextureCube> UberDefaultCubeSamplerTextures = new(StringComparer.Ordinal);

    [YamlIgnore]
    private XRShader? _uberCanonicalFragmentShader;
    [YamlIgnore]
    private long _uberVariantRequestSerial;
    [YamlIgnore]
    private Task? _uberVariantBuildTask;
    [YamlIgnore]
    private readonly object _uberVariantBuildLock = new();
    [YamlIgnore]
    private CancellationTokenSource? _uberVariantBuildCancellation;
    [YamlIgnore]
    private readonly object _uberVariantRequestDebounceLock = new();
    [YamlIgnore]
    private CancellationTokenSource? _uberVariantRequestDebounceCancellation;

    public bool TryGetUberMaterialState(out XRShader? fragmentShader, out ShaderUiManifest manifest)
    {
        XRShader? activeFragmentShader = GetShader(EShaderType.Fragment);
        fragmentShader = activeFragmentShader;
        manifest = ShaderUiManifest.Empty;
        if (activeFragmentShader is null)
            return false;

        XRShader canonicalShader = ResolveCanonicalUberFragmentShader(activeFragmentShader);
        string? shaderPath = ResolveShaderPathOrName(canonicalShader);
        if (!string.Equals(Path.GetFileName(shaderPath), "UberShader.frag", StringComparison.OrdinalIgnoreCase))
            return false;

        fragmentShader = canonicalShader;
        manifest = canonicalShader.GetUiManifest();
        return manifest.Properties.Count > 0;
    }

    public void EnsureUberStateInitialized()
    {
        XRShader? activeFragmentShader = GetShader(EShaderType.Fragment);
        if (TryGetUberMaterialState(out XRShader? fragmentShader, out ShaderUiManifest manifest) && fragmentShader is not null)
        {
            if (activeFragmentShader is not null &&
                !ReferenceEquals(activeFragmentShader, fragmentShader) &&
                ActiveUberVariant.IsEmpty &&
                UberShaderVariantBuilder.IsGeneratedVariant(activeFragmentShader))
            {
                SetShader(EShaderType.Fragment, fragmentShader, coerceShaderType: true);
            }

            EnsureUberStateInitialized(fragmentShader, manifest);
        }
    }

    public void ApplyShaderProgramMetadata(XRRenderProgram? program)
    {
        if (program is null)
            return;

        if (string.IsNullOrWhiteSpace(program.Name) && program.Separable)
            program.Name = BuildShaderPipelineProgramName();

        program.SetShaderProgramDiagnosticMetadata(new XRRenderProgram.ShaderProgramDiagnosticMetadata(
            Name,
            null,
            null,
            program.Separable ? "MaterialPipeline" : "CombinedMaterial",
            program.ProgramDescriptor.StableKey,
            ActiveUberVariant.IsEmpty ? null : "Uber material variant"));

        if (ActiveUberVariant.IsEmpty || ActiveUberVariant.VariantHash == 0)
        {
            program.SetShaderVariantMetadata(null);
            return;
        }

        program.SetShaderVariantMetadata(new XRRenderProgram.ShaderProgramVariantMetadata(
            ShaderProgramMaterialVariantKind,
            ActiveUberVariant.VariantHash,
            XRRenderProgram.EShaderProgramBinaryCachePolicy.BypassWhenDriverParallelCompile));
    }

    private string BuildShaderPipelineProgramName()
    {
        string materialName = string.IsNullOrWhiteSpace(Name)
            ? "<unnamed material>"
            : Name!;

        if (ActiveUberVariant.IsEmpty || ActiveUberVariant.VariantHash == 0)
            return string.Concat("MaterialPipeline:", materialName);

        return string.Concat(
            "MaterialPipelineVariant:",
            materialName,
            ":",
            ActiveUberVariant.VariantHash.ToString("x16", CultureInfo.InvariantCulture));
    }

    public bool IsUberFeatureEnabled(string featureId, bool defaultEnabled)
    {
        UberMaterialFeatureState? authored = UberAuthoredState.GetFeature(featureId);
        return authored?.Enabled ?? defaultEnabled;
    }

    public EShaderUiPropertyMode GetUberPropertyMode(string propertyName, EShaderUiPropertyMode defaultMode, bool isSampler)
    {
        UberMaterialPropertyState? authored = UberAuthoredState.GetProperty(propertyName);
        if (authored is not null)
            return authored.Mode;

        if (isSampler)
            return EShaderUiPropertyMode.Animated;

        return defaultMode == EShaderUiPropertyMode.Unspecified
            ? EShaderUiPropertyMode.Static
            : defaultMode;
    }

    /// <summary>
    /// Keeps authorable runtime-mutable Uber properties as uniforms instead of
    /// baking their current values into a material-specific shader variant.
    /// Explicit material/pass/engine/debug-static declarations remain static.
    /// </summary>
    public bool UseRuntimeUberPropertyBindings()
    {
        if (!TryGetUberMaterialState(out _, out ShaderUiManifest manifest))
            return false;

        List<string> runtimePropertyNames = [];
        foreach (ShaderUiProperty property in manifest.Properties)
        {
            if (property.IsSampler ||
                !IsAuthorableUberProperty(property) ||
                property.HasExplicitMutability &&
                property.Mutability != EShaderUiPropertyMutability.Runtime)
            {
                continue;
            }

            runtimePropertyNames.Add(property.Name);
        }

        return UpdateUberAuthoredState(
            static (state, propertyNames) => state.SetPropertyModes(
                propertyNames,
                EShaderUiPropertyMode.Animated),
            runtimePropertyNames);
    }

    internal void EnsureUberStateInitialized(XRShader fragmentShader, ShaderUiManifest manifest)
    {
        UberMaterialAuthoredState current = UberAuthoredState ?? UberMaterialAuthoredState.Empty;
        UberMaterialAuthoredState next = current;
        XRShader canonicalShader = ResolveCanonicalUberFragmentShader(fragmentShader);

        if (ActiveUberVariant.IsEmpty && UberShaderVariantBuilder.IsGeneratedVariant(fragmentShader) && !ReferenceEquals(canonicalShader, fragmentShader))
            SetShader(EShaderType.Fragment, canonicalShader, coerceShaderType: true);

        foreach (ShaderUiFeature feature in manifest.Features)
            next = next.EnsureFeature(feature.Id, ResolveInitialFeatureEnabled(canonicalShader, feature));

        foreach (ShaderUiProperty property in manifest.Properties)
        {
            if (!IsAuthorableUberProperty(property))
                continue;

            next = next.EnsurePropertyMode(property.Name, UberShaderVariantBuilder.ResolvePropertyMode(this, property));
        }

        if (!current.Equals(next))
            UberAuthoredState = next;

        EnsureUberAuthorableParameters(manifest);
        EnsureUberEnabledFeatureResources(manifest);
    }

    private void EnsureUberAuthorableParameters(ShaderUiManifest manifest)
    {
        bool changed = false;
        foreach (ShaderUiProperty property in manifest.Properties)
        {
            if (!property.IsSampler && IsAuthorableUberProperty(property))
                changed |= EnsureUberDefaultParameter(property);
        }

        if (changed)
            MarkDirty();
    }

    private void EnsureUberFeatureResources(string featureId)
    {
        if (!TryGetUberMaterialState(out XRShader? fragmentShader, out ShaderUiManifest manifest) || fragmentShader is null)
            return;

        EnsureUberStateInitialized(fragmentShader, manifest);

        EnsureUberFeatureResources(manifest, featureId);
    }

    private void EnsureUberEnabledFeatureResources(ShaderUiManifest manifest)
    {
        bool changed = false;
        foreach (ShaderUiFeature feature in manifest.Features)
        {
            if (!IsUberFeatureEnabled(feature.Id, feature.DefaultEnabled))
                continue;

            changed |= EnsureUberFeatureResources(manifest, feature.Id);
        }

        if (changed)
            MarkDirty();
    }

    private bool EnsureUberFeatureResources(ShaderUiManifest manifest, string featureId)
    {

        bool texturesChanged = false;
        bool parametersChanged = false;

        foreach (ShaderUiProperty property in manifest.Properties)
        {
            // Pipeline-owned inputs (shadow atlases, AO buffers, camera state, and
            // similar resources) are bound by the renderer. Material initialization
            // must not create placeholder assets for them or they become serialized
            // as if they were authored Unity material properties.
            if (!IsAuthorableUberProperty(property))
                continue;

            if (!string.Equals(property.FeatureId, featureId, StringComparison.Ordinal))
                continue;

            if (property.IsSampler)
            {
                texturesChanged |= EnsureUberDefaultSamplerTexture(property);
                continue;
            }

            parametersChanged |= EnsureUberDefaultParameter(property);
        }

        if (string.Equals(featureId, "stylized-shading", StringComparison.Ordinal))
            parametersChanged |= EnsureStylizedLightingModeDefault();

        return texturesChanged || parametersChanged;
    }

    private bool EnsureUberDefaultParameter(ShaderUiProperty property)
    {
        if (!ShaderVar.GlslTypeMap.TryGetValue(property.GlslType, out EShaderVarType shaderVarType))
            return false;

        ShaderVar[] current = Parameters ?? [];
        ShaderVar? firstNamedParameter = null;
        ShaderVar? matchingParameter = null;
        int namedParameterCount = 0;
        foreach (ShaderVar parameter in current)
        {
            if (parameter is null ||
                !string.Equals(parameter.Name, property.Name, StringComparison.Ordinal))
                continue;

            firstNamedParameter ??= parameter;
            namedParameterCount++;
            if (parameter.TypeName == shaderVarType)
                matchingParameter ??= parameter;
        }

        ShaderVar? resolvedParameter = matchingParameter;
        if (resolvedParameter is null)
        {
            resolvedParameter = ShaderVar.CreateForType(shaderVarType, property.Name);
            if (resolvedParameter is null)
                return false;

            // Unity stores many enum/toggle properties as floats and legacy YAML
            // could infer vec3 for aliased vec4 values. Seed a replacement from
            // the manifest default, then overlay authored components when the
            // legacy value is non-default.
            ApplyUberDefaultLiteral(resolvedParameter, property.DefaultLiteral);
            if (firstNamedParameter is not null &&
                !IsShaderParameterAtLanguageDefault(firstNamedParameter))
            {
                CopyCompatibleUberParameterValue(firstNamedParameter, resolvedParameter);
            }
        }

        bool parametersChanged = NormalizeUberParameter(
            property.Name,
            resolvedParameter,
            current,
            namedParameterCount);
        bool defaultChanged = TryApplyUberDefaultLiteral(resolvedParameter, property);
        return parametersChanged || defaultChanged;
    }

    private bool NormalizeUberParameter(
        string parameterName,
        ShaderVar resolvedParameter,
        ShaderVar[] current,
        int namedParameterCount)
    {
        if (namedParameterCount == 1 &&
            current.Any(parameter => ReferenceEquals(parameter, resolvedParameter)))
        {
            return false;
        }

        List<ShaderVar> normalized = new(
            current.Length - Math.Max(0, namedParameterCount - 1) + (namedParameterCount == 0 ? 1 : 0));
        bool inserted = false;
        foreach (ShaderVar parameter in current)
        {
            if (parameter is null)
                continue;

            if (string.Equals(parameter.Name, parameterName, StringComparison.Ordinal))
            {
                if (!inserted)
                {
                    normalized.Add(resolvedParameter);
                    inserted = true;
                }

                continue;
            }

            normalized.Add(parameter);
        }

        if (!inserted)
            normalized.Add(resolvedParameter);

        Parameters = [.. normalized];
        return true;
    }

    private static void CopyCompatibleUberParameterValue(ShaderVar source, ShaderVar destination)
    {
        if (TryGetUberScalarValue(source, out double scalar))
        {
            switch (destination)
            {
                case ShaderBool shaderBool:
                    shaderBool.SetValue(Math.Abs(scalar) > double.Epsilon);
                    return;
                case ShaderInt shaderInt:
                    shaderInt.SetValue((int)Math.Clamp(Math.Truncate(scalar), int.MinValue, int.MaxValue));
                    return;
                case ShaderUInt shaderUInt:
                    shaderUInt.SetValue((uint)Math.Clamp(Math.Truncate(scalar), uint.MinValue, uint.MaxValue));
                    return;
                case ShaderFloat shaderFloat:
                    shaderFloat.SetValue((float)scalar);
                    return;
            }
        }

        if (!TryGetUberVectorValue(source, out Vector4 vector, out int componentCount))
            return;

        switch (destination)
        {
            case ShaderVector2 shaderVector2:
                shaderVector2.SetValue(new Vector2(vector.X, vector.Y));
                break;
            case ShaderVector3 shaderVector3:
                shaderVector3.SetValue(new Vector3(vector.X, vector.Y, vector.Z));
                break;
            case ShaderVector4 shaderVector4:
            {
                Vector4 current = shaderVector4.Value;
                shaderVector4.SetValue(new Vector4(
                    vector.X,
                    componentCount >= 2 ? vector.Y : current.Y,
                    componentCount >= 3 ? vector.Z : current.Z,
                    componentCount >= 4 ? vector.W : current.W));
                break;
            }
        }
    }

    private static bool TryGetUberScalarValue(ShaderVar parameter, out double value)
    {
        switch (parameter)
        {
            case ShaderBool shaderBool:
                value = shaderBool.Value ? 1.0 : 0.0;
                return true;
            case ShaderInt shaderInt:
                value = shaderInt.Value;
                return true;
            case ShaderUInt shaderUInt:
                value = shaderUInt.Value;
                return true;
            case ShaderFloat shaderFloat:
                value = shaderFloat.Value;
                return true;
            case ShaderDouble shaderDouble:
                value = shaderDouble.Value;
                return true;
            default:
                value = 0.0;
                return false;
        }
    }

    private static bool TryGetUberVectorValue(
        ShaderVar parameter,
        out Vector4 value,
        out int componentCount)
    {
        switch (parameter)
        {
            case ShaderVector2 shaderVector2:
                value = new Vector4(shaderVector2.Value, 0.0f, 0.0f);
                componentCount = 2;
                return true;
            case ShaderVector3 shaderVector3:
                value = new Vector4(shaderVector3.Value, 0.0f);
                componentCount = 3;
                return true;
            case ShaderVector4 shaderVector4:
                value = shaderVector4.Value;
                componentCount = 4;
                return true;
            default:
                if (TryGetUberScalarValue(parameter, out double scalar))
                {
                    value = new Vector4((float)scalar, 0.0f, 0.0f, 0.0f);
                    componentCount = 1;
                    return true;
                }

                value = Vector4.Zero;
                componentCount = 0;
                return false;
        }
    }

    private bool TryApplyUberDefaultLiteral(ShaderVar parameter, ShaderUiProperty property)
    {
        if (string.IsNullOrWhiteSpace(property.DefaultLiteral))
            return false;

        UberMaterialPropertyState? authoredProperty = UberAuthoredState.GetProperty(property.Name);
        if (!string.IsNullOrWhiteSpace(authoredProperty?.StaticLiteral))
            return false;

        if (!IsShaderParameterAtLanguageDefault(parameter))
            return false;

        object previousValue = parameter.GenericValue;
        ApplyUberDefaultLiteral(parameter, property.DefaultLiteral);
        return !Equals(previousValue, parameter.GenericValue);
    }

    private static bool IsShaderParameterAtLanguageDefault(ShaderVar parameter)
    {
        return parameter switch
        {
            ShaderBool shaderBool => !shaderBool.Value,
            ShaderInt shaderInt => shaderInt.Value == 0,
            ShaderUInt shaderUInt => shaderUInt.Value == 0u,
            ShaderFloat shaderFloat => shaderFloat.Value == 0.0f,
            ShaderVector2 shaderVector2 => shaderVector2.Value == Vector2.Zero,
            ShaderVector3 shaderVector3 => shaderVector3.Value == Vector3.Zero,
            ShaderVector4 shaderVector4 => shaderVector4.Value == Vector4.Zero,
            _ => false,
        };
    }

    private bool EnsureStylizedLightingModeDefault()
    {
        UberMaterialPropertyState? authoredProperty = UberAuthoredState.GetProperty("_LightingMode");
        if (!string.IsNullOrWhiteSpace(authoredProperty?.StaticLiteral))
            return false;

        if (Parameter<ShaderInt>("_LightingMode") is not ShaderInt lightingMode)
            return false;

        if (lightingMode.Value != 6)
            return false;

        lightingMode.SetValue(5);
        return true;
    }

    private bool EnsureUberDefaultSamplerTexture(ShaderUiProperty property)
    {
        foreach (XRTexture? texture in Textures)
        {
            if (texture?.SamplerName?.Equals(property.Name, StringComparison.Ordinal) == true)
                return false;
        }

        XRTexture defaultTexture = property.GlslType switch
        {
            "sampler2DArray" => UberDefaultArraySamplerTextures.GetOrAdd(
                property.Name,
                static key => new XRTexture2DArray(CreateDefaultUberSamplerTexture(key))
                {
                    Name = key,
                    SamplerName = key,
                    AutoGenerateMipmaps = false,
                    Resizable = false,
                }),
            "samplerCube" => UberDefaultCubeSamplerTextures.GetOrAdd(
                property.Name,
                static key => new XRTextureCube(1u)
                {
                    Name = key,
                    SamplerName = key,
                    AutoGenerateMipmaps = false,
                    Resizable = false,
                }),
            _ => GetDefaultUberSamplerTexture(property.Name),
        };
        EventList<XRTexture?> updated = [.. Textures, defaultTexture];
        Textures = updated;
        return true;
    }

    private static XRTexture2D GetDefaultUberSamplerTexture(string samplerName)
        => UberDefaultSamplerTextures.GetOrAdd(samplerName, static key => CreateDefaultUberSamplerTexture(key));

    private static XRTexture2D CreateDefaultUberSamplerTexture(string samplerName)
    {
        ColorF4 color = ResolveDefaultUberSamplerColor(samplerName);
        bool isIdentityRamp = string.Equals(samplerName, "_ToonRamp", StringComparison.Ordinal);
        XRTexture2D texture = new(isIdentityRamp ? 2u : 1u, 1u, color)
        {
            Name = samplerName,
            SamplerName = samplerName,
            MagFilter = ETexMagFilter.Linear,
            MinFilter = ETexMinFilter.Linear,
            UWrap = isIdentityRamp ? ETexWrapMode.ClampToEdge : ETexWrapMode.Repeat,
            VWrap = isIdentityRamp ? ETexWrapMode.ClampToEdge : ETexWrapMode.Repeat,
            AlphaAsTransparency = true,
            AutoGenerateMipmaps = false,
            Resizable = false,
        };

        if (isIdentityRamp)
        {
            texture.Mipmaps[0].Data = new DataSource(
            [
                0, 0, 0, 255,
                255, 255, 255, 255,
            ]);
        }

        texture.ImportedUsage = ResolveDefaultUberSamplerUsage(samplerName);
        texture.ImportedColorSpace = texture.ImportedUsage == ETextureImportUsage.Color
            ? ETextureColorSpace.Srgb
            : ETextureColorSpace.Linear;
        texture.AlphaAsTransparency = texture.ImportedUsage == ETextureImportUsage.Color;
        return texture;
    }

    private static ColorF4 ResolveDefaultUberSamplerColor(string samplerName)
    {
        if (samplerName.Contains("Normal", StringComparison.OrdinalIgnoreCase)
            || samplerName.Contains("Bump", StringComparison.OrdinalIgnoreCase))
            return new ColorF4(0.5f, 0.5f, 1.0f, 1.0f);

        if (string.Equals(samplerName, "_PBRMetallicMaps", StringComparison.Ordinal) ||
            string.Equals(samplerName, "_DissolveDetailNoise", StringComparison.Ordinal))
            return new ColorF4(0.0f, 0.0f, 0.0f, 1.0f);

        return ColorF4.White;
    }

    private static ETextureImportUsage ResolveDefaultUberSamplerUsage(string samplerName)
    {
        if (samplerName.Contains("Normal", StringComparison.OrdinalIgnoreCase) ||
            samplerName.Contains("Bump", StringComparison.OrdinalIgnoreCase))
            return ETextureImportUsage.Normal;

        if (samplerName.Contains("Mask", StringComparison.OrdinalIgnoreCase) ||
            samplerName.Contains("Metallic", StringComparison.OrdinalIgnoreCase) ||
            samplerName.Contains("Smoothness", StringComparison.OrdinalIgnoreCase) ||
            samplerName.Contains("Noise", StringComparison.OrdinalIgnoreCase) ||
            samplerName.Contains("Parallax", StringComparison.OrdinalIgnoreCase))
            return ETextureImportUsage.Data;

        return ETextureImportUsage.Color;
    }

    private static void ApplyUberDefaultLiteral(ShaderVar parameter, string? defaultLiteral)
    {
        if (string.IsNullOrWhiteSpace(defaultLiteral))
            return;

        string literal = defaultLiteral.Trim();

        if (parameter is ShaderBool shaderBool && bool.TryParse(literal, out bool boolValue))
        {
            shaderBool.SetValue(boolValue);
            return;
        }

        if (parameter is ShaderInt shaderInt && TryParseIntLiteral(literal, out int intValue))
        {
            shaderInt.SetValue(intValue);
            return;
        }

        if (parameter is ShaderUInt shaderUInt && TryParseUIntLiteral(literal, out uint uintValue))
        {
            shaderUInt.SetValue(uintValue);
            return;
        }

        if (parameter is ShaderFloat shaderFloat && float.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out float floatValue))
        {
            shaderFloat.SetValue(floatValue);
            return;
        }

        if (parameter is ShaderVector2 shaderVector2 && TryParseFloatVectorLiteral(literal, "vec2", 2, out float[]? vec2Values) && vec2Values is not null)
        {
            shaderVector2.SetValue(new Vector2(vec2Values[0], vec2Values[1]));
            return;
        }

        if (parameter is ShaderVector3 shaderVector3 && TryParseFloatVectorLiteral(literal, "vec3", 3, out float[]? vec3Values) && vec3Values is not null)
        {
            shaderVector3.SetValue(new Vector3(vec3Values[0], vec3Values[1], vec3Values[2]));
            return;
        }

        if (parameter is ShaderVector4 shaderVector4 && TryParseFloatVectorLiteral(literal, "vec4", 4, out float[]? vec4Values) && vec4Values is not null)
            shaderVector4.SetValue(new Vector4(vec4Values[0], vec4Values[1], vec4Values[2], vec4Values[3]));
    }

    private static bool TryParseIntLiteral(string literal, out int value)
        => int.TryParse(literal, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static bool TryParseUIntLiteral(string literal, out uint value)
    {
        literal = literal.EndsWith("u", StringComparison.OrdinalIgnoreCase)
            ? literal[..^1]
            : literal;

        return uint.TryParse(literal, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseFloatVectorLiteral(string literal, string prefix, int expectedComponentCount, out float[]? values)
    {
        values = null;
        if (!literal.StartsWith(prefix + "(", StringComparison.Ordinal) || !literal.EndsWith(")", StringComparison.Ordinal))
            return false;

        string[] parts = literal[(prefix.Length + 1)..^1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 1 && parts.Length != expectedComponentCount)
            return false;

        float[] parsed = new float[expectedComponentCount];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[i]))
                return false;
        }

        if (parts.Length == 1)
            Array.Fill(parsed, parsed[0]);

        values = parsed;
        return true;
    }

    // All CPU-side request shaping stays here on the material path so renderer work is limited
    // to backend-facing compile/adoption once a prepared variant is ready.
    public void RequestUberVariantRebuild()
    {
        CancelUberVariantRebuildDebounce();

        if (!TryGetUberMaterialState(out XRShader? fragmentShader, out ShaderUiManifest manifest) || fragmentShader is null)
            return;

        EnsureUberStateInitialized(fragmentShader, manifest);
        XRShader canonicalShader = ResolveCanonicalUberFragmentShader(fragmentShader);

        long serial = Interlocked.Increment(ref _uberVariantRequestSerial);
        CancellationTokenSource cancellationTokenSource = ResetUberVariantBuildCancellation();
        UberShaderVariantTelemetry.RecordRequest();
        SetUberVariantStatus(new UberMaterialVariantStatus
        {
            Stage = EUberMaterialVariantStage.Requested,
            ActiveVariantHash = ActiveUberVariant.VariantHash,
            RequestedVariantHash = RequestedUberVariant.VariantHash,
        });

        _uberVariantBuildTask = Task.Run(async () =>
        {
            await Task.Delay(UberVariantDebounceMilliseconds, cancellationTokenSource.Token).ConfigureAwait(false);
            cancellationTokenSource.Token.ThrowIfCancellationRequested();

            SetUberVariantStatus(new UberMaterialVariantStatus
            {
                Stage = EUberMaterialVariantStage.Preparing,
                ActiveVariantHash = ActiveUberVariant.VariantHash,
                RequestedVariantHash = RequestedUberVariant.VariantHash,
            });

            return UberShaderVariantBuilder.PrepareVariant(this, canonicalShader, manifest, cancellationTokenSource.Token);
        }, cancellationTokenSource.Token).ContinueWith(task => ApplyPreparedUberVariant(serial, task), TaskScheduler.Default);
    }

    public bool PrepareUberVariantImmediately()
    {
        CancelUberVariantRebuildDebounce();

        if (!TryGetUberMaterialState(out XRShader? fragmentShader, out ShaderUiManifest manifest) || fragmentShader is null)
            return false;

        EnsureUberStateInitialized(fragmentShader, manifest);
        XRShader canonicalShader = ResolveCanonicalUberFragmentShader(fragmentShader);

        try
        {
            UberShaderVariantBuilder.PreparedUberVariant prepared = UberShaderVariantBuilder.PrepareVariant(this, canonicalShader, manifest);
            SetRequestedUberVariant(prepared.Request);

            if (ActiveUberVariant.VariantHash != 0 &&
                ActiveUberVariant.VariantHash == prepared.BindingState.VariantHash &&
                UberShaderVariantBuilder.IsGeneratedVariant(GetShader(EShaderType.Fragment)))
            {
                return true;
            }

            UberShaderVariantTelemetry.RecordRequest();
            SetUberVariantStatus(new UberMaterialVariantStatus
            {
                Stage = EUberMaterialVariantStage.Preparing,
                ActiveVariantHash = ActiveUberVariant.VariantHash,
                RequestedVariantHash = prepared.Request.VariantHash,
                CacheHit = prepared.CacheHit,
                PreparationMilliseconds = prepared.PreparationMilliseconds,
                UniformCount = prepared.UniformCount,
                SamplerCount = prepared.SamplerCount,
                GeneratedSourceLength = prepared.GeneratedSourceLength,
            });

            Stopwatch adoptionStopwatch = Stopwatch.StartNew();
            SetActiveUberVariant(prepared.BindingState);
            SetShader(EShaderType.Fragment, prepared.FragmentShader, coerceShaderType: true);
            adoptionStopwatch.Stop();

            UberMaterialVariantStatus activeStatus = new()
            {
                Stage = EUberMaterialVariantStage.Active,
                RequestedVariantHash = prepared.Request.VariantHash,
                ActiveVariantHash = prepared.BindingState.VariantHash,
                CacheHit = prepared.CacheHit,
                PreparationMilliseconds = prepared.PreparationMilliseconds,
                AdoptionMilliseconds = adoptionStopwatch.Elapsed.TotalMilliseconds,
                UniformCount = prepared.UniformCount,
                SamplerCount = prepared.SamplerCount,
                GeneratedSourceLength = prepared.GeneratedSourceLength,
            };
            SetUberVariantStatus(activeStatus);
            UberShaderVariantTelemetry.RecordSuccess(activeStatus);
            return true;
        }
        catch (Exception ex)
        {
            RestoreSafeUberFallback();
            UberShaderVariantTelemetry.RecordFailure();
            SetUberVariantStatus(new UberMaterialVariantStatus
            {
                Stage = EUberMaterialVariantStage.Failed,
                RequestedVariantHash = RequestedUberVariant.VariantHash,
                ActiveVariantHash = ActiveUberVariant.VariantHash,
                FailureReason = ex.GetBaseException().Message,
            });
            return false;
        }
    }

    public bool EnsureUberVariantPreparedForRendering()
    {
        XRShader? activeFragmentShader = GetShader(EShaderType.Fragment);
        if (HasRenderableUberVariantState(activeFragmentShader))
            return false;

        if (!TryGetUberMaterialState(out XRShader? fragmentShader, out ShaderUiManifest manifest) || fragmentShader is null)
            return false;

        EnsureUberStateInitialized(fragmentShader, manifest);

        if (UberVariantStatus.Stage is EUberMaterialVariantStage.Requested or
            EUberMaterialVariantStage.Preparing or
            EUberMaterialVariantStage.Compiling)
        {
            return false;
        }

        return PrepareUberVariantImmediately();
    }

    /// <summary>
    /// Returns true if this material does not require uber-variant preparation
    /// (non-uber material) or if a generated variant is already active. Used by
    /// the GL mesh generation queue to gate first-use renderer Generate() calls
    /// so the synchronous shader-source generation does not run on the render
    /// thread inside <see cref="GLMeshRenderer.Generate"/>.
    /// </summary>
    public bool IsUberVariantReadyForRendering()
    {
        XRShader? activeFragmentShader = GetShader(EShaderType.Fragment);
        if (HasRenderableUberVariantState(activeFragmentShader))
            return true;

        // Non-uber materials never need variant prep.
        if (!TryGetUberMaterialState(out _, out _))
            return true;

        // Failed prep falls back to the canonical shader; don't keep the queue
        // blocked waiting on a state that won't progress.
        return UberVariantStatus.Stage is EUberMaterialVariantStage.Failed;
    }

    /// <summary>
    /// Kicks off an asynchronous uber-variant build for this material if one is
    /// not already in flight or complete. Safe no-op for non-uber materials.
    /// </summary>
    public void RequestUberVariantPreparationIfNeeded()
    {
        XRShader? activeFragmentShader = GetShader(EShaderType.Fragment);
        if (HasRenderableUberVariantState(activeFragmentShader))
            return;

        if (UberVariantStatus.Stage is EUberMaterialVariantStage.Requested or
            EUberMaterialVariantStage.Preparing or
            EUberMaterialVariantStage.Compiling)
        {
            return;
        }

        if (!TryGetUberMaterialState(out _, out _))
            return;

        RequestUberVariantRebuild();
    }

    private bool HasRenderableUberVariantState(XRShader? activeFragmentShader)
    {
        if (ActiveUberVariant.IsEmpty || ActiveUberVariant.VariantHash == 0)
            return false;

        if (UberShaderVariantBuilder.IsGeneratedVariant(activeFragmentShader))
            return true;

        UberMaterialVariantStatus status = UberVariantStatus;
        return status.Stage is EUberMaterialVariantStage.Ready or EUberMaterialVariantStage.Active &&
               status.ActiveVariantHash == ActiveUberVariant.VariantHash;
    }

    public void RequestUberVariantRebuildDebounced(int debounceMilliseconds = UberConstantPropertyEditDebounceMilliseconds)
    {
        if (debounceMilliseconds <= 0)
        {
            RequestUberVariantRebuild();
            return;
        }

        CancellationTokenSource cancellationTokenSource = ResetUberVariantRequestDebounceCancellation();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(debounceMilliseconds, cancellationTokenSource.Token).ConfigureAwait(false);
                cancellationTokenSource.Token.ThrowIfCancellationRequested();
                RequestUberVariantRebuild();
            }
            catch (OperationCanceledException)
            {
            }
        }, cancellationTokenSource.Token);
    }

    private void ApplyPreparedUberVariant(long serial, Task<UberShaderVariantBuilder.PreparedUberVariant> task)
    {
        if (serial != Interlocked.Read(ref _uberVariantRequestSerial))
            return;

        if (task.IsFaulted)
        {
            RestoreSafeUberFallback();
            UberShaderVariantTelemetry.RecordFailure();
            SetUberVariantStatus(new UberMaterialVariantStatus
            {
                Stage = EUberMaterialVariantStage.Failed,
                RequestedVariantHash = RequestedUberVariant.VariantHash,
                ActiveVariantHash = ActiveUberVariant.VariantHash,
                FailureReason = task.Exception?.GetBaseException().Message,
            });
            return;
        }

        if (task.IsCanceled)
            return;

        UberShaderVariantBuilder.PreparedUberVariant prepared = task.Result;
        SetRequestedUberVariant(prepared.Request);

        SetUberVariantStatus(new UberMaterialVariantStatus
        {
            Stage = EUberMaterialVariantStage.Preparing,
            RequestedVariantHash = prepared.Request.VariantHash,
            ActiveVariantHash = ActiveUberVariant.VariantHash,
            CacheHit = prepared.CacheHit,
            PreparationMilliseconds = prepared.PreparationMilliseconds,
            UniformCount = prepared.UniformCount,
            SamplerCount = prepared.SamplerCount,
            GeneratedSourceLength = prepared.GeneratedSourceLength,
        });

        if (ActiveUberVariant.Equals(prepared.BindingState) &&
            UberShaderVariantBuilder.IsGeneratedVariant(GetShader(EShaderType.Fragment)))
        {
            UberMaterialVariantStatus status = new()
            {
                Stage = EUberMaterialVariantStage.Active,
                RequestedVariantHash = prepared.Request.VariantHash,
                ActiveVariantHash = prepared.BindingState.VariantHash,
                CacheHit = true,
                PreparationMilliseconds = prepared.PreparationMilliseconds,
                AdoptionMilliseconds = 0.0,
                UniformCount = prepared.UniformCount,
                SamplerCount = prepared.SamplerCount,
                GeneratedSourceLength = prepared.GeneratedSourceLength,
            };
            SetUberVariantStatus(status);
            UberShaderVariantTelemetry.RecordSuccess(status);
            return;
        }

        SetUberVariantStatus(new UberMaterialVariantStatus
        {
            Stage = EUberMaterialVariantStage.Compiling,
            RequestedVariantHash = prepared.Request.VariantHash,
            ActiveVariantHash = ActiveUberVariant.VariantHash,
            CacheHit = prepared.CacheHit,
            PreparationMilliseconds = prepared.PreparationMilliseconds,
            UniformCount = prepared.UniformCount,
            SamplerCount = prepared.SamplerCount,
            GeneratedSourceLength = prepared.GeneratedSourceLength,
        });

        Stopwatch adoptionStopwatch = Stopwatch.StartNew();
        SetActiveUberVariant(prepared.BindingState);
        SetShader(EShaderType.Fragment, prepared.FragmentShader, coerceShaderType: true);
        adoptionStopwatch.Stop();

        UberMaterialVariantStatus activeStatus = new()
        {
            Stage = EUberMaterialVariantStage.Active,
            RequestedVariantHash = prepared.Request.VariantHash,
            ActiveVariantHash = prepared.BindingState.VariantHash,
            CacheHit = prepared.CacheHit,
            PreparationMilliseconds = prepared.PreparationMilliseconds,
            AdoptionMilliseconds = adoptionStopwatch.Elapsed.TotalMilliseconds,
            UniformCount = prepared.UniformCount,
            SamplerCount = prepared.SamplerCount,
            GeneratedSourceLength = prepared.GeneratedSourceLength,
        };
        SetUberVariantStatus(activeStatus);
        UberShaderVariantTelemetry.RecordSuccess(activeStatus);
    }

    private CancellationTokenSource ResetUberVariantBuildCancellation()
    {
        lock (_uberVariantBuildLock)
        {
            _uberVariantBuildCancellation?.Cancel();
            _uberVariantBuildCancellation?.Dispose();
            _uberVariantBuildCancellation = new CancellationTokenSource();
            return _uberVariantBuildCancellation;
        }
    }

    private CancellationTokenSource ResetUberVariantRequestDebounceCancellation()
    {
        lock (_uberVariantRequestDebounceLock)
        {
            _uberVariantRequestDebounceCancellation?.Cancel();
            _uberVariantRequestDebounceCancellation?.Dispose();
            _uberVariantRequestDebounceCancellation = new CancellationTokenSource();
            return _uberVariantRequestDebounceCancellation;
        }
    }

    private void CancelUberVariantRebuildDebounce()
    {
        lock (_uberVariantRequestDebounceLock)
        {
            _uberVariantRequestDebounceCancellation?.Cancel();
            _uberVariantRequestDebounceCancellation?.Dispose();
            _uberVariantRequestDebounceCancellation = null;
        }
    }

    private XRShader ResolveCanonicalUberFragmentShader(XRShader fragmentShader)
    {
        if (_uberCanonicalFragmentShader is not null &&
            !UberShaderVariantBuilder.IsGeneratedVariant(_uberCanonicalFragmentShader))
        {
            return _uberCanonicalFragmentShader;
        }

        if (!UberShaderVariantBuilder.IsGeneratedVariant(fragmentShader))
        {
            _uberCanonicalFragmentShader = fragmentShader;
            return fragmentShader;
        }

        // Native material serialization embeds generated variant text but does
        // not persist the canonical engine shader's FilePath. Rehydrate that
        // canonical identity explicitly so a reloaded material is still
        // recognized as Uber-backed. This also keeps the lightweight pending
        // fallback active while a large OpenGL variant finishes linking.
        string? shaderPath = fragmentShader.Source?.FilePath ?? fragmentShader.FilePath;
        if (!string.IsNullOrWhiteSpace(shaderPath) && File.Exists(shaderPath))
        {
            TextFile text = new(shaderPath);
            text.LoadText(shaderPath);

            _uberCanonicalFragmentShader = new XRShader(fragmentShader.Type, text)
            {
                Name = fragmentShader.Name,
                GenerateAsync = fragmentShader.GenerateAsync,
            };
            return _uberCanonicalFragmentShader;
        }

        _uberCanonicalFragmentShader = ShaderHelper.UberFragForward();
        return _uberCanonicalFragmentShader;
    }

    private static string? ResolveShaderPathOrName(XRShader shader)
    {
        if (!string.IsNullOrWhiteSpace(shader.Source?.FilePath))
            return shader.Source.FilePath;
        if (!string.IsNullOrWhiteSpace(shader.FilePath))
            return shader.FilePath;
        if (!string.IsNullOrWhiteSpace(shader.Source?.Name))
            return shader.Source.Name;
        return shader.Name;
    }

    private void RestoreSafeUberFallback()
    {
        XRShader? fragmentShader = GetShader(EShaderType.Fragment);
        if (fragmentShader is null || !ActiveUberVariant.IsEmpty)
            return;

        XRShader canonicalShader = ResolveCanonicalUberFragmentShader(fragmentShader);
        if (!ReferenceEquals(fragmentShader, canonicalShader))
            SetShader(EShaderType.Fragment, canonicalShader, coerceShaderType: true);
    }

    private static bool ResolveInitialFeatureEnabled(XRShader fragmentShader, ShaderUiFeature feature)
    {
        // Feature UI annotations (//@feature(... default=off)) in the canonical
        // source are the source of truth for a feature's default state.
        //
        // The canonical Uber fragment source may contain fallback
        // XRENGINE_UBER_DISABLE_* guards so raw, unprepared shader use stays
        // safe. Those guards are not authored material state; the variant
        // builder strips them and reinjects the material's own feature mask.
        //
        // So: honor feature.DefaultEnabled directly. Features without a guard
        // macro also fall through to the same annotation-driven default.
        return feature.DefaultEnabled;
    }

    private static bool IsAuthorableUberProperty(ShaderUiProperty property)
        => property.Name.StartsWith("_", StringComparison.Ordinal) ||
           string.Equals(property.Name, "AlphaCutoff", StringComparison.Ordinal);
}
