using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace XREngine.Core;

/// <summary>
/// Enumerates the loadable types in an assembly once and shares the partial
/// result with every reflection-based discovery service.
/// </summary>
public static class XRLoadableTypeCatalog
{
    private sealed class AssemblyTypes(Type[] types, Type[] exportedTypes)
    {
        internal Type[] Types { get; } = types;
        internal Type[] ExportedTypes { get; } = exportedTypes;
    }

    private static readonly object CacheLock = new();
    private static readonly ConditionalWeakTable<Assembly, AssemblyTypes> Cache = new();
    private static readonly ConcurrentDictionary<string, byte> LoadFailureDiagnostics =
        new(StringComparer.Ordinal);

    private static readonly string[] IgnoredEngineScanAssemblyPrefixes =
    [
        "System",
        "Microsoft",
        "netstandard",
        "mscorlib",
        "Silk.NET",
        "Newtonsoft",
        "YamlDotNet",
        "MathNet",
        "ImGuiNET",
        "SixLabors",
        "Assimp",
        "SharpFont",
        "StbImage",
        "NVorbis",
        "NAudio",
        "OpenTK",
        "Vulkan",
        "Steamworks",
        "Concentus",
        "Bepu",
        "Jolt",
        "MagicPhysX",
        "Rive",
        "NUnit",
        "Shouldly",
        "testhost",
        "NuGet",
        "Google",
        "Grpc",
        "SkiaSharp",
        "HarfBuzz"
    ];

    /// <summary>Gets every type that the runtime could load from <paramref name="assembly"/>.</summary>
    public static IReadOnlyList<Type> GetTypes(Assembly assembly)
        => GetOrCreate(assembly).Types;

    /// <summary>Gets every externally visible loadable type from <paramref name="assembly"/>.</summary>
    public static IReadOnlyList<Type> GetExportedTypes(Assembly assembly)
        => GetOrCreate(assembly).ExportedTypes;

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="assembly"/> can contain engine domain types,
    /// transforms, or type redirect attributes. Excludes BCL and third-party libraries.
    /// </summary>
    public static bool ShouldScanAssemblyForEngineTypes(Assembly? assembly)
    {
        if (assembly is null || assembly.IsDynamic)
            return false;

        string? fullName = assembly.FullName;
        if (string.IsNullOrEmpty(fullName))
            return true;

        ReadOnlySpan<char> nameSpan = fullName.AsSpan();
        int commaIndex = nameSpan.IndexOf(',');
        if (commaIndex > 0)
            nameSpan = nameSpan[..commaIndex];

        for (int i = 0; i < IgnoredEngineScanAssemblyPrefixes.Length; i++)
        {
            string prefix = IgnoredEngineScanAssemblyPrefixes[i];
            if (nameSpan.StartsWith(prefix.AsSpan(), StringComparison.OrdinalIgnoreCase) &&
                (nameSpan.Length == prefix.Length || nameSpan[prefix.Length] is '.' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns stable, deduplicated loader diagnostics captured while building
    /// the catalog. This is a cold diagnostic API and allocates a snapshot.
    /// </summary>
    public static string[] GetLoadFailureDiagnostics()
        => [.. LoadFailureDiagnostics.Keys.Order(StringComparer.Ordinal)];

    private static AssemblyTypes GetOrCreate(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        lock (CacheLock)
        {
            if (Cache.TryGetValue(assembly, out AssemblyTypes? existing))
                return existing;

            AssemblyTypes created = LoadAssemblyTypes(assembly);
            Cache.Add(assembly, created);
            return created;
        }
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Runtime type discovery is used only by dynamic/editor paths; published AOT paths use registered metadata.")]
    private static AssemblyTypes LoadAssemblyTypes(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            types = [.. exception.Types.OfType<Type>()];
            Exception?[] loaderExceptions = exception.LoaderExceptions ?? [];
            if (loaderExceptions.Length == 0)
                RecordLoadFailure(assembly, exception);
            else
                foreach (Exception? loaderException in loaderExceptions)
                    if (loaderException is not null)
                        RecordLoadFailure(assembly, loaderException);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or
                FileLoadException or
                BadImageFormatException or
                NotSupportedException)
        {
            types = [];
            RecordLoadFailure(assembly, exception);
        }

        Type[] exportedTypes = [.. types.Where(static type => type.IsVisible)];
        return new(types, exportedTypes);
    }

    private static void RecordLoadFailure(Assembly assembly, Exception exception)
    {
        string assemblyName = assembly.FullName ?? assembly.GetName().Name ?? "<unknown assembly>";
        string key = $"{assemblyName} | {exception.GetType().FullName}: {exception.Message}";
        LoadFailureDiagnostics.TryAdd(key, 0);
    }
}
