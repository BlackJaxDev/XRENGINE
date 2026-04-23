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
    private static readonly ConcurrentDictionary<string, XRTexture2D> UberDefaultSamplerTextures = new(StringComparer.Ordinal);

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
        string? shaderPath = canonicalShader.Source?.FilePath ?? canonicalShader.FilePath;
        if (!string.Equals(Path.GetFileName(shaderPath), "UberShader.frag", StringComparison.OrdinalIgnoreCase))
            return false;

        fragmentShader = canonicalShader;
        manifest = canonicalShader.GetUiManifest();
        return manifest.Properties.Count > 0;
    }

    public void EnsureUberStateInitialized()
    {
        if (TryGetUberMaterialState(out XRShader? fragmentShader, out ShaderUiManifest manifest) && fragmentShader is not null)
            EnsureUberStateInitialized(fragmentShader, manifest);
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

        EnsureUberEnabledFeatureResources(manifest);
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
            if (!string.Equals(property.FeatureId, featureId, StringComparison.Ordinal))
                continue;

            if (property.IsSampler)
            {
                texturesChanged |= EnsureUberDefaultSamplerTexture(property.Name);
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
        if (Parameter<ShaderVar>(property.Name) is not null)
            return false;

        if (!ShaderVar.GlslTypeMap.TryGetValue(property.GlslType, out EShaderVarType shaderVarType))
            return false;

        ShaderVar? parameter = ShaderVar.CreateForType(shaderVarType, property.Name);
        if (parameter is null)
            return false;

        ApplyUberDefaultLiteral(parameter, property.DefaultLiteral);

        ShaderVar[] current = Parameters ?? [];
        Array.Resize(ref current, current.Length + 1);
        current[^1] = parameter;
        Parameters = current;
        return true;
    }

    private bool EnsureStylizedLightingModeDefault()
    {
        if (UberAuthoredState.GetProperty("_LightingMode") is not null)
            return false;

        if (Parameter<ShaderInt>("_LightingMode") is not ShaderInt lightingMode)
            return false;

        if (lightingMode.Value != 6)
            return false;

        lightingMode.SetValue(5);
        return true;
    }

    private bool EnsureUberDefaultSamplerTexture(string samplerName)
    {
        foreach (XRTexture? texture in Textures)
        {
            if (texture?.SamplerName?.Equals(samplerName, StringComparison.Ordinal) == true)
                return false;
        }

        XRTexture2D defaultTexture = GetDefaultUberSamplerTexture(samplerName);
        EventList<XRTexture?> updated = [.. Textures, defaultTexture];
        Textures = updated;
        return true;
    }

    private static XRTexture2D GetDefaultUberSamplerTexture(string samplerName)
        => UberDefaultSamplerTextures.GetOrAdd(samplerName, static key => CreateDefaultUberSamplerTexture(key));

    private static XRTexture2D CreateDefaultUberSamplerTexture(string samplerName)
    {
        ColorF4 color = ResolveDefaultUberSamplerColor(samplerName);
        return new XRTexture2D(1u, 1u, color)
        {
            Name = samplerName,
            SamplerName = samplerName,
            MagFilter = ETexMagFilter.Linear,
            MinFilter = ETexMinFilter.Linear,
            UWrap = ETexWrapMode.Repeat,
            VWrap = ETexWrapMode.Repeat,
            AlphaAsTransparency = true,
            AutoGenerateMipmaps = false,
            Resizable = false,
        };
    }

    private static ColorF4 ResolveDefaultUberSamplerColor(string samplerName)
    {
        if (samplerName.Contains("Normal", StringComparison.OrdinalIgnoreCase)
            || samplerName.Contains("Bump", StringComparison.OrdinalIgnoreCase))
        {
            return new ColorF4(0.5f, 0.5f, 1.0f, 1.0f);
        }

        return ColorF4.White;
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
        if (parts.Length != expectedComponentCount)
            return false;

        float[] parsed = new float[expectedComponentCount];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[i]))
                return false;
        }

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

        UberShaderVariantTelemetry.RecordRequest();
        SetUberVariantStatus(new UberMaterialVariantStatus
        {
            Stage = EUberMaterialVariantStage.Preparing,
            ActiveVariantHash = ActiveUberVariant.VariantHash,
            RequestedVariantHash = RequestedUberVariant.VariantHash,
        });

        try
        {
            Stopwatch adoptionStopwatch = Stopwatch.StartNew();
            UberShaderVariantBuilder.PreparedUberVariant prepared = UberShaderVariantBuilder.PrepareVariant(this, canonicalShader, manifest);
            SetRequestedUberVariant(prepared.Request);
            SetShader(EShaderType.Fragment, prepared.FragmentShader, coerceShaderType: true);
            SetActiveUberVariant(prepared.BindingState);
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

        if (ActiveUberVariant.Equals(prepared.BindingState))
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
        SetShader(EShaderType.Fragment, prepared.FragmentShader, coerceShaderType: true);
        SetActiveUberVariant(prepared.BindingState);
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
        if (_uberCanonicalFragmentShader is not null)
            return _uberCanonicalFragmentShader;

        if (!UberShaderVariantBuilder.IsGeneratedVariant(fragmentShader))
        {
            _uberCanonicalFragmentShader = fragmentShader;
            return fragmentShader;
        }

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

        _uberCanonicalFragmentShader = fragmentShader;
        return fragmentShader;
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
        // The canonical Uber fragment source contains NO unconditional
        // XRENGINE_UBER_DISABLE_* defines for hand-authored materials; those
        // are injected per-material by UberShaderVariantBuilder from authored
        // state. The only source-level disable cascade is inside the
        // XRENGINE_UBER_IMPORT_MATERIAL pipeline-axis block, which applies
        // exclusively to the imported-material variant and is ignored here
        // because we inspect the raw canonical source.
        //
        // So: honor feature.DefaultEnabled directly. Features without a guard
        // macro also fall through to the same annotation-driven default.
        return feature.DefaultEnabled;
    }

    private static bool IsAuthorableUberProperty(ShaderUiProperty property)
        => property.Name.StartsWith("_", StringComparison.Ordinal) ||
           string.Equals(property.Name, "AlphaCutoff", StringComparison.Ordinal);
}