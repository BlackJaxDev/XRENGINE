using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

namespace XREngine;

public enum EXRRuntimeBuildKind
{
    Development,
    Published,
    PublishedAot,
}

public static class XRRuntimeEnvironment
{
    public const string PublishedDefineConstant = "XRE_PUBLISHED";
    public const string AotRuntimeDefineConstant = "XRE_AOT_RUNTIME";

    private static EXRRuntimeBuildKind _buildKind = EXRRuntimeBuildKind.Development;
    private static string? _publishedConfigArchivePath;

    public static EXRRuntimeBuildKind BuildKind => _buildKind;

    public static bool IsPublishedBuild => BuildKind != EXRRuntimeBuildKind.Development;

    public static bool IsAotRuntimeBuild => BuildKind == EXRRuntimeBuildKind.PublishedAot;

    public static bool IsDevelopmentBuild => BuildKind == EXRRuntimeBuildKind.Development;

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
    }

    public static string ComposeDefineConstants(string? existing, bool includePublishedBuild, bool includeAotRuntime)
    {
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

        return string.Join(';', defineSet);
    }
}
