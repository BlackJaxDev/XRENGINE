using System.Globalization;
using OscCore.OscQuery;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Editor;
using XREngine.Runtime.Bootstrap;
using XREngine.Scene.Prefabs;
using YamlDotNet.RepresentationModel;

namespace XREngine.Scene.Importers;

/// <summary>
/// Stages a user-supplied Unity/VPM package, discovers or resolves requested <c>.controller</c>
/// assets, remaps the face mesh path, compiles the controllers, and merges their layers and
/// parameters into an avatar's single <see cref="AnimStateMachineComponent"/>.
/// </summary>
public static class VrcftTemplateAttachmentService
{
    private const int MaxUnmatchedSampleCount = 12;
    private const string AvatarSdkScriptGuid = "67cc4cb7839cd3741b63733d5adf0442";

    /// <summary>
    /// Stages or locates the package described by <paramref name="options"/> and returns all
    /// package-relative <c>.controller</c> paths sorted alphabetically.
    /// </summary>
    public static IReadOnlyList<string> DiscoverControllers(VrcftTemplateAttachOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        (_, string packageRoot, _) = ResolveTemplatePackage(options);
        if (!Directory.Exists(packageRoot))
            return [];

        var controllers = new List<string>();
        foreach (string file in Directory.EnumerateFiles(packageRoot, "*.controller", SearchOption.AllDirectories))
            controllers.Add(Path.GetRelativePath(packageRoot, file).Replace('\\', '/'));

        controllers.Sort(StringComparer.OrdinalIgnoreCase);
        return controllers;
    }

    /// <summary>
    /// Compiles and merges all controllers listed in <see cref="VrcftTemplateAttachOptions.ControllerPaths"/>
    /// into <paramref name="targetStateMachineComponent"/> (or a dedicated non-body
    /// <see cref="AnimStateMachineComponent"/> on <paramref name="avatarRoot"/> when omitted).
    /// </summary>
    public static VrcftTemplateAttachResult Attach(
        SceneNode avatarRoot,
        VrcftTemplateAttachOptions options,
        AnimStateMachineComponent? targetStateMachineComponent = null)
    {
        ArgumentNullException.ThrowIfNull(avatarRoot);
        ArgumentNullException.ThrowIfNull(options);

        if (options.ControllerPaths is null || options.ControllerPaths.Count == 0)
            throw new ArgumentException("At least one .controller path must be specified.", nameof(options));

        (string projectRoot, string packageRoot, SourceProjectImportContext? reusableContext) =
            ResolveTemplatePackage(options);

        var resolvedControllerPaths = new List<string>(options.ControllerPaths.Count);
        for (int i = 0; i < options.ControllerPaths.Count; i++)
        {
            string rawControllerPath = options.ControllerPaths[i]?.Trim() ?? string.Empty;
            if (rawControllerPath.Length == 0)
                continue;

            string fullControllerPath = Path.IsPathRooted(rawControllerPath)
                ? Path.GetFullPath(rawControllerPath)
                : Path.GetFullPath(Path.Combine(packageRoot, rawControllerPath.Replace('/', Path.DirectorySeparatorChar)));

            if (!File.Exists(fullControllerPath))
            {
                throw new FileNotFoundException(
                    $"Animator controller '{rawControllerPath}' was not found in '{packageRoot}'.",
                    fullControllerPath);
            }

            resolvedControllerPaths.Add(fullControllerPath);
        }

        if (resolvedControllerPaths.Count == 0)
            throw new ArgumentException("At least one non-empty .controller path must be specified.", nameof(options));

        VrcftFaceMeshResolution faceMesh = VrcftFaceMeshResolver.Resolve(
            avatarRoot,
            options.FaceMeshPathOverride);

        string sourceMeshPath = string.IsNullOrWhiteSpace(options.SourceFaceMeshPath)
            ? "Body"
            : VrcftFaceMeshResolver.NormalizeRelativePath(options.SourceFaceMeshPath);

        IReadOnlyDictionary<string, string>? nodePathRewrites = null;
        if (faceMesh.RelativeNodePath is string targetMeshPath
            && !string.Equals(targetMeshPath, sourceMeshPath, StringComparison.Ordinal))
        {
            nodePathRewrites = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [sourceMeshPath] = targetMeshPath,
            };
        }

