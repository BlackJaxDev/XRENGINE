using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

namespace XREngine;

public static partial class XRRuntimeEnvironment
{
    public const string PublishedDefineConstant = "XRE_PUBLISHED";
    public const string AotRuntimeDefineConstant = "XRE_AOT_RUNTIME";

    /// <summary>
    /// The define constant of a development launcher that reads the published archives. Only non-AOT development and test
    /// launchers use it.
    /// </summary>
    public const string DevelopmentArchivesDefineConstant = "XRE_DEVELOPMENT_ARCHIVES";

    private static EXRRuntimeBuildKind _buildKind = EXRRuntimeBuildKind.Development;
    private static string? _publishedConfigArchivePath;

    public static EXRRuntimeBuildKind BuildKind => _buildKind;

    /// <summary>True for a published build. A development build that reads the archives is not a published build.</summary>
    public static bool IsPublishedBuild => BuildKind is EXRRuntimeBuildKind.Published or EXRRuntimeBuildKind.PublishedAot;

    public static bool IsAotRuntimeBuild => BuildKind == EXRRuntimeBuildKind.PublishedAot;

    /// <summary>True for a development build, also when it reads the published archives.</summary>
    public static bool IsDevelopmentBuild => BuildKind is EXRRuntimeBuildKind.Development or EXRRuntimeBuildKind.DevelopmentArchives;

    /// <summary>True when asset loads read the configured published archives.</summary>
    public static bool ReadsPublishedArchives => BuildKind is EXRRuntimeBuildKind.Published or EXRRuntimeBuildKind.PublishedAot or EXRRuntimeBuildKind.DevelopmentArchives;

    public static bool SupportsDynamicCode => RuntimeFeature.IsDynamicCodeSupported;

    public static string? PublishedConfigArchivePath => _publishedConfigArchivePath;

    public static void ConfigureBuildKind(EXRRuntimeBuildKind buildKind)
        => _buildKind = buildKind;

    public static void ConfigurePublishedPaths(string? configArchivePath)
    {
        _publishedConfigArchivePath = string.IsNullOrWhiteSpace(configArchivePath)
            ? null
            : Path.GetFullPath(configArchivePath);
        AotRuntimeMetadataStore.ResetForTestsOrReconfiguration();
        Core.Files.PublishedArchiveRegistry.CloseAll();
    }

    /// <summary>
    /// Joins the existing define constants with the build-kind constants. A request for the development-archive constant
    /// together with a published constant is an error, because a published build never reads archives as a development
    /// build.
    /// </summary>
    public static string ComposeDefineConstants(
        string? existing,
        bool includePublishedBuild,
        bool includeAotRuntime,
        bool includeDevelopmentArchives = false)
    {
        if (includeDevelopmentArchives && (includePublishedBuild || includeAotRuntime))
            throw new ArgumentException(
                $"{DevelopmentArchivesDefineConstant} is for non-AOT development launchers only. It cannot join a published build.",
                nameof(includeDevelopmentArchives));

        HashSet<string> defineSet = new(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(existing))
        {
            foreach (string token in existing.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                defineSet.Add(token);
        }

        if (includePublishedBuild)
            defineSet.Add(PublishedDefineConstant);

        if (includeAotRuntime)
            defineSet.Add(AotRuntimeDefineConstant);

        if (includeDevelopmentArchives)
            defineSet.Add(DevelopmentArchivesDefineConstant);

        return string.Join(';', defineSet);
    }
}
