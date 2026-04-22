using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Core.Files;
using YamlDotNet.Serialization;

namespace XREngine.Rendering;

public partial class XRMaterial
{
    private const int UberVariantDebounceMilliseconds = 40;

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
    }

    // All CPU-side request shaping stays here on the material path so renderer work is limited
    // to backend-facing compile/adoption once a prepared variant is ready.
    public void RequestUberVariantRebuild()
    {
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