        var compileOptions = new UnityAnimatorCompileOptions
        {
            NodePathRewrites = nodePathRewrites,
            AllowMissingImportedBindings = options.AllowMissingImportedBindings,
            EmitUnsupportedFeatureDiagnostics = true,
        };

        SourceProjectImportContext importContext = reusableContext
            ?? new SourceProjectImportContext(
                resolvedControllerPaths[0],
                explicitProjectOrAssetsRoot: projectRoot,
                progress: options.Progress);

        AnimStateMachineComponent stateMachineComponent = targetStateMachineComponent
            ?? ResolveTargetStateMachineComponent(avatarRoot);

        if (stateMachineComponent.Humanoid is null
            && avatarRoot.GetComponent<HumanoidComponent>() is HumanoidComponent humanoid)
            stateMachineComponent.Humanoid = humanoid;

        bool wasActive = stateMachineComponent.IsActive;
        bool shouldDeactivateForMerge = stateMachineComponent.IsActiveInHierarchy;
        if (shouldDeactivateForMerge)
            stateMachineComponent.IsActive = false;

        AnimStateMachine stateMachine = stateMachineComponent.StateMachine;
        int firstAddedLayerIndex = stateMachine.Layers.Count;

        var compileResults = new List<UnityAnimatorCompileResult>(resolvedControllerPaths.Count);
        var diagnostics = new List<SourceImportDiagnostic>();

        for (int i = 0; i < resolvedControllerPaths.Count; i++)
        {
            UnityAnimatorCompileResult compiled = UnityAnimatorGraphCompiler.MergeInto(
                stateMachine,
                resolvedControllerPaths[i],
                importContext,
                compileOptions);
            compileResults.Add(compiled);
            diagnostics.AddRange(compiled.Diagnostics);
        }

        Dictionary<string, OscQueryValueKind> oscParameterTypeOverrides = CollectCompanionExpressionParameters(
            resolvedControllerPaths,
            stateMachine,
            options.ApplyLocalDefaults);

        if (options.ApplyLocalDefaults)
            ApplyLocalDefaults(stateMachine);

        if (shouldDeactivateForMerge && wasActive)
            stateMachineComponent.IsActive = true;

        (int matchedCount, List<string> unmatchedBlendshapes) =
            VrcftFaceMeshResolver.AuditBlendshapeCoverage(
                stateMachine,
                firstAddedLayerIndex,
                faceMesh.BlendshapeNames);

        SourceImportDiagnostic coverageDiagnostic = CreateCoverageDiagnostic(
            avatarRoot,
            resolvedControllerPaths,
            faceMesh,
            matchedCount,
            unmatchedBlendshapes);
        diagnostics.Add(coverageDiagnostic);
        importContext.Diagnostics.Add(coverageDiagnostic);
        Debug.Animation($"[VrcftTemplate] {coverageDiagnostic.Message}");

