using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using NUnit.Framework;
using Shouldly;
using XREngine.Editor.HotReload;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class RendererBackendCollectibleModuleTests
{
#if DEBUG
    private const string TestConfiguration = "Debug";
#else
    private const string TestConfiguration = "Release";
#endif

    private RendererBackendBuildService? _buildService;
    private string _manifestPath = string.Empty;

    [OneTimeSetUp]
    public async Task BuildCollectibleGeneration()
    {
        _buildService = new(FindWorkspaceRoot())
        {
            RetainedGenerationCount = 3,
        };
        RendererBackendBuildResult result = await _buildService.BuildAsync(
            RendererBackendId.OpenGL,
            TestConfiguration);
        result.Succeeded.ShouldBeTrue(result.Output);
        _manifestPath = result.ManifestPath.ShouldNotBeNull();
    }

    [OneTimeTearDown]
    public void DisposeBuildService()
        => _buildService?.Dispose();

    [TearDown]
    public void ResetFailureInjection()
        => RendererReloadFailureInjection.Reset();

    [Test]
    public void CollectibleGeneration_UnloadsForOneHundredCycles()
    {
        RendererBackendModuleLoader loader = new();
        long startBytes = GC.GetTotalMemory(forceFullCollection: true);
        for (int cycle = 0; cycle < 100; cycle++)
        {
            WeakReference context = LoadAndBeginUnload(loader, _manifestPath);
            RendererBackendModuleLoader.VerifyUnloaded(context, 5)
                .ShouldBeTrue($"Collectible renderer generation remained alive at cycle {cycle}.");
        }

        long retainedBytes = GC.GetTotalMemory(forceFullCollection: true) - startBytes;
        retainedBytes.ShouldBeLessThan(
            32L * 1024L * 1024L,
            "Collectible backend loads should reach a bounded managed-memory steady state.");
    }

    [Test]
    public void Loader_RejectsBadAbi()
    {
        string manifest = WriteVariant(
            value => value with { AbiVersion = RendererBackendAbi.CurrentVersion + 1 },
            "bad-abi");
        Should.Throw<RendererBackendModuleValidationException>(
                () => new RendererBackendModuleLoader().Load(manifest))
            .Message.ShouldContain("incompatible");
    }

    [Test]
    public void Loader_RejectsWrongArchitecture()
    {
        Architecture wrong = RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? Architecture.Arm64
            : Architecture.X64;
        string manifest = WriteVariant(
            value => value with { ProcessArchitecture = wrong },
            "wrong-architecture");
        Should.Throw<RendererBackendModuleValidationException>(
                () => new RendererBackendModuleLoader().Load(manifest))
            .Message.ShouldContain("does not match");
    }

    [Test]
    public void Loader_RejectsHashMismatch()
    {
        string manifest = WriteVariant(
            value =>
            {
                Dictionary<string, string> hashes = new(value.FileHashes, StringComparer.OrdinalIgnoreCase)
                {
                    [value.EntryAssembly] = new string('0', 64),
                };
                return value with { FileHashes = hashes };
            },
            "bad-hash");
        Should.Throw<RendererBackendModuleValidationException>(
                () => new RendererBackendModuleLoader().Load(manifest))
            .Message.ShouldContain("hash mismatch");
    }

    [Test]
    public void Loader_RejectsMissingNativeDependency()
    {
        string manifest = WriteVariant(
            value =>
            {
                Dictionary<string, string> hashes = new(value.FileHashes, StringComparer.OrdinalIgnoreCase)
                {
                    ["missing-native.dll"] = new string('0', 64),
                };
                return value with { FileHashes = hashes };
            },
            "missing-native");
        Should.Throw<RendererBackendModuleValidationException>(
                () => new RendererBackendModuleLoader().Load(manifest))
            .Message.ShouldContain("missing");
    }

    [Test]
    public void Loader_RejectsWrongBackendIdentity()
    {
        string manifest = WriteVariant(
            value => value with { BackendId = "not-the-open-gl-module" },
            "wrong-backend");
        Should.Throw<RendererBackendModuleValidationException>(
                () => new RendererBackendModuleLoader().Load(manifest))
            .Message.ShouldContain("backend");
    }

    [Test]
    public void Loader_RejectsDuplicateStableContract()
    {
        RendererBackendGenerationManifest original = ReadManifest(_manifestPath);
        string generationDirectory = Path.GetDirectoryName(_manifestPath)!;
        string contractName = "XREngine.Runtime.Rendering.dll";
        string contractPath = Path.Combine(generationDirectory, contractName);
        string stableAssemblyPath = typeof(IRendererBackendModule).Assembly.Location;
        File.Copy(stableAssemblyPath, contractPath, overwrite: true);
        try
        {
            Dictionary<string, string> hashes = new(original.FileHashes, StringComparer.OrdinalIgnoreCase)
            {
                [contractName] = ComputeHash(contractPath),
            };
            string manifest = WriteVariant(
                value => value with { FileHashes = hashes },
                "duplicate-contract");
            Should.Throw<RendererBackendModuleValidationException>(
                    () => new RendererBackendModuleLoader().Load(manifest))
                .Message.ShouldContain("duplicate stable contract assembly");
        }
        finally
        {
            File.Delete(contractPath);
        }
    }

    [Test]
    public void ExplicitModuleValidationFailure_IsActionableAndLeavesNoContext()
    {
        RendererReloadFailureInjection.Failures =
            RendererReloadInjectedFailure.ModuleValidation;
        RendererReloadInjectedException exception = Should.Throw<RendererReloadInjectedException>(
            () => new RendererBackendModuleLoader().Load(_manifestPath));
        exception.Failure.ShouldBe(RendererReloadInjectedFailure.ModuleValidation);
        exception.Phase.ShouldBe("module validation");
    }

    [Test]
    public async Task ExplicitBackendBuildFailure_DoesNotPublishManifest()
    {
        RendererReloadFailureInjection.Failures =
            RendererReloadInjectedFailure.BackendBuild;
        RendererBackendBuildResult result = await _buildService!.BuildAsync(
            RendererBackendId.OpenGL,
            TestConfiguration);
        result.Succeeded.ShouldBeFalse();
        result.ManifestPath.ShouldBeNull();
        result.Diagnostics.ShouldContain(
            diagnostic => diagnostic.Code == "HOTRELOAD" &&
                diagnostic.Message.Contains("Injected", StringComparison.Ordinal));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadAndBeginUnload(
        RendererBackendModuleLoader loader,
        string manifestPath)
    {
        LoadedRendererBackendGeneration generation = loader.Load(manifestPath);
        generation.Registration.Metadata.Generation.ShouldBeGreaterThan(0);
        return generation.BeginUnload();
    }

    private string WriteVariant(
        Func<RendererBackendGenerationManifest, RendererBackendGenerationManifest> mutate,
        string name)
    {
        RendererBackendGenerationManifest value = mutate(ReadManifest(_manifestPath));
        string path = Path.Combine(
            Path.GetDirectoryName(_manifestPath)!,
            $"renderer-backend-generation.{name}.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static RendererBackendGenerationManifest ReadManifest(string path)
        => JsonSerializer.Deserialize<RendererBackendGenerationManifest>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException($"Invalid test manifest '{path}'.");

    private static string ComputeHash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream))
            .ToLowerInvariant();
    }

    private static string FindWorkspaceRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "XRENGINE.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the XRENGINE workspace root.");
    }
}