        return new VrcftTemplateAttachResult
        {
            StateMachineComponent = stateMachineComponent,
            TemplatePackageRoot = packageRoot,
            ControllerPaths = resolvedControllerPaths,
            CompileResults = compileResults,
            FaceMeshResolution = faceMesh,
            MatchedBlendshapeCount = matchedCount,
            UnmatchedBlendshapes = unmatchedBlendshapes,
            Diagnostics = diagnostics,
            OscParameterTypeOverrides = oscParameterTypeOverrides,
        };
    }

    private static AnimStateMachineComponent ResolveTargetStateMachineComponent(SceneNode avatarRoot)
    {
        if (avatarRoot.GetComponent<Data.Components.AnimStateMachineOscReceiverComponent>()?.AnimStateMachine is AnimStateMachineComponent bound)
            return bound;

        foreach (AnimStateMachineComponent existing in avatarRoot.GetComponents<AnimStateMachineComponent>())
        {
            if (!existing.DrivesHumanoidBodyPose)
                return existing;
        }

        return avatarRoot.AddComponent<AnimStateMachineComponent>("Face Tracking Animator")
            ?? throw new InvalidOperationException(
                $"Could not add AnimStateMachineComponent to avatar node '{avatarRoot.Name}'.");
    }

    private static (string ProjectRoot, string PackageRoot, SourceProjectImportContext? ReusableContext)
        ResolveTemplatePackage(VrcftTemplateAttachOptions options)
    {
        string? requestedPackageId = string.IsNullOrWhiteSpace(options.PackageId)
            ? null
            : options.PackageId.Trim();

        // Reuse the avatar's already-staged Unity project when it contains the requested package.
        if (options.ExistingProjectContext is SourceProjectImportContext existingContext
            && TryFindPackageDirectory(existingContext.ProjectRoot, requestedPackageId, out string existingPackageRoot))
        {
            return (existingContext.ProjectRoot, existingPackageRoot, existingContext);
        }

        string? source = string.IsNullOrWhiteSpace(options.PackageSource)
            ? null
            : options.PackageSource.Trim();
        if (source is null)
        {
            throw new InvalidOperationException(
                "No template PackageSource was configured. Supply a VPM repository URL, local .zip archive, package folder, or Unity project root.");
        }

        // Use a local Unity project directory directly when it already contains Assets/ and Packages/.
        if (!IsRemoteSource(source))
        {
            string localPath = ResolveLocalSourcePath(source, options.ResolveLocalPath);
            if (Directory.Exists(localPath)
                && Directory.Exists(Path.Combine(localPath, "Assets"))
                && TryFindPackageDirectory(localPath, requestedPackageId, out string nestedPackageRoot))
            {
                return (localPath, nestedPackageRoot, null);
            }
        }

        string cacheRoot = ResolveCacheRoot(options.CacheRoot);
        bool isVpmRepository = IsVpmRepositorySource(source);
        var dependency = new UnityPackageDependencySettings
        {
            Source = source,
            PackageId = isVpmRepository ? requestedPackageId : null,
            Version = isVpmRepository ? options.PackageVersion : null,
        };

        List<string> resolvedSources = UnityPackageDependencyDownloader.Resolve(
            [dependency],
            cacheRoot,
            path => ResolveLocalSourcePath(path, options.ResolveLocalPath),
            options.Progress);

        if (resolvedSources.Count == 0)
            throw new FileNotFoundException($"Could not resolve template package source '{source}'.");

        string stagedProjectRoot = UnityPackageImportSource.PrepareStandalonePackageProject(
            resolvedSources[0],
            cacheRoot,
            options.Progress);

        if (!TryFindPackageDirectory(stagedProjectRoot, requestedPackageId, out string stagedPackageRoot))
        {
            string detail = requestedPackageId is null
                ? "no package directory with package.json was found under 'Packages/' (set PackageId if multiple packages exist)"
                : $"package 'Packages/{requestedPackageId}' was not found";
            throw new DirectoryNotFoundException(
                $"Staged template project '{stagedProjectRoot}' could not resolve package: {detail}.");
        }

        return (stagedProjectRoot, stagedPackageRoot, null);
    }

    private static bool TryFindPackageDirectory(
        string projectRoot,
        string? requestedPackageId,
        out string packageRoot)
    {
        string packagesDir = Path.Combine(projectRoot, "Packages");
        if (!Directory.Exists(packagesDir))
        {
            packageRoot = string.Empty;
            return false;
        }

        if (requestedPackageId is not null)
        {
            string exact = Path.Combine(packagesDir, requestedPackageId);
            if (Directory.Exists(exact) && File.Exists(Path.Combine(exact, "package.json")))
            {
                packageRoot = exact;
                return true;
            }

            packageRoot = string.Empty;
            return false;
        }

        string? singleCandidate = null;
        foreach (string dir in Directory.EnumerateDirectories(packagesDir))
        {
            if (!File.Exists(Path.Combine(dir, "package.json")))
                continue;
            if (singleCandidate is not null)
            {
                packageRoot = string.Empty;
                return false;
            }
            singleCandidate = dir;
        }

        if (singleCandidate is not null)
        {
            packageRoot = singleCandidate;
            return true;
        }

        packageRoot = string.Empty;
        return false;
    }

    private static string ResolveCacheRoot(string? explicitCacheRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitCacheRoot))
            return Path.GetFullPath(explicitCacheRoot);

        string? engineCache = Engine.Assets.GameCachePath;
        if (string.IsNullOrWhiteSpace(engineCache))
            throw new InvalidOperationException(
                "Template attachment requires a cache directory. Set VrcftTemplateAttachOptions.CacheRoot or initialize Engine.Assets.GameCachePath.");

        return engineCache;
    }

    private static string ResolveLocalSourcePath(string rawPath, Func<string, string>? resolver)
        => resolver is not null ? resolver(rawPath) : Path.GetFullPath(rawPath);

    private static bool IsRemoteSource(string source)
        => source.StartsWith("vcc:", StringComparison.OrdinalIgnoreCase)
            || (Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) && !uri.IsFile);

    private static bool IsVpmRepositorySource(string source)
    {
        if (source.StartsWith("vcc:", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
            return false;

        string extension = Path.GetExtension(uri.AbsolutePath);
        return !extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".unitypackage", StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyLocalDefaults(AnimStateMachine stateMachine)
    {
        if (stateMachine.Variables.TryGetValue("IsLocal", out AnimVar? isLocal))
            isLocal.BoolValue = true;
        else
            stateMachine.NewBool("IsLocal", true);

        if (stateMachine.Variables.TryGetValue("FT/DirectBlend", out AnimVar? directBlend))
            directBlend.FloatValue = 1.0f;

        // VRCFaceTracking sends EyeTrackingActive, LipTrackingActive, and ExpressionTrackingActive
        // as ConditionalBoolParameter without sendOnLoad, so an already-running VRCFT instance does
        // not re-emit true when a new client connects. Enable them locally by default; VRCFT still
        // overrides them to false over OSC whenever a tracking module is paused or inactive.
        if (stateMachine.Variables.TryGetValue("EyeTrackingActive", out AnimVar? eyeActive))
            eyeActive.BoolValue = true;

        if (stateMachine.Variables.TryGetValue("LipTrackingActive", out AnimVar? lipActive))
            lipActive.BoolValue = true;

        if (stateMachine.Variables.TryGetValue("ExpressionTrackingActive", out AnimVar? expressionActive))
            expressionActive.BoolValue = true;
    }

    private static Dictionary<string, OscQueryValueKind> CollectCompanionExpressionParameters(
        IReadOnlyList<string> resolvedControllerPaths,
        AnimStateMachine stateMachine,
        bool applyDefaults)
    {
        var overrides = new Dictionary<string, OscQueryValueKind>(StringComparer.Ordinal);
        var scannedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < resolvedControllerPaths.Count; i++)
        {
            string? directory = Path.GetDirectoryName(resolvedControllerPaths[i]);
            if (string.IsNullOrWhiteSpace(directory) || !scannedDirectories.Add(directory) || !Directory.Exists(directory))
                continue;

            foreach (string assetPath in Directory.EnumerateFiles(directory, "*.asset", SearchOption.TopDirectoryOnly))
                TryReadExpressionParametersAsset(assetPath, stateMachine, applyDefaults, overrides);
        }

        return overrides;
    }

    private static void TryReadExpressionParametersAsset(
        string assetPath,
        AnimStateMachine stateMachine,
        bool applyDefaults,
        Dictionary<string, OscQueryValueKind> overrides)
    {
        string rawText;
        try
        {
            rawText = File.ReadAllText(assetPath);
        }
        catch (IOException)
        {
            return;
        }

        // Fast reject for binary assets or unrelated ScriptableObjects before YAML parsing.
        if (!rawText.Contains(AvatarSdkScriptGuid, StringComparison.OrdinalIgnoreCase)
            || !rawText.Contains("parameters:", StringComparison.Ordinal))
        {
            return;
        }

        int monoStart = rawText.IndexOf("MonoBehaviour:", StringComparison.Ordinal);
        if (monoStart < 0)
            return;

        try
        {
            var yaml = new YamlStream();
            using var reader = new StringReader(rawText[monoStart..]);
            yaml.Load(reader);
            if (yaml.Documents.Count == 0
                || yaml.Documents[0].RootNode is not YamlMappingNode root
                || !root.Children.TryGetValue(new YamlScalarNode("MonoBehaviour"), out YamlNode? monoNode)
                || monoNode is not YamlMappingNode monoMap
                || !monoMap.Children.TryGetValue(new YamlScalarNode("parameters"), out YamlNode? parametersNode)
                || parametersNode is not YamlSequenceNode parametersSeq)
            {
                return;
            }

            foreach (YamlNode item in parametersSeq.Children)
            {
                if (item is not YamlMappingNode paramMap)
                    continue;

                string? name = TryGetYamlScalar(paramMap, "name");
                if (string.IsNullOrWhiteSpace(name) || !stateMachine.Variables.TryGetValue(name, out AnimVar? variable))
                    continue;

                if (int.TryParse(TryGetYamlScalar(paramMap, "valueType"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int valueType))
                {
                    OscQueryValueKind kind = valueType switch
                    {
                        0 => OscQueryValueKind.WholeNumber,
                        1 => OscQueryValueKind.FloatingPoint,
                        2 => OscQueryValueKind.Boolean,
                        _ => OscQueryValueKind.None,
                    };

                    if (kind != OscQueryValueKind.None)
                        overrides[name] = kind;
                }

                if (applyDefaults
                    && float.TryParse(TryGetYamlScalar(paramMap, "defaultValue"), NumberStyles.Float, CultureInfo.InvariantCulture, out float defaultValue)
                    && MathF.Abs(defaultValue) > 1e-6f
                    && MathF.Abs(variable.FloatValue) <= 1e-6f)
                {
                    variable.FloatValue = defaultValue;
                }
            }
        }
        catch (Exception)
        {
            // Ignore malformed or unrelated YAML assets in the same folder.
        }
    }

    private static string? TryGetYamlScalar(YamlMappingNode map, string key)
        => map.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value) && value is YamlScalarNode scalar
            ? scalar.Value
            : null;

    private static SourceImportDiagnostic CreateCoverageDiagnostic(
        SceneNode avatarRoot,
        IReadOnlyList<string> controllerPaths,
        VrcftFaceMeshResolution faceMesh,
        int matchedCount,
        List<string> unmatchedBlendshapes)
    {
        string avatarName = string.IsNullOrWhiteSpace(avatarRoot.Name) ? SceneNode.DefaultName : avatarRoot.Name;
        int totalReferenced = matchedCount + unmatchedBlendshapes.Count;
        string primaryControllerPath = controllerPaths[0];
        string controllerSummary = controllerPaths.Count == 1
            ? Path.GetFileName(primaryControllerPath)
            : $"{controllerPaths.Count} controllers";

        if (faceMesh.Source == EVrcftFaceMeshResolutionSource.None || faceMesh.ModelComponent is null)
        {
            string targetSummary = faceMesh.RelativeNodePath is null
                ? "no target face mesh was found"
                : $"target path '{FormatDisplayPath(faceMesh.RelativeNodePath)}' has no ModelComponent";
            return new SourceImportDiagnostic
            {
                Code = "VRCFT0002",
                Severity = SourceImportDiagnosticSeverity.Warning,
                Category = SourceImportDiagnosticCategory.OptionalUnsupported,
                Message = $"Attached {controllerSummary} to '{avatarName}', but {targetSummary}. " +
                          $"Blendshape coverage: 0/{totalReferenced} matched.",
                SourcePath = primaryControllerPath,
                PropertyPath = faceMesh.RelativeNodePath,
            };
        }

        string displayPath = FormatDisplayPath(faceMesh.RelativeNodePath ?? string.Empty);
        string unmatchedDetail = unmatchedBlendshapes.Count == 0
            ? "all referenced blendshapes matched"
            : FormatUnmatchedSample(unmatchedBlendshapes);

        return new SourceImportDiagnostic
        {
            Code = "VRCFT0001",
            Severity = SourceImportDiagnosticSeverity.Info,
            Category = SourceImportDiagnosticCategory.OptionalUnsupported,
            Message = $"Attached {controllerSummary} to '{avatarName}' targeting '{displayPath}' " +
                      $"({faceMesh.Source}). Blendshape coverage: {matchedCount}/{totalReferenced} matched, " +
                      $"{unmatchedBlendshapes.Count} unmatched ({unmatchedDetail}).",
            SourcePath = primaryControllerPath,
            PropertyPath = faceMesh.RelativeNodePath,
        };
    }

    private static string FormatDisplayPath(string relativePath)
        => relativePath.Length == 0 ? "<avatar root>" : relativePath;

    private static string FormatUnmatchedSample(List<string> unmatchedBlendshapes)
    {
        int sampleCount = Math.Min(unmatchedBlendshapes.Count, MaxUnmatchedSampleCount);
        string sample = string.Join(", ", unmatchedBlendshapes.Take(sampleCount));
        return unmatchedBlendshapes.Count > sampleCount
            ? $"missing: {sample}, +{unmatchedBlendshapes.Count - sampleCount} more"
            : $"missing: {sample}";
    }
}